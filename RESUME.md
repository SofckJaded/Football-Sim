# RESUME.md — checkpoint

Read this after `AGENTS.md`; do not re-derive state from the planning document.

**Last updated:** 2026-11-12 (session 1, after batch 2)

---

## Current goal

Implement `docs/ADVANCED_AI_IMPLEMENTATION_PLAN.md` in batches. **A0 is complete and its exit gate
passed.** The current milestone is **A1 — runtime seams without changing tactical policy**.

## Active batch

**Batch 3 — A1.4 (contact-time offside geometry) then A1.3 (RNG isolation).**
A0: 5/5 tasks. A1: 0/5 tasks.

## Last completed task

**A0.5** — Release performance sample. Also completed in batch 2: A0.3 (ten scenario fixtures +
`ScenarioRunner` + `--scenario` CLI) and A0.4 (bounded decision trace, deliberate split between chosen
action and executed contact). Suite **246/246 green**.

## Next recommended batch

Batch 3, in this order:

1. **A1.4 — capture offside geometry at the contact.** Today `MatchRulesEngine` reads the live
   `BallState` when it processes the contact, after `BallPhysicsSolver.Step` has already advanced the
   ball. Introduce a `ContactGeometrySnapshot` taken at the moment the contact is made (ball position,
   owning player positions, contact tick/order) and judge Law 11 from it. Must preserve: event ordering,
   restart-origin stamping (`StampRestartOrigin`), the deliberate-play/save distinction, and every
   existing offside exemption test. The proof is an invariant test: **moving the ball after the contact
   but before the rules tick must not change the offside verdict.**
2. **A1.3 — separate RNG streams.** Today one `SeededRandom(_config.Seed)` is shared by perception,
   decisions and physical execution, so adding a perception query shifts every subsequent physics
   sample. Split into derived match-local streams via a documented integer seed mixer, and prove:
   diagnostics on/off are identical, two interleaved matches in one process are independent, and an
   existing recorded-command replay still reproduces exactly.
3. If both land cleanly, start **A1.1** by extracting the perception block out of `PerceptionSystem`
   unchanged (behaviour-preserving commit), before touching candidate identity.

## Files currently relevant

| File | Why |
| :--- | :--- |
| `src/FootballSim/RulesAndGoalkeeper.cs` | `StepRules`, `SnapshotOffsidePositions`, `StampRestartOrigin`, `StartScenario` (added in A0.2) |
| `src/FootballSim/FootballerLocomotion.cs` | where `BallContactEvent` is created (line ~1285) — the snapshot belongs beside it |
| `src/FootballSim/MatchEngine.cs` | tick order: decisions → execution → stamp → statistics → possession → physics → rules |
| `src/FootballSim/CorePrimitives.cs` | `BallContactEvent`, `ControllerSourceType`, `SeededRandom` |
| `src/FootballSim/AI/DecisionTrace.cs` | trace buffer pattern to reuse for any new bounded log |
| `src/FootballSim/Scenarios/` | `ScenarioLibrary`, `ScenarioRunner` — fixtures for A1 proofs |
| `tests/FootballSim.Tests/AuditRegressionTests.cs` | existing offside/restart/replay regressions that must stay green |
| `docs/DECISIONS.md` | append a decision record for any design choice that a reader would otherwise re-litigate |

## Known blockers / risks

- **Tick ordering is delicate.** `_rulesEngine.StepRules` runs *after* `_ballSolver.Step`, which is
  exactly why the offside snapshot is stale. Reordering the tick is *not* the fix (physics and rules
  both depend on this order); capture geometry instead.
- **One shared RNG.** Splitting streams changes every recorded trajectory, so the existing
  byte-identical replay tests will fail by design unless the split preserves the *existing* stream for
  the existing consumers. Keep the legacy stream as stream 0 and derive new streams for new consumers;
  a change of physics trajectories is a versioned behaviour change, not a refactor.
- **46 files of audit regressions** pin current behaviour. Any refactor that "tidies" a formula will
  break them; that is the suite working, not an obstacle to route around.
- **41.7 KB allocated per tick** (measured) is the headroom A1 extraction must not enlarge.
- No git identity is configured globally on this machine.

## Important decisions

- A0's two deferred items (rendered clips → A9.4, per-phase tick breakdown → A9.3) are recorded in
  `TASKS.md` with reasons; do not silently drop them.
- Controller decisions are traced by reading `TacticalUtilityAiController.LastCandidates` at the engine
  level rather than by adding a callback to the controller. Keep the engine as the single observer until
  A1.2 introduces a real intent object.
- Scenario ownership is seeded directly, never faked with a synthetic contact event (a fake contact would
  also fire statistics and rule handlers).
- `MatchRulesEngine.StartScenario` takes primitives, not a `MatchScenario`, to keep the rules engine free
  of the scenario type.

## What not to redo

- Do not re-run the baseline suite, the tactical gate or the perf sample to confirm them — all recorded
  for 2026-11-12 under `artifacts/advanced-ai/`.
- Do not re-read the planning document end to end; read the A1 section only.
- Do not add tactical assertions to scenario tests yet; the trace exists, but the behaviours it would
  assert (support, cover, timing) are A3–A6 work.
- Do not "fix" the wide/shirt-keyed anchors — A3.3 owns structural ownership.
- Do not delete or weaken any of the 246 tests to make a refactor pass.
