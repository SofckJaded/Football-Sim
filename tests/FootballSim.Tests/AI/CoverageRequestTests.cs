using System;
using System.Collections.Generic;
using System.Linq;
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
/// A3.5 — the coverage request lifecycle, driven through the one allocator.
///
/// A3.3 proved a vacated line gets covered; this pins the lifecycle that grew out of it: nearby bids are
/// recorded, the winner is deterministic, the request is accepted, held, and ended with a stated reason
/// (handback, expiry, owner removal or the passage ending), and only one request can be live per team so
/// one press cannot recursively reshuffle the side.
/// </summary>
public sealed class CoverageRequestTests
{
    private const float Dt = SimConstants.FixedDeltaTime;
    private static readonly PitchDimensions Pitch = new();

    private const int StepOutCentreBack = 4;
    private const int OtherCentreBack = 5;
    private const int DefensiveMidfielder = 6;

    private sealed record Setup(
        TeamJobCoordinator Jobs,
        List<FootballerState> Players,
        BallState Ball,
        TeamFormation HomeFormation,
        TeamFormation AwayFormation);

    private static FootballerState Player(int id, TeamId team, TacticalRole role, int shirt, Vector3 position) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = shirt,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(role),
        Position = position,
        FacingXZ = new Vector2(-1f, 0f)
    };

    /// <summary>
    /// The away carrier holds the ball between the home lines, the home centre-back has stepped out to
    /// press him, and the other centre-back is still on the line. The defensive midfielder is deliberately
    /// on the far side of the ball so the geometry does not leave the choice ambiguous unless a test moves
    /// him.
    /// </summary>
    private static Setup CentreBackSteppedOutSetup(
        int homeResponderId = StepOutCentreBack,
        Vector3? defensiveMidfielderPosition = null)
    {
        const int goalkeeper = 1;
        const int awayCarrier = 11;

        var players = new List<FootballerState>
        {
            Player(goalkeeper, TeamId.Home, TacticalRole.Goalkeeper, 1, new Vector3(-48f, 0f, 0f)),
            Player(StepOutCentreBack, TeamId.Home, TacticalRole.CenterBack, 4, new Vector3(-20f, 0f, -8f)),
            Player(OtherCentreBack, TeamId.Home, TacticalRole.CenterBack, 5, new Vector3(-22f, 0f, 6f)),
            Player(DefensiveMidfielder, TeamId.Home, TacticalRole.DefensiveMidfielder, 6,
                defensiveMidfielderPosition ?? new Vector3(-6f, 0f, 16f)),
            Player(awayCarrier, TeamId.Away, TacticalRole.CentralMidfielder, 8, new Vector3(-5.4f, 0f, -9.8f))
        };

        var ball = new BallState
        {
            Position = new Vector3(-5.4f, 0.11f, -9.8f),
            LastTouchedByTeam = TeamId.Away,
            LastTouchedByPlayerId = awayCarrier
        };

        var tactics = new TeamTacticalCoordinator(
            Pitch, TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack);
        tactics.AssignFormations(players, teamSize: 5, fromPositions: true, homeAttackDirX: 1f);

        // The anchors are what "out of the line" is measured against; write them the way the engine does.
        var possession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Away, awayCarrier, null);
        tactics.ObservePossession(possession, ball, 1f, Dt);
        tactics.UpdateTeamAnchors(players, ball, 1f);

        // Now the centre-back presses: he is on the ball, well ahead of the shape point he was given.
        FootballerState stepper = players.First(p => p.Id == StepOutCentreBack);
        stepper.Position = new Vector3(-6f, 0f, -9.5f);

        var jobs = new TeamJobCoordinator(Pitch);
        var setup = new Setup(jobs, players, ball, tactics.HomeFormation, tactics.AwayFormation);
        Run(setup, possession, possessingTeam: TeamId.Away, homeResponderId, awayResponderId: null, tick: 0);
        return setup;
    }

    private static void Run(
        Setup setup,
        BallPossessionSnapshot possession,
        TeamId? possessingTeam,
        int? homeResponderId,
        int? awayResponderId,
        int tick)
    {
        setup.Jobs.Update(
            setup.Players, setup.Ball, possession, possessingTeam,
            homeResponderId, awayResponderId,
            homeDirX: 1f, tick,
            TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
            new TeamBelief(TeamId.Home), new TeamBelief(TeamId.Away),
            setup.HomeFormation, setup.AwayFormation);
    }

    private static BallPossessionSnapshot AwayControlled => new(
        BallControlState.Controlled, TeamId.Away, 11, null);

    [Fact]
    public void ACentreBackSteppingOut_OpensARequest_ThatTheNextDefenderAccepts()
    {
        Setup setup = CentreBackSteppedOutSetup();

        Assert.Equal(TacticalJob.Presser, setup.Jobs.Get(StepOutCentreBack)!.Value.Job);

        CoverageRequest request = setup.Jobs.CoverageOf(TeamId.Home);
        Assert.True(request.IsLive, $"no teammate took the vacated gap (state {request.State})");
        Assert.Equal(CoverageRequestState.Accepted, request.State);
        Assert.Equal(OtherCentreBack, request.OwnerPlayerId);
        Assert.Equal(StepOutCentreBack, request.SourcePlayerId);
        Assert.Equal(1, setup.Jobs.CoverageRequestsOpened);
        Assert.Equal(0, setup.Jobs.CoverageRequestsClosed);

        TeamJobAssignment duty = setup.Jobs.Get(request.OwnerPlayerId)!.Value;
        Assert.Equal(TacticalJob.ProtectGap, duty.Job);
        Assert.True(duty.IsTemporary);
        Assert.Equal(StepOutCentreBack, duty.SourcePlayerId);
        Assert.Contains("vacated", duty.Reason);

        // The region is the slot the stepping defender owns — his place in the back line.
        Assert.True(setup.HomeFormation.TryGetWorldAnchor(StepOutCentreBack, 1f, out Vector3 slot));
        Assert.True(Vector3.Distance(request.Region, slot) < 0.5f,
            $"protected region {request.Region} is not the vacated slot {slot}");
    }

    [Fact]
    public void TheRequest_RecordsTheBids_AndTheWinnerIsTheBestOfThem()
    {
        Setup setup = CentreBackSteppedOutSetup(defensiveMidfielderPosition: new Vector3(-24f, 0f, 0f));
        CoverageRequest request = setup.Jobs.CoverageOf(TeamId.Home);

        Assert.True(request.BidCount >= 2,
            $"only {request.BidCount} teammate(s) bid; the bid table is the point of the lifecycle");

        CoverageBid? winning = request.WinningBid;
        Assert.NotNull(winning);
        Assert.Equal(request.OwnerPlayerId, winning!.Value.PlayerId);

        // Deterministic selection: the accepted owner is the highest-scoring bid, ties by lower id.
        for (int i = 0; i < request.BidCount; i++)
        {
            CoverageBid bid = request.Bid(i);
            if (bid.PlayerId == request.OwnerPlayerId) continue;
            Assert.True(winning.Value.Score > bid.Score ||
                        (MathF.Abs(winning.Value.Score - bid.Score) < 1e-4f && winning.Value.PlayerId < bid.PlayerId),
                $"#{bid.PlayerId} scored {bid.Score:F3} against the winner's {winning.Value.Score:F3}");
        }

        // Two identical worlds accept the same owner.
        Setup again = CentreBackSteppedOutSetup(defensiveMidfielderPosition: new Vector3(-24f, 0f, 0f));
        Assert.Equal(request.OwnerPlayerId, again.Jobs.CoverageOf(TeamId.Home).OwnerPlayerId);
    }

    [Fact]
    public void TheOwner_KeepsTheSameRegion_WhileTheSourceIsStillOut()
    {
        Setup setup = CentreBackSteppedOutSetup();
        CoverageRequest first = setup.Jobs.CoverageOf(TeamId.Home);
        int owner = first.OwnerPlayerId;
        int createdTick = first.CreatedTick;

        for (int tick = 6; tick <= 60; tick += 6)
        {
            Run(setup, AwayControlled, TeamId.Away, homeResponderId: StepOutCentreBack, awayResponderId: null, tick);
            CoverageRequest current = setup.Jobs.CoverageOf(TeamId.Home);

            Assert.True(current.IsLive);
            Assert.Equal(owner, current.OwnerPlayerId);
            Assert.Equal(createdTick, current.CreatedTick);   // the same request, not a new one
            Assert.Equal(first.Region, current.Region);
        }

        Assert.Equal(1, setup.Jobs.CoverageRequestsOpened);
        Assert.Equal(0, setup.Jobs.CoverageRequestsClosed);
        Assert.Equal(TacticalJob.ProtectGap, setup.Jobs.Get(owner)!.Value.Job);
    }

    [Fact]
    public void TheGap_IsHandedBack_WhenTheSourceReturnsToHisLine()
    {
        Setup setup = CentreBackSteppedOutSetup();
        CoverageRequest request = setup.Jobs.CoverageOf(TeamId.Home);
        int owner = request.OwnerPlayerId;

        // He walks back to the shape point he was given: the line is whole again.
        FootballerState stepper = setup.Players.First(p => p.Id == StepOutCentreBack);
        stepper.Position = stepper.TacticalAnchor;

        Run(setup, AwayControlled, TeamId.Away, homeResponderId: StepOutCentreBack, awayResponderId: null, tick: 6);

        Assert.False(setup.Jobs.CoverageOf(TeamId.Home).IsLive);
        Assert.Equal(CoverageRequestState.HandedBack, SetupLastState(setup));
        Assert.Contains("back in his line", setup.Jobs.CoverageOf(TeamId.Home).Reason);
        Assert.Equal(1, setup.Jobs.CoverageRequestsClosed);
        Assert.NotEqual(TacticalJob.ProtectGap, setup.Jobs.Get(owner)!.Value.Job);
    }

    [Fact]
    public void WinningTheBallBack_EndsTheRequestWithThePassage()
    {
        Setup setup = CentreBackSteppedOutSetup();
        Assert.True(setup.Jobs.CoverageOf(TeamId.Home).IsLive);

        var homePossession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, DefensiveMidfielder, null);
        Run(setup, homePossession, TeamId.Home, homeResponderId: DefensiveMidfielder, awayResponderId: null, tick: 6);

        Assert.False(setup.Jobs.CoverageOf(TeamId.Home).IsLive);
        Assert.Equal(CoverageRequestState.PassageEnded, setup.Jobs.CoverageOf(TeamId.Home).State);
        Assert.Equal(1, setup.Jobs.CoverageRequestsClosed);
        Assert.DoesNotContain(setup.Jobs.Assignments.Values, a => a.Job == TacticalJob.ProtectGap);
    }

    [Fact]
    public void ARequest_NeverOutlivesItsWindow()
    {
        Setup setup = CentreBackSteppedOutSetup();

        // The press never ends on its own; the hold is still bounded. Expiry closes the request, and the
        // gap — still vacated — is then covered by a fresh request (its CreatedTick is later).
        for (int tick = 6; tick <= 1000; tick += 6)
            Run(setup, AwayControlled, TeamId.Away, homeResponderId: StepOutCentreBack, awayResponderId: null, tick);

        Assert.True(setup.Jobs.CoverageRequestsClosed >= 1, "the hold never ended");
        CoverageRequest request = setup.Jobs.CoverageOf(TeamId.Home);
        if (request.IsLive)
        {
            Assert.True(request.CreatedTick > 720,
                $"the original request survived its own window (created at {request.CreatedTick})");
            Assert.Equal(CoverageRequestState.Expired, request.PreviousState);
        }
    }

    [Fact]
    public void AnOwnerWhoLeavesThePitch_EndsTheRequestWithThatReason()
    {
        // Both remaining line players are within reach of the gap, so the request can be re-owned.
        Setup setup = CentreBackSteppedOutSetup(defensiveMidfielderPosition: new Vector3(-24f, 0f, 0f));
        int firstOwner = setup.Jobs.CoverageOf(TeamId.Home).OwnerPlayerId;

        FootballerState owner = setup.Players.First(p => p.Id == firstOwner);
        owner.IsSentOff = true;

        Run(setup, AwayControlled, TeamId.Away, homeResponderId: StepOutCentreBack, awayResponderId: null, tick: 6);

        CoverageRequest request = setup.Jobs.CoverageOf(TeamId.Home);
        Assert.Equal(CoverageRequestState.OwnerRemoved, request.PreviousState);
        Assert.Contains("left the pitch", request.PreviousReason);
        Assert.True(request.IsLive, "the vacated gap was left uncovered after its owner was sent off");
        Assert.NotEqual(firstOwner, request.OwnerPlayerId);
        Assert.Equal(1, setup.Jobs.CoverageRequestsClosed);
        Assert.Equal(2, setup.Jobs.CoverageRequestsOpened);
    }

    [Fact]
    public void ADisplacedDefender_WhoIsNotPressing_DoesNotVacate()
    {
        // The defensive midfielder is the presser; the centre-back is out of position but has no pulling
        // duty, so there is no press to cover and no request is invented.
        Setup setup = CentreBackSteppedOutSetup(homeResponderId: DefensiveMidfielder);

        Assert.False(setup.Jobs.CoverageOf(TeamId.Home).IsLive);
        Assert.Equal(0, setup.Jobs.CoverageRequestsOpened);
    }

    [Fact]
    public void OnlyOneRequest_CanBeLivePerTeam_BoundingTheCascade()
    {
        Setup setup = CentreBackSteppedOutSetup(defensiveMidfielderPosition: new Vector3(-24f, 0f, 0f));
        CoverageRequest request = setup.Jobs.CoverageOf(TeamId.Home);
        CoverageRequest sameInstance = request;

        // A second line player is dragged out of position while the first request is live: the mechanism
        // keeps the one request rather than reshuffling the side again.
        FootballerState otherCentreBack = setup.Players.First(p => p.Id == OtherCentreBack);
        otherCentreBack.Position = new Vector3(-8f, 0f, 6f);

        for (int tick = 6; tick <= 60; tick += 6)
        {
            Run(setup, AwayControlled, TeamId.Away, homeResponderId: StepOutCentreBack, awayResponderId: null, tick);
        }

        Assert.Same(sameInstance, setup.Jobs.CoverageOf(TeamId.Home));
        Assert.Equal(1, setup.Jobs.CoverageRequestsOpened);
        Assert.Equal(request.CreatedTick, setup.Jobs.CoverageOf(TeamId.Home).CreatedTick);
    }

    [Fact]
    public void ATurnover_ReassignsEveryDutyToTheNewPhase()
    {
        Setup setup = CentreBackSteppedOutSetup();
        Assert.All(
            setup.Jobs.Assignments.Where(entry => entry.Key is not 11),
            entry => Assert.True(IsDefensiveJob(entry.Value.Job), $"#{entry.Key} kept {entry.Value.Job}"));

        var homePossession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, DefensiveMidfielder, null);
        Run(setup, homePossession, TeamId.Home, homeResponderId: DefensiveMidfielder, awayResponderId: null, tick: 6);

        // A turnover voids incompatible duties on the tick it happens: nothing attacking survives into
        // defence and nothing defensive survives into possession.
        Assert.All(
            setup.Jobs.Assignments.Where(entry => entry.Key is not 11),
            entry => Assert.True(IsPossessionJob(entry.Value.Job), $"#{entry.Key} kept {entry.Value.Job}"));
    }

    /// <summary>The state a request ended in, looking through a re-open that has already happened.</summary>
    private static CoverageRequestState SetupLastState(Setup setup)
    {
        CoverageRequest request = setup.Jobs.CoverageOf(TeamId.Home);
        return request.IsLive ? request.PreviousState : request.State;
    }

    private static bool IsDefensiveJob(TacticalJob job) => job is
        TacticalJob.Presser or TacticalJob.CoverDefender or TacticalJob.ProtectGap or TacticalJob.Marker
        or TacticalJob.DefensiveLine or TacticalJob.Shape;

    private static bool IsPossessionJob(TacticalJob job) => job is
        TacticalJob.BallResponder or TacticalJob.NearOutlet or TacticalJob.ThirdPlayerOption
        or TacticalJob.FarSideWidth or TacticalJob.DepthRun or TacticalJob.Overlap
        or TacticalJob.DefensiveCover or TacticalJob.Shape;
}
