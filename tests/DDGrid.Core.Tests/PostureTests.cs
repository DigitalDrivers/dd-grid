using System.Numerics;
using DDGrid.Core.Protocol;
using Xunit;

namespace DDGrid.Core.Tests;

/// <summary>
/// How a car looks while it drives: wheels that steer into a corner, a body that leans out of it and dips
/// under braking, a nose that points where the car goes. The signs and the scale are the game's, as they
/// were read off screenshots of a car sent known values (dd-grid SPEC, "What looking at it found").
/// </summary>
public class PostureTests
{
    private static Vector3 ForwardOf(float heading) => new(-MathF.Sin(heading), 0, MathF.Cos(heading));

    /// <summary>
    /// A loop of radius <paramref name="radius"/>. Turning right is a heading that grows: that is the game's
    /// way round, seen on the screen.
    /// </summary>
    private static Lane Circle(float radius, bool right)
    {
        const int count = 720;
        var length = 2 * MathF.PI * radius / count;
        return new([.. Enumerable.Range(0, count).Select(i =>
        {
            var heading = (right ? 1 : -1) * i * 2 * MathF.PI / count;
            var position = right
                ? new Vector3(radius * MathF.Cos(heading), 0, radius * MathF.Sin(heading))
                : new Vector3(-radius * MathF.Cos(-heading), 0, radius * MathF.Sin(-heading));
            return new LanePoint(position, i * length, length, radius, 8f, 8f, ForwardOf(heading), Vector3.UnitY, 0f, 0f);
        })]);
    }

    private static Lane Straight() => new([.. Enumerable.Range(0, 1000).Select(i =>
        new LanePoint(new Vector3(0, 0, i), i, 1f, 3000f, 5f, 5f, new Vector3(0, 0, 1), Vector3.UnitY, 0f, 0f))]);

    /// <summary>Drives a bot for a few seconds at a steady pace and returns what it sends then.</summary>
    private static CarState Settled(Lane lane, float lapSeconds)
    {
        var bot = new Bot(lane, SpeedProfile.ForLapTime(lane, CarLimits.Nominal, lapSeconds));
        for (var i = 0; i < 100; i++) bot.Advance(0.05f);
        return bot.State();
    }

    [Fact]
    public void steers_into_a_corner_the_way_it_turns()
    {
        // A 50 m corner with a 2.45 m wheelbase: about 2.8 degrees of lock, which the game takes in half degrees.
        var right = Settled(Circle(50f, right: true), 20f);
        var left = Settled(Circle(50f, right: false), 20f);
        Assert.InRange(right.WheelAngle, 131, 136);
        Assert.InRange(left.WheelAngle, 118, 123);
        Assert.Equal(127, Settled(Straight(), 30f).WheelAngle);
    }

    [Fact]
    public void leans_out_of_a_corner_and_not_on_a_straight()
    {
        // Positive roll puts the left side down: the lean of a right-hand corner.
        Assert.True(Settled(Circle(50f, right: true), 20f).Rotation.Z > 0.005f);
        Assert.True(Settled(Circle(50f, right: false), 20f).Rotation.Z < -0.005f);
        Assert.Equal(0f, Settled(Straight(), 30f).Rotation.Z, 3);
    }

    [Fact]
    public void squats_when_it_pulls_away_and_dips_when_it_brakes()
    {
        // A kilometre in fifteen seconds: a car that pulls away like a racing car.
        var lane = Straight();
        var bot = new Bot(lane, SpeedProfile.ForLapTime(lane, CarLimits.Nominal, 15f));
        bot.StartFrom(100f);
        for (var i = 0; i < 10; i++) bot.Advance(0.05f);
        Assert.True(bot.State().Rotation.Y > 0.005f, "the nose comes up under power");

        // A stopped car ahead with both sides taken: it has to brake hard.
        var blocked = new Surroundings(20f, 0f, true, true, 0f);
        for (var i = 0; i < 60; i++) bot.Advance(0.05f);
        for (var i = 0; i < 6; i++) bot.Advance(0.05f, blocked with { GapAhead = 20f - i });
        Assert.True(bot.State().Rotation.Y < -0.005f, "the nose goes down under braking");
    }

    [Fact]
    public void points_where_it_goes_while_changing_line()
    {
        // Started out on the left of the line: it comes across to the right, and the nose points that way
        // instead of the car sliding sideways with its nose along the line.
        var lane = Straight();
        var bot = new Bot(lane, SpeedProfile.ForLapTime(lane, CarLimits.Nominal, 15f));
        bot.StartFrom(100f, lateralOffset: 3f);
        for (var i = 0; i < 40; i++) bot.Advance(0.05f);
        var state = bot.State();

        Assert.InRange(bot.LateralOffset, 0.3f, 2.9f);
        Assert.True(state.Rotation.X > 0.002f, $"heading {state.Rotation.X:F4}: the nose should point right, towards the line");
        // And the car moves the way it points.
        var along = new Vector3(state.Velocity.X, 0, state.Velocity.Z);
        Assert.True(Vector3.Dot(Vector3.Normalize(along), ForwardOf(state.Rotation.X)) > 0.999f);
    }

    [Fact]
    public void encodes_the_lock_in_half_degrees_right_positive()
    {
        Assert.Equal(127, CarState.EncodeWheelAngle(0f));
        Assert.Equal(147, CarState.EncodeWheelAngle(10f));
        Assert.Equal(107, CarState.EncodeWheelAngle(-10f));
        // No car locks further than that.
        Assert.Equal(197, CarState.EncodeWheelAngle(90f));
        Assert.Equal(57, CarState.EncodeWheelAngle(-90f));
    }
}
