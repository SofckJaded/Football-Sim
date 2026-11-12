# Football simulation audit and implementation handoff

Date: 2026-11-06. Repository root: `C:\Games\geminiball`.

## Scope and current state

Reviewed every authored C# source file, the Godot scene/project configuration, existing tests, CLI, and project documentation. Built the full solution and ran the existing tests. Added a separate diagnostic console project and captured its output. **Production gameplay code has not been changed.** This is an audit and implementation handoff, with reproduced failures and proposed acceptance checks.

- Full solution build: **success, 0 warnings, 0 errors**.
- Existing xUnit suite: **12 passed, 0 failed**.
- Diagnostic runs: two 18,000-tick 11v11 samples using seed `20260818`, one autonomous and one with Home #9 human-controlled and idle; controlled passing, keeper, input, and rules scenarios.
- No Godot UI playtest was performed. Input and presentation findings below are source-level findings; the C# Godot project compiled successfully.
- There is no `.git` repository in the supplied workspace, so no commit baseline or history was available.
- “Reproduced” means observed with the included probe. “Inspection” means established from the source path but not reproduced as a full match scenario. Design recommendations are explicitly identified.

Priority: **P1** = fix before gameplay tuning; **P2** = functional correctness or important behavior; **P3** = presentation, documentation, or maintenance. No security assessment is implied.

## Start here: the three reported problems

### F01 — P1 — Pass prediction uses a fraction of travel time

**Reproduced.** `src/FootballSim/IndividualAI.cs:309–375`, `HumanInputController.cs:348–387`, `RulesAndGoalkeeper.cs:124–133`, `IndividualAI.cs:549–561`, and `FootballerLocomotion.cs:432–439`.

The code does lead ordinary AI passes, but only by `velocity * min(0.35, (distance / 18) * 0.45)`. A 20 m pass therefore predicts about 0.35 seconds of motion. Actual execution uses `8 + 11.5 * power` m/s, followed by drag and ground friction, so the ball arrives much later. A human call reduces power further without increasing prediction time. Through calls use a fixed 0.45-second velocity lead plus 2.5 m in the attacking direction. Keeper distribution and set-piece passes aim at the current receiver position. Human assisted passing has another fixed lead of 0.25 seconds.

The controlled 20 m pass to a runner moving laterally at 6 m/s missed by **8.065 m** at its closest approach, 1.35 seconds after launch. This isolates the pass planner and physical ball trajectory; the receiver continues at constant velocity and reception assistance is excluded. It is not a measurement of every real match pass.

Other contributing details:

- Lane clearance is scored toward the receiver's **current** position, before predicting the actual target.
- Aiming originates at the player's centre rather than the ball's contact position.
- Clamping a future target to the current offside line suppresses a legal run beyond that line after the kick. Receiver eligibility should be checked at release, separately from the eventual receiving position.
- There is no intended receiver or pass-arrival metadata, making coordinated reception difficult.

**Implementation:** create one shared pass planner used by outfield AI, keeper distribution, set pieces, and assisted human passes. Given ball position, receiver state, pass type, and desired arrival speed, solve for a future receiving point and a kick that can reach it. For an initial implementation, iterate flight-time estimation and receiver prediction a few times; use a calibrated lookup or a bounded ball-physics rollout that does not mutate the live ball, RNG, or solver time. Evaluate interception risk on the resulting path. Use bounded acceleration/turn prediction for the human and a short velocity filter; don't require knowing their future input. Return target, receiver ID, flight time, aim, power, and a feasibility result. Reject or change pass type if the runner cannot be reached.

**Acceptance:** deterministic 5/10/20/30 m scenarios, stationary/walking/sprinting receivers, lateral/forward/diagonal runs, both attack directions, AI and human recipients, normal and through calls. Compare actual closest approach against the configured reception reach. Check arrival speed as well as distance. Include a receiver changing direction, an opponent moving into the predicted lane, and a runner onside at release who receives beyond the old line.

### F02 — P1 — Keeper parries preserve incoming spin and can return into goal

**Reproduced, including the natural match trace.** `FootballerLocomotion.cs:357–387`, especially `377–385`; subsequent spin/ground coupling is in `BallPhysics.cs:159–202` and `227–236`.

A parry overwrites linear velocity but leaves angular velocity unchanged. The ball can carry roughly 100 rad/s of rolling spin into a save. After its velocity is redirected away from goal, the old spin still drives ground interaction in the incoming direction.

In the human-idle match, Away GK #12 parried at **tick 1540**. The outgoing ball had velocity approximately `(-1.767, 1.249, -6.726)` m/s, away from the keeper's own positive-X goal, but retained spin `(81.801, 0, -93.741)` rad/s. At **tick 1627**, it crossed that goal line at `(52.640, 0.110, -1.631)` without another player touch.

The probe replays that exact outgoing ball state using ball physics alone:

| Replay | Result |
| --- | --- |
| Retain original spin | Ball returns into the keeper's own goal |
| Clear spin, same position and velocity | Ball continues away; no goal |

This establishes a direct cause for the reported keeper behavior. A separate low-spin parry did not reverse; this is not a claim that every save produces an own goal.

**Implementation:** model a parry's angular impulse along with its linear impulse. A practical first correction is strong spin damping or resetting spin on a hand contact, then assigning a small controlled outgoing spin. Select a safe exit path using goal geometry, not just the keeper's current facing. Validate the full resulting flight and bounce. Preserve legitimate rebound opportunities.

**Acceptance:** replay the tick-1540 state and its mirrored counterpart. Test rolling/high-spin shots, aerial shots, both sides of goal, and keepers facing sideways or backward. A successful parry directed away from goal must not reverse into goal solely because it retained incoming rolling spin.

### F03 — P1 — A “clean catch” leaves a loose ball; keeper then chases/tackles it

**Reproduced.** `FootballerLocomotion.cs:361–373`, `RulesAndGoalkeeper.cs:31–35,66–75`, and `FootballerLocomotion.cs:299–306,524–545`.

`GK:CleanCatch` places the ball 0.42 m in the keeper's facing direction on the ground, gives it 1 m/s, and blocks further keeper contact for 0.65 seconds. There is no held-ball state. During recovery, the controller cannot choose distribution, so it chooses `GK:Rush1v1Sweep`, even for a ball the keeper just caught. The keeper can run past it, turn back, and use the generic tackle branch to propel it toward the goal.

With a Home keeper at X=-51.9 m:

- Facing toward own goal: “catch” followed by own-goal-line crossing in **31 ticks**.
- Initially facing upfield: “catch”, rush, `TackleWonBall!` at ticks 39 and 62, and own-goal-line crossing at **tick 82**.

These are controlled close-to-line scenarios with no other players. They isolate the keeper lifecycle rather than measure match-wide frequency.

**Implementation:** add explicit keeper states such as `Positioning`, `Saving`, `HoldingBall`, `Distribution`, and `Recovering`. A caught ball must be held authoritatively, excluded from free-ball integration and opponent foot contacts, until an actual release. Do not rush or tackle a held/friendly controlled ball. A foot collection can remain a free-ball interaction, but needs a distinct control state and safe movement. Carry the keeper's team/attack direction into contact execution rather than inferring safety from facing. Coordinate this change with F02 and F12.

**Acceptance:** caught ball remains secure for the entire recovery interval; both facing-direction probes stop producing own goals; keeper releases exactly once toward a reachable teammate or safe outlet. Test backward-facing catches, saves at both goal lines, friendly back-passes handled with the feet, interrupted distribution, and keeper replacement/removal.

### F04 — P1 — Off-ball AI mostly has only one formation-following option

**Inspection plus measured action counts; coordinated behavior is a design improvement.** `IndividualAI.cs:139–193,413–520`, `TacticsAndTeamAI.cs:135–197`.

For a primary responder, `GenerateOffBallCandidates` adds one chase/receive candidate and returns. Every other outfield AI gets one support/hold command. There is no utility competition between marking, covering, overlapping, making a run, offering a short outlet, or intercepting. Most variation is a formation anchor, sine/cosine displacement, and repulsion from teammates. Support targets do not consider opponents or whether the ball carrier has a passing lane to that location.

| Five-minute sample | Support/hold decisions | All AI outfield decisions | Share |
| --- | ---: | ---: | ---: |
| Autonomous 11v11 | 323,936 | 360,000 | 89.98% |
| Human Home #9 idle | 308,616 | 342,000 | 90.24% |

These measure **selected actions, not physical immobility**. Formation movement itself is expected for many players; the problem is the absence of situational jobs and useful alternatives.

**Implementation in stages:**

1. At team level, assign persistent jobs relative to the actual carrier/loose ball. In possession: one near outlet, one diagonal/third-player option, one depth run, far-side width, and defensive cover. Out of possession: one presser, one cover player, nearby marking/interception assignments, and a shifting defensive line. Keep remaining players in shape.
2. Generate a small set of reachable target points per job. Score passing-lane visibility, opponent separation, travel cost, progress, width, offside eligibility, and role discipline. A nearby support player should move out of an opponent's cover shadow.
3. Commit to a **job and target** for a bounded period, with urgent interrupts on possession changes. Current commitment is only a 0.10 bonus to an action name for 0.18 seconds, so it does not preserve a run target or receiver assignment.
4. Use formation anchors as a constraint/fallback. Replace periodic forward oscillation with runs triggered by carrier control, body direction, available space, and defensive line movement. Add fullback overlaps, midfielder support triangles, striker checking short/spinning behind, and far-side winger movement incrementally.
5. Add debug labels for job, target, receiver, and reason. Tune movement after keeper/rules/possession defects are fixed; their state corruption currently contaminates tactical evaluation.

**Acceptance:** with a stationary human carrier, nearby AI creates multiple reachable passing options and at least one contextual run; after a turnover, a presser and cover defender act promptly while shape remains coherent. Cover an idle human near a loose ball, opposing low/high blocks, both directions, and 5v5. Track open passing options, completed passes, penetrative runs, unmarked threats, responder stability, and distances travelled. Do not optimize for constant motion or arbitrarily increase all run speeds.

## Other correctness findings

### F05 — P1 — Half identity is conflated with temporary restart phase

**Reproduced.** `RulesAndGoalkeeper.cs:175,435–450`.

`GetHomeAttackDirectionX()` returns -1 only when `Phase == SecondHalf`. During a second-half throw-in, free kick, corner, goal kick, penalty, or goal celebration, it returns +1. Controllers and tactical anchors therefore use first-half directions during those phases. Restarts then choose a half using hard-coded `MatchClockSeconds < 2700`, ignoring `HalfDurationSeconds`.

The probe enters the second half, triggers a throw-in, observes home direction +1, then resumes. With one-second halves it resumes **FirstHalf**; with 2700-second halves it resumes SecondHalf but still uses the wrong direction while the restart is active.

**Fix/check:** store period independently from play/restart phase; use configured half length throughout. Test every restart and a goal in both halves at short and normal durations, preserving direction, score ownership, and clock progression.

### F06 — P1 — Rules pauses do not pause physical execution; full time does not stop the engine

**Reproduced.** `MatchEngine.cs:138–194`, `RulesAndGoalkeeper.cs:192–200`.

Controllers, movement, contacts, and ball physics run before rules check pause/full time. In the throw-in probe the ball was already moving at roughly 19 m/s horizontally while `PhasePauseTimer` still had **0.233 s** remaining. Players can kick/tackle and the ball can leave play while rule evaluation is suspended. `StepTick()` also moves the ball after FullTime, and Godot continues calling it.

**Fix/check:** define legal execution per match state at the engine boundary. Freeze ball/kick/contact execution during setup/whistle delays; allow only explicitly supported repositioning. Await an actual restart contact before entering live play. Stop authoritative gameplay at full time. Test that no score, possession, contact, or movement changes occur during the corresponding stopped states.

### F07 — P1 — Kickoff taker flag persists into open play

**Reproduced.** `RulesAndGoalkeeper.cs:453–497`, `IndividualAI.cs:125–129`.

Kickoff assigns `DesignatedSetPieceTakerId` but does not clear it when the kickoff is taken. After 600 ticks it still identified Home #8 even though the ball was last touched by #1. Whenever #8 later comes within 4.5 m, AI bypasses normal decisions and runs `EvaluateSetPieceRestart`, even when phase is FirstHalf. Setup also does not reset the old taker before searching for a new eligible one; a sent-off kickoff candidate can leave a stale designation.

**Fix/check:** use an explicit pending-restart context, consume it on the first valid kick, and clear it on reset. Restrict restart AI to an active restart. Test subsequent open-play possession by the kickoff taker and a missing/sent-off preferred taker.

### F08 — P1 — Offside snapshots are taken when toucher ID changes, not when the ball is played

**Reproduced.** `RulesAndGoalkeeper.cs:221–235,337–372`.

A player can dribble and then pass without changing `LastTouchedByPlayerId`; the snapshot still describes the earlier touch. The probe starts with #9 offside, moves him onside before the same passer's later kick, then receives: rules incorrectly award a free kick for offside. The inverse can miss a real offence. The rules engine receives no explicit ball-play event.

**Fix/check:** emit an execution contact event with tick, contact kind, player/team, ball location, and pre-contact player positions. Refresh offside eligibility at every relevant play/touch, including repeated contacts by the same player. Cover receivers moving onside and offside between first control and release.

### F09 — P1 — Zero-length normalization and incomplete numerical guards admit NaN

**Reproduced.** `RulesAndGoalkeeper.cs:69–75`, `BallPhysics.cs:249–255`; other unchecked normalizations exist at `IndividualAI.cs:384` and `HumanInputController.cs:255,292`.

When a recovering keeper is exactly over the ball, the sweep branch normalizes a zero vector and emits a non-finite movement command. Ball safety checks only `nextPos.X` and `v.X` for NaN, leaving invalid Y/Z, infinity, or spin undetected. A ball with finite X and NaN Z remains invalid after a solver step.

**Fix/check:** introduce safe normalization with a role-appropriate fallback. Validate all vector components and scalar inputs at public simulation boundaries. Provide deterministic recovery plus a diagnostic for invalid internal state. Test coincident positions, all axes, infinity, and degenerate target points; do not silently conceal recurring invalid state.

### F10 — P2 — Last touch and proximity are treated as possession

**Inspection.** `TacticsAndTeamAI.cs:51–74`, `IndividualAI.cs:139–193`, `MatchEngine.cs:203–211`.

Team tactics confirm possession after 0.45 seconds of unchanged **last-touch team**, without checking control. A deflection or unreachable ball can count as sustained possession. A nearby teammate within 1.85 m and ball speed below 9.5 m/s disables the primary responder even when that player is idle, unable to kick, or not controlling the ball. An idle human being nearest also suppresses AI responders because the human is included in the nearest-teammate ranking. Each AI elects a responder independently from noisy estimates, so there is no guaranteed unique assignment or fallback.

**Fix/check:** separate `LastTouch`, `ControlledBy`, `InFlightPass`, `LooseBall`, and `KeeperHeld`. Elect responders centrally using time-to-reach and eligibility, with hysteresis; preserve the intended human receiver's chance to collect, but let AI recover a genuinely abandoned loose ball. Transition immediately on authoritative control change and smooth only team shape. Test contested/loose balls, deflections, an unavailable or idle nearest human, and an intended human pass.

### F11 — P2 — Reception assistance can override unrelated player movement

**Inspection.** `FootballerLocomotion.cs:118–150`.

Any teammate within 6.5 m of a moving friendly ball may receive a 78% steering blend toward its ray. There is no intended-receiver check and no check that this is actually a pass rather than a teammate dribbling. This affects human movement too. The approach dot product uses displacement without normalizing it, so its threshold is distance-dependent. Assistance can redirect several teammates and fight a human's requested run.

**Fix/check:** scope assist to a designated or plausibly reachable receiver, use predicted closest-approach time, and cap its effect on explicit human input. It must not activate around a teammate's controlled dribble. Test a human deliberately running past a teammate, two nearby potential receivers, and opting away from an incoming pass.

### F12 — P2 — Keeper intent and physical eligibility disagree

**Inspection.** `RulesAndGoalkeeper.cs:31–34,37–75,106–133`, `FootballerLocomotion.cs:269–279,357–358`.

- Keeper chooses distribution within `0.75 * 1.65 = 1.2375 m`, but standing contact reach is `0.75 * 1.12 = 0.84 m`. Distribution movement follows the teammate, so the keeper can move away from a ball it cannot yet kick.
- Distribution proximity is planar and does not establish actual control or height eligibility.
- Save execution says it requires a goalbound shot, but actually checks only opposing last touch and speed >6.5 m/s. It can catch/parry a ball moving away, and has no own-penalty-area restriction.
- Distribution has no explicit own-goal intersection check. Keeper threat prediction also ignores height and uses absolute distance to the keeper plane, allowing already-passed balls to look like future threats.

**Fix/check:** share contact eligibility between observation, decision, and execution, while allowing a separate approach range. Establish control before distribution. Validate a signed future crossing and height for saves, enforce the intended handling area, and check the outgoing route against the keeper's own goal. Test balls just inside/outside reach, overhead balls, balls behind the keeper, and friendly foot control.

### F13 — P2 — Restart setup leaks stale state and uses the wrong team's approach side

**Inspection.** `RulesAndGoalkeeper.cs:375–432`, `FootballerLocomotion.cs:326–346`.

`TriggerSetPiece` changes `LastTouchedByTeam` but preserves the previous player ID, offside passer identity/team, ball `PreviousPosition`, and players' recovery/control state. The taker is placed using **home** attack direction even for away restarts; facing and interpolation state are not reset there. A taker on cooldown can miss the 0.25-second restart window, then lose its designation. A foul callback can set up a restart but execution continues into `ExecuteBallContact` and overwrites that newly reset ball. Exact-overlap opponents are not displaced because `d > 1e-4` is required.

**Fix/check:** make restart setup an atomic transition with explicit reset policy, team-relative placement, valid facing, and an eligible taker. Return a stop/contact-cancel result from a foul decision. Verify all restart types for both teams, a recovering taker, a foul during contact resolution, and exact overlap.

### F14 — P2 — Offside logic lacks contact and restart semantics

**Inspection.** `RulesAndGoalkeeper.cs:221–235,337–372`; `CorePrimitives.cs:151–177`.

Any new opposing touch discards the attacking offside snapshot, including a keeper save/deflection. There is no direct-restart exemption or direct/indirect free-kick distinction. The current contact/state model cannot express these cases. Under [IFAB Law 11](https://www.theifab.com/laws/latest/offside/), direct reception from a goal kick, throw-in, or corner is exempt; a deliberate save does not by itself remove an existing offside restriction, and offside is restarted with an indirect free kick.

**Fix/check:** carry restart origin and contact classification in events. Add separate scenarios for deliberate defensive play versus save/deflection, each exempt restart, and an indirect free kick entering the goal without another touch. This should follow the F08 event change.

### F15 — P2 — Live human tackle buffering fails at close range

**Reproduced.** `HumanInputController.cs:162,304–306`, `MatchClientNode.cs:172–179`.

Live input calls `QueueTackle()`. `Decide()` only emits that buffered tackle if distance is **greater than 0.85 m** and has no upper bound. At 0.6 m, the probe returns `RequestTackle=false`; at long range it spends the buffer immediately while contact is impossible. Existing tests set the compatibility `TacklePressed` property, which forces `_immediateTackleRequested` and bypasses this failure.

**Fix/check:** emit or retain the tackle over a deliberate, physically reachable timing window without excluding point-blank contact. Test the actual QueueTackle path at 0.3/0.6/1.0 m, beyond reach, and during recovery.

### F16 — P2 — Documented controls and actual input disagree; shot aim ignores dedicated aim input

**Inspection.** `README.md:38–52`, `MatchClientNode.cs:92–180`, `HumanInputController.cs:257–292`.

| Action | README | Actual implementation |
| --- | --- | --- |
| Tackle | Q / B; gamepad B | R / L; no gamepad tackle binding |
| Shoot | F / K; gamepad X | F / K; gamepad B |
| Shield | Ctrl / C; LB | Ctrl only; C is cross and LB is high modifier |
| Through pass | E / L | E / U |
| Finesse/high modifier | Alt / Z; LT | Q / Alt; LB / LT |
| Mouse aim | Cursor screen raycast | No cursor-to-pitch aiming code |
| Inspector | Tab cycles player | Tab temporarily shows anchors |
| Debug HUD | F1 toggles | No F1 handler |

Right-stick aim populates `AimStickXZ`, but assisted shooting chooses the corner from movement or the old buffered aim, rather than the current resolved aim. The displayed forward arrow follows body facing, not `LastResolvedAimDirectionXZ`. Input also hard-codes gamepad device 0 instead of choosing from the actual connected IDs.

All four held kick buttons share one `ChargingIntent` and charge timer. Holding two different action buttons calls `UpdateButtonCharge` for each every frame, repeatedly switching the intent and resetting its charge; releasing either can use charge state belonging to the other. Define explicit action priority or maintain independent charge state.

**Fix/check:** make one binding/action source of truth and generate HUD/help from it. Resolve shot aim from the intended current aim source. Add device-aware input and a cursor raycast if mouse aiming remains promised. Verify actual press/hold/release paths for every documented action with movement and aim pointing in different directions.

### F17 — P2 — Commands and attempted-contact analytics are recorded before actual execution

**Inspection.** `HumanInputController.cs:227–228,295–297`, `MatchEngine.cs:173–185`, `FootballerLocomotion.cs:255–346`.

A human kick is consumed by the controller before execution accepts it. The controller's 1.125 m planar strike range differs from execution reach and vertical checks; a rival can also win the single-contact arbitration. A valid-looking button release can disappear without a kick. Analytics use a still smaller pre-movement 3D range (`ReachRadius * 1.15`) and count proposed commands even if another player wins contact. ThroughPass and LobbedThroughPass are omitted from pass counts entirely. Possession analytics count last touch, including stopped phases.

**Fix/check:** return authoritative execution events and update statistics/buffers from accepted/rejected contacts with explicit retry policy. Count all pass types, separate attempts/completions, and define live controlled possession separately from last touch. Test lost arbitration, reach boundaries, an aerial ball, a buffered kick during recovery, and each kick enum.

### F18 — P2 — Advertised behavior fields are inert, and body alignment always evaluates perfectly

**Inspection.** `CorePrimitives.cs:108–123,186–225`, `FootballerLocomotion.cs:408–414`.

`ShieldBall` is populated by controllers but never read by execution. All `PlayerTendencies` are unused after being exposed in observations. Team `Tempo01`, `RiskTolerance01`, and `PositionalFreedom01` do not affect behavior. ReactionDelaySeconds scales small ball-velocity noise instead of introducing delayed perception/reaction. Tactical profiles therefore differ in a narrower set of behaviors than their names/data suggest. Explicit kicks assign `player.FacingXZ = aimXZ` **before** computing alignment, making the dot product always 1 and bypassing the accuracy penalty for turning to kick.

**Fix/check:** implement meaningful effects for supported fields or remove/label them as unimplemented. Compute body alignment from pre-contact facing and honor turn constraints. Use controlled A/B tests that change one parameter at a time; test a kick directly behind a sprinting player and shielding under pressure.

### F19 — P2 — Human call state is global across matches

**Reproduced.** `HumanInputController.cs:87–95,178–182,209–220`, `MatchEngine.cs:68`.

Human call-for-pass state is static. Creating another match resets a live match's call: the probe records caller #9 before constructing the second engine, then null afterward. Interleaving engines or tests can also share/decrement the same timer. This breaks match isolation and weakens deterministic/replay guarantees.

**Fix/check:** store call state on the match or in a match-owned communication context. Test two interleaved engines with different human calls and different seeds, plus independent replay.

### F20 — P2 — Boundary and scoring rules use endpoint state; goal attribution is only last touch

**Inspection.** `RulesAndGoalkeeper.cs:237–284`, `BallPhysics.cs:92–106,262–341`.

Rules inspect only the final tick position, test goal-line exit before touchline exit, and do not find the first crossing on the actual substep trajectory. A high shot descending behind the goal can be classified using its later height; a tick crossing multiple boundaries has no first-event ordering. Goal geometry uses centre Z/Y only and a hard-coded 0.11 m ball radius. The score event's `PlayerId` is simply last toucher, so a keeper's deflection is not distinguished from a genuine own goal or an attacker's goal after a save.

**Fix/check:** expose the first authoritative boundary crossing from ball substeps, including crossing position, event order, and configured radius. Keep goal scorer/own-goal attribution separate from `LastTouchPlayerId`. Test a crossbar-height crossing followed by descent behind goal, diagonal exits, post rebounds, and configurable geometry. Boundary behavior should follow the whole-ball crossing requirement in [IFAB Law 9](https://www.theifab.com/laws/latest/the-ball-in-and-out-of-play/).

The probe's `keeperLastTouchGoals` is intentionally named as such: **6 of 8** goals in the autonomous sample and **7 of 9** in the human-idle sample had a defending keeper as last toucher. These counts are not proof that every such goal should be officially attributed as an own goal. F02 establishes an actual parry-induced return into goal independently.

### F21 — P2 — Controller override API can bypass the pre-match lock

**Inspection.** `MatchEngine.cs:124–132`, `MatchEngine.cs:82–83,143–147`.

Mid-match human overrides are rejected only when the original config already designated a human and the target differs. A match with no original human can receive one after kickoff, despite the documented lock invariant. Replacing the designated player's controller does not update `LockedHumanController`, leaving live input and spectator status tied to an obsolete object. Unknown IDs are accepted into the dictionary without a player validation.

**Fix/check:** enforce the invariant independently of the initial nullable config, or split test overrides from supported runtime transitions. Keep authoritative human-controller references consistent. Test no-human-to-human overrides after tick zero, replacement of the original controller, invalid IDs, and removal of the locked player.

## Presentation and maintenance findings

### F22 — P3 — Visible geometry and smoothing differ from simulation assumptions

**Inspection.** `MatchClientNode.cs:202–229,548–549`, `FootballerLocomotion.cs:209–245`.

- Ball render radius is **0.16 m** while physics uses **0.11 m**, making apparent contact/reach differ from physical contact.
- Camera smoothing uses a fixed 0.14 lerp per rendered frame, so tracking response changes with frame rate. The simulation accumulator has no catch-up cap, which can produce prolonged stalls after a large frame delay.
- Player collision resolution ignores vertical separation, so an airborne player is pushed horizontally by a grounded player. Exactly coincident centres are skipped rather than separated. These are physical model limitations requiring an explicit design choice.
- Inspector indexing assumes `DesignatedHumanPlayerId` is a valid one-based array index. There is no input/config validation for invalid human IDs, nonpositive half lengths, invalid team sizes, or degenerate pitch/player attributes.

**Fix/check:** share physical dimensions with rendering, use delta-aware camera smoothing, bound and diagnose catch-up work, handle coincident contacts deterministically, and validate public config. Test 30/60/144 FPS presentation and invalid exported IDs. The ordinary collision velocity impulse was checked separately and correctly reduced closing speed from 2.0 to 1.1 m/s; its sign is **not** an audit finding.

### F23 — P2/P3 — Test and documentation claims exceed demonstrated coverage

**Inspection plus executed suite.** `tests/FootballSim.Tests/SimulationPhaseGatesTests.cs`, `docs/STATUS.md`, `docs/TESTING.md`, `docs/KNOWN_ISSUES.md`, `README.md`.

- The “same seed and recorded commands” test actually runs two fresh AI matches; it does not record and replay a match.
- The tactical test says “higher pass ratio” in its name, but asserts only team width. It runs 90 seconds, not through halftime or full time.
- No existing assertions cover keeper catches/parries/distribution, moving human reception, rules transitions, live QueueTackle input, completed matches, goal attribution, or the offside cases above.
- The input test uses compatibility properties rather than the hold/release/QueueTackle path wired to Godot.
- There is no multi-seed tactical regression assertion; CLI output is not a gate. CLI can print “Match Complete” after reaching a tick cap without asserting FullTime. Short-half restart corruption makes this distinction important.
- STATUS still says tests await local execution. KNOWN_ISSUES contains only net simplification and subjective feel. Several documents cite numbered requirements/sections without including the source specification.
- SDK versions are declared, but there is no `global.json` pin for the .NET SDK or package lock file, so “pinned toolchain” is only partial.

**Fix/check:** promote the reproduced cases to focused regressions during implementation; add a real recorded-command replay with isolated per-match state, both-half/full-match completion checks, and a multi-seed tactical report with actual execution metrics. Update status, known issues, control help, and specification references after fixes. Do not treat the current green suite as validation of all claimed phases.

## Recommended implementation sequence

1. **Correct authoritative state:** F05–F09, F13. Persist period, define live/restart/stopped execution, consume restarts once, emit typed contact events, guard non-finite vectors. These changes provide dependable state for subsequent work.
2. **Fix goalkeeping:** F02/F03/F12. Reproduce and eliminate the spin-return and catch/rush failures on both ends. Include actual hold/release ownership and safe distribution.
3. **Fix pass planning and reception:** F01/F10/F11/F17. Shared physical pass planner, ownership/receiver metadata, scoped reception help, and execution acknowledgements/metrics.
4. **Improve team behavior:** F04/F10/F18. Persistent tactical jobs, situational target scoring, central responder assignment, and meaningful profile parameters. Compare several seeds with a human carrier as well as autonomous play.
5. **Complete rules/input consistency:** F14–F16/F19–F21. Offside semantics, actual controls, isolated call state, and override invariants.
6. **Presentation and documentation:** F22/F23. Verify the Godot experience, reconcile controls/help, and update project status and limitations.

Keep each change reviewable with a targeted failing regression written first and a passing result after the fix. Existing physics and determinism checks remain useful, but preserve them alongside gameplay-specific assertions. Avoid a full engine rewrite: simulation/presentation separation and the shared controller contract are suitable foundations.

## Reproduction assets and commands

Added files:

- `tools/FootballSim.AuditProbe/FootballSim.AuditProbe.csproj`
- `tools/FootballSim.AuditProbe/Program.cs`
- `tools/FootballSim.AuditProbe/README.md`
- `artifacts/audit-probe-2026-11-06.json`
- This handoff.

From the repository root:

```powershell
dotnet build FootballSimulation.sln --no-restore
dotnet test tests/FootballSim.Tests/FootballSim.Tests.csproj --no-restore
dotnet build tools/FootballSim.AuditProbe/FootballSim.AuditProbe.csproj
dotnet run --project tools/FootballSim.AuditProbe --no-build > artifacts/audit-probe-2026-11-06.json
```

The probe is deliberately outside the solution/test gate. It reports baseline observations, including known defects; it is **not** an acceptance-test suite and a successful process exit does not mean gameplay is correct. The match samples run for fixed ticks, not to FullTime. Re-run after a fix and compare the named scenario outputs. Source line references describe the audited production files and will move as fixes are implemented.
