# dd-grid

Simulated drivers for Digital Drivers races: a full grid on an online race server, so a handful of members
can race a proper field instead of each other alone.

They are not the race server's own AI. That one is traffic — it has no laps, no classification, and the
server only counts a lap that comes from a connected client. These drive as clients: they speak the game's
network protocol, take a slot of the entry list like a member does, and report their laps the same way, so
race control, live timing, the classification and the result need no change to see them.

## Layout

| Path | What |
| --- | --- |
| `src/DDGrid.Core/` | The racing line, the speed profile, the bots, their names, and the client that speaks the game's protocol. No dependencies. |
| `tools/DDGrid.TrackTool/` | Builds a track pack from an installed game. Run once per track. |
| `data/tracks/` | The track packs, one per track and layout. |
| `tests/DDGrid.Core.Tests/` | xUnit; tests marked `[GameFact]` skip themselves without the game. |
| `tests/DDGrid.RaceTests/` | Bots racing a real AssettoServer, checksums and all. |
| `SPEC.md` | What is being built, what is proven, and in what order. |

## Build and test

Requires the .NET 9 SDK.

```bash
scripts/check.sh
```

## Track packs

The race servers carry only the files they need for checksums, which leaves out the track models — so the
bots cannot read the grid boxes there and are told instead. On a machine with the game installed:

```bash
dotnet run --project tools/DDGrid.TrackTool -- \
    --game "/mnt/c/Program Files (x86)/Steam/steamapps/common/assettocorsa" \
    --track ks_nurburgring --layout layout_gp_a --out data/tracks
```

The tool says what it found and what looks wrong, and exits non-zero when something does. A track without
a race grid (the Nordschleife tourist layout, for one) says so: there is nowhere to line up.

## License

Private, all rights reserved. It is built against no AssettoServer code: the race tests only run the
server as a program.
