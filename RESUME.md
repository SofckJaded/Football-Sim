# RESUME.md — checkpoint

Read this after `AGENTS.md`; do not re-derive state from the planning document.

**Last updated:** 2026-11-12 (session 1, after batch 1)

---

## Current goal

Implement `docs/ADVANCED_AI_IMPLEMENTATION_PLAN.md` in batches, starting at **A0**. Nothing from A1
onward starts until the A0 exit gate passes.

## Active batch

**Batch 2 — A0.3, first half: scenario fixtures 1–5 plus a scenario runner.**
Milestone A0 is 2/5 tasks done (A0.1, A0.2).

## Last completed task

**A0.2** — explicit scenario world. `src/FootballSim/Scenarios/` (`MatchScenario`, `ScenarioPlayer`,
`ScenarioCommands`, `FixedCommandController`, `ScriptedCommandController`),
`MatchSimulationEngine(MatchScenario)`, `BallPossessionTracker.Seed`,
`TeamTacticalCoordinator.SeedPossession`, `MatchRulesEngine.StartScenario`.
12 tests in `tests/FootballSim.Tests/Scenarios/ScenarioSetupTests.cs`. Suite 203/203 green.

## Next recommended batch

Batch 2 (A0.3 part 1). Build reusable fixtures under `tests/FootballSim.Tests/Scenarios/`:

| # | Fixture | Home | Away | Key starting condition |
| :-- | :--- | :--- | :--- | :--- |
| 1 | 3v2 build-up | GK, CB, CM (carrier) | CB, CM | home controls centrally |
| 2 | 4v3 wide overload | GK, CB, FB, W + CM | 3 | ball with wide full-back |
| 3 | CB steps out | 4 | 3 | defender is pulled out of line |
| 4 | Rear-pressure reception | GK, CB, DM (in flight pass) | presser behind receiver | `InFlightPass` |
| 5 | High line run | 4, runner in behind | 3 + GK high | attacker ahead of the line |

Then a `ScenarioRunner` helper (step N live ticks, stop at full time, return a `ScenarioRunResult`
with final positions, possession, contacts, events) so assertions stop re-implementing loop code.

Each fixture also gets one determinism/golden-shape test. Do **not** write tactical assertions yet —
that is A0.3's exit gate and needs A0.4's trace to be meaningful.

## Files currently relevant

- `src/FootballSim/Scenarios/MatchScenario.cs` — the seam. Read `Validate()` first: it states what a
  fixture may and may not assume.
- `src/FootballSim/Scenarios/ScenarioControllers.cs` — `ScenarioCommands.Move/Hold/MoveToward`.
- `tests/FootballSim.Tests/Scenarios/ScenarioSetupTests.cs` — fixture pattern (`ThreeVersusTwo`),
  `FindById` helper, determinism test shape to copy.
- `src/FootballSim/TacticsAndTeamAI.cs` — anchors still keyed by shirt number; `teamSize` now comes from
  `PlayersPerTeam`.
- `src/FootballSim/TeamJobs.cs` — job assignment; scenarios inherit real jobs, not scenario-specific ones.

## Known blockers / risks

- **Anchor table is shirt-keyed, not role-keyed.** `ComputePlayerAnchor` uses
  `GetFormationRoleAndSlot(ShirtNumber, teamSize)`, so a scenario player's tactical anchor depends on
  their shirt number even though their `TacticalRole` is explicit. Assign realistic shirts in fixtures
  until A3 replaces formation ownership.
- **Unbalanced sides inherit a 5v5 anchor table.** With 3 home / 2 away, `PlayersPerTeam = 3` selects
  the 5-a-side slot table for both teams. Fine for short probes, wrong for anything else — A3's job.
- **Scenario controllers are stateful.** A `MatchScenario` holding a `ScriptedCommandController` must
  not be reused for two engines (the script will be half-consumed). Build a fresh fixture per engine.
- **`KeeperHeld` start is implemented but untested.** It sets `Ball.HeldByPlayerId`; prove it with a
  keeper test before relying on it.
- No git identity is configured globally on this machine; each clone needs `git config --local user.name/user.email`.

## Important decisions

- Scenario ownership is seeded directly (`BallPossessionTracker.Seed`) rather than faked with a synthetic
  contact event, because a fake contact would also fire statistics and rule handlers.
- `MatchRulesEngine.StartScenario` takes primitives, not a `MatchScenario`, so the rules engine stays
  free of the scenario type.
- Scenario matches skip the even-team-count part of `SimulationConfigValidation.Validate` (3v2 is
  deliberately unbalanced) but still validate pitch, profiles, attributes, tendencies and every player.
- Baseline evidence lives in `artifacts/advanced-ai/`; the reference machine is recorded in its README.

## What not to redo

- Do not re-run the baseline suite or the tactical gate to confirm they pass — recorded for 2026-11-12.
- Do not re-read `docs/ADVANCED_AI_IMPLEMENTATION_PLAN.md` end to end; read the A0 section only.
- Do not add tactical assertions to scenario tests before the trace exists.
- Do not "fix" the wide-anchor limitation by rewriting `TacticsAndTeamAI` now — A3 owns structural
  ownership and will replace it.
