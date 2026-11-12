using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Scenarios;
using Xunit;

namespace FootballSim.Tests.Scenarios;

/// <summary>
/// A0.2: a scenario is an explicit starting world. These tests pin the three properties the rest of
/// the milestone depends on — the fragment starts exactly where the fixture says, ownership is seeded
/// without inventing a contact event, and the same fixture reproduces the same run.
/// </summary>
public sealed class ScenarioSetupTests
{
    private const int HomeGk = 101;
    private const int HomeCb = 102;
    private const int HomeCm = 103;
    private const int AwayCb = 201;
    private const int AwayCm = 202;

    /// <summary>3v2 build-up fragment: home has the ball at a central midfielder's feet.</summary>
    private static MatchScenario ThreeVersusTwo(IFootballerController? awayCmController = null,
        IFootballerController? homeCmController = null)
    {
        var scenario = new MatchScenario
        {
            Name = "3v2 build-up",
            Seed = 4242UL,
            BallPosition = new Vector3(-4.4f, 0f, 8f),
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = HomeCm
        };

        scenario.Add(MatchScenario.Player(HomeGk, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-48f, 0f, 0f), 1));
        scenario.Add(MatchScenario.Player(HomeCb, TeamId.Home, TacticalRole.CenterBack, new Vector3(-20f, 0f, -4f), 4));
        scenario.Add(MatchScenario.Player(HomeCm, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(-5f, 0f, 8f), 8));
        scenario.Add(MatchScenario.Player(AwayCb, TeamId.Away, TacticalRole.CenterBack, new Vector3(10f, 0f, 0f), 4));
        scenario.Add(MatchScenario.Player(AwayCm, TeamId.Away, TacticalRole.CentralMidfielder, new Vector3(2f, 0f, -8f), 8));

        if (homeCmController is not null)
            scenario.WithPlayer(scenario.FindPlayer(HomeCm).WithController(homeCmController));
        if (awayCmController is not null)
            scenario.WithPlayer(scenario.FindPlayer(AwayCm).WithController(awayCmController));

        return scenario;
    }

    [Fact]
    public void Scenario_StartsEveryPlayerExactlyWhereTheFixtureSays()
    {
        var engine = new MatchSimulationEngine(ThreeVersusTwo());

        Assert.Equal(5, engine.Players.Count);
        Assert.Equal(3, engine.Players.Count(p => p.Team == TeamId.Home));
        Assert.Equal(2, engine.Players.Count(p => p.Team == TeamId.Away));

        Assert.Equal(new Vector3(-48f, 0f, 0f), engine.Players[0].Position);
        Assert.Equal(new Vector3(-5f, 0f, 8f), engine.Players[2].Position);
        Assert.Equal(new Vector3(10f, 0f, 0f), engine.Players[3].Position);

        // The ball sits at the carrier's feet at the physical rest height, not at y = 0.
        Assert.Equal(-4.4f, engine.Ball.Position.X, 3);
        Assert.Equal(8f, engine.Ball.Position.Z, 3);
        Assert.Equal(engine.BallRadius, engine.Ball.Position.Y, 3);
        Assert.Equal(Vector3.Zero, engine.Ball.Velocity);

        Assert.Equal(MatchPhase.FirstHalf, engine.Rules.Phase);
        Assert.Equal(0, engine.CurrentTick);
        Assert.Null(engine.LockedHumanController);
        Assert.Equal("3v2 build-up", engine.Scenario?.Name);
    }

    [Fact]
    public void Scenario_SeedsBallControlWithoutNeedingAContactEvent()
    {
        var engine = new MatchSimulationEngine(ThreeVersusTwo());

        Assert.Equal(BallControlState.Controlled, engine.Possession.State);
        Assert.Equal(HomeCm, engine.Possession.ControllingPlayerId);
        Assert.Equal(TeamId.Home, engine.Possession.PossessionTeam);

        // The tactical layer must agree from the first live tick, otherwise the first second of every
        // attacking fragment is spent acting as if the opponent had the ball.
        engine.StepTick();
        Assert.Equal(TeamId.Home, engine.Tactics.ConfirmedPossessingTeam);
        Assert.Equal(BallControlState.Controlled, engine.Possession.State);
        Assert.Equal(HomeCm, engine.Possession.ControllingPlayerId);
    }

    [Fact]
    public void Scenario_LooseBallStartsWithNobodyInPossession()
    {
        var scenario = ThreeVersusTwo();
        scenario.WithPlayer(scenario.FindPlayer(HomeCm).MovedTo(new Vector3(-5f, 0f, 8f)));

        var loose = new MatchScenario
        {
            Name = "loose ball",
            Seed = 7UL,
            BallPosition = new Vector3(0f, 0f, 0f),
            BallSetup = ScenarioBallSetup.Loose
        };
        foreach (ScenarioPlayer player in scenario.Players) loose.Add(player);

        var engine = new MatchSimulationEngine(loose);

        Assert.Equal(BallControlState.Loose, engine.Possession.State);
        Assert.Null(engine.Possession.PossessionTeam);
        Assert.Null(engine.Tactics.ConfirmedPossessingTeam);
    }

    [Fact]
    public void Scenario_ControlledBallTooFarFromTheOwnerIsRejectedAtConstruction()
    {
        var scenario = new MatchScenario
        {
            Name = "impossible control",
            BallPosition = new Vector3(20f, 0f, 0f),
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = HomeCm
        };
        scenario.Add(MatchScenario.Player(HomeCm, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(-5f, 0f, 8f), 8));
        scenario.Add(MatchScenario.Player(AwayCb, TeamId.Away, TacticalRole.CenterBack, new Vector3(10f, 0f, 0f), 4));

        ArgumentException error = Assert.Throws<ArgumentException>(() => new MatchSimulationEngine(scenario));
        Assert.Contains("drops control beyond 3 m", error.Message);
    }

    [Fact]
    public void Scenario_RejectsDuplicatePlayerIds()
    {
        var scenario = new MatchScenario { Name = "duplicate ids", BallSetup = ScenarioBallSetup.Loose };
        scenario.Add(MatchScenario.Player(1, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(-5f, 0f, 0f), 8));
        scenario.Add(MatchScenario.Player(1, TeamId.Away, TacticalRole.CentralMidfielder, new Vector3(5f, 0f, 0f), 8));

        ArgumentException error = Assert.Throws<ArgumentException>(() => new MatchSimulationEngine(scenario));
        Assert.Contains("two players with id 1", error.Message);
    }

    [Fact]
    public void Scenario_RejectsAnOwnerThatIsNotInTheScenario()
    {
        var scenario = new MatchScenario
        {
            Name = "unknown owner",
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = 999
        };
        scenario.Add(MatchScenario.Player(1, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(-5f, 0f, 0f), 8));
        scenario.Add(MatchScenario.Player(2, TeamId.Away, TacticalRole.CentralMidfielder, new Vector3(5f, 0f, 0f), 8));

        ArgumentException error = Assert.Throws<ArgumentException>(() => new MatchSimulationEngine(scenario));
        Assert.Contains("without a valid BallOwnerPlayerId", error.Message);
    }

    [Fact]
    public void Scenario_RejectsAPlayerWhoStartsOffThePitch()
    {
        var scenario = new MatchScenario { Name = "off pitch", BallSetup = ScenarioBallSetup.Loose };
        scenario.Add(MatchScenario.Player(1, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(-5f, 0f, 0f), 8));
        scenario.Add(MatchScenario.Player(2, TeamId.Away, TacticalRole.CentralMidfielder, new Vector3(5f, 0f, 0f), 8));
        scenario.Add(MatchScenario.Player(3, TeamId.Away, TacticalRole.Winger, new Vector3(0f, 0f, 40f), 11));

        ArgumentException error = Assert.Throws<ArgumentException>(() => new MatchSimulationEngine(scenario));
        Assert.Contains("starts off the pitch", error.Message);
    }

    [Fact]
    public void SameScenario_ProducesIdenticalRuns()
    {
        MatchScenario scenario = ThreeVersusTwo();
        var a = new MatchSimulationEngine(scenario);
        var b = new MatchSimulationEngine(scenario);

        for (int tick = 0; tick < 240; tick++)
        {
            a.StepTick();
            b.StepTick();
        }

        Assert.Equal(a.Ball.Position, b.Ball.Position);
        Assert.Equal(a.Ball.Velocity, b.Ball.Velocity);
        Assert.Equal(a.CurrentTick, b.CurrentTick);
        Assert.Equal(a.Possession, b.Possession);
        Assert.Equal(a.Players.Count, b.Players.Count);

        for (int i = 0; i < a.Players.Count; i++)
        {
            Assert.Equal(a.Players[i].Id, b.Players[i].Id);
            Assert.Equal(a.Players[i].Position, b.Players[i].Position);
            Assert.Equal(a.Players[i].Velocity, b.Players[i].Velocity);
            Assert.Equal(a.Players[i].FacingXZ, b.Players[i].FacingXZ);
            Assert.Equal(a.Players[i].Stamina01, b.Players[i].Stamina01);
        }
    }

    [Fact]
    public void ScriptedController_DrivesItsPlayerAndReportsWhatExecutionReceived()
    {
        var script = new Dictionary<int, FootballerCommand>
        {
            [0] = ScenarioCommands.Move(new Vector2(1f, 0f), sprint: true),
            [1] = ScenarioCommands.Move(new Vector2(1f, 0f), sprint: true)
        };
        var scripted = new ScriptedCommandController(script, ScenarioCommands.Hold(), label: "sprint east");
        MatchScenario scenario = ThreeVersusTwo(awayCmController: scripted);

        var engine = new MatchSimulationEngine(scenario);
        FootballerState runner = engine.Players.FindById(AwayCm);
        float startX = runner.Position.X;

        for (int tick = 0; tick < 30; tick++)
            engine.StepTick();

        Assert.Equal(ControllerSourceType.Scripted, engine.GetController(AwayCm).SourceType);
        Assert.True(runner.Position.X > startX, $"scripted runner moved {runner.Position.X - startX:F2} m east");
        Assert.Equal(30, scripted.DecisionsMade);
        Assert.Contains("sprint east", scripted.GetDebugSummary());
    }

    [Fact]
    public void FixedCommandController_NeverReactsToTheBall()
    {
        var frozen = new FixedCommandController(ScenarioCommands.Hold(), label: "frozen opponent");
        MatchScenario scenario = ThreeVersusTwo(awayCmController: frozen);
        var engine = new MatchSimulationEngine(scenario);

        FootballerState frozenPlayer = engine.Players.FindById(AwayCm);
        Vector3 start = frozenPlayer.Position;

        for (int tick = 0; tick < 120; tick++)
            engine.StepTick();

        Assert.Equal(start.X, frozenPlayer.Position.X, 2);
        Assert.Equal(start.Z, frozenPlayer.Position.Z, 2);
    }

    [Fact]
    public void ScenarioMatch_StillReachesFullTimeUnderTheNormalRulesClock()
    {
        var scenario = new MatchScenario
        {
            Name = "short fragment",
            Seed = 11UL,
            HalfDurationSeconds = 2f,
            BallSetup = ScenarioBallSetup.Loose,
            BallPosition = new Vector3(0f, 0f, 0f)
        };
        scenario.Add(MatchScenario.Player(1, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-48f, 0f, 0f), 1));
        scenario.Add(MatchScenario.Player(2, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(-6f, 0f, 0f), 8));
        scenario.Add(MatchScenario.Player(3, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(48f, 0f, 0f), 1));
        scenario.Add(MatchScenario.Player(4, TeamId.Away, TacticalRole.CentralMidfielder, new Vector3(6f, 0f, 0f), 8));

        var engine = new MatchSimulationEngine(scenario);
        for (int tick = 0; tick < 600 && engine.Rules.Phase != MatchPhase.FullTime; tick++)
            engine.StepTick();

        Assert.Equal(MatchPhase.FullTime, engine.Rules.Phase);
    }

    [Fact]
    public void NormalMatchConstruction_IsUnaffectedByTheScenarioPath()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig { Seed = 99UL });

        Assert.Null(engine.Scenario);
        Assert.Equal(22, engine.Players.Count);
        Assert.Equal(11, engine.Players.Count(p => p.Team == TeamId.Home));
        Assert.Equal(BallControlState.Loose, engine.Possession.State);
        Assert.Equal(MatchPhase.FirstHalf, engine.Rules.Phase);
        Assert.Equal(0f, engine.Rules.MatchClockSeconds);
    }
}

internal static class FootballerStateListExtensions
{
    /// <summary>Id lookup for tests that do not want to depend on player ordering.</summary>
    public static FootballerState FindById(this IReadOnlyList<FootballerState> players, int id)
    {
        foreach (FootballerState player in players)
            if (player.Id == id) return player;
        throw new ArgumentOutOfRangeException(nameof(id), id, $"No player #{id}.");
    }
}
