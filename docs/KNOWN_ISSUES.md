# Known Issues & Limitations

## Simulation model

- **Goal net physics:** an analytical bounding box energy damper behind the goal plane, not cloth
  vertex simulation. This is a deliberate simplification, not a defect.
- **Player contacts** are capsule-vs-capsule with a vertical-clearance rule; there is no limb, shoulder
  or sliding tackle contact model. The ordinary collision impulse is correct (it reduced closing speed
  from 2.0 to 1.1 m/s in the audit check); the earlier suspected impulse-sign issue was **not** a
  finding.
- **Tackle restraint is a game-feel rule, not a physical model.** A tackle winner is protected for
  0.4 s (cleared by their own deliberate kick) and each AI can request a tackle once per 0.55 s. This
  exists because two close AI players used to trade the ball with 8–9 alternating tackles in 7 s; the
  same face-off now produces 1 tackle and the winner carries the ball away. If tackling feels too
  strong or too rare, retune `FootballerExecutionSystem.TackleShieldSeconds` and
  `TacticalUtilityAiController.TackleCooldownSeconds` together (and keep the winner/victim recovery
  times in proportion).
- **Subjective feel tuning:** camera comfort, control responsiveness, shooting feel and drill difficulty
  need a human playtest in Godot 4.7.2. No automated test can close that. The first playtest pass fixed
  the five reported AI behaviours (carries out of play, first touch, close-quarters ball jitter/camera
  shake, weak defence, out-of-position players), but the *feel* of the fixes — especially the tighter
  ball control, the covering defender and the smoothed camera look target — has not been reviewed by a
  human in a rendered session.
- **Shot counts are lower than before the defensive pass.** The AI now keeps the ball (a real first
  touch, a control lock, more passing) and defends with an intercepting press, so probe shots are
  roughly 60–70 % of the pre-pass numbers while passes are up. This is a balance choice, not a defect;
  retune the shot/pass utilities if playtests show the attacks are too patient.
- **AI pass volume is lower than before the passing pass.** The AI abandons a pass whose line is blocked
  (a time-aware test: the ball's flight against each opponent's predicted run), so it sometimes keeps the
  ball and dribbles instead of playing a giveaway. Measured over 8 × 11v11 600 s: 262 → 190 passes per
  match, completion 73 % → 87 %, interceptions 27 % → 13 %, median pass power 65 % → 85 %. If the match
  reads as too sterile or the defender is too easy to hold off, retune
  `IsPassLaneBlocked`'s `PassLaneReactionSeconds` / `PassLaneTurnSpeed` / `PassLaneContactReach` and the
  shot/pass/dribble utilities together; do not delete the blocked-line test or fall back to the static
  `EvaluateLaneClearance` as the only gate.
- **Long ground passes use maximum power.** A ground pass can only still be arriving crisply (~9 m/s)
  after about 22 m, so any pass longer than that is struck at 100 % power (it still arrives trapable,
  just later). This is the physics of the drag model, not a bug; a lofted/driven pass model is the way
  to make long passes look different.

## Presentation and configuration

- **Presentation timing is tested, rendering is not measured.** `FixedStepClock` and
  `CameraSmoothing` are unit-tested at 30/60/144 FPS and the real scenes are booted for 180 frames, but
  a rendered session at 30 FPS vs 144 FPS on real hardware has not been visually inspected. Interactive
  playtesting of the menu, match and drills is still outstanding.
- **The ball is now visually smaller.** Its rendered radius is the physical 0.11 m (previously a
  hard-coded 0.16 m sphere), so the pitch reads slightly differently. This is intentional: apparent
  contact, reach and the whole-ball boundary plane now agree with what the simulation resolves.
- **Configuration is validated fail-fast.** An out-of-range team size, nonpositive or non-finite half
  length, human id that is not part of the match, degenerate pitch geometry, ball parameters or player
  attributes now throw (`ArgumentOutOfRangeException`) at construction instead of being silently
  clamped or failing much later. Callers that previously relied on lenient values must sanitize them
  first — the menu already clamps half length through `MatchPresets`.
- **Inspector ids are resolved, not indexed.** `MatchClientNode` looks the inspected player up by id
  with a fallback into the real squad; never index `Players[playerId - 1]` again (a 5-a-side match has
  ten players, so the default striker id #9 is not a valid array index).

## Advanced AI — baseline measurements and known limitations

Recorded 2026-11-12 before any advanced-AI behaviour change. Numbers are on the reference machine
documented in `artifacts/advanced-ai/README.md`.

- **The AI allocates far more than it should.** A Release 11v11 tick allocates **41.7 KB** (mean over
  20 000 ticks, diagnostics off), which is 2.5 MB/s of garbage and 50 gen-0 collections per 20 000 ticks.
  Wall time is fine today (mean 102 µs, p99 343 µs against a 16 667 µs budget), so this is headroom being
  spent, not a current stall. It becomes a real constraint once A2–A6 add spatial caches and candidate
  sets; A9.3 owns the fix. Do not add per-tick LINQ or per-candidate lists on top of this.
- **Per-tick spikes reach ~1 ms at p-max** (964 µs measured). One slow tick is invisible at 60 Hz, but it
  is the number to watch when the utility set grows.
- **Tactical anchors are keyed by shirt number, not by role.** `TeamTacticalCoordinator.ComputePlayerAnchor`
  reads `GetFormationRoleAndSlot(player.ShirtNumber, teamSize)`. A player whose explicit `TacticalRole`
  differs from the slot for their shirt will be anchored at the slot's position. Scenario fixtures must
  therefore assign realistic shirt numbers; A3.3 replaces this with real responsibility ownership.
- **Unbalanced scenario sides inherit a full-size anchor table.** `teamSize` is `PlayersPerTeam`, so a
  3v2 fixture uses the 5-a-side slot table for both teams. Acceptable for short probes; A3 fixes the
  structural model.
- **A scenario starts with no history.** Perception memory (A2.2) does not exist yet, so a fragment's
  first ticks have no aged knowledge to reason from. Scenario assertions must not assume reaction
  history that the engine has not built up.
- **The decision trace is deliberately incomplete.** It records tick, player, duty, chosen action, the
  runner-up and the margin, plus executed contacts. `KnowledgeAgeTicks`, `CandidateKey` and `IntentId`
  are null until A2.2, A1.1 and A1.2 respectively — a zero would read as a measurement the code cannot
  make. The inspector does not yet render the trace.

## Tooling and documentation

- **Toolchain pinning is partial.** `global.json` pins .NET SDK 8.0.x (`rollForward: latestFeature`) and
  every `PackageReference` uses an exact version, but no `packages.lock.json` is committed, so
  transitive package resolution is not byte-pinned.
- **Legacy specification references.** Older documents cite numbered design sections (`Section 0`,
  `Section 3.1`, `§3.3`, `Phase 0–9`). That numbering belongs to the original design brief, which is
  **not part of this repository**. Treat these as historical labels only:
  `README.md` is the product/controls source of truth, `docs/ARCHITECTURE.md` the design summary,
  `docs/AUDIT_HANDOFF_2026-11-06.md` the audit, and `TASKS.md` / `PROGRESS.md` / `RESUME.md` the work
  state. Where a legacy citation is retained, the surrounding text describes the actual requirement.
- **Audited defects:** all 23 findings (F01–F23) are fixed or covered by a dedicated diagnostic; no
  reproduced P1 remains. See `PROGRESS.md` for the per-batch evidence. A green suite validates the
  simulated match and the presentation timing, not how the game feels.
