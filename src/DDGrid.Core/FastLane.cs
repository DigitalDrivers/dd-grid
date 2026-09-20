using System.Numerics;

namespace DDGrid.Core;

/// <summary>
/// One point of the game's AI line. The line itself is the racing line the game's own AI follows, so it is
/// the line the bots drive too. The speed the file records is the speed of whatever car recorded the line,
/// not a fast lap, so it is not read here: the bots work their speed out from <see cref="Radius"/> and the
/// car instead.
/// </summary>
/// <param name="Position">World position, metres.</param>
/// <param name="Distance">Distance from the start of the lap along the line, metres.</param>
/// <param name="Length">Length of the segment to the next point, metres.</param>
/// <param name="Radius">Radius of the curve here, metres. Straights are in the thousands.</param>
/// <param name="SideLeft">Room to the left edge of the track, metres.</param>
/// <param name="SideRight">Room to the right edge of the track, metres.</param>
/// <param name="Forward">Unit vector along the line.</param>
/// <param name="Normal">Unit vector out of the road surface.</param>
/// <param name="Camber">Banking, positive to the right.</param>
/// <param name="Grade">Slope, positive uphill.</param>
public readonly record struct LanePoint(
    Vector3 Position,
    float Distance,
    float Length,
    float Radius,
    float SideLeft,
    float SideRight,
    Vector3 Forward,
    Vector3 Normal,
    float Camber,
    float Grade);

/// <summary>Reads <c>ai/fast_lane.ai</c> of a track, the file the game's AI drives by.</summary>
public static class FastLane
{
    public const int SupportedVersion = 7;

    public static LanePoint[] ReadFile(string path)
    {
        using var file = File.OpenRead(path);
        return Read(file);
    }

    public static LanePoint[] Read(Stream stream)
    {
        using var reader = new BinaryReader(stream);
        var version = reader.ReadInt32();
        if (version != SupportedVersion)
            throw new NotSupportedException($"AI line version {version} is not supported, only {SupportedVersion}");

        var count = reader.ReadInt32();
        if (count <= 1) throw new InvalidDataException($"AI line has {count} points");
        reader.ReadInt32(); // lap time of the recording, always 0 in practice
        reader.ReadInt32(); // sample count of the recording

        var positions = new Vector3[count];
        var distances = new float[count];
        for (var i = 0; i < count; i++)
        {
            positions[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            distances[i] = reader.ReadSingle();
            reader.ReadInt32(); // point id, the index again
        }

        var extraCount = reader.ReadInt32();
        if (extraCount != count)
            throw new InvalidDataException($"AI line has {count} points but {extraCount} extra records");

        var points = new LanePoint[count];
        for (var i = 0; i < count; i++)
        {
            reader.ReadSingle(); // speed of the recording car
            reader.ReadSingle(); // gas
            reader.ReadSingle(); // brake
            reader.ReadSingle(); // obsolete lateral g
            var radius = reader.ReadSingle();
            var sideLeft = reader.ReadSingle();
            var sideRight = reader.ReadSingle();
            var camber = reader.ReadSingle() * reader.ReadSingle(); // camber times direction, which is 1 or -1
            var normal = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var length = reader.ReadSingle();
            var forward = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            reader.ReadSingle(); // tag
            var grade = reader.ReadSingle();

            points[i] = new LanePoint(positions[i], distances[i], length, radius, sideLeft, sideRight, forward, normal, camber, grade);
        }

        return points;
    }
}
