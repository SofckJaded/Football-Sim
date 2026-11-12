# Testing Guide

## Suites

| File | What it proves |
| :--- | :--- |
| `tests/FootballSim.Tests/SimulationPhaseGatesTests.cs` | Same-seed determinism (two fresh AI matches), ball-physics lab behaviours, reach enforcement, controller-swap parity, utility-AI candidates, tactical profile shape, and the pre-match human lock. |
| `tests/FootballSim.Tests/AuditRegressionTests.cs` | Focused regressions for audit findings F01–F21: pass planning, keeper parry/hold/distribution, ownership + responder + reception scope, team jobs, shot aiming and the shooting model, contact-based metrics, behaviour fields, the controller-override lock, offside restarts and boundary/attribution. They drive the live controller/execution path, not test-only compatibility properties. |
| `tests/FootballSim.Tests/PresentationAndConfigTests.cs` | Audit F22: the ball's rendered dimensions come from the physics radius; the fixed-step clock simulates the same second at 30/60/144 FPS and bounds/reports catch-up; camera smoothing is time-based (and matches the old 60 FPS feel); invalid match/pitch/ball/attribute/tendency/profile configuration is rejected at construction; the HUD inspector id is resolved against the real squad instead of indexing by id. |
| `tests/FootballSim.Tests/ReplayAndCoverageTests.cs` | Audit F23: a **real** recorded-command replay (every decided command recorded from a live AI match and replayed into two interleaved engines, matching every tick bit-for-bit), a short match that reaches `HalfTime` and `FullTime` with both halves played and terminal full time, and a four-seed tactical gate (complete matches, execution-aware kick counts, width separation reported per seed and asserted across the set). |
| `tests/FootballSim.Tests/GameplayFeelTests.cs` | Playtest behaviours: an AI carrier near a touchline never carries the whole ball out; a first touch cushions instead of teleporting/kicking the ball away and stays in reach; two players on the same ball do not ping-pong it (only a requested tackle breaks the control lock); two close AI players do not trade the ball with repeated tackles (post-tackle shield + AI tackle cooldown); the covering defender steps in front of a carrier inside the last 20 m (and never for a loose ball or own possession); off-ball jobs are not assigned beyond their travel cap. |
| `tests/FootballSim.Tests/MatchSetupTests.cs`, `TrainingDrillsTests.cs`, `TrainingSessionTests.cs` | Menu/match setup, drill registry, spawn/reset/rally rules, and the training human-input path. |

Run everything with `dotnet test tests/FootballSim.Tests/FootballSim.Tests.csproj` (about 11 s; the
multi-seed gate is the slowest test at ~10 s).

## Gates beyond the unit suite

- **Headless CLI** (`dotnet run --project src/FootballSim.Headless -- --match-5v5 | --match-11v11`):
  exits non-zero unless the match actually reaches `FullTime`; the tick cap is reported as
  `INCOMPLETE` (audit F23). It prints the phase/period, clock vs configured match length, score,
  possession, pass/shot metrics and the execution-aware kick ledger.
- **Tactical batch** (`--tactical-batch`): five complete 11v11 matches (4-minute halves) with
  per-seed metrics and an aggregate width gate (majority of seeds plus a mean separation); exits
  non-zero when the gate fails.
- **Audit probe** (`tools/FootballSim.AuditProbe`, `start.bat probe`): a separate diagnostic, not part
  of the test gate. It reports scenario observations and two 18,000-tick match samples, and its exit
  code does not mean gameplay is correct. Batches 20–21 added `ball_geometry`, `frame_timing`,
  `config_validation`, `inspector_index`, `recorded_replay`, `match_completion` and
  `multi_seed_tactical`. The playtest pass added `artifacts/audit-probe-2026-11-11-playtest.json`
  (baseline `-preplaytest`): compare its `multi_seed_tactical` and `match_health_multi_seed` numbers,
  not the scorelines.
- **Godot boot check:** `Godot --path src/FootballSim.Godot res://MainMatchScene.tscn --quit-after 180`
  (and the same for `FreeTrainingScene.tscn`) must exit 0 with no script errors. This catches scene
  wiring and rendering-path regressions; it is not an FPS or visual-quality test.

## Conventions

- Every fix ships with a regression that drives the **live** path; expected-output-only properties are
  not acceptable substitutes. Where a defect is reproducible, the new test was first confirmed to fail
  with the fix reverted.
- Never overwrite `artifacts/audit-probe-2026-11-06.json`; each batch writes
  `artifacts/audit-probe-<date>-batch<n>.json` and is diffed against the previous artifact.
- Match scores are chaotic. Judge health on passes, shots, possession and executed kicks, and prefer
  scenario probes or multi-seed aggregates over a single scoreline.
