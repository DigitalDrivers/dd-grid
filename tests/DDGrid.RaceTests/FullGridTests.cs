using System.Numerics;
using DDGrid.Core;
using DDGrid.Core.Protocol;
using Xunit;

namespace DDGrid.RaceTests;

public class FullGridTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int Cars = 20;
    private const int Laps = 10;
    private const float LapSeconds = 4f;

    /// <summary>A ring of 600 m, so a lap takes seconds instead of two minutes.</summary>
    private static Lane Ring(float length = 600f)
    {
        var radius = length / MathF.Tau;
        var points = new List<LanePoint>();
        for (var i = 0; i < (int)length; i++)
        {
            var angle = i / length * MathF.Tau;
            points.Add(new LanePoint(
                new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius),
                i, 1f, radius, 6f, 6f,
                new Vector3(-MathF.Sin(angle), 0, MathF.Cos(angle)), new Vector3(0, 1, 0), 0f, 0f));
        }
        return new Lane([.. points]);
    }

    /// <summary>Boxes behind the line in two staggered columns, the way a track lays out its grid.</summary>
    private static TrackSlot[] GridBehindTheLine(Lane lane, int count)
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

    /// <summary>
    /// Watches every position a bot sends, so a bot that leaves the track cannot go unnoticed. A car
    /// coming across from its grid box is beside the line on purpose, so what is measured is the distance
    /// from where the bot means to be.
    /// </summary>
    private sealed class OnTrack(IRaceLink inner, Lane lane) : IRaceLink
    {
        public RaceBot? Watched { get; set; }
        public float WorstDeviation { get; private set; }
        public List<uint> Laps { get; } = [];

        public byte SessionId => inner.SessionId;
        public SessionSnapshot Session => inner.Session;
        public long? MillisecondsToStart => inner.MillisecondsToStart;

        public void SeeCars(List<CarSighting> into) => inner.SeeCars(into);

        public void Send(in CarState state)
        {
            // Only once it is running: in its box a car stands beside the line on purpose.
            if (state.Velocity.LengthSquared() > 1)
            {
                var sample = lane.Sample(lane.DistanceOf(state.Position));
                var across = Vector3.Normalize(Vector3.Cross(sample.Normal, sample.Forward));
                var meant = sample.Position + across * (Watched?.Bot.LateralOffset ?? 0);
                WorstDeviation = MathF.Max(WorstDeviation, Vector3.Distance(meant, state.Position));
            }
            inner.Send(state);
        }

        public Task CompleteLapAsync(uint lapTimeMs, IReadOnlyList<uint> splits, byte cuts = 0)
        {
            Laps.Add(lapTimeMs);
            return inner.CompleteLapAsync(lapTimeMs, splits, cuts);
        }
    }

    [ServerFact]
    public async Task a_full_grid_lines_up_starts_together_and_runs_the_distance()
    {
        await using var server = await TestServer.StartAsync(Cars, Laps, waitSeconds: 20);
        var lane = Ring();
        var grid = GridBehindTheLine(lane, Cars);
        var profile = SpeedProfile.ForLapTime(lane, CarLimits.Nominal, LapSeconds);
        var field = Roster.Field(Cars, seed: 7);
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        var clients = new List<RaceClient>();
        var watchers = new List<OnTrack>();
        var bots = new List<RaceBot>();
        try
        {
            for (var i = 0; i < Cars; i++)
            {
                var client = await RaceClient.JoinAsync(new RaceClientOptions
                {
                    Host = "127.0.0.1",
                    Port = server.Port,
                    Guid = Roster.GuidOf(i),
                    Name = field[i].Name,
                    Nation = field[i].Nation,
                    CarModel = TestServer.Car,
                    ServerRoot = server.Root,
                }, stop.Token);
                clients.Add(client);
                var watcher = new OnTrack(client, lane);
                watchers.Add(watcher);
                var bot = new RaceBot(watcher, lane, profile, grid, reactionSeconds: 0.2f + i * 0.01f);
                watcher.Watched = bot;
                bots.Add(bot);
            }

            // Everyone is in, the lights are still red: every car stands in a box of its own.
            using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            while (clients[0].MillisecondsToStart is null or > 3000 && await ticker.WaitForNextTickAsync(stop.Token))
                foreach (var bot in bots) await bot.TickAsync(0.05f);

            Assert.All(bots, bot => Assert.Equal(RacePhase.OnGrid, bot.Phase));
            Assert.Equal(Cars, bots.Select(b => b.GridPlace).Distinct().Count());

            while (bots.Any(b => b.Bot.Laps < Laps) && await ticker.WaitForNextTickAsync(stop.Token))
                foreach (var bot in bots) await bot.TickAsync(0.05f);

            await Task.Delay(1500, stop.Token);
        }
        finally
        {
            foreach (var client in clients) await client.DisposeAsync();
        }

        // The server counted every lap of every car, and threw nobody out.
        Assert.DoesNotContain("failed checksum", server.Log);
        Assert.DoesNotContain("kicked", server.Log);
        Assert.Equal(Cars * Laps, server.Log.Split("Lap completed by").Length - 1);

        // Every full lap is the lap time the bots were set to. The first one is short: it starts on the
        // grid, a few car lengths behind the line, exactly as a driver's does.
        var full = watchers.SelectMany(w => w.Laps.Skip(1)).ToList();
        Assert.Equal(Cars * (Laps - 1), full.Count);
        Assert.All(full, lap => Assert.InRange(lap, (uint)(LapSeconds * 1000 - 250), (uint)(LapSeconds * 1000 + 250)));

        // Nobody wandered off the line it meant to be on.
        Assert.All(watchers, watcher => Assert.True(watcher.WorstDeviation < 1f, $"{watcher.WorstDeviation:F2} m off the line"));

        output.WriteLine($"{Cars} cars, {Laps} laps each: {server.Log.Split("Lap completed by").Length - 1} laps counted by the server");
        output.WriteLine($"full laps {full.Min()} - {full.Max()} ms (set to {LapSeconds * 1000}), worst line deviation {watchers.Max(w => w.WorstDeviation):F3} m");
        output.WriteLine($"grid places {string.Join(" ", bots.Select(b => b.GridPlace))}");
    }
}
