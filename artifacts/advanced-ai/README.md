# artifacts/advanced-ai — baseline and benchmark evidence

Everything here is **recorded evidence**, not generated documentation. A file is added when a gate is
run; nothing is edited to look better later.

## Reference machine (A0.5)

| Field | Value |
| :--- | :--- |
| CPU | AMD Ryzen 7 7700 (8C / 16T, 3.8 GHz base) |
| RAM | 31.4 GB |
| OS | Windows 11 Pro 10.0.26200 |
| .NET SDK | 8.0.426 (`net8.0`, Release) |
| Godot | 4.7.2-stable mono |
| Recorded | 2026-11-12 |

## 2026-11-12 — pre-AI baseline (A0.1)

Taken before any advanced-AI change, on the unmodified simulation. These are the numbers later
behavioral changes are compared against; they are **not** targets.

### Test suite

`dotnet test tests\FootballSim.Tests` — **191 passed, 0 failed**, 13 s (Debug). This count is the
pre-A0.2 baseline; the suite grew to 203 with the scenario-setup tests.

### 11v11 headless match

`dotnet run --project src\FootballSim.Headless -c Release -- --match-11v11`
→ `a0-baseline-11v11-2026-11-12.txt`

| Metric | Value |
| :--- | :--- |
| Wall time | 22 024 ms for 326 152 ticks (≈ 67 µs/tick, Release, single-threaded) |
| Status | FULL TIME, clock 5400.0 s |
| Score | Home (Possession / Positional Play) 25 – 10 Away (Direct Counterattack) |
| Possession share | Home 47.0 % / Away 53.0 % |
| Controlled ball | Home 43.3 % / Away 56.7 % |
| Passes attempted | Home 812 / Away 767 |
| Passes completed | Home 700 (86 %) / Away 692 (90 %) |
| Shots | Home 96 / Away 69 |
| Mean team width | Home 42.4 m / Away 41.7 m |
| Kicks executed | 1767 executed / 1938 requested |
| Refusals | OutOfReach 102, BallTooHigh 52, LostArbitration 16, FacingAway 1 |
| Rule events | 68 |

The high score is a real property of the current simulation, not a measurement error: 25–10 over
90 minutes with 96 + 69 shots. It is recorded here so that any future claim of "better defending"
has a starting point.

### Tactical differentiation gate

`dotnet run --project src\FootballSim.Headless -c Release -- --tactical-batch`
→ `a0-baseline-tactical-gate-2026-11-12.txt`

Five seeds × 240 s, Possession Control vs Direct Counterattack: all reached full time, all played,
the possession side was wider on **5/5** seeds, mean width separation **1.92 m**. **GATE: PASS.**

Per-seed ball-territory and pass numbers vary widely (31–46 passes, 44–54 % possession), which is the
variance the plan's "≥ 8 paired seeds" rule exists for.

## Still to capture (A0.5 proper)

- Allocation measurements and AI-only tick cost breakdown (p50 / p95 / p99).
- 5v5 baseline.
- Short rendered clips from the Godot client.
- Fixed development seed set + a separate held-out seed set, recorded per matchup.
