# Match presentation overhaul — visual evidence (2026-11-21)

Owner-requested UI batch: the in-match HUD was rebuilt around a scoreboard and two player cards, the old
debug/control text wall was hidden, and the Match Setup "Debug mode" switch now turns on an on-pitch AI debug
overlay instead of more text.

- **Build:** `dotnet build FootballSimulation.sln -c Debug` (0 warnings).
- **Simulation version:** `SimulationVersion.Tag = ai51-rs1`. This batch changes no simulation code and does not
  bump `AiBehaviour`; the HUD and the debug view only read match state.
- **Match captured:** seed `20260818`, 11 v 11, 4-minute halves, created by launching
  `res://MainMatchScene.tscn` directly (main scene = menu; direct scene launch is testing only, the menu path is
  the product path). `hud-normal.png` and `debug-on.png` are 1280 × 720 client captures.
- **How the debug frame was captured:** `MatchSetupOptions.DebugMode` was temporarily defaulted to `true` for the
  capture session only, then reverted before the commit. The menu option and GameSettings persistence are the
  production path; F3 does the same toggle in a match.
- **Tests:** full suite `1047/1047` green (the new test is `MatchSetup_DebugMode_IsOffByDefaultAndCarriedToTheMatchScene`).

## Files

| File | Shows |
| :--- | :--- |
| `hud-normal.png` | Clean in-match HUD. Top-left scoreboard; bottom-left active human player (#9 striker: stamina 100 %, power empty because no charge was held); bottom-right focus card (here the ball carrier, #2 right back, stamina 98 %). No debug, control or context text. |
| `debug-on.png` | The same match with Debug mode on. The old text is still hidden; the AI's state is drawn on the pitch. |
| `match-setup-debug-option.png` | Match Setup page: the new persisted **Debug mode** option (Off / On) and its explanation paragraph. Captured at 2560 × 1440 because `settings.cfg` on this machine stores that window size. |
| `headless-debug.txt` | `--headless --quit-after 240` run with debug mode enabled: clean boot, no script errors. |

## Debug overlay legend

Watched team is cyan, the other team is red. **F4** swaps which team the map, belief and perception layers show;
team-coded geometry swaps with it. There is deliberately no in-game legend (the whole point is to keep text off
the pitch), so this table is the reference.

| Visual | AI model |
| :--- | :--- |
| Cell tint: cyan/red grid on the grass | A2.4–A2.6 whole-pitch control map (`SpatialOverlayMap`): the watched team's own live players against the opponents it *believes* in. Stronger arrival wins the cell; alpha is the combined share. |
| Amber ring (radius grows with age), dashed link to a body | A2.2/A2.6 belief vs truth: the ring is where the watched team believes an opponent is; the dash joins it to the real body when the belief is stale. |
| Team-coloured hairline across the pitch + faint strip | A6.3 defensive-line band: bright line = standing depth, strip = allowed `MinProjX..MaxProjX` range. Amber ring on a runner = credible run in behind capping the line. |
| Small coloured disc at a player's target, dashed line to it | A3.3/A3.4 persistent job anchor. Colour = duty: yellow ball responder, orange presser, red marker, light blue defensive line, green depth run, teal overlap, lime outlet, violet cover/third-player, magenta protect-gap. White ring = temporary responsibility. |
| Red dashed line player-to-player | A6.1/A6.3 marking link (marker duty to the marked opponent). |
| Cyan ring around a player | A6.1 elected ball responder. |
| White ring + link at the ball, dashed link to a receiver | Ball state: carrier at feet, latest ball velocity, planned pass receiver (A4/`BallPossession`) and its line. |
| Dashed line to a ring on the grass | A5.3 advertised run's meeting region (colour = team, opacity = promise confidence). |
| Violet dashed line to a ring | A8.1 combination return region the receiver must play next. |
| Orange ring on the carrier, orange/amber points and links | A8.4 short trap contract: presser point, screen point, allocator cover. Drawn from the contract's own believed carrier position. |
| Gold flag + gold ring + dashed taker link | A8.5 live restart routine: the placed ball, the named receiver and the taker's delivery. |
| Magenta ring + dashed owner link | A3.5 live coverage request region and its owner. |
| Thin short dashed lines between players | A7.2 heard team call, caller to receiver, watched team only. |
| Pale wedge + gaze line + green/amber dots | A2.2/A7.1 sight cone and current picture: green = seen this tick, amber = memory, ring = growing position uncertainty; the white ring away from the ball is the perceived ball (reaction delay/noise). |
| Pink arrow + target ring | A4.6 last selected AI candidate's target (only when the active player is AI, e.g. spectator mode). |

## Known evidence gaps

- Debug mode was reviewed only on the standard 11 v 11 match, not on the 5-a-side path, training drills or
  full-time/half-time banners. Those code paths are shared, but a human playtest of them is still outstanding.
- The screenshots are static frames. Motion readability (whether the layers can be watched live) is the owner's
  call; the batch only proves the overlay renders and is read-only with respect to the simulation.
