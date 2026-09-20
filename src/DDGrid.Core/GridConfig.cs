using System.Text.Json;
using System.Text.Json.Serialization;

namespace DDGrid.Core;

/// <summary>
/// What the platform writes next to a race server's preset to ask for a grid of simulated drivers.
/// Race control mounts it into the container that drives them and points dd-grid at it.
/// </summary>
public sealed record GridConfig
{
    /// <summary>The race server. dd-grid shares its network, so this is the loopback address.</summary>
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; }

    /// <summary>The folder the server runs in: its <c>content/</c> is read for the checksums.</summary>
    public string ServerRoot { get; init; } = "/data";

    public required string Track { get; init; }

    /// <summary>Empty for a track without layouts.</summary>
    public string Layout { get; init; } = "";

    public required string Car { get; init; }

    /// <summary>The liveries the server gave the simulated drivers; for the log, the server decides them.</summary>
    public string[] Skins { get; init; } = [];

    /// <summary>How many of them.</summary>
    public int Bots { get; init; }

    /// <summary>How quick they are, in per cent. A hundred is the pace of the nominal car.</summary>
    public int Level { get; init; } = 95;

    /// <summary>A lap time to drive instead of the one the level works out to, seconds.</summary>
    public float? LapSeconds { get; init; }

    /// <summary>Picks which drivers turn up and in which order.</summary>
    public int Seed { get; init; } = 1;

    /// <summary>How long to keep trying while the race server is still starting up.</summary>
    public int WaitSeconds { get; init; } = 180;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static GridConfig Read(string path) => JsonSerializer.Deserialize<GridConfig>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"{path} is empty");

    /// <summary>
    /// The lap time the field is set to. Without one given, the nominal car's own lap round this track,
    /// slowed by however far short of a hundred per cent the level is.
    /// </summary>
    public float TargetLapSeconds(Lane lane) =>
        LapSeconds ?? SpeedProfile.For(lane, CarLimits.Nominal).LapTimeSeconds * 100f / Math.Clamp(Level, 50, 120);

    /// <summary>Where this track's grid and pit boxes are, relative to a folder of track packs.</summary>
    public string PackPath(string folder) => Path.Combine(folder, TrackPack.FileName(Track, Layout));
}
