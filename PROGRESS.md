# PROGRESS.md — completed work log

Newest entries first. One entry per completed task or batch. Keep entries short and factual: what
changed, where, and what proves it. Details that a future session needs go in `RESUME.md`.

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
