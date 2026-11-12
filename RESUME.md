# RESUME.md — checkpoint

Read this after `AGENTS.md`; do not re-derive state from the planning document.

**Last updated:** 2026-11-12 (session 1, after batch 3)

---

## Current goal

Implement `docs/ADVANCED_AI_IMPLEMENTATION_PLAN.md` in batches.
**A0 is complete (5/5, exit gate passed). A1 is 2/5** — A1.3 and A1.4 are done.

## Active batch

None open. Batch 3 finished clean: suite **260/260 green**, nothing uncommitted.

## Last completed task

**A1.3 + A1.4, in one batch.**
- A1.4: `ContactGeometrySnapshot` captured before the physics step and threaded into the offside
  judgement, so a laws decision no longer reads a ball the physics already moved.
- A1.3: `RandomStream`/`RandomStreams.Derive` split perception from execution; `SimulationVersion.Tag`
  is now `ai2-rs1` and printed by every headless run.

## Next recommended batch

**Batch 4 — A1.1, in two commits (both behaviour-preserving):**

1. **`AiRuntimeConfig`** (small, do this first). A validated settings object owned by match
   configuration: feature switches, cadences (15/10/6/5/3/1 Hz per plan §3), candidate limits
   (≤ 12 off-ball, ≤ 20 carrier), hysteresis margins, commitment ranges. Every field needs a validated
   range and a neutral default that reproduces today's behaviour exactly. Prove it with a test that
   the default configuration changes nothing and that an out-of-range value is rejected at
   construction. Do **not** wire the cadence fields into the tick yet — that is A1.2.
2. **Stable candidate keys.** Today `UtilityCandidate.ActionName` is both the label and the identity,
   which is why two different passes to two different players can share a name. Add a key of
   action type + target player/run/region id + variant, keep `ActionName` for display, and put the key
   into `DecisionTraceEntry.CandidateKey` (currently null by design). This touches every
   `_lastCandidates.Add(...)` site in `IndividualAI.cs` (~30) — mechanical but wide; do it as one
   reviewable commit and let the 260 tests prove behaviour is unchanged.

Then **A1.5** (reset hooks for restart / halftime / full time / player removal / training reset /
controller replacement) and **A1.2** (controller-owned intent executor, scheduler behind a feature
switch), in that order.

## Files currently relevant

| File | Why |
| :--- | :--- |
| `src/FootballSim/CorePrimitives.cs` | `RandomStream`, `RandomStreams`, `SimulationVersion`, `PreMatchConfig`, `UtilityCandidate` is in `IndividualAI.cs` |
| `src/FootballSim/IndividualAI.cs` | `UtilityCandidate`, `TacticalUtilityAiController.Decide`, every `Add(...)` candidate site, `CommitmentTimer` |
| `src/FootballSim/AI/DecisionTrace.cs` | where `CandidateKey` is waiting to be filled |
| `src/FootballSim/RulesAndGoalkeeper.cs` | `ContactGeometrySnapshot` (pattern for preallocated reuse) |
| `src/FootballSim/SimulationConfigValidation.cs` | where `AiRuntimeConfig` validation belongs |
| `tests/FootballSim.Tests/Scenarios/` | fixture + runner pattern for behaviour-preservation proofs |
| `docs/DECISIONS.md` | append a decision when a design choice is not self-evident |

## Known blockers / risks

- **Any change to candidate generation risks changing decisions.** The safe pattern used so far is:
  one commit for the pure refactor, a test that the same scenario produces the same 240-tick state
  before/after, and only then a behaviour change.
- **`IndividualAI.cs` is 1 340 lines.** A1.1 extraction is the moment to split it, but only in
  behaviour-preserving moves (perception → its own file, candidate generation → its own file). Do not
  restructure and re-tune in the same commit.
- **41.3 KB allocated per tick** — unchanged by A1.3/A1.4. New per-candidate arrays will make it worse;
  reuse buffers as `ContactGeometrySnapshot` does.
- **Trajectory versioning.** `SimulationVersion.AiBehaviour` must be incremented for anything that
  changes trajectories, and artifacts re-captured with the new tag. Do not compare across versions.
- No git identity is configured globally on this machine.

## Important decisions

- Laws geometry is frozen at the play (`ContactGeometrySnapshot`), not fixed by reordering the tick.
  Physics-then-rules order is load-bearing for boundary and goal handling.
- Passing no snapshot keeps the legacy live-state path, so standalone rule probes and older unit tests
  remain valid. A test pins that fallback.
- The RNG split is a declared version bump, not a silent change; cross-version trajectory identity is
  explicitly not required (same-version command replay still is).
- The decision stream (`RandomStream.Decision`) is reserved but unused, so its seed never has to move.
- Scenario fixtures live in the simulation assembly (not the test assembly) because the CLI and the
  suite must run the same fragment.

## What not to redo

- Do not re-run the baseline suite, tactical gate, 11v11 match or perf sample to confirm recorded
  numbers — all four are in `artifacts/advanced-ai/` for 2026-11-12 under tags `ai1` and `ai2-rs1`.
- Do not re-read the planning document end to end; read the A1 section only.
- Do not add tactical assertions to scenario tests yet (behaviours are A3–A6 work).
- Do not "fix" shirt-keyed anchors or the 5-a-side table for unbalanced sides — A3 owns structure.
- Do not weaken any of the 260 tests to make a refactor pass.
