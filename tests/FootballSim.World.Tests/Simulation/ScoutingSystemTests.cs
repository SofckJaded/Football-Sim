using System;
using System.Collections.Generic;
using System.Linq;
using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Players;
using FootballSim.World.Simulation;
using FootballSim.World.World;
using Xunit;
using Xunit.Abstractions;

namespace FootballSim.World.Tests.Simulation;

/// <summary>
/// Stage W3 scouting contract (plan §5.4): the visibility baselines and backstory exposure table, the weekly
/// visibility tick, the cup giant-killing spotlight and the scouting shortlist the transfer market works from.
///
/// <para>The generated world is created once and used read-only; visibility is a per-player mutable signal, so
/// every test that drives the weekly tick or inspects a controlled shortlist builds its own fresh world.</para>
/// </summary>
public sealed class ScoutingSystemTests
{
    private const ulong DefaultSeed = 20261127UL;

    private static readonly Lazy<WorldPack> FullPack =
        new(() => WorldPackLoader.Load(WorldPackPaths.ResolveDefaultPack()));

    /// <summary>One generated, never-mutated world used by the read-only table tests.</summary>
    private static readonly Lazy<WorldState> Generated =
        new(() => WorldGenerator.Generate(FullPack.Value, DefaultSeed));

    private readonly ITestOutputHelper _output;

    public ScoutingSystemTests(ITestOutputHelper output) => _output = output;

    private static WorldPack Pack => FullPack.Value;

    // =============================================================================================
    // 1. Documented tables.
    // =============================================================================================

    [Fact]
    public void VisibilityTables_MatchTheDocumentedValues()
    {
        Assert.Equal(0.55f, ScoutingSystem.VisibilityBaselineForTier(1));
        Assert.Equal(0.45f, ScoutingSystem.VisibilityBaselineForTier(2));
        Assert.Equal(0.35f, ScoutingSystem.VisibilityBaselineForTier(3));
        Assert.Equal(0.25f, ScoutingSystem.VisibilityBaselineForTier(4));
        Assert.Equal(0.16f, ScoutingSystem.VisibilityBaselineForTier(5));
        Assert.Equal(0.10f, ScoutingSystem.VisibilityBaselineForTier(6));

        Assert.Equal(1.45f, ScoutingSystem.ExposureMultiplier(BackstoryKind.EliteAcademy));
        Assert.Equal(1.15f, ScoutingSystem.ExposureMultiplier(BackstoryKind.RegionalAcademy));
        Assert.Equal(0.75f, ScoutingSystem.ExposureMultiplier(BackstoryKind.LocalClubYouth));
        Assert.Equal(0.60f, ScoutingSystem.ExposureMultiplier(BackstoryKind.StreetFootball));
        Assert.Equal(0.70f, ScoutingSystem.ExposureMultiplier(BackstoryKind.MultisportLateStarter));

        Assert.Equal(2.5f, ScoutingSystem.GiantKillingVisibilityMultiplier);

        Assert.Throws<ArgumentOutOfRangeException>(() => ScoutingSystem.VisibilityBaselineForTier(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScoutingSystem.VisibilityBaselineForTier(7));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScoutingSystem.ExposureMultiplier((BackstoryKind)99));
    }

    // =============================================================================================
    // 2. Weekly visibility tick.
    // =============================================================================================

    [Fact]
    public void AdvanceWeek_KeepsVisibilityInRangeAndMovesAPerformingPlayerUp()
    {
        WorldState world = NewWorld();

        WorldPlayer riser = FindPlayer(
            world, player => player.ClubId.IsValid && world.Clubs[player.ClubId].Tier == 1 && player.Age <= 22);
        riser.Visibility01 = 0.05f;
        riser.Form.Record(new MatchAppearance(world.Season, world.Week, 90, 8.5f));
        riser.CurrentSeason.Goals = 5;
        riser.CurrentSeason.Assists = 2;

        WorldPlayer faller = FindPlayer(
            world, player => player.ClubId.IsValid && world.Clubs[player.ClubId].Tier == 6);
        faller.Visibility01 = 0.95f;
        faller.Form.Record(new MatchAppearance(world.Season, world.Week, 90, 5.0f));

        ScoutingSystem.AdvanceWeek(world, new WorldRng(2026_1127UL));

        Assert.True(
            riser.Visibility01 > 0.05f,
            $"a performing tier-1 youngster's visibility must rise (was 0.05, now {riser.Visibility01})");
        Assert.True(
            faller.Visibility01 < 0.95f,
            $"an unused amateur's visibility must fall toward his tier target (now {faller.Visibility01})");

        float minimum = 1f;
        float maximum = 0f;
        foreach (WorldPlayer player in world.Players.All)
        {
            Assert.False(float.IsNaN(player.Visibility01), $"player {player.Id} visibility became NaN");
            Assert.InRange(player.Visibility01, 0f, 1f);
            minimum = Math.Min(minimum, player.Visibility01);
            maximum = Math.Max(maximum, player.Visibility01);
        }

        _output.WriteLine(
            $"weekly visibility: riser {riser.Age}y tier 1 -> {riser.Visibility01:0.000}; " +
            $"amateur -> {faller.Visibility01:0.000}; world range {minimum:0.000}..{maximum:0.000}");
    }

    [Fact]
    public void AdvanceWeek_FreeAgentsDecayTowardTheVisibilityFloor()
    {
        WorldState world = NewWorld();

        WorldPlayer freeAgent = FindPlayer(world, player => player.ClubId.IsValid);
        WorldClub oldClub = world.Clubs[freeAgent.ClubId];
        oldClub.RemovePlayer(freeAgent.Id);
        freeAgent.ClubId = ClubId.None;
        freeAgent.Visibility01 = 0.90f;

        ScoutingSystem.AdvanceWeek(world, new WorldRng(7UL));

        Assert.True(freeAgent.Visibility01 < 0.90f);
        Assert.True(freeAgent.Visibility01 > 0.15f, "a free agent decays toward 0.15, never below it in one week");
    }

    // =============================================================================================
    // 3. Scouting shortlist.
    // =============================================================================================

    [Fact]
    public void FindTargets_RespectsThresholdsExclusionsOrderAndMaxTargets()
    {
        WorldState world = NewWorld();

        // Every player is market-visible; exclusions are then purely about the documented rules.
        foreach (WorldPlayer player in world.Players.All) player.Visibility01 = 0.70f;

        WorldClub buyer = world.Clubs.InTier(1)[0];
        WorldPlayer retired = FindPlayer(
            world, player => player.ClubId.IsValid && player.ClubId != buyer.Id,
            preferredTier: 1);
        retired.Visibility01 = 1.0f;
        retired.IsRetired = true;

        WorldPlayer loaned = FindPlayer(
            world, player => player.ClubId.IsValid && player.ClubId != buyer.Id && player.Id != retired.Id,
            preferredTier: 2);
        loaned.Visibility01 = 1.0f;
        loaned.Contract.IsOnLoan = true;

        WorldPlayer tooOld = FindPlayer(
            world,
            player => player.ClubId.IsValid && player.ClubId != buyer.Id &&
                player.Id != retired.Id && player.Id != loaned.Id,
            preferredTier: 3);
        tooOld.Visibility01 = 1.0f;
        tooOld.Age = 35;

        IReadOnlyList<ScoutingTarget> targets = ScoutingSystem.FindTargets(world, buyer);

        float threshold = 0.60f - 0.06f * buyer.Tier;
        Assert.NotEmpty(targets);
        Assert.True(targets.Count <= 12);
        Assert.DoesNotContain(targets, target => target.ClubId == buyer.Id);
        Assert.DoesNotContain(targets, target => target.PlayerId == retired.Id);
        Assert.DoesNotContain(targets, target => target.PlayerId == loaned.Id);
        Assert.DoesNotContain(targets, target => target.PlayerId == tooOld.Id);

        for (int index = 0; index < targets.Count; index++)
        {
            ScoutingTarget target = targets[index];
            WorldPlayer player = world.Players[target.PlayerId];
            WorldClub club = world.Clubs[target.ClubId];

            Assert.Equal(player.ClubId, target.ClubId);
            Assert.True(target.Visibility > threshold);
            Assert.True(player.Visibility01 > threshold);
            Assert.True(player.Age <= 32);
            Assert.False(player.IsRetired);
            Assert.False(player.Contract.IsOnLoan);

            // "Prefer players from lower or equal tiers": never a club above the buyer.
            Assert.True(club.Tier >= buyer.Tier);

            if (index == 0) continue;
            ScoutingTarget previous = targets[index - 1];
            Assert.True(
                previous.Score > target.Score ||
                (previous.Score == target.Score && previous.PlayerId.Value < target.PlayerId.Value),
                "the shortlist must be sorted by score descending, then player id ascending");
        }

        // Explicit max-targets cap.
        IReadOnlyCollection<ScoutingTarget> five = (IReadOnlyCollection<ScoutingTarget>)
            ScoutingSystem.FindTargets(world, buyer, maxTargets: 5);
        Assert.Equal(5, five.Count);

        // Different buyers have different thresholds: an amateur club sees much less.
        WorldClub amateurBuyer = world.Clubs.InTier(6)[0];
        IReadOnlyList<ScoutingTarget> amateurTargets = ScoutingSystem.FindTargets(world, amateurBuyer);
        Assert.NotEmpty(amateurTargets);
        Assert.All(amateurTargets, target => Assert.True(target.Visibility > 0.24f));
        Assert.All(amateurTargets, target => Assert.Equal(6, world.Clubs[target.ClubId].Tier));

        // The report is deterministic: repeated scans produce the identical ranking.
        IReadOnlyList<ScoutingTarget> again = ScoutingSystem.FindTargets(world, buyer);
        Assert.Equal(targets.Count, again.Count);
        for (int index = 0; index < targets.Count; index++)
        {
            Assert.Equal(targets[index], again[index]);
        }

        _output.WriteLine(
            $"scouting: tier-1 buyer sees {targets.Count} targets above {threshold:0.00} " +
            $"(top {world.Players[targets[0].PlayerId].DisplayName}, score {targets[0].Score:0.000}); " +
            $"tier-6 buyer sees {amateurTargets.Count} above 0.24");
    }

    [Fact]
    public void FindTargets_OnThePristineWorldIsDeterministicAndTierGated()
    {
        WorldState world = Generated.Value;
        WorldClub topFlightBuyer = world.Clubs.InTier(1)[0];
        WorldClub amateurBuyer = world.Clubs.InTier(6)[0];

        // No scouting week has run yet. Generated players start at 0.30 x backstory exposure, i.e. at most
        // 0.435, so the top flight's 0.54 threshold cannot see anybody in a freshly generated world.
        IReadOnlyList<ScoutingTarget> topFlightTargets = ScoutingSystem.FindTargets(world, topFlightBuyer);
        Assert.Empty(topFlightTargets);

        // The amateur threshold (0.24) still finds the best-exposed amateurs, capped and deterministic.
        IReadOnlyList<ScoutingTarget> amateurTargets = ScoutingSystem.FindTargets(world, amateurBuyer);
        Assert.NotEmpty(amateurTargets);
        Assert.True(amateurTargets.Count <= 12);
        Assert.All(amateurTargets, target => Assert.True(target.Visibility > 0.24f));
        Assert.All(amateurTargets, target => Assert.Equal(6, world.Clubs[target.ClubId].Tier));

        IReadOnlyList<ScoutingTarget> again = ScoutingSystem.FindTargets(world, amateurBuyer);
        Assert.Equal(amateurTargets.Count, again.Count);
        for (int index = 0; index < amateurTargets.Count; index++)
        {
            Assert.Equal(amateurTargets[index], again[index]);
        }
    }

    // =============================================================================================
    // 4. Cup giant-killing spotlight.
    // =============================================================================================

    [Fact]
    public void ApplyCupGiantKillingSpikes_MultipliesTheUnderdogWinnersSquadVisibility()
    {
        WorldState world = NewWorld();

        // Replace the generated bracket with two hand-recorded ties: a 5-division giant-killing and a
        // one-division upset that must not trigger the spotlight.
        world.Cup.Reset(world.Season);
        CupRoundBracket round = world.Cup.AddRound(CupRound.RoundOf64, 15);

        WorldClub junior = world.Clubs.InTier(6)[0];
        WorldClub giant = world.Clubs.InTier(1)[0];
        WorldClub nearGiant = world.Clubs.InTier(5)[0];
        WorldClub oneTierAbove = world.Clubs.InTier(4)[0];

        SetVisibility(world, junior, 0.25f);
        SetVisibility(world, giant, 0.25f);
        SetVisibility(world, nearGiant, 0.25f);
        SetVisibility(world, oneTierAbove, 0.25f);

        // One winner is already near the ceiling: the spike must clamp instead of overshooting.
        WorldPlayer cappedWinner = world.Players[junior.Squad[0]];
        cappedWinner.Visibility01 = 0.90f;

        AddPlayedTie(world, round, junior.Id, giant.Id, homeGoals: 2, awayGoals: 0);
        AddPlayedTie(world, round, nearGiant.Id, oneTierAbove.Id, homeGoals: 1, awayGoals: 0);

        ScoutingSystem.ApplyCupGiantKillingSpikes(world);

        Assert.Equal(1.0f, cappedWinner.Visibility01);
        foreach (PlayerId playerId in junior.Squad)
        {
            if (playerId == cappedWinner.Id) continue;
            Assert.Equal(0.625f, world.Players[playerId].Visibility01, 5);
        }

        // The defeated giant, the one-tier winners and the one-tier losers keep their normalised visibility.
        Assert.All(giant.Squad, id => Assert.Equal(0.25f, world.Players[id].Visibility01, 5));
        Assert.All(nearGiant.Squad, id => Assert.Equal(0.25f, world.Players[id].Visibility01, 5));
        Assert.All(oneTierAbove.Squad, id => Assert.Equal(0.25f, world.Players[id].Visibility01, 5));

        _output.WriteLine(
            $"giant-killing: tier-6 {junior.Name} beating tier-1 {giant.Name} x2.5 -> " +
            $"{world.Players[junior.Squad[1]].Visibility01:0.000}; a one-tier upset is ignored");
    }

    // =============================================================================================
    // Helpers.
    // =============================================================================================

    /// <summary>Generates a fresh world for a test that mutates visibility or the cup.</summary>
    private static WorldState NewWorld() => WorldGenerator.Generate(Pack, DefaultSeed);

    /// <summary>First player in registry order that satisfies the predicate.</summary>
    private static WorldPlayer FindPlayer(
        WorldState world, Func<WorldPlayer, bool> predicate, int? preferredTier = null)
    {
        if (preferredTier.HasValue)
        {
            foreach (WorldClub club in world.Clubs.InTier(preferredTier.Value))
            {
                foreach (PlayerId id in club.Squad)
                {
                    if (predicate(world.Players[id])) return world.Players[id];
                }
            }
        }

        foreach (WorldPlayer player in world.Players.All)
        {
            if (predicate(player)) return player;
        }

        throw new InvalidOperationException("The generated world contained no player matching the test predicate.");
    }

    /// <summary>Pins every squad player's visibility to one value.</summary>
    private static void SetVisibility(WorldState world, WorldClub club, float visibility)
    {
        IReadOnlyList<PlayerId> squad = club.Squad;
        for (int i = 0; i < squad.Count; i++)
        {
            world.Players[squad[i]].Visibility01 = visibility;
        }
    }

    /// <summary>Adds one played cup tie to a round (used to build the synthetic giant-killing bracket).</summary>
    private static void AddPlayedTie(
        WorldState world,
        CupRoundBracket round,
        ClubId homeClubId,
        ClubId awayClubId,
        int homeGoals,
        int awayGoals)
    {
        var fixture = new WorldFixture(
            world.AllocateMatchId(),
            world.Season,
            round.Week,
            CompetitionKind.Cup,
            LeagueId.None,
            0,
            round.Round,
            homeClubId,
            awayClubId);
        fixture.RecordCupResult(homeGoals, awayGoals, homePenaltyGoals: 0, awayPenaltyGoals: 0);
        round.AddTie(new CupTie(fixture));
    }
}
