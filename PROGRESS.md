# PROGRESS.md — completed work log

Newest entries first. One entry per completed task or batch. Keep entries short and factual: what
changed, where, and what proves it. Details that a future session needs go in `RESUME.md`.

---

## 2026-11-12 — Session 1, batch 3: A1.4 contact-time offside + A1.3 random-stream split

- **A1.4 — laws judged from the play, not from the physics result.** The rules engine runs after
  `BallPhysicsSolver.Step`, so it was drawing the offside line from a ball position the physics had
  already advanced: a long pass could lift the line past a genuinely offside attacker, and the offside
  free kick was placed where the ball ended up rather than where the offence happened.
  - New `ContactGeometrySnapshot` (in `RulesAndGoalkeeper.cs`): preallocated, reused buffer holding the
    tick, the ball position and every player's id/team/position/active flag as of the play.
  - `MatchSimulationEngine.StepTick` captures it right after contact execution and **before**
    `_ballSolver.Step`, then passes it to `StepRules`.
  - `SnapshotOffsidePositions` and the offside-offence restart spot use the snapshot when present, and
    fall back to live state when it is not — so standalone probes, dry-run rule invocations and every
    pre-A1.4 test keep working unchanged.
  - 5 tests (`ContactTimeOffsideTests`), including the invariant the plan asks for: **moving the ball
    after the contact does not change the offside verdict**, plus a test that pins the old live-state
    behaviour of the no-snapshot path so the fallback cannot silently become the main path.
- **A1.3 — separate random streams.** One `SeededRandom` was shared by perception noise, and physical
  execution, so any perception change shifted every later physics sample.
  - `RandomStream` (Execution / Perception / Decision) and `RandomStreams.Derive`: SplitMix64's
    finalizer over `seed + goldenRatio*(stream+1)`, documented and tested for determinism, separation
    and collision-freedom over 512 consecutive seeds.
  - Engine now owns `_executionRng` and `_perceptionRng`; the decision stream is reserved but unused so
    its seed never has to move later.
  - `SimulationVersion.Tag` (`ai2-rs1`) printed by every headless run, so artifacts state which
    behaviour produced them.
  - 9 tests (`RandomStreamTests`): derive determinism, stream separation, no state sharing, "perception
    draws do not change the execution sequence", same-seed identity, interleaved matches, different
    seeds diverging, scenario replay identity.
- **Behaviour change handled explicitly, not hidden.** Both changes alter trajectories, so the tactical
  differentiation gate was re-run rather than assumed: **PASS** (5/5 seeds, mean width separation
  2.64 m, up from 1.92 m). 11v11 re-captured: 29–17 (was 25–10), possession 50.6/49.4. Perf re-measured:
  mean 113.0 µs, p99 421.9 µs, allocation unchanged at 41.3 KB/tick. All recorded under
  `artifacts/advanced-ai/` with the new version tag, and the decision records are in `docs/DECISIONS.md`.
- **Suite: 260/260 green.**

---

## 2026-11-12 — Session 1, batch 2: A0 complete (fixtures, trace, performance)

- **A0.3 scenario library** — `src/FootballSim/Scenarios/ScenarioLibrary.cs`: the ten required fixtures
  as named factories with a stated intent each (`build-up-3v2`, `wide-overload-4v3`, `cb-step-out`,
  `rear-pressure-reception`, `high-line-run`, `winger-fullback`, `counterattack-3v3`, `touchline-press`,
  `box-cross`, `keeper-sweep-race`). Plus `ScenarioRunner` / `ScenarioRunResult`: steps a fixture, stops
  at full time or budget, and returns final state, contact attempts, executed contacts, rule events and
  (optionally) the decision trace. New `--scenario <key> [ticks]` CLI mode so a behaviour can be
  reproduced without writing a test.
  - 11 new tests (`ScenarioLibraryTests`): ten scenarios × (start state, seeded ball on tick 1, identical
    two-engine replay), plus targeted invariants (ball genuinely in flight to its intended receiver, the
    high-line striker onside at the start) and runner behaviour (budget stop, early full-time stop).
- **A0.4 decision trace** — `src/FootballSim/AI/DecisionTrace.cs`:
  - `DecisionTraceEntry` (tick, player, team, role, duty, job reason, chosen action, utility, runner-up,
    runner-up utility, candidate count, target player, target point) and a bounded
    `DecisionTraceBuffer` (4 096 entries, oldest dropped, off by default).
  - `MatchSimulationEngine.DecisionTrace`; the engine records the utility AI's own candidate set (so the
    runner-up and margin are real), and any other controller by its debug summary.
  - `MatchSimulationEngine.ContactLog` (bounded 4 096): executed contacts, kept separately from the
    chosen action. This is the "chosen vs executed" split the A0 gate asks for.
  - `KnowledgeAgeTicks`, `CandidateKey`, `IntentId` are null by design and documented as belonging to
    A2.2 / A1.1 / A1.2 — a zero would look like a measurement.
  - 8 tests (`DecisionTraceTests`) including "trace does not change the simulation" (byte-identical run
    with tracing on vs off) and the capacity bound.
- **A0.5 performance sample** — `--perf` CLI mode (warm-up, percentiles, allocation per tick, GC counts,
  budget verdict). Release 11v11, 20 000 ticks: mean **101.7 µs**, p50/p95/p99 **68.5/273.6/343.3 µs**,
  max 963.7 µs, **41 727 bytes allocated per tick**, 50 gen-0 collections. Recorded in
  `artifacts/advanced-ai/a0-perf-2026-11-12.txt` and summarised in that directory's README.
  - The allocation figure is a real finding, not a surprise to hide: it is now the first entry in a new
    "Advanced AI — baseline measurements and known limitations" section of `docs/KNOWN_ISSUES.md`,
    alongside the shirt-keyed anchor limitation and the scenario-history limitation.
- **A0 exit gate passed.** Suite **246/246 green**. Milestone A1 is next.
- **Two A0.5 sub-items deliberately deferred with reasons recorded in `TASKS.md`:** rendered clips
  (belong to A9.4, need a human at the machine) and a per-phase tick breakdown (A9.3). Neither is a
  condition of the A0 exit gate.

---

## 2026-11-12 — Session 1, batch 1: A0.1 baseline + A0.2 scenario seam

- **A0.1 baseline captured** (`artifacts/advanced-ai/`):
  - Suite: 191/191 passed (13 s, Debug).
  - 11v11 headless, Release: FULL TIME, 326 152 ticks, 22.0 s wall (≈ 67 µs/tick). Score 25–10,
    possession 47.0/53.0, passes 812/767 (86 %/90 %), shots 96/69, width 42.4/41.7 m, kicks 1767/1938
    executed with `OutOfReach`/`BallTooHigh`/`LostArbitration`/`FacingAway` refusals.
  - Tactical differentiation gate: 5/5 seeds completed and played, possession side wider on 5/5,
    mean width separation 1.92 m → **PASS**.
  - `artifacts/advanced-ai/README.md` records the reference machine and explains what is still missing
    (allocations, 5v5, rendered clips, fixed seed sets).
- **A0.2 scenario seam** — new `src/FootballSim/Scenarios/`:
  - `MatchScenario` + `ScenarioPlayer`: explicit players (id/team/shirt/role/position/velocity/facing/
    stamina/attributes/tendencies/active/controller), ball state, control setup
    (`Loose`/`Controlled`/`InFlightPass`/`KeeperHeld`), phase, clock, score, profiles, seed, duration.
    `Validate()` rejects duplicate ids, off-pitch players, non-finite vectors, bad stamina, unknown
    owners/receivers, and a controlled ball more than 2.5 m from its owner. Shirt numbers auto-assign
    in registration order when unset.
  - `ScenarioCommands`, `FixedCommandController`, `ScriptedCommandController` — scripted opponents that
    never fall back to utility AI.
  - Engine: `MatchSimulationEngine(MatchScenario)` overload plus `Scenario` property; squad built from
    specs (human lock, keeper controller, per-spec controller all honoured); ownership seeded without a
    contact event.
  - Seams added to existing types: `BallPossessionTracker.Seed`, `TeamTacticalCoordinator.SeedPossession`,
    `MatchRulesEngine.StartScenario`, `TeamTacticalCoordinator.UpdateTeamAnchors(..., int teamSize)`
    (normal matches pass `PlayersPerTeam`, which equals the old `players.Count / 2`).
  - **12 tests** in `tests/FootballSim.Tests/Scenarios/ScenarioSetupTests.cs`: exact start state,
    seeded control visible on tick 1, loose ball, four validation failures, byte-identical 240-tick
    determinism across two engines, scripted controller actually drives execution, fixed controller never
    reacts, a scenario still reaches full time, and normal match construction is unchanged.
  - Suite now **203/203 green**. `README.md` test count corrected from 180 to 203.

---

## 2026-11-12 — Session 1, batch 0: repository bootstrap

**Goal of this batch:** make the repository a self-describing, versioned working environment before any
AI code changes.

- **S1.1 Repo initialised.** `git init` on `master`, remote `origin` →
  `https://github.com/SofckJaded/Football-Sim.git`. Repo-local identity set (no global git identity was
  configured on this machine).
- **S1.2 `.gitignore` rewritten.** Now covers .NET build output (`bin/`, `obj/`, `*.dll`, `*.pdb`,
  `*.exe`, NuGet caches), IDE state, Godot (`.godot/`, `*.translation`, `*.import`,
  `export_presets.cfg`), test/coverage output, logs, OS junk, and the stale duplicated source tree under
  `artifacts/baseline-*/`. Recorded evidence under `artifacts/` stays versioned.
- **S1.3 `AGENTS.md` added.** Durable operating contract: toolchain commands, batching protocol, tracker
  protocol, architecture rules that are easy to violate, codebase map, definition of done, style, git.
- **S1.4 Tracker files created.** `TASKS.md` (A0–A10 backlog transcribed from the plan, plus cross-cutting
  obligations), `PROGRESS.md` (this file), `RESUME.md` (checkpoint).
- **S1.5 Baseline verified.** `dotnet test tests\FootballSim.Tests` → **191 passed, 0 failed, 13 s**
  (2026-11-12, .NET SDK 8.0.426, Debug). README still claims 180 tests; corrected later with A0.1 evidence.

`docs/ADVANCED_AI_IMPLEMENTATION_PLAN.md` left untouched — it is the source of truth for scope.
