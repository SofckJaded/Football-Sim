using System;
using System.Collections.Generic;
using System.Linq;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Scenarios;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A4.5 — robust hysteresis with current-action re-evaluation.
///
/// The rule is <c>best − current &gt; absolute + relative × max(|current|, ε)</c>. The tests split into the
/// pure rule (including the case the plan warns about, a negative current score) and a behavioural test
/// that the two configuration fields actually change how often a player changes his mind.
/// </summary>
public sealed class SwitchMarginTests
{
    /// <summary>Margins of zero are the documented ablation: any strictly better alternative wins.</summary>
    [Fact]
    public void ZeroMargins_SwitchOnAnyImprovement()
    {
        Assert.True(TacticalUtilityAiController.ShouldSwitch(1.01f, 1.00f, 0f, 0f));
        Assert.False(TacticalUtilityAiController.ShouldSwitch(1.00f, 1.00f, 0f, 0f));
        Assert.False(TacticalUtilityAiController.ShouldSwitch(0.99f, 1.00f, 0f, 0f));
    }

    /// <summary>A gain inside the margin is not a reason to change what the player is doing.</summary>
    [Fact]
    public void ASmallGain_DoesNotSwitch()
    {
        // absolute 0.05 + relative 0.10 × 1.00 = 0.15 required.
        Assert.False(TacticalUtilityAiController.ShouldSwitch(1.10f, 1.00f, 0.05f, 0.10f));
        Assert.True(TacticalUtilityAiController.ShouldSwitch(1.16f, 1.00f, 0.05f, 0.10f));
    }

    /// <summary>The relative part makes a high-value current action stickier than a cheap one.</summary>
    [Fact]
    public void AHighValueCurrentAction_IsStickierThanACheapOne()
    {
        // Same absolute gain of 0.3: enough to leave a 1.0 action, not enough to leave a 3.0 one.
        Assert.True(TacticalUtilityAiController.ShouldSwitch(1.30f, 1.00f, 0.05f, 0.10f));
        Assert.False(TacticalUtilityAiController.ShouldSwitch(3.30f, 3.00f, 0.05f, 0.10f));
    }

    /// <summary>
    /// The plan's warning, as a test: with a negative current score the rule must make switching *easier*,
    /// not harder. A proportional comparison would require an improvement in proportion to a magnitude the
    /// action does not have — here the current action is already costing the side value.
    /// </summary>
    [Fact]
    public void ANegativeCurrentScore_DoesNotBlockASwitch()
    {
        // current = −1.0: required = 0.05 + 0.10 × 1.0 = 0.15. A best of −0.5 is 0.5 better.
        Assert.True(TacticalUtilityAiController.ShouldSwitch(-0.5f, -1.0f, 0.05f, 0.10f));
        // And a best that is only marginally less bad still switches, because there is little to protect.
        Assert.True(TacticalUtilityAiController.ShouldSwitch(-0.84f, -1.0f, 0.05f, 0.10f));
        // While an alternative that is *worse* never does.
        Assert.False(TacticalUtilityAiController.ShouldSwitch(-1.2f, -1.0f, 0.05f, 0.10f));
    }

    /// <summary>Non-finite inputs are treated as unusable rather than propagating NaN into a decision.</summary>
    [Fact]
    public void NonFiniteScores_AreHandled()
    {
        Assert.False(TacticalUtilityAiController.ShouldSwitch(float.NaN, 1f, 0.05f, 0.10f));
        Assert.False(TacticalUtilityAiController.ShouldSwitch(float.PositiveInfinity, 1f, float.NaN, float.NaN));
        Assert.True(TacticalUtilityAiController.ShouldSwitch(2f, float.NaN, 0.05f, 0.10f));
    }

    /// <summary>
    /// The behavioural half: with a large switch margin the same fixture's players change their chosen
    /// action fewer times than with the ablation (no hysteresis). This is the evidence that the two
    /// configuration fields reach the decision path rather than sitting in a settings object.
    /// </summary>
    [Fact]
    public void TheConfiguredMargin_ChangesHowOftenPlayersSwitch()
    {
        int eager = CountActionChanges(new AiRuntimeConfig { SwitchAbsoluteMargin = 0f, SwitchRelativeMargin = 0f });
        int sticky = CountActionChanges(new AiRuntimeConfig { SwitchAbsoluteMargin = 1.0f, SwitchRelativeMargin = 0.5f });

        Assert.True(eager > 0, "the fixture must produce at least one action change for the comparison to mean anything");
        Assert.True(sticky < eager,
            $"a 1.0/0.5 margin produced {sticky} action changes against {eager} with no hysteresis");
    }

    /// <summary>Runs a fixture with the given AI settings and counts how often a player changes action.</summary>
    private static int CountActionChanges(AiRuntimeConfig ai)
    {
        MatchScenario scenario = WithAi(ScenarioLibrary.BuildUpThreeVersusTwo(), ai);
        ScenarioRunResult run = ScenarioRunner.Run(scenario, 600, traceDecisions: true);

        int changes = 0;
        var last = new Dictionary<int, string>();
        foreach (DecisionTraceEntry entry in run.Decisions)
        {
            string identity = entry.CandidateKey ?? entry.SelectedAction;
            if (last.TryGetValue(entry.PlayerId, out string? previous) && previous != identity) changes++;
            last[entry.PlayerId] = identity;
        }

        return changes;
    }

    /// <summary>
    /// Rebuilds a scenario with different AI settings. <see cref="MatchScenario.Ai"/> is `init` on purpose
    /// (an ablation belongs to the fixture), so the fixture is copied rather than mutated.
    /// </summary>
    private static MatchScenario WithAi(MatchScenario source, AiRuntimeConfig ai)
    {
        var scenario = new MatchScenario
        {
            Name = source.Name,
            Seed = source.Seed,
            Pitch = source.Pitch,
            HomeProfile = source.HomeProfile,
            AwayProfile = source.AwayProfile,
            Ai = ai,
            HalfDurationSeconds = source.HalfDurationSeconds,
            MatchClockSeconds = source.MatchClockSeconds,
            Period = source.Period,
            HomeScore = source.HomeScore,
            AwayScore = source.AwayScore,
            HumanPlayerId = source.HumanPlayerId,
            BallPosition = source.BallPosition,
            BallVelocity = source.BallVelocity,
            BallAngularVelocity = source.BallAngularVelocity,
            BallSetup = source.BallSetup,
            BallOwnerPlayerId = source.BallOwnerPlayerId,
            IntendedReceiverId = source.IntendedReceiverId,
            BallLastTouchedByTeam = source.BallLastTouchedByTeam
        };
        foreach (ScenarioPlayer player in source.Players) scenario.Add(player);
        return scenario;
    }
}
