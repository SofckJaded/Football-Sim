# RESUME.md — checkpoint

Written at every stop. Read this after `AGENTS.md`; do not re-derive state from the planning document.

**Last updated:** 2026-11-12 (session 1)

---

## Current goal

Implement `docs/ADVANCED_AI_IMPLEMENTATION_PLAN.md` incrementally, in batches, starting at **A0**
(baseline + reusable scenario harness). Nothing from A1 onward may start until the A0 exit gate passes.

## Active batch

**Batch 0 (repository bootstrap) — complete.**
**Batch 1 (A0.1 headless baseline + A0.2 scenario setup API) — next.**

## Last completed task

Batch 0: `git init` + remote, `.gitignore`, `AGENTS.md`, `TASKS.md` / `PROGRESS.md` / `RESUME.md`,
baseline suite run **191/191 passing**.

## Next recommended batch

Batch 1 — A0.1 tail and A0.2, in this order:

1. **A0.1 finish.** Run the headless tactical gate and a full 11v11 headless match; record the numbers in
   `artifacts/advanced-ai/` so later behaviour changes have something to be compared against. Also correct
   the "180 tests" claim in `README.md` to the measured count.
2. **A0.2.** Add a scenario setup seam: explicit player specs (id, team, shirt, role, position, velocity,
   facing, attributes, tendencies, active), ball position/velocity/control owner, direction, seed, duration.
   Then a scripted/fixed controller helper for opponents. Proposed home: `src/FootballSim/Scenarios/`
   (`MatchScenario`, `ScenarioPlayer`, `MatchSimulationEngine` constructor overload) with the reusable
   fixtures under `tests/FootballSim.Tests/Scenarios/`.
3. **A0.3 start.** Get the first two fixtures (3v2 build-up, CB step-out) running deterministically.

## Files currently relevant

- `src/FootballSim/MatchEngine.cs` — engine construction, tick order, `OverrideController`.
- `src/FootballSim/CorePrimitives.cs` — `FootballerObservation`, `PreMatchConfig`, `TeamTacticalProfile`.
- `src/FootballSim/FootballerLocomotion.cs` — `FootballerState` (mutable `Position`/`Velocity`), contact rules.
- `src/FootballSim/RulesAndGoalkeeper.cs` — `StartMatch`, `SetupKickoffPositions`, phase/pause gating.
- `src/FootballSim/BallPossession.cs` — ownership tracker; a scenario must seed control coherently.
- `src/FootballSim.Tests/AuditRegressionTests.cs` — existing helpers (`Player(...)`, `Observe(...)`,
  `ParkEveryoneExcept`, `IdleController`) to reuse rather than reinvent.
- `src/FootballSim.Headless/Program.cs` — CLI modes for the baseline capture.
- `tests/FootballSim.Tests/Scenarios/` — does not exist yet; to be created.

## Known blockers / risks

- No git identity was configured on this machine; `git config --local` was used. If a future clone is made
  by a different tool, set identity again before committing.
- `artifacts/baseline-2026-11-07/` is a stale full copy of the source tree from an earlier session. It is
  git-ignored and must never be searched, edited, or treated as current code.
- Scenario construction has no first-class API yet: today a scenario would have to mutate
  `FootballerState` after construction and hope the possession tracker, tactical coordinator, team-job
  coordinator and rules engine all resynchronise. A0.2 exists to replace that with an explicit seam.
- `MatchSimulationEngine` exposes `Players` as `IReadOnlyList<FootballerState>` while the elements are
  mutable. Treat that as a leak to be closed by the scenario seam, not as a permission to poke state.

## Important decisions

- Tracking files are the durable memory; chat context is disposable.
- Milestones are a dependency graph (`A0→A1→A2→A3→A4→A5→A6→A7→A8→A9`, A10 optional), not a checklist.
- Behaviour-preserving extraction lands before behaviour changes, in separate commits.
- Recorded evidence lives in `artifacts/`; new advanced-AI output goes to `artifacts/advanced-ai/`.
- `AGENTS.md` is the instruction file name (the plural convention that agents auto-discover). It is the
  file the owner asked for as "agent.md".

## What not to redo

- Do not re-run the baseline suite to confirm it passes; it is recorded as 191/191 on 2026-11-12.
- Do not re-read the whole planning document — read the milestone section you are executing.
- Do not rewrite `README.md`, `docs/ARCHITECTURE.md`, `docs/TESTING.md` wholesale; make surgical edits when
  the plan calls for them (A9.5).
- Do not add a second team-job allocator, a parallel style/profile system, or a second world model.
