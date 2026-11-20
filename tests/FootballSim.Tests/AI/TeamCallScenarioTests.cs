using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Scenarios;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A7.2 — the channel in a real match.
///
/// The unit tests prove the board, the producer and the consumer separately; these tests prove the three
/// are actually wired together through the engine's own observation build: a call is heard only after its
/// communication delay, it reaches the carrier's candidate table (and nothing before that tick), it is
/// team-scoped, the step/drop instruction reaches the A6.3 band the allocator places defenders from, and
/// two identical matches raise exactly the same calls.
/// </summary>
public sealed class TeamCallScenarioTests
{
    private const int CarrierId = 108;
    private const int DefenderId = 205;

    private const int Delay = 18;

    // ---------------------------------------------------------------------------------------------
    // The exit-gate clause: a man-on call changes the carrier's options after its delay.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AManOnCall_ChangesTheCarriersOptions_OnlyAfterItsDelay()
    {
        // The producer is switched off in both worlds so the injected shout is the only call either engine
        // can hear: this test isolates the delay and the consumer, which is exactly the exit-gate clause.
        MatchScenario scenario = WithAi(ManOnScenario(), new AiRuntimeConfig { EnableTeamCalls = false });
        var control = new MatchSimulationEngine(scenario);
        var treated = new MatchSimulationEngine(scenario);

        for (int tick = 1; tick <= 1; tick++)
        {
            control.StepTick();
            treated.StepTick();
        }

        // Inject the shout from a caller who is not in the fixture: the A/B is about the consumer path and
        // the delay, with no AI producer noise in either world.
        Assert.Equal(0, treated.Communication.Calls.PublishedCount);
        Assert.True(treated.Communication.Calls.Publish(
            TeamId.Home, TeamCallKind.ManOn, callerId: 999, CarrierId, namedPlayerId: 0,
            strength01: 0.9f, publishedTick: treated.CurrentTick, delayTicks: Delay, lifetimeTicks: 27));

        var carrierControl = (TacticalUtilityAiController)control.GetController(CarrierId);
        var carrierTreated = (TacticalUtilityAiController)treated.GetController(CarrierId);

        // Step to the last tick before the shout can be heard: both worlds are the same world.
        while (control.CurrentTick < Delay)
        {
            control.StepTick();
            treated.StepTick();
        }

        Assert.Equal(Delay, control.CurrentTick);
        FootballerObservation beforeControl = Observation(control);
        FootballerObservation beforeTreated = Observation(treated);

        Assert.True(beforeControl.Calls.IsNeutral);
        Assert.True(beforeTreated.Calls.IsNeutral);
        Assert.Equal(CarrierId, beforeControl.BallControllingPlayerId);
        Assert.Equal(CarrierId, beforeTreated.BallControllingPlayerId);
        Assert.Equal(beforeControl.Calls, beforeTreated.Calls);
        Assert.Equal(TableOf(carrierControl), TableOf(carrierTreated));

        // The delay has passed: the treated carrier hears it, the control carrier does not.
        control.StepTick();
        treated.StepTick();

        FootballerObservation afterControl = Observation(control);
        FootballerObservation afterTreated = Observation(treated);

        Assert.True(afterControl.Calls.IsNeutral);
        Assert.Equal(TeamCallKind.ManOn, afterTreated.Calls.Kind);
        Assert.Equal(999, afterTreated.Calls.CallerId);
        Assert.Equal(CarrierId, afterControl.BallControllingPlayerId);
        Assert.Equal(CarrierId, afterTreated.BallControllingPlayerId);

        // The call moves the action's preference term, so the same picture scores differently. The delta is
        // the documented preference for every candidate that call is about, and zero for the rest.
        Assert.NotEqual(TableOf(carrierControl), TableOf(carrierTreated));
        AssertPreferenceDeltas(carrierControl, carrierTreated);
    }

    [Fact]
    public void ACallExpires_AndStopsBeingHeard()
    {
        MatchScenario scenario = ManOnScenario();
        var engine = new MatchSimulationEngine(scenario);
        engine.StepTick();

        Assert.True(engine.Communication.Calls.Publish(
            TeamId.Home, TeamCallKind.ManOn, callerId: 999, CarrierId, namedPlayerId: 0,
            strength01: 0.9f, publishedTick: engine.CurrentTick, delayTicks: Delay, lifetimeTicks: 6));

        while (engine.CurrentTick < Delay)
            engine.StepTick();

        Assert.True(Observation(engine).Calls.IsNeutral);
        engine.StepTick();
        Assert.Equal(TeamCallKind.ManOn, Observation(engine).Calls.Kind);

        while (engine.CurrentTick <= Delay + 7)
            engine.StepTick();

        Assert.True(Observation(engine).Calls.IsNeutral);
    }

    [Fact]
    public void AnOpponentsCall_IsNeverHeard()
    {
        MatchScenario scenario = ManOnScenario();
        var engine = new MatchSimulationEngine(scenario);
        engine.StepTick();

        // Addressed to the home carrier by name, but shouted on the away board: a call is team-scoped.
        Assert.True(engine.Communication.Calls.Publish(
            TeamId.Away, TeamCallKind.ManOn, callerId: 999, CarrierId, namedPlayerId: 0,
            strength01: 0.9f, publishedTick: engine.CurrentTick, delayTicks: Delay, lifetimeTicks: 60));

        while (engine.CurrentTick < Delay + 2)
            engine.StepTick();

        Assert.True(Observation(engine).Calls.IsNeutral);
    }

    // ---------------------------------------------------------------------------------------------
    // Step / drop reach the one allocator.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AStepCall_ReachesTheAllocatorAndLiftsTheLineBand()
    {
        (MatchSimulationEngine control, MatchSimulationEngine treated, int publishTick) = DefendingPair();

        Assert.True(treated.Communication.Calls.Publish(
            TeamId.Home, TeamCallKind.Step, callerId: 999, targetPlayerId: 0, namedPlayerId: 0,
            strength01: 0.8f, publishedTick: publishTick, delayTicks: Delay,
            lifetimeTicks: TeamCallPlanner.LineDirectiveLifetimeTicks));

        while (control.CurrentTick < publishTick + Delay + 3)
        {
            control.StepTick();
            treated.StepTick();
        }

        DefensiveLineBand without = control.TeamJobs.LineBandOf(TeamId.Home);
        DefensiveLineBand with = treated.TeamJobs.LineBandOf(TeamId.Home);

        Assert.True(without.IsValid && with.IsValid);
        Assert.Equal(0f, without.CallShiftMeters);
        Assert.Equal(TeamCallPlanner.StepLineShiftMeters, with.CallShiftMeters, precision: 4);

        // The band moved up (or the caps held it exactly where it was), and it is still inside its own range.
        Assert.True(with.ProjX >= without.ProjX - 1e-3f);
        Assert.InRange(with.ProjX, with.MinProjX - 1e-3f, with.MaxProjX + 1e-3f);
    }

    [Fact]
    public void ADropCall_ReachesTheAllocatorAndPullsTheLineBandBack()
    {
        (MatchSimulationEngine control, MatchSimulationEngine treated, int publishTick) = DefendingPair();

        Assert.True(treated.Communication.Calls.Publish(
            TeamId.Home, TeamCallKind.Drop, callerId: 999, targetPlayerId: 0, namedPlayerId: 0,
            strength01: 0.8f, publishedTick: publishTick, delayTicks: Delay,
            lifetimeTicks: TeamCallPlanner.LineDirectiveLifetimeTicks));

        while (control.CurrentTick < publishTick + Delay + 3)
        {
            control.StepTick();
            treated.StepTick();
        }

        DefensiveLineBand without = control.TeamJobs.LineBandOf(TeamId.Home);
        DefensiveLineBand with = treated.TeamJobs.LineBandOf(TeamId.Home);

        Assert.True(without.IsValid && with.IsValid);
        Assert.Equal(TeamCallPlanner.DropLineShiftMeters, with.CallShiftMeters, precision: 4);
        Assert.True(with.ProjX <= without.ProjX + 1e-3f);
        Assert.InRange(with.ProjX, with.MinProjX - 1e-3f, with.MaxProjX + 1e-3f);
    }

    // ---------------------------------------------------------------------------------------------
    // Determinism, the ablation switch, and the bound in a live match.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TwoIdenticalMatches_RaiseTheSameCalls()
    {
        var first = new MatchSimulationEngine(ElevenVersusEleven(seed: 9911UL));
        var second = new MatchSimulationEngine(ElevenVersusEleven(seed: 9911UL));

        for (int tick = 0; tick < 300; tick++)
        {
            first.StepTick();
            second.StepTick();

            for (int i = 0; i < first.Players.Count; i++)
            {
                FootballerState a = first.Players[i];
                FootballerObservation? left = first.GetLastObservation(a.Id);
                FootballerObservation? right = second.GetLastObservation(a.Id);
                if (left is null || right is null) continue;
                Assert.Equal(left.Calls, right.Calls);
            }
        }

        Assert.True(first.Communication.Calls.PublishedCount > 0,
            "a healthy 11v11 fragment must produce calls");
        Assert.Equal(first.Communication.Calls.PublishedCount, second.Communication.Calls.PublishedCount);
        Assert.Equal(first.Communication.Calls.SuppressedCount, second.Communication.Calls.SuppressedCount);
        Assert.True(first.Communication.Calls.VisibleCount(TeamId.Home, first.CurrentTick) <= TeamCallBoard.Capacity);
    }

    [Fact]
    public void CallsOff_LeavesEveryObservationNeutral()
    {
        MatchScenario withCalls = ElevenVersusEleven(seed: 9911UL);
        var off = new MatchSimulationEngine(WithAi(withCalls, new AiRuntimeConfig { EnableTeamCalls = false }));
        var on = new MatchSimulationEngine(WithAi(withCalls, new AiRuntimeConfig { EnableTeamCalls = true }));

        for (int tick = 0; tick < 300; tick++)
        {
            off.StepTick();
            on.StepTick();

            foreach (FootballerState player in off.Players)
            {
                FootballerObservation? observation = off.GetLastObservation(player.Id);
                if (observation is null) continue;
                Assert.True(observation.Calls.IsNeutral,
                    $"calls off but player #{player.Id} heard {observation.Calls.Describe()}");
            }
        }

        Assert.Equal(0, off.Communication.Calls.PublishedCount);
        Assert.True(on.Communication.Calls.PublishedCount > 0,
            "calls on must publish in the same fixture, or the A/B proves nothing");
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The carrier, a defender closing on him, and the two keepers: no pass option, no other caller.</summary>
    private static MatchScenario ManOnScenario()
    {
        var scenario = new MatchScenario
        {
            Name = "man-on call A/B",
            Seed = 9090UL,
            BallPosition = new Vector3(0.3f, 0f, 0f),
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = CarrierId
        };

        scenario.Add(MatchScenario.Player(101, TeamId.Home, TacticalRole.Goalkeeper,
            new Vector3(-48f, 0f, 0f), 1));
        scenario.Add(MatchScenario.Player(CarrierId, TeamId.Home, TacticalRole.CentralMidfielder,
            new Vector3(0f, 0f, 0f), 8));
        scenario.Add(MatchScenario.Player(201, TeamId.Away, TacticalRole.Goalkeeper,
            new Vector3(48f, 0f, 0f), 1));
        scenario.Add(MatchScenario.Player(DefenderId, TeamId.Away, TacticalRole.CenterBack,
            new Vector3(5.5f, 0f, -0.5f), 4));
        return scenario;
    }

    /// <summary>
    /// Home defending against a held away carrier, twice: the producer is switched off in both worlds so the
    /// only call either engine can hear is the one the test injects.
    /// </summary>
    private static (MatchSimulationEngine Control, MatchSimulationEngine Treated, int PublishTick) DefendingPair()
    {
        MatchScenario fixture = WithAi(ScenarioLibrary.CenterBackStepsOut(),
            new AiRuntimeConfig { EnableTeamCalls = false });
        var control = new MatchSimulationEngine(fixture);
        var treated = new MatchSimulationEngine(fixture);

        for (int tick = 0; tick < 10; tick++)
        {
            control.StepTick();
            treated.StepTick();
        }

        return (control, treated, control.CurrentTick);
    }

    private static MatchScenario ElevenVersusEleven(ulong seed)
    {
        var scenario = new MatchScenario
        {
            Name = "team calls 11v11",
            Seed = seed,
            HalfDurationSeconds = 600f,
            HomeProfile = TeamTacticalProfile.PossessionControl,
            AwayProfile = TeamTacticalProfile.DirectCounterattack,
            BallPosition = new Vector3(0.3f, 0f, 0f),
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = 111
        };

        Position(TeamId.Home, 101, TacticalRole.Goalkeeper, -48f, 0f, 1);
        Position(TeamId.Home, 102, TacticalRole.CenterBack, -35f, 10f, 4);
        Position(TeamId.Home, 103, TacticalRole.CenterBack, -35f, -10f, 5);
        Position(TeamId.Home, 104, TacticalRole.FullBack, -30f, 24f, 2);
        Position(TeamId.Home, 105, TacticalRole.FullBack, -30f, -24f, 3);
        Position(TeamId.Home, 106, TacticalRole.DefensiveMidfielder, -20f, 0f, 6);
        Position(TeamId.Home, 107, TacticalRole.CentralMidfielder, -8f, 12f, 8);
        Position(TeamId.Home, 108, TacticalRole.CentralMidfielder, -8f, -12f, 10);
        Position(TeamId.Home, 109, TacticalRole.Winger, 0f, 22f, 7);
        Position(TeamId.Home, 110, TacticalRole.Winger, 0f, -22f, 11);
        Position(TeamId.Home, 111, TacticalRole.Striker, -2f, 0f, 9);

        Position(TeamId.Away, 201, TacticalRole.Goalkeeper, 48f, 0f, 1);
        Position(TeamId.Away, 202, TacticalRole.CenterBack, 35f, 8f, 4);
        Position(TeamId.Away, 203, TacticalRole.CenterBack, 35f, -8f, 5);
        Position(TeamId.Away, 204, TacticalRole.FullBack, 30f, 24f, 2);
        Position(TeamId.Away, 205, TacticalRole.FullBack, 30f, -24f, 3);
        Position(TeamId.Away, 206, TacticalRole.DefensiveMidfielder, 20f, 0f, 6);
        Position(TeamId.Away, 207, TacticalRole.CentralMidfielder, 8f, 12f, 8);
        Position(TeamId.Away, 208, TacticalRole.CentralMidfielder, 8f, -12f, 10);
        Position(TeamId.Away, 209, TacticalRole.Winger, 0f, 22f, 7);
        Position(TeamId.Away, 210, TacticalRole.Winger, 0f, -22f, 11);
        Position(TeamId.Away, 211, TacticalRole.Striker, 2f, 0f, 9);

        return scenario;

        void Position(TeamId team, int id, TacticalRole role, float x, float z, int shirt) =>
            scenario.Add(MatchScenario.Player(id, team, role, new Vector3(x, 0f, z), shirt));
    }

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
            BallLastTouchedByTeam = source.BallLastTouchedByTeam,
            BallLastPlayKind = source.BallLastPlayKind,
            BallLastPlayIntent = source.BallLastPlayIntent,
            BallLastPlayPlayerId = source.BallLastPlayPlayerId
        };
        foreach (ScenarioPlayer player in source.Players) scenario.Add(player);
        return scenario;
    }

    private static FootballerObservation Observation(MatchSimulationEngine engine) =>
        engine.GetLastObservation(CarrierId) ??
        throw new Xunit.Sdk.XunitException($"no observation for carrier #{CarrierId}");

    /// <summary>The carrier's raw candidate table, stable across two identical worlds.</summary>
    private static string TableOf(TacticalUtilityAiController controller)
    {
        var text = new StringBuilder();
        foreach (UtilityCandidate candidate in controller.LastCandidates)
        {
            text.Append(candidate.Key.Describe()).Append('=').Append(candidate.UtilityScore.ToString("F4"))
                .Append(';');
        }

        return text.ToString();
    }

    /// <summary>
    /// The man-on preferences, candidate by candidate: shield, bounce pass, carry and take-on each moved by
    /// the documented amount on the same picture, and every other option unchanged.
    /// </summary>
    private static void AssertPreferenceDeltas(
        TacticalUtilityAiController control,
        TacticalUtilityAiController treated)
    {
        Assert.Equal(control.LastCandidates.Count, treated.LastCandidates.Count);

        for (int i = 0; i < control.LastCandidates.Count; i++)
        {
            UtilityCandidate without = control.LastCandidates[i];
            UtilityCandidate with = treated.LastCandidates[i];
            Assert.Equal(without.Key, with.Key);

            float expected = without.Key.Kind switch
            {
                CandidateKind.Shield => ActionValueScale.CallShieldBonus,
                CandidateKind.Pass => ActionValueScale.CallBouncePassBonus,
                CandidateKind.Carry => -ActionValueScale.CallCarryPenalty,
                CandidateKind.Dribble => -ActionValueScale.CallTakeOnPenalty,
                _ => 0f
            };

            Assert.Equal(expected, with.UtilityScore - without.UtilityScore, precision: 3);
        }
    }
}
