using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Rules;

namespace FootballSim.Scenarios;

/// <summary>
/// Outcome of one scenario run: the engine that was stepped, what it did, and every contact that the
/// execution layer accepted or refused.
///
/// The result deliberately exposes the live <see cref="MatchSimulationEngine"/> rather than a copied
/// snapshot. A scenario assertion is allowed to inspect authoritative state (positions, possession,
/// contact log); a *tactical* assertion must still read what the player knew, which is why perception
/// and trace data are separate concerns (A0.4 / A2).
/// </summary>
public sealed class ScenarioRunResult
{
    public required MatchScenario Scenario { get; init; }
    public required MatchSimulationEngine Engine { get; init; }

    /// <summary>Ticks this run actually stepped.</summary>
    public int TicksRun { get; init; }

    /// <summary>True when the run ended because the match reached full time rather than the tick budget.</summary>
    public bool ReachedFullTime { get; init; }

    /// <summary>Every contact request, in tick order, including the refused ones.</summary>
    public IReadOnlyList<BallContactAttempt> ContactAttempts { get; init; } = Array.Empty<BallContactAttempt>();

    /// <summary>Every executed contact, oldest first (bounded by the engine's contact log).</summary>
    public IReadOnlyList<BallContactEvent> Contacts { get; init; } = Array.Empty<BallContactEvent>();

    /// <summary>Every rules event raised during the run, oldest first.</summary>
    public IReadOnlyList<MatchRuleEvent> RuleEvents { get; init; } = Array.Empty<MatchRuleEvent>();

    /// <summary>Controller decisions in tick order, when the run was traced.</summary>
    public IReadOnlyList<DecisionTraceEntry> Decisions { get; init; } = Array.Empty<DecisionTraceEntry>();

    public MatchAnalyticsSummary Analytics => Engine.Analytics;
    public Vector3 BallPosition => Engine.Ball.Position;
    public BallPossessionSnapshot Possession => Engine.Possession;
    public int FinalTick => Engine.CurrentTick;

    public FootballerState Player(int id)
    {
        foreach (FootballerState player in Engine.Players)
            if (player.Id == id) return player;
        throw new ArgumentOutOfRangeException(nameof(id), id, $"Scenario run has no player #{id}.");
    }

    /// <summary>Compact one-line summary for test failure messages and CLI output.</summary>
    public string Describe() =>
        $"{Scenario.Name}: {TicksRun} ticks, {(ReachedFullTime ? "full time" : "budget stop")}, " +
        $"possession {Possession.State}/{Possession.PossessionTeam}, ball " +
        $"({BallPosition.X:F1}, {BallPosition.Y:F2}, {BallPosition.Z:F1}), " +
        $"{Contacts.Count} contacts, {RuleEvents.Count} rule events";
}

/// <summary>
/// Steps a scenario for a bounded number of live ticks. This exists so fixtures do not each
/// re-implement the loop, the contact capture and the full-time stop condition — and so the headless
/// CLI can run the same fragment a test runs.
/// </summary>
public static class ScenarioRunner
{
    /// <summary>Runs <paramref name="ticks"/> ticks, or fewer if the match reaches full time.</summary>
    public static ScenarioRunResult Run(
        MatchScenario scenario,
        int ticks,
        MatchSimulationEngine? engine = null,
        bool traceDecisions = false)
    {
        if (ticks < 0) throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "Tick budget cannot be negative.");
        return RunUntil(
            scenario,
            _ => false,
            ticks,
            engine,
            traceDecisions);
    }

    /// <summary>
    /// Runs until <paramref name="stopCondition"/> is true, full time is reached, or the tick budget is
    /// exhausted. The stop condition is evaluated after each step.
    /// </summary>
    public static ScenarioRunResult RunUntil(
        MatchScenario scenario,
        Func<MatchSimulationEngine, bool> stopCondition,
        int maxTicks,
        MatchSimulationEngine? engine = null,
        bool traceDecisions = false)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(stopCondition);
        if (maxTicks < 1) throw new ArgumentOutOfRangeException(nameof(maxTicks), maxTicks, "Tick budget must be positive.");

        var match = engine ?? new MatchSimulationEngine(scenario);
        if (traceDecisions) match.DecisionTrace.IsEnabled = true;

        var attempts = new List<BallContactAttempt>();
        int lastAttemptTick = -1;
        int ticksRun = 0;
        bool reachedFullTime = false;

        while (ticksRun < maxTicks)
        {
            if (match.Rules.Phase == MatchPhase.FullTime)
            {
                reachedFullTime = true;
                break;
            }

            match.StepTick();
            ticksRun++;

            IReadOnlyList<BallContactAttempt> tickAttempts = match.LastContactAttempts;
            if (tickAttempts.Count > 0 && tickAttempts[0].Tick != lastAttemptTick)
            {
                lastAttemptTick = tickAttempts[0].Tick;
                attempts.AddRange(tickAttempts);
            }

            if (match.Rules.Phase == MatchPhase.FullTime)
            {
                reachedFullTime = true;
                break;
            }

            if (stopCondition(match)) break;
        }

        return new ScenarioRunResult
        {
            Scenario = scenario,
            Engine = match,
            TicksRun = ticksRun,
            ReachedFullTime = reachedFullTime,
            ContactAttempts = attempts,
            Contacts = new List<BallContactEvent>(match.ContactLog),
            RuleEvents = new List<MatchRuleEvent>(match.Rules.Events),
            Decisions = traceDecisions
                ? new List<DecisionTraceEntry>(match.DecisionTrace.Entries)
                : Array.Empty<DecisionTraceEntry>()
        };
    }
}
