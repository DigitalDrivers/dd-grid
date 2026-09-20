using System.Numerics;
using Xunit;

namespace DDGrid.Core.Tests;

public class GridConfigTests
{
    /// <summary>Exactly what dd-platform writes beside a race server's preset.</summary>
    private const string FromThePlatform = """
    {
      "host": "127.0.0.1",
      "port": 9610,
      "serverRoot": "/data",
      "track": "ks_nurburgring",
      "layout": "layout_gp_a",
      "car": "ks_porsche_911_gt3_cup_2017",
      "skins": [
        "00_cup",
        "01_racing_912"
      ],
      "bots": 12,
      "level": 95
    }
    """;

    private static GridConfig Read(string json)
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, json);
            return GridConfig.Read(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void reads_what_the_platform_writes()
    {
        var config = Read(FromThePlatform);

        Assert.Equal("127.0.0.1", config.Host);
        Assert.Equal(9610, config.Port);
        Assert.Equal("/data", config.ServerRoot);
        Assert.Equal("ks_nurburgring", config.Track);
        Assert.Equal("layout_gp_a", config.Layout);
        Assert.Equal("ks_porsche_911_gt3_cup_2017", config.Car);
        Assert.Equal(["00_cup", "01_racing_912"], config.Skins);
        Assert.Equal(12, config.Bots);
        Assert.Equal(95, config.Level);
        // What the platform does not say, because it has no opinion about it.
        Assert.Null(config.LapSeconds);
        Assert.Equal(180, config.WaitSeconds);
    }

    [Fact]
    public void finds_the_track_pack_the_platform_only_names()
    {
        Assert.Equal(Path.Combine("data", "tracks", "ks_nurburgring__layout_gp_a.json"), Read(FromThePlatform).PackPath(Path.Combine("data", "tracks")));
    }

    [Fact]
    public void turns_the_level_into_a_lap_time_and_takes_one_that_is_given()
    {
        var lane = new Lane([.. Enumerable.Range(0, 1000).Select(i =>
            new LanePoint(new Vector3(0, 0, i), i, 1f, 400f, 5f, 5f, new Vector3(0, 0, 1), new Vector3(0, 1, 0), 0f, 0f))]);
        var nominal = SpeedProfile.For(lane, CarLimits.Nominal).LapTimeSeconds;

        // Ninety-five per cent of the pace is five per cent more time.
        Assert.Equal(nominal * 100f / 95f, Read(FromThePlatform).TargetLapSeconds(lane), 0.01f);
        Assert.Equal(nominal, Read(FromThePlatform.Replace("\"level\": 95", "\"level\": 100")).TargetLapSeconds(lane), 0.01f);
        // A lap time given outright is what it drives, whatever the level says.
        Assert.Equal(118f, Read(FromThePlatform.Replace("\"bots\": 12", "\"lapSeconds\": 118, \"bots\": 12")).TargetLapSeconds(lane));
    }

    [Fact]
    public void says_which_file_it_could_not_read()
    {
        Assert.Throws<FileNotFoundException>(() => GridConfig.Read(Path.Combine(Path.GetTempPath(), "dd-grid-not-here.json")));
    }
}
