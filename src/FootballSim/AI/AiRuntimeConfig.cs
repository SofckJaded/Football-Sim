namespace FootballSim.AI;

/// <summary>
/// Validated AI settings, owned by the match configuration (<see cref="FootballSim.Core.PreMatchConfig.Ai"/>).
///
/// The point of this type is that every AI number a tuner might want to change has one home with a
/// validated range, instead of being a literal inside a decision method — a literal cannot be validated,
/// cannot be recorded in an artifact, and cannot be ablated (A9.2).
///
/// It is deliberately a plain settings object and not an interface: there is exactly one implementation
/// and there is no evidence yet that a second is needed.
/// </summary>
public sealed class AiRuntimeConfig
{
    /// <summary>
    /// The defaults. Where a value had a literal before this type existed it is that literal, so a match
    /// built with <see cref="Default"/> reproduces the behaviour of the commit that introduced the field.
    /// The exception is a deliberate behaviour milestone: A7.1's <see cref="EnableScanning"/> is on by
    /// default because turning the head is the behaviour that version ships, and the switch is the A/B
    /// ablation rather than a hidden guard.
    /// </summary>
    public static readonly AiRuntimeConfig Default = new();

    // ---------------------------------------------------------------------------------------------
    // Consumed today by TacticalUtilityAiController.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Utility added to the committed action while a commitment is live, so small score wobbles do not
    /// flip the decision every tick.
    /// </summary>
    public float CommitmentBonus { get; init; } = 0.10f;

    /// <summary>
    /// How long a team-job action is held once chosen. A job target is a commitment, not a reflex, so it
    /// is held longer than a reactive action.
    /// </summary>
    public float JobCommitmentSeconds { get; init; } = 0.45f;

    /// <summary>How long a reactive (non-job) action is held once chosen.</summary>
    public float ReactionCommitmentSeconds { get; init; } = 0.18f;

    /// <summary>
    /// Absolute part of the switch cost (A4.5): the alternative must beat the current action by at least
    /// this much, on the action-value scale, before the player changes what he is doing. Small on purpose:
    /// the decision is re-scored every tick, so this exists to absorb the per-tick wobble, not to make a
    /// carrier stubborn. Measured at 0.05/0.10 — the first values — the reference match lost a third of its
    /// passes, so the default sits well inside the spread between competing actions instead.
    /// </summary>
    public float SwitchAbsoluteMargin { get; init; } = 0.02f;

    /// <summary>
    /// Relative part of the switch cost (A4.5), applied to the magnitude of the current action's own score:
    /// <c>best − current &gt; absolute + relative × max(|current|, ε)</c>. Using the magnitude (rather than
    /// multiplying the utilities, the plan's example to avoid) is what makes the rule behave sanely for
    /// negative scores, where a proportional comparison inverts the decision.
    /// </summary>
    public float SwitchRelativeMargin { get; init; } = 0.04f;

    // ---------------------------------------------------------------------------------------------
    // Declared here so the plan's cadence table and candidate caps live in one place rather than being
    // invented inside the consumers.
    //
    // The candidate caps below (MaxPassTargets, MaxCarrierCandidates, MaxOffBallCandidates) are read by the
    // current build. The scheduler fields are read by AiScheduler and only take effect when
    // EnableScheduledDecisions is on, so they are the part that is declared ahead of its default.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Live ticks between perception refreshes (plan §3: 15 Hz, i.e. every 4 ticks).</summary>
    public int PerceptionRefreshTicks { get; init; } = 4;

    /// <summary>Live ticks between utility updates for the carrier and nearby defenders (10 Hz).</summary>
    public int CarrierDecisionTicks { get; init; } = 6;

    /// <summary>Live ticks between utility updates for other outfield players (6 Hz).</summary>
    public int OutfieldDecisionTicks { get; init; } = 10;

    /// <summary>Live ticks between refreshes of cached local spatial queries (10 Hz).</summary>
    public int LocalSpatialCacheTicks { get; init; } = 6;

    /// <summary>Live ticks between rebuilds of the coarse team-belief spatial map (5 Hz).</summary>
    public int TeamBeliefMapTicks { get; init; } = 12;

    /// <summary>Live ticks between tactical-structure updates (3 Hz).</summary>
    public int TacticalStructureTicks { get; init; } = 20;

    /// <summary>Live ticks between local coverage/coordination updates (10 Hz).</summary>
    public int LocalCoordinationTicks { get; init; } = 6;

    /// <summary>Live ticks between slow structural reassessments (1 Hz).</summary>
    public int StructuralReassessmentTicks { get; init; } = 60;

    /// <summary>
    /// Upper bound on generated off-ball candidates per player per decision (A9.3). Not consumed yet.
    /// </summary>
    public int MaxOffBallCandidates { get; init; } = 12;

    /// <summary>
    /// Upper bound on carrier candidates after scoring (A4.1). The carrier's action set is an explicit
    /// list — shot, passes, recycle, carry, take-on, shield, clearance — and this is the hard cap the
    /// finished set is trimmed to, best score first.
    /// </summary>
    public int MaxCarrierCandidates { get; init; } = 20;

    /// <summary>
    /// Upper bound on receivers that get a full pass plan and success estimate in one decision (A4.1).
    /// The rest are pruned on a cheap progression/space pre-rank, which is what keeps the pass model's
    /// cost proportional to the configured budget rather than to the number of visible teammates.
    /// </summary>
    public int MaxPassTargets { get; init; } = 8;

    /// <summary>
    /// Upper bound on off-ball run paths offered to one player in one decision (A5.1). Each duty's kind
    /// list is already short; this is the budget that keeps a wide duty (a depth runner has four kinds)
    /// from costing four reach solves and four lane tests per tick. The kinds are offered in priority
    /// order — support and depth before speculation — so a smaller budget drops the speculative end.
    /// </summary>
    public int MaxRunCandidates { get; init; } = 4;

    /// <summary>
    /// When true, a player re-scores its candidates on its <see cref="PerceptionRefreshTicks"/>-style cadence
    /// (<see cref="CarrierDecisionTicks"/> / <see cref="OutfieldDecisionTicks"/>) instead of every live tick,
    /// re-emitting its live intent in between.
    ///
    /// <b>Off by default.</b> Turning it on is a behaviour change — a player acts on an evaluation up to one
    /// cadence window old — so it stays a switch until that behaviour has been tuned with it on (A9).
    /// </summary>
    public bool EnableScheduledDecisions { get; init; }

    // ---------------------------------------------------------------------------------------------
    // A7.1 — scanning. A player's head is independent of his body: it turns at a bounded rate toward an
    // attention target, holds, and returns. See ScanPlanner.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether players scan at all (A7.1). <b>On by default</b>: this is the behaviour A7.1 ships, and the
    /// field exists so A9.2 can ablate it and so evidence can run the same worlds with the feature off —
    /// with scanning off every player is back to the pre-A7.1 cone fixed to the body facing.
    /// </summary>
    public bool EnableScanning { get; init; } = true;

    /// <summary>
    /// How far the head may turn from the body facing, in degrees. A neck cannot spin all the way round;
    /// with the sight cone's own half-angle this still lets a turned head see the space behind the shoulder.
    /// </summary>
    public float ScanMaxHeadTurnDegrees { get; init; } = 100f;

    /// <summary>
    /// Bounded head turn rate in degrees per second. This is the reason a scan costs something: the head
    /// needs time to get to the target, so a very short pass can arrive before the glance completes.
    /// </summary>
    public float ScanTurnRateDegreesPerSecond { get; init; } = 600f;

    /// <summary>How long the head holds a completed visual fix before returning to the body facing.</summary>
    public float ScanHoldSeconds { get; init; } = 0.20f;

    /// <summary>
    /// How long the head spends back on the body facing after a fix before it may scan again. A scan is a
    /// discrete glance, not permanent rear vision.
    /// </summary>
    public float ScanCooldownSeconds { get; init; } = 0.35f;

    /// <summary>
    /// A believed opponent not seen for this long is stale enough to be worth turning to refresh. Shorter
    /// values make players scan more often; the value is a memory policy, not a scan speed.
    /// </summary>
    public float ScanRefreshAgeSeconds { get; init; } = 1.0f;

    /// <summary>Furthest a believed opponent can be and still attract a scan, in metres.</summary>
    public float ScanAttentionRangeMeters { get; init; } = 18f;

    // ---------------------------------------------------------------------------------------------
    // A7.2 — delayed expiring team calls. A player may shout one instruction at a time at his own team;
    // the board applies the delay and the expiry. See TeamCalls/TeamCallPlanner.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether players make team calls at all (A7.2). <b>On by default</b>: this is the behaviour the
    /// version ships, and the field exists so A9.2 can ablate the channel and so the evidence probe can
    /// run the same worlds with it off. It gates the <b>producer</b>: with calls off no player shouts, so
    /// every observation's <see cref="FootballerObservation.Calls"/> stays neutral. The board itself is not
    /// short-circuited, so a fixture or test can still publish a call directly to exercise the consumer.
    /// </summary>
    public bool EnableTeamCalls { get; init; } = true;

    /// <summary>
    /// How long a shout takes to reach a team-mate, in live ticks. The default is
    /// <see cref="TeamCallBoard">&apos;s</see> A5.3 figure (18 ticks = 0.30 s) on purpose: a call is the
    /// same kind of communication as a run advertisement, and two different delays for two team channels
    /// would be a second convention where one already exists.
    /// </summary>
    public int TeamCallCommunicationDelayTicks { get; init; } = 18;

    // ---------------------------------------------------------------------------------------------
    // A7.3 — reception planning. The intended receiver of a pass in flight plans the first touch
    // (kill / open body / stride touch / shield / escape) before the ball arrives. See
    // ReceptionPlanner.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the intended receiver plans the first touch (A7.3). <b>On by default</b>: this is the
    /// behaviour the version ships, and the field exists so A9.2 can ablate it and so the evidence probe
    /// can run the same worlds with it off. Off means the receiver keeps the pre-A7.3 intercept ray
    /// exactly — one candidate, one facing, no plan — which is what makes the A/B two builds rather
    /// than two tunings of the same decision.
    /// </summary>
    public bool EnableReceptionPlanning { get; init; } = true;

    // ---------------------------------------------------------------------------------------------
    // A7.5 — separated error sources. Stamina and composure are the shared causes; one module converts
    // them into observation noise, evaluation error, reaction latency and execution error (see
    // PlayerCognitiveState).
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether A7.5's cognitive error model is active: stamina and composure producing observation noise,
    /// evaluation error and reaction latency, pressure and fatigue degrading a first touch, and a reception
    /// plan's anticipation being measurable against the contact it actually got. <b>On by default</b>: this
    /// is the behaviour the version ships, and the field exists so A9.2 can ablate the mechanism and so the
    /// evidence probe can run the same worlds with it off. Off is exactly the previous A7.4 build: the
    /// distance-based observation noise, the attribute's own reaction delay, no evaluation error, no fatigue
    /// or pressure term on a first touch and no anticipation penalty — which is what makes the A/B two
    /// builds rather than two tunings of the same numbers.
    /// </summary>
    public bool EnableCognitiveError { get; init; } = true;

    /// <summary>
    /// Whether the match fields <b>archetypes</b> — playmaker, ball winner, inverted fullback, touchline
    /// winger, inside forward, target forward, sweeper keeper — as preferences on the one decision pipeline
    /// (A7.6, see <see cref="PlayerArchetypeProfile"/>). <b>On by default</b>: this is the behaviour the
    /// version ships, and the field exists so the evidence probe can run the same match with the uniform
    /// pre-A7.6 squad and so A9.2 can ablate it. Off means every player is <see cref="PlayerArchetype.None"/>
    /// — the role's own tendencies, no run preference, the neutral scan attention and the neutral keeper — so
    /// the off run is the previous build rather than a near-miss.
    ///
    /// The switch resolves a player's archetype when the squad is built. A scenario fixture that states an
    /// archetype explicitly keeps it even with this off: the field says what the default squad is, not what a
    /// fixture may describe.
    /// </summary>
    public bool EnableArchetypes { get; init; } = true;

    // ---------------------------------------------------------------------------------------------
    // A8.1 — bounded combinations. A one-two or a third-player move is a contract between team-mates:
    // participants, a return region, an expected arrival, a confidence and an expiry, published through the
    // one communication channel. See CombinationCoordinator.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether players form one-two and third-player contracts (A8.1). <b>On by default</b>: this is the
    /// behaviour the version ships, and the field exists so A9.2 can ablate the mechanism and so the evidence
    /// probe can run the same worlds with it off. Off means the carrier never proposes a contract, the board
    /// stays empty, every observation's <see cref="FootballerObservation.Combination"/> is neutral and no
    /// candidate or score is touched — the version-46 build exactly, which is what makes the A/B two builds
    /// rather than two tunings of the same decision.
    /// </summary>
    public bool EnableCombinations { get; init; } = true;

    // ---------------------------------------------------------------------------------------------
    // A8.2 — the shallow continuation search. From the carrier's own perceived state, score the best few
    // first actions and at most a few continuations each, to depth two. See ShallowActionSearch.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the carrier runs A8.2's shallow search to choose which continuation is worth a contract.
    /// <b>On by default</b>: this is the behaviour the version ships. The field exists so A9.2 can ablate the
    /// search and so the evidence probe can run the same worlds with it off — and with it off the A8.1
    /// heuristic trigger is exactly the previous build, which is what makes the A/B two builds rather than two
    /// tunings of the same decision.
    ///
    /// The switch does not gate combinations: <see cref="EnableCombinations"/> off is still the pre-A8 build.
    /// With combinations on and this off, the carrier forms contracts from A8.1's pressure/geometry heuristic
    /// alone. With both on, the search proposes for the best few first actions and the heuristic still covers
    /// every receiver the bounded search did not examine.
    /// </summary>
    public bool EnableShallowSearch { get; init; } = true;

    // ---------------------------------------------------------------------------------------------
    // A8.3 — switch / overload-to-isolate recognition. The carrier recognizes, from his own picture, that the
    // near side is closed and the far side is free enough to switch to. See SwitchRecognition.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the carrier recognizes switch / overload-to-isolate opportunities (A8.3). <b>On by default</b>:
    /// this is the behaviour the version ships, and the field exists so A9.2 can ablate the recognition and so
    /// the evidence probe can run the same worlds with it off. Off means no switch preference is added to any
    /// pass and no far-side receiver is rescued from the pass-target pre-rank — the version-48 build exactly,
    /// which is what makes the A/B two builds rather than two tunings of the same decision.
    /// </summary>
    public bool EnableSwitchRecognition { get; init; } = true;

    // ---------------------------------------------------------------------------------------------
    // A8.4 — short trap contracts. The defending side commits to a short, coordinated trap — pressure, screening
    // and cover — around a carrier pinned to a touchline. See TrapCoordinator / TrapPolicy.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the defending team raises short trap contracts (A8.4). <b>On by default</b>: this is the behaviour
    /// the version ships, and the field exists so A9.2 can ablate the mechanism and so the evidence probe can run
    /// the same worlds with it off. Off means the trap board never raises anything, every observation's
    /// <see cref="FootballerObservation.Trap"/> is neutral, and no candidate or point is touched — the version-49
    /// build exactly, which is what makes the A/B two builds rather than two tunings of the same decision.
    /// </summary>
    public bool EnableTrapContracts { get; init; } = true;

    /// <summary>Short, stable description used in artifacts and replay metadata.</summary>
    public string Describe() =>
        $"commit(bonus {CommitmentBonus:F2}, job {JobCommitmentSeconds:F2}s, react {ReactionCommitmentSeconds:F2}s) " +
        $"switch(abs {SwitchAbsoluteMargin:F2}, rel {SwitchRelativeMargin:F2}) " +
        $"cadence(p{PerceptionRefreshTicks} c{CarrierDecisionTicks} o{OutfieldDecisionTicks} " +
        $"s{LocalSpatialCacheTicks} b{TeamBeliefMapTicks} t{TacticalStructureTicks} " +
        $"l{LocalCoordinationTicks} r{StructuralReassessmentTicks}) " +
        $"caps(off {MaxOffBallCandidates}, carrier {MaxCarrierCandidates}, passTargets {MaxPassTargets}, " +
        $"runs {MaxRunCandidates}) " +
        $"scan({(EnableScanning ? "on" : "off")} turn {ScanMaxHeadTurnDegrees:F0}deg " +
        $"rate {ScanTurnRateDegreesPerSecond:F0}deg/s hold {ScanHoldSeconds:F2}s " +
        $"range {ScanAttentionRangeMeters:F0}m) " +
        $"calls({(EnableTeamCalls ? "on" : "off")} delay {TeamCallCommunicationDelayTicks}t) " +
        $"reception({(EnableReceptionPlanning ? "on" : "off")}) " +
        $"cognitive({(EnableCognitiveError ? "on" : "off")}) " +
        $"archetypes({(EnableArchetypes ? "on" : "off")}) " +
        $"combinations({(EnableCombinations ? "on" : "off")}) " +
        $"search({(EnableShallowSearch ? "on" : "off")}) " +
        $"switchrec({(EnableSwitchRecognition ? "on" : "off")}) " +
        $"traps({(EnableTrapContracts ? "on" : "off")})";
}

/// <summary>
/// Range checks for <see cref="AiRuntimeConfig"/>. Kept out of the type itself so the settings stay a
/// plain data object and validation has one obvious entry point next to the other config checks.
/// </summary>
public static class AiRuntimeConfigValidation
{
    /// <summary>Longest commitment a configuration may request, in seconds.</summary>
    public const float MaxCommitmentSeconds = 5f;

    /// <summary>Largest commitment bonus; above this the current action would always win.</summary>
    public const float MaxCommitmentBonus = 1f;

    /// <summary>
    /// Largest switch margin a configuration may request. Above this no alternative ever wins and the
    /// player is frozen on its first action, which is an ablation rather than a tuning value.
    /// </summary>
    public const float MaxSwitchMargin = 5f;

    /// <summary>Largest head turn a configuration may request, in degrees. Past this it is no longer a neck.</summary>
    public const float MaxScanHeadTurnDegrees = 140f;

    /// <summary>Fastest head turn rate a configuration may request, in degrees per second.</summary>
    public const float MaxScanTurnRateDegreesPerSecond = 2000f;

    /// <summary>Longest scan hold or return window a configuration may request, in seconds.</summary>
    public const float MaxScanWindowSeconds = 3f;

    /// <summary>Oldest sighting a configuration may call "fresh enough not to look again", in seconds.</summary>
    public const float MaxScanRefreshAgeSeconds = 5f;

    /// <summary>Furthest scan attention a configuration may request, in metres.</summary>
    public const float MaxScanAttentionRangeMeters = 60f;

    /// <summary>
    /// Longest communication delay a configuration may request, in live ticks (5 s). A longer delay is not
    /// a shout, it is a rumour: the call's own lifetimes are all under two seconds.
    /// </summary>
    public const int MaxTeamCallDelayTicks = 300;

    public static void Validate(AiRuntimeConfig? config, string name = "AiRuntimeConfig")
    {
        if (config is null)
            throw new ArgumentNullException(name, "AI runtime configuration must not be null.");

        RequireFinite(config.CommitmentBonus, name, nameof(AiRuntimeConfig.CommitmentBonus));
        RequireRange(config.CommitmentBonus, 0f, MaxCommitmentBonus, name, nameof(AiRuntimeConfig.CommitmentBonus));

        RequireFinite(config.JobCommitmentSeconds, name, nameof(AiRuntimeConfig.JobCommitmentSeconds));
        RequireRange(config.JobCommitmentSeconds, 0f, MaxCommitmentSeconds, name,
            nameof(AiRuntimeConfig.JobCommitmentSeconds));

        RequireFinite(config.ReactionCommitmentSeconds, name, nameof(AiRuntimeConfig.ReactionCommitmentSeconds));
        RequireRange(config.ReactionCommitmentSeconds, 0f, MaxCommitmentSeconds, name,
            nameof(AiRuntimeConfig.ReactionCommitmentSeconds));

        RequireFinite(config.SwitchAbsoluteMargin, name, nameof(AiRuntimeConfig.SwitchAbsoluteMargin));
        RequireRange(config.SwitchAbsoluteMargin, 0f, MaxSwitchMargin, name,
            nameof(AiRuntimeConfig.SwitchAbsoluteMargin));

        RequireFinite(config.SwitchRelativeMargin, name, nameof(AiRuntimeConfig.SwitchRelativeMargin));
        RequireRange(config.SwitchRelativeMargin, 0f, MaxSwitchMargin, name,
            nameof(AiRuntimeConfig.SwitchRelativeMargin));

        // A cadence of zero ticks would mean "every tick" expressed as a division, which then breaks as
        // soon as a scheduler modulo is applied. One tick is the floor.
        RequireTicks(config.PerceptionRefreshTicks, name, nameof(AiRuntimeConfig.PerceptionRefreshTicks));
        RequireTicks(config.CarrierDecisionTicks, name, nameof(AiRuntimeConfig.CarrierDecisionTicks));
        RequireTicks(config.OutfieldDecisionTicks, name, nameof(AiRuntimeConfig.OutfieldDecisionTicks));
        RequireTicks(config.LocalSpatialCacheTicks, name, nameof(AiRuntimeConfig.LocalSpatialCacheTicks));
        RequireTicks(config.TeamBeliefMapTicks, name, nameof(AiRuntimeConfig.TeamBeliefMapTicks));
        RequireTicks(config.TacticalStructureTicks, name, nameof(AiRuntimeConfig.TacticalStructureTicks));
        RequireTicks(config.LocalCoordinationTicks, name, nameof(AiRuntimeConfig.LocalCoordinationTicks));
        RequireTicks(config.StructuralReassessmentTicks, name, nameof(AiRuntimeConfig.StructuralReassessmentTicks));

        RequireCandidates(config.MaxOffBallCandidates, name, nameof(AiRuntimeConfig.MaxOffBallCandidates));
        RequireCandidates(config.MaxCarrierCandidates, name, nameof(AiRuntimeConfig.MaxCarrierCandidates));
        RequireCandidates(config.MaxPassTargets, name, nameof(AiRuntimeConfig.MaxPassTargets));
        RequireCandidates(config.MaxRunCandidates, name, nameof(AiRuntimeConfig.MaxRunCandidates));

        // A7.1 scanning. Zero is legal for the head turn and its rate (that is the "no scanning motion"
        // ablation), but not for the attention range: a scan that can see nobody is not a configuration.
        RequireFinite(config.ScanMaxHeadTurnDegrees, name, nameof(AiRuntimeConfig.ScanMaxHeadTurnDegrees));
        RequireRange(config.ScanMaxHeadTurnDegrees, 0f, MaxScanHeadTurnDegrees, name,
            nameof(AiRuntimeConfig.ScanMaxHeadTurnDegrees));

        RequireFinite(config.ScanTurnRateDegreesPerSecond, name, nameof(AiRuntimeConfig.ScanTurnRateDegreesPerSecond));
        RequireRange(config.ScanTurnRateDegreesPerSecond, 0f, MaxScanTurnRateDegreesPerSecond, name,
            nameof(AiRuntimeConfig.ScanTurnRateDegreesPerSecond));

        RequireFinite(config.ScanHoldSeconds, name, nameof(AiRuntimeConfig.ScanHoldSeconds));
        RequireRange(config.ScanHoldSeconds, 0f, MaxScanWindowSeconds, name,
            nameof(AiRuntimeConfig.ScanHoldSeconds));

        RequireFinite(config.ScanCooldownSeconds, name, nameof(AiRuntimeConfig.ScanCooldownSeconds));
        RequireRange(config.ScanCooldownSeconds, 0f, MaxScanWindowSeconds, name,
            nameof(AiRuntimeConfig.ScanCooldownSeconds));

        RequireFinite(config.ScanRefreshAgeSeconds, name, nameof(AiRuntimeConfig.ScanRefreshAgeSeconds));
        RequireRange(config.ScanRefreshAgeSeconds, 0f, MaxScanRefreshAgeSeconds, name,
            nameof(AiRuntimeConfig.ScanRefreshAgeSeconds));

        RequireFinite(config.ScanAttentionRangeMeters, name, nameof(AiRuntimeConfig.ScanAttentionRangeMeters));
        RequireRange(config.ScanAttentionRangeMeters, 1f, MaxScanAttentionRangeMeters, name,
            nameof(AiRuntimeConfig.ScanAttentionRangeMeters));

        // A7.2 team calls. Zero delay is legal only as an ablation (a call that is heard the tick it is
        // made); it is deliberately not the default, because the delay is what the milestone is about.
        RequireRange(config.TeamCallCommunicationDelayTicks, 0, MaxTeamCallDelayTicks, name,
            nameof(AiRuntimeConfig.TeamCallCommunicationDelayTicks));
    }

    private static void RequireFinite(float value, string owner, string field)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(field, value, $"{owner}.{field} must be a finite number.");
    }

    private static void RequireRange(float value, float min, float max, string owner, string field)
    {
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(
                field, value, $"{owner}.{field} must be between {min} and {max}.");
    }

    private static void RequireTicks(int value, string owner, string field)
    {
        if (value < 1 || value > 600)
            throw new ArgumentOutOfRangeException(
                field, value, $"{owner}.{field} must be between 1 and 600 live ticks.");
    }

    private static void RequireCandidates(int value, string owner, string field)
    {
        if (value < 1 || value > 64)
            throw new ArgumentOutOfRangeException(
                field, value, $"{owner}.{field} must be between 1 and 64 candidates.");
    }
}
