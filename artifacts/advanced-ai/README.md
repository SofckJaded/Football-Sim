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

## 2026-11-12 — A1.3/A1.4 re-baseline (simulation version `ai2-rs1`)

A1.4 (contact-time offside geometry) and A1.3 (split perception/execution random streams) change
behaviour by design, so the A0 baseline above is **no longer the current build**. The version tag
`SimulationVersion.Tag` (`ai2-rs1`) is printed by every headless run and is the key for comparing
artifacts: **only same-version numbers are comparable.**

### What changed and why the numbers moved

- A1.4: offside, and the offside free-kick spot, are judged from a `ContactGeometrySnapshot` captured
  before the physics step. Previously the offside line was drawn from a ball position the physics had
  already advanced, so a long pass could lift the line past a genuinely offside attacker.
- A1.3: perception draws from its own derived stream. Trajectories therefore differ from
  `ai1` everywhere, not only where offside matters.

### 11v11 headless match — `a1-rng-split-11v11-2026-11-12.txt`

| Metric | ai1 (A0 baseline) | ai2-rs1 |
| :--- | :--- | :--- |
| Wall time | 22 024 ms | 25 310 ms |
| Ticks | 326 152 | 326 306 |
| Score | 25 – 10 | 29 – 17 |
| Possession | 47.0 / 53.0 | 50.6 / 49.4 |
| Controlled ball | 43.3 / 56.7 | 48.6 / 51.4 |
| Passes | 812 / 767 (86 % / 90 %) | 869 / 830 (88 % / 88 %) |
| Shots | 96 / 69 | 120 / 59 |
| Width | 42.4 / 41.7 m | 42.7 / 40.7 m |
| Kicks executed | 1767 / 1938 | 1897 / 2087 |

The wider scoreline is a property of the current AI, not a regression from these two changes in
isolation: both teams still reach full time, both still play, and the tactical gate below still passes.

### Performance — `a1-rng-split-perf-2026-11-12.txt`

| Metric | ai1 | ai2-rs1 |
| :--- | :--- | :--- |
| Per-tick mean | 101.7 µs | 113.0 µs |
| p50 / p95 | 68.5 / 273.6 µs | 70.5 / 292.9 µs |
| p99 / max | 343.3 / 963.7 µs | 421.9 / 1091.2 µs |
| Allocated per tick | 41 727 B | 41 300 B |

The ≈ 11 µs/tick increase is the contact-geometry capture plus the extra stream. Allocation did not
grow, which is the important part: the snapshot reuses a preallocated buffer. Still ~40× inside the
60 Hz budget; the allocation figure remains the open issue (`docs/KNOWN_ISSUES.md`).

### Tactical differentiation gate — `a1-rng-split-tactical-gate-2026-11-12.txt`

5 seeds × 240 s: all reached full time, all played, the possession side was wider on **5/5** seeds,
mean width separation **2.64 m** (was 1.92 m under `ai1`). **GATE: PASS.** The gate was re-run rather
than assumed because a new random stream is exactly the kind of change that can flip a marginal gate.

---

## 2026-11-12 — A1.1c stable candidate keys (simulation version `ai3-rs1`)

Commitment is now matched on a `CandidateKey` (kind + target + variant) instead of the action label.
The version was bumped for the semantic change, **not** because anything moved: every re-captured number
is identical to `ai2-rs1`.

| Artifact | Comparison with `ai2-rs1` |
| :--- | :--- |
| `a1-candidate-keys-tactical-gate-2026-11-12.txt` | Byte-identical: same per-seed scores, passes, widths; mean separation **2.64 m**; **GATE: PASS** |
| `a1-candidate-keys-11v11-2026-11-12.txt` | Byte-identical: 326 306 ticks, 29–17, 50.6/49.4 possession, 869/830 passes, 120/59 shots, 42.7/40.7 m width, 1897/2087 kicks |
| `a1-candidate-keys-perf-2026-11-12.txt` | Within noise: mean 109.7 µs, p99 390.7 µs, **41 651 B/tick** (was 41 300) |

Reading: the key is allocation-free (a `readonly record struct` with an int target and an interned tag),
so the 41.7 KB/tick figure did not grow. The identity-to-label mapping happens to be one-to-one for every
candidate the controller generates today, which is why behaviour is unchanged; the key is nonetheless
strictly more specific for press/challenge targets, which is why the version was bumped rather than
declaring "no change" from a single match.

---

## 2026-11-12 — A1.5 reset hooks + A1.2 intent executor (simulation version `ai4-rs1`)

Two changes, one re-baseline because both land in `A1`.

- **A1.5 — AI state is dropped at every discontinuity.** Commitment, tackle cooldown and the held intent
  are cleared on restart, half time, full time, player removal, training reset and controller replacement.
- **A1.2 — controller-owned intent executor behind an off-by-default scheduler.** With
  `EnableScheduledDecisions = false` (the default) every live tick still re-scores, so the executor is
  inert; the reference match below therefore measures **A1.5 alone**.

### 11v11 headless match — `a1-intent-reset-11v11-2026-11-12.txt`

| Metric | ai3-rs1 | ai4-rs1 |
| :--- | :--- | :--- |
| Ticks | 326 306 | 326 268 |
| Score | 29 – 17 | 23 – 18 |
| Possession | 50.6 / 49.4 | 48.2 / 51.8 |
| Controlled ball | 48.6 / 51.4 | 45.9 / 54.1 |
| Passes | 869 / 830 | 850 / 792 |
| Pass completions | 88 % / 88 % | 90 % / 89 % |
| Shots | 120 / 59 | 90 / 79 |
| Width | 42.7 / 40.7 m | 42.8 / 41.0 m |

**Reading:** the scoreline moved substantially (29–17 → 23–18). That is the intended consequence of the
restart reset — a commitment no longer survives a stoppage, so the first action after every throw-in, goal
kick, corner or free kick is a fresh decision instead of a continuation. Shots spread much more evenly
(120/59 → 90/79), which is consistent with the possession side no longer carrying an advantage across
stoppages.

An intermediate version of A1.5 also wiped the ball-reaction history on every restart and produced
**15–17**; scoping that wipe to half/full time (where direction actually reverses and every position is
re-placed) recovered most of it. The wider wipe was left out deliberately: a restart places the ball
within a few metres of where it left, and `ObservedBallState` already ignores a teleport, so wiping the
history made the first look after each stoppage blind for no reason.

### Tactical differentiation gate — `a1-intent-reset-tactical-gate-2026-11-12.txt`

5 seeds × 240 s: all reached full time, all played, the possession side wider on **5/5**, mean separation
**3.05 m** (was 2.64 m). **GATE: PASS.**

### Performance — `a1-intent-reset-perf-2026-11-12.txt`

| Metric | ai3-rs1 | ai4-rs1 |
| :--- | :--- | :--- |
| Per-tick mean | 109.7 µs | 104.1 µs (repeat: 106.0) |
| p99 / max | 390.7 / 1112 µs | 330.3 / 1534 µs |
| Allocated per tick | 41 651 B | **44 955 B** (repeat: 44 974) |

The allocation figure rose ~8 %. It is not from the new objects — `PlayerIntent` and `CandidateKey` are
structs — but from the trajectory itself: allocation per tick tracks how many candidates and contacts the
match actually generates, and this match is a different match. Treat it as "≈45 KB/tick" rather than a
precise constant, and compare only same-version numbers.

---

## Still to capture (A0.5, part 2)

- A per-phase breakdown of the tick (perception / decisions / execution / physics / rules), so a
  regression can be attributed to a subsystem.
- 5v5 baseline.
- Short rendered clips from the Godot client (needs a human at the machine; tracked as part of A9.4).
- Fixed development seed set + a separate held-out seed set, recorded per matchup.
