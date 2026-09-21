namespace DDGrid.Core;

/// <summary>What a driver can see of the cars immediately around them.</summary>
/// <param name="GapAhead">Metres to the car in front along the line; <see cref="float.MaxValue"/> when the road is clear.</param>
/// <param name="SpeedAhead">How fast that car is going, metres a second.</param>
/// <param name="LeftBlocked">Another car is alongside on the left.</param>
/// <param name="RightBlocked">Another car is alongside on the right.</param>
/// <param name="TouchedFrom">Which side a car is touching this one, -1 right, 1 left, 0 not at all.</param>
/// <param name="AcrossAhead">How far across the car in front is from this one; only one close across is in the way.</param>
public readonly record struct Surroundings(float GapAhead, float SpeedAhead, bool LeftBlocked, bool RightBlocked, float TouchedFrom, float AcrossAhead = 0f)
{
    /// <summary>Nobody near.</summary>
    public static readonly Surroundings Clear = new(float.MaxValue, 0f, false, false, 0f);
}

/// <summary>
/// How a driver behaves among others: how close to follow, when the tow helps, which way to go round.
/// All of it worked out from the situation alone, so it can be tested without a car, a track or a server.
/// </summary>
public static class Racecraft
{
    /// <summary>How far out a car pulls to go round another, metres.</summary>
    public const float OvertakeOffsetMeters = 2.6f;

    /// <summary>
    /// Past this there is nobody worth worrying about. It has to be more than a braking distance from
    /// racing speed, or a driver would see a stopped car too late to stop for it.
    /// </summary>
    public const float LooksAheadMeters = 220f;

    /// <summary>How far across another car still takes up the road in front: a car's width and a little room.</summary>
    public const float BlocksAcrossMeters = 2.2f;

    /// <summary>Walking pace: how a driver gets out from behind a car that does not move.</summary>
    public const float CrawlMs = 2.5f;

    /// <summary>
    /// How steeply a car changes line: metres across per metre along. A car moves sideways by driving, so
    /// this is a slope, not a speed — it takes about a hundred metres to change lane.
    /// </summary>
    public const float LaneChangeSlope = 0.025f;

    /// <summary>
    /// How steeply a driver steers round a car that stands still. At racing speed that is a change of lane
    /// like any other; at walking pace a driver on full lock is out from behind it within a car length or
    /// two. Without that a car stood behind another could never get out.
    /// </summary>
    public static float AcrossSlopeAt(float speedMs) => LaneChangeSlope + 0.6f * MathF.Max(0f, 1f - speedMs / 12f);

    /// <summary>
    /// The speed to go round a car that stands still right in front. Following it would mean stopping, and
    /// a stopped car cannot move across: so a driver who has a side to go to creeps out past it instead of
    /// queueing behind it for good. Not while touching it.
    /// </summary>
    public static float PullingOutSpeed(float target, bool pullingOut, in Surroundings around)
        => pullingOut && around.SpeedAhead < 1f && around.GapAhead is > 4.6f and < LooksAheadMeters ? MathF.Max(target, CrawlMs) : target;

    /// <summary>The gap a driver wants to the car in front at this speed: about a fifth of a second of it.</summary>
    public static float WantedGap(float speedMs) => 5f + 0.25f * speedMs;

    /// <summary>
    /// How fast to go given what is in front. What matters is not the gap but what is left of it after
    /// shedding the speed difference: a car twenty metres behind another at the same speed is fine, the
    /// same twenty metres onto a stopped car is not. So the braking distance for the difference comes off
    /// the gap first, and what remains is measured against the gap the driver wants.
    /// </summary>
    /// <param name="brakeDecel">How hard this car can brake, metres a second a second.</param>
    public static float FollowingSpeed(float pace, float ownSpeed, float brakeDecel, in Surroundings around)
    {
        // Far away, or already beside it on a line of its own: nothing to slow down for.
        if (around.GapAhead > LooksAheadMeters || MathF.Abs(around.AcrossAhead) >= BlocksAcrossMeters) return pace;

        var closing = ownSpeed - around.SpeedAhead;
        var toShed = closing > 0 ? closing * closing / (2f * MathF.Max(1f, brakeDecel)) : 0f;
        var wanted = WantedGap(ownSpeed);
        var spare = around.GapAhead - toShed - wanted;
        if (spare > 0) return pace;

        return MathF.Min(pace, MathF.Max(0f, around.SpeedAhead + spare * 0.5f));
    }

    /// <summary>
    /// The tow. Behind another car on a straight the air is easier, worth a few per cent. Not in a corner,
    /// where the car in front takes the grip away instead.
    /// </summary>
    public static float WithTow(float pace, in Surroundings around, float radiusMeters)
        => around.GapAhead is > 3f and < 45f && radiusMeters > 600f ? pace * 1.03f : pace;

    /// <summary>
    /// Where across the road to be. Clear road: on the line. Coming up on someone slower: out to whichever
    /// side is free, and stay there until past. The side is only given up once the car in front is gone.
    /// </summary>
    public static float WantedOffset(float currentOffset, float pace, in Surroundings around)
    {
        // Clear road: back to the line — but not across a car that is still alongside on the way back.
        if (around.GapAhead > LooksAheadMeters)
            return (currentOffset > 0.5f && around.RightBlocked) || (currentOffset < -0.5f && around.LeftBlocked) ? currentOffset : 0f;
        if (pace <= around.SpeedAhead + 0.5f) return currentOffset;

        // Already out of the way and getting on with it: hold the line being taken.
        if (MathF.Abs(currentOffset) > 0.5f) return currentOffset;

        if (!around.LeftBlocked) return OvertakeOffsetMeters;
        if (!around.RightBlocked) return -OvertakeOffsetMeters;
        return currentOffset;
    }

    /// <summary>
    /// What contact costs. A car that is hit loses speed and is pushed across, the more so the harder it
    /// was hit; there is no physics behind it, only the plain fact that being hit costs time.
    /// </summary>
    public static (float Speed, float Offset) AfterContact(float speed, float offset, float closingSpeed, float fromSide)
    {
        var severity = Math.Clamp(closingSpeed / 25f, 0.15f, 1f);
        return (speed * (1f - 0.35f * severity), offset - fromSide * (0.8f + 1.5f * severity));
    }
}
