using FootballSim.Core;
using FootballSim.World.Core;

namespace FootballSim.World.Players;

/// <summary>
/// Deterministic creation of <see cref="WorldPlayer"/>s for the two creation modes (plan §4.1) and for
/// squad-filling NPCs (plan W1).
///
/// <para><b>Draw order is part of the save format.</b> Every authentic and custom player derives its
/// randomness from <c>WorldRng.For(seed, WorldRandomStream.Talent)</c> and consumes it in a fixed order:
/// talent anchor (authentic only), latent aptitudes (authentic only), all eighteen ceilings, all eighteen
/// plasticities, footedness/weak foot/birth week, personality, then match sharpness. Changing that order —
/// or inserting a draw before the ceilings — invalidates every previously generated player, so it must not
/// be touched.</para>
///
/// <para><b>Hard invariant (plan §4.4):</b> the talent anchor, latents, ceilings and plasticities are all
/// rolled before the backstory is read. The backstory therefore only changes starting realization, domain
/// tilts, personality and visibility; switching <see cref="BackstoryKind"/> for the same seed leaves every
/// ceiling bit-for-bit identical (gate-tested).</para>
///
/// <para><b>Custom mode</b> skips the anchor/aptitude rolls entirely (consuming no draws), so its ceilings
/// are a pure function of the supplied aptitudes plus the same per-seed ceiling noise.</para>
///
/// <para><b>NPC mode</b> replaces step 1 with a tier-targeted anchor and then runs the identical
/// ceiling/realization pipeline, so a Tier-6 NPC still occasionally rolls a genuinely high ceiling: the
/// hidden-gem gate is deliberately still active in the amateur tiers.</para>
/// </summary>
public static class PlayerGenerator
{
    // ---------------------------------------------------------------------------------------------
    // Supported generation ranges (the WorldPlayer entity itself is wider; the generator is stricter).
    // ---------------------------------------------------------------------------------------------

    /// <summary>Smallest starting age accepted by the generator.</summary>
    private const int MinimumAge = 15;

    /// <summary>Largest starting age accepted by the generator.</summary>
    private const int MaximumAge = 38;

    private const int MinimumTier = 1;
    private const int MaximumTier = 6;

    private const float MinimumTalentAnchor = 25f;
    private const float MaximumTalentAnchor = 97f;

    private const float MinimumLatentAptitude = 20f;
    private const float MaximumLatentAptitude = 98f;
    private const float LatentSpreadStdDev = 9f;
    private const float GoalkeepingLatentStdDev = 6f;

    /// <summary>Mean of the low outfield goalkeeper-aptitude roll; most outfielders are poor in goal.</summary>
    private const float OutfieldGoalkeepingMean = 30f;
    private const float OutfieldGoalkeepingStdDev = 10f;
    private const float OutfieldGoalkeepingMinimum = 5f;
    private const float OutfieldGoalkeepingMaximum = 60f;

    private const float LeftFootedProbability = 0.23f;
    private const float WeakFootMinimum = 0.25f;
    private const float WeakFootMaximum = 0.85f;
    private const int BirthWeekMinimumInclusive = 1;
    private const int BirthWeekMaximumExclusive = 53;

    /// <summary>Visibility of a baseline (no backstory exposure) player; multipliers scale this.</summary>
    private const float BaseVisibility = 0.30f;

    private const float PersonalityNoiseStdDev = 6f;

    private const float MinimumStartingCurrent = 15f;
    private const float MinimumRealizationRatio = 0.30f;
    private const float MaximumRealizationRatio = 0.98f;

    private const float MinimumMatchSharpness = 0.55f;
    private const float MatchSharpnessSpread = 0.15f;

    /// <summary>Current tilt an academy keeper gets on top of the positional work (Current only).</summary>
    private const float GoalkeeperCurrentTilt = 4f;

    /// <summary>Current tilt an academy keeper gets on positioning (Current only).</summary>
    private const float GoalkeeperPositioningTilt = 2f;

    // ---------------------------------------------------------------------------------------------
    // NPC tier targeting (plan W1: squads must match their division band, but gems stay possible low).
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// NPC talent anchor per tier, indexed by tier. The plan's target mean overall: Tier 1 ≈ 80 down to
    /// Tier 6 ≈ 38. Index 0 is unused so the array reads naturally as <c>[tier]</c>.
    /// </summary>
    private static readonly float[] NpcTierAnchor = { 0f, 80f, 70f, 62f, 54f, 46f, 38f };

    private const float NpcTierAnchorStdDev = 7f;
    private const float NpcHiddenGemProbability = 0.07f;
    private const float NpcHiddenGemBonusMinimum = 10f;
    private const float NpcHiddenGemBonusMaximum = 20f;
    private const float NpcBustProbability = 0.07f;
    private const float NpcBustPenaltyMinimum = 8f;
    private const float NpcBustPenaltyMaximum = 15f;

    /// <summary>
    /// Fixed tier-weighted backstory probabilities for NPCs. Row <c>tier - 1</c>, column order matches
    /// <see cref="BackstoryKind"/> ordinals: EliteAcademy, RegionalAcademy, LocalClubYouth, StreetFootball,
    /// MultisportLateStarter. Every row sums to 1. Top-flight squads are overwhelmingly academy products;
    /// amateur squads are overwhelmingly local-club and street footballers (plan §4.4 social pyramid).
    /// </summary>
    private static readonly float[][] NpcBackstoryWeights =
    {
        new[] { 0.55f, 0.30f, 0.10f, 0.03f, 0.02f }, // Tier 1
        new[] { 0.40f, 0.35f, 0.15f, 0.05f, 0.05f }, // Tier 2
        new[] { 0.22f, 0.34f, 0.25f, 0.10f, 0.09f }, // Tier 3
        new[] { 0.10f, 0.25f, 0.35f, 0.16f, 0.14f }, // Tier 4
        new[] { 0.04f, 0.16f, 0.42f, 0.24f, 0.14f }, // Tier 5
        new[] { 0.02f, 0.10f, 0.45f, 0.30f, 0.13f }  // Tier 6
    };

    /// <summary>Backstory presets, indexed by <see cref="BackstoryKind"/> ordinal.</summary>
    private static readonly BackstoryProfile[] BackstoryProfiles = CreateBackstoryProfiles();

    // ---------------------------------------------------------------------------------------------
    // Public factory methods.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Generates a career player in Authentic mode: the hidden talent is rolled from the population pyramid
    /// and the backstory only decorates how that talent has been realized so far.
    /// </summary>
    /// <param name="seed">Generation seed. The same seed with the same arguments always yields the same player.</param>
    /// <param name="age">Starting age, 15..38 inclusive.</param>
    /// <param name="backstory">Where the player came from. Never changes a ceiling or a plasticity.</param>
    /// <param name="preferGoalkeeper">Rolls the high goalkeeper aptitude and tilts the starting keeper skills.</param>
    /// <param name="id">Identity assigned to the player (does not influence any generated value).</param>
    /// <param name="firstName">Given name (does not influence any generated value; may be empty).</param>
    /// <param name="lastName">Family name (does not influence any generated value; may be empty).</param>
    /// <param name="preferredFootOverride">
    /// Optional strong-foot override for the creation wizard (W4 career). The foot is still rolled first and
    /// then replaced, so the draw order — and therefore every other generated value — is unchanged.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="age"/> is outside 15..38, or <paramref name="backstory"/> is not a defined value.
    /// </exception>
    public static WorldPlayer GenerateAuthentic(
        ulong seed,
        int age,
        BackstoryKind backstory,
        bool preferGoalkeeper = false,
        PlayerId id = default,
        string firstName = "",
        string lastName = "",
        Footedness? preferredFootOverride = null)
    {
        ValidateAge(age);
        ValidateBackstory(backstory);

        WorldRng rng = WorldRng.For(seed, WorldRandomStream.Talent);

        // Steps 1-2: the population pyramid, then the four latent aptitudes derived from it.
        float talentAnchor = RollAuthenticTalentAnchor(rng);
        LatentAptitudes aptitudes = RollLatentAptitudes(rng, talentAnchor, preferGoalkeeper);

        return BuildPlayer(
            rng, aptitudes, age, backstory, preferGoalkeeper, id, firstName, lastName, preferredFootOverride);
    }

    /// <summary>
    /// Generates a career player in Custom mode: the caller supplies the latent aptitudes directly and the
    /// ceilings are derived from them through the exact same formula as Authentic mode. The backstory then
    /// affects only realization, domain tilts, personality and visibility.
    /// </summary>
    /// <param name="seed">Generation seed.</param>
    /// <param name="age">Starting age, 15..38 inclusive.</param>
    /// <param name="customAptitudes">Caller-chosen aptitudes; they are clamped into 0..100 before use.</param>
    /// <param name="backstory">Where the player came from. Never changes a ceiling or a plasticity.</param>
    /// <param name="preferGoalkeeper">Rolls the starting keeper skill tilt (the goalkeeper aptitude is the caller's).</param>
    /// <param name="id">Identity assigned to the player (does not influence any generated value).</param>
    /// <param name="firstName">Given name (may be empty).</param>
    /// <param name="lastName">Family name (may be empty).</param>
    /// <param name="preferredFootOverride">
    /// Optional strong-foot override for the creation wizard (W4 career). The foot is still rolled first and
    /// then replaced, so the draw order — and therefore every other generated value — is unchanged.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="age"/> is outside 15..38, or <paramref name="backstory"/> is not a defined value.
    /// </exception>
    public static WorldPlayer GenerateCustom(
        ulong seed,
        int age,
        LatentAptitudes customAptitudes,
        BackstoryKind backstory,
        bool preferGoalkeeper = false,
        PlayerId id = default,
        string firstName = "",
        string lastName = "",
        Footedness? preferredFootOverride = null)
    {
        ValidateAge(age);
        ValidateBackstory(backstory);

        // Custom mode consumes no talent-anchor or aptitude draws: the stream starts with the first
        // ceiling, so the ceilings depend only on the supplied aptitudes plus per-seed ceiling noise.
        WorldRng rng = WorldRng.For(seed, WorldRandomStream.Talent);
        LatentAptitudes aptitudes = customAptitudes.Clamped();

        return BuildPlayer(
            rng, aptitudes, age, backstory, preferGoalkeeper, id, firstName, lastName, preferredFootOverride);
    }

    /// <summary>
    /// Generates a squad-filling NPC tuned to a division tier: the talent anchor is centred on the tier's
    /// target strength, with small hidden-gem and bust gates. The backstory is picked deterministically from
    /// tier-weighted probabilities and, as always, never changes a ceiling.
    /// </summary>
    /// <param name="seed">Generation seed.</param>
    /// <param name="tier">Division tier, 1 (top flight) .. 6 (amateur).</param>
    /// <param name="age">Starting age, 15..38 inclusive.</param>
    /// <param name="targetRole">The role the NPC is being generated to fill; only goalkeeper changes the latents.</param>
    /// <param name="id">Identity assigned to the player (does not influence any generated value).</param>
    /// <param name="firstName">Given name (may be empty).</param>
    /// <param name="lastName">Family name (may be empty).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="tier"/> is outside 1..6 or <paramref name="age"/> is outside 15..38.
    /// </exception>
    public static WorldPlayer GenerateNpcForTier(
        ulong seed,
        int tier,
        int age,
        TacticalRole targetRole,
        PlayerId id = default,
        string firstName = "",
        string lastName = "")
    {
        ValidateAge(age);
        ValidateTier(tier);

        bool preferGoalkeeper = targetRole == TacticalRole.Goalkeeper;
        WorldRng rng = WorldRng.For(seed, WorldRandomStream.Talent);

        // Step 1 (NPC variant): tier target + hidden gem / bust gates; step 2 is the shared aptitude roll.
        float talentAnchor = RollNpcTalentAnchor(rng, tier);
        LatentAptitudes aptitudes = RollLatentAptitudes(rng, talentAnchor, preferGoalkeeper);

        // Picked from a separate identity stream, so the choice consumes no talent draws at all.
        BackstoryKind backstory = PickNpcBackstory(seed, tier);

        return BuildPlayer(
            rng, aptitudes, age, backstory, preferGoalkeeper, id, firstName, lastName, preferredFootOverride: null);
    }

    // ---------------------------------------------------------------------------------------------
    // Steps 1-2: talent anchor and latent aptitudes.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Step 1 of Authentic generation: the right-skewed population-pyramid mixture (plan §4.3). The
    /// category draw comes first, then exactly one normal draw for the chosen band.
    /// </summary>
    private static float RollAuthenticTalentAnchor(WorldRng rng)
    {
        float roll = rng.NextFloat();
        float anchor;
        if (roll < 0.55f) anchor = rng.NextGaussian(48f, 8f);          // Amateur / semi-pro ceiling
        else if (roll < 0.85f) anchor = rng.NextGaussian(65f, 7f);     // Lower-pro / solid pyramid
        else if (roll < 0.97f) anchor = rng.NextGaussian(78f, 6f);     // Top-flight ceiling
        else anchor = rng.NextGaussian(89f, 4f);                       // Star / generational

        return Math.Clamp(anchor, MinimumTalentAnchor, MaximumTalentAnchor);
    }

    /// <summary>
    /// Step 1 of NPC generation: a tier-centred anchor with a 7% hidden-gem bonus and a 7% bust penalty.
    /// Draw order is: base anchor, gem gate (plus bonus when it hits), bust gate (minus penalty when it
    /// hits). Gems remain possible in Tiers 5-6 by design; the tier only moves the centre.
    /// </summary>
    private static float RollNpcTalentAnchor(WorldRng rng, int tier)
    {
        float anchor = rng.NextGaussian(NpcTierAnchor[tier], NpcTierAnchorStdDev);

        if (rng.NextBool(NpcHiddenGemProbability))
        {
            anchor += rng.NextRange(NpcHiddenGemBonusMinimum, NpcHiddenGemBonusMaximum);
        }

        if (rng.NextBool(NpcBustProbability))
        {
            anchor -= rng.NextRange(NpcBustPenaltyMinimum, NpcBustPenaltyMaximum);
        }

        return Math.Clamp(anchor, MinimumTalentAnchor, MaximumTalentAnchor);
    }

    /// <summary>
    /// Step 2: four independent aptitude offsets around the talent anchor. Goalkeeping is rolled high for
    /// keepers and from a low outfield distribution otherwise. Draw order is Athleticism, Technical,
    /// Mental, Goalkeeping.
    /// </summary>
    private static LatentAptitudes RollLatentAptitudes(WorldRng rng, float talentAnchor, bool preferGoalkeeper)
    {
        float athleticism = Math.Clamp(
            talentAnchor + rng.NextGaussian(0f, LatentSpreadStdDev),
            MinimumLatentAptitude, MaximumLatentAptitude);
        float technical = Math.Clamp(
            talentAnchor + rng.NextGaussian(0f, LatentSpreadStdDev),
            MinimumLatentAptitude, MaximumLatentAptitude);
        float mental = Math.Clamp(
            talentAnchor + rng.NextGaussian(0f, LatentSpreadStdDev),
            MinimumLatentAptitude, MaximumLatentAptitude);
        float goalkeeping = preferGoalkeeper
            ? Math.Clamp(
                talentAnchor + rng.NextGaussian(0f, GoalkeepingLatentStdDev),
                MinimumLatentAptitude, MaximumLatentAptitude)
            : Math.Clamp(
                rng.NextGaussian(OutfieldGoalkeepingMean, OutfieldGoalkeepingStdDev),
                OutfieldGoalkeepingMinimum, OutfieldGoalkeepingMaximum);

        return new LatentAptitudes(athleticism, technical, mental, goalkeeping);
    }

    // ---------------------------------------------------------------------------------------------
    // Steps 3-10: the shared realization pipeline.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs steps 3-10 for an already-rolled set of latent aptitudes. The stream must be positioned exactly
    /// after step 2 (or at its start in Custom mode).
    /// </summary>
    private static WorldPlayer BuildPlayer(
        WorldRng rng,
        in LatentAptitudes aptitudes,
        int age,
        BackstoryKind backstory,
        bool preferGoalkeeper,
        PlayerId id,
        string firstName,
        string lastName,
        Footedness? preferredFootOverride)
    {
        // Steps 3-4: ceilings then plasticities, before any backstory value is read.
        PlayerAttributeSet attributes = RollAttributes(rng, aptitudes);

        // Step 5: flavour rolls that still belong to the pre-backstory, ceiling-stable prefix of the stream.
        Footedness preferredFoot =
            rng.NextFloat() < LeftFootedProbability ? Footedness.Left : Footedness.Right;
        float weakFoot01 = rng.NextRange(WeakFootMinimum, WeakFootMaximum);
        int birthWeek = rng.NextInt(BirthWeekMinimumInclusive, BirthWeekMaximumExclusive);

        // The creation wizard may force a foot; the roll above is kept so the stream never moves.
        if (preferredFootOverride is { } forcedFoot) preferredFoot = forcedFoot;

        // Step 6: only now read the backstory. It never changes a ceiling or a plasticity.
        BackstoryProfile profile = BackstoryProfiles[(int)backstory];

        // Steps 7-8: age runway + backstory realization offsets + domain tilts -> starting Current.
        ApplyStartingCurrent(attributes, age, profile, preferGoalkeeper);

        // Step 9: personality draws deliberately come after every ceiling draw.
        PersonalityTraits personality = RollPersonality(rng, profile.PersonalityDelta);

        WorldPlayer player = new WorldPlayer(
            id,
            firstName,
            lastName,
            age,
            birthWeek,
            preferredFoot,
            weakFoot01,
            attributes,
            aptitudes,
            personality,
            backstory);

        // Step 10: the rest of the fresh-player defaults.
        player.Visibility01 = Math.Clamp(BaseVisibility * profile.VisibilityMultiplier, 0f, 1f);
        player.Condition01 = 1f;
        player.MatchSharpness01 = MinimumMatchSharpness + rng.NextRange(0f, MatchSharpnessSpread);
        player.Form01 = 0.5f;
        player.TrainingFocus = TrainingFocus.Balanced;
        player.ClubId = ClubId.None;
        player.CurrentSeason.Reset(0);
        return player;
    }

    /// <summary>
    /// Steps 3-4: every ceiling (roster order), then every plasticity (roster order). Ceilings are a latent
    /// blend plus independent noise, except <see cref="AttributeId.NaturalReach"/>, which is a body-frame
    /// roll only weakly correlated with athleticism.
    /// </summary>
    private static PlayerAttributeSet RollAttributes(WorldRng rng, in LatentAptitudes aptitudes)
    {
        IReadOnlyList<AttributeDefinition> definitions = AttributeMetadata.AllDefinitions;
        float[] ceilings = new float[definitions.Count];
        float[] plasticities = new float[definitions.Count];

        // Step 3: all ceilings.
        for (int i = 0; i < definitions.Count; i++)
        {
            AttributeDefinition definition = definitions[i];
            float ceiling;
            if (definition.IsBodyFrameRoll)
            {
                ceiling = AttributeMetadata.NaturalReachPopulationMean +
                    AttributeMetadata.NaturalReachAthleticismWeight * (aptitudes.Athleticism - 50f) +
                    rng.NextGaussian(0f, AttributeMetadata.NaturalReachPopulationStdDev);
            }
            else
            {
                ceiling = definition.Weights.Blend(aptitudes) +
                    rng.NextGaussian(0f, definition.CeilingNoiseStdDev);
            }

            ceilings[(int)definition.Id] = Math.Clamp(
                ceiling, AttributeMetadata.MinimumCeiling, AttributeMetadata.MaximumCeiling);
        }

        // Step 4: all plasticities.
        for (int i = 0; i < definitions.Count; i++)
        {
            AttributeDefinition definition = definitions[i];
            plasticities[(int)definition.Id] =
                rng.NextRange(definition.PlasticityMin, definition.PlasticityMax);
        }

        PlayerAttributeState[] states = new PlayerAttributeState[AttributeMetadata.Count];
        for (int i = 0; i < states.Length; i++)
        {
            // Current starts at the floor; ApplyStartingCurrent fills it in from the age runway.
            states[i] = new PlayerAttributeState(
                AttributeMetadata.MinimumValue, ceilings[i], plasticities[i]);
        }

        return new PlayerAttributeSet(states);
    }

    /// <summary>
    /// Steps 7-8: age realization ratio by category, shifted by the backstory offsets and clamped, then
    /// multiplied by the ceiling and adjusted by the domain tilts (attribute points, may be negative).
    /// A goalkeeper also gets the academy-position tilts on the two keeper skills.
    /// </summary>
    private static void ApplyStartingCurrent(
        PlayerAttributeSet attributes, int age, BackstoryProfile profile, bool preferGoalkeeper)
    {
        IReadOnlyList<AttributeDefinition> definitions = AttributeMetadata.AllDefinitions;
        for (int i = 0; i < definitions.Count; i++)
        {
            AttributeDefinition definition = definitions[i];
            float realization = RealizationRatio(definition.Category, age) +
                profile.OffsetFor(definition.Category);
            float current = attributes.Ceiling(definition.Id) *
                Math.Clamp(realization, MinimumRealizationRatio, MaximumRealizationRatio);
            current += profile.TiltFor(definition.Id);

            if (preferGoalkeeper)
            {
                if (definition.Id == AttributeId.Goalkeeping) current += GoalkeeperCurrentTilt;
                else if (definition.Id == AttributeId.Positioning) current += GoalkeeperPositioningTilt;
            }

            attributes.SetCurrent(
                definition.Id,
                Math.Clamp(current, MinimumStartingCurrent, attributes.Ceiling(definition.Id)));
        }
    }

    /// <summary>
    /// Step 7: the plan §4.4 age runway. Linear between the two anchors, clamped to the first anchor below
    /// it and to the last anchor above it (speed shows early, the body fills out, skill has the longest
    /// runway).
    /// </summary>
    private static float RealizationRatio(AttributeCategory category, int age) => category switch
    {
        AttributeCategory.Innate => InterpolateAnchor(0.86f, 0.97f, 16, 21, age),
        AttributeCategory.BodyTrainable => InterpolateAnchor(0.58f, 0.84f, 16, 22, age),
        AttributeCategory.SkillAndMind => InterpolateAnchor(0.48f, 0.78f, 16, 22, age),
        _ => throw new ArgumentOutOfRangeException(
            nameof(category), category, "Unknown attribute category.")
    };

    /// <summary>Linear interpolation between two (age, ratio) anchors, clamped outside the anchor range.</summary>
    private static float InterpolateAnchor(
        float startRatio, float endRatio, int startAge, int endAge, int age)
    {
        if (age <= startAge) return startRatio;
        if (age >= endAge) return endRatio;
        float progress = (age - startAge) / (float)(endAge - startAge);
        return startRatio + (endRatio - startRatio) * progress;
    }

    /// <summary>
    /// Step 9: neutral personality plus the backstory deltas plus independent noise, in record order
    /// (Resilience, WorkEthic, Creativity, Discipline), clamped to 0..100.
    /// </summary>
    private static PersonalityTraits RollPersonality(WorldRng rng, in PersonalityTraits delta)
    {
        PersonalityTraits neutral = PersonalityTraits.Neutral;
        float resilience = neutral.Resilience + delta.Resilience +
            rng.NextGaussian(0f, PersonalityNoiseStdDev);
        float workEthic = neutral.WorkEthic + delta.WorkEthic +
            rng.NextGaussian(0f, PersonalityNoiseStdDev);
        float creativity = neutral.Creativity + delta.Creativity +
            rng.NextGaussian(0f, PersonalityNoiseStdDev);
        float discipline = neutral.Discipline + delta.Discipline +
            rng.NextGaussian(0f, PersonalityNoiseStdDev);

        return new PersonalityTraits(resilience, workEthic, creativity, discipline).Clamped();
    }

    // ---------------------------------------------------------------------------------------------
    // NPC backstory selection.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Picks the NPC's backstory from the tier-weighted table. The roll comes from the world-generation
    /// stream rather than the talent stream: it is still fully determined by the seed, but choosing a
    /// backstory consumes zero draws from the pipeline that rolls ceilings.
    /// </summary>
    private static BackstoryKind PickNpcBackstory(ulong seed, int tier)
    {
        WorldRng identityRng = WorldRng.For(seed, WorldRandomStream.WorldGeneration);
        float roll = identityRng.NextFloat();
        float[] weights = NpcBackstoryWeights[tier - 1];
        float cumulative = 0f;

        for (int i = 0; i < weights.Length; i++)
        {
            cumulative += weights[i];
            if (roll < cumulative) return (BackstoryKind)i;
        }

        // Only reachable through float rounding of the last bucket; the last bucket owns the remainder.
        return (BackstoryKind)(weights.Length - 1);
    }

    // ---------------------------------------------------------------------------------------------
    // Backstory presets.
    // ---------------------------------------------------------------------------------------------

    /// <summary>All five backstory presets, built once (plan §4.4 table).</summary>
    private static BackstoryProfile[] CreateBackstoryProfiles()
    {
        BackstoryProfile[] profiles = new BackstoryProfile[Enum.GetValues<BackstoryKind>().Length];

        // High realization, technical/positioning tilt, excellent exposure, low resilience when benched.
        profiles[(int)BackstoryKind.EliteAcademy] = new BackstoryProfile(
            innateOffset: 0.02f,
            bodyTrainableOffset: 0.03f,
            skillAndMindOffset: 0.10f,
            visibilityMultiplier: 1.45f,
            personalityDelta: new PersonalityTraits(Resilience: -15f, WorkEthic: 5f, Creativity: 3f, Discipline: 8f),
            currentTilts: CreateTilts(
                (AttributeId.Passing, 5f),
                (AttributeId.Finishing, 5f),
                (AttributeId.FirstTouch, 5f),
                (AttributeId.Dribbling, 5f),
                (AttributeId.Positioning, 3f)));

        // Medium-high realization, balanced technical/mental tilt, good coaching and exposure.
        profiles[(int)BackstoryKind.RegionalAcademy] = new BackstoryProfile(
            innateOffset: 0.01f,
            bodyTrainableOffset: 0.02f,
            skillAndMindOffset: 0.05f,
            visibilityMultiplier: 1.15f,
            personalityDelta: new PersonalityTraits(Resilience: 3f, WorkEthic: 3f, Creativity: 0f, Discipline: 0f),
            currentTilts: CreateTilts(
                (AttributeId.Passing, 2f),
                (AttributeId.Finishing, 2f),
                (AttributeId.FirstTouch, 2f),
                (AttributeId.Dribbling, 2f),
                (AttributeId.Vision, 2f),
                (AttributeId.Composure, 2f),
                (AttributeId.Positioning, 2f),
                (AttributeId.OffTheBall, 2f)));

        // Baseline realization, modest stamina/work-ethic tilt, low visibility.
        profiles[(int)BackstoryKind.LocalClubYouth] = new BackstoryProfile(
            innateOffset: 0f,
            bodyTrainableOffset: 0f,
            skillAndMindOffset: 0f,
            visibilityMultiplier: 0.75f,
            personalityDelta: new PersonalityTraits(Resilience: 5f, WorkEthic: 8f, Creativity: 0f, Discipline: 0f),
            currentTilts: CreateTilts((AttributeId.Stamina, 2f)));

        // Low realization, dribbling/touch tilt, poor positioning, high resilience and creativity.
        profiles[(int)BackstoryKind.StreetFootball] = new BackstoryProfile(
            innateOffset: 0f,
            bodyTrainableOffset: -0.02f,
            skillAndMindOffset: -0.03f,
            visibilityMultiplier: 0.60f,
            personalityDelta: new PersonalityTraits(Resilience: 18f, WorkEthic: 0f, Creativity: 15f, Discipline: -12f),
            currentTilts: CreateTilts(
                (AttributeId.Dribbling, 5f),
                (AttributeId.FirstTouch, 5f),
                (AttributeId.Positioning, -5f)));

        // Low technical realization, body tilt, high work ethic and resilience.
        profiles[(int)BackstoryKind.MultisportLateStarter] = new BackstoryProfile(
            innateOffset: 0.01f,
            bodyTrainableOffset: 0.04f,
            skillAndMindOffset: -0.06f,
            visibilityMultiplier: 0.70f,
            personalityDelta: new PersonalityTraits(Resilience: 10f, WorkEthic: 14f, Creativity: 0f, Discipline: 0f),
            currentTilts: CreateTilts(
                (AttributeId.Stamina, 4f),
                (AttributeId.Strength, 4f),
                (AttributeId.Passing, -6f),
                (AttributeId.FirstTouch, -6f)));

        return profiles;
    }

    /// <summary>Builds an attribute-point tilt table from sparse (attribute, points) entries.</summary>
    private static float[] CreateTilts(params (AttributeId Id, float Amount)[] entries)
    {
        float[] tilts = new float[AttributeMetadata.Count];
        foreach ((AttributeId id, float amount) in entries) tilts[(int)id] += amount;
        return tilts;
    }

    // ---------------------------------------------------------------------------------------------
    // Validation.
    // ---------------------------------------------------------------------------------------------

    private static void ValidateAge(int age)
    {
        if (age is < MinimumAge or > MaximumAge)
        {
            throw new ArgumentOutOfRangeException(
                nameof(age), age, $"Starting age must be between {MinimumAge} and {MaximumAge}.");
        }
    }

    private static void ValidateTier(int tier)
    {
        if (tier is < MinimumTier or > MaximumTier)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tier), tier, $"Division tier must be between {MinimumTier} and {MaximumTier}.");
        }
    }

    private static void ValidateBackstory(BackstoryKind backstory)
    {
        if (!Enum.IsDefined(backstory))
        {
            throw new ArgumentOutOfRangeException(
                nameof(backstory), backstory, "Unknown backstory preset.");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Nested backstory preset type.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One backstory preset: realization offsets per category, attribute-point current tilts, personality
    /// deltas and the visibility multiplier. None of these fields can ever influence a ceiling.
    /// </summary>
    private sealed class BackstoryProfile
    {
        private readonly float _innateOffset;
        private readonly float _bodyTrainableOffset;
        private readonly float _skillAndMindOffset;
        private readonly float[] _currentTilts;

        public BackstoryProfile(
            float innateOffset,
            float bodyTrainableOffset,
            float skillAndMindOffset,
            float visibilityMultiplier,
            PersonalityTraits personalityDelta,
            float[] currentTilts)
        {
            _innateOffset = innateOffset;
            _bodyTrainableOffset = bodyTrainableOffset;
            _skillAndMindOffset = skillAndMindOffset;
            VisibilityMultiplier = visibilityMultiplier;
            PersonalityDelta = personalityDelta;
            _currentTilts = currentTilts;
        }

        /// <summary>Exposure multiplier applied to <see cref="BaseVisibility"/>.</summary>
        public float VisibilityMultiplier { get; }

        /// <summary>Personality deltas applied around <see cref="PersonalityTraits.Neutral"/>.</summary>
        public PersonalityTraits PersonalityDelta { get; }

        /// <summary>Realization offset added to the age ratio for one attribute category.</summary>
        public float OffsetFor(AttributeCategory category) => category switch
        {
            AttributeCategory.Innate => _innateOffset,
            AttributeCategory.BodyTrainable => _bodyTrainableOffset,
            AttributeCategory.SkillAndMind => _skillAndMindOffset,
            _ => 0f
        };

        /// <summary>Attribute-point addition to the starting current of one attribute.</summary>
        public float TiltFor(AttributeId id) => _currentTilts[(int)id];
    }
}
