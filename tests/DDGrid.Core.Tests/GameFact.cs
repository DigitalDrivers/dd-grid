using Xunit;

namespace DDGrid.Core.Tests;

/// <summary>
/// A test that needs the game itself. The race servers never have it, so these run only where Assetto
/// Corsa is installed: on the machine that builds the track packs. AC_ROOT overrides the usual place.
/// </summary>
public sealed class GameFactAttribute : FactAttribute
{
    public const string DefaultPath = "/mnt/c/Program Files (x86)/Steam/steamapps/common/assettocorsa";

    public static string Path => Environment.GetEnvironmentVariable("AC_ROOT") ?? DefaultPath;

    public GameFactAttribute()
    {
        if (!Directory.Exists(System.IO.Path.Combine(Path, "content", "tracks")))
            Skip = $"Assetto Corsa is not installed at {Path}";
    }
}
