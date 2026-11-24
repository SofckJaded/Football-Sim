using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
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
        // Receiver is moving at 7 m/s, meets ball near (0, 0), and StrideTouch places the meeting point
        // StrideLeadMeters (0.8 m) ahead — inside the old 1.5 m sprint cutoff, outside 0.35 m.
        FootballerObservation obs = Receiving(
            new Vector2(0f, 2.5f),
            new Vector2(0f, -5.0f),
            visible: [Mate(ForwardMateId, 18f, 4f)],
            selfPosition: Vector2.Zero,
            selfVelocity: new Vector3(7f, 0f, 0f),
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
}
