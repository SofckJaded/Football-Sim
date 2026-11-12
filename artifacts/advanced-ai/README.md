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

## 2026-11-12 — Release performance sample (A0.5, part 1)

`dotnet run --project src\FootballSim.Headless -c Release -- --perf 20000`
→ `a0-perf-2026-11-12.txt`

11v11, 22 active players, diagnostics off, 2 000 warm-up ticks then 20 000 measured.

| Metric | Value |
| :--- | :--- |
| Per-tick mean | 101.7 µs |
| p50 / p95 | 68.5 / 273.6 µs |
| p99 / max | 343.3 / 963.7 µs |
| Fixed-step budget | 16 666.7 µs (60 Hz) |
| Total | 2 034.2 ms for 20 000 ticks |
| **Allocated per tick** | **41 727 bytes** |
| GC collections (gen0/1/2) | 50 / 0 / 0 |

Reading: the tick loop has roughly **50×** headroom against the 60 Hz budget, which is what makes an
expanded utility set affordable. The allocation figure is the opposite story — 2.5 MB/s of garbage — and
is the first thing A9.3 has to fix before A2–A6 start caching per-player spatial data. See
`docs/KNOWN_ISSUES.md`.

## Still to capture (A0.5, part 2)

- A per-phase breakdown of the tick (perception / decisions / execution / physics / rules), so a
  regression can be attributed to a subsystem.
- 5v5 baseline.
- Short rendered clips from the Godot client (needs a human at the machine; tracked as part of A9.4).
- Fixed development seed set + a separate held-out seed set, recorded per matchup.
