namespace FootballSim.World.Players;

/// <summary>
/// The eighteen player attributes of the unified player engine. Values are stable ordinals: they are
/// part of the save format, so never renumber or reuse a slot.
///
/// The order is the plan's attribute roster (W1): three innate, four body-trainable and eleven
/// skill-and-mind attributes. Every attribute carries three numbers (<see cref="PlayerAttributeState"/>):
/// the current value, a hidden ceiling and a plasticity that says how fast training closes the gap.
/// </summary>
public enum AttributeId
{
    /// <summary>Innate: top sprint speed.</summary>
    TopSpeed = 0,
    /// <summary>Innate: acceleration from a standing start.</summary>
    Acceleration = 1,
    /// <summary>Innate: body frame / natural reach, not trained.</summary>
    NaturalReach = 2,
    /// <summary>Body-trainable: upper-body strength in duels.</summary>
    Strength = 3,
    /// <summary>Body-trainable: aerobic engine and repeat-effort capacity.</summary>
    Stamina = 4,
    /// <summary>Body-trainable: change of direction quickness.</summary>
    Agility = 5,
    /// <summary>Body-trainable: balance and body control under contact.</summary>
    Balance = 6,
    /// <summary>Skill and mind: range, weight and choice of pass.</summary>
    Passing = 7,
    /// <summary>Skill and mind: shot technique and placement.</summary>
    Finishing = 8,
    /// <summary>Skill and mind: shot power (technique-driven, so skill rather than innate).</summary>
    ShotPower = 9,
    /// <summary>Skill and mind: first touch and control quality.</summary>
    FirstTouch = 10,
    /// <summary>Skill and mind: carrying the ball past opponents.</summary>
    Dribbling = 11,
    /// <summary>Skill and mind: defensive duels and timing.</summary>
    Tackling = 12,
    /// <summary>Skill and mind: scanning, reading and seeing the next pass.</summary>
    Vision = 13,
    /// <summary>Skill and mind: staying calm in pressure moments.</summary>
    Composure = 14,
    /// <summary>Skill and mind: positional discipline and anticipation.</summary>
    Positioning = 15,
    /// <summary>Skill and mind: movement and timing without the ball.</summary>
    OffTheBall = 16,
    /// <summary>Skill and mind: goalkeeper-specific technical and shot-stopping ability.</summary>
    Goalkeeping = 17
}

/// <summary>
/// How an attribute responds to training and age. The category is what makes progression legible:
/// innate attributes are set early and decline, body-trainable attributes fill out and trade off against
/// each other, and skill-and-mind attributes are the plastic, long-runway ones.
/// </summary>
public enum AttributeCategory
{
    /// <summary>Ceiling set early; training barely moves it; declines with age or injury.</summary>
    Innate = 0,

    /// <summary>High training gain possible, with explicit trade-offs between body attributes.</summary>
    BodyTrainable = 1,

    /// <summary>Highly plastic; driven by reps and match minutes; ages well.</summary>
    SkillAndMind = 2
}

/// <summary>
/// Weight of each latent aptitude in one attribute's ceiling. The weights of an attribute sum to 1, so a
/// ceiling is a weighted blend of the player's aptitudes plus independent noise. Keeping this in the
/// metadata table (rather than inside the generator) is what lets the emergent-identity and coach-report
/// systems reason about "which aptitude drives this attribute" without duplicating the formula.
/// </summary>
public readonly record struct LatentWeights(
    float Athleticism,
    float Technical,
    float Mental,
    float Goalkeeping)
{
    /// <summary>Creates a weights row, e.g. <c>LatentWeights.Of(0.85f, 0f, 0.15f)</c>.</summary>
    public static LatentWeights Of(
        float athleticism, float technical, float mental, float goalkeeping = 0f) =>
        new(athleticism, technical, mental, goalkeeping);

    /// <summary>Sum of the four weights (1 for a normal attribute).</summary>
    public float Sum => Athleticism + Technical + Mental + Goalkeeping;

    /// <summary>Blends the four aptitudes with these weights.</summary>
    public float Blend(in LatentAptitudes aptitudes) =>
        Athleticism * aptitudes.Athleticism +
        Technical * aptitudes.Technical +
        Mental * aptitudes.Mental +
        Goalkeeping * aptitudes.Goalkeeping;
}

/// <summary>
/// Static metadata for one attribute: display labels, category, plasticity band and age curve anchors.
/// </summary>
/// <param name="Id">Stable ordinal.</param>
/// <param name="Label">Full display label, e.g. "Top Speed".</param>
/// <param name="ShortLabel">Compact label for tables, e.g. "PAC".</param>
/// <param name="Category">Innate / body-trainable / skill-and-mind.</param>
/// <param name="PlasticityMin">Lower bound of the rolled plasticity band.</param>
/// <param name="PlasticityMax">Upper bound of the rolled plasticity band.</param>
/// <param name="PeakAgeMin">First age of the plateau.</param>
/// <param name="PeakAgeMax">Last age of the plateau.</param>
/// <param name="DeclineOnsetAge">Age after which the attribute starts to decline.</param>
/// <param name="Weights">Latent-aptitude weights used to derive the ceiling.</param>
/// <param name="CeilingNoiseStdDev">Independent noise added to the ceiling blend.</param>
public sealed record AttributeDefinition(
    AttributeId Id,
    string Label,
    string ShortLabel,
    AttributeCategory Category,
    float PlasticityMin,
    float PlasticityMax,
    int PeakAgeMin,
    int PeakAgeMax,
    int DeclineOnsetAge,
    LatentWeights Weights,
    float CeilingNoiseStdDev)
{
    /// <summary>
    /// Natural reach is not a talent blend: it is a body-frame roll around a fixed population mean,
    /// only weakly correlated with athleticism (W1 §4.3). The generator special-cases this id using
    /// <see cref="AttributeMetadata.NaturalReachPopulationMean"/> and
    /// <see cref="AttributeMetadata.NaturalReachAthleticismWeight"/>.
    /// </summary>
    public bool IsBodyFrameRoll =>
        Id == AttributeId.NaturalReach;

    /// <summary>Middle of the rolled plasticity band (used by coach reports and scouting estimates).</summary>
    public float PlasticityMid => (PlasticityMin + PlasticityMax) * 0.5f;
}

/// <summary>
/// The one authoritative attribute metadata table. Everything that needs an attribute's label, category,
/// training response or age curve reads it here; nothing re-declares the roster.
/// </summary>
public static class AttributeMetadata
{
    /// <summary>Number of attributes in the engine.</summary>
    public const int Count = 18;

    /// <summary>Population mean of the body-frame roll for <see cref="AttributeId.NaturalReach"/>.</summary>
    public const float NaturalReachPopulationMean = 58f;

    /// <summary>Standard deviation of the body-frame roll before the athleticism correlation.</summary>
    public const float NaturalReachPopulationStdDev = 11.6f;

    /// <summary>How strongly the body-frame roll follows athleticism (plan §4.3: 0.25).</summary>
    public const float NaturalReachAthleticismWeight = 0.25f;

    /// <summary>Hard floor of any attribute current value or ceiling.</summary>
    public const float MinimumValue = 1f;

    /// <summary>Hard floor of a rolled ceiling.</summary>
    public const float MinimumCeiling = 20f;

    /// <summary>Hard ceiling of a rolled ceiling.</summary>
    public const float MaximumCeiling = 99f;

    /// <summary>Minimum plasticity.</summary>
    public const float MinimumPlasticity = 0.05f;

    /// <summary>Maximum plasticity.</summary>
    public const float MaximumPlasticity = 1.00f;

    private static readonly AttributeDefinition[] Definitions =
    {
        new(AttributeId.TopSpeed, "Top Speed", "PAC", AttributeCategory.Innate,
            0.08f, 0.16f, 21, 24, 28, LatentWeights.Of(0.85f, 0f, 0.15f), 6f),
        new(AttributeId.Acceleration, "Acceleration", "ACC", AttributeCategory.Innate,
            0.10f, 0.18f, 21, 24, 27, LatentWeights.Of(0.85f, 0f, 0.15f), 6f),
        new(AttributeId.NaturalReach, "Natural Reach", "REA", AttributeCategory.Innate,
            0.03f, 0.06f, 19, 21, 33, LatentWeights.Of(0.25f, 0f, 0f), 11.6f),
        new(AttributeId.Strength, "Strength", "STR", AttributeCategory.BodyTrainable,
            0.55f, 0.78f, 24, 28, 31, LatentWeights.Of(0.70f, 0.15f, 0.15f), 6f),
        new(AttributeId.Stamina, "Stamina", "STA", AttributeCategory.BodyTrainable,
            0.60f, 0.82f, 23, 28, 30, LatentWeights.Of(0.70f, 0.15f, 0.15f), 6f),
        new(AttributeId.Agility, "Agility", "AGI", AttributeCategory.BodyTrainable,
            0.45f, 0.68f, 22, 26, 29, LatentWeights.Of(0.70f, 0.15f, 0.15f), 6f),
        new(AttributeId.Balance, "Balance", "BAL", AttributeCategory.BodyTrainable,
            0.50f, 0.72f, 23, 27, 31, LatentWeights.Of(0.70f, 0.15f, 0.15f), 6f),
        new(AttributeId.Passing, "Passing", "PAS", AttributeCategory.SkillAndMind,
            0.72f, 0.95f, 26, 31, 34, LatentWeights.Of(0f, 0.75f, 0.25f), 6f),
        new(AttributeId.Finishing, "Finishing", "FIN", AttributeCategory.SkillAndMind,
            0.70f, 0.92f, 25, 30, 33, LatentWeights.Of(0f, 0.75f, 0.25f), 6f),
        new(AttributeId.ShotPower, "Shot Power", "POW", AttributeCategory.SkillAndMind,
            0.58f, 0.80f, 24, 29, 32, LatentWeights.Of(0.50f, 0.50f, 0f), 6f),
        new(AttributeId.FirstTouch, "First Touch", "TEC", AttributeCategory.SkillAndMind,
            0.75f, 0.95f, 25, 30, 34, LatentWeights.Of(0f, 0.75f, 0.25f), 6f),
        new(AttributeId.Dribbling, "Dribbling", "DRI", AttributeCategory.SkillAndMind,
            0.72f, 0.92f, 24, 29, 32, LatentWeights.Of(0f, 0.75f, 0.25f), 6f),
        new(AttributeId.Tackling, "Tackling", "TAC", AttributeCategory.SkillAndMind,
            0.70f, 0.90f, 26, 31, 33, LatentWeights.Of(0.20f, 0.45f, 0.35f), 6f),
        new(AttributeId.Vision, "Vision", "VIS", AttributeCategory.SkillAndMind,
            0.72f, 0.96f, 27, 33, 36, LatentWeights.Of(0f, 0.25f, 0.75f), 6f),
        new(AttributeId.Composure, "Composure", "CMP", AttributeCategory.SkillAndMind,
            0.70f, 0.94f, 27, 34, 37, LatentWeights.Of(0f, 0.25f, 0.75f), 6f),
        new(AttributeId.Positioning, "Positioning", "POS", AttributeCategory.SkillAndMind,
            0.74f, 0.96f, 27, 34, 36, LatentWeights.Of(0f, 0.25f, 0.75f), 6f),
        new(AttributeId.OffTheBall, "Off The Ball", "OFF", AttributeCategory.SkillAndMind,
            0.72f, 0.94f, 26, 32, 35, LatentWeights.Of(0f, 0.25f, 0.75f), 6f),
        new(AttributeId.Goalkeeping, "Goalkeeping", "GK", AttributeCategory.SkillAndMind,
            0.70f, 0.92f, 27, 34, 36, LatentWeights.Of(0f, 0f, 0.20f, 0.80f), 6f)
    };

    /// <summary>Every attribute id in roster order.</summary>
    public static IReadOnlyList<AttributeId> All { get; } = Array.AsReadOnly(new[]
    {
        AttributeId.TopSpeed, AttributeId.Acceleration, AttributeId.NaturalReach,
        AttributeId.Strength, AttributeId.Stamina, AttributeId.Agility, AttributeId.Balance,
        AttributeId.Passing, AttributeId.Finishing, AttributeId.ShotPower, AttributeId.FirstTouch,
        AttributeId.Dribbling, AttributeId.Tackling, AttributeId.Vision, AttributeId.Composure,
        AttributeId.Positioning, AttributeId.OffTheBall, AttributeId.Goalkeeping
    });

    /// <summary>Attributes of one category, in roster order.</summary>
    public static IReadOnlyList<AttributeId> OfCategory(AttributeCategory category)
    {
        var result = new List<AttributeId>(Count);
        foreach (AttributeId id in All)
        {
            if (Get(id).Category == category) result.Add(id);
        }
        return result;
    }

    /// <summary>Metadata for an attribute.</summary>
    public static AttributeDefinition Get(AttributeId id) => Definitions[(int)id];

    /// <summary>All metadata rows in roster order.</summary>
    public static IReadOnlyList<AttributeDefinition> AllDefinitions { get; } =
        Array.AsReadOnly((AttributeDefinition[])Definitions.Clone());

    /// <summary>Validates that an attribute value is inside the engine's hard range.</summary>
    public static bool IsValidValue(float value) => value is >= MinimumValue and <= 100f;
}
