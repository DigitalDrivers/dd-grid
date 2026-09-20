using DDGrid.Core;
using DDGrid.Core.Protocol;

// Puts a field of simulated drivers on a race server and drives them until it is stopped.
//
//   dotnet run --project tools/DDGrid.Drive -- --host 127.0.0.1 --port 9600 --server-root <folder> \
//       --track-pack data/tracks/ks_nurburgring__layout_gp_a.json --car ks_porsche_911_gt3_cup_2017 \
//       --bots 8 --lap-seconds 115

var values = new Dictionary<string, string>();
for (var i = 0; i + 1 < args.Length; i += 2) values[args[i].TrimStart('-')] = args[i + 1];

string Required(string name) => values.TryGetValue(name, out var value) ? value
    : throw new ArgumentException($"--{name} is missing");
int Number(string name, int fallback) => values.TryGetValue(name, out var value) ? int.Parse(value) : fallback;

var host = values.GetValueOrDefault("host", "127.0.0.1");
var port = Number("port", 9600);
var serverRoot = Required("server-root");
var pack = TrackPack.FromJson(File.ReadAllText(Required("track-pack")));
var car = Required("car");
var count = Number("bots", 8);
var lapSeconds = float.Parse(values.GetValueOrDefault("lap-seconds", "115"));
var skins = values.GetValueOrDefault("skins", "").Split(',', StringSplitOptions.RemoveEmptyEntries);

var lanePath = Path.Combine(serverRoot, "content", "tracks", pack.Track, pack.Layout, "ai", "fast_lane.ai");
var lane = new Lane(FastLane.ReadFile(lanePath));
Console.WriteLine($"{pack.Track}/{pack.Layout}: {lane.Length:F0} m, {pack.Grid.Length} grid boxes");

var field = Roster.Field(count, seed: Number("seed", 1));
var clients = new List<RaceClient>();
var bots = new List<RaceBot>();

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

for (var i = 0; i < count; i++)
{
    // A field is not all the same pace: a little under half a per cent a place, fastest first.
    var profile = SpeedProfile.ForLapTime(lane, CarLimits.Nominal, lapSeconds * (1 + i * 0.004f));
    var client = await RaceClient.JoinAsync(new RaceClientOptions
    {
        Host = host,
        Port = port,
        Guid = Roster.GuidOf(i),
        Name = field[i].Name,
        Nation = field[i].Nation,
        CarModel = car,
        ServerRoot = serverRoot,
    }, stop.Token);
    clients.Add(client);
    bots.Add(new RaceBot(client, lane, profile, pack.Grid, reactionSeconds: 0.25f + i * 0.03f));
    Console.WriteLine($"slot {client.SessionId}: {field[i].Name} ({field[i].Nation}), lap {profile.LapTimeSeconds:F1} s"
        + (skins.Length > 0 ? $", skin {skins[i % skins.Length]}" : ""));
}

Console.WriteLine($"{clients[0].Session.Type} '{clients[0].Session.Name}' on {clients[0].TrackName}; driving at 20 Hz, Ctrl+C to stop");

using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
var said = DateTime.UtcNow;
try
{
    while (await ticker.WaitForNextTickAsync(stop.Token))
    {
        foreach (var bot in bots) await bot.TickAsync(0.05f);

        if (DateTime.UtcNow - said < TimeSpan.FromSeconds(5)) continue;
        said = DateTime.UtcNow;
        var first = bots[0];
        Console.WriteLine($"{clients[0].Session.Type} {first.Phase}, start in {clients[0].MillisecondsToStart / 1000d:F1} s, "
            + $"laps {string.Join(" ", bots.Select(b => b.Bot.Laps))}");
    }
}
catch (OperationCanceledException)
{
}

Console.WriteLine("leaving");
foreach (var client in clients) await client.DisposeAsync();
