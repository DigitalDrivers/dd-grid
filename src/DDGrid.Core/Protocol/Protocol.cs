namespace DDGrid.Core.Protocol;

/// <summary>The packets a car sends. The numbers are the game's.</summary>
public enum ClientPacket : byte
{
    RequestNewConnection = 0x3D,
    CleanExitDrive = 0x43,
    Checksum = 0x44,
    PositionUpdate = 0x46,
    LapCompleted = 0x49,
    CarConnect = 0x4E,
    SessionRequest = 0x4F,
    PingPong = 0xF8,
}

/// <summary>The packets a car is sent, as far as a bot cares.</summary>
public enum ServerPacket : byte
{
    NewCarConnection = 0x3E,
    NoSlotsAvailable = 0x45,
    PositionUpdate = 0x46,
    MegaPacket = 0x48,
    LapCompleted = 0x49,
    CurrentSessionUpdate = 0x4A,
    RaceOver = 0x4B,
    CarConnect = 0x4E,
    RaceStart = 0x57,
    AuthFailed = 0x6F,
    PingUpdate = 0xF9,
}

/// <summary>What the game shows about a car beyond where it is.</summary>
[Flags]
public enum CarStatus : uint
{
    None = 0,
    BrakeLightsOn = 0x10,
    LightsOn = 0x20,
    HazardsOn = 0x2000,
    HighBeamsOff = 0x4000,
}

public enum SessionType : byte
{
    Booking = 0,
    Practice = 1,
    Qualifying = 2,
    Race = 3,
}
