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
/// Stage W3 ecosystem gate, fast half: <see cref="WorldEcosystem"/> as the living world's season loop — the
/// opening-contract bridge, the weekly finance/medical/scouting/transfer hook, the full off-season rollover
/// and the world-health telemetry.
///
/// <para>The expensive state is shared through lazies: one generated world (~40 ms), one season played
/// through the ecosystem (~1 s), an identical replay for determinism, the same seed played by the bare
/// <see cref="WorldSeasonRunner"/> for the schedule comparison, and one two-season chain. The 20-season soak
/// lives in <see cref="WorldSoakTests"/>.</para>
/// </summary>
public sealed class WorldEcosystemTests
{
    private const ulong DefaultSeed = 20261127UL;

    /// <summary>Six divisions x 306 double-round-robin fixtures.</summary>
    private const int LeagueMatchesPerSeason = 1836;

    /// <summary>Eight cup rounds: 18 + 26 + 32 + 16 + 8 + 4 + 2 + 1.</summary>
    private const int CupTiesPerSeason = 107;

    private const int ClubsPerLeague = 18;
    private const int ClubsInWorld = 108;
    private const int LeaguesInWorld = 6;
    private const int FixturesPerLeague = 306;

    private static readonly Lazy<WorldPack> FullPack =
        new(() => WorldPackLoader.Load(WorldPackPaths.ResolveDefaultPack()));

    /// <summary>An untouched generated world (read-only for the tests that share it).</summary>
    private static readonly Lazy<WorldState> Fresh =
        new(() => WorldGenerator.Generate(FullPack.Value, DefaultSeed));

    /// <summary>One world played through the ecosystem for a full season and rolled over.</summary>
    private static readonly Lazy<EcosystemSeason> Season =
        new(() => EcosystemSeason.Play(DefaultSeed));

    /// <summary>An identical replay of <see cref="Season"/> for the determinism gate.</summary>
    private static readonly Lazy<EcosystemSeason> SeasonAgain =
        new(() => EcosystemSeason.Play(DefaultSeed));

    /// <summary>The same seed played by the bare W2 runner, for the schedule comparison.</summary>
    private static readonly Lazy<BarePlaythrough> BareSeason =
        new(() => BarePlaythrough.Play(DefaultSeed));

    /// <summary>One world advanced through two complete <see cref="WorldEcosystem.PlaySeasonWithRollover"/> cycles.</summary>
    private static readonly Lazy<TwoSeasonChain> Chain =
        new(() => TwoSeasonChain.Play(DefaultSeed));

    private readonly ITestOutputHelper _output;

    public WorldEcosystemTests(ITestOutputHelper output) => _output = output;

    private static WorldPack Pack => FullPack.Value;

    // =============================================================================================
    // 1. Construction and opening state.
    // =============================================================================================

    [Fact]
    public void Constructor_DoesNotMutateTheWorld()
    {
        WorldState world = Fresh.Value;

        // A generated world has no contracts, no played fixture and no transfer history.
        WorldSnapshot before = WorldSnapshot.Capture(world);
        var ecosystem = new WorldEcosystem(world);
        WorldSnapshot after = WorldSnapshot.Capture(world);

        Assert.Equal(before, after);
        Assert.Same(world, ecosystem.World);

        // The opening contracts are still missing: construction did not secretly initialise the economy.
        Assert.Contains(
            world.Players.All.Where(player => player.ClubId.IsValid),
            player => player.Contract.EndSeason == 0);

        _output.WriteLine(
            $"constructor: {world.Players.Count} players, {world.Clubs.Count} clubs, " +
            $"{world.TransferLog.Count} log entries — state unchanged");
    }

    [Fact]
    public void EnsureInitialized_GivesEveryClubPlayerAContractAndIsIdempotent()
    {
        WorldState world = WorldGenerator.Generate(Pack, DefaultSeed);
        var ecosystem = new WorldEcosystem(world);

        int playersWithClubs = 0;
        foreach (WorldPlayer player in world.Players.All)
        {
            if (!player.ClubId.IsValid) continue;
            playersWithClubs++;
            Assert.Equal(0, player.Contract.EndSeason);
        }

        Assert.Equal(ClubsInWorld * 24, playersWithClubs);

        ecosystem.EnsureInitialized();

        // Every squad player now has a real contract priced from his club's division.
        foreach (WorldPlayer player in world.Players.All)
        {
            if (!player.ClubId.IsValid) continue;
            WorldClub club = world.Clubs[player.ClubId];
            PlayerContract contract = player.Contract;

            Assert.Equal(world.Season, contract.SignedSeason);
            Assert.True(contract.EndSeason >= world.Season, $"player {player.Id} has no contract run");
            Assert.InRange(contract.EndSeason, world.Season, world.Season + 3);
            Assert.Equal(ContractAndFinanceSystem.WageDemand(player, club.Tier), contract.WeeklyWage);
            Assert.False(contract.IsOnLoan);
        }

        // Wage bills and budgets agree with the contracts that now exist.
        foreach (WorldClub club in world.Clubs.All)
        {
            long expectedBill = 0L;
            foreach (PlayerId playerId in club.Squad)
            {
                expectedBill += world.Players[playerId].Contract.WeeklyWage;
            }

            Assert.Equal(expectedBill, club.Finances.WeeklyWageBill);
            Assert.True(club.Finances.WeeklyWageBudget >= 1L);
            Assert.True(club.Finances.TransferBudget >= 0L);
        }

        // Idempotent: a second and third call rewrite nothing (the contract lengths are not re-rolled).
        ContractSnapshot first = ContractSnapshot.Capture(world);
        ecosystem.EnsureInitialized();
        ecosystem.EnsureInitialized();
        AssertSameContracts(first, ContractSnapshot.Capture(world));

        // A second ecosystem over the already-initialised world also leaves it untouched.
        var second = new WorldEcosystem(world);
        second.EnsureInitialized();
        AssertSameContracts(first, ContractSnapshot.Capture(world));

        _output.WriteLine(
            $"EnsureInitialized: {playersWithClubs} squad players contracted, " +
            $"{world.Clubs.All.Sum(club => club.Finances.WeeklyWageBill):N0} weekly wages, idempotent");
    }

    // =============================================================================================
    // 2. One season through the ecosystem.
    // =============================================================================================

    [Fact]
    public void PlaySeason_CompletesTheSeasonAndRunsTheWeeklyEcosystem()
    {
        EcosystemSeason play = Season.Value;
        WorldState world = play.World;
        SeasonSummary summary = play.Summary;
        SeasonRolloverSummary rollover = play.Rollover;

        // --- The season itself: the W2 completeness contract ---------------------------------------
        Assert.Equal(1, summary.Season);
        Assert.Equal(LeagueMatchesPerSeason, summary.LeagueMatchesPlayed);
        Assert.Equal(CupTiesPerSeason, summary.CupTiesPlayed);
        Assert.Equal(LeaguesInWorld, summary.TablesByTier.Count);
        Assert.All(summary.TablesByTier, table => Assert.Equal(ClubsPerLeague, table.Count));
        Assert.True(play.CupCompleteAtSeasonEnd);
        Assert.True(summary.CupChampionClubId.IsValid);
        Assert.Equal(WorldCalendar.OffSeasonFirstWeek, play.WeekAtSeasonEnd);
        Assert.Equal(LeagueMatchesPerSeason, play.PlayedLeagueFixturesAtSeasonEnd);

        // --- Finance ran: money moved and wages were paid -------------------------------------------
        int changedBalances = 0;
        for (int clubIndex = 0; clubIndex < world.Clubs.Count; clubIndex++)
        {
            if (play.BalancesAtSeasonStart[clubIndex] != play.BalancesAtSeasonEnd[clubIndex]) changedBalances++;
        }

        Assert.True(changedBalances > ClubsInWorld / 2, $"only {changedBalances} clubs had any cash flow");

        long totalWages = 0L;
        foreach (WorldClub club in world.Clubs.All)
        {
            Assert.True(club.Finances.WeeklyWageBill >= 0L);
            totalWages += club.Finances.WeeklyWageBill;
        }

        Assert.True(totalWages > 0L, "the whole pyramid paid no wages");
        Assert.True(
            world.Clubs.All.All(club => club.Tier >= 5 || club.Finances.WeeklyWageBill > 0L),
            "a professional club has an empty wage bill");
        // Relegated clubs keep the contracts they signed in a higher division, so an amateur club may
        // still carry a small wage bill; it must never be negative.
        Assert.True(world.Clubs.All.All(club => club.Finances.WeeklyWageBill >= 0L));

        // --- Scouting ran: visibility moved every week and the cup spotlight is applied -------------
        int movedVisibility = 0;
        for (int index = 0; index < play.VisibilityAtSeasonStart.Length; index++)
        {
            if (play.VisibilityAtSeasonStart[index] != play.VisibilityAtSeasonEnd[index]) movedVisibility++;
        }

        Assert.True(
            movedVisibility > play.VisibilityAtSeasonStart.Length * 9 / 10,
            $"only {movedVisibility} players had scouting visibility updated");

        // --- Contracts and the market ran -----------------------------------------------------------
        Assert.True(rollover.Transfers > 0, "the transfer windows produced no moves");
        Assert.True(world.CountTransfers(TransferKind.Permanent) > 0);
        Assert.True(rollover.InjuriesRecovered > 0, "the club-medical recovery tick never healed anybody");
        Assert.True(rollover.ManagerChanges > 0, "the manager carousel made no changes");
        Assert.True(rollover.Retirements > 0, "the off-season produced no retirements");
        Assert.True(rollover.YouthIntakes >= ClubsInWorld, "every club must receive at least one intake");

        // --- The world-health telemetry is finite and internally consistent -------------------------
        Assert.Equal(1, rollover.Season);
        Assert.InRange(rollover.Population, LifecycleSystem.MinimumActivePopulation, 3200);
        Assert.Equal(rollover.Population, play.Ecosystem.ActivePopulation());
        Assert.Equal(rollover.MeanAge, play.Ecosystem.MeanActiveAge(), 10);
        Assert.InRange(rollover.MeanAge, 18.0, 40.0);
        Assert.Equal(rollover.DivisionMoves.Count, summary.DivisionMoves.Count);
        Assert.Equal(summary.DivisionMoves, rollover.DivisionMoves);
        AssertFiniteTierMeans(rollover);

        _output.WriteLine(
            $"season {summary.Season}: {summary.LeagueMatchesPlayed} league matches, " +
            $"{summary.CupTiesPlayed} cup ties, champion {world.Clubs[summary.CupChampionClubId].Name}; " +
            $"{changedBalances} clubs had cash flow, {totalWages:N0} weekly wages, " +
            $"{movedVisibility}/{play.VisibilityAtSeasonStart.Length} visibility values moved; " +
            $"rollover: {rollover.Transfers} market moves, {rollover.Retirements} retirements, " +
            $"{rollover.YouthIntakes} intakes, {rollover.ManagerChanges} manager changes, " +
            $"{rollover.InjuriesRecovered} injuries recovered, population {rollover.Population}, " +
            $"mean age {rollover.MeanAge:0.00}");
    }

    [Fact]
    public void PlaySeason_KeepsTheFixtureScheduleIdenticalToTheBareRunner()
    {
        EcosystemSeason play = Season.Value;
        BarePlaythrough bare = BareSeason.Value;

        // Totals: the point of the comparison — the ecosystem runs around the same match programme.
        Assert.Equal(bare.Summary.LeagueMatchesPlayed, play.Summary.LeagueMatchesPlayed);
        Assert.Equal(LeagueMatchesPerSeason, play.Summary.LeagueMatchesPlayed);
        Assert.Equal(CupTiesPerSeason, play.Summary.CupTiesPlayed);
        Assert.Equal(bare.Summary.CupTiesPlayed, play.Summary.CupTiesPlayed);

        // The fixture list itself is byte-for-byte the same: the ecosystem's weekly work consumes none of
        // the runner's scheduling draws, so every match is still played by the same clubs, home/away, in
        // the same week, with the same match id.
        int comparedFixtures = 0;
        for (int leagueIndex = 0; leagueIndex < LeaguesInWorld; leagueIndex++)
        {
            WorldFixture[] ecosystemFixtures = play.PlayedLeagueFixtures[leagueIndex];
            IReadOnlyList<WorldFixture> bareFixtures = bare.World.Leagues[leagueIndex].Fixtures;
            Assert.Equal(FixturesPerLeague, ecosystemFixtures.Length);
            Assert.Equal(bareFixtures.Count, ecosystemFixtures.Length);

            for (int fixtureIndex = 0; fixtureIndex < ecosystemFixtures.Length; fixtureIndex++)
            {
                WorldFixture left = ecosystemFixtures[fixtureIndex];
                WorldFixture right = bareFixtures[fixtureIndex];
                Assert.Equal(right.Id, left.Id);
                Assert.Equal(right.Season, left.Season);
                Assert.Equal(right.Week, left.Week);
                Assert.Equal(right.HomeClubId, left.HomeClubId);
                Assert.Equal(right.AwayClubId, left.AwayClubId);
                Assert.True(left.IsPlayed);
                comparedFixtures++;
            }
        }

        Assert.Equal(LeagueMatchesPerSeason, comparedFixtures);

        // The match-result draws are untouched: the quick-sim and cup streams advanced through exactly the
        // same sequence during the season in both worlds, so the ecosystem never fed the runner's randomness.
        IReadOnlyDictionary<WorldRandomStream, ulong> bareStates = bare.World.CaptureRngStates();
        Assert.Equal(StateOf(bareStates, WorldRandomStream.QuickSim), play.QuickSimStateAtSeasonEnd);
        Assert.Equal(StateOf(bareStates, WorldRandomStream.Cup), play.CupStateAtSeasonEnd);

        // Scorelines are *not* expected to be identical: the ecosystem legitimately changes who is
        // available (weekly medical recovery), who belongs to whom (transfer windows) and therefore the
        // selected XI. Measure the divergence so a regression that perturbs the match model itself shows up
        // as a total rather than a hand-wave.
        int identicalScorelines = 0;
        for (int leagueIndex = 0; leagueIndex < LeaguesInWorld; leagueIndex++)
        {
            WorldFixture[] left = play.PlayedLeagueFixtures[leagueIndex];
            IReadOnlyList<WorldFixture> right = bare.World.Leagues[leagueIndex].Fixtures;
            for (int fixtureIndex = 0; fixtureIndex < left.Length; fixtureIndex++)
            {
                if (left[fixtureIndex].HomeGoals == right[fixtureIndex].HomeGoals &&
                    left[fixtureIndex].AwayGoals == right[fixtureIndex].AwayGoals)
                {
                    identicalScorelines++;
                }
            }
        }

        _output.WriteLine(
            $"bare-runner comparison: {comparedFixtures} identical fixtures (ids, clubs, weeks), " +
            $"{identicalScorelines}/{comparedFixtures} identical scorelines " +
            $"({LeagueMatchesPerSeason - identicalScorelines} differ because the ecosystem changes " +
            $"availability/squads, not the match model); QuickSim and Cup stream states identical");
    }

    // =============================================================================================
    // 3. Off-season rollover.
    // =============================================================================================

    [Fact]
    public void Rollover_AgesPlayersArchivesTheSeasonAndBuildsTheNextOne()
    {
        EcosystemSeason play = Season.Value;
        WorldState world = play.World;
        SeasonRolloverSummary rollover = play.Rollover;

        // --- Aging: everyone who existed at season end is one year older ---------------------------
        Assert.Equal(play.AgesAtSeasonEnd.Length, play.PlayerCountAtSeasonEnd);
        for (int index = 0; index < play.AgesAtSeasonEnd.Length; index++)
        {
            WorldPlayer player = world.Players.All[index];
            Assert.Equal(play.AgesAtSeasonEnd[index] + 1, player.Age);
        }

        // --- Retirements and youth intakes were recorded ------------------------------------------
        Assert.True(rollover.Retirements > 0);
        Assert.True(rollover.YouthIntakes >= ClubsInWorld);

        int retiredLogEntries = 0;
        int rolloverIntakeLogEntries = 0;
        foreach (TransferRecord record in world.TransferLog)
        {
            if (record.Kind == TransferKind.Retirement)
            {
                retiredLogEntries++;
            }
            else if (record.Kind == TransferKind.YouthIntake &&
                     record.Season == 1 &&
                     record.Week == WorldCalendar.OffSeasonFirstWeek)
            {
                // Intakes created by the rollover itself: the annual intake plus any emergency call-up.
                rolloverIntakeLogEntries++;
            }
        }

        Assert.Equal(rollover.Retirements, retiredLogEntries);
        Assert.True(
            rolloverIntakeLogEntries >= rollover.YouthIntakes,
            "the rollover intake ledger must cover every annual intake plus any emergency call-up");

        // Retired players stay in the registry for history, so the registry grows by the rollover intakes.
        Assert.Equal(play.PlayerCountAtSeasonEnd + rolloverIntakeLogEntries, world.Players.Count);

        int retiredPlayers = 0;
        foreach (WorldPlayer player in world.Players.All)
        {
            if (player.IsRetired) retiredPlayers++;
        }

        Assert.Equal(world.Players.Count - retiredPlayers, rollover.Population);
        Assert.True(rollover.Population > 0);
        Assert.InRange(rollover.MeanAge, 18.0, 40.0);

        // --- Season stats: played seasons archived, live counters reset to the new season ----------
        for (int index = 0; index < play.AppearancesAtSeasonEnd.Length; index++)
        {
            WorldPlayer player = world.Players.All[index];
            int appearances = play.AppearancesAtSeasonEnd[index];

            if (appearances > 0)
            {
                Assert.Single(player.SeasonHistory);
                PlayerSeasonStats archived = player.SeasonHistory[0];
                Assert.Equal(1, archived.Season);
                Assert.Equal(appearances, archived.Appearances);
                Assert.Equal(play.GoalsAtSeasonEnd[index], archived.Goals);
            }
            else
            {
                Assert.Empty(player.SeasonHistory);
            }

            Assert.Equal(2, player.CurrentSeason.Season);
            Assert.Equal(0, player.CurrentSeason.Appearances);
            Assert.Equal(0, player.CurrentSeason.Goals);
            Assert.Equal(0, player.CurrentSeason.MinutesPlayed);
            Assert.Equal(0f, player.CurrentSeason.RatingSum);
        }

        // --- Managers start the new season with a clean record ------------------------------------
        foreach (WorldManager manager in world.Managers.All)
        {
            Assert.Equal(0, manager.MatchesInCharge);
            Assert.Equal(0, manager.Wins);
            Assert.Equal(0, manager.Draws);
            Assert.Equal(0, manager.Losses);
        }

        // --- The calendar and the next season's competitions --------------------------------------
        Assert.Equal(new WorldDate(2, WorldCalendar.PreSeasonFirstWeek), world.Date);
        Assert.Equal(2, world.Season);
        Assert.True(play.CupCompleteAtSeasonEnd);

        int nextSeasonLeagueFixtures = 0;
        foreach (WorldLeague league in world.Leagues)
        {
            Assert.Equal(ClubsPerLeague, league.Clubs.Count);
            Assert.Equal(FixturesPerLeague, league.Fixtures.Count);
            nextSeasonLeagueFixtures += league.Fixtures.Count;

            var matchdays = new int[WorldCalendar.LeagueMatchdaysPerSeason + 1];
            foreach (WorldFixture fixture in league.Fixtures)
            {
                Assert.False(fixture.IsPlayed);
                Assert.Equal(2, fixture.Season);
                Assert.InRange(fixture.Week, WorldCalendar.LeagueFirstHalfFirstWeek, WorldCalendar.LeagueSecondHalfLastWeek);
                matchdays[fixture.LeagueMatchday]++;
            }

            for (int matchday = 1; matchday <= WorldCalendar.LeagueMatchdaysPerSeason; matchday++)
            {
                Assert.Equal(9, matchdays[matchday]);
            }
        }

        Assert.Equal(LeagueMatchesPerSeason, nextSeasonLeagueFixtures);

        // A fresh cup with only its preliminary round drawn, and no champion carried over.
        Assert.Equal(2, world.Cup.Season);
        Assert.False(world.Cup.IsComplete);
        Assert.False(world.Cup.ChampionClubId.IsValid);
        Assert.Single(world.Cup.Rounds);
        CupRoundBracket preliminary = world.Cup.Rounds[0];
        Assert.Equal(CupRound.Preliminary, preliminary.Round);
        Assert.Equal(18, preliminary.Ties.Count);
        Assert.All(preliminary.Ties, tie => Assert.False(tie.Fixture.IsPlayed));

        // --- Every squad is playable and every contract belongs to the new season -----------------
        foreach (WorldClub club in world.Clubs.All)
        {
            Assert.True(club.Squad.Count >= 18, $"club {club.Name} has only {club.Squad.Count} players");

            int available = 0;
            foreach (PlayerId playerId in club.Squad)
            {
                WorldPlayer player = world.Players[playerId];
                Assert.Equal(club.Id, player.ClubId);
                Assert.False(player.IsRetired);
                Assert.True(player.Attributes.IsValid());
                Assert.True(player.Contract.EndSeason >= 2,
                    $"player {player.Id} at {club.Name} has expired contract {player.Contract.EndSeason}");
                if (player.IsAvailableForSelection) available++;
            }

            Assert.True(available >= 16, $"club {club.Name} has only {available} available players");
        }

        // --- Budgets were refreshed for the new season --------------------------------------------
        foreach (WorldClub club in world.Clubs.All)
        {
            Assert.True(club.Finances.WeeklyWageBudget >= 1L);
            Assert.True(club.Finances.TransferBudget >= 0L);
            Assert.True(club.Finances.WeeklyWageBill >= 0L);
        }

        // --- Division moves: the summary exposes the runner's promotion/relegation ledger ----------
        Assert.NotEmpty(rollover.DivisionMoves);
        Assert.Equal(play.Summary.DivisionMoves.Count, rollover.DivisionMoves.Count);
        foreach (DivisionMove move in rollover.DivisionMoves)
        {
            Assert.Equal(1, Math.Abs(move.ToTier - move.FromTier));
            Assert.InRange(move.FromTier, 1, 6);
            Assert.InRange(move.ToTier, 1, 6);
            Assert.Equal(move.ToTier, world.Clubs[move.ClubId].Tier);
        }

        _output.WriteLine(
            $"rollover: aged {play.PlayerCountAtSeasonEnd} players, {rollover.Retirements} retirements, " +
            $"{rollover.YouthIntakes} youth intakes, {rollover.ManagerChanges} manager changes, " +
            $"{rollover.DivisionMoves.Count} division moves; next season {nextSeasonLeagueFixtures} " +
            $"fixtures + cup preliminary; population {rollover.Population}, mean age {rollover.MeanAge:0.00}");
    }

    [Fact]
    public void Rollover_RejectsAWorldThatIsStillInTheCompetitiveSeason()
    {
        WorldState world = WorldGenerator.Generate(Pack, DefaultSeed);
        var ecosystem = new WorldEcosystem(world);
        ecosystem.EnsureInitialized();

        Assert.Throws<ArgumentNullException>(() => ecosystem.Rollover(null!));

        // Week 1 (fresh world) and week 5 (a competitive match week) both refuse the rollover.
        Assert.Throws<InvalidOperationException>(() => ecosystem.Rollover(new SeasonSummary { Season = 1 }));

        world.Date = new WorldDate(1, WorldCalendar.LeagueFirstHalfFirstWeek);
        Assert.Throws<InvalidOperationException>(() => ecosystem.Rollover(new SeasonSummary { Season = 1 }));

        world.Date = new WorldDate(1, WorldCalendar.LeagueSecondHalfLastWeek);
        Assert.Throws<InvalidOperationException>(() => ecosystem.Rollover(new SeasonSummary { Season = 1 }));
    }

    // =============================================================================================
    // 4. Chained seasons.
    // =============================================================================================

    [Fact]
    public void PlaySeasonWithRollover_ChainsTwoSeasons()
    {
        TwoSeasonChain chain = Chain.Value;
        WorldState world = chain.World;

        Assert.Equal(2, chain.Summaries.Count);
        Assert.Equal(2, chain.Rollovers.Count);

        for (int index = 0; index < chain.Summaries.Count; index++)
        {
            SeasonSummary summary = chain.Summaries[index];
            Assert.Equal(index + 1, summary.Season);
            Assert.Equal(LeagueMatchesPerSeason, summary.LeagueMatchesPlayed);
            Assert.Equal(CupTiesPerSeason, summary.CupTiesPlayed);
            Assert.True(summary.CupChampionClubId.IsValid);

            SeasonRolloverSummary rollover = chain.Rollovers[index];
            Assert.Equal(index + 1, rollover.Season);
            Assert.InRange(rollover.Population, 2200, 3200);
            Assert.InRange(rollover.MeanAge, 18.0, 40.0);
            Assert.True(rollover.YouthIntakes >= ClubsInWorld);
        }

        // Two rollovers ran, so the world is now in its third pre-season with fresh competitions.
        Assert.Equal(new WorldDate(3, WorldCalendar.PreSeasonFirstWeek), world.Date);
        Assert.Equal(3, world.Cup.Season);
        Assert.Single(world.Cup.Rounds);
        Assert.False(world.Cup.IsComplete);

        int fixtures = 0;
        foreach (WorldLeague league in world.Leagues)
        {
            Assert.Equal(ClubsPerLeague, league.Clubs.Count);
            fixtures += league.Fixtures.Count;
            Assert.All(league.Fixtures, fixture => Assert.False(fixture.IsPlayed));
        }

        Assert.Equal(LeagueMatchesPerSeason, fixtures);

        // The ecosystem kept running: the log grew across both seasons and every player is still valid.
        Assert.True(world.TransferLog.Count > 200);
        Assert.True(world.CountTransfers(TransferKind.YouthIntake) > 2 * ClubsInWorld);
        Assert.True(world.CountTransfers(TransferKind.Permanent) > 0);
        foreach (WorldPlayer player in world.Players.All)
        {
            Assert.True(player.Attributes.IsValid(), $"player {player.Id} has invalid attributes");
        }

        _output.WriteLine(
            $"two-season chain: champions {string.Join(", ", chain.Summaries.Select((s, i) => $"S{i + 1} " + world.Clubs[s.CupChampionClubId].Name))} cup; " +
            $"{world.TransferLog.Count} log entries, {world.Players.Count} registry rows, " +
            $"{chain.Rollovers[0].Population} -> {chain.Rollovers[1].Population} active population");
    }

    // =============================================================================================
    // 5. World-health metrics.
    // =============================================================================================

    [Fact]
    public void TierMeanOverall_IsOrderedAndMatchesTheSquadMeans()
    {
        WorldState world = Fresh.Value;
        var ecosystem = new WorldEcosystem(world);

        var means = new float[LeaguesInWorld + 1];
        for (int tier = 1; tier <= LeaguesInWorld; tier++)
        {
            means[tier] = ecosystem.TierMeanOverall(tier);
            Assert.True(float.IsFinite(means[tier]));
            Assert.InRange(means[tier], AttributeMetadata.MinimumValue, 99f);

            // Agrees with an independent computation over the clubs currently in the tier.
            float clubSum = 0f;
            int clubCount = 0;
            foreach (WorldClub club in world.Clubs.All)
            {
                if (club.Tier != tier) continue;
                float playerSum = 0f;
                foreach (PlayerId playerId in club.Squad)
                {
                    playerSum += world.Players[playerId].Attributes.MeanCurrent();
                }

                clubSum += playerSum / club.Squad.Count;
                clubCount++;
            }

            Assert.Equal(ClubsPerLeague, clubCount);
            Assert.Equal(clubSum / clubCount, means[tier], 4);
        }

        // The pyramid separates cleanly: the top flight outclasses the third tier, which outclasses the
        // amateur division, and the full ordering is non-increasing down the pyramid.
        Assert.True(means[1] > means[3], $"tier 1 ({means[1]:0.0}) must outclass tier 3 ({means[3]:0.0})");
        Assert.True(means[3] > means[6], $"tier 3 ({means[3]:0.0}) must outclass tier 6 ({means[6]:0.0})");
        for (int tier = 1; tier < LeaguesInWorld; tier++)
        {
            Assert.True(
                means[tier] > means[tier + 1],
                $"tier {tier} ({means[tier]:0.0}) must outclass tier {tier + 1} ({means[tier + 1]:0.0})");
        }

        // Population and age helpers agree with the registry.
        int active = 0;
        long ageSum = 0L;
        foreach (WorldPlayer player in world.Players.All)
        {
            if (player.IsRetired) continue;
            active++;
            ageSum += player.Age;
        }

        Assert.Equal(active, ecosystem.ActivePopulation());
        Assert.Equal(ageSum / (double)active, ecosystem.MeanActiveAge(), 10);

        Assert.Throws<ArgumentOutOfRangeException>(() => ecosystem.TierMeanOverall(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ecosystem.TierMeanOverall(7));

        _output.WriteLine(
            "generated world tier means: " +
            string.Join(", ", Enumerable.Range(1, LeaguesInWorld).Select(tier => $"T{tier} {means[tier]:0.0}")) +
            $"; active population {active}, mean age {ecosystem.MeanActiveAge():0.00}");
    }

    // =============================================================================================
    // 6. Determinism.
    // =============================================================================================

    [Fact]
    public void PlaySeasonWithRollover_IsDeterministicForTheSameSeed()
    {
        EcosystemSeason expected = Season.Value;
        EcosystemSeason actual = SeasonAgain.Value;

        AssertSameSummary(expected.Summary, actual.Summary);
        AssertSameRollover(expected.Rollover, actual.Rollover);
        AssertSameTables(expected.Summary, actual.Summary);

        // The cup champion and the whole season's market/lifecycle ledger are identical.
        Assert.Equal(expected.Summary.CupChampionClubId, actual.Summary.CupChampionClubId);
        Assert.Equal(expected.Summary.TopScorerId, actual.Summary.TopScorerId);
        Assert.Equal(expected.Summary.TopScorerGoals, actual.Summary.TopScorerGoals);

        Assert.Equal(expected.World.TransferLog.Count, actual.World.TransferLog.Count);
        for (int index = 0; index < expected.World.TransferLog.Count; index++)
        {
            Assert.Equal(expected.World.TransferLog[index], actual.World.TransferLog[index]);
        }

        // Sample of player state after the rollover (ages, contracts, attributes, archived history).
        Assert.Equal(expected.World.Players.Count, actual.World.Players.Count);
        for (int index = 0; index < expected.World.Players.Count; index += 37)
        {
            WorldPlayer left = expected.World.Players.All[index];
            WorldPlayer right = actual.World.Players.All[index];
            Assert.Equal(left.Id, right.Id);
            Assert.Equal(left.Age, right.Age);
            Assert.Equal(left.IsRetired, right.IsRetired);
            Assert.Equal(left.ClubId, right.ClubId);
            Assert.Equal(left.Contract.EndSeason, right.Contract.EndSeason);
            Assert.Equal(left.Contract.WeeklyWage, right.Contract.WeeklyWage);
            Assert.Equal(left.SeasonHistory.Count, right.SeasonHistory.Count);
            Assert.Equal(BitConverter.SingleToInt32Bits(left.Visibility01), BitConverter.SingleToInt32Bits(right.Visibility01));
            for (int attribute = 0; attribute < AttributeMetadata.Count; attribute++)
            {
                AttributeId id = (AttributeId)attribute;
                Assert.Equal(
                    BitConverter.SingleToInt32Bits(left.Attributes.Current(id)),
                    BitConverter.SingleToInt32Bits(right.Attributes.Current(id)));
            }
        }

        // Same next-season fixture list and cup draw (their draws are deterministic too).
        for (int leagueIndex = 0; leagueIndex < LeaguesInWorld; leagueIndex++)
        {
            IReadOnlyList<WorldFixture> left = expected.World.Leagues[leagueIndex].Fixtures;
            IReadOnlyList<WorldFixture> right = actual.World.Leagues[leagueIndex].Fixtures;
            Assert.Equal(left.Count, right.Count);
            for (int fixtureIndex = 0; fixtureIndex < left.Count; fixtureIndex++)
            {
                Assert.Equal(left[fixtureIndex].Id, right[fixtureIndex].Id);
                Assert.Equal(left[fixtureIndex].Week, right[fixtureIndex].Week);
                Assert.Equal(left[fixtureIndex].HomeClubId, right[fixtureIndex].HomeClubId);
                Assert.Equal(left[fixtureIndex].AwayClubId, right[fixtureIndex].AwayClubId);
            }
        }

        Assert.Equal(expected.World.Cup.Season, actual.World.Cup.Season);
        Assert.Equal(expected.World.Cup.Rounds.Count, actual.World.Cup.Rounds.Count);
        Assert.Equal(expected.World.Cup.ChampionClubId, actual.World.Cup.ChampionClubId);

        _output.WriteLine(
            $"determinism: identical season {expected.Summary.Season} tables/champions, cup champion " +
            $"{expected.World.Clubs[expected.Summary.CupChampionClubId].Name}, top scorer " +
            $"{expected.World.Players[expected.Summary.TopScorerId].DisplayName} " +
            $"({expected.Summary.TopScorerGoals}), {expected.World.TransferLog.Count} transfer-log entries " +
            $"and {expected.World.Players.Count} registry rows");
    }

    // =============================================================================================
    // Helpers.
    // =============================================================================================

    /// <summary>Reads a stream's state, treating an unused stream as state 0 (both sides are compared alike).</summary>
    private static ulong StateOf(IReadOnlyDictionary<WorldRandomStream, ulong> states, WorldRandomStream stream) =>
        states.TryGetValue(stream, out ulong state) ? state : 0UL;

    private static void AssertFiniteTierMeans(SeasonRolloverSummary rollover)
    {
        float[] means =
        {
            rollover.Tier1MeanOverall, rollover.Tier2MeanOverall, rollover.Tier3MeanOverall,
            rollover.Tier4MeanOverall, rollover.Tier5MeanOverall, rollover.Tier6MeanOverall
        };

        for (int index = 0; index < means.Length; index++)
        {
            Assert.True(float.IsFinite(means[index]), $"tier {index + 1} mean overall is not finite");
            Assert.InRange(means[index], AttributeMetadata.MinimumValue, 99f);
        }

        Assert.True(means[0] > means[4] && means[4] > means[5],
            "tier means do not separate at the pyramid's ends");
    }

    private static void AssertSameContracts(ContractSnapshot expected, ContractSnapshot actual)
    {
        Assert.Equal(expected.EndSeasons, actual.EndSeasons);
        Assert.Equal(expected.Wages, actual.Wages);
        Assert.Equal(expected.SignedSeasons, actual.SignedSeasons);
        Assert.Equal(expected.Balances, actual.Balances);
        Assert.Equal(expected.WageBills, actual.WageBills);
        Assert.Equal(expected.WageBudgets, actual.WageBudgets);
        Assert.Equal(expected.TransferBudgets, actual.TransferBudgets);
    }

    private static void AssertSameSummary(SeasonSummary expected, SeasonSummary actual)
    {
        Assert.Equal(expected.Season, actual.Season);
        Assert.Equal(expected.LeagueMatchesPlayed, actual.LeagueMatchesPlayed);
        Assert.Equal(expected.LeagueGoals, actual.LeagueGoals);
        Assert.Equal(expected.GoalsPerMatch, actual.GoalsPerMatch);
        Assert.Equal(expected.CupTiesPlayed, actual.CupTiesPlayed);
        Assert.Equal(expected.CupChampionClubId, actual.CupChampionClubId);
        Assert.Equal(expected.TopScorerId, actual.TopScorerId);
        Assert.Equal(expected.TopScorerGoals, actual.TopScorerGoals);
        Assert.Equal(expected.InjuriesThisSeason, actual.InjuriesThisSeason);
        Assert.Equal(expected.YellowCardsThisSeason, actual.YellowCardsThisSeason);
        Assert.Equal(expected.RedCardsThisSeason, actual.RedCardsThisSeason);
        Assert.Equal(expected.ChampionsByTier, actual.ChampionsByTier);
        Assert.Equal(expected.DivisionMoves, actual.DivisionMoves);

        Assert.Equal(
            expected.PlayoffWinnerWasHigherDivisionClub.Count,
            actual.PlayoffWinnerWasHigherDivisionClub.Count);
        for (int boundaryTier = 1; boundaryTier <= 5; boundaryTier++)
        {
            Assert.Equal(
                expected.PlayoffWinnerWasHigherDivisionClub[boundaryTier],
                actual.PlayoffWinnerWasHigherDivisionClub[boundaryTier]);
        }
    }

    private static void AssertSameRollover(SeasonRolloverSummary expected, SeasonRolloverSummary actual)
    {
        Assert.Equal(expected.Season, actual.Season);
        Assert.Equal(expected.Retirements, actual.Retirements);
        Assert.Equal(expected.YouthIntakes, actual.YouthIntakes);
        Assert.Equal(expected.Population, actual.Population);
        Assert.Equal(expected.MeanAge, actual.MeanAge, 10);
        Assert.Equal(expected.Tier1MeanOverall, actual.Tier1MeanOverall);
        Assert.Equal(expected.Tier2MeanOverall, actual.Tier2MeanOverall);
        Assert.Equal(expected.Tier3MeanOverall, actual.Tier3MeanOverall);
        Assert.Equal(expected.Tier4MeanOverall, actual.Tier4MeanOverall);
        Assert.Equal(expected.Tier5MeanOverall, actual.Tier5MeanOverall);
        Assert.Equal(expected.Tier6MeanOverall, actual.Tier6MeanOverall);
        Assert.Equal(expected.Transfers, actual.Transfers);
        Assert.Equal(expected.ManagerChanges, actual.ManagerChanges);
        Assert.Equal(expected.InjuriesRecovered, actual.InjuriesRecovered);
        Assert.Equal(expected.DivisionMoves, actual.DivisionMoves);
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
                Assert.Equal(left[position].ClubId, right[position].ClubId);
                Assert.Equal(left[position].Played, right[position].Played);
                Assert.Equal(left[position].Won, right[position].Won);
                Assert.Equal(left[position].Drawn, right[position].Drawn);
                Assert.Equal(left[position].Lost, right[position].Lost);
                Assert.Equal(left[position].GoalsFor, right[position].GoalsFor);
                Assert.Equal(left[position].GoalsAgainst, right[position].GoalsAgainst);
                Assert.Equal(left[position].Points, right[position].Points);
            }
        }
    }

    /// <summary>A comparable snapshot of every cheap-to-read piece of world state (for "no mutation" checks).</summary>
    private sealed record WorldSnapshot(
        WorldDate Date,
        int PlayerCount,
        int TransferLogCount,
        long BalanceSum,
        int PlayedFixtures,
        int CupRounds,
        int CupTies,
        int ManagerMatches,
        int SampleAge,
        int SampleContractEnd,
        float SampleVisibility,
        int SampleFixtureId)
    {
        public static WorldSnapshot Capture(WorldState world)
        {
            WorldPlayer sample = world.Players.All[0];
            WorldManager sampleManager = world.Managers.All[0];
            WorldFixture sampleFixture = world.Leagues[0].Fixtures[0];

            int playedFixtures = 0;
            int cupTies = 0;
            foreach (WorldLeague league in world.Leagues)
            {
                foreach (WorldFixture fixture in league.Fixtures)
                {
                    if (fixture.IsPlayed) playedFixtures++;
                }
            }

            foreach (CupRoundBracket round in world.Cup.Rounds)
            {
                cupTies += round.Ties.Count;
            }

            long balanceSum = 0L;
            foreach (WorldClub club in world.Clubs.All) balanceSum += club.Finances.Balance;

            return new WorldSnapshot(
                world.Date,
                world.Players.Count,
                world.TransferLog.Count,
                balanceSum,
                playedFixtures,
                world.Cup.Rounds.Count,
                cupTies,
                sampleManager.MatchesInCharge,
                sample.Age,
                sample.Contract.EndSeason,
                sample.Visibility01,
                sampleFixture.Id.Value);
        }
    }

    /// <summary>A comparable snapshot of every contract and budget (for idempotence checks).</summary>
    private sealed record ContractSnapshot(
        int[] EndSeasons,
        long[] Wages,
        int[] SignedSeasons,
        long[] Balances,
        long[] WageBills,
        long[] WageBudgets,
        long[] TransferBudgets)
    {
        public static ContractSnapshot Capture(WorldState world)
        {
            var endSeasons = new int[world.Players.Count];
            var wages = new long[world.Players.Count];
            var signedSeasons = new int[world.Players.Count];
            for (int index = 0; index < world.Players.Count; index++)
            {
                WorldPlayer player = world.Players.All[index];
                endSeasons[index] = player.Contract.EndSeason;
                wages[index] = player.Contract.WeeklyWage;
                signedSeasons[index] = player.Contract.SignedSeason;
            }

            var balances = new long[world.Clubs.Count];
            var wageBills = new long[world.Clubs.Count];
            var wageBudgets = new long[world.Clubs.Count];
            var transferBudgets = new long[world.Clubs.Count];
            for (int index = 0; index < world.Clubs.Count; index++)
            {
                WorldClub club = world.Clubs.All[index];
                balances[index] = club.Finances.Balance;
                wageBills[index] = club.Finances.WeeklyWageBill;
                wageBudgets[index] = club.Finances.WeeklyWageBudget;
                transferBudgets[index] = club.Finances.TransferBudget;
            }

            return new ContractSnapshot(
                endSeasons, wages, signedSeasons, balances, wageBills, wageBudgets, transferBudgets);
        }
    }

    /// <summary>One generated world played through the ecosystem for a season and rolled over, with
    /// snapshots taken on both sides of the rollover.</summary>
    private sealed class EcosystemSeason
    {
        private EcosystemSeason()
        {
        }

        public WorldState World { get; private init; } = null!;

        public WorldEcosystem Ecosystem { get; private init; } = null!;

        public SeasonSummary Summary { get; private init; } = null!;

        public SeasonRolloverSummary Rollover { get; private init; } = null!;

        public int WeekAtSeasonEnd { get; private init; }

        public bool CupCompleteAtSeasonEnd { get; private init; }

        public int PlayedLeagueFixturesAtSeasonEnd { get; private init; }

        public int PlayerCountAtSeasonEnd { get; private init; }

        public float[] VisibilityAtSeasonStart { get; private init; } = Array.Empty<float>();

        public float[] VisibilityAtSeasonEnd { get; private init; } = Array.Empty<float>();

        public long[] BalancesAtSeasonStart { get; private init; } = Array.Empty<long>();

        public long[] BalancesAtSeasonEnd { get; private init; } = Array.Empty<long>();

        public int[] AgesAtSeasonEnd { get; private init; } = Array.Empty<int>();

        public int[] AppearancesAtSeasonEnd { get; private init; } = Array.Empty<int>();

        public int[] GoalsAtSeasonEnd { get; private init; } = Array.Empty<int>();

        /// <summary>The played season's fixture lists, captured before the rollover regenerates them.</summary>
        public WorldFixture[][] PlayedLeagueFixtures { get; private init; } = Array.Empty<WorldFixture[]>();

        /// <summary>Quick-sim stream state at season end, before the rollover creates next season's cup.</summary>
        public ulong QuickSimStateAtSeasonEnd { get; private init; }

        /// <summary>Cup-draw stream state at season end, before the rollover creates next season's cup.</summary>
        public ulong CupStateAtSeasonEnd { get; private init; }

        public static EcosystemSeason Play(ulong seed)
        {
            WorldState world = WorldGenerator.Generate(FullPack.Value, seed);
            var ecosystem = new WorldEcosystem(world);

            int startCount = world.Players.Count;
            var visibilityAtStart = new float[startCount];
            for (int index = 0; index < startCount; index++)
            {
                visibilityAtStart[index] = world.Players.All[index].Visibility01;
            }

            var balancesAtStart = new long[world.Clubs.Count];
            for (int index = 0; index < world.Clubs.Count; index++)
            {
                balancesAtStart[index] = world.Clubs.All[index].Finances.Balance;
            }

            SeasonSummary summary = ecosystem.PlaySeason();

            // The runner's match streams are snapshotted before the rollover, because the rollover itself
            // draws next season's cup: the comparison must isolate the season loop.
            IReadOnlyDictionary<WorldRandomStream, ulong> statesAtSeasonEnd = world.CaptureRngStates();
            ulong quickSimStateAtSeasonEnd =
                statesAtSeasonEnd.TryGetValue(WorldRandomStream.QuickSim, out ulong quickSimState) ? quickSimState : 0UL;
            ulong cupStateAtSeasonEnd =
                statesAtSeasonEnd.TryGetValue(WorldRandomStream.Cup, out ulong cupState) ? cupState : 0UL;

            int weekAtSeasonEnd = world.Week;
            bool cupComplete = world.Cup.IsComplete;
            int playedLeagueFixtures = 0;
            var playedFixturesByLeague = new WorldFixture[world.Leagues.Count][];
            for (int leagueIndex = 0; leagueIndex < world.Leagues.Count; leagueIndex++)
            {
                WorldLeague league = world.Leagues[leagueIndex];
                var playedFixtures = new List<WorldFixture>(FixturesPerLeague);
                foreach (WorldFixture fixture in league.Fixtures)
                {
                    if (!fixture.IsPlayed) continue;
                    playedFixtures.Add(fixture);
                    playedLeagueFixtures++;
                }

                playedFixturesByLeague[leagueIndex] = playedFixtures.ToArray();
            }

            int seasonEndCount = world.Players.Count;
            var visibilityAtEnd = new float[startCount];
            for (int index = 0; index < startCount; index++)
            {
                visibilityAtEnd[index] = world.Players.All[index].Visibility01;
            }

            var balancesAtEnd = new long[world.Clubs.Count];
            for (int index = 0; index < world.Clubs.Count; index++)
            {
                balancesAtEnd[index] = world.Clubs.All[index].Finances.Balance;
            }

            var agesAtEnd = new int[seasonEndCount];
            var appearancesAtEnd = new int[seasonEndCount];
            var goalsAtEnd = new int[seasonEndCount];
            for (int index = 0; index < seasonEndCount; index++)
            {
                WorldPlayer player = world.Players.All[index];
                agesAtEnd[index] = player.Age;
                appearancesAtEnd[index] = player.CurrentSeason.Appearances;
                goalsAtEnd[index] = player.CurrentSeason.Goals;
            }

            SeasonRolloverSummary rollover = ecosystem.Rollover(summary);

            return new EcosystemSeason
            {
                World = world,
                Ecosystem = ecosystem,
                Summary = summary,
                Rollover = rollover,
                WeekAtSeasonEnd = weekAtSeasonEnd,
                CupCompleteAtSeasonEnd = cupComplete,
                PlayedLeagueFixturesAtSeasonEnd = playedLeagueFixtures,
                PlayerCountAtSeasonEnd = seasonEndCount,
                VisibilityAtSeasonStart = visibilityAtStart,
                VisibilityAtSeasonEnd = visibilityAtEnd,
                BalancesAtSeasonStart = balancesAtStart,
                BalancesAtSeasonEnd = balancesAtEnd,
                AgesAtSeasonEnd = agesAtEnd,
                AppearancesAtSeasonEnd = appearancesAtEnd,
                GoalsAtSeasonEnd = goalsAtEnd,
                PlayedLeagueFixtures = playedFixturesByLeague,
                QuickSimStateAtSeasonEnd = quickSimStateAtSeasonEnd,
                CupStateAtSeasonEnd = cupStateAtSeasonEnd
            };
        }
    }

    /// <summary>The same seed played by the bare W2 runner (no weekly ecosystem hook).</summary>
    private sealed class BarePlaythrough
    {
        private BarePlaythrough(WorldState world, SeasonSummary summary)
        {
            World = world;
            Summary = summary;
        }

        public WorldState World { get; }

        public SeasonSummary Summary { get; }

        public static BarePlaythrough Play(ulong seed)
        {
            WorldState world = WorldGenerator.Generate(FullPack.Value, seed);
            return new BarePlaythrough(world, WorldSeasonRunner.RunSeason(world));
        }
    }

    /// <summary>One world advanced through two complete ecosystem seasons.</summary>
    private sealed class TwoSeasonChain
    {
        private TwoSeasonChain(WorldState world, List<SeasonSummary> summaries, List<SeasonRolloverSummary> rollovers)
        {
            World = world;
            Summaries = summaries;
            Rollovers = rollovers;
        }

        public WorldState World { get; }

        public List<SeasonSummary> Summaries { get; }

        public List<SeasonRolloverSummary> Rollovers { get; }

        public static TwoSeasonChain Play(ulong seed)
        {
            WorldState world = WorldGenerator.Generate(FullPack.Value, seed);
            var ecosystem = new WorldEcosystem(world);
            var summaries = new List<SeasonSummary>(2);
            var rollovers = new List<SeasonRolloverSummary>(2);

            for (int season = 0; season < 2; season++)
            {
                SeasonSummary summary = ecosystem.PlaySeasonWithRollover(out SeasonRolloverSummary rollover);
                summaries.Add(summary);
                rollovers.Add(rollover);
            }

            return new TwoSeasonChain(world, summaries, rollovers);
        }
    }
}
