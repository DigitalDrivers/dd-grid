using Xunit;

namespace DDGrid.Core.Tests;

public class RacecraftTests
{
    private const float Pace = 60f;          // what the driver would do alone, metres a second
    private const float Brake = 15f;         // metres a second a second

    private static Surroundings Ahead(float gap, float speed, bool left = false, bool right = false)
        => new(gap, speed, left, right, 0f);

    [Fact]
    public void keeps_its_own_pace_with_the_road_to_itself()
    {
        Assert.Equal(Pace, Racecraft.FollowingSpeed(Pace, Pace, Brake, Surroundings.Clear));
        Assert.Equal(Pace, Racecraft.FollowingSpeed(Pace, Pace, Brake, Ahead(500f, 10f)));
    }

    [Fact]
    public void launches_from_the_grid_instead_of_creeping_behind_the_car_in_front()
    {
        // Standing on the grid, one box behind another car that is also standing: go.
        Assert.Equal(Pace, Racecraft.FollowingSpeed(Pace, 0f, Brake, Ahead(12f, 0f)));
    }

    [Fact]
    public void stops_behind_a_stopped_car_instead_of_into_it()
    {
        // At sixty metres a second it takes 120 m to stop, so far away there is nothing to do yet.
        Assert.Equal(Pace, Racecraft.FollowingSpeed(Pace, Pace, Brake, Ahead(400f, 0f)));

        // Driven out: it comes up on a stopped car at full speed and has to stop short of it.
        var own = Pace;
        var gap = 400f;
        for (var step = 0; step < 2000 && own > 0.05f; step++)
        {
            var target = Racecraft.FollowingSpeed(Pace, own, Brake, Ahead(gap, 0f));
            own = MathF.Max(0f, own + Math.Clamp(target - own, -Brake * 0.05f, 8f * 0.05f));
            gap -= own * 0.05f;
        }

        Assert.True(gap > 2f, $"came to a stand {gap:F1} m from it");
        Assert.InRange(own, 0f, 0.1f);
    }

    [Fact]
    public void settles_behind_a_slower_car_at_a_gap_of_its_own()
    {
        // A quicker car comes up on a slower one and stays behind it instead of driving into it.
        var own = Pace;
        var gap = 120f;
        const float theirs = 45f;
        for (var step = 0; step < 400; step++)
        {
            var target = Racecraft.FollowingSpeed(Pace, own, Brake, Ahead(gap, theirs));
            own += Math.Clamp(target - own, -Brake * 0.05f, 8f * 0.05f);
            gap += (theirs - own) * 0.05f;
        }

        Assert.True(gap > 2f, $"ended up {gap:F1} m behind");
        Assert.InRange(own, theirs - 3f, theirs + 3f);
    }

    [Fact]
    public void takes_the_tow_on_a_straight_but_not_through_a_corner()
    {
        Assert.True(Racecraft.WithTow(Pace, Ahead(20f, Pace), radiusMeters: 3000f) > Pace);
        Assert.Equal(Pace, Racecraft.WithTow(Pace, Ahead(20f, Pace), radiusMeters: 60f));
        Assert.Equal(Pace, Racecraft.WithTow(Pace, Ahead(200f, Pace), radiusMeters: 3000f));
    }

    [Fact]
    public void steers_across_more_sharply_the_slower_it_goes()
    {
        // At racing speed a change of lane takes a hundred metres; pulling out of a queue at walking pace
        // takes a car length or two, or a car stood behind another could never get out.
        Assert.Equal(0.025f, Racecraft.AcrossSlopeAt(60f), 3);
        Assert.True(Racecraft.AcrossSlopeAt(3f) > 0.3f);
        Assert.True(Racecraft.AcrossSlopeAt(10f) < Racecraft.AcrossSlopeAt(3f));
    }

    [Fact]
    public void pulls_out_to_the_free_side_and_holds_it_until_past()
    {
        // Coming up on someone slower with both sides free: out it goes.
        var offset = Racecraft.WantedOffset(0f, Pace, Ahead(40f, 50f));
        Assert.Equal(Racecraft.OvertakeOffsetMeters, offset);

        // Still alongside: hold the line being taken rather than weaving back.
        Assert.Equal(offset, Racecraft.WantedOffset(offset, Pace, Ahead(6f, 50f, left: true)));

        // Past and clear: back to the racing line.
        Assert.Equal(0f, Racecraft.WantedOffset(offset, Pace, Surroundings.Clear));
    }

    [Fact]
    public void goes_the_other_way_round_when_one_side_is_taken()
    {
        Assert.Equal(-Racecraft.OvertakeOffsetMeters, Racecraft.WantedOffset(0f, Pace, Ahead(40f, 50f, left: true)));
    }

    [Fact]
    public void stays_in_line_behind_a_car_it_cannot_get_past()
    {
        // Both sides taken, or no quicker than the car in front: stay where you are.
        Assert.Equal(0f, Racecraft.WantedOffset(0f, Pace, Ahead(40f, 50f, left: true, right: true)));
        Assert.Equal(0f, Racecraft.WantedOffset(0f, Pace, Ahead(40f, Pace)));
    }

    [Fact]
    public void being_hit_costs_speed_and_pushes_the_car_across()
    {
        var (speed, offset) = Racecraft.AfterContact(60f, 0f, closingSpeed: 20f, fromSide: 1f);

        Assert.InRange(speed, 40f, 58f);
        Assert.True(offset < -0.8f, $"pushed to {offset:F2} m");

        // A brush costs less than a hit.
        var (gentle, _) = Racecraft.AfterContact(60f, 0f, closingSpeed: 3f, fromSide: 1f);
        Assert.True(gentle > speed);
    }
}
