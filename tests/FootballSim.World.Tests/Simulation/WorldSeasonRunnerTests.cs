using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// Stage W2 gate: <see cref="WorldSeasonRunner"/> plays the real generated world through a full competitive
/// season - 1,836 league matches, the 107-tie domestic cup, the weekly growth tick, the five
/// promotion/relegation play-offs and the rollover - deterministically and inside the performance budget.
///
/// <para>The expensive world is generated once (a few tens of milliseconds) and its season is played once
/// (~0.6 s); the summary and every applied effect are asserted against that single run. Determinism re-plays
/// a second identical world and a different-seed world, and the performance test generates and plays a fresh
/// world with a stopwatch.</para>
/// </summary>
public sealed class WorldSeasonRunnerTests
{
    private const ulong DefaultSeed = 20261127UL;

    /// <summary>Six divisions x 306 double-round-robin fixtures.</summary>
    private const int LeagueMatchesPerSeason = 1836;

    /// <summary>Eight cup rounds: 18 + 26 + 32 + 16 + 8 + 4 + 2 + 1.</summary>
    private const int CupTiesPerSeason = 107;

    private const int MatchdaysPerSeason = 34;
    private const int FixturesPerMatchday = 9;
    private const int ClubsPerLeague = 18;
    private const int ClubsInWorld = 108;

    private static readonly Lazy<WorldPack> FullPack =
        new(() => WorldPackLoader.Load(WorldPackPaths.ResolveDefaultPack()));

    private static readonly Lazy<SeasonPlaythrough> Season =
        new(() => SeasonPlaythrough.Play(DefaultSeed));

    private static readonly Lazy<SeasonPlaythrough> SeasonAgain =
        new(() => SeasonPlaythrough.Play(DefaultSeed));

    private static readonly Lazy<SeasonPlaythrough> OtherSeedSeason =
        new(() => SeasonPlaythrough.Play(DefaultSeed + 1UL));

    private readonly ITestOutputHelper _output;

    public WorldSeasonRunnerTests(ITestOutputHelper output) => _output = output;

    private static WorldPack Pack => FullPack.Value;
    private static SeasonPlaythrough Run => Season.Value;
    private static WorldState World => Run.World;
    private static SeasonSummary Summary => Run.Summary;

    // =============================================================================================
    // 1. Full-season completeness.
    // =============================================================================================

    [Fact]
    public void PlaySeason_CompletesTheWholeSeasonAndProducesRealisticScorelines()
    {
        WorldState world = World;
        SeasonSummary summary = Summary;

        Assert.Equal(1, summary.Season);
        Assert.Equal(LeagueMatchesPerSeason, summary.LeagueMatchesPlayed);

        // Every league fixture has a result, and every division held its 34 matchdays of 9 fixtures.
        int playedLeagueFixtures = 0;
        foreach (WorldLeague league in world.Leagues)
        {
            var fixturesPerMatchday = new int[MatchdaysPerSeason + 1];
            foreach (WorldFixture fixture in league.Fixtures)
            {
                Assert.True(fixture.IsPlayed, $"fixture {fixture} was never played");
                Assert.InRange(fixture.LeagueMatchday, 1, MatchdaysPerSeason);
                fixturesPerMatchday[fixture.LeagueMatchday]++;
                playedLeagueFixtures++;
            }

            for (int matchday = 1; matchday <= MatchdaysPerSeason; matchday++)
            {
                Assert.Equal(FixturesPerMatchday, fixturesPerMatchday[matchday]);
            }
        }

        Assert.Equal(LeagueMatchesPerSeason, playedLeagueFixtures);

        // Every club played its full 34-match season.
        Assert.Equal(6, summary.TablesByTier.Count);
        foreach (IReadOnlyList<LeagueTableRow> table in summary.TablesByTier)
        {
            Assert.Equal(ClubsPerLeague, table.Count);
            Assert.All(table, row => Assert.Equal(MatchdaysPerSeason, row.Played));
        }

        // The cup ran its full eight rounds and crowned a champion from the final's winner.
        Assert.True(world.Cup.IsComplete);
        Assert.True(world.Cup.ChampionClubId.IsValid);
        Assert.Equal(CupTiesPerSeason, summary.CupTiesPlayed);
        Assert.Equal(8, world.Cup.Rounds.Count);

        int cupTiesPlayed = 0;
        foreach (CupRoundBracket round in world.Cup.Rounds)
        {
            Assert.True(round.IsResolved, $"cup round {round.Round} is not resolved");
            foreach (CupTie tie in round.Ties)
            {
                Assert.True(tie.Fixture.IsPlayed);
                cupTiesPlayed++;
            }
        }

        Assert.Equal(CupTiesPerSeason, cupTiesPlayed);
        CupTie final = world.Cup.RoundOf(CupRound.Final)!.Ties[0];
        Assert.Equal(world.Cup.ChampionClubId, final.WinnerClubId);

        // Scoring realism (plan W2 gate): a real 1,836-match generated season must average 2.3..3.1
        // goals per game. The quick-sim's expected-goals base/exponent are calibrated on this real world
        // (see WorldQuickSim's calibration comment); the same band is asserted by the evenly-matched
        // quick-sim calibration fixture.
        float leagueGoalsPerMatch = summary.LeagueGoals / (float)summary.LeagueMatchesPlayed;
        _output.WriteLine(
            $"season {summary.Season}: {summary.LeagueMatchesPlayed} league matches, " +
            $"{summary.LeagueGoals} league goals, goals/match " +
            $"{leagueGoalsPerMatch:0.000} (summary {summary.GoalsPerMatch:0.000}), " +
            $"{summary.CupTiesPlayed} cup ties, cup champion {world.Clubs[summary.CupChampionClubId].Name}");
        Assert.InRange(leagueGoalsPerMatch, 2.3f, 3.1f);
        Assert.InRange(summary.GoalsPerMatch, 2.3f, 3.1f);
        Assert.Equal(leagueGoalsPerMatch, summary.GoalsPerMatch, 3);
    }

    // =============================================================================================
    // 2. Standings integrity.
    // =============================================================================================

    [Fact]
    public void PlaySeason_ProducesInternallyConsistentLeagueTables()
    {
        WorldState world = World;
        SeasonSummary summary = Summary;

        Assert.Equal(6, summary.TablesByTier.Count);
        Assert.Equal(6, summary.ChampionsByTier.Count);

        for (int tier = 1; tier <= 6; tier++)
        {
            IReadOnlyList<LeagueTableRow> table = summary.TablesByTier[tier - 1];
            Assert.Equal(ClubsPerLeague, table.Count);
            Assert.Equal(table[0].ClubId, summary.ChampionsByTier[tier - 1]);
            Assert.Equal(world.LeagueForTier(tier).Clubs.Count, table.Count);

            int goalsFor = 0;
            int goalsAgainst = 0;
            for (int position = 0; position < table.Count; position++)
            {
                LeagueTableRow row = table[position];
                Assert.Equal(MatchdaysPerSeason, row.Played);
                Assert.Equal(row.Won + row.Drawn + row.Lost, row.Played);
                Assert.Equal(3 * row.Won + row.Drawn, row.Points);
                Assert.Equal(row.GoalsFor - row.GoalsAgainst, row.GoalDifference);

                goalsFor += row.GoalsFor;
                goalsAgainst += row.GoalsAgainst;
                AssertTableOrdering(table, position);
            }

            // Every goal scored in the division was conceded in it.
            Assert.Equal(goalsFor, goalsAgainst);

            // 306 matches x (2 or 3 points) bounds the division's total points.
            int totalPoints = 0;
            foreach (LeagueTableRow row in table) totalPoints += row.Points;
            Assert.InRange(totalPoints, 306 * 2, 306 * 3);
        }

        // The season ends in the off-season: week 46 of the same season.
        Assert.Equal(1, world.Season);
        Assert.Equal(WorldCalendar.OffSeasonFirstWeek, world.Week);
        Assert.Equal(new WorldDate(1, WorldCalendar.OffSeasonFirstWeek), world.Date);
    }

    // =============================================================================================
    // 3. Promotion / relegation.
    // =============================================================================================

    [Fact]
    public void PlaySeason_AppliesExactlyTenAutomaticMovesEachWayAndTheFivePlayoffs()
    {
        WorldState world = World;
        SeasonSummary summary = Summary;

        Assert.Equal(5, summary.PlayoffWinnerWasHigherDivisionClub.Count);
        Assert.Equal(Enumerable.Range(1, 5), summary.PlayoffWinnerWasHigherDivisionClub.Keys.OrderBy(tier => tier));

        int automaticPromotions = 0;
        int automaticRelegations = 0;
        int playoffMoves = 0;
        foreach (DivisionMove move in summary.DivisionMoves)
        {
            Assert.Equal(1, Math.Abs(move.ToTier - move.FromTier));
            Assert.InRange(move.FromTier, 1, 6);
            Assert.InRange(move.ToTier, 1, 6);

            if (move.ViaPlayoff) playoffMoves++;
            else if (move.ToTier < move.FromTier) automaticPromotions++;
            else automaticRelegations++;
        }

        Assert.Equal(10, automaticPromotions);
        Assert.Equal(10, automaticRelegations);

        // A play-off is only a move when the lower-division club wins it: the two clubs then swap.
        int boundariesWonByTheLowerClub = summary.PlayoffWinnerWasHigherDivisionClub.Count(pair => !pair.Value);
        Assert.Equal(2 * boundariesWonByTheLowerClub, playoffMoves);

        // Auto moves match the final tables exactly: bottom two down, top two up.
        for (int tier = 1; tier <= 5; tier++)
        {
            IReadOnlyList<LeagueTableRow> table = summary.TablesByTier[tier - 1];
            AssertAutomaticMove(summary, table[16].ClubId, tier, tier + 1);
            AssertAutomaticMove(summary, table[17].ClubId, tier, tier + 1);
        }

        for (int tier = 2; tier <= 6; tier++)
        {
            IReadOnlyList<LeagueTableRow> table = summary.TablesByTier[tier - 1];
            AssertAutomaticMove(summary, table[0].ClubId, tier, tier - 1);
            AssertAutomaticMove(summary, table[1].ClubId, tier, tier - 1);
        }

        // The play-off pairings are 16th of the higher division against 3rd of the lower one; the winner
        // plays in the higher division next season.
        for (int boundaryTier = 1; boundaryTier <= 5; boundaryTier++)
        {
            ClubId higherClub = summary.TablesByTier[boundaryTier - 1][15].ClubId;
            ClubId lowerClub = summary.TablesByTier[boundaryTier][2].ClubId;
            bool higherDivisionClubWon = summary.PlayoffWinnerWasHigherDivisionClub[boundaryTier];

            bool higherClubMoved = summary.DivisionMoves.Any(move => move.ViaPlayoff && move.ClubId == higherClub);
            bool lowerClubMoved = summary.DivisionMoves.Any(move => move.ViaPlayoff && move.ClubId == lowerClub);
            Assert.Equal(!higherDivisionClubWon, higherClubMoved);
            Assert.Equal(!higherDivisionClubWon, lowerClubMoved);

            int expectedHigherTier = higherDivisionClubWon ? boundaryTier : boundaryTier + 1;
            int expectedLowerTier = higherDivisionClubWon ? boundaryTier + 1 : boundaryTier;
            Assert.Equal(expectedHigherTier, world.Clubs[higherClub].Tier);
            Assert.Equal(expectedLowerTier, world.Clubs[lowerClub].Tier);
        }

        // After the rollover every division has exactly 18 clubs and every club is registered where it
        // says it plays.
        var registered = new HashSet<ClubId>();
        foreach (WorldLeague league in world.Leagues)
        {
            Assert.Equal(ClubsPerLeague, league.Clubs.Count);
            foreach (ClubId clubId in league.Clubs)
            {
                WorldClub club = world.Clubs[clubId];
                Assert.Equal(league.Tier, club.Tier);
                Assert.Equal(league.Id, club.LeagueId);
                Assert.True(registered.Add(clubId), $"club {clubId} is registered in two divisions");
            }
        }

        Assert.Equal(ClubsInWorld, registered.Count);
        for (int tier = 1; tier <= 6; tier++)
        {
            Assert.Equal(ClubsPerLeague, world.Clubs.InTier(tier).Count);
        }

        string champions = string.Join(", ", summary.ChampionsByTier.Select(id => world.Clubs[id].Name));
        string playoffDecisions = string.Join(", ", Enumerable.Range(1, 5).Select(tier =>
            $"T{tier}:{(summary.PlayoffWinnerWasHigherDivisionClub[tier] ? "higher" : "lower")}"));
        _output.WriteLine(
            $"rollover: {automaticPromotions} automatic promotions, {automaticRelegations} automatic " +
            $"relegations, {playoffMoves} play-off moves ({boundariesWonByTheLowerClub}/5 boundaries won by " +
            $"the lower club: {playoffDecisions}); champions {champions}");
    }

    // =============================================================================================
    // 4. Match effects on players.
    // =============================================================================================

    [Fact]
    public void PlaySeason_AppliesMatchEffectsAndGrowthToEveryPlayer()
    {
        WorldState world = World;
        SeasonSummary summary = Summary;
        SeasonPlaythrough run = Run;

        Assert.True(summary.TopScorerId.IsValid, "the season produced no top scorer");
        Assert.True(summary.TopScorerGoals > 0);

        int maximumGoals = 0;
        foreach (WorldPlayer player in world.Players.All)
        {
            int goals = player.CurrentSeason.Goals;
            if (goals > maximumGoals) maximumGoals = goals;
        }

        Assert.Equal(maximumGoals, summary.TopScorerGoals);
        Assert.Equal(maximumGoals, world.Players[summary.TopScorerId].CurrentSeason.Goals);

        int playersWithAppearances = 0;
        int totalGoals = 0;
        int totalAssists = 0;
        int yellowCards = 0;
        int redCards = 0;
        int underTwentyFour = 0;
        int grownPlayers = 0;

        foreach (WorldPlayer player in world.Players.All)
        {
            PlayerSeasonStats stats = player.CurrentSeason;

            // No negative production, and the season stamp is the season that was played.
            Assert.Equal(summary.Season, stats.Season);
            Assert.True(stats.Appearances >= 0);
            Assert.True(stats.MinutesPlayed >= 0);
            Assert.True(stats.Goals >= 0);
            Assert.True(stats.Assists >= 0);
            Assert.True(stats.Shots >= 0);
            Assert.True(stats.Tackles >= 0);
            Assert.True(stats.Saves >= 0);
            Assert.True(stats.YellowCards >= 0);
            Assert.True(stats.RedCards >= 0);

            // Every appearance is a start or a substitute appearance, and stats are never ahead of it.
            Assert.Equal(stats.Appearances, stats.Starts + stats.SubstituteAppearances);
            Assert.True(stats.Starts <= stats.Appearances);
            Assert.True(stats.SubstituteAppearances <= stats.Appearances);

            if (stats.Appearances > 0)
            {
                playersWithAppearances++;
                Assert.True(stats.MinutesPlayed > 0);
                Assert.True(player.Form.Appearances.Count > 0, $"player {player.Id} has no form history");
                Assert.True(
                    MathF.Abs(stats.RatingSum - stats.Appearances * stats.AverageRating) <= 1e-2f,
                    $"player {player.Id} rating sum {stats.RatingSum} disagrees with mean");
            }

            // Card totals agree with the per-player counters.
            totalGoals += stats.Goals;
            totalAssists += stats.Assists;
            yellowCards += stats.YellowCards;
            redCards += stats.RedCards;

            // Top scorer tie-break: nobody with the same goal count has a lower player id.
            if (stats.Goals == maximumGoals && maximumGoals > 0)
            {
                Assert.True(player.Id.Value >= summary.TopScorerId.Value);
            }

            // Growth: every attribute stays inside its hard range (NaN would fail IsValid).
            Assert.True(player.Attributes.IsValid(), $"player {player.Id} has invalid attributes");

            if (player.Age < 24)
            {
                underTwentyFour++;
                if (player.Attributes.MeanCurrent() != run.SeasonStartMeanCurrent[player.Id]) grownPlayers++;
            }
        }

        Assert.True(playersWithAppearances > 1000, $"only {playersWithAppearances} players appeared");
        Assert.True(totalGoals > 0);
        Assert.True(totalAssists > 0);
        Assert.True(summary.YellowCardsThisSeason > 0, "a whole season produced no yellow cards");
        Assert.True(summary.RedCardsThisSeason >= 0);
        Assert.Equal(summary.YellowCardsThisSeason, yellowCards);
        Assert.Equal(summary.RedCardsThisSeason, redCards);
        Assert.True(summary.InjuriesThisSeason > 0, "a whole season produced no injuries");

        // The growth tick visibly developed the squad: nearly every under-24 player has moved.
        Assert.True(underTwentyFour > 0);
        double grownShare = grownPlayers / (double)underTwentyFour;
        _output.WriteLine(
            $"match effects: {playersWithAppearances} players appeared, {totalGoals} goals, " +
            $"{totalAssists} assists, {summary.YellowCardsThisSeason} yellow / " +
            $"{summary.RedCardsThisSeason} red cards, {summary.InjuriesThisSeason} injuries, " +
            $"top scorer {world.Players[summary.TopScorerId].DisplayName} ({summary.TopScorerGoals}); " +
            $"{grownPlayers}/{underTwentyFour} under-24 players grew ({grownShare:P1})");
        Assert.True(grownShare >= 0.90, $"only {grownShare:P1} of under-24 players changed attributes");
    }

    // =============================================================================================
    // 5. Determinism.
    // =============================================================================================

    [Fact]
    public void PlaySeason_IsDeterministicForTheSameSeed()
    {
        SeasonPlaythrough expected = Run;
        SeasonPlaythrough actual = SeasonAgain.Value;

        Assert.Equal(expected.Summary.CupChampionClubId, actual.Summary.CupChampionClubId);
        Assert.Equal(expected.Summary.ChampionsByTier, actual.Summary.ChampionsByTier);
        Assert.Equal(expected.Summary.TopScorerId, actual.Summary.TopScorerId);
        Assert.Equal(expected.Summary.TopScorerGoals, actual.Summary.TopScorerGoals);
        Assert.Equal(expected.Summary.LeagueGoals, actual.Summary.LeagueGoals);
        Assert.Equal(expected.Summary.LeagueMatchesPlayed, actual.Summary.LeagueMatchesPlayed);
        Assert.Equal(expected.Summary.CupTiesPlayed, actual.Summary.CupTiesPlayed);
        Assert.Equal(expected.Summary.InjuriesThisSeason, actual.Summary.InjuriesThisSeason);
        Assert.Equal(expected.Summary.YellowCardsThisSeason, actual.Summary.YellowCardsThisSeason);
        Assert.Equal(expected.Summary.RedCardsThisSeason, actual.Summary.RedCardsThisSeason);
        AssertSamePlayoffDecisions(expected.Summary, actual.Summary);
        AssertSameDivisionMoves(expected.Summary, actual.Summary);
        AssertSameTables(expected.Summary, actual.Summary);
        AssertSameSeasonFixtures(expected.World, actual.World);
        AssertSameCup(expected.World, actual.World);

        // A sample of player state is bit-identical too (attributes and season production).
        for (int index = 0; index < expected.World.Players.Count; index += 97)
        {
            WorldPlayer left = expected.World.Players.All[index];
            WorldPlayer right = actual.World.Players.All[index];

            Assert.Equal(left.Id, right.Id);
            Assert.Equal(left.Age, right.Age);
            Assert.Equal(left.CurrentSeason.Appearances, right.CurrentSeason.Appearances);
            Assert.Equal(left.CurrentSeason.Goals, right.CurrentSeason.Goals);
            Assert.Equal(left.CurrentSeason.RatingSum, right.CurrentSeason.RatingSum);
            Assert.Equal(left.Condition01, right.Condition01);
            Assert.Equal(left.Form.Appearances.Count, right.Form.Appearances.Count);
            for (int attribute = 0; attribute < AttributeMetadata.Count; attribute++)
            {
                AttributeId id = (AttributeId)attribute;
                Assert.Equal(
                    BitConverter.SingleToInt32Bits(left.Attributes.Current(id)),
                    BitConverter.SingleToInt32Bits(right.Attributes.Current(id)));
            }
        }

        _output.WriteLine(
            $"determinism: identical cup champion {expected.World.Clubs[expected.Summary.CupChampionClubId].Name}, " +
            $"champions, 1,836 scorelines, 6x18 table rows, top scorer " +
            $"{expected.World.Players[expected.Summary.TopScorerId].DisplayName} and " +
            $"{expected.Summary.DivisionMoves.Count} division moves");
    }

    [Fact]
    public void PlaySeason_DiffersForADifferentWorldSeed()
    {
        SeasonPlaythrough expected = Run;
        SeasonPlaythrough actual = OtherSeedSeason.Value;

        bool differs =
            expected.Summary.CupChampionClubId != actual.Summary.CupChampionClubId ||
            !expected.Summary.ChampionsByTier.SequenceEqual(actual.Summary.ChampionsByTier) ||
            expected.Summary.TopScorerId != actual.Summary.TopScorerId ||
            expected.Summary.LeagueGoals != actual.Summary.LeagueGoals ||
            !expected.Summary.DivisionMoves.SequenceEqual(actual.Summary.DivisionMoves);

        // Also require a scoreline to differ: the two seasons must not be the same simulated match list.
        for (int leagueIndex = 0; leagueIndex < expected.World.Leagues.Count && !differs; leagueIndex++)
        {
            IReadOnlyList<WorldFixture> leftFixtures = expected.World.Leagues[leagueIndex].Fixtures;
            IReadOnlyList<WorldFixture> rightFixtures = actual.World.Leagues[leagueIndex].Fixtures;
            for (int fixtureIndex = 0; fixtureIndex < leftFixtures.Count && !differs; fixtureIndex++)
            {
                WorldFixture left = leftFixtures[fixtureIndex];
                WorldFixture right = rightFixtures[fixtureIndex];
                differs = left.HomeClubId != right.HomeClubId ||
                    left.HomeGoals != right.HomeGoals ||
                    left.AwayGoals != right.AwayGoals;
            }
        }

        Assert.True(differs, "a different world seed produced the identical season");
    }

    // =============================================================================================
    // 6. Performance.
    // =============================================================================================

    [Fact]
    public void GenerateAndPlaySeason_CompletesWellInsideThePerformanceBudget()
    {
        var stopwatch = Stopwatch.StartNew();
        WorldState world = WorldGenerator.Generate(Pack, DefaultSeed);
        double generationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        SeasonSummary summary = new WorldSeasonRunner(world).PlaySeason();
        stopwatch.Stop();
        double totalMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        Assert.Equal(LeagueMatchesPerSeason, summary.LeagueMatchesPlayed);
        Assert.Equal(CupTiesPerSeason, summary.CupTiesPlayed);
        Assert.True(world.Cup.IsComplete);

        _output.WriteLine(
            $"generate + full season: {totalMilliseconds:0.0} ms " +
            $"(generation {generationMilliseconds:0.0} ms, season " +
            $"{(totalMilliseconds - generationMilliseconds):0.0} ms; " +
            $"{(totalMilliseconds - generationMilliseconds) / LeagueMatchesPerSeason:0.000} ms/match; " +
            $"goals/match {summary.GoalsPerMatch:0.000}; budget 3000 ms)");
        Assert.True(
            totalMilliseconds < 3000.0,
            $"generate + full season took {totalMilliseconds:0.0} ms (budget 3000 ms)");
    }

    // =============================================================================================
    // 7. Week-level unit checks.
    // =============================================================================================

    [Fact]
    public void AdvanceWeek_PreSeasonWeeksPlayNothingAndAdvanceTheCalendar()
    {
        WorldState world = WorldGenerator.Generate(Pack, DefaultSeed);
        var runner = new WorldSeasonRunner(world);

        for (int week = 1; week <= 4; week++)
        {
            Assert.Equal(week, world.Week);
            WorldDate dateBefore = world.Date;

            WeekReport report = runner.AdvanceWeek();

            Assert.Equal(dateBefore, report.Date);
            Assert.Equal(0, report.LeagueMatchesPlayed);
            Assert.Equal(0, report.CupTiesPlayed);
            Assert.Equal(0, report.GoalsScored);
            Assert.Empty(report.CupRoundsCompleted);
            Assert.Equal(new WorldDate(1, week + 1), world.Date);
        }

        // No fixture has been played in pre-season and the cup still has only its drawn preliminary round.
        Assert.All(world.CurrentSeasonFixtures(), fixture => Assert.False(fixture.IsPlayed));
        Assert.Single(world.Cup.Rounds);
        Assert.False(world.Cup.IsComplete);
        Assert.Equal(0, world.Players.All.Sum(player => player.CurrentSeason.Appearances));
    }

    [Fact]
    public void AdvanceWeek_WeekFivePlaysTheFirstLeagueMatchdayAndThePreliminaryCupRound()
    {
        WorldState world = WorldGenerator.Generate(Pack, DefaultSeed);
        var runner = new WorldSeasonRunner(world);
        for (int week = 1; week <= 4; week++) runner.AdvanceWeek();

        WeekReport report = runner.AdvanceWeek();

        Assert.Equal(new WorldDate(1, 5), report.Date);
        Assert.Equal(54, report.LeagueMatchesPlayed);
        Assert.Equal(18, report.CupTiesPlayed);
        Assert.Single(report.CupRoundsCompleted);
        Assert.Equal(CupRound.Preliminary, report.CupRoundsCompleted[0]);

        // The report agrees exactly with the fixtures that now carry a result in week 5.
        (int leaguePlayed, int cupPlayed, int goals) = CountPlayedFixtures(world, week: 5);
        Assert.Equal(report.LeagueMatchesPlayed, leaguePlayed);
        Assert.Equal(report.CupTiesPlayed, cupPlayed);
        Assert.Equal(report.GoalsScored, goals);
        Assert.Equal(72, leaguePlayed + cupPlayed);

        // The preliminary round is resolved and the cup scheduler drew the second round for week 10.
        Assert.True(world.Cup.RoundOf(CupRound.Preliminary)!.IsResolved);
        CupRoundBracket secondRound = world.Cup.RoundOf(CupRound.SecondRound)!;
        Assert.Equal(CupRound.SecondRound, secondRound.Round);
        Assert.Equal(10, secondRound.Week);
        Assert.Equal(26, secondRound.Ties.Count);
        Assert.Equal(2, secondRound.Byes.Count);
        Assert.False(world.Cup.IsComplete);
        Assert.Equal(new WorldDate(1, 6), world.Date);

        // The next league week is an ordinary matchday 2: no cup tie is due, so no cup round is completed
        // and no further round is drawn while the second round waits for week 10.
        WeekReport nextLeagueWeek = runner.AdvanceWeek();
        Assert.Equal(new WorldDate(1, 6), nextLeagueWeek.Date);
        Assert.Equal(54, nextLeagueWeek.LeagueMatchesPlayed);
        Assert.Equal(0, nextLeagueWeek.CupTiesPlayed);
        Assert.Empty(nextLeagueWeek.CupRoundsCompleted);
        Assert.Equal(2, world.Cup.Rounds.Count);
    }

    [Fact]
    public void AdvanceWeek_WeekTenPlaysTheSecondCupRoundAndCreatesTheRoundOf64()
    {
        WorldState world = WorldGenerator.Generate(Pack, DefaultSeed);
        var runner = new WorldSeasonRunner(world);
        for (int week = 1; week <= 9; week++) runner.AdvanceWeek();

        WeekReport report = runner.AdvanceWeek();

        Assert.Equal(new WorldDate(1, 10), report.Date);
        Assert.Equal(54, report.LeagueMatchesPlayed);
        Assert.Equal(26, report.CupTiesPlayed);
        Assert.Single(report.CupRoundsCompleted);
        Assert.Equal(CupRound.SecondRound, report.CupRoundsCompleted[0]);

        Assert.True(world.Cup.RoundOf(CupRound.SecondRound)!.IsResolved);
        CupRoundBracket roundOf64 = world.Cup.RoundOf(CupRound.RoundOf64)!;
        Assert.Equal(15, roundOf64.Week);
        Assert.Equal(32, roundOf64.Ties.Count);
        Assert.Empty(roundOf64.Byes);
        Assert.False(world.Cup.IsComplete);
    }

    [Fact]
    public void AdvanceWeek_RedCardSuspendsThePlayerAndTheSelectorSkipsHim()
    {
        WorldState world = WorldGenerator.Generate(Pack, DefaultSeed);
        var runner = new WorldSeasonRunner(world);

        // Weeks 1-4 are pre-season; cards can only arrive once competitive matches start.
        for (int week = 1; week <= 4; week++) runner.AdvanceWeek();

        WorldPlayer? suspended = null;
        for (int attempt = 0; attempt < 10 && suspended is null; attempt++)
        {
            runner.AdvanceWeek();
            foreach (WorldPlayer player in world.Players.All)
            {
                if (player.CurrentSeason.RedCards > 0 && player.SuspensionWeeksRemaining > 0)
                {
                    suspended = player;
                    break;
                }
            }
        }

        Assert.NotNull(suspended);
        Assert.False(SquadSelector.IsAvailable(suspended!));

        // The manager's selector must not pick a suspended player for the next match.
        WorldClub club = world.Clubs[suspended!.ClubId];
        SelectedLineup lineup = SquadSelector.Select(world, club);
        Assert.DoesNotContain(suspended.Id, lineup.StartingXI);
        Assert.DoesNotContain(suspended.Id, lineup.Bench);

        // The weekly tick serves the ban; once it reaches zero the player is available again.
        int weeksServed = 0;
        while (suspended.SuspensionWeeksRemaining > 0 && weeksServed < 5)
        {
            runner.AdvanceWeek();
            weeksServed++;
        }

        Assert.Equal(0, suspended.SuspensionWeeksRemaining);
        Assert.Equal(2, weeksServed);
        Assert.True(SquadSelector.IsAvailable(suspended));
    }

    // =============================================================================================
    // 8. Guards.
    // =============================================================================================

    [Fact]
    public void PlaySeason_RejectsAWorldThatIsNotAtWeekOne()
    {
        WorldState midSeason = WorldGenerator.Generate(
            Pack, new WorldGenerationOptions { Seed = DefaultSeed, StartingWeek = 5 });
        var runner = new WorldSeasonRunner(midSeason);
        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => runner.PlaySeason());
        Assert.Contains("week", exception.Message, StringComparison.OrdinalIgnoreCase);

        // A world started at week 1 but already advanced is rejected too.
        WorldState advanced = WorldGenerator.Generate(Pack, DefaultSeed);
        var advancedRunner = new WorldSeasonRunner(advanced);
        advancedRunner.AdvanceWeek();
        Assert.Throws<InvalidOperationException>(() => advancedRunner.PlaySeason());
    }

    [Fact]
    public void ConstructorAndRunSeason_GuardTheirArguments()
    {
        Assert.Throws<ArgumentNullException>(() => { _ = new WorldSeasonRunner(null!); });
        Assert.Throws<ArgumentNullException>(() => WorldSeasonRunner.RunSeason(null!));
    }

    // =============================================================================================
    // Helpers.
    // =============================================================================================

    /// <summary>A generated world, the summary of its played season and the pre-season growth snapshot.</summary>
    private sealed class SeasonPlaythrough
    {
        private SeasonPlaythrough(WorldState world, SeasonSummary summary, Dictionary<PlayerId, float> startMean)
        {
            World = world;
            Summary = summary;
            SeasonStartMeanCurrent = startMean;
        }

        public WorldState World { get; }

        public SeasonSummary Summary { get; }

        /// <summary>Every player's mean current attribute at generation, before the season's growth.</summary>
        public Dictionary<PlayerId, float> SeasonStartMeanCurrent { get; }

        public static SeasonPlaythrough Play(ulong seed)
        {
            WorldState world = WorldGenerator.Generate(FullPack.Value, seed);

            var startMean = new Dictionary<PlayerId, float>(world.Players.Count);
            foreach (WorldPlayer player in world.Players.All)
            {
                startMean.Add(player.Id, player.Attributes.MeanCurrent());
            }

            SeasonSummary summary = new WorldSeasonRunner(world).PlaySeason();
            return new SeasonPlaythrough(world, summary, startMean);
        }
    }

    /// <summary>Asserts a final table is sorted by points, then goal difference, then goals scored.</summary>
    private static void AssertTableOrdering(IReadOnlyList<LeagueTableRow> table, int position)
    {
        if (position == 0) return;

        LeagueTableRow previous = table[position - 1];
        LeagueTableRow current = table[position];
        if (previous.Points != current.Points)
        {
            Assert.True(
                previous.Points > current.Points,
                $"position {position} has more points than the row above it: {previous.ClubId} {previous.Points} " +
                $"vs {current.ClubId} {current.Points}");
            return;
        }

        if (previous.GoalDifference != current.GoalDifference)
        {
            Assert.True(
                previous.GoalDifference > current.GoalDifference,
                $"position {position} has a better goal difference than the row above it");
            return;
        }

        Assert.True(
            previous.GoalsFor >= current.GoalsFor,
            $"position {position} scored more goals than the row above it");
    }

    /// <summary>Asserts the club left its table tier for the adjacent tier with an automatic (non-play-off) move.</summary>
    private static void AssertAutomaticMove(SeasonSummary summary, ClubId clubId, int fromTier, int toTier)
    {
        DivisionMove? move = summary.DivisionMoves.FirstOrDefault(candidate => candidate.ClubId == clubId);
        Assert.NotNull(move);
        Assert.False(move!.ViaPlayoff, $"club {clubId} moved via a play-off, expected an automatic move");
        Assert.Equal(fromTier, move.FromTier);
        Assert.Equal(toTier, move.ToTier);
    }

    /// <summary>Counts the fixtures that carry a result in one week and sums their normal-time goals.</summary>
    private static (int League, int Cup, int Goals) CountPlayedFixtures(WorldState world, int week)
    {
        int league = 0;
        int cup = 0;
        int goals = 0;

        foreach (WorldLeague currentLeague in world.Leagues)
        {
            foreach (WorldFixture fixture in currentLeague.Fixtures)
            {
                if (!fixture.IsPlayed || fixture.Week != week) continue;
                league++;
                goals += fixture.HomeGoals + fixture.AwayGoals;
            }
        }

        foreach (CupRoundBracket round in world.Cup.Rounds)
        {
            foreach (CupTie tie in round.Ties)
            {
                if (!tie.Fixture.IsPlayed || tie.Fixture.Week != week) continue;
                cup++;
                goals += tie.Fixture.HomeGoals + tie.Fixture.AwayGoals;
            }
        }

        return (league, cup, goals);
    }

    private static void AssertSamePlayoffDecisions(SeasonSummary expected, SeasonSummary actual)
    {
        Assert.Equal(expected.PlayoffWinnerWasHigherDivisionClub.Count, actual.PlayoffWinnerWasHigherDivisionClub.Count);
        for (int boundaryTier = 1; boundaryTier <= 5; boundaryTier++)
        {
            Assert.Equal(
                expected.PlayoffWinnerWasHigherDivisionClub[boundaryTier],
                actual.PlayoffWinnerWasHigherDivisionClub[boundaryTier]);
        }
    }

    private static void AssertSameDivisionMoves(SeasonSummary expected, SeasonSummary actual)
    {
        Assert.Equal(expected.DivisionMoves.Count, actual.DivisionMoves.Count);
        for (int index = 0; index < expected.DivisionMoves.Count; index++)
        {
            Assert.Equal(expected.DivisionMoves[index], actual.DivisionMoves[index]);
        }
    }

    private static void AssertSameTables(SeasonSummary expected, SeasonSummary actual)
    {
        Assert.Equal(expected.TablesByTier.Count, actual.TablesByTier.Count);
        for (int tier = 0; tier < expected.TablesByTier.Count; tier++)
        {
            IReadOnlyList<LeagueTableRow> left = expected.TablesByTier[tier];
            IReadOnlyList<LeagueTableRow> right = actual.TablesByTier[tier];
            Assert.Equal(left.Count, right.Count);

            for (int position = 0; position < left.Count; position++)
            {
                LeagueTableRow leftRow = left[position];
                LeagueTableRow rightRow = right[position];
                Assert.Equal(leftRow.ClubId, rightRow.ClubId);
                Assert.Equal(leftRow.Played, rightRow.Played);
                Assert.Equal(leftRow.Won, rightRow.Won);
                Assert.Equal(leftRow.Drawn, rightRow.Drawn);
                Assert.Equal(leftRow.Lost, rightRow.Lost);
                Assert.Equal(leftRow.GoalsFor, rightRow.GoalsFor);
                Assert.Equal(leftRow.GoalsAgainst, rightRow.GoalsAgainst);
                Assert.Equal(leftRow.Points, rightRow.Points);
                Assert.Equal(leftRow.GoalDifference, rightRow.GoalDifference);
            }
        }
    }

    private static void AssertSameSeasonFixtures(WorldState expected, WorldState actual)
    {
        Assert.Equal(expected.Leagues.Count, actual.Leagues.Count);
        for (int leagueIndex = 0; leagueIndex < expected.Leagues.Count; leagueIndex++)
        {
            IReadOnlyList<WorldFixture> leftFixtures = expected.Leagues[leagueIndex].Fixtures;
            IReadOnlyList<WorldFixture> rightFixtures = actual.Leagues[leagueIndex].Fixtures;
            Assert.Equal(leftFixtures.Count, rightFixtures.Count);

            for (int fixtureIndex = 0; fixtureIndex < leftFixtures.Count; fixtureIndex++)
            {
                WorldFixture left = leftFixtures[fixtureIndex];
                WorldFixture right = rightFixtures[fixtureIndex];
                Assert.Equal(left.Id, right.Id);
                Assert.Equal(left.Week, right.Week);
                Assert.Equal(left.HomeClubId, right.HomeClubId);
                Assert.Equal(left.AwayClubId, right.AwayClubId);
                Assert.Equal(left.IsPlayed, right.IsPlayed);
                Assert.Equal(left.HomeGoals, right.HomeGoals);
                Assert.Equal(left.AwayGoals, right.AwayGoals);
            }
        }
    }

    private static void AssertSameCup(WorldState expected, WorldState actual)
    {
        Assert.Equal(expected.Cup.IsComplete, actual.Cup.IsComplete);
        Assert.Equal(expected.Cup.ChampionClubId, actual.Cup.ChampionClubId);
        Assert.Equal(expected.Cup.Rounds.Count, actual.Cup.Rounds.Count);

        for (int roundIndex = 0; roundIndex < expected.Cup.Rounds.Count; roundIndex++)
        {
            CupRoundBracket left = expected.Cup.Rounds[roundIndex];
            CupRoundBracket right = actual.Cup.Rounds[roundIndex];
            Assert.Equal(left.Round, right.Round);
            Assert.Equal(left.Week, right.Week);
            Assert.Equal(left.Ties.Count, right.Ties.Count);
            Assert.Equal(left.Byes.Count, right.Byes.Count);

            for (int tieIndex = 0; tieIndex < left.Ties.Count; tieIndex++)
            {
                CupTie leftTie = left.Ties[tieIndex];
                CupTie rightTie = right.Ties[tieIndex];
                Assert.Equal(leftTie.Fixture.Id, rightTie.Fixture.Id);
                Assert.Equal(leftTie.HomeClubId, rightTie.HomeClubId);
                Assert.Equal(leftTie.AwayClubId, rightTie.AwayClubId);
                Assert.Equal(leftTie.Fixture.IsPlayed, rightTie.Fixture.IsPlayed);
                Assert.Equal(leftTie.WinnerClubId, rightTie.WinnerClubId);
                Assert.Equal(leftTie.Fixture.HomeGoals, rightTie.Fixture.HomeGoals);
                Assert.Equal(leftTie.Fixture.AwayGoals, rightTie.Fixture.AwayGoals);
                Assert.Equal(leftTie.Fixture.HomePenaltyGoals, rightTie.Fixture.HomePenaltyGoals);
                Assert.Equal(leftTie.Fixture.AwayPenaltyGoals, rightTie.Fixture.AwayPenaltyGoals);
            }
        }
    }
}
