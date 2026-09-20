using System.Numerics;
using DDGrid.Core.Protocol;

namespace DDGrid.Core;

/// <summary>A car as the rest of the field sees it: where it is round the lap, how fast, how far across.</summary>
public readonly record struct CarOnTrack(byte SessionId, float Distance, float SpeedMs, float LateralOffset);

/// <summary>
/// Every car on track, in the terms a driver thinks in: distance round the lap and offset across the road.
/// Filled once a tick, then asked by every bot what is around it.
/// </summary>
public sealed class Field(Lane lane)
{
    /// <summary>A car this close in front or behind, and this close across, is touching.</summary>
    private const float TouchAheadMeters = 4.5f;
    private const float TouchAcrossMeters = 2.0f;
    /// <summary>A car within this much of a side is alongside, and that side is not free.</summary>
    private const float AlongsideMeters = 7f;

    private readonly List<CarOnTrack> _cars = [];

    public IReadOnlyList<CarOnTrack> Cars => _cars;

    public void Clear() => _cars.Clear();

    public void Add(CarOnTrack car) => _cars.Add(car);

    /// <summary>
    /// A car the server reported, put onto the line: the nearest point of it says how far round the lap it
    /// is, and how far across. That is how a bot sees a driver it is not driving itself.
    /// </summary>
    public void Add(in CarSighting sighting)
    {
        var distance = lane.DistanceOf(sighting.Position);
        var sample = lane.Sample(distance);
        var across = Vector3.Normalize(Vector3.Cross(sample.Normal, sample.Forward));
        _cars.Add(new CarOnTrack(sighting.SessionId, distance, sighting.Velocity.Length(),
            Vector3.Dot(sighting.Position - sample.Position, across)));
    }

    /// <summary>What the car on this slot has around it.</summary>
    public Surroundings Around(byte sessionId)
    {
        var me = _cars.FirstOrDefault(c => c.SessionId == sessionId);
        var ahead = float.MaxValue;
        var aheadSpeed = 0f;
        var left = false;
        var right = false;
        var touchedFrom = 0f;

        foreach (var other in _cars)
        {
            if (other.SessionId == sessionId) continue;

            var gap = Gap(me.Distance, other.Distance);
            var across = other.LateralOffset - me.LateralOffset;

            // In front, near enough to matter, and not so far across that it is on another part of the road.
            if (gap > 0 && gap < ahead && MathF.Abs(across) < 4f)
            {
                ahead = gap;
                aheadSpeed = other.SpeedMs;
            }

            if (MathF.Abs(gap) < AlongsideMeters)
            {
                if (across > 0.5f) left = true;
                else if (across < -0.5f) right = true;
            }

            if (MathF.Abs(gap) < TouchAheadMeters && MathF.Abs(across) < TouchAcrossMeters)
                touchedFrom = across >= 0 ? 1f : -1f;
        }

        return new Surroundings(ahead, aheadSpeed, left, right, touchedFrom);
    }

    /// <summary>How far in front of <paramref name="from"/> the other car is; negative means behind.</summary>
    private float Gap(float from, float to)
    {
        var gap = to - from;
        if (gap > lane.Length / 2) gap -= lane.Length;
        if (gap < -lane.Length / 2) gap += lane.Length;
        return gap;
    }
}
