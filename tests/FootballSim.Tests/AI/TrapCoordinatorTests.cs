using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A8.4 — the trap board's lifecycle, at the model level: raise, keep, and end exactly once, with the ending
/// named. The board is driven through the same <see cref="TrapCoordinator.Update"/> the match calls, from the
/// fixture in <see cref="TrapTestWorld"/>.
/// </summary>
public sealed class TrapCoordinatorTests
{
    private const int PresserId = TrapTestWorld.HomeFullBack;
    private const int CoverId = TrapTestWorld.HomeCover;
    private const int ScreenerId = TrapTestWorld.HomeScreener;
    private const int CarrierId = TrapTestWorld.AwayCarrier;

    private static readonly PitchDimensions Pitch = TrapTestWorld.Pitch;
    private static readonly TeamTacticalProfile Profile = TeamTacticalProfile.PossessionControl;

    [Fact]
    public void ATrapIsRaised_KeptAndExpired()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        var board = new TrapCoordinator();

        Assert.True(Update(board, world, tick: 1), "the fixture's picture is a trap");
        Assert.Equal(1, board.RaisedCount);
        Assert.Equal(1, board.LiveCount(1));
        Assert.True(board.TryGetLive(TeamId.Home, 1, out TrapContract contract));
        Assert.Equal(TrapCoordinator.TrapDurationTicks, contract.ExpiresTick - contract.CreatedTick);

        // The same picture on a later tick keeps the same contract rather than re-raising it.
        Assert.True(Update(board, world, tick: 2));
        Assert.Equal(1, board.RaisedCount);
        Assert.True(board.TryGetLive(TeamId.Home, 2, out TrapContract kept));
        Assert.Equal(contract.TrapId, kept.TrapId);

        Assert.True(Update(board, world, tick: contract.ExpiresTick), "the last live tick is the expiry tick");
        Assert.True(Update(board, world, tick: contract.ExpiresTick + 1),
            "the picture still supports a trap, so the side re-commits on the next tick");
        Assert.Equal(1, board.CountOfEnd(TrapEndReason.Expired));
        Assert.Equal(2, board.RaisedCount);
        Assert.True(board.TryGetLive(TeamId.Home, contract.ExpiresTick + 1, out TrapContract renewed));
        Assert.NotEqual(contract.TrapId, renewed.TrapId);

        // A trap whose ball is no longer controlled is not renewed.
        var loose = BallPossessionSnapshot.Loose;
        Assert.False(Update(board, world, tick: contract.ExpiresTick + 2, possession: loose));
        Assert.False(board.TryGetLive(TeamId.Home, contract.ExpiresTick + 2, out _));
        Assert.Equal(1, board.CountOfEnd(TrapEndReason.Loose));
    }

    [Fact]
    public void OurOwnControlOfTheBall_IsTheSuccessfulTrap()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        var board = new TrapCoordinator();
        Assert.True(Update(board, world, tick: 1));

        var homeBall = new BallPossessionSnapshot(
            BallControlState.Controlled, TeamId.Home, TrapTestWorld.HomeCover, null);
        Assert.False(Update(board, world, tick: 2, possession: homeBall));
        Assert.Equal(1, board.CountOfEnd(TrapEndReason.Won));
        Assert.Equal(0, board.LiveCount(2));
    }

    [Fact]
    public void ALooseBall_EndsTheTrapWithoutAWinner()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        var board = new TrapCoordinator();
        Assert.True(Update(board, world, tick: 1));

        Assert.False(Update(board, world, tick: 2, possession: BallPossessionSnapshot.Loose));
        Assert.Equal(1, board.CountOfEnd(TrapEndReason.Loose));
    }

    /// <summary>The exit gate's escape clause: a carrier who played his way out ends the trap and the side
    /// recovers before it re-commits.</summary>
    [Fact]
    public void APassOutOfTheTrap_IsAnEscape_AndTheTeamRecovers()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        var board = new TrapCoordinator();
        Assert.True(Update(board, world, tick: 1));

        var inFlight = new BallPossessionSnapshot(
            BallControlState.InFlightPass, TeamId.Away, null, TrapTestWorld.AwayMidfielder);
        Assert.False(Update(board, world, tick: 2, possession: inFlight));
        Assert.Equal(1, board.CountOfEnd(TrapEndReason.Escaped));
        Assert.True(board.IsRecovering(TeamId.Home, 2));
        Assert.Equal(0, board.LiveCount(2));

        // Still recovering: the same picture cannot re-raise the trap straight away.
        Assert.False(Update(board, world, tick: 3));
        Assert.Equal(1, board.RaisedCount);
        Assert.Equal(1, board.RecoveryBlockedCount);

        // Recovered: the same picture raises a fresh trap.
        Assert.True(Update(board, world, tick: 2 + TrapCoordinator.RecoveryTicks));
        Assert.Equal(2, board.RaisedCount);
    }

    [Fact]
    public void ACarrierWhoCarriedTheBallOutOfTheRegion_IsAnEscape()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        var board = new TrapCoordinator();
        Assert.True(Update(board, world, tick: 1));

        world.Player(CarrierId).Position = new Vector3(0f, 0f, -30f + TrapPolicy.TrapEscapeMeters + 2f);
        world.Ball.Position = world.Player(CarrierId).Position with { Y = 0.11f };
        world.Ball.PreviousPosition = world.Ball.Position;

        Assert.False(Update(board, world, tick: 2,
            belief: TeamBelief.FromTruth(TeamId.Home, world.Players)));
        Assert.Equal(1, board.CountOfEnd(TrapEndReason.Escaped));
        Assert.True(board.IsRecovering(TeamId.Home, 2));
    }

    [Fact]
    public void AChangedPresser_EndsTheTrap()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        var board = new TrapCoordinator();
        Assert.True(Update(board, world, tick: 1));

        Assert.False(Update(board, world, tick: 2, presserId: TrapTestWorld.HomeCenterBack));
        Assert.Equal(1, board.CountOfEnd(TrapEndReason.PresserLost));
    }

    [Fact]
    public void ALostCover_EndsTheTrap()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        var board = new TrapCoordinator();
        Assert.True(Update(board, world, tick: 1));

        // The allocator no longer holds the man the trap was written around: a trap that cannot preserve
        // cover is not the contract the players agreed to.
        Assert.False(Update(board, world, tick: 2, jobs: new TeamJobCoordinator(Pitch)));
        Assert.Equal(1, board.CountOfEnd(TrapEndReason.CoverLost));
    }

    [Fact]
    public void APlayerWhoLeavesThePitch_ReleasesHisTrapRole()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        var board = new TrapCoordinator();
        Assert.True(Update(board, world, tick: 1));

        board.ReleasePlayer(ScreenerId, 2);
        Assert.Equal(1, board.CountOfEnd(TrapEndReason.ParticipantRemoved));
        Assert.Equal(0, board.LiveCount(2));
        Assert.False(board.ContextFor(TeamId.Home, PresserId, 2).IsActive);
    }

    [Fact]
    public void APassageReset_ClearsTheTrapAndTheRecovery()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        var board = new TrapCoordinator();
        Assert.True(Update(board, world, tick: 1));
        board.ClearAll(2);
        Assert.Equal(1, board.CountOfEnd(TrapEndReason.Reset));
        Assert.False(board.IsRecovering(TeamId.Home, 2));
        Assert.Equal(0, board.LiveCount(2));

        // A new passage may trap again at once.
        Assert.True(Update(board, world, tick: 3));
    }

    [Fact]
    public void TheBoardCarriesOneRolePerParticipant_AndNothingForAnybodyElse()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        var board = new TrapCoordinator();
        Assert.True(Update(board, world, tick: 1));

        TrapContext presser = board.ContextFor(TeamId.Home, PresserId, 1);
        TrapContext screener = board.ContextFor(TeamId.Home, ScreenerId, 1);
        TrapContext cover = board.ContextFor(TeamId.Home, CoverId, 1);

        Assert.True(presser.IsPresser);
        Assert.True(screener.IsScreener);
        Assert.True(cover.IsCover);
        Assert.Equal(CarrierId, presser.CarrierId);
        Assert.Equal(PresserId, presser.PresserId);
        Assert.Equal(ScreenerId, screener.ScreenId);
        Assert.Equal(CoverId, cover.CoverId);
        Assert.Equal(presser.TrapId, screener.TrapId);
        Assert.Equal(presser.Confidence01, cover.Confidence01);

        Assert.False(board.ContextFor(TeamId.Home, TrapTestWorld.HomeGoalkeeper, 1).IsActive);
        Assert.False(board.ContextFor(TeamId.Away, PresserId, 1).IsActive);
        Assert.False(board.ContextFor(TeamId.Home, PresserId, 2 + TrapCoordinator.TrapDurationTicks).IsActive);
    }

    [Fact]
    public void OneTeamRunsAtMostOneTrap()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        var board = new TrapCoordinator();
        Assert.True(Update(board, world, tick: 1));
        for (int tick = 2; tick < 30; tick++) Assert.True(Update(board, world, tick));
        Assert.Equal(1, board.RaisedCount);
        Assert.Equal(1, board.LiveCount(29));
    }

    [Fact]
    public void TheDisabledBuild_RaisesNothing()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        var board = new TrapCoordinator();
        Assert.False(Update(board, world, tick: 1, enabled: false));
        Assert.Equal(0, board.RaisedCount);
        Assert.Equal(0, board.LiveCount(1));
        Assert.False(board.ContextFor(TeamId.Home, PresserId, 1).IsActive);
    }

    [Fact]
    public void TheSameSequence_ProducesTheSameContracts()
    {
        TrapTestWorld.World worldA = TrapTestWorld.Build();
        TrapTestWorld.World worldB = TrapTestWorld.Build();
        var boardA = new TrapCoordinator();
        var boardB = new TrapCoordinator();

        for (int tick = 1; tick <= 40; tick++)
        {
            bool liveA = Update(boardA, worldA, tick);
            bool liveB = Update(boardB, worldB, tick);
            Assert.Equal(liveA, liveB);
            if (!liveA) continue;
            Assert.True(boardA.TryGetLive(TeamId.Home, tick, out TrapContract a));
            Assert.True(boardB.TryGetLive(TeamId.Home, tick, out TrapContract b));
            Assert.Equal(a, b);
        }

        Assert.Equal(boardA.RaisedCount, boardB.RaisedCount);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    private static bool Update(
        TrapCoordinator board,
        TrapTestWorld.World world,
        int tick,
        TeamBelief? belief = null,
        BallPossessionSnapshot? possession = null,
        int? presserId = null,
        TeamJobCoordinator? jobs = null,
        bool enabled = true) =>
        board.Update(
            TeamId.Home, world.Players, belief ?? TeamBelief.FromTruth(TeamId.Home, world.Players),
            possession ?? TrapTestWorld.Possession(),
            presserId ?? PresserId, supportPresserId: null, jobs ?? world.Jobs,
            humanPlayerId: null, attackDirX: 1f, Profile.Compactness01, Pitch, tick, enabled);
}
