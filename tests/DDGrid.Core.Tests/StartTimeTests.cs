using DDGrid.Core.Protocol;
using Xunit;

namespace DDGrid.Core.Tests;

/// <summary>
/// The server says when the lights go out in the car's own clock — the tick count the car sent in its
/// pings — cut to 32 bits: the start as a signed number, "now" as an unsigned one.
/// </summary>
public class StartTimeTests
{
    /// <summary>What the server sends a car whose clock reads <paramref name="clientNowMs"/>, for a start that far off.</summary>
    private static (int StartTime, uint ServerTime) Sent(ulong clientNowMs, long toStartMs)
        => (unchecked((int)(uint)(clientNowMs + (ulong)toStartMs)), unchecked((uint)clientNowMs));

    [Fact]
    public void reads_the_start_on_a_machine_that_was_just_started()
    {
        var (start, now) = Sent(3_600_000, 55_000);
        Assert.Equal(55_000, RaceClient.MillisecondsUntil(start, now));
    }

    [Fact]
    public void reads_the_start_on_a_machine_that_has_been_up_for_weeks()
    {
        // Thirty days of uptime: the clock no longer fits a signed 32-bit number. Worked out in 64 bits the
        // start came out 49.7 days ago, so the bots drove straight through the grid procedure.
        var (start, now) = Sent(30UL * 24 * 3_600_000, 55_000);
        Assert.Equal(55_000, RaceClient.MillisecondsUntil(start, now));

        // And a start that has passed is still one that has passed.
        (start, now) = Sent(30UL * 24 * 3_600_000, -2_000);
        Assert.Equal(-2_000, RaceClient.MillisecondsUntil(start, now));
    }

    [Fact]
    public void reads_the_start_across_the_moment_the_32_bit_clock_rolls_over()
    {
        var (start, now) = Sent(uint.MaxValue - 10_000UL, 55_000);
        Assert.Equal(55_000, RaceClient.MillisecondsUntil(start, now));
    }
}
