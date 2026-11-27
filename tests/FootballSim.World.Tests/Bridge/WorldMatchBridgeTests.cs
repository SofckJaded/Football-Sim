using System;
using System.Collections.Generic;
using System.Linq;
using FootballSim;
using FootballSim.Core;
using FootballSim.World.Bridge;
using FootballSim.World.Career;
using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Players;
using FootballSim.World.Simulation;
using FootballSim.World.Tests.Career;
using FootballSim.World.World;
using Xunit;

namespace FootballSim.World.Tests.Bridge;

/// <summary>
/// W5 gate tests for the match bridge: a real headless <see cref="MatchSimulationEngine"/> built from two world
/// squads, its per-player report, determinism, cup-tie shoot-outs and the career round trip.
/// </summary>
public sealed class WorldMatchBridgeTests
{
    private const float ShortHalfSeconds = 20f;

    [Fact]
    public void PrepareMatch_BuildsElevenSpecsPerSide_AndLocksTheHuman()
    {
        WorldState world = CreateWorld();
        WorldFixture fixture = FirstLeagueFixture(world);
        SelectedLineup home = SquadSelector.Select(world, world.Clubs[fixture.HomeClubId]);
        SelectedLineup away = SquadSelector.Select(world, world.Clubs[fixture.AwayClubId]);
        PlayerId human = home.StartingXI[5];

        WorldMatchSetup setup = WorldMatchBridge.PrepareMatch(world, fixture, home, away, human);

        Assert.Equal(11, setup.Config.HomeLineup!.Count);
        Assert.Equal(11, setup.Config.AwayLineup!.Count);
        Assert.Equal(11, setup.HomeShirtPlayers.Count);
        Assert.Equal(11, setup.AwayShirtPlayers.Count);
        Assert.Equal(human, setup.HumanPlayerId);
        Assert.NotNull(setup.HumanEnginePlayerId);
        Assert.True(setup.HumanIsHome);
        Assert.Equal(human, setup.HomeShirtPlayers[setup.HumanEnginePlayerId!.Value - 1]);
        Assert.Equal(setup.HumanEnginePlayerId, setup.Config.DesignatedHumanPlayerId);
        Assert.NotNull(setup.HomeProfile);
        Assert.NotNull(setup.AwayProfile);
        Assert.Equal(world.Clubs[fixture.HomeClubId].Name, setup.HomeClubName);
        Assert.Equal(world.Clubs[fixture.AwayClubId].Name, setup.AwayClubName);

        // The keeper shirt is the best keeper in the world XI (primary-role preference included), and every
        // world starter is used exactly once.
        Assert.Equal(11, setup.HomeShirtPlayers.Distinct().Count());
        Assert.Equal(11, setup.AwayShirtPlayers.Distinct().Count());
        float KeeperScore(PlayerId playerId)
        {
            WorldPlayer candidate = world.Players[playerId];
            float score = IdentityEvaluator.Suitability(candidate, WorldRole.Goalkeeper);
            return IdentityEvaluator.Evaluate(candidate).PrimaryRole == WorldRole.Goalkeeper ? score + 12f : score;
        }

        float bestKeeperScore = home.StartingXI.Max(KeeperScore);
        Assert.Equal(bestKeeperScore, KeeperScore(setup.HomeShirtPlayers[0]));

        // The engine's own construction-time validation accepts the lineup hook.
        SimulationConfigValidation.Validate(setup.Config, new PitchDimensions());
    }

    [Fact]
    public void PrepareMatch_UsesTheManagerTacticalProfiles()
    {
        WorldState world = CreateWorld();
        WorldFixture fixture = FirstLeagueFixture(world);
        WorldClub homeClub = world.Clubs[fixture.HomeClubId];
        WorldClub awayClub = world.Clubs[fixture.AwayClubId];

        WorldMatchSetup setup = WorldMatchBridge.PrepareMatch(world, fixture);

        Assert.Equal(world.Managers[homeClub.ManagerId].TacticalProfile.Name, setup.HomeProfile.Name);
        Assert.Equal(world.Managers[awayClub.ManagerId].TacticalProfile.Name, setup.AwayProfile.Name);
        Assert.Null(setup.HumanEnginePlayerId);
        Assert.Null(setup.Config.DesignatedHumanPlayerId);
    }

    [Fact]
    public void Bridge_SimulatesAFullMatch_AndProducesAValidReport()
    {
        WorldState world = CreateWorld();
        WorldFixture fixture = FirstLeagueFixture(world);
        WorldMatchSetup setup = WorldMatchBridge.PrepareMatch(world, fixture, humanPlayerId: null, ShortHalfSeconds);

        LiveMatchReport report = WorldMatchBridge.SimulateHeadless(world, setup);

        Assert.Equal(fixture.Id, report.FixtureId);
        Assert.InRange(report.HomeGoals, 0, 30);
        Assert.InRange(report.AwayGoals, 0, 30);
        Assert.Equal(11, report.HomePlayers.Count);
        Assert.Equal(11, report.AwayPlayers.Count);
        Assert.Empty(report.Injuries); // the engine models no in-match injuries (documented v1 limit)

        foreach (PlayerMatchLine line in report.HomePlayers.Concat(report.AwayPlayers))
        {
            Assert.InRange(line.Rating, 4.0f, 10.0f);
            Assert.InRange(line.EndingStamina01, 0f, 1f);
            Assert.InRange(line.ConditionCost01, 0f, 1f);
            Assert.True(line.Minutes > 0, "Every engine starter plays the whole match.");
            Assert.True(line.Assists <= line.Goals + 3);
            Assert.True(line.Shots >= 0);
        }

        int homeGoalsFromLines = report.HomePlayers.Sum(line => line.Goals);
        int awayGoalsFromLines = report.AwayPlayers.Sum(line => line.Goals);
        Assert.True(homeGoalsFromLines <= report.HomeGoals, "Own goals are not credited to an attacker.");
        Assert.True(awayGoalsFromLines <= report.AwayGoals);
        Assert.InRange(report.HomeStats.PossessionPercent + report.AwayStats.PossessionPercent, 99, 101);
        Assert.True(report.HomeStats.ShotsOnTarget >= report.HomeGoals);
        Assert.True(report.AwayStats.ShotsOnTarget >= report.AwayGoals);
        Assert.True(report.HomeStats.Shots >= report.HomeStats.ShotsOnTarget);
    }

    [Fact]
    public void Bridge_IsDeterministicForTheSameFixtureAndLineups()
    {
        WorldState world = CreateWorld();
        WorldFixture fixture = FirstLeagueFixture(world);

        LiveMatchReport first = WorldMatchBridge.SimulateHeadless(
            world, WorldMatchBridge.PrepareMatch(world, fixture, humanPlayerId: null, 15f));
        LiveMatchReport second = WorldMatchBridge.SimulateHeadless(
            world, WorldMatchBridge.PrepareMatch(world, fixture, humanPlayerId: null, 15f));

        Assert.Equal(first.HomeGoals, second.HomeGoals);
        Assert.Equal(first.AwayGoals, second.AwayGoals);
        Assert.Equal(first.HomePlayers.Count, second.HomePlayers.Count);
        for (int index = 0; index < first.HomePlayers.Count; index++)
        {
            PlayerMatchLine left = first.HomePlayers[index];
            PlayerMatchLine right = second.HomePlayers[index];
            Assert.Equal(left.PlayerId, right.PlayerId);
            Assert.Equal(left.Minutes, right.Minutes);
            Assert.Equal(left.Goals, right.Goals);
            Assert.Equal(left.Assists, right.Assists);
            Assert.Equal(BitConverter.SingleToInt32Bits(left.Rating), BitConverter.SingleToInt32Bits(right.Rating));
            Assert.Equal(
                BitConverter.SingleToInt32Bits(left.EndingStamina01),
                BitConverter.SingleToInt32Bits(right.EndingStamina01));
        }
    }

    [Fact]
    public void Bridge_LevelCupTie_IsSettledByADecisiveShootout()
    {
        WorldState world = CreateWorld();
        CupTie tie = world.Cup.CurrentRound!.Ties[0];
        WorldMatchSetup setup = WorldMatchBridge.PrepareMatch(
            world, tie.Fixture, humanPlayerId: null, halfDurationSeconds: 10f);

        LiveMatchReport report = WorldMatchBridge.SimulateHeadless(world, setup);

        Assert.True(setup.IsCupTie);
        if (report.HomeGoals == report.AwayGoals)
        {
            Assert.True(report.WentToPenalties);
            Assert.NotEqual(report.HomePenaltyGoals, report.AwayPenaltyGoals);
        }
        else
        {
            Assert.False(report.WentToPenalties);
            Assert.Equal(0, report.HomePenaltyGoals + report.AwayPenaltyGoals);
        }

        // A decisive outcome is exactly what WorldFixture.RecordCupResult accepts.
        var fixture = new WorldFixture(
            new MatchId(900_001), world.Season, 10, CompetitionKind.Cup,
            LeagueId.None, 0, tie.Fixture.CupRound, tie.Fixture.HomeClubId, tie.Fixture.AwayClubId);
        fixture.RecordCupResult(
            report.HomeGoals, report.AwayGoals, report.HomePenaltyGoals, report.AwayPenaltyGoals);
        Assert.True(fixture.IsPlayed);
    }

    [Fact]
    public void CareerSession_LiveMatch_UpdatesTheWorldStandingsAndPlayerStats()
    {
        CareerSession session = CareerTestHarness.CreateCareer();
        CareerAdvanceResult matchday = CareerTestHarness.AdvanceUntil(
            session, result => result.Kind == CareerEventKind.HumanMatchday);
        WorldFixture fixture = matchday.Fixture!;
        WorldLeague league = session.World.LeagueForTier(session.HumanClub!.Tier);
        int playedBefore = StandingsCalculator.Calculate(league).Sum(row => row.Played);
        int appsBefore = session.HumanPlayer.CurrentSeason.Appearances;

        CareerMatchResult result = session.SimulateHumanLiveMatch(halfDurationSeconds: 20f);

        Assert.Equal(fixture.Id, result.FixtureId);
        Assert.False(session.HasPreparedLiveMatch);
        Assert.True(session.HasQueuedMatchResult);

        session.AdvanceToNextEventOrMatchday();

        Assert.True(fixture.IsPlayed);
        Assert.Equal(result.HomeGoals, fixture.HomeGoals);
        Assert.Equal(result.AwayGoals, fixture.AwayGoals);
        int playedAfter = StandingsCalculator.Calculate(league).Sum(row => row.Played);
        Assert.True(playedAfter > playedBefore, "The played fixture must appear in the league table.");
        Assert.True(session.CanSave);

        if (result.HumanLine is not null)
        {
            Assert.True(session.HumanPlayer.CurrentSeason.Appearances > appsBefore);
            Assert.True(session.HumanPlayer.CurrentSeason.MinutesPlayed >= result.HumanLine.Minutes);
        }
    }

    [Fact]
    public void CareerSession_LiveMatch_CanBeDiscardedWithoutTouchingTheWorld()
    {
        CareerSession session = CareerTestHarness.CreateCareer();
        CareerTestHarness.AdvanceUntil(session, result => result.Kind == CareerEventKind.HumanMatchday);
        string before = CareerTestHarness.Fingerprint(session);

        session.PrepareHumanLiveMatchSetup(10f);
        Assert.False(session.CanSave);
        Assert.Throws<InvalidOperationException>(() => session.AdvanceToNextEventOrMatchday());

        session.DiscardLiveMatch();
        Assert.True(session.CanSave);
        Assert.Equal(before, CareerTestHarness.Fingerprint(session));
    }

    private static WorldState CreateWorld(ulong seed = 20261127UL)
    {
        WorldPack pack = WorldPackLoader.Load(WorldPackPaths.ResolveDefaultPack());
        WorldState world = WorldGenerator.Generate(pack, new WorldGenerationOptions { Seed = seed });
        new WorldEcosystem(world).EnsureInitialized();
        return world;
    }

    private static WorldFixture FirstLeagueFixture(WorldState world)
    {
        WorldLeague league = world.Leagues[0];
        for (int index = 0; index < league.Fixtures.Count; index++)
        {
            WorldFixture fixture = league.Fixtures[index];
            if (fixture.Week == WorldCalendar.LeagueFirstHalfFirstWeek) return fixture;
        }

        throw new InvalidOperationException("The generated world has no first-week league fixture.");
    }
}
