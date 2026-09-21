using System.Net.Sockets;
using DDGrid.Core;
using DDGrid.Core.Protocol;

// Puts a field of simulated drivers on a race server and drives them until it is stopped.
//
//   dotnet run --project tools/DDGrid.Drive -- --config /data/presets/<race>/dd-grid.json
//
// The config is what the platform writes beside the race server's preset; see GridConfig. Everything in
// it can also be given on the command line, which is how a race is tried out by hand:
//
//   ... -- --track ks_nurburgring --layout layout_gp_a --car ks_porsche_911_gt3_cup_2017 \
//          --server-root /tmp/race --port 9610 --bots 8 --lap-seconds 118

var values = new Dictionary<string, string>();
for (var i = 0; i + 1 < args.Length; i += 2) values[args[i].TrimStart('-')] = args[i + 1];

GridConfig config;
try
{
    config = Configure();
}
catch (Exception error) when (error is ArgumentException or IOException or System.Text.Json.JsonException)
{
    // Started wrong: say what is missing, not where in the code it was noticed.
    Console.Error.WriteLine(error.Message);
    Console.Error.WriteLine("usage: --config <file>, or --track <id> --car <model> --server-root <folder> --port <n> --bots <n>");
    return 2;
}

GridConfig Configure()
{
    var given = values.TryGetValue("config", out var configPath) ? GridConfig.Read(configPath) : null;
    string Required(string name) => values.TryGetValue(name, out var value) ? value
        : throw new ArgumentException($"--{name} is missing (or use --config)");

    return new GridConfig
    {
        Host = values.GetValueOrDefault("host", given?.Host ?? "127.0.0.1"),
        Port = values.TryGetValue("port", out var port) ? int.Parse(port) : given?.Port ?? 9600,
        ServerRoot = values.GetValueOrDefault("server-root", given?.ServerRoot ?? "/data"),
        Track = values.GetValueOrDefault("track", given?.Track ?? Required("track")),
        Layout = values.GetValueOrDefault("layout", given?.Layout ?? ""),
        Car = values.GetValueOrDefault("car", given?.Car ?? Required("car")),
        Skins = given?.Skins ?? [],
        Bots = values.TryGetValue("bots", out var howMany) ? int.Parse(howMany) : given?.Bots ?? 0,
        Level = values.TryGetValue("level", out var level) ? int.Parse(level) : given?.Level ?? 95,
        LapSeconds = values.TryGetValue("lap-seconds", out var lap) ? float.Parse(lap) : given?.LapSeconds,
        Seed = values.TryGetValue("seed", out var seed) ? int.Parse(seed) : given?.Seed ?? 1,
        WaitSeconds = values.TryGetValue("wait-seconds", out var wait) ? int.Parse(wait) : given?.WaitSeconds ?? 180,
    };
}

// The track packs ship with dd-grid: a track's grid boxes are not on the race server.
var packs = values.GetValueOrDefault("track-packs", Path.Combine(AppContext.BaseDirectory, "data", "tracks"));
var pack = TrackPack.FromJson(File.ReadAllText(config.PackPath(packs)));
var lanePath = Path.Combine(config.ServerRoot, "content", "tracks", config.Track, config.Layout, "ai", "fast_lane.ai");
var lane = new Lane(FastLane.ReadFile(lanePath));
var target = config.TargetLapSeconds(lane);
Console.WriteLine($"{config.Track}/{config.Layout}: {lane.Length:F0} m, {pack.Grid.Length} grid boxes, {config.Bots} drivers at {target:F1} s a lap");

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

var drivers = Roster.Field(config.Bots, config.Seed);
var clients = new List<RaceClient>();
var bots = new List<RaceBot>();
// Every car on track, as the bots see it. Filled once a tick: the bots from what they are doing, and
// everyone else from what the server says about them.
var field = new Field(lane);
var ours = new HashSet<byte>();
var sightings = new List<CarSighting>();

RaceClientOptions OptionsOf(int i) => new()
{
    Host = config.Host,
    Port = config.Port,
    Guid = Roster.GuidOf(i),
    Name = drivers[i].Name,
    Nation = drivers[i].Nation,
    CarModel = config.Car,
    ServerRoot = config.ServerRoot,
};

for (var i = 0; i < config.Bots; i++)
{
    // A field is not all the same pace: a little under half a per cent a place, fastest first.
    var profile = SpeedProfile.ForLapTime(lane, CarLimits.Nominal, target * (1 + i * 0.004f));
    var client = await JoinAsync(OptionsOf(i), i == 0 ? config.WaitSeconds : 15, stop.Token);

    clients.Add(client);
    ours.Add(client.SessionId);
    // Every driver errs now and then, and each one in their own way.
    bots.Add(new RaceBot(client, lane, profile, pack.Grid, reactionSeconds: 0.25f + i * 0.03f, field: field, mistakeSeed: 1000 + i));
    Console.WriteLine($"slot {client.SessionId}: {drivers[i].Name} ({drivers[i].Nation}), lap {profile.LapTimeSeconds:F1} s"
        + (config.Skins.Length > 0 ? $", skin {config.Skins[i % config.Skins.Length]}" : ""));
}

Console.WriteLine($"{clients[0].Session.Type} '{clients[0].Session.Name}' on {clients[0].TrackName}; driving at 20 Hz, Ctrl+C to stop");

using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
var said = DateTime.UtcNow;
// A car the server throws out comes back on its slot, the way a member whose game dropped would. Until it
// is back it is not on the track, so the others do not see it either.
var rejoining = new Task<RaceClient>?[bots.Count];
var nextTry = new DateTime[bots.Count];
try
{
    while (await ticker.WaitForNextTickAsync(stop.Token))
    {
        for (var i = 0; i < bots.Count; i++)
        {
            if (clients[i].IsConnected || DateTime.UtcNow < nextTry[i]) continue;
            if (rejoining[i] == null)
            {
                Console.WriteLine($"slot {clients[i].SessionId}: {drivers[i].Name} is off the server ({clients[i].LostBecause}), rejoining");
                rejoining[i] = JoinAsync(OptionsOf(i), 30, stop.Token);
            }
            else if (rejoining[i]!.IsCompleted)
            {
                if (rejoining[i]!.IsCompletedSuccessfully)
                {
                    var lost = clients[i];
                    clients[i] = rejoining[i]!.Result;
                    clients[i].TakeOverFrom(lost);
                    ours.Add(clients[i].SessionId);
                    bots[i].Reconnected(clients[i]);
                    _ = lost.DisposeAsync().AsTask();
                    Console.WriteLine($"slot {clients[i].SessionId}: {drivers[i].Name} is back");
                }
                else
                {
                    // Refused, or no answer: try again in a while rather than knock on every tick.
                    Console.WriteLine($"slot {clients[i].SessionId}: {drivers[i].Name} could not rejoin: {rejoining[i]!.Exception?.GetBaseException().Message}");
                    nextTry[i] = DateTime.UtcNow.AddSeconds(10);
                }
                rejoining[i] = null;
            }
        }

        field.Clear();
        for (var i = 0; i < bots.Count; i++)
            if (clients[i].IsConnected)
                field.Add(bots[i].Bot.Seen(clients[i].SessionId));
        // Any car still on the server sees the whole field.
        var eyes = clients.FirstOrDefault(c => c.IsConnected);
        if (eyes != null)
        {
            eyes.SeeCars(sightings);
            foreach (var sighting in sightings)
                if (!ours.Contains(sighting.SessionId))
                    field.Add(sighting);
        }

        for (var i = 0; i < bots.Count; i++)
        {
            if (!clients[i].IsConnected) continue;
            try
            {
                await bots[i].TickAsync(0.05f);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // One driver's trouble is that driver's; the rest of the field races on.
                Console.WriteLine($"slot {clients[i].SessionId}: {drivers[i].Name}: {error.GetType().Name}: {error.Message}");
            }
        }

        if (DateTime.UtcNow - said < TimeSpan.FromSeconds(5)) continue;
        said = DateTime.UtcNow;
        var lead = eyes ?? clients[0];
        Console.WriteLine($"{lead.Session.Type} {bots[0].Phase}, start in {lead.MillisecondsToStart / 1000d:F1} s, "
            + $"laps {string.Join(" ", bots.Select(b => b.Bot.Laps))}, on the server {clients.Count(c => c.IsConnected)} of {clients.Count}");
    }
}
catch (OperationCanceledException)
{
}

Console.WriteLine("leaving");
foreach (var client in clients) await client.DisposeAsync();
return 0;

// The race server may still be starting when dd-grid does, so a car keeps knocking while nothing
// answers. A server that answers and says no — no slot for this driver, or the wrong content — has
// made up its mind, and knocking again will not change it.
static async Task<RaceClient> JoinAsync(RaceClientOptions options, int waitSeconds, CancellationToken token)
{
    var until = DateTime.UtcNow.AddSeconds(waitSeconds);
    while (true)
    {
        try
        {
            return await RaceClient.JoinAsync(options, token);
        }
        catch (Exception error) when (error is SocketException or IOException && DateTime.UtcNow < until && !token.IsCancellationRequested)
        {
            Console.WriteLine($"waiting for the race server: {error.Message}");
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
    }
}
