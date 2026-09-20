using System.Numerics;
using DDGrid.Core.Protocol;

namespace DDGrid.Core;

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
    private float _lapTimeSeconds;
    private float _lastSpeed;

    public Bot(Lane lane, SpeedProfile profile, float startDistance = 0f)
    {
        _lane = lane;
        _profile = profile;
        Distance = startDistance;
        Speed = profile.At(startDistance);
    }

    /// <summary>How far round the lap, metres.</summary>
    public float Distance { get; private set; }

    /// <summary>Metres a second.</summary>
    public float Speed { get; private set; }

    /// <summary>Laps finished since it went out.</summary>
    public int Laps { get; private set; }

    /// <summary>
    /// Drives on. Returns the lap time when the car crossed the line in this step, otherwise null.
    /// </summary>
    public uint? Advance(float seconds)
    {
        _lastSpeed = Speed;
        Speed = _profile.At(Distance);
        var moved = Distance + Speed * seconds;
        _lapTimeSeconds += seconds;

        if (moved < _lane.Length)
        {
            Distance = moved;
            return null;
        }

        // Over the line: the lap that just ended is worth the time it took.
        Distance = moved - _lane.Length;
        Laps++;
        var lapTime = (uint)MathF.Round(_lapTimeSeconds * 1000);
        _lapTimeSeconds = 0;
        return lapTime;
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

        return new CarState
        {
            Position = sample.Position,
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
        CarState.Still(box.Position, CarState.Facing(new Vector3(MathF.Sin(box.HeadingRad), 0, MathF.Cos(box.HeadingRad)), 0f));
}
