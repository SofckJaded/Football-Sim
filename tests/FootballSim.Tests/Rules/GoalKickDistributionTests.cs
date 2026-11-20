using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Rules;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.Rules;

/// <summary>
/// M5 — the keeper's goal-kick exemption is read from the <b>armed restart</b>, not from the match phase.
///
/// A restart's pause runs with the phase set to the restart and is reset before the first live tick, so a
/// controller never observes <c>MatchPhase.GoalKick</c>: the phase test that guarded the distribution gate was
/// dead code and the gate was on for the one restart Law 11 exempts. These fixtures pin both halves — the
/// phase alone must not exempt, and the armed goal kick must.
/// </summary>
public sealed class GoalKickDistributionTests
{
    private static readonly PitchDimensions Pitch = new();

    private const int KeeperId = 1;
    private const int MateId = 6;

    [Fact]
    public void TheExemption_FollowsTheArmedRestart_NotThePhase()
    {
        Assert.True(GoalkeeperDecisionModel.IsGoalKickRestart(Observe(RestartKind.GoalKick, MatchPhase.FirstHalf)),
            "an armed goal kick is the restart the receiver exemption belongs to");

        // The exact shape of the defect: the rules engine sets the phase and the pause resets it to the half
        // before the first live decision, so the phase alone never carries the restart.
        Assert.False(
            GoalkeeperDecisionModel.IsGoalKickRestart(Observe(RestartKind.None, MatchPhase.GoalKick)),
            "a goal-kick phase with no armed restart is the dead case this rule replaced");

        Assert.False(GoalkeeperDecisionModel.IsGoalKickRestart(Observe(RestartKind.None, MatchPhase.FirstHalf)));
    }

    /// <summary>The exemption is the kicking team's; the opposition keeper of the same fixture is not exempt.</summary>
    [Fact]
    public void TheExemption_IsOnlyForTheKickingTeam()
    {
        FootballerObservation homeKeeper = Observe(RestartKind.GoalKick, MatchPhase.FirstHalf);
        FootballerObservation awayKeeper = Observe(RestartKind.GoalKick, MatchPhase.FirstHalf, away: true);

        Assert.True(GoalkeeperDecisionModel.IsGoalKickRestart(homeKeeper));
        Assert.False(GoalkeeperDecisionModel.IsGoalKickRestart(awayKeeper));
    }

    private static FootballerObservation Observe(RestartKind restart, MatchPhase phase, bool away = false)
    {
        TeamId selfTeam = away ? TeamId.Away : TeamId.Home;
        var keeper = new FootballerState
        {
            Id = away ? KeeperId + 1 : KeeperId,
            Team = selfTeam,
            ShirtNumber = 1,
            Role = TacticalRole.Goalkeeper,
            Attributes = new PlayerAttributes(),
            Tendencies = PlayerTendencyProfile.ForRole(TacticalRole.Goalkeeper),
            Position = new Vector3(-46.5f, 0f, 6f),
            FacingXZ = new Vector2(1f, 0f)
        };

        var visible = new List<PerceivedEntity>
        {
            new(20, TeamId.Away, TacticalRole.Striker, new Vector3(-30f, 0f, -26f), Vector3.Zero, 1f),
            new(MateId, selfTeam, TacticalRole.CentralMidfielder, new Vector3(-34f, 0f, 8f), Vector3.Zero, 1f)
        };

        return new FootballerObservation
        {
            SelfId = keeper.Id,
            SelfTeam = selfTeam,
            SelfRole = keeper.Role,
            SelfPosition = keeper.Position,
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = keeper.FacingXZ,
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            IsSetPieceTaker = restart == RestartKind.GoalKick,
            PerceivedBallPosition = new Vector3(-46f, 0.11f, 6f),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = selfTeam,
            BallLastTouchedByPlayerId = keeper.Id,
            BallLastTouchedByTeam = selfTeam,
            BallLastPlay = BallLastPlay.None,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = keeper.Id,
            TacticalAnchorPosition = keeper.Position,
            TacticalJob = TacticalJob.Shape,
            AttackingDirectionX = away ? -1f : 1f,
            MatchTimeSeconds = 30f,
            CurrentMatchPhase = phase,
            Tick = 900,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = keeper.Tendencies,
            SelfAttributes = keeper.Attributes,
            TacticalContext = new TeamContext(
                selfTeam, selfTeam, PossessionStability.Controlled, TeamPhase.Attacking,
                TeamZone.OwnThird, TransitionMode.None,
                restart == RestartKind.GoalKick
                    ? new RestartContext(restart, TeamId.Home)
                    : RestartContext.None,
                false)
        };
    }
}
