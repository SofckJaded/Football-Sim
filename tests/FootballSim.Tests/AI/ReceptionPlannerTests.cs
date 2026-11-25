using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A7.3 — planning the first touch before the ball arrives.
///
/// Each test states one receiver picture and asks what he plans. The milestone's claims are checked here:
/// the intended receiver of a pass in flight plans a touch; the plan is chosen from feasible
/// (orientation, touch) pairs rather than assumed; a fresh sighting of the presser supports shield/escape
/// while an aged (memory-only) belief does not; an A7.2 man-on call changes the choice; "cannot say" stays
/// a value instead of a mean pressure; and the plan prepares the next action (pass/shot/cross).
/// </summary>
public sealed class ReceptionPlannerTests
{
    private const int ReceiverId = 106;
    private const int ForwardMateId = 107;
    private const int PresserId = 205;
    private const int BoxMateId = 109;

    private static readonly Vector2 BallInFlightPosition = new(6f, 0f);
    private static readonly Vector2 BallInFlightVelocity = new(-5f, 0f);

    // A slower ball the receiver meets almost on the spot: the pressure tests need the meeting point to
    // be near the receiver so "the presser is close to the reception" is stated honestly.
    private static readonly Vector2 SlowBallPosition = new(1.5f, 0f);
    private static readonly Vector2 SlowBallVelocity = new(-3f, 0f);

    // ---------------------------------------------------------------------------------------------
    // A plan exists exactly for the intended receiver of a pass in flight.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheIntendedReceiver_PlansTheFirstTouch()
    {
        FootballerObservation obs = Receiving(BallInFlightPosition, BallInFlightVelocity);

        var planner = new ReceptionPlanner(new PitchDimensions());
        Assert.True(planner.TryPlan(obs, out ReceptionPlan plan));

        Assert.True(plan.IsPlanned);
        Assert.Equal(ReceiverId, obs.IntendedReceiverId);
        Assert.InRange(plan.ArrivalSeconds, 0f, ReceptionPlanner.PlanHorizonSeconds);
        Assert.InRange(plan.Score, 0f, 1f);
        Assert.Equal(ReceptionPlanner.MaxOptions, planner.OptionCount);
    }

    [Fact]
    public void APassToSomebodyElse_IsNotThisPlayersReceptionToPlan()
    {
        FootballerObservation obs = Receiving(BallInFlightPosition, BallInFlightVelocity,
            intendedReceiverId: ForwardMateId);

        Assert.False(new ReceptionPlanner(new PitchDimensions()).TryPlan(obs, out _));
    }

    [Fact]
    public void AControlledBall_IsNotAReceptionToPlan()
    {
        FootballerObservation obs = Receiving(BallInFlightPosition, BallInFlightVelocity,
            controlState: BallControlState.Controlled);

        Assert.False(new ReceptionPlanner(new PitchDimensions()).TryPlan(obs, out _));
    }

    [Fact]
    public void APlanNeedsAMeetableFlight()
    {
        // The ball is running away from the player: no sample can ever be met inside the horizon, so the
        // planner declines instead of inventing a meeting point behind the ball.
        FootballerObservation obs = Receiving(new Vector2(2f, 0f), new Vector2(10f, 0f));

        Assert.False(new ReceptionPlanner(new PitchDimensions()).TryPlan(obs, out _));
    }

    // ---------------------------------------------------------------------------------------------
    // Beliefs: fresh sighting, aged memory, A7.2 call, and "cannot say".
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AFreshPresserCloseToTheMeetingPoint_PlansAShield()
    {
        FootballerObservation obs = Receiving(SlowBallPosition, SlowBallVelocity,
            visible: [Mate(ForwardMateId, 14f, 5f), Opponent(PresserId, -2.5f, 0f)]);

        Assert.True(new ReceptionPlanner(new PitchDimensions()).TryPlan(obs, out ReceptionPlan plan));

        Assert.Equal(ReceptionTouchKind.Shield, plan.TouchKind);
        Assert.True(plan.PressureKnown);
        Assert.True(plan.Pressure01 > 0.3f, $"the presser only registered {plan.Pressure01:F2} pressure");
    }

    [Fact]
    public void TheSamePresserAsAnAgedBelief_DoesNotJustifyAShield()
    {
        // The exact same world, except the sighting is 30 ticks old: the A2.2 memory's positional
        // uncertainty is now large, so shielding (which leans on where the presser is) loses to the touch
        // that assumes nothing. This is the scanner/memory half of the exit gate, stated as one difference.
        FootballerObservation fresh = Receiving(SlowBallPosition, SlowBallVelocity,
            visible: [Mate(ForwardMateId, 14f, 5f), Opponent(PresserId, -2.5f, 0f)]);
        FootballerObservation aged = Receiving(SlowBallPosition, SlowBallVelocity,
            visible:
            [
                Mate(ForwardMateId, 14f, 5f),
                Opponent(PresserId, -2.5f, 0f, ageTicks: 30, confidence: 0.5f, uncertaintyMeters: 1.0f)
            ]);

        var planner = new ReceptionPlanner(new PitchDimensions());
        Assert.True(planner.TryPlan(fresh, out ReceptionPlan freshPlan));
        Assert.True(planner.TryPlan(aged, out ReceptionPlan agedPlan));

        Assert.Equal(ReceptionTouchKind.Shield, freshPlan.TouchKind);
        Assert.NotEqual(ReceptionTouchKind.Shield, agedPlan.TouchKind);
        Assert.True(agedPlan.PressureUncertainty01 > freshPlan.PressureUncertainty01);
    }

    [Fact]
    public void AManOnCallToTheReceiver_ChangesThePlan_WhenTheSightingIsAged()
    {
        // A7.2's channel, A7.3's consumer: the shout says "somebody is on you". It raises the pressure
        // belief and makes the shield plan available again even though the receiver's own sighting is old.
        // The two pictures differ only in the call.
        PerceivedEntity agedPresser = Opponent(PresserId, -2.5f, 0f, ageTicks: 30, confidence: 0.5f,
            uncertaintyMeters: 1.0f);
        FootballerObservation silent = Receiving(SlowBallPosition, SlowBallVelocity,
            visible: [Mate(ForwardMateId, 14f, 5f), agedPresser]);
        FootballerObservation warned = Receiving(SlowBallPosition, SlowBallVelocity,
            visible: [Mate(ForwardMateId, 14f, 5f), agedPresser],
            calls: new TeamCallContext(TeamCallKind.ManOn, CallerId: 102, NamedPlayerId: 0,
                LineDirective: TeamCallKind.None));

        var planner = new ReceptionPlanner(new PitchDimensions());
        Assert.True(planner.TryPlan(silent, out ReceptionPlan silentPlan));
        Assert.True(planner.TryPlan(warned, out ReceptionPlan warnedPlan));

        Assert.Equal(ReceptionTouchKind.Shield, warnedPlan.TouchKind);
        Assert.NotEqual(ReceptionTouchKind.Shield, silentPlan.TouchKind);
        Assert.True(warnedPlan.PressureKnown);
        Assert.True(warnedPlan.Pressure01 >= ReceptionPlanner.ManOnPressure01 - 1e-3f);
        Assert.Equal(ReceptionPlanner.ManOnUncertainty01, warnedPlan.PressureUncertainty01, precision: 3);
    }

    [Fact]
    public void NoPressureEvidence_IsAnUnknownPressure_NotAFreeOne()
    {
        // "Cannot say" is a value: the plan reports that no presser is known, and it does not substitute
        // a mean (0.5) or claim the space is free (0). The uncertainty floor stays visible.
        FootballerObservation obs = Receiving(BallInFlightPosition, BallInFlightVelocity,
            visible: [Mate(ForwardMateId, 14f, 5f)]);

        Assert.True(new ReceptionPlanner(new PitchDimensions()).TryPlan(obs, out ReceptionPlan plan));

        Assert.False(plan.PressureKnown);
        Assert.Equal(0f, plan.Pressure01);
        Assert.True(plan.PressureUncertainty01 >= ReceptionPlanner.NoPressureEvidenceUncertainty01);
    }

    [Fact]
    public void ASearchingPicture_IsMoreUncertainAboutSpaceThanAFreshOne()
    {
        // No opponent in either picture: the difference is how stale the whole picture is
        // (FootballerObservation.KnowledgeAgeTicks). A player whose picture is 30 ticks old cannot claim
        // the space is free as confidently as one who has just looked around.
        FootballerObservation fresh = Receiving(BallInFlightPosition, BallInFlightVelocity,
            visible: [Mate(ForwardMateId, 14f, 5f)], knowledgeAgeTicks: 0);
        FootballerObservation searching = Receiving(BallInFlightPosition, BallInFlightVelocity,
            visible: [Mate(ForwardMateId, 14f, 5f)], knowledgeAgeTicks: 30);

        var planner = new ReceptionPlanner(new PitchDimensions());
        Assert.True(planner.TryPlan(fresh, out ReceptionPlan freshPlan));
        Assert.True(planner.TryPlan(searching, out ReceptionPlan searchingPlan));

        Assert.True(searchingPlan.PressureUncertainty01 > freshPlan.PressureUncertainty01);
    }

    // ---------------------------------------------------------------------------------------------
    // Feasible (orientation, touch) pairs.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AStrideTouch_IsRefused_WhenTheRunAheadIsBlocked()
    {
        // Running straight at a ball the receiver meets almost on the spot with an opponent standing where
        // the stride touch would put the ball: the pair is refused with the stated reason rather than
        // scored as a mediocre option.
        FootballerObservation obs = Receiving(SlowBallPosition, SlowBallVelocity,
            visible: [Mate(ForwardMateId, 14f, 5f), Opponent(PresserId, 2.0f, 0f)],
            selfVelocity: new Vector3(7f, 0f, 0f));

        var planner = new ReceptionPlanner(new PitchDimensions());
        Assert.True(planner.TryPlan(obs, out _));

        ReceptionOption stride = OptionOf(planner, ReceptionTouchKind.StrideTouch);
        Assert.False(stride.Feasible);
        Assert.Equal("no room ahead for a stride touch", stride.Reason);
    }

    [Fact]
    public void AnEscapeTouch_IsRefused_WhenTheEscapeLaneIsBlocked()
    {
        // The nearest presser stands in front of the reception, so the natural escape is back and away
        // from him; another opponent standing on that exact touch point refuses it. Shielding remains
        // feasible — a refusal is about the lane, not about the whole plan.
        FootballerObservation obs = Receiving(SlowBallPosition, SlowBallVelocity,
            visible:
            [
                Mate(ForwardMateId, 14f, 5f),
                Opponent(PresserId, -2.5f, 0f),
                Opponent(206, 2.0f, 0.1f),
                Opponent(207, -0.3f, -0.8f)
            ]);

        var planner = new ReceptionPlanner(new PitchDimensions());
        Assert.True(planner.TryPlan(obs, out _));

        Assert.False(OptionOf(planner, ReceptionTouchKind.EscapePressure).Feasible, OptionsText(planner));
        Assert.True(OptionOf(planner, ReceptionTouchKind.Shield).Feasible, OptionsText(planner));
    }

    [Fact]
    public void AnOpenBody_IsRefused_WhenTheBodyCannotTurnInTime()
    {
        // The ball is nearly on the receiver, the body faces the wrong way and this body turns slowly:
        // there is no time to open up before contact, so the half-turn pair is refused instead of being
        // commanded into the ground.
        FootballerObservation obs = Receiving(new Vector2(1.2f, 0f), new Vector2(-8f, 0f),
            visible: [Mate(ForwardMateId, 14f, 5f)],
            selfFacingXZ: new Vector2(-1f, 0f),
            attributes: new PlayerAttributes { AgilityTurnRate = 2f });

        var planner = new ReceptionPlanner(new PitchDimensions());
        Assert.True(planner.TryPlan(obs, out _));

        ReceptionOption openBody = OptionOf(planner, ReceptionTouchKind.OpenBody);
        Assert.False(openBody.Feasible);
        Assert.Equal("cannot open the body by arrival", openBody.Reason);
    }

    [Fact]
    public void EveryKindIsEvaluated_AndEveryRefusalCarriesAReason()
    {
        FootballerObservation obs = Receiving(BallInFlightPosition, BallInFlightVelocity,
            visible: [Mate(ForwardMateId, 14f, 5f), Opponent(PresserId, -2.5f, 0f)]);

        var planner = new ReceptionPlanner(new PitchDimensions());
        Assert.True(planner.TryPlan(obs, out _));

        var seen = new HashSet<ReceptionTouchKind>();
        for (int i = 0; i < planner.OptionCount; i++)
        {
            ReceptionOption option = planner.GetOption(i);
            Assert.True(seen.Add(option.TouchKind), $"{option.TouchKind} was evaluated twice");
            if (!option.Feasible) Assert.False(string.IsNullOrEmpty(option.Reason));
        }

        Assert.Equal(ReceptionPlanner.MaxOptions, seen.Count);
    }

    // ---------------------------------------------------------------------------------------------
    // The next action.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AReceptionUpThePitch_PreparesAPass_AndOpensTheBodyTowardTheFurthestMate()
    {
        FootballerObservation obs = Receiving(BallInFlightPosition, BallInFlightVelocity,
            visible: [Mate(ForwardMateId, 14f, 5f), Mate(108, 20f, -10f)]);

        Assert.True(new ReceptionPlanner(new PitchDimensions()).TryPlan(obs, out ReceptionPlan plan));

        Assert.Equal(ReceptionNextAction.Pass, plan.NextAction);
        Assert.True(plan.FacingXZ.X > 0.8f, $"the body opened toward ({plan.FacingXZ.X:F2},{plan.FacingXZ.Y:F2})");
        Assert.True(plan.NextActionValue > 0f);
    }

    [Fact]
    public void AReceptionInsideShootingRange_PreparesAShot()
    {
        FootballerObservation obs = Receiving(new Vector2(46f, 0f), new Vector2(-6f, 0f),
            visible: [Mate(ForwardMateId, 30f, 5f)],
            selfPosition: new Vector2(40f, 0f));

        Assert.True(new ReceptionPlanner(new PitchDimensions()).TryPlan(obs, out ReceptionPlan plan));

        Assert.Equal(ReceptionNextAction.Shot, plan.NextAction);
        Assert.True(plan.FacingXZ.X > 0.8f);
    }

    [Fact]
    public void AWideAdvancedReception_PreparesACross()
    {
        FootballerObservation obs = Receiving(new Vector2(36f, 20f), new Vector2(-6f, 0f),
            visible: [Mate(BoxMateId, 45f, 5f)],
            selfPosition: new Vector2(31f, 20f));

        Assert.True(new ReceptionPlanner(new PitchDimensions()).TryPlan(obs, out ReceptionPlan plan));

        Assert.Equal(ReceptionNextAction.Cross, plan.NextAction);
        Assert.True(plan.FacingXZ.Y < -0.4f, $"the body opened toward ({plan.FacingXZ.X:F2},{plan.FacingXZ.Y:F2})");
    }

    // ---------------------------------------------------------------------------------------------
    // Determinism.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheSamePicture_ProducesTheSamePlan()
    {
        FootballerObservation obs = Receiving(BallInFlightPosition, BallInFlightVelocity,
            visible: [Mate(ForwardMateId, 14f, 5f), Opponent(PresserId, -2.5f, 0f, ageTicks: 18)]);

        var planner = new ReceptionPlanner(new PitchDimensions());
        Assert.True(planner.TryPlan(obs, out ReceptionPlan first));
        Assert.True(planner.TryPlan(obs, out ReceptionPlan second));

        Assert.Equal(first, second);
        for (int i = 0; i < planner.OptionCount; i++)
            Assert.Equal(planner.GetOption(i), planner.GetOption(i));
    }

    [Fact]
    public void ReceptionPlanner_TightArrival_PromotesKillAndOpenBodyToSprint()
    {
        // Lateral pass arriving ~4.5 m away in ~0.83 s: jogging takes ~0.95 s (> 0.83 - 0.10), while
        // sprinting reaches in ~0.78 s. Kill and OpenBody must promote Sprint == true instead of jogging.
        FootballerObservation obs = Receiving(
            new Vector2(4.5f, -3.5f),
            new Vector2(0f, 5.0f),
            visible: [Mate(ForwardMateId, 18f, 4f)],
            selfPosition: Vector2.Zero,
            selfFacingXZ: new Vector2(1f, 0f));

        var planner = new ReceptionPlanner(new PitchDimensions());
        Assert.True(planner.TryPlan(obs, out _), OptionsText(planner));

        ReceptionOption kill = OptionOf(planner, ReceptionTouchKind.Kill);
        Assert.True(kill.Feasible, OptionsText(planner));
        Assert.True(kill.Sprint, $"tight arrival must promote Kill to Sprint == true ({OptionsText(planner)})");

        ReceptionOption openBody = OptionOf(planner, ReceptionTouchKind.OpenBody);
        Assert.True(openBody.Feasible, OptionsText(planner));
        Assert.True(openBody.Sprint, $"tight arrival must promote OpenBody to Sprint == true ({OptionsText(planner)})");
    }

    [Fact]
    public void AddReceptionCandidates_KeepsSprintEnabled_InsideOneAndHalfMeters()
    {
        // Receiver is jogging at 3 m/s (physically able to meet near (0, 0) without flying past reach),
        // and StrideTouch places the meeting point StrideLeadMeters (0.8 m) ahead — inside the old
        // 1.5 m sprint cutoff, outside 0.35 m.
        FootballerObservation obs = Receiving(
            new Vector2(0f, 2.5f),
            new Vector2(0f, -5.0f),
            visible: [Mate(ForwardMateId, 18f, 4f)],
            selfPosition: Vector2.Zero,
            selfVelocity: new Vector3(3f, 0f, 0f),
            selfFacingXZ: new Vector2(1f, 0f));

        var generator = new CandidateGenerator(new PitchDimensions());
        generator.GenerateOffBallCandidates(
            obs,
            new Vector3(52.5f, 0f, 0f),
            new Vector3(-52.5f, 0f, 0f),
            myInterceptXZ: Vector2.Zero,
            isPrimaryBallResponder: true,
            offsideLineProjX: 40f);

        UtilityCandidate stride = Assert.Single(generator.Candidates,
            c => c.Key.Kind == CandidateKind.Intercept && c.Key.TargetTag == "stride");
        float dist = Vector2.Distance(
            new Vector2(obs.SelfPosition.X, obs.SelfPosition.Z),
            new Vector2(stride.TargetXZ!.Value.X, stride.TargetXZ.Value.Z));
        Assert.InRange(dist, 0.36f, 1.49f);
        Assert.True(stride.Command.Sprint,
            $"AddReceptionCandidates must keep Sprint == true at {dist:F2} m (< 1.5 m cutoff)");
    }

    [Fact]
    public void InterceptPassRay_Fallback_SprintsAndSteersInsideOldCutoffs()
    {
        var aiNoPlanner = new AiRuntimeConfig { EnableReceptionPlanning = false };
        var generator = new CandidateGenerator(new PitchDimensions(), ai: aiNoPlanner);

        // Ball travelling along -Z from (0, 3) to (0, 0). Self at (0.80, 0) is 0.80 m from the ray:
        // inside the old 1.5 m sprint cutoff, outside the unified 0.35 m sprint deadband.
        FootballerObservation obsSprint = Receiving(
            new Vector2(0f, 3.0f),
            new Vector2(0f, -4.0f),
            selfPosition: new Vector2(0.80f, 0f),
            selfFacingXZ: new Vector2(-1f, 0f));
        generator.GenerateOffBallCandidates(
            obsSprint,
            new Vector3(52.5f, 0f, 0f),
            new Vector3(-52.5f, 0f, 0f),
            myInterceptXZ: new Vector2(0f, 0f),
            isPrimaryBallResponder: true,
            offsideLineProjX: 40f);

        UtilityCandidate sprintFallback = Assert.Single(generator.Candidates,
            c => c.Key.Kind == CandidateKind.Intercept);
        Assert.True(sprintFallback.Command.Sprint,
            "InterceptPassRay fallback must sprint at 0.80 m off the ray (old 1.5 m cutoff removed)");

        // Self at (0.25, 0) is inside the old 0.45 m arrival deadband, outside the unified 0.15 m deadband:
        // fallback must still steer toward the ray at (0, 0) rather than halting into ball-facing idle.
        var steerGenerator = new CandidateGenerator(new PitchDimensions(), ai: aiNoPlanner);
        FootballerObservation obsSteer = Receiving(
            new Vector2(0f, 3.0f),
            new Vector2(0f, -4.0f),
            selfPosition: new Vector2(0.25f, 0f),
            selfFacingXZ: new Vector2(-1f, 0f));
        steerGenerator.GenerateOffBallCandidates(
            obsSteer,
            new Vector3(52.5f, 0f, 0f),
            new Vector3(-52.5f, 0f, 0f),
            myInterceptXZ: new Vector2(0f, 0f),
            isPrimaryBallResponder: true,
            offsideLineProjX: 40f);

        UtilityCandidate steerFallback = Assert.Single(steerGenerator.Candidates,
            c => c.Key.Kind == CandidateKind.Intercept);
        Assert.True(steerFallback.Command.DesiredMoveDirectionXZ.X < -0.5f,
            $"InterceptPassRay fallback must steer toward ray at 0.25 m instead of stopping (got DesiredMoveDirectionXZ={steerFallback.Command.DesiredMoveDirectionXZ})");
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers: one receiver picture, stated explicitly.
    // ---------------------------------------------------------------------------------------------

    private static FootballerObservation Receiving(
        Vector2 ballPosition,
        Vector2 ballVelocity,
        IReadOnlyList<PerceivedEntity>? visible = null,
        TeamCallContext calls = default,
        Vector2? selfPosition = null,
        Vector2? selfFacingXZ = null,
        Vector3? selfVelocity = null,
        PlayerAttributes? attributes = null,
        int knowledgeAgeTicks = 0,
        int intendedReceiverId = ReceiverId,
        BallControlState controlState = BallControlState.InFlightPass)
    {
        Vector2 self = selfPosition ?? Vector2.Zero;
        return new FootballerObservation
        {
            SelfId = ReceiverId,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.DefensiveMidfielder,
            SelfPosition = new Vector3(self.X, 0f, self.Y),
            SelfVelocity = selfVelocity ?? Vector3.Zero,
            SelfFacingXZ = selfFacingXZ ?? new Vector2(1f, 0f),
            Scan = ScanStatus.None,
            Calls = calls,
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(ballPosition.X, 0.11f, ballPosition.Y),
            PerceivedBallVelocity = new Vector3(ballVelocity.X, 0f, ballVelocity.Y),
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = 102,
            BallLastTouchedByTeam = TeamId.Home,
            BallControlState = controlState,
            BallControllingPlayerId = null,
            IntendedReceiverId = intendedReceiverId,
            TacticalAnchorPosition = new Vector3(self.X, 0f, self.Y),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 15f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = 700,
            KnowledgeAgeTicks = knowledgeAgeTicks,
            VisiblePlayers = visible ?? [Mate(ForwardMateId, 14f, 5f)],
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.DefensiveMidfielder),
            SelfAttributes = attributes ?? new PlayerAttributes()
        };
    }

    private static PerceivedEntity Mate(int id, float x, float z) =>
        new(id, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(x, 0f, z), Vector3.Zero, 1f)
        {
            Capability = CapabilityEstimate.Generic
        };

    private static PerceivedEntity Opponent(
        int id,
        float x,
        float z,
        int ageTicks = 0,
        float confidence = 1f,
        float uncertaintyMeters = 0f) =>
        new(id, TeamId.Away, TacticalRole.DefensiveMidfielder, new Vector3(x, 0f, z), Vector3.Zero, confidence)
        {
            Capability = CapabilityEstimate.Generic,
            ObservedAgeTicks = ageTicks,
            PositionUncertaintyMeters = uncertaintyMeters
        };

    private static ReceptionOption OptionOf(ReceptionPlanner planner, ReceptionTouchKind kind)
    {
        for (int i = 0; i < planner.OptionCount; i++)
        {
            ReceptionOption option = planner.GetOption(i);
            if (option.TouchKind == kind) return option;
        }

        throw new Xunit.Sdk.XunitException($"the planner evaluated no {kind} option");
    }

    private static string OptionsText(ReceptionPlanner planner)
    {
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < planner.OptionCount; i++)
        {
            if (i > 0) text.Append("; ");
            ReceptionOption option = planner.GetOption(i);
            text.Append(option.Describe())
                .Append(" @(").Append(option.MeetingPointXZ.X.ToString("F2")).Append(',')
                .Append(option.MeetingPointXZ.Y.ToString("F2")).Append(')');
        }

        return text.ToString();
    }

    // ---------------------------------------------------------------------------------------------
    // ai64 — unpressed escape hijack, pre-kick dribble velocity, arrival braking & pressed sprint
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ReceptionPlanner_UnpressedReceiver_DoesNotPickEscapePressureOverKill()
    {
        // Nearest opponent 5.9 m from meeting point => pressure ~0.017; EscapePressure must score
        // below Kill rather than hijacking an unpressed reception via full SpaceWeight (0.10).
        FootballerObservation obs = Receiving(
            new Vector2(8f, 0f),
            new Vector2(-12f, 0f),
            visible: [Mate(ForwardMateId, 14f, 5f), Opponent(PresserId, -3.5f, 0f)],
            selfPosition: Vector2.Zero,
            selfFacingXZ: new Vector2(1f, 0f));

        var planner = new ReceptionPlanner(new PitchDimensions());
        Assert.True(planner.TryPlan(obs, out ReceptionPlan plan));
        Assert.InRange(plan.Pressure01, 0.001f, 0.15f);
        ReceptionOption escape = OptionOf(planner, ReceptionTouchKind.EscapePressure);
        ReceptionOption kill = OptionOf(planner, ReceptionTouchKind.Kill);
        Assert.True(escape.Feasible);
        Assert.True(escape.Score < kill.Score,
            $"Unpressed EscapePressure ({escape.Score:F3}) must not outscore Kill ({kill.Score:F3}) at pressure {plan.Pressure01:F3}");
        Assert.NotEqual(ReceptionTouchKind.EscapePressure, plan.TouchKind);
    }

    [Fact]
    public void ReceptionPlanner_PreKickStaleBallVelocity_DoesNotProjectBogusRun()
    {
        // Pass 4 (t=264) pre-kick reaction delay: ball is still 0.39 m from passer moving at passer dribble speed.
        var passer = new PerceivedEntity(
            102, TeamId.Home, TacticalRole.Winger,
            new Vector3(-3.65f, 0f, -12.15f),
            new Vector3(-5.05f, 0f, 1.25f),
            1f)
        {
            Capability = CapabilityEstimate.Generic
        };

        FootballerObservation obs = Receiving(
            new Vector2(-4.04f, -12.17f),
            new Vector2(-4.74f, 1.20f),
            visible: [passer, Mate(ForwardMateId, 14f, 5f)],
            selfPosition: new Vector2(-9.17f, -6.10f),
            selfVelocity: new Vector3(-5.08f, 0f, -5.05f),
            selfFacingXZ: new Vector2(-0.7f, -0.7f));

        var generator = new CandidateGenerator(new PitchDimensions());
        generator.GenerateOffBallCandidates(
            obs,
            new Vector3(52.5f, 0f, 0f),
            new Vector3(-52.5f, 0f, 0f),
            myInterceptXZ: new Vector2(-10.4f, -11.3f),
            isPrimaryBallResponder: true,
            offsideLineProjX: 40f);

        UtilityCandidate candidate = Assert.Single(generator.Candidates, c => c.Key.Kind == CandidateKind.Intercept);
        Assert.False(candidate.Command.Sprint,
            "Receiver must not sprint toward a phantom pre-kick dribble projection");
        Assert.True(candidate.Command.DesiredMoveDirectionXZ.Length() < 0.2f,
            $"Receiver must hold/brake during pre-kick reaction delay (got {candidate.Command.DesiredMoveDirectionXZ})");
    }

    [Fact]
    public void AddReceptionCandidates_FastApproachingReceiver_BrakesInsteadOfOvershootingMeetingPoint()
    {
        var pitch = new PitchDimensions();
        var receiverPos = new Vector3(0f, 0f, 0f);
        var receiverVel = new Vector3(7.5f, 0f, 0f);
        var ballPos = new Vector3(1.3f, 0.11f, 4.0f);
        var ballVel = new Vector3(0f, 0f, -15.0f);

        FootballerObservation obs = Receiving(
            new Vector2(ballPos.X, ballPos.Z),
            new Vector2(ballVel.X, ballVel.Z),
            visible: [Mate(ForwardMateId, 14f, 5f)],
            selfPosition: new Vector2(receiverPos.X, receiverPos.Z),
            selfVelocity: receiverVel,
            selfFacingXZ: new Vector2(1f, 0f));

        var generator = new CandidateGenerator(pitch);
        generator.GenerateOffBallCandidates(
            obs,
            new Vector3(52.5f, 0f, 0f),
            new Vector3(-52.5f, 0f, 0f),
            myInterceptXZ: new Vector2(1.3f, 0f),
            isPrimaryBallResponder: true,
            offsideLineProjX: 40f);

        UtilityCandidate best = generator.Candidates[0];
        for (int i = 1; i < generator.Candidates.Count; i++)
        {
            if (generator.Candidates[i].UtilityScore > best.UtilityScore)
                best = generator.Candidates[i];
        }

        Assert.False(best.Command.Sprint,
            $"Fast approaching receiver must stop sprinting (selected {best.ActionName})");
        Assert.True(best.Command.DesiredMoveDirectionXZ.Length() < 0.7f,
            $"Fast approaching receiver must brake (move mag={best.Command.DesiredMoveDirectionXZ.Length():F2}, {best.ActionName})");

        // Drive 35 ticks through real execution & ball physics to verify the receiver traps the pass.
        var receiver = new FootballerState
        {
            Id = ReceiverId,
            Team = TeamId.Home,
            ShirtNumber = 6,
            Role = TacticalRole.DefensiveMidfielder,
            Position = receiverPos,
            Velocity = receiverVel,
            FacingXZ = new Vector2(1f, 0f),
            Attributes = new PlayerAttributes(),
            Tendencies = PlayerTendencyProfile.ForRole(TacticalRole.DefensiveMidfielder)
        };
        var ball = new BallState
        {
            Position = ballPos,
            PreviousPosition = ballPos,
            Velocity = ballVel,
            LastTouchedByPlayerId = 102,
            LastTouchedByTeam = TeamId.Home
        };

        var execution = new FootballerExecutionSystem(pitch);
        var ballSolver = new BallPhysicsSolver(pitch: pitch);
        var rng = new SeededRandom(1);
        bool trapped = false;
        for (int tick = 1; tick <= 35; tick++)
        {
            FootballerObservation stepObs = Receiving(
                new Vector2(ball.Position.X, ball.Position.Z),
                new Vector2(ball.Velocity.X, ball.Velocity.Z),
                visible: [Mate(ForwardMateId, 14f, 5f)],
                selfPosition: new Vector2(receiver.Position.X, receiver.Position.Z),
                selfVelocity: receiver.Velocity,
                selfFacingXZ: receiver.FacingXZ);

            generator.Clear();
            generator.GenerateOffBallCandidates(
                stepObs,
                new Vector3(52.5f, 0f, 0f),
                new Vector3(-52.5f, 0f, 0f),
                myInterceptXZ: new Vector2(ball.Position.X, 0f),
                isPrimaryBallResponder: true,
                offsideLineProjX: 40f);

            UtilityCandidate stepBest = generator.Candidates[0];
            for (int i = 1; i < generator.Candidates.Count; i++)
            {
                if (generator.Candidates[i].UtilityScore > stepBest.UtilityScore)
                    stepBest = generator.Candidates[i];
            }

            receiver.LastCommand = stepBest.Command;
            execution.UpdateLocomotionAndContacts(
                new[] { receiver }, ball, rng, SimConstants.FixedDeltaTime,
                receptionAssist: new ReceptionAssistContext(BallControlState.InFlightPass, ReceiverId, HumanPlayerId: null));
            ballSolver.Step(ball, SimConstants.FixedDeltaTime);

            for (int c = 0; c < execution.ContactEvents.Count; c++)
            {
                if (execution.ContactEvents[c].PlayerId == ReceiverId &&
                    execution.ContactEvents[c].Kind == BallContactKind.Collection)
                {
                    trapped = true;
                    break;
                }
            }

            if (trapped) break;
        }

        Assert.True(trapped, "Fast approaching receiver must brake and trap the pass instead of running past it");
    }

    [Fact]
    public void AddReceptionCandidates_PressedShieldReception_SprintsToMeetBallWithoutEarlyShieldSlowdown()
    {
        // Receiver 1.8 m from meeting point with 0.60 s flight under heavy pressure (0.88):
        // Shield option must sprint without engaging ShieldBall slowdown while closing the gap.
        FootballerObservation obs = Receiving(
            new Vector2(1.8f, 7.8f),
            new Vector2(0f, -13.0f),
            visible: [Mate(ForwardMateId, 14f, 5f), Opponent(PresserId, 2.5f, 0f)],
            selfPosition: Vector2.Zero,
            selfVelocity: Vector3.Zero,
            selfFacingXZ: new Vector2(1f, 0f));

        var generator = new CandidateGenerator(new PitchDimensions());
        generator.GenerateOffBallCandidates(
            obs,
            new Vector3(52.5f, 0f, 0f),
            new Vector3(-52.5f, 0f, 0f),
            myInterceptXZ: new Vector2(1.8f, 0f),
            isPrimaryBallResponder: true,
            offsideLineProjX: 40f);

        UtilityCandidate shield = Assert.Single(generator.Candidates,
            c => c.Key.Kind == CandidateKind.Intercept && c.Key.TargetTag == "shield");
        Assert.True(shield.Command.Sprint, "Pressed Shield reception 1.8 m away must sprint to meet the ball");
        Assert.False(shield.Command.ShieldBall, "ShieldBall slowdown must not engage while still > 1.25 m from meeting point");
    }
}
