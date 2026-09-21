using System.Numerics;
using DDGrid.Core.Protocol;

namespace DDGrid.Core;

/// <summary>A lap that is over: what it took, and the time at each sector line.</summary>
public readonly record struct CompletedLap(uint TimeMs, uint[] Splits);

/// <summary>
/// One simulated driver on track: how far round the lap it is, how fast, how far across the road, and
/// what the other cars see of it. It drives the racing line at the speed the profile asks for, keeps off
/// the car in front, pulls out to go round it, and loses time when it is hit.
/// </summary>
public sealed class Bot
{
    private const float TyreDiameterMeters = 0.65f;
    private const int IdleRpm = 1200;
    private const int MaxRpm = 8000;
    private const int Gears = 6;

    /// <summary>
    /// A centimetre of air under the car. The racing line was recorded while driving, with the suspension
    /// loaded, so a car put exactly on it sits a touch into the road.
    /// </summary>
    private const float RideHeightMeters = 0.01f;

    /// <summary>Room left between a car and the edge of the track.</summary>
    private const float EdgeMarginMeters = 1.5f;

    /// <summary>The same contact shakes a car once, not once every tick.</summary>
    private const float ContactCooldownSeconds = 1.5f;

    /// <summary>How often a driver who makes mistakes makes one, on average.</summary>
    private const float MistakeEverySeconds = 150f;

    /// <summary>What a mistake costs while it lasts, and how long that is.</summary>
    private const float MistakeCost = 0.90f;
    private const float MistakeSeconds = 2f;

    private readonly Lane _lane;
    private readonly SpeedProfile _profile;
    private readonly uint[] _splits = new uint[3];
    private float _lapTimeSeconds;
    private float _lastSpeed;
    private readonly Random? _mistakes;
    private float _offsetTarget;
    private float _contactCooldown;
    private float _mistakeLeft;

    /// <param name="mistakeSeed">
    /// A driver who is not perfect: now and then a corner comes out wrong and costs a few tenths. Nought
    /// is a driver who never errs, which is what a lap time is measured against.
    /// </param>
    public Bot(Lane lane, SpeedProfile profile, float startDistance = 0f, int mistakeSeed = 0)
    {
        _lane = lane;
        _profile = profile;
        _mistakes = mistakeSeed == 0 ? null : new Random(mistakeSeed);
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

    /// <summary>How far beside the racing line the car is, metres, positive to the left.</summary>
    public float LateralOffset { get; private set; }

    /// <summary>How the car sees itself from the rest of the field.</summary>
    public CarOnTrack Seen(byte sessionId) => new(sessionId, Distance, Speed, LateralOffset);

    /// <summary>Drives on with the road to itself.</summary>
    public CompletedLap? Advance(float seconds) => Advance(seconds, Surroundings.Clear);

    /// <summary>
    /// Drives on. Returns the lap when the car crossed the line in this step, otherwise null.
    /// </summary>
    public CompletedLap? Advance(float seconds, in Surroundings around)
    {
        _lastSpeed = Speed;
        _contactCooldown = MathF.Max(0f, _contactCooldown - seconds);
        var here = _lane.Sample(Distance);

        // How fast the driver wants to go: their own pace, helped by the tow, held back by the car in
        // front, and every so often spoiled by getting a corner wrong.
        if (_mistakeLeft > 0) _mistakeLeft -= seconds;
        else if (_mistakes != null && _mistakes.NextDouble() < seconds / MistakeEverySeconds) _mistakeLeft = MistakeSeconds;

        var limits = _profile.Limits;
        var pace = Racecraft.WithTow(_profile.At(Distance), around, here.Radius);
        if (_mistakeLeft > 0) pace *= MistakeCost;
        var target = Racecraft.FollowingSpeed(pace, Speed, limits.BrakeG * 9.81f, around);
        var pullingOut = MathF.Abs(Racecraft.WantedOffset(_offsetTarget, pace, around) - LateralOffset) > 0.3f;
        target = Racecraft.PullingOutSpeed(target, pullingOut, around);

        Speed = target > Speed
            ? MathF.Min(target, Speed + limits.AccelG * 9.81f * seconds)
            : MathF.Max(target, Speed - limits.BrakeG * 9.81f * seconds);

        if (around.TouchedFrom != 0f && _contactCooldown <= 0f)
        {
            var closing = MathF.Max(3f, MathF.Abs(Speed - around.SpeedAhead));
            (Speed, LateralOffset) = Racecraft.AfterContact(Speed, LateralOffset, closing, around.TouchedFrom);
            _offsetTarget = LateralOffset;
            _contactCooldown = ContactCooldownSeconds;
        }

        // Where across the road to be, and as much of the way there as driving this far allows.
        _offsetTarget = Racecraft.WantedOffset(_offsetTarget, pace, around);
        var room = Math.Clamp(_offsetTarget, -MathF.Max(0f, here.SideRight - EdgeMarginMeters), MathF.Max(0f, here.SideLeft - EdgeMarginMeters));
        // Round a car that stands still a driver steers hard; any other change of line is a gentle one, which
        // is also what keeps a field from snapping onto the line in single file when the lights go out.
        var aroundStoppedCar = pullingOut && around.SpeedAhead < 1f && around.GapAhead < Racecraft.LooksAheadMeters;
        var step = (aroundStoppedCar ? Racecraft.AcrossSlopeAt(Speed) : Racecraft.LaneChangeSlope) * Speed * seconds;
        LateralOffset = MathF.Abs(room - LateralOffset) <= step ? room : LateralOffset + MathF.Sign(room - LateralOffset) * step;

        var moved = Distance + Speed * seconds;
        _lapTimeSeconds += seconds;

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
    /// stands beside the line and comes across onto it as it drives away, instead of appearing on it the
    /// moment the lights go out.
    /// </param>
    public void StartFrom(float distance, float lateralOffset = 0f)
    {
        Distance = _lane.Wrap(distance);
        Speed = 0;
        _lastSpeed = 0;
        Laps = 0;
        _lapTimeSeconds = 0;
        LateralOffset = lateralOffset;
        _offsetTarget = 0f;
        _contactCooldown = 0f;
        Array.Clear(_splits);
    }

    /// <summary>What the other cars are told about this one.</summary>
    public CarState State()
    {
        var sample = _lane.Sample(Distance);
        var slowing = Speed < _lastSpeed - 0.05f;
        var share = Math.Clamp(Speed / MathF.Max(_profile.At(Distance), 1f), 0f, 1f);
        var gear = (byte)(1 + Math.Clamp((int)(Speed / 12f), 0, Gears - 1));
        // Inside a gear the engine runs up from idle to its limit and drops back at the change.
        var inGear = Speed / 12f - (gear - 1);

        var position = LateralOffset == 0f
            ? sample.Position
            : sample.Position + Vector3.Normalize(Vector3.Cross(sample.Normal, sample.Forward)) * LateralOffset;
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

    private static uint Milliseconds(float seconds) => (uint)MathF.Round(seconds * 1000);
}
