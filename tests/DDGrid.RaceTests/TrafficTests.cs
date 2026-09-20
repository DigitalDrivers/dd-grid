using System.Numerics;
using DDGrid.Core;
using DDGrid.Core.Protocol;
using Xunit;

namespace DDGrid.RaceTests;

public class TrafficTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int Bots = 6;
    private const int Laps = 4;

    /// <summary>A ring of 600 m with room to go round on either side.</summary>
    private static Lane Ring(float length = 600f)
    {
        var radius = length / MathF.Tau;
        var points = new List<LanePoint>();
        for (var i = 0; i < (int)length; i++)
        {
            var angle = i / length * MathF.Tau;
            points.Add(new LanePoint(
                new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius),
                i, 1f, radius, 8f, 8f,
                new Vector3(-MathF.Sin(angle), 0, MathF.Cos(angle)), new Vector3(0, 1, 0), 0f, 0f));
        }
        return new Lane([.. points]);
    }

    /// <summary>Boxes behind the line in two staggered columns, so the field does not start on one spot.</summary>
    private static TrackSlot[] Grid(Lane lane, int count)
    {
        var boxes = new TrackSlot[count];
        for (var i = 0; i < count; i++)
        {
            var sample = lane.Sample(lane.Length - 8 - i * 6);
            var across = Vector3.Normalize(Vector3.Cross(sample.Normal, sample.Forward)) * (i % 2 == 0 ? 2.5f : -2.5f);
            boxes[i] = new TrackSlot(i, sample.Position + across, MathF.Atan2(sample.Forward.X, sample.Forward.Z));
        }
        return boxes;
    }

    private static async Task<RaceClient> JoinAsync(TestServer server, int slot, string name, CancellationToken token) =>
        await RaceClient.JoinAsync(new RaceClientOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            Guid = Roster.GuidOf(slot),
            Name = name,
            CarModel = TestServer.Car,
            ServerRoot = server.Root,
        }, token);

    /// <summary>
    /// The bots share a road with two drivers who are in the way: one going at a third of their pace, one
    /// standing still on the racing line. Neither reacts to anything, as a member who has not seen them
    /// coming would not. The bots have to get past without hitting either.
    /// </summary>
    [ServerFact]
    public async Task goes_round_a_slower_driver_instead_of_into_the_back_of_them()
    {
        await using var server = await TestServer.StartAsync(cars: Bots + 2, laps: Laps, waitSeconds: 10);
        var lane = Ring();
        var field = new Field(lane);
        var grid = Grid(lane, Bots + 2);
        var quick = SpeedProfile.ForLapTime(lane, CarLimits.Nominal, 8f);
        var slow = SpeedProfile.ForLapTime(lane, CarLimits.Nominal, 20f);
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        var clients = new List<RaceClient>();
        var bots = new List<RaceBot>();
        var ours = new HashSet<byte>();
        var sightings = new List<CarSighting>();
        var closest = float.MaxValue;
        var touched = 0;
        var passes = 0;

        try
        {
            for (var i = 0; i < Bots; i++)
            {
                var client = await JoinAsync(server, i, Roster.Field(Bots, seed: 3)[i].Name, stop.Token);
                clients.Add(client);
                ours.Add(client.SessionId);
                bots.Add(new RaceBot(client, lane, quick, grid, reactionSeconds: 0.2f, field: field));
            }

            // The two who are in the way, each on a connection of its own: the bots only know about them
            // through the server, the way they would know about a member.
            await using var crawlerClient = await JoinAsync(server, Bots, "Slow Member", stop.Token);
            await using var parkedClient = await JoinAsync(server, Bots + 1, "Parked Member", stop.Token);
            var crawler = new Bot(lane, slow, startDistance: 150f);
            var parked = new Bot(lane, slow, startDistance: 320f);

            using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            var before = new float[Bots];
            while (bots.Any(b => b.Bot.Laps < Laps) && await ticker.WaitForNextTickAsync(stop.Token))
            {
                // The two in the way go first, so the bots see where they are this tick.
                crawler.Advance(0.05f);
                crawlerClient.Send(crawler.State());
                parkedClient.Send(parked.State()); // never advanced: standing on the line

                field.Clear();
                for (var i = 0; i < bots.Count; i++) field.Add(bots[i].Bot.Seen(clients[i].SessionId));
                clients[0].SeeCars(sightings);
                foreach (var sighting in sightings)
                    if (!ours.Contains(sighting.SessionId))
                        field.Add(sighting);

                foreach (var bot in bots) await bot.TickAsync(0.05f);

                // How close anyone came, and whether anyone actually made contact. Only once they are
                // running: before the lights the field still holds where the cars were made, all on one spot.
                if (bots[0].Phase != RacePhase.Racing) continue;

                for (var i = 0; i < bots.Count; i++)
                {
                    var around = field.Around(clients[i].SessionId);
                    if (around.TouchedFrom != 0) touched++;

                    foreach (var other in new[] { crawler, parked })
                    {
                        var along = MathF.Abs(lane.Wrap(bots[i].Bot.Distance - other.Distance + lane.Length / 2) - lane.Length / 2);
                        var across = MathF.Abs(bots[i].Bot.LateralOffset - other.LateralOffset);
                        if (along < 40) closest = MathF.Min(closest, MathF.Sqrt(along * along + across * across));
                    }

                    // Getting past the standing car: this step carried the bot over where it stands.
                    var moved = lane.Wrap(bots[i].Bot.Distance - before[i]);
                    if (moved > 0 && lane.Wrap(parked.Distance - before[i]) <= moved) passes++;
                    before[i] = bots[i].Bot.Distance;
                }
            }

            await Task.Delay(1000, stop.Token);
        }
        finally
        {
            foreach (var client in clients) await client.DisposeAsync();
        }

        output.WriteLine($"closest approach {closest:F2} m, contacts {touched}, times past the standing car {passes}");
        output.WriteLine($"laps {string.Join(" ", bots.Select(b => b.Bot.Laps))}, server counted {server.Log.Split("Lap completed by").Length - 1}");

        // Nobody drove into anybody.
        Assert.Equal(0, touched);
        Assert.True(closest > 2f, $"came within {closest:F2} m");
        // Everyone got past the two in the way, lap after lap, instead of queueing up behind them.
        Assert.All(bots, bot => Assert.Equal(Laps, bot.Bot.Laps));
        // The grid stands past where the standing car is, so the opening lap does not come by it; the
        // cars that finish early keep going until the last one is done, so there can be more.
        Assert.True(passes >= Bots * (Laps - 1), $"only {passes} times past it");
        Assert.DoesNotContain("kicked", server.Log);
    }
}
