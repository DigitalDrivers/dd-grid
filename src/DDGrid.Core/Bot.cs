using System.Numerics;
using DDGrid.Core.Protocol;

namespace DDGrid.Core;

/// <summary>A lap that is over: what it took, and the time at each sector line.</summary>
public readonly record struct CompletedLap(uint TimeMs, uint[] Splits);

/// <summary>
/// One simulated driver on track: how far round the lap it is, how fast, and what the other cars see of
/// it. It follows the racing line at the speed the profile asks for; everything else — fighting for
/// position, contact, mistakes — comes later.
/// </summary>
public sealed class Bot
{
    private const float TyreDiameterMeters = 0.65f;
    private const int IdleRpm = 1200;
    private const int MaxRpm = 8000;
    private const int Gears = 6;

    private readonly Lane _lane;
    private readonly SpeedProfile _profile;
    /// <summary>How far a car takes to come across onto the line after a start from its box.</summary>
    private const float MergeMeters = 180f;

    /// <summary>
    /// A centimetre of air under the car. The racing line was recorded while driving, with the suspension
    /// loaded, so a car put exactly on it sits a touch into the road.
    /// </summary>
    private const float RideHeightMeters = 0.01f;

    private readonly uint[] _splits = new uint[3];
    private float _lapTimeSeconds;
    private float _lastSpeed;
    private float _offsetMeters;
    private float _offsetFadeLeft;

    public Bot(Lane lane, SpeedProfile profile, float startDistance = 0f)
    {
        _lane = lane;
        _profile = profile;
        Distance = startDistance;
        Speed = profile.At(startDistance);
        _lastSpeed = Speed;
    }

    /// <summary>How far round the lap, metres.</summary>
    public float Distance { get; private set; }

    /// <summary>Metres a second.</summary>
    public float Speed { get; private set; }

    /// <summary>Laps finished since it went out.</summary>
    public int Laps { get; private set; }

    /// <summary>
    /// Drives on. Returns the lap when the car crossed the line in this step, otherwise null.
    /// </summary>
    public CompletedLap? Advance(float seconds)
    {
        _lastSpeed = Speed;
        // The profile says how fast to be here. A car standing on the grid cannot be that fast yet, so it
        // works its way up at the rate it accelerates; once it is up to speed the profile is what it drives.
        var target = _profile.At(Distance);
        var limits = _profile.Limits;
        Speed = target > Speed
            ? MathF.Min(target, Speed + limits.AccelG * 9.81f * seconds)
            : MathF.Max(target, Speed - limits.BrakeG * 9.81f * seconds);
        var moved = Distance + Speed * seconds;
        _lapTimeSeconds += seconds;
        if (_offsetFadeLeft > 0) _offsetFadeLeft = MathF.Max(0, _offsetFadeLeft - Speed * seconds);

        // Three sectors, as the game has them: the time is taken as the car passes each line.
        for (var sector = 0; sector < _splits.Length - 1; sector++)
        {
            var at = _lane.Length * (sector + 1) / _splits.Length;
            if (Distance < at && moved >= at) _splits[sector] = Milliseconds(_lapTimeSeconds);
        }

        if (moved < _lane.Length)
        {
            Distance = moved;
            return null;
        }

        // Over the line: the lap that just ended is worth the time it took, and the last sector with it.
        Distance = moved - _lane.Length;
        Laps++;
        // Every sector time is measured from the line, so the last one is the lap itself.
        _splits[^1] = Milliseconds(_lapTimeSeconds);
        var lap = new CompletedLap(_splits[^1], [.. _splits]);
        _lapTimeSeconds = 0;
        Array.Clear(_splits);
        return lap;
    }

    /// <summary>
    /// Puts the car somewhere on the line and starts a fresh lap from there. A race of its own starts at
    /// nought laps: the count belongs to the race, not to the car.
    /// </summary>
    /// <param name="lateralOffset">
    /// How far beside the line it starts, metres, positive to the left. A car starting from its grid box
    /// stands beside the line and comes across onto it over the first stretch, instead of appearing on it
    /// the moment the lights go out.
    /// </param>
    public void StartFrom(float distance, float lateralOffset = 0f)
    {
        Distance = _lane.Wrap(distance);
        Speed = 0;
        _lastSpeed = 0;
        Laps = 0;
        _lapTimeSeconds = 0;
        _offsetMeters = lateralOffset;
        _offsetFadeLeft = MergeMeters;
        Array.Clear(_splits);
    }

    /// <summary>How far beside the line the car is right now, metres.</summary>
    public float LateralOffset => _offsetFadeLeft <= 0 ? 0 : _offsetMeters * (_offsetFadeLeft / MergeMeters);

    private static uint Milliseconds(float seconds) => (uint)MathF.Round(seconds * 1000);

    /// <summary>What the other cars are told about this one.</summary>
    public CarState State()
    {
        var sample = _lane.Sample(Distance);
        var slowing = Speed < _lastSpeed - 0.05f;
        var share = Math.Clamp(Speed / MathF.Max(_profile.At(Distance), 1f), 0f, 1f);
        var gear = (byte)(1 + Math.Clamp((int)(Speed / 12f), 0, Gears - 1));
        // Inside a gear the engine runs up from idle to its limit and drops back at the change.
        var inGear = Speed / 12f - (gear - 1);

        // Beside the line while it is still coming across from its grid box.
        var offset = LateralOffset;
        var position = offset == 0
            ? sample.Position
            : sample.Position + Vector3.Normalize(Vector3.Cross(sample.Normal, sample.Forward)) * offset;
        position.Y += RideHeightMeters;

        return new CarState
        {
            Position = position,
            Rotation = CarState.Facing(sample.Forward, 0f),
            Velocity = sample.Forward * Speed,
            TyreAngularSpeed = CarState.WheelSpeed(Speed, TyreDiameterMeters),
            SteerAngle = 127,
            WheelAngle = 127,
            EngineRpm = (ushort)(IdleRpm + (MaxRpm - IdleRpm) * Math.Clamp(inGear, 0f, 1f)),
            Gear = (byte)(gear + 1), // the game counts reverse as 0 and neutral as 1
            Status = CarStatus.HighBeamsOff | (slowing ? CarStatus.BrakeLightsOn : 0),
            Gas = (byte)(slowing ? 0 : 255 * share),
            NormalizedPosition = Distance / _lane.Length,
        };
    }

    /// <summary>Puts the car on its grid box, facing the way the box does, standing still.</summary>
    public static CarState OnGrid(TrackSlot box) =>
        CarState.Still(box.Position with { Y = box.Position.Y + RideHeightMeters },
            CarState.Facing(new Vector3(MathF.Sin(box.HeadingRad), 0, MathF.Cos(box.HeadingRad)), 0f));
}
