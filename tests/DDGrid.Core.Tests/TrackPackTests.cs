using System.Numerics;
using Xunit;

namespace DDGrid.Core.Tests;

public class TrackPackTests
{
    /// <summary>A straight racing line along Z, one point a metre.</summary>
    private static LanePoint[] Lane(int length = 200) =>
        [.. Enumerable.Range(0, length).Select(i => new LanePoint(
            new Vector3(0, 0, i), i, 1f, 1000f, 5f, 5f, new Vector3(0, 0, 1), new Vector3(0, 1, 0), 0f, 0f))];

    private static TrackPack Pack(params TrackSlot[] grid) => new("test", "layout", 200f, grid, []);

    [Fact]
    public void takes_a_grid_along_the_track()
    {
        var pack = Pack(
            new TrackSlot(0, new Vector3(-3, 0, 100), 0f),
            new TrackSlot(1, new Vector3(3, 0, 94), 0f),
            new TrackSlot(2, new Vector3(-3, 0, 88), 0f));

        Assert.Empty(pack.Problems(Lane()));
    }

    [Fact]
    public void reports_a_box_that_is_nowhere_near_the_track()
    {
        var pack = Pack(new TrackSlot(0, new Vector3(0, 0, 100), 0f), new TrackSlot(1, new Vector3(900, 0, 100), 0f));

        Assert.Contains(pack.Problems(Lane()), p => p.Contains("grid box 1") && p.Contains("from the racing line"));
    }

    [Fact]
    public void reports_a_missing_box_and_an_empty_grid()
    {
        Assert.Contains(Pack(new TrackSlot(1, new Vector3(0, 0, 10), 0f)).Problems(Lane()), p => p.Contains("grid box 0 is missing"));
        Assert.Contains(Pack().Problems(Lane()), p => p.Contains("no grid boxes"));
    }

    [Fact]
    public void lets_spare_boxes_stand_beside_the_track_but_not_pole()
    {
        // Tracks park the boxes they do not need off to the side, pole never.
        var spare = Pack(new TrackSlot(0, new Vector3(0, 0, 100), 0f), new TrackSlot(1, new Vector3(-60, 0, 100), 0f));
        Assert.Empty(spare.Problems(Lane()));

        var pole = Pack(new TrackSlot(0, new Vector3(-60, 0, 100), 0f));
        Assert.Contains(pole.Problems(Lane()), p => p.Contains("pole is 60 m"));
    }

    [Fact]
    public void survives_being_written_and_read_back()
    {
        var pack = new TrackPack("ks_nurburgring", "layout_gp_a", 5076.4f,
            [new TrackSlot(0, new Vector3(-6.03f, 64.99f, -767.77f), 0.1f)],
            [new TrackSlot(0, new Vector3(-27.03f, 61.57f, -450.94f), 0.2f)]);

        var read = TrackPack.FromJson(pack.ToJson());

        // A record with arrays in it is not equal by value, so the written form is what is compared.
        Assert.Equal(pack.ToJson(), read.ToJson());
        Assert.Equal(pack.Grid[0], read.Grid[0]);
        Assert.Equal(pack.LengthM, read.LengthM);
        Assert.Equal("ks_nurburgring__layout_gp_a.json", TrackPack.FileName(pack.Track, pack.Layout));
        Assert.Equal("nordschleife.json", TrackPack.FileName("nordschleife", ""));
    }
}
