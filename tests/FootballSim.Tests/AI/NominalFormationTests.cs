using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Scenarios;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A3.3 — nominal formation ownership.
///
/// The formation used to be a shirt-number lookup executed every time an anchor was written: a player's
/// place was a property of his number, a 3v2 fixture inherited the eleven-a-side slot table, and two
/// players in the same area were only kept apart by their shirts. Ownership is now assigned once, per
/// player id, and only a deliberate act moves it.
/// </summary>
public sealed class NominalFormationTests
{
    private static readonly PitchDimensions Pitch = new();

    private static FootballerState Player(int id, TeamId team, TacticalRole role, int shirt, Vector3 position) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = shirt,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(role),
        Position = position,
        FacingXZ = new Vector2(1f, 0f)
    };

    private static List<FootballerState> ElevenAtKickoff()
    {
        var players = new List<FootballerState>
        {
            Player(1, TeamId.Home, TacticalRole.Goalkeeper, 1, new Vector3(-48f, 0f, 0f)),
            Player(2, TeamId.Home, TacticalRole.FullBack, 2, new Vector3(-24f, 0f, 24f)),
            Player(3, TeamId.Home, TacticalRole.FullBack, 3, new Vector3(-24f, 0f, -24f)),
            Player(4, TeamId.Home, TacticalRole.CenterBack, 4, new Vector3(-22f, 0f, 9f)),
            Player(5, TeamId.Home, TacticalRole.CenterBack, 5, new Vector3(-22f, 0f, -9f)),
            Player(6, TeamId.Home, TacticalRole.DefensiveMidfielder, 6, new Vector3(-9f, 0f, 0f)),
            Player(7, TeamId.Home, TacticalRole.Winger, 7, new Vector3(16f, 0f, 26f)),
            Player(8, TeamId.Home, TacticalRole.CentralMidfielder, 8, new Vector3(1f, 0f, 11f)),
            Player(9, TeamId.Home, TacticalRole.Striker, 9, new Vector3(21f, 0f, 0f)),
            Player(10, TeamId.Home, TacticalRole.CentralMidfielder, 10, new Vector3(2f, 0f, -11f)),
            Player(11, TeamId.Home, TacticalRole.Winger, 11, new Vector3(16f, 0f, -26f))
        };

        return players;
    }

    [Fact]
    public void TheSquadTemplate_IsReadOnce_AndNormalPlayNeverRewritesIt()
    {
        List<FootballerState> players = ElevenAtKickoff();
        var formation = new TeamFormation(TeamId.Home, Pitch);

        formation.AssignFromShirts(players, teamSize: 11);

        // The keeper is not an outfield slot (A3.3): eleven players own ten slots.
        Assert.Equal(10, formation.Count);
        Assert.Equal(10, formation.Assignments);
        Assert.Equal(0, formation.Reassignments);

        // A minute of normal play: every player near enough to the slot he owns, so nothing is a
        // mismatch and the formation is untouched.
        for (int tick = 1; tick <= 600; tick++)
        {
            foreach (FootballerState player in players)
            {
                formation.TryGetWorldAnchor(player.Id, 1f, out Vector3 slot);
                player.Position = slot + new Vector3(6f, 0f, 4f);
                formation.ReviewMismatch(player, 1f, tick, onOwnDuty: true);
            }
        }

        Assert.Equal(0, formation.Reassignments);
        Assert.Equal(10, formation.Assignments);
    }

    [Fact]
    public void APositionalPerturbation_DoesNotSwapNominalRoles()
    {
        List<FootballerState> players = ElevenAtKickoff();
        var formation = new TeamFormation(TeamId.Home, Pitch);
        formation.AssignFromShirts(players, teamSize: 11);

        FootballerState right = players[3];   // #4, the +Z centre-back
        FootballerState left = players[4];    // #5, the −Z centre-back
        Assert.True(formation.TryGet(right.Id, out NominalSlot rightSlot));
        Assert.True(formation.TryGet(left.Id, out NominalSlot leftSlot));
        Assert.True(rightSlot.NormZ > 0f && leftSlot.NormZ < 0f);

        // The two centre-backs cross over each other by a couple of metres. A role that were derived
        // from position would now hand each man the other's slot.
        for (int tick = 1; tick <= 300; tick++)
        {
            right.Position = new Vector3(-20f + tick * 0.002f, 0f, 6f - tick * 0.02f);
            left.Position = new Vector3(-20f, 0f, -6f + tick * 0.02f);
            formation.ReviewMismatch(right, 1f, tick, onOwnDuty: true);
            formation.ReviewMismatch(left, 1f, tick, onOwnDuty: true);
        }

        Assert.True(formation.TryGet(right.Id, out NominalSlot rightAfter));
        Assert.True(formation.TryGet(left.Id, out NominalSlot leftAfter));
        Assert.Equal(rightSlot, rightAfter);
        Assert.Equal(leftSlot, leftAfter);
        Assert.Equal(0, formation.Reassignments);
    }

    [Fact]
    public void AFixtureOwnsTheShapeItDescribes_NotTheElevenASideTable()
    {
        // build-up-3v2 is a three-a-side home team whose centre-back stands at (−20, −4). The eleven-a-
        // side table would give shirt #4 the slot (−0.55, 0.26) — about (−22.5, +8.8) in the world.
        var engine = new MatchSimulationEngine(ScenarioLibrary.BuildUpThreeVersusTwo());
        TeamFormation formation = engine.Tactics.HomeFormation;

        Assert.Equal(2, formation.Count);   // the keeper holds no outfield slot
        Assert.Equal(2, formation.Assignments);

        foreach (ScenarioPlayer spec in ScenarioLibrary.BuildUpThreeVersusTwo().Players)
        {
            if (spec.Team != TeamId.Home) continue;
            if (spec.Role == TacticalRole.Goalkeeper) continue;
            Assert.True(formation.TryGetWorldAnchor(spec.Id, 1f, out Vector3 anchor));
            Assert.Equal(spec.Position.X, anchor.X, precision: 2);
            Assert.Equal(spec.Position.Z, anchor.Z, precision: 2);
        }

        FootballerState centreBack = engine.Players.First(p => p.Id == ScenarioLibrary.BuildUpHomeCb);
        Assert.True(formation.TryGetWorldAnchor(centreBack.Id, 1f, out Vector3 fixtureSlot));
        Vector3 tableSlot = new(-22.5f, 0f, 8.84f);
        Assert.True(Vector3.Distance(fixtureSlot, tableSlot) > 5f,
            $"fixture slot {fixtureSlot} looks like the eleven-a-side table slot {tableSlot}");
    }

    [Fact]
    public void APersistentStationaryMismatch_MovesOneSlot_OnlyAfterItsWindow()
    {
        var formation = new TeamFormation(TeamId.Home, Pitch);
        FootballerState striker = Player(9, TeamId.Home, TacticalRole.Striker, 9, new Vector3(21f, 0f, 0f));
        var players = new List<FootballerState> { striker };
        formation.AssignFromShirts(players, teamSize: 11);
        Assert.True(formation.TryGet(9, out NominalSlot before));

        // He has been standing 25 m away from the slot he owns. Under the window, nothing moves.
        striker.Position = new Vector3(5f, 0f, -30f);
        for (int tick = 1; tick < 1100; tick++)
            Assert.False(formation.ReviewMismatch(striker, 1f, tick, onOwnDuty: true));

        Assert.Equal(0, formation.Reassignments);
        Assert.True(formation.TryGet(9, out NominalSlot unchanged));
        Assert.Equal(before, unchanged);

        // Past the 20 s window the slot moves — by a bounded step, not a teleport.
        bool moved = false;
        for (int tick = 1100; tick < 1300 && !moved; tick++)
            moved = formation.ReviewMismatch(striker, 1f, tick, onOwnDuty: true);

        Assert.True(moved, "a 20 s stationary mismatch never moved the slot");
        Assert.Equal(1, formation.Reassignments);
        Assert.True(formation.TryGet(9, out NominalSlot after));
        Assert.NotEqual(before, after);
        Assert.True(NominalSlotGap(before, after) <= 0.51f, "the reassignment was not a bounded step");

        // And not again on the next tick: the window restarts.
        Assert.False(formation.ReviewMismatch(striker, 1f, 1301, onOwnDuty: true));
        Assert.Equal(1, formation.Reassignments);
    }

    [Fact]
    public void AContextualDuty_IsNotAMismatch_NoMatterHowLongItLasts()
    {
        var formation = new TeamFormation(TeamId.Home, Pitch);
        FootballerState winger = Player(7, TeamId.Home, TacticalRole.Winger, 7, new Vector3(16f, 0f, 26f));
        formation.AssignFromShirts(new List<FootballerState> { winger }, teamSize: 11);

        // Holding far-side width is a duty somewhere else, not a broken formation.
        winger.Position = new Vector3(16f, 0f, -30f);
        for (int tick = 1; tick <= 5000; tick++)
            Assert.False(formation.ReviewMismatch(winger, 1f, tick, onOwnDuty: false));

        Assert.Equal(0, formation.Reassignments);
    }

    [Fact]
    public void RemovingAPlayer_DropsHisOwnership()
    {
        List<FootballerState> players = ElevenAtKickoff();
        var formation = new TeamFormation(TeamId.Home, Pitch);
        formation.AssignFromShirts(players, teamSize: 11);

        formation.Remove(4);

        Assert.Equal(9, formation.Count);
        Assert.False(formation.TryGet(4, out _));
        Assert.False(formation.TryGetWorldAnchor(4, 1f, out _));
    }

    private static float NominalSlotGap(in NominalSlot a, in NominalSlot b)
    {
        float dx = a.NormX - b.NormX;
        float dz = a.NormZ - b.NormZ;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
