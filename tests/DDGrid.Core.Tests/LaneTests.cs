using System.Numerics;
using Xunit;

namespace DDGrid.Core.Tests;

public class LaneTests
{
    /// <summary>A straight line along Z, ten points, two metres apart.</summary>
    private static Lane Straight() => new([.. Enumerable.Range(0, 10).Select(i =>
        new LanePoint(new Vector3(0, 0, i * 2), i * 2, 2f, 1000f, 4f, 6f, new Vector3(0, 0, 1), new Vector3(0, 1, 0), 0f, 0f))]);

    [Fact]
    public void measures_the_whole_lap()
    {
        Assert.Equal(20f, Straight().Length);
    }

    [Fact]
    public void finds_the_place_between_two_points()
    {
        var sample = Straight().Sample(5f);

        Assert.Equal(new Vector3(0, 0, 5), sample.Position);
        Assert.Equal(new Vector3(0, 0, 1), sample.Forward);
        Assert.Equal(4f, sample.SideLeft);
    }

    [Fact]
    public void brings_a_distance_past_the_line_back_to_the_start()
    {
        var lane = Straight();

        Assert.Equal(1f, lane.Wrap(21f));
        Assert.Equal(19f, lane.Wrap(-1f));
        Assert.Equal(new Vector3(0, 0, 3), lane.Sample(23f).Position);
    }

    [Fact]
    public void needs_a_line_to_follow()
    {
        Assert.Throws<ArgumentException>(() => new Lane([]));
    }
}
