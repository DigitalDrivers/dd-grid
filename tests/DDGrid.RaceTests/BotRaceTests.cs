using System.Numerics;
using DDGrid.Core;
using DDGrid.Core.Protocol;
using Xunit;

namespace DDGrid.RaceTests;

public class BotRaceTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>A short oval so a lap takes seconds: this is about the server accepting the cars.</summary>
    private static Lane Oval(float length = 400f)
    {
        var points = new List<LanePoint>();
        for (var i = 0; i < (int)length; i++)
        {
            var angle = i / length * MathF.Tau;
            points.Add(new LanePoint(
                new Vector3(MathF.Cos(angle) * length / MathF.Tau, 0, MathF.Sin(angle) * length / MathF.Tau),
                i, 1f, length / MathF.Tau, 5f, 5f,
                new Vector3(-MathF.Sin(angle), 0, MathF.Cos(angle)), new Vector3(0, 1, 0), 0f, 0f));
        }
        return new Lane([.. points]);
    }

    /// <summary>Drives one bot until it has finished the laps it was given, or time runs out.</summary>
    private static async Task DriveAsync(RaceClient client, Bot bot, int laps, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (bot.Laps < laps && await timer.WaitForNextTickAsync(cancellationToken))
        {
            var lapTime = bot.Advance(0.05f);
            client.Send(bot.State());
            if (lapTime.HasValue) await client.CompleteLapAsync(lapTime.Value);
        }
    }

    [ServerFact]
    public async Task a_field_of_bots_joins_a_real_server_and_has_its_laps_counted()
    {
        const int cars = 3;
        const int laps = 2;
        await using var server = await TestServer.StartAsync(cars, laps);
        var lane = Oval();
        var profile = SpeedProfile.ForLapTime(lane, CarLimits.Nominal, 8f);
        var field = Roster.Field(cars, seed: 1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var clients = new List<RaceClient>();
        var driving = new List<Task>();
        try
        {
            for (var i = 0; i < cars; i++)
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
                driving.Add(DriveAsync(client, new Bot(lane, profile, startDistance: i * 20f), laps, stop.Token));
            }

            await Task.WhenAll(driving);
            // The server logs a lap as it counts it; give the last one a moment to arrive.
            await Task.Delay(1000, stop.Token);
        }
        finally
        {
            foreach (var client in clients) await client.DisposeAsync();
        }

        Assert.DoesNotContain("failed checksum", server.Log);
        foreach (var driver in field)
        {
            Assert.Contains($"{driver.Name} (", server.Log); // "... has connected"
            Assert.Contains($"Lap completed by {driver.Name}", server.Log);
        }
        // Every car went round as often as it was asked to.
        Assert.Equal(cars * laps, server.Log.Split("Lap completed by").Length - 1);

        foreach (var line in server.Log.Split('\n').Where(l => l.Contains("has connected") || l.Contains("Lap completed by")))
            output.WriteLine(line.Trim());
    }

    [ServerFact]
    public async Task a_bot_whose_content_differs_is_thrown_out()
    {
        await using var server = await TestServer.StartAsync(cars: 1, laps: 1);
        // The same file names, different content: exactly what a driver with a modified track looks like.
        var otherRoot = Directory.CreateTempSubdirectory("dd-grid-other-").FullName;
        Directory.CreateDirectory(Path.Combine(otherRoot, "content", "tracks", TestServer.Track, "data"));
        File.WriteAllText(Path.Combine(otherRoot, "content", "tracks", TestServer.Track, "data", "surfaces.ini"), "[SURFACE_0]\nFRICTION=1.5\n");
        File.WriteAllText(Path.Combine(otherRoot, "content", "tracks", TestServer.Track, "models.ini"), "[MODEL_0]\nFILE=cheat.kn5\n");
        Directory.CreateDirectory(Path.Combine(otherRoot, "content", "cars", TestServer.Car));
        File.WriteAllBytes(Path.Combine(otherRoot, "content", "cars", TestServer.Car, "data.acd"), new byte[2048]);

        var client = await RaceClient.JoinAsync(new RaceClientOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            Guid = Roster.GuidOf(0),
            Name = "Wrong Content",
            CarModel = TestServer.Car,
            ServerRoot = otherRoot,
        });

        try
        {
            await server.WaitForAsync("failed checksum", TimeSpan.FromSeconds(15));
        }
        finally
        {
            await client.DisposeAsync();
            Directory.Delete(otherRoot, true);
        }

        Assert.Contains("failed checksum", server.Log);
        output.WriteLine(server.Log.Split('\n').First(l => l.Contains("failed checksum")).Trim());
    }
}
