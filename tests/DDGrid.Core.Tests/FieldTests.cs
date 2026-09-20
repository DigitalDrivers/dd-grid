using System.Numerics;
using DDGrid.Core.Protocol;
using Xunit;

namespace DDGrid.Core.Tests;

public class FieldTests
{
    /// <summary>A straight kilometre along Z, ten metres of room each side.</summary>
    private static Lane Straight() => new([.. Enumerable.Range(0, 1000).Select(i =>
        new LanePoint(new Vector3(0, 0, i), i, 1f, 3000f, 10f, 10f, new Vector3(0, 0, 1), new Vector3(0, 1, 0), 0f, 0f))]);

    private static Field With(params CarOnTrack[] cars)
    {
        var field = new Field(Straight());
        foreach (var car in cars) field.Add(car);
        return field;
    }

    [Fact]
    public void finds_the_car_in_front_and_how_fast_it_is()
    {
        var field = With(
            new CarOnTrack(0, 100f, 60f, 0f),
            new CarOnTrack(1, 130f, 45f, 0f),
            new CarOnTrack(2, 300f, 70f, 0f),
            new CarOnTrack(3, 60f, 65f, 0f));   // behind

        var around = field.Around(0);

        Assert.Equal(30f, around.GapAhead);
        Assert.Equal(45f, around.SpeedAhead);
    }

    [Fact]
    public void sees_across_the_start_line()
    {
        // A car just over the line is in front of one just short of it, not a lap behind.
        var field = With(new CarOnTrack(0, 990f, 60f, 0f), new CarOnTrack(1, 10f, 55f, 0f));

        Assert.Equal(20f, field.Around(0).GapAhead, 0.01f);
        Assert.True(field.Around(1).GapAhead > 900f); // the other way round it really is a lap
    }

    [Fact]
    public void ignores_a_car_on_the_far_side_of_the_road()
    {
        var field = With(new CarOnTrack(0, 100f, 60f, 0f), new CarOnTrack(1, 120f, 30f, 6f));

        Assert.Equal(float.MaxValue, field.Around(0).GapAhead);
    }

    [Fact]
    public void knows_which_side_is_taken()
    {
        var alongsideLeft = With(new CarOnTrack(0, 100f, 60f, 0f), new CarOnTrack(1, 103f, 60f, 3f)).Around(0);
        Assert.True(alongsideLeft.LeftBlocked);
        Assert.False(alongsideLeft.RightBlocked);

        var alongsideRight = With(new CarOnTrack(0, 100f, 60f, 0f), new CarOnTrack(1, 98f, 60f, -3f)).Around(0);
        Assert.True(alongsideRight.RightBlocked);
        Assert.False(alongsideRight.LeftBlocked);

        // Far up the road is not alongside.
        Assert.False(With(new CarOnTrack(0, 100f, 60f, 0f), new CarOnTrack(1, 140f, 60f, 3f)).Around(0).LeftBlocked);
    }

    [Fact]
    public void notices_contact_and_from_which_side()
    {
        Assert.Equal(1f, With(new CarOnTrack(0, 100f, 60f, 0f), new CarOnTrack(1, 102f, 60f, 1.2f)).Around(0).TouchedFrom);
        Assert.Equal(-1f, With(new CarOnTrack(0, 100f, 60f, 0f), new CarOnTrack(1, 99f, 60f, -1.2f)).Around(0).TouchedFrom);
        Assert.Equal(0f, With(new CarOnTrack(0, 100f, 60f, 0f), new CarOnTrack(1, 108f, 60f, 0f)).Around(0).TouchedFrom);
    }

    [Fact]
    public void puts_a_car_the_server_reported_onto_the_line()
    {
        var field = new Field(Straight());
        field.Add(new CarOnTrack(0, 100f, 60f, 0f));
        // Three metres to one side of the line, two hundred metres up the road, doing 40 m/s.
        field.Add(new CarSighting(1, new Vector3(3, 0, 200), new Vector3(0, 0, 40), 0));

        var around = field.Around(0);
        Assert.Equal(100f, around.GapAhead, 1f);
        Assert.Equal(40f, around.SpeedAhead, 0.1f);
        Assert.Equal(3f, field.Cars[^1].LateralOffset, 0.01f);
    }
}
