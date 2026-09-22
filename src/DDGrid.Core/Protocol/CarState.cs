using System.Numerics;

namespace DDGrid.Core.Protocol;

/// <summary>
/// Where a car is and what it is doing, as the game tells the server twenty times a second. Everything in
/// here ends up on the screens of the other drivers: the wheels turn, the brake lights come on, the engine
/// is heard at the right pitch.
/// </summary>
public struct CarState
{
    public Vector3 Position;
    /// <summary>Heading, pitch and roll, in the game's own order — see <see cref="Facing"/>.</summary>
    public Vector3 Rotation;
    public Vector3 Velocity;
    /// <summary>How fast the wheels turn, in the game's encoding — see <see cref="WheelSpeed"/>.</summary>
    public byte TyreAngularSpeed;
    public byte SteerAngle;
    public byte WheelAngle;
    public ushort EngineRpm;
    public byte Gear;
    public CarStatus Status;
    public byte Gas;
    /// <summary>How far round the lap, 0 to 1.</summary>
    public float NormalizedPosition;

    /// <summary>Straight ahead, wheels straight: what a car standing still looks like.</summary>
    public static CarState Still(Vector3 position, Vector3 rotation) => new()
    {
        Position = position,
        Rotation = rotation,
        Velocity = Vector3.Zero,
        TyreAngularSpeed = WheelSpeed(0f, 0.65f),
        SteerAngle = 127,
        WheelAngle = 127,
        EngineRpm = 900,
        Gear = 1,
        Status = CarStatus.HighBeamsOff,
        Gas = 0,
    };

    /// <summary>
    /// Which way a car points when it drives along <paramref name="forward"/> on a road banked by
    /// <paramref name="camber"/>. The order is the game's: heading, then how steeply it climbs, then how
    /// far it leans.
    /// </summary>
    public static Vector3 Facing(Vector3 forward, float camber) => new(
        MathF.Atan2(forward.Z, forward.X) - MathF.PI / 2,
        -(MathF.Atan2(new Vector2(forward.Z, forward.X).Length(), forward.Y) - MathF.PI / 2),
        camber);

    /// <summary>
    /// The front wheels' lock, in the byte the game sends: half degrees either side of 127, above it to the
    /// right. Read off the game by showing a parked car known values and looking at its wheels from the front
    /// (+100 is about 45 degrees to the right). No car locks further than 35 degrees.
    /// </summary>
    public static byte EncodeWheelAngle(float degreesRight) => (byte)(127 + Math.Clamp(MathF.Round(degreesRight * 2f), -70f, 70f));

    /// <summary>
    /// How fast the wheels turn, in the byte the game sends: turns a second times six, on a logarithmic
    /// scale so that walking pace and full speed both fit.
    /// </summary>
    public static byte WheelSpeed(float speedMs, float tyreDiameterMeters)
    {
        var turns = speedMs / (MathF.PI * tyreDiameterMeters) * 6;
        var encoded = MathF.Round(MathF.Log10(MathF.Abs(turns) + 1f) * 20f) * MathF.Sign(turns);
        return (byte)(Math.Clamp(encoded, -100f, 154f) + 100f);
    }

    public readonly void Write(ref PacketWriter writer, byte sequence, uint timestamp)
    {
        writer.Id(ClientPacket.PositionUpdate);
        writer.Byte(sequence);
        writer.Value(timestamp);
        writer.Value(Position);
        writer.Value(Rotation);
        writer.Value(Velocity);
        writer.Byte(TyreAngularSpeed);
        writer.Byte(TyreAngularSpeed);
        writer.Byte(TyreAngularSpeed);
        writer.Byte(TyreAngularSpeed);
        writer.Byte(SteerAngle);
        writer.Byte(WheelAngle);
        writer.Value(EngineRpm);
        writer.Byte(Gear);
        writer.Value((uint)Status);
        writer.Value((short)0); // performance delta, what the game shows against the best lap
        writer.Byte(Gas);
        writer.Value(NormalizedPosition);
    }
}
