using FootballSim.World.Core;

namespace FootballSim.World.Players;

/// <summary>
/// Weekly training environment supplied by the player's club (the W2 <c>Club</c> maps itself to this).
/// </summary>
/// <param name="Tier">Division tier, 1 (top flight) .. 6 (amateur); selects the tier training multiplier.</param>
/// <param name="CoachingQuality01">
/// Club coaching quality, 0.70..1.30 (plan §4.5). It multiplies the tier's own training factor, so a
/// strong tier-6 setup can never out-train a weak tier-1 setup: the tier factor is the pyramid level
/// and this field is the club-level variation within it.
/// </param>
/// <param name="WeeklySessions">Organised sessions per week (plan §4.5: 1-2 amateur up to 5-6 elite).</param>
public readonly record struct TrainingEnvironment(int Tier, float CoachingQuality01, int WeeklySessions)
{
    /// <summary>
    /// Tier defaults for clubs without an override: the tier training factor already encodes the
    /// pyramid level (see <see cref="GrowthEngine.AdvanceWeek"/>), so the neutral club quality is 1.00
    /// and only the session count varies with the division.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier"/> is outside 1..6.</exception>
    public static TrainingEnvironment ForTier(int tier) => tier switch
    {
        1 => new TrainingEnvironment(1, 1.00f, 5),
        2 => new TrainingEnvironment(2, 1.00f, 5),
        3 => new TrainingEnvironment(3, 1.00f, 4),
        4 => new TrainingEnvironment(4, 1.00f, 3),
        5 => new TrainingEnvironment(5, 1.00f, 2),
        6 => new TrainingEnvironment(6, 1.00f, 1),
        _ => throw new ArgumentOutOfRangeException(
            nameof(tier), tier, "Division tier must be between 1 and 6.")
    };
}

/// <summary>What one week of training/growth changed.</summary>
/// <param name="TrainingGain">
/// Total attribute points added by this week's training gain loop across all eighteen attributes. Body
/// trade-off drags, aging decline and the post-match performance burst are reported separately (or not at
/// all), so this number is the training contribution alone.
/// </param>
/// <param name="DeclineApplied">Total attribute points lost to age decline this week (a positive number).</param>
/// <param name="AttributesImproved">
/// Count of attributes whose end-of-week current value is above its start-of-week value (a net measure, so
/// an attribute whose gain was fully wiped out by a trade-off or decline does not count).
/// </param>
/// <param name="CeilingNudgesApplied">Number of skill-and-mind ceiling nudges granted this week (0..2).</param>
/// <param name="ConditionDelta">Change applied to <see cref="WorldPlayer.Condition01"/> after clamping.</param>
/// <param name="SharpnessDelta">Change applied to <see cref="WorldPlayer.MatchSharpness01"/> after clamping.</param>
public readonly record struct GrowthWeekResult(
    float TrainingGain,
    float DeclineApplied,
    int AttributesImproved,
    int CeilingNudgesApplied,
    float ConditionDelta,
    float SharpnessDelta);

/// <summary>
/// The weekly growth engine (plan §4.5 and W1).
///
/// <para>One call advances a player by one training week: training gains decelerated by the soft ceiling,
/// training-focus multipliers, club/coaching quality, work ethic and match minutes; body-trainable
/// trade-offs; age decline; smooth physical-ceiling aging; post-match performance effects; sustained-form
/// skill ceiling nudges; condition, sharpness and the twelve-week trend snapshot.</para>
///
/// <para><b>Determinism.</b> Every random value comes from the supplied <see cref="WorldRng"/> (world code
/// uses the <see cref="WorldRandomStream.Growth"/> stream). Draw order is fixed: training jitter in roster
/// order (only for attributes that can actually gain), then the performance burst in skill-and-mind roster
/// order, then the poor-performance resilience check, then ceiling-nudge accept rolls and amounts in
/// skill-and-mind roster order. Bytes are reproducible for the same player state, inputs and RNG state.</para>
///
/// <para><b>Mutation contract.</b> The player's attribute set, condition, sharpness, form and trend history
/// are the only things this engine touches; match appearances are recorded by the caller (the appearance
/// needs a season/week, which this API does not take), so the soft-ceiling-decay window is read, never
/// written, here.</para>
/// </summary>
public static class GrowthEngine
{
    // ---------------------------------------------------------------------------------------------
    // Calibration constants (plan §4.5). Public so tests and the audit probe can cite them directly.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Base weekly gain of a skill-and-mind attribute (before every multiplier). Calibrated with
    /// <see cref="BodyTrainableBaseGain"/> and <see cref="InnateBaseGain"/> so a sixteen-year-old skill
    /// attribute with a 25-35 point gap opens roughly that gap in about four seasons of full training.
    /// </summary>
    public const float SkillAndMindBaseGain = 0.10f;

    /// <summary>Base weekly gain of a body-trainable attribute (before every multiplier).</summary>
    public const float BodyTrainableBaseGain = 0.09f;

    /// <summary>Base weekly gain of an innate attribute (before every multiplier; tiny by design).</summary>
    public const float InnateBaseGain = 0.02f;

    /// <summary>Below this current-to-ceiling gap the soft ceiling keeps gains at the floor factor.</summary>
    private const float SoftCeilingReferenceGap = 18f;

    /// <summary>Floor of the soft-ceiling factor so progress never hard-stops before the cap is reached.</summary>
    private const float SoftCeilingMinimumFactor = 0.08f;

    /// <summary>Training jitter band (plan §4.5 "Steady, Controllable": ±15% around the calibrated gain).</summary>
    private const float MinimumTrainingJitter = 0.85f;

    private const float MaximumTrainingJitter = 1.15f;

    /// <summary>Rehab-only training rate for an injured player.</summary>
    private const float InjuredTrainingMultiplier = 0.25f;

    /// <summary>A poor match rating (at or below 5.5) costs 15% of this week's skill gain.</summary>
    private const float PoorPerformanceGainMultiplier = 0.85f;

    // Match-minute acceleration of skill-and-mind gain (plan §4.5 §4).
    private const int MatchMinutesAccelerationThreshold = 60;
    private const int ShortCameoUpperMinutes = 14;
    private const int PartialMatchUpperMinutes = 59;
    private const float FullMatchSkillGainMultiplier = 1.45f;
    private const float ShortCameoSkillGainMultiplier = 1.20f;
    private const float FringeSkillGainMultiplier = 1.00f;
    private const float NoMinutesSkillGainMultiplier = 0.75f;

    // Body-trainable trade-offs (plan §4.5).
    private const float StrengthAgilityDragThreshold = 72f;
    private const float StrengthAgilityDrag = 0.04f;
    private const float OverBulkHeadroomTaxThreshold = 78f;
    private const float OverBulkTopSpeedCeilingTax = 0.015f;
    private const float AgilityStrengthDragThreshold = 70f;
    private const float AgilityStrengthDrag = 0.03f;

    // Age decline (plan §4.5 §1) uses each CATEGORY's onset; see WeeklyDeclinePerWeek for why.
    private const int InnateDeclineOnsetAge = 28;
    private const int BodyTrainableDeclineOnsetAge = 30;
    private const int SkillAndMindDeclineOnsetAge = 34;
    private const float InnateDeclineBase = 0.020f;
    private const float InnateDeclinePerYear = 0.010f;
    private const float InnateDeclineCap = 0.08f;
    private const float BodyDeclineBase = 0.015f;
    private const float BodyDeclinePerYear = 0.008f;
    private const float BodyDeclineCap = 0.06f;
    private const float SkillDeclineBase = 0.010f;
    private const float SkillDeclinePerYear = 0.005f;
    private const float SkillDeclineCap = 0.04f;

    // Physical ceiling aging pressure past 29 (plan §4.5 §5: smooth decline, never a cliff).
    private const int PhysicalCeilingAgingOnsetAge = 29;
    private const float PhysicalCeilingAgingBase = 0.004f;
    private const float PhysicalCeilingAgingPerYear = 0.002f;
    private const float MaximumPhysicalCeilingAgingPerWeek = 0.03f;

    // Post-match performance effects.
    private const float PerformanceBurstRatingThreshold = 7.5f;
    private const float PerformanceBurstPerRatingPoint = 0.05f;
    private const float PerformanceBurstNoiseMinimum = 0.7f;
    private const float PerformanceBurstNoiseMaximum = 1.2f;
    private const float PoorPerformanceRatingThreshold = 5.5f;
    private const float PoorPerformanceConditionLoss = 0.05f;
    private const float PoorPerformanceFormLoss = 0.02f;

    // Soft skill-ceiling nudges (plan §4.5 §5).
    private const int SoftCeilingNudgeFormWindow = 8;
    private const float SoftCeilingNudgeRatingThreshold = 7.6f;
    private const int SoftCeilingNudgeMinimumMinutes = 60;
    private const float SoftCeilingNudgeCurrentWindow = 4.0f;
    private const float SoftCeilingNudgeChance = 0.25f;
    private const float SoftCeilingNudgeMinimumAmount = 0.3f;
    private const float SoftCeilingNudgeMaximumAmount = 1.0f;
    private const int MaximumCeilingNudgesPerWeek = 2;

    // Condition / sharpness bookkeeping.
    private const float BaseConditionGain = 0.18f;
    private const float MatchLoadConditionCost = 0.30f;
    private const float RecoveryConditionGain = 0.10f;
    private const float InjuredConditionRecovery = 0.10f;
    private const float NoMinutesSharpnessLoss = -0.06f;
    private const float ShortCameoSharpnessLoss = -0.03f;
    private const float PartialMatchSharpnessGain = 0.04f;
    private const float FullMatchSharpnessGain = 0.10f;
    private const float RecoverySharpnessGain = 0.03f;

    // Environment validation.
    private const float MinimumCoachingQuality = 0.70f;
    private const float MaximumCoachingQuality = 1.30f;
    private const int MaximumWeeklySessions = 14;
    private const int MaximumMatchMinutesPerWeek = 180;

    /// <summary>Tier training factors, indexed by tier; index 0 is unused.</summary>
    private static readonly float[] TierTrainingFactors = { 0f, 1.10f, 1.00f, 0.90f, 0.75f, 0.60f, 0.45f };

    /// <summary>Skill-and-mind attributes in roster order (cached; growth runs every player every week).</summary>
    private static readonly AttributeId[] SkillAndMindAttributes =
        AttributeMetadata.OfCategory(AttributeCategory.SkillAndMind).ToArray();

    /// <summary>Innate and body-trainable attributes in roster order, i.e. everything age/injury ages.</summary>
    private static readonly AttributeId[] PhysicalAttributes = BuildPhysicalAttributes();

    // ---------------------------------------------------------------------------------------------
    // Public API.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Advances one player through one week of training, match load, aging and recovery.
    /// </summary>
    /// <param name="player">The player to mutate (attributes, condition, sharpness, form, trend history).</param>
    /// <param name="environment">The club's training environment for the week (tier, coaching, sessions).</param>
    /// <param name="focus">The weekly training-focus preset.</param>
    /// <param name="matchMinutesThisWeek">Competitive minutes played this week, 0..180.</param>
    /// <param name="averageMatchRatingThisWeek">
    /// Average match rating this week: 0 when no minutes were played, otherwise 4.0..10.0.
    /// </param>
    /// <param name="rng">The world growth random stream (deterministic; see the class remarks for draw order).</param>
    /// <returns>The measured changes this week.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> or <paramref name="rng"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The environment, focus or match-week inputs are outside their valid ranges.
    /// </exception>
    public static GrowthWeekResult AdvanceWeek(
        WorldPlayer player,
        in TrainingEnvironment environment,
        TrainingFocus focus,
        int matchMinutesThisWeek,
        float averageMatchRatingThisWeek,
        WorldRng rng)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(rng);
        ValidateEnvironment(environment);
        ValidateFocus(focus);
        ValidateMatchWeek(matchMinutesThisWeek, averageMatchRatingThisWeek);

        PlayerAttributeSet attributes = player.Attributes;

        // Start-of-week snapshot: AttributesImproved is deliberately an end-to-end net measure, so it can
        // only be computed after every effect has landed. stackalloc keeps the weekly path allocation-free.
        Span<float> weekStart = stackalloc float[AttributeMetadata.Count];
        for (int i = 0; i < AttributeMetadata.Count; i++)
        {
            weekStart[i] = attributes.Current((AttributeId)i);
        }

        int age = player.Age;
        bool injured = player.Injury.IsInjured;
        bool playedPoorly = matchMinutesThisWeek > 0 &&
            averageMatchRatingThisWeek <= PoorPerformanceRatingThreshold;
        float clubFactor = TierTrainingFactors[environment.Tier] * environment.CoachingQuality01;
        float workEthicFactor = 0.80f + player.Personality.WorkEthic / 250f;
        float skillMatchMultiplier = MatchMinutesMultiplier(matchMinutesThisWeek);

        // 1. Training gain. Roster order; the jitter draw is consumed only for attributes that can gain.
        float trainingGain = 0f;
        for (int i = 0; i < AttributeMetadata.Count; i++)
        {
            AttributeId id = (AttributeId)i;
            AttributeDefinition definition = AttributeMetadata.Get(id);

            // At (or, after aging, past) the ceiling: training cannot add current; decline still applies below.
            if (attributes.Current(id) >= attributes.Ceiling(id)) continue;

            float ageMultiplier = AgeCurveMultiplier(age, definition.Category);
            if (ageMultiplier <= 0f) continue; // No-training band: nothing to add, so no draw is consumed.

            float gain = BaseGain(definition.Category)
                * SoftCeilingFactor(attributes.Gap(id))
                * ageMultiplier
                * FocusMultiplier(focus, id)
                * clubFactor
                * workEthicFactor
                * (definition.Category == AttributeCategory.SkillAndMind ? skillMatchMultiplier : 1f)
                * rng.NextRange(MinimumTrainingJitter, MaximumTrainingJitter);

            if (definition.Category == AttributeCategory.SkillAndMind && playedPoorly)
            {
                // "Reduce this week's skill gain by 15%": applied to the gain itself so a capped attribute
                // never ends up *below* a value that the untouched gain would have produced.
                gain *= PoorPerformanceGainMultiplier;
            }

            if (injured) gain *= InjuredTrainingMultiplier; // Rehab only: no match/reps stimulus.

            float applied = attributes.AdjustCurrent(id, gain);
            if (applied > 0f) trainingGain += applied;
        }

        // 2. Body-trainable trade-offs (plan §4.5). Evaluated after the gain pass so "Strength > 72" reflects
        //    this week's work; the over-bulk TopSpeed shave is a soft headroom tax on the CEILING, never a
        //    current-value loss (the current value only follows if the player was already at the cap).
        switch (focus)
        {
            case TrainingFocus.StrengthAndPower:
                if (attributes.Current(AttributeId.Strength) > StrengthAgilityDragThreshold)
                {
                    attributes.AdjustCurrent(AttributeId.Agility, -StrengthAgilityDrag);
                }

                if (attributes.Current(AttributeId.Strength) > OverBulkHeadroomTaxThreshold)
                {
                    attributes.AdjustCeiling(AttributeId.TopSpeed, -OverBulkTopSpeedCeilingTax);
                }

                break;

            case TrainingFocus.AgilityAndQuickness:
                if (attributes.Current(AttributeId.Strength) > AgilityStrengthDragThreshold)
                {
                    attributes.AdjustCurrent(AttributeId.Strength, -AgilityStrengthDrag);
                }

                break;
        }

        // 3. Age decline (never a gain; declining attributes lose a little every week past their onset).
        float declineApplied = 0f;
        for (int i = 0; i < AttributeMetadata.Count; i++)
        {
            AttributeId id = (AttributeId)i;
            float decline = WeeklyDeclinePerWeek(age, id);
            if (decline <= 0f) continue;

            float applied = attributes.AdjustCurrent(id, -decline);
            if (applied < 0f) declineApplied += -applied;
        }

        // 4. Physical ceiling aging pressure: past 29 the body's headroom erodes smoothly alongside the
        //    current values. Use one bounded step a week, never a cliff.
        if (age > PhysicalCeilingAgingOnsetAge)
        {
            float perWeek = MathF.Min(
                PhysicalCeilingAgingBase + PhysicalCeilingAgingPerYear * (age - PhysicalCeilingAgingOnsetAge),
                MaximumPhysicalCeilingAgingPerWeek);
            for (int i = 0; i < PhysicalAttributes.Length; i++)
            {
                attributes.AdjustCeiling(PhysicalAttributes[i], -perWeek);
            }
        }

        // 5. Post-match performance effects.
        if (matchMinutesThisWeek >= MatchMinutesAccelerationThreshold &&
            averageMatchRatingThisWeek >= PerformanceBurstRatingThreshold)
        {
            // A genuinely good performance gives a small extra burst on skill-and-mind current, noisy so a
            // single great game is a nudge rather than a growth engine.
            float burst = PerformanceBurstPerRatingPoint *
                (averageMatchRatingThisWeek - PerformanceBurstRatingThreshold);
            for (int i = 0; i < SkillAndMindAttributes.Length; i++)
            {
                attributes.AdjustCurrent(
                    SkillAndMindAttributes[i],
                    burst * rng.NextRange(PerformanceBurstNoiseMinimum, PerformanceBurstNoiseMaximum));
            }
        }
        else if (playedPoorly)
        {
            // The skill gain was already cut above; now test whether the player's resilience absorbs the bad
            // game or whether condition and form take the hit. 0 resilience always fails, 100 never fails.
            if (rng.NextFloat() < 1f - player.Personality.Resilience / 100f)
            {
                player.Condition01 -= PoorPerformanceConditionLoss;
                player.Form01 -= PoorPerformanceFormLoss;
            }
        }

        // 6. Soft skill-ceiling nudges: sustained high ratings let skill-and-mind headroom creep up, at most
        //    two attributes a week and always inside the player's lifetime +8 ledger.
        int ceilingNudgesApplied = 0;
        if (player.Form.AverageRating(SoftCeilingNudgeFormWindow) >= SoftCeilingNudgeRatingThreshold &&
            player.Form.HasSustainedMinutes(SoftCeilingNudgeFormWindow, SoftCeilingNudgeMinimumMinutes))
        {
            for (int i = 0; i < SkillAndMindAttributes.Length; i++)
            {
                if (ceilingNudgesApplied >= MaximumCeilingNudgesPerWeek) break;

                AttributeId id = SkillAndMindAttributes[i];
                if (attributes.Current(id) < attributes.Ceiling(id) - SoftCeilingNudgeCurrentWindow) continue;

                float available = player.AvailableCeilingNudge(id);
                if (available <= 0f) continue;

                if (rng.NextFloat() >= SoftCeilingNudgeChance) continue;

                float requested = rng.NextRange(SoftCeilingNudgeMinimumAmount, SoftCeilingNudgeMaximumAmount);
                float amount = MathF.Min(
                    requested,
                    MathF.Min(available, AttributeMetadata.MaximumCeiling - attributes.Ceiling(id)));
                if (amount <= 0f) continue;

                attributes.SetCeiling(id, attributes.Ceiling(id) + amount);
                player.RecordCeilingNudge(id, amount);
                ceilingNudgesApplied++;
            }
        }

        // 7. Condition: match load spends it, recovery work and injury restore it. An injured player carries
        //    no load at all (they did not play or train fully), so rehab replaces the load term.
        float conditionBefore = player.Condition01;
        float conditionDelta = injured
            ? InjuredConditionRecovery
            : BaseConditionGain - MatchLoadConditionCost * (matchMinutesThisWeek / 90f);
        if (focus == TrainingFocus.RecoveryAndLight) conditionDelta += RecoveryConditionGain;
        player.Condition01 = conditionBefore + conditionDelta;
        float conditionApplied = player.Condition01 - conditionBefore;

        // 8. Sharpness: minutes build it, idleness rusts it. Recovery work adds a light technical reminder.
        float sharpnessBefore = player.MatchSharpness01;
        float sharpnessDelta = matchMinutesThisWeek switch
        {
            0 => NoMinutesSharpnessLoss,
            <= ShortCameoUpperMinutes => ShortCameoSharpnessLoss,
            <= PartialMatchUpperMinutes => PartialMatchSharpnessGain,
            _ => FullMatchSharpnessGain
        };
        if (focus == TrainingFocus.RecoveryAndLight) sharpnessDelta += RecoverySharpnessGain;
        player.MatchSharpness01 = sharpnessBefore + sharpnessDelta;
        float sharpnessApplied = player.MatchSharpness01 - sharpnessBefore;

        // 9. Exactly one trend snapshot per week, after every mutation, so the UI arrows reflect the week.
        player.Trends.Record(attributes);

        // 10. Form01 is a view of the rolling appearance window: rebuild it whenever the caller has recorded
        //     appearances (the engine never records them itself, because it has no calendar context).
        if (player.Form.Appearances.Count > 0)
        {
            player.Form01 = player.Form.Form01(SoftCeilingNudgeFormWindow);
        }

        int attributesImproved = 0;
        for (int i = 0; i < AttributeMetadata.Count; i++)
        {
            if (attributes.Current((AttributeId)i) > weekStart[i]) attributesImproved++;
        }

        return new GrowthWeekResult(
            trainingGain,
            declineApplied,
            attributesImproved,
            ceilingNudgesApplied,
            conditionApplied,
            sharpnessApplied);
    }

    /// <summary>
    /// Applies the permanent ceiling damage of a severe injury (plan §4.5 §5). Only CEILINGS are shaved;
    /// current values follow only where a ceiling drops below them. The injury's
    /// <see cref="InjuryState.PermanentDamageApplied"/> ledger makes a repeated call for the same injury a
    /// no-op, so W3's tension/severe-injury rolls can call this exactly once per injury without double damage.
    /// </summary>
    /// <param name="player">The player to mutate.</param>
    /// <param name="kind">The injury that caused the damage.</param>
    /// <param name="rng">The world injuries random stream (draw order: attributes in the table below, each one draw).</param>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> or <paramref name="rng"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined injury kind.</exception>
    public static void ApplyPermanentInjuryDamage(WorldPlayer player, InjuryKind kind, WorldRng rng)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(rng);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown injury kind.");
        }

        // Concussions, illnesses and the minor strains heal clean; None is the no-injury sentinel.
        if (!HasPermanentDamage(kind)) return;

        // Never twice for one injury: the state resets the ledger when a new injury begins.
        if (player.Injury.PermanentDamageApplied > 0) return;

        PlayerAttributeSet attributes = player.Attributes;
        switch (kind)
        {
            case InjuryKind.KneeLigament:
                ShaveCeiling(attributes, AttributeId.TopSpeed, 3.0f, 6.0f, rng);
                ShaveCeiling(attributes, AttributeId.Acceleration, 2.5f, 5.0f, rng);
                ShaveCeiling(attributes, AttributeId.Agility, 2.0f, 4.0f, rng);
                break;

            case InjuryKind.HamstringTear:
                ShaveCeiling(attributes, AttributeId.TopSpeed, 1.5f, 3.5f, rng);
                ShaveCeiling(attributes, AttributeId.Acceleration, 1.5f, 3.0f, rng);
                break;

            case InjuryKind.AnkleLigament:
                ShaveCeiling(attributes, AttributeId.Agility, 1.5f, 3.5f, rng);
                ShaveCeiling(attributes, AttributeId.Balance, 1.0f, 2.5f, rng);
                break;

            case InjuryKind.FootFracture:
                ShaveCeiling(attributes, AttributeId.Acceleration, 1.5f, 3.0f, rng);
                ShaveCeiling(attributes, AttributeId.Agility, 1.0f, 2.5f, rng);
                break;

            case InjuryKind.BackInjury:
                ShaveCeiling(attributes, AttributeId.Balance, 1.0f, 2.5f, rng);
                ShaveCeiling(attributes, AttributeId.Strength, 1.0f, 2.0f, rng);
                break;
        }

        player.Injury.MarkPermanentDamageApplied();
    }

    /// <summary>
    /// The age multiplier that scales weekly training gain for one attribute category (plan §4.5 §1).
    /// Innate growth windows close first, body growth peaks slightly later, and skill-and-mind has the longest
    /// runway. Ages below 16 use the youngest band. Decline is not modelled here; it is
    /// <see cref="WeeklyDeclinePerWeek"/>'s job.
    /// </summary>
    /// <param name="age">Player age in years.</param>
    /// <param name="category">Attribute category.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="category"/> is not a defined category.</exception>
    public static float AgeCurveMultiplier(int age, AttributeCategory category) => category switch
    {
        AttributeCategory.Innate => age <= 20 ? 0.25f : 0.0f,
        AttributeCategory.BodyTrainable => age <= 22 ? 1.15f : age <= 26 ? 0.65f : 0.0f,
        AttributeCategory.SkillAndMind => age <= 23 ? 1.20f : age <= 28 ? 0.75f : age <= 33 ? 0.30f : 0.0f,
        _ => throw new ArgumentOutOfRangeException(
            nameof(category), category, "Unknown attribute category.")
    };

    /// <summary>
    /// The soft-ceiling slowdown factor (plan §4.5 §2) for a current-to-ceiling gap: 0 when the gap is
    /// non-positive, otherwise <c>Clamp(gap / 18, 0.08, 1)</c> so progress smoothly decelerates instead of
    /// hitting an abrupt wall while never stopping entirely before the cap.
    /// </summary>
    /// <param name="gap">Ceiling minus current for one attribute.</param>
    public static float SoftCeilingFactor(float gap) =>
        gap <= 0f ? 0f : Math.Clamp(gap / SoftCeilingReferenceGap, SoftCeilingMinimumFactor, 1f);

    /// <summary>
    /// Points of CURRENT value one attribute loses per week to age, as a non-negative number (plan §4.5 §1).
    ///
    /// <para><b>Onset choice.</b> The thresholds are the CATEGORY onsets from the plan (Innate 28, body 30,
    /// skill-and-mind 34), not each attribute's own <see cref="AttributeDefinition.DeclineOnsetAge"/> value.
    /// The per-attribute field is documentation of the plan's finer-grained roster table; using it here would
    /// mean two thresholds per category in the simulation and would contradict the plan's age-curve bands.</para>
    /// </summary>
    /// <param name="age">Player age in years.</param>
    /// <param name="attribute">The attribute being aged.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attribute"/> is not a defined attribute.</exception>
    public static float WeeklyDeclinePerWeek(int age, AttributeId attribute)
    {
        if (!Enum.IsDefined(attribute))
        {
            throw new ArgumentOutOfRangeException(nameof(attribute), attribute, "Unknown attribute.");
        }

        return AttributeMetadata.Get(attribute).Category switch
        {
            AttributeCategory.Innate => age < InnateDeclineOnsetAge
                ? 0f
                : MathF.Min(
                    InnateDeclineBase + InnateDeclinePerYear * (age - InnateDeclineOnsetAge),
                    InnateDeclineCap),

            AttributeCategory.BodyTrainable => age < BodyTrainableDeclineOnsetAge
                ? 0f
                : MathF.Min(
                    BodyDeclineBase + BodyDeclinePerYear * (age - BodyTrainableDeclineOnsetAge),
                    BodyDeclineCap),

            AttributeCategory.SkillAndMind => age < SkillAndMindDeclineOnsetAge
                ? 0f
                : MathF.Min(
                    SkillDeclineBase + SkillDeclinePerYear * (age - SkillAndMindDeclineOnsetAge),
                    SkillDeclineCap),

            _ => 0f
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Factor tables.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Unmultiplied weekly base gain for one category.</summary>
    private static float BaseGain(AttributeCategory category) => category switch
    {
        AttributeCategory.Innate => InnateBaseGain,
        AttributeCategory.BodyTrainable => BodyTrainableBaseGain,
        AttributeCategory.SkillAndMind => SkillAndMindBaseGain,
        _ => throw new ArgumentOutOfRangeException(
            nameof(category), category, "Unknown attribute category.")
    };

    /// <summary>Skill-and-mind match-minute acceleration; body and innate attributes are never minute-scaled.</summary>
    private static float MatchMinutesMultiplier(int minutes) => minutes switch
    {
        0 => NoMinutesSkillGainMultiplier,
        <= ShortCameoUpperMinutes => FringeSkillGainMultiplier,
        <= PartialMatchUpperMinutes => ShortCameoSkillGainMultiplier,
        _ => FullMatchSkillGainMultiplier
    };

    /// <summary>Training-focus preset multiplier for one attribute (plan §4.5 §3).</summary>
    private static float FocusMultiplier(TrainingFocus focus, AttributeId id) => focus switch
    {
        TrainingFocus.Balanced => 1.00f,

        TrainingFocus.RecoveryAndLight => 0.20f,

        TrainingFocus.StrengthAndPower => id switch
        {
            AttributeId.Strength => 1.60f,
            AttributeId.Balance => 1.20f,
            _ => 1.00f
        },

        TrainingFocus.EnduranceAndEngine => id switch
        {
            AttributeId.Stamina => 1.60f,
            AttributeId.Strength => 0.70f,
            _ => 1.00f
        },

        TrainingFocus.AgilityAndQuickness => id switch
        {
            AttributeId.Agility => 1.60f,
            AttributeId.Balance => 1.20f,
            _ => 1.00f
        },

        TrainingFocus.FinishingAndShooting => id switch
        {
            AttributeId.Finishing => 1.60f,
            AttributeId.ShotPower => 1.20f,
            _ => 1.00f
        },

        TrainingFocus.PassingAndVision => id switch
        {
            AttributeId.Passing => 1.60f,
            AttributeId.Vision => 1.20f,
            _ => 1.00f
        },

        TrainingFocus.BallControlAndDribbling => id switch
        {
            AttributeId.Dribbling => 1.60f,
            AttributeId.FirstTouch => 1.20f,
            _ => 1.00f
        },

        TrainingFocus.DefendingAndPositioning => id switch
        {
            AttributeId.Tackling => 1.60f,
            AttributeId.Positioning => 1.20f,
            _ => 1.00f
        },

        TrainingFocus.Goalkeeping => id switch
        {
            AttributeId.Goalkeeping => 1.60f,
            AttributeId.Positioning => 1.10f,
            AttributeId.Composure => 1.10f,
            _ => 1.00f
        },

        _ => throw new ArgumentOutOfRangeException(
            nameof(focus), focus, "Unknown training focus preset.")
    };

    // ---------------------------------------------------------------------------------------------
    // Permanent-injury helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>True for the injury kinds that leave permanent ceiling damage.</summary>
    private static bool HasPermanentDamage(InjuryKind kind) => kind is
        InjuryKind.KneeLigament or InjuryKind.HamstringTear or InjuryKind.AnkleLigament or
        InjuryKind.FootFracture or InjuryKind.BackInjury;

    /// <summary>Rolls one shave amount in the inclusive range and subtracts it from the attribute's ceiling.</summary>
    private static void ShaveCeiling(
        PlayerAttributeSet attributes, AttributeId id, float minimum, float maximum, WorldRng rng)
    {
        attributes.AdjustCeiling(id, -rng.NextRange(minimum, maximum));
    }

    /// <summary>Physical (innate + body-trainable) attributes in roster order, cached for the aging pass.</summary>
    private static AttributeId[] BuildPhysicalAttributes()
    {
        var ids = new List<AttributeId>(AttributeMetadata.Count);
        foreach (AttributeId id in AttributeMetadata.All)
        {
            if (AttributeMetadata.Get(id).Category != AttributeCategory.SkillAndMind) ids.Add(id);
        }

        return ids.ToArray();
    }

    // ---------------------------------------------------------------------------------------------
    // Validation.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Validates the club environment for a week of growth.</summary>
    private static void ValidateEnvironment(in TrainingEnvironment environment)
    {
        // ForTier also performs the tier-range check (it throws for tier 0 and 7).
        _ = TrainingEnvironment.ForTier(environment.Tier);

        if (environment.WeeklySessions is < 0 or > MaximumWeeklySessions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(environment),
                environment.WeeklySessions,
                $"Weekly training sessions must be between 0 and {MaximumWeeklySessions}.");
        }

        if (!float.IsFinite(environment.CoachingQuality01))
        {
            throw new ArgumentOutOfRangeException(
                nameof(environment), environment.CoachingQuality01, "Coaching quality must be finite.");
        }

        // Plan §4.5: a club's coaching quality lives in 0.70..1.30. The tier's own multiplier is applied
        // separately, so the band is the same for every division.
        if (environment.CoachingQuality01 is < MinimumCoachingQuality or > MaximumCoachingQuality)
        {
            throw new ArgumentOutOfRangeException(
                nameof(environment),
                environment.CoachingQuality01,
                $"Coaching quality must be between {MinimumCoachingQuality} and {MaximumCoachingQuality}.");
        }
    }

    /// <summary>Validates the focus preset.</summary>
    private static void ValidateFocus(TrainingFocus focus)
    {
        if (!Enum.IsDefined(focus))
        {
            throw new ArgumentOutOfRangeException(
                nameof(focus), focus, "Unknown training focus preset.");
        }
    }

    /// <summary>Validates minutes and rating for the match week.</summary>
    private static void ValidateMatchWeek(int matchMinutesThisWeek, float averageMatchRatingThisWeek)
    {
        if (matchMinutesThisWeek is < 0 or > MaximumMatchMinutesPerWeek)
        {
            throw new ArgumentOutOfRangeException(
                nameof(matchMinutesThisWeek),
                matchMinutesThisWeek,
                $"Match minutes must be between 0 and {MaximumMatchMinutesPerWeek}.");
        }

        if (!float.IsFinite(averageMatchRatingThisWeek) ||
            averageMatchRatingThisWeek is < 0f or > 10f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(averageMatchRatingThisWeek),
                averageMatchRatingThisWeek,
                "Match rating must be between 0 and 10.");
        }

        // A player who actually played must carry a real 4.0..10.0 match rating; 0.0 is the no-appearance value.
        if (matchMinutesThisWeek > 0 && averageMatchRatingThisWeek < 4f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(averageMatchRatingThisWeek),
                averageMatchRatingThisWeek,
                "A player with match minutes needs a rating between 4.0 and 10.0.");
        }
    }
}
