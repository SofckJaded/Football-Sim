using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using FootballSim.Core;

namespace FootballSim.World.Players;

/// <summary>
/// The eight functional roles the emergent identity evaluates (plan §4.7): goalkeeper, centre-back,
/// full-back, defensive midfielder, central midfielder, attacking midfielder, winger and striker.
///
/// The ordinals are stable and are used for tie-breaking, so never renumber or reuse a slot. These are
/// <b>world</b> roles — they describe where a player's attributes point, not a fixed class. The bridge to
/// the match engine's <see cref="TacticalRole"/> is <see cref="IdentityEvaluator.ToTacticalRole"/>.
/// </summary>
public enum WorldRole
{
    /// <summary>Shot-stopping and goal-line presence.</summary>
    Goalkeeper = 0,

    /// <summary>Central defender: duels, positional discipline and aerial reach.</summary>
    CenterBack = 1,

    /// <summary>Wide defender: tackling plus the engine to cover the flank.</summary>
    FullBack = 2,

    /// <summary>Holding midfielder: screening, tackling and simple distribution.</summary>
    DefensiveMidfielder = 3,

    /// <summary>Central midfielder: passing and vision link the whole side.</summary>
    CentralMidfielder = 4,

    /// <summary>Attacking midfielder: creation between the lines.</summary>
    AttackingMidfielder = 5,

    /// <summary>Wide attacker: pace, dribbling and one-versus-one threat.</summary>
    Winger = 6,

    /// <summary>Central forward: finishing and movement in the box.</summary>
    Striker = 7
}

/// <summary>One role's suitability score, 0..100, produced by <see cref="IdentityEvaluator.Evaluate"/>.</summary>
/// <param name="Role">The functional role being scored.</param>
/// <param name="Score">Weighted current-attribute suitability, 0..100.</param>
public readonly record struct RoleSuitability(WorldRole Role, float Score);

/// <summary>
/// The player's current emergent identity, recomputed from live attributes (never stored as a fixed class).
/// Includes the bridge mapping to the match engine's role/archetype/tendencies.
///
/// The positional members are what a UI renders; <see cref="PrimaryScore"/> and <see cref="SuitedTo"/> are
/// convenience reads over <see cref="RoleSuitabilities"/>. Equality is structural over every list and over
/// the bridged tendency values, so two evaluations of unchanged attributes compare equal — which is what
/// makes "identity is a pure function of the player" directly testable.
/// </summary>
/// <param name="PrimaryRole">Highest-scoring role; ties resolve by <see cref="WorldRole"/> order.</param>
/// <param name="SecondaryRoles">Every other role within six points of the primary, or at least 85% of it.</param>
/// <param name="RoleSuitabilities">All eight roles, sorted by score descending (ties by role order).</param>
/// <param name="StyleTags">One or two emergent playing-style tags.</param>
/// <param name="TacticalRole">Bridge to the match engine's role vocabulary.</param>
/// <param name="Archetype">Bridge to the match engine's archetype vocabulary.</param>
/// <param name="Tendencies">Bridge to the match engine's bounded tendency values.</param>
public sealed record EmergentIdentity(
    WorldRole PrimaryRole,
    IReadOnlyList<WorldRole> SecondaryRoles,
    IReadOnlyList<RoleSuitability> RoleSuitabilities,
    IReadOnlyList<string> StyleTags,
    TacticalRole TacticalRole,
    PlayerArchetype Archetype,
    PlayerTendencies Tendencies)
{
    /// <summary>Suitability score of <see cref="PrimaryRole"/>, 0..100.</summary>
    public float PrimaryScore
    {
        get
        {
            for (int i = 0; i < RoleSuitabilities.Count; i++)
            {
                if (RoleSuitabilities[i].Role == PrimaryRole) return RoleSuitabilities[i].Score;
            }

            return 0f;
        }
    }

    /// <summary>
    /// True when the player can be fielded in <paramref name="role"/>: the role is the primary role or a
    /// listed secondary role, or it scores within <paramref name="within"/> points of the primary role.
    /// The default six points matches <see cref="SecondaryRoles"/>' distance rule, so by default this agrees
    /// with "primary or secondary".
    /// </summary>
    public bool SuitedTo(WorldRole role, float within = 6f)
    {
        if (role == PrimaryRole) return true;
        for (int i = 0; i < SecondaryRoles.Count; i++)
        {
            if (SecondaryRoles[i] == role) return true;
        }

        for (int i = 0; i < RoleSuitabilities.Count; i++)
        {
            if (RoleSuitabilities[i].Role == role)
            {
                return RoleSuitabilities[i].Score >= PrimaryScore - within;
            }
        }

        return false;
    }

    /// <summary>Structural equality: every role list, the tags and the nine bridged tendency values.</summary>
    public bool Equals(EmergentIdentity? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (PrimaryRole != other.PrimaryRole ||
            TacticalRole != other.TacticalRole ||
            Archetype != other.Archetype ||
            SecondaryRoles.Count != other.SecondaryRoles.Count ||
            RoleSuitabilities.Count != other.RoleSuitabilities.Count ||
            StyleTags.Count != other.StyleTags.Count)
        {
            return false;
        }

        for (int i = 0; i < SecondaryRoles.Count; i++)
        {
            if (SecondaryRoles[i] != other.SecondaryRoles[i]) return false;
        }

        for (int i = 0; i < RoleSuitabilities.Count; i++)
        {
            if (!RoleSuitabilities[i].Equals(other.RoleSuitabilities[i])) return false;
        }

        for (int i = 0; i < StyleTags.Count; i++)
        {
            if (!string.Equals(StyleTags[i], other.StyleTags[i], StringComparison.Ordinal)) return false;
        }

        return TendenciesEqual(Tendencies, other.Tendencies);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(PrimaryRole);
        hash.Add(TacticalRole);
        hash.Add(Archetype);
        for (int i = 0; i < SecondaryRoles.Count; i++) hash.Add(SecondaryRoles[i]);
        for (int i = 0; i < RoleSuitabilities.Count; i++) hash.Add(RoleSuitabilities[i]);
        for (int i = 0; i < StyleTags.Count; i++) hash.Add(StyleTags[i], StringComparer.Ordinal);
        AddTendencies(ref hash, Tendencies);
        return hash.ToHashCode();
    }

    private static bool TendenciesEqual(PlayerTendencies? left, PlayerTendencies? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;

        return left.RiskTolerance01 == right.RiskTolerance01 &&
               left.DirectPassPreference01 == right.DirectPassPreference01 &&
               left.ShootingAppetite01 == right.ShootingAppetite01 &&
               left.DribblePreference01 == right.DribblePreference01 &&
               left.PositionalDiscipline01 == right.PositionalDiscipline01 &&
               left.PressingAggression01 == right.PressingAggression01 &&
               left.CrossPreference01 == right.CrossPreference01 &&
               left.ScanAppetite01 == right.ScanAppetite01 &&
               left.SweepAggression01 == right.SweepAggression01;
    }

    private static void AddTendencies(ref HashCode hash, PlayerTendencies? tendencies)
    {
        if (tendencies is null) return;
        hash.Add(tendencies.RiskTolerance01);
        hash.Add(tendencies.DirectPassPreference01);
        hash.Add(tendencies.ShootingAppetite01);
        hash.Add(tendencies.DribblePreference01);
        hash.Add(tendencies.PositionalDiscipline01);
        hash.Add(tendencies.PressingAggression01);
        hash.Add(tendencies.CrossPreference01);
        hash.Add(tendencies.ScanAppetite01);
        hash.Add(tendencies.SweepAggression01);
    }
}

/// <summary>
/// The transparent role formulas behind <see cref="IdentityEvaluator.Suitability"/> (plan §4.7): eight rows
/// of attribute weights, each row summing to exactly 1.00, so a suitability score is a weighted mean of the
/// live current values on the same 0..100 scale as an attribute.
///
/// The table is public read-only data rather than a private constant because the plan calls the formula
/// "transparent": tests, the UI and the coach report all need to know which attributes define a role
/// without duplicating the numbers.
/// </summary>
public static class RoleSuitabilityWeights
{
    // Declaration order is initialization order for static field initializers: the rows and dictionary
    // views must exist before All is built from them.
    private static readonly float[][] Rows = CreateRows();

    private static readonly IReadOnlyDictionary<AttributeId, float>[] Dictionaries = CreateDictionaries(Rows);

    /// <summary>Every role's weight row, in <see cref="WorldRole"/> order (GK, CB, FB, DM, CM, AM, W, ST).</summary>
    public static IReadOnlyList<(WorldRole Role, IReadOnlyDictionary<AttributeId, float> Weights)> All { get; } =
        CreateAll(Dictionaries);

    /// <summary>The weight row for one role. Every weight is positive and the row sums to 1.00.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="role"/> is not a defined world role.</exception>
    public static IReadOnlyDictionary<AttributeId, float> For(WorldRole role) => Dictionaries[IndexOf(role)];

    /// <summary>
    /// The per-attribute weight row as a fixed <see cref="AttributeId"/>-ordinal array. Used by the
    /// suitability sum so the arithmetic order is roster order and therefore bit-stable.
    /// </summary>
    internal static float[] Row(WorldRole role) => Rows[IndexOf(role)];

    private static int IndexOf(WorldRole role)
    {
        int index = (int)role;
        if (index < 0 || index >= Rows.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown world role.");
        }

        return index;
    }

    private static float[][] CreateRows() => new[]
    {
        // Goalkeeper: shot-stopping first, with positioning, calmness, body control and reach behind it.
        Row(
            (AttributeId.Goalkeeping, 0.62f),
            (AttributeId.Positioning, 0.12f),
            (AttributeId.Composure, 0.10f),
            (AttributeId.Balance, 0.06f),
            (AttributeId.Agility, 0.06f),
            (AttributeId.NaturalReach, 0.04f)),

        // Centre-back: duel-winning plus position, strength and aerial reach; enough passing to build.
        Row(
            (AttributeId.Tackling, 0.20f),
            (AttributeId.Positioning, 0.20f),
            (AttributeId.Strength, 0.15f),
            (AttributeId.Balance, 0.12f),
            (AttributeId.Composure, 0.12f),
            (AttributeId.NaturalReach, 0.08f),
            (AttributeId.Passing, 0.05f),
            (AttributeId.Vision, 0.04f),
            (AttributeId.TopSpeed, 0.04f)),

        // Full-back: defending plus the engine and pace to cover the touchline, with a delivery.
        Row(
            (AttributeId.Tackling, 0.16f),
            (AttributeId.Positioning, 0.14f),
            (AttributeId.Stamina, 0.14f),
            (AttributeId.TopSpeed, 0.12f),
            (AttributeId.Passing, 0.10f),
            (AttributeId.Acceleration, 0.08f),
            (AttributeId.Balance, 0.08f),
            (AttributeId.Agility, 0.08f),
            (AttributeId.Composure, 0.06f),
            (AttributeId.Vision, 0.04f)),

        // Defensive midfielder: screening and tackling first, then circulation and the engine.
        Row(
            (AttributeId.Tackling, 0.22f),
            (AttributeId.Positioning, 0.16f),
            (AttributeId.Passing, 0.14f),
            (AttributeId.Stamina, 0.12f),
            (AttributeId.Vision, 0.10f),
            (AttributeId.Composure, 0.10f),
            (AttributeId.Strength, 0.08f),
            (AttributeId.Balance, 0.04f),
            (AttributeId.Agility, 0.04f)),

        // Central midfielder: the most balanced passing/engine row of the eight.
        Row(
            (AttributeId.Passing, 0.20f),
            (AttributeId.Vision, 0.16f),
            (AttributeId.FirstTouch, 0.12f),
            (AttributeId.Stamina, 0.12f),
            (AttributeId.Composure, 0.08f),
            (AttributeId.Positioning, 0.08f),
            (AttributeId.Tackling, 0.08f),
            (AttributeId.Dribbling, 0.08f),
            (AttributeId.OffTheBall, 0.04f),
            (AttributeId.Agility, 0.04f)),

        // Attacking midfielder: creation first (vision, passing, touch), then the box threat.
        Row(
            (AttributeId.Vision, 0.16f),
            (AttributeId.Passing, 0.16f),
            (AttributeId.FirstTouch, 0.14f),
            (AttributeId.Dribbling, 0.12f),
            (AttributeId.Finishing, 0.12f),
            (AttributeId.OffTheBall, 0.10f),
            (AttributeId.Composure, 0.08f),
            (AttributeId.Agility, 0.06f),
            (AttributeId.ShotPower, 0.06f)),

        // Winger: pace and one-versus-one quality, with enough end product to matter.
        Row(
            (AttributeId.Dribbling, 0.16f),
            (AttributeId.TopSpeed, 0.16f),
            (AttributeId.Acceleration, 0.12f),
            (AttributeId.FirstTouch, 0.12f),
            (AttributeId.Agility, 0.12f),
            (AttributeId.OffTheBall, 0.12f),
            (AttributeId.Passing, 0.10f),
            (AttributeId.Finishing, 0.06f),
            (AttributeId.Stamina, 0.04f)),

        // Striker: finishing is the single heaviest weight in the whole table.
        Row(
            (AttributeId.Finishing, 0.24f),
            (AttributeId.OffTheBall, 0.16f),
            (AttributeId.Composure, 0.12f),
            (AttributeId.Strength, 0.10f),
            (AttributeId.TopSpeed, 0.10f),
            (AttributeId.FirstTouch, 0.10f),
            (AttributeId.ShotPower, 0.08f),
            (AttributeId.Balance, 0.06f),
            (AttributeId.Dribbling, 0.04f))
    };

    private static float[] Row(params (AttributeId Id, float Weight)[] entries)
    {
        float[] row = new float[AttributeMetadata.Count];
        foreach ((AttributeId id, float weight) in entries) row[(int)id] = weight;
        return row;
    }

    private static IReadOnlyDictionary<AttributeId, float>[] CreateDictionaries(float[][] rows)
    {
        var dictionaries = new IReadOnlyDictionary<AttributeId, float>[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            var row = new Dictionary<AttributeId, float>();
            for (int attribute = 0; attribute < rows[i].Length; attribute++)
            {
                float weight = rows[i][attribute];
                if (weight > 0f) row[(AttributeId)attribute] = weight;
            }

            dictionaries[i] = new ReadOnlyDictionary<AttributeId, float>(row);
        }

        return dictionaries;
    }

    private static IReadOnlyList<(WorldRole Role, IReadOnlyDictionary<AttributeId, float> Weights)> CreateAll(
        IReadOnlyDictionary<AttributeId, float>[] dictionaries)
    {
        var all = new (WorldRole Role, IReadOnlyDictionary<AttributeId, float> Weights)[dictionaries.Length];
        for (int i = 0; i < dictionaries.Length; i++) all[i] = ((WorldRole)i, dictionaries[i]);
        return Array.AsReadOnly(all);
    }
}

/// <summary>
/// The emergent-identity calculator (plan §4.7). Players are not locked into a static class: every read
/// recomputes eight role suitabilities from live attributes, derives one or two style tags, and bridges the
/// result to the match engine's <see cref="TacticalRole"/>, <see cref="PlayerArchetype"/> and
/// <see cref="PlayerTendencies"/>.
///
/// Every function here is pure and deterministic: no RNG, no clock, no unordered iteration. Suitability
/// arithmetic always runs in <see cref="AttributeId"/> roster order so two equal players produce bit-equal
/// scores.
/// </summary>
public static class IdentityEvaluator
{
    /// <summary>How close (in points) a role must be to the primary to count as secondary.</summary>
    public const float SecondaryWithinPoints = 6f;

    /// <summary>The alternative secondary-role rule: at least this fraction of the primary score.</summary>
    public const float SecondaryFraction = 0.85f;

    private const int RoleCount = 8;

    // ---------------------------------------------------------------------------------------------
    // Style-tag vocabulary. Tags are display strings (the plan fixes their wording), so they are shared
    // constants rather than an enum: the coach report and the UI both print them verbatim.
    // ---------------------------------------------------------------------------------------------
    private const string SpeedDemonTag = "Speed Demon";
    private const string DeepLyingPlaymakerTag = "Deep-Lying Playmaker";
    private const string PoacherTag = "Poacher";
    private const string BoxToBoxEngineTag = "Box-to-Box Engine";
    private const string TargetForwardTag = "Target Forward";
    private const string TechnicalDribblerTag = "Technical Dribbler";
    private const string BallWinningDestroyerTag = "Ball-Winning Destroyer";
    private const string ComposedBallPlayingCbTag = "Composed Ball-Playing CB";
    private const string SweeperKeeperTag = "Sweeper Keeper";
    private const string RawAmateurProspectTag = "Raw Amateur Prospect";
    private const string BalancedProfileTag = "Balanced Profile";
    private const string PhysicalPresenceTag = "Physical Presence";

    // ---------------------------------------------------------------------------------------------
    // Style-tag thresholds (plan §4.7). Values are attribute points on the same 0..100 scale.
    // ---------------------------------------------------------------------------------------------
    private const float SpeedDemonPace = 80f;
    private const float DeepLyingPassing = 75f;
    private const float DeepLyingVision = 75f;
    private const float DeepLyingPositioning = 65f;
    private const float PoacherFinishing = 78f;
    private const float PoacherOffTheBall = 75f;
    private const float BoxToBoxStamina = 75f;
    private const float BoxToBoxTackling = 70f;
    private const float BoxToBoxPassing = 70f;
    private const float TargetStrength = 78f;
    private const float TargetBalance = 72f;
    private const float TargetFinishing = 72f;
    private const float TechnicalDribbling = 78f;
    private const float TechnicalFirstTouch = 75f;
    private const float TechnicalAgility = 70f;
    private const float DestroyerTackling = 78f;
    private const float DestroyerStrength = 72f;
    private const float DestroyerStamina = 72f;
    private const float ComposedPositioning = 75f;
    private const float ComposedComposure = 75f;
    private const float ComposedPassing = 70f;
    private const float ComposedTackling = 72f;
    private const float SweeperGoalkeeping = 75f;
    private const float SweeperComposure = 70f;
    private const float SweeperPassing = 60f;
    private const float RawAmateurMean = 45f;

    /// <summary>
    /// The complete weighting-derived tendency delta bound. An attribute that is forty points from the
    /// population middle contributes 0.10 to a tendency, so a single extreme current value can lean a
    /// preference but can never replace the role preset.
    /// </summary>
    private const float TendencyDeltaRange = 400f;

    private const float MaximumTendencyDelta = 0.12f;
    private const float MinimumTendencyValue = 0.02f;
    private const float MaximumTendencyValue = 0.98f;

    /// <summary>Evaluates a player's full emergent identity from their current attributes.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> is null.</exception>
    public static EmergentIdentity Evaluate(WorldPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);

        RoleSuitability[] suitabilities = new RoleSuitability[RoleCount];
        for (int i = 0; i < RoleCount; i++)
        {
            WorldRole role = (WorldRole)i;
            suitabilities[i] = new RoleSuitability(role, Suitability(player, role));
        }

        SortDescending(suitabilities);

        WorldRole primaryRole = suitabilities[0].Role;
        float primaryScore = suitabilities[0].Score;

        var secondary = new List<WorldRole>(RoleCount - 1);
        for (int i = 1; i < suitabilities.Length; i++)
        {
            float score = suitabilities[i].Score;
            if (score >= primaryScore - SecondaryWithinPoints || score >= SecondaryFraction * primaryScore)
            {
                secondary.Add(suitabilities[i].Role);
            }
        }

        IReadOnlyList<string> styleTags = StyleTagsOf(player);
        PlayerArchetype archetype = ToArchetype(primaryRole, styleTags, player);

        return new EmergentIdentity(
            primaryRole,
            secondary.AsReadOnly(),
            Array.AsReadOnly(suitabilities),
            styleTags,
            ToTacticalRole(primaryRole),
            archetype,
            BuildTendencies(player, primaryRole, archetype));
    }

    /// <summary>
    /// One role's suitability, 0..100: the weighted mean of the live current values plus the small
    /// personality tilt, clamped. Clean arithmetic with no RNG, so it is safe to call every tick.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="role"/> is not a defined world role.</exception>
    public static float Suitability(WorldPlayer player, WorldRole role)
    {
        ArgumentNullException.ThrowIfNull(player);

        float[] weights = RoleSuitabilityWeights.Row(role);
        float score = 0f;
        for (int attribute = 0; attribute < weights.Length; attribute++)
        {
            float weight = weights[attribute];
            if (weight != 0f) score += weight * player.Attributes.Current((AttributeId)attribute);
        }

        score += PersonalityTilt(player, role);
        return Math.Clamp(score, 0f, 100f);
    }

    /// <summary>Maps a world role onto the match engine's role vocabulary.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="role"/> is not a defined world role.</exception>
    public static TacticalRole ToTacticalRole(WorldRole role) => role switch
    {
        WorldRole.Goalkeeper => TacticalRole.Goalkeeper,
        WorldRole.CenterBack => TacticalRole.CenterBack,
        WorldRole.FullBack => TacticalRole.FullBack,
        WorldRole.DefensiveMidfielder => TacticalRole.DefensiveMidfielder,
        WorldRole.CentralMidfielder => TacticalRole.CentralMidfielder,
        WorldRole.AttackingMidfielder => TacticalRole.CentralMidfielder,
        WorldRole.Winger => TacticalRole.Winger,
        WorldRole.Striker => TacticalRole.Striker,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown world role.")
    };

    /// <summary>
    /// Maps a primary role plus its style tags onto the match engine's archetype vocabulary (plan §4.7).
    ///
    /// This overload is tag-only: it never reads the player, so it can be reused anywhere the tags are
    /// already known. <see cref="Evaluate"/> uses <see cref="ToArchetype(WorldRole, IReadOnlyList{string},
    /// WorldPlayer)"/>, which adds the fullback "comfortable on the ball" clause that needs the passing
    /// value.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="styleTags"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="primaryRole"/> is not a defined world role.</exception>
    public static PlayerArchetype ToArchetype(WorldRole primaryRole, IReadOnlyList<string> styleTags)
    {
        ArgumentNullException.ThrowIfNull(styleTags);
        ValidateRole(primaryRole);
        return MapArchetype(primaryRole, styleTags, passingCurrent: float.NaN);
    }

    /// <summary>
    /// Player-aware archetype mapping: identical to the tag-only overload, plus the plan's fullback rule
    /// "inverted full-back if deep-lying playmaker <i>or</i> passing is at least 72".
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> or <paramref name="styleTags"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="primaryRole"/> is not a defined world role.</exception>
    public static PlayerArchetype ToArchetype(
        WorldRole primaryRole, IReadOnlyList<string> styleTags, WorldPlayer player)
    {
        ArgumentNullException.ThrowIfNull(styleTags);
        ArgumentNullException.ThrowIfNull(player);
        ValidateRole(primaryRole);
        return MapArchetype(primaryRole, styleTags, player.Attributes.Current(AttributeId.Passing));
    }

    /// <summary>
    /// Bridges the world identity onto the match engine's nine tendency values: the role/archetype preset
    /// first, then one bounded attribute-driven delta per axis (plan §4.7), every result clamped to
    /// 0.02..0.98 so <see cref="SimulationConfigValidation.ValidateTendencies"/> always accepts it.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> is null.</exception>
    public static PlayerTendencies BuildTendencies(
        WorldPlayer player, WorldRole primaryRole, PlayerArchetype archetype)
    {
        ArgumentNullException.ThrowIfNull(player);

        PlayerTendencies preset = PlayerTendencyProfile.For(ToTacticalRole(primaryRole), archetype);

        float finishing = AttributeDelta(player, AttributeId.Finishing);
        float dribbling = AttributeDelta(player, AttributeId.Dribbling);
        float vision = AttributeDelta(player, AttributeId.Vision);
        float positioning = AttributeDelta(player, AttributeId.Positioning);
        float stamina = AttributeDelta(player, AttributeId.Stamina);
        float passing = AttributeDelta(player, AttributeId.Passing);

        // Keepers lean off their line in proportion to their shot-stopping; outfielders get no sweep delta.
        float sweep = primaryRole == WorldRole.Goalkeeper
            ? (player.Attributes.Current(AttributeId.Goalkeeping) - 50f) / 500f
            : 0f;

        return new PlayerTendencies
        {
            ShootingAppetite01 = BoundedTendency(preset.ShootingAppetite01, finishing),
            DribblePreference01 = BoundedTendency(preset.DribblePreference01, dribbling),
            RiskTolerance01 = BoundedTendency(preset.RiskTolerance01, vision),
            PositionalDiscipline01 = BoundedTendency(preset.PositionalDiscipline01, positioning),
            PressingAggression01 = BoundedTendency(preset.PressingAggression01, stamina),
            DirectPassPreference01 = BoundedTendency(preset.DirectPassPreference01, passing),
            CrossPreference01 = BoundedTendency(preset.CrossPreference01, passing * 0.5f),
            ScanAppetite01 = BoundedTendency(preset.ScanAppetite01, vision),
            SweepAggression01 = BoundedTendency(preset.SweepAggression01, sweep)
        };
    }

    /// <summary>
    /// The player's emergent playing-style tags (plan §4.7): every fixed rule is evaluated in order and the
    /// first two matches are kept, so an elite spike cannot be crowded out by the late "raw prospect" rule.
    /// With no match, exactly one dominant-profile tag is returned, so the result is always one or two tags.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> is null.</exception>
    public static IReadOnlyList<string> StyleTagsOf(WorldPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);

        var tags = new List<string>(2);
        AddIf(tags, IsSpeedDemon(player), SpeedDemonTag);
        AddIf(tags, IsDeepLyingPlaymaker(player), DeepLyingPlaymakerTag);
        AddIf(tags, IsPoacher(player), PoacherTag);
        AddIf(tags, IsBoxToBoxEngine(player), BoxToBoxEngineTag);
        AddIf(tags, IsTargetForward(player), TargetForwardTag);
        AddIf(tags, IsTechnicalDribbler(player), TechnicalDribblerTag);
        AddIf(tags, IsBallWinningDestroyer(player), BallWinningDestroyerTag);
        AddIf(tags, IsComposedBallPlayingCentreBack(player), ComposedBallPlayingCbTag);
        AddIf(tags, IsSweeperKeeper(player), SweeperKeeperTag);
        AddIf(tags, IsRawAmateurProspect(player), RawAmateurProspectTag);

        if (tags.Count == 0) tags.Add(DominantProfileTag(player));

        return tags.AsReadOnly();
    }

    // ---------------------------------------------------------------------------------------------
    // Suitability internals.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The small personality tilt on a role's suitability. Defensive roles reward discipline, attacking
    /// roles reward creativity, and the central midfield role is deliberately neutral so it stays the
    /// "pure attributes" control.
    /// </summary>
    private static float PersonalityTilt(WorldPlayer player, WorldRole role) => role switch
    {
        WorldRole.Goalkeeper or WorldRole.CenterBack or WorldRole.FullBack or
            WorldRole.DefensiveMidfielder => 0.03f * (player.Personality.Discipline - 50f),
        WorldRole.AttackingMidfielder or WorldRole.Winger or WorldRole.Striker =>
            0.03f * (player.Personality.Creativity - 50f),
        _ => 0f
    };

    /// <summary>Insertion sort (eight items, no delegate allocation) with enum order as the tie-break.</summary>
    private static void SortDescending(RoleSuitability[] items)
    {
        for (int i = 1; i < items.Length; i++)
        {
            RoleSuitability current = items[i];
            int j = i - 1;
            while (j >= 0 && CompareDescending(items[j], current) > 0)
            {
                items[j + 1] = items[j];
                j--;
            }

            items[j + 1] = current;
        }
    }

    private static int CompareDescending(RoleSuitability left, RoleSuitability right)
    {
        int byScore = right.Score.CompareTo(left.Score);
        return byScore != 0 ? byScore : left.Role.CompareTo(right.Role);
    }

    // ---------------------------------------------------------------------------------------------
    // Archetype internals.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The plan §4.7 archetype table. A <see cref="float.NaN"/> passing value means "tag-only call": the
    /// fullback clause then needs the deep-lying-playmaker tag because there is no passing number.
    /// </summary>
    private static PlayerArchetype MapArchetype(
        WorldRole primaryRole, IReadOnlyList<string> styleTags, float passingCurrent)
    {
        bool hasPlayerPassing = !float.IsNaN(passingCurrent);

        switch (primaryRole)
        {
            case WorldRole.Goalkeeper:
                return HasTag(styleTags, SweeperKeeperTag)
                    ? PlayerArchetype.SweeperKeeper
                    : PlayerArchetype.None;

            case WorldRole.Striker:
                // Only a target-forward profile maps; a poacher's running is already the striker preset.
                return HasTag(styleTags, TargetForwardTag)
                    ? PlayerArchetype.TargetForward
                    : PlayerArchetype.None;

            case WorldRole.Winger:
            case WorldRole.AttackingMidfielder:
                if (HasTag(styleTags, SpeedDemonTag) || HasTag(styleTags, PoacherTag))
                    return PlayerArchetype.InsideForward;
                if (HasTag(styleTags, TechnicalDribblerTag))
                    return PlayerArchetype.TouchlineWinger;
                if (HasTag(styleTags, DeepLyingPlaymakerTag))
                    return PlayerArchetype.Playmaker;
                return PlayerArchetype.None;

            case WorldRole.CentralMidfielder:
                if (HasTag(styleTags, DeepLyingPlaymakerTag)) return PlayerArchetype.Playmaker;
                if (HasTag(styleTags, BallWinningDestroyerTag)) return PlayerArchetype.BallWinner;
                return PlayerArchetype.None;

            case WorldRole.DefensiveMidfielder:
                return HasTag(styleTags, BallWinningDestroyerTag) ||
                       HasTag(styleTags, BoxToBoxEngineTag)
                    ? PlayerArchetype.BallWinner
                    : PlayerArchetype.None;

            case WorldRole.FullBack:
                if (HasTag(styleTags, DeepLyingPlaymakerTag)) return PlayerArchetype.InvertedFullBack;
                return hasPlayerPassing && passingCurrent >= 72f
                    ? PlayerArchetype.InvertedFullBack
                    : PlayerArchetype.None;

            default:
                // No centre-back archetype exists in the match engine by design (A7.6).
                return PlayerArchetype.None;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Tendency internals.
    // ---------------------------------------------------------------------------------------------

    /// <summary>One attribute's bounded tendency delta: (current - 50) / 400, clamped to ±0.12.</summary>
    private static float AttributeDelta(WorldPlayer player, AttributeId id) =>
        Math.Clamp(
            (player.Attributes.Current(id) - 50f) / TendencyDeltaRange,
            -MaximumTendencyDelta,
            MaximumTendencyDelta);

    private static float BoundedTendency(float preset, float delta) =>
        Math.Clamp(preset + delta, MinimumTendencyValue, MaximumTendencyValue);

    // ---------------------------------------------------------------------------------------------
    // Style-tag rules (fixed plan order).
    // ---------------------------------------------------------------------------------------------

    private static void AddIf(List<string> tags, bool match, string tag)
    {
        if (tags.Count < 2 && match) tags.Add(tag);
    }

    private static float Current(WorldPlayer player, AttributeId id) => player.Attributes.Current(id);

    private static bool IsSpeedDemon(WorldPlayer player) =>
        Current(player, AttributeId.TopSpeed) >= SpeedDemonPace &&
        Current(player, AttributeId.Acceleration) >= SpeedDemonPace;

    private static bool IsDeepLyingPlaymaker(WorldPlayer player) =>
        Current(player, AttributeId.Passing) >= DeepLyingPassing &&
        Current(player, AttributeId.Vision) >= DeepLyingVision &&
        Current(player, AttributeId.Positioning) >= DeepLyingPositioning;

    private static bool IsPoacher(WorldPlayer player) =>
        Current(player, AttributeId.Finishing) >= PoacherFinishing &&
        Current(player, AttributeId.OffTheBall) >= PoacherOffTheBall;

    private static bool IsBoxToBoxEngine(WorldPlayer player) =>
        Current(player, AttributeId.Stamina) >= BoxToBoxStamina &&
        Current(player, AttributeId.Tackling) >= BoxToBoxTackling &&
        Current(player, AttributeId.Passing) >= BoxToBoxPassing;

    private static bool IsTargetForward(WorldPlayer player) =>
        Current(player, AttributeId.Strength) >= TargetStrength &&
        Current(player, AttributeId.Balance) >= TargetBalance &&
        Current(player, AttributeId.Finishing) >= TargetFinishing;

    private static bool IsTechnicalDribbler(WorldPlayer player) =>
        Current(player, AttributeId.Dribbling) >= TechnicalDribbling &&
        Current(player, AttributeId.FirstTouch) >= TechnicalFirstTouch &&
        Current(player, AttributeId.Agility) >= TechnicalAgility;

    private static bool IsBallWinningDestroyer(WorldPlayer player) =>
        Current(player, AttributeId.Tackling) >= DestroyerTackling &&
        Current(player, AttributeId.Strength) >= DestroyerStrength &&
        Current(player, AttributeId.Stamina) >= DestroyerStamina;

    private static bool IsComposedBallPlayingCentreBack(WorldPlayer player) =>
        Current(player, AttributeId.Positioning) >= ComposedPositioning &&
        Current(player, AttributeId.Composure) >= ComposedComposure &&
        Current(player, AttributeId.Passing) >= ComposedPassing &&
        Current(player, AttributeId.Tackling) >= ComposedTackling;

    private static bool IsSweeperKeeper(WorldPlayer player) =>
        Current(player, AttributeId.Goalkeeping) >= SweeperGoalkeeping &&
        Current(player, AttributeId.Composure) >= SweeperComposure &&
        Current(player, AttributeId.Passing) >= SweeperPassing;

    /// <summary>Evaluated last: the whole outfield game is still amateur-level on average (below 45).</summary>
    private static bool IsRawAmateurProspect(WorldPlayer player)
    {
        float sum = 0f;
        int count = 0;
        foreach (AttributeId id in AttributeMetadata.All)
        {
            if (id == AttributeId.Goalkeeping) continue;
            sum += Current(player, id);
            count++;
        }

        return count > 0 && sum / count < RawAmateurMean;
    }

    /// <summary>
    /// The one-tag fallback when no fixed rule matched: a frame-led profile is called "Physical Presence"
    /// (strength and reach are the two highest attributes), everything else "Balanced Profile".
    /// </summary>
    private static string DominantProfileTag(WorldPlayer player)
    {
        int highest = 0;
        for (int attribute = 1; attribute < AttributeMetadata.Count; attribute++)
        {
            if (Current(player, (AttributeId)attribute) > Current(player, (AttributeId)highest))
            {
                highest = attribute;
            }
        }

        int second = -1;
        for (int attribute = 0; attribute < AttributeMetadata.Count; attribute++)
        {
            if (attribute == highest) continue;
            if (second < 0 || Current(player, (AttributeId)attribute) >
                Current(player, (AttributeId)second))
            {
                second = attribute;
            }
        }

        bool frameLed =
            (highest == (int)AttributeId.Strength && second == (int)AttributeId.NaturalReach) ||
            (highest == (int)AttributeId.NaturalReach && second == (int)AttributeId.Strength);
        return frameLed ? PhysicalPresenceTag : BalancedProfileTag;
    }

    private static bool HasTag(IReadOnlyList<string> styleTags, string tag)
    {
        for (int i = 0; i < styleTags.Count; i++)
        {
            if (string.Equals(styleTags[i], tag, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static void ValidateRole(WorldRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown world role.");
        }
    }
}
