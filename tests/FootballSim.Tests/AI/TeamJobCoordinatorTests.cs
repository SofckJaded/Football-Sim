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
/// The allocator's duty counts have to describe the jobs it actually handed out. A player elected to
/// press who was already counted as a marker must move that count, not add a second copy of it.
/// </summary>
public sealed class TeamJobCoordinatorTests
{
    private static readonly PitchDimensions Pitch = new();

    [Fact]
    public void AMarkerElectedToPress_DoesNotConsumeTheMarkerBudget()
    {
        var homeCbA = Player(102, TeamId.Home, TacticalRole.CenterBack, new Vector3(-25f, 0f, -8f));
        var homeCbB = Player(103, TeamId.Home, TacticalRole.CenterBack, new Vector3(-25f, 0f, 8f));
        var homeCbC = Player(104, TeamId.Home, TacticalRole.CenterBack, new Vector3(-25f, 0f, 0f));
        var homeCbE = Player(106, TeamId.Home, TacticalRole.CenterBack, new Vector3(-25f, 0f, 5f));
        var homeCbD = Player(105, TeamId.Home, TacticalRole.CenterBack, new Vector3(20f, 0f, -20f));
        // The fourth centre-back's shape slot is far from where he starts, so his line duty is not kept
        // on the second pass. He is therefore available to pick up the threat the elected presser leaves.
        homeCbD.TacticalAnchor = new Vector3(0f, 0f, 30f);

        var awayA = Player(201, TeamId.Away, TacticalRole.Striker, new Vector3(-20f, 0f, -10f));
        var awayB = Player(202, TeamId.Away, TacticalRole.Striker, new Vector3(-20f, 0f, 0f));
        var players = new List<FootballerState>
        {
            homeCbA, homeCbB, homeCbC, homeCbE, homeCbD, awayA, awayB
        };

        var homeFormation = new TeamFormation(TeamId.Home, Pitch);
        homeFormation.AssignFromPositions(new[] { homeCbA, homeCbB, homeCbC, homeCbE, homeCbD }, 1f);
        var awayFormation = new TeamFormation(TeamId.Away, Pitch);
        awayFormation.AssignFromPositions(new[] { awayA, awayB }, -1f);

        var coordinator = new TeamJobCoordinator(Pitch);
        var ball = new BallState
        {
            Position = awayA.Position with { Y = 0.11f },
            PreviousPosition = awayA.Position with { Y = 0.11f },
            Velocity = Vector3.Zero
        };
        var possession = new BallPossessionSnapshot(
            BallControlState.Controlled, TeamId.Away, awayA.Id, null);

        coordinator.Update(
            players, ball, possession, possessingTeam: TeamId.Away,
            homeResponderId: null, awayResponderId: null, homeDirX: 1f, tick: 1,
            homeProfile: TeamTacticalProfile.PossessionControl,
            awayProfile: TeamTacticalProfile.DirectCounterattack,
            homeOpponents: TeamBelief.FromTruth(TeamId.Home, players),
            awayOpponents: TeamBelief.FromTruth(TeamId.Away, players),
            homeFormation: homeFormation, awayFormation: awayFormation);

        // Both threats must be marked before the responder is introduced; otherwise the fixture cannot
        // show the count moving from marker to presser.
        var firstMarkers = new List<FootballerState>();
        foreach (FootballerState player in new[] { homeCbA, homeCbB, homeCbC, homeCbE, homeCbD })
        {
            if (coordinator.Get(player.Id)?.Job == TacticalJob.Marker) firstMarkers.Add(player);
        }

        var jobs = new System.Text.StringBuilder();
        foreach (FootballerState player in new[] { homeCbA, homeCbB, homeCbC, homeCbE, homeCbD })
            jobs.Append($"#{player.Id}:{coordinator.Get(player.Id)?.Job}/{coordinator.Get(player.Id)?.TargetPlayerId} ");
        Assert.True(firstMarkers.Count == 2,
            $"expected two markers, got {firstMarkers.Count}; jobs {jobs} cap " +
            $"{DefensiveLineModel.MarkerCount(TeamTacticalProfile.PossessionControl)}");
        FootballerState responder = firstMarkers[0];
        int vacatedThreatId = coordinator.Get(responder.Id)!.Value.TargetPlayerId!.Value;
        Assert.Equal(TacticalJob.DefensiveLine, coordinator.Get(homeCbD.Id)?.Job);

        // The responder is now the elected presser. The old marker count must be released, leaving a
        // spare defender able to mark the threat the presser just left.
        homeCbD.Position = new Vector3(-18f, 0f, -10f);
        coordinator.Update(
            players, ball, possession, possessingTeam: TeamId.Away,
            homeResponderId: responder.Id, awayResponderId: null, homeDirX: 1f, tick: 7,
            homeProfile: TeamTacticalProfile.PossessionControl,
            awayProfile: TeamTacticalProfile.DirectCounterattack,
            homeOpponents: TeamBelief.FromTruth(TeamId.Home, players),
            awayOpponents: TeamBelief.FromTruth(TeamId.Away, players),
            homeFormation: homeFormation, awayFormation: awayFormation);

        Assert.Equal(TacticalJob.Presser, coordinator.Get(responder.Id)?.Job);
        Assert.Equal(TacticalJob.Marker, coordinator.Get(homeCbD.Id)?.Job);
        Assert.Equal(vacatedThreatId, coordinator.Get(homeCbD.Id)?.TargetPlayerId);

        int homeMarkers = 0;
        foreach (FootballerState player in players)
        {
            if (player.Team != TeamId.Home) continue;
            if (coordinator.Get(player.Id)?.Job == TacticalJob.Marker) homeMarkers++;
        }

        Assert.Equal(2, homeMarkers);
    }

    private static FootballerState Player(int id, TeamId team, TacticalRole role, Vector3 position) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(role),
        Position = position,
        TacticalAnchor = position,
        FacingXZ = new Vector2(team == TeamId.Home ? 1f : -1f, 0f)
    };
}
