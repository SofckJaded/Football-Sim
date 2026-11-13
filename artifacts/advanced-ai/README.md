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

## 2026-11-12 — A2.1 one reach model (simulation version `ai5-rs1`)

`AI/TimeToReachModel.cs` replaces four incompatible time-to-reach estimates with one that mirrors
`StepPlayerLocomotion` (fatigue-adjusted speed cap, averaged turn cost, braking/accelerating
acceleration scaled by recovery balance, arrival defined by contact reach). The responder election now
scores each candidate against *its own* meeting with the ball instead of one shared intercept point.

### 11v11 headless match — `a2-reach-11v11-2026-11-12.txt`

| Metric | ai4-rs1 | ai5-rs1 |
| :--- | :--- | :--- |
| Ticks | 326 268 | 326 368 |
| Score | 23 – 18 | **9 – 6** |
| Possession | 48.2 / 51.8 | 49.4 / 50.6 |
| Passes | 850 / 792 | 870 / 880 |
| Pass completions | 90 % / 89 % | **76 % / 79 %** |
| Shots | 90 / 79 | **57 / 36** |

**Reading:** the defending side now *solves* the interception (where and when it can meet the ball)
instead of approximating it with a closest-approach point, so more passes are cut out and far fewer
shots are conceded. That is the intended direction for an advanced-AI milestone, and it moves the match
towards realistic numbers (15 goals is still high; 76–79 % pass completion is normal football). The
magnitude is *not* separately attributed to the interception target versus the receiving side's run
target — `PassSuccessModel` (A4.2) owns that measurement. Recorded in `docs/KNOWN_ISSUES.md`.

### Tactical differentiation gate — `a2-reach-tactical-gate-2026-11-12.txt`

5 seeds × 240 s: all reached full time, all played, possession side wider on **5/5**, mean separation
**1.54 m** (was 3.05 m). **GATE: PASS**, but the separation is the weakest it has been since A0. With
better defenders, 240 s fragments are more often decided by duels than by shape; A3 (structure) and A9
(style tuning) are where that number has to be rebuilt.

### Performance — `a2-reach-perf-2026-11-12.txt`

| Metric | ai4-rs1 | ai5-rs1 |
| :--- | :--- | :--- |
| Per-tick mean | 104.1 µs | 112.9 µs |
| p99 / max | 330.3 / 1534 µs | 357.4 / 1485 µs |
| Allocated per tick | 44 955 B | 45 050 B |

≈ 9 µs/tick for the reach model, no allocation growth: the model is closed-form (a 48-sample scan plus
a bisection on the squared distance) and allocation-free.

---

## 2026-11-12 — A2.2 sight filtering and observation memory (simulation version `ai6-rs1`)

Perception stops being omniscient. An opponent outside the facing cone (and not inside 2.5 m) is no
longer measured every tick: the player publishes a **prediction** from the last sighting, with age,
decaying confidence, damped velocity extrapolation and growing positional uncertainty. Teammates are
still observed every tick, because team shape is coordinated by the job system and teammate belief is
A7.2's delayed team calls.

### 11v11 headless match — `a2-memory-11v11-2026-11-12.txt`

| Metric | ai5-rs1 | ai6-rs1 |
| :--- | :--- | :--- |
| Ticks | 326 368 | 326 258 |
| Score | 9 – 6 | 8 – 6 |
| Possession | 49.4 / 50.6 | 48.8 / 51.2 |
| Passes | 870 / 880 (76 % / 79 %) | 840 / 854 (77 % / 76 %) |
| Shots | 57 / 36 | 79 / 51 |
| Width | 42.8 / 41.0 m | 42.5 / 40.9 m |

**Reading:** the change is deliberately small at match level — every team still plays, and shot volume
moved back towards the ai4 level. What it buys is the invariant, not a scoreline: an unseen opponent's
live motion can no longer reach an estimate, a candidate score or a decision (proven in
`PlayerMemoryTests`, including two worlds that differ only in a hidden opponent's direction and produce
byte-identical pictures and candidate tables).

One keeper-level consequence worth recording: players now sometimes carry the ball against *stale*
knowledge of the press. In the 3v2 build-up fixture the carrier dribbles away unchallenged and the
fragment produces no requested contact in 4 s, where it used to produce two. Two trace/scheduler
regressions used that fixture as a "the run actually played" window and now use the keeper-sweep fixture,
which is guaranteed to produce keeper and striker contacts in the same window.

### Tactical differentiation gate — `a2-memory-tactical-gate-2026-11-12.txt`

5 seeds × 240 s: all reached full time, all played, possession side wider on **5/5**, mean separation
**1.91 m** (was 1.54 m). **GATE: PASS.**

### Performance — `a2-memory-perf-2026-11-12.txt`

| Metric | ai5-rs1 | ai6-rs1 |
| :--- | :--- | :--- |
| Per-tick mean | 112.9 µs | 106.5 µs |
| p99 / max | 357.4 / 1485 µs | 367.6 / 579 µs |
| Allocated per tick | 45 050 B | 49 232 B |

The mean went *down*: fewer opponents are measured per tick, so fewer noise draws and fewer list entries
are built. The allocation rose ~4 KB/tick, which is the same trajectory-dependence documented above (the
belief store itself allocates one small object per observer/observed pair for the whole match).

---

## 2026-11-12 — A2.3 estimated capability + A2.4 local pressure (simulation version `ai7-rs1`)

Two changes, one re-baseline because both are in A2 and both move trajectories.

- **A2.3 — scorers estimate an opponent's body from a scouted band, never a live attribute.**
  `PerceivedEntity.Capability` (`CapabilityEstimate`) is either a `ScoutingReport` entry (built once from
  the squad at kickoff, quantised to 0.25 m/s and 0.8 confidence) or `CapabilityEstimate.Generic`.
  `ReachKinematics.FromEntity` is the only way in; there is deliberately no overload that takes a
  `FootballerState`. The keeper's sweep race and the local responder estimate now use it.
- **A2.4 — the carrier reads a local pressure window.** `LocalPressureField` is 5×5 cells of 3 m around
  the carrier, at most five nearby opponents, a 1.5 s horizon, built once per decision and cached by
  (observer, tick, centre). Every cell publishes both arrival times, the margin, a directional
  pressure, a coarse control share and explicit boundary/unclaimed flags. The carrier's carry-direction
  score blends it 60/40 with the old distance-to-nearest-opponent term — blended, not multiplied, so the
  same crowding is not charged twice.
- **Carry-direction hysteresis was added with it.** Without it the pressure term flipped the carrier's
  direction left and right every tick and the carrier ran on the spot (0.1 m of travel in two seconds in
  a 1v1 face-off). The remembered direction now has to be beaten by 0.04 to be abandoned.

### 11v11 headless match — `a2-pressure-11v11-2026-11-12.txt`

| Metric | ai6-rs1 | ai7-rs1 |
| :--- | :--- | :--- |
| Ticks | 326 258 | 326 180 |
| Score | 8 – 6 | 7 – 6 |
| Possession | 48.8 / 51.2 | 50.6 / 49.4 |
| Passes | 840 / 854 (77 % / 76 %) | 906 / 818 (76 % / 74 %) |
| Shots | 79 / 51 | 72 / 45 |
| Width | 42.5 / 40.9 m | 43.3 / 40.9 m |

**Reading:** a small, sane move — the match still plays, both teams still get on the ball, and the shot
counts stay in the same band. The mid-batch build (before the hysteresis) scored 14–9; the hysteresis
removed most of the extra shot volume it created, which is consistent with the carrier no longer
oscillating but committing to a direction.

### One regression had to be re-stated

`GameplayFeelTests.TwoClosePlayers_DoNotTradeTheBallWithRepeatedTackles` asserted 1–3 tackles in a 1v1
face-off. With pressure-aware carrying, a defender standing 0.8 m in front is now something the carrier
turns away from instead of dribbling into: the face-off resolves as an evasion with **no** dispossession
(measured, and the carrier travels 6 m doing it). The upper bound, the carried ball and the movement are
the invariants that test exists for; the lower bound was pinning the old behaviour, so it is now 0–3 and
the reason is recorded in the test itself.

### Tactical differentiation gate — `a2-pressure-tactical-gate-2026-11-12.txt`

5 seeds × 240 s: all reached full time, all played, the possession side wider on **4/5** seeds, mean
separation **1.57 m**. **GATE: PASS** (the gate needs a majority and > 0.5 m mean).

### Performance — `a2-pressure-perf-2026-11-12.txt`

| Metric | ai6-rs1 | ai7-rs1 |
| :--- | :--- | :--- |
| Per-tick mean | 106.5 µs | 117.6 µs |
| p99 / max | 367.6 / 579 µs | 346.4 / 1407 µs |
| Allocated per tick | 49 232 B | **60 850 B** |

The pressure window itself allocates nothing per tick (25 cells in a preallocated array, five opponents
in a preallocated buffer, one `CandidateGenerator` per player). The ~11 KB/tick rise is the trajectory:
this is a different match with more possession changes and more passes attempted. It is nonetheless the
new high-water mark for A9.3 — allocation is now ≈61 KB/tick.

---

## 2026-11-12 — A2.5 normalised influence + A2.6 team belief and maps (simulation version `ai8-rs1`)

- **A2.5** split the cell's single "control" number into `InfluenceShare` (observer's side),
  `OpponentInfluenceShare` and `UnclaimedShare`, which always sum to 1, with both absolute arrival times
  kept alongside. Weights are reachability (`1 - arrival/horizon`), so two players who each need 1.2 s of
  a 1.5 s horizon split a third of the cell and leave the rest unclaimed — which a 50/50 share could not
  say.
- **A2.6** gave a team a picture of its opponents: `TeamBelief` is the union of the team's own
  observations (youngest sighting per opponent), and the persistent `TeamJobCoordinator` now reads it
  instead of the authoritative squad — markers, offside line and space are all belief-driven. A
  `FromTruth` variant of the same type exists for validation and the overlay, so the two are one shape and
  a test can prove the difference. `SpatialOverlayMap` renders the same shares over the whole pitch for
  diagnostics; `MatchClientNode` shows a labelled belief/pressure readout.

### 11v11 headless match — `a2-team-belief-11v11-2026-11-12.txt`

| Metric | ai7-rs1 | ai8-rs1 |
| :--- | :--- | :--- |
| Ticks | 326 180 | 326 086 |
| Score | 7 – 6 | 6 – 6 |
| Possession | 50.6 / 49.4 | 50.6 / 49.4 |
| Passes | 906 / 818 (76 % / 74 %) | 943 / 878 (75 % / 76 %) |
| Shots | 72 / 45 | 57 / 49 |
| Width | 43.3 / 40.9 m | 43.0 / 40.9 m |

An intermediate build of this batch scored **3–10**: the belief view carried the opponent goalkeeper, and
the marker assignment — which used to filter keepers explicitly — started sending a centre-back to mark
him. `TeamBelief.Opponent` now carries the role and the assignment filters keepers again. That is a
reminder that a "pure refactor" of who supplies positions is not refactoring at all.

### Tactical differentiation gate — `a2-team-belief-tactical-gate-2026-11-12.txt`

5 seeds × 240 s: all reached full time, all played, the possession side wider on **5/5**, mean separation
**2.23 m** — the best since A1. **GATE: PASS.**

### Performance — `a2-team-belief-perf-2026-11-12.txt`

| Metric | ai7-rs1 | ai8-rs1 |
| :--- | :--- | :--- |
| Per-tick mean | 117.6 µs | 138.1 µs |
| p99 / max | 346.4 / 1407 µs | 499.9 / 1712 µs |
| Allocated per tick | 60 850 B | 61 169 B |

≈ 20 µs/tick more, with allocation flat: the match generates more candidates and contacts (1 821 passes
attempted versus 1 724), and the belief rebuild adds a bounded amount of CPU. Still ~120× inside the 60 Hz
step. A9.3 owns the profile.

---

## 2026-11-12 — A3.1 team context + A3.2 context-sensitive hysteresis (simulation version `ai9-rs1`)

- **A3.1** splits each team's single tactical state into possession stability, claim/phase, zone,
  transition window and armed restart (`AI/TeamContext.cs`), resolved per live tick by
  `TeamTacticalCoordinator` and published to every player's observation. A deflection now leaves the
  claim with the team that played the ball while its stability drops to loose, and an armed restart
  claims the ball for the team that will take it.
- **A3.2** removes the single 3.5 s state lock. The phase and the transition now change on the turnover
  tick; the zone alone keeps a 3 m deadband plus a 3.5 s structural dwell; a turnover or an emergency
  defence re-resolves both zones immediately.

### 11v11 headless match — `a3-team-context-11v11-2026-11-12.txt`

| Metric | ai8-rs1 | ai9-rs1 |
| :--- | :--- | :--- |
| Wall time | 43 728 ms | 33 456 ms |
| Ticks | 326 086 | 326 430 |
| Score | 6 – 6 | **9 – 7** |
| Possession | 50.6 / 49.4 | 50.4 / 49.6 |
| Controlled ball | 50.1 / 49.9 | 49.0 / 51.0 |
| Passes | 943 / 878 (75 % / 76 %) | 859 / 832 (74 % / 76 %) |
| Shots | 57 / 49 | 72 / 60 |
| Width | 43.0 / 40.9 m | 43.6 / 40.6 m |
| Kicks executed | 1955 / 2102 | 1849 / 2115 |

**Reading:** the match still reaches full time and both teams still play. The scoreline moved from
6–6 to 9–7, which is what the two intended mechanisms predict at this stage: a counter-attack is no
longer held off by a structural timer (so a direct side can go forward immediately after winning the
ball), and a restart no longer starts with the wrong team holding the ball claim (so shape is right
for the first ticks of every throw-in, goal kick, corner and free kick). The magnitude is a match-level
observation, not a per-mechanism measurement; A3.3–A3.6 rebuild structure and A9.2's ablations are the
place to attribute it.

### Tactical differentiation gate — `a3-team-context-tactical-gate-2026-11-12.txt`

5 seeds × 240 s: all reached full time, all played, the possession side wider on **4/5** seeds, mean
separation **1.93 m** (was 2.23 m). **GATE: PASS** (the gate needs a majority and > 0.5 m mean). The
separation remains the weakest area of the gate, as it has been since A2.1; A3.3+ and A9.1 own it.

### Performance — `a3-team-context-perf-2026-11-12.txt`

| Metric | ai8-rs1 | ai9-rs1 |
| :--- | :--- | :--- |
| Per-tick mean | 138.1 µs | **124.1 µs** |
| p50 / p95 | — | 90.8 / 347.2 µs |
| p99 / max | 499.9 / 1712 µs | 469.5 / 1345.9 µs |
| Allocated per tick | 61 169 B | 61 268 B |

The context is six small enums, a nullable team id and a bool in one `readonly record struct`; it is
copied into each observation by value and `ObserveRestart` returns immediately when the armed restart
is unchanged. Allocation is flat (+99 B) and the mean went down, which is trajectory, not a claim that
context resolution is free.

---

## 2026-11-12 — A3.3 formation ownership and temporary responsibility (simulation version `ai10-rs1`)

- **Ownership replaced the shirt table.** `TeamFormation` assigns one `NominalSlot` per player id, once:
  the pre-match template for a generated squad, the fixture's own layout for a scenario. The anchor
  writer reads the slot, so a role can no longer be derived from a number, and an unbalanced fixture
  holds a shape of its own size.
- **A vacated line is protected.** A line player pulled out of his line by the press leaves the slot he
  owns; the nearest eligible line player takes it as a temporary responsibility (`ProtectGap`) and hands
  it back when the line is whole again (or the press ends, the ball leaves, the hold expires, the passage
  turns over, or either player leaves the pitch).
- **The `cb-step-out` fixture was tightened** so the centre-back really is the elected presser (the
  defensive midfielder was moved to the far side of the ball). The protection appears at tick ≈151,
  targets #102's slot and is handed back at ≈343.

### 11v11 headless match — `a3-formation-ownership-11v11-2026-11-12.txt`

| Metric | ai9-rs1 | ai10-rs1 |
| :--- | :--- | :--- |
| Wall time | 33 456 ms | 31 428 ms |
| Ticks | 326 430 | 326 326 |
| Score | 9 – 7 | **8 – 4** |
| Possession | 50.4 / 49.6 | 49.0 / 51.0 |
| Controlled ball | 49.0 / 51.0 | 47.6 / 52.4 |
| Passes | 859 / 832 (74 % / 76 %) | 839 / 867 (73 % / 75 %) |
| Shots | 72 / 60 | 73 / 53 |
| Width | 43.6 / 40.6 m | 43.1 / 40.8 m |
| Kicks executed | 1849 / 2115 | 1862 / 2092 |

**Reading:** the conceded total fell from 7 to 4 and the away side took 7 fewer shots, which is the
direction the gap protection is supposed to move a match in. The magnitude is a match-level
observation; A3.4–A3.6 rebuild the rest of the structure and A9.2's ablations are where this gets
attributed to a mechanism.

### Tactical differentiation gate — `a3-formation-ownership-tactical-gate-2026-11-12.txt`

5 seeds × 240 s: all reached full time, all played, the possession side wider on **5/5** seeds, mean
separation **3.34 m** — the largest since A1 and the first 5/5 since `ai8`. **GATE: PASS.**

### Performance — `a3-formation-ownership-perf-2026-11-12.txt`

| Metric | ai9-rs1 | ai10-rs1 |
| :--- | :--- | :--- |
| Per-tick mean | 124.1 µs | 118.9 µs |
| p50 / p95 | 90.8 / 347.2 µs | 92.5 / 308.4 µs |
| p99 / max | 469.5 / 1345.9 µs | 428.9 / 833.0 µs |
| Allocated per tick | 61 268 B | 62 262 B |

The mechanism allocates nothing per tick: slots are a dictionary of structs written at setup, the
responsibility is a struct, and both the vacancy search and the mismatch review reuse the lists the
assignment pass already made. The ≈1 KB/tick rise is the trajectory, as before.

### One gate changed sign on the way, and was traced rather than re-stated

The first version measured "out of his line" from the *slot*, which in a real match sits behind the
defending shape (the shape carries the line height and ball terms). That made every defending centre-back
a vacancy, the possession side's protection duties pulled it narrower, and
`Phase6_PossessionTeam_ExhibitsWiderShape_ThanDirectCounterTeam` failed at 90 s (40.2 m vs 41.6 m).
Measuring the line from the shape anchor and protecting the slot removed the false vacancies; the same
90-second test now passes and the multi-seed gate is 5/5 at 3.34 m.

---

## 2026-11-12 — A3.4 job targets as scored constraint regions (simulation version `ai11-rs1`)

- A duty's target is now a **region** (`JobRegion`) scored against the ball, the teammates already placed,
  the opponents the team believes in and the space around it, with soft rewards for width, depth,
  staggering and complementary channels and no lane index. The chosen target is refined by a bounded
  two-ring probe.
- Two structural fixes preceded the numbers: kept incumbent regions now count as placed (before that,
  `wide-overload-4v3` handed two duties targets 0.4 m apart), and the probe can reach the next channel.
- The `cb-step-out` carrier is now a fixed controller that holds the ball, so the fixture asks its
  question deterministically; `cb-step-out` is A3.3's subject and its protection still appears and is
  handed back.

### 11v11 headless match — `a3-job-regions-11v11-2026-11-12.txt`

| Metric | ai10-rs1 | ai11-rs1 |
| :--- | :--- | :--- |
| Wall time | 31 428 ms | 30 800 ms |
| Ticks | 326 326 | 326 526 |
| Score | 8 – 4 | **5 – 3** |
| Possession | 49.0 / 51.0 | 50.4 / 49.6 |
| Controlled ball | 47.6 / 52.4 | 48.9 / 51.1 |
| Passes | 839 / 867 (73 % / 75 %) | 788 / 743 (72 % / 72 %) |
| Shots | 73 / 53 | **78 / 75** |
| Width | 43.1 / 40.8 m | 43.5 / 41.0 m |

**Reading:** the shot split is close to even for the first time (was 73/53), and the game is less
high-scoring (8 goals, was 12). Pass completion is ~2 pp lower, which is the honest cost of targets that
sit in space rather than on a teammate; A4.2's `PassSuccessModel` is where that gets measured properly
and `docs/KNOWN_ISSUES.md` records it as open.

### Tactical differentiation gate — `a3-job-regions-tactical-gate-2026-11-12.txt`

5 seeds × 240 s: all reached full time, all played, the possession side wider on **4/5** seeds, mean
separation **1.90 m**. **GATE: PASS** (majority and > 0.5 m). The separation is lower than `ai10`'s
3.34 m and the mechanism is recorded in `docs/KNOWN_ISSUES.md`: complementary channels widen *both*
teams, compressing a possession-vs-direct difference that used to come from stacking.

### Performance — `a3-job-regions-perf-2026-11-12.txt`

| Metric | ai10-rs1 | ai11-rs1 |
| :--- | :--- | :--- |
| Per-tick mean | 118.9 µs | 122.0 µs |
| p50 / p95 | 92.5 / 308.4 µs | 92.8 / 326.7 µs |
| p99 / max | 428.9 / 833.0 µs | 449.2 / 1594.6 µs |
| Allocated per tick | 62 262 B | 62 604 B |

≈3 µs/tick for the region scoring: the terms are structs and float math, the assigned-region list is
reused and cleared per team pass, and the probe only runs for the player who won the duty.

### Tuning history, recorded because it is the evidence

| Configuration | Gate (wider / mean) | Notes |
| :--- | :--- | :--- |
| space-dominant (0.45/0.25), one ring, incumbents invisible | 5/5, 2.36 m | passed, but two duties were handed the same point |
| space-dominant, two rings, incumbents visible | **3/5, 0.17 m** | FAIL — the space term pulls targets together |
| connection-dominant (0.35/0.35), two rings, incumbents visible, smooth stagger | 5/5, 1.45 m | passed; closest possession pair 4.6 m |
| connection-dominant, linear stagger 0.30, profile-scaled width/channel | **4/5, 1.90 m** | shipped: passes the gate and the structure invariant |

---

## 2026-11-12 — A3.5 coverage requests + A3.6 rest defence (simulation version `ai12-rs1`)

- **A3.5** turns the vacated-region mechanism into a request with a recorded bid table, a deterministic
  winner (best bid, ties by lower id), an accepted hold, and a stated close reason — handback, expiry,
  owner removal, the passage ending, or no bidder. One request can be live per team; that is the cascade
  bound.
- **A3.6** models rest defence (players behind the ball, worst recovery time through the reach model,
  unmarked central access, outlet-lane exposure, goal-side threats) and gates the possession allocator's
  box run and overlap on the allowance that picture grants (2 / 1 / 0). The other attacking duties are
  untouched.

### 11v11 headless match — `a3-coverage-restdefence-11v11-2026-11-12.txt`

| Metric | ai11-rs1 | ai12-rs1 |
| :--- | :--- | :--- |
| Wall time | 30 800 ms | 31 916 ms |
| Ticks | 326 526 | 326 594 |
| Score | 5 – 3 | **10 – 4** |
| Possession | 50.4 / 49.6 | 49.6 / 50.4 |
| Controlled ball | 48.9 / 51.1 | 48.6 / 51.4 |
| Passes | 788 / 743 (72 % / 72 %) | 785 / 804 (73 % / 75 %) |
| Shots | 78 / 75 | 69 / 64 |
| Width | 43.5 / 41.0 m | 43.2 / 41.1 m |

**Reading:** the shot split stays close (69/64) and pass completion recovered to 73 %/75 %. The goal
count went back up to 14, and that number has now swung 5 → 8 → 10 → 14 across `ai10`–`ai12` with
similar possession and shots: it is recorded, not explained, and `docs/KNOWN_ISSUES.md` says so. The
stable signals are the gate, the shot split and the pass completion.

### Tactical differentiation gate — `a3-coverage-restdefence-tactical-gate-2026-11-12.txt`

5 seeds × 240 s: all reached full time, all played, the possession side wider on **5/5** seeds, mean
separation **2.20 m** (was 1.90 m at `ai11`). **GATE: PASS.**

### Performance — `a3-coverage-restdefence-perf-2026-11-12.txt`

| Metric | ai11-rs1 | ai12-rs1 |
| :--- | :--- | :--- |
| Per-tick mean | 122.0 µs | 118.5 µs |
| p50 / p95 | 92.8 / 326.7 µs | 92.0 / 300.0 µs |
| p99 / max | 449.2 / 1594.6 µs | 394.8 / 1787.4 µs |
| Allocated per tick | 62 604 B | 61 934 B |

Both mechanisms are allocation-free per tick: the request is one reused object per team with a fixed
bid array, and the rest-defence picture is a struct evaluated once per in-possession pass.

### A3 is complete

The A3 exit gate is recorded in `TASKS.md` with the tests that prove each clause. The milestone's own
numbers: gate 5/5 at 2.20 m, width 43.2/41.1 m in the reference match, and the shape metrics checked in
5v5, 11v11, after the interval and with a player sent off (`TeamShapeMetricsTests`).

---

## 2026-11-12 — A4.1 carrier action set + A4.2 pass-success model (simulation version `ai13-rs1`)

Files: `a4-tactical-gate.txt`, `a4-match-11v11.txt`, `a4-perf.txt`. Release build, reference machine.

### 11v11 headless match — `a4-match-11v11.txt`

| Metric | ai12-rs1 | ai13-rs1 |
| :--- | :--- | :--- |
| Wall time | 31 916 ms | 35 719 ms |
| Ticks | 326 594 | 327 650 |
| Score | 10 – 4 | **2 – 4** |
| Possession | 49.6 / 50.4 | 48.9 / 51.1 |
| Controlled ball | 48.6 / 51.4 | 48.3 / 51.7 |
| Passes | 785 / 804 (73 % / 75 %) | 1253 / 1398 (**77 % / 79 %**) |
| Shots | 69 / 64 | 47 / 29 |
| Width (all-tick) | 43.2 / 41.1 m | 42.6 / 41.0 m |

**Reading:** the pass count went up by roughly 60 % and completion with it, which is what A4.2 is for —
the score now asks *how likely the ball is to arrive* instead of using a flat lane threshold, so passes
that would have been abandoned for a merely average lane (and passes that would have been played into a
blocked one) are separated. The goal count fell to 6 total and is, per `KNOWN_ISSUES.md`, still not a
mechanism signal on one seed. The shot split moved in the possession side's favour (47/29).

### Tactical differentiation gate — `a4-tactical-gate.txt`

5 seeds × 240 s: all reached full time, all played, the possession side wider on **5/5** seeds, mean
separation **3.84 m**. **GATE: PASS.**

This gate now compares the **possession-scoped** span (`HomeAttackingWidthMeters`: the lateral span of a
team's outfield players sampled only while that team has the ball), and the artifact prints the
unconditional span beside it. Reason: under the new carrier behaviour the unconditional span read
**2/5 seeds at a mean of 1.19 m**, and the per-seed sign tracked possession share (a team with >50 % of
the ball was wider) rather than the width profile. Carrying the ball inward — which is what a better
value model does — was being measured as "a narrower profile". On the measure the profile is actually
about, the effect is not marginal: 5/5 at 3.84 m, every seed between 2.7 m and 5.1 m.

### Performance — `a4-perf.txt`

| Metric | ai12-rs1 | ai13-rs1 |
| :--- | :--- | :--- |
| Per-tick mean | 118.5 µs | 127.4 µs |
| p50 / p95 | 92.0 / 300.0 µs | 91.5 / 313.4 µs |
| p99 / max | 394.8 / 1787.4 µs | 490.4 / 1057.7 µs |
| Allocated per tick | 61 934 B | 62 234 B |

The cost is the expected one: every planned pass runs the lane sample plus one reach query per opponent
and the success estimate. Allocation is flat (the pass-target buffer and the terms are structs), and the
60 Hz budget holds at p99 with two orders of magnitude to spare. A9.3 owns the general fix.

### What A4.1/A4.2 deliberately did not do

- No unified value scale for pass / carry / shot, and no removal of the final-third backward-pass ban —
  both are A4.4, and they are only allowed once a better chance provably beats a poor shot and a safe
  recycle beats a dangerous forced pass.
- No reception value (A4.3): the pass score's gain is still a progression/space proxy. A4.3 replaces it
  with the post-reception state, and must read `PassSuccessTerms.DefenderPressure01` rather than adding a
  second pressure estimate.
- No hysteresis change (A4.5): the commitment bonus still holds the current action.

---

## 2026-11-12 — A4.3 reception value + A4.4 one documented scale (simulation version `ai14-rs1`)

Files: `a4-reception-scale-tactical-gate.txt`, `a4-reception-scale-match-11v11.txt`,
`a4-reception-scale-perf.txt`. Release build, reference machine.

### 11v11 headless match — `a4-reception-scale-match-11v11.txt`

| Metric | ai13-rs1 | ai14-rs1 |
| :--- | :--- | :--- |
| Wall time | 35 719 ms | 56 540 ms |
| Ticks | 327 650 | 327 244 |
| Score | 2 – 4 | **9 – 8** |
| Possession | 48.9 / 51.1 | 51.1 / 48.9 |
| Controlled ball | 48.3 / 51.7 | 50.5 / 49.5 |
| Passes | 1253 / 1398 (77 % / 79 %) | **754 / 714** (71 % / 69 %) |
| Shots | 47 / 29 | **71 / 50** |
| Width (all-tick) | 42.6 / 41.0 m | 44.1 / 41.4 m |

**Reading:** the carrier now values what the reception produces instead of the act of passing, so the pass
count roughly halved and the shot count rose by half. Completion fell 6 pp because fewer, longer and more
forward balls are played. The goal count (17) is again outside anything defensible as a mechanic signal —
`KNOWN_ISSUES.md` has said so since `ai10`, and the shot count is the number to watch.

### Tactical differentiation gate — `a4-reception-scale-tactical-gate.txt`

5 seeds × 240 s: all reached full time, all played, the possession side wider on **5/5** seeds, mean
separation **1.44 m** (possession-scoped span; the unconditional span is printed beside it). **GATE: PASS.**

### Performance — `a4-reception-scale-perf.txt`

| Metric | ai13-rs1 | ai14-rs1 |
| :--- | :--- | :--- |
| Per-tick mean | 127.4 µs | 172.5 µs |
| p50 / p95 | 91.5 / 313.4 µs | 132.7 / 345.6 µs |
| p99 / max | 490.4 / 1057.7 µs | 538.8 / 1489.3 µs |
| Allocated per tick | 62 234 B | 62 948 B |

The cost is the reception value: it runs once per planned pass (~8 a decision) and once per carry, and each
run walks the visible players three times. Allocation is flat. The 60 Hz budget holds at p99 with ~31× to
spare, and A9.3 owns making the model cheaper (one pass over the visible list per evaluation).

### Why A4.4's shot rework is not in this version

The measured conversion curve (`conversion × goal value − (1 − conversion) × miss cost`) was implemented
and measured. With it the reference team stopped attacking — a 10-minute probe at 240 s halves produced
**286 passes and no shot**, and the full match produced 26 goals from 7 shots because the AI preferred
passing the ball into the box to shooting from it. Thirteen constant sets were tried (goal value 4/5,
reception maxima 1.35–2.65, loss base 0.45–0.80, forward preference 0.35–0.60) and none produced both
penetration and shots. A4.4 says to remove the guaranteed close-range bonus *only once a better chance can
provably win*; that condition failed, so the tested curve stays. `TASKS.md` records the clause as open and
the missing prerequisite is A5.3's pass power/flight work.

---

## 2026-11-12 — A4.5 hysteresis + A4.6 explanations (simulation version `ai15-rs1`)

Files: `a4-hysteresis-explanations-tactical-gate.txt`, `a4-hysteresis-explanations-match-11v11.txt`,
`a4-hysteresis-explanations-perf.txt`. Release build, reference machine.

### 11v11 headless match — `a4-hysteresis-explanations-match-11v11.txt`

| Metric | ai14-rs1 | ai15-rs1 |
| :--- | :--- | :--- |
| Wall time | 56 540 ms | 50 313 ms |
| Ticks | 327 244 | 326 540 |
| Score | 9 – 8 | **4 – 5** |
| Possession | 51.1 / 48.9 | 49.8 / 50.2 |
| Passes | 754 / 714 (71 % / 69 %) | **637 / 565** (70 % / 65 %) |
| Shots | 71 / 50 | 52 / 65 |
| Width (all-tick) | 44.1 / 41.4 m | 43.9 / 41.7 m |

**Reading:** the switch rule costs some ball movement — a carrier now holds what he is doing until the
alternative clears the margin — which is visible as ~15 % fewer passes and a slightly flatter shot split.
The first margin tried (0.05/0.10) cost a third of the passes and was rejected on that measurement; the
shipped default (0.02/0.04) sits inside the spread between competing actions.

### Tactical differentiation gate — `a4-hysteresis-explanations-tactical-gate.txt`

5 seeds × 240 s: all reached full time, all played, the possession side wider on **5/5** seeds, mean
separation **2.76 m**. **GATE: PASS.**

### Performance — `a4-hysteresis-explanations-perf.txt`

| Metric | ai14-rs1 | ai15-rs1 |
| :--- | :--- | :--- |
| Per-tick mean | 172.5 µs | 172.0 µs |
| p50 / p95 | 132.7 / 345.6 µs | 135.3 / 389.3 µs |
| p99 / max | 538.8 / 1489.3 µs | 545.4 / 1790.6 µs |
| Allocated per tick | 62 948 B | 63 555 B |

Flat: the explanations are four floats in an existing struct and a bounded array of interned strings, and
the trace copies rejections only while tracing is on. A9.3 still owns the general allocation work.

### A4 exit gate

Recorded in `TASKS.md`: **three of four clauses pass**, each with the test that proves it. The missing
clause is A4.4's "a better chance can provably beat a poor shot", which is blocked on A5.3 (see above)
rather than on A4 itself. The gate's evidence lives in `CarrierDecisionGateTests` (live decisions and a
live fixture), `ReceptionValueModelTests` and `PassSuccessModelTests` (the model-level comparisons).

---

## 2026-11-12 — A4.4 closed: the shot on the documented scale (simulation version `ai16-rs1`)

Files: `a4-shot-scale-tactical-gate.txt`, `a4-shot-scale-match-11v11.txt`, `a4-shot-scale-perf.txt`.

### 11v11 headless match — `a4-shot-scale-match-11v11.txt`

| Metric | ai15-rs1 | ai16-rs1 |
| :--- | :--- | :--- |
| Wall time | 50 313 ms | 47 226 ms |
| Ticks | 326 540 | 326 168 |
| Score | 4 – 5 | **1 – 2** |
| Possession | 49.8 / 50.2 | 50.5 / 49.5 |
| Passes | 637 / 565 (70 % / 65 %) | **607 / 597** (72 % / 70 %) |
| Shots | 52 / 65 | **29 / 22** |
| Width (all-tick) | 43.9 / 41.7 m | 43.9 / 40.7 m |

**Reading:** this is the most plausible match of the milestone. 51 shots in ninety minutes and 3 goals is a
normal game, and completion recovered above 70 % on both sides because the shot no longer forces the carrier
to choose between a flat bonus and everything else.

### Tactical differentiation gate — `a4-shot-scale-tactical-gate.txt`

5 seeds × 240 s: all reached full time, all played, the possession side wider on **5/5** seeds, mean
separation **3.08 m**. **GATE: PASS.**

### Performance — `a4-shot-scale-perf.txt`

| Metric | ai15-rs1 | ai16-rs1 |
| :--- | :--- | :--- |
| Per-tick mean | 172.0 µs | 166.3 µs |
| p50 / p95 | 135.3 / 389.3 µs | 139.3 / 317.5 µs |
| p99 / max | 545.4 / 1790.6 µs | 798.7 / 1154.6 µs |
| Allocated per tick | 63 555 B | 63 622 B |

### Why the earlier attempts failed, in one paragraph

The shot's conversion curve was tried twice before and both times the side stopped shooting: the reception
value was £2.15-scale at its maximum then (a ball into the box outbid a shot from it), and the goal value was
too low for a covered shot to be worth taking at all. The fix was not the shot curve — it was the reception
maximum (1.45 since `ai14`) plus `GoalValue = 7`. That is the honest history, and it is why the A4.4 clause
was left open rather than forced.

---

## 2026-11-12 — the A4 pass audit (simulation version `ai17-rs1`)

Files: `a4-pass-audit-tactical-gate.txt`, `a4-pass-audit-11v11.txt`, `a4-pass-audit-perf.txt`.

Four defects were fixed after a playtest report that passes "miss" (details and the measurement in
`PROGRESS.md`, batch 20). The measurement is the part worth keeping: a twenty-minute segment counted every
pass's outcome and compared it with what `PassSuccessModel` predicted.

### What the pass model predicted against what happened (`ai16`, 289 passes)

| Predicted `p` band | Passes | Reached the intended man | Intercepted |
| :--- | ---: | ---: | ---: |
| 0.85 – 1.00 | 237 | 70 % | 20 % |
| 0.70 – 0.85 | 21 | 62 % | 38 % |
| below 0.40 (no trace — keeper/set-piece) | 30 | 47 % | 47 % |
| **all** | **289** | **66 %** | **24 %** |

Mean predicted completion **0.82**; 74 % of balls came within 1.5 m of the intended man at some point and
14 % never came within 6 m. By type: ground 71 %, through 66 %, raised **0/13**. The model is optimistic by
about fifteen points but ordered correctly; the raised alternative was the one category with no successes
at all.

### 11v11 headless match — `a4-pass-audit-11v11.txt`

| Metric | ai16-rs1 | ai17-rs1 |
| :--- | :--- | :--- |
| Wall time | 47 226 ms | 46 354 ms |
| Ticks | 326 168 | 326 298 |
| Score | 1 – 2 | **1 – 1** |
| Possession | 50.5 / 49.5 | 50.0 / 50.0 |
| Passes | 607 / 597 (72 % / 70 %) | **647 / 584** (70 % / 67 %) |
| Shots | 29 / 22 | **30 / 28** |
| Width (all-tick) | 43.9 / 40.7 m | 43.7 / 41.6 m |

### Tactical differentiation gate — `a4-pass-audit-tactical-gate.txt`

5 seeds × 240 s: full time, played, the possession side wider on **5/5** seeds, mean separation **5.01 m**.
**GATE: PASS.**

### Performance — `a4-pass-audit-perf.txt`

| Metric | ai16-rs1 (artifact) | ai16-rs1 (re-measured today) | ai17-rs1 |
| :--- | ---: | ---: | ---: |
| Per-tick mean | 166.3 µs | 233.7 µs | 169.6 µs |
| p99 / max | 798.7 / 1154.6 µs | 835.5 / 1616.2 µs | 672.0 / 1269.7 µs |
| Allocated per tick | 63 622 B | 63 609 B | 64 168 B |

**Perf reading:** single samples on this machine vary by ±40 % with machine state (the `ai16` build
re-measured today is 233.7 µs, against 166.3 µs when its artifact was captured). The honest comparison is
the three columns above: the audited build is inside that variance, so the finer lane sampling plus its
flight-path hoist and conservative reachability pre-filter are **perf-neutral**, and p99 stays far inside
the 16.7 ms step. A9.3 still owns the allocation work.

**Reading:** the fixes are correctness fixes and they did not move completion on this seed (70/67 % against
72/70 %). The audit's value is that it rules out "one broken constant" and names the two loss channels:
interceptions (one pass in four) and passes into space the runner does not continue (A5.3). The lane test
now samples the ball's travel rather than 0.1 s of flight.

---

## 2026-11-13 — the restart taker becomes a passer (simulation version `ai18-rs1`)

Files: `a4-restart-delivery-tactical-gate.txt`, `a4-restart-delivery-11v11.txt`,
`a4-restart-delivery-perf.txt`.

A rendered-session report ("passes miss, and a ball out of play is the reproducer") was measured with a
temporary harness over a thirty-minute 11v11 segment, classifying passes by `RestartOrigin`:

| Restart | Passes | reached the man | to the opposition | ball ran >3 m past him |
| :--- | ---: | ---: | ---: | ---: |
| Throw-in (`ai17`) | 35 | 10 (29 %) | 20 (57 %) | 16 |
| Throw-in (`ai18`) | 29 | **24 (83 %)** | 3 | **0** |

The cause was `EvaluateSetPieceRestart`: it aimed at the receiver's current feet and chose power from a
linear `distance / 32` ramp, lofting everything past 22 m — a 22 m throw travelled 40 m and a 30 m throw
50 m, over the receiver's head. The taker now plans through `CandidateGenerator.TryPlanSetPieceDelivery`
with the carrier's planner and success model, prefers a lane that is open, and keeps the blind fallback
only so a restart cannot be stranded.

### 11v11 headless match — `a4-restart-delivery-11v11.txt`

| Metric | ai16-rs1 | ai17-rs1 | ai18-rs1 |
| :--- | ---: | ---: | ---: |
| Ticks | 326 168 | 326 298 | 326 312 |
| Score | 1 – 2 | 1 – 1 | **3 – 0** |
| Possession | 50.5 / 49.5 | 50.0 / 50.0 | 51.2 / 48.8 |
| Passes | 607 / 597 (72 / 70 %) | 647 / 584 (70 / 67 %) | **588 / 642 (71 / 74 %)** |
| Shots | 29 / 22 | 30 / 28 | **41 / 8** |
| Width (all-tick) | 43.9 / 40.7 m | 43.7 / 41.6 m | 44.3 / 41.6 m |

### Tactical differentiation gate — `a4-restart-delivery-tactical-gate.txt`

5 seeds × 240 s: full time, played, the possession side wider on **5/5** seeds, mean separation **4.52 m**.
**GATE: PASS.**

### Performance — `a4-restart-delivery-perf.txt`

169.6 µs → **175.8 µs** mean, p99 591.2 µs, 64 614 B/tick. Inside this machine's ±40 % run-to-run variance;
the restart scan only runs while a restart is pending.

---

## 2026-11-13 — a lead pass is a bet on a run (simulation version `ai19-rs1`)

Files: `a4-lead-risk-tactical-gate.txt`, `a4-lead-risk-11v11.txt`, `a4-lead-risk-perf.txt`.

A rendered-session report ("through passes are broken, a pass to feet works") was measured at the decision
level with a carrier, one mate and no defenders:

| Mate | Candidate | What the ball did | `p` before | `p` after |
| :--- | :--- | :--- | ---: | ---: |
| running away at 6 m/s | `ToFeet` | 26.6 m ball to a man 14.6 m away (12 m lead) | 0.92 | **0.81** |
| standing still | `ToRun` | 5 m past a man who was not running | 0.92 | **not offered** |
| standing still | `ToFeet` | at him | 0.97 | 0.97 |

The model was crediting the runner with a sprint onto a run nothing had committed to (A5.3 does not exist
yet). The completion is now discounted by the lead (`PassSuccessModel.LeadRisk01` = 0.20,
`LeadRiskScaleMeters` = 8 m), and a ball into space requires a receiver who is actually running
(`CandidateGenerator.MinRunIntoSpaceSpeed` = 1.5 m/s). `LeadRisk01 = 0.35` was tried first and made a
four-minute multi-seed match go shotless, so 0.20 is the shipped value.

### 11v11 headless match — `a4-lead-risk-11v11.txt`

| Metric | ai18-rs1 | ai19-rs1 |
| :--- | ---: | ---: |
| Ticks | 326 312 | 325 632 |
| Score | 3 – 0 | **6 – 1** |
| Possession | 51.2 / 48.8 | 52.8 / 47.2 |
| Passes | 588 / 642 (71 / 74 %) | **407 / 440 (79 / 82 %)** |
| Shots | 41 / 8 | 34 / 18 |
| Width (all-tick) | 44.3 / 41.6 m | 44.8 / 40.8 m |

**The trade:** completion is up eight points on both sides, and pass volume is down about a third because
the side keeps the ball when the only forward option is a ball into space it cannot trust. That is the
intended direction — the alternative was 200 passes a match that nobody received.

### Tactical differentiation gate — `a4-lead-risk-tactical-gate.txt`

5 seeds × 240 s: full time, played, the possession side wider on **5/5** seeds, mean separation **4.26 m**.
**GATE: PASS.**

### Performance — `a4-lead-risk-perf.txt`

160.9 µs mean, p99 663.3 µs, 65 069 B/tick (inside this machine's run-to-run variance).

---

## Still to capture (A0.5, part 2)

- A per-phase breakdown of the tick (perception / decisions / execution / physics / rules), so a
  regression can be attributed to a subsystem.
- 5v5 baseline.
- Short rendered clips from the Godot client (needs a human at the machine; tracked as part of A9.4).
- Fixed development seed set + a separate held-out seed set, recorded per matchup.
