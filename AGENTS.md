# AGENTS.md — operating instructions for AI sessions

This file is the standing contract for any agent (or human) working in this repository.
Read it first, then read `RESUME.md`, then `TASKS.md`. Do **not** re-read the full planning
document at the start of a session — read only the milestone section you are working on.

---

## 1. What this project is

A deterministic 60 Hz football (soccer) match simulation in C# 12 / .NET 8, rendered by Godot 4.7.2 (.NET).
All eleven players per team are driven by utility-based AI. A human may be locked to one player
pre-match; the AI must never take that player over.

- **Authority:** physics + match laws. The AI proposes `FootballerCommand`; the execution layer decides
  what actually happens (reach, arbitration, recovery, contact).
- **Determinism:** same seed + same config + same commands ⇒ identical state. No wall-clock time, no
  frame-rate dependence, no unordered collection iteration in decision paths.
- **Godot independence:** `src/FootballSim` must never reference `Godot`. Anything Godot-specific lives
  in `src/FootballSim.Godot`.

## 2. Toolchain and commands

| Purpose | Command |
| :--- | :--- |
| Toolchain pin | `.NET 8.0` SDK (`global.json`), Godot `4.7.2-stable` mono |
| Build everything | `dotnet build FootballSimulation.sln -c Debug` |
| Run the test suite | `dotnet test tests\FootballSim.Tests\FootballSim.Tests.csproj --nologo` |
| Headless 11v11 match | `dotnet run --project src\FootballSim.Headless -c Debug -- match` |
| Headless 5v5 match | `dotnet run --project src\FootballSim.Headless -c Debug -- 5v5` |
| Audit probe JSON | `dotnet run --project tools\FootballSim.AuditProbe -c Debug` |
| Shortcut wrapper | `start.bat check` (build + test), `start.bat verify` (build + test + probe) |

Tests take ~13 s. Always run the full suite after a change; it currently passes **298/298**.

### 2.1 Repository scope

Git holds **code only**, by owner decision. `README.md`, `PROGRESS.md`, `TASKS.md`, `RESUME.md` and
`docs/` are `.gitignore`d and untracked; they exist on the working machine and were removed from the
index on 2026-11-12. `AGENTS.md` stays tracked because it is a stable contract rather than per-change
documentation. Consequences to keep in mind:

- A fresh clone has **no docs and no plan**. Do not `git add -f` them back without the owner asking.
- Edits to untracked files are not recoverable from git history.
- Diffs are therefore always code diffs. Keep them that way: do not commit notes into code.

### 2.2 Recording evidence

Captured evidence goes under `artifacts/advanced-ai/` and **must be UTF-8 text**, so git can diff and
review it. `Tee-Object` writes UTF-16 by default, which git stores as an opaque binary blob
(converted to UTF-8 on 2026-11-12). Capture with an explicit encoding:

```powershell
dotnet run --project src\FootballSim.Headless -c Release -- --match-11v11 2>&1 |
    Out-File artifacts\advanced-ai\<name>.txt -Encoding utf8
```

Every artifact states `SimulationVersion.Tag` (e.g. `ai3-rs1`). **Only same-version numbers are
comparable**; a behaviour change bumps `SimulationVersion.AiBehaviour` and requires re-capturing the
tactical gate, the 11v11 match and the perf sample.

## 3. Working protocol (how work is executed in this repo)

### 3.1 Two-tier tracking

Three files at the repository root are the durable memory. Chat context is disposable.

| File | Holds |
| :--- | :--- |
| `TASKS.md` | The whole backlog as checkboxes, grouped by milestone. Ticked only when the code exists and the test/evidence exists. |
| `PROGRESS.md` | Append-only, newest-first log: one short entry per completed task (what changed, which tests prove it). |
| `RESUME.md` | Rewritten every checkpoint: current goal, last completed task, next batch, active risks. |

### 3.2 Batching

- Work in **batches of 3–8 small tasks** that share files or concepts. Fewer if a change is risky.
- Never stop after one tiny edit; always finish a batch before pausing.
- Never try to do a whole milestone in one turn. Milestones are several batches.
- After each completed task: tick `TASKS.md`, append to `PROGRESS.md`, commit.
- At the end of each batch: run the full suite, rewrite `RESUME.md`, commit, push.

### 3.3 Checkpoint and stop when

the batch is complete · context is getting long · a change turns out to be unexpectedly large ·
the plan and the code contradict each other · a decision needs the repository owner.

When stopping, `RESUME.md` must let the next session continue without re-deriving anything.

### 3.4 Change discipline

- Small, reversible commits with messages of the form `A0.2: add scenario setup API`.
- Run the test suite before every commit that touches `src/`.
- Behaviour-preserving refactors come **before** behaviour changes and get their own commit.
- Never change a regression test's expectation to make a failure disappear; change it only with
  documented behaviour evidence.
- Do not reformat or "tidy" unrelated files. Keep diffs reviewable.
- New configuration fields need: validation, a neutral default that preserves existing behaviour, and
  evidence (a test) that changing the field changes behaviour.

## 4. Architecture rules that are easy to violate

1. **One world.** `MatchSimulationEngine` owns `FootballerState` / `BallState` / laws. Do not introduce a
   second mutable world model.
2. **Knowledge boundaries.** Truth is available to physics, rules, and *labelled* diagnostics. Decision
   scorers may only use what perception and permitted communication delivered. An opponent's live
   position, velocity, or intent must not reach a scorer through a shared cache.
3. **One allocator.** `TeamJobCoordinator` assigns persistent team jobs. Extend it; do not add a second
   competing allocator.
4. **Discrete actions are not cached commands.** A kick, tackle, or call is emitted once and confirmed by
   contact feedback; it must not be re-issued every tick because a utility cache was stale.
5. **Cadence ≠ timers.** Slowing the utility cadence must not slow reaction latency, commitment expiry, or
   human-call timers. Timers tick every live tick.
6. **No duplicate penalties.** If execution already applies a fatigue/error effect, the scorer must not
   multiply by it again.
7. **Determinism.** Match-local seeded RNG streams only. Diagnostics and tracing must consume no simulation
   randomness. Elapsed live ticks are the clock (pauses are not play).
8. **Extend, do not fork.** Extend `TeamTacticalProfile`, `PlayerTendencies`, `PlayerAttributes`,
   `FootballerObservation` (with defaulted members), `MatchCommunicationContext`. No parallel systems.

## 5. Codebase map (where to look)

```
src/FootballSim/
  CorePrimitives.cs          types: pitch, RNG, enums, observation, attributes, profiles, PreMatchConfig
  MatchEngine.cs             authority: fixed tick, scheduling, controllers, analytics
  FootballerLocomotion.cs    FootballerState, contact rules, execution/steering/contacts
  BallPhysics.cs             BallState, flight, trajectory queries
  BallPossession.cs          control/loose/in-flight/keeper-held model, responder election
  IndividualAI.cs            TacticalUtilityAiController: candidates, utility, commitment
  PassPlanner.cs             calibrated ground-pass power/lead/flight, lane blocking
  TacticsAndTeamAI.cs        tactical coordinator, formation slots, anchors, possession hysteresis
  TeamJobs.cs                persistent outlet/width/depth/overlap/cover/marker/line jobs
  RulesAndGoalkeeper.cs      laws, offside, restarts, MatchRulesEngine; GoalkeeperController
  MatchStatistics.cs         executed-outcome counters
  SimulationConfigValidation.cs  construction-time validation
  HumanInputController.cs    the locked-human input path and call-for-pass
  Training/                  drills, sessions, training passer controller
src/FootballSim.Headless/    CLI: full matches, tactical gate, JSON output
src/FootballSim.Godot/       rendering, camera, HUD, inspector overlays
tests/FootballSim.Tests/     xUnit; ~5.4k lines of regressions
tools/FootballSim.AuditProbe/ standalone diagnostics probe
docs/                        plan, architecture, testing map, known issues, decisions
artifacts/                   recorded evidence (probe JSON, screenshots). `artifacts/advanced-ai/` is
                             the drop point for advanced-AI benchmark output.
artifacts/baseline-*/        stale full source snapshot from an older session — ignored by git,
                             excluded from searches; do not edit, do not treat as current code.
```

## 6. Where the work is defined

`docs/ADVANCED_AI_IMPLEMENTATION_PLAN.md` holds milestones **A0–A10** with exit gates. Its section 2
("Starting point") describes the current implementation accurately. Read the single milestone section
you are executing plus its exit gate; nothing else.

Milestone order is a dependency graph, not a suggestion:
`A0 → A1 → A2 → A3 → A4 → A5 → A6 → A7 → A8 → A9`, with A10 optional.
A3 needs A2. A4 needs A2+A3. A5 needs A3+A4. A6 needs A3+A4+A5. Do not start A8 before A5/A6/A7 gates pass.

## 7. Definition of done for a task

A task is done only when **all** of these are true:

1. The code exists and the solution builds with no new warnings.
2. Determinism is preserved (or the change is explicitly a versioned behaviour change).
3. A test, scenario assertion, or recorded artifact proves the behaviour — not a comment claiming it.
4. `TASKS.md` ticked, `PROGRESS.md` entry written, commit made.
5. Any newly exposed limitation is written into `docs/KNOWN_ISSUES.md`, not silently left.

## 8. Style conventions

- File-scoped namespaces; `sealed` by default; `readonly` fields; explicit types where the right-hand side
  is not obvious.
- XML doc comments on public types and on anything non-obvious (they are the reason the audit findings are
  traceable). Reference audit/plan IDs in comments when a rule exists for that reason.
- No `LINQ` in per-tick hot paths; no allocation per candidate where a reusable buffer is practical.
- Comments explain *why*, not *what*.

## 9. Git

- Remote: `https://github.com/SofckJaded/Football-Sim.git`, branch `master`.
- Commit and push at the end of every batch, and after any milestone gate.
- Never commit `bin/`, `obj/`, `.godot/`, or a duplicate source tree.
