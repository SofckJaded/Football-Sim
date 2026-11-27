using System;
using System.Collections.Generic;
using System.Linq;
using FootballSim.Core;
using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Players;
using FootballSim.World.Simulation;
using FootballSim.World.Tests.Data;
using FootballSim.World.World;
using Xunit;

namespace FootballSim.World.Tests.Simulation;

/// <summary>
/// W2 squad-selection contract (plan §5.4): the XI and bench are built greedily from the live selection
/// score, availability is respected, the goalkeeper slot prefers a natural keeper, the documented weights
/// are reproduced exactly and the squad-status buckets are derived from the same ranking.
///
/// Every test builds its own two-club world from the W0 pack plus <see cref="PlayerGenerator"/>, so the
/// suite exercises the selector in isolation from the world generator and the season runner.
/// </summary>
public sealed class SquadSelectorTests
{
    private const int PlayersPerSquad = 24;

    // =============================================================================================
    // Team shape.
    // =============================================================================================

    [Fact]
    public void Select_BuildsElevenInSlotOrderAndSevenOnTheBench()
    {
        WorldState world = TestWorld.Create();
        WorldClub club = TestWorld.CreateClub(world, clubNumber: 1, tier: 1, seedBase: 5_000UL);

        SelectedLineup lineup = SquadSelector.Select(world, club);

        Assert.Equal(club.Id, lineup.ClubId);
        Assert.Equal(FormationKind.FourFourTwo, lineup.Formation);
        Assert.Equal(Formations.StartingRoles(lineup.Formation), lineup.StartingRoles);
        Assert.Equal(11, lineup.StartingXI.Count);
        Assert.Equal(7, lineup.Bench.Count);
        Assert.Equal(Formations.BenchRoles(), lineup.BenchRoles);

        // Eleven unique starters and seven unique substitutes, with no overlap between the two lists.
        Assert.Equal(11, lineup.StartingXI.Distinct().Count());
        Assert.Equal(7, lineup.Bench.Distinct().Count());
        Assert.Empty(lineup.StartingXI.Intersect(lineup.Bench));

        // Slot 0 is the goalkeeper, and the natural-keeper preference must have been honoured because
        // this squad contains two generated keepers.
        Assert.Equal(WorldRole.Goalkeeper, lineup.StartingRoles[0]);
        WorldPlayer keeper = world.Player(lineup.StartingXI[0]);
        bool naturalKeeperExists = club.Squad.Any(id =>
            IdentityEvaluator.Suitability(world.Player(id), WorldRole.Goalkeeper) >= 50f);
        Assert.True(naturalKeeperExists);
        Assert.True(
            IdentityEvaluator.Suitability(keeper, WorldRole.Goalkeeper) >= 50f,
            "the goalkeeper slot should hold a natural keeper when one is available");

        // Nobody unavailable is ever selected, and every selected id belongs to the club.
        foreach (PlayerId id in lineup.StartingXI.Concat(lineup.Bench))
        {
            Assert.Contains(id, club.Squad);
            Assert.True(SquadSelector.IsAvailable(world.Player(id)), $"unavailable player {id} was selected");
        }
    }

    [Fact]
    public void Select_UsesTheManagersFormation()
    {
        WorldState world = TestWorld.Create();
        WorldClub club = TestWorld.CreateClub(
            world, clubNumber: 1, tier: 1, seedBase: 6_000UL, formation: FormationKind.FourThreeThree);

        SelectedLineup lineup = SquadSelector.Select(world, club);

        Assert.Equal(FormationKind.FourThreeThree, lineup.Formation);
        Assert.Equal(Formations.StartingRoles(FormationKind.FourThreeThree), lineup.StartingRoles);
        Assert.Equal(WorldRole.Goalkeeper, lineup.StartingRoles[0]);
        Assert.Single(lineup.StartingRoles, role => role == WorldRole.Striker);
        Assert.Equal(2, lineup.StartingRoles.Count(role => role == WorldRole.Winger));
    }

    [Fact]
    public void Select_FieldsOutfieldPlayerInGoalWhenNoNaturalKeeperExists()
    {
        WorldState world = TestWorld.Create();
        WorldClub club = TestWorld.CreateClub(
            world, clubNumber: 1, tier: 3, seedBase: 7_000UL, playerCount: 18, includeGoalkeepers: false);

        // Guarantee no outfield player is a natural keeper, so the fallback path is the only way to fill goal.
        foreach (PlayerId id in club.Squad)
        {
            world.Player(id).Attributes.SetCurrent(AttributeId.Goalkeeping, 1f);
        }

        SelectedLineup lineup = SquadSelector.Select(world, club);

        Assert.Equal(11, lineup.StartingXI.Count);
        Assert.Equal(7, lineup.Bench.Count);
        Assert.Equal(WorldRole.Goalkeeper, lineup.StartingRoles[0]);
        Assert.True(
            IdentityEvaluator.Suitability(world.Player(lineup.StartingXI[0]), WorldRole.Goalkeeper) < 50f,
            "this squad has no natural keeper, so the fallback should field an outfield player");
    }

    // =============================================================================================
    // Availability and merit.
    // =============================================================================================

    [Fact]
    public void Select_NeverPicksInjuredOrSuspendedPlayers()
    {
        WorldState world = TestWorld.Create();
        WorldClub club = TestWorld.CreateClub(world, clubNumber: 1, tier: 1, seedBase: 8_000UL);

        WorldPlayer injured = world.Player(club.Squad[2]);
        injured.Injury.Begin(InjuryKind.HamstringTear, InjurySeverity.Moderate, weeksOut: 4);

        WorldPlayer suspended = world.Player(club.Squad[3]);
        suspended.SuspensionWeeksRemaining = 2;

        SelectedLineup lineup = SquadSelector.Select(world, club);

        Assert.DoesNotContain(injured.Id, lineup.StartingXI);
        Assert.DoesNotContain(injured.Id, lineup.Bench);
        Assert.DoesNotContain(suspended.Id, lineup.StartingXI);
        Assert.DoesNotContain(suspended.Id, lineup.Bench);
        Assert.False(SquadSelector.IsAvailable(injured));
        Assert.False(SquadSelector.IsAvailable(suspended));
        Assert.All(
            lineup.StartingXI.Concat(lineup.Bench),
            id => Assert.True(SquadSelector.IsAvailable(world.Player(id))));
    }

    [Fact]
    public void Select_PrefersAClearlyBetterPlayerOverAClearlyWorseOne()
    {
        WorldState world = TestWorld.Create();
        WorldClub club = TestWorld.CreateClub(world, clubNumber: 1, tier: 1, seedBase: 9_000UL);

        // Two players of the same age, one a full-capability standout and one a clearly limited squad man.
        WorldPlayer better = world.Player(club.Squad[2]);
        WorldPlayer worse = world.Player(club.Squad[3]);
        better.Age = 24;
        worse.Age = 24;
        MakeElite(better);
        MakeLimited(worse);

        const WorldRole comparisonRole = WorldRole.CentralMidfielder;
        Assert.True(
            SquadSelector.SelectionScore(better, comparisonRole, club) >
            SquadSelector.SelectionScore(worse, comparisonRole, club) + 0.20f,
            "the standout should score clearly higher for the same slot role");

        SelectedLineup lineup = SquadSelector.Select(world, club);

        Assert.Contains(better.Id, lineup.StartingXI);
        Assert.DoesNotContain(worse.Id, lineup.StartingXI);
        Assert.DoesNotContain(worse.Id, lineup.Bench);
    }

    [Fact]
    public void Select_ThrowsWhenFewerThanElevenPlayersAreAvailable()
    {
        WorldState world = TestWorld.Create();
        WorldClub club = TestWorld.CreateClub(world, clubNumber: 1, tier: 1, seedBase: 10_000UL);

        // Suspend fourteen players, leaving only ten available.
        for (int i = 0; i < 14; i++) world.Player(club.Squad[i]).SuspensionWeeksRemaining = 1;
        int available = club.Squad.Count(id => SquadSelector.IsAvailable(world.Player(id)));
        Assert.Equal(10, available);

        Assert.Throws<InvalidOperationException>(() => SquadSelector.Select(world, club));
    }

    [Fact]
    public void Select_ThrowsWhenAnXIAndAFullBenchCannotBeFilled()
    {
        WorldState world = TestWorld.Create();
        WorldClub club = TestWorld.CreateClub(world, clubNumber: 1, tier: 1, seedBase: 11_000UL);

        // Seventeen available: enough for the XI, not enough for the eleven plus seven bench contract.
        for (int i = 0; i < 7; i++) world.Player(club.Squad[i]).SuspensionWeeksRemaining = 1;

        Assert.Throws<InvalidOperationException>(() => SquadSelector.Select(world, club));
    }

    // =============================================================================================
    // Determinism.
    // =============================================================================================

    [Fact]
    public void Select_IsDeterministicAcrossRepeatedCallsAndWorlds()
    {
        WorldState firstWorld = TestWorld.Create();
        WorldClub firstClub = TestWorld.CreateClub(firstWorld, clubNumber: 1, tier: 1, seedBase: 12_000UL);

        SelectedLineup first = SquadSelector.Select(firstWorld, firstClub);
        SelectedLineup second = SquadSelector.Select(firstWorld, firstClub);

        Assert.Equal(first.StartingXI, second.StartingXI);
        Assert.Equal(first.StartingRoles, second.StartingRoles);
        Assert.Equal(first.Bench, second.Bench);
        Assert.Equal(first.BenchRoles, second.BenchRoles);
        Assert.Equal(first.Formation, second.Formation);

        // Rebuilding the identical world (same seeds and registration order) must select the same team.
        WorldState rebuiltWorld = TestWorld.Create();
        WorldClub rebuiltClub = TestWorld.CreateClub(rebuiltWorld, clubNumber: 1, tier: 1, seedBase: 12_000UL);
        SelectedLineup rebuilt = SquadSelector.Select(rebuiltWorld, rebuiltClub);
        Assert.Equal(first.StartingXI, rebuilt.StartingXI);
        Assert.Equal(first.Bench, rebuilt.Bench);
    }

    // =============================================================================================
    // Squad status and the transparent score breakdown.
    // =============================================================================================

    [Fact]
    public void SquadStatusOf_ReportsTheActualMatchdaySelection()
    {
        WorldState world = TestWorld.Create();
        WorldClub club = TestWorld.CreateClub(world, clubNumber: 1, tier: 1, seedBase: 13_000UL);
        SelectedLineup lineup = SquadSelector.Select(world, club);

        var documented = new[] { "Key Starter", "First Team", "Rotation / Bench", "Reserves" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (PlayerId id in club.Squad)
        {
            string status = SquadSelector.SquadStatusOf(world, club, id);
            Assert.Contains(status, documented);
            seen.Add(status);
        }

        foreach (PlayerId id in lineup.StartingXI)
        {
            Assert.Equal("Key Starter", SquadSelector.SquadStatusOf(world, club, id));
        }

        foreach (PlayerId id in lineup.Bench)
        {
            Assert.Equal("First Team", SquadSelector.SquadStatusOf(world, club, id));
        }

        foreach (PlayerId id in club.Squad.Except(lineup.StartingXI).Except(lineup.Bench))
        {
            Assert.Equal("Rotation / Bench", SquadSelector.SquadStatusOf(world, club, id));
        }

        Assert.Equal(3, seen.Count);

        // The best available player by best-slot selection score must be a key starter.
        PlayerId bestId = default;
        float bestScore = float.NegativeInfinity;
        foreach (PlayerId id in club.Squad)
        {
            WorldPlayer player = world.Player(id);
            if (!SquadSelector.IsAvailable(player)) continue;
            foreach (WorldRole role in Formations.StartingRoles(lineup.Formation))
            {
                float score = SquadSelector.SelectionScore(player, role, club);
                if (score > bestScore ||
                    (score == bestScore && bestId.IsValid && id.Value < bestId.Value))
                {
                    bestScore = score;
                    bestId = id;
                }
            }
        }

        Assert.True(bestId.IsValid);
        Assert.Equal("Key Starter", SquadSelector.SquadStatusOf(world, club, bestId));

        // An unavailable player drops out of the matchday squad and is listed as a reserve.
        PlayerId injuredId = club.Squad.First(id => !lineup.StartingXI.Contains(id) && !lineup.Bench.Contains(id));
        world.Player(injuredId).Injury.Begin(InjuryKind.AnkleLigament, InjurySeverity.Minor, weeksOut: 2);
        Assert.Equal("Reserves", SquadSelector.SquadStatusOf(world, club, injuredId));
    }

    [Fact]
    public void ScoreBreakdown_ReproducesTheDocumentedWeightedFormula()
    {
        WorldState world = TestWorld.Create();
        WorldClub club = TestWorld.CreateClub(world, clubNumber: 1, tier: 1, seedBase: 14_000UL);
        WorldPlayer player = world.Player(club.Squad[5]);
        club.SetManagerTrust(player.Id, 0.71f);

        // Give the player a form window so the form term is non-trivial.
        player.Form.Record(new MatchAppearance(1, 5, 90, 7.6f));
        player.Form.Record(new MatchAppearance(1, 6, 90, 6.8f));
        player.Condition01 = 0.82f;
        player.MatchSharpness01 = 0.74f;

        const WorldRole role = WorldRole.CentralMidfielder;
        SelectionScoreBreakdown breakdown = SquadSelector.ScoreBreakdown(player, role, club);

        Assert.Equal(
            IdentityEvaluator.Suitability(player, role) / 100f,
            breakdown.RoleSuitability01,
            5);
        Assert.Equal(player.Form.Form01(8), breakdown.Form01);
        Assert.Equal(player.Condition01, breakdown.Condition01);
        Assert.Equal(player.MatchSharpness01, breakdown.Sharpness01);
        Assert.Equal(club.ManagerTrustOf(player.Id), breakdown.ManagerTrust01);

        float expected =
            0.50f * breakdown.RoleSuitability01 +
            0.18f * breakdown.Form01 +
            0.14f * breakdown.Condition01 +
            0.10f * breakdown.Sharpness01 +
            0.08f * breakdown.ManagerTrust01;
        Assert.True(
            MathF.Abs(breakdown.Total - expected) < 1e-4f,
            $"Total {breakdown.Total} should equal the documented weighted formula {expected}");
        Assert.Equal(breakdown.Total, SquadSelector.SelectionScore(player, role, club));
    }

    // =============================================================================================
    // Helpers.
    // =============================================================================================

    /// <summary>Makes one player a full-capability standout (99 in every attribute, all conditions ideal).</summary>
    private static void MakeElite(WorldPlayer player)
    {
        foreach (AttributeId id in AttributeMetadata.All)
        {
            // Keep the goalkeeper attribute low enough that this outfield player is not chosen in goal.
            float target = id == AttributeId.Goalkeeping ? 10f : 99f;
            player.Attributes.SetCeiling(id, MathF.Max(target, 20f));
            player.Attributes.SetCurrent(id, target);
        }

        player.Condition01 = 1f;
        player.MatchSharpness01 = 1f;
        player.Form01 = 1f;
    }

    /// <summary>Makes one player a clearly limited squad member at the attribute floor.</summary>
    private static void MakeLimited(WorldPlayer player)
    {
        foreach (AttributeId id in AttributeMetadata.All) player.Attributes.SetCurrent(id, 15f);

        player.Condition01 = 0.3f;
        player.MatchSharpness01 = 0.1f;
        player.Form01 = 0.1f;
    }

    /// <summary>Hand-built world: the W0 pack plus generated clubs and managers; no W2 generators involved.</summary>
    private static class TestWorld
    {
        private static readonly TacticalRole[] FullRoleMix =
        {
            TacticalRole.Goalkeeper, TacticalRole.Goalkeeper,
            TacticalRole.CenterBack, TacticalRole.CenterBack, TacticalRole.CenterBack, TacticalRole.CenterBack,
            TacticalRole.FullBack, TacticalRole.FullBack, TacticalRole.FullBack, TacticalRole.FullBack,
            TacticalRole.DefensiveMidfielder, TacticalRole.DefensiveMidfielder,
            TacticalRole.CentralMidfielder, TacticalRole.CentralMidfielder,
            TacticalRole.CentralMidfielder, TacticalRole.CentralMidfielder,
            TacticalRole.Winger, TacticalRole.Winger,
            TacticalRole.Striker, TacticalRole.Striker, TacticalRole.Striker, TacticalRole.Striker,
            TacticalRole.CentralMidfielder, TacticalRole.FullBack
        };

        private static readonly TacticalRole[] OutfieldOnlyMix =
        {
            TacticalRole.CenterBack, TacticalRole.CenterBack, TacticalRole.FullBack, TacticalRole.FullBack,
            TacticalRole.DefensiveMidfielder, TacticalRole.CentralMidfielder, TacticalRole.CentralMidfielder,
            TacticalRole.Winger, TacticalRole.Winger, TacticalRole.Striker, TacticalRole.Striker,
            TacticalRole.CenterBack, TacticalRole.FullBack, TacticalRole.DefensiveMidfielder,
            TacticalRole.CentralMidfielder, TacticalRole.Winger, TacticalRole.Striker, TacticalRole.CentralMidfielder
        };

        internal static WorldState Create()
        {
            WorldPack pack = WorldPackLoader.Parse(TestWorldPacks.ValidJson());
            return new WorldState(pack, seed: 20_261_127UL, WorldDate.Start);
        }

        internal static WorldClub CreateClub(
            WorldState world,
            int clubNumber,
            int tier,
            ulong seedBase,
            FormationKind formation = FormationKind.FourFourTwo,
            int playerCount = PlayersPerSquad,
            bool includeGoalkeepers = true)
        {
            IReadOnlyList<ClubDef> tierClubs = world.Pack.ClubsInTier(tier);
            ClubDef definition = tierClubs[clubNumber % tierClubs.Count];
            var club = new WorldClub(
                new ClubId(clubNumber),
                definition,
                new LeagueId(1),
                ManagerId.None,
                new ClubFacilities(),
                new ClubFinances());

            ManagerId managerId = world.AllocateManagerId();
            world.Managers.Add(new WorldManager(
                managerId,
                "Test",
                $"Manager{clubNumber}",
                age: 45,
                club.Id,
                TeamTacticalProfile.PossessionControl,
                formation,
                reputation: 60));
            club.ManagerId = managerId;
            world.Clubs.Add(club);

            for (int i = 0; i < playerCount; i++)
            {
                TacticalRole role = includeGoalkeepers
                    ? FullRoleMix[i % FullRoleMix.Length]
                    : OutfieldOnlyMix[i % OutfieldOnlyMix.Length];
                ulong playerSeed = seedBase + (ulong)(i * 37 + 1);
                WorldPlayer player = PlayerGenerator.GenerateNpcForTier(
                    playerSeed,
                    tier,
                    age: 22 + i % 7,
                    role,
                    world.AllocatePlayerId());
                player.ClubId = club.Id;
                world.Players.Add(player);
                club.AddPlayer(player.Id);
            }

            return club;
        }
    }
}
