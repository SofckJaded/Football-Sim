using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Scenarios;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A3.6 — rest defence and transition exposure.
///
/// The model answers "if we lost the ball here, how exposed are we?" from our own positions, the reach
/// model and what the team believes about the opponent. Its aggregate gates forward commitments, so the
/// second half of this file proves that an overlap is offered by a secure side and refused by an exposed
/// one — the "constraints according to transition exposure, rather than a fixed count behind the ball"
/// part of the milestone.
/// </summary>
public sealed class TransitionSecurityTests
{
    private static readonly PitchDimensions Pitch = new();

    private static FootballerState Player(
        int id, TeamId team, TacticalRole role, float x, float z, Vector3? velocity = null) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(role),
        Position = new Vector3(x, 0f, z),
        Velocity = velocity ?? Vector3.Zero,
        FacingXZ = new Vector2(1f, 0f)
    };

    private static BallState BallAt(float x, float z) => new()
    {
        Position = new Vector3(x, 0f, z),
        LastTouchedByTeam = TeamId.Home
    };

    private static TeamBelief BeliefAbout(IEnumerable<FootballerState> players) =>
        TeamBelief.FromTruth(TeamId.Home, players.ToList());

    private static RestDefence Evaluate(
        IReadOnlyList<FootballerState> players,
        BallState ball,
        TeamBelief opponents,
        float attackDirX = 1f) =>
        TransitionSecurityModel.Evaluate(players, TeamId.Home, ball, opponents, attackDirX, Pitch);

    [Fact]
    public void MorePlayersBehindTheBall_IsSafer()
    {
        var ball = BallAt(10f, 0f);
        TeamBelief noThreats = BeliefAbout(new[] { Player(20, TeamId.Away, TacticalRole.CenterBack, 45f, 0f) });

        var oneBack = new List<FootballerState>
        {
            Player(1, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f),
            Player(2, TeamId.Home, TacticalRole.CenterBack, 2f, 0f),
            Player(3, TeamId.Home, TacticalRole.CentralMidfielder, 20f, 6f),
            Player(4, TeamId.Home, TacticalRole.Winger, 24f, -8f)
        };

        var threeBack = new List<FootballerState>
        {
            Player(1, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f),
            Player(2, TeamId.Home, TacticalRole.CenterBack, 2f, 0f),
            Player(3, TeamId.Home, TacticalRole.CentralMidfielder, 4f, 6f),
            Player(4, TeamId.Home, TacticalRole.Winger, 6f, -8f)
        };

        RestDefence thin = Evaluate(oneBack, ball, noThreats);
        RestDefence sound = Evaluate(threeBack, ball, noThreats);

        Assert.Equal(1, thin.BehindBall);
        Assert.Equal(3, sound.BehindBall);
        Assert.True(sound.Security01 > thin.Security01 + 0.1f,
            $"three behind the ball ({sound.Security01:F2}) must be safer than one ({thin.Security01:F2})");
    }

    [Fact]
    public void AForwardWhoCannotGetBack_LowersSecurity_AndShowsInTheRecoveryTime()
    {
        var ball = BallAt(10f, 0f);
        TeamBelief noThreats = BeliefAbout(new[] { Player(20, TeamId.Away, TacticalRole.CenterBack, 45f, 0f) });

        var near = new List<FootballerState>
        {
            Player(1, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f),
            Player(2, TeamId.Home, TacticalRole.CenterBack, 0f, 0f),
            Player(3, TeamId.Home, TacticalRole.CentralMidfielder, 6f, 6f),
            Player(4, TeamId.Home, TacticalRole.Striker, 22f, -4f)
        };

        var far = new List<FootballerState>
        {
            Player(1, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f),
            Player(2, TeamId.Home, TacticalRole.CenterBack, 0f, 0f),
            Player(3, TeamId.Home, TacticalRole.CentralMidfielder, 6f, 6f),
            Player(4, TeamId.Home, TacticalRole.Striker, 46f, -4f)
        };

        RestDefence close = Evaluate(near, ball, noThreats);
        RestDefence stranded = Evaluate(far, ball, noThreats);

        Assert.True(stranded.WorstRecoverySeconds > close.WorstRecoverySeconds + 0.5f,
            $"a striker 36 m further upfield must take longer to recover " +
            $"({stranded.WorstRecoverySeconds:F1}s vs {close.WorstRecoverySeconds:F1}s)");
        Assert.True(close.Security01 > stranded.Security01,
            $"the stranded side ({stranded.Security01:F2}) must be less secure than the compact one ({close.Security01:F2})");
    }

    [Fact]
    public void AnUnmarkedCentralOpponent_RaisesCentralAccess_AndLowersSecurity()
    {
        var ball = BallAt(10f, 0f);
        var home = new List<FootballerState>
        {
            Player(1, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f),
            Player(2, TeamId.Home, TacticalRole.CenterBack, -30f, -16f),
            Player(3, TeamId.Home, TacticalRole.CentralMidfielder, 0f, 20f)
        };

        var marked = BeliefAbout(new[]
        {
            Player(20, TeamId.Away, TacticalRole.CentralMidfielder, -8f, 2f),
            Player(21, TeamId.Away, TacticalRole.CenterBack, 45f, 0f)
        });

        var unmarkedHome = new List<FootballerState>
        {
            Player(1, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f),
            Player(2, TeamId.Home, TacticalRole.CenterBack, -8.5f, 1.5f),   // standing on him
            Player(3, TeamId.Home, TacticalRole.CentralMidfielder, 0f, 20f)
        };

        RestDefence open = Evaluate(home, ball, marked);
        RestDefence covered = Evaluate(unmarkedHome, ball, marked);

        Assert.True(open.CentralAccess01 > covered.CentralAccess01 + 0.25f,
            $"an unmarked central opponent is more accessible ({open.CentralAccess01:F2}) than a marked one ({covered.CentralAccess01:F2})");
        Assert.True(open.Security01 < covered.Security01,
            $"the open side ({open.Security01:F2}) must be less secure than the covered one ({covered.Security01:F2})");
    }

    [Fact]
    public void OpponentsGoalSideOfTheBall_NearOurGoal_AreCountedAsThreats()
    {
        var ball = BallAt(10f, 0f);
        var home = new List<FootballerState>
        {
            Player(1, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f),
            Player(2, TeamId.Home, TacticalRole.CenterBack, 0f, 0f)
        };

        TeamBelief none = BeliefAbout(new[] { Player(20, TeamId.Away, TacticalRole.CenterBack, 45f, 0f) });
        TeamBelief two = BeliefAbout(new[]
        {
            Player(20, TeamId.Away, TacticalRole.CenterBack, 45f, 0f),
            Player(21, TeamId.Away, TacticalRole.Striker, -16f, 4f),
            Player(22, TeamId.Away, TacticalRole.Winger, -12f, -8f)
        });

        RestDefence quiet = Evaluate(home, ball, none);
        RestDefence threatened = Evaluate(home, ball, two);

        Assert.Equal(0f, quiet.Threat01);
        Assert.True(threatened.Threat01 >= 0.6f, $"two of three believed opponents are threats ({threatened.Threat01:F2})");
        Assert.True(threatened.Security01 < quiet.Security01);
    }

    [Fact]
    public void TheAggregate_IsAlwaysBounded_EvenWithNoPlayers()
    {
        var ball = BallAt(0f, 0f);
        RestDefence empty = TransitionSecurityModel.Evaluate(
            new List<FootballerState>(), TeamId.Home, ball, new TeamBelief(TeamId.Home), 1f, Pitch);

        Assert.Equal(0, empty.BehindBall);
        Assert.InRange(empty.Security01, 0f, 1f);
        Assert.InRange(empty.CentralAccess01, 0f, 1f);
        Assert.InRange(empty.OutletExposure01, 0f, 1f);
        Assert.InRange(empty.Threat01, 0f, 1f);
        Assert.True(float.IsFinite(empty.WorstRecoverySeconds));
        Assert.False(empty.AllowsForwardCommitment);
    }

    [Fact]
    public void TheEvaluation_IsDeterministic()
    {
        var players = new List<FootballerState>
        {
            Player(1, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f),
            Player(2, TeamId.Home, TacticalRole.CenterBack, 0f, 4f),
            Player(3, TeamId.Home, TacticalRole.Winger, 28f, -20f)
        };
        var ball = BallAt(12f, -6f);
        TeamBelief belief = BeliefAbout(new[]
        {
            Player(20, TeamId.Away, TacticalRole.Striker, -6f, 2f),
            Player(21, TeamId.Away, TacticalRole.Winger, 6f, 18f)
        });

        Assert.Equal(Evaluate(players, ball, belief), Evaluate(players, ball, belief));
    }

    // ---- The gate: forward commitments follow transition exposure -------------------------------

    [Fact]
    public void TheForwardAllowance_FollowsTheSecurityPicture()
    {
        static RestDefence At(float security) => new(2, 1f, 0.2f, 0.2f, 0.1f, security);

        Assert.Equal(0, At(TransitionSecurityModel.MinSecurityForForwardRun - 0.05f).ForwardAllowance);
        Assert.False(At(TransitionSecurityModel.MinSecurityForForwardRun - 0.05f).AllowsForwardCommitment);

        Assert.Equal(1, At(TransitionSecurityModel.MinSecurityForForwardRun + 0.05f).ForwardAllowance);
        Assert.Equal(1, At(TransitionSecurityModel.SecureForTwoForwardRuns - 0.05f).ForwardAllowance);

        Assert.Equal(2, At(TransitionSecurityModel.SecureForTwoForwardRuns + 0.05f).ForwardAllowance);
        Assert.Equal(2, At(1f).ForwardAllowance);
    }

    /// <summary>
    /// An eleven-a-side possession shape: a carrier at (0, 10), a back four, and an away high block. The
    /// same world is used twice below, once with the back line at home and once with it pushed into the
    /// opposition half, so the only difference is the transition picture.
    /// </summary>
    private static (List<FootballerState> Players, BallState Ball) ElevenInPossession()
    {
        var players = new List<FootballerState>();

        static void Add(List<FootballerState> list, int id, TeamId team, TacticalRole role, float x, float z)
        {
            FootballerState player = Player(id, team, role, x, z);
            player.TacticalAnchor = player.Position;
            list.Add(player);
        }

        Add(players, 1, TeamId.Home, TacticalRole.Goalkeeper, -50f, 0f);
        Add(players, 2, TeamId.Home, TacticalRole.FullBack, -8f, 18f);
        Add(players, 3, TeamId.Home, TacticalRole.FullBack, -8f, -18f);
        Add(players, 4, TeamId.Home, TacticalRole.CenterBack, -20f, 6f);
        Add(players, 5, TeamId.Home, TacticalRole.CenterBack, -20f, -6f);
        Add(players, 6, TeamId.Home, TacticalRole.DefensiveMidfielder, -6f, 2f);
        Add(players, 7, TeamId.Home, TacticalRole.Winger, 10f, 26f);
        Add(players, 8, TeamId.Home, TacticalRole.CentralMidfielder, 0f, 10f);   // carrier
        Add(players, 9, TeamId.Home, TacticalRole.Striker, 18f, 0f);
        Add(players, 10, TeamId.Home, TacticalRole.CentralMidfielder, 6f, -12f);
        Add(players, 11, TeamId.Home, TacticalRole.Winger, 10f, -26f);

        Add(players, 12, TeamId.Away, TacticalRole.Goalkeeper, 50f, 0f);
        Add(players, 13, TeamId.Away, TacticalRole.CenterBack, 16f, 6f);
        Add(players, 14, TeamId.Away, TacticalRole.CenterBack, 20f, -6f);
        Add(players, 15, TeamId.Away, TacticalRole.DefensiveMidfielder, 12f, 8f);
        Add(players, 16, TeamId.Away, TacticalRole.FullBack, 26f, 20f);
        Add(players, 17, TeamId.Away, TacticalRole.FullBack, 26f, -20f);
        Add(players, 18, TeamId.Away, TacticalRole.CentralMidfielder, 14f, -4f);
        Add(players, 19, TeamId.Away, TacticalRole.Winger, 30f, 18f);
        Add(players, 20, TeamId.Away, TacticalRole.Winger, 30f, -18f);
        Add(players, 21, TeamId.Away, TacticalRole.Striker, 34f, 0f);
        Add(players, 22, TeamId.Away, TacticalRole.CentralMidfielder, 22f, 0f);

        return (players, new BallState
        {
            Position = new Vector3(0f, 0.11f, 10f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 8
        });
    }

    private static TeamJobCoordinator RunJobs(List<FootballerState> players, BallState ball)
    {
        var jobs = new TeamJobCoordinator(Pitch);
        jobs.Update(
            players, ball,
            new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, 8, null),
            possessingTeam: TeamId.Home,
            homeResponderId: null, awayResponderId: 15,
            homeDirX: 1f, tick: 0,
            TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
            TeamBelief.FromTruth(TeamId.Home, players), TeamBelief.FromTruth(TeamId.Away, players),
            new TeamFormation(TeamId.Home, Pitch), new TeamFormation(TeamId.Away, Pitch));
        return jobs;
    }

    [Fact]
    public void ATrustfulPossessionShape_CommitsItsBoxRunAndOverlap()
    {
        (List<FootballerState> players, BallState ball) = ElevenInPossession();
        TeamJobCoordinator jobs = RunJobs(players, ball);

        RestDefence security = jobs.SecurityOf(TeamId.Home);
        Assert.True(security.ForwardAllowance >= 2,
            $"the possession shape scored {security.Security01:F2}, allowance {security.ForwardAllowance}");

        Assert.Contains(jobs.Assignments.Values, a => a.Job == TacticalJob.Overlap);
        Assert.Contains(jobs.Assignments.Values, a => a.Job == TacticalJob.DepthRun);
    }

    [Fact]
    public void AnExposedShape_WithdrawsItsBoxRunAndOverlap()
    {
        (List<FootballerState> players, BallState ball) = ElevenInPossession();

        // Same shape, but the back six are up in the opposition half and the away side has two runners
        // loose in the middle: nothing behind the ball, nothing to recover with, an open centre.
        MoveTo(players, 2, 34f, 18f);
        MoveTo(players, 3, 34f, -18f);
        MoveTo(players, 4, 32f, 6f);
        MoveTo(players, 5, 32f, -6f);
        MoveTo(players, 6, 30f, 2f);
        MoveTo(players, 21, 2f, 4f);      // away striker, central and goal-side of the ball
        MoveTo(players, 22, 4f, -8f);     // away midfielder, central and goal-side of the ball

        TeamJobCoordinator jobs = RunJobs(players, ball);
        RestDefence security = jobs.SecurityOf(TeamId.Home);

        Assert.Equal(0, security.ForwardAllowance);
        Assert.DoesNotContain(jobs.Assignments.Values, a => a.Job == TacticalJob.Overlap);
        Assert.DoesNotContain(jobs.Assignments.Values, a => a.Job == TacticalJob.DepthRun);

        // The constraint is on the forward commitments, not on the attacking structure as a whole.
        Assert.Contains(jobs.Assignments.Values, a => a.Job == TacticalJob.NearOutlet);
        Assert.Contains(jobs.Assignments.Values, a => a.Job == TacticalJob.FarSideWidth);
    }

    private static void MoveTo(List<FootballerState> players, int playerId, float x, float z)
    {
        FootballerState player = players.First(p => p.Id == playerId);
        player.Position = new Vector3(x, 0f, z);
        player.TacticalAnchor = player.Position;
    }
}