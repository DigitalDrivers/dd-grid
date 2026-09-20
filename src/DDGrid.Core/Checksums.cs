using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DDGrid.Core;

/// <summary>
/// The proof a car brings that it has the same content as everyone else. The server names the files in the
/// handshake and expects their MD5 sums in that order, with the car's own data last; a sum that does not
/// match gets the car thrown out. The bots read the very files the server reads, so they match by
/// construction — as long as the two rules below are followed.
/// </summary>
public static partial class Checksums
{
    /// <summary>
    /// A server that asks for Custom Shaders Patch writes its track name as <c>csp/2651/../ks_nurburgring</c>.
    /// It then checksums the track's surfaces with the patch's change applied, and so must a car.
    /// </summary>
    [GeneratedRegex(@"^csp/(\d+)/\.\.")]
    private static partial Regex CspTrack();

    public static bool NeedsSurfacesFix(string trackName) => CspTrack().IsMatch(trackName);

    /// <summary>
    /// A server asking for the patch names its track <c>csp/2651/../ks_nurburgring</c>, and asks for the
    /// checksums of files under that name — while reading them from the plain folder itself. A car has to
    /// do the same, or it looks for files that are not there.
    /// </summary>
    [GeneratedRegex(@"csp/\d+/\.\.(?:/\w+/\.\.)?/")]
    private static partial Regex CspPathPrefix();

    public static string RealPath(string virtualPath) => CspPathPrefix().Replace(virtualPath, "", 1);

    /// <summary>
    /// The block of sums the server asked for, in its order, with the car's <c>data.acd</c> appended.
    /// </summary>
    /// <param name="serverRoot">The folder the server runs in: <c>content/</c> and <c>system/</c> live there.</param>
    /// <param name="paths">The files the handshake asked for, as it spelled them.</param>
    /// <param name="carModel">The car this bot drives.</param>
    /// <param name="surfacesFix">Whether the server asks for Custom Shaders Patch.</param>
    public static byte[] ForHandshake(string serverRoot, IReadOnlyList<string> paths, string carModel, bool surfacesFix)
    {
        var block = new byte[(paths.Count + 1) * MD5.HashSizeInBytes];

        for (var i = 0; i < paths.Count; i++)
        {
            var file = Path.Combine(serverRoot, RealPath(paths[i]).Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file))
                throw new FileNotFoundException($"The server asked for the checksum of {paths[i]}, which is not in {serverRoot}", file);

            Sum(file, surfacesFix && IsPatched(paths[i])).CopyTo(block, i * MD5.HashSizeInBytes);
        }

        var carData = Path.Combine(serverRoot, "content", "cars", carModel, "data.acd");
        // A car without data.acd has no checksum on the server either, and zeroes are what the game sends.
        if (File.Exists(carData)) Sum(carData, false).CopyTo(block, paths.Count * MD5.HashSizeInBytes);

        return block;
    }

    /// <summary>
    /// Which of the asked-for files the patch changes: the track's own surfaces and the models file of a
    /// layout. Not the game's own <c>system/data/surfaces.ini</c>, and not a track without layouts.
    /// </summary>
    private static bool IsPatched(string path) =>
        !path.StartsWith("system/", StringComparison.OrdinalIgnoreCase)
        && (path.EndsWith("/data/surfaces.ini", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).StartsWith("models_", StringComparison.OrdinalIgnoreCase));

    private static byte[] Sum(string file, bool patched)
    {
        if (!patched)
        {
            using var stream = File.OpenRead(file);
            return MD5.HashData(stream);
        }

        // What the patch does to the file before the game reads it: the first surface loses its name.
        var bytes = File.ReadAllBytes(file);
        var firstSurface = bytes.AsSpan().IndexOf("SURFACE_0"u8);
        if (firstSurface > 0) "CSP"u8.CopyTo(bytes.AsSpan(firstSurface, 3));
        return MD5.HashData(bytes);
    }
}
