using System.Numerics;
using Xunit;

namespace DDGrid.Core.Tests;

public class FastLaneTests
{
    /// <summary>An AI line file as the game writes it, with the fields this test cares about set.</summary>
    private static byte[] File(int version = 7, int pointCount = 3, int extraCount = 3)
    {
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        writer.Write(version);
        writer.Write(pointCount);
        writer.Write(0); // lap time
        writer.Write(0); // samples
        for (var i = 0; i < pointCount; i++)
        {
            writer.Write(1f * i); writer.Write(2f * i); writer.Write(3f * i); // position
            writer.Write(10f * i); // distance from the start
            writer.Write(i); // id
        }
        writer.Write(extraCount);
        for (var i = 0; i < extraCount; i++)
        {
            writer.Write(50f); writer.Write(1f); writer.Write(0f); writer.Write(0f); // speed, gas, brake, lat g
            writer.Write(100f + i); // radius
            writer.Write(4f); writer.Write(5f); // room left and right
            writer.Write(0.5f); writer.Write(-1f); // camber times direction
            writer.Write(0f); writer.Write(1f); writer.Write(0f); // normal
            writer.Write(9f); // segment length
            writer.Write(0f); writer.Write(0f); writer.Write(1f); // forward
            writer.Write(0f); // tag
            writer.Write(0.02f); // grade
        }
        return stream.ToArray();
    }

    [Fact]
    public void reads_the_fields_the_bots_drive_by()
    {
        var lane = FastLane.Read(new MemoryStream(File()));

        Assert.Equal(3, lane.Length);
        Assert.Equal(new Vector3(2, 4, 6), lane[2].Position);
        Assert.Equal(20f, lane[2].Distance);
        Assert.Equal(9f, lane[1].Length);
        Assert.Equal(101f, lane[1].Radius);
        Assert.Equal(4f, lane[0].SideLeft);
        Assert.Equal(5f, lane[0].SideRight);
        Assert.Equal(new Vector3(0, 0, 1), lane[0].Forward);
        Assert.Equal(new Vector3(0, 1, 0), lane[0].Normal);
        Assert.Equal(-0.5f, lane[0].Camber, 5);
        Assert.Equal(0.02f, lane[0].Grade, 5);
    }

    [Fact]
    public void refuses_a_version_it_cannot_read()
    {
        var error = Assert.Throws<NotSupportedException>(() => FastLane.Read(new MemoryStream(File(version: 5))));
        Assert.Contains("version 5", error.Message);
    }

    [Fact]
    public void refuses_a_file_whose_halves_do_not_match()
    {
        Assert.Throws<InvalidDataException>(() => FastLane.Read(new MemoryStream(File(pointCount: 3, extraCount: 2))));
    }

    [GameFact]
    public void reads_the_real_line_of_a_real_track()
    {
        var lane = FastLane.ReadFile(Path.Combine(GameFactAttribute.Path, "content/tracks/ks_nurburgring/layout_gp_a/ai/fast_lane.ai"));

        Assert.InRange(lane.Length, 1000, 20000);
        Assert.InRange(lane[^1].Distance, 5000, 5200); // the GP layout is about 5.1 km
        // On a straight the radius runs into the millions, so there is no sensible upper bound.
        Assert.All(lane, point => Assert.True(float.IsFinite(point.Radius) && point.Radius > 1f, $"radius {point.Radius}"));
        Assert.All(lane, point => Assert.InRange(point.Forward.Length(), 0.9f, 1.1f));
        // Corners are corners and straights are straight: the line has both.
        Assert.Contains(lane, point => point.Radius < 50);
        Assert.Contains(lane, point => point.Radius > 1000);
    }
}
