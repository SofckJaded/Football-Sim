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
    /// The defaults. They are the values the controller used as literals before this type existed, so a
    /// match built with <see cref="Default"/> reproduces the previous behaviour exactly.
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
    // Declared now, consumed by A1.2 / A4 / A9.
    //
    // These are validated here so the plan's cadence table and candidate caps live in one place rather
    // than being invented inside a scheduler that does not exist yet. Nothing reads them at the moment:
    // the current engine decides and perceives every live tick. Do not describe them as active.
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

    /// <summary>Short, stable description used in artifacts and replay metadata.</summary>
    public string Describe() =>
        $"commit(bonus {CommitmentBonus:F2}, job {JobCommitmentSeconds:F2}s, react {ReactionCommitmentSeconds:F2}s) " +
        $"switch(abs {SwitchAbsoluteMargin:F2}, rel {SwitchRelativeMargin:F2}) " +
        $"cadence(p{PerceptionRefreshTicks} c{CarrierDecisionTicks} o{OutfieldDecisionTicks} " +
        $"s{LocalSpatialCacheTicks} b{TeamBeliefMapTicks} t{TacticalStructureTicks} " +
        $"l{LocalCoordinationTicks} r{StructuralReassessmentTicks}) " +
        $"caps(off {MaxOffBallCandidates}, carrier {MaxCarrierCandidates}, passTargets {MaxPassTargets}, " +
        $"runs {MaxRunCandidates})";
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
