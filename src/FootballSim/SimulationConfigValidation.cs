using System;
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

    /// <summary>Validates the six behaviour tendencies (each is a 0..1 blend weight).</summary>
    public static void ValidateTendencies(PlayerTendencies tendencies, string name = "tendencies")
    {
        ArgumentNullException.ThrowIfNull(tendencies);

        Require01(tendencies.RiskTolerance01, $"{name}.RiskTolerance01");
        Require01(tendencies.DirectPassPreference01, $"{name}.DirectPassPreference01");
        Require01(tendencies.ShootingAppetite01, $"{name}.ShootingAppetite01");
        Require01(tendencies.DribblePreference01, $"{name}.DribblePreference01");
        Require01(tendencies.PositionalDiscipline01, $"{name}.PositionalDiscipline01");
        Require01(tendencies.PressingAggression01, $"{name}.PressingAggression01");
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
