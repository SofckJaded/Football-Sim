# Engineering Decisions

- **Single Core Assembly (`FootballSim`):** Combined Core, Physics, Locomotion, AI, Tactics, and Rules namespaces into one zero-dependency C# library as recommended in Section 2 to avoid circular project references while keeping strict module boundaries.
- **Godot 4.7.2 .NET (`net8.0`):** Pinned `Godot.NET.Sdk/4.7.2` with `net8.0` for out-of-the-box compatibility with the official Godot 4.7.2 Mono build.

## Advanced AI (A0–A1)

- **Scenario ownership is seeded, not simulated (A0.2).** A fixture that starts mid-play calls
  `BallPossessionTracker.Seed(snapshot)` instead of injecting a synthetic contact event. A fake contact
  would also fire statistics, the rules engine and the contact log, producing a match that never
  happened. The seed is still validated on the first tick (a carrier more than 3 m from the ball loses
  control), so a fixture cannot create a possession the physics would forbid.
- **`MatchRulesEngine.StartScenario` takes primitives, not a `MatchScenario` (A0.2).** The rules engine
  stays free of the scenario type; the engine adapts. `MatchSimulationEngine` is the only place that
  knows about both.
- **Scenario matches skip the even-team-count check (A0.2).** `SimulationConfigValidation.Validate`
  requires equal sides because it validates a generated squad. A 3v2 fixture is deliberately
  unbalanced, so the scenario path validates the pieces it actually supplies (pitch, profiles,
  attributes, tendencies, players, ball) instead.
- **The decision trace separates "chosen" from "executed" (A0.4).** `DecisionTrace` records what the
  controller picked (including its runner-up and margin); `ContactLog` and `LastContactAttempts` record
  what the execution layer accepted or refused. Neither is derived from the other, which is what makes
  "the AI wanted X but execution did Y" a diagnosable difference rather than a guess.
- **Unmodelled trace fields are null, never zero (A0.4).** `KnowledgeAgeTicks`, `CandidateKey` and
  `IntentId` stay null until A2.2/A1.1/A1.2. A zero would read as a measurement ("perfectly fresh
  knowledge"), which is a claim the code cannot make.
- **Offside geometry is frozen at the contact, not fixed by reordering the tick (A1.4).** Physics and
  rules both depend on the current tick order (physics integrates, then laws judge). Reordering would
  break boundary and goal handling, so the engine captures a `ContactGeometrySnapshot` before
  integration and hands it to `StepRules`. Passing no snapshot falls back to live state, which keeps
  every pre-A1.4 unit test and standalone probe valid.
- **The random-stream split is a versioned behaviour change (A1.3).** Perception previously drew from
  the same `SeededRandom` as execution, so removing those draws necessarily changes every trajectory.
  Rather than fake continuity, the build now declares `SimulationVersion.AiBehaviour = 2` and records
  the tag in artifacts. Cross-version trajectory identity is explicitly not a requirement; same-version
  command replay remains exact.
- **Stream derivation is a documented mixer, not a hash library (A1.3).** `RandomStreams.Derive` is
  SplitMix64's finalizer over `seed + goldenRatio * (stream + 1)`. It only has to be deterministic and
  to separate streams; both properties are tested, and the golden-ratio stride means adding a future
  stream never moves the seeds of the existing ones.

