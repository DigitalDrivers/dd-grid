# dd-grid — simulated drivers for online races

What is being built, what of it is proven, and in what order. Every claim below was checked against
AssettoServer v0.0.55-pre35 (the version dd-link pins), the platform sources, and an installed game;
the file references say where.

## 1. The job

A Digital Drivers race with six members entered is six cars on track. Members want a full field. The
game can only do that offline, against its own AI, one driver at a time — which dd-desktop 0.4.0 already
offers. This gives them both: members race each other **and** a full grid, online, in one race.

## 2. Why not the server's own AI

AssettoServer has AI cars, but they are traffic:

- They are not clients, and a lap only counts when it comes from one: `SessionManager.OnLapCompleted`
  takes an `ACTcpClient` (`SessionManager.cs:123`).
- Without laps they never enter `CurrentSession.Results`, which is what the server's own standings are
  built from (`ACTcpClient.cs:935-960`), so they would have no position, no classification, no result.
- Their behaviour is written for traffic: states spawn ahead of players and despawn behind them.

Making them race means changing the server, which means a fork to maintain. Not this.

## 3. What is built instead

A bot joins the race server the way the game does: handshake and checksums over TCP, then position
updates, ping replies and completed laps over UDP. The server then treats it as a car like any other —
it appears in the entry list, in the standings, in the result, in dd-link's classification, in live
timing, in replays, and it can be penalised or thrown out. **The platform needs no new concept of a
driver.**

That this works is not a guess: `dd-link/tests/DDLink.ServerTests/FakeDriver.cs` already does it against
a real AssettoServer in CI, including reporting laps, a driver swap and a ban. The difference between
that and a racing bot is the driving, not the protocol.

The cost is known too. dd-link's load test measured 24 such cars at 20 updates a second: about 6 % of one
CPU core and 150 MB on the server (dd-link README, measured 2026-09-19).

## 4. What the server does not give us, and where it comes from

| Needed | Where it comes from |
| --- | --- |
| Racing line, curve radius, track width, camber, slope | The track's `ai/fast_lane.ai`, which **is** on the race servers. `src/DDGrid.Core/FastLane.cs` reads it. |
| Grid and pit boxes | The track models, which are **not** on the race servers. Built into a track pack once per track, see §6. |
| The session being driven | The handshake says which one is running. After that the server only tells a car when the car **asks**: it sends `SessionRequest` with the session it believes is running, and the server answers `CurrentSessionUpdate` when the two differ (`ACUdpServer.cs:132-137`). The bots ask once a second, as the game does. |
| Grid order | The `CurrentSessionUpdate` that answers a changed session, in order. Without one — a server with a single race session — the entry list is the order. |
| When the lights go out | `RaceStart`, carrying the start time and the server's time **both in the car's own clock** (`SessionManager.cs:497-512`), so there is no clock to keep in step: what is left is one subtraction. |
| Checksums to get in | Computed from the same content the server has: `system/data/surfaces.ini`, the track's `surfaces.ini` and `models.ini`, the track folder, and the car's `data.acd` (`ChecksumManager.cs:62-128`). The server names those files under its own track name, so a server asking for the patch asks for `content/tracks/csp/2651/../<track>/data/surfaces.ini` while reading the plain folder itself — `Checksums.RealPath` undoes that. |

The recorded speed in `fast_lane.ai` is the speed of whatever car recorded the line, not a fast lap — on
the Nürburgring GP line it peaks at 125 km/h. AssettoServer reads and throws it away
(`FastLaneParser.cs:157`), and so do we. The pace comes from the geometry and a target lap time (§7).

## 5. The blocker: Steam authentication

The race preset turns Steam authentication on (`dd-platform/apps/web/server/utils/race-preset.ts`,
`UseSteamAuth: true`). A bot has no Steam session ticket, and a missing ticket is refused
(`NativeSteam.cs:71-73`). A plugin cannot wave it through either: `RegisterSteam()` runs at
`Startup.cs:69`, before plugins are registered at `Startup.cs:72`, so `SteamSlotFilter` is the first
filter in the chain and no dd-link filter can get in front of it.

Two ways out; **A is the decision**:

- **A — off for bot races.** `UseSteamAuth: false` in the preset when the event has a bot grid. One line.
  Slots stay locked to SteamIDs (`GuidSlotFilter`), but the IDs are no longer proven, so someone who
  knows a member's SteamID and writes their own client could take that member's slot. These races are
  unranked, the server is not in the public lobby, and the address only reaches entered drivers through
  the app.
- **B — dd-link checks Steam itself.** With `UseSteamAuth: false` the server registers no Steam filter,
  and dd-link (registered before `WhitelistSlotFilter` and `GuidSlotFilter`) can register its own: wave
  through the known bot GUIDs, validate everyone else's ticket against the Steam Web API the way
  `WebApiSteam.cs` does. Keeps the proof for members. Costs about a hundred lines in dd-link and a Steam
  Web API key.

Decided 2026-09-20: A. B stays on the table for when bot races become a fixture.

Bot GUIDs are deliberately not SteamIDs: `1000 + n`. Anything below 76561197960265728 is not a Steam
account, so the platform can tell a simulated driver from a member by the number alone.

## 6. Track packs — done

`tools/DDGrid.TrackTool` reads an installed game and writes `data/tracks/<track>__<layout>.json`: the
grid boxes and pit boxes with position and heading, and the length of the racing line. It finds the
`AC_START_n` and `AC_PIT_n` dummies in the track's models by their node header instead of parsing a
300 MB model in full, follows the offsets in `models_<layout>.ini`, and checks the result against the
racing line.

Verified against the game: 24 grid boxes and 24 pit boxes on `ks_nurburgring/layout_gp_a`, matching the
game's own layout — two staggered columns 12 m apart down the straight for boxes 0-17, the spare boxes
18-23 parked sideways off the track, which is how Kunos built it. Also ks_nordschleife, ks_barcelona,
ks_red_bull_ring, rt_california_highway and imola (no layout). The Nordschleife tourist layout correctly
reports that it has no race grid at all.

A track needs its pack before it can have a bot race. That is one command and belongs in the content
workflow.

## 7. The driver model

**Pace.** The speed profile comes from the line's geometry, not from a physics engine:

1. `v(i) = sqrt(aLat * radius(i))`, capped at the car's top speed. On a straight the radius runs into the
   millions, so it is capped before the root.
2. A pass backwards through the lap applies the braking limit, a pass forwards the acceleration limit;
   twice around, because the lap is a loop.
3. The lap time that follows is `Σ length(i) / v(i)`. Scaling all three limits by `s` scales the lap time
   by `1/sqrt(s)`, so the `s` that hits a wanted lap time is `(simulated / target)²` — no search needed.

So a bot is one number: the lap time it should drive. The platform knows what members drive on that track
in that car, so a field can be set to "a second off pole" instead of a made-up percentage, and the `aiLevel`
of the existing bot races maps onto it: `target = reference * 100 / aiLevel`.

Per bot on top of that: a spread over the field, a little noise per lap, and a small chance of a mistake.

**Driving.** The bot holds a distance along the line and an offset across it. Every tick it moves to its
target speed within the limits, moves its offset towards where it wants to be, and sends position,
rotation, wheel speed, gear, engine speed, throttle and brake lights, all worked out from the line and
the speed. 20 updates a second, the rate the preset already sets.

**Traffic.** A bot sees every other car — the server sends it all of them. It turns their positions into
distances along the line, brakes for the car in front, gets a tow behind it on a straight, and moves
offset to pass when it is quicker and the side is free.

**Contact.** A bot is not simulated by the game's physics: a member who hits one feels the hit (the game
works contacts with other cars out locally), but the bot only reacts the way we make it. Contact within a
few metres costs it speed and pushes it sideways, in proportion to the closing speed. Without that the
bots look like they are on rails, which is the honest limit of this approach — see §11.

## 8. The race

| Session | What the bots do | Built |
| --- | --- | --- |
| Practice | Go out and drive laps. | yes |
| Qualifying | Drive laps at their pace and report them, so the grid order is theirs to earn. | yes, same as practice |
| Race | Take the grid box for their place in the order, stand still until the start time, launch after a reaction time of their own, race the laps, report every one with its sector times. | yes |
| After the flag | Slow down, drive in, park. | no, phase 2 |

Sector times ride along in the lap packet, the way the game sends them. The separate live `SectorSplit`
packet is not sent: the server only passes it on and nothing reads it (`ACTcpClient.cs:814-818`).

Waiting in the pit box before going out is not built either. A bot that joins simply drives.

## 9. The platform

- **events**: two columns, `bot_grid` (0 = none) and `bot_level` (per cent, as the offline bot races
  already use).
- **race-preset.ts**: `bot_grid` entries after the members' cars, each with a bot GUID and a livery of
  its own; `MAX_CLIENTS` raised by the same number; `UseSteamAuth` per §5; `REGISTER_TO_LOBBY=0` stays as
  it is, so a bot race is never in the public lobby. Plus one new preset file, `dd-grid.json`: the
  server's port, the track, and the roster with car, livery, name and target lap time.
- **race-control**: when the preset holds a `dd-grid.json`, start a second container next to the race
  server (`engine.ts` already builds the race container this way) with `NetworkMode:
  container:dd-race-<id>`, so the bots reach the server on `127.0.0.1` and no port is opened; content
  mounted read-only, as the race server has it.
- **result**: a race with a bot grid is not an official race. It gives XP and nothing else — the rule
  the offline bot races already follow (`progression-db.ts:77-81`). The ingest writes one `bot_races` row
  per member instead of an official result, and `loadOfficialRaces` skips those events. The table already
  has every field this needs: `ai_level`, `laps`, `field_size`, `position`, `laps_done`, `total_time_ms`,
  `best_lap_ms`, `cuts`, `xp`, and `botRaceXp()` works out the XP as it does today.
- **UI**: the bot grid on the event page, simulated drivers marked in the entry list and in live timing.

## 10. Order of work

| Phase | What | Done when |
| --- | --- | --- |
| 0 **done** | The protocol client, the checksums, the racing line, the speed profile, the names. | `scripts/check.sh` is green: three named bots join a real AssettoServer, it counts every one of their laps, and a bot whose content differs is thrown out. |
| 1 **done** | Grid start, the race procedure, lap and sector reporting. | A test race of 20 bots over 10 laps: all 200 laps counted by the server, every full lap within 200 ms of the target, nobody off the line, nobody thrown out. |
| 2 | Following, passing, tow, contact, mistakes, retirements. | Test race against simulated members that brake on purpose: no bot drives into them, positions change hands, a hit bot loses time. |
| 3 | Platform: columns, preset, sidecar, XP, UI. | e2e: an event with a bot grid runs, and every member gets their XP. |
| 4 | Tuning against real members on a real track. | A live test race with members. |

What Phase 0 answered along the way:

- The server sends the plain position updates unless a client announces the CSP feature `CUSTOM_UPDATE`
  (`ACTcpClient.cs:428`), which the bots deliberately do not. Announcing a CSP *build* is separate, and
  they do that, because `EnableClientMessages` and `EnableUdpClientMessages` are on by default and pull
  the server's minimum CSP version up to 2651.
- Bot laps arrive as real laps: the server logs them, counts them and puts the cars in order.
- A car is told about its session only when it asks (§4). A bot that never asks never learns that the
  race started, and would drive through the countdown.
- Scaling a car to a lap time scales what it can do everywhere, its acceleration included, so a standing
  start comes out of the same one number.

## 11. What this cannot do

The bots are not simulated by the game's physics. They can be scripted to react to contact, but they
cannot be shoved off the line the way a real car can, and side by side through a fast corner they will
look a little smoother than a human. Full physics would mean running a real game instance per race on a
Windows machine with a graphics card and feeding its AI cars in — an order of magnitude more
infrastructure for the last ten per cent, and a sync problem on top. Not now; the door stays open.

## 12. Decisions taken

1. **Steam authentication**: A — off for bot races, see §5.
2. **Where this lives**: `DigitalDrivers/dd-grid`, private. There is no AssettoServer code in here and
   none is needed; the race tests only run the server as a program.
3. **Names**: an entry list of drivers who do not exist (`Roster.cs`). They read like racing drivers so
   the field looks like a field, and none of them is a real person, so nobody can be taken for one. What
   marks them as simulated belongs on the platform, not in their names.
4. **Scoring**: XP only, no points and no rating — the rule the offline bot races already follow.
5. **Lobby**: never. `REGISTER_TO_LOBBY=0`, the app is the only way in.
