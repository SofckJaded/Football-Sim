using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// Regression tests for the pass-model findings of the 2026-11-22 behaviour audit (AIB-02a…AIB-02d):
/// <list type="bullet">
/// <item><b>AIB-02a</b> — the lane step-in modelled a defender's close as a flat
/// <c>LaneTurnSpeed = 1.5 m/s</c> after the reaction delay, so a stationary defender covered only
/// <c>0.90 + 1.5 × 0.75 = 2.02 m</c> in a 1.0 s pass where the locomotion model gives him
/// <c>0.5 × 6.8 × 0.75² = 1.91 m</c> of acceleration plus the real ~1.24 m receive reach.</item>
/// <item><b>AIB-02b</b> — the completion estimate took only the single worst defender, so three defenders
/// clustered around the reception scored exactly like one.</item>
/// <item><b>AIB-02c</b> — the airborne lane test skipped every defender within
/// <see cref="PassSuccessModel.AirborneLandingContestMeters"/> of the receiver regardless of whether the
/// landing contest actually priced him, so a lofted ball into contested space was priced as safe.</item>
/// <item><b>AIB-02d</b> — <see cref="PassPlanner.PlanGroundPass"/> could not hedge the lead of a receiver
/// who has not committed to a run, and its crisp-power retry started below the anchored power.</item>
/// </list>
///
/// These are model-level tests on synthetic inputs in the house style of <c>AudFixModelsTests</c>: every
/// fixture is built so the pre-fix answer is visible from the arithmetic in the comment, and the
/// assertion is written so it <b>fails on the pre-fix code</b> and passes after.
/// </summary>
public sealed class AibPassSuccessTests
{
    // ---------------------------------------------------------------------------------------------
    // AIB-02a — the lane step-in is an acceleration/reach question, not a 1.5 m/s turn speed
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A stationary defender 3.2 m off the lane, 13 m into a 16 m pass at power 0.55, is on the ball in
    /// time: the reach model needs 0.71 s to get within 1.24 m of the lane point and the ball takes
    /// ~1.24 s to get there, leaving 0.99 s after the 0.25 s reaction.
    ///
    /// Pre-fix this was <b>not</b> a block: the step-in covered <c>closingSpeed + LaneTurnSpeed</c> =
    /// <c>1.5 m/s</c> for 0.99 s = 1.49 m against a gap of <c>3.2 − 0.90 = 2.30 m</c>. The fixed model
    /// covers 3.2 m (acceleration ramp to 8.4 m/s plus the 1.24 m reach) and the assertion below fails on
    /// the pre-fix formula.
    /// </summary>
    [Fact]
    public void AIB02a_LaneStepIn_UsesRealAccelerationAndReach()
    {
        PassPlanner planner = Planner;
        const float length = 16f;
        const float power = 0.55f;
        const float projection = 13f;
        const float perpendicular = 3.2f;

        Assert.True(planner.TryGetFlightTime(power, length, out float flight));
        Assert.True(planner.TryGetFlightTime(power, projection, out float timeToPoint));
        float available = timeToPoint - PassSuccessModel.OpponentReactionSeconds;
        Assert.True(available > 0.5f, $"fixture gives only {available:F2} s after the reaction");

        // The pre-fix step-in, stated so the fixture cannot silently stop exercising the defect.
        float preFixGap = perpendicular - PassSuccessModel.LaneContactReachMeters;
        float preFixCover = 1.5f * available; // LaneTurnSpeed = 1.5 m/s, no acceleration
        Assert.True(preFixGap > preFixCover,
            $"fixture is inside the pre-fix reach: gap {preFixGap:F2} m vs {preFixCover:F2} m covered");

        PerceivedEntity defender = Opponent(
            14, new Vector2(projection, perpendicular), Vector2.Zero);

        bool blocked = Model.IsLaneBlocked(
            Vector2.Zero, new Vector2(length, 0f), power, flight,
            new[] { defender }, TeamId.Home);

        Assert.True(blocked,
            $"a stationary defender {perpendicular:F1} m off a 16 m lane can reach it in " +
            $"{available:F2} s ({flight:F2} s flight) and must block the pass");
    }

    /// <summary>
    /// The same geometry must stay open for a firm pass: at power 1.00 the ball reaches the 13 m point in
    /// ~0.80 s, leaving ~0.55 s, and the defender needs 0.71 s to close 3.2 m. This is the "do not
    /// over-block" guard the audit asks for — a fast ball down an open lane is still a good pass.
    /// </summary>
    [Fact]
    public void AIB02a_FastPassDownTheSameOpenLane_StaysOpen()
    {
        PassPlanner planner = Planner;
        const float length = 16f;
        const float power = 1.00f;
        const float projection = 13f;
        const float perpendicular = 3.2f;

        Assert.True(planner.TryGetFlightTime(power, length, out float flight));
        Assert.True(planner.TryGetFlightTime(power, projection, out float timeToPoint));
        Assert.True(timeToPoint - PassSuccessModel.OpponentReactionSeconds < 0.60f,
            "the fixture must give the defender less time than the 3.2 m close needs");

        PerceivedEntity defender = Opponent(
            14, new Vector2(projection, perpendicular), Vector2.Zero);

        bool blocked = Model.IsLaneBlocked(
            Vector2.Zero, new Vector2(length, 0f), power, flight,
            new[] { defender }, TeamId.Home);

        Assert.False(blocked, "a fast pass must not be blocked by a defender who cannot get across in time");
    }

    /// <summary>
    /// Monotonicity: for the same defender and lane, a firmer pass can only be less blocked. Before the
    /// fix the step-in distance was linear in the available time; after it is the reach model's own
    /// acceleration ramp, which is also monotone in the available time — so the ordering must hold, and it
    /// is what stops the new model from rejecting every pass.
    /// </summary>
    [Fact]
    public void AIB02a_FirmerPass_IsNeverMoreBlocked()
    {
        PassPlanner planner = Planner;
        const float length = 16f;
        PerceivedEntity defender = Opponent(14, new Vector2(13f, 3.2f), Vector2.Zero);

        bool seenOpen = false;
        for (int index = 0; index <= 16; index++)
        {
            float power = 0.20f + 0.05f * index;
            if (!planner.TryGetFlightTime(power, length, out float flight)) continue;

            bool blocked = Model.IsLaneBlocked(
                Vector2.Zero, new Vector2(length, 0f), power, flight,
                new[] { defender }, TeamId.Home);

            if (seenOpen) Assert.False(blocked, $"power {power:F2} re-blocked a lane that a firmer ball opened");
            if (!blocked) seenOpen = true;
        }

        Assert.True(seenOpen, "the fixture never produced an open lane; it does not exercise the ordering");
    }

    /// <summary>
    /// A defender already running into the lane is still caught by the sampled-flight test: the old
    /// closing-velocity rule must not have been lost with <c>LaneTurnSpeed</c>. Crossing at 6 m/s reaches
    /// the 8 m point (where the ball is at ~0.67 s) at ~0.67 s, so the ball runs straight into him.
    /// </summary>
    [Fact]
    public void AIB02a_DefenderRunningIntoTheLane_StillBlocks()
    {
        PassPlanner planner = Planner;
        const float length = 16f;
        const float power = 0.55f;
        Assert.True(planner.TryGetFlightTime(power, length, out float flight));

        PerceivedEntity crosser = Opponent(
            14, new Vector2(8f, 4f), new Vector2(0f, -6f));

        bool blocked = Model.IsLaneBlocked(
            Vector2.Zero, new Vector2(length, 0f), power, flight,
            new[] { crosser }, TeamId.Home);

        Assert.True(blocked, "a defender running into the lane must still block the pass");
    }

    // ---------------------------------------------------------------------------------------------
    // AIB-02b — the arrival estimate accumulates multi-defender congestion
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One defender can be at the reception around the time the ball is; three can. Pre-fix all four
    /// fixtures scored identically because the estimate kept only the minimum arrival. The crowd factor is
    /// bounded: two extra defenders are charged, a fourth changes nothing, and a defender who cannot be
    /// there in the window changes nothing either.
    ///
    /// Arithmetic: the 10 m plan is struck at power 0.55, flight 0.87 s. A defender 3.5 m off the
    /// reception needs 0.76 s to reach it (0.71 s for 3.2 m in the AIB-02a test above) plus the 0.25 s
    /// reaction ⇒ 1.01 s, inside the 0.87 + 0.5 s congestion window. None of them blocks the lane: the
    /// step-in needs more than the 0.62 s the ball leaves after the reaction.
    /// </summary>
    [Fact]
    public void AIB02b_CrowdedReception_ScoresWorseThanAOneOnOne()
    {
        FootballerState self = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        Vector2 reception = new(10f, 0f);
        PerceivedEntity receiver = Mate(9, reception);
        PassPlan plan = Planner.PlanGroundPass(Vector2.Zero, 9, reception, Vector2.Zero, 8f);
        Assert.True(plan.Feasible);
        Assert.Equal(10f, Vector2.Distance(Vector2.Zero, plan.TargetXZ), 2);

        PassSuccessTerms one = EstimateWith(self, receiver, plan, 3.5f);
        PassSuccessTerms two = EstimateWith(self, receiver, plan, 3.5f, 4.3f);
        PassSuccessTerms three = EstimateWith(self, receiver, plan, 3.5f, 4.3f, 5.2f);
        PassSuccessTerms four = EstimateWith(self, receiver, plan, 3.5f, 4.3f, 5.2f, 6.0f);
        PassSuccessTerms oneAndFar = EstimateWith(self, receiver, plan, 3.5f, 12f);

        Assert.False(one.LaneBlocked, "the fixture must not be a blocked lane: the crowd is the penalty");
        Assert.Equal(0, one.CongestionExtraDefenders);
        Assert.Equal(1, two.CongestionExtraDefenders);
        Assert.Equal(2, three.CongestionExtraDefenders);

        // Pre-fix: all five completions are exactly the same number (the minimum arrival is unchanged).
        Assert.True(two.CompletionProbability01 < one.CompletionProbability01 - 1e-4f,
            $"one man ({one.CompletionProbability01:F3}) scored the same as two ({two.CompletionProbability01:F3})");
        Assert.True(three.CompletionProbability01 < two.CompletionProbability01 - 1e-4f,
            $"a third defender ({three.CompletionProbability01:F3}) must cost more than the second");
        Assert.Equal(
            three.CompletionProbability01, four.CompletionProbability01, 5);
        Assert.Equal(one.CompletionProbability01, oneAndFar.CompletionProbability01, 5);
        Assert.True(oneAndFar.CongestionExtraDefenders == 0,
            "a defender outside the arrival window must not be charged as congestion");
    }

    /// <summary>
    /// The congestion factor is the bounded multiplier the estimate documents, not an unbounded product:
    /// with two extra defenders it is <c>1 − 2 × 0.12 = 0.76</c>, and a fifth contesting defender adds
    /// nothing.
    /// </summary>
    [Fact]
    public void AIB02b_CongestionFactor_IsBounded()
    {
        FootballerState self = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        Vector2 reception = new(10f, 0f);
        PerceivedEntity receiver = Mate(9, reception);
        PassPlan plan = Planner.PlanGroundPass(Vector2.Zero, 9, reception, Vector2.Zero, 8f);
        Assert.True(plan.Feasible);

        PassSuccessTerms one = EstimateWith(self, receiver, plan, 3.5f);
        PassSuccessTerms three = EstimateWith(self, receiver, plan, 3.5f, 4.3f, 5.2f);

        Assert.Equal(1f, one.CongestionFactor01, 5);
        Assert.Equal(1f - 2f * PassSuccessModel.DefenderCongestionStep01, three.CongestionFactor01, 5);

        // The occupancy itself is still exactly the pre-fix single-worst computation: the crowd is a
        // bounded extra factor, not a replacement for the arrival term.
        Assert.True(three.CompletionProbability01 > 0.2f,
            $"the bounded crowd factor must not zero an otherwise ordinary pass ({three.CompletionProbability01:F3})");
    }

    // ---------------------------------------------------------------------------------------------
    // AIB-02c — the airborne landing contest is priced, and the lob over a block survives
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A raised ball whose landing several defenders can reach late is a contested ball. One defender is
    /// priced by the arrival term exactly as before; two cost an extra 12 %, three (or more) 24 %. Pre-fix
    /// all of these read as one defender. The landing is learnt from the model itself so the fixture
    /// follows the calibration instead of hard-coding metres.
    /// </summary>
    [Fact]
    public void AIB02c_ContestedLanding_ScoresWorseThanAnOpenOne()
    {
        FootballerState self = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        PerceivedEntity receiver = Mate(9, new Vector2(20f, 0f));
        FootballerObservation open = Observation(self, receiver);

        LoftedPassTerms baseline = Model.EstimateLofted(open, receiver, 20f, laneClearance01: 1f);
        Assert.True(baseline.Feasible);
        Vector2 landing = baseline.LandingXZ;
        Vector2 aim = SimMath.SafeNormalize(
            landing - new Vector2(self.Position.X, self.Position.Z), new Vector2(1f, 0f));
        Vector2 normal = new(-aim.Y, aim.X);

        LoftedPassTerms one = Raised(self, receiver, landing, normal * 10.5f);
        LoftedPassTerms two = Raised(self, receiver, landing, normal * 10.5f, -normal * 10.5f);
        LoftedPassTerms three = Raised(self, receiver, landing, normal * 10.5f, -normal * 10.5f, normal * 13f);
        LoftedPassTerms four = Raised(
            self, receiver, landing, normal * 10.5f, -normal * 10.5f, normal * 13f, -normal * 13f);

        Assert.Equal(0, baseline.CongestionExtraDefenders);
        Assert.Equal(0, one.CongestionExtraDefenders);
        Assert.Equal(1, two.CongestionExtraDefenders);
        Assert.Equal(2, three.CongestionExtraDefenders);
        Assert.True(two.CompletionProbability01 < one.CompletionProbability01 - 1e-4f,
            $"a landing two men can reach ({two.CompletionProbability01:F3}) must score worse than one " +
            $"({one.CompletionProbability01:F3})");
        Assert.True(three.CompletionProbability01 < two.CompletionProbability01 - 1e-4f);
        Assert.Equal(three.CompletionProbability01, four.CompletionProbability01, 5);
    }

    /// <summary>
    /// A lob over a compact block must still be offered: the block stands where the ball is above
    /// <see cref="PassSuccessModel.AirborneBlockHeightMeters"/>, so those defenders are not blockers, and a
    /// landing nobody can reach early is not congestion either.
    /// </summary>
    [Fact]
    public void AIB02c_LobOverACompactBlock_IsNotBlocked()
    {
        FootballerState self = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        PerceivedEntity receiver = Mate(9, new Vector2(20f, 0f));

        LoftedPassTerms open = Model.EstimateLofted(Observation(self, receiver), receiver, 20f, 1f);
        Assert.True(open.Feasible);

        // The block stands on the line but only 8–12 m out, where the lofted ball is still well above
        // heading height (the trajectory is measured, not assumed: the estimate's own height profile).
        var block = new[]
        {
            Opponent(14, new Vector2(8f, 0.6f)),
            Opponent(15, new Vector2(10f, 0.6f)),
            Opponent(16, new Vector2(12f, 0.6f))
        };

        LoftedPassTerms through = Model.EstimateRaised(
            Observation(self, Append(new[] { receiver }, block)), receiver, open.TargetXZ,
            RaisedPassProfile.Lofted, laneClearance01: 1f);

        Assert.True(through.Feasible);
        Assert.False(through.LaneBlocked,
            "the ball is above heading height over the whole compact block: it is a lob, not a blocked pass");
        Assert.True(through.CompletionProbability01 > PassSuccessModel.MinCompletion01 * 4f,
            $"a lob over a block must stay a real option ({through.CompletionProbability01:F3})");
    }

    /// <summary>
    /// AIB-02c: inside the landing-contest zone a defender is exempt from the height-aware block only when
    /// the arrival term actually prices him for that contest. A defender who cannot get to the landing
    /// (here a heavy, slow body) but stands under the falling ball is a block — the old code skipped the
    /// whole last six metres unconditionally, so this pass read as unblocked.
    /// </summary>
    [Fact]
    public void AIB02c_UnpricedDefenderUnderTheFallingBall_IsABlock()
    {
        FootballerState self = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        PerceivedEntity receiver = Mate(9, new Vector2(20f, 0f));
        LoftedPassTerms open = Model.EstimateLofted(Observation(self, receiver), receiver, 20f, 1f);
        Assert.True(open.Feasible);
        Vector2 landing = open.LandingXZ;
        Assert.True(landing.X > 15f, "fixture assumes the 20 m loft lands near the receiver");

        // 4 m short of the landing on the line: the calibrated lofted trajectory is below 2.2 m there, so
        // a body that can reach the ball plays it.
        Vector2 spot = new(landing.X - 4f, landing.Y + 0.5f);
        PerceivedEntity heavy = Opponent(14, spot, Vector2.Zero,
            new CapabilityEstimate(
                MaxSprintSpeed: 0.5f, Acceleration: 0.5f, AgilityTurnRate: 1f,
                ReachRadius: 0.75f, Confidence01: 0f));

        LoftedPassTerms blocked = Model.EstimateRaised(
            Observation(self, Append(new[] { receiver }, new[] { heavy })), receiver, open.TargetXZ,
            RaisedPassProfile.Lofted, laneClearance01: 1f);

        Assert.True(blocked.Feasible);
        Assert.True(blocked.LaneBlocked,
            "a body under the falling ball that the landing contest does not price is a block");
        Assert.True(blocked.CompletionProbability01 <= PassSuccessModel.BlockedRaisedCompletionCap01 + 1e-4f);
    }

    /// <summary>
    /// The other half of the same rule: a defender the arrival term <i>does</i> price is exempt from the
    /// block, because the contest is already in the completion (the standing brief's no-duplicate-penalty
    /// rule). A fast defender near the landing must therefore not flip the height-aware verdict.
    /// </summary>
    [Fact]
    public void AIB02c_PricedLandingContestor_IsNotDoubleCharged()
    {
        FootballerState self = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        PerceivedEntity receiver = Mate(9, new Vector2(20f, 0f));
        LoftedPassTerms open = Model.EstimateLofted(Observation(self, receiver), receiver, 20f, 1f);
        Assert.True(open.Feasible);
        Vector2 landing = open.LandingXZ;

        PerceivedEntity contestor = Opponent(14, new Vector2(landing.X - 4f, landing.Y + 0.5f));
        LoftedPassTerms priced = Model.EstimateRaised(
            Observation(self, Append(new[] { receiver }, new[] { contestor })), receiver, open.TargetXZ,
            RaisedPassProfile.Lofted, laneClearance01: 1f);

        Assert.True(priced.Feasible);
        Assert.False(priced.LaneBlocked,
            "a defender the arrival term already zeroes must not be charged again as a blocker");
    }

    /// <summary>
    /// AIB-02c, scoping half: the crowd factor prices the <b>raised alternative</b> — the unpromised loft
    /// over a blocked ground lane — and must not also charge a <b>cross</b>. A5.5 deliberately prices the
    /// cross's aerial contest through the widened arrival scale and the reception side (where the ball is
    /// aimed at the runner, not at the space), so a second charge for the same late-arriving contestors is
    /// the duplicate penalty the standing brief forbids. Measured on the boxed-in-winger fixture of
    /// <c>DeliveryChoiceTests</c>: the 12 % crowd charge took a real box cross from 1.15 to 0.93 and made it
    /// lose to a 0.99 carry, so the duplicate charge — not the cross — was the defect.
    ///
    /// Pre-fix (before the scoping): both profiles charged the crowd, so a cross lost 12 % of its completion
    /// to two defenders arriving half a second late at the landing.
    /// </summary>
    [Fact]
    public void AIB02c_CrossDelivery_IsNotDoubleChargedForTheAerialContest()
    {
        FootballerState self = Player(
            8, TeamId.Home, TacticalRole.Winger, new Vector3(32.4f, 0f, 20f));
        PerceivedEntity runner = Mate(9, new Vector2(40f, -2f));
        Vector2 region = new(41.5f, -6f);

        LoftedPassTerms open = Model.EstimateRaised(
            Observation(self, runner), runner, region, RaisedPassProfile.Cross, laneClearance01: 1f);
        Assert.True(open.Feasible);
        Vector2 landing = open.LandingXZ;
        Vector2 aim = SimMath.SafeNormalize(
            landing - new Vector2(self.Position.X, self.Position.Z), new Vector2(1f, 0f));
        Vector2 normal = new(-aim.Y, aim.X);

        // Two defenders arriving about half a second after the ball: inside the crowd window the raised
        // alternative charges, but they are the aerial contest A5.5 already prices for a cross.
        PerceivedEntity near = Opponent(14, landing + normal * 10.4f);
        PerceivedEntity late = Opponent(15, landing + normal * 12f);

        LoftedPassTerms crossOne = CrossOrLoft(self, runner, region, RaisedPassProfile.Cross, near);
        LoftedPassTerms crossTwo = CrossOrLoft(self, runner, region, RaisedPassProfile.Cross, near, late);
        LoftedPassTerms loftOne = CrossOrLoft(self, runner, region, RaisedPassProfile.Lofted, near);
        LoftedPassTerms loftTwo = CrossOrLoft(self, runner, region, RaisedPassProfile.Lofted, near, late);

        Assert.Equal(0, crossTwo.CongestionExtraDefenders);
        Assert.Equal(1f, crossTwo.CongestionFactor01, 5);
        Assert.Equal(
            crossOne.CompletionProbability01, crossTwo.CompletionProbability01, 5);

        // The charge itself is not disabled: the same two bodies still crowd the raised alternative.
        Assert.True(loftTwo.CongestionExtraDefenders >= 1,
            "the crowd factor must still price the raised alternative's contested landing");
        Assert.Equal(1f - PassSuccessModel.DefenderCongestionStep01, loftTwo.CongestionFactor01, 5);
        Assert.True(loftTwo.CompletionProbability01 < loftOne.CompletionProbability01 - 1e-4f,
            $"a second late contestor must still cost the raised alternative " +
            $"({loftTwo.CompletionProbability01:F3} against {loftOne.CompletionProbability01:F3})");
    }

    // ---------------------------------------------------------------------------------------------
    // AIB-02d — planner: uncommitted lead hedge, retry goes firmer, not gentler
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A receiver jogging sideways at 5 m/s has no committed run, so a pass to his feet must not be aimed
    /// <c>velocity × flightTime</c> metres ahead of him. Pre-fix every <see cref="PassPlanner"/> caller got
    /// the full modelled lead (here ~5 m): the audit's "pass to absolutely nowhere" when he stops or turns
    /// back. The hedged plan is still feasible — the solve freezes the receiver at the bound instead of
    /// refusing the pass — and the committed plan keeps its honest lead.
    /// </summary>
    [Fact]
    public void AIB02d_UncommittedReceiver_GetsAHedgedLead()
    {
        PassPlanner planner = Planner;
        Vector2 reception = new(10f, 0f);
        Vector2 jogging = new(0f, 5f);

        PassPlan committed = planner.PlanGroundPass(
            Vector2.Zero, 9, reception, jogging, 8f, maxLeadMeters: 6f);
        PassPlan uncommitted = planner.PlanGroundPass(
            Vector2.Zero, 9, reception, jogging, 8f, maxLeadMeters: 6f,
            receiverCommittedToRun: false);

        Assert.True(committed.Feasible);
        Assert.True(uncommitted.Feasible);

        float committedLead = Vector2.Distance(reception, committed.TargetXZ);
        float uncommittedLead = Vector2.Distance(reception, uncommitted.TargetXZ);

        Assert.True(committedLead > PassPlanner.UncommittedLeadMaxMeters + 0.5f,
            $"the fixture must have a lead worth hedging (was {committedLead:F2} m)");
        Assert.True(uncommittedLead <= PassPlanner.UncommittedLeadMaxMeters + 1e-3f,
            $"an uncommitted lead of {uncommittedLead:F2} m exceeds the hedge");
        Assert.True(uncommittedLead < committedLead - 0.5f);
    }

    /// <summary>
    /// The committed runner is untouched: a through-ball style lead into an announced run must still be
    /// aimed where the runner will be, or the A4.2 lead model is undone.
    /// </summary>
    [Fact]
    public void AIB02d_CommittedRunner_KeepsItsLead()
    {
        PassPlanner planner = Planner;
        Vector2 reception = new(10f, 0f);

        PassPlan committed = planner.PlanGroundPass(
            Vector2.Zero, 9, reception, new Vector2(0f, 6f), 8f, maxLeadMeters: 10f);

        Assert.True(committed.Feasible);
        Assert.True(Vector2.Distance(reception, committed.TargetXZ) > 3f,
            "a committed runner's lead must not be hedged down to the uncommitted bound");
    }

    /// <summary>
    /// A pass whose crisp solve fails must be retried <b>firmer</b>. The fixture is a 15 m pass to a
    /// 6 m/s lateral runner with an 8 m lead bound: the anchored power (0.75) solves at ~1.45 s, a lead of
    /// ~8.7 m, and is refused; the retry finds a firmer power whose shorter flight fits inside the bound.
    ///
    /// Pre-fix code started the retry at <c>PowerIndexForDistance(15) + 1 = 5</c> (power 0.45), i.e. below
    /// the crisp anchor. That could not actually rescue the solve (a gentler ball lengthens the flight and
    /// therefore the very lead it was refused for) and it discarded a caller's <c>minPower01</c>; this test
    /// pins the intended invariant — the returned plan is never gentler than the anchored power.
    /// </summary>
    [Fact]
    public void AIB02d_FailedCrispSolve_RetriesFirmer()
    {
        PassPlanner planner = Planner;
        Vector2 reception = new(15f, 0f);
        Vector2 lateral = new(0f, 6f);
        const float leadBound = 8f;

        float anchor = AnchoredPower(planner, 15f, minPower01: 0f);
        PassPlan plan = planner.PlanGroundPass(
            Vector2.Zero, 9, reception, lateral, 8f, maxLeadMeters: leadBound);

        Assert.True(plan.Feasible);
        Assert.True(plan.Power01 > anchor + 1e-3f,
            $"the retried plan ({plan.Power01:F2}) must be firmer than the anchor ({anchor:F2}) that failed");
        Assert.True(Vector2.Distance(reception, plan.TargetXZ) <= leadBound + 1e-3f,
            "the retry must not buy feasibility with an out-of-bound lead");
    }

    /// <summary>
    /// The caller's power floor survives the retry and the pitch-clamp re-anchor: a human caller asking for
    /// at least 0.60 must never get a gentler ball back.
    /// </summary>
    [Fact]
    public void AIB02d_RetriedPlan_RespectsTheCallersPowerFloor()
    {
        PassPlanner planner = Planner;
        const float floor = 0.60f;
        PassPlan plan = planner.PlanGroundPass(
            Vector2.Zero, 9, new Vector2(15f, 0f), new Vector2(0f, 6f), 8f,
            maxLeadMeters: 8f, minPower01: floor, freezeReceiverAtLeadBound: false);

        Assert.True(plan.Feasible);
        Assert.True(plan.Power01 >= floor - 1e-4f,
            $"a plan at {plan.Power01:F2} dropped the caller's {floor:F2} power floor");
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures and helpers
    // ---------------------------------------------------------------------------------------------

    private static PassPlanner Planner => new(new PitchDimensions());

    private static PassSuccessModel Model => new(Planner);

    /// <summary>
    /// Estimates the 10 m feet pass with one or more opponents placed <paramref name="offLateral"/> metres
    /// off the reception point on alternating sides. No defender is on the lane: this isolates the crowd
    /// factor from the block verdict.
    /// </summary>
    private static PassSuccessTerms EstimateWith(
        FootballerState self,
        PerceivedEntity receiver,
        PassPlan plan,
        params float[] offLateral)
    {
        var visible = new List<PerceivedEntity> { receiver };
        for (int index = 0; index < offLateral.Length; index++)
        {
            float side = index % 2 == 0 ? 1f : -1f;
            visible.Add(Opponent(
                20 + index, new Vector2(10f, side * offLateral[index])));
        }

        return Model.Estimate(Observation(self, visible.ToArray()), receiver, plan, laneClearance01: 1f);
    }

    /// <summary>
    /// Estimates a 20 m lofted ball to the receiver, with the defenders placed relative to the landing the
    /// model itself produces.
    /// </summary>
    private static LoftedPassTerms Raised(
        FootballerState self,
        PerceivedEntity receiver,
        Vector2 landing,
        params Vector2[] offsets)
    {
        var visible = new List<PerceivedEntity> { receiver };
        for (int index = 0; index < offsets.Length; index++)
            visible.Add(Opponent(20 + index, landing + offsets[index]));

        FootballerObservation observation = Observation(self, visible.ToArray());
        return Model.EstimateRaised(
            observation, receiver, landing, RaisedPassProfile.Lofted, laneClearance01: 1f);
    }

    /// <summary>
    /// Estimates an airborne delivery of the requested profile aimed at <paramref name="region"/>, with the
    /// <paramref name="contests"/> defenders appended to the visible list next to the receiver.
    /// </summary>
    private static LoftedPassTerms CrossOrLoft(
        FootballerState self,
        PerceivedEntity receiver,
        Vector2 region,
        RaisedPassProfile profile,
        params PerceivedEntity[] contests)
    {
        var visible = new List<PerceivedEntity> { receiver };
        visible.AddRange(contests);
        return Model.EstimateRaised(
            Observation(self, visible.ToArray()), receiver, region, profile, laneClearance01: 1f);
    }

    /// <summary>
    /// The documented <see cref="PassPlanner"/> power anchor: the crisper of the lowest trapable-reach power
    /// and the lowest firm-pace power, raised to the caller's floor.
    /// </summary>
    private static float AnchoredPower(PassPlanner planner, float distance, float minPower01)
    {
        float trapable = 1f;
        float crisp = 1f;
        bool foundTrapable = false;
        bool foundCrisp = false;
        for (int index = 0; index < 17; index++)
        {
            float power = 0.20f + 0.05f * index;
            if (!foundTrapable && planner.RangeFor(power) >= distance)
            {
                trapable = power;
                foundTrapable = true;
            }

            if (!foundCrisp && planner.CrispRangeFor(power) >= distance)
            {
                crisp = power;
                foundCrisp = true;
            }
        }

        float anchor = MathF.Max(trapable, crisp);
        if (minPower01 > 0f) anchor = MathF.Max(anchor, minPower01);
        return anchor;
    }

    private static FootballerObservation Observation(FootballerState self, params PerceivedEntity[] visible) =>
        Observation(self.Id, self.Team, self.Role, self.Position, visible);

    private static PerceivedEntity[] Append(PerceivedEntity[] head, PerceivedEntity[] tail)
    {
        var all = new PerceivedEntity[head.Length + tail.Length];
        head.CopyTo(all, 0);
        tail.CopyTo(all, head.Length);
        return all;
    }

    private static FootballerState Player(int id, TeamId team, TacticalRole role, Vector3 position) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(role),
        Position = position,
        FacingXZ = new Vector2(team == TeamId.Home ? 1f : -1f, 0f)
    };

    private static PerceivedEntity Mate(int playerId, Vector2 position) => new(
        playerId, TeamId.Home, TacticalRole.CentralMidfielder,
        new Vector3(position.X, 0f, position.Y), Vector3.Zero, 0.95f)
    {
        Capability = CapabilityEstimate.Generic
    };

    private static PerceivedEntity Opponent(int playerId, Vector2 position, Vector2 velocity = default) =>
        Opponent(playerId, position, velocity, CapabilityEstimate.Generic);

    private static PerceivedEntity Opponent(
        int playerId,
        Vector2 position,
        Vector2 velocity,
        CapabilityEstimate capability) => new(
        playerId, TeamId.Away, TacticalRole.CenterBack,
        new Vector3(position.X, 0f, position.Y),
        new Vector3(velocity.X, 0f, velocity.Y), 0.95f)
    {
        Capability = capability
    };

    private static FootballerObservation Observation(
        int selfId, TeamId selfTeam, TacticalRole selfRole, Vector3 selfPosition, params PerceivedEntity[] visible) => new()
    {
        SelfId = selfId,
        SelfTeam = selfTeam,
        SelfRole = selfRole,
        SelfPosition = selfPosition,
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(selfTeam == TeamId.Home ? 1f : -1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = selfPosition,
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = selfTeam,
        BallLastTouchedByPlayerId = selfId,
        TacticalAnchorPosition = selfPosition,
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 10f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = 100,
        VisiblePlayers = visible,
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = new PlayerTendencies(),
        SelfAttributes = new PlayerAttributes()
    };
}
