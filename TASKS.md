# TASKS.md — advanced AI backlog

Source of truth for remaining work. Derived from `docs/ADVANCED_AI_IMPLEMENTATION_PLAN.md`
(milestones A0–A10). One checkbox per plan task. A box is ticked only when code + evidence exist.

Legend: `[ ]` not started · `[~]` in progress (details in `PROGRESS.md`) · `[x]` done and proven.

---

## Status summary

| Milestone | State | Tasks |
| :--- | :--- | :--- |
| A0 Baseline + scenario harness | **complete** | 5 / 5 |
| A1 Runtime seams | **in progress** | 2 / 5 |
| A2 Spatial foundations | not started | 0 / 6 |
| A3 Team context + responsibilities | not started | 0 / 6 |
| A4 Carrier decisions | not started | 0 / 6 |
| A5 Off-ball runs | not started | 0 / 6 |
| A6 Coordinated defence + keeper | not started | 0 / 6 |
| A7 Cognition + reception | not started | 0 / 6 |
| A8 Combinations + restarts | not started | 0 / 5 |
| A9 Styles + performance + release | not started | 0 / 5 |
| A10 Optional calibration | not started | 0 / 4 |

---

## A0 — Baseline and scenario harness

Primary files: `tests/FootballSim.Tests`, `src/FootballSim.Headless/Program.cs`,
`tools/FootballSim.AuditProbe/Program.cs`. New shared scenario support: `src/FootballSim/Scenarios/`
and `tests/FootballSim.Tests/Scenarios/`.

- [x] **A0.1** Run existing suite and headless tactical gate; capture current 5v5/11v11 behaviour.
  Audit diagnostics stay intact. — *191/191 green; 11v11 full time in 22.0 s / 326 152 ticks (25–10);
  tactical gate PASS on 5/5 seeds.* Evidence: `artifacts/advanced-ai/`.
- [x] **A0.2** Scenario setup from positions, velocities, roles, attributes, ball/control state,
  direction, seed, duration. Helper controllers for fixed/scripted opponents. — *`FootballSim.Scenarios`
  (`MatchScenario`, `ScenarioPlayer`, `FixedCommandController`, `ScriptedCommandController`,
  `ScenarioCommands`); `MatchSimulationEngine(MatchScenario)`; seeded ownership via
  `BallPossessionTracker.Seed` + `TeamTacticalCoordinator.SeedPossession`; `MatchRulesEngine.StartScenario`.
  12 tests in `tests/FootballSim.Tests/Scenarios/ScenarioSetupTests.cs`.*
- [x] **A0.3** Establish ten initial scenarios: 3v2 build-up, 4v3 wide overload, CB step-out, rear-pressure
  reception, high-line run, winger/fullback overlap, 3v3 counterattack, touchline press, box cross, and
  keeper sweep race. — *`src/FootballSim/Scenarios/ScenarioLibrary.cs` (10 named fixtures, each with a
  stated intent) + `ScenarioRunner`/`ScenarioRunResult`; `--scenario` CLI mode.
  `ScenarioLibraryTests`: 10 scenarios × start state / seeded ball / two-engine determinism, plus
  targeted invariants (pass in flight to its receiver, striker onside at start).*
- [x] **A0.4** Log a bounded trace of tick, player, knowledge age, role/duty, candidate identity,
  utility terms, selected intent, cancellation, and executed/refused contact. Add trace assertions
  incrementally as behavior is implemented. — *`AI/DecisionTrace.cs`: `DecisionTraceBuffer` (bounded,
  off by default) + `DecisionTraceEntry`. Engine records tick/player/duty/job reason/chosen action/
  runner-up/margin/candidate count/target player; `ContactLog` records executed contacts separately.
  `KnowledgeAgeTicks`, `CandidateKey`, `IntentId` stay null until A2.2/A1.1/A1.2 rather than faking a
  value. 8 tests including "tracing does not change the simulation" and the capacity bound.*
- [x] **A0.5** Capture same-machine Release timings, allocations, existing match metrics, and short
  rendered clips. Store seed/config/toolchain/hardware with results under a new `artifacts/advanced-ai/`
  directory. — *`--perf` CLI mode; `artifacts/advanced-ai/a0-perf-2026-11-12.txt`: mean 101.7 µs,
  p99 343.3 µs, 41.7 KB allocated/tick. Metrics + hardware + seeds recorded in
  `artifacts/advanced-ai/README.md`. **Rendered clips deferred** to A9.4 — they need a human at the
  machine, and nothing in A0's exit gate depends on them.*

**Exit gate:** each scenario reproduces its start state and runs deterministically; baseline full matches
reach full time; measurements distinguish chosen from executed actions.

> **Gate status: PASSED 2026-11-12.** Scenarios reproduce byte-identically across two engines
> (`ScenarioLibraryTests.Scenario_ReproducesIdenticallyAcrossTwoFreshEngines`); the 11v11 baseline
> reached full time; `DecisionTrace` (chosen) and `ContactLog` / `LastContactAttempts` (executed,
> refused) are independent sources. Two jobs were deliberately not done here because they belong to
> later milestones: rendered clips (A9.4) and a per-phase tick breakdown (A9.3).

## A1 — Runtime seams without changing tactical policy

Primary files: `MatchEngine.cs`, `CorePrimitives.cs`, `IndividualAI.cs`, `SimulationConfigValidation.cs`,
`FootballerLocomotion.cs`, `RulesAndGoalkeeper.cs`. New modules: `AI/AiRuntimeConfig.cs`, `AI/AiScheduler.cs`,
`AI/PlayerIntent.cs`, `AI/DecisionTrace.cs`.

- [ ] **A1.1** Extract perception and candidate/selection helpers (behaviour-preserving). Add validated
  config, stable candidate keys, bounded trace buffers, explicit reset hooks.
- [ ] **A1.2** Controller-owned intent executor; keep current decision cadence initially. Scheduling behind
  a feature switch only after command/contact behaviour is pinned.
- [x] **A1.3** Separate RNG streams; extend command replay coverage before any cadence/noise change.
  Interleaved matches and diagnostics-on/off stay isolated. — *`RandomStream` + `RandomStreams.Derive`
  (SplitMix64 finalizer over `seed + goldenRatio*(stream+1)`); engine now owns `_executionRng` and
  `_perceptionRng`; `SimulationVersion.Tag = ai2-rs1`. 9 tests in `RandomStreamTests` including
  "perception draws do not change the execution sequence" and interleaved-match isolation. Behaviour
  change accepted and re-baselined: tactical gate re-run → PASS (5/5 seeds, mean width separation
  2.64 m), 11v11 re-captured (29–17).*
- [x] **A1.4** Capture offside geometry at the contact instead of reading an advanced ball position.
  Preserve event ordering, restart-origin stamping, deliberate-play/save distinctions, exemption tests.
  — *`ContactGeometrySnapshot` (preallocated, reused; ball + player geometry captured before the physics
  step) threaded through `StepRules`/`ProcessContactEvents`/`ApplyContactToOffsidePhase`/
  `SnapshotOffsidePositions`; the offside free-kick spot uses the contact geometry too. Passing no
  snapshot keeps the legacy live-state path. 5 tests in `ContactTimeOffsideTests`, the key one being
  "offside verdict does not change when the ball moves after the contact".*
- [ ] **A1.5** Reset handling for restart, halftime/direction reversal, full time, player removal, training
  reset, controller replacement — clear memory, caches, intent, queued calls, contracts explicitly.

**Exit gate:** extraction preserves commands; scheduled execution never duplicates a kick; command replay
works with different perception cadence or AI bypassed; offside invariant to post-contact ball movement.

## A2 — Spatial foundations and the knowledge boundary

Primary files: `BallPossession.cs`, extracted `PerceptionSystem`, `IndividualAI.cs`, `PassPlanner.cs`.
New modules: `AI/TimeToReachModel.cs`, `AI/PressureModel.cs`, `AI/SpatialAnalysis.cs`, `AI/PlayerMemory.cs`.

- [ ] **A2.1** One reach model: velocity, turn cost, acceleration, fatigue-adjusted speed, contact reach.
  Validate against real locomotion, then use in responder election, interception, control, sweep.
- [ ] **A2.2** Sight filtering + persistent observation memory. Unseen entities keep predicted positions
  with growing uncertainty (not fresh truth at lower confidence). Timestamps + orientation uncertainty.
- [ ] **A2.3** Read-only estimated-kinematics view for scorers; scouted capability for opponents, never
  hidden live attributes or intentions.
- [ ] **A2.4** Directional local pressure, arrival margins, coarse control on ~3 m grid; boundary cells
  explicit; cache by observer/tick/model inputs.
- [ ] **A2.5** Separate relative influence from absolute arrival: keep arrival times and an
  unclaimed/unreachable state alongside normalized control shares.
- [ ] **A2.6** Truth maps for validation, separate belief queries for tactics, plus control/pressure/
  perception-age overlays in `MatchClientNode.cs`.

**Exit gate:** reach predictions respond to turning/acceleration/opposing velocity; faster defenders do not
systematically improve estimated pass safety; maps finite/bounded/normalized; unseen motion cannot leak.

## A3 — Team context and persistent responsibilities

New modules: `AI/TeamContext.cs`, `AI/CoverageRequest.cs`, `AI/TransitionSecurityModel.cs`.

- [ ] **A3.1** Separate possession stability, attack/defence context, transition mode, restart context.
- [ ] **A3.2** Context-sensitive hysteresis replacing the single state lock; turnovers interrupt dwell.
- [ ] **A3.3** Nominal formation ownership + phase role/duty/temporary responsibility; slow reassignment on
  explicit events only, never a whole-team reassignment per tick.
- [ ] **A3.4** Job targets as constraint regions scored against ball/teammates/opponents/space.
- [ ] **A3.5** Coverage request lifecycle: creation, bids, deterministic owner, acceptance, recovery,
  handback, expiry, owner removal; bounded cascades.
- [ ] **A3.6** Rest-defence model: outlet routes, central access, recovery times, opponent threats.

**Exit gate:** CB step-out gap gets protected and handed back safely; perturbations do not swap roles;
width/length/line metrics plausible across 5v5/11v11/halftime/missing player; turnovers void duties.

## A4 — Carrier decisions and reception value

New modules: `AI/PassSuccessModel.cs`, `AI/ReceptionValueModel.cs`, `AI/ActionValueModel.cs`.

- [ ] **A4.1** Feasible passes to feet and run regions, carries, dribbles, shots, shield, clearance,
  recycle; keeper support where appropriate; boundaries/contact reach/technical capability as constraints.
- [ ] **A4.2** Extend calibrated ground trajectory with arrival margins, reaction/turn costs, reach/height,
  receiving speed, orientation, pressure, uncertainty; hard-reject blocked ground passes, evaluate lofted
  alternatives separately.
- [ ] **A4.3** Value post-reception state: progression, turn ability, next options, shooting chance,
  pressure, structure, turnover exposure.
- [ ] **A4.4** One documented scale for pass/carry/shot. Remove blanket backward-pass bans and guaranteed
  close-range shot bonuses only once better alternatives provably win. Prevent endless recycling.
- [ ] **A4.5** Robust hysteresis with re-evaluation:
  `best - current > absoluteMargin + relativeMargin * max(abs(current), epsilon)`.
- [ ] **A4.6** Preserve firm pass power, human-caller handling, intended-receiver metadata, keeper-safe
  distribution; add term-by-term explanations and rejection reasons to the inspector.

**Exit gate:** free receiver beats blocked lane; pressured backward-facing receiver < open equivalent;
unpressured team progresses instead of circulating; existing blocked-lane and firm-power tests still hold.

## A5 — Off-ball runs and reception compatibility

New modules: `AI/RunCandidateGenerator.cs`, `AI/RunIntent.cs`, `AI/RunCoordinator.cs`.

- [ ] **A5.1** Bounded path set: hold width, check short, in-behind, overlap, underlap, recycle support,
  blind-side/late box arrival, pin, decoy — support/depth before speculative decoys.
- [ ] **A5.2** Score paths on arrival advantage, pass availability, receiving orientation, line breaks,
  congestion, role usefulness, transition exposure; start windows/waypoints/regions/expiry, not timeless points.
- [ ] **A5.3** Advertise runs with confidence and communication delay; carrier matches pass arrival to the
  runner's future reachable region; runner stays committed long enough to receive.
- [ ] **A5.4** Tactical onside margins before release; eligibility belongs to the contact snapshot, no
  re-clamping every tick.
- [ ] **A5.5** Complementary box arrivals by arrival time, finishing/aerial ability, rest defence; compare
  early cross, driven/lofted, cutback, carry, recycle; extend prediction before scoring lofted deliveries.
- [ ] **A5.6** Pin/decoy value from a bounded follow-vs-hold defender response; remove sinusoidal anchor pushes.

**Exit gate:** high-line scenario produces correctly timed playable runs; winger/fullback chooses
overlap/underlap/support from defender response; cross targets are future meet points; no start/stop
oscillation; unavailable human is never auto-moved.

## A6 — Coordinated defending and goalkeeper integration

New modules: `AI/PressPolicy.cs`, `AI/DefensiveLineModel.cs`, `AI/GoalkeeperDecisionModel.cs`.

- [ ] **A6.1** Press, delay, cover, screen, track, intercept, hold, drop candidates; pressure-cover-balance.
- [ ] **A6.2** Angled pressure and cover-shadow scoring; back passes, poor touches, rear-facing reception,
  slow passes, touchline as evidence moderated by cover/bypass risk.
- [ ] **A6.3** Shared depth band from passer pressure, credible runs, field position, keeper sweep coverage;
  local exceptions, explicit marking handovers, zone/man mix by style.
- [ ] **A6.4** Turnover response: counterpress, delay/recover, or retreat from local arrival numbers,
  central security, carrier constraint, escape routes. Counterpress is a maximum window, not automatic.
- [ ] **A6.5** Shared reach/trajectory for sweep races, shot interception, cross claim/punch, 1v1 block,
  buildup support, distribution. Add missing physical capability before enabling its candidate.
- [ ] **A6.6** Gate keeper handling on implemented legal restrictions (area, deliberate back-pass);
  preserve held-ball protection, own-goal safeguards, one-release behaviour.

**Exit gate:** pressure usually has useful cover; unsupported press can lose to delay; secure vs exposed
turnovers diverge; sweep choices respond to arrival perturbations; existing keeper/offside/tackle tests green.

## A7 — Scanning, reception planning, cognitive differences

New modules: `AI/ScanPlanner.cs`, `AI/ReceptionPlanner.cs`, `AI/PlayerCognitiveState.cs`.

- [ ] **A7.1** Head/gaze direction independent of body facing, bounded scan motion, attention targets,
  simple occlusion, scan utility before reception/uncertain actions; feed memory from A2.
- [ ] **A7.2** Delayed expiring team calls (man-on, turn, step, drop, cover, switch, run) that update
  belief/preference, not hidden truth; team-scoped and bounded.
- [ ] **A7.3** Plan reception before arrival: kill, open body, escape pressure, stride touch, shield,
  prepare pass/shot/cross; evaluate orientation/touch pairs against the next action.
- [ ] **A7.4** Optional reception intent on the command boundary with neutral behaviour for existing callers;
  execution still resolves real contact/control error/pressure/foot constraints.
- [ ] **A7.5** Separate observation error, evaluation error, reaction latency, execution error; reuse
  stamina/composure/technical state; probabilistic anticipation that can be wrong.
- [ ] **A7.6** Archetypes as candidate/weight/perception preferences (playmaker, ball winner, inverted
  fullback, touchline winger, inside forward, target forward, sweeper keeper) on one pipeline.

**Exit gate:** scan reveals a rear threat that an unscanned player only estimates; man-on call changes
options after its delay; pressure reduces control quality; first-touch/training regressions pass; disabling
a cognitive feature measurably changes behaviour.

## A8 — Bounded combinations and restart coordination

New modules: `AI/CombinationCoordinator.cs`, `AI/ShallowActionSearch.cs`, `AI/RestartPolicy.cs`.

- [ ] **A8.1** One-two and third-player contracts with participants, return region, expected arrival,
  confidence, expiry, cancellation; any participant may abandon an invalid lane.
- [ ] **A8.2** Shallow search: best three first actions × three continuations, depth two; include defender
  uncertainty; no truth-state rollouts.
- [ ] **A8.3** Switch/overload recognition from local pressure, far-side opportunity, matchup, actual pass
  feasibility, turnover cost; no forced switch after N passes.
- [ ] **A8.4** Short trap contracts (pressure, screen, cover, escape condition, recovery); failed traps
  return players to valid duties.
- [ ] **A8.5** Small legal routine library for kick-off, throw-in, corner, free kick, goal kick with
  taker/support/target roles; rules supply legal placement; clear on completion/invalidation.

**Exit gate:** combination scenarios beat search-disabled; blocked return aborts cleanly; touchline trap
preserves cover and can be escaped; routines respect the law model; no contract outlives its possession.

## A9 — Styles, performance, release

- [ ] **A9.1** Presets: positional possession, direct, counterattacking, high press, low block, man-oriented;
  compare with identical squads/seeds and swapped ends.
- [ ] **A9.2** Ablations for local cover, rest defence, perception limits, orientation, commitment, run
  coordination, sweep, lookahead — find inert or overwhelming features.
- [ ] **A9.3** Profile Release 11v11; bound candidates, trajectory samples, map refreshes, queues, search
  nodes; reuse buffers in hot paths; tracing off must be ~free.
- [ ] **A9.4** Rendered Godot review: spacing, continuity, support, pressure/cover, reception, keeper,
  transitions; AI-only, active human, idle human, 5v5, 11v11, all drills.
- [ ] **A9.5** Enable by default only after gates pass; keep a validated legacy preset; version the AI model;
  update `ARCHITECTURE.md`, `TESTING.md`, `KNOWN_ISSUES.md` and the work-state files with evidence.

**Exit gate:** all scenario/replay/rule/training/tactical/performance/rendered checks pass; remaining
subjective tuning and law-model limits stated explicitly.

## A10 — Optional data-informed calibration

- [ ] Evaluate dataset coverage and usage terms before importing Metrica/StatsBomb material.
- [ ] Reproducible offline feature extraction + calibration, one model at a time.
- [ ] Keep training separate from runtime inference; version features/coefficients/dataset/evaluation;
  ship a heuristic fallback; no runtime network dependency.
- [ ] Validate on held-out matches/scenarios; never present tracking-data fit as proof of game calibration.

---

## Cross-cutting obligations (apply to every milestone)

- [ ] Written play never animates: motion continuity and controller feel are visually reviewed, not assumed.
- [ ] Every new metric documents numerator, denominator, event source, live-play window.
- [ ] Every hard invariant in plan §5 gets a test: finite state, bounded probabilities, valid targets,
  normalized control shares, contact-time offside, replay identity, no unseen-motion leak.
- [ ] Fixed development seed set plus a separate held-out seed set; ≥ 8 paired seeds per matchup at release.
- [ ] Diagnostics overlays are labelled truth / team belief / player belief.
