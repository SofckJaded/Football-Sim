using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Rules;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A6.5 at the decision level, and the A6 exit gate's keeper clause: <b>keeper and striker arrival
/// perturbations change sweep choices sensibly, and infeasible sweeps are declined</b>.
///
/// Every fixture is the keeper's own observation — a believed ball, believed opponents, the one reach model — and
/// the ball's real trajectory rolled from its believed state. The model's own physics lives in
/// <see cref="BallTrajectoryTests"/>; this file proves the two comparisons and that the controller acts on them.
/// </summary>
public sealed class GoalkeeperDecisionTests
{
    private static readonly PitchDimensions Pitch = new();

    private const int KeeperId = 1;
    private const int StrikerId = 15;
    private const int MateId = 6;

    // ---------------------------------------------------------------------------------------------
    // The gate clause: arrival perturbations.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The same ball, the same keeper, the only difference a striker's distance. A striker already between the
    /// ball and the goal wins the race and the keeper stays on his line; a striker chasing from behind cannot
    /// catch the ball and the keeper sweeps. "Sensibly" is the point: the choice follows the arrival, not a
    /// radius.
    /// </summary>
    [Fact]
    public void SweepChoices_RespondToTheStrikerArrival()
    {
        BallState ball = RollingInward();

        GoalkeeperRace beaten = EvaluateSweep(ball, strikerXZ: new Vector2(-42f, 3f));
        GoalkeeperRace free = EvaluateSweep(ball, strikerXZ: new Vector2(-20f, 3f));

        Assert.True(free.ShouldGo,
            $"a striker 22 m behind the ball did not leave the keeper free to sweep: {free.Describe()}");
        Assert.Equal(GoalkeeperRaceReason.KeeperFirst, free.Reason);

        Assert.False(beaten.ShouldGo,
            $"a striker standing on the ball's path did not beat the keeper to it: {beaten.Describe()}");
        Assert.True(beaten.Reason is GoalkeeperRaceReason.OpponentFirst
                or GoalkeeperRaceReason.Contested
                or GoalkeeperRaceReason.KeeperCannotReach,
            $"the decline was reported as {beaten.Reason}");
        Assert.True(beaten.OpponentSeconds < free.OpponentSeconds,
            $"the near striker ({beaten.OpponentSeconds:F2} s) must arrive before the far one " +
            $"({free.OpponentSeconds:F2} s)");

        // Both worlds run the identical model over the identical ball: only the belief differs.
        Assert.True(free.KeeperSeconds < float.MaxValue);
    }

    /// <summary>
    /// An infeasible sweep is declined with a stated reason: the ball is outside the area, or simply further
    /// than the keeper will travel for a race he has to sprint to, or it is not the opponents' ball at all.
    /// Evaluated on the model directly, because the controller never even asks the question in those cases.
    /// </summary>
    [Fact]
    public void InfeasibleSweeps_AreDeclinedWithAReason()
    {
        // Outside the penalty area: 20 m from the goal line the area is 16.5 m deep.
        GoalkeeperRace outsideRace = ModelSweep(RollingInward(position: new Vector3(-30f, 0.11f, 3f)));
        Assert.False(outsideRace.ShouldGo);
        Assert.Equal(GoalkeeperRaceReason.OutsideArea, outsideRace.Reason);

        // Inside the area but beyond the keeper's commit range.
        GoalkeeperRace farRace = ModelSweep(
            RollingInward(position: new Vector3(-37f, 0.11f, 3f)), keeperXZ: new Vector2(-52f, 0f));
        Assert.False(farRace.ShouldGo);
        Assert.Equal(GoalkeeperRaceReason.OutOfRange, farRace.Reason);

        // A friendly loose ball is never charged: the keeper's own back-pass is played with the feet.
        GoalkeeperRace friendly = ModelSweep(RollingInward(), possessingTeam: TeamId.Home);
        Assert.False(friendly.ShouldGo);
        Assert.Equal(GoalkeeperRaceReason.NotTheOpponentsBall, friendly.Reason);

        // No prediction, no sweeping: a guess is worse than staying home.
        GoalkeeperRace unpredicted = ModelSweep(RollingInward(), buildTrajectory: false);
        Assert.False(unpredicted.ShouldGo);
        Assert.Equal(GoalkeeperRaceReason.NoTrajectory, unpredicted.Reason);
    }

    // ---------------------------------------------------------------------------------------------
    // The shot: dive only for a ball that is going in and can be reached.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A shot on target that the body can reach is dived for, and the dive goes to the ball's own interception
    /// point rather than to a fixed spot on the goal line.
    /// </summary>
    [Fact]
    public void AReachableShot_IsDivedFor_AtTheInterceptionPoint()
    {
        var controller = new GoalkeeperController(Pitch);
        FootballerState keeperState = Keeper(new Vector3(-50f, 0f, 0f));
        BallState ball = ShotFrom(new Vector3(-40f, 0.11f, -2f), new Vector3(-20f, 0f, 2f));

        FootballerCommand command = controller.Decide(
            Observe(keeperState, ball, possession: TeamId.Away), dt: 1f / 60f);

        Assert.True(controller.LastShot.IsOnTarget, "the shot was not recognised as on target");
        Assert.True(controller.LastShot.CanIntercept, "a shot at the keeper was declared out of reach");
        Assert.True(command.RequestKeeperDive, "the keeper did not dive for a reachable shot");
        Assert.StartsWith("GK:DiveSave", controller.GetDebugSummary());

        // The dive is aimed at a point on the ball's path, inside the goal mouth, in front of the line.
        Vector3 aim = keeperState.Position + command.KeeperDiveDirection;
        Assert.InRange(aim.X, -Pitch.HalfLength, -Pitch.HalfLength + 12f);
        Assert.InRange(aim.Z, -Pitch.HalfGoalWidth, Pitch.HalfGoalWidth);
    }

    /// <summary>
    /// The other side of the rule: a shot that is on target but aimed at the far corner is not dived for. The old
    /// gate dived whenever the ball was heading at the goal, whether or not the body could get there — a late dive
    /// is a goal with an empty net behind it.
    /// </summary>
    [Fact]
    public void AShotBeyondReach_IsNotDivedFor()
    {
        var controller = new GoalkeeperController(Pitch);
        // Standing at one post, with the shot going in at the other: about 6.5 m across, in six tenths of a second.
        FootballerState keeperState = Keeper(new Vector3(-50f, 0f, 3.4f));
        BallState ball = ShotFrom(new Vector3(-41f, 0.11f, -2.5f), new Vector3(-18f, 0f, -1f));

        FootballerCommand command = controller.Decide(
            Observe(keeperState, ball, possession: TeamId.Away), dt: 1f / 60f);

        Assert.True(controller.LastShot.IsOnTarget, "the fixture is meant to be a shot on target");
        Assert.False(controller.LastShot.CanIntercept,
            $"a shot with a {controller.LastShot.ClosestApproachMeters:F1} m nearest approach was declared " +
            "interceptable");
        Assert.False(command.RequestKeeperDive, "the keeper dived for a shot it could not reach");
        Assert.StartsWith("GK:AngleBisect", controller.GetDebugSummary());
    }

    /// <summary>
    /// A6.6: reach is not permission. The same shot, the same keeper, the keeper's distance from his goal
    /// the only difference: standing inside his area the body can reach the ball's path and the hands may
    /// use it, so he dives; standing just outside the area the body can still reach it, but handling it
    /// would be an offence, so he must not.
    /// </summary>
    [Fact]
    public void AShotReachableOnlyOutsideTheArea_IsNotDivedFor()
    {
        // A 30 m/s shot from 19.5 m out crosses the goal line inside the 0.9 s horizon. The area line is
        // 16.5 m from the goal, so -36.6 m is inside it and -34.2 m is 2 m outside: the ball passes each
        // keeper at a sample the body can reach, and only the legality of the handling differs.
        var controller = new GoalkeeperController(Pitch);
        BallState shot = ShotFrom(new Vector3(-33f, 0.11f, 0.6f), new Vector3(-30f, 0f, 0f));

        FootballerState insideKeeper = Keeper(new Vector3(-36.6f, 0f, 1.8f));
        FootballerCommand inside = controller.Decide(
            Observe(insideKeeper, shot, TeamId.Away), dt: 1f / 60f);

        Assert.True(controller.LastShot.IsOnTarget, controller.LastShot.Describe());
        Assert.True(controller.LastShot.CanIntercept, controller.LastShot.Describe());
        Assert.Equal(GoalkeeperHandlingReason.Allowed, controller.LastShot.HandlingBlocked);
        Assert.True(inside.RequestKeeperDive, "the keeper did not dive for a shot he may handle");

        FootballerState outsideKeeper = Keeper(new Vector3(-34.2f, 0f, 1.8f));
        FootballerCommand outside = controller.Decide(
            Observe(outsideKeeper, shot, TeamId.Away), dt: 1f / 60f);

        Assert.True(controller.LastShot.IsOnTarget, controller.LastShot.Describe());
        Assert.True(controller.LastShot.CanIntercept,
            $"the fixture needs a physically reachable ball: {controller.LastShot.Describe()}");
        Assert.Equal(GoalkeeperHandlingReason.OutsideOwnPenaltyArea, controller.LastShot.HandlingBlocked);
        Assert.False(controller.LastShot.CanSave, "a shot outside the area was declared saveable");
        Assert.False(outside.RequestKeeperDive, "the keeper dived for a ball he may not handle");
        Assert.DoesNotContain("DiveSave", controller.GetDebugSummary());
    }

    /// <summary>
    /// A6.6: the handling gate is the same one the execution layer applies. A ball at 1.8 m is above the
    /// keeper's feet and inside his hands — an opponent's ball he plays, a team-mate's deliberate kick he
    /// must leave alone (his feet cannot reach it), which is exactly the "played with the feet" rule.
    /// </summary>
    [Fact]
    public void ABackPassAtHandlingHeight_IsNotGathered()
    {
        var controller = new GoalkeeperController(Pitch);
        FootballerState keeperState = Keeper(new Vector3(-51.9f, 0f, 0f));
        BallState ball = ShotFrom(new Vector3(-51.3f, 1.8f, 0f), Vector3.Zero);

        FootballerCommand opponentsBall = controller.Decide(
            Observe(keeperState, ball, TeamId.Away), dt: 1f / 60f);
        Assert.True(controller.LastHandling.MayUseHands, controller.LastHandling.Describe());
        Assert.NotEqual(KickIntentType.None, opponentsBall.KickType);

        var backPass = new BallLastPlay(BallContactKind.Kick, KickIntentType.GroundPass, MateId, TeamId.Home);
        FootballerCommand matesBall = controller.Decide(
            Observe(keeperState, ball, TeamId.Home, lastPlay: backPass), dt: 1f / 60f);

        Assert.False(controller.LastHandling.MayUseHands);
        Assert.Equal(GoalkeeperHandlingReason.TeamMateDeliberateKick, controller.LastHandling.Reason);
        Assert.Equal(KickIntentType.None, matesBall.KickType);
    }

    /// <summary>
    /// Nothing carries over between decisions: a shot evaluated last tick must not still be "on target" after
    /// the ball has become a slow loose ball somewhere else.
    /// </summary>
    [Fact]
    public void TheDecisionsDoNotCarryOverBetweenTicks()
    {
        var controller = new GoalkeeperController(Pitch);
        FootballerState keeperState = Keeper(new Vector3(-50f, 0f, 0f));

        controller.Decide(Observe(keeperState,
            ShotFrom(new Vector3(-40f, 0.11f, -2f), new Vector3(-20f, 0f, 2f)), TeamId.Away), 1f / 60f);
        Assert.True(controller.LastShot.IsOnTarget);

        BallState settled = ShotFrom(new Vector3(-20f, 0.11f, 6f), Vector3.Zero);
        controller.Decide(Observe(keeperState, settled, TeamId.Home), 1f / 60f);

        Assert.False(controller.LastShot.IsOnTarget, "last tick's shot is still reported as this tick's");
        Assert.False(controller.LastSweep.ShouldGo, "last tick's sweep is still reported as this tick's");
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The same ball every sweep fixture shares: rolling into the box from the corner of the area.</summary>
    private static BallState RollingInward(Vector3? position = null) => ShotFrom(
        position ?? new Vector3(-38f, 0.11f, 3f),
        new Vector3(-6f, 0f, -0.3f));

    private static BallState ShotFrom(Vector3 position, Vector3 velocity) => new()
    {
        Position = position,
        PreviousPosition = position,
        Velocity = velocity
    };

    private static FootballerState Keeper(Vector3 position) => new()
    {
        Id = KeeperId,
        Team = TeamId.Home,
        ShirtNumber = 1,
        Role = TacticalRole.Goalkeeper,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(TacticalRole.Goalkeeper),
        Position = position,
        Velocity = Vector3.Zero,
        FacingXZ = new Vector2(1f, 0f)
    };

    /// <summary>One keeper's observation of the ball and of one believed striker.</summary>
    private static FootballerObservation Observe(
        FootballerState keeper,
        BallState ball,
        TeamId possession,
        Vector2? strikerXZ = null,
        BallLastPlay? lastPlay = null)
    {
        var visible = new List<PerceivedEntity>
        {
            new(StrikerId, TeamId.Away, TacticalRole.Striker,
                new Vector3(strikerXZ?.X ?? -20f, 0f, strikerXZ?.Y ?? 3f),
                Vector3.Zero, 1f)
        };

        return new FootballerObservation
        {
            SelfId = keeper.Id,
            SelfTeam = keeper.Team,
            SelfRole = keeper.Role,
            SelfPosition = keeper.Position,
            SelfVelocity = keeper.Velocity,
            SelfFacingXZ = keeper.FacingXZ,
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 0f,
            IsSetPieceTaker = false,
            PerceivedBallPosition = ball.Position,
            PerceivedBallVelocity = ball.Velocity,
            PossessingTeam = possession,
            BallLastTouchedByPlayerId = lastPlay?.PlayerId ?? (possession == TeamId.Home ? keeper.Id : StrikerId),
            BallLastTouchedByTeam = lastPlay?.Team ?? possession,
            BallLastPlay = lastPlay ?? BallLastPlay.None,
            BallControlState = possession == TeamId.Home
                ? BallControlState.Controlled
                : BallControlState.Loose,
            BallControllingPlayerId = possession == TeamId.Home ? keeper.Id : null,
            TacticalAnchorPosition = keeper.Position,
            TacticalJob = Tactics.TacticalJob.Shape,
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 30f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            Tick = 900,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = keeper.Tendencies,
            SelfAttributes = keeper.Attributes
        };
    }

    /// <summary>Runs the sweep model on a fresh trajectory for these positions.</summary>
    private static GoalkeeperRace EvaluateSweep(
        BallState ball,
        Vector2 strikerXZ,
        Vector2? keeperXZ = null,
        TeamId possessingTeam = TeamId.Away)
    {
        var keeper = new GoalkeeperController(Pitch);
        keeper.Decide(
            Observe(Keeper(new Vector3(keeperXZ?.X ?? -49f, 0f, keeperXZ?.Y ?? 0f)), ball, possessingTeam, strikerXZ),
            1f / 60f);

        return keeper.LastSweep;
    }

    /// <summary>
    /// Runs the sweep model directly, bypassing the controller's own early exits, so the model's guards are the
    /// thing under test.
    /// </summary>
    private static GoalkeeperRace ModelSweep(
        BallState ball,
        Vector2? keeperXZ = null,
        TeamId possessingTeam = TeamId.Away,
        bool buildTrajectory = true)
    {
        FootballerObservation observation = Observe(
            Keeper(new Vector3(keeperXZ?.X ?? -49f, 0f, keeperXZ?.Y ?? 0f)),
            ball,
            possessingTeam,
            strikerXZ: new Vector2(-20f, 3f));

        var trajectory = new BallTrajectoryEstimate(pitch: Pitch);
        if (buildTrajectory)
        {
            trajectory.TryBuild(ball, GoalkeeperDecisionModel.SweepRaceHorizonSeconds);
        }

        return GoalkeeperDecisionModel.EvaluateSweep(observation, trajectory, Pitch);
    }
}
