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
    public void a_car_that_is_hit_loses_time_to_one_that_is_not()
    {
        var lane = Oval();
        var profile = SpeedProfile.ForLapTime(lane, CarLimits.Nominal, 60f);
        var clean = new Bot(lane, profile, startDistance: 200f);
        var hit = new Bot(lane, profile, startDistance: 200f);
        var contact = new Surroundings(3f, 20f, false, false, 1f);

        var pushedTo = 0f;
        var widestStep = 0f;
        for (var step = 0; step < 100; step++)
        {
            clean.Advance(0.05f);
            var before = hit.LateralOffset;
            hit.Advance(0.05f, step == 10 ? contact : Surroundings.Clear);
            widestStep = MathF.Max(widestStep, MathF.Abs(hit.LateralOffset - before));
            if (step is > 10 and < 30) pushedTo = MathF.Max(pushedTo, MathF.Abs(hit.LateralOffset));
        }

        // On this oval that is about four tenths of a second, which is what a shove costs.
        Assert.True(hit.Distance < clean.Distance - 3f, $"only {clean.Distance - hit.Distance:F1} m behind");
        // Shoved across within a moment of the contact, and back on the line by the end of it.
        Assert.True(pushedTo > 0.5f, $"only pushed to {pushedTo:F2} m");
        Assert.Equal(0f, hit.LateralOffset, 0.1f);
        // Slid across rather than put there: no step of a twentieth of a second jumps more than a car could slide.
        Assert.True(widestStep <= 4f * 0.05f + 0.001f, $"jumped {widestStep:F2} m in one step");
    }

    [Fact]
    public void a_driver_who_makes_mistakes_is_slower_than_one_who_does_not()
    {
        var lane = Oval();
        var profile = SpeedProfile.ForLapTime(lane, CarLimits.Nominal, 60f);
        var perfect = new Bot(lane, profile);
        var human = new Bot(lane, profile, mistakeSeed: 7);

        var perfectLaps = new List<uint>();
        var humanLaps = new List<uint>();
        for (var step = 0; step < 20_000 && perfectLaps.Count < 10; step++)
        {
            if (perfect.Advance(0.05f) is { } a) perfectLaps.Add(a.TimeMs);
            if (human.Advance(0.05f) is { } b) humanLaps.Add(b.TimeMs);
        }

        var both = Math.Min(perfectLaps.Count, humanLaps.Count);
        Assert.All(perfectLaps, lap => Assert.InRange(lap, 59_900u, 60_100u));
        // Mistakes cost tenths over a run, not seconds in a lap: nobody falls off the road.
        Assert.True(humanLaps.Take(both).Sum(l => (long)l) > perfectLaps.Take(both).Sum(l => (long)l), "the mistakes cost nothing");
        Assert.All(humanLaps, lap => Assert.InRange(lap, 59_900u, 62_000u));
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
