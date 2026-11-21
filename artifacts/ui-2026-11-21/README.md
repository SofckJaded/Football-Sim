# Match presentation + debug overlay — visual evidence (2026-11-21)

Owner-requested presentation work in two passes:
1. A minimal in-match HUD (scoreboard + two player cards) replacing the old debug-text HUD.
2. A **debug-overhaul**: the on-pitch debug view was rebuilt around what a reviewer needs to see — player
   state, marking/target tethers, pass forensics (intent vs. execution vs. turf physics), the trap radius and
   why a touch was refused, the pass corridor with time-scaled defender reach, and the carrier's frozen decision
   with a readable box. The old A2–A8 structural layers (belief ghosts, sight cone, job/anchor fan, run and
   combination regions, coverage/call/restart markers) were **replaced** by this view; the control map and the
   defensive-line bands were kept as background. The documented colour/shape meaning of the old layers is
   therefore gone with them — recoverable from git history if ever wanted again.

- **Build:** `dotnet build FootballSimulation.sln -c Debug` (0 warnings).
- **Simulation version:** `SimulationVersion.Tag = ai51-rs1`. Both passes are presentation-only: no simulation
  or decision code changed and `AiBehaviour` was not bumped. Nothing here writes match state or draws simulation
  randomness; the resting point and ball ETAs replay a **clone** of the live ball through a second
  `BallPhysicsSolver` with the match's own pitch, and the label layer only projects engine state to the screen.
- **Match captured:** seed `20260818`, 11 v 11, 4-minute halves, launched as `res://MainMatchScene.tscn`
  directly (testing-only path; the menu is the product path). Frames are 1280 × 720.
- **How the rendered frames were captured:** `MatchSetupOptions.DebugMode` was temporarily defaulted to `true`
  and a temporary environment-gated harness saved viewport PNGs at 2 s intervals and in short bursts around
  every pass kick, then quit after the half. Both hacks were reverted before the commit; the menu switch and F3
  are the production path.
- **Tests:** full suite `1047/1047`.

## Files

| File | Shows |
| :--- | :--- |
| `hud-normal.png` | Clean HUD from the first pass (scoreboard, player cards; no debug text). |
| `debug-on.png` | The first-pass debug overlay (the older A2–A8 layer set) — kept only as history; the current overlay is the overhaul below. |
| `match-setup-debug-option.png` | Match Setup: the persisted **Debug mode** Off/On row and its explanation. |
| `debug-overhaul-decision.png` | Current overlay: the frozen decision box (top-right, with `FROZEN x.xs` countdown) plus the pitch option-line fan — green chosen, yellow alternatives, red refusals with reason and shirt number — over the player state labels and tethers. |
| `debug-overhaul-pass.png` | Current overlay, ball in flight: cyan intended-target X (ground and head height), solid white passer line, dashed yellow initial-velocity arrow with speed, cyan trap radius at the receiver, orange spoked defender-reach circles flagged on the corridor, magenta resting ring + link + "stopped … past", ETA plate, dotted receiver vector, corridor rectangle. |
| `debug-overhaul-touch-rejection.png` | A 2 s `WRONG_STATE` touch-rejection tag above a player with a ball inside the trap radius that was not controlled. |
| `debug-overhaul-capture-log.txt` | Console log of the capture run: harness save count, and the touch-rejection probe lines proving the reasons fired (removed from the committed code). |
| `headless-debug.txt` | `--headless --quit-after 240` boot with debug mode: clean, no script errors. |

## Current overlay legend

Text is drawn in **screen space at a fixed pixel size** (13–15 px) with a priority + collision-avoidance
planner, so labels stay the same size at any camera distance/zoom and move apart instead of overprinting; the
pass/touch labels get a dark plate. Priority: pass forensic plate > touch rejection > pass geometry > frozen
option scores > player states.

| Visual | Meaning |
| :--- | :--- |
| Small coloured word over every player | Player state from the team context/jobs: `ON_BALL`, `RECEIVING`, `KEEPER`, `KEEPER_HOLD`, `PRESSING`, `MARKING`, `COVERING`, `LINE`, `REST_DEFENCE`, `COVER_GAP`, `OUTLET`, `THIRD_OPTION`, `SUPPORT_RUN`, `OVERLAP`, `HOLD_WIDTH`, `SUPPORT`, `DEFENDING`, `CHASE`, `RECOVERY`, `COUNTER_PRESS`, `COUNTER_RUN`. Green = the player's team is in the attacking phase, blue = defending. |
| Blue line + dot at the far end | Marking tether: the `MARKING` defendant to the exact opponent he is responsible for. |
| Green line or green dashes + green X | Attacker's target tether: the coordinate he is sprinting toward (solid = current job anchor, dashed = an advertised run meeting region). |
| Cyan X (on the ground and a smaller one at head height) | The pass's **intended target** — the point the planner actually aimed at (lead/announced region), not just the receiver's feet. Head-height copy keeps it visible when the receiver stands on it. |
| Solid white line, passer → X | The pass the carrier intended. |
| Dashed yellow arrow + `x.x m/s` | The **actual initial velocity** after execution error/inaccuracy. |
| Magenta ring + dashed link + `stopped x m short/past` | Where the ball would stop **untouched** under the match's drag/rolling/bounce model (or `would leave pitch` when the first line it crosses ends the play). The number is the distance from that point to the cyan X. |
| `Ball: x.xxs` / `Recv: x.xxs` plate at the X | Ball flight ETA to the X and the receiver's ETA to the X, both frozen at kick time. |
| Dotted pale line, receiver → X | Predicted receiver vector at kick time. |
| Cyan spoked wireframe circle at the receiver's feet | The exact trap/first-touch contact radius (`BallContactRules.ReachRadius`). |
| Red plate over a player (e.g. `TOO_HIGH`, `ON_COOLDOWN`, `WRONG_STATE`, `CONTESTED`) | Touch rejection: the ball was inside the trap radius and was not controlled. Reasons come from the execution layer's own attempt log where it exists (`BallTooHigh`, `InRecovery`, `FacingAway`, `LostArbitration`, `BallHeld`, `RestartTriggered`); the receiver case re-evaluates the same `BallContactRules` gates read-only. 2 s lifetime. |
| Blue/white rectangle along the pass line | Pass corridor, half-width = the widest opponent tackle/interception reach, so a body closer to the line than the rectangle is half-wide can play the ball. Blue = no defender can reach it in time, orange = at least one can. |
| Spoked circle around a defender near the corridor | Time-scaled defender reach: sprint distance during the ball's ETA to his point on the line. Blue = does not overlap the corridor, orange = overlaps (the pass was never safe even if he was not on the line at the kick). |
| Green / yellow / red straight lines from the carrier with a number or reason+shirt | Frozen carrier decision, held 3 s after it was made (the box shows the countdown): green = chosen with its score, yellow = other legal options with their scores, red dashed = refused options with the reason and the target's shirt number (`RANGE #7`, `BLOCKED #3`). They dim under a live pass so the pass forensics own the pitch. |
| Top-right box | The decision readout: title `<shirt> → <winner>`, `FROZEN x.xs`, then winner and up to two of {top alternative, most useful refusal}: rank, action, score, and `(P p | G gain | L loss)` — the three numbers the score is made of. A refused row prints `SKIP` and the reason. |

**Team colours:** the control map (background cell tint) shows the watched team's belief; **F4** swaps the
watched team. The overlay keeps no in-game text legend by design.

## Known evidence gaps

- The overlay is a **review tool, never a decision source**: it must stay read-only. The resting-point replay
  is a debug-side clone, not a prediction the AI uses.
- **The resting point is the free-roll answer, not a claim that the pass missed.** A completed pass is trapped
  by the receiver's first touch, so a "stopped 20 m past" reading means the ball was still rolling fast at the
  X (a firm, timed pass) — compare it with the cyan X and the ETA, and read a *short* fall-off as the
  power/physics mismatch the owner asked about. Passes that would leave the pitch now say so instead of drawing
  a ring outside the board.
- **Labels and tags were verified on 11 v 11 only.** 5-a-side, drills and the half-time/full-time banners share
  the paths but have no captured frames.
- The **pass target can be off-screen** when the broadcast camera lags a long ball; the overlay does not clamp
  the X/ETA card to the viewport edge (deliberate: it would lie about the location). The resting-ring link
  leads off-frame in that case.
- The HUD restyle in this batch was not separately re-captured after the debug work; the cards and scoreboard
  are visible in the debug frames and were verified in the first-pass captures.
- Touch rejection tags were observed for `WRONG_STATE` and `CONTESTED` (tackle-attempt arbitration) in the
  capture log; `TOO_HIGH`/`ON_COOLDOWN` are rarer in open play and were not caught on a saved frame, though the
  same code path produced the two observed reasons.
