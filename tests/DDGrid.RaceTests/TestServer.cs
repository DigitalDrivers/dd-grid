using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace DDGrid.RaceTests;

/// <summary>
/// A test that needs a built AssettoServer to race against. check.sh points DDGRID_SERVER_DLL at one;
/// where there is none, these skip.
/// </summary>
public sealed class ServerFactAttribute : FactAttribute
{
    public static string? Dll
    {
        get
        {
            var set = Environment.GetEnvironmentVariable("DDGRID_SERVER_DLL");
            if (!string.IsNullOrEmpty(set)) return File.Exists(set) ? set : null;
            var cached = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache/dd-link/AssettoServer-v0.0.55-pre35/AssettoServer/bin/Release/net9.0/AssettoServer.dll");
            return File.Exists(cached) ? cached : null;
        }
    }

    public ServerFactAttribute()
    {
        if (Dll == null) Skip = "No AssettoServer to race against; set DDGRID_SERVER_DLL";
    }
}

/// <summary>
/// A real AssettoServer in a temporary folder, with content made up for the test: the server works its
/// checksums out from those files, and the bots have to work out the same ones.
/// </summary>
public sealed class TestServer : IAsyncDisposable
{
    public const string Car = "dd_test_car";
    public const string Track = "dd_test_track";

    private readonly Process _process;
    private readonly StringBuilder _log = new();

    private TestServer(Process process, string root, int port)
    {
        _process = process;
        Root = root;
        Port = port;
    }

    /// <summary>The folder the server runs in; the bots read their checksums from the same one.</summary>
    public string Root { get; }

    public int Port { get; }

    public string Log { get { lock (_log) return _log.ToString(); } }

    public static async Task<TestServer> StartAsync(int cars, int laps, int waitSeconds = 4)
    {
        var dll = ServerFactAttribute.Dll ?? throw new InvalidOperationException("No AssettoServer");
        // The server looks for plugins next to itself and stops when the folder is not there at all.
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(dll)!, "plugins"));
        var root = Directory.CreateTempSubdirectory("dd-grid-race-").FullName;
        var preset = Directory.CreateDirectory(Path.Combine(root, "presets", "test")).FullName;
        var port = FreePort();

        WriteContent(root);

        File.WriteAllText(Path.Combine(preset, "server_cfg.ini"), $"""
            [SERVER]
            NAME=dd-grid race test
            TRACK={Track}
            CONFIG_TRACK=
            PASSWORD=
            ADMIN_PASSWORD=dd-grid-test-admin
            UDP_PORT={port}
            TCP_PORT={port}
            HTTP_PORT={FreePort()}
            MAX_CLIENTS={cars}
            CLIENT_SEND_INTERVAL_HZ=20
            REGISTER_TO_LOBBY=0
            LOOP_MODE=1
            RACE_OVER_TIME=4
            RESULT_SCREEN_TIME=2

            [RACE]
            NAME=Race
            LAPS={laps}
            WAIT_TIME={waitSeconds}
            IS_OPEN=1

            [WEATHER_0]
            GRAPHICS=3_clear
            BASE_TEMPERATURE_AMBIENT=20
            BASE_TEMPERATURE_ROAD=8
            VARIATION_AMBIENT=1
            VARIATION_ROAD=1
            """);

        File.WriteAllText(Path.Combine(preset, "entry_list.ini"), string.Concat(
            Enumerable.Range(0, cars).Select(i => $"[CAR_{i}]\nMODEL={Car}\nSKIN=\nGUID={Core.Roster.GuidOf(i)}\n\n")));

        var start = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "dotnet"), [dll, "--preset", "test"])
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (!File.Exists(start.FileName)) start.FileName = "dotnet";

        var server = new TestServer(Process.Start(start)!, root, port);
        server._process.OutputDataReceived += server.OnOutput;
        server._process.ErrorDataReceived += server.OnOutput;
        server._process.BeginOutputReadLine();
        server._process.BeginErrorReadLine();

        await server.WaitForAsync("Starting HTTP server on port", TimeSpan.FromSeconds(30));
        return server;
    }

    /// <summary>Content the server can checksum: it only ever reads these files, never their meaning.</summary>
    private static void WriteContent(string root)
    {
        // Without this the server goes to GitHub for where the track is in the world, and refuses to start
        // for a track it does not find there. One made-up entry keeps the test off the network.
        var cfg = Directory.CreateDirectory(Path.Combine(root, "cfg")).FullName;
        File.WriteAllText(Path.Combine(cfg, "data_track_params.ini"),
            $"[{Track}]\nNAME=dd-grid test track\nLATITUDE=50.33\nLONGITUDE=6.94\nTIMEZONE=Europe/Berlin\n");

        var track = Directory.CreateDirectory(Path.Combine(root, "content", "tracks", Track, "data")).FullName;
        File.WriteAllText(Path.Combine(track, "surfaces.ini"), "[SURFACE_0]\nKEY=ROAD\nFRICTION=0.96\n");
        File.WriteAllText(Path.Combine(root, "content", "tracks", Track, "models.ini"), "[MODEL_0]\nFILE=dd_test_track.kn5\n");
        var car = Directory.CreateDirectory(Path.Combine(root, "content", "cars", Car)).FullName;
        File.WriteAllBytes(Path.Combine(car, "data.acd"), RandomNumberGeneratorBytes(2048));
    }

    private static byte[] RandomNumberGeneratorBytes(int count)
    {
        var bytes = new byte[count];
        new Random(4711).NextBytes(bytes); // the same content every run, so a failure can be looked at
        return bytes;
    }

    public async Task WaitForAsync(string text, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (Log.Contains(text)) return;
            if (_process.HasExited) throw new InvalidOperationException($"The server stopped before '{text}':\n{Log}");
            await Task.Delay(100);
        }
        throw new TimeoutException($"The server never said '{text}':\n{Log}");
    }

    public static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private void OnOutput(object sender, DataReceivedEventArgs args)
    {
        if (args.Data == null) return;
        lock (_log) _log.AppendLine(args.Data);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(true);
            await _process.WaitForExitAsync();
        }
        catch (InvalidOperationException) { /* already gone */ }
        _process.Dispose();
        try { Directory.Delete(Root, true); } catch (IOException) { /* the server may still hold a file */ }
    }
}
