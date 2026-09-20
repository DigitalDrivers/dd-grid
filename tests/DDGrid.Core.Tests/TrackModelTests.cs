using System.Numerics;
using System.Text;
using Xunit;

namespace DDGrid.Core.Tests;

public class TrackModelTests
{
    /// <summary>A node as a .kn5 holds it: type, name, children, active, and for a dummy its matrix.</summary>
    private static byte[] Node(string name, Vector3 position, float heading = 0f, int type = 1, int? nameLength = null)
    {
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        writer.Write(type);
        writer.Write(nameLength ?? name.Length);
        writer.Write(Encoding.ASCII.GetBytes(name));
        writer.Write(1); // one child
        writer.Write((byte)1); // active
        float[] matrix =
        [
            MathF.Cos(heading), 0, -MathF.Sin(heading), 0,
            0, 1, 0, 0,
            MathF.Sin(heading), 0, MathF.Cos(heading), 0,
            position.X, position.Y, position.Z, 1,
        ];
        foreach (var value in matrix) writer.Write(value);
        return stream.ToArray();
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void reads_the_boxes_in_index_order()
    {
        var model = Concat(
            Node("AC_START_1", new Vector3(10, 1, 20)),
            Node("AC_START_0", new Vector3(0, 1, 0), heading: MathF.PI / 2),
            Node("AC_PIT_0", new Vector3(-30, 1, 0)));

        var grid = TrackModel.ReadSlots(model, TrackModel.GridPrefix);

        Assert.Equal([0, 1], grid.Select(s => s.Index));
        Assert.Equal(new Vector3(0, 1, 0), grid[0].Position);
        Assert.Equal(MathF.PI / 2, grid[0].HeadingRad, 4);
        Assert.Equal(new Vector3(10, 1, 20), grid[1].Position);
        Assert.Equal(0f, grid[1].HeadingRad, 4);
        Assert.Single(TrackModel.ReadSlots(model, TrackModel.PitPrefix));
    }

    [Fact]
    public void ignores_the_name_where_it_is_not_a_node()
    {
        // The same text inside a mesh or a material is not a box: the header in front of it says so.
        var model = Concat(
            Encoding.ASCII.GetBytes("texture:AC_START_0.dds"),
            Node("AC_START_3", new Vector3(5, 1, 5), type: 2),
            Node("AC_START_4", new Vector3(5, 1, 5), nameLength: 99));

        Assert.Empty(TrackModel.ReadSlots(model, TrackModel.GridPrefix));
    }

    [Fact]
    public void keeps_the_first_of_two_models_holding_the_same_box()
    {
        var model = Concat(
            Node("AC_START_0", new Vector3(1, 0, 0)),
            Node("AC_START_0", new Vector3(2, 0, 0)));

        Assert.Equal(new Vector3(1, 0, 0), Assert.Single(TrackModel.ReadSlots(model, TrackModel.GridPrefix)).Position);
    }

    [GameFact]
    public void reads_the_real_grid_of_a_real_track()
    {
        var path = Path.Combine(GameFactAttribute.Path, "content/tracks/ks_nurburgring/ks_nurburgring.kn5");

        var grid = TrackModel.ReadSlotsFromFile(path, TrackModel.GridPrefix);
        var pits = TrackModel.ReadSlotsFromFile(path, TrackModel.PitPrefix);

        Assert.Equal(24, grid.Length);
        Assert.Equal(24, pits.Length);
        Assert.Equal(Enumerable.Range(0, 24), grid.Select(s => s.Index));
        // The boxes on the straight stand in two staggered columns, about twelve metres apart. The last
        // few boxes of this track are parked sideways off the track, the way Kunos built it.
        Assert.All(grid, slot => Assert.InRange(slot.Position.Y, 60f, 70f));
        Assert.InRange(Vector3.Distance(grid[0].Position, grid[2].Position), 15f, 35f);
        Assert.All(grid.Take(16), slot => Assert.InRange(MathF.Abs(slot.HeadingRad - grid[0].HeadingRad), 0f, 0.1f));
        Assert.All(grid.Take(16).Where((_, i) => i % 2 == 0), slot => Assert.InRange(slot.Position.X, -7f, -5f));
        Assert.All(grid.Take(16).Where((_, i) => i % 2 == 1), slot => Assert.InRange(slot.Position.X, -15f, -12f));
        // The pit boxes stand in one line along the pit lane.
        Assert.All(pits, slot => Assert.InRange(slot.Position.X, -30f, -25f));
    }
}
