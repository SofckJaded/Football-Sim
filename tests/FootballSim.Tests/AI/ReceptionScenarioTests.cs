using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Scenarios;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A7.3 — reception planning in a real match.
///
/// The planner tests prove the model; these prove the wiring: the intended receiver of a pass in flight
/// gets reception options instead of the fixed intercept ray, a man-on call addressed to the receiver
/// changes his plan only after the communication delay (the exit gate's "relevant receiver" clause), the
/// ablation reproduces the pre-A7.3 candidate exactly, the producer warns a receiver it can see under
/// pressure, and two identical matches plan identically.
/// </summary>
public sealed class ReceptionScenarioTests
{
    private const int ReceiverId = 106;
    private const int PasserId = 102;
    private const int PresserId = 205;
    private const int ForwardMateId = 107;

    private const int Delay = 18;

    // ---------------------------------------------------------------------------------------------
    // The exit-gate clause: a man-on call changes the receiver's options after its delay.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AManOnCallToTheReceiver_ChangesHisOptions_OnlyAfterItsDelay()
    {
        // The producer is off in both worlds, so the injected shout is the only call either engine can
        // hear; scanning is off too, so the rear presser cannot be refreshed by a danger glance. That
        // isolates the consumer and the delay — exactly the exit gate's A/B.
        MatchScenario scenario = RearReception(new AiRuntimeConfig
        {
            EnableTeamCalls = false,
            EnableScanning = false
        });
        var control = new MatchSimulationEngine(scenario);
        var treated = new MatchSimulationEngine(scenario);

        control.StepTick();
        treated.StepTick();

        Assert.Equal(0, treated.Communication.Calls.PublishedCount);
        Assert.True(treated.Communication.Calls.Publish(
            TeamId.Home, TeamCallKind.ManOn, callerId: 999, ReceiverId, namedPlayerId: 0,
            strength01: 0.9f, publishedTick: treated.CurrentTick, delayTicks: Delay, lifetimeTicks: 27));

        var controlReceiver = (TacticalUtilityAiController)control.GetController(ReceiverId);
        var treatedReceiver = (TacticalUtilityAiController)treated.GetController(ReceiverId);

        // The last tick before the shout is audible: both worlds are the same world.
        while (control.CurrentTick < Delay)
        {
            control.StepTick();
            treated.StepTick();
        }

        FootballerObservation beforeControl = Observation(control);
        FootballerObservation beforeTreated = Observation(treated);
        Assert.True(beforeControl.Calls.IsNeutral);
        Assert.True(beforeTreated.Calls.IsNeutral);
        Assert.Equal(ReceiverId, beforeControl.IntendedReceiverId);
        Assert.Equal(ReceiverId, beforeTreated.IntendedReceiverId);
        Assert.Equal(TableOf(controlReceiver), TableOf(treatedReceiver));
        Assert.NotEqual(ReceptionTouchKind.Shield, controlReceiver.LastReceptionPlan.TouchKind);

        // The delay has passed: the treated receiver hears it, the control receiver does not.
        control.StepTick();
        treated.StepTick();

        FootballerObservation afterControl = Observation(control);
        FootballerObservation afterTreated = Observation(treated);

        Assert.True(afterControl.Calls.IsNeutral);
        Assert.Equal(TeamCallKind.ManOn, afterTreated.Calls.Kind);
        Assert.Equal(999, afterTreated.Calls.CallerId);

        // The call moves the plan: the same picture scores the pressure-relief touches completely
        // differently.
        //
        // AUD-P4 (2026-11-23) moved the expected kind from Shield to EscapePressure, with a documented
        // reason. EscapePressure used to be refused as "unreachable in time" because its 1.3 m off-path
        // body offset pushed the touch point beyond the arrival slack — the very defect AUD-P4 fixes — so
        // Shield was the only pressure-relief option left and won by default. With the body offset back
        // inside the 1.24 m receiving reach (0.50 m, clamped to 0.55 m of the ball's path) the escape is
        // feasible, pays the relief plus its space bonus, and wins. The exit-gate property is unchanged:
        // the shout, and only the shout, changes the touch table, and the treated plan carries the
        // man-on pressure belief.
        Assert.NotEqual(TableOf(controlReceiver), TableOf(treatedReceiver));
        Assert.Equal(ReceptionTouchKind.EscapePressure, treatedReceiver.LastReceptionPlan.TouchKind);
        Assert.NotEqual(ReceptionTouchKind.EscapePressure, controlReceiver.LastReceptionPlan.TouchKind);
        Assert.True(treatedReceiver.LastReceptionPlan.Pressure01 >= ReceptionPlanner.ManOnPressure01 - 1e-3f);

        // A7.4's seam: the plan the receiver decided on is recorded on the decision-side intent.
        PlayerIntent? intent = treatedReceiver.CurrentIntent;
        Assert.NotNull(intent);
        Assert.True(intent!.Value.Reception.IsPlanned);
        Assert.Equal(ReceptionTouchKind.EscapePressure, intent.Value.Reception.TouchKind);
    }

    // ---------------------------------------------------------------------------------------------
    // The ablation and the new option set.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void PlanningOff_KeepsTheExactPreA73InterceptRay()
    {
        var engine = new MatchSimulationEngine(RearReception(PlanningOff));
        for (int i = 0; i < 5; i++) engine.StepTick();

        var receiver = (TacticalUtilityAiController)engine.GetController(ReceiverId);
        Assert.Single(receiver.LastCandidates);
        UtilityCandidate candidate = receiver.LastCandidates[0];
        Assert.Equal("InterceptPassRay", candidate.ActionName);
        Assert.Equal("ray", candidate.Key.TargetTag);
        Assert.Equal(0.98f, candidate.UtilityScore);
        Assert.False(receiver.LastReceptionPlan.IsPlanned);
        Assert.False(receiver.CurrentIntent!.Value.Reception.IsPlanned);
    }

    [Fact]
    public void PlanningOn_GivesTheReceiverFeasibleTouchOptions()
    {
        var engine = new MatchSimulationEngine(RearReception(PlanningOn));
        for (int i = 0; i < 5; i++) engine.StepTick();

        var receiver = (TacticalUtilityAiController)engine.GetController(ReceiverId);
        Assert.True(receiver.LastCandidates.Count >= 3,
            $"the receiver only had {receiver.LastCandidates.Count} reception options");

        foreach (UtilityCandidate candidate in receiver.LastCandidates)
        {
            Assert.Equal(CandidateKind.Intercept, candidate.Key.Kind);
            Assert.StartsWith("Receive", candidate.ActionName);
        }

        Assert.True(receiver.LastReceptionPlan.IsPlanned);
        Assert.True(receiver.CurrentIntent!.Value.Reception.IsPlanned);
    }

    // ---------------------------------------------------------------------------------------------
    // The producer warns a receiver it can see.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheProducer_WarnsTheReceiver_AndTheWarningIsHeardAfterTheDelay()
    {
        // The shipped producer, in a fixture where the presser closes from behind the receiver and the
        // passer can see it: the call reaches the receiver through the 18-tick communication delay.
        // (The stock rear-pressure fixture's pass arrives before the delay can elapse, so this states the
        // same producer rule in a flight long enough to observe the delay.)
        var engine = new MatchSimulationEngine(RearReception(new AiRuntimeConfig
        {
            EnableTeamCalls = true,
            EnableScanning = false
        }, presserX: -3.2f));

        engine.StepTick();
        Assert.True(engine.Communication.Calls.PublishedCount > 0, "the producer made no call at all");

        while (engine.CurrentTick < Delay)
            engine.StepTick();

        FootballerObservation before = engine.GetLastObservation(ReceiverId)!;
        Assert.True(before.Calls.IsNeutral);
        Assert.Equal(ReceiverId, before.IntendedReceiverId);

        engine.StepTick();

        FootballerObservation after = engine.GetLastObservation(ReceiverId)!;
        Assert.Equal(TeamCallKind.ManOn, after.Calls.Kind);
    }

    // ---------------------------------------------------------------------------------------------
    // The height-aware gate: a ball above torso height is a reception, not a kick.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AHighBallNearAReceiver_EntersTheReceivingPath_InsteadOfAskingForKicks()
    {
        // The known defect A7.3 owns: the on-ball gate was planar, so a receiver under a 2.2 m ball was
        // offered the carrier action set and asked for kicks execution refuses as BallTooHigh. The two
        // observations differ only in the perceived ball height; the planar geometry is identical.
        FootballerObservation high = ReceivingObservation(ballY: 2.2f, controlled: false);
        FootballerObservation grounded = ReceivingObservation(ballY: 0.11f, controlled: false);

        var plannerOff = new AiRuntimeConfig { EnableReceptionPlanning = false };
        var highController = new TacticalUtilityAiController(new PitchDimensions(), null, plannerOff);
        var groundedController = new TacticalUtilityAiController(new PitchDimensions(), null, plannerOff);

        highController.Decide(high, SimConstants.FixedDeltaTime);
        groundedController.Decide(grounded, SimConstants.FixedDeltaTime);

        // The high ball is a reception: the receiver enters the receiving path, not the carrier's kick set.
        Assert.Contains(highController.LastCandidates, c => c.ActionName == "InterceptPassRay");
        Assert.DoesNotContain(highController.LastCandidates,
            c => c.Key.Kind is CandidateKind.Pass or CandidateKind.Shot or CandidateKind.Carry);

        // The same planar distance at feet height keeps the pre-A7.3 behaviour exactly.
        Assert.DoesNotContain(groundedController.LastCandidates, c => c.ActionName == "InterceptPassRay");
        Assert.NotEmpty(groundedController.LastCandidates);
    }

    [Fact]
    public void AGroundedBallAtTheFeet_StillGetsTheCarrierActionSet()
    {
        // The gate must not change what "on the ball" means for a ball actually at the feet.
        FootballerObservation controlled = ReceivingObservation(ballY: 0.11f, controlled: true);
        var controller = new TacticalUtilityAiController(new PitchDimensions(), null,
            new AiRuntimeConfig { EnableReceptionPlanning = false });

        controller.Decide(controlled, SimConstants.FixedDeltaTime);

        Assert.DoesNotContain(controller.LastCandidates, c => c.ActionName == "InterceptPassRay");
        Assert.Contains(controller.LastCandidates,
            c => c.Key.Kind is CandidateKind.Pass or CandidateKind.Carry or CandidateKind.Shield
                or CandidateKind.Clearance or CandidateKind.Dribble);
    }

    // ---------------------------------------------------------------------------------------------
    // A7.4: the plan crosses the command boundary and reaches execution.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ThePlan_ReachesExecution_AsTheTouchTheReceiverActuallyTook()
    {
        var engine = new MatchSimulationEngine(RearReception(PlanningOn));
        var receiver = (TacticalUtilityAiController)engine.GetController(ReceiverId);

        string? diagnosticWithIntent = null;
        for (int tick = 1; tick <= 240 && diagnosticWithIntent is null; tick++)
        {
            engine.StepTick();

            foreach (FootballerState player in engine.Players)
            {
                if (player.Id != ReceiverId) continue;
                if (!player.LastExecutionDiagnostic.StartsWith("FirstTouchCushion:", StringComparison.Ordinal))
                    continue;

                diagnosticWithIntent = player.LastExecutionDiagnostic;

                // The command that took the touch carried the plan, and it is the plan the controller
                // decided on this tick — not a stale copy from an earlier decision.
                Assert.True(receiver.LastReceptionPlan.IsPlanned,
                    "the touch carried an intent but the controller recorded no plan");
                Assert.Equal(receiver.LastReceptionPlan.TouchKind, player.LastCommand.ReceptionIntent.TouchKind);
            }
        }

        Assert.NotNull(diagnosticWithIntent);
    }

    [Fact]
    public void PlanningOff_LeavesEveryFirstTouchNeutral()
    {
        var engine = new MatchSimulationEngine(RearReception(PlanningOff));

        bool touched = false;
        for (int tick = 1; tick <= 240; tick++)
        {
            engine.StepTick();

            foreach (FootballerState player in engine.Players)
            {
                if (!player.LastExecutionDiagnostic.StartsWith("FirstTouchCushion", StringComparison.Ordinal))
                    continue;

                Assert.Equal("FirstTouchCushion", player.LastExecutionDiagnostic);
                Assert.False(player.LastCommand.ReceptionIntent.IsPlanned,
                    $"player {player.Id} took an intended first touch with planning off");
                touched = true;
            }
        }

        Assert.True(touched, "the fixture never produced a first touch, so the ablation proved nothing");
    }

    // ---------------------------------------------------------------------------------------------
    // Determinism.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TwoIdenticalMatches_PlanTheSameReceptionsTickForTick()
    {
        var left = new MatchSimulationEngine(RearReception(PlanningOn));
        var right = new MatchSimulationEngine(RearReception(PlanningOn));

        for (int tick = 1; tick <= 40; tick++)
        {
            left.StepTick();
            right.StepTick();

            var leftReceiver = (TacticalUtilityAiController)left.GetController(ReceiverId);
            var rightReceiver = (TacticalUtilityAiController)right.GetController(ReceiverId);

            Assert.Equal(leftReceiver.LastReceptionPlan, rightReceiver.LastReceptionPlan);
            Assert.Equal(TableOf(leftReceiver), TableOf(rightReceiver));
            Assert.Equal(left.Ball.Position, right.Ball.Position);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Fixture and helpers.
    // ---------------------------------------------------------------------------------------------

    private static readonly AiRuntimeConfig PlanningOn = new()
    {
        EnableTeamCalls = false,
        EnableScanning = false
    };

    private static readonly AiRuntimeConfig PlanningOff = new()
    {
        EnableReceptionPlanning = false,
        EnableTeamCalls = false,
        EnableScanning = false
    };

    /// <summary>
    /// The receiver faces up the pitch, the pass is in flight from behind him and the presser closes
    /// from behind his back. The worlds differ only in their AI configuration (and, in the call test,
    /// in one injected shout).
    /// </summary>
    private static MatchScenario RearReception(AiRuntimeConfig ai, float presserX = -5.5f) => new MatchScenario
    {
        Name = "rear reception",
        Seed = 7373UL,
        Ai = ai,
        BallPosition = new Vector3(6f, 0.11f, 0f),
        BallVelocity = new Vector3(-5f, 0f, 0f),
        BallSetup = ScenarioBallSetup.InFlightPass,
        BallOwnerPlayerId = PasserId,
        IntendedReceiverId = ReceiverId
    }
    .Add(MatchScenario.Player(101, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-45f, 0f, 0f), 1))
    .Add(MatchScenario.Player(PasserId, TeamId.Home, TacticalRole.CenterBack, new Vector3(18f, 0f, 6f), 4))
    .Add(Facing(MatchScenario.Player(ReceiverId, TeamId.Home, TacticalRole.DefensiveMidfielder,
        new Vector3(0f, 0f, 0f), 6), new Vector2(1f, 0f)))
    .Add(MatchScenario.Player(ForwardMateId, TeamId.Home, TacticalRole.CentralMidfielder,
        new Vector3(14f, 0f, 8f), 8))
    .Add(MatchScenario.Player(201, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(45f, 0f, 0f), 1))
    .Add(MatchScenario.Player(202, TeamId.Away, TacticalRole.CenterBack, new Vector3(10f, 0f, -12f), 4))
    .Add(Facing(MatchScenario.Player(PresserId, TeamId.Away, TacticalRole.DefensiveMidfielder,
            new Vector3(presserX, 0f, 0.3f), 6), new Vector2(1f, 0f))
        .WithController(new FixedCommandController(ScenarioCommands.Move(new Vector2(1f, 0f)), "presser closes")));

    private static ScenarioPlayer Facing(ScenarioPlayer player, Vector2 facing) => new()
    {
        Id = player.Id,
        Team = player.Team,
        Role = player.Role,
        ShirtNumber = player.ShirtNumber,
        Position = player.Position,
        Velocity = player.Velocity,
        FacingXZ = facing,
        Stamina01 = player.Stamina01,
        Attributes = player.Attributes,
        Tendencies = player.Tendencies,
        IsActiveOnPitch = player.IsActiveOnPitch,
        Controller = player.Controller
    };

    /// <summary>A minimal observation for the controller-level height-gate tests.</summary>
    private static FootballerObservation ReceivingObservation(float ballY, bool controlled) => new()
    {
        SelfId = ReceiverId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.DefensiveMidfielder,
        SelfPosition = Vector3.Zero,
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = new Vector3(controlled ? 0.35f : 0.9f, ballY, 0f),
        PerceivedBallVelocity = controlled ? Vector3.Zero : new Vector3(-2f, 0f, 0f),
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = controlled ? ReceiverId : PasserId,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = controlled ? BallControlState.Controlled : BallControlState.InFlightPass,
        BallControllingPlayerId = controlled ? ReceiverId : null,
        IntendedReceiverId = controlled ? null : ReceiverId,
        IsDesignatedBallResponder = true,
        TacticalAnchorPosition = Vector3.Zero,
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 5f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = 300,
        VisiblePlayers = Array.Empty<PerceivedEntity>(),
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.DefensiveMidfielder),
        SelfAttributes = new PlayerAttributes()
    };

    private static FootballerObservation Observation(MatchSimulationEngine engine) =>
        engine.GetLastObservation(ReceiverId) ??
        throw new Xunit.Sdk.XunitException($"no observation for receiver #{ReceiverId}");

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
}
