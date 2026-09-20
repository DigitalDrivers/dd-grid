using Xunit;

namespace DDGrid.Core.Tests;

public class ChecksumTests
{
    [Theory]
    [InlineData("csp/2651/../ks_nurburgring", true)]
    [InlineData("csp/2651/../C/../ks_nurburgring", true)]
    [InlineData("ks_nurburgring", false)]
    public void knows_when_the_server_asks_for_the_patch(string trackName, bool expected)
    {
        Assert.Equal(expected, Checksums.NeedsSurfacesFix(trackName));
    }

    [Theory]
    [InlineData("content/tracks/csp/2651/../ks_nurburgring/data/surfaces.ini", "content/tracks/ks_nurburgring/data/surfaces.ini")]
    [InlineData("content/tracks/csp/2651/../C/../ks_nurburgring/models.ini", "content/tracks/ks_nurburgring/models.ini")]
    [InlineData("system/data/surfaces.ini", "system/data/surfaces.ini")]
    public void finds_the_file_the_server_means(string asked, string real)
    {
        Assert.Equal(real, Checksums.RealPath(asked));
    }

    [Fact]
    public void sums_the_files_the_server_named_and_the_car_last()
    {
        var root = Directory.CreateTempSubdirectory("dd-grid-sums-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "content", "tracks", "t", "data"));
            File.WriteAllText(Path.Combine(root, "content", "tracks", "t", "data", "surfaces.ini"), "[SURFACE_0]\nKEY=ROAD\n");
            Directory.CreateDirectory(Path.Combine(root, "content", "cars", "c"));
            File.WriteAllBytes(Path.Combine(root, "content", "cars", "c", "data.acd"), [1, 2, 3]);

            var plain = Checksums.ForHandshake(root, ["content/tracks/t/data/surfaces.ini"], "c", surfacesFix: false);
            var patched = Checksums.ForHandshake(root, ["content/tracks/t/data/surfaces.ini"], "c", surfacesFix: true);

            Assert.Equal(32, plain.Length); // one file and the car
            // The patch renames the first surface before the game reads the file, so the sum differs.
            Assert.NotEqual(plain[..16], patched[..16]);
            Assert.Equal(plain[16..], patched[16..]); // the car is the car
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void says_which_file_is_missing_instead_of_being_thrown_out_for_it()
    {
        var root = Directory.CreateTempSubdirectory("dd-grid-sums-").FullName;
        try
        {
            var error = Assert.Throws<FileNotFoundException>(() => Checksums.ForHandshake(root, ["content/tracks/t/models.ini"], "c", false));
            Assert.Contains("content/tracks/t/models.ini", error.Message);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
