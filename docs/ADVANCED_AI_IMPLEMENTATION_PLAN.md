# Advanced football AI implementation plan

Status: proposed work; implementation has not started. Prepared 2026-11-12.

Primary input: [deep-research-report.md](../deep-research-report.md). This plan translates its architecture, tactical principles, scenarios, and validation strategy into changes to the current C# simulation. Research claims and source confidence remain documented in that report; its embedded citation IDs are not independently usable source links.

## 1. Outcome and scope

Build an autonomous 11v11 AI in which players support, defend, receive, and combine through compatible local decisions. Team tactics should produce recognizable styles while players differ in perception, anticipation, risk preferences, and execution.

The first playable release should demonstrate useful support positions, purposeful progression, pressure with cover, and sensible reactions to turnovers. The advanced release adds timed runs, planned reception, coordinated pressing, goalkeeper integration, and selective combinations. Data-fitted models are a subsequent optional stage.

The implementation must retain the deterministic 60 Hz, Godot-independent simulation, the shared human/AI execution pipeline, career-player control lock, training drills, and existing rules and regression coverage. Physics and match rules remain authoritative. New AI issues are separate from the completed audit backlog in `TASKS.md`.

No general-purpose long-horizon planner, reinforcement-learning training pipeline, or animation overhaul is required for these releases.

## 2. Starting point: extend what already exists

The following observations come from the current source, not just the report or historical documentation.

| Area | Existing implementation | Work required |
| --- | --- | --- |
| Simulation and execution | `MatchEngine.cs` advances at 60 Hz; all controllers emit `FootballerCommand`; `FootballerLocomotion.cs` executes movement and contacts. | Introduce slower cognition and persistent intent without slowing steering or repeating discrete kicks. |
| Possession and responders | `BallPossession.cs` tracks controlled, loose, in-flight, and keeper-held states; one responder is elected per team with hysteresis. | Share a movement-aware arrival model. Current responder reach is distance divided by a fixed fraction of sprint speed. |
| Team structure | `TacticsAndTeamAI.cs` has five tactical states, profile-dependent anchors, state locks, and timed sinusoidal attacking offsets. | Add overlapping tactical contexts, threat-dependent structure, and persistent structural ownership. Replace artificial offsets once meaningful runs exist. |
| Team jobs | `TeamJobs.cs` already assigns persistent outlet, width, depth, overlap, cover, marking, and line jobs. | Extend this coordinator with responsibility transfer and coordination lifecycles; avoid a second competing allocator. Actual `MinimumHoldTicks` is 42, or 0.70 s at 60 Hz, despite older prose describing roughly 0.42 s. |
| Player decisions | `IndividualAI.cs` generates utility candidates with targets, commitment bonuses, onside support, carries, shots, and passes. | Separate candidate generation, value calculation, persistent intent, and execution. Replace action-name identity with a stable action/target identity. |
| Passing | `PassPlanner.cs` provides calibrated ground-pass power, lead, and flight queries. `IsPassLaneBlocked` already checks moving defenders against flight time. | Reuse calibration and the blocked-pass regressions; extend interception and reception estimates instead of reverting to static lane clearance. |
| Carrier restrictions | Current scoring favors shots inside fixed distances, bans many backward final-third passes, excludes keeper receivers, and reserves intentional through-ball behavior largely for human calls. | Replace blanket restrictions with comparable pass/carry/shot values and add autonomous run-targeted options. Preserve the human-call contract. |
| Perception | `PerceptionSystem` delays/tracks ball changes and adds confidence-dependent position noise. | Every active opponent is still included on every observation, including behind the player. Add actual visibility, observation age, memory, and controlled communication. |
| Technical execution | First touch, dribble control, shielding, fatigue, facing, preferred foot, recovery, and tackle restraint already affect execution. | Add a chosen reception objective and richer cognition using these existing effects; do not apply the same fatigue/error penalty twice. |
| Goalkeeper | `GoalkeeperController` has positioning, save, hold, distribution, recovery, and basic box sweeping. | Share arrival/trajectory estimates; add threat-aware depth, support, aerial decisions, and safe sweep races. Keep the held-ball lifecycle. |
| Rules | `RulesAndGoalkeeper.cs` has contact-driven offside phases, restart exemptions, and indirect-restart handling. | Verify contact-time geometry before timed runs. The offside snapshot currently reads live `BallState` during rules processing after ball integration, rather than solely the contact geometry. |
| Verification and debugging | xUnit regressions, recorded-command replay, headless match/tactical modes, audit probe, execution metrics, candidate lists, and Godot anchor/inspector views exist. | Extend with reusable tactical scenarios, score breakdowns, spatial overlays, behavior metrics, and repeatable performance samples. |

## 3. Architecture and ownership

```text
MatchSimulationEngine: authoritative state, rules, contacts, fixed tick
    |
    +-- Scheduled shared computations: geometry, trajectories, arrival queries
    |
    +-- PerceptionSystem: observations, scans, memory, delayed communication
    |       |
    |       +-- Team beliefs -> tactical context -> TeamJobCoordinator
    |       |                                      responsibilities/contracts
    |       +-- Player beliefs <-------------------- permitted team messages
    |               |
    |               +-- Feasible candidates -> utility -> persistent intent
    |                                                    |
    +----------------------------------------------------+-- 60 Hz executor
                                                             |
                                                   FootballerCommand
                                                             |
                                                  Existing physical execution
```

### State boundaries

- **Truth:** the engine owns `FootballerState`, `BallState`, laws, and contact events. Keep these as the single authoritative world rather than introducing a second mutable `MatchWorld`.
- **Shared algorithms:** reach, trajectory, pressure, and control models accept explicit input snapshots. Their implementations can be shared; their input knowledge must remain explicit.
- **Team knowledge:** own formation, role ownership, public restart state, tactical policy, and fused observations/messages. Exact unseen opponent positions must not enter job anchors, press requests, or advertised runs indirectly through a truth-based spatial map.
- **Player knowledge:** visible and remembered entities, estimated ball motion, uncertainty, own state, known tactical instructions, and received calls. Candidate scorers cannot access the engine's player collection. Authoritative possession still drives execution and metrics, but an unseen opponent's carrier ID, intended receiver, or private command must not become perfect player knowledge through observation metadata.
- **Diagnostics:** truth-based maps may be generated for overlays and evaluation, but must be labeled and unavailable to decision scorers. Team/player belief maps are distinct caches keyed by observer, tick, and model version.

Use the existing `FootballerObservation` as the controller-facing adapter. Extend it with optional/defaulted fields so standalone tests, replay controllers, human input, and `TrainingSession` remain valid. Extract modules from `IndividualAI.cs` incrementally while retaining `TacticalUtilityAiController` as the public controller.

Extend `TeamTacticalProfile`, `PlayerTendencies`, and `PlayerAttributes` rather than adding competing style/profile systems. New configuration fields require validation, neutral defaults, and evidence that changing the field changes behavior. Extend `MatchCommunicationContext` for AI messages while preserving current human call-for-pass semantics.

### Core contracts to introduce

These are proposed types, not existing APIs. Keep the initial implementation concrete; add interfaces only where alternative implementations are actually needed.

| Contract | Contents and owner |
| --- | --- |
| `AiRuntimeConfig` | Validated feature switches, cadence, candidate limits, utility margins, and commitment ranges; owned by match configuration. |
| `AiDecisionSnapshot` | Tick-stamped immutable inputs to one update; references stable buffers rather than copying the entire world for every candidate. |
| `PlayerBelief` / `ObservedPlayer` | Last observed position, velocity, orientation estimate, age, uncertainty, source, and confidence; owned per player by perception. |
| `TeamContext` | Possession stability, attacking/defending context, transition mode, restart context, and structural goals; owned per team. |
| `PlayerResponsibility` | Nominal role, phase role, current duty, local responsibility, owner, and release conditions; coordinated through `TeamJobCoordinator`. |
| `ActionCandidate` / `UtilityBreakdown` | Stable candidate key, target/path, feasibility, expected value terms, knowledge confidence, and rejection reason. Keys use action type plus target player/run/region ID and variant, not a changing score or floating-point target coordinate. |
| `PlayerIntent` | Candidate identity, path/region, start/expiry ticks, commitment, progress, cancellation reason, and execution stage; owned by controller. |
| `CoverageRequest`, `RunIntent`, `CombinationIntent`, `TeamCall` | Bounded, team-scoped coordination records with owner, recipients, creation/expiry tick, and explicit lifecycle. |
| `ContactGeometrySnapshot` | Ball and relevant player geometry at the authoritative play, with contact tick/order; owned by execution/rules. |

### Deterministic scheduling and execution

Keep physical execution, steering, human input, replay commands, and urgent execution guards at 60 Hz. Separate choosing a new intention from producing the next command for the current intention.

| Work | Initial cadence | Tick interval at 60 Hz |
| --- | --- | --- |
| Movement, contact/recovery guards, intent execution | 60 Hz | 1 |
| Perception refresh and head/attention update | 15 Hz | 4 |
| Carrier and nearby defender utility | 10 Hz | 6 |
| Other outfield utility | 6 Hz | 10 |
| Cached local spatial queries | 10 Hz | 6 |
| Coarse team-belief spatial map | 5 Hz | 12 |
| Tactical structure | 3 Hz | 20 |
| Local coverage/coordination | 10 Hz, plus relevant events | 6 |
| Slow structural reassessment | 1 Hz, plus structural events | 60 |

These rates are initial engineering settings. Stagger players using stable ID-derived offsets. Count elapsed live ticks explicitly across pauses; do not assume every scheduled interval contains live play.

Advance reaction history, commitment/recovery timers, time-on-ball, and human-call timers independently of utility cadence. Maintain tick-stamped history sufficient for delayed observation; reducing perception refresh must not silently erase reaction latency or slow timer expiry.

Possession changes, an arriving pass, observed immediate danger, restarts, unavailable players, and invalid intentions can request an earlier update. Public rule changes interrupt immediately; hidden opponent events must still respect perception latency. Process requests in a stable order with bounded work and a safe hold/recover fallback.

Kicks, tackles, and coordination messages are discrete actions, not cached commands repeated between utility updates. Track requested, executed, rejected, and expired actions; use contact feedback and existing cooldowns to prevent duplicate execution. Revalidate the current intention at every motor tick without recomputing the full utility set.

Use match-local deterministic RNG streams for perception, decision uncertainty, and physical execution, derived by a documented integer seed mixer. Diagnostic collection must consume no simulation randomness. Record AI/config version with replay data. Existing command replay must remain exact for the same version; cross-version trajectory identity is not a requirement. Command-only replay must not depend on running perception to advance the execution RNG.

## 4. Delivery sequence

Every checkbox below is future work. Each milestone should be delivered in small, reviewable changes with scenario evidence. Do not start combinations before the foundational gates pass.

| Milestone | Depends on | Result |
| --- | --- | --- |
| A0 | — | Baseline, reusable tactical scenarios, and measurement contract |
| A1 | A0 | Safe runtime seams, replay isolation, and contact-time law geometry |
| A2 | A1 | Movement-aware spatial analysis and minimum credible perception |
| A3 | A2 | Team context, structure, local cover, and transition security |
| A4 | A2, A3 | Comparable carrier decisions and reception value |
| A5 | A3, A4 | Timed off-ball runs and receiver/pass coordination |
| A6 | A3, A4, A5 | Coordinated defence and integrated goalkeeper |
| A7 | A2, A4, A5, A6 | Scanning, planned first touch, and player differentiation |
| A8 | A5, A6, A7 | Selective combinations, traps, and restart routines |
| A9 | A0–A8 | Style presets, tuning, performance verification, and release |
| A10, optional | A9 | Data-informed calibration |

### A0 — Establish the baseline and scenario harness

Primary files: `tests/FootballSim.Tests`, `src/FootballSim.Headless/Program.cs`, `tools/FootballSim.AuditProbe/Program.cs`. Proposed shared scenario support: `tests/FootballSim.Tests/Scenarios` and, when needed by the CLI, a small Godot-free scenario utility project.

- [ ] A0.1 Run the existing suite and headless tactical gate; capture the current 5v5/11v11 behavior before changing AI. Keep existing audit diagnostics intact.
- [ ] A0.2 Define scenario setup from positions, velocities, roles, attributes, ball/control state, direction, seed, and duration. Add helper controllers for fixed or scripted opponents.
- [ ] A0.3 Establish ten initial scenarios: 3v2 build-up, 4v3 wide overload, CB step-out, rear-pressure reception, high-line run, winger/fullback overlap, 3v3 counterattack, touchline press, box cross, and keeper sweep race.
- [ ] A0.4 Log a bounded trace of tick, player, knowledge age, role/duty, candidate identity, utility terms, selected intent, cancellation, and executed/refused contact. Add trace assertions incrementally as behavior is implemented.
- [ ] A0.5 Capture same-machine Release timings, allocations, existing match metrics, and short rendered clips. Store seed/config/toolchain/hardware with results under a new `artifacts/advanced-ai/` directory.

Exit gate: each scenario can reproduce its starting state and run deterministically; baseline full matches reach full time; measurements distinguish chosen actions from executed actions. Baseline scenarios may expose missing behavior and are not all expected to pass advanced tactical assertions yet.

### A1 — Add runtime seams without changing tactical policy

Primary files: `MatchEngine.cs`, `CorePrimitives.cs`, `IndividualAI.cs`, `SimulationConfigValidation.cs`, `FootballerLocomotion.cs`, `RulesAndGoalkeeper.cs`. Proposed modules: `AI/AiRuntimeConfig.cs`, `AI/AiScheduler.cs`, `AI/PlayerIntent.cs`, `AI/DecisionTrace.cs`.

- [ ] A1.1 Extract perception and candidate/selection helpers in behavior-preserving changes. Add validated configuration, stable candidate keys, bounded trace buffers, and explicit reset hooks.
- [ ] A1.2 Introduce a controller-owned intent executor; initially retain the current decision cadence. Add scheduling behind a feature switch only after command/contact behavior is pinned.
- [ ] A1.3 Separate RNG streams and extend command replay coverage before any cadence or noise changes. Verify interleaved matches and diagnostics-on/off runs remain isolated.
- [ ] A1.4 Capture offside geometry at the contact, rather than reading a ball position already advanced by physics. Preserve event ordering, restart-origin stamping, deliberate-play/save distinctions, and existing exemption tests.
- [ ] A1.5 Define reset handling for restart, halftime/direction reversal, full time, player removal, training reset, and controller replacement. Clear or revalidate memories, caches, intent, queued calls, and contracts explicitly.

Exit gate: extraction preserves commands before intentional behavior changes; scheduled execution does not duplicate a single kick; command replay works when perception cadence differs or AI is bypassed. Offside results are invariant to moving the ball after contact before rules consume the event. Existing rule, human-lock, training, and tackle regressions pass.

### A2 — Build spatial foundations and the knowledge boundary

Primary files: `BallPossession.cs`, extracted `PerceptionSystem`, `IndividualAI.cs`, `PassPlanner.cs`. Proposed modules: `AI/TimeToReachModel.cs`, `AI/PressureModel.cs`, `AI/SpatialAnalysis.cs`, `AI/PlayerMemory.cs`.

- [ ] A2.1 Implement one reach model using current velocity, turn cost, acceleration, fatigue-adjusted speed, and contact reach. Compare predictions against real locomotion scenarios, then use it in responder election, interception, control, and sweep queries.
- [ ] A2.2 Add basic sight filtering and persistent observation memory now. Unseen entities retain predicted positions with growing uncertainty; they do not receive fresh truth positions with lower confidence. Include timestamps and orientation uncertainty. More elaborate scanning follows in A7.
- [ ] A2.3 Expose a read-only estimated-kinematics view to scorers. Use known/scouted capability estimates or conservative defaults for opponents, not their hidden live attributes or intentions.
- [ ] A2.4 Implement directional local pressure, arrival margins, and coarse control. Start near a 3 m grid, with boundary cells handled explicitly, and refine only relevant local candidates. Cache predictions on observer/tick/model inputs.
- [ ] A2.5 Distinguish relative influence from absolute ability to arrive: a 50/50 team share does not mean either team can reach a distant cell soon. Keep arrival times and an unclaimed/unreachable state alongside normalized control shares.
- [ ] A2.6 Supply truth maps for validation and separate team-belief/local-belief queries for tactics. Add control, pressure, and perception-age overlays to `MatchClientNode.cs`.

Exit gate: reach predictions respond correctly to turning, acceleration, and opposing velocity; faster defenders do not systematically improve estimated pass safety. All maps are finite and bounded, with normalized shares including any unclaimed component. In the absence of a permitted observation or delivered message, an unseen opponent changing direction does not instantly change a player's estimate, candidate score, or team assignment through another API.

### A3 — Evolve team context and persistent responsibilities

Primary files: `TacticsAndTeamAI.cs`, `TeamJobs.cs`, `CorePrimitives.cs`. Proposed modules: `AI/TeamContext.cs`, `AI/CoverageRequest.cs`, `AI/TransitionSecurityModel.cs`.

- [ ] A3.1 Add separate possession stability, attack/defence context, transition mode, and restart context. A loose ball can coexist with a previous team's possession claim; do not reinterpret last deliberate play as current stable control.
- [ ] A3.2 Replace the single tactical-state lock with context-sensitive hysteresis. Turnovers and emergency defence must interrupt a slow structural dwell timer.
- [ ] A3.3 Preserve nominal formation ownership and add phase role, duty, and temporary responsibility. Allow slow structural reassignment on explicit events or a persistent mismatch, never a fresh whole-team assignment every tick.
- [ ] A3.4 Extend job targets into constraint regions scored relative to ball, teammates, opponents, and space. Reward width, depth, staggering, connections, and complementary channels without compulsory five-lane occupancy.
- [ ] A3.5 Implement coverage request creation, nearby bids, deterministic owner selection, acceptance, recovery, handback, expiry, and owner-removal handling. Bound cascades so one press cannot recursively reshuffle the entire team.
- [ ] A3.6 Model rest defence using dangerous outlet routes, central access, recovery times, and opponent threats. Add constraints to overlaps and box runs according to transition exposure, rather than a fixed count behind the ball.

Exit gate: in the CB step-out scenario, a nearby appropriate player protects the consequential gap and holds responsibility until handback is safe. Small positional perturbations do not swap nominal roles. Width/length/line-spacing metrics remain plausible across 5v5, 11v11, halftime, and a missing outfield player. Turnovers invalidate incompatible duties promptly.

### A4 — Upgrade carrier decisions and receiving-state value

Primary files: `IndividualAI.cs`, `PassPlanner.cs`, `TeamJobs.cs`, `MatchStatistics.cs`. Proposed modules: `AI/PassSuccessModel.cs`, `AI/ReceptionValueModel.cs`, `AI/ActionValueModel.cs`.

- [ ] A4.1 Generate feasible passes to feet and run regions, carries, dribbles, shots, shield, clearance, and recycle options. Include keeper support where appropriate. Keep pitch boundaries, contact availability, and technical capability as constraints.
- [ ] A4.2 Extend the existing calibrated ground trajectory with defender/receiver arrival margins, reaction/turn costs, reach/height, receiving speed, orientation, pressure, and uncertainty. Retain hard rejection for a clearly blocked ground pass; evaluate feasible lofted alternatives separately.
- [ ] A4.3 Value the state after successful reception using progression, ability to turn, next options, shooting opportunity, pressure, structure, and turnover exposure. Goal-location value is one feature, not the entire receiving score.
- [ ] A4.4 Put pass, carry, and shot utilities on the same documented scale. Remove blanket backward-pass bans and guaranteed close-range shot bonuses only after a better chance can beat a poor shot and safe recycling can beat a dangerous forced pass. Prevent endless recycling through state/progression value and elapsed-possession context.
- [ ] A4.5 Use robust hysteresis with current-action re-evaluation: switch only when `best - current > absoluteMargin + relativeMargin * max(abs(current), epsilon)`, unless the current action is invalid or an emergency interrupts it. Avoid the report's multiplicative example for negative utilities.
- [ ] A4.6 Preserve firm pass power, the human caller's special handling, intended receiver metadata, and keeper-safe distribution. Add term-by-term explanations and rejection reasons to the inspector.

Value convention: `ReceptionValue` is conditional on successful reception. A pass score applies completion probability once: `pComplete * receptionGain - (1 - pComplete) * lossCost - effortCost - structuralCost + preferenceBias`. Document which component owns transition risk so it is not subtracted again under another name. Do not multiply a probability-weighted receiving score by completion probability a second time.

Exit gate: a free useful receiver beats a blocked lane, a pressured backward-facing receiver is less valuable than the equivalent open receiver, and an unpressured team progresses rather than circulating indefinitely. Rear-pressure, bad-shot/good-cutback, keeper-recycle, and genuinely open-pass scenarios demonstrate the intended alternatives. Existing blocked-lane and firm-power tests remain meaningful.

### A5 — Make off-ball runs and reception mutually compatible

Primary files: `TeamJobs.cs`, `IndividualAI.cs`, `PassPlanner.cs`, `HumanInputController.cs`. Proposed modules: `AI/RunCandidateGenerator.cs`, `AI/RunIntent.cs`, `AI/RunCoordinator.cs`.

- [ ] A5.1 Generate a bounded set of paths: hold width, check short, straight/diagonal in-behind, overlap, underlap, recycle support, blind-side/late box arrival, pin, and decoy. Deliver simple support/depth paths before speculative decoys.
- [ ] A5.2 Score paths on arrival advantage, pass availability, receiving orientation, line breaks, congestion, role usefulness, and transition exposure. Move from timeless point targets to start windows, waypoints, receiving regions, and expiry.
- [ ] A5.3 Advertise run intentions through permitted team coordination, with confidence and communication delay. A carrier should match its pass arrival to the runner's future reachable region; the runner remains committed long enough to receive a delivered pass.
- [ ] A5.4 Apply tactical onside margins before release. Once a legal pass has been played, let the receiver continue beyond the current line; legal eligibility belongs to the contact snapshot, not a new onside clamp every tick.
- [ ] A5.5 Allocate complementary box-arrival responsibilities according to arrival time, finishing/aerial capability, and rest defence. Compare early cross, driven/lofted delivery, cutback, carry, and recycle. Extend prediction for supported lofted/cross execution types before scoring them; do not assess an airborne delivery using ground-pass flight times.
- [ ] A5.6 Evaluate pin/decoy value using a bounded follow-versus-hold defender response and the change in a teammate's opportunity. Remove sinusoidal anchor pushes as intent-driven movement replaces them.

Exit gate: the high-line scenario produces correctly timed playable runs; the winger/fullback scenario can choose overlap, underlap, or support according to the defender response and cover. Cross targets are future meet points. Runs do not oscillate between start/stop on small score changes or force a return pass from a carrier. An unavailable human does not get assigned autonomous movement.

### A6 — Coordinate defending and integrate the goalkeeper

Primary files: `TeamJobs.cs`, `IndividualAI.cs`, `TacticsAndTeamAI.cs`, `RulesAndGoalkeeper.cs`. Proposed modules: `AI/PressPolicy.cs`, `AI/DefensiveLineModel.cs`, `AI/GoalkeeperDecisionModel.cs`.

- [ ] A6.1 Generate press, delay, cover, screen, track, intercept, hold, and drop candidates. Evolve the existing second-defender challenge into pressure-cover-balance so two defenders do not independently abandon the same dangerous space.
- [ ] A6.2 Implement angled pressure and cover-shadow scoring. Treat back passes, poor touches, rear-facing reception, slow passes, and touchline position as evidence, moderated by cover and bypass risk.
- [ ] A6.3 Give the defensive line a shared depth band based on passer pressure, credible runs, field position, and keeper sweep coverage. Support local exceptions and explicit marking handovers, mixing zone and opponent responsibility by style.
- [ ] A6.4 At turnover compare local arrival numbers, central security, carrier constraint, and escape routes to choose counterpress, delay/recover, or retreat. Counterpress duration is a maximum window, not an automatic instruction on every loss.
- [ ] A6.5 Use shared reach/trajectory estimates for sweep races, shot interception, cross claim/punch, 1v1 block, buildup support, and short/medium/long distribution. Add any missing physical action capability before enabling its AI candidate.
- [ ] A6.6 Gate goalkeeper handling against the implemented legal restrictions, including area and deliberate teammate back-pass restrictions where missing. Preserve held-ball protection, own-goal safeguards, and one-release behavior. Document remaining law-model limitations separately from tactical quality.

Exit gate: pressure usually has useful cover in the relevant scenario family; an unsupported press can lose to delay/hold. Secure and exposed turnover setups choose different transition responses. Keeper/striker arrival perturbations change sweep choices sensibly, and infeasible sweeps are declined. Existing catch, parry, held-ball, offside, and tackle-spam coverage stays green.

### A7 — Add scanning, reception planning, and cognitive differences

Primary files: extracted `PerceptionSystem`, `CorePrimitives.cs`, `FootballerLocomotion.cs`, `HumanInputController.cs`, `Training/TrainingSession.cs`. Proposed modules: `AI/ScanPlanner.cs`, `AI/ReceptionPlanner.cs`, `AI/PlayerCognitiveState.cs`.

- [ ] A7.1 Add head/gaze direction independently of body facing, bounded scan motion, attention targets, simple relevant occlusion, and scan utility before reception or uncertain actions. Feed visible entities into the memory introduced in A2.
- [ ] A7.2 Add delayed, expiring team calls such as man-on, turn, step, drop, cover, switch, and run. Calls update belief or preference, not exact hidden world state. Keep calls team-scoped and bounded.
- [ ] A7.3 Plan reception before arrival: kill, open body, escape pressure, stride touch, shield, prepare pass/shot/cross. Evaluate feasible orientation/touch pairs against the next action and uncertainty.
- [ ] A7.4 Add optional reception intent to the command/execution boundary, with neutral behavior for existing human/training/replay callers. Execution still resolves real ball contact, control error, pressure, and foot/turn constraints; intent must not teleport or guarantee control.
- [ ] A7.5 Separate observation error, evaluation error, reaction latency, and execution error. Use existing stamina/composure/technical state as shared causes, and add new cognitive fields only where a measured behavior requires them. Model anticipation probabilistically so an early guess can be wrong.
- [ ] A7.6 Express archetypes as candidate/weight/perception preferences: playmaker, ball winner, inverted fullback, touchline winger, inside forward, target forward, and sweeper keeper. All use the same decision pipeline.

Exit gate: scanning can reveal a rear threat before reception; lack of a scan leaves an aged/uncertain estimate. A man-on call changes the relevant receiver's options after its delay. Pressure reduces control quality over paired samples; better scanning can improve chosen options without guaranteeing completion. First-touch and training regressions still pass, and disabling a cognitive feature produces a measurable difference.

### A8 — Add bounded combinations and restart coordination

Primary files: team coordinator, run coordinator, carrier scorer, `RulesAndGoalkeeper.cs`. Proposed modules: `AI/CombinationCoordinator.cs`, `AI/ShallowActionSearch.cs`, `AI/RestartPolicy.cs`.

- [ ] A8.1 Add one-two and third-player contracts with participants, return region, expected arrival, confidence, expiry, and cancellation. All participants retain the option to abandon an invalid lane.
- [ ] A8.2 Search only a few high-value continuations from the perceived state. Initially examine the best three first actions and at most three continuations each, to depth two; include defender uncertainty and avoid truth-state rollouts.
- [ ] A8.3 Recognize switch/overload-to-isolate opportunities from local pressure, far-side opportunity, receiver matchup, actual pass feasibility, and turnover cost. No forced switch after a fixed number of passes.
- [ ] A8.4 Extend basic press coordination into short trap contracts with pressure, screening, cover, an escape condition, and recovery behavior. Failure to trap must return players to valid duties.
- [ ] A8.5 Add a small library of legal kick-off, throw-in, corner, free-kick, and goal-kick routines with taker/support/target roles. The rules engine supplies legal placement and restart state; routines choose among lawful options and clear on restart completion or invalidation.

Exit gate: third-man/one-two scenarios increase useful continuations compared with search disabled, and a blocked return aborts cleanly. The touchline trap preserves cover and can be escaped. Restart participants respect the current law model, no contract survives into unrelated possession, and inactive players release their contract ownership.

### A9 — Tune styles, verify performance, and release

Primary files: `CorePrimitives.cs`, `SimulationConfigValidation.cs`, `src/FootballSim.Headless/Program.cs`, `MatchStatistics.cs`, `src/FootballSim.Godot/MatchClientNode.cs`, tests and project documentation.

- [ ] A9.1 Extend existing presets with positional possession, direct, counterattacking, high-press, low-block, and man-oriented configurations. Preserve old defaults during development and compare styles with the same squads/seeds and swapped ends/opponents.
- [ ] A9.2 Add feature ablations for local cover, rest defence, perception limits, orientation, commitment, run coordination, sweep behavior, and lookahead. Use differences to find features that are inert or overwhelming.
- [ ] A9.3 Profile Release 11v11 on documented hardware. Bound candidate counts, trajectory samples, map refreshes, queues, and search nodes. Reuse arrays/buffers in new hot paths; ensure tracing off has negligible cost.
- [ ] A9.4 Complete rendered Godot review for spacing, movement continuity, support, pressure/cover, reception, keeper decisions, and transitions. Exercise AI-only, active human, idle human, 5v5, 11v11, and all training drills.
- [ ] A9.5 Enable advanced AI by default only after release gates pass. Keep a validated legacy preset/feature configuration for comparison during tuning, plus an explicit AI model/config version in artifacts. Update `ARCHITECTURE.md`, `TESTING.md`, `KNOWN_ISSUES.md`, and the project work-state files with actual evidence.

Exit gate: all applicable scenario, replay, rule, training, tactical, performance, and rendered-review checks below pass. Remaining subjective tuning and law-model limitations are stated explicitly.

### A10 — Optional data-informed calibration

- [ ] Evaluate dataset coverage and usage terms before importing Metrica or StatsBomb material referenced in the report.
- [ ] Add reproducible offline feature extraction and calibration for reach, pass completion, pressure, reception value, shot quality, or positioning distributions, one model at a time.
- [ ] Keep training/calibration separate from runtime inference; version features, coefficients, dataset slice, and evaluation results. Ship a heuristic fallback and use no runtime network dependency.
- [ ] Validate on held-out matches/scenarios, and compare calibration against both tactical behavior and the simulation's actual physics. Do not present professional tracking-data fit as proof that this game's probabilities are calibrated.

## 5. Verification and release gates

### Scenario assertions

Use exact assertions for laws and invariants. Tactical cases allow several credible actions and assess consequences over a time window, not just one chosen action name.

| Scenario | Main acceptance evidence |
| --- | --- |
| 3v2 build-up | Useful short/deeper options; free receiver can be found; safe progress without compulsory pass sequence. |
| 4v3 wide overload | Complementary width/support; overload creates an opportunity instead of permanent congestion. |
| CB steps out | Gap coverage, retained runner responsibility, explicit recovery and handback. |
| DM receives under rear pressure | Scan/call/memory changes shield, bounce pass, turn, or touch choice appropriately. |
| Two attackers against high line | Tactical timing before release and contact-snapshot eligibility after release. |
| Winger plus fullback | Defender response changes the useful run/pass; rest-defence cost can veto the run. |
| 3v3 counterattack | Forward opportunity weighed against pressure and loss exposure; defence can delay/recover. |
| Touchline press | Angled pressure plus screened escape and useful cover; sensible exit if bypassed. |
| Box cross | Complementary arrival paths; actual deliverable trajectory; recycle when delivery is poor. |
| Keeper sweep race | Ball height, competing arrival, defender recovery, and goal exposure influence the decision. |

### Hard invariants and regressions

- Finite state, bounded probabilities, valid paths/targets, and normalized control shares; no impossible role/coverage ownership or unbounded coordination queues.
- Contact-time offside and restart semantics; no tactical onside estimate is used as the legal verdict.
- Same seed/config/model/input yields identical AI and authoritative state on the supported runtime; recorded commands reproduce execution with AI bypassed; interleaved matches and logging do not interfere.
- Unseen instantaneous motion cannot leak through observation, spatial maps, assignments, advertised runs, or future-state search.
- Increasing defender reach/speed does not systematically make a pass safer; increasing pressure does not systematically improve first touch; adding central cover reduces exposure all else equal.
- Public constructor/config validation, human lock/call handling, training resets, contact reach, firm passes, carry boundaries, keeper holding, and tackle restraint remain covered.

### Tactical measurements

Track executed pass/shot/contact outcomes alongside width, length, centroid, line distances, local density, progression, turnovers by zone, and possession duration. Add opportunities offered/taken, run expiry/offsides, press attempts/bypasses, cover handovers, counterpress escapes, switches, combination continuations, scans, memory age, and poor touches.

For each new metric, specify numerator, denominator, event source, and live-play window. For example, pass completion is completed executed passes divided by executed pass attempts; run offers are coordinated run events rather than one count per decision tick. Attribute a combination from contact/intent events rather than proximity alone.

Use a fixed development seed set and a separate held-out seed set. Start release evaluation with at least eight paired seeds for each supported matchup; expand only where variance prevents a decision. Report distributions and per-seed results, not only averages. Style differences should appear in several relevant metrics and visible behavior, not just scorelines or pass counts. Preserve the existing tactical differentiation gate; change a gate only with documented behavior evidence, never merely to make a failure disappear.

### Performance envelope

Initial targets, to be checked against A0's measured machine: at 22 active players in a warmed Release build, diagnostics disabled, AI work p95 at or below 4 ms and total simulation tick p95 at or below 10 ms, with p99 within the 16.67 ms fixed-step window. These are proposed engineering budgets, not measured current performance or research constants. Record hardware and revise an infeasible budget explicitly before adding more behavior.

Start with at most 12 off-ball candidates, 20 carrier candidates after cheap pruning, and the 3-by-3 continuation search described above. Bound trajectory work while using swept-segment checks or conservative rejection so the sample cap cannot silently miss an interceptor. Candidate/work limits are deterministic; wall-clock timing must never change a simulated choice. Performance CI should use stable hardware; portable tests enforce work limits and allocation behavior rather than fragile timing assertions.

### Debugging surfaces

Extend the existing inspector in stages: chosen/runner-up utility and rejection reasons in A1/A4; control/pressure and knowledge source in A2; duties/coverage/rest-defence risk in A3; run paths, contact-time offside, and pass meet points in A5; press/line/keeper race targets in A6; gaze, memory age, first-touch plans, and active contracts in A7/A8. Show whether an overlay uses truth, team belief, or player belief.

## 6. Release slices and first implementation batch

| Slice | Required milestones | Visible result |
| --- | --- | --- |
| Foundation | A0–A2 | Replayable scenarios, safe execution cadence, credible arrival/control and knowledge inputs. |
| First playable tactical AI | A3–A4, plus the current regression suite and targeted rendered review | Support and progression with structural cover and risk-aware carrier choices. |
| Advanced match AI | A5–A7 | Timed runs, coordinated defence, integrated keeper, scanning, and planned reception. |
| Advanced release | A8–A9 | Useful combinations/restarts, differentiated styles, bounded runtime, documented validation. |
| Calibrated models | A10 | Optional evidence-based parameter/model improvements. |

Begin with **A0**. The first reviewable batch should deliver the baseline artifact, reusable CB-step-out and 3v2 fixtures, and structured decision traces. Complete the remaining A0 fixtures next, then deliver A1 extraction, replay/RNG isolation, contact-time offside coverage, and reset/scheduling changes in separate increments. Keep tactical weights and behavior unchanged until the baseline is reproducible. Implement A2 reach/perception boundaries before expanding the behavior library.

This plan does not assign calendar dates: A0 establishes the measured baseline and implementation cost. Record actual milestone evidence and remaining work as implementation proceeds.

## 7. Research-to-work traceability

| Research report sections | Implementation coverage |
| --- | --- |
| Executive model; real-football state; reference shape/responsibilities | A1 boundaries, A2 reach/control, A3 context/ownership |
| Laws must be deterministic | A1 contact geometry, A5 run timing, A6 handling, A8 restarts |
| Attacking structure; superiorities; receiving value; switches | A3 structure, A4 value, A5 movement, A8 continuations |
| Runs; decoys; crosses/cutbacks | A5 path candidates, response-dependent value, arrival responsibilities |
| Defensive structure; cover shadows; handovers; counterpress | A3 cover/rest defence, A6 defensive policy, A8 traps |
| Goalkeeper behavior | A4 support options, A6 keeper/line coordination |
| Cognition; memory; scanning; communication; correlated error | A2 minimum knowledge model, A7 full cognition/reception |
| System architecture; update rates; hysteresis; limited planning | A1 runtime, A2 caches, A4 selection, A8 bounded search |
| Validation; scenarios; properties; overlays; metrics; ablations | A0 harness, milestone gates, A9 release evaluation |
| Advanced data-informed model; research-data calibration | Optional A10 |
