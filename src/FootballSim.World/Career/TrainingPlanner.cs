using FootballSim.World.Players;

namespace FootballSim.World.Career;

/// <summary>
/// One training focus explained for the UI (plan §4.5 and stage W6 "Training" panel): the boosted
/// attributes, the attributes it actively costs, and a readable trade-off note. The description mirrors
/// <see cref="GrowthEngine"/>'s multipliers, which are the authority; this type only explains them.
/// </summary>
/// <param name="Focus">The preset being described.</param>
/// <param name="Label">Human-readable preset name.</param>
/// <param name="Summary">One sentence on what the week is for.</param>
/// <param name="BoostedAttributes">Attributes whose training gain is multiplied up.</param>
/// <param name="TradeOffAttributes">Attributes that can actually lose current value or headroom.</param>
/// <param name="TradeOffNote">
/// The explicit body-trainable trade-off, or an empty string when the focus has no downside.
/// </param>
public sealed record TrainingFocusDescription(
    TrainingFocus Focus,
    string Label,
    string Summary,
    IReadOnlyList<AttributeId> BoostedAttributes,
    IReadOnlyList<AttributeId> TradeOffAttributes,
    string TradeOffNote)
{
    /// <summary>Display labels of the boosted attributes (roster order).</summary>
    public IReadOnlyList<string> BoostedLabels => AttributeLabels(BoostedAttributes);

    /// <summary>Display labels of the trade-off attributes (roster order).</summary>
    public IReadOnlyList<string> TradeOffLabels => AttributeLabels(TradeOffAttributes);

    private static IReadOnlyList<string> AttributeLabels(IReadOnlyList<AttributeId> ids)
    {
        var labels = new string[ids.Count];
        for (int index = 0; index < ids.Count; index++) labels[index] = AttributeMetadata.Get(ids[index]).Label;
        return labels;
    }
}

/// <summary>
/// The training planner (plan stage W4): a thin, deterministic explanation layer over the growth engine's ten
/// presets. It recommends a focus for an emergent role and describes exactly what a focus boosts and costs,
/// so the career UI can show the body-trainable trade-offs without duplicating the growth rules.
/// </summary>
public static class TrainingPlanner
{
    /// <summary>Every preset in <see cref="TrainingFocus"/> order.</summary>
    public static IReadOnlyList<TrainingFocus> AllFocuses { get; } = BuildAllFocuses();

    /// <summary>
    /// The focus the AI would choose for a role (the same mapping the season runner uses), so a career player
    /// can compare the coaching suggestion with his own choice.
    /// </summary>
    /// <param name="role">The player's emergent or slot role.</param>
    public static TrainingFocus RecommendedFocusFor(WorldRole role) => role switch
    {
        WorldRole.Goalkeeper => TrainingFocus.Goalkeeping,
        WorldRole.CenterBack or WorldRole.FullBack or WorldRole.DefensiveMidfielder =>
            TrainingFocus.DefendingAndPositioning,
        WorldRole.CentralMidfielder or WorldRole.AttackingMidfielder => TrainingFocus.PassingAndVision,
        WorldRole.Winger => TrainingFocus.BallControlAndDribbling,
        WorldRole.Striker => TrainingFocus.FinishingAndShooting,
        _ => TrainingFocus.Balanced
    };

    /// <summary>
    /// The recommended focus for a player, derived from his emergent primary role (the career's default
    /// suggestion; the user always keeps the final choice).
    /// </summary>
    /// <param name="player">The player to advise.</param>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> is null.</exception>
    public static TrainingFocus RecommendedFocusFor(WorldPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return RecommendedFocusFor(IdentityEvaluator.Evaluate(player).PrimaryRole);
    }

    /// <summary>Describes one preset: label, summary, boosted attributes and explicit trade-offs.</summary>
    /// <param name="focus">The preset to describe.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="focus"/> is undefined.</exception>
    public static TrainingFocusDescription Describe(TrainingFocus focus) => focus switch
    {
        TrainingFocus.Balanced => new TrainingFocusDescription(
            focus,
            "Balanced",
            "Even development across every attribute; the neutral control.",
            Array.Empty<AttributeId>(),
            Array.Empty<AttributeId>(),
            string.Empty),

        TrainingFocus.StrengthAndPower => new TrainingFocusDescription(
            focus,
            "Strength & Power",
            "Gym blocks: build strength and balance, at a cost in agility once already strong.",
            new[] { AttributeId.Strength, AttributeId.Balance },
            new[] { AttributeId.Agility, AttributeId.TopSpeed },
            "Past 72 strength, agility loses 0.04 a week; past 78 strength, top-speed headroom shaves 0.015 a week."),

        TrainingFocus.EnduranceAndEngine => new TrainingFocusDescription(
            focus,
            "Endurance & Engine",
            "Aerobic work: build the engine that keeps late runs sharp.",
            new[] { AttributeId.Stamina },
            new[] { AttributeId.Strength },
            "Explosive strength gain is held to 0.70x while the endurance block runs."),

        TrainingFocus.AgilityAndQuickness => new TrainingFocusDescription(
            focus,
            "Agility & Quickness",
            "Change-of-direction work: sharper turns and quicker feet.",
            new[] { AttributeId.Agility, AttributeId.Balance },
            new[] { AttributeId.Strength },
            "Past 70 strength, strength loses 0.03 a week while the quickness block runs."),

        TrainingFocus.FinishingAndShooting => new TrainingFocusDescription(
            focus,
            "Finishing & Shooting",
            "Striking reps: sharper finishing and heavier shooting.",
            new[] { AttributeId.Finishing, AttributeId.ShotPower },
            Array.Empty<AttributeId>(),
            string.Empty),

        TrainingFocus.PassingAndVision => new TrainingFocusDescription(
            focus,
            "Passing & Vision",
            "Technical distribution work: cleaner passing and a quicker picture of the pitch.",
            new[] { AttributeId.Passing, AttributeId.Vision },
            Array.Empty<AttributeId>(),
            string.Empty),

        TrainingFocus.BallControlAndDribbling => new TrainingFocusDescription(
            focus,
            "Ball Control & Dribbling",
            "Close-control reps: a better first touch and more comfort carrying the ball.",
            new[] { AttributeId.Dribbling, AttributeId.FirstTouch },
            Array.Empty<AttributeId>(),
            string.Empty),

        TrainingFocus.DefendingAndPositioning => new TrainingFocusDescription(
            focus,
            "Defending & Positioning",
            "Defensive shape work: stronger tackling and better positional discipline.",
            new[] { AttributeId.Tackling, AttributeId.Positioning },
            Array.Empty<AttributeId>(),
            string.Empty),

        TrainingFocus.Goalkeeping => new TrainingFocusDescription(
            focus,
            "Goalkeeping",
            "Keeper-specific work: handling and the composure to read the game from the back.",
            new[] { AttributeId.Goalkeeping, AttributeId.Positioning, AttributeId.Composure },
            Array.Empty<AttributeId>(),
            string.Empty),

        TrainingFocus.RecoveryAndLight => new TrainingFocusDescription(
            focus,
            "Recovery & Light",
            "An unload week: little training stimulus, better condition and sharpness recovery.",
            Array.Empty<AttributeId>(),
            Array.Empty<AttributeId>(),
            "Training gain is held to 0.20x; condition and sharpness recover faster instead."),

        _ => throw new ArgumentOutOfRangeException(nameof(focus), focus, "Unknown training focus preset.")
    };

    /// <summary>Every preset described once, in <see cref="TrainingFocus"/> order (UI list source).</summary>
    public static IReadOnlyList<TrainingFocusDescription> DescribeAll()
    {
        var descriptions = new TrainingFocusDescription[AllFocuses.Count];
        for (int index = 0; index < descriptions.Length; index++) descriptions[index] = Describe(AllFocuses[index]);
        return descriptions;
    }

    private static IReadOnlyList<TrainingFocus> BuildAllFocuses()
    {
        var focuses = new List<TrainingFocus>(10);
        foreach (TrainingFocus focus in Enum.GetValues<TrainingFocus>()) focuses.Add(focus);
        return focuses.AsReadOnly();
    }
}
