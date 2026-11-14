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

namespace FootballSim.Tests.AI;

/// <summary>
/// A6.1/A6.2 at the engine level, on the fixture the plan names for it: the touchline press. The model-level
/// clauses live in <see cref="PressPolicyTests"/>; these assertions check that the policy is actually reached
/// by the live controllers and that the team-level balance holds inside a real passage of play.
/// </summary>
public sealed class CoordinatedDefenceTests
{
    /// <summary>
    /// The cover defender the allocator chose stands where his shadow is worth something: goal-side of the
    /// presser, and the action is the policy's own cover act rather than his stale anchor.
    /// </summary>
    [Fact]
    public void TheAllocatedCover_DropsOntoTheShadowLane()
    {
        var engine = new MatchSimulationEngine(ScenarioLibrary.TouchlinePress());

        FootballerState? cover = null;
        for (int tick = 0; tick < 240 && cover is null; tick++)
        {
            engine.StepTick();
            cover = FindJobPlayer(engine, TeamId.Home, TacticalJob.CoverDefender);
        }

        Assert.NotNull(cover);
        FootballerState presser = FindPresser(engine, TeamId.Home);
        var controller = Assert.IsType<TacticalUtilityAiController>(engine.GetController(cover!.Id));
        Assert.Equal("CoverTheLane", controller.LastSelected.ActionName);

        Vector3? target = controller.LastSelected.TargetXZ;
        Assert.NotNull(target);

        Vector2 presserXZ = new(presser.Position.X, presser.Position.Z);
        Vector2 targetXZ = new(target!.Value.X, target.Value.Z);
        FootballerObservation observation = engine.GetLastObservation(cover.Id)!;
        Vector2 goalSide = PressPolicy.GoalSideXZ(presserXZ, observation, engine.Pitch);

        float behind = Vector2.Dot(targetXZ - presserXZ, goalSide);
        Assert.True(behind > 3f, $"the cover stood {behind:F1} m behind the press");
    }

    /// <summary>
    /// The balance holds at team level: in any one tick <b>at most one</b> home player is offered the second
    /// man's step-in (<see cref="CandidateKind.Challenge"/>). Two defenders committing independently was the
    /// behaviour A6.1 replaces. (Two 5v5 fragments, so the property is checked with the ball on each side.)
    /// </summary>
    [Fact]
    public void AtMostOneDefender_IsOfferedTheSecondMan()
    {
        int worstOverall = 0;
        foreach (MatchScenario scenario in new[]
                 {
                     ScenarioLibrary.TouchlinePress(),
                     ScenarioLibrary.CounterattackThreeVersusThree()
                 })
        {
            var engine = new MatchSimulationEngine(scenario);

            for (int tick = 0; tick < 240; tick++)
            {
                engine.StepTick();
                if (tick % 3 != 0) continue;

                var offered = new Dictionary<TeamId, int>();
                foreach (FootballerState player in engine.Players)
                {
                    if (!player.IsActiveOnPitch) continue;
                    if (engine.GetController(player.Id) is not TacticalUtilityAiController ai) continue;

                    foreach (UtilityCandidate candidate in ai.LastCandidates)
                    {
                        if (candidate.Key.Kind != CandidateKind.Challenge) continue;
                        offered[player.Team] = offered.GetValueOrDefault(player.Team) + 1;
                        break;
                    }
                }

                foreach (int count in offered.Values)
                    worstOverall = Math.Max(worstOverall, count);
            }
        }

        Assert.InRange(worstOverall, 0, 1);
    }

    /// <summary>
    /// The presser's approach point is on the inside of a touchline carrier — the line is then the second
    /// defender — and it is a point, not a direction: the trace can show it.
    /// </summary>
    [Fact]
    public void ThePresserTakesTheInsideOfTheTouchlineCarrier()
    {
        var engine = new MatchSimulationEngine(ScenarioLibrary.TouchlinePress());
        for (int tick = 0; tick < 90; tick++) engine.StepTick();

        FootballerState presser = FindPresser(engine, TeamId.Home);
        var controller = Assert.IsType<TacticalUtilityAiController>(engine.GetController(presser.Id));

        Assert.Equal(CandidateKind.Press, controller.LastSelected.Key.Kind);
        Vector3? target = controller.LastSelected.TargetXZ;
        Assert.NotNull(target);

        FootballerState carrier = engine.Players.Single(p => p.Id == engine.Possession.ControllingPlayerId);
        Assert.True(carrier.Position.Z < 0f, "the fixture's carrier is on the negative touchline");
        Assert.True(target!.Value.Z > carrier.Position.Z + 0.2f,
            $"the presser aimed at Z={target.Value.Z:F1} from a carrier at {carrier.Position.Z:F1}: outside him");
    }

    /// <summary>
    /// The live path publishes one second man per defending team. Two equidistant centre-backs observe the
    /// same situation through different noise; with the match election every one of their observations
    /// carries the same id, so at most one second-man candidate exists on any tick.
    /// </summary>
    [Fact]
    public void TheLiveMatch_PublishesOneSecondManPerTeam()
    {
        var scenario = new MatchScenario
        {
            Name = "equidistant second men",
            Seed = 9UL,
            BallPosition = new Vector3(-20f, 0f, 0f),
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = 207
        };

        scenario.Add(MatchScenario.Player(101, TeamId.Home, TacticalRole.Goalkeeper,
            new Vector3(-48f, 0f, 0f), 1));
        scenario.Add(MatchScenario.Player(109, TeamId.Home, TacticalRole.Striker,
            new Vector3(-20f, 0f, -3f), 9));
        scenario.Add(MatchScenario.Player(102, TeamId.Home, TacticalRole.CenterBack,
            new Vector3(-28f, 0f, -6f), 4));
        scenario.Add(MatchScenario.Player(103, TeamId.Home, TacticalRole.CenterBack,
            new Vector3(-28f, 0f, 6f), 5));
        scenario.Add(MatchScenario.Player(209, TeamId.Away, TacticalRole.Goalkeeper,
            new Vector3(46f, 0f, 0f), 1).WithController(
            new FixedCommandController(ScenarioCommands.Hold(), "holding keeper")));
        scenario.Add(MatchScenario.Player(207, TeamId.Away, TacticalRole.Striker,
            new Vector3(-20f, 0f, 0f), 9).WithController(
            new FixedCommandController(ScenarioCommands.Hold(), "holding carrier")));

        var engine = new MatchSimulationEngine(scenario);

        int samples = 0;
        int defendingTicks = 0;
        int supportSituations = 0;
        int electedZero = 0;
        int electedOther = 0;
        for (int tick = 0; tick < 240; tick++)
        {
            engine.StepTick();
            if (tick % 3 != 0) continue;

            bool defending = false;
            int? published = null;
            foreach (FootballerState player in engine.Players)
            {
                if (player.Team != TeamId.Home || !player.IsActiveOnPitch) continue;
                FootballerObservation observation = engine.GetLastObservation(player.Id)!;
                if (observation.BallControlState != BallControlState.Controlled ||
                    observation.PossessingTeam == TeamId.Home)
                {
                    continue;
                }

                defending = true;
                Assert.NotNull(observation.SupportPresserPlayerId);
                if (published is null) published = observation.SupportPresserPlayerId;
                else Assert.Equal(published, observation.SupportPresserPlayerId);
                if (PressPolicy.BuildSituation(observation, engine.Pitch).IsSupport) supportSituations++;
            }

            if (!defending) continue;
            defendingTicks++;
            if (published == 0) electedZero++;
            else if (published is not null) electedOther++;

            int offered = 0;
            foreach (FootballerState player in engine.Players)
            {
                if (player.Team != TeamId.Home || !player.IsActiveOnPitch) continue;
                if (engine.GetController(player.Id) is not TacticalUtilityAiController ai) continue;
                foreach (UtilityCandidate candidate in ai.LastCandidates)
                {
                    if (candidate.Key.Kind != CandidateKind.Challenge) continue;
                    offered++;
                    break;
                }
            }

            Assert.InRange(offered, 0, 1);
            if (offered > 0) samples++;
        }

        Assert.True(samples > 0,
            $"the fixture never offered the second man: defending {defendingTicks}, " +
            $"elected zero {electedZero}, elected other {electedOther}, support situations {supportSituations}");
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    private static FootballerState FindPresser(MatchSimulationEngine engine, TeamId team)
    {
        foreach (FootballerState player in engine.Players)
        {
            if (player.Team != team || !player.IsActiveOnPitch) continue;
            if (engine.GetLastObservation(player.Id)?.IsDesignatedBallResponder == true) return player;
        }

        throw new InvalidOperationException($"No elected ball responder for {team}.");
    }

    private static FootballerState? FindJobPlayer(MatchSimulationEngine engine, TeamId team, TacticalJob job)
    {
        foreach (FootballerState player in engine.Players)
        {
            if (player.Team != team || !player.IsActiveOnPitch) continue;
            if (engine.TeamJobs.Get(player.Id)?.Job == job) return player;
        }

        return null;
    }
}
