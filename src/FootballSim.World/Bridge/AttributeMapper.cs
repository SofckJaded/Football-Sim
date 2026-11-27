using System;
using FootballSim.Core;
using FootballSim.World.Players;

namespace FootballSim.World.Bridge;

/// <summary>
/// Stage W5 match bridge: maps one living-world <see cref="WorldPlayer"/> onto the match engine's identity
/// types (<see cref="PlayerAttributes"/>, <see cref="PlayerTendencies"/>, <see cref="MatchPlayerSpec"/>).
///
/// <para><b>Pure and deterministic.</b> The mapping is a function of the player (plus the role and archetype
/// the player was selected for). It reads no clock, no random stream and no shared state, so replaying the
/// same world state produces the same squad bit for bit.</para>
///
/// <para><b>Bounds come from the plan.</b> Each engine field interpolates linearly between the endpoints of
/// <c>docs/WORLD_CAREER_PLAN.md</c> §4.2's mapping column as the world attribute rises from 1 to 99. Every
/// output therefore lands inside <see cref="SimulationConfigValidation.ValidatePlayerAttributes"/> and
/// <see cref="SimulationConfigValidation.ValidateTendencies"/> for any world player. A non-finite current
/// attribute (only reachable by mutating a player after creation) is mapped to the attribute floor instead of
/// poisoning the engine squad.</para>
///
/// <para><b>Monotonicity.</b> Raising one world attribute never makes its engine field worse: every direct
/// mapping is a strictly increasing linear function of that attribute, and the two blended fields
/// (<see cref="PlayerAttributes.FootAccuracy01"/> and <see cref="PlayerAttributes.ReactionDelaySeconds"/>)
/// use positive weights on all of their inputs. Reaction delay is the one field where "better" means lower,
/// so it is the one field that falls as its world inputs rise.</para>
/// </summary>
public static class AttributeMapper
{
    // ---------------------------------------------------------------------------------------------
    // Engine endpoints (plan §4.2, last column). World attributes are on a 1..99 axis.
    // ---------------------------------------------------------------------------------------------

    private const float SprintSpeedMin = 6.6f;
    private const float SprintSpeedMax = 9.6f;
    private const float AccelerationMin = 5.0f;
    private const float AccelerationMax = 8.2f;
    private const float ReachRadiusMin = 0.68f;
    private const float ReachRadiusMax = 0.84f;
    private const float Strength01Min = 0.45f;
    private const float Strength01Max = 0.96f;
    private const float StaminaCapacityMin = 0.72f;
    private const float StaminaCapacityMax = 1.25f;
    private const float AgilityTurnRateMin = 7.2f;
    private const float AgilityTurnRateMax = 11.2f;
    private const float Balance01Min = 0.48f;
    private const float Balance01Max = 0.96f;
    private const float FootAccuracy01Min = 0.48f;
    private const float FootAccuracy01Max = 0.96f;
    private const float ShotPowerMin = 24.0f;
    private const float ShotPowerMax = 35.5f;
    private const float FirstTouch01Min = 0.48f;
    private const float FirstTouch01Max = 0.96f;
    private const float Composure01Min = 0.45f;
    private const float Composure01Max = 0.96f;
    private const float GoalkeepingReflex01Min = 0.45f;
    private const float GoalkeepingReflex01Max = 0.96f;
    private const float ReactionDelayMin = 0.04f;
    private const float ReactionDelayMax = 0.16f;

    // ---------------------------------------------------------------------------------------------
    // Blend weights for the two composite fields.
    // ---------------------------------------------------------------------------------------------

    /// <summary>A passer's weight in <see cref="PlayerAttributes.FootAccuracy01"/>; the finisher takes the rest.</summary>
    private const float FootAccuracyPassingWeight = 0.55f;
    private const float FootAccuracyFinishingWeight = 0.45f;

    /// <summary>Anticipation weights for <see cref="PlayerAttributes.ReactionDelaySeconds"/> (they sum to 1).</summary>
    private const float ReactionPositioningWeight = 0.45f;
    private const float ReactionComposureWeight = 0.35f;
    private const float ReactionOffTheBallWeight = 0.20f;

    // ---------------------------------------------------------------------------------------------
    // Tendency offset spans. Each is the peak-to-peak movement a 1 -> 99 world attribute (or 0 -> 100
    // personality trait) can add to the role/archetype preset, applied around the neutral 0.5 midpoint.
    // The spans are deliberately small: the engine preset stays the statement, the player only leans it.
    // ---------------------------------------------------------------------------------------------

    private const float ShootingAppetiteSpan = 0.18f;
    private const float DirectPassSpan = 0.15f;
    private const float RiskCreativitySpan = 0.12f;
    private const float RiskComposureSpan = 0.10f;
    private const float DribbleSpan = 0.22f;
    private const float DisciplinePositioningSpan = 0.16f;
    private const float DisciplinePersonalitySpan = 0.10f;
    private const float PressingTacklingSpan = 0.16f;
    private const float PressingStaminaSpan = 0.10f;
    private const float CrossPassingSpan = 0.18f;
    private const float ScanVisionSpan = 0.20f;
    private const float SweepPositioningSpan = 0.12f;
    private const float SweepGoalkeepingSpan = 0.12f;

    /// <summary>
    /// Maps every one of the eighteen world attributes onto the engine's fourteen identity fields.
    /// </summary>
    /// <param name="player">The world player to map; must not be null.</param>
    /// <returns>A fully populated, validation-clean <see cref="PlayerAttributes"/> instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> is null.</exception>
    public static PlayerAttributes ToAttributes(WorldPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        PlayerAttributeSet source = player.Attributes;

        // FootAccuracy blends two world attributes before mapping, so improving either one raises the field.
        float footSkill = FootAccuracyPassingWeight * Sanitized(source.Current(AttributeId.Passing)) +
            FootAccuracyFinishingWeight * Sanitized(source.Current(AttributeId.Finishing));

        // A composed, well-positioned, off-the-ball reader reacts sooner (the field is a delay, not a bonus).
        float anticipation = ReactionPositioningWeight * Sanitized(source.Current(AttributeId.Positioning)) +
            ReactionComposureWeight * Sanitized(source.Current(AttributeId.Composure)) +
            ReactionOffTheBallWeight * Sanitized(source.Current(AttributeId.OffTheBall));

        return new PlayerAttributes
        {
            PreferredFoot = player.PreferredFoot,
            MaxSprintSpeed = Map(source.Current(AttributeId.TopSpeed), SprintSpeedMin, SprintSpeedMax),
            Acceleration = Map(source.Current(AttributeId.Acceleration), AccelerationMin, AccelerationMax),
            AgilityTurnRate = Map(source.Current(AttributeId.Agility), AgilityTurnRateMin, AgilityTurnRateMax),
            StaminaCapacity = Map(source.Current(AttributeId.Stamina), StaminaCapacityMin, StaminaCapacityMax),
            FootAccuracy01 = Map(footSkill, FootAccuracy01Min, FootAccuracy01Max),
            ShotPowerMax = Map(source.Current(AttributeId.ShotPower), ShotPowerMin, ShotPowerMax),
            FirstTouch01 = Map(source.Current(AttributeId.FirstTouch), FirstTouch01Min, FirstTouch01Max),
            Balance01 = Map(source.Current(AttributeId.Balance), Balance01Min, Balance01Max),
            Strength01 = Map(source.Current(AttributeId.Strength), Strength01Min, Strength01Max),
            ReachRadius = Map(source.Current(AttributeId.NaturalReach), ReachRadiusMin, ReachRadiusMax),
            ReactionDelaySeconds = Math.Clamp(
                Map(anticipation, ReactionDelayMax, ReactionDelayMin), ReactionDelayMin, ReactionDelayMax),
            Composure01 = Map(source.Current(AttributeId.Composure), Composure01Min, Composure01Max),
            GoalkeepingReflex01 = Map(
                source.Current(AttributeId.Goalkeeping), GoalkeepingReflex01Min, GoalkeepingReflex01Max)
        };
    }

    /// <summary>
    /// Builds the engine tendencies from the role/archetype preset the engine itself would use
    /// (<see cref="PlayerTendencyProfile.For"/>), then leans each axis by the world attributes and
    /// personality that drive it. Every result is clamped to 0..1, so the output always validates.
    /// </summary>
    /// <param name="player">The world player whose attributes and personality are read.</param>
    /// <param name="role">The tactical role the player is fielded in.</param>
    /// <param name="archetype">The archetype the player is fielded as (None keeps the role preset exactly).</param>
    /// <returns>A validation-clean tendency set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> is null.</exception>
    public static PlayerTendencies ToTendencies(WorldPlayer player, TacticalRole role, PlayerArchetype archetype)
    {
        ArgumentNullException.ThrowIfNull(player);

        // The engine's own bridge mapping is the baseline; this method only personalises it.
        PlayerTendencies preset = PlayerTendencyProfile.For(role, archetype);
        PlayerAttributeSet attributes = player.Attributes;
        PersonalityTraits personality = player.Personality;

        float finishing = Attribute01(attributes.Current(AttributeId.Finishing));
        float vision = Attribute01(attributes.Current(AttributeId.Vision));
        float passing = Attribute01(attributes.Current(AttributeId.Passing));
        float dribbling = Attribute01(attributes.Current(AttributeId.Dribbling));
        float tackling = Attribute01(attributes.Current(AttributeId.Tackling));
        float stamina = Attribute01(attributes.Current(AttributeId.Stamina));
        float positioning = Attribute01(attributes.Current(AttributeId.Positioning));
        float goalkeeping = Attribute01(attributes.Current(AttributeId.Goalkeeping));
        float composure = Attribute01(attributes.Current(AttributeId.Composure));
        float creativity = Personality01(personality.Creativity);
        float discipline = Personality01(personality.Discipline);

        return new PlayerTendencies
        {
            // Finishing: a better finisher wants the shot more.
            ShootingAppetite01 = Clamp01(
                preset.ShootingAppetite01 + ShootingAppetiteSpan * (finishing - 0.5f)),

            // Vision: a better reader of the picture prefers to play the direct ball.
            DirectPassPreference01 = Clamp01(
                preset.DirectPassPreference01 + DirectPassSpan * (vision - 0.5f)),

            // Creativity raises risk appetite; composure adds a smaller share (a calm player can carry risk).
            RiskTolerance01 = Clamp01(
                preset.RiskTolerance01 +
                RiskCreativitySpan * (creativity - 0.5f) +
                RiskComposureSpan * (composure - 0.5f)),

            // Dribbling: a better carrier keeps the ball at his feet.
            DribblePreference01 = Clamp01(
                preset.DribblePreference01 + DribbleSpan * (dribbling - 0.5f)),

            // Positioning plus the Discipline personality trait: the two inputs to staying in shape.
            PositionalDiscipline01 = Clamp01(
                preset.PositionalDiscipline01 +
                DisciplinePositioningSpan * (positioning - 0.5f) +
                DisciplinePersonalitySpan * (discipline - 0.5f)),

            // Tackling and stamina: the skill and the engine to press.
            PressingAggression01 = Clamp01(
                preset.PressingAggression01 +
                PressingTacklingSpan * (tackling - 0.5f) +
                PressingStaminaSpan * (stamina - 0.5f)),

            // Passing: a better passer trusts his delivery into the box.
            CrossPreference01 = Clamp01(
                preset.CrossPreference01 + CrossPassingSpan * (passing - 0.5f)),

            // Vision: a better reader refreshes a stale picture more eagerly.
            ScanAppetite01 = Clamp01(
                preset.ScanAppetite01 + ScanVisionSpan * (vision - 0.5f)),

            // Positioning and goalkeeping: keeper-specific boldness to leave his line.
            SweepAggression01 = Clamp01(
                preset.SweepAggression01 +
                SweepPositioningSpan * (positioning - 0.5f) +
                SweepGoalkeepingSpan * (goalkeeping - 0.5f))
        };
    }

    /// <summary>
    /// Packages one world player as the engine's per-shirt request. The engine still derives the formation
    /// slot from the shirt number; the bridge states the identity, the role/archetype the player was selected
    /// for, the mapped attributes and tendencies, the starting freshness and the strong foot.
    /// </summary>
    /// <param name="player">The world player to package.</param>
    /// <param name="role">The tactical role to field the player in.</param>
    /// <param name="archetype">The archetype to field the player as.</param>
    /// <param name="initialStamina01">Starting freshness, clamped to 0..1 (NaN becomes 0).</param>
    /// <returns>A fully populated <see cref="MatchPlayerSpec"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> is null.</exception>
    public static MatchPlayerSpec ToMatchPlayerSpec(
        WorldPlayer player, TacticalRole role, PlayerArchetype archetype, float initialStamina01)
    {
        ArgumentNullException.ThrowIfNull(player);

        return new MatchPlayerSpec(
            Name: player.DisplayName,
            Role: role,
            Archetype: archetype,
            Attributes: ToAttributes(player),
            Tendencies: ToTendencies(player, role, archetype),
            InitialStamina01: ClampStamina01(initialStamina01),
            PreferredFoot: player.PreferredFoot);
    }

    // ---------------------------------------------------------------------------------------------
    // Numeric helpers. Pure, allocation-free and NaN-safe.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Clamps a world attribute into the 1..99 axis, mapping NaN to the floor.</summary>
    private static float Sanitized(float worldValue)
    {
        if (float.IsNaN(worldValue)) return AttributeMetadata.MinimumValue;
        return Math.Clamp(worldValue, AttributeMetadata.MinimumValue, AttributeMetadata.MaximumCeiling);
    }

    /// <summary>World attribute on a 0..1 axis: 1 -> 0, 99 -> 1.</summary>
    private static float Unit(float worldValue) =>
        (Sanitized(worldValue) - AttributeMetadata.MinimumValue) /
        (AttributeMetadata.MaximumCeiling - AttributeMetadata.MinimumValue);

    /// <summary>World attribute mapped linearly onto an engine range; the mapping is strictly increasing.</summary>
    private static float Map(float worldValue, float engineMinimum, float engineMaximum) =>
        engineMinimum + (engineMaximum - engineMinimum) * Unit(worldValue);

    /// <summary>World attribute as a 0..1 weight for the tendency offsets.</summary>
    private static float Attribute01(float worldValue) => Unit(worldValue);

    /// <summary>Personality trait (0..100) as a 0..1 weight; a corrupt value reads as neutral.</summary>
    private static float Personality01(float personalityValue)
    {
        if (float.IsNaN(personalityValue)) return 0.5f;
        return Math.Clamp(personalityValue, 0f, 100f) * 0.01f;
    }

    /// <summary>Clamps a tendency into 0..1; NaN becomes 0 so it can never poison validation.</summary>
    private static float Clamp01(float tendency) =>
        float.IsNaN(tendency) ? 0f : Math.Clamp(tendency, 0f, 1f);

    /// <summary>Clamps a starting-freshness value into 0..1; NaN becomes 0.</summary>
    private static float ClampStamina01(float stamina01) =>
        float.IsNaN(stamina01) ? 0f : Math.Clamp(stamina01, 0f, 1f);
}
