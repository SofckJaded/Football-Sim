using System;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Tactics;
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
    // ai62 — the live opening-pass replays (seed 20260818, MatchClientNode spectator setup)
    //
    // The ai60/ai61 fixes passed every synthetic unit test while the rendered opening still gifted
    // interceptions, because the failing inputs were the *reach model's non-collinear* ones: a defender
    // with sideways momentum was declared unreachable, which removed him from the lane test, the arrival
    // race and the space-race gate all at once. These three fixtures are the exact live observations of
    // the 2026-11-24 diagnostic (passes at t=102, t=222, t=406), copied from
    // artifacts/advanced-ai/ai62-opening-passes-before.txt, so the fix is tested on the geometry that failed in
    // the rendered match rather than on a synthetic one.
    // =============================================================================================

    /// <summary>
    /// Brief Pass 1 (t=102): Home #9 plays a 1.60 s <c>ToSpace</c> ball 14 m ahead of #7 while Away #18
    /// is freshly sighted 5.76 m from the target, running (−7.94, −1.35) at 8.05 m/s back up the lane.
    /// Pre-fix <c>TimeToReachModel.ToPoint(#18, target)</c> said unreachable (lateral drift ~1.4 m),
    /// the space race was won at p = 0.912 and #18 intercepted 0.98 s later.
    ///
    /// <para>Fixed: the presser's modelled arrival must beat the receiver's by the
    /// <see cref="CandidateGenerator.SpaceRaceMarginSeconds"/> gate, so the probe is refused
    /// (<c>space-race-lost</c>) or the ground lane test blocks it. The model half is asserted directly so
    /// the test states which physics changed, not only that a candidate vanished.</para>
    /// </summary>
    [Fact]
    public void LiveReplay_Pass1_ToSpaceIntoSprintingPresser_IsRejected()
    {
        FootballerObservation obs = LiveObservation(
            selfId: 9,
            selfTeam: TeamId.Home,
            selfRole: TacticalRole.Striker,
            selfPos: new Vector2(-6.940f, -0.159f),
            selfVel: new Vector2(7.842f, -2.339f),
            facing: new Vector2(0.8989f, -0.4381f),
            stamina: 0.991f,
            ball: new Vector2(-6.328f, -0.392f),
            ballVel: new Vector2(7.930f, -1.741f),
            tick: 102,
            phase: TeamPhase.Attacking,
            stability: PossessionStability.Controlled,
            zone: TeamZone.MiddleThird,
            visible: LivePass1Visible());

        Vector2 target = new(3.923f, 19.272f);
        PerceivedEntity presser = FindEntity(obs, 18);
        PerceivedEntity receiver = FindEntity(obs, 7);

        // The physics half: with the fix the presser reaches the space point much sooner than #7 does,
        // by more than the gate the space probe applies. Pre-fix the presser's query returned the 2.5 s
        // horizon as an unreachable value, so the race margin was positive and the probe admitted it.
        ReachEstimate presserReach = TimeToReachModel.ToPoint(
            ReachKinematics.FromEntity(presser), target, CandidateGenerator.SpaceRaceHorizonSeconds);
        ReachEstimate receiverReach = TimeToReachModel.ToPoint(
            ReachKinematics.FromEntity(receiver), target, CandidateGenerator.SpaceRaceHorizonSeconds);
        Assert.True(presserReach.Reachable, "the fresh presser must be able to reach the space point");
        Assert.True(receiverReach.Reachable, "the receiver must be able to reach his own space ball");
        Assert.True(presserReach.Seconds + CandidateGenerator.SpaceRaceMarginSeconds <= receiverReach.Seconds,
            $"the presser arrives {presserReach.Seconds:F2} s against the receiver's {receiverReach.Seconds:F2} s; " +
            "pre-fix the presser was 'unreachable' and the race was scored 0.9 s in the receiver's favour");

        var generator = new CandidateGenerator(Pitch, communication: null, PlainPicture()) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(obs, new Vector3(Pitch.HalfLength, 0f, 0f), 40f);

        Assert.DoesNotContain(generator.Candidates, candidate =>
            candidate.Key.Kind == CandidateKind.Pass &&
            candidate.Key.TargetPlayerId == 7 &&
            string.Equals(candidate.Key.TargetTag, CandidateGenerator.SpacePassTag, StringComparison.Ordinal));

        bool refused = false;
        for (int i = 0; i < generator.RejectionCount; i++)
        {
            CandidateRejection rejection = generator.GetRejection(i);
            if (rejection.TargetPlayerId != 7) continue;
            if (rejection.Reason == CandidateGenerator.RejectionSpaceRaceLost ||
                rejection.Reason == CandidateGenerator.RejectionLaneBlocked)
            {
                refused = true;
            }
        }

        Assert.True(refused,
            "the ToSpace ball into the sprinting presser must be refused (space-race-lost or lane-blocked); " +
            "pre-fix it won at p = 0.912 and was intercepted");
    }

    /// <summary>
    /// Brief Pass 2 (t=222): Away #18 plays a 0.98 s <c>ToRun</c> ball to #20's advertised run while Home
    /// #4 and #6 are on top of the meeting region — #4 3.23 m away at (−2.72, −1.98), #6 5.14 m away at
    /// (5.38, 6.35). Pre-fix both were declared unreachable by the lateral-drift bug, so the arrival race
    /// read as uncontested (defender term 1) and the promised ball was priced p = 0.970; #4 intercepted it.
    ///
    /// <para><b>Measured correction to the brief's expectation.</b> The brief predicted the fixed model
    /// would price this as a <i>decisively</i> lost race (p ≤ 0.05). It does not: with the exact live
    /// promise (arrival 1.25 s, radius 5 m, confidence 0.9802) the contest is now a genuine race —
    /// #4's effective arrival is ≈1.23 s against the promised 1.25 s — so the completion falls to 0.5195
    /// with a defender-edge margin instead of 0.970. That is the honest arithmetic: the pre-fix error was
    /// "uncontested → effectively certain", not "certain → impossible". The assertions pin the fixed
    /// verdict (defender terrain, defender term below even, most of the completion gone); the report
    /// records the deviation and the live replay result.</para>
    /// </summary>
    [Fact]
    public void LiveReplay_Pass2_ToRunIntoCoveringAndPressing_IsRejected()
    {
        FootballerObservation obs = LiveObservation(
            selfId: 18,
            selfTeam: TeamId.Away,
            selfRole: TacticalRole.Winger,
            selfPos: new Vector2(-5.435f, 11.111f),
            selfVel: new Vector2(-7.392f, 0.314f),
            facing: new Vector2(-0.8385f, 0.5448f),
            stamina: 0.961f,
            ball: new Vector2(-5.988f, 11.338f),
            ballVel: new Vector2(-7.282f, -0.522f),
            tick: 222,
            phase: TeamPhase.Attacking,
            stability: PossessionStability.Controlled,
            zone: TeamZone.MiddleThird,
            visible: LivePass2Picture());

        Vector2 ballXZ = new(-5.988f, 11.338f);
        Vector2 target = new(-17.250f, 8.911f);
        float distance = Vector2.Distance(ballXZ, target);
        Assert.True(Planner.TryGetFlightTime(0.60f, distance, out float flight));
        Assert.InRange(flight, 0.90f, 1.10f); // the live 14.8 m/s ball, 0.98 s
        var plan = new PassPlan(true, SimMath.SafeNormalize(target - ballXZ, Vector2.UnitX), target,
            Power01: 0.60f, FlightTimeSeconds: flight, DistanceMeters: distance);

        PerceivedEntity receiver = FindEntity(obs, 20);
        ReachEstimate receiverReach = TimeToReachModel.ToPoint(
            ReachKinematics.FromEntity(receiver), target, flight + 1f);
        Assert.True(receiverReach.Reachable, "the receiver must be able to reach his own promised run");

        // The exact live promise from the baseline run-board capture (AiBehaviour 61):
        //   InBehind meet=(-17.250, 8.911) r=5.0 meetTick=297 (1.25 s after the t=222 kick)
        //   confidence 0.9802, published t=204, visible t=222.
        // The promise arrival (1.25 s) is what the contest is measured at, not the ball's 0.98 s flight.
        var promise = new PassArrivalPromise(
            target, ArrivalSeconds: 1.25f, MeetRadiusMeters: 5f, Confidence01: 0.9802f);
        float lane = CandidateGenerator.EvaluateLaneClearance(ballXZ, target, obs.VisiblePlayers, obs.SelfTeam);
        PassSuccessTerms terms = Model.Estimate(obs, receiver, plan, lane, promise);

        float defenderTerm = Math.Clamp(1f - terms.DefenderPressure01, 0f, 1f);
        Assert.True(terms.DefenderMarginSeconds < 0f,
            $"the arrival race must now be the defender's to win (margin {terms.DefenderMarginSeconds:F3} s); " +
            "pre-fix both defenders were 'unreachable' by the lateral-drift bug and the margin was +1.05 s");
        Assert.True(defenderTerm < 0.5f,
            $"the defender term must fall below even (measured {defenderTerm:F3}); pre-fix it was exactly 1");
        Assert.True(terms.CompletionProbability01 <= 0.70f,
            $"the delivery must lose most of its completion (measured {terms.CompletionProbability01:F3}); " +
            "the live pre-fix pricing was p = 0.970");
    }

    /// <summary>
    /// Brief Pass 3 (t=406): Home #2 plays a 0.95 s ball to #4's feet while Away #20 — whose last sighting
    /// is 51 ticks (0.85 s) old with <c>PositionUncertaintyMeters = 1.49</c> — sits 0.0 m off the lane in
    /// truth and 2.37 m off it in the stale picture. Pre-fix the candidate passed the <b>unladen</b>
    /// corridor metric (<c>chargeUncertainty: false</c>) and scored lane = 1.000, p = 0.920; #20
    /// intercepted 8 ticks after the kick.
    ///
    /// <para>The test asserts the metric falls sharply once the sighting doubt is charged (the fix's
    /// Task 2.1/2.2), that the estimate prices it, and that the candidate wiring uses the charged value:
    /// pre-fix <c>TryAddGroundPass</c> passed the unladen metric.</para>
    /// </summary>
    [Fact]
    public void LiveReplay_Pass3_StalePresserNearLane_ReducesLaneClearanceAndCompletion()
    {
        FootballerObservation obs = LiveObservation(
            selfId: 2,
            selfTeam: TeamId.Home,
            selfRole: TacticalRole.FullBack,
            selfPos: new Vector2(-19.027f, 23.349f),
            selfVel: new Vector2(4.026f, 5.341f),
            facing: new Vector2(0.5677f, 0.8232f),
            stamina: 0.971f,
            ball: new Vector2(-18.674f, 23.836f),
            ballVel: new Vector2(3.800f, 4.751f),
            tick: 406,
            phase: TeamPhase.Attacking,
            stability: PossessionStability.Controlled,
            zone: TeamZone.OwnThird,
            visible: LivePass3Picture());

        Vector2 ballXZ = new(-18.674f, 23.836f);
        Vector2 target = new(-17.927f, 11.711f);
        float distance = Vector2.Distance(ballXZ, target);
        Assert.True(Planner.TryGetFlightTime(0.70f, distance, out float flight));
        Assert.InRange(flight, 0.85f, 1.05f); // the live 15.9 m/s ball, 0.93 s
        var plan = new PassPlan(true, SimMath.SafeNormalize(target - ballXZ, Vector2.UnitX), target,
            Power01: 0.70f, FlightTimeSeconds: flight, DistanceMeters: distance);

        PerceivedEntity stale = FindEntity(obs, 20);
        Assert.Equal(51, stale.ObservedAgeTicks);
        Assert.Equal(1.490f, stale.PositionUncertaintyMeters, 3);

        float rawLane = CandidateGenerator.EvaluateLaneClearance(
            ballXZ, target, obs.VisiblePlayers, obs.SelfTeam);
        float chargedLane = CandidateGenerator.EvaluateLaneClearance(
            ballXZ, target, obs.VisiblePlayers, obs.SelfTeam, chargeUncertainty: true);
        Assert.Equal(1.0f, rawLane, 3); // the unladen metric the pre-fix candidate used
        Assert.True(chargedLane < 0.60f,
            $"a 1.49 m stale sighting 2.37 m off the lane must cost most of the corridor " +
            $"(measured {chargedLane:F3}, pre-fix candidate used {rawLane:F3})");

        PerceivedEntity receiver = FindEntity(obs, 4);
        PassSuccessTerms raw = Model.Estimate(obs, receiver, plan, rawLane);
        PassSuccessTerms charged = Model.Estimate(obs, receiver, plan, chargedLane);
        Assert.True(raw.CompletionProbability01 > 0.90f,
            $"the pre-fix live pricing was p = 0.920 with the unladen lane (measured {raw.CompletionProbability01:F3})");
        Assert.True(charged.CompletionProbability01 < raw.CompletionProbability01 - 0.10f,
            $"charging the sighting doubt must cost real completion (charged " +
            $"{charged.CompletionProbability01:F3} vs unladen {raw.CompletionProbability01:F3})");

        // Candidate wiring: the emitted feet ball must carry the charged pricing, not the unladen one.
        var generator = new CandidateGenerator(Pitch, communication: null, PlainPicture()) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(obs, new Vector3(Pitch.HalfLength, 0f, 0f), 40f);

        bool foundPass = false;
        for (int i = 0; i < generator.Candidates.Count; i++)
        {
            UtilityCandidate candidate = generator.Candidates[i];
            if (candidate.Key.Kind != CandidateKind.Pass || candidate.Key.TargetPlayerId != 4) continue;
            if (!string.Equals(candidate.Key.TargetTag, "ToFeet", StringComparison.Ordinal)) continue;

            foundPass = true;
            Assert.True(candidate.Terms.IsPresent);
            Assert.True(candidate.Terms.Probability01 < raw.CompletionProbability01 - 0.10f,
                $"the candidate must be priced with the charged corridor ({candidate.Terms.Probability01:F3}); " +
                $"pre-fix it used the unladen {raw.CompletionProbability01:F3}");
        }

        // Either verdict is acceptable and the report states which one shipped; what is not acceptable
        // is the pass looking clean. If it was not emitted, a refusal for this target must exist.
        if (!foundPass)
        {
            bool refused = false;
            for (int i = 0; i < generator.RejectionCount; i++)
            {
                if (generator.GetRejection(i).TargetPlayerId == 4) refused = true;
            }

            Assert.True(refused, "a pass that is not emitted must have been refused for a stated reason");
        }
    }

    /// <summary>
    /// ai63 Task 3 (Playtest Img 1): exact <c>t=128</c> Home #9 -> #8 observation from
    /// <c>ai62-opening-passes.txt</c>. Away #21 starts past <c>targetXZ</c> (<c>projectionRaw = 17.88 m &gt;
    /// length = 15.79 m</c>) running back up-lane (<c>(-6.154, 0.954)</c>) and intercepted the ball ~4 m
    /// short of <c>targetXZ</c>. Pre-fix Check 3 only tested <c>length</c> for <c>projectionRaw &gt; length</c>;
    /// testing up-lane lookahead points along his closing run catches him as <c>IsLaneBlocked == true</c>.
    /// </summary>
    [Fact]
    public void LiveReplay_OpeningPass2_t128_PastTargetUpLanePresser_IsBlocked()
    {
        Vector2 ballXZ = new(-3.724f, -2.358f);
        Vector2 targetXZ = new(10.822f, -8.501f);
        const float power01 = 0.70f;
        const float flightSeconds = 1.333f;

        PerceivedEntity presser21 = Opponent(21, new Vector2(14.095f, -6.125f), new Vector2(-6.154f, 0.954f));
        Assert.True(Model.IsLaneBlocked(
            ballXZ, targetXZ, power01, flightSeconds, new[] { presser21 }, TeamId.Home),
            "Away #21 running back up-lane from past targetXZ must be caught by Check 3 up-lane lookahead");
    }

    /// <summary>
    /// ai63 Task 7 (ai62 code-review follow-up): isolated unit test for <c>sightTimeBonus</c> in
    /// <see cref="PassSuccessModel.IsLaneBlocked"/> Check 3. A stationary defender 2.4 m off the lane at
    /// along=10 m (outside Check 1's sampled corridor even with the 0.25 m doubt cap, and outside Check 2's
    /// 3.5 m launch cone) is just out of reach when freshly sighted (<c>PositionUncertaintyMeters = 0</c>),
    /// and blocks the lane when stale (<c>PositionUncertaintyMeters = 4.0f</c>, +0.20 s <c>sightTimeBonus</c>).
    /// </summary>
    [Fact]
    public void Check3_StaleSightingUncertainty_CreditsStepInTime_WhileFreshStaysOpen()
    {
        Vector2 from = Vector2.Zero;
        Vector2 to = new(16f, 0f);
        PassPlan plan = Planner.PlanGroundPassToRegion(from, to, arriveInSeconds: 1.25f, minPower01: 0.70f);
        Assert.True(plan.Feasible);

        // At along = 10 m on a power-0.70 16 m ball, flight to 10 m is ~0.72 s; reaction 0.25 s -> available = 0.47 s.
        // Stationary defender at (10, 2.25): must run 2.25 - 1.22 = 1.03 m from rest (~0.52 s: outside 0.47 s,
        // inside 0.47 + 0.20 = 0.67 s). Also 2.25 > 0.90 + 0.25 = 1.15 m so Check 1 never triggers.
        PerceivedEntity fresh = Opponent(31, new Vector2(10f, 2.25f), Vector2.Zero);
        PerceivedEntity stale = fresh with { PositionUncertaintyMeters = 4.0f, ObservedAgeTicks = 40 };

        Assert.False(Model.IsLaneBlocked(
            from, plan.TargetXZ, plan.Power01, plan.FlightTimeSeconds, new[] { fresh }, TeamId.Home),
            "fresh stationary defender just out of reach must leave the lane open");
        Assert.True(Model.IsLaneBlocked(
            from, plan.TargetXZ, plan.Power01, plan.FlightTimeSeconds, new[] { stale }, TeamId.Home),
            "stale sighting (+0.20 s sightTimeBonus in Check 3) must credit step-in time and block the lane");
    }

    [Fact]
    public void Check3_ClosingTowardLanePoint_RampsReactionSmoothly_NoBinaryCliff()
    {
        Vector2 from = Vector2.Zero;
        Vector2 to = new(16f, 0f);
        PassPlan plan = Planner.PlanGroundPassToRegion(from, to, arriveInSeconds: 1.25f, minPower01: 0.70f);
        Assert.True(plan.Feasible);

        // Stationary defender at (10, 2.25) cannot cross the 1.03 m gap inside available = flight - 0.25 s.
        PerceivedEntity stationary = Opponent(31, new Vector2(10f, 2.25f), Vector2.Zero);
        Assert.False(Model.IsLaneBlocked(
            from, plan.TargetXZ, plan.Power01, plan.FlightTimeSeconds, new[] { stationary }, TeamId.Home));

        // Same lateral distance, closing perpendicularly at 0.85 m/s (below the old 1.0 m/s binary cliff):
        // smooth reaction ramp shrinks reaction below 0.25 s and closes the lane.
        PerceivedEntity subCliffRamp = Opponent(31, new Vector2(10f, 2.25f), new Vector2(0f, -0.85f));
        Assert.True(Model.IsLaneBlocked(
            from, plan.TargetXZ, plan.Power01, plan.FlightTimeSeconds, new[] { subCliffRamp }, TeamId.Home),
            "sub-1.0 m/s closing velocity must smoothly reduce reaction instead of hitting a binary 1.0 m/s cliff");

        // Diagonal run back toward a down-lane sample point (low perpendicular closingInto = 0.45 m/s,
        // moving up-lane toward pointOnLane = (10.0, 0)): directional closing speed must beat the old cliff.
        PerceivedEntity diagonalBack = Opponent(32, new Vector2(10.4f, 2.25f), new Vector2(-1.5f, -0.45f));
        Assert.False(Model.IsLaneBlocked(
            from, plan.TargetXZ, plan.Power01, plan.FlightTimeSeconds,
            new[] { diagonalBack with { EstimatedVelocity = Vector3.Zero } }, TeamId.Home));
        Assert.True(Model.IsLaneBlocked(
            from, plan.TargetXZ, plan.Power01, plan.FlightTimeSeconds, new[] { diagonalBack }, TeamId.Home),
            "sprinting diagonally toward a down-lane point must lower reaction even when perpendicular speed < 1.0 m/s");
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

    // ---- ai62 live-replay fixtures -------------------------------------------------------------
    //
    // The entity lists below are the exact engine observations of the 2026-11-24 opening-pass
    // diagnostic at t=102 (brief Pass 1), t=222 (brief Pass 2) and t=406 (brief Pass 3). Only the
    // opponents are kept for the two model-level fixtures (the estimate and the lane test skip
    // team-mates); the candidate-level fixture keeps the whole visible picture because the pass
    // target set is built from the team-mates.

    /// <summary>One perceived entity with its exact remembered age/doubt and scouted capability band.</summary>
    private static PerceivedEntity E(
        int id, TeamId team, TacticalRole role,
        float px, float pz, float vx, float vz, float uncertaintyMeters, int age, float confidence,
        float capSpeed, float capAcceleration, float capTurn, float capReach, float capConfidence) =>
        new(id, team, role, new Vector3(px, 0f, pz), new Vector3(vx, 0f, vz), confidence)
        {
            ObservedAgeTicks = age,
            PositionUncertaintyMeters = uncertaintyMeters,
            Capability = new CapabilityEstimate(
                capSpeed, capAcceleration, capTurn, capReach, capConfidence)
        };

    private static PerceivedEntity[] LivePass1Visible() =>
    [
        // Pass 1 (t=102), self Home #9 — the whole visible picture.
        E(1, TeamId.Home, TacticalRole.Goalkeeper, -40.950f, -0.079f, 2.718f, -0.605f, 0.000f, 0, 0.48f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(2, TeamId.Home, TacticalRole.FullBack, -25.668f, 19.652f, 7.499f, 1.505f, 0.000f, 0, 0.58f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(3, TeamId.Home, TacticalRole.FullBack, -25.418f, -19.613f, 7.652f, -1.593f, 0.000f, 0, 0.58f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(4, TeamId.Home, TacticalRole.CenterBack, -26.567f, 7.480f, 8.079f, 1.196f, 0.000f, 0, 0.67f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(5, TeamId.Home, TacticalRole.CenterBack, -26.724f, -7.426f, 8.110f, -1.307f, 0.000f, 0, 0.68f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(6, TeamId.Home, TacticalRole.DefensiveMidfielder, -24.903f, 0.073f, 4.365f, 0.099f, 0.000f, 0, 0.72f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(7, TeamId.Home, TacticalRole.Winger, -10.077f, 19.272f, 7.116f, 0.841f, 0.000f, 0, 0.70f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(8, TeamId.Home, TacticalRole.CentralMidfielder, 6.420f, -5.050f, 5.567f, -4.334f, 0.000f, 0, 0.78f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(10, TeamId.Home, TacticalRole.CentralMidfielder, -17.060f, -5.729f, 5.939f, 2.004f, 0.000f, 0, 0.82f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(11, TeamId.Home, TacticalRole.Winger, -12.613f, -24.543f, 4.769f, -5.190f, 0.000f, 0, 0.61f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(12, TeamId.Away, TacticalRole.Goalkeeper, 48.326f, 0.188f, -0.029f, 0.012f, 0.533f, 10, 0.19f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(13, TeamId.Away, TacticalRole.FullBack, 27.096f, 15.268f, -5.993f, -1.447f, 0.000f, 0, 0.43f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(14, TeamId.Away, TacticalRole.FullBack, 27.268f, -15.007f, -6.105f, 1.628f, 0.000f, 0, 0.43f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(15, TeamId.Away, TacticalRole.CenterBack, 27.832f, 5.805f, -6.303f, -0.537f, 0.000f, 0, 0.46f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(16, TeamId.Away, TacticalRole.CenterBack, 27.914f, -5.716f, -6.315f, 0.415f, 0.000f, 0, 0.46f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(17, TeamId.Away, TacticalRole.DefensiveMidfielder, 20.803f, -0.025f, -7.845f, 0.285f, 0.533f, 10, 0.50f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(18, TeamId.Away, TacticalRole.Winger, 9.168f, 16.884f, -7.941f, -1.351f, 0.000f, 0, 0.64f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(19, TeamId.Away, TacticalRole.CentralMidfielder, 17.844f, 6.277f, -5.565f, -2.930f, 0.000f, 0, 0.61f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(20, TeamId.Away, TacticalRole.Striker, 6.381f, 0.046f, -8.334f, -0.518f, 0.000f, 0, 0.79f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(21, TeamId.Away, TacticalRole.CentralMidfielder, 16.738f, -6.820f, -6.006f, 1.577f, 0.000f, 0, 0.62f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(22, TeamId.Away, TacticalRole.Winger, 9.375f, -16.857f, -8.022f, 1.296f, 0.000f, 0, 0.64f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f)
    ];

    private static PerceivedEntity[] LivePass2Picture() =>
    [
        // Pass 2 (t=222), self Away #18 — the Home defenders plus the receiver (Away #20) the test needs
        // to look up; team-mates are skipped by both the estimate and the lane test.
        E(20, TeamId.Away, TacticalRole.Striker, -7.224f, 3.025f, -6.480f, 3.782f, 0.000f, 0, 0.87f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(1, TeamId.Home, TacticalRole.Goalkeeper, -41.286f, 3.021f, -5.780f, -1.093f, 0.000f, 0, 0.43f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(2, TeamId.Home, TacticalRole.FullBack, -14.998f, 23.794f, -0.525f, 0.580f, 0.000f, 0, 0.76f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(3, TeamId.Home, TacticalRole.FullBack, -15.043f, -21.225f, 0.763f, -0.624f, 0.743f, 19, 0.41f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(4, TeamId.Home, TacticalRole.CenterBack, -19.781f, 10.916f, -2.715f, -1.976f, 0.000f, 0, 0.78f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(5, TeamId.Home, TacticalRole.CenterBack, -15.852f, -4.219f, 0.489f, 5.649f, 0.000f, 0, 0.71f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(6, TeamId.Home, TacticalRole.DefensiveMidfielder, -13.215f, 5.729f, 5.384f, 6.355f, 0.000f, 0, 0.86f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(7, TeamId.Home, TacticalRole.Winger, 1.703f, 19.551f, 5.633f, -2.636f, 0.000f, 0, 0.83f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(8, TeamId.Home, TacticalRole.CentralMidfielder, 10.214f, -7.318f, -0.908f, 1.392f, 1.070f, 33, 0.52f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(9, TeamId.Home, TacticalRole.Striker, 6.523f, 0.422f, 5.998f, 0.825f, 0.883f, 25, 0.68f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(10, TeamId.Home, TacticalRole.CentralMidfielder, -6.834f, -3.065f, 3.757f, 3.260f, 0.767f, 20, 0.66f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(11, TeamId.Home, TacticalRole.Winger, -3.922f, -29.804f, 4.042f, 1.254f, 0.790f, 21, 0.31f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f)
    ];

    private static PerceivedEntity[] LivePass3Picture() =>
    [
        // Pass 3 (t=406), self Home #2 — the Away picture (including the 51-tick-old #20) plus the
        // receiver (Home #4) the test looks up; team-mates are skipped by the estimate and the lane test.
        E(4, TeamId.Home, TacticalRole.CenterBack, -18.070f, 9.704f, 0.153f, 2.150f, 0.000f, 0, 0.79f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(12, TeamId.Away, TacticalRole.Goalkeeper, 42.321f, 1.850f, 7.098f, 0.003f, 0.000f, 0, 0.20f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(13, TeamId.Away, TacticalRole.FullBack, 17.822f, 21.466f, 2.368f, 0.009f, 0.000f, 0, 0.43f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(14, TeamId.Away, TacticalRole.FullBack, 19.285f, -20.512f, 2.478f, -0.010f, 1.467f, 50, 0.15f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(15, TeamId.Away, TacticalRole.CenterBack, 17.005f, 6.831f, 3.532f, 0.990f, 0.000f, 0, 0.39f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(16, TeamId.Away, TacticalRole.CenterBack, 18.195f, -6.413f, 1.859f, 0.056f, 0.510f, 9, 0.25f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(17, TeamId.Away, TacticalRole.DefensiveMidfielder, 14.797f, 0.969f, 5.416f, 0.181f, 0.000f, 0, 0.37f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(18, TeamId.Away, TacticalRole.Winger, -10.951f, 16.827f, 3.350f, -1.522f, 0.000f, 0, 0.84f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(19, TeamId.Away, TacticalRole.CentralMidfielder, 0.345f, 9.647f, -0.764f, 1.667f, 0.000f, 0, 0.63f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(20, TeamId.Away, TacticalRole.Striker, -20.731f, 18.657f, -1.660f, 5.237f, 1.490f, 51, 0.66f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(21, TeamId.Away, TacticalRole.CentralMidfielder, -1.187f, -5.115f, 2.883f, 0.036f, 1.700f, 60, 0.36f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f),
        E(22, TeamId.Away, TacticalRole.Winger, -11.451f, -13.195f, 1.626f, 1.461f, 1.630f, 57, 0.31f, 8.50f, 7.00f, 9.50f, 0.75f, 0.80f)
    ];

    private static PerceivedEntity FindEntity(in FootballerObservation obs, int playerId)
    {
        for (int i = 0; i < obs.VisiblePlayers.Count; i++)
            if (obs.VisiblePlayers[i].PlayerId == playerId) return obs.VisiblePlayers[i];
        throw new InvalidOperationException($"player #{playerId} is not in the fixture's picture");
    }

    /// <summary>An observation reproducing one live decision instant (the fields the scorers read).</summary>
    private static FootballerObservation LiveObservation(
        int selfId,
        TeamId selfTeam,
        TacticalRole selfRole,
        Vector2 selfPos,
        Vector2 selfVel,
        Vector2 facing,
        float stamina,
        Vector2 ball,
        Vector2 ballVel,
        int tick,
        TeamPhase phase,
        PossessionStability stability,
        TeamZone zone,
        PerceivedEntity[] visible) => new()
    {
        SelfId = selfId,
        SelfTeam = selfTeam,
        SelfRole = selfRole,
        SelfPosition = new Vector3(selfPos.X, 0f, selfPos.Y),
        SelfVelocity = new Vector3(selfVel.X, 0f, selfVel.Y),
        SelfFacingXZ = facing,
        Stamina01 = stamina,
        AvailableToKick = true,
        ControlQuality01 = 0.745f,
        PerceivedBallPosition = new Vector3(ball.X, 0f, ball.Y),
        PerceivedBallVelocity = new Vector3(ballVel.X, 0f, ballVel.Y),
        PossessingTeam = selfTeam,
        BallLastTouchedByPlayerId = selfId,
        BallLastTouchedByTeam = selfTeam,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = selfId,
        TacticalAnchorPosition = new Vector3(selfPos.X, 0f, selfPos.Y),
        AttackingDirectionX = selfTeam == TeamId.Home ? 1f : -1f,
        MatchTimeSeconds = tick * SimConstants.FixedDeltaTime,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = tick,
        VisiblePlayers = visible,
        TeamProfile = selfTeam == TeamId.Home
            ? TeamTacticalProfile.PossessionControl
            : TeamTacticalProfile.DirectCounterattack,
        SelfTendencies = new PlayerTendencies(),
        SelfAttributes = new PlayerAttributes(),
        TacticalJob = TacticalJob.BallResponder,
        IsDesignatedBallResponder = true,
        BallResponderPlayerId = selfId,
        TacticalContext = new TeamContext(
            selfTeam, selfTeam, stability, phase, zone, TransitionMode.None,
            new RestartContext(RestartKind.None, null), IsEmergencyDefence: false),
        KnowledgeAgeTicks = LongestMemoryAge(visible)
    };

    private static int LongestMemoryAge(PerceivedEntity[] visible)
    {
        int age = 0;
        for (int i = 0; i < visible.Length; i++)
            if (visible[i].ObservedAgeTicks > age) age = visible[i].ObservedAgeTicks;
        return age;
    }

    // =============================================================================================
    // ai64 — live Opening Pass 2 (t=127) past-target up-lane sprinter blind spot & A6.B8 guard
    // =============================================================================================

    [Fact]
    public void LiveReplay_OpeningPass2_t127_PastTargetUpLanePresser_IsBlockedAndChargedInLaneClearance()
    {
        Vector2 ball = new(-3.79f, -2.22f);
        Vector2 target = new(10.73f, -8.28f);
        PerceivedEntity presser = Opponent(21, new Vector2(14.15f, -6.07f), new Vector2(-7.50f, 2.14f));

        Assert.True(
            Model.IsLaneBlocked(ball, target, power01: 0.70f, flightTimeSeconds: 1.32f,
                new[] { presser }, TeamId.Home),
            "Away #21 sprinting up-lane from past targetXZ at t=127 must block the pass lane");

        float clearance = CandidateGenerator.EvaluateLaneClearance(
            ball, target, new[] { presser }, TeamId.Home, chargeUncertainty: true);
        Assert.True(clearance < 1.0f,
            $"EvaluateLaneClearance must charge past-target up-lane sprinter (got {clearance:F3})");
    }

    [Fact]
    public void EvaluateLaneClearance_StandingOrRecedingDefenderPastTarget_RemainsUncharged()
    {
        Vector2 ball = Vector2.Zero;
        Vector2 target = new(15f, 0f);

        PerceivedEntity standing = Opponent(14, new Vector2(17f, 0f), Vector2.Zero);
        PerceivedEntity receding = Opponent(15, new Vector2(17f, 0f), new Vector2(5f, 0f));

        Assert.Equal(1.0f, CandidateGenerator.EvaluateLaneClearance(
            ball, target, new[] { standing }, TeamId.Home, chargeUncertainty: true));
        Assert.Equal(1.0f, CandidateGenerator.EvaluateLaneClearance(
            ball, target, new[] { receding }, TeamId.Home, chargeUncertainty: true));
    }
}
