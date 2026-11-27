using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FootballSim;
using FootballSim.World.Bridge;
using FootballSim.World.Career;
using FootballSim.Core;
using FootballSim.World.Core;
using FootballSim.World.Players;
using FootballSim.World.Simulation;
using FootballSim.World.World;
using Xunit;

namespace FootballSim.World.Tests.Career;

/// <summary>
/// W7 gate tests for the live matchday handoff: a career matchday prepared by the session, played in the real
/// 60 Hz engine through the bridge, committed back into the career, applied by the next advance (standings,
/// stats, condition and the growth tick) and autosaved/reloaded.
/// </summary>
public sealed class CareerLiveMatchTests
{
    [Fact]
    public void CareerMatchday_PlayedInTheRealEngine_IsAppliedToTheWorldAndCareer()
    {
        CareerSession session = CareerTestHarness.CreateCareer();
        CareerAdvanceResult matchday = CareerTestHarness.AdvanceUntil(
            session, result => result.Kind == CareerEventKind.HumanMatchday);
        WorldFixture fixture = matchday.Fixture!;
        WorldClub club = session.HumanClub!;
        WorldLeague league = session.World.LeagueForTier(club.Tier);
        int playedBefore = StandingsCalculator.Calculate(league).Sum(row => row.Played);
        int appearancesBefore = session.HumanPlayer.CurrentSeason.Appearances;
        int formCountBefore = session.HumanPlayer.Form.Appearances.Count;
        var attributesBefore = new float[18];
        for (int attribute = 0; attribute < attributesBefore.Length; attribute++)
        {
            attributesBefore[attribute] = session.HumanPlayer.Attributes.Current((AttributeId)attribute);
        }

        // Force the human into the XI so the live segment guarantees a personal match line (a bench start is
        // the separate cameo path, covered by the bridge's forced-starter test below).
        WorldMatchSetup setup = session.PrepareHumanLiveMatchSetup(halfDurationSeconds: 20f, forceHumanStarter: true);
        Assert.Equal(fixture.Id, setup.FixtureId);
        Assert.NotNull(setup.HumanEnginePlayerId);
        Assert.True(setup.IsCupTie == fixture.IsCupTie);

        MatchSimulationEngine engine = WorldMatchBridge.CreateEngine(setup);
        WorldMatchBridge.RunToFullTime(engine);
        Assert.Equal(MatchPhase.FullTime, engine.Rules.Phase);

        CareerMatchResult result = session.CommitHumanLiveMatchEngine(engine, setup);
        Assert.Equal(fixture.Id, result.FixtureId);
        Assert.NotNull(result.HumanLine);
        Assert.InRange(result.HumanLine!.Rating, 4.0f, 10.0f);
        Assert.True(result.HumanLine.Minutes > 0);

        // The result is only queued: the world is untouched until the next advance applies it.
        Assert.False(fixture.IsPlayed);
        Assert.True(session.HasQueuedMatchResult);
        Assert.False(session.CanSave);

        session.AdvanceToNextEventOrMatchday();

        Assert.True(fixture.IsPlayed);
        Assert.Equal(result.HomeGoals, fixture.HomeGoals);
        Assert.Equal(result.AwayGoals, fixture.AwayGoals);
        int playedAfter = StandingsCalculator.Calculate(league).Sum(row => row.Played);
        Assert.True(playedAfter > playedBefore, "The live match must appear in the league table.");

        // Appearance, condition and the growth tick all see the match.
        Assert.True(session.HumanPlayer.CurrentSeason.Appearances > appearancesBefore);
        Assert.True(session.HumanPlayer.CurrentSeason.MinutesPlayed >= result.HumanLine.Minutes);
        // The match cost is applied: the week's condition ends at or below the live line's stamina (week 5 can
        // also contain a cup tie the runner quick-sims after the live match).
        Assert.InRange(session.HumanPlayer.Condition01, 0f, result.HumanLine.EndingStamina01 + 0.001f);
        Assert.True(session.HumanPlayer.Form.Appearances.Count > formCountBefore);

        // One growth tick ran for the week (match minutes accelerate skill growth); at least one current value
        // must have moved, and no attribute may leave its valid range.
        bool anyAttributeMoved = false;
        for (int attribute = 0; attribute < attributesBefore.Length; attribute++)
        {
            float after = session.HumanPlayer.Attributes.Current((AttributeId)attribute);
            Assert.InRange(after, 1f, 99f);
            if (MathF.Abs(after - attributesBefore[attribute]) > 0.0001f) anyAttributeMoved = true;
        }

        Assert.True(anyAttributeMoved, "The weekly growth tick should have moved at least one attribute.");
        Assert.True(session.CanSave);
    }

    [Fact]
    public void CareerMatchday_SaveAndReload_ContinuesIdentically()
    {
        CareerSession session = CareerTestHarness.CreateCareer();
        CareerTestHarness.AdvanceUntil(session, result => result.Kind == CareerEventKind.HumanMatchday);
        WorldMatchSetup setup = session.PrepareHumanLiveMatchSetup(15f, forceHumanStarter: true);
        MatchSimulationEngine engine = WorldMatchBridge.CreateEngine(setup);
        WorldMatchBridge.RunToFullTime(engine);
        session.CommitHumanLiveMatchEngine(engine, setup);
        session.AdvanceToNextEventOrMatchday();

        string beforeSave = CareerTestHarness.Fingerprint(session);
        string path = Path.Combine(Path.GetTempPath(), $"career-w7-{Guid.NewGuid():N}.json");
        try
        {
            SaveManager.Save(session, path);
            Assert.True(File.Exists(path));

            CareerSession reloaded = SaveManager.Load(path);
            Assert.Equal(session.HumanPlayerId, reloaded.HumanPlayerId);
            Assert.Equal(beforeSave, CareerTestHarness.Fingerprint(reloaded));
            Assert.True(reloaded.CanSave);

            // Both careers then advance the same two weeks with the same handling and must stay identical.
            CareerTestHarness.AdvanceWeeks(session, 2);
            CareerTestHarness.AdvanceWeeks(reloaded, 2);
            Assert.Equal(CareerTestHarness.Fingerprint(session), CareerTestHarness.Fingerprint(reloaded));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void BenchCameo_ForcedStarter_PutsTheHumanOnThePitch()
    {
        CareerSession session = CareerTestHarness.CreateCareer();
        CareerTestHarness.AdvanceUntil(session, result => result.Kind == CareerEventKind.HumanMatchday);

        WorldMatchSetup natural = session.PrepareHumanLiveMatchSetup(10f);
        bool naturalStarter = natural.HumanEnginePlayerId is not null;
        session.DiscardLiveMatch();

        WorldMatchSetup forced = session.PrepareHumanLiveMatchSetup(10f, forceHumanStarter: true);
        Assert.NotNull(forced.HumanEnginePlayerId);
        int shirtIndex = forced.HumanEnginePlayerId!.Value;
        IReadOnlyList<PlayerId> shirts = shirtIndex <= 11 ? forced.HomeShirtPlayers : forced.AwayShirtPlayers;
        Assert.Equal(session.HumanPlayerId, shirts[(shirtIndex - 1) % 11]);

        MatchSimulationEngine engine = WorldMatchBridge.CreateEngine(forced);
        WorldMatchBridge.RunToFullTime(engine);
        CareerMatchResult result = session.CommitHumanLiveMatchEngine(engine, forced);
        Assert.NotNull(result.HumanLine);
        Assert.Equal(session.HumanPlayerId, result.HumanLine!.PlayerId);

        // The forced setup is a normal prepared match: the natural one may have benched him, but the forced
        // one always fields him.
        Assert.True(!naturalStarter || forced.HumanEnginePlayerId == natural.HumanEnginePlayerId);
    }

    [Fact]
    public void DiscardingALiveMatch_LeavesTheCareerSavableAndUnchanged()
    {
        CareerSession session = CareerTestHarness.CreateCareer();
        CareerTestHarness.AdvanceUntil(session, result => result.Kind == CareerEventKind.HumanMatchday);
        string before = CareerTestHarness.Fingerprint(session);

        session.PrepareHumanLiveMatchSetup(10f);
        Assert.False(session.CanSave);
        Assert.True(session.HasPreparedLiveMatch);
        Assert.Throws<InvalidOperationException>(() => session.AdvanceToNextEventOrMatchday());

        session.DiscardLiveMatch();

        Assert.True(session.CanSave);
        Assert.False(session.HasPreparedLiveMatch);
        Assert.Equal(before, CareerTestHarness.Fingerprint(session));
    }

    [Fact]
    public void CommitWithoutAPreparedMatch_Throws()
    {
        CareerSession session = CareerTestHarness.CreateCareer();
        CareerTestHarness.AdvanceUntil(session, result => result.Kind == CareerEventKind.HumanMatchday);

        Assert.Throws<InvalidOperationException>(() =>
        {
            WorldMatchSetup setup = WorldMatchBridge.PrepareMatch(
                session.World, session.HumanFixturesThisWeek()[0], halfDurationSeconds: 10f);
            MatchSimulationEngine engine = WorldMatchBridge.CreateEngine(setup);
            WorldMatchBridge.RunToFullTime(engine);
            session.CommitHumanLiveMatchEngine(engine, setup);
        });
    }
}
