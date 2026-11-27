using System;
using FootballSim.AI;
using FootballSim.Physics;

namespace FootballSim.Core;

/// <summary>
/// Validates the public simulation configuration before anything is built from it (audit F22).
///
/// The audit found that a match, pitch or squad configured with degenerate values was accepted and
/// only failed much later — a nonpositive half length, an invalid team size, a human id that is not
/// part of the match, a goal wider than the pitch, a negative reach or a non-finite speed. Those
/// values either divide by zero or silently poison the AI, so they are rejected up front with the
/// offending field named.
///
/// The validators are plain public functions so callers that build their own squads (the audit probe,
/// custom scenarios) can apply exactly the same limits the match engine applies to its own squads.
/// </summary>
public static class SimulationConfigValidation
{
    /// <summary>Smallest team the formation code supports (a single outfield player plus keeper).</summary>
    public const int MinPlayersPerTeam = 1;

    /// <summary>
    /// Largest team <c>TeamTacticalCoordinator.GetFormationRoleAndSlot</c> has a formation for.
    /// </summary>
    public const int MaxPlayersPerTeam = 11;

    /// <summary>Upper bound for a half: anything longer is a configuration mistake, not a match.</summary>
    public const float MaxHalfDurationSeconds = 6f * 60f * 60f;

    /// <summary>Validates everything a match needs before its squads are created.</summary>
    public static void Validate(PreMatchConfig config, PitchDimensions pitch)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(pitch);

        ValidateMatchConfig(config); // includes both team tactical profiles
        ValidatePitch(pitch);
    }

    /// <summary>
    /// Validates the match-level configuration: team size, half length and the pre-match human id.
    /// </summary>
    public static void ValidateMatchConfig(PreMatchConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.PlayersPerTeam < MinPlayersPerTeam || config.PlayersPerTeam > MaxPlayersPerTeam)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config.PlayersPerTeam), config.PlayersPerTeam,
                $"PlayersPerTeam must be between {MinPlayersPerTeam} and {MaxPlayersPerTeam}.");
        }

        RequireFinite(config.HalfDurationSeconds, nameof(config.HalfDurationSeconds));
        if (config.HalfDurationSeconds <= 0f || config.HalfDurationSeconds > MaxHalfDurationSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config.HalfDurationSeconds), config.HalfDurationSeconds,
                $"HalfDurationSeconds must be greater than zero and at most {MaxHalfDurationSeconds} s.");
        }

        if (config.DesignatedHumanPlayerId is int humanId)
        {
            int playerCount = config.PlayersPerTeam * 2;
            if (humanId < 1 || humanId > playerCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(config.DesignatedHumanPlayerId), humanId,
                    $"{nameof(config.DesignatedHumanPlayerId)} must be an existing player id between 1 " +
                    $"and {playerCount} for {config.PlayersPerTeam}-a-side.");
            }
        }

        ValidateTacticalProfile(config.HomeProfile, nameof(config.HomeProfile));
        ValidateTacticalProfile(config.AwayProfile, nameof(config.AwayProfile));
        ValidateLineups(config);
        AiRuntimeConfigValidation.Validate(config.Ai, nameof(config.Ai));
    }

    /// <summary>
    /// Validates the W5 additive external lineups, when they are present. A null lineup means "generate this
    /// side exactly as before", which is the pre-W5 behaviour; a stated lineup must be a complete, valid
    /// shirt-ordered squad so the bridge cannot half-replace a team.
    /// </summary>
    /// <param name="config">The match configuration to validate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A lineup length, shirt, role, stamina or attribute value is invalid.</exception>
    public static void ValidateLineups(PreMatchConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        ValidateLineup(config.HomeLineup, config.PlayersPerTeam, nameof(config.HomeLineup));
        ValidateLineup(config.AwayLineup, config.PlayersPerTeam, nameof(config.AwayLineup));
    }

    private static void ValidateLineup(IReadOnlyList<MatchPlayerSpec>? lineup, int playersPerTeam, string name)
    {
        if (lineup is null) return;

        if (lineup.Count != playersPerTeam)
        {
            throw new ArgumentOutOfRangeException(
                name, lineup.Count,
                $"{name} must state exactly {playersPerTeam} players for a {playersPerTeam}-a-side match.");
        }

        for (int index = 0; index < lineup.Count; index++)
        {
            MatchPlayerSpec spec = lineup[index]
                ?? throw new ArgumentOutOfRangeException(name, index, $"{name}[{index}] is null.");

            if (spec.ShirtNumber is int shirt && shirt != index + 1)
            {
                throw new ArgumentOutOfRangeException(
                    name, shirt,
                    $"{name}[{index}] states shirt {shirt}; a lineup is ordered by shirt 1..{playersPerTeam}.");
            }

            if (spec.Role is { } role && !Enum.IsDefined(role))
            {
                throw new ArgumentOutOfRangeException(
                    name, role, $"{name}[{index}].Role is not a defined tactical role.");
            }

            if (!Enum.IsDefined(spec.Archetype))
            {
                throw new ArgumentOutOfRangeException(
                    name, spec.Archetype, $"{name}[{index}].Archetype is not a defined archetype.");
            }

            if (!float.IsFinite(spec.InitialStamina01) || spec.InitialStamina01 < 0f || spec.InitialStamina01 > 1f)
            {
                throw new ArgumentOutOfRangeException(
                    name, spec.InitialStamina01,
                    $"{name}[{index}].InitialStamina01 must be a finite value inside 0..1.");
            }

            if (spec.Attributes is { } attributes)
            {
                ValidatePlayerAttributes(attributes, $"{name}[{index}].Attributes");
            }

            if (spec.Tendencies is { } tendencies)
            {
                ValidateTendencies(tendencies, $"{name}[{index}].Tendencies");
            }

            if (spec.PreferredFoot is { } foot && !Enum.IsDefined(foot))
            {
                throw new ArgumentOutOfRangeException(
                    name, foot, $"{name}[{index}].PreferredFoot is not a valid foot.");
            }
        }
    }

    /// <summary>
    /// Validates every pitch dimension and the relationships between them (a goal wider than the
    /// pitch, a penalty area longer than a half, a negative radius, ...).
    /// </summary>
    public static void ValidatePitch(PitchDimensions pitch)
    {
        ArgumentNullException.ThrowIfNull(pitch);

        RequirePositive(pitch.Length, nameof(pitch.Length));
        RequirePositive(pitch.Width, nameof(pitch.Width));
        RequirePositive(pitch.GoalWidth, nameof(pitch.GoalWidth));
        RequirePositive(pitch.GoalHeight, nameof(pitch.GoalHeight));
        RequireFinite(pitch.GoalDepth, nameof(pitch.GoalDepth));
        if (pitch.GoalDepth < 0f)
            throw new ArgumentOutOfRangeException(nameof(pitch.GoalDepth), pitch.GoalDepth, "GoalDepth cannot be negative.");
        RequirePositive(pitch.GoalPostRadius, nameof(pitch.GoalPostRadius));
        RequirePositive(pitch.PenaltyAreaLength, nameof(pitch.PenaltyAreaLength));
        RequirePositive(pitch.PenaltyAreaWidth, nameof(pitch.PenaltyAreaWidth));
        RequirePositive(pitch.GoalAreaLength, nameof(pitch.GoalAreaLength));
        RequirePositive(pitch.GoalAreaWidth, nameof(pitch.GoalAreaWidth));
        RequirePositive(pitch.PenaltySpotDistance, nameof(pitch.PenaltySpotDistance));
        if (pitch.CenterCircleRadius < 0f || !float.IsFinite(pitch.CenterCircleRadius))
        {
            throw new ArgumentOutOfRangeException(
                nameof(pitch.CenterCircleRadius), pitch.CenterCircleRadius,
                "CenterCircleRadius must be a finite, non-negative number.");
        }

        // Derived values must also be finite (a NaN Length would slip through the > 0 tests above).
        RequireFinite(pitch.HalfLength, nameof(pitch.HalfLength));
        RequireFinite(pitch.HalfWidth, nameof(pitch.HalfWidth));
        RequireFinite(pitch.HalfGoalWidth, nameof(pitch.HalfGoalWidth));

        if (pitch.GoalWidth >= pitch.Width)
            throw new ArgumentOutOfRangeException(nameof(pitch.GoalWidth), pitch.GoalWidth, "The goal must be narrower than the pitch.");
        if (pitch.GoalPostRadius * 2f >= pitch.GoalWidth)
            throw new ArgumentOutOfRangeException(nameof(pitch.GoalPostRadius), pitch.GoalPostRadius, "The goal posts must fit inside the goal.");
        if (pitch.PenaltyAreaLength > pitch.HalfLength)
            throw new ArgumentOutOfRangeException(nameof(pitch.PenaltyAreaLength), pitch.PenaltyAreaLength, "The penalty area cannot be longer than a half.");
        if (pitch.PenaltyAreaWidth > pitch.Width)
            throw new ArgumentOutOfRangeException(nameof(pitch.PenaltyAreaWidth), pitch.PenaltyAreaWidth, "The penalty area cannot be wider than the pitch.");
        if (pitch.GoalAreaLength > pitch.PenaltyAreaLength)
            throw new ArgumentOutOfRangeException(nameof(pitch.GoalAreaLength), pitch.GoalAreaLength, "The goal area cannot be longer than the penalty area.");
        if (pitch.GoalAreaWidth > pitch.PenaltyAreaWidth)
            throw new ArgumentOutOfRangeException(nameof(pitch.GoalAreaWidth), pitch.GoalAreaWidth, "The goal area cannot be wider than the penalty area.");
        if (pitch.PenaltySpotDistance >= pitch.HalfLength)
            throw new ArgumentOutOfRangeException(nameof(pitch.PenaltySpotDistance), pitch.PenaltySpotDistance, "The penalty spot must be inside the pitch.");
        if (pitch.PenaltySpotDistance > pitch.PenaltyAreaLength)
            throw new ArgumentOutOfRangeException(
                nameof(pitch.PenaltySpotDistance), pitch.PenaltySpotDistance,
                "The penalty spot must be inside the penalty area, not beyond its depth.");
        if (pitch.CenterCircleRadius > MathF.Min(pitch.HalfLength, pitch.HalfWidth))
            throw new ArgumentOutOfRangeException(nameof(pitch.CenterCircleRadius), pitch.CenterCircleRadius, "The centre circle must fit on the pitch.");
    }

    /// <summary>Validates every advertised player attribute (finite, physically meaningful ranges).</summary>
    public static void ValidatePlayerAttributes(PlayerAttributes attributes, string name = "attributes")
    {
        ArgumentNullException.ThrowIfNull(attributes);

        if (!Enum.IsDefined(attributes.PreferredFoot))
            throw new ArgumentOutOfRangeException(name, attributes.PreferredFoot, $"{name}.PreferredFoot is not a valid foot.");

        RequirePositive(attributes.MaxSprintSpeed, $"{name}.MaxSprintSpeed");
        RequireFinite(attributes.Acceleration, $"{name}.Acceleration");
        if (attributes.Acceleration < 0f)
            throw new ArgumentOutOfRangeException($"{name}.Acceleration", attributes.Acceleration, "Acceleration cannot be negative.");
        RequirePositive(attributes.AgilityTurnRate, $"{name}.AgilityTurnRate");
        RequirePositive(attributes.StaminaCapacity, $"{name}.StaminaCapacity");
        Require01(attributes.FootAccuracy01, $"{name}.FootAccuracy01");
        RequirePositive(attributes.ShotPowerMax, $"{name}.ShotPowerMax");
        Require01(attributes.FirstTouch01, $"{name}.FirstTouch01");
        Require01(attributes.Balance01, $"{name}.Balance01");
        Require01(attributes.Strength01, $"{name}.Strength01");
        RequirePositive(attributes.ReachRadius, $"{name}.ReachRadius");
        RequireFinite(attributes.ReactionDelaySeconds, $"{name}.ReactionDelaySeconds");
        if (attributes.ReactionDelaySeconds < 0f)
            throw new ArgumentOutOfRangeException($"{name}.ReactionDelaySeconds", attributes.ReactionDelaySeconds, "ReactionDelaySeconds cannot be negative.");
        Require01(attributes.Composure01, $"{name}.Composure01");
        Require01(attributes.GoalkeepingReflex01, $"{name}.GoalkeepingReflex01");
    }

    /// <summary>Validates the nine behaviour tendencies (each is a 0..1 blend weight).</summary>
    public static void ValidateTendencies(PlayerTendencies tendencies, string name = "tendencies")
    {
        ArgumentNullException.ThrowIfNull(tendencies);

        Require01(tendencies.RiskTolerance01, $"{name}.RiskTolerance01");
        Require01(tendencies.DirectPassPreference01, $"{name}.DirectPassPreference01");
        Require01(tendencies.ShootingAppetite01, $"{name}.ShootingAppetite01");
        Require01(tendencies.DribblePreference01, $"{name}.DribblePreference01");
        Require01(tendencies.PositionalDiscipline01, $"{name}.PositionalDiscipline01");
        Require01(tendencies.PressingAggression01, $"{name}.PressingAggression01");
        // A7.6: the three archetype preference axes that are not covered by the six above.
        Require01(tendencies.CrossPreference01, $"{name}.CrossPreference01");
        Require01(tendencies.ScanAppetite01, $"{name}.ScanAppetite01");
        Require01(tendencies.SweepAggression01, $"{name}.SweepAggression01");
    }

    /// <summary>Validates a team tactical profile (every field feeds an AI utility).</summary>
    public static void ValidateTacticalProfile(TeamTacticalProfile profile, string name = "profile")
    {
        ArgumentNullException.ThrowIfNull(profile);

        Require01(profile.Directness01, $"{name}.Directness01");
        Require01(profile.Tempo01, $"{name}.Tempo01");
        Require01(profile.AttackingWidth01, $"{name}.AttackingWidth01");
        Require01(profile.DefensiveLineHeight01, $"{name}.DefensiveLineHeight01");
        Require01(profile.Compactness01, $"{name}.Compactness01");
        Require01(profile.PressingIntensity01, $"{name}.PressingIntensity01");
        Require01(profile.RiskTolerance01, $"{name}.RiskTolerance01");
        Require01(profile.PositionalFreedom01, $"{name}.PositionalFreedom01");
        // A9.1: the explicit zone/man axis; 0.50 is its neutral value.
        Require01(profile.ManOrientation01, $"{name}.ManOrientation01");
        RequireFinite(profile.CounterPressDurationSeconds, $"{name}.CounterPressDurationSeconds");
        if (profile.CounterPressDurationSeconds < 0f)
        {
            throw new ArgumentOutOfRangeException(
                $"{name}.CounterPressDurationSeconds", profile.CounterPressDurationSeconds,
                "CounterPressDurationSeconds cannot be negative.");
        }
    }

    /// <summary>
    /// Validates the ball physics parameters. The radius is shared with rendering
    /// (<see cref="BallGeometry"/>), so a degenerate value would also draw the wrong ball.
    /// </summary>
    public static void ValidateBallPhysics(BallPhysicsParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        RequirePositive(parameters.Mass, nameof(parameters.Mass));
        RequirePositive(parameters.Radius, nameof(parameters.Radius));
        RequirePositive(parameters.AirDensity, nameof(parameters.AirDensity));
        RequireNonNegative(parameters.DragCoeffSubcritical, nameof(parameters.DragCoeffSubcritical));
        RequireNonNegative(parameters.DragCoeffSupercritical, nameof(parameters.DragCoeffSupercritical));
        RequirePositive(parameters.CriticalSpeed, nameof(parameters.CriticalSpeed));
        RequireNonNegative(parameters.MagnusLiftScale, nameof(parameters.MagnusLiftScale));
        RequireNonNegative(parameters.MaxMagnusCoeff, nameof(parameters.MaxMagnusCoeff));
        RequireNonNegative(parameters.AirSpinDecayRate, nameof(parameters.AirSpinDecayRate));
        Require01(parameters.GroundRestitution, nameof(parameters.GroundRestitution));
        Require01(parameters.PostRestitution, nameof(parameters.PostRestitution));
        RequireNonNegative(parameters.SlidingFriction, nameof(parameters.SlidingFriction));
        RequireNonNegative(parameters.RollingResistance, nameof(parameters.RollingResistance));
        Require01(parameters.BounceTangentialGrip, nameof(parameters.BounceTangentialGrip));
        RequireNonNegative(parameters.RestBounceVelocityThreshold, nameof(parameters.RestBounceVelocityThreshold));
        RequireNonNegative(parameters.StopVelocityEpsilon, nameof(parameters.StopVelocityEpsilon));
    }

    private static void RequireFinite(float value, string name)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be a finite number.");
    }

    private static void RequirePositive(float value, string name)
    {
        RequireFinite(value, name);
        if (value <= 0f)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be greater than zero.");
    }

    private static void RequireNonNegative(float value, string name)
    {
        RequireFinite(value, name);
        if (value < 0f)
            throw new ArgumentOutOfRangeException(name, value, $"{name} cannot be negative.");
    }

    private static void Require01(float value, string name)
    {
        RequireFinite(value, name);
        if (value < 0f || value > 1f)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between 0 and 1.");
    }
}
