using DDGrid.Core.Protocol;

namespace DDGrid.Core;

public enum RacePhase
{
    /// <summary>Standing in its grid box, waiting for the lights.</summary>
    OnGrid,
    /// <summary>Out on track.</summary>
    Racing,
}

/// <summary>
/// One simulated driver's race: it lines up where the server says it starts, waits for the lights, takes
/// a moment to react, and then drives the line and reports every lap it finishes.
/// </summary>
public sealed class RaceBot
{
    private readonly IRaceLink _link;
    private readonly Lane _lane;
    private readonly IReadOnlyList<TrackSlot> _grid;
    private readonly float _reactionSeconds;
    private TrackSlot? _box;
    private float _reactionLeft;

    /// <param name="grid">The track's grid boxes. Empty means there is nowhere to line up, so it drives.</param>
    /// <param name="reactionSeconds">How long this driver takes to get going when the lights go out.</param>
    public RaceBot(IRaceLink link, Lane lane, SpeedProfile profile, IReadOnlyList<TrackSlot> grid, float reactionSeconds = 0.3f)
    {
        _link = link;
        _lane = lane;
        _grid = grid;
        _reactionSeconds = reactionSeconds;
        Bot = new Bot(lane, profile);
    }

    public Bot Bot { get; }

    public RacePhase Phase { get; private set; } = RacePhase.Racing;

    /// <summary>Where this car starts, counting from pole, or -1 when it is not on the grid.</summary>
    public int GridPlace { get; private set; } = -1;

    public async Task TickAsync(float seconds)
    {
        var session = _link.Session;
        var toStart = _link.MillisecondsToStart;

        // A race that has not started yet: stand in the box. The server says when the lights go out, and
        // until it has said anything at all a car on a race server belongs on the grid.
        if (session.Type == SessionType.Race && _grid.Count > 0 && toStart is null or > 0)
        {
            LineUp(session, entering: Phase != RacePhase.OnGrid);
            if (_box != null)
            {
                Phase = RacePhase.OnGrid;
                _reactionLeft = _reactionSeconds;
                _link.Send(Bot.OnGrid(_box.Value));
                return;
            }
        }

        // Lights out. This driver takes a moment.
        if (Phase == RacePhase.OnGrid && _reactionLeft > 0 && _box != null)
        {
            _reactionLeft -= seconds;
            _link.Send(Bot.OnGrid(_box.Value));
            return;
        }

        Phase = RacePhase.Racing;
        var lap = Bot.Advance(seconds);
        _link.Send(Bot.State());
        if (lap.HasValue) await _link.CompleteLapAsync(lap.Value.TimeMs, lap.Value.Splits);
    }

    /// <summary>
    /// Takes the box that belongs to this car's place in the order the server sent. The boxes stand
    /// behind the line, so the car joins the racing line there and its first crossing is a lap, exactly
    /// as it is for a driver in the game. Without an order from the server the entry list is the order.
    /// </summary>
    private void LineUp(SessionSnapshot session, bool entering)
    {
        var place = session.GridPlaceOf(_link.SessionId);
        if (place < 0) place = _link.SessionId;

        if (place >= _grid.Count)
        {
            // More cars than boxes: this one starts from the line instead of standing in a field.
            _box = null;
            GridPlace = -1;
            return;
        }

        if (!entering && place == GridPlace) return;

        GridPlace = place;
        _box = _grid[place];
        Bot.StartFrom(_lane.DistanceOf(_box.Value.Position));
    }
}
