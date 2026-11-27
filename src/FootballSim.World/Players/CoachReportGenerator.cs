using System;
using System.Collections.Generic;
using System.Globalization;
using FootballSim.Core;
using FootballSim.World.Core;

namespace FootballSim.World.Players;

/// <summary>
/// A qualitative coach/scout verdict (plan §4.6). It is deliberately a <b>narrative</b> object: the gaps it
/// carries are noisy 0..100-scale estimates, and no member can carry a raw attribute ceiling or plasticity,
/// so an Authentic-mode UI cannot leak hidden potential through it.
/// </summary>
/// <param name="Sentences">Three to five readable verdict sentences, in report order.</param>
/// <param name="Summary">One fixed short label, e.g. "Rough diamond" or "Close to his peak".</param>
/// <param name="InnateGapEstimate">Noisy estimate of remaining innate (pace/reach) room, -20..100.</param>
/// <param name="BodyGapEstimate">Noisy estimate of remaining body-trainable room, -20..100.</param>
/// <param name="SkillGapEstimate">Noisy estimate of remaining skill-and-mind room, -20..100.</param>
/// <param name="ObservationNoise">The judge's noise band in attribute points (9 for an amateur, 2.5 for elite).</param>
public sealed record CoachReport(
    IReadOnlyList<string> Sentences,
    string Summary,
    float InnateGapEstimate,
    float BodyGapEstimate,
    float SkillGapEstimate,
    float ObservationNoise)
{
    /// <summary>The whole report as continuous prose (sentences joined by single spaces).</summary>
    public string Text => string.Join(" ", Sentences);
}

/// <summary>
/// Generates the fuzzy coach/scout report (plan §4.6): a small number of natural-language sentences about a
/// player's remaining room, perturbed by an accuracy error that shrinks with the judge's ability.
///
/// <para><b>Fog of war.</b> The generator never prints a ceiling, a plasticity or any number: it converts
/// the hidden category gaps into noisy estimates and then into prose buckets, so a coach with poor judging
/// ability genuinely cannot tell a capped player from a prospect with room left.</para>
///
/// <para><b>Determinism.</b> The same player, judging ability and <see cref="WorldRng"/> stream always
/// produce the same report. The draw order is fixed: one Gaussian per category (innate, body-trainable,
/// skill-and-mind), then one template pick per sentence in sentence order.</para>
/// </summary>
public static class CoachReportGenerator
{
    /// <summary>Smallest number of sentences a report can contain.</summary>
    public const int MinimumSentences = 3;

    /// <summary>Largest number of sentences a report can contain.</summary>
    public const int MaximumSentences = 5;

    /// <summary>
    /// Observation noise (std dev, in attribute points) for a judge with zero ability: the Tier 6 amateur
    /// coach is off by roughly nine points on the average estimate.
    /// </summary>
    public const float MaximumObservationNoise = 9.0f;

    /// <summary>
    /// Observation noise for a judge with perfect ability (Tier 1 coach): about two and a half points.
    /// The mapping is linear between the two: <c>9.0 - 6.5 * judgingAbility01</c>.
    /// </summary>
    public const float MinimumObservationNoise = 2.5f;

    private const float ObservationNoiseRange = MaximumObservationNoise - MinimumObservationNoise;
    private const float MinimumGapEstimate = -20f;
    private const float MaximumGapEstimate = 100f;

    /// <summary>How many of the primary role's heaviest attributes make up its "key attribute" signal.</summary>
    private const int KeyAttributeCount = 3;

    // ---------------------------------------------------------------------------------------------
    // Templates. Every string is digit-free by construction: the report must never print a number, and
    // a test asserts that no character of the text or summary is a digit.
    // ---------------------------------------------------------------------------------------------

    private static readonly string[] InnateSettled =
    {
        "His pace and reach are what they are, and there is no extra gear hiding in him.",
        "What you see with his pace and reach is what you get from now on.",
        "The raw pace and reach are already banked, so do not expect another level."
    };

    private static readonly string[] InnateLittleLeft =
    {
        "There is a little raw pace and reach still to come, but only a little.",
        "He might find a touch more pace and reach, though it will not change his game.",
        "A small amount of pace and reach could still arrive with the right work."
    };

    private static readonly string[] InnateRealRoom =
    {
        "He still has real room to add pace and reach before he settles.",
        "There is genuine room left in his legs, so his pace and reach should still climb.",
        "His pace and reach are not finished products yet, and there is worthwhile room there."
    };

    private static readonly string[] InnateSignificantRoom =
    {
        "He has significant raw pace and reach still to unlock, which is the exciting part.",
        "There is a lot of raw pace and reach still to come from him.",
        "His legs are far from finished, so expect real gains in pace and reach."
    };

    private static readonly string[] BodyFilledOut =
    {
        "His frame is close to filled out, so gym work now would mostly trade one quality for another.",
        "He is nearly as strong as his frame will get, and pushing harder would cost him some agility.",
        "The body is almost there, so any further strength work means giving something back in agility."
    };

    private static readonly string[] BodySomeRoom =
    {
        "Put him in the gym and his strength could jump a level, though it may cost him a little agility.",
        "There is room in his frame and his engine, but bulking up would likely blunt his turn.",
        "A dedicated strength block should help, with the usual trade-off in agility and lightness."
    };

    private static readonly string[] BodyPlentyRoom =
    {
        "His body still has plenty of room to fill out, which buys strength and stamina at the cost of some agility.",
        "A proper strength programme would change him physically, though the trade-off is always a little sharpness.",
        "There is a lot of physical development left, and its shape will be a trade between power and mobility."
    };

    private static readonly string[] SkillNearlySettled =
    {
        "His {0} is already close to where it will settle, so the gains will be fine margins.",
        "{1} is the part of his game with the least room left, though small details still improve.",
        "There is not much left to add to his {0}, only refinement."
    };

    private static readonly string[] SkillSomeRoom =
    {
        "With regular minutes, his {0} should sharpen noticeably.",
        "The clearest technical gain is his {0}, which wants reps more than anything.",
        "Give him a run of games and his {0} will come on."
    };

    private static readonly string[] SkillBigRoom =
    {
        "His {0} has the most room of anything in his game, so the upside there is real.",
        "Give him time and his {0} could become his calling card.",
        "The biggest gains in his game will come in his {0}."
    };

    private static readonly string[] PeakNearCap =
    {
        "He is close to the finished article as {0}.",
        "There is very little development left in him as {0}.",
        "As {0}, he is about as good as he is going to be."
    };

    private static readonly string[] PeakWellOnWay =
    {
        "He is well on the way as {0}, with only fine margins left to find.",
        "As {0} he is most of the way there, though the details still sharpen.",
        "He is a settled option as {0}, with only small refinements left."
    };

    private static readonly string[] PeakStillDeveloping =
    {
        "There is still a lot of development left in him as {0}.",
        "He is a long way from the finished article as {0}.",
        "His game as {0} is still taking shape and should improve a good deal."
    };

    private static readonly string[] RoleWithSecondary =
    {
        "His attributes point to {0} first and foremost, with {1} a solid second option.",
        "He reads as {0}, though he could also do a job as {1}.",
        "If you had to label him, it is {0} for now, with {1} next in line."
    };

    private static readonly string[] RolePrimaryOnly =
    {
        "His attributes point clearly to {0} and not much else right now.",
        "He reads as {0}, and no other role is close at the moment.",
        "Everything in his profile says {0}."
    };

    /// <summary>
    /// Generates one coach/scout report. The three category estimates are the true mean gaps plus Gaussian
    /// noise scaled by <c>9.0 - 6.5 * judgingAbility01</c>, clamped to -20..100.
    /// </summary>
    /// <param name="player">Player being judged.</param>
    /// <param name="judgingAbility01">Judge quality, 0 (Tier 6 amateur) .. 1 (Tier 1 elite).</param>
    /// <param name="rng">Deterministic noise stream; its state advances and is not shared by the generator.</param>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> or <paramref name="rng"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="judgingAbility01"/> is outside 0..1 (including NaN).
    /// </exception>
    public static CoachReport Generate(WorldPlayer player, float judgingAbility01, WorldRng rng)
    {
        ArgumentNullException.ThrowIfNull(player);
        ValidateJudgingAbility(judgingAbility01);
        ArgumentNullException.ThrowIfNull(rng);

        float observationNoise = MaximumObservationNoise - ObservationNoiseRange * judgingAbility01;
        float innateGap = EstimateGap(player, AttributeCategory.Innate, observationNoise, rng);
        float bodyGap = EstimateGap(player, AttributeCategory.BodyTrainable, observationNoise, rng);
        float skillGap = EstimateGap(player, AttributeCategory.SkillAndMind, observationNoise, rng);

        // The identity is a pure read (no RNG), so it can be built before the sentence templates are picked.
        EmergentIdentity identity = IdentityEvaluator.Evaluate(player);

        var sentences = new List<string>(MaximumSentences)
        {
            InnateSentence(innateGap, rng),
            BodySentence(bodyGap, rng),
            SkillSentence(SkillRoomAttribute(player, identity.PrimaryRole), skillGap, rng),
            PeakSentence(identity.PrimaryRole, KeyAttributeMeanGap(player, identity.PrimaryRole), rng)
        };

        // Sentence five is the optional one: it is only appended while the cap leaves room for it.
        if (sentences.Count < MaximumSentences)
        {
            sentences.Add(RoleSentence(identity.PrimaryRole, BestSecondaryRole(identity), rng));
        }

        return new CoachReport(
            sentences.AsReadOnly(),
            Summarise(innateGap, bodyGap, skillGap),
            innateGap,
            bodyGap,
            skillGap,
            observationNoise);
    }

    // ---------------------------------------------------------------------------------------------
    // Estimation.
    // ---------------------------------------------------------------------------------------------

    private static float EstimateGap(
        WorldPlayer player, AttributeCategory category, float observationNoise, WorldRng rng)
    {
        float trueGap = player.Attributes.MeanGap(category);
        float noisy = trueGap + rng.NextGaussian(0f, observationNoise);
        return Math.Clamp(noisy, MinimumGapEstimate, MaximumGapEstimate);
    }

    /// <summary>
    /// The skill-and-mind attribute with the largest true remaining gap (roster order breaks ties). For an
    /// outfield player goalkeeping is excluded: a keeper's hands are not part of an outfielder's upside,
    /// and mentioning them would make every amateur report read the same.
    /// </summary>
    private static AttributeId SkillRoomAttribute(WorldPlayer player, WorldRole primaryRole)
    {
        bool includeGoalkeeping = primaryRole == WorldRole.Goalkeeper;
        AttributeId best = AttributeId.Passing;
        float bestGap = float.MinValue;

        foreach (AttributeId id in AttributeMetadata.OfCategory(AttributeCategory.SkillAndMind))
        {
            if (!includeGoalkeeping && id == AttributeId.Goalkeeping) continue;
            float gap = player.Attributes.Gap(id);
            if (gap > bestGap)
            {
                bestGap = gap;
                best = id;
            }
        }

        return best;
    }

    /// <summary>
    /// Mean remaining gap of the primary role's three heaviest weighted attributes. This is the "where is he
    /// as a {role}" signal: the weights are the same transparent table the suitability score uses.
    /// </summary>
    private static float KeyAttributeMeanGap(WorldPlayer player, WorldRole primaryRole)
    {
        IReadOnlyDictionary<AttributeId, float> weights = RoleSuitabilityWeights.For(primaryRole);
        var key = new List<(AttributeId Id, float Weight)>(weights.Count);
        foreach (AttributeId id in AttributeMetadata.All)
        {
            if (weights.TryGetValue(id, out float weight) && weight > 0f) key.Add((id, weight));
        }

        key.Sort((left, right) =>
        {
            int byWeight = right.Weight.CompareTo(left.Weight);
            return byWeight != 0 ? byWeight : ((int)left.Id).CompareTo((int)right.Id);
        });

        int count = Math.Min(KeyAttributeCount, key.Count);
        if (count == 0) return 0f;

        float sum = 0f;
        for (int i = 0; i < count; i++) sum += player.Attributes.Gap(key[i].Id);
        return sum / count;
    }

    private static WorldRole? BestSecondaryRole(EmergentIdentity identity) =>
        identity.SecondaryRoles.Count > 0 ? identity.SecondaryRoles[0] : null;

    // ---------------------------------------------------------------------------------------------
    // Sentences.
    // ---------------------------------------------------------------------------------------------

    private static string InnateSentence(float estimate, WorldRng rng)
    {
        if (estimate <= 0.5f) return Pick(rng, InnateSettled);
        if (estimate <= 3f) return Pick(rng, InnateLittleLeft);
        if (estimate <= 7f) return Pick(rng, InnateRealRoom);
        return Pick(rng, InnateSignificantRoom);
    }

    private static string BodySentence(float estimate, WorldRng rng)
    {
        if (estimate <= 2f) return Pick(rng, BodyFilledOut);
        if (estimate <= 6f) return Pick(rng, BodySomeRoom);
        return Pick(rng, BodyPlentyRoom);
    }

    private static string SkillSentence(AttributeId attribute, float estimate, WorldRng rng)
    {
        string lower = AttributeMetadata.Get(attribute).Label.ToLowerInvariant();
        string capitalised = char.ToUpperInvariant(lower[0]) + lower.Substring(1);

        if (estimate <= 3f) return Pick(rng, SkillNearlySettled, lower, capitalised);
        if (estimate <= 9f) return Pick(rng, SkillSomeRoom, lower, capitalised);
        return Pick(rng, SkillBigRoom, lower, capitalised);
    }

    private static string PeakSentence(WorldRole primaryRole, float keyMeanGap, WorldRng rng)
    {
        string rolePhrase = RolePhrase(primaryRole);
        if (keyMeanGap <= 1.5f) return Pick(rng, PeakNearCap, rolePhrase);
        if (keyMeanGap <= 5f) return Pick(rng, PeakWellOnWay, rolePhrase);
        return Pick(rng, PeakStillDeveloping, rolePhrase);
    }

    private static string RoleSentence(WorldRole primaryRole, WorldRole? secondaryRole, WorldRng rng)
    {
        string primary = RolePhrase(primaryRole);
        if (secondaryRole is null) return Pick(rng, RolePrimaryOnly, primary);
        return Pick(rng, RoleWithSecondary, primary, RolePhrase(secondaryRole.Value));
    }

    /// <summary>Human role name with its article, e.g. "a centre-back", "an attacking midfielder".</summary>
    private static string RolePhrase(WorldRole role) => role switch
    {
        WorldRole.Goalkeeper => "a goalkeeper",
        WorldRole.CenterBack => "a centre-back",
        WorldRole.FullBack => "a full-back",
        WorldRole.DefensiveMidfielder => "a holding midfielder",
        WorldRole.CentralMidfielder => "a central midfielder",
        WorldRole.AttackingMidfielder => "an attacking midfielder",
        WorldRole.Winger => "a winger",
        WorldRole.Striker => "a striker",
        _ => "a footballer"
    };

    // ---------------------------------------------------------------------------------------------
    // Summary.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One fixed short verdict chosen from the three noisy estimates. The order matters: an obviously
    /// finished player never reads as a prospect, and a finished player never reads as a rough diamond.
    /// </summary>
    private static string Summarise(float innateGap, float bodyGap, float skillGap)
    {
        float overall = 0.25f * innateGap + 0.30f * bodyGap + 0.45f * skillGap;

        if (innateGap <= 1.5f && bodyGap <= 3f && skillGap <= 5f) return "Close to his peak";
        if (skillGap >= 16f && (innateGap >= 6f || bodyGap >= 9f)) return "High-upside prospect";
        if (overall >= 13f) return "Rough diamond";
        if (skillGap >= 9f) return "Long-term project";
        if (overall <= 4f) return "Limited upside";
        return "Steady squad option";
    }

    // ---------------------------------------------------------------------------------------------
    // Template helpers.
    // ---------------------------------------------------------------------------------------------

    private static string Pick(WorldRng rng, string[] templates) => templates[rng.NextIndex(templates.Length)];

    private static string Pick(WorldRng rng, string[] templates, params object[] arguments) =>
        string.Format(CultureInfo.InvariantCulture, templates[rng.NextIndex(templates.Length)], arguments);

    private static void ValidateJudgingAbility(float judgingAbility01)
    {
        if (!(judgingAbility01 >= 0f && judgingAbility01 <= 1f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(judgingAbility01),
                judgingAbility01,
                "Judging ability must be between zero and one.");
        }
    }
}

// =================================================================================================
// Authentic-mode public view (plan §4.6). This is the whole fog-of-war boundary: these record types
// expose current values, trends, identity and a qualitative report, and structurally nothing else.
// =================================================================================================

/// <summary>
/// One attribute row of the Authentic-mode player screen: label, category badge, integer current value and
/// the twelve-week trend arrow. Deliberately has no ceiling, plasticity or latent member.
/// </summary>
/// <param name="Id">Attribute identity (roster order is preserved by the group builder).</param>
/// <param name="Label">Display label, e.g. "Top Speed".</param>
/// <param name="Category">Category badge shown next to the row.</param>
/// <param name="Current">Current value rounded to a whole number, 1..99.</param>
/// <param name="Trend">Change in current value over the last twelve recorded weeks.</param>
/// <param name="TrendArrow">"^" (rising), "-" (flat) or "v" (falling).</param>
public sealed record PublicAttributeView(
    AttributeId Id,
    string Label,
    AttributeCategory Category,
    int Current,
    float Trend,
    string TrendArrow);

/// <summary>
/// One category group of the Authentic-mode attribute panel: "Innate", "Body-Trainable" or "Skill &amp; Mind".
/// </summary>
/// <param name="Category">Category this group represents.</param>
/// <param name="Label">Human group label.</param>
/// <param name="Attributes">Attribute rows of the group, in roster order.</param>
public sealed record PublicAttributeGroup(
    AttributeCategory Category,
    string Label,
    IReadOnlyList<PublicAttributeView> Attributes);

/// <summary>
/// Everything an Authentic-mode UI may show about one player: identity, the eighteen current attribute
/// rows grouped by category, the coach report and the player's condition/form/visibility. No member of this
/// object graph carries a ceiling, a plasticity or a latent value, which is the plan's hard fog-of-war gate.
/// </summary>
/// <param name="Id">Stable player id.</param>
/// <param name="DisplayName">Player name.</param>
/// <param name="Age">Age in whole years.</param>
/// <param name="PreferredFoot">Strong foot.</param>
/// <param name="PrimaryRole">Emergent primary role.</param>
/// <param name="SecondaryRoles">Emergent secondary roles.</param>
/// <param name="StyleTags">Emergent playing-style tags.</param>
/// <param name="RoleSuitabilities">All eight role scores, sorted descending.</param>
/// <param name="AttributeGroups">Three category groups holding all eighteen attribute rows.</param>
/// <param name="CoachReport">Qualitative, noisy coach verdict.</param>
/// <param name="Condition01">Condition/freshness, 0..1.</param>
/// <param name="Form01">Rolling form, 0..1.</param>
/// <param name="Visibility01">Scouting visibility, 0..1.</param>
public sealed record PlayerPublicView(
    PlayerId Id,
    string DisplayName,
    int Age,
    Footedness PreferredFoot,
    WorldRole PrimaryRole,
    IReadOnlyList<WorldRole> SecondaryRoles,
    IReadOnlyList<string> StyleTags,
    IReadOnlyList<RoleSuitability> RoleSuitabilities,
    IReadOnlyList<PublicAttributeGroup> AttributeGroups,
    CoachReport CoachReport,
    float Condition01,
    float Form01,
    float Visibility01);

/// <summary>
/// Builds the Authentic-mode <see cref="PlayerPublicView"/>: identity from
/// <see cref="IdentityEvaluator.Evaluate"/>, the report from <see cref="CoachReportGenerator.Generate"/>, and
/// the three attribute groups in roster order with integer currents and twelve-week trend arrows.
/// </summary>
public static class PlayerPublicViewBuilder
{
    /// <summary>Group label for <see cref="AttributeCategory.Innate"/>.</summary>
    public const string InnateGroupLabel = "Innate";

    /// <summary>Group label for <see cref="AttributeCategory.BodyTrainable"/>.</summary>
    public const string BodyTrainableGroupLabel = "Body-Trainable";

    /// <summary>Group label for <see cref="AttributeCategory.SkillAndMind"/>.</summary>
    public const string SkillAndMindGroupLabel = "Skill & Mind";

    /// <summary>Trend delta at or above which the arrow points up.</summary>
    public const float RisingTrendThreshold = 0.4f;

    /// <summary>Trend delta at or below which the arrow points down.</summary>
    public const float FallingTrendThreshold = -0.4f;

    private static readonly AttributeCategory[] GroupOrder =
    {
        AttributeCategory.Innate,
        AttributeCategory.BodyTrainable,
        AttributeCategory.SkillAndMind
    };

    /// <summary>Builds the fog-of-war view of one player.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> or <paramref name="rng"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="judgingAbility01"/> is outside 0..1.</exception>
    public static PlayerPublicView Build(WorldPlayer player, float judgingAbility01, WorldRng rng)
    {
        ArgumentNullException.ThrowIfNull(player);

        // Generate validates the judging range and the RNG, and its draw order is fixed here because the
        // report is built before the (RNG-free) identity read.
        CoachReport coachReport = CoachReportGenerator.Generate(player, judgingAbility01, rng);
        EmergentIdentity identity = IdentityEvaluator.Evaluate(player);

        var groups = new PublicAttributeGroup[GroupOrder.Length];
        for (int i = 0; i < GroupOrder.Length; i++) groups[i] = BuildGroup(player, GroupOrder[i]);

        return new PlayerPublicView(
            player.Id,
            player.DisplayName,
            player.Age,
            player.PreferredFoot,
            identity.PrimaryRole,
            identity.SecondaryRoles,
            identity.StyleTags,
            identity.RoleSuitabilities,
            Array.AsReadOnly(groups),
            coachReport,
            player.Condition01,
            player.Form01,
            player.Visibility01);
    }

    private static PublicAttributeGroup BuildGroup(WorldPlayer player, AttributeCategory category)
    {
        IReadOnlyList<AttributeId> ids = AttributeMetadata.OfCategory(category);
        var views = new PublicAttributeView[ids.Count];
        for (int i = 0; i < ids.Count; i++)
        {
            AttributeId id = ids[i];
            float trend = player.Trends.Delta(id, PlayerTrendHistory.WindowWeeks, player.Attributes);
            views[i] = new PublicAttributeView(
                id,
                AttributeMetadata.Get(id).Label,
                category,
                RoundCurrent(player.Attributes.Current(id)),
                trend,
                ArrowFor(trend));
        }

        return new PublicAttributeGroup(category, GroupLabel(category), Array.AsReadOnly(views));
    }

    private static string GroupLabel(AttributeCategory category) => category switch
    {
        AttributeCategory.Innate => InnateGroupLabel,
        AttributeCategory.BodyTrainable => BodyTrainableGroupLabel,
        _ => SkillAndMindGroupLabel
    };

    private static int RoundCurrent(float current) =>
        Math.Clamp((int)MathF.Round(current, MidpointRounding.AwayFromZero), 1, 99);

    /// <summary>Twelve-week trend arrow: rising above +0.4 points, falling below -0.4, otherwise flat.</summary>
    private static string ArrowFor(float trend) =>
        trend > RisingTrendThreshold ? "^" : trend < FallingTrendThreshold ? "v" : "-";
}
