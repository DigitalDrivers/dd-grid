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
| Grid and pit boxes | The track models, which are **not** on the race servers. Built into a track pack once per track, see §6. A track marks them about a metre **above** the road and the game drops a car onto the surface; a bot has no physics to fall with, so it takes its height from the racing line, which was recorded where a driving car sits. |
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

Bot GUIDs are deliberately not SteamIDs: `10000000000000000 + n`. Anything below 76561197960265728 is not
a Steam account, so the platform can tell a simulated driver from a member by the number alone — and they
are seventeen digits long all the same, because that is what the game and every message about a race
expect of a driver's id.

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
3. That gives the shape. What it costs is then **measured** by driving a lap of it in the same 50 ms steps
   the bots drive in: a car cannot change speed between two steps and cannot accelerate faster than it can,
   and over a lap of the Nürburgring that adds up to 1.6 s — 1.3 % — of quiet error. Scaling all three
   limits by `s` scales the lap time by `1/sqrt(s)`, so each round of measuring lands the next one closer.

So a bot is one number: the lap time it should drive. The platform knows what members drive on that track
in that car, so a field can be set to "a second off pole" instead of a made-up percentage, and the `aiLevel`
of the existing bot races maps onto it: `target = reference * 100 / aiLevel`.

Per bot on top of that: a spread over the field, a little noise per lap, and a small chance of a mistake.

**The start.** A car stands in its box, which is beside the line, and comes across onto it over the first
180 m. Without that the whole field snaps onto the racing line the moment the lights go out and drives away
in single file.

**Driving.** The bot holds a distance along the line and an offset across it. Every tick it moves to its
target speed within the limits, moves its offset towards where it wants to be, and sends position,
rotation, wheel speed, gear, engine speed, throttle and brake lights, all worked out from the line and
the speed. 20 updates a second, the rate the preset already sets.

**Traffic.** A bot sees every other car: the server sends every car's position to every client, so one
connection is the whole field. Their positions become distances along the line, and from there:

- **Following.** What matters is not the gap but what is left of it after shedding the speed difference,
  so the braking distance for that difference comes off the gap first. That is why a car stood one box
  behind another on the grid still launches, and why one arriving at a stopped car brakes 150 m early.
- **The tow.** Behind a car on a straight, three per cent; not in a corner, where the car in front takes
  the grip instead.
- **Passing.** Quicker than the car in front and a side free: out to that side, held until past, then back
  to the line. A car moves across by driving, so the offset follows a slope — about a hundred metres to
  change lane — not a speed.
- **Mistakes.** Every so often a corner comes out wrong and costs a couple of tenths. Each driver errs in
  their own way and always the same way, so a race can be run again.

**Contact.** A bot is not simulated by the game's physics: a member who hits one feels the hit (the game
works contacts with other cars out locally), but the bot only reacts the way we make it. Contact costs it
speed and pushes it across, in proportion to the closing speed, and it finds its way back to the line.
Without that the bots look like they are on rails, which is the honest limit of this approach — see §11.

Retirements are **not** built. A bot that stops is a parked car the rest of the field has to get round,
and a result the platform has to read as a retirement; both belong with the platform work in phase 3.

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
  mounted read-only, as the race server has it. dd-grid waits for the server to come up, so the order the
  two start in does not matter, and the track packs ship inside its image.
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
| 1 **done** | Grid start, the race procedure, lap and sector reporting. | A test race of 20 bots over 10 laps: all 200 laps counted by the server, every full lap within 250 ms of the target, nobody off the line they meant to be on, nobody thrown out. Seen in the game on a real server: the field in its grid boxes, the names and flags in the entry list, the standings with real gaps, 2:01 laps of the Nürburgring in a 911 Cup. |
| 2 **done** | Following, passing, tow, contact, mistakes. | A test race where the bots share the road with two drivers who do not react — one at a third of their pace, one standing on the racing line: no contact at all, the closest anyone came was 2.60 m, and everyone got by lap after lap instead of queueing up. A hit car ends up behind one that was not hit; a driver who errs is slower over a run than one who does not. |
| 3 **done** | Platform: columns, preset, sidecar, XP, UI. | The preset writes the bot slots and dd-grid's own config; race control starts dd-grid beside the server only for a race that asks for it; a race with a bot grid books XP for its members and never becomes an official result; the event page and the timing say which drivers are simulated. |
| 4 | Tuning against real members on a real track. | A live test race with members. |

What looking at it found (2026-09-22, a car parked in front of a real client and sent known values, a
screenshot of each). The cars looked like models on rails: the front wheels never steered, the body never
leaned, and a car changing line slid sideways with its nose along the line. What the game makes of the values:

- `WheelAngle` is the lock of the front wheels in half degrees either side of 127, above it to the right:
  +100 is about 45 degrees, seen from in front of the car. `SteerAngle` moves only the steering wheel inside;
  it stays at 127, nobody sees it from outside and the game gives no scale for it.
- The rotation is heading, pitch, roll. A growing heading turns the car to the right; a positive pitch lifts
  the nose; a positive roll lowers the left side, the lean of a right-hand corner.
- So a bot now points where it goes (the line's heading, turned by as much as it moves across), steers by
  the yaw rate over a 2.45 m wheelbase, leans 1.5 degrees a g out of a corner and 1 degree a g under power
  and braking, takes the banking and the slope of the road from the line, and slides across at 4 m/s when
  it is hit instead of jumping there.

What looking at it in the game found (2026-09-20, eight bots on the Nürburgring GP, a real client in the
field):

- The cars floated about a metre over the grid. A track's `AC_START` markers stand above the road and the
  game drops a car onto it; a bot has to be put on the surface itself — and a centimetre above it, because
  the racing line was recorded with the suspension loaded.
- They pulled away from their boxes in single file, because the start put them on the line at the box's
  distance and threw the offset away.
- They drove straight into a car parked on the racing line: a bot cannot see another car yet. Phase 2.
- They lapped 1.3 % slower than they were set to, which is where the measured lap time came from. With
  that in, a bot set to 1:58.000 drove 1:58.003 on the Nürburgring, and one set to 2:01.300 drove 2:01.253.
  The lap right after a standing start is two to three seconds slower, because the car is still picking up
  speed as it crosses the line for the first time — as a real one is.

What the first live bot race with a member found (2026-09-21, Nürburgring Sprint, the member at the back):

- The bots never lined up: 60 s before the start they were still lapping, then queued behind the member.
  The start time comes in the car's own clock cut to 32 bits, and was worked out in 64 bits — 49.7 days off
  once the host had been up for 24.9 days. Worked out in 32 bits now (`RaceClient.MillisecondsUntil`).
- A car stood behind one that does not move stayed there for good: stopped, it could not move across,
  and a car up to 4 m across still counted as in the way while passing takes it 2.6 m out. Now a driver
  creeps out past a stopped car and steers hard at walking pace; only a car within 2.2 m across slows it,
  and nobody turns back onto the line across a car still alongside.
- All bots vanished after two laps. Asking for the session once a second (§4) now and then lands in the
  moment the server has switched sessions but not set the grid yet; AssettoServer then fails to answer
  (`CurrentSessionUpdate` with a null `Grid`) and throws the car out. The bot kept driving on a dead
  connection and its next lap threw a `Broken pipe` nobody caught, which ended the process. The server
  tells every car about a new session on its own once the grid is set, so a car asks only every 30 s
  now; a car that is thrown out anyway rejoins its slot and drives on, and one driver's error never stops
  the others. Proven against a real server: a bot put on the blacklist mid-race was refused while
  blacklisted and back on its slot ten seconds after it was lifted; the field raced on, 12 of 12.

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
