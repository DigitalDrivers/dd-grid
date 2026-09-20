using System.Numerics;
using Xunit;

namespace DDGrid.Core.Tests;

public class PaceTests
{
    /// <summary>An oval: two straights of 400 m and two corners of a given radius, a point a metre.</summary>
    private static Lane Oval(float cornerRadius = 50f)
    {
        var points = new List<LanePoint>();
        var distance = 0f;
        foreach (var (length, radius) in new[] { (400f, 5_000_000f), (157f, cornerRadius), (400f, 5_000_000f), (157f, cornerRadius) })
            for (var i = 0; i < length; i++)
            {
                points.Add(new LanePoint(new Vector3(0, 0, distance), distance, 1f, radius, 5f, 5f, new Vector3(0, 0, 1), new Vector3(0, 1, 0), 0f, 0f));
                distance++;
            }
        return new Lane([.. points]);
    }

    [Fact]
    public void slows_down_for_the_corners_and_runs_free_on_the_straights()
    {
        var profile = SpeedProfile.For(Oval(), CarLimits.Nominal);

        var inTheCorner = profile.At(480);
        var onTheStraight = profile.At(200);
        Assert.True(onTheStraight > inTheCorner * 1.5f, $"straight {onTheStraight:F1} m/s, corner {inTheCorner:F1} m/s");
        // A 50 m corner at 1.4 g is about 26 m/s, whatever else the profile does.
        Assert.InRange(inTheCorner, 24f, 28f);
        Assert.InRange(onTheStraight, 40f, CarLimits.Nominal.TopSpeedMs);
    }

    [Fact]
    public void brakes_before_the_corner_instead_of_at_it()
    {
        var profile = SpeedProfile.For(Oval(), CarLimits.Nominal);

        // 50 m before the first corner the car is already slower than in the middle of the straight.
        Assert.True(profile.At(350) < profile.At(200));
        Assert.True(profile.At(395) < profile.At(350));
    }

    [Fact]
    public void braking_reaches_back_through_the_start_line()
    {
        // The last corner of the lap ends at the start line, so the profile has to wrap around it.
        var lane = Oval();
        var profile = SpeedProfile.For(lane, CarLimits.Nominal);

        Assert.True(profile.At(lane.Length - 5) < profile.At(lane.Length - 100) * 1.2f);
        Assert.InRange(profile.At(0.5f), 20f, 40f); // still coming out of the last corner
    }

    [Theory]
    [InlineData(60f)]
    [InlineData(90f)]
    [InlineData(120f)]
    public void drives_the_lap_time_it_is_asked_for(float target)
    {
        var profile = SpeedProfile.ForLapTime(Oval(), CarLimits.Nominal, target);

        Assert.Equal(target, profile.LapTimeSeconds, 0.05f);
    }

    [Fact]
    public void a_tighter_corner_costs_time()
    {
        var wide = SpeedProfile.For(Oval(cornerRadius: 120f), CarLimits.Nominal);
        var tight = SpeedProfile.For(Oval(cornerRadius: 30f), CarLimits.Nominal);

        Assert.True(tight.LapTimeSeconds > wide.LapTimeSeconds);
    }

    [GameFact]
    public void drives_the_lap_time_it_was_set_to_round_a_real_track()
    {
        var lane = new Lane(FastLane.ReadFile(Path.Combine(GameFactAttribute.Path, "content/tracks/ks_nurburgring/layout_gp_a/ai/fast_lane.ai")));
        var profile = SpeedProfile.ForLapTime(lane, CarLimits.Nominal, 118f);

        // Three laps one after another, driven the way the bots drive them.
        var bot = new Bot(lane, profile);
        var laps = new List<uint>();
        for (var step = 0; step < 20_000 && laps.Count < 3; step++)
        {
            var lap = bot.Advance(SpeedProfile.StepSeconds);
            if (lap.HasValue) laps.Add(lap.Value.TimeMs);
        }

        Assert.Equal(118f, profile.LapTimeSeconds, 0.05f);
        Assert.All(laps, lap => Assert.InRange(lap, 117_800u, 118_200u));
    }

    [GameFact]
    public void puts_a_real_car_round_a_real_track_in_a_believable_time()
    {
        var lane = new Lane(FastLane.ReadFile(Path.Combine(GameFactAttribute.Path, "content/tracks/ks_nurburgring/layout_gp_a/ai/fast_lane.ai")));

        var profile = SpeedProfile.For(lane, CarLimits.Nominal);

        // A GT3 lap of the Nürburgring GP is about two minutes; the nominal car is in that country.
        Assert.InRange(profile.LapTimeSeconds, 100f, 150f);
        // And it is set to whatever is wanted.
        Assert.Equal(115f, SpeedProfile.ForLapTime(lane, CarLimits.Nominal, 115f).LapTimeSeconds, 0.05f);
    }
}
