using System;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// The pass-interception blind spots of the 2026-11-24 supervisor brief
/// (<c>docs/task-pass-interception-fixes.md</c>): a defender sprinting back into the passing corridor
/// from past the receiver was skipped by the segment guard before any test ran, a stationary/covering
/// defender in the middle of the lane was never asked about points further down the lane, the additive
/// lane term scored ~0.30 completion for a pass whose receiver had already decisively lost the arrival
/// race, and a close presser in front of a passer who had to turn was invisible because the ball was
/// priced as leaving the foot instantly with a full 0.25 s defender reaction.
///
/// <para><b>House style</b> (as <c>Aib3PassPhysicsTests</c>): every fixture re-derives the pre-fix
/// answer from the public reach model and the planner's own tables and asserts it, so the failing half
/// of the fixture cannot silently stop exercising the defect. Each test's XML comment states which
/// assertion fails on the pre-fix code.</para>
/// </summary>
public sealed class PassInterceptionBlindSpotTests
{
    private static readonly PitchDimensions Pitch = new();
    private static readonly PassPlanner Planner = new(Pitch);
    private static readonly PassSuccessModel Model = new(Planner);
    private const int CarrierId = 8;
    private const int MateId = 9;
    private const int ObservationTick = 500;

    // =============================================================================================
    // Case 1 — a defender sprinting back up the lane from behind the receiver
    // =============================================================================================

    /// <summary>
    /// Case 1 of the brief: Blue plays a 19.4 m/s ball (1.60 s flight) down the pitch while Red
    /// <c>PRESSING</c> starts ~1.5 m <b>past</b> the receiver and ~4 m off the lane, sprinting back up
    /// the lane into the corridor. He intercepts it in the rendered session.
    ///
    /// <para><b>Pre-fix defect.</b> The segment guard
    /// (<c>projectionRaw &lt; -0.90 || projectionRaw &gt; length + 0.90</c>) skipped the defender before
    /// Check 1 ever ran. The fixture asserts the run physically meets the ball inside the real reach
    /// (the sampled-run arithmetic below), so the only thing that hides it is the guard.</para>
    /// </summary>
    [Fact]
    public void Pass1_DefenderSprintingUpLaneFromPastTarget_IsDetectedAsBlocked()
    {
        Vector2 from = Vector2.Zero;
        Vector2 to = new(25f, 0f);
        PassPlan plan = Planner.PlanGroundPassToRegion(from, to, arriveInSeconds: 1.60f);
        Assert.True(plan.Feasible, "the audit's 1.60 s 25 m ball must be plannable");
        Assert.True(Vector2.Distance(plan.TargetXZ, to) < 0.05f);
        float flight = plan.FlightTimeSeconds;
        Assert.InRange(flight, 1.40f, 2.10f); // the nearest power-grid flight to the audit's 1.60 s

        // The audit geometry: he starts 1.5 m past the target and 4 m off the lane; his sprint carries him
        // back into the corridor to meet the ball late in the flight.
        float meetSeconds = 0.70f * flight;
        Vector2 meet = from + Vector2.UnitX * Planner.BallDistanceAtTime(plan.Power01, meetSeconds);
        Vector2 start = new(to.X + 1.5f, 4f);
        Vector2 run = (meet - start) / meetSeconds;
        Assert.InRange(run.Length(), 4.5f, 7.5f); // a sprint, not a teleport
        Assert.True(run.X < 0f, "the run must carry him back up the lane");

        PerceivedEntity presser = Opponent(14, start, run);
        float length = Vector2.Distance(from, plan.TargetXZ);
        float projectionRaw = Vector2.Dot(start - from, Vector2.UnitX);
        Assert.True(projectionRaw > length + PassSuccessModel.LaneContactReachMeters,
            $"the fixture must start outside the pre-fix segment band ({projectionRaw:F2} m against " +
            $"{length + PassSuccessModel.LaneContactReachMeters:F2} m), or the guard is not what it tests");

        // Pre-fix half: the constant-velocity run physically meets the ball, so Check 1 (had it run) sees it.
        float closest = ClosestSampledApproach(presser, from, to, plan.Power01, flight, 0f, out float meetingTime);
        Assert.True(closest <= PassSuccessModel.LaneStepInReachMeters,
            $"the fixture's run must physically meet the ball ({closest:F2} m at {meetingTime:F2} s); " +
            "otherwise the guard is not the only thing hiding it");

        Assert.True(Model.IsLaneBlocked(
                from, plan.TargetXZ, plan.Power01, flight, new[] { presser }, TeamId.Home),
            "a defender sprinting back into the corridor from past the target must block the lane; " +
            "pre-fix the projection band skipped him before any test ran");

        // And the estimate prices it as the impossible ball rather than a merely average one: hard cap.
        PerceivedEntity receiver = Mate(MateId, to);
        PassSuccessTerms terms = Model.Estimate(
            Observation(Player(from, Vector2.UnitX), receiver, presser), receiver, plan, laneClearance01: 1f);
        Assert.True(terms.LaneBlocked);
        Assert.True(terms.CompletionProbability01 <= PassSuccessModel.BlockedLaneCompletionCap01 + 1e-4f,
            $"a blocked lane must be capped at {PassSuccessModel.BlockedLaneCompletionCap01:F2} " +
            $"(measured {terms.CompletionProbability01:F3})");
    }

    // =============================================================================================
    // Case 2 — the gifted ball: a past-target presser, a stationary covering defender, and the blend
    // =============================================================================================

    /// <summary>
    /// Case 2, first half: Red <c>SUPPORT_RUN</c> plays a 14.8 m/s ball (0.97 s flight) and Blue
    /// <c>PRESSING</c> sits ~2.5 m past <c>targetXZ</c>, sprinting back up the lane onto it.
    ///
    /// <para><b>Pre-fix defect.</b> Same blanket projection band as Case 1: 2.5 m past the receiver is
    /// outside <c>length + 0.90</c>, so the defender was skipped.</para>
    /// </summary>
    [Fact]
    public void Pass2_PresserSprintingUpLaneFromPastTargetOnTheTargetArrival_IsRejected()
    {
        Vector2 from = Vector2.Zero;
        Vector2 to = new(14f, 0f);
        PassPlan plan = Planner.PlanGroundPassToRegion(from, to, arriveInSeconds: 0.97f);
        Assert.True(plan.Feasible);
        float flight = plan.FlightTimeSeconds;
        Assert.InRange(flight, 0.85f, 1.30f);

        float meetSeconds = 0.90f * flight;
        Vector2 meet = from + Vector2.UnitX * Planner.BallDistanceAtTime(plan.Power01, meetSeconds);
        Vector2 start = new(to.X + 2.5f, 3f);
        Vector2 run = (meet - start) / meetSeconds;
        Assert.InRange(run.Length(), 3.0f, PassSuccessModel.OpponentSprintSpeed + 0.01f);

        PerceivedEntity presser = Opponent(14, start, run);
        float length = Vector2.Distance(from, plan.TargetXZ);
        Assert.True(Vector2.Dot(start - from, Vector2.UnitX) > length + PassSuccessModel.LaneContactReachMeters);

        float closest = ClosestSampledApproach(presser, from, to, plan.Power01, flight, 0f, out _);
        Assert.True(closest <= PassSuccessModel.LaneStepInReachMeters,
            $"the run must physically meet the ball ({closest:F2} m)");

        Assert.True(Model.IsLaneBlocked(
                from, plan.TargetXZ, plan.Power01, flight, new[] { presser }, TeamId.Home),
            "the presser 2.5 m past targetXZ who sprints up the lane onto the arrival must block the ball");
    }

    /// <summary>
    /// Case 2, second half: Blue <c>COVERING</c> stands in the middle of the lane, ~3.4 m off it, facing
    /// the passer (so his along-lane velocity is not positive) and steps into a <b>down-lane</b> point
    /// when he reads the pass.
    ///
    /// <para><b>Pre-fix defect.</b> Check 2 tested only the perpendicular foot projection and
    /// <c>if (alongRun &lt;= 0f) break;</c> skipped the lookahead points entirely, so the point he can
    /// actually step into — the down-lane midpoint — was never asked about. The assertions derive both
    /// halves from <see cref="TimeToReachModel"/> and the planner's own distance/time table.</para>
    /// </summary>
    [Fact]
    public void Pass2_CoveringDefenderMidLane_IsBlockedByTheDownLaneLookahead()
    {
        Vector2 from = Vector2.Zero;
        Vector2 to = new(18f, 0f);
        PassPlan plan = Planner.PlanGroundPassToRegion(from, to, arriveInSeconds: 4f); // the gentlest ball
        Assert.True(plan.Feasible);
        float flight = plan.FlightTimeSeconds;
        Assert.True(flight > 2.5f, $"the fixture needs a slow ball so the late lane points are reachable ({flight:F2} s)");

        const float coveringAlong = 5f;
        const float coveringOff = 3.4f;
        PerceivedEntity covering = Opponent(14, new Vector2(coveringAlong, coveringOff), Vector2.Zero);

        // The sampled run cannot see him: a stationary body 3.4 m off the line never meets the ball.
        float closest = ClosestSampledApproach(covering, from, to, plan.Power01, flight, 0f, out _);
        Assert.True(closest > PassSuccessModel.LaneStepInReachMeters,
            $"the covering body must be outside the sampled reach ({closest:F2} m), or this is not the lookahead's case");

        // Pre-fix half 1: his own perpendicular foot is the earliest point and the full reaction makes it
        // unreachable, which is the only point the old loop asked about before the along-run break.
        Assert.True(Planner.TryGetFlightTime(plan.Power01, coveringAlong, out float footTime));
        float oldAvailable = footTime - PassSuccessModel.OpponentReactionSeconds;
        Assert.True(oldAvailable > 0f);
        Vector2 foot = new(coveringAlong, 0f);
        ReachEstimate oldStep = TimeToReachModel.ToPoint(
            ReachKinematics.FromEntity(covering), foot, oldAvailable);
        Assert.False(oldStep.Reachable && oldStep.Seconds <= oldAvailable,
            $"the old foot-only step-in must be out of reach ({oldStep.Seconds:F2} s against {oldAvailable:F2} s)");

        // Pre-fix half 2: he faces the passer, so his along-lane velocity is not positive and the old loop
        // broke before any lookahead point; only the foot point was ever tested.
        Vector2 coveringVelocity = new(covering.EstimatedVelocity.X, covering.EstimatedVelocity.Z);
        Assert.True(Vector2.Dot(coveringVelocity, Vector2.UnitX) <= 0f);

        // Fixed half: the down-lane midpoint is a point he can step into, and it is exactly the point the
        // audit's COVERING defender takes.
        float midAlong = 0.5f * (coveringAlong + Vector2.Distance(from, to));
        Vector2 mid = new(midAlong, 0f);
        Assert.True(Planner.TryGetFlightTime(plan.Power01, midAlong, out float midTime));
        float midAvailable = midTime - PassSuccessModel.OpponentReactionSeconds;
        ReachEstimate midReach = TimeToReachModel.ToPoint(
            ReachKinematics.FromEntity(covering), mid, midAvailable);
        Assert.True(midReach.Reachable && midReach.Seconds <= midAvailable,
            $"the fixture needs the midpoint to be reachable ({midReach.Seconds:F2} s against {midAvailable:F2} s), " +
            "or the fixed lookahead is not what blocks him");

        Assert.True(Model.IsLaneBlocked(
                from, plan.TargetXZ, plan.Power01, flight, new[] { covering }, TeamId.Home),
            "a covering defender who can step into a down-lane point must block the lane; pre-fix " +
            "the along-run break stopped the loop at his perpendicular foot");
    }

    /// <summary>
    /// Case 2, the blend: a receiver loses the arrival race decisively (<c>defenderTerm == 0</c>) while the
    /// caller's lane metric is perfectly clean (<c>lane == 1</c>).
    ///
    /// <para><b>Pre-fix defect.</b>
    /// <c>receiverTerm · ((1 − LaneWeight)·defenderTerm + LaneWeight·lane)</c> returns <b>0.30</b> for
    /// <c>defenderTerm == 0, lane == 1</c> — exactly <see cref="CandidateGenerator.MinViableDeliveryCompletion01"/>,
    /// so a clean corridor plus the lowered delivery floor admitted the guaranteed turnover. The shipped
    /// lost-race factor (<see cref="PassSuccessModel.LostRaceFactor01"/>, zero at the brief's own
    /// <c>defenderMargin &lt;= −0.20 s</c> with a 0.05 s ramp) prices it at the
    /// <see cref="PassSuccessModel.MinCompletion01"/> floor instead.</para>
    /// </summary>
    [Fact]
    public void Pass2_LostArrivalRace_CannotBeRescuedByACleanLane()
    {
        Vector2 from = Vector2.Zero;
        Vector2 to = new(16f, 0f);
        PassPlan plan = Planner.PlanGroundPassToRegion(from, to, arriveInSeconds: 2.25f);
        Assert.True(plan.Feasible);

        // The defender is already at the arrival point (2 m past it so he is outside the lane corridor and
        // the lane test): he wins the race outright, the receiver has lost it.
        PerceivedEntity receiver = Mate(MateId, to);
        PerceivedEntity waiting = Opponent(14, new Vector2(to.X + 2f, 0f), Vector2.Zero);
        PassSuccessTerms terms = Model.Estimate(
            Observation(Player(from, Vector2.UnitX), receiver, waiting), receiver, plan, laneClearance01: 1f);

        float defenderTerm = Math.Clamp(1f - terms.DefenderPressure01, 0f, 1f);
        Assert.Equal(0f, defenderTerm, 4); // the race is lost: the pre-fix blend's whole premise
        Assert.Equal(1f, terms.LaneClearance01); // the caller's corridor metric is clean
        Assert.Equal(1f, terms.CongestionFactor01, 4); // one defender: no crowd charge
        Assert.False(terms.LaneBlocked);

        // Pre-fix arithmetic: the additive lane term alone cleared the lowered delivery floor.
        float preFixBlend = (1f - PassSuccessModel.LaneWeight) * defenderTerm +
                            PassSuccessModel.LaneWeight * terms.LaneClearance01;
        Assert.Equal(0.30f, preFixBlend, 4);
        Assert.True(preFixBlend >= CandidateGenerator.MinViableDeliveryCompletion01,
            "the pre-fix blend must clear the delivery floor, or the lane is not what admitted the pass");

        // Fixed: the lost-race factor zeroes the lane share once the race is decisively lost.
        Assert.True(terms.CompletionProbability01 <= PassSuccessModel.MinCompletion01 + 1e-4f,
            $"a lost arrival race must price at the floor (measured {terms.CompletionProbability01:F3}, " +
            $"pre-fix blend {preFixBlend:F2})");
    }

    /// <summary>
    /// Case 2 through the real candidate gate (the brief's Task 3 audit): the cleaned-up corridor takes the
    /// <b>lowered</b> 0.30 delivery floor in <c>TryAddGroundPass</c>. With <c>defenderTerm == 0</c> and a
    /// clean corridor the pre-fix completion was exactly 0.30, which is not below the floor, so the pass to
    /// a man whose arrival is already lost was emitted. The shipped lost-race factor prices it at 0.02 and the
    /// gate refuses it for <c>low-completion</c>.
    /// </summary>
    [Fact]
    public void Pass2_CleanCorridorFloor_CannotForceAdmitALostRacePass()
    {
        Vector2 ballXZ = Vector2.Zero;
        PerceivedEntity mate = Mate(MateId, new Vector2(16f, 0f));
        PerceivedEntity waiting = Opponent(14, new Vector2(18f, 0f), Vector2.Zero);
        FootballerObservation observation = Observation(Player(Vector2.Zero, Vector2.UnitX), mate, waiting);
        var generator = new CandidateGenerator(Pitch, communication: null, PlainPicture()) { TimeOnBall = 0.8f };

        generator.GenerateOnBallCandidates(observation, new Vector3(Pitch.HalfLength, 0f, 0f), 40f);

        // Rebuild the same question the gate asks, to prove which half of the arithmetic changed.
        PassPlan plan = Planner.PlanGroundPass(ballXZ, MateId, new Vector2(16f, 0f), Vector2.Zero, 8f);
        Assert.True(plan.Feasible);
        float lane = CandidateGenerator.EvaluateLaneClearance(
            ballXZ, plan.TargetXZ, observation.VisiblePlayers, TeamId.Home);
        PassSuccessTerms terms = Model.Estimate(observation, mate, plan, lane);
        float defenderTerm = Math.Clamp(1f - terms.DefenderPressure01, 0f, 1f);
        Assert.Equal(0f, defenderTerm, 4);
        // CandidateGenerator.LaneCongestionRatio01 is internal; the public gate behaviour is the 0.85 threshold.
        Assert.True(lane >= 0.85f,
            $"the fixture needs a clean corridor so the lowered 0.30 floor applies (lane {lane:F2})");
        Assert.True((1f - PassSuccessModel.LaneWeight) * defenderTerm +
                    PassSuccessModel.LaneWeight * lane >= CandidateGenerator.MinViableDeliveryCompletion01,
            "the pre-fix blend must clear the lowered floor, or the blend is not what admits the pass");

        Assert.DoesNotContain(generator.Candidates, candidate =>
            candidate.Key.Kind == CandidateKind.Pass &&
            candidate.Key.TargetPlayerId == MateId &&
            string.Equals(candidate.Key.TargetTag, "ToFeet", StringComparison.Ordinal));

        bool refusedForCompletion = false;
        for (int i = 0; i < generator.RejectionCount; i++)
        {
            CandidateRejection rejection = generator.GetRejection(i);
            if (rejection.TargetPlayerId == MateId &&
                rejection.Reason == CandidateGenerator.RejectionLowCompletion)
            {
                refusedForCompletion = true;
            }
        }

        Assert.True(refusedForCompletion,
            "the pass to a receiver whose arrival is already lost must be refused for low completion; " +
            "pre-fix its 0.30 blend cleared the lowered delivery floor and it was offered");
    }

    // =============================================================================================
    // Case 3 — the turn-and-kick into a close presser on the launch line
    // =============================================================================================

    /// <summary>
    /// Case 3 of the brief: a pressured carrier turns back upfield (<c>turnAngle ≈ 180°</c>) and kicks into
    /// a presser ~2 m away who is closing across the pass line.
    ///
    /// <para><b>Pre-fix defects.</b> (a) Check 2 asked <c>available = timeToPoint − 0.25 s</c>, which is
    /// negative for a ~2 m point, so it skipped; (b) Check 1 priced the ball as launched at <c>t = 0</c>
    /// with the defender frozen at his pre-turn position, so the ~0.15 s the body needs to turn and swing
    /// was not charged; (c) the close presser was not tested as a launch-cone body that needs no reaction
    /// time. The test asserts the no-delay sampled run stays outside reach (pre-fix half) and that the
    /// fixed model derives a non-zero launch delay from <see cref="FootballerObservation.SelfFacingXZ"/>
    /// and blocks the ball.</para>
    /// </summary>
    [Fact]
    public void Pass3_CarrierTurningIntoAClosePresserOnTheLaunchLine_IsBlocked()
    {
        Vector2 from = Vector2.Zero;
        Vector2 to = new(14f, 0f);
        PassPlan plan = Planner.PlanGroundPassToRegion(from, to, arriveInSeconds: 0.97f);
        Assert.True(plan.Feasible);
        float flight = plan.FlightTimeSeconds;

        Vector2 facing = -Vector2.UnitX; // turned away from the pass
        float turnAngle = MathF.Acos(Math.Clamp(Vector2.Dot(facing, plan.AimDirectionXZ), -1f, 1f));
        Assert.True(turnAngle > 2.5f, $"the fixture needs a near-reversal ({turnAngle:F2} rad)");

        // ~1.8 m ahead on the pass line, ~2.1 m off it, closing at ~7 m/s: a body that shin-blocks a ball
        // struck after the turn but is outside reach of the ball struck at t = 0.
        PerceivedEntity presser = Opponent(14, new Vector2(1.4f, 2.1f), new Vector2(-1.2f, -7.0f));
        Assert.True(Vector2.Distance(new Vector2(1.4f, 2.1f), from) < 2.6f,
            "the presser must be inside the emergency-pressure ring so the carrier still releases the ball");
        Assert.True(Vector2.Dot(presser.EstimatedPosition.X * Vector2.UnitX +
                                new Vector2(0f, presser.EstimatedPosition.Z) - from, plan.AimDirectionXZ) > 0f,
            "the presser must be ahead of the ball on the pass line");

        FootballerObservation observation = Observation(
            Player(from, facing), Mate(MateId, to), presser);

        // Pre-fix half: with instant launch and no turn delay the sampled run never comes within reach.
        float closest = ClosestSampledApproach(presser, from, to, plan.Power01, flight, 0f, out _);
        Assert.True(closest > PassSuccessModel.LaneStepInReachMeters,
            $"without the launch delay the sampled run must be outside reach ({closest:F2} m), " +
            "or the launch delay is not what catches him");

        // The fixed model derives the delay from the body turn, bounded by the locomotion turn rate.
        float delay = PassSuccessModel.LaunchDelaySeconds(observation, plan.AimDirectionXZ);
        Assert.Equal(PassSuccessModel.MaxPassLaunchDelaySeconds, delay, 3);
        Assert.True(delay > 0f);

        Assert.True(Model.IsLaneBlocked(plan, observation),
            "a close presser closing across the launch line must block the ball struck after the turn; " +
            "pre-fix the ball was priced as leaving the foot instantly with the defender a reaction behind");

        // Candidate level: under heavy pressure the pass is still not emitted — the fix must not make the
        // carrier gun-shy but must stop him gifting the ball.
        var generator = new CandidateGenerator(Pitch, communication: null, PlainPicture()) { TimeOnBall = 0.05f };
        generator.GenerateOnBallCandidates(observation, new Vector3(Pitch.HalfLength, 0f, 0f), 40f);

        Assert.DoesNotContain(generator.Candidates, candidate =>
            candidate.Key.Kind == CandidateKind.Pass &&
            candidate.Key.TargetPlayerId == MateId &&
            string.Equals(candidate.Key.TargetTag, "ToFeet", StringComparison.Ordinal));

        bool refusedForTheLane = false;
        for (int i = 0; i < generator.RejectionCount; i++)
        {
            CandidateRejection rejection = generator.GetRejection(i);
            if (rejection.TargetPlayerId == MateId &&
                rejection.Reason == CandidateGenerator.RejectionLaneBlocked)
            {
                refusedForTheLane = true;
            }
        }

        Assert.True(refusedForTheLane,
            "a pressured carrier turning into a close presser must have the ball refused for lane-blocked; " +
            "pre-fix no lane test saw the presser and the emergency floor admitted the pass");
    }

    // =============================================================================================
    // Negative regressions — the fixes must not make the AI gun-shy
    // =============================================================================================

    /// <summary>
    /// Negative regression 1: a chaser 1.5 m behind the passer and 0.4 m off the line — A6.B8's own
    /// fixture — must stay clear of the lane test, and so must a chaser moving away or running alongside.
    /// The removed projection guard must not become a guard that blocks every forward ball a tracked
    /// carrier plays. Pre-fix this test passes too; it exists so the fixed scan cannot break it.
    /// </summary>
    [Fact]
    public void Regression_TrailingChaserBehindThePasser_DoesNotBlockAnOpenPass()
    {
        Vector2 from = new(0.4f, 0f);
        Vector2 to = new(22f, 0f);
        var plan = new PassPlan(true, Vector2.UnitX, to, Power01: 0.80f, FlightTimeSeconds: 1.50f,
            DistanceMeters: 22f);
        PerceivedEntity receiver = Mate(MateId, to);
        PerceivedEntity[] trailing =
        {
            Opponent(14, new Vector2(from.X - 1.5f, from.Y + 0.4f), Vector2.Zero),            // stationary
            Opponent(15, new Vector2(from.X - 1.5f, from.Y + 0.4f), new Vector2(-3f, 0f)),    // receding
            Opponent(16, new Vector2(from.X - 1.5f, from.Y + 0.4f), new Vector2(0f, -3f)),    // stepping away
            Opponent(17, new Vector2(from.X - 1.5f, from.Y + 0.4f), new Vector2(6f, 0f))      // chasing forward
        };

        foreach (PerceivedEntity chaser in trailing)
        {
            Assert.False(Model.IsLaneBlocked(
                    from, plan.TargetXZ, plan.Power01, plan.FlightTimeSeconds, new[] { chaser }, TeamId.Home),
                $"a chaser behind the passer moving ({chaser.EstimatedVelocity.X:F1}, " +
                $"{chaser.EstimatedVelocity.Z:F1}) must not block the forward ball");

            PassSuccessTerms terms = Model.Estimate(
                Observation(Player(from, Vector2.UnitX), receiver, chaser), receiver, plan, laneClearance01: 1f);
            Assert.False(terms.LaneBlocked);
            Assert.True(terms.CompletionProbability01 > 0.5f,
                $"the open pass must keep a real completion ({terms.CompletionProbability01:F2})");
        }
    }

    /// <summary>
    /// Negative regression 2: a genuinely open target keeps its pre-change completion exactly. With
    /// <c>defenderTerm == 1</c> (nobody can reach the arrival) the shipped blend is the original additive
    /// <c>(1 − LaneWeight)·1 + LaneWeight·lane</c> and <see cref="PassSuccessModel.LostRaceFactor01"/> is 1, so
    /// a safe pass must not lose a single point of completion. The pinned 0.85 is the pre-change value for this
    /// fixture (the same arithmetic <c>Aib2PassPhysicsTests</c> records for a 0.5-lane pass).
    /// </summary>
    [Fact]
    public void Regression_OpenTargetWithClearReceiverAdvantage_PreservesTheOriginalCompletion()
    {
        Vector2 from = Vector2.Zero;
        Vector2 to = new(16f, 0f);
        var plan = new PassPlan(true, Vector2.UnitX, to, Power01: 0.35f, FlightTimeSeconds: 2.25f,
            DistanceMeters: 16f);
        PerceivedEntity receiver = Mate(MateId, to);
        const float lane = 0.5f;

        PassSuccessTerms open = Model.Estimate(
            Observation(Player(from, Vector2.UnitX), receiver), receiver, plan, lane);
        Assert.False(open.LaneBlocked);
        Assert.Equal(0f, open.DefenderPressure01, 5); // defenderTerm == 1: the whole invariant's premise
        Assert.Equal(0.85f, open.CompletionProbability01, 4); // (1 − 0.30)·1 + 0.30·0.5, unchanged
        Assert.Equal((1f - PassSuccessModel.LaneWeight) + PassSuccessModel.LaneWeight * lane,
            open.CompletionProbability01, 4);

        // A defender who cannot reach the lane or the arrival at all adds nothing: same completion exactly.
        PerceivedEntity farAway = Opponent(14, new Vector2(-8f, 3f), Vector2.Zero);
        Assert.False(Model.IsLaneBlocked(
            from, plan.TargetXZ, plan.Power01, plan.FlightTimeSeconds, new[] { farAway }, TeamId.Home));
        PassSuccessTerms withBystander = Model.Estimate(
            Observation(Player(from, Vector2.UnitX), receiver, farAway), receiver, plan, lane);
        Assert.Equal(0f, withBystander.DefenderPressure01, 5);
        Assert.Equal(open.CompletionProbability01, withBystander.CompletionProbability01, 4);
    }

    // =============================================================================================
    // Follow-up patch (2026-11-24 supervisor review): ramp boundaries, the launch-delay scope, the
    // zero-reaction cone, the past-target step-in and the long-ball horizon
    // =============================================================================================

    /// <summary>
    /// The brief's lost-race turn is a published ramp, not a hidden multiplier: the exact factor is asserted
    /// at its boundaries and at the A8.3 switch margin the literal multiplicative blend used to delete.
    ///
    /// <para>Boundaries: margin −0.14 s → 1 (close contest preserved), −0.175 s → 0.5 (mid-ramp), −0.20 s and
    /// below → 0 (decisively lost, the brief's own criterion). Above −0.15 s the factor is exactly 1, which is
    /// what keeps the A8.3 switch ball above the delivery floor.</para>
    /// </summary>
    [Fact]
    public void FollowUp_LostRaceFactor_RampBoundaries_AreTheBriefsCriterion()
    {
        Assert.Equal(0.20f, PassSuccessModel.DecisiveRaceMarginSeconds, 4);
        Assert.Equal(0.05f, PassSuccessModel.DecisiveRaceRampSeconds, 4);

        Assert.Equal(1f, PassSuccessModel.LostRaceFactor01(-0.14f), 5);   // A8.3 switch margin: preserved
        Assert.Equal(1f, PassSuccessModel.LostRaceFactor01(-0.15f), 5);   // ramp start
        Assert.Equal(0.5f, PassSuccessModel.LostRaceFactor01(-0.175f), 5); // mid-ramp
        Assert.Equal(0f, PassSuccessModel.LostRaceFactor01(-0.20f), 5);   // the brief's criterion
        Assert.Equal(0f, PassSuccessModel.LostRaceFactor01(-0.30f), 5);   // defenderTerm == 0 region
        Assert.Equal(1f, PassSuccessModel.LostRaceFactor01(0.30f), 5);    // uncontested: identical formula
        Assert.Equal(1f, PassSuccessModel.LostRaceFactor01(float.PositiveInfinity), 5);

        // End-to-end: the A8.3 switch geometry (a far-side winger whose marker arrives 0.14 s before the ball)
        // must keep the factor at 1 and stay above the delivery floor, and the completion must be exactly the
        // published `additive blend × factor` applied to the same no-defender fixture.
        Vector2 from = new(0.4f, 0f);
        Vector2 to = new(10f, 20f);
        var plan = new PassPlan(true, SimMath.SafeNormalize(to - from, Vector2.UnitX), to,
            Power01: 1.00f, FlightTimeSeconds: 1.583f, DistanceMeters: Vector2.Distance(from, to));
        PerceivedEntity receiver = Mate(MateId, to);
        PerceivedEntity marker = Opponent(206, new Vector2(13f, 26f), Vector2.Zero);
        FootballerObservation withMarker = Observation(Player(from, Vector2.UnitX), receiver, marker);
        FootballerObservation withoutMarker = Observation(Player(from, Vector2.UnitX), receiver);

        const float lane = 0.85f; // 0.85 keeps the no-defender completion below MaxCompletion01 so the
                                  // ratio arithmetic below is exact rather than a clamped comparison
        PassSuccessTerms contested = Model.Estimate(withMarker, receiver, plan, lane);
        PassSuccessTerms open = Model.Estimate(withoutMarker, receiver, plan, lane);
        Assert.InRange(contested.DefenderMarginSeconds, -0.18f, -0.08f); // the switch's close contest
        Assert.Equal(1f, PassSuccessModel.LostRaceFactor01(contested.DefenderMarginSeconds), 5);
        Assert.True(contested.CompletionProbability01 >= CandidateGenerator.MinViableDeliveryCompletion01,
            $"the close-contest switch ball must stay viable ({contested.CompletionProbability01:F3}); " +
            "the literal multiplicative blend priced it 0.213 and deleted it");

        float defenderTerm = Math.Clamp(1f - contested.DefenderPressure01, 0f, 1f);
        float defendedLane = (1f - PassSuccessModel.LaneWeight) * defenderTerm +
                             PassSuccessModel.LaneWeight * lane;
        float openBlend = (1f - PassSuccessModel.LaneWeight) + PassSuccessModel.LaneWeight * lane;
        float expected = open.CompletionProbability01 *
                         (defendedLane * PassSuccessModel.LostRaceFactor01(contested.DefenderMarginSeconds)) /
                         openBlend;
        Assert.Equal(expected, contested.CompletionProbability01, 4);
    }

    /// <summary>
    /// Follow-up Bug 2: the defender horizon must not have a 3.5 s ceiling. A wide-open 4.0 s ball (no
    /// defenders at all) used to fall into the no-defender fallback <c>horizon + reaction = 3.75 s</c>, which
    /// is *before* its own 4.0 s contest: the resulting −0.25 s margin priced it as a decisively lost race at
    /// the 0.02 floor. The horizon now matches <c>EstimateRaised</c> (contest + slack, no ceiling).
    /// </summary>
    [Fact]
    public void FollowUp_UncontestedLongBall_IsNotFlooredByTheDefenderHorizon()
    {
        Vector2 from = Vector2.Zero;
        Vector2 to = new(16f, 0f);
        var plan = new PassPlan(true, Vector2.UnitX, to, Power01: 0.20f, FlightTimeSeconds: 4.00f,
            DistanceMeters: 16f);
        PerceivedEntity receiver = Mate(MateId, to);
        PassSuccessTerms terms = Model.Estimate(
            Observation(Player(from, Vector2.UnitX), receiver), receiver, plan, laneClearance01: 1f);

        // The pre-fix shape: the capped horizon's no-defender fallback is before the contest.
        const float oldClampedHorizon = 3.5f;
        float oldMargin = oldClampedHorizon + PassSuccessModel.OpponentReactionSeconds - plan.FlightTimeSeconds;
        Assert.Equal(-0.25f, oldMargin, 3);
        Assert.Equal(0f, PassSuccessModel.LostRaceFactor01(oldMargin), 5); // the pre-fix floor

        // Fixed: the fallback is after the contest, so the open ball keeps its real completion.
        Assert.Equal(0f, terms.DefenderPressure01, 5);
        Assert.True(terms.DefenderMarginSeconds > PassSuccessModel.DecisiveRaceMarginSeconds,
            $"the no-defender margin must be positive ({terms.DefenderMarginSeconds:F2} s)");
        Assert.Equal(1f, PassSuccessModel.LostRaceFactor01(terms.DefenderMarginSeconds), 5);
        Assert.False(terms.LaneBlocked);
        Assert.True(terms.CompletionProbability01 > 0.5f,
            $"an uncontested 4.0 s ball must not be floored by the horizon (measured {terms.CompletionProbability01:F3})");
    }

    /// <summary>
    /// Follow-up Bug 1, first half: the passer's turn lead-in is charged only inside the close-range cone.
    /// Execution has no kick wind-up, so phase-shifting a distant defender invents blocks on clean long balls
    /// and hides a distant runner the shift moves past the ball's arrival window. The helper is the one owner
    /// of the scope, and the behavioural half proves a distant defender's verdict is invariant to the delay.
    /// </summary>
    [Fact]
    public void FollowUp_DistantDefender_IsNotPhaseShiftedByTheLaunchLeadIn()
    {
        Vector2 from = Vector2.Zero;

        // The rule itself (one owner, used by both the reach bound and the sampled run).
        Assert.Equal(0.15f, PassSuccessModel.EffectiveLaunchDelaySeconds(from, new Vector2(4.0f, 0f), 0.15f), 5);
        Assert.Equal(0f, PassSuccessModel.EffectiveLaunchDelaySeconds(from, new Vector2(4.6f, 0f), 0.15f), 5);
        Assert.Equal(0f, PassSuccessModel.EffectiveLaunchDelaySeconds(from, new Vector2(20f, 0f), 0.15f), 5);
        Assert.Equal(0f, PassSuccessModel.EffectiveLaunchDelaySeconds(from, new Vector2(1f, 0f), 0f), 5);
        Assert.True(PassSuccessModel.CloseRangeLaunchConeMeters <= 4.5f);

        // Behavioural half: a defender 20 m from the launch point (the past-target closing runner fixture) gets
        // the same verdict whether or not the caller supplies the turn lead-in.
        Vector2 to = new(18f, 0f);
        PassPlan plan = Planner.PlanGroundPassToRegion(from, to, arriveInSeconds: 4f);
        Assert.True(plan.Feasible);
        PerceivedEntity distant = Opponent(14, new Vector2(20f, 2.5f), new Vector2(-3f, -1f));
        Assert.True(Vector2.Distance(new Vector2(20f, 2.5f), from) > PassSuccessModel.CloseRangeLaunchConeMeters,
            "the fixture's defender must be outside the close-range cone");
        Assert.Equal(
            Model.IsLaneBlocked(from, plan.TargetXZ, plan.Power01, plan.FlightTimeSeconds,
                new[] { distant }, TeamId.Home, launchDelaySeconds: 0f),
            Model.IsLaneBlocked(from, plan.TargetXZ, plan.Power01, plan.FlightTimeSeconds,
                new[] { distant }, TeamId.Home, launchDelaySeconds: PassSuccessModel.MaxPassLaunchDelaySeconds));
    }

    /// <summary>
    /// Follow-up Bug 1, second half: the launch cone is a real <b>zero-reaction step-in</b> through the one
    /// reach model, not a duplicate of the sampled run. Fixture: a short 8 m ball (minimum power, ~0.95 s
    /// flight), the carrier turned 180° from the aim (0.15 s lead-in), and a stationary defender 2 m off the
    /// lane 3.4 m past the launch point — his constant-velocity run stays 2 m from the ball's path (outside
    /// the 1.24 m sampled reach), his own-foot step-in has no time left of the 0.25 s reaction, and the
    /// down-lane midpoint is too far to step to; only the zero-reaction cone can reach him.
    /// </summary>
    [Fact]
    public void FollowUp_CloseRangeLaunchCone_CatchesAnOpponentTheSampledRunMisses()
    {
        Vector2 from = Vector2.Zero;
        Vector2 to = new(8f, 0f);
        PassPlan plan = Planner.PlanGroundPassToRegion(from, to, arriveInSeconds: 3f);
        Assert.True(plan.Feasible);
        float flight = plan.FlightTimeSeconds;
        Assert.True(flight < 1.5f, $"the fixture needs the gentlest short ball ({flight:F2} s)");

        Vector2 facing = -Vector2.UnitX; // the pass demands a reversal, so the lead-in is the maximum
        Vector2 defenderPosition = new(3.4f, 2.0f);
        PerceivedEntity covering = Opponent(14, defenderPosition, Vector2.Zero);
        FootballerObservation observation = Observation(
            Player(from, facing), Mate(MateId, to), covering);

        float delay = PassSuccessModel.LaunchDelaySeconds(observation, plan.AimDirectionXZ);
        Assert.Equal(PassSuccessModel.MaxPassLaunchDelaySeconds, delay, 3);
        Assert.Equal(delay, PassSuccessModel.EffectiveLaunchDelaySeconds(from, defenderPosition, delay), 4);

        // The sampled run cannot see him: a stationary body 2 m off the lane stays 2 m from the ball.
        float closest = ClosestSampledApproach(covering, from, to, plan.Power01, flight, delay, out _);
        Assert.Equal(2f, closest, 2);
        Assert.True(closest > PassSuccessModel.LaneStepInReachMeters,
            $"the sampled run must miss the fixture's defender ({closest:F2} m), or the cone is not isolated");

        // The cone's own arithmetic: zero reaction, the turn lead-in plus the ball's time to his foot point.
        Assert.True(Planner.TryGetFlightTime(plan.Power01, 3.4f, out float coneArrival));
        float coneAvailable = delay + coneArrival;
        float coneMustRun = Vector2.Distance(defenderPosition, new Vector2(3.4f, 0f)) -
                            PassSuccessModel.LaneStepInReachMeters;
        ReachEstimate coneStep = TimeToReachModel.ToPoint(
            ReachKinematics.FromEntity(covering), new Vector2(3.4f, 0f), coneAvailable);
        Assert.True(coneStep.Reachable && coneStep.Seconds <= coneAvailable,
            $"the fixture must be reachable by a zero-reaction step in {coneAvailable:F2} s " +
            $"(reach {coneStep.Seconds:F2} s, must run {coneMustRun:F2} m)");

        // The down-lane midpoint step-in is genuinely out of reach, so the cone is what catches him.
        Assert.True(Planner.TryGetFlightTime(plan.Power01, 5.7f, out float midTime));
        float midAvailable = midTime - PassSuccessModel.OpponentReactionSeconds;
        ReachEstimate midStep = TimeToReachModel.ToPoint(
            ReachKinematics.FromEntity(covering), new Vector2(5.7f, 0f), MathF.Max(midAvailable, 0.01f));
        Assert.False(midStep.Reachable && midStep.Seconds <= midAvailable,
            $"the midpoint step-in must be out of reach, or the cone is not isolated " +
            $"(reach {midStep.Seconds:F2} s against {midAvailable:F2} s)");

        Assert.True(Model.IsLaneBlocked(plan, observation),
            "a body 2 m off the lane at the launch point that can step in during the turn must block the ball; " +
            "the constant-velocity sampled run alone misses him");
    }

    /// <summary>
    /// Follow-up: a defender past the target whose run closes on the arrival takes the *step-in* test at
    /// <c>targetXZ</c> (the brief's Task 1.3 endpoint half), and the fixture keeps the sampled run out of the
    /// way so the step-in is what catches him: he starts 2 m past the target, jogs back toward it, and his
    /// constant-velocity path never comes within reach of the ball's flight.
    /// </summary>
    [Fact]
    public void FollowUp_PastTargetClosingRunner_IsCaughtByTheArrivalStepIn()
    {
        Vector2 from = Vector2.Zero;
        Vector2 to = new(18f, 0f);
        PassPlan plan = Planner.PlanGroundPassToRegion(from, to, arriveInSeconds: 4f); // slow ball
        Assert.True(plan.Feasible);
        float flight = plan.FlightTimeSeconds;

        Vector2 start = new(to.X + 2f, 2.5f);
        Vector2 velocity = new(-0.2f, -0.2f); // closing on the target: dot(run, target - start) > 0
        PerceivedEntity recovering = Opponent(14, start, velocity);
        Assert.True(Vector2.Dot(velocity, to - start) > 0f);
        Assert.True(Vector2.Dot(start - from, Vector2.UnitX) > plan.DistanceMeters +
                    PassSuccessModel.LaneContactReachMeters);

        // The sampled run misses: the ball is only near his position early in the flight, when it is still far
        // down the lane from him.
        float closest = ClosestSampledApproach(recovering, from, to, plan.Power01, flight, 0f, out _);
        Assert.True(closest > PassSuccessModel.LaneStepInReachMeters,
            $"the sampled run must miss the fixture's defender ({closest:F2} m) so the step-in is isolated");

        Assert.True(Model.IsLaneBlocked(
                from, plan.TargetXZ, plan.Power01, flight, new[] { recovering }, TeamId.Home),
            "a defender past the target whose run closes back onto the arrival can step onto the ball there");

        // A standing or receding past-target body stays clear (A6.B8): the arrival race is the defender term's.
        PerceivedEntity standing = Opponent(15, start, Vector2.Zero);
        Assert.False(Model.IsLaneBlocked(
            from, plan.TargetXZ, plan.Power01, flight, new[] { standing }, TeamId.Home));
    }

    // =============================================================================================
    // Fixtures and helpers
    // =============================================================================================

    private static AiRuntimeConfig PlainPicture() => new()
    {
        EnableCombinations = false,
        EnableShallowSearch = false,
        EnableSwitchRecognition = false
    };

    /// <summary>
    /// The minimum distance between a defender's constant-velocity run and the ball's own sampled path,
    /// with <paramref name="launchDelaySeconds"/> charged to the defender's clock — the pre-fix sampled
    /// run when it is zero, and the fixed one when it is the turn delay. Re-derived from the planner's
    /// table and the published lane sampling, so a fixture cannot silently stop exercising the defect.
    /// </summary>
    private static float ClosestSampledApproach(
        PerceivedEntity defender,
        Vector2 from,
        Vector2 to,
        float power,
        float flight,
        float launchDelaySeconds,
        out float atTime)
    {
        Vector2 segment = to - from;
        float length = segment.Length();
        Vector2 direction = segment / length;
        float travelled = MathF.Min(Planner.BallDistanceAtTime(power, flight), length);
        int samples = Math.Clamp(
            (int)MathF.Ceiling(MathF.Max(travelled, 1f) / PassSuccessModel.LaneSampleStepMeters),
            PassSuccessModel.MinLaneSamples,
            PassSuccessModel.MaxLaneSamples);
        Vector2 position = new(defender.EstimatedPosition.X, defender.EstimatedPosition.Z);
        Vector2 velocity = new(defender.EstimatedVelocity.X, defender.EstimatedVelocity.Z);
        float best = float.MaxValue;
        atTime = 0f;
        for (int sample = 0; sample <= samples; sample++)
        {
            float t = flight * sample / samples;
            float ballDistance = MathF.Min(Planner.BallDistanceAtTime(power, t), length);
            Vector2 ball = from + direction * ballDistance;
            float distance = Vector2.Distance(position + velocity * (t + launchDelaySeconds), ball);
            if (distance >= best) continue;
            best = distance;
            atTime = t;
        }

        return best;
    }

    private static PerceivedEntity Opponent(int id, Vector2 position, Vector2 velocity) =>
        new(id, TeamId.Away, TacticalRole.CenterBack,
            new Vector3(position.X, 0f, position.Y), new Vector3(velocity.X, 0f, velocity.Y), 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };

    private static PerceivedEntity Mate(int playerId, Vector2 position) =>
        new(playerId, TeamId.Home, TacticalRole.CentralMidfielder,
            new Vector3(position.X, 0f, position.Y), Vector3.Zero, 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };

    private static FootballerState Player(Vector2 position, Vector2 facing) => new()
    {
        Id = CarrierId,
        Team = TeamId.Home,
        ShirtNumber = CarrierId,
        Role = TacticalRole.CentralMidfielder,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(TacticalRole.CentralMidfielder),
        Position = new Vector3(position.X, 0f, position.Y),
        FacingXZ = facing
    };

    private static FootballerObservation Observation(
        FootballerState self,
        params PerceivedEntity[] visible) => new()
    {
        SelfId = self.Id,
        SelfTeam = self.Team,
        SelfRole = self.Role,
        SelfPosition = self.Position,
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = self.FacingXZ,
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = self.Position,
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = self.Team,
        BallLastTouchedByPlayerId = self.Id,
        BallLastTouchedByTeam = self.Team,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = self.Id,
        TacticalAnchorPosition = self.Position,
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 10f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = ObservationTick,
        VisiblePlayers = visible,
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = new PlayerTendencies(),
        SelfAttributes = new PlayerAttributes()
    };
}
