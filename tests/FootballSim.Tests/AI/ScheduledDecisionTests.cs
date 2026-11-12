using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Scenarios;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A1.2 — one intent, executed every tick; candidate scoring only on its cadence.
///
/// The switch is off by default, so the most important test here is that turning it off changes nothing.
/// The rest prove the machinery works when it is on: it re-scores on the cadence, re-emits in between, and
/// never extends an expired intent.
/// </summary>
public sealed class ScheduledDecisionTests
{
    private static readonly AiRuntimeConfig Scheduled = new()
    {
        EnableScheduledDecisions = true,
        CarrierDecisionTicks = 6,
        OutfieldDecisionTicks = 10,
        JobCommitmentSeconds = 1.0f,
        ReactionCommitmentSeconds = 0.5f
    };

    // ---- defaults ------------------------------------------------------------------------------

    [Fact]
    public void Scheduling_IsOffByDefault()
    {
        Assert.False(AiRuntimeConfig.Default.EnableScheduledDecisions);
        Assert.False(new MatchSimulationEngine(new PreMatchConfig()).Config.Ai.EnableScheduledDecisions);
    }

    [Fact]
    public void SchedulingOff_DecidesOnEveryTick()
    {
        ScenarioRunResult run = ScenarioRunner.Run(ScenarioLibrary.BuildUpThreeVersusTwo(), 60);
        var controller = (TacticalUtilityAiController)run.Engine.GetController(ScenarioLibrary.BuildUpHomeCm);

        // Every live tick is a decision when the scheduler is off.
        Assert.Equal(60, controller.DecisionsComputed);
    }

    // ---- the schedule itself -------------------------------------------------------------------

    [Fact]
    public void Schedule_IsDeterministic()
    {
        var scheduler = new AiScheduler(Scheduled);

        for (int tick = 1; tick <= 40; tick++)
        {
            bool first = scheduler.ShouldRecompute(tick, 7, hasBall: true);
            bool second = scheduler.ShouldRecompute(tick, 7, hasBall: true);
            Assert.Equal(first, second);
        }
    }

    [Fact]
    public void Schedule_HonoursTheConfiguredCadence()
    {
        var scheduler = new AiScheduler(Scheduled);
        const int id = 7;

        int carrierHits = CountHits(scheduler, id, hasBall: true, out int carrierWindow);
        int outfieldHits = CountHits(scheduler, id, hasBall: false, out int outfieldWindow);

        // One hit per window, per class of player.
        Assert.Equal(carrierWindow, carrierHits);
        Assert.Equal(outfieldWindow, outfieldHits);
    }

    [Fact]
    public void Schedule_StaggersPlayersSoTheTeamDoesNotDecideAtOnce()
    {
        // With a 10-tick window and ten players, a clustered schedule would have most of them share a
        // phase. The id-derived offset must spread them.
        int[] offsets = Enumerable.Range(1, 11).Select(id => AiScheduler.PhaseOffset(id, 10)).ToArray();

        Assert.Equal(11, offsets.Length);
        Assert.True(offsets.Distinct().Count() >= 7,
            $"phases collapsed: {string.Join(",", offsets)}");
    }

    [Fact]
    public void Schedule_AlwaysRecomputesWhenTheTickIsUnknown()
    {
        var scheduler = new AiScheduler(Scheduled);

        // A standalone controller (a probe, a unit test) has no engine tick; it must never be left
        // re-emitting an intent forever because tick 0 never lands on a cadence boundary.
        for (int i = 0; i < 12; i++)
            Assert.True(scheduler.ShouldRecompute(0, i, hasBall: true));
        Assert.True(scheduler.ShouldRecompute(-5, 3, hasBall: false));
    }

    [Fact]
    public void SchedulingDisabled_AlwaysRecomputes()
    {
        var scheduler = new AiScheduler(AiRuntimeConfig.Default);

        for (int tick = 0; tick < 30; tick++)
            Assert.True(scheduler.ShouldRecompute(tick, tick % 11 + 1, hasBall: tick % 2 == 0));
    }

    // ---- the executor --------------------------------------------------------------------------

    [Fact]
    public void Intent_WindowIsHalfOpenAndTickBased()
    {
        var intent = new PlayerIntent(CandidateKey.None, "Carry", FootballerCommand.Idle, StartTick: 10, ExpiryTick: 16);

        Assert.False(intent.IsLiveAt(9));
        Assert.True(intent.IsLiveAt(10));
        Assert.True(intent.IsLiveAt(15));
        Assert.False(intent.IsLiveAt(16));
        Assert.Equal(0, intent.AgeTicks(10));
        Assert.Equal(5, intent.AgeTicks(15));
    }

    [Fact]
    public void Executor_ReEmitsTheLiveCommandAndRefusesAnExpiredOne()
    {
        var executor = new IntentExecutor();
        var command = ScenarioCommands.Move(new Vector2(1f, 0f), sprint: true);
        executor.Set(new PlayerIntent(CandidateKey.None, "Carry", command, 10, 16));

        Assert.True(executor.TryGetLiveCommand(12, out FootballerCommand live));
        Assert.Equal(command, live);

        Assert.False(executor.TryGetLiveCommand(16, out _));

        executor.Clear();
        Assert.False(executor.HasIntent);
    }

    // ---- through the controller ----------------------------------------------------------------

    [Fact]
    public void SchedulingOn_SkipsScoringInsideTheWindowButKeepsEmittingTheSameCommand()
    {
        var controller = new TacticalUtilityAiController(new PitchDimensions(), null, Scheduled);
        var scheduler = new AiScheduler(Scheduled);
        const int id = 8;

        int computeTick = FindTick(scheduler, id, hasBall: true, mustRecompute: true);
        int reuseTick = computeTick + 1;
        Assert.False(scheduler.ShouldRecompute(reuseTick, id, hasBall: true),
            "the fixture assumes the next tick is outside the cadence window");

        FootballerCommand first = controller.Decide(Observation(id, onBall: true, tick: computeTick), SimConstants.FixedDeltaTime);
        int afterFirst = controller.DecisionsComputed;

        FootballerCommand second = controller.Decide(Observation(id, onBall: true, tick: reuseTick), SimConstants.FixedDeltaTime);

        Assert.Equal(1, afterFirst);
        Assert.Equal(afterFirst, controller.DecisionsComputed);
        Assert.Equal(first, second);
        Assert.NotNull(controller.CurrentIntent);
    }

    [Fact]
    public void SchedulingOn_DecidesAtTheCadenceOverAScenario()
    {
        ScenarioRunResult run = ScenarioRunner.Run(WithAi(ScenarioLibrary.BuildUpThreeVersusTwo(), Scheduled), 120);
        var carrier = (TacticalUtilityAiController)run.Engine.GetController(ScenarioLibrary.BuildUpHomeCm);

        // The carrier keeps the ball in this fixture, so it uses the 6-tick carrier cadence: about 20
        // evaluations in 120 ticks, and certainly not 120.
        Assert.InRange(carrier.DecisionsComputed, 10, 45);
        Assert.True(carrier.DecisionsComputed < 120);
    }

    [Fact]
    public void SchedulingOn_TheMatchStillPlaysAndReachesFullTime()
    {
        ScenarioRunResult run = ScenarioRunner.Run(WithAi(ScenarioLibrary.BuildUpThreeVersusTwo(), Scheduled), 300);

        Assert.Equal(300, run.TicksRun);
        Assert.NotEmpty(run.ContactAttempts);
        Assert.True(run.Engine.Rules.MatchClockSeconds > 0f);
    }

    [Fact]
    public void SchedulingOn_IsStillDeterministic()
    {
        ScenarioRunResult first = ScenarioRunner.Run(WithAi(ScenarioLibrary.HighLineRun(), Scheduled), 180);
        ScenarioRunResult second = ScenarioRunner.Run(WithAi(ScenarioLibrary.HighLineRun(), Scheduled), 180);

        Assert.Equal(first.BallPosition, second.BallPosition);
        Assert.Equal(first.Possession, second.Possession);
        Assert.Equal(first.ContactAttempts.Count, second.ContactAttempts.Count);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static int CountHits(AiScheduler scheduler, int playerId, bool hasBall, out int window)
    {
        int hits = 0;
        int first = -1;
        for (int tick = 1; tick <= 600; tick++)
        {
            if (!scheduler.ShouldRecompute(tick, playerId, hasBall)) continue;
            if (first < 0) first = tick;
            hits++;
        }

        window = hits;
        return first < 0 ? 0 : hits;
    }

    private static int FindTick(AiScheduler scheduler, int playerId, bool hasBall, bool mustRecompute)
    {
        for (int tick = 1; tick < 60; tick++)
            if (scheduler.ShouldRecompute(tick, playerId, hasBall) == mustRecompute) return tick;
        throw new InvalidOperationException("no tick matched the requested schedule state");
    }

    private static FootballerObservation Observation(int id, bool onBall, int tick) => new()
    {
        SelfId = id,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CentralMidfielder,
        SelfPosition = new Vector3(0f, 0f, 0f),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = onBall,
        ControlQuality01 = onBall ? 1f : 0f,
        PerceivedBallPosition = onBall ? new Vector3(0.4f, 0f, 0f) : new Vector3(9f, 0f, 3f),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = onBall ? TeamId.Home : TeamId.Away,
        BallLastTouchedByPlayerId = id,
        BallControlState = onBall ? BallControlState.Controlled : BallControlState.Loose,
        BallControllingPlayerId = onBall ? id : null,
        TacticalAnchorPosition = Vector3.Zero,
        AttackingDirectionX = 1f,
        MatchTimeSeconds = tick / 60f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = tick,
        VisiblePlayers = new List<PerceivedEntity>
        {
            new(9, TeamId.Home, TacticalRole.Striker, new Vector3(20f, 0f, 2f), Vector3.Zero, 0.9f)
        },
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = new PlayerTendencies(),
        SelfAttributes = new PlayerAttributes()
    };

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
