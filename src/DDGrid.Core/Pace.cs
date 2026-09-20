namespace DDGrid.Core;

/// <summary>
/// What a car can do, as the three numbers a lap time comes out of. They are not measured from the car's
/// own data: a bot is set to a lap time, and <see cref="SpeedProfile.ForLapTime"/> works out the limits
/// that produce it.
/// </summary>
/// <param name="LateralG">Grip through a corner.</param>
/// <param name="AccelG">Getting out of one.</param>
/// <param name="BrakeG">Getting into one.</param>
/// <param name="TopSpeedMs">Flat out, metres a second.</param>
public readonly record struct CarLimits(float LateralG, float AccelG, float BrakeG, float TopSpeedMs)
{
    /// <summary>A modern racing car, near enough to start from; the lap time is what really sets the pace.</summary>
    public static readonly CarLimits Nominal = new(1.4f, 0.9f, 1.6f, 80f);

    /// <summary>
    /// The same car quicker or slower all over. Speed goes with the root of the grip, so a factor of four
    /// on the limits is twice the speed everywhere and half the lap time.
    /// </summary>
    public CarLimits Scaled(float factor) =>
        new(LateralG * factor, AccelG * factor, BrakeG * factor, TopSpeedMs * MathF.Sqrt(factor));
}

/// <summary>
/// How fast to be at every point of the racing line: as fast as the corner allows, held back by what it
/// takes to slow down for the next one and to speed up out of the last.
/// </summary>
public sealed class SpeedProfile
{
    private const float G = 9.81f;
    /// <summary>A straight has a radius in the millions; past this it is a straight.</summary>
    private const float RadiusCapMeters = 2000f;

    /// <summary>The step the bots drive in: a lap time is only true for the rate it was driven at.</summary>
    public const float StepSeconds = 0.05f;

    private readonly float[] _speeds;
    private readonly Lane _lane;

    private SpeedProfile(Lane lane, float[] speeds, CarLimits limits)
    {
        _lane = lane;
        _speeds = speeds;
        Limits = limits;
    }

    /// <summary>What the car this profile belongs to can do; scaled to the lap time it was set to.</summary>
    public CarLimits Limits { get; }

    /// <summary>
    /// What driving this profile really takes, seconds: measured by driving a lap of it in the steps the
    /// bots drive in, not worked out from the speeds. A car cannot change speed between two steps and does
    /// not accelerate faster than it can, and over a lap that adds up to more than a second.
    /// </summary>
    public float LapTimeSeconds { get; private set; }

    public static SpeedProfile For(Lane lane, CarLimits limits)
    {
        var speeds = new float[lane.Count];
        for (var i = 0; i < speeds.Length; i++)
            speeds[i] = MathF.Min(limits.TopSpeedMs, MathF.Sqrt(limits.LateralG * G * MathF.Min(lane[i].Radius, RadiusCapMeters)));

        // Twice around each way: the lap is a loop, so braking for the first corner has to reach back
        // through the start line into the last one.
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = speeds.Length - 1; i >= 0; i--)
            {
                var next = (i + 1) % speeds.Length;
                speeds[i] = MathF.Min(speeds[i], MathF.Sqrt(speeds[next] * speeds[next] + 2 * limits.BrakeG * G * lane[i].Length));
            }
            for (var i = 0; i < speeds.Length; i++)
            {
                var next = (i + 1) % speeds.Length;
                speeds[next] = MathF.Min(speeds[next], MathF.Sqrt(speeds[i] * speeds[i] + 2 * limits.AccelG * G * lane[i].Length));
            }
        }

        var profile = new SpeedProfile(lane, speeds, limits);
        profile.LapTimeSeconds = profile.MeasureLap();
        return profile;
    }

    /// <summary>
    /// The profile that takes a given lap time. Scaling the limits by <c>s</c> divides the lap time by the
    /// root of <c>s</c> everywhere at once, so one try lands close; the tries after that measure what the
    /// car really does and take out what stepping and acceleration cost it.
    /// </summary>
    public static SpeedProfile ForLapTime(Lane lane, CarLimits limits, float targetSeconds)
    {
        if (targetSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(targetSeconds));

        var profile = For(lane, limits);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var factor = profile.LapTimeSeconds / targetSeconds;
            if (MathF.Abs(factor - 1) < 0.0002f) break;
            limits = limits.Scaled(factor * factor);
            profile = For(lane, limits);
        }
        return profile;
    }

    /// <summary>One flying lap, driven the way a bot drives it.</summary>
    private float MeasureLap()
    {
        var bot = new Bot(_lane, this);
        for (var step = 0; step < 100_000; step++)
        {
            var lap = bot.Advance(StepSeconds);
            if (lap.HasValue) return lap.Value.TimeMs / 1000f;
        }
        throw new InvalidOperationException("A lap of this profile never ends");
    }

    /// <summary>How fast to be at this distance into the lap, metres a second.</summary>
    public float At(float distance)
    {
        var i = _lane.IndexAt(distance);
        var next = (i + 1) % _speeds.Length;
        var point = _lane[i];
        var along = point.Length > 0 ? Math.Clamp((_lane.Wrap(distance) - point.Distance) / point.Length, 0f, 1f) : 0f;
        return float.Lerp(_speeds[i], _speeds[next], along);
    }
}
