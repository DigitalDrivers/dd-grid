using System.Numerics;
using DDGrid.Core.Protocol;
using Xunit;

namespace DDGrid.Core.Tests;

public class RaceBotTests
{
    /// <summary>A connection that remembers what the bot sent instead of a server.</summary>
    private sealed class FakeLink : IRaceLink
    {
        public byte SessionId { get; set; }
        public SessionSnapshot Session { get; set; } = SessionSnapshot.Unknown;
        public long? MillisecondsToStart { get; set; }
        public List<CarState> Sent { get; } = [];
        public List<(uint Time, uint[] Splits)> Laps { get; } = [];

        public void Send(in CarState state) => Sent.Add(state);

        public Task CompleteLapAsync(uint lapTimeMs, IReadOnlyList<uint> splits, byte cuts = 0)
        {
            Laps.Add((lapTimeMs, [.. splits]));
            return Task.CompletedTask;
        }
    }

    /// <summary>A straight kilometre along Z, so a distance is also a place.</summary>
    private static Lane Straight(int length = 1000) => new([.. Enumerable.Range(0, length).Select(i =>
        new LanePoint(new Vector3(0, 0, i), i, 1f, 3000f, 5f, 5f, new Vector3(0, 0, 1), new Vector3(0, 1, 0), 0f, 0f))]);

    /// <summary>
    /// Boxes behind the line, in two columns, the way a track has them — and a metre above the road,
    /// the way a track marks them.
    /// </summary>
    private static TrackSlot[] Grid(int count = 4) => [.. Enumerable.Range(0, count).Select(i =>
        new TrackSlot(i, new Vector3(i % 2 == 0 ? -3 : 3, 1, 990 - i * 6), 0f))];

    private static SessionSnapshot Race(params byte[] grid) => new(SessionType.Race, "Race", 3, 0, grid, 0);

    [Fact]
    public async Task stands_in_its_box_until_the_lights_go_out()
    {
        var link = new FakeLink { SessionId = 1, Session = Race(0, 1, 2, 3), MillisecondsToStart = 5000 };
        var bot = new RaceBot(link, Straight(), SpeedProfile.ForLapTime(Straight(), CarLimits.Nominal, 30f), Grid());

        for (var i = 0; i < 10; i++) await bot.TickAsync(0.05f);

        Assert.Equal(RacePhase.OnGrid, bot.Phase);
        Assert.Equal(1, bot.GridPlace);
        Assert.All(link.Sent, state => Assert.Equal(new Vector3(3, 0, 984), state.Position));
        Assert.All(link.Sent, state => Assert.Equal(Vector3.Zero, state.Velocity));
        Assert.Empty(link.Laps);
    }

    [Fact]
    public async Task starts_where_the_server_put_it_in_the_order_not_where_its_slot_is()
    {
        // Slot 3 qualified on pole, so it lines up in box 0.
        var link = new FakeLink { SessionId = 3, Session = Race(3, 0, 1, 2), MillisecondsToStart = 1000 };
        var bot = new RaceBot(link, Straight(), SpeedProfile.ForLapTime(Straight(), CarLimits.Nominal, 30f), Grid());

        await bot.TickAsync(0.05f);

        Assert.Equal(0, bot.GridPlace);
        Assert.Equal(new Vector3(-3, 0, 990), link.Sent[0].Position);
    }

    [Fact]
    public async Task puts_the_car_on_the_road_and_not_above_it()
    {
        // The markers of a real track stand about a metre up; the game drops a car onto the surface.
        var link = new FakeLink { SessionId = 0, Session = Race(0), MillisecondsToStart = 5000 };
        var bot = new RaceBot(link, Straight(), SpeedProfile.ForLapTime(Straight(), CarLimits.Nominal, 30f), Grid());

        await bot.TickAsync(0.05f);

        Assert.Equal(0f, link.Sent[^1].Position.Y);
    }

    [Fact]
    public async Task pulls_away_from_its_box_and_comes_across_onto_the_line()
    {
        var lane = Straight();
        var link = new FakeLink { SessionId = 1, Session = Race(0, 1), MillisecondsToStart = 100 };
        var bot = new RaceBot(link, lane, SpeedProfile.ForLapTime(lane, CarLimits.Nominal, 15f), Grid(), reactionSeconds: 0f);
        await bot.TickAsync(0.05f);
        Assert.Equal(3f, link.Sent[^1].Position.X); // box 1, the right-hand column

        link.MillisecondsToStart = -10;
        // Just under way: still out beside the line, not on it.
        for (var i = 0; i < 20; i++) await bot.TickAsync(0.05f);
        Assert.InRange(link.Sent[^1].Position.X, 2.5f, 3.01f);

        // A few hundred metres on it has come across.
        for (var i = 0; i < 200; i++) await bot.TickAsync(0.05f);
        Assert.Equal(0f, link.Sent[^1].Position.X, 0.01f);
    }

    [Fact]
    public async Task takes_a_moment_to_react_and_then_pulls_away()
    {
        var lane = Straight();
        var link = new FakeLink { SessionId = 0, Session = Race(0), MillisecondsToStart = 100 };
        // A kilometre in fifteen seconds: the car that drives that also accelerates like one.
        var bot = new RaceBot(link, lane, SpeedProfile.ForLapTime(lane, CarLimits.Nominal, 15f), Grid(), reactionSeconds: 0.5f);
        await bot.TickAsync(0.05f);

        link.MillisecondsToStart = -10;
        for (var i = 0; i < 9; i++) await bot.TickAsync(0.05f); // 0.45 s, still reacting
        Assert.Equal(RacePhase.OnGrid, bot.Phase);
        Assert.Equal(0f, bot.Bot.Speed);

        for (var i = 0; i < 20; i++) await bot.TickAsync(0.05f);
        Assert.Equal(RacePhase.Racing, bot.Phase);
        Assert.True(bot.Bot.Speed > 5f, $"still at {bot.Bot.Speed:F1} m/s");
        // It pulls away from its box instead of appearing on the line.
        Assert.True(link.Sent[^1].Position.Z > 990f);
    }

    [Fact]
    public async Task reports_the_lap_it_drove_with_its_sector_times()
    {
        var lane = Straight();
        var link = new FakeLink { SessionId = 0, Session = new SessionSnapshot(SessionType.Practice, "Practice", 0, 10, [], 0) };
        var bot = new RaceBot(link, lane, SpeedProfile.ForLapTime(lane, CarLimits.Nominal, 20f), []);

        for (var i = 0; i < 600; i++) await bot.TickAsync(0.05f); // 30 s, more than a lap

        var lap = Assert.Single(link.Laps);
        Assert.Equal(RacePhase.Racing, bot.Phase);
        // It went out at the pace of the line, so the lap is the time it was set to.
        Assert.InRange(lap.Time, 19_800u, 20_400u);
        Assert.Equal(3, lap.Splits.Length);
        Assert.True(lap.Splits[0] < lap.Splits[1] && lap.Splits[1] < lap.Splits[2], $"sectors {string.Join(", ", lap.Splits)}");
        Assert.Equal(lap.Time, lap.Splits[^1]);
    }

    [Fact]
    public async Task starts_the_next_race_at_nought_laps()
    {
        var lane = Straight();
        var link = new FakeLink { SessionId = 0, Session = Race(0), MillisecondsToStart = -1 };
        var bot = new RaceBot(link, lane, SpeedProfile.ForLapTime(lane, CarLimits.Nominal, 15f), Grid(), reactionSeconds: 0f);

        for (var i = 0; i < 600; i++) await bot.TickAsync(0.05f);
        Assert.True(bot.Bot.Laps > 0, "it never finished a lap");

        // The race is over and the next countdown starts: it lines up again and its laps are its own.
        link.MillisecondsToStart = 5000;
        await bot.TickAsync(0.05f);

        Assert.Equal(RacePhase.OnGrid, bot.Phase);
        Assert.Equal(0, bot.Bot.Laps);
    }

    [Fact]
    public async Task drives_straight_out_in_practice_and_lines_up_again_for_the_race()
    {
        var lane = Straight();
        var link = new FakeLink { SessionId = 0, Session = new SessionSnapshot(SessionType.Practice, "Practice", 0, 10, [], 0) };
        var bot = new RaceBot(link, lane, SpeedProfile.ForLapTime(lane, CarLimits.Nominal, 20f), Grid());

        await bot.TickAsync(0.05f);
        Assert.Equal(RacePhase.Racing, bot.Phase);
        Assert.Equal(-1, bot.GridPlace);

        link.Session = Race(0);
        link.MillisecondsToStart = 3000;
        await bot.TickAsync(0.05f);

        Assert.Equal(RacePhase.OnGrid, bot.Phase);
        Assert.Equal(new Vector3(-3, 0, 990), link.Sent[^1].Position);
    }
}
