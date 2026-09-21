using System.Numerics;
using Xunit;

namespace DDGrid.Core.Tests;

/// <summary>
/// A car stands still right in front: a member who does not get away at the start, or one who spun and
/// stopped. The field has to go round it, not queue up behind it for the rest of the race.
/// </summary>
public class StoppedCarTests
{
    /// <summary>A straight kilometre along Z, five metres of road either side of the line.</summary>
    private static Lane Straight() => new([.. Enumerable.Range(0, 1000).Select(i =>
        new LanePoint(new Vector3(0, 0, i), i, 1f, 3000f, 5f, 5f, new Vector3(0, 0, 1), new Vector3(0, 1, 0), 0f, 0f))]);

    [Theory]
    [InlineData(24f)]  // the next box on the same side of the grid
    [InlineData(12f)]  // half that: stopped right behind it on track
    public void gets_round_a_car_standing_still_right_in_front(float gapMeters)
    {
        var lane = Straight();
        var field = new Field(lane);
        var stopped = new CarOnTrack(9, 500f, 0f, 0f);
        var bot = new Bot(lane, SpeedProfile.ForLapTime(lane, CarLimits.Nominal, 30f));
        bot.StartFrom(500f - gapMeters);

        var closest = float.MaxValue;
        for (var step = 0; step < 20 * 20 && bot.Distance < 530f; step++)
        {
            field.Clear();
            field.Add(stopped);
            field.Add(bot.Seen(1));
            bot.Advance(0.05f, field.Around(1));

            var along = MathF.Abs(bot.Distance - stopped.Distance);
            var across = MathF.Abs(bot.LateralOffset - stopped.LateralOffset);
            if (along < 4.5f) closest = MathF.Min(closest, across);
        }

        Assert.True(bot.Distance >= 530f, $"still behind it after twenty seconds, {500f - bot.Distance:F1} m short");
        // Side by side with it, never over it: a car is about two metres wide.
        Assert.True(closest > 2f, $"came within {closest:F2} m across while alongside");
    }
}
