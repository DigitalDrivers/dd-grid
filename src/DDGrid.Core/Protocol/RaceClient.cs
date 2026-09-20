using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace DDGrid.Core.Protocol;

public sealed record RaceClientOptions
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    /// <summary>Deliberately not a SteamID: everything below 76561197960265728 is a simulated driver.</summary>
    public required ulong Guid { get; init; }
    public required string Name { get; init; }
    public required string CarModel { get; init; }
    public string Nation { get; init; } = "DEU";
    public string Team { get; init; } = "";
    public string Password { get; init; } = "";
    /// <summary>The folder the server runs in, for the checksums. Null skips them, for a server without content.</summary>
    public string? ServerRoot { get; init; }
    /// <summary>
    /// The build of Custom Shaders Patch the car claims. A server can ask for a minimum; CUSTOM_UPDATE is
    /// deliberately not in the list, so the server keeps sending the plain position updates.
    /// </summary>
    public int CspVersion { get; init; } = 3465;
}

/// <summary>What the server last said about the session being driven.</summary>
public sealed record SessionSnapshot(SessionType Type, string Name, int Laps, int Minutes, byte[] Grid, long StartTimeMs)
{
    public static readonly SessionSnapshot Unknown = new(SessionType.Practice, "", 0, 0, [], 0);

    /// <summary>Where this car starts, counting from pole; -1 when it is not on the grid.</summary>
    public int GridPlaceOf(byte sessionId) => Array.IndexOf(Grid, sessionId);
}

/// <summary>Where another car was when it was last heard from.</summary>
public readonly record struct CarSighting(byte SessionId, Vector3 Position, Vector3 Velocity, long HeardAtMs);

/// <summary>What a bot needs from its connection. The real one is <see cref="RaceClient"/>.</summary>
public interface IRaceLink
{
    byte SessionId { get; }
    SessionSnapshot Session { get; }
    /// <summary>How long until the session starts, or null while the server has not said. Negative once it runs.</summary>
    long? MillisecondsToStart { get; }
    void Send(in CarState state);
    Task CompleteLapAsync(uint lapTimeMs, IReadOnlyList<uint> splits, byte cuts = 0);
    /// <summary>Every other car the server has told this one about, as of now.</summary>
    void SeeCars(List<CarSighting> into);
}

/// <summary>
/// A car on a race server, without a game: the handshake and the checksums over TCP, then the car's
/// position, its laps and the answers to the server's pings over UDP. To the server this is a driver.
/// </summary>
public sealed class RaceClient : IRaceLink, IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops = [];
    private readonly byte[] _tcpBuffer = new byte[4096];
    private readonly byte[] _udpBuffer = new byte[512];
    private readonly SemaphoreSlim _tcpLock = new(1);
    private readonly Dictionary<byte, CarSighting> _seen = [];
    private byte _sequence;
    private byte _lapCount;
    private long _startAtTicks;
    private bool _knowsStart;

    private RaceClient(TcpClient tcp, UdpClient udp, byte sessionId, string trackName, string trackConfig)
    {
        _tcp = tcp;
        _udp = udp;
        SessionId = sessionId;
        TrackName = trackName;
        TrackConfig = trackConfig;
    }

    /// <summary>The slot the server gave this car. It is also the grid box the game would put it in.</summary>
    public byte SessionId { get; }

    /// <summary>As the server names it, which for a server asking for CSP carries the version in front.</summary>
    public string TrackName { get; }

    public string TrackConfig { get; }

    public SessionSnapshot Session { get; private set; } = SessionSnapshot.Unknown;

    /// <summary>
    /// How long until the lights go out, in milliseconds, or null while the server has not said. The
    /// server sends it in the car's own clock, so there is nothing to keep in step.
    /// </summary>
    public long? MillisecondsToStart => _knowsStart ? _startAtTicks - Environment.TickCount64 : null;

    public event Action<SessionSnapshot>? SessionChanged;

    /// <summary>Connects, proves the content, and takes the slot. Throws when the server says no.</summary>
    public static async Task<RaceClient> JoinAsync(RaceClientOptions options, CancellationToken cancellationToken = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(options.Host, options.Port, cancellationToken);
        var buffer = new byte[4096];

        var writer = new PacketWriter(buffer);
        writer.Id(ClientPacket.RequestNewConnection);
        writer.Value<ushort>(202); // the game's protocol version
        writer.Utf8(options.Guid.ToString());
        writer.Utf32(options.Name);
        writer.Utf8(options.Team);
        writer.Utf8(options.Nation);
        writer.Utf8(options.CarModel);
        writer.Utf8(options.Password);
        writer.Utf8($"SPECTATING_AWARE,EMOJI,SLOT_INDEX,CLIENT_MESSAGES,CLIENT_UDP_MESSAGES,WEATHERFX_V1,{options.CspVersion}", longLength: true);
        await SendAsync(tcp, buffer, writer.Length, cancellationToken);

        var answer = await ReadPacketAsync(tcp, buffer, cancellationToken);
        if (answer.Length == 0) throw new IOException("The server closed the connection during the handshake");
        var reader = new PacketReader(answer);
        var kind = (ServerPacket)reader.Byte();
        if (kind != ServerPacket.NewCarConnection)
        {
            tcp.Dispose();
            throw new InvalidOperationException(kind switch
            {
                ServerPacket.AuthFailed => $"The server refused {options.Name}: {reader.Utf32()}",
                ServerPacket.NoSlotsAvailable => $"The server has no slot for {options.Name}",
                _ => $"The server answered the handshake of {options.Name} with 0x{(byte)kind:X2}",
            });
        }

        var handshake = ReadHandshake(ref reader);

        if (options.ServerRoot != null)
        {
            var block = Checksums.ForHandshake(options.ServerRoot, handshake.ChecksumPaths, options.CarModel,
                Checksums.NeedsSurfacesFix(handshake.TrackName));
            writer = new PacketWriter(buffer);
            writer.Id(ClientPacket.Checksum);
            writer.Bytes(block);
            await SendAsync(tcp, buffer, writer.Length, cancellationToken);
        }
        else
        {
            // A server without content asks for nothing but the car, and has no sum to compare either.
            writer = new PacketWriter(buffer);
            writer.Id(ClientPacket.Checksum);
            writer.Bytes(new byte[(handshake.ChecksumPaths.Length + 1) * 16]);
            await SendAsync(tcp, buffer, writer.Length, cancellationToken);
        }

        var udp = new UdpClient();
        udp.Connect(options.Host, handshake.UdpPort == 0 ? options.Port : handshake.UdpPort);
        var client = new RaceClient(tcp, udp, handshake.SessionId, handshake.TrackName, handshake.TrackConfig)
        {
            Session = handshake.Session,
        };
        await client.AnnounceUdpAsync(cancellationToken);
        client._loops.Add(client.ReadTcpAsync());
        client._loops.Add(client.ReadUdpAsync());
        client._loops.Add(client.AskForTheSessionAsync());
        return client;
    }

    /// <summary>
    /// Every other car the server has told this one about. The server sends all of them to every car, so
    /// one connection sees the whole field.
    /// </summary>
    public void SeeCars(List<CarSighting> into)
    {
        into.Clear();
        lock (_seen)
            foreach (var car in _seen.Values)
                into.Add(car);
    }

    /// <summary>Sends where the car is. Twenty times a second is what the servers ask for.</summary>
    public void Send(in CarState state)
    {
        var buffer = new byte[128];
        var writer = new PacketWriter(buffer);
        state.Write(ref writer, _sequence++, (uint)Environment.TickCount);
        try
        {
            _udp.Send(writer.Written);
        }
        catch (SocketException)
        {
            // A dropped update is a dropped update; the next one is 50 ms away.
        }
    }

    /// <summary>Crosses the line. The server counts the lap, puts the car in the order and tells everyone.</summary>
    public async Task CompleteLapAsync(uint lapTimeMs, IReadOnlyList<uint> splits, byte cuts = 0)
    {
        var writer = new PacketWriter(_tcpBuffer.AsSpan(2));
        writer.Id(ClientPacket.LapCompleted);
        writer.Value((uint)Environment.TickCount);
        writer.Value(lapTimeMs);
        writer.Byte((byte)splits.Count);
        foreach (var split in splits) writer.Value(split);
        writer.Byte(cuts);
        writer.Byte(++_lapCount);
        await SendLockedAsync(writer.Length);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await Task.WhenAll(_loops); }
        catch (Exception) { /* the loops end with the connection */ }

        try
        {
            var writer = new PacketWriter(_tcpBuffer.AsSpan(2));
            writer.Id(ClientPacket.CleanExitDrive);
            await SendLockedAsync(writer.Length);
        }
        catch (Exception) { /* leaving anyway */ }

        _udp.Dispose();
        _tcp.Dispose();
        _stop.Dispose();
        _tcpLock.Dispose();
    }

    private readonly record struct Handshake(byte SessionId, ushort UdpPort, string TrackName, string TrackConfig, string[] ChecksumPaths, SessionSnapshot Session);

    private static Handshake ReadHandshake(ref PacketReader reader)
    {
        reader.Utf32();                       // server name
        var udpPort = reader.Value<ushort>();
        reader.Byte();                        // refresh rate
        var trackName = reader.Utf8();
        var trackConfig = reader.Utf8();
        reader.Utf8();                        // car model
        reader.Utf8();                        // car skin
        reader.Skip(4 + 2 + 1 + 1 + 1 + 1 + 1 + 1);       // sun angle, tyres out, blankets, tc, abs, stability, autoclutch, jump start
        reader.Skip(4 + 4 + 4 + 1 + 1 + 4 + 4 + 1 + 1);   // damage, fuel and tyre rates, mirror, contacts, race over, result screen, extra lap, gas penalty
        reader.Skip(2 + 2 + 2);               // pit window start and end, inverted grid
        var sessionId = reader.Byte();
        var sessionCount = reader.Byte();
        reader.Skip(sessionCount * 5);        // every session: type, laps, minutes
        var sessionName = reader.Utf8();      // the session running
        reader.Byte();                        // its id
        var sessionType = (SessionType)reader.Byte();
        var sessionMinutes = reader.Value<ushort>();
        var sessionLaps = reader.Value<ushort>();
        reader.Skip(4);                       // track grip
        reader.Byte();                        // the slot to spawn in, which is the session id again
        reader.Skip(8);                       // how long the session has been running

        var checksumCount = reader.Byte();
        var paths = new string[checksumCount];
        for (var i = 0; i < checksumCount; i++) paths[i] = reader.Utf8();

        // No grid order here: that comes with a session update, and without one the order is the entry list.
        return new Handshake(sessionId, udpPort, trackName, trackConfig, paths,
            new SessionSnapshot(sessionType, sessionName, sessionLaps, sessionMinutes, [], 0));
    }

    // UDP has no handshake of its own: the car says which slot it is until the server answers.
    private async Task AnnounceUdpAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await _udp.SendAsync(new byte[] { (byte)ClientPacket.CarConnect, SessionId }, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                var reply = await _udp.ReceiveAsync(timeout.Token);
                if (reply.Buffer.Length > 0 && reply.Buffer[0] == (byte)ServerPacket.CarConnect) return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
        throw new IOException("The server did not take the car's UDP connection");
    }

    /// <summary>
    /// The server tells a car about the session only when the car asks with the session it believes is
    /// running and the two differ. So ask, once a second, the way the game does — otherwise a car never
    /// learns that the race has started or where it starts from.
    /// </summary>
    private async Task AskForTheSessionAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!_stop.IsCancellationRequested && await timer.WaitForNextTickAsync(_stop.Token))
        {
            try
            {
                await _udp.SendAsync(new[] { (byte)ClientPacket.SessionRequest, (byte)Session.Type }, _stop.Token);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    private async Task ReadUdpAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await _udp.ReceiveAsync(_stop.Token); }
            catch (Exception) { return; }
            if (packet.Buffer.Length < 1) continue;

            // Where everyone else is. A batch holds up to twenty cars, and the server sends single
            // updates too; both carry the same record per car.
            if (packet.Buffer[0] == (byte)ServerPacket.MegaPacket || packet.Buffer[0] == (byte)ServerPacket.PositionUpdate)
            {
                try { ReadPositions(packet.Buffer); }
                catch (ArgumentOutOfRangeException) { /* a short packet is not worth the connection */ }
                continue;
            }

            // How long until the session starts. The server keeps saying until five seconds after it did.
            if (packet.Buffer[0] == (byte)ServerPacket.RaceStart && packet.Buffer.Length >= 11)
            {
                var reader = new PacketReader(packet.Buffer);
                reader.Byte();
                var startTime = reader.Value<int>();
                var serverTime = reader.Value<uint>();
                _startAtTicks = Environment.TickCount64 + (startTime - (long)serverTime);
                _knowsStart = true;
                continue;
            }

            // The server pings once a second and drops a car that stays quiet for fifteen.
            if (packet.Buffer[0] == (byte)ServerPacket.PingUpdate && packet.Buffer.Length >= 5)
            {
                var writer = new PacketWriter(_udpBuffer);
                writer.Id(ClientPacket.PingPong);
                writer.Value(BitConverter.ToInt32(packet.Buffer, 1));
                writer.Value(Environment.TickCount);
                try { await _udp.SendAsync(writer.Written.ToArray(), _stop.Token); }
                catch (Exception) { return; }
            }
        }
    }

    /// <summary>
    /// One car's record inside a position packet: who it is, where, and how fast. The rest of the record
    /// is for drawing the car, which a bot does not do.
    /// </summary>
    private void ReadPositions(byte[] buffer)
    {
        var reader = new PacketReader(buffer);
        var batched = (ServerPacket)reader.Byte() == ServerPacket.MegaPacket;
        var count = 1;
        if (batched)
        {
            reader.Skip(4 + 2);      // the server's time and this car's ping
            count = reader.Byte();
        }

        var now = Environment.TickCount64;
        lock (_seen)
        {
            for (var i = 0; i < count; i++)
            {
                var sessionId = reader.Byte();
                reader.Skip(1 + 4 + 2);          // sequence, timestamp, ping
                var position = reader.Value<Vector3>();
                reader.Skip(12);                 // rotation
                var velocity = reader.Value<Vector3>();
                reader.Skip(4 + 1 + 1 + 2 + 1 + 4);   // tyres, steering, engine, gear, lights
                if (!batched) reader.Skip(2 + 1);     // delta to the best lap, and the throttle

                if (sessionId != SessionId) _seen[sessionId] = new CarSighting(sessionId, position, velocity, now);
            }
        }
    }

    private async Task ReadTcpAsync()
    {
        var buffer = new byte[8192];
        while (!_stop.IsCancellationRequested)
        {
            byte[] packet;
            try { packet = await ReadPacketAsync(_tcp, buffer, _stop.Token); }
            catch (Exception) { return; }
            if (packet.Length == 0) return;

            if ((ServerPacket)packet[0] == ServerPacket.CurrentSessionUpdate)
            {
                try { OnSessionUpdate(packet); }
                catch (Exception) { /* a packet we cannot read is not worth the connection */ }
            }
        }
    }

    private void OnSessionUpdate(byte[] packet)
    {
        var reader = new PacketReader(packet);
        reader.Byte();
        var name = reader.Utf8();
        reader.Byte(); // session id
        var type = (SessionType)reader.Byte();
        var minutes = reader.Value<ushort>();
        var laps = reader.Value<ushort>();
        reader.Skip(4); // track grip
        // The grid is one byte per car and the packet ends with the start time, so what is left says how
        // many cars there are.
        var grid = new byte[reader.Remaining - 8];
        for (var i = 0; i < grid.Length; i++) grid[i] = reader.Byte();
        var startTime = reader.Value<long>();

        // Laps are numbered within their session, so a new one starts at one again.
        if (Session.Name != name || Session.Type != type) _lapCount = 0;
        Session = new SessionSnapshot(type, name, laps, minutes, grid, startTime);
        SessionChanged?.Invoke(Session);
    }

    private async Task SendLockedAsync(int length)
    {
        await _tcpLock.WaitAsync();
        try
        {
            BitConverter.TryWriteBytes(_tcpBuffer.AsSpan(0, 2), (ushort)length);
            await _tcp.GetStream().WriteAsync(_tcpBuffer.AsMemory(0, length + 2));
        }
        finally
        {
            _tcpLock.Release();
        }
    }

    private static async Task SendAsync(TcpClient tcp, byte[] payload, int length, CancellationToken cancellationToken)
    {
        var framed = new byte[length + 2];
        BitConverter.TryWriteBytes(framed.AsSpan(0, 2), (ushort)length);
        payload.AsSpan(0, length).CopyTo(framed.AsSpan(2));
        await tcp.GetStream().WriteAsync(framed, cancellationToken);
    }

    // Every packet over TCP starts with its length as two bytes.
    private static async Task<byte[]> ReadPacketAsync(TcpClient tcp, byte[] buffer, CancellationToken cancellationToken)
    {
        var stream = tcp.GetStream();
        if (!await ReadExactAsync(stream, buffer.AsMemory(0, 2), cancellationToken)) return [];
        var length = BitConverter.ToUInt16(buffer, 0);
        if (length > buffer.Length) throw new IOException($"A packet of {length} bytes does not fit");
        return await ReadExactAsync(stream, buffer.AsMemory(0, length), cancellationToken) ? buffer[..length] : [];
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> target, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < target.Length)
        {
            var count = await stream.ReadAsync(target[read..], cancellationToken);
            if (count == 0) return false;
            read += count;
        }
        return true;
    }
}
