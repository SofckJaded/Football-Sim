using System;
using System.Linq;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A8.5 — the restart-routine board's lifecycle. One routine per restart, one stated ending, and no routine
/// surviving the restart it was elected for (the A8 exit gate's "no contract survives into unrelated possession,
/// and inactive players release their contract ownership").
///
/// The election itself is pinned in <see cref="RestartPolicyTests"/>; the live wiring in
/// <see cref="RestartScenarioTests"/>.
/// </summary>
public sealed class RestartCoordinatorTests
{
    private const int RaiseTick = 100;

    private static bool Update(
        RestartCoordinator board,
        RestartTestWorld.World world,
        int tick,
        RestartKind activeRestart = RestartKind.ThrowIn,
        int? takerId = RestartTestWorld.HomeTaker,
        int? lastTouchPlayerId = null,
        TeamId? lastTouchTeam = TeamId.Home,
        bool enabled = true,
        TeamId restartTeam = TeamId.Home) =>
        board.Update(
            restartTeam, activeRestart, RestartTestWorld.ThrowInBall, takerId, world.Players,
            world.Belief(restartTeam), TeamTacticalProfile.PossessionControl, null,
            restartTeam == TeamId.Home ? 1f : -1f, RestartTestWorld.Pitch,
            lastTouchPlayerId, lastTouchTeam, tick, enabled);

    private static (RestartCoordinator Board, RestartTestWorld.World World, RestartRoutine Routine) Raise()
    {
        RestartTestWorld.World world = RestartTestWorld.Build();
        var board = new RestartCoordinator();
        Assert.True(Update(board, world, RaiseTick));
        Assert.True(board.TryGetLive(RaiseTick, out RestartRoutine routine));
        return (board, world, routine);
    }

    [Fact]
    public void AnArmedRestart_RaisesOneRoutineWithItsThreeRoles()
    {
        (RestartCoordinator board, RestartTestWorld.World world, RestartRoutine routine) = Raise();

        Assert.Equal(1, board.RaisedCount);
        Assert.Equal(RestartKind.ThrowIn, routine.Kind);
        Assert.Equal(TeamId.Home, routine.Team);
        Assert.Equal(RestartTestWorld.HomeTaker, routine.TakerId);
        Assert.Equal(RestartRole.Taker, routine.RoleOf(routine.TakerId));
        Assert.True(routine.SupportId > 0, "the fixture provides a free support option");
        Assert.Equal(RestartRole.Support, routine.RoleOf(routine.SupportId));
        Assert.Equal(RestartRole.Target, routine.RoleOf(routine.TargetId));
        Assert.Equal(routine.ReceiverId,
            routine.Option == RestartOption.Short ? routine.SupportId : routine.TargetId);

        // The participants are told; nobody else is.
        RestartRoutineContext taker = board.ContextFor(TeamId.Home, routine.TakerId, RaiseTick);
        Assert.True(taker.IsActive && taker.IsTaker);
        Assert.Equal(RaiseTick, taker.CreatedTick);
        Assert.Equal(routine.ExpiresTick, taker.ExpiresTick);
        Assert.Equal(routine.ReceiverId, taker.ReceiverId);
        Assert.True(board.ContextFor(TeamId.Home, routine.SupportId, RaiseTick).IsSupport);
        Assert.True(board.ContextFor(TeamId.Home, routine.TargetId, RaiseTick).IsTarget);

        Assert.False(board.ContextFor(TeamId.Home, RestartTestWorld.HomeCenterBack, RaiseTick).IsActive);
        Assert.False(board.ContextFor(TeamId.Away, routine.TakerId, RaiseTick).IsActive);
        Assert.False(board.ContextFor(TeamId.Home, 0, RaiseTick).IsActive);
    }

    [Fact]
    public void TheRoutine_IsKeptWhileTheRestartIsArmed()
    {
        (RestartCoordinator board, RestartTestWorld.World world, RestartRoutine routine) = Raise();

        Assert.True(Update(board, world, RaiseTick + 1));
        Assert.True(board.TryGetLive(RaiseTick + 1, out RestartRoutine kept));

        Assert.Equal(1, board.RaisedCount);
        Assert.Equal(routine, kept);
    }

    [Fact]
    public void ATakenRestart_EndsTheRoutineAsTaken()
    {
        (RestartCoordinator board, RestartTestWorld.World world, _) = Raise();

        Assert.False(Update(board, world, RaiseTick + 1, activeRestart: RestartKind.None,
            takerId: null, lastTouchPlayerId: RestartTestWorld.HomeTaker, lastTouchTeam: TeamId.Home));

        Assert.False(board.HasLive(RaiseTick + 1));
        Assert.Equal(1, board.CountOfEnd(RestartEndReason.Taken));
        Assert.Equal(0, board.CountOfEnd(RestartEndReason.Reset));
    }

    [Fact]
    public void AnOpponentTouchingFirst_EndsTheRoutineAsStolen()
    {
        (RestartCoordinator board, RestartTestWorld.World world, _) = Raise();

        Assert.False(Update(board, world, RaiseTick + 1, activeRestart: RestartKind.None,
            takerId: null, lastTouchPlayerId: RestartTestWorld.AwayCenterBack, lastTouchTeam: TeamId.Away));

        Assert.Equal(1, board.CountOfEnd(RestartEndReason.Stolen));
    }

    [Fact]
    public void ASetupResetWithoutATouch_EndsTheRoutineAsReset()
    {
        (RestartCoordinator board, RestartTestWorld.World world, _) = Raise();

        Assert.False(Update(board, world, RaiseTick + 1, activeRestart: RestartKind.None,
            takerId: null, lastTouchPlayerId: null, lastTouchTeam: null));

        Assert.Equal(0, board.CountOfEnd(RestartEndReason.Taken));
        Assert.Equal(1, board.CountOfEnd(RestartEndReason.Reset));
    }

    [Fact]
    public void ADifferentRestart_ReplacesTheRoutine()
    {
        (RestartCoordinator board, RestartTestWorld.World world, _) = Raise();

        // A corner for the same side at the same spot: a different restart, so the old answer does not apply.
        Assert.False(Update(board, world, RaiseTick + 1, activeRestart: RestartKind.CornerKick));
        Assert.Equal(1, board.CountOfEnd(RestartEndReason.Replaced));
    }

    [Fact]
    public void AChangedTaker_ReplacesTheRoutine()
    {
        (RestartCoordinator board, RestartTestWorld.World world, _) = Raise();

        Assert.False(Update(board, world, RaiseTick + 1, takerId: RestartTestWorld.HomeMidfielder));
        Assert.Equal(1, board.CountOfEnd(RestartEndReason.Replaced));
    }

    [Fact]
    public void AnExpiredWindow_EndsTheRoutineOnce()
    {
        (RestartCoordinator board, RestartTestWorld.World world, RestartRoutine routine) = Raise();
        int afterWindow = routine.ExpiresTick + 1;

        Assert.False(Update(board, world, afterWindow));
        Assert.Equal(1, board.CountOfEnd(RestartEndReason.Expired));
        Assert.False(board.HasLive(afterWindow));

        // The restart is still armed, so a fresh routine may be elected for it — the expiry ended one routine,
        // it did not make the restart unavailable.
        Assert.True(Update(board, world, afterWindow + 1));
        Assert.Equal(2, board.RaisedCount);
        Assert.Equal(1, board.CountOfEnd(RestartEndReason.Expired));
    }

    [Fact]
    public void ATakerWhoLeavesThePitch_EndsTheRoutine()
    {
        (RestartCoordinator board, RestartTestWorld.World world, RestartRoutine routine) = Raise();
        world.Player(routine.TakerId).IsSentOff = true;

        Assert.False(Update(board, world, RaiseTick + 1));
        Assert.Equal(1, board.CountOfEnd(RestartEndReason.TakerLost));
    }

    [Fact]
    public void ANamedParticipantWhoLeavesThePitch_EndsTheRoutine()
    {
        (RestartCoordinator board, RestartTestWorld.World world, RestartRoutine routine) = Raise();
        world.Player(routine.SupportId).IsSentOff = true;

        Assert.False(Update(board, world, RaiseTick + 1));
        Assert.Equal(1, board.CountOfEnd(RestartEndReason.ParticipantRemoved));
    }

    [Fact]
    public void ReleasePlayer_EndsTheRoutineWithTheReasonThePlayerHeld()
    {
        (RestartCoordinator takerBoard, RestartTestWorld.World takerWorld, RestartRoutine takerRoutine) = Raise();
        takerBoard.ReleasePlayer(takerRoutine.TakerId, RaiseTick + 1);
        Assert.False(takerBoard.HasLive(RaiseTick + 1));
        Assert.Equal(1, takerBoard.CountOfEnd(RestartEndReason.TakerLost));

        (RestartCoordinator supportBoard, _, RestartRoutine supportRoutine) = Raise();
        supportBoard.ReleasePlayer(supportRoutine.SupportId, RaiseTick + 1);
        Assert.Equal(1, supportBoard.CountOfEnd(RestartEndReason.ParticipantRemoved));

        // An uninvolved player releases nothing.
        (RestartCoordinator otherBoard, _, _) = Raise();
        otherBoard.ReleasePlayer(RestartTestWorld.HomeCenterBack, RaiseTick + 1);
        Assert.True(otherBoard.HasLive(RaiseTick + 1));
        Assert.Equal(0, otherBoard.CountOfEnd(RestartEndReason.ParticipantRemoved));
    }

    [Fact]
    public void ClearAll_AttributesThePassageEnding()
    {
        (RestartCoordinator resetBoard, _, _) = Raise();
        resetBoard.ClearAll(RestartKind.None);
        Assert.Equal(1, resetBoard.CountOfEnd(RestartEndReason.Reset));

        (RestartCoordinator replacedBoard, _, _) = Raise();
        replacedBoard.ClearAll(RestartKind.CornerKick);
        Assert.Equal(1, replacedBoard.CountOfEnd(RestartEndReason.Replaced));
    }

    [Fact]
    public void DisabledBoard_RaisesNothingAndPublishesNothing()
    {
        RestartTestWorld.World world = RestartTestWorld.Build();
        var board = new RestartCoordinator();

        Assert.False(Update(board, world, RaiseTick, enabled: false));

        Assert.Equal(0, board.RaisedCount);
        Assert.False(board.HasLive(RaiseTick));
        Assert.False(board.ContextFor(TeamId.Home, RestartTestWorld.HomeTaker, RaiseTick).IsActive);
    }

    [Fact]
    public void OneRestart_HoldsOneRoutine()
    {
        (RestartCoordinator board, RestartTestWorld.World world, RestartRoutine routine) = Raise();

        Assert.True(Update(board, world, RaiseTick));
        Assert.True(board.TryGetLive(RaiseTick, out RestartRoutine again));

        Assert.Equal(1, board.RaisedCount);
        Assert.Equal(routine.RoutineId, again.RoutineId);
    }

    [Fact]
    public void TheBoard_IsDeterministic()
    {
        RestartTestWorld.World world = RestartTestWorld.Build();
        var left = new RestartCoordinator();
        var right = new RestartCoordinator();

        for (int tick = RaiseTick; tick < RaiseTick + 5; tick++)
        {
            Assert.Equal(Update(left, world, tick), Update(right, world, tick));
        }

        Assert.True(left.TryGetLive(RaiseTick + 4, out RestartRoutine leftRoutine));
        Assert.True(right.TryGetLive(RaiseTick + 4, out RestartRoutine rightRoutine));
        Assert.Equal(leftRoutine, rightRoutine);
        Assert.Equal(left.RaisedCount, right.RaisedCount);
    }
}
