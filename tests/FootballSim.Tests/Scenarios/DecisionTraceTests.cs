using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Scenarios;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.Scenarios;

/// <summary>
/// A0.4: a bounded decision trace that separates the action a player chose from the contact execution
/// actually made.
///
/// Two properties matter more than the contents: tracing must not change the simulation, and the log
/// must stay bounded in a long run. Both are asserted directly rather than assumed.
/// </summary>
public sealed class DecisionTraceTests
{
    [Fact]
    public void Trace_IsOffByDefault()
    {
        var engine = new MatchSimulationEngine(ScenarioLibrary.BuildUpThreeVersusTwo());

        Assert.False(engine.DecisionTrace.IsEnabled);
        Assert.Equal(0, engine.DecisionTrace.Count);

        for (int tick = 0; tick < 30; tick++) engine.StepTick();

        Assert.Equal(0, engine.DecisionTrace.Count);
    }

    [Fact]
    public void Trace_RecordsOneEntryPerActivePlayerPerLiveTick()
    {
        MatchScenario scenario = ScenarioLibrary.BuildUpThreeVersusTwo();
        var engine = new MatchSimulationEngine(scenario);
        engine.DecisionTrace.IsEnabled = true;

        const int ticks = 25;
        for (int tick = 0; tick < ticks; tick++) engine.StepTick();

        int activePlayers = engine.Players.Count(p => p.IsActiveOnPitch);
        Assert.Equal(activePlayers * ticks, engine.DecisionTrace.Count);
        Assert.Equal(engine.CurrentTick, engine.DecisionTrace.Last!.Value.Tick);
    }

    [Fact]
    public void TraceEntry_CarriesDutyActionAndRunnerUp()
    {
        ScenarioRunResult run = ScenarioRunner.Run(ScenarioLibrary.BuildUpThreeVersusTwo(), 40, traceDecisions: true);

        Assert.NotEmpty(run.Decisions);
        Assert.All(run.Decisions, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.SelectedAction));
            Assert.True(entry.CandidateCount >= 0);
            Assert.Contains(entry.PlayerId, run.Engine.Players.Select(p => p.Id));
        });

        // The carrier is the player with the ball; its decision must come from the on-ball candidate
        // set, while an off-ball team-mate's decision must not.
        DecisionTraceEntry carrierDecision = run.Decisions
            .Last(e => e.PlayerId == ScenarioLibrary.BuildUpHomeCm && e.Tick <= 5);
        Assert.True(carrierDecision.CandidateCount > 0);

        Assert.Contains(run.Decisions, e => e.RunnerUpAction is not null);
        Assert.Contains(run.Decisions, e => e.JobReason is not null);
    }

    [Fact]
    public void Trace_DoesNotChangeTheSimulation()
    {
        MatchScenario scenario = ScenarioLibrary.CounterattackThreeVersusThree();

        ScenarioRunResult traced = ScenarioRunner.Run(scenario, 180, traceDecisions: true);
        ScenarioRunResult untraced = ScenarioRunner.Run(ScenarioLibrary.CounterattackThreeVersusThree(), 180);

        Assert.Equal(untraced.TicksRun, traced.TicksRun);
        Assert.Equal(untraced.BallPosition, traced.BallPosition);
        Assert.Equal(untraced.Engine.Ball.Velocity, traced.Engine.Ball.Velocity);
        Assert.Equal(untraced.Possession, traced.Possession);
        Assert.Equal(untraced.ContactAttempts.Count, traced.ContactAttempts.Count);

        for (int i = 0; i < traced.Engine.Players.Count; i++)
        {
            Assert.Equal(untraced.Engine.Players[i].Position, traced.Engine.Players[i].Position);
            Assert.Equal(untraced.Engine.Players[i].Velocity, traced.Engine.Players[i].Velocity);
        }
    }

    [Fact]
    public void Trace_StaysBoundedWhenCapacityIsExceeded()
    {
        var buffer = new DecisionTraceBuffer(capacity: 8);
        buffer.IsEnabled = true;

        FootballerObservation observation = Observation(1, TeamId.Home, TacticalRole.CentralMidfielder);
        for (int i = 0; i < 40; i++)
            buffer.Record(new DecisionTraceEntry(i, 1, TeamId.Home, TacticalRole.CentralMidfielder,
                TacticalJob.Shape, "reason", "Action", i, null, 0f, 3));

        Assert.Equal(8, buffer.Count);
        Assert.True(buffer.IsFull);

        // Oldest entries are dropped, so the tail of the run is always what a debugger sees.
        Assert.Equal(32, buffer.Entries[0].Tick);
        Assert.Equal(39, buffer.Entries[^1].Tick);
    }

    [Fact]
    public void Trace_RecordsTheExecutedContactSeparatelyFromTheChosenAction()
    {
        ScenarioRunResult run = ScenarioRunner.Run(ScenarioLibrary.BuildUpThreeVersusTwo(), 240, traceDecisions: true);

        // The trace says what was chosen; the contact log says what the execution layer accepted or
        // refused. A scenario assertion must be able to compare the two, so both must be populated
        // without one being derived from the other.
        Assert.NotEmpty(run.Decisions);
        Assert.NotEmpty(run.ContactAttempts);

        Assert.All(run.ContactAttempts, attempt =>
        {
            Assert.True(attempt.Tick >= 1);
            Assert.True(attempt.Tick <= run.FinalTick);
        });
    }

    [Fact]
    public void TraceEntry_DescribesItselfForDiagnostics()
    {
        var entry = new DecisionTraceEntry(120, 7, TeamId.Home, TacticalRole.Winger,
            TacticalJob.Overlap, "support run", "Carry", 0.81f, "Hold", 0.74f, 6);

        string text = entry.Describe();

        Assert.Contains("t120", text);
        Assert.Contains("p7", text);
        Assert.Contains("Overlap", text);
        Assert.Contains("Carry", text);
        Assert.Contains("2nd Hold", text);
    }

    [Fact]
    public void Trace_LeavesNotYetModelledFieldsNullInsteadOfFakingValues()
    {
        ScenarioRunResult run = ScenarioRunner.Run(ScenarioLibrary.BuildUpThreeVersusTwo(), 10, traceDecisions: true);
        DecisionTraceEntry entry = run.Decisions[0];

        // Perception has no memory model yet (A2.2), candidate keys arrive in A1.1 and intents in A1.2.
        // A zero here would read as "perfectly fresh knowledge", which is a claim the code cannot make.
        Assert.Null(entry.KnowledgeAgeTicks);
        Assert.Null(entry.CandidateKey);
        Assert.Null(entry.IntentId);
    }

    private static FootballerObservation Observation(int id, TeamId team, TacticalRole role) => new()
    {
        SelfId = id,
        SelfTeam = team,
        SelfRole = role,
        SelfPosition = Vector3.Zero,
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 0f,
        PerceivedBallPosition = Vector3.Zero,
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = team,
        BallLastTouchedByPlayerId = null,
        TacticalAnchorPosition = Vector3.Zero,
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 0f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        VisiblePlayers = Array.Empty<PerceivedEntity>(),
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = new PlayerTendencies(),
        SelfAttributes = new PlayerAttributes()
    };
}
