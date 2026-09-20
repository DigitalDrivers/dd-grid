using System.Globalization;
using System.Numerics;
using DDGrid.Core;

// Builds the track pack of one track and layout from an installed game: the grid and pit boxes out of the
// track's models, checked against the racing line. Run once per track, ship the result with the bots.
//
//   dotnet run --project tools/DDGrid.TrackTool -- --game <assettocorsa> --track ks_nurburgring \
//       --layout layout_gp_a --out data/tracks

var options = ParseArguments(args);
if (options == null) return 2;
var (gamePath, track, layout, outPath) = options.Value;

var trackPath = Path.Combine(gamePath, "content", "tracks", track);
if (!Directory.Exists(trackPath))
{
    Console.Error.WriteLine($"No track {track} in {gamePath}");
    return 1;
}

var lanePath = Path.Combine(trackPath, layout, "ai", "fast_lane.ai");
if (!File.Exists(lanePath))
{
    Console.Error.WriteLine($"No racing line at {lanePath}");
    return 1;
}
var lane = FastLane.ReadFile(lanePath);
Console.WriteLine($"racing line: {lane.Length} points, {lane[^1].Distance:F0} m");

var grid = new List<TrackSlot>();
var pits = new List<TrackSlot>();
foreach (var (file, offset, rotated) in Models(trackPath, layout))
{
    var model = File.ReadAllBytes(file);
    var fromModel = (Grid: TrackModel.ReadSlots(model, TrackModel.GridPrefix), Pits: TrackModel.ReadSlots(model, TrackModel.PitPrefix));
    if (fromModel.Grid.Length == 0 && fromModel.Pits.Length == 0) continue;

    // A model the track also turns would need its boxes turned with it, which this reader does not do.
    if (rotated)
    {
        Console.Error.WriteLine($"PROBLEM: {Path.GetFileName(file)} holds boxes but the track turns it");
        continue;
    }

    Console.WriteLine($"{Path.GetFileName(file)}: {fromModel.Grid.Length} grid boxes, {fromModel.Pits.Length} pit boxes");
    Add(grid, fromModel.Grid, offset);
    Add(pits, fromModel.Pits, offset);
}

var pack = new TrackPack(track, layout, lane[^1].Distance, [.. grid.OrderBy(s => s.Index)], [.. pits.OrderBy(s => s.Index)]);
var problems = pack.Problems(lane);
foreach (var problem in problems) Console.Error.WriteLine($"PROBLEM: {problem}");

Directory.CreateDirectory(outPath);
var target = Path.Combine(outPath, TrackPack.FileName(track, layout));
File.WriteAllText(target, pack.ToJson());
Console.WriteLine($"wrote {target}: {pack.Grid.Length} grid boxes, {pack.Pits.Length} pit boxes");
return problems.Count == 0 ? 0 : 1;

// Boxes of a model the track places somewhere else move with it.
static void Add(List<TrackSlot> into, TrackSlot[] slots, Vector3 offset)
{
    foreach (var slot in slots)
        if (into.All(s => s.Index != slot.Index))
            into.Add(slot with { Position = slot.Position + offset });
}

// The models of a layout, in the order the track lists them. Without a models file the track is a single
// model named after itself.
static IEnumerable<(string File, Vector3 Offset, bool Rotated)> Models(string trackPath, string layout)
{
    var modelsFile = Path.Combine(trackPath, layout.Length == 0 ? "models.ini" : $"models_{layout}.ini");
    if (!File.Exists(modelsFile))
    {
        var single = Path.Combine(trackPath, $"{Path.GetFileName(trackPath)}.kn5");
        if (File.Exists(single)) yield return (single, Vector3.Zero, false);
        yield break;
    }

    string? file = null;
    var position = Vector3.Zero;
    var rotated = false;
    foreach (var raw in File.ReadLines(modelsFile).Append("["))
    {
        var line = raw.Trim();
        if (line.StartsWith('['))
        {
            if (file != null) yield return (file, position, rotated);
            file = null;
            position = Vector3.Zero;
            rotated = false;
            continue;
        }
        var parts = line.Split('=', 2);
        if (parts.Length != 2) continue;
        switch (parts[0].Trim().ToUpperInvariant())
        {
            case "FILE":
                var path = Path.Combine(trackPath, parts[1].Trim());
                file = File.Exists(path) ? path : null;
                break;
            case "POSITION":
                position = Vector(parts[1]);
                break;
            case "ROTATION":
                rotated = Vector(parts[1]) != Vector3.Zero;
                break;
        }
    }
}

static Vector3 Vector(string value)
{
    var parts = value.Split(',');
    if (parts.Length != 3) return Vector3.Zero;
    return new Vector3(Number(parts[0]), Number(parts[1]), Number(parts[2]));
    static float Number(string part) => float.TryParse(part.Trim(), CultureInfo.InvariantCulture, out var number) ? number : 0f;
}

static (string Game, string Track, string Layout, string Out)? ParseArguments(string[] args)
{
    var values = new Dictionary<string, string>();
    for (var i = 0; i + 1 < args.Length; i += 2) values[args[i].TrimStart('-')] = args[i + 1];
    if (!values.TryGetValue("game", out var game) || !values.TryGetValue("track", out var track))
    {
        Console.Error.WriteLine("usage: --game <assettocorsa folder> --track <id> [--layout <id>] [--out <folder>]");
        return null;
    }
    return (game, track, values.GetValueOrDefault("layout", ""), values.GetValueOrDefault("out", "data/tracks"));
}
