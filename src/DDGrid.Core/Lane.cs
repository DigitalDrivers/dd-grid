using System.Numerics;

namespace DDGrid.Core;

/// <summary>Where the line is at some distance into the lap, and which way it points there.</summary>
public readonly record struct LaneSample(Vector3 Position, Vector3 Forward, Vector3 Normal, float Radius, float SideLeft, float SideRight);

/// <summary>
/// The racing line as something to drive along: a distance into the lap in, a place on the track out.
/// The lap is a loop, so a distance past the line comes out at the start again.
/// </summary>
public sealed class Lane
{
    private readonly LanePoint[] _points;

    public Lane(LanePoint[] points)
    {
        if (points.Length < 2) throw new ArgumentException("A racing line needs at least two points", nameof(points));
        _points = points;
        Length = points[^1].Distance + points[^1].Length;
    }

    /// <summary>The whole lap, metres.</summary>
    public float Length { get; }

    public int Count => _points.Length;

    public LanePoint this[int index] => _points[index];

    /// <summary>The distance brought back into one lap.</summary>
    public float Wrap(float distance)
    {
        var wrapped = distance % Length;
        return wrapped < 0 ? wrapped + Length : wrapped;
    }

    /// <summary>The point at or before this distance.</summary>
    public int IndexAt(float distance)
    {
        var wrapped = Wrap(distance);
        var low = 0;
        var high = _points.Length - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (_points[middle].Distance <= wrapped) low = middle;
            else high = middle - 1;
        }
        return low;
    }

    public LaneSample Sample(float distance)
    {
        var i = IndexAt(distance);
        var next = (i + 1) % _points.Length;
        var from = _points[i];
        var to = _points[next];
        var along = from.Length > 0 ? Math.Clamp((Wrap(distance) - from.Distance) / from.Length, 0f, 1f) : 0f;

        return new LaneSample(
            Vector3.Lerp(from.Position, to.Position, along),
            Vector3.Normalize(Vector3.Lerp(from.Forward, to.Forward, along)),
            Vector3.Normalize(Vector3.Lerp(from.Normal, to.Normal, along)),
            float.Lerp(from.Radius, to.Radius, along),
            float.Lerp(from.SideLeft, to.SideLeft, along),
            float.Lerp(from.SideRight, to.SideRight, along));
    }
}
