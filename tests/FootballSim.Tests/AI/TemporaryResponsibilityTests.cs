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
/// A3.3 — temporary responsibility: the gap a defender leaves when he steps out of his line.
///
/// The mechanism is one bounded responsibility per team: the teammate who can slide into the vacated
/// region owns it (`ProtectGap`) until handback is safe — the source is back in the line, the press
/// that pulled him out has ended, the ball has left the area, the hold expires, or his team wins the
/// ball back. These tests drive the allocator directly so the region, the owner and the handback are
/// deterministic, and the scenario test proves the same thing against the live engine.
/// </summary>
public sealed class TemporaryResponsibilityTests
{
    private const float Dt = SimConstants.FixedDeltaTime;
    private static readonly PitchDimensions Pitch = new();

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
    /// The away carrier is between the home lines, the home centre-back has stepped out to press him,
    /// and the other centre-back is still on the line. The defensive midfielder is deliberately on the
    /// far side of the ball so the geometry does not leave the choice ambiguous.
    /// </summary>
    private static Setup CentreBackSteppedOutSetup(int homeResponderId = StepOutCentreBack)
    {
        const int goalkeeper = 1;
        const int otherCentreBack = 5;
        const int defensiveMidfielder = 6;
        const int awayCarrier = 11;

        var players = new List<FootballerState>
        {
            Player(goalkeeper, TeamId.Home, TacticalRole.Goalkeeper, 1, new Vector3(-48f, 0f, 0f)),
            Player(StepOutCentreBack, TeamId.Home, TacticalRole.CenterBack, 4, new Vector3(-20f, 0f, -8f)),
            Player(otherCentreBack, TeamId.Home, TacticalRole.CenterBack, 5, new Vector3(-22f, 0f, 6f)),
            Player(defensiveMidfielder, TeamId.Home, TacticalRole.DefensiveMidfielder, 6, new Vector3(-6f, 0f, 16f)),
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
        tactics.UpdateTeamAnchors(players, ball, 1f, Dt);

        // Now the centre-back presses: he is on the ball, well ahead of the shape point he was given.
        FootballerState stepper = players.First(p => p.Id == StepOutCentreBack);
        stepper.Position = new Vector3(-6f, 0f, -9.5f);

        var jobs = new TeamJobCoordinator(Pitch);
        var setup = new Setup(jobs, players, ball, tactics.HomeFormation, tactics.AwayFormation);
        Run(setup, possession, possessingTeam: TeamId.Away, homeResponderId, awayResponderId: null, tick: 0);
        return setup;
    }

    private const int StepOutCentreBack = 4;

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
    public void ACentreBackSteppingOut_LeavesHisSlotToTheNextDefender()
    {
        Setup setup = CentreBackSteppedOutSetup();

        Assert.Equal(TacticalJob.Presser, setup.Jobs.Get(StepOutCentreBack)!.Value.Job);

        TemporaryResponsibility? responsibility = setup.Jobs.ResponsibilityOf(TeamId.Home);
        Assert.True(responsibility.HasValue, "no teammate took the vacated gap");
        TemporaryResponsibility held = responsibility!.Value;

        Assert.Equal(5, held.OwnerPlayerId);          // the other centre-back slides across
        Assert.Equal(StepOutCentreBack, held.SourcePlayerId);
        Assert.Equal(1, setup.Jobs.ResponsibilityAssignments);
        Assert.Equal(0, setup.Jobs.ResponsibilityHandbacks);

        TeamJobAssignment duty = setup.Jobs.Get(held.OwnerPlayerId)!.Value;
        Assert.Equal(TacticalJob.ProtectGap, duty.Job);
        Assert.True(duty.IsTemporary);
        Assert.Equal(StepOutCentreBack, duty.SourcePlayerId);

        // The region he protects is the slot the stepping defender owns — his place in the back line.
        Assert.True(setup.HomeFormation.TryGetWorldAnchor(StepOutCentreBack, 1f, out Vector3 slot));
        Assert.True(Vector3.Distance(duty.Anchor, slot) < 0.5f,
            $"protection anchor {duty.Anchor} is not the vacated slot {slot}");

        // The keeper is never the cover.
        Assert.NotEqual(1, held.OwnerPlayerId);
    }

    [Fact]
    public void TheOwner_KeepsTheSameRegion_WhileTheSourceIsStillOut()
    {
        Setup setup = CentreBackSteppedOutSetup();
        TemporaryResponsibility first = setup.Jobs.ResponsibilityOf(TeamId.Home)!.Value;

        for (int tick = 6; tick <= 60; tick += 6)
        {
            Run(setup, AwayControlled, TeamId.Away, homeResponderId: StepOutCentreBack, awayResponderId: null, tick);
            TemporaryResponsibility current = setup.Jobs.ResponsibilityOf(TeamId.Home)!.Value;
            Assert.Equal(first.OwnerPlayerId, current.OwnerPlayerId);
            Assert.Equal(first.Region, current.Region);
        }

        Assert.Equal(1, setup.Jobs.ResponsibilityAssignments);
        Assert.Equal(0, setup.Jobs.ResponsibilityHandbacks);
        Assert.Equal(TacticalJob.ProtectGap, setup.Jobs.Get(first.OwnerPlayerId)!.Value.Job);
    }

    [Fact]
    public void TheGap_IsHandedBack_WhenTheSourceReturnsToTheLine()
    {
        Setup setup = CentreBackSteppedOutSetup();
        TemporaryResponsibility held = setup.Jobs.ResponsibilityOf(TeamId.Home)!.Value;

        // He walks back to the shape point he was given: the line is whole again.
        FootballerState stepper = setup.Players.First(p => p.Id == StepOutCentreBack);
        stepper.Position = stepper.TacticalAnchor;

        Run(setup, AwayControlled, TeamId.Away, homeResponderId: StepOutCentreBack, awayResponderId: null, tick: 6);

        Assert.Null(setup.Jobs.ResponsibilityOf(TeamId.Home));
        Assert.Equal(1, setup.Jobs.ResponsibilityHandbacks);
        Assert.NotEqual(TacticalJob.ProtectGap, setup.Jobs.Get(held.OwnerPlayerId)!.Value.Job);
    }

    [Fact]
    public void WinningTheBallBack_VoidsTheResponsibility()
    {
        Setup setup = CentreBackSteppedOutSetup();
        Assert.NotNull(setup.Jobs.ResponsibilityOf(TeamId.Home));

        // Home wins the ball: the gap belonged to the passage that just ended.
        var homePossession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, 6, null);
        Run(setup, homePossession, TeamId.Home, homeResponderId: 6, awayResponderId: null, tick: 6);

        Assert.Null(setup.Jobs.ResponsibilityOf(TeamId.Home));
        Assert.Equal(1, setup.Jobs.ResponsibilityHandbacks);
        Assert.DoesNotContain(setup.Jobs.Assignments.Values, a => a.Job == TacticalJob.ProtectGap);
    }

    [Fact]
    public void AResponsibility_NeverOutlivesItsWindow()
    {
        Setup setup = CentreBackSteppedOutSetup();

        // The press never ends on its own; the hold is still bounded. Expiry releases the owner, and
        // the gap — still vacated — is then covered by a fresh responsibility, not by the same one.
        for (int tick = 6; tick <= 1000; tick += 6)
            Run(setup, AwayControlled, TeamId.Away, homeResponderId: StepOutCentreBack, awayResponderId: null, tick);

        Assert.True(setup.Jobs.ResponsibilityHandbacks >= 1, "the hold never ended");
        if (setup.Jobs.ResponsibilityOf(TeamId.Home) is { } current)
        {
            Assert.True(current.AcquiredTick > 720,
                $"the original responsibility survived its own window (acquired at {current.AcquiredTick})");
        }
    }

    [Fact]
    public void ADisplacedDefender_WhoIsNotPressing_DoesNotVacate()
    {
        // The defensive midfielder is the presser; the centre-back is simply out of position but has
        // no pulling duty, so there is no press to cover and no responsibility is invented.
        Setup setup = CentreBackSteppedOutSetup(homeResponderId: 6);

        Assert.Null(setup.Jobs.ResponsibilityOf(TeamId.Home));
        Assert.Equal(0, setup.Jobs.ResponsibilityAssignments);
    }

    [Fact]
    public void ATurnover_ReassignsEveryDutyToTheNewPhase()
    {
        Setup setup = CentreBackSteppedOutSetup();
        Assert.All(
            setup.Jobs.Assignments.Where(entry => IsHome(entry.Key, setup)),
            entry => Assert.True(IsDefensiveJob(entry.Value.Job), $"#{entry.Key} kept {entry.Value.Job}"));

        var homePossession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, 6, null);
        Run(setup, homePossession, TeamId.Home, homeResponderId: 6, awayResponderId: null, tick: 6);

        // A turnover voids incompatible duties on the tick it happens: nothing attacking survives into
        // defence and nothing defensive survives into possession.
        Assert.All(
            setup.Jobs.Assignments.Where(entry => IsHome(entry.Key, setup)),
            entry => Assert.True(IsPossessionJob(entry.Value.Job), $"#{entry.Key} kept {entry.Value.Job}"));
    }

    private static bool IsHome(int playerId, Setup setup) =>
        setup.Players.First(p => p.Id == playerId).Team == TeamId.Home;

    private static bool IsDefensiveJob(TacticalJob job) => job is
        TacticalJob.Presser or TacticalJob.CoverDefender or TacticalJob.ProtectGap or TacticalJob.Marker
        or TacticalJob.DefensiveLine or TacticalJob.Shape;

    private static bool IsPossessionJob(TacticalJob job) => job is
        TacticalJob.BallResponder or TacticalJob.NearOutlet or TacticalJob.ThirdPlayerOption
        or TacticalJob.FarSideWidth or TacticalJob.DepthRun or TacticalJob.Overlap
        or TacticalJob.DefensiveCover or TacticalJob.Shape;
}
