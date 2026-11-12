using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;

namespace FootballSim.Tactics;

public enum TeamTacticalState
{
    InPossessionBuildUp,
    InPossessionFinalThird,
    DefensiveBlock,
    CounterPressTransition,
    CounterAttackTransition
}

public sealed class TeamTacticalCoordinator
{
    private readonly PitchDimensions _pitch;
    private TeamId? _confirmedPossessingTeam = TeamId.Home;
    private float _transitionTimer;
    private float _homeStateLockTimer;
    private float _awayStateLockTimer;
    private float _elapsedTime;

    public TeamTacticalCoordinator(PitchDimensions pitch)
    {
        _pitch = pitch;
    }

    public TeamId? ConfirmedPossessingTeam => _confirmedPossessingTeam;
    public TeamTacticalState HomeState { get; private set; } = TeamTacticalState.InPossessionBuildUp;
    public TeamTacticalState AwayState { get; private set; } = TeamTacticalState.DefensiveBlock;

    /// <summary>
    /// Confirms team possession from the authoritative control model instead of the last touch
    /// (audit F10). Control changes are applied immediately; a loose ball keeps the team that had
    /// it, so a deflection or an unreachable clearance cannot hand possession to the team that
    /// merely brushed the ball. Call this every live tick.
    /// </summary>
    public void ObservePossession(BallPossessionSnapshot possession, float dt)
    {
        // Controlled, keeper-held, an in-flight pass, and a loose ball all carry the team that has
        // the right to the ball. For a loose ball that is the team that last *deliberately played*
        // it — a deflection/parry leaves the previous team's claim intact (audit F10).
        TeamId? authoritativeTeam = possession.PossessionTeam;

        if (authoritativeTeam.HasValue)
        {
            if (authoritativeTeam != _confirmedPossessingTeam)
            {
                _confirmedPossessingTeam = authoritativeTeam;
                _transitionTimer = 0f;
            }
            else
            {
                _transitionTimer += dt;
            }
        }
        else
        {
            // Loose: nobody is in control. Never promote the last toucher from here.
            _transitionTimer += dt;
        }
    }

    /// <summary>
    /// Declares the confirmed possessing team directly. Only scenario fragments need this: a real
    /// match learns possession from the ownership tracker on the first live tick, but a fragment that
    /// starts mid-play must not spend its first ticks acting as if the other team had the ball.
    /// </summary>
    public void SeedPossession(TeamId? team)
    {
        _confirmedPossessingTeam = team;
        _transitionTimer = 0f;
    }

    public void UpdateTeamAnchors(
        IReadOnlyList<FootballerState> players,
        BallState ball,
        TeamTacticalProfile homeProfile,
        TeamTacticalProfile awayProfile,
        float homeAttackDirX,
        float dt,
        int teamSize)
    {
        _elapsedTime += dt;
        _homeStateLockTimer = MathF.Max(0f, _homeStateLockTimer - dt);
        _awayStateLockTimer = MathF.Max(0f, _awayStateLockTimer - dt);

        // Update Home and Away tactical states with hysteresis & minimum dwell time (3.5s)
        var desiredHome = ResolveDesiredState(TeamId.Home, HomeState, ball, homeProfile, homeAttackDirX);
        if (desiredHome != HomeState && _homeStateLockTimer <= 0f)
        {
            HomeState = desiredHome;
            _homeStateLockTimer = 3.5f;
        }

        var desiredAway = ResolveDesiredState(TeamId.Away, AwayState, ball, awayProfile, -homeAttackDirX);
        if (desiredAway != AwayState && _awayStateLockTimer <= 0f)
        {
            AwayState = desiredAway;
            _awayStateLockTimer = 3.5f;
        }

        foreach (var p in players)
        {
            if (!p.IsActiveOnPitch) continue;
            var profile = p.Team == TeamId.Home ? homeProfile : awayProfile;
            var state = p.Team == TeamId.Home ? HomeState : AwayState;
            float attackDirX = p.Team == TeamId.Home ? homeAttackDirX : -homeAttackDirX;

            p.TacticalAnchor = ComputePlayerAnchor(p, teamSize, ball, profile, state, attackDirX);
        }
    }

    private TeamTacticalState ResolveDesiredState(
        TeamId team,
        TeamTacticalState currentState,
        BallState ball,
        TeamTacticalProfile profile,
        float attackDirX)
    {
        bool hasBall = _confirmedPossessingTeam == team;
        float ballProgressX = ball.Position.X * attackDirX;

        if (hasBall)
        {
            if (_transitionTimer < 3.0f && profile.Directness01 > 0.65f)
                return TeamTacticalState.CounterAttackTransition;

            // Spatial hysteresis deadband: must cross +20m to enter FinalThird, and drop below +5m to revert to BuildUp
            float enterFinalThirdX = _pitch.HalfLength * 0.36f; // ~18.9m
            float exitFinalThirdX = _pitch.HalfLength * 0.12f;  // ~6.3m

            if (currentState == TeamTacticalState.InPossessionFinalThird)
                return ballProgressX < exitFinalThirdX ? TeamTacticalState.InPossessionBuildUp : TeamTacticalState.InPossessionFinalThird;

            return ballProgressX > enterFinalThirdX ? TeamTacticalState.InPossessionFinalThird : TeamTacticalState.InPossessionBuildUp;
        }
        else
        {
            if (_transitionTimer < profile.CounterPressDurationSeconds && profile.PressingIntensity01 > 0.60f)
                return TeamTacticalState.CounterPressTransition;

            return TeamTacticalState.DefensiveBlock;
        }
    }

    private Vector3 ComputePlayerAnchor(
        FootballerState player,
        int teamSize,
        BallState ball,
        TeamTacticalProfile profile,
        TeamTacticalState state,
        float attackDirX)
    {
        if (player.Role == TacticalRole.Goalkeeper)
        {
            float gkBaseX = -attackDirX * (_pitch.HalfLength - 2.5f);
            float sweepOffset = state is TeamTacticalState.InPossessionBuildUp or TeamTacticalState.InPossessionFinalThird
                ? attackDirX * (4.5f * profile.DefensiveLineHeight01)
                : 0f;
            float gkZ = Math.Clamp(ball.Position.Z * 0.14f, -3.1f, 3.1f);
            return new Vector3(gkBaseX + sweepOffset, 0f, gkZ);
        }

        (float baseNormX, float baseNormZ) = GetBaseFormationSlot(player.ShirtNumber, teamSize);

        bool attacking = state is TeamTacticalState.InPossessionBuildUp
            or TeamTacticalState.InPossessionFinalThird
            or TeamTacticalState.CounterAttackTransition;

        float ballNormX = Math.Clamp((ball.Position.X * attackDirX) / _pitch.HalfLength, -1f, 1f);
        float lineShift = (profile.DefensiveLineHeight01 - 0.5f) * 18.0f;
        float longitudinalCompression = attacking ? (1.0f - 0.22f * profile.Compactness01) : (1.0f - 0.45f * profile.Compactness01);

        // Role-specific dynamic forward runs when in possession
        float roleAttackPush = 0f;
        if (attacking)
        {
            roleAttackPush = player.Role switch
            {
                TacticalRole.Striker => 10.0f + 5.0f * MathF.Sin(_elapsedTime * 1.4f + player.Id),
                TacticalRole.Winger => 9.0f + 4.5f * MathF.Cos(_elapsedTime * 1.3f + player.Id),
                TacticalRole.CentralMidfielder => 6.5f + 4.0f * MathF.Sin(_elapsedTime * 1.1f + player.Id * 0.7f),
                TacticalRole.FullBack => 7.0f * profile.AttackingWidth01,
                _ => 2.5f
            };
        }

        float worldX = attackDirX * (
            baseNormX * (_pitch.HalfLength * 0.78f) * longitudinalCompression +
            ballNormX * 11.5f +
            lineShift +
            (attacking ? roleAttackPush * 0.65f : -4.0f));

        float widthScale = attacking
            ? (0.72f + 0.25f * profile.AttackingWidth01)
            : (0.70f - 0.15f * profile.Compactness01);

        // Keep players in their horizontal lanes instead of collapsing onto the ball's Z coordinate
        float ballLateralShift = attacking
            ? ball.Position.Z * 0.08f
            : ball.Position.Z * 0.12f;

        float worldZ = baseNormZ * _pitch.HalfWidth * widthScale + ballLateralShift;

        worldX = Math.Clamp(worldX, -_pitch.HalfLength + 4.0f, _pitch.HalfLength - 4.0f);
        worldZ = Math.Clamp(worldZ, -_pitch.HalfWidth + 2.5f, _pitch.HalfWidth - 2.5f);

        return new Vector3(worldX, 0f, worldZ);
    }

    public static (TacticalRole Role, float NormX, float NormZ) GetFormationRoleAndSlot(int shirtNumber, int teamSize)
    {
        if (shirtNumber == 1) return (TacticalRole.Goalkeeper, -0.95f, 0.0f);

        if (teamSize <= 5)
        {
            return shirtNumber switch
            {
                2 => (TacticalRole.CenterBack, -0.45f, 0.0f),
                3 => (TacticalRole.Winger, 0.0f, -0.55f),
                4 => (TacticalRole.Winger, 0.0f, 0.55f),
                _ => (TacticalRole.Striker, 0.45f, 0.0f)
            };
        }

        return shirtNumber switch
        {
            2 => (TacticalRole.FullBack, -0.48f, 0.72f),
            3 => (TacticalRole.FullBack, -0.48f, -0.72f),
            4 => (TacticalRole.CenterBack, -0.55f, 0.26f),
            5 => (TacticalRole.CenterBack, -0.55f, -0.26f),
            6 => (TacticalRole.DefensiveMidfielder, -0.22f, 0.0f),
            7 => (TacticalRole.Winger, 0.38f, 0.75f),
            8 => (TacticalRole.CentralMidfielder, 0.02f, 0.34f),
            9 => (TacticalRole.Striker, 0.52f, 0.0f),
            10 => (TacticalRole.CentralMidfielder, 0.06f, -0.34f),
            11 => (TacticalRole.Winger, 0.38f, -0.75f),
            _ => (TacticalRole.CentralMidfielder, 0.0f, 0.0f)
        };
    }

    private static (float NormX, float NormZ) GetBaseFormationSlot(int shirtNumber, int teamSize)
    {
        var (_, nx, nz) = GetFormationRoleAndSlot(shirtNumber, teamSize);
        return (nx, nz);
    }
}
