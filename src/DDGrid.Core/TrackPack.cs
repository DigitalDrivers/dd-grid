using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DDGrid.Core;

/// <summary>
/// What a bot needs about a track that the race server cannot tell it: where the grid and the pit boxes
/// are. Built once per track and layout from the game's files, then shipped with the bots. The racing
/// line is not in here; that one the server has, in the track's ai/fast_lane.ai.
/// </summary>
public sealed record TrackPack(string Track, string Layout, float LengthM, TrackSlot[] Grid, TrackSlot[] Pits)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // A position is a Vector3, whose x, y and z are fields, not properties: without this they are
        // written as an empty object and read back as zero.
        IncludeFields = true,
    };

    /// <summary>The name of the pack of a track and layout; an empty layout is a track without layouts.</summary>
    public static string FileName(string track, string layout) => layout.Length == 0 ? $"{track}.json" : $"{track}__{layout}.json";

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static TrackPack FromJson(string json) => JsonSerializer.Deserialize<TrackPack>(json, Json)
        ?? throw new InvalidDataException("Empty track pack");

    /// <summary>
    /// What is wrong with the pack, measured against the racing line of the same track. Boxes a track does
    /// not need are often parked off to the side, so only pole has to be on the track itself; a box far
    /// from everything means the model put it under a node that moves it, which this reader does not
    /// follow, and that track needs a look before its bots line up in a field somewhere.
    /// </summary>
    public IReadOnlyList<string> Problems(LanePoint[] lane)
    {
        var problems = new List<string>();
        if (Grid.Length == 0) problems.Add("no grid boxes found");
        for (var i = 0; i < Grid.Length; i++)
            if (Grid[i].Index != i)
                problems.Add($"grid box {i} is missing, the next one is {Grid[i].Index}");

        float ToLane(TrackSlot slot) => MathF.Sqrt(lane.Min(p => Vector3.DistanceSquared(p.Position, slot.Position)));

        if (Grid.Length > 0 && ToLane(Grid[0]) > 30f)
            problems.Add($"pole is {ToLane(Grid[0]):F0} m from the racing line");

        foreach (var (name, slots) in new[] { ("grid box", Grid), ("pit box", Pits) })
            foreach (var slot in slots)
                if (ToLane(slot) > 500f)
                    problems.Add($"{name} {slot.Index} is {ToLane(slot):F0} m from the racing line");

        return problems;
    }
}
