using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FootballSim.Core;
using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Players;
using FootballSim.World.Simulation;
using FootballSim.World.Tests.Data;
using FootballSim.World.World;
using Xunit;
using Xunit.Abstractions;

namespace FootballSim.World.Tests.Simulation;

/// <summary>
/// W2 quick-sim contract (plan Â§5.3): deterministic replay, internally consistent event/stat/rating
/// output, cup shoot-outs, no world mutation, the plan's goal band and tier separation, and the
/// performance budget. Every fixture is built from the W0 pack plus <see cref="PlayerGenerator"/> so the
/// suite depends only on the W2 spine.
/// </summary>
public sealed class WorldQuickSimTests
{
    private readonly ITestOutputHelper _output;

    public WorldQuickSimTests(ITestOutputHelper output) => _output = output;

    private const int PlayersPerSquad = 24;

    // =============================================================================================
    // Determinism.
    // =============================================================================================

    [Fact]
    public void Simulate_IsDeterministicForTheSameRngSeed()
    {
        Fixture fixture = Fixture.Create(tier: 1);

        QuickSimResult first = WorldQuickSim.Simulate(
            fixture.World, fixture.Home, fixture.Away,
            fixture.HomeLineup, fixture.AwayLineup, new WorldRng(9_001UL));
        QuickSimResult second = WorldQuickSim.Simulate(
            fixture.World, fixture.Home, fixture.Away,
            fixture.HomeLineup, fixture.AwayLineup, new WorldRng(9_001UL));

        AssertSameResult(first, second);
    }

    [Fact]
    public void Simulate_ProducesDifferentMatchesForDifferentRngSeeds()
    {
        Fixture fixture = Fixture.Create(tier: 1);

        var signatures = new HashSet<string>(StringComparer.Ordinal);
        for (ulong seed = 1; seed <= 4; seed++)
        {
            QuickSimResult result = WorldQuickSim.Simulate(
                fixture.World, fixture.Home, fixture.Away,
                fixture.HomeLineup, fixture.AwayLineup, new WorldRng(seed));
            signatures.Add(Signature(result));
        }

        Assert.Equal(4, signatures.Count);
    }

    [Fact]
    public void Simulate_WrappedFixture_ReplaysIdenticallyWithoutAdvancingTheWorldStream()
    {
        Fixture fixture = Fixture.Create(tier: 1);
        var worldFixture = new WorldFixture(
            new MatchId(7),
            season: 1,
            week: 5,
            CompetitionKind.League,
            new LeagueId(1),
            leagueMatchday: 1,
            CupRound.None,
            fixture.Home.Id,
            fixture.Away.Id);

        QuickSimResult first = WorldQuickSim.Simulate(fixture.World, worldFixture);
        IReadOnlyDictionary<WorldRandomStream, ulong> streamAfterFirst = fixture.World.CaptureRngStates();
        QuickSimResult second = WorldQuickSim.Simulate(fixture.World, worldFixture);
        IReadOnlyDictionary<WorldRandomStream, ulong> streamAfterSecond = fixture.World.CaptureRngStates();

        AssertSameResult(first, second);
        Assert.Equal(fixture.Home.Id, first.HomeClubId);
        Assert.Equal(fixture.Away.Id, first.AwayClubId);
        Assert.False(worldFixture.IsPlayed, "the quick sim must not record the result on the fixture");
        Assert.Equal(streamAfterFirst.Count, streamAfterSecond.Count);
        foreach (KeyValuePair<WorldRandomStream, ulong> pair in streamAfterFirst)
        {
            Assert.Equal(pair.Value, streamAfterSecond[pair.Key]);
        }
    }

    [Fact]
    public void Simulate_DoesNotMutatePlayersOrTheWorld()
    {
        Fixture fixture = Fixture.Create(tier: 2);

        var conditionsBefore = new Dictionary<PlayerId, float>();
        var attributeSumsBefore = new Dictionary<PlayerId, float>();
        foreach (WorldPlayer player in fixture.World.Players.All)
        {
            conditionsBefore[player.Id] = player.Condition01;
            attributeSumsBefore[player.Id] = player.Attributes.MeanCurrent();
        }

        WorldQuickSim.Simulate(
            fixture.World, fixture.Home, fixture.Away,
            fixture.HomeLineup, fixture.AwayLineup, new WorldRng(77UL), knockout: true);

        foreach (WorldPlayer player in fixture.World.Players.All)
        {
            Assert.Equal(conditionsBefore[player.Id], player.Condition01);
            Assert.Equal(attributeSumsBefore[player.Id], player.Attributes.MeanCurrent());
            Assert.False(player.Injury.IsInjured, $"the sim must not record an injury on {player.Id}");
            Assert.Equal(0, player.CurrentSeason.Appearances);
        }
    }

    // =============================================================================================
    // Internal consistency.
    // =============================================================================================

    [Fact]
    public void Simulate_EmitsInternallyConsistentEventsStatsAndLines()
    {
        Fixture fixture = Fixture.Create(tier: 1);
        int totalInjuries = 0;

        for (ulong seed = 1; seed <= 300; seed++)
        {
            QuickSimResult result = WorldQuickSim.Simulate(
                fixture.World, fixture.Home, fixture.Away,
                fixture.HomeLineup, fixture.AwayLineup, new WorldRng(seed));
            totalInjuries += result.Injuries.Count;
            AssertConsistent(result, fixture);
        }

        Assert.True(totalInjuries > 0, "300 matches should produce at least one in-match injury event");
    }

    [Fact]
    public void Simulate_ProducedSeasonsEventsReferencePlayersOnTheField()
    {
        Fixture fixture = Fixture.Create(tier: 1);

        for (ulong seed = 31; seed <= 40; seed++)
        {
            QuickSimResult result = WorldQuickSim.Simulate(
                fixture.World, fixture.Home, fixture.Away,
                fixture.HomeLineup, fixture.AwayLineup, new WorldRng(seed));

            Dictionary<PlayerId, PlayerMatchLine> homeLines =
                result.HomePlayers.ToDictionary(line => line.PlayerId);
            Dictionary<PlayerId, PlayerMatchLine> awayLines =
                result.AwayPlayers.ToDictionary(line => line.PlayerId);

            foreach (GoalEvent goal in result.Goals)
            {
                SelectedLineup lineup = goal.IsHome ? fixture.HomeLineup : fixture.AwayLineup;
                Dictionary<PlayerId, PlayerMatchLine> lines = goal.IsHome ? homeLines : awayLines;
                PlayerMatchLine scorerLine = lines[goal.ScorerId];
                if (lineup.StartingXI.Contains(goal.ScorerId))
                {
                    Assert.True(goal.Minute <= scorerLine.Minutes);
                }
                else
                {
                    Assert.True(goal.Minute >= 90 - scorerLine.Minutes);
                }

                if (goal.AssistId.IsValid)
                {
                    Assert.True(lines.ContainsKey(goal.AssistId));
                    Assert.NotEqual(goal.ScorerId, goal.AssistId);
                    PlayerMatchLine assistLine = lines[goal.AssistId];
                    if (lineup.StartingXI.Contains(goal.AssistId))
                    {
                        Assert.True(goal.Minute <= assistLine.Minutes);
                    }
                    else
                    {
                        Assert.True(goal.Minute >= 90 - assistLine.Minutes);
                    }
                }
            }

            foreach (SubstitutionEvent substitution in result.Substitutions)
            {
                SelectedLineup lineup = substitution.IsHome ? fixture.HomeLineup : fixture.AwayLineup;
                Assert.Contains(substitution.OffPlayerId, lineup.StartingXI);
                Assert.Contains(substitution.OnPlayerId, lineup.Bench);
                Dictionary<PlayerId, PlayerMatchLine> lines = substitution.IsHome ? homeLines : awayLines;
                Assert.Equal(substitution.Minute, lines[substitution.OffPlayerId].Minutes);
            }

            // The keeper cannot score and every goal came from an on-target shot.
            AssertGoalsFitShotStats(result);
        }
    }

    // =============================================================================================
    // Cup shoot-outs.
    // =============================================================================================

    [Fact]
    public void KnockoutDraw_IsResolvedByADecisiveShootout()
    {
        Fixture fixture = Fixture.Create(tier: 1);
        bool sawDraw = false;

        for (ulong seed = 1; seed <= 200; seed++)
        {
            QuickSimResult result = WorldQuickSim.Simulate(
                fixture.World, fixture.Home, fixture.Away,
                fixture.HomeLineup, fixture.AwayLineup, new WorldRng(seed), knockout: true);

            Assert.True(result.WinnerClubId.IsValid, "a knockout tie always has a winner");
            Assert.Contains(result.WinnerClubId, new[] { fixture.Home.Id, fixture.Away.Id });

            if (result.HomeGoals != result.AwayGoals) continue;

            sawDraw = true;
            Assert.True(result.WentToPenalties);
            Assert.NotEqual(result.HomePenaltyGoals, result.AwayPenaltyGoals);
            ClubId expectedWinner = result.HomePenaltyGoals > result.AwayPenaltyGoals
                ? fixture.Home.Id
                : fixture.Away.Id;
            Assert.Equal(expectedWinner, result.WinnerClubId);
            break;
        }

        Assert.True(sawDraw, "200 knockout simulations should contain at least one level 90-minute tie");
    }

    [Fact]
    public void LeagueDraw_StaysADrawAndNeverGoesToPenalties()
    {
        Fixture fixture = Fixture.Create(tier: 1);
        bool sawDraw = false;

        for (ulong seed = 1; seed <= 200; seed++)
        {
            QuickSimResult result = WorldQuickSim.Simulate(
                fixture.World, fixture.Home, fixture.Away,
                fixture.HomeLineup, fixture.AwayLineup, new WorldRng(seed), knockout: false);

            if (result.HomeGoals != result.AwayGoals) continue;

            sawDraw = true;
            Assert.False(result.WentToPenalties);
            Assert.Equal(0, result.HomePenaltyGoals);
            Assert.Equal(0, result.AwayPenaltyGoals);
            Assert.Equal(ClubId.None, result.WinnerClubId);
            break;
        }

        Assert.True(sawDraw, "200 league simulations should contain at least one draw");
    }

    // =============================================================================================
    // Calibration and team ratings.
    // =============================================================================================

    [Fact]
    public void EvenlyMatchedClubs_AverageGoalsInThePlanBand()
    {
        // Identical squads: the only asymmetry is the plan's +4% home advantage.
        Fixture fixture = Fixture.Create(tier: 1, seedBase: 500_000UL);
        TeamRatings homeRatings = WorldQuickSim.EvaluateTeam(fixture.World, fixture.HomeLineup);
        TeamRatings awayRatings = WorldQuickSim.EvaluateTeam(fixture.World, fixture.AwayLineup);
        _output.WriteLine(
            $"even fixture: home A{homeRatings.Attack:0.00}/M{homeRatings.Midfield:0.00}/" +
            $"D{homeRatings.Defence:0.00}/GK{homeRatings.Goalkeeping:0.00}; " +
            $"away A{awayRatings.Attack:0.00}/D{awayRatings.Defence:0.00}/GK{awayRatings.Goalkeeping:0.00}; " +
            $"gap {homeRatings.Attack - (0.72f * awayRatings.Defence + 0.28f * awayRatings.Goalkeeping):0.00}");
        const int Matches = 400;
        int totalGoals = 0;

        for (int match = 0; match < Matches; match++)
        {
            QuickSimResult result = WorldQuickSim.Simulate(
                fixture.World, fixture.Home, fixture.Away,
                fixture.HomeLineup, fixture.AwayLineup, new WorldRng((ulong)(match + 1)));
            totalGoals += result.HomeGoals + result.AwayGoals;
        }

        double average = totalGoals / (double)Matches;
        _output.WriteLine($"even-match mean total goals over {Matches} matches: {average:0.000}");
        Assert.InRange(average, 2.3, 3.1);
    }

    [Fact]
    public void TierOne_BeatsTierSix_Overwhelmingly()
    {
        WorldState world = TestWorlds.Create();
        WorldClub tierOne = TestWorlds.CreateClub(world, clubNumber: 1, tier: 1, seedBase: 600_000UL);
        WorldClub tierSix = TestWorlds.CreateClub(world, clubNumber: 2, tier: 6, seedBase: 700_000UL);
        SelectedLineup tierOneLineup = SquadSelector.Select(world, tierOne);
        SelectedLineup tierSixLineup = SquadSelector.Select(world, tierSix);

        const int Matches = 120;
        int tierOneWins = 0;
        int tierOneGoalDifference = 0;

        for (int match = 0; match < Matches; match++)
        {
            bool tierOneAtHome = match % 2 == 0;
            WorldClub home = tierOneAtHome ? tierOne : tierSix;
            WorldClub away = tierOneAtHome ? tierSix : tierOne;
            SelectedLineup homeLineup = tierOneAtHome ? tierOneLineup : tierSixLineup;
            SelectedLineup awayLineup = tierOneAtHome ? tierSixLineup : tierOneLineup;

            QuickSimResult result = WorldQuickSim.Simulate(
                world, home, away, homeLineup, awayLineup, new WorldRng((ulong)(match + 1)));

            int tierOneGoals = tierOneAtHome ? result.HomeGoals : result.AwayGoals;
            int tierSixGoals = tierOneAtHome ? result.AwayGoals : result.HomeGoals;
            tierOneGoalDifference += tierOneGoals - tierSixGoals;
            if (tierOneGoals > tierSixGoals) tierOneWins++;
        }

        double winRate = tierOneWins / (double)Matches;
        double averageDifference = tierOneGoalDifference / (double)Matches;
        _output.WriteLine(
            $"tier 1 vs tier 6: win rate {winRate:P1}, average goal difference {averageDifference:0.00}");

        Assert.True(winRate > 0.90, $"tier 1 should win more than 90% of head-to-heads, got {winRate:P1}");
        Assert.True(averageDifference > 2.0, $"tier 1 should average more than two goals of margin, got {averageDifference:0.00}");
    }

    [Fact]
    public void EvaluateTeam_ReturnsBoundedRatingsAndRatesAStrongerXiHigher()
    {
        WorldState world = TestWorlds.Create();
        WorldClub strongClub = TestWorlds.CreateClub(world, clubNumber: 1, tier: 1, seedBase: 800_000UL);
        WorldClub weakClub = TestWorlds.CreateClub(world, clubNumber: 2, tier: 6, seedBase: 900_000UL);
        SelectedLineup strongLineup = SquadSelector.Select(world, strongClub);
        SelectedLineup weakLineup = SquadSelector.Select(world, weakClub);

        TeamRatings strong = WorldQuickSim.EvaluateTeam(world, strongLineup);
        TeamRatings weak = WorldQuickSim.EvaluateTeam(world, weakLineup);

        _output.WriteLine($"tier-1 XI: {strong}");
        _output.WriteLine($"tier-6 XI: {weak}");

        foreach (TeamRatings ratings in new[] { strong, weak })
        {
            Assert.InRange(ratings.Attack, 0f, 100f);
            Assert.InRange(ratings.Midfield, 0f, 100f);
            Assert.InRange(ratings.Defence, 0f, 100f);
            Assert.InRange(ratings.Goalkeeping, 0f, 100f);
            Assert.InRange(ratings.Overall, 0f, 100f);
        }

        Assert.True(strong.Attack > weak.Attack);
        Assert.True(strong.Midfield > weak.Midfield);
        Assert.True(strong.Defence > weak.Defence);
        Assert.True(strong.Goalkeeping > weak.Goalkeeping);
        Assert.True(strong.Overall > weak.Overall + 20f);
    }

    // =============================================================================================
    // Performance.
    // =============================================================================================

    [Fact]
    public void FiftyFourSimulations_CompleteWellInsideTheBudget()
    {
        Fixture fixture = Fixture.Create(tier: 1);

        // Warm up JIT and static initialisation before the measured run.
        for (ulong warmup = 1; warmup <= 3; warmup++)
        {
            WorldQuickSim.Simulate(
                fixture.World, fixture.Home, fixture.Away,
                fixture.HomeLineup, fixture.AwayLineup, new WorldRng(warmup));
        }

        var stopwatch = Stopwatch.StartNew();
        for (int match = 0; match < 54; match++)
        {
            WorldQuickSim.Simulate(
                fixture.World, fixture.Home, fixture.Away,
                fixture.HomeLineup, fixture.AwayLineup, new WorldRng((ulong)(match + 1)));
        }

        stopwatch.Stop();
        _output.WriteLine($"54 matches took {stopwatch.Elapsed.TotalMilliseconds:0.00} ms " +
                          $"({stopwatch.Elapsed.TotalMilliseconds / 54.0:0.000} ms/match)");
        Assert.True(
            stopwatch.Elapsed.TotalMilliseconds < 150.0,
            $"54 quick-sim matches took {stopwatch.Elapsed.TotalMilliseconds:0.0} ms (budget 150 ms)");
    }

    // =============================================================================================
    // Consistency helpers.
    // =============================================================================================

    private static void AssertGoalsFitShotStats(QuickSimResult result)
    {
        // Every goal came from an on-target shot and the scoreline agrees with the goal event list.
        Assert.True(result.HomeGoals <= result.HomeStats.ShotsOnTarget);
        Assert.True(result.AwayGoals <= result.AwayStats.ShotsOnTarget);
        Assert.Equal(result.HomeGoals, result.Goals.Count(goal => goal.IsHome));
        Assert.Equal(result.AwayGoals, result.Goals.Count(goal => !goal.IsHome));
    }

    private static void AssertConsistent(QuickSimResult result, Fixture fixture)
    {
        Assert.Equal(100, result.HomeStats.PossessionPercent + result.AwayStats.PossessionPercent);
        Assert.InRange(result.HomeStats.PossessionPercent, 1, 99);
        Assert.True(result.HomeStats.ShotsOnTarget <= result.HomeStats.Shots);
        Assert.True(result.AwayStats.ShotsOnTarget <= result.AwayStats.Shots);
        Assert.True(result.HomeGoals <= result.HomeStats.ShotsOnTarget);
        Assert.True(result.AwayGoals <= result.AwayStats.ShotsOnTarget);
        Assert.Equal(result.HomeGoals, result.Goals.Count(goal => goal.IsHome));
        Assert.Equal(result.AwayGoals, result.Goals.Count(goal => !goal.IsHome));

        Dictionary<PlayerId, PlayerMatchLine> homeLines =
            result.HomePlayers.ToDictionary(line => line.PlayerId);
        Dictionary<PlayerId, PlayerMatchLine> awayLines =
            result.AwayPlayers.ToDictionary(line => line.PlayerId);

        // Every goal/card/substitution/injury names the right player and the right side.
        foreach (GoalEvent goal in result.Goals)
        {
            Assert.InRange(goal.Minute, 1, 90);
            Dictionary<PlayerId, PlayerMatchLine> lines = goal.IsHome ? homeLines : awayLines;
            Assert.True(lines.ContainsKey(goal.ScorerId), $"goal scorer {goal.ScorerId} is not in the match lines");
            if (goal.AssistId.IsValid)
            {
                Assert.True(lines.ContainsKey(goal.AssistId));
                Assert.NotEqual(goal.ScorerId, goal.AssistId);
            }
        }

        foreach (CardEvent card in result.Cards)
        {
            Assert.InRange(card.Minute, 1, 90);
            Dictionary<PlayerId, PlayerMatchLine> lines = card.IsHome ? homeLines : awayLines;
            Assert.True(lines.ContainsKey(card.PlayerId));
        }

        foreach (SubstitutionEvent substitution in result.Substitutions)
        {
            Assert.InRange(substitution.Minute, 60, 82);
            SelectedLineup lineup = substitution.IsHome ? fixture.HomeLineup : fixture.AwayLineup;
            Assert.Contains(substitution.OffPlayerId, lineup.StartingXI);
            Assert.Contains(substitution.OnPlayerId, lineup.Bench);
            Dictionary<PlayerId, PlayerMatchLine> lines = substitution.IsHome ? homeLines : awayLines;
            Assert.True(lines.ContainsKey(substitution.OnPlayerId));
            Assert.Equal(substitution.Minute, lines[substitution.OffPlayerId].Minutes);
        }

        foreach (InjuryEvent injury in result.Injuries)
        {
            SelectedLineup lineup = injury.IsHome ? fixture.HomeLineup : fixture.AwayLineup;
            Assert.Contains(injury.PlayerId, lineup.StartingXI);
            Dictionary<PlayerId, PlayerMatchLine> lines = injury.IsHome ? homeLines : awayLines;
            Assert.InRange(injury.Minute, 1, lines[injury.PlayerId].Minutes);
            Assert.NotEqual(InjuryKind.None, injury.Kind);
            Assert.NotEqual(InjurySeverity.None, injury.Severity);
            (int minWeeks, int maxWeeks) = injury.Severity switch
            {
                InjurySeverity.Knock => (1, 1),
                InjurySeverity.Minor => (1, 3),
                InjurySeverity.Moderate => (3, 8),
                InjurySeverity.Severe => (10, 26),
                _ => (0, 0)
            };
            Assert.InRange(injury.WeeksOut, minWeeks, maxWeeks);
        }

        // Every line is a plausible footballer's match line, and only players who took the field appear.
        foreach (PlayerMatchLine line in result.HomePlayers.Concat(result.AwayPlayers))
        {
            Assert.InRange(line.Minutes, 1, 90);
            Assert.InRange(line.Rating, WorldQuickSim.MinimumRating, WorldQuickSim.MaximumRating);
            Assert.True(line.PassesCompleted <= line.PassesAttempted);
            Assert.True(line.PassesAttempted > 0);
            Assert.True(line.ShotsOnTarget <= line.Shots);
            Assert.True(line.Goals <= line.ShotsOnTarget);
            Assert.True(line.Goals >= 0 && line.Assists >= 0 && line.Tackles >= 0 && line.Saves >= 0);
            Assert.True(line.YellowCards >= 0 && line.RedCards >= 0);
            Assert.InRange(line.EndingStamina01, 0.02f, 1f);
            Assert.InRange(line.ConditionCost01, 0f, 1f);
        }

        int homeSubsOn = result.Substitutions.Count(substitution => substitution.IsHome);
        int awaySubsOn = result.Substitutions.Count(substitution => !substitution.IsHome);
        Assert.Equal(11 + homeSubsOn, result.HomePlayers.Count);
        Assert.Equal(11 + awaySubsOn, result.AwayPlayers.Count);
        Assert.Equal(11 * 90, result.HomePlayers.Sum(line => line.Minutes));
        Assert.Equal(11 * 90, result.AwayPlayers.Sum(line => line.Minutes));

        // Card counters on the line agree with the event list.
        foreach (PlayerMatchLine line in result.HomePlayers)
        {
            Assert.Equal(
                line.YellowCards,
                result.Cards.Count(card => card.IsHome && !card.IsRed && card.PlayerId == line.PlayerId));
            Assert.Equal(
                line.RedCards,
                result.Cards.Count(card => card.IsHome && card.IsRed && card.PlayerId == line.PlayerId));
        }

        foreach (PlayerMatchLine line in result.AwayPlayers)
        {
            Assert.Equal(
                line.YellowCards,
                result.Cards.Count(card => !card.IsHome && !card.IsRed && card.PlayerId == line.PlayerId));
            Assert.Equal(
                line.RedCards,
                result.Cards.Count(card => !card.IsHome && card.IsRed && card.PlayerId == line.PlayerId));
        }

        // A bench player who never came on has no match line.
        HashSet<PlayerId> substitutesUsed = result.Substitutions
            .Select(substitution => substitution.OnPlayerId)
            .ToHashSet();
        PlayerId unusedSubstitute = fixture.AwayLineup.Bench.First(id => !substitutesUsed.Contains(id));
        Assert.Null(result.LineOf(unusedSubstitute));
        Assert.NotNull(result.LineOf(fixture.HomeLineup.StartingXI[0]));
    }

    private static void AssertSameResult(QuickSimResult expected, QuickSimResult actual)
    {
        Assert.Equal(expected.HomeClubId, actual.HomeClubId);
        Assert.Equal(expected.AwayClubId, actual.AwayClubId);
        Assert.Equal(expected.HomeGoals, actual.HomeGoals);
        Assert.Equal(expected.AwayGoals, actual.AwayGoals);
        Assert.Equal(expected.WentToPenalties, actual.WentToPenalties);
        Assert.Equal(expected.HomePenaltyGoals, actual.HomePenaltyGoals);
        Assert.Equal(expected.AwayPenaltyGoals, actual.AwayPenaltyGoals);
        Assert.Equal(expected.Goals, actual.Goals);
        Assert.Equal(expected.Cards, actual.Cards);
        Assert.Equal(expected.Injuries, actual.Injuries);
        Assert.Equal(expected.Substitutions, actual.Substitutions);
        Assert.Equal(expected.HomePlayers, actual.HomePlayers);
        Assert.Equal(expected.AwayPlayers, actual.AwayPlayers);
        Assert.Equal(expected.HomeStats, actual.HomeStats);
        Assert.Equal(expected.AwayStats, actual.AwayStats);
        Assert.Equal(expected.WinnerClubId, actual.WinnerClubId);
    }

    private static string Signature(QuickSimResult result) =>
        $"{result.HomeGoals}:{result.AwayGoals}|" +
        string.Join(",", result.Goals.Select(goal => $"{goal.Minute}-{goal.ScorerId.Value}")) + "|" +
        string.Join(",", result.HomePlayers.Select(line => $"{line.PlayerId.Value}={line.Rating:0.0000}")) + "|" +
        string.Join(",", result.AwayPlayers.Select(line => $"{line.PlayerId.Value}={line.Rating:0.0000}")) + "|" +
        string.Join(",", result.Cards.Select(card => $"{card.Minute}-{card.PlayerId.Value}-{card.IsRed}"));

    // =============================================================================================
    // Fixture and world builders.
    // =============================================================================================

    private sealed record Fixture(
        WorldState World,
        WorldClub Home,
        WorldClub Away,
        SelectedLineup HomeLineup,
        SelectedLineup AwayLineup)
    {
        /// <summary>
        /// Two clubs in the same division. Passing the same <paramref name="seedBase"/> builds the
        /// identical squad twice (the per-player generation seed does not depend on the club id), which is
        /// the strongest possible "evenly matched" pairing for the goal-average calibration.
        /// </summary>
        internal static Fixture Create(int tier, ulong seedBase = 500_000UL)
        {
            WorldState world = TestWorlds.Create();
            WorldClub home = TestWorlds.CreateClub(world, clubNumber: 1, tier, seedBase);
            WorldClub away = TestWorlds.CreateClub(world, clubNumber: 2, tier, seedBase);
            return new Fixture(
                world,
                home,
                away,
                SquadSelector.Select(world, home),
                SquadSelector.Select(world, away));
        }
    }

    /// <summary>Hand-built world: the W0 pack plus generated players and managers; no W2 generators involved.</summary>
    private static class TestWorlds
    {
        private static readonly TacticalRole[] RoleMix =
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

        internal static WorldState Create()
        {
            WorldPack pack = WorldPackLoader.Parse(TestWorldPacks.ValidJson());
            return new WorldState(pack, seed: 20_261_127UL, WorldDate.Start);
        }

        internal static WorldClub CreateClub(WorldState world, int clubNumber, int tier, ulong seedBase)
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
                FormationKind.FourFourTwo,
                reputation: 60));
            club.ManagerId = managerId;
            world.Clubs.Add(club);

            for (int i = 0; i < PlayersPerSquad; i++)
            {
                TacticalRole role = RoleMix[i % RoleMix.Length];
                ulong playerSeed = seedBase + (ulong)(i * 37 + 1);
                WorldPlayer player = PlayerGenerator.GenerateNpcForTier(
                    playerSeed,
                    tier,
                    age: 24 + i % 5,
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
