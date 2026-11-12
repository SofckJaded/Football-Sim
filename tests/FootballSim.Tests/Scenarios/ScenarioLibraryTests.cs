using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Scenarios;
using Xunit;

namespace FootballSim.Tests.Scenarios;

/// <summary>
/// A0.3: the ten fixture families exist, start from the state they describe, and reproduce exactly.
///
/// These are *structural* assertions on purpose. A tactical assertion ("the cover player protects the
/// gap") only means something once decision tracing exists (A0.4); asserting it now would pin the
/// current placeholder behaviour and make the real work look like a regression.
/// </summary>
public sealed class ScenarioLibraryTests
{
    public static TheoryData<string> ScenarioKeys()
    {
        var data = new TheoryData<string>();
        foreach (ScenarioLibrary.Definition definition in ScenarioLibrary.All) data.Add(definition.Key);
        return data;
    }

    private static ScenarioLibrary.Definition Definition(string key) =>
        ScenarioLibrary.All.Single(d => d.Key == key);

    [Fact]
    public void Library_ContainsTheTenRequiredScenarios()
    {
        string[] required =
        {
            "build-up-3v2", "wide-overload-4v3", "cb-step-out", "rear-pressure-reception",
            "high-line-run", "winger-fullback", "counterattack-3v3", "touchline-press",
            "box-cross", "keeper-sweep-race"
        };

        Assert.Equal(10, ScenarioLibrary.All.Count);
        Assert.Equal(10, ScenarioLibrary.All.Select(d => d.Key).Distinct().Count());
        foreach (string key in required)
            Assert.Contains(ScenarioLibrary.All, d => d.Key == key);

        foreach (ScenarioLibrary.Definition definition in ScenarioLibrary.All)
            Assert.False(string.IsNullOrWhiteSpace(definition.Intent), $"{definition.Key} needs a stated intent");
    }

    [Theory]
    [MemberData(nameof(ScenarioKeys))]
    public void Scenario_StartsEveryDeclaredPlayer(string key)
    {
        MatchScenario scenario = Definition(key).Build();
        var engine = new MatchSimulationEngine(scenario);

        Assert.Equal(scenario.Players.Count, engine.Players.Count);
        foreach (ScenarioPlayer spec in scenario.Players)
        {
            FootballerState actual = engine.Players.First(p => p.Id == spec.Id);
            Assert.Equal(spec.Team, actual.Team);
            Assert.Equal(spec.Role, actual.Role);
            Assert.Equal(spec.Position, actual.Position);
            Assert.Equal(spec.Velocity, actual.Velocity);
            Assert.Equal(spec.ShirtNumber, actual.ShirtNumber);
            Assert.True(actual.IsActiveOnPitch);
        }

        Assert.Equal(MatchPhase.FirstHalf, engine.Rules.Phase);
        Assert.Equal(0, engine.CurrentTick);
    }

    [Theory]
    [MemberData(nameof(ScenarioKeys))]
    public void Scenario_BallSetupIsHonouredOnTheFirstTick(string key)
    {
        MatchScenario scenario = Definition(key).Build();
        var engine = new MatchSimulationEngine(scenario);

        engine.StepTick();

        Assert.Equal(scenario.BallSetup switch
        {
            ScenarioBallSetup.Controlled => BallControlState.Controlled,
            ScenarioBallSetup.KeeperHeld => BallControlState.KeeperHeld,
            ScenarioBallSetup.InFlightPass => BallControlState.InFlightPass,
            _ => BallControlState.Loose
        }, engine.Possession.State);

        if (scenario.BallSetup != ScenarioBallSetup.Loose)
            Assert.NotNull(engine.Possession.PossessionTeam);
    }

    [Theory]
    [MemberData(nameof(ScenarioKeys))]
    public void Scenario_ReproducesIdenticallyAcrossTwoFreshEngines(string key)
    {
        MatchScenario scenario = Definition(key).Build();
        ScenarioRunResult first = ScenarioRunner.Run(scenario, 120);
        ScenarioRunResult second = ScenarioRunner.Run(Definition(key).Build(), 120);

        Assert.Equal(first.TicksRun, second.TicksRun);
        Assert.Equal(first.BallPosition, second.BallPosition);
        Assert.Equal(first.Engine.Ball.Velocity, second.Engine.Ball.Velocity);
        Assert.Equal(first.Possession, second.Possession);
        Assert.Equal(first.ContactAttempts.Count, second.ContactAttempts.Count);

        for (int i = 0; i < first.Engine.Players.Count; i++)
        {
            FootballerState a = first.Engine.Players[i];
            FootballerState b = second.Engine.Players[i];
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.Position, b.Position);
            Assert.Equal(a.Velocity, b.Velocity);
            Assert.Equal(a.Stamina01, b.Stamina01);
        }
    }

    [Fact]
    public void Runner_StopsAtItsBudgetAndReportsWhatHappened()
    {
        ScenarioRunResult result = ScenarioRunner.Run(ScenarioLibrary.BuildUpThreeVersusTwo(), 90);

        Assert.Equal(90, result.TicksRun);
        Assert.Equal(90, result.FinalTick);
        Assert.False(result.ReachedFullTime);
        Assert.Single(result.RuleEvents);
        Assert.StartsWith("3v2 build-up", result.Describe());
    }

    [Fact]
    public void Runner_StopsEarlyWhenTheScenarioReachesFullTime()
    {
        MatchScenario scenario = ScenarioLibrary.BuildUpThreeVersusTwo();
        var shortMatch = new MatchScenario
        {
            Name = scenario.Name,
            Seed = scenario.Seed,
            HalfDurationSeconds = 1f,
            BallSetup = scenario.BallSetup,
            BallOwnerPlayerId = scenario.BallOwnerPlayerId,
            BallPosition = scenario.BallPosition,
            BallVelocity = scenario.BallVelocity
        };
        foreach (ScenarioPlayer player in scenario.Players) shortMatch.Add(player);

        ScenarioRunResult result = ScenarioRunner.Run(shortMatch, 600);

        Assert.True(result.ReachedFullTime);
        Assert.Equal(MatchPhase.FullTime, result.Engine.Rules.Phase);
        Assert.True(result.TicksRun < 600);
    }

    [Fact]
    public void RearPressureScenario_StartsWithTheBallInFlightToItsIntendedReceiver()
    {
        var engine = new MatchSimulationEngine(ScenarioLibrary.RearPressureReception());

        Assert.Equal(BallControlState.InFlightPass, engine.Possession.State);
        Assert.Equal(ScenarioLibrary.RearPressureReceiver, engine.Possession.IntendedReceiverId);
        Assert.Equal(TeamId.Home, engine.Possession.PossessionTeam);
        Assert.Equal(ScenarioLibrary.RearPressurePasser, engine.Ball.LastTouchedByPlayerId);

        // The presser is behind the receiver relative to the passer: the receiver cannot see them while
        // facing the ball. That is the whole point of the fixture, so assert it rather than trusting
        // the coordinates.
        FootballerState receiver = engine.Players.First(p => p.Id == ScenarioLibrary.RearPressureReceiver);
        FootballerState presser = engine.Players.First(p => p.Id == ScenarioLibrary.RearPressurePresser);
        Assert.True(presser.Position.X > receiver.Position.X);
        Assert.True(Vector3.Distance(presser.Position, receiver.Position) < 4f);
    }

    [Fact]
    public void HighLineScenario_KeepsTheStrikerOnsideAndBehindTheBall()
    {
        var engine = new MatchSimulationEngine(ScenarioLibrary.HighLineRun());

        FootballerState striker = engine.Players.First(p => p.Id == ScenarioLibrary.HighLineStriker);
        FootballerState carrier = engine.Players.First(p => p.Id == ScenarioLibrary.HighLineCarrier);

        // The striker has drifted ahead of the ball (that is what makes the run possible) but is still
        // behind the second-last defender, so at the start of the fragment there is no offside.
        Assert.True(striker.Position.X > carrier.Position.X);

        List<float> awayX = engine.Players
            .Where(p => p.Team == TeamId.Away)
            .Select(p => p.Position.X)
            .OrderByDescending(x => x)
            .ToList();
        Assert.True(awayX.Count >= 2);
        float secondLastDefenderX = awayX[1];
        Assert.True(striker.Position.X < secondLastDefenderX,
            $"striker at {striker.Position.X:F1} must be behind the second-last defender at {secondLastDefenderX:F1}");
    }
}
