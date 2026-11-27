using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Players;
using FootballSim.World.Simulation;
using FootballSim.World.World;
using Xunit;
using Xunit.Abstractions;

namespace FootballSim.World.Tests.World;

/// <summary>
/// Stage W3 verification gate — the 20-season living-world soak (plan §6 "Stage W3"):
/// <list type="number">
/// <item><see cref="TwentySeasons_StayHealthyAndDeterministic"/> runs the real 108-club / six-division world
/// through twenty consecutive <see cref="WorldEcosystem.PlaySeasonWithRollover"/> cycles from a fixed seed
/// and asserts the plan's health contract: no crashes, no invalid players or non-finite numbers, a stable
/// population and age pyramid, tier separation that neither collapses nor drifts more than four OVR, a
/// living transfer/retirement/intake volume, and a complete competitive season every single year.</item>
/// <item><see cref="TwoSeasons_AreDeterministicForTheSameSeed"/> replays two seasons twice from the same seed
/// and compares final tables, cup champions, the whole transfer log, top scorers, world-health metrics and
/// sampled player attributes, then proves a different seed changes the season.</item>
/// </list>
///
/// <para>Runtime budget: the 20-season soak measures itself and asserts well under two minutes; the whole
/// file stays far below that.</para>
/// </summary>
public sealed class WorldSoakTests
{
    /// <summary>Fixed soak seed (the plan's date).</summary>
    private const ulong SoakSeed = 20261127UL;

    /// <summary>A second seed for the negative determinism check.</summary>
    private const ulong OtherSeed = 20261128UL;

    /// <summary>Seasons the W3 gate simulates.</summary>
    private const int SoakSeasons = 20;

    /// <summary>Six divisions x 306 double-round-robin fixtures.</summary>
    private const int LeagueMatchesPerSeason = 1836;

    /// <summary>Eight cup rounds: 18 + 26 + 32 + 16 + 8 + 4 + 2 + 1.</summary>
    private const int CupTiesPerSeason = 107;

    private const int LeaguesInWorld = 6;
    private const int ClubsPerLeague = 18;
    private const int ClubsInWorld = 108;
    private const int FixturesPerLeague = 306;

    /// <summary>Plan W3 health band for the active population in season 20.</summary>
    private const int MinimumPopulation = 2200;
    private const int MaximumPopulation = 3100;

    /// <summary>Plan W3 health band for the active mean age in season 20.</summary>
    private const double MinimumMeanAge = 22.5;
    private const double MaximumMeanAge = 27.5;

    /// <summary>Plan W3: no tier's season-20 mean overall may drift more than this from season 1.</summary>
    private const float MaximumTierOverallDrift = 4.0f;

    /// <summary>Plan W3: at least this many permanent/free/loan moves across the soak.</summary>
    private const int MinimumTransfers = 500;

    /// <summary>Plan W3: at least this many retirements plus youth intakes across the soak.</summary>
    private const int MinimumLifecycleEvents = 100;

    /// <summary>Hard runtime ceiling for the 20-season soak (plan gate).</summary>
    private const double MaximumSoakMilliseconds = 120_000.0;

    /// <summary>A club balance beyond this magnitude would be a numeric accident, not football money.</summary>
    private const long SaneBalanceMagnitude = 1_000_000_000_000_000L;

    private static readonly Lazy<WorldPack> FullPack =
        new(() => WorldPackLoader.Load(WorldPackPaths.ResolveDefaultPack()));

    private readonly ITestOutputHelper _output;

    public WorldSoakTests(ITestOutputHelper output) => _output = output;

    private static WorldPack Pack => FullPack.Value;

    // =============================================================================================
    // 1. The 20-season soak — the W3 gate.
    // =============================================================================================

    [Fact]
    public void TwentySeasons_StayHealthyAndDeterministic()
    {
        WorldState world = WorldGenerator.Generate(Pack, SoakSeed);
        var ecosystem = new WorldEcosystem(world);

        var health = new List<SeasonHealth>(SoakSeasons);
        var stopwatch = Stopwatch.StartNew();

        for (int season = 1; season <= SoakSeasons; season++)
        {
            long seasonStart = stopwatch.ElapsedMilliseconds;

            // Any exception fails the test here (it is the "zero crashes" gate).
            SeasonSummary summary = ecosystem.PlaySeasonWithRollover(out SeasonRolloverSummary rollover);
            double seasonMilliseconds = stopwatch.ElapsedMilliseconds - seasonStart;

            // --- Competitive season: every year is complete ----------------------------------------
            Assert.Equal(season, summary.Season);
            Assert.Equal(LeagueMatchesPerSeason, summary.LeagueMatchesPlayed);
            Assert.Equal(CupTiesPerSeason, summary.CupTiesPlayed);
            Assert.True(summary.CupChampionClubId.IsValid, $"season {season} crowned no cup champion");
            Assert.Equal(season, rollover.Season);

            AssertLeaguesAreComplete(world, season + 1);

            // --- World health: numbers stay real and finite ----------------------------------------
            AssertWorldIsFinite(world, season);
            AssertPopulationIsRegistered(world);

            // --- World health: a self-sustaining population and age pyramid -------------------------
            Assert.InRange(rollover.Population, MinimumPopulation - 400, MaximumPopulation + 200);
            Assert.InRange(rollover.MeanAge, 18.0, 40.0);
            Assert.True(
                rollover.YouthIntakes > 0,
                $"season {season} produced no youth intake (the world would stop replacing itself)");

            // --- World health: the pyramid separates cleanly every single season --------------------
            Assert.True(
                rollover.Tier1MeanOverall > rollover.Tier3MeanOverall,
                $"season {season}: tier 1 ({rollover.Tier1MeanOverall:0.0}) must outclass tier 3 " +
                $"({rollover.Tier3MeanOverall:0.0})");
            Assert.True(
                rollover.Tier3MeanOverall > rollover.Tier6MeanOverall,
                $"season {season}: tier 3 ({rollover.Tier3MeanOverall:0.0}) must outclass tier 6 " +
                $"({rollover.Tier6MeanOverall:0.0})");

            int minimumSquad = int.MaxValue;
            int maximumSquad = 0;
            int minimumAvailable = int.MaxValue;
            long squadTotal = 0L;
            foreach (WorldClub club in world.Clubs.All)
            {
                int available = 0;
                foreach (PlayerId playerId in club.Squad)
                {
                    if (world.Players[playerId].IsAvailableForSelection) available++;
                }

                minimumSquad = Math.Min(minimumSquad, club.Squad.Count);
                maximumSquad = Math.Max(maximumSquad, club.Squad.Count);
                minimumAvailable = Math.Min(minimumAvailable, available);
                squadTotal += club.Squad.Count;
            }

            int activePopulation = ecosystem.ActivePopulation();
            int freeAgents = 0;
            foreach (WorldPlayer player in world.Players.All)
            {
                if (!player.IsRetired && !player.ClubId.IsValid) freeAgents++;
            }

            health.Add(new SeasonHealth(
                season,
                rollover.Population,
                rollover.MeanAge,
                rollover.Tier1MeanOverall,
                rollover.Tier2MeanOverall,
                rollover.Tier3MeanOverall,
                rollover.Tier4MeanOverall,
                rollover.Tier5MeanOverall,
                rollover.Tier6MeanOverall,
                rollover.Transfers,
                rollover.Retirements,
                rollover.YouthIntakes,
                rollover.ManagerChanges,
                rollover.InjuriesRecovered,
                summary.LeagueGoals,
                summary.GoalsPerMatch,
                minimumSquad,
                maximumSquad,
                minimumAvailable,
                squadTotal / (double)ClubsInWorld,
                freeAgents,
                world.TransferLog.Count,
                activePopulation,
                seasonMilliseconds));
        }

        stopwatch.Stop();

        // =========================================================================================
        // Season-20 gate assertions (plan W3).
        // =========================================================================================
        SeasonHealth final = health[^1];
        SeasonHealth first = health[0];

        Assert.InRange(final.Population, MinimumPopulation, MaximumPopulation);
        Assert.InRange(final.MeanAge, MinimumMeanAge, MaximumMeanAge);

        // Tier separation holds every season (asserted above); the drift against season 1 is bounded.
        for (int index = 0; index < LeaguesInWorld; index++)
        {
            float seasonOne = first.TierMeanOverall(index);
            float seasonTwenty = final.TierMeanOverall(index);
            Assert.True(
                MathF.Abs(seasonTwenty - seasonOne) <= MaximumTierOverallDrift,
                $"tier {index + 1} mean overall drifted from {seasonOne:0.0} to {seasonTwenty:0.0} " +
                $"(limit {MaximumTierOverallDrift:0.0})");
        }

        // =========================================================================================
        // Living-world volume gates (plan W3).
        // =========================================================================================
        int marketMoves =
            world.CountTransfers(TransferKind.Permanent) +
            world.CountTransfers(TransferKind.Free) +
            world.CountTransfers(TransferKind.Loan);
        Assert.True(
            marketMoves >= MinimumTransfers,
            $"only {marketMoves} permanent/free/loan moves in {SoakSeasons} seasons (gate {MinimumTransfers})");

        int retirements = 0;
        int intakes = 0;
        foreach (SeasonHealth season in health)
        {
            retirements += season.Retirements;
            intakes += season.YouthIntakes;
        }

        Assert.True(
            retirements + intakes >= MinimumLifecycleEvents,
            $"only {retirements} retirements + {intakes} intakes (gate {MinimumLifecycleEvents})");

        // The world actually churned: every season had a transfer window and manager turnover.
        Assert.Contains(health, season => season.Transfers > 0);
        Assert.True(health.Sum(season => season.ManagerChanges) > 0);

        // =========================================================================================
        // Runtime gate.
        // =========================================================================================
        double totalMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        double slowestSeason = health.Max(season => season.Milliseconds);

        WriteHealthTable(health);
        _output.WriteLine(
            $"SOAK RESULT: {SoakSeasons} seasons in {totalMilliseconds / 1000.0:0.00} s " +
            $"(slowest season {slowestSeason:0} ms, budget {MaximumSoakMilliseconds / 1000.0:0} s); " +
            $"registry {world.Players.Count} rows / {final.Population} active; " +
            $"{marketMoves} market moves, {retirements} retirements, {intakes} intakes; " +
            $"season-20 population {final.Population} [{MinimumPopulation}..{MaximumPopulation}], " +
            $"mean age {final.MeanAge:0.00} [{MinimumMeanAge}..{MaximumMeanAge}]");

        Assert.True(
            totalMilliseconds < MaximumSoakMilliseconds,
            $"the {SoakSeasons}-season soak took {totalMilliseconds / 1000.0:0.0} s " +
            $"(budget {MaximumSoakMilliseconds / 1000.0:0} s)");
    }

    // =============================================================================================
    // 2. Two-season determinism — same seed identical, different seed different.
    // =============================================================================================

    [Fact]
    public void TwoSeasons_AreDeterministicForTheSameSeed()
    {
        TwoSeasonRun expected = TwoSeasonRun.Play(SoakSeed);
        TwoSeasonRun actual = TwoSeasonRun.Play(SoakSeed);

        AssertTwoSeasonRunsAreIdentical(expected, actual);

        // A different world seed must change the simulated season (champion, tables or top scorer).
        TwoSeasonRun other = TwoSeasonRun.Play(OtherSeed);
        bool differs = false;

        for (int season = 0; season < 2 && !differs; season++)
        {
            if (expected.Summaries[season].CupChampionClubId != other.Summaries[season].CupChampionClubId ||
                expected.Summaries[season].TopScorerId != other.Summaries[season].TopScorerId ||
                expected.Summaries[season].TopScorerGoals != other.Summaries[season].TopScorerGoals ||
                expected.Summaries[season].LeagueGoals != other.Summaries[season].LeagueGoals)
            {
                differs = true;
                break;
            }

            for (int tier = 0; tier < LeaguesInWorld && !differs; tier++)
            {
                IReadOnlyList<LeagueTableRow> left = expected.Summaries[season].TablesByTier[tier];
                IReadOnlyList<LeagueTableRow> right = other.Summaries[season].TablesByTier[tier];
                for (int position = 0; position < left.Count; position++)
                {
                    if (left[position].ClubId != right[position].ClubId ||
                        left[position].Points != right[position].Points ||
                        left[position].GoalsFor != right[position].GoalsFor ||
                        left[position].GoalsAgainst != right[position].GoalsAgainst)
                    {
                        differs = true;
                        break;
                    }
                }
            }
        }

        Assert.True(differs, "a different world seed produced the identical two seasons");

        _output.WriteLine(
            $"determinism: two seasons replayed identically ({expected.TransferLog.Count} transfer-log " +
            $"entries, champions " +
            $"{string.Join(" + ", expected.Summaries.Select(summary => summary.CupChampionClubId.Value))}); " +
            $"seed {OtherSeed} changed the season (cup champion " +
            $"{other.Summaries[0].CupChampionClubId.Value}, top scorer " +
            $"{other.Summaries[0].TopScorerId.Value})");
    }

    // =============================================================================================
    // Assertions.
    // =============================================================================================

    /// <summary>Every division has its 18 correctly-registered clubs and next season's 306 unplayed fixtures.</summary>
    private static void AssertLeaguesAreComplete(WorldState world, int nextSeason)
    {
        Assert.Equal(LeaguesInWorld, world.Leagues.Count);

        int leagueFixtures = 0;
        var registeredClubs = new HashSet<ClubId>();
        foreach (WorldLeague league in world.Leagues)
        {
            Assert.Equal(ClubsPerLeague, league.Clubs.Count);
            Assert.Equal(FixturesPerLeague, league.Fixtures.Count);

            foreach (ClubId clubId in league.Clubs)
            {
                WorldClub club = world.Clubs[clubId];
                Assert.Equal(league.Tier, club.Tier);
                Assert.Equal(league.Id, club.LeagueId);
                Assert.True(registeredClubs.Add(clubId), $"club {clubId} is registered in two divisions");
            }

            foreach (WorldFixture fixture in league.Fixtures)
            {
                Assert.False(fixture.IsPlayed, "the rollover left a next-season fixture already played");
                Assert.Equal(nextSeason, fixture.Season);
                Assert.InRange(
                    fixture.Week,
                    WorldCalendar.LeagueFirstHalfFirstWeek,
                    WorldCalendar.LeagueSecondHalfLastWeek);
                leagueFixtures++;
            }
        }

        Assert.Equal(ClubsInWorld, registeredClubs.Count);
        Assert.Equal(LeagueMatchesPerSeason, leagueFixtures);

        // Every squad is playable for the new season: the rollover safety valve guarantees eighteen
        // registered and sixteen available players; the weekly matchday valve tops availability up to twenty
        // before the first match.
        foreach (WorldClub club in world.Clubs.All)
        {
            Assert.True(
                club.Squad.Count >= 18,
                $"season {nextSeason}: {club.Name} has only {club.Squad.Count} registered players");
            int available = CountAvailable(world, club);
            Assert.True(
                available >= 16,
                $"season {nextSeason}: {club.Name} has only {available} available players");
        }

        // The new season's cup is a fresh bracket waiting in its preliminary round.
        Assert.Equal(nextSeason, world.Cup.Season);
        Assert.False(world.Cup.IsComplete);
        Assert.False(world.Cup.ChampionClubId.IsValid);
        Assert.Single(world.Cup.Rounds);
        Assert.Equal(CupRound.Preliminary, world.Cup.Rounds[0].Round);
        Assert.Equal(18, world.Cup.Rounds[0].Ties.Count);
        Assert.All(world.Cup.Rounds[0].Ties, tie => Assert.False(tie.Fixture.IsPlayed));
    }

    /// <summary>Zero NaN/infinity and every attribute/contract inside its documented range.</summary>
    private static void AssertWorldIsFinite(WorldState world, int season)
    {
        Assert.Equal(LeaguesInWorld, world.Leagues.Count);

        foreach (WorldClub club in world.Clubs.All)
        {
            ClubFinances finances = club.Finances;
            Assert.InRange(finances.Balance, -SaneBalanceMagnitude, SaneBalanceMagnitude);
            Assert.InRange(finances.TransferBudget, 0L, SaneBalanceMagnitude);
            Assert.InRange(finances.WeeklyWageBudget, 1L, SaneBalanceMagnitude);
            Assert.InRange(finances.WeeklyWageBill, 0L, SaneBalanceMagnitude);
            Assert.InRange(club.Reputation, 1, 100);
            Assert.InRange(club.Tier, 1, LeaguesInWorld);
        }

        int playersWithAppearances = 0;
        int playersWithArchivedSeasons = 0;
        foreach (WorldPlayer player in world.Players.All)
        {
            Assert.True(player.Attributes.IsValid(), $"season {season}: player {player.Id} has invalid attributes");
            Assert.True(
                float.IsFinite(player.Attributes.MeanCurrent()),
                $"season {season}: player {player.Id} has a non-finite mean overall");
            Assert.True(float.IsFinite(player.Condition01), $"season {season}: player {player.Id} condition");
            Assert.True(float.IsFinite(player.Visibility01), $"season {season}: player {player.Id} visibility");
            Assert.True(float.IsFinite(player.MatchSharpness01), $"season {season}: player {player.Id} sharpness");
            Assert.True(float.IsFinite(player.Form01), $"season {season}: player {player.Id} form");
            Assert.InRange(player.Age, WorldPlayer.MinimumAge, WorldPlayer.MaximumAge);
            Assert.InRange(player.Contract.WeeklyWage, 0L, 100_000_000L);
            Assert.InRange(player.Contract.EndSeason, 0, 10_000);
            Assert.True(
                float.IsFinite(player.CurrentSeason.RatingSum),
                $"season {season}: player {player.Id} rating sum");

            if (player.CurrentSeason.Appearances > 0)
            {
                playersWithAppearances++;
                Assert.True(float.IsFinite(player.CurrentSeason.AverageRating));
                Assert.InRange(player.CurrentSeason.AverageRating, 0f, 10f);
                Assert.Equal(
                    player.CurrentSeason.Appearances,
                    player.CurrentSeason.Starts + player.CurrentSeason.SubstituteAppearances);
            }

            if (player.SeasonHistory.Count > 0)
            {
                playersWithArchivedSeasons++;
                foreach (PlayerSeasonStats archived in player.SeasonHistory)
                {
                    Assert.InRange(archived.Season, 1, season);
                    Assert.True(archived.Appearances > 0);
                    Assert.True(archived.Starts + archived.SubstituteAppearances <= archived.Appearances);
                    Assert.True(float.IsFinite(archived.RatingSum));
                    Assert.True(float.IsFinite(archived.AverageRating));
                }
            }
        }

        // The rollover archives the finished season, so after season 1 the archive alone proves the match
        // programme produced real player output (the live counters were just reset).
        Assert.True(
            playersWithAppearances > 0 || playersWithArchivedSeasons > 1000,
            $"season {season}: only {playersWithArchivedSeasons} players have an archived season");
    }

    /// <summary>
    /// Every active player is either registered in exactly one squad or a free agent waiting for a window;
    /// the squads plus the free agents add up to the active population.
    /// </summary>
    private static void AssertPopulationIsRegistered(WorldState world)
    {
        var squads = new HashSet<PlayerId>();
        foreach (WorldClub club in world.Clubs.All)
        {
            foreach (PlayerId playerId in club.Squad)
            {
                WorldPlayer player = world.Players[playerId];
                Assert.False(player.IsRetired, $"retired player {player.Id} is still in {club.Name}'s squad");
                Assert.Equal(club.Id, player.ClubId);
                Assert.True(squads.Add(playerId), $"player {playerId} is registered in two squads");
            }
        }

        int active = 0;
        int freeAgents = 0;
        foreach (WorldPlayer player in world.Players.All)
        {
            if (player.IsRetired) continue;
            active++;

            if (!player.ClubId.IsValid)
            {
                // A released player stays on the market until the next rollover's viability pass.
                freeAgents++;
                Assert.False(squads.Contains(player.Id), $"free agent {player.Id} is still in a squad");
                continue;
            }

            Assert.True(
                squads.Contains(player.Id),
                $"active player {player.Id} is not registered in any squad");
        }

        Assert.Equal(active, squads.Count + freeAgents);
    }

    private static int CountAvailable(WorldState world, WorldClub club)
    {
        int available = 0;
        foreach (PlayerId playerId in club.Squad)
        {
            if (world.Players[playerId].IsAvailableForSelection) available++;
        }

        return available;
    }

    // =============================================================================================
    // Determinism comparison.
    // =============================================================================================

    private static void AssertTwoSeasonRunsAreIdentical(TwoSeasonRun expected, TwoSeasonRun actual)
    {
        Assert.Equal(2, expected.Summaries.Count);
        Assert.Equal(2, actual.Summaries.Count);

        for (int season = 0; season < 2; season++)
        {
            SeasonSummary left = expected.Summaries[season];
            SeasonSummary right = actual.Summaries[season];

            Assert.Equal(left.Season, right.Season);
            Assert.Equal(left.LeagueMatchesPlayed, right.LeagueMatchesPlayed);
            Assert.Equal(left.LeagueGoals, right.LeagueGoals);
            Assert.Equal(left.CupTiesPlayed, right.CupTiesPlayed);
            Assert.Equal(left.CupChampionClubId, right.CupChampionClubId);
            Assert.Equal(left.TopScorerId, right.TopScorerId);
            Assert.Equal(left.TopScorerGoals, right.TopScorerGoals);
            Assert.Equal(left.InjuriesThisSeason, right.InjuriesThisSeason);
            Assert.Equal(left.YellowCardsThisSeason, right.YellowCardsThisSeason);
            Assert.Equal(left.RedCardsThisSeason, right.RedCardsThisSeason);
            Assert.Equal(left.ChampionsByTier, right.ChampionsByTier);
            Assert.Equal(left.DivisionMoves, right.DivisionMoves);

            Assert.Equal(left.TablesByTier.Count, right.TablesByTier.Count);
            for (int tier = 0; tier < left.TablesByTier.Count; tier++)
            {
                IReadOnlyList<LeagueTableRow> leftTable = left.TablesByTier[tier];
                IReadOnlyList<LeagueTableRow> rightTable = right.TablesByTier[tier];
                Assert.Equal(leftTable.Count, rightTable.Count);

                for (int position = 0; position < leftTable.Count; position++)
                {
                    LeagueTableRow leftRow = leftTable[position];
                    LeagueTableRow rightRow = rightTable[position];
                    Assert.Equal(leftRow.ClubId, rightRow.ClubId);
                    Assert.Equal(leftRow.Played, rightRow.Played);
                    Assert.Equal(leftRow.Won, rightRow.Won);
                    Assert.Equal(leftRow.Drawn, rightRow.Drawn);
                    Assert.Equal(leftRow.Lost, rightRow.Lost);
                    Assert.Equal(leftRow.Points, rightRow.Points);
                    Assert.Equal(leftRow.GoalsFor, rightRow.GoalsFor);
                    Assert.Equal(leftRow.GoalsAgainst, rightRow.GoalsAgainst);
                }
            }

            SeasonRolloverSummary leftRollover = expected.Rollovers[season];
            SeasonRolloverSummary rightRollover = actual.Rollovers[season];
            Assert.Equal(leftRollover.Season, rightRollover.Season);
            Assert.Equal(leftRollover.Retirements, rightRollover.Retirements);
            Assert.Equal(leftRollover.YouthIntakes, rightRollover.YouthIntakes);
            Assert.Equal(leftRollover.Population, rightRollover.Population);
            Assert.Equal(leftRollover.MeanAge, rightRollover.MeanAge, 10);
            Assert.Equal(leftRollover.Tier1MeanOverall, rightRollover.Tier1MeanOverall);
            Assert.Equal(leftRollover.Tier2MeanOverall, rightRollover.Tier2MeanOverall);
            Assert.Equal(leftRollover.Tier3MeanOverall, rightRollover.Tier3MeanOverall);
            Assert.Equal(leftRollover.Tier4MeanOverall, rightRollover.Tier4MeanOverall);
            Assert.Equal(leftRollover.Tier5MeanOverall, rightRollover.Tier5MeanOverall);
            Assert.Equal(leftRollover.Tier6MeanOverall, rightRollover.Tier6MeanOverall);
            Assert.Equal(leftRollover.Transfers, rightRollover.Transfers);
            Assert.Equal(leftRollover.ManagerChanges, rightRollover.ManagerChanges);
            Assert.Equal(leftRollover.InjuriesRecovered, rightRollover.InjuriesRecovered);
            Assert.Equal(leftRollover.DivisionMoves, rightRollover.DivisionMoves);
        }

        // The whole transfer/lifecycle log, entry for entry (season, week, player, clubs, kind, fee).
        Assert.Equal(expected.TransferLog.Count, actual.TransferLog.Count);
        for (int index = 0; index < expected.TransferLog.Count; index++)
        {
            TransferRecord left = expected.TransferLog[index];
            TransferRecord right = actual.TransferLog[index];
            Assert.Equal(left.Season, right.Season);
            Assert.Equal(left.Week, right.Week);
            Assert.Equal(left.PlayerId, right.PlayerId);
            Assert.Equal(left.FromClubId, right.FromClubId);
            Assert.Equal(left.ToClubId, right.ToClubId);
            Assert.Equal(left.Kind, right.Kind);
            Assert.Equal(left.Fee, right.Fee);
        }

        // Sampled player state: the same ids, ages, clubs, contracts and attribute values.
        Assert.Equal(expected.RegistryCount, actual.RegistryCount);
        Assert.Equal(expected.SampledPlayers.Count, actual.SampledPlayers.Count);
        for (int sample = 0; sample < expected.SampledPlayers.Count; sample++)
        {
            SampledPlayer left = expected.SampledPlayers[sample];
            SampledPlayer right = actual.SampledPlayers[sample];
            Assert.Equal(left.Id, right.Id);
            Assert.Equal(left.Age, right.Age);
            Assert.Equal(left.IsRetired, right.IsRetired);
            Assert.Equal(left.ClubId, right.ClubId);
            Assert.Equal(left.ContractEndSeason, right.ContractEndSeason);
            Assert.Equal(left.ContractWeeklyWage, right.ContractWeeklyWage);
            Assert.Equal(left.Appearances, right.Appearances);
            Assert.Equal(left.Goals, right.Goals);
            Assert.Equal(left.Attributes, right.Attributes);
        }

        // Same next-season fixture list.
        Assert.Equal(expected.FixtureIds, actual.FixtureIds);
        Assert.Equal(expected.FixtureWeeks, actual.FixtureWeeks);
        Assert.Equal(expected.FixtureHomeClubs, actual.FixtureHomeClubs);
        Assert.Equal(expected.FixtureAwayClubs, actual.FixtureAwayClubs);
    }

    // =============================================================================================
    // Telemetry.
    // =============================================================================================

    private void WriteHealthTable(IReadOnlyList<SeasonHealth> health)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "S  | pop  | age  | T1    T2    T3    T4    T5    T6   | trf ret int mgr inj | " +
            "reg min/avg/max avail | free | goals/gm | log   | ms");
        foreach (SeasonHealth season in health)
        {
            builder.AppendLine(
                $"{season.Season,2:00} | {season.Population,4} | {season.MeanAge,4:0.00} | " +
                $"{season.Tier1MeanOverall,5:0.0} {season.Tier2MeanOverall,5:0.0} " +
                $"{season.Tier3MeanOverall,5:0.0} {season.Tier4MeanOverall,5:0.0} " +
                $"{season.Tier5MeanOverall,5:0.0} {season.Tier6MeanOverall,5:0.0} | " +
                $"{season.Transfers,3} {season.Retirements,3} {season.YouthIntakes,3} " +
                $"{season.ManagerChanges,3} {season.InjuriesRecovered,3} | " +
                $"{season.MinimumSquad,3}/{season.AverageSquad,4:0.0}/{season.MaximumSquad,3} " +
                $"{season.MinimumAvailable,3} | {season.FreeAgents,4} | " +
                $"{season.GoalsPerMatch,5:0.00} | {season.TransferLogCount,5} | {season.Milliseconds,5:0}");
        }

        foreach (string line in builder.ToString().Split(Environment.NewLine))
        {
            if (line.Length > 0) _output.WriteLine(line);
        }
    }

    // =============================================================================================
    // Data carriers.
    // =============================================================================================

    /// <summary>One season's world-health telemetry.</summary>
    private sealed record SeasonHealth(
        int Season,
        int Population,
        double MeanAge,
        float Tier1MeanOverall,
        float Tier2MeanOverall,
        float Tier3MeanOverall,
        float Tier4MeanOverall,
        float Tier5MeanOverall,
        float Tier6MeanOverall,
        int Transfers,
        int Retirements,
        int YouthIntakes,
        int ManagerChanges,
        int InjuriesRecovered,
        int LeagueGoals,
        float GoalsPerMatch,
        int MinimumSquad,
        int MaximumSquad,
        int MinimumAvailable,
        double AverageSquad,
        int FreeAgents,
        int TransferLogCount,
        int ActivePopulation,
        double Milliseconds)
    {
        /// <summary>Tier mean overall by one-based tier.</summary>
        public float TierMeanOverall(int tier) => tier switch
        {
            1 => Tier1MeanOverall,
            2 => Tier2MeanOverall,
            3 => Tier3MeanOverall,
            4 => Tier4MeanOverall,
            5 => Tier5MeanOverall,
            _ => Tier6MeanOverall
        };
    }

    /// <summary>One sampled player's deterministic state (record equality is exact, arrays are compared too).</summary>
    private sealed record SampledPlayer(
        PlayerId Id,
        int Age,
        bool IsRetired,
        ClubId ClubId,
        int ContractEndSeason,
        long ContractWeeklyWage,
        int Appearances,
        int Goals,
        float[] Attributes,
        float Visibility,
        float Condition);

    /// <summary>Two complete ecosystem seasons replayed from one seed, with everything determinism checks need.</summary>
    private sealed class TwoSeasonRun
    {
        private TwoSeasonRun()
        {
        }

        public WorldState World { get; private init; } = null!;

        public List<SeasonSummary> Summaries { get; } = new(2);

        public List<SeasonRolloverSummary> Rollovers { get; } = new(2);

        public IReadOnlyList<TransferRecord> TransferLog { get; private init; } = Array.Empty<TransferRecord>();

        public int RegistryCount { get; private init; }

        public IReadOnlyList<SampledPlayer> SampledPlayers { get; private init; } = Array.Empty<SampledPlayer>();

        public int[] FixtureIds { get; private init; } = Array.Empty<int>();

        public int[] FixtureWeeks { get; private init; } = Array.Empty<int>();

        public int[] FixtureHomeClubs { get; private init; } = Array.Empty<int>();

        public int[] FixtureAwayClubs { get; private init; } = Array.Empty<int>();

        public static TwoSeasonRun Play(ulong seed)
        {
            WorldState world = WorldGenerator.Generate(FullPack.Value, seed);
            var ecosystem = new WorldEcosystem(world);
            var summaries = new List<SeasonSummary>(2);
            var rollovers = new List<SeasonRolloverSummary>(2);

            for (int season = 0; season < 2; season++)
            {
                summaries.Add(ecosystem.PlaySeasonWithRollover(out SeasonRolloverSummary rollover));
                rollovers.Add(rollover);
            }

            // Snapshot the append-only log (the world keeps growing it; the check compares two finished runs).
            var transferLog = new List<TransferRecord>(world.TransferLog.Count);
            for (int index = 0; index < world.TransferLog.Count; index++)
            {
                transferLog.Add(world.TransferLog[index]);
            }

            // Sample a spread of players, always resolving the same registry slots in both runs.
            var sampled = new List<SampledPlayer>(32);
            for (int index = 0; index < world.Players.Count; index += 83)
            {
                WorldPlayer player = world.Players.All[index];
                var attributes = new float[AttributeMetadata.Count];
                for (int attribute = 0; attribute < AttributeMetadata.Count; attribute++)
                {
                    attributes[attribute] = player.Attributes.Current((AttributeId)attribute);
                }

                sampled.Add(new SampledPlayer(
                    player.Id,
                    player.Age,
                    player.IsRetired,
                    player.ClubId,
                    player.Contract.EndSeason,
                    player.Contract.WeeklyWage,
                    player.CurrentSeason.Appearances,
                    player.CurrentSeason.Goals,
                    attributes,
                    player.Visibility01,
                    player.Condition01));
            }

            // The next season's fixture list is part of the deterministic state too.
            int fixtureCount = 0;
            foreach (WorldLeague league in world.Leagues) fixtureCount += league.Fixtures.Count;
            var fixtureIds = new int[fixtureCount];
            var fixtureWeeks = new int[fixtureCount];
            var fixtureHome = new int[fixtureCount];
            var fixtureAway = new int[fixtureCount];
            int fixtureIndex = 0;
            foreach (WorldLeague league in world.Leagues)
            {
                foreach (WorldFixture fixture in league.Fixtures)
                {
                    fixtureIds[fixtureIndex] = fixture.Id.Value;
                    fixtureWeeks[fixtureIndex] = fixture.Week;
                    fixtureHome[fixtureIndex] = fixture.HomeClubId.Value;
                    fixtureAway[fixtureIndex] = fixture.AwayClubId.Value;
                    fixtureIndex++;
                }
            }

            var run = new TwoSeasonRun
            {
                World = world,
                TransferLog = transferLog,
                RegistryCount = world.Players.Count,
                SampledPlayers = sampled,
                FixtureIds = fixtureIds,
                FixtureWeeks = fixtureWeeks,
                FixtureHomeClubs = fixtureHome,
                FixtureAwayClubs = fixtureAway
            };

            run.Summaries.AddRange(summaries);
            run.Rollovers.AddRange(rollovers);
            return run;
        }
    }
}
