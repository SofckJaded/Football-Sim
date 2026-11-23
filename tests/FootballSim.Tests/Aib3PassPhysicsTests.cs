using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// Regression tests for the three pass-physics findings of the 2026-11-23 audit
/// (<c>docs/AUDIT_AI57_2026-11-23.md</c>), all in the proposal layer:
/// <list type="bullet">
/// <item><b>AIB-08 (finding 3, Image 4)</b> — <see cref="PassSuccessModel.IsLaneBlocked"/> under-modelled a
/// moving defender: the sampled run used the 0.90 m true-contact margin instead of the real intercept
/// reach (~1.24 m) plus a bounded positional doubt, the step-in test only asked about the perpendicular
/// foot projection (the earliest, hardest point), it charged the full 0.25 s reaction to a defender already
/// closing into the corridor, and <see cref="CandidateGenerator.EvaluateLaneClearance"/> was 100 % static.</item>
/// <item><b>AIB-10 (finding 4, Image 5)</b> — the `ToSpace` promise carried the ball's own flight as its
/// arrival, so the pass model could not see an impossible ball that had rolled far past the promised grass
/// before the receiver could get there; and the defender contest in <see cref="PassSuccessModel.Estimate"/>
/// (ground) and <see cref="PassSuccessModel.EstimateRaised"/> measured the defender against the ball's
/// flight instead of <c>max(flight, receiverArrival)</c>.</item>
/// <item><b>AIB-11 (finding 6, Images 8/9)</b> — every <c>ToRun</c> path passed <c>MinPower01: 0f</c>, so an
/// advertised run ball was a slow timed roller that the lane test then hard-rejected, and
/// <see cref="PassPlanner.MinCrispPower01"/> had no caller in the AI.</item>
/// </list>
///
/// House style: model-level fixtures on synthetic inputs with the <i>pre-fix</i> answer visible in the
/// arithmetic. Where a pre-fix result depends on the old formula, the test re-derives it from the public
/// reach model (<see cref="TimeToReachModel"/>) and the planner's own tables, and asserts it, so the
/// failing half of the fixture cannot silently stop exercising the defect. Pre-fix defect proof per test
/// is stated in each test's XML comment.
/// </summary>
public sealed class Aib3PassPhysicsTests
{
    private static readonly PitchDimensions Pitch = new();
    private static readonly PassPlanner Planner = new(Pitch);
    private static readonly PassSuccessModel Model = new(Planner);
    private const int CarrierId = 8;
    private const int MateId = 9;
    private const int ObservationTick = 500;

    // =============================================================================================
    // AIB-08 — the moving-defender lane physics
    // =============================================================================================

    /// <summary>
    /// AIB-08 Step 1: the sampled run must use the reach a body actually plays the ball with.
    ///
    /// Fixture: a 16 m pass from (0,0) to (16,0) at power 0.35 (flight 2.250 s), and a defender starting at
    /// (0, 4.4) running (7, −1.5) — a diagonal run that converges on the lane and is <b>1.055 m</b> from the
    /// ball at its closest space-time approach (t = 2.25 s, at the receiver). Arithmetic:
    /// <code>
    ///   old sampled reach        = LaneContactReachMeters = 0.90 m   -> 1.055 > 0.90  -> NOT blocked
    ///   real intercept reach     = max(1.22, ReachRadius 1.2375)     -> 1.055 &lt; 1.2375 -> blocked
    ///   old step-in (foot at x=0, ball there at 0.03 s)              -> not reachable      -> NOT blocked
    /// </code>
    /// So the pre-fix model calls this pass open (proved by the two explicit pre-fix assertions below), and
    /// the fixed one blocks it on the sampled run — the audit's "defender passing 0.95–1.22 m from the ball
    /// escapes Step 1" defect. No step-in lookahead applies here (the run has no along-lane component its
    /// foot test could use: the foot is at the passer).
    /// </summary>
    [Fact]
    public void AIB08_MovingDefenderPassingWithinTheRealReach_BlocksTheLane()
    {
        const float length = 16f;
        const float power = 0.35f;
        Vector2 from = Vector2.Zero;
        Vector2 to = new(length, 0f);
        Assert.True(Planner.TryGetFlightTime(power, length, out float flight));
        Assert.Equal(2.250f, flight, 3);

        PerceivedEntity defender = Opponent(14, new Vector2(0f, 4.4f), new Vector2(7f, -1.5f));
        float minimumDistance = MinSampledDistance(defender, from, to, power, flight, out float atTime);
        Assert.True(MathF.Abs(minimumDistance - 1.055f) < 0.01f,
            $"the fixture measures a sampled minimum of {minimumDistance:F3} m, expected 1.055 m");
        Assert.Equal(2.250f, atTime, 1);

        // Pre-fix half: the sampled run compared against the 0.90 m margin, and the foot step-in failed.
        Assert.True(minimumDistance > PassSuccessModel.LaneContactReachMeters,
            $"the fixture must sit outside the pre-fix sampled reach: {minimumDistance:F3} m");
        Assert.False(OpponentStepInReachable(defender, from, to, power, PassSuccessModel.OpponentReactionSeconds),
            "the fixture's old foot step-in must be out of reach, or it does not isolate Step 1");

        bool blocked = Model.IsLaneBlocked(from, to, power, flight, new[] { defender }, TeamId.Home);
        Assert.True(blocked,
            $"a defender passing {minimumDistance:F3} m from the ball (real reach " +
            $"{PassSuccessModel.LaneStepInReachMeters:F2} m) must block the pass; pre-fix it was open");
    }

    /// <summary>
    /// AIB-08 Step 1, the uncertainty half: a <b>stale</b> sighting widens the moving-run reach by the
    /// bounded <see cref="PassSuccessModel.LaneReachUncertaintyCapMeters"/>, never by the whole doubt.
    ///
    /// Fixture: the same 16 m lane at power 0.35 with a defender at (0, 3.0) running (7, −2) — sampled
    /// minimum <b>1.281 m</b>: outside the fresh 1.2375 m reach (asserted clear below) but inside the
    /// capped 1.2375 + 0.25 = 1.4875 m reach. A 5 m reported doubt is capped, so the same defender with
    /// 5.0 m uncertainty is blocked exactly as one with 0.25 m (both reach 1.4875 m), and the fresh
    /// observation stays open. Pre-fix both were open: the sampled test used 0.90 m and never read the
    /// doubt at all.
    /// </summary>
    [Fact]
    public void AIB08_StaleSightingWidensTheMovingReach_OnlyUpToTheCap()
    {
        const float length = 16f;
        const float power = 0.35f;
        Vector2 from = Vector2.Zero;
        Vector2 to = new(length, 0f);
        Assert.True(Planner.TryGetFlightTime(power, length, out float flight));
        PerceivedEntity fresh = Opponent(14, new Vector2(0f, 3.0f), new Vector2(7f, -2f));
        float minimumDistance = MinSampledDistance(fresh, from, to, power, flight, out _);
        Assert.Equal(1.281f, minimumDistance, 2);
        Assert.True(minimumDistance > 1.2375f && minimumDistance <= 1.4875f,
            $"the fixture must sit between the fresh and the doubted reach: {minimumDistance:F3} m");

        Assert.False(Model.IsLaneBlocked(from, to, power, flight, new[] { fresh }, TeamId.Home),
            "a fresh sighting of this run is outside the real intercept reach and must stay open");

        PerceivedEntity cappedDoubt = fresh with { PositionUncertaintyMeters = 0.25f };
        PerceivedEntity hugeDoubt = fresh with { PositionUncertaintyMeters = 5f };
        Assert.True(Model.IsLaneBlocked(from, to, power, flight, new[] { cappedDoubt }, TeamId.Home),
            "a stale sighting must widen the moving-run intercept reach");
        Assert.True(Model.IsLaneBlocked(from, to, power, flight, new[] { hugeDoubt }, TeamId.Home),
            "a huge reported doubt is capped, not ignored");

        // The fresh pass still goes through the same lane test as before; only the doubted one changed.
        Assert.False(Model.IsLaneBlocked(
            from, to, power, flight,
            new[] { fresh with { PositionUncertaintyMeters = 0f } }, TeamId.Home));
        Assert.Equal(PassSuccessModel.LaneReachUncertaintyCapMeters, 0.25f);
    }

    /// <summary>
    /// AIB-08 Step 2: the step-in must look down the lane, not only at the perpendicular foot.
    ///
    /// Fixture: an 18 m pass from (0,0) at power 0.30 (flight 2.933 s) and a defender at (2, 3.0) running
    /// (5, −1): a diagonal runner whose minimum distance to the ball over the whole flight is <b>1.33 m</b>,
    /// so neither the old 0.90 m sampled test nor the new 1.2375 m one catches him. The old step-in test
    /// asked about the foot point at x = 2, where the ball arrives in ~0.03 s (before any reaction) — the
    /// earliest and hardest point — and concluded "not reachable". His actual intercept is metres further
    /// down the lane (x ≈ 6.9 at t ≈ 0.95 s), where he is already running: the two bounded lookahead points
    /// find it. Pre-fix this pass was open (both pre-fix halves asserted below).
    /// </summary>
    [Fact]
    public void AIB08_DiagonalRunner_IsBlockedByTheLaneLookahead()
    {
        const float length = 18f;
        const float power = 0.30f;
        Vector2 from = Vector2.Zero;
        Vector2 to = new(length, 0f);
        Assert.True(Planner.TryGetFlightTime(power, length, out float flight));
        Assert.Equal(2.933f, flight, 3);

        PerceivedEntity runner = Opponent(14, new Vector2(2f, 3.0f), new Vector2(5f, -1f));
        float minimumDistance = MinSampledDistance(runner, from, to, power, flight, out _);
        Assert.Equal(1.33f, minimumDistance, 2);
        Assert.True(minimumDistance > PassSuccessModel.LaneStepInReachMeters,
            $"the fixture must be outside the fixed sampled reach too, or it does not isolate Step 2: {minimumDistance:F2} m");

        // Pre-fix half: the foot-only step-in with the full reaction cannot be there in time.
        Assert.False(OpponentStepInReachable(runner, from, to, power, PassSuccessModel.OpponentReactionSeconds),
            "the fixture's old foot-only step-in must fail, or the lookahead is not what catches him");

        bool blocked = Model.IsLaneBlocked(from, to, power, flight, new[] { runner }, TeamId.Home);
        Assert.True(blocked,
            $"a defender running diagonally down the lane (intercept at x ≈ 6.9 m) must block it; " +
            "pre-fix only the earliest foot point was tested");

        // A defender running away from the lane past the receiver is not a block: the segment, not the
        // infinite line, is the question (A6.B8 stays).
        PerceivedEntity pastReceiver = Opponent(
            15, new Vector2(20f, 3f), new Vector2(5f, 4f));
        Assert.False(Model.IsLaneBlocked(from, to, power, flight, new[] { pastReceiver }, TeamId.Home),
            "a defender past the receiver and running away cannot block this segment");
    }

    /// <summary>
    /// AIB-08 reaction half: a defender already closing into the corridor keeps
    /// <see cref="PassSuccessModel.ClosingIntoReactionScale"/> of the reaction, not the whole delay.
    ///
    /// Fixture: an 18 m lane at power 0.35 (flight 2.700 s), defender at (6, 2.6) moving (0, −1.5) — a
    /// 1.5 m/s lateral close (above the 1.0 m/s threshold). The ball reaches his foot point (x = 6) at
    /// 0.583 s. Pre-fix available = 0.583 − 0.25 = 0.333 s, and the reach model needs longer than that to
    /// close the 2.6 − 1.2375 = 1.36 m gap (asserted below). Fixed: available = 0.583 − 0.10 = 0.483 s and
    /// the reach model answers 0.429 s — two tenths of a second are exactly the duplicate charge the audit
    /// names. The sampled run does not catch him (minimum distance 1.70 m), so the reaction is the only
    /// difference.
    /// </summary>
    [Fact]
    public void AIB08_DefenderAlreadyClosingIntoTheLane_KeepsLessReaction()
    {
        const float length = 18f;
        const float power = 0.35f;
        Vector2 from = Vector2.Zero;
        Vector2 to = new(length, 0f);
        Assert.True(Planner.TryGetFlightTime(power, length, out float flight));
        Assert.Equal(2.700f, flight, 3);

        PerceivedEntity closer = Opponent(14, new Vector2(6f, 2.6f), new Vector2(0f, -1.5f));
        float minimumDistance = MinSampledDistance(closer, from, to, power, flight, out _);
        Assert.True(minimumDistance > PassSuccessModel.LaneContactReachMeters,
            $"the sampled run must not catch him, or the reaction is not isolated: {minimumDistance:F2} m");

        Assert.True(Planner.TryGetFlightTime(power, 6f, out float footTime));
        Assert.Equal(0.583f, footTime, 3);

        // Pre-fix half: the full 0.25 s reaction leaves too little time, re-derived from the public model.
        Assert.False(OpponentStepInReachable(closer, from, to, power, PassSuccessModel.OpponentReactionSeconds),
            "the fixture must be out of reach with the full reaction delay");
        ReachEstimate reduced = StepIn(closer, from, to, power,
            footTime - PassSuccessModel.OpponentReactionSeconds * PassSuccessModel.ClosingIntoReactionScale);
        Assert.True(reduced.Reachable && reduced.Seconds <= footTime - 0.10f,
            $"with the reduced reaction the defender must be there in time (reach {reduced.Seconds:F3} s " +
            $"against {footTime - 0.10f:F3} s)");

        bool blocked = Model.IsLaneBlocked(from, to, power, flight, new[] { closer }, TeamId.Home);
        Assert.True(blocked,
            "a defender already closing into the lane must not be charged the whole reaction delay");
        Assert.Equal(0.40f, PassSuccessModel.ClosingIntoReactionScale, 3);
        Assert.Equal(1.0f, PassSuccessModel.ClosingIntoReactionSpeedMetersPerSecond, 3);
    }

    /// <summary>
    /// AIB-08, the scoring half: <see cref="CandidateGenerator.EvaluateLaneClearance"/> must fall for a
    /// defender closing into the corridor before the ball is scored, and must be bit-for-bit unchanged for
    /// the same defender standing still.
    ///
    /// Fixture: a 20 m lane from (0,0) to (20,0), one defender at (10, 3.0). He is 3.0 m off the lane at the
    /// corridor's 2.3 m intercept radius (ratio 3.0/2.3 → clamped 1.0, so the static metric scored a fully
    /// open lane). Closing at 5 m/s he is 1.25 m off it 0.35 s later (ratio 1.25/2.3 = 0.543, plus the
    /// bounded congestion charge: (0.85 − 0.543)/0.85 = 0.361 → ×(1 − 0.18 × 0.361) = 0.935 ⇒ ≈0.508).
    /// Pre-fix both values were exactly 1.0 — the audit's "100 % static, scored like he stood still".
    /// </summary>
    [Fact]
    public void AIB08_LaneClearance_FallsForADefenderClosingIntoTheCorridor()
    {
        Vector2 from = Vector2.Zero;
        Vector2 to = new(20f, 0f);
        PerceivedEntity stationary = Opponent(14, new Vector2(10f, 3.0f), Vector2.Zero);
        PerceivedEntity closing = Opponent(14, new Vector2(10f, 3.0f), new Vector2(0f, -5f));

        float still = CandidateGenerator.EvaluateLaneClearance(from, to, new[] { stationary }, TeamId.Home);
        float moving = CandidateGenerator.EvaluateLaneClearance(from, to, new[] { closing }, TeamId.Home);

        Assert.Equal(1.0f, still, 4); // pre-fix value for both — the static formula's answer
        Assert.Equal(0.508f, moving, 2);
        Assert.True(moving < still - 0.30f,
            $"a defender closing into the corridor ({moving:F3}) must not score like one standing " +
            $"still ({still:F3}); pre-fix both were 1.0");
        Assert.Equal(0.35f, CandidateGenerator.LaneProjectionHorizonSeconds, 3);
        Assert.Equal(0.5f, CandidateGenerator.LaneProjectionMinSpeedMetersPerSecond, 3);
    }

    // =============================================================================================
    // AIB-10 — the space promise and the receiver's real arrival
    // =============================================================================================

    /// <summary>
    /// AIB-10: the audit's impossible space pass is refused.
    ///
    /// Fixture (the audit's shape): the carrier has the ball at (0, 0), the only team-mate is <b>6 m
    /// behind</b> him at (−6, 0), so the two probes are 14 m and 8 m ahead — at (8, 0) and (2, 0). The far
    /// probe is an 8.0 m ball and the receiver cannot get there until <b>2.06 s</b>; the plan at the crisp
    /// floor reaches the region well inside a second, so at his arrival the ball has rolled far past it:
    /// <code>
    ///   receiver reach to the far probe = 2.06 s
    ///   crisp plan at 8.0 m              = 0.45 power, ≈0.7 s flight
    ///   ball distance at 2.06 s          ≫ 12 m  ⇒  far more than 4 m past the 8.0 m aim point
    ///   roll-out &gt; SpacePromiseCatchRadiusMeters (5.22 m = the 4 m promise region + one 1.22 m
    ///   receiving reach) ⇒ refuse
    /// </code>
    /// The near probe is inside <see cref="CandidateGenerator.MinPassDistanceMeters"/> and is refused as
    /// out-of-range, so no <c>ToSpace</c> candidate survives. Pre-fix the promise's arrival was the ball's
    /// own flight, so the pass model's timing term was exactly 1 and the candidate was offered — the
    /// audit's Image 5.
    /// </summary>
    [Fact]
    public void AIB10_ImpossibleSpacePass_IsRejected()
    {
        Vector2 ballXZ = Vector2.Zero;
        PerceivedEntity receiver = Mate(new Vector2(-6f, 0f));
        FootballerObservation observation = Observation(Player(Vector2.Zero), receiver);
        var generator = new CandidateGenerator(Pitch, communication: null, PlainPicture()) { TimeOnBall = 0.8f };

        generator.GenerateOnBallCandidates(observation, new Vector3(Pitch.HalfLength, 0f, 0f), 40f);

        Assert.DoesNotContain(generator.Candidates,
            c => c.Key.Kind == CandidateKind.Pass && c.Key.TargetTag == CandidateGenerator.SpacePassTag);

        bool refusedForTheRoll = false;
        bool refusedForRange = false;
        for (int i = 0; i < generator.RejectionCount; i++)
        {
            CandidateRejection rejection = generator.GetRejection(i);
            if (rejection.TargetPlayerId != MateId) continue;
            if (rejection.Reason == CandidateGenerator.RejectionSpaceRolledPast) refusedForTheRoll = true;
            if (rejection.Reason == CandidateGenerator.RejectionOutOfRange) refusedForRange = true;
        }

        Assert.True(refusedForTheRoll,
            "the probe whose ball has rolled out of the promised region by the receiver's arrival must be " +
            "refused with the rollout reason (pre-fix no such gate existed and the candidate was offered)");
        Assert.True(refusedForRange, "the near probe is inside the minimum pass distance and must be refused as out of range");

        // Independent arithmetic of the criterion the builder used: the plan's own roll at the receiver's
        // arrival, measured from the planner's table, is far outside the promise region.
        Vector2 farProbe = new(8f, 0f);
        ReachEstimate reach = TimeToReachModel.ToPoint(
            ReachKinematics.FromEntity(receiver), farProbe, CandidateGenerator.SpaceRaceHorizonSeconds);
        Assert.True(reach.Reachable);
        Assert.Equal(2.06f, reach.Seconds, 2);
        float aimDistance = Vector2.Distance(ballXZ, farProbe);
        PassPlan plan = Planner.PlanGroundPassToRegion(
            ballXZ, farProbe, reach.Seconds, Planner.MinCrispPower01(aimDistance));
        Assert.True(plan.Feasible);
        float rolledPast = Planner.BallDistanceAtTime(plan.Power01, reach.Seconds) - aimDistance;
        Assert.True(rolledPast > CandidateGenerator.SpacePromiseCatchRadiusMeters,
            $"the ball is only {rolledPast:F1} m past the aim point at the receiver's arrival; " +
            $"the fixture must exceed the {CandidateGenerator.SpacePromiseCatchRadiusMeters:F1} m catch radius");
        Assert.Equal(
            CandidateGenerator.SpacePromiseRadiusMeters + PassSuccessModel.LaneStepInReachMeters,
            CandidateGenerator.SpacePromiseCatchRadiusMeters);
    }

    /// <summary>
    /// AIB-10, the guard half: a legitimate firm ball into grass must still be offered after the rollout gate.
    ///
    /// Fixture: carrier at (0, 8) with the ball at his feet, receiver at (8, −4) with 14 m of empty onside
    /// grass in front of him, no advertisement and nobody near. The far probe is a 25.1 m ball at the crisp
    /// floor (1.00 power, ≈2.07 s flight), while the receiver needs ≈2.3 s to run the 14 m to it — the ball
    /// has rolled only <b>1.2 m</b> past the aim point when he arrives, comfortably inside the 4 m promise
    /// region. The candidate must exist and its command power must be the crisp floor, i.e. the gate must
    /// not delete the AUD-P3 delivery.
    /// </summary>
    [Fact]
    public void AIB10_FirmSpaceBallIntoReachableGrass_IsStillOffered()
    {
        Vector2 ballXZ = new(0f, 8f);
        PerceivedEntity receiver = Mate(new Vector2(8f, -4f));
        FootballerObservation observation = Observation(Player(new Vector2(0f, 8f)), receiver);
        var generator = new CandidateGenerator(Pitch, communication: null, PlainPicture()) { TimeOnBall = 0.8f };

        generator.GenerateOnBallCandidates(observation, new Vector3(Pitch.HalfLength, 0f, 0f), 40f);

        UtilityCandidate space = Single(generator, CandidateGenerator.SpacePassTag, MateId);
        Assert.True(space.TargetXZ.HasValue);
        Vector2 aim = new(space.TargetXZ!.Value.X, space.TargetXZ.Value.Z);
        float distance = Vector2.Distance(ballXZ, aim);
        float crispFloor = Planner.MinCrispPower01(distance);
        Assert.True(space.Command.KickPower01 >= crispFloor - 1e-3f,
            $"the space ball must keep its crisp floor: {space.Command.KickPower01:F2} against {crispFloor:F2}");

        // The gate's own arithmetic: at the receiver's arrival the ball is only just past the aim point,
        // well inside the promise region — so the gate must keep the ball.
        ReachEstimate reach = TimeToReachModel.ToPoint(
            ReachKinematics.FromEntity(receiver), aim, CandidateGenerator.SpaceRaceHorizonSeconds);
        Assert.True(reach.Reachable);
        PassPlan plan = Planner.PlanGroundPassToRegion(ballXZ, aim, reach.Seconds, crispFloor);
        Assert.True(plan.Feasible);
        float rolledPast = Planner.BallDistanceAtTime(plan.Power01, reach.Seconds) - distance;
        Assert.True(rolledPast <= CandidateGenerator.SpacePromiseCatchRadiusMeters,
            $"a legitimate space ball rolled {rolledPast:F1} m past the aim point at the receiver's arrival");
        Assert.True(rolledPast < CandidateGenerator.SpacePromiseCatchRadiusMeters - 2f,
            $"the fixture must be well inside the region (rolled {rolledPast:F1} m past), not a boundary case");
    }

    /// <summary>
    /// AIB-10 / finding 4 part 2: the ground <see cref="PassSuccessModel.Estimate"/> measures the defender
    /// contest against <c>max(flight, receiverArrival)</c>.
    ///
    /// Fixture: a timed 10 m ball (`PlanGroundPassToRegion(..., arriveInSeconds: 2.0)`) planned at power
    /// 0.20, flight <b>1.450 s</b>, with a promise that the receiver meets it at <b>1.9 s</b> (radius 6 m,
    /// so the timing tolerance absorbs the 0.45 s early arrival and the receiver term stays 1). A defender
    /// standing 9 m off the aim point reaches it at <b>1.71 s</b> — between the ball and the receiver:
    /// <code>
    ///   pre-fix:  defenderMargin = 1.71 − 1.45 = +0.26 s -> defenderTerm = 1.00, p = 0.782
    ///   fixed  :  defenderMargin = 1.71 − 1.90 = −0.19 s -> defenderTerm = 0.12, p ≈ 0.22
    /// </code>
    /// A defender arriving after both (2.43 s &gt; 1.9 s, and outside the congestion window) still scores
    /// exactly like an empty picture. Pre-fix the hostile fixture priced identically to the open one.
    /// </summary>
    [Fact]
    public void AIB10_DefenderArrivingBeforeTheReceiver_IsCharged()
    {
        Vector2 origin = Vector2.Zero;
        Vector2 target = new(10f, 0f);
        PassPlan plan = Planner.PlanGroundPassToRegion(origin, target, arriveInSeconds: 2f);
        Assert.True(plan.Feasible);
        Assert.Equal(0.20f, plan.Power01, 3);
        Assert.Equal(1.450f, plan.FlightTimeSeconds, 3);

        FootballerState self = Player(origin);
        PerceivedEntity receiver = Mate(new Vector2(4f, 0f));
        var promise = new PassArrivalPromise(target, 1.9f, 6f, 1f);
        PassSuccessTerms open = Model.Estimate(
            Observation(self, receiver), receiver, plan, laneClearance01: 0.5f, promise);
        Assert.Equal(0.7820f, open.CompletionProbability01, 3);
        Assert.Equal(0f, open.DefenderPressure01, 5); // no defender: neutral term

        PerceivedEntity interceptor = Opponent(14, new Vector2(10f, 9f), Vector2.Zero);
        PassSuccessTerms contested = Model.Estimate(
            Observation(self, receiver, interceptor), receiver, plan, laneClearance01: 0.5f, promise);

        Assert.Equal(1.71f, contested.DefenderArrivalSeconds, 2);
        // The published margin is against the receiver's real arrival, not the ball's flight.
        Assert.Equal(contested.DefenderArrivalSeconds - promise.ArrivalSeconds,
            contested.DefenderMarginSeconds, 4);
        float preFixMargin = contested.DefenderArrivalSeconds - plan.FlightTimeSeconds;
        float preFixTerm = Math.Clamp(
            0.5f + preFixMargin / PassSuccessModel.DefenderMarginScaleSeconds, 0f, 1f);
        Assert.Equal(1f, preFixTerm, 3); // the whole defect: the pre-fix defender term was neutral
        Assert.True(contested.DefenderPressure01 > 0.80f,
            $"a defender arriving before the receiver must be charged, not neutral ({contested.DefenderPressure01:F2})");
        Assert.True(contested.CompletionProbability01 < open.CompletionProbability01 - 0.4f,
            $"the hostile promise ({contested.CompletionProbability01:F3}) must price below the open one " +
            $"({open.CompletionProbability01:F3}); pre-fix both read the ball's flight and scored the same");

        // A defender arriving after BOTH still scores neutral: the rule must not charge late arrivals.
        PerceivedEntity late = Opponent(15, new Vector2(10f, 15f), Vector2.Zero);
        PassSuccessTerms lateTerms = Model.Estimate(
            Observation(self, receiver, late), receiver, plan, laneClearance01: 0.5f, promise);
        Assert.True(lateTerms.DefenderArrivalSeconds > promise.ArrivalSeconds + 0.25f,
            $"the late fixture must arrive after both instants ({lateTerms.DefenderArrivalSeconds:F2} s)");
        Assert.Equal(open.CompletionProbability01, lateTerms.CompletionProbability01, 5);
        Assert.Equal(0f, lateTerms.DefenderPressure01, 5);
        Assert.Equal(lateTerms.DefenderArrivalSeconds - promise.ArrivalSeconds,
            lateTerms.DefenderMarginSeconds, 4);
    }

    /// <summary>
    /// AIB-10 consistency: the raised/airborne estimator applies the same <c>max(flight, receiverArrival)</c>
    /// rule. Fixture: a 30 m lofted delivery lands at (30.33, 0) after <b>2.02 s</b>; the receiver stands at
    /// (10, 0) and cannot reach the landing inside the flight window, so his real arrival is the model's
    /// later convention (flight + 1 s = 3.02 s). A defender 18 m from the landing gets there at
    /// <b>2.79 s</b> — after the ball, before the receiver:
    /// <code>
    ///   pre-fix:  (2.79 − 2.02) / (1.0 s arrival scale)  ⇒ term 1.00, LandingPressure 0.00
    ///   fixed  :  (2.79 − 3.02) / 1.0                    ⇒ term 0.27, LandingPressure 0.73
    /// </code>
    /// The model's landing-pressure term is asserted against the reach model's own arrival, not a literal.
    /// </summary>
    [Fact]
    public void AIB10_RaisedEstimator_AlsoMeasuresTheContestAgainstTheReceiverArrival()
    {
        FootballerState self = Player(Vector2.Zero);
        Vector2 target = new(30f, 0f);
        PerceivedEntity receiver = Mate(new Vector2(10f, 0f));
        FootballerObservation clean = Observation(self, receiver);
        LoftedPassTerms cleanTerms = Model.EstimateRaised(clean, receiver, target, RaisedPassProfile.Lofted, 0.5f);
        Assert.True(cleanTerms.Feasible);
        Assert.Equal(2.02f, cleanTerms.FlightTimeSeconds, 2);
        Vector2 landing = cleanTerms.LandingXZ;
        Assert.True(Vector2.Distance(landing, new Vector2(30.33f, 0f)) < 0.1f,
            $"the landing must be the calibrated one, got {landing}");

        ReachEstimate receiverReach = TimeToReachModel.ToPoint(
            ReachKinematics.FromEntity(receiver), landing, MathF.Max(cleanTerms.FlightTimeSeconds, 1f));
        Assert.False(receiverReach.Reachable, "the fixture needs a receiver who cannot reach the landing with the ball");
        float receiverArrival = cleanTerms.FlightTimeSeconds + 1f;

        PerceivedEntity defender = Opponent(14, landing + new Vector2(0f, 18f), Vector2.Zero);
        LoftedPassTerms contested = Model.EstimateRaised(
            Observation(self, receiver, defender), receiver, target, RaisedPassProfile.Lofted, 0.5f);

        ReachEstimate defenderReach = TimeToReachModel.ToPoint(
            ReachKinematics.FromEntity(defender), landing, MathF.Max(cleanTerms.FlightTimeSeconds + 1f, 1f));
        float defenderArrival = (defenderReach.Reachable ? defenderReach.Seconds : cleanTerms.FlightTimeSeconds + 1f)
                                + PassSuccessModel.OpponentReactionSeconds;
        Assert.Equal(2.79f, defenderArrival, 2);

        float arrivalScale = PassSuccessModel.DefenderMarginScaleSeconds * PassSuccessModel.AirborneContestScale;
        float preFixTerm = Math.Clamp(
            0.5f + (defenderArrival - cleanTerms.FlightTimeSeconds) / arrivalScale, 0f, 1f);
        float fixedTerm = Math.Clamp(0.5f + (defenderArrival - receiverArrival) / arrivalScale, 0f, 1f);
        Assert.Equal(1f, preFixTerm, 3); // pre-fix: a defender who arrives with the ball is not "pressure"
        Assert.Equal(1f - fixedTerm, contested.LandingPressure01, 3);
        Assert.True(contested.LandingPressure01 > 0.70f,
            $"a defender reaching the landing before the receiver must be charged " +
            $"(pressure {contested.LandingPressure01:F2}, pre-fix 0.00)");

        // Sanity: a landing nobody else can reach still reads as uncontested.
        PerceivedEntity tooLate = Opponent(15, landing + new Vector2(0f, 30f), Vector2.Zero);
        LoftedPassTerms uncontested = Model.EstimateRaised(
            Observation(self, receiver, tooLate), receiver, target, RaisedPassProfile.Lofted, 0.5f);
        Assert.Equal(0f, uncontested.LandingPressure01, 5);
    }

    // =============================================================================================
    // AIB-11 — the advertised ToRun ball is crisp, not a timed roller
    // =============================================================================================

    /// <summary>
    /// AIB-11: the advertised <c>ToRun</c> ball is struck with the crisp floor, so its flight is materially
    /// shorter than the timed roller the old <c>MinPower01: 0f</c> path produced at the same distance.
    ///
    /// Fixture: the carrier has the ball at (0, 8) and team-mate #9 stands at (6, 8) with a live
    /// advertisement to (16, 8) at tick 608 — 1.8 s away, 16.0 m from the ball. Arithmetic:
    /// <code>
    ///   pre-fix timed plan (MinPower01 0): 0.50 power / 1.800 s flight  — the audit's slow roller
    ///   crisp floor MinCrispPower01(16):   0.80 power, table flight ≈1.18 s — the candidate's command
    ///   promise timing: |1.18 − 1.80| − tolerance(5 m radius) > 0, so the model charges the early
    ///   arrival (AUD-P5's stated trade); completion 0.57 stays above the 0.30 delivery floor.
    /// </code>
    /// Pre-fix the candidate's power was 0.50 and its 1.80 s flight is exactly the kind of slow ball the
    /// lane test then rejected (the audit's Images 8/9). The assertion is on the power/flight, not on a
    /// magic pass/fail.
    /// </summary>
    [Fact]
    public void AIB11_AdvertisedRunBall_HasTheCrispFloorAndAShorterFlight()
    {
        Vector2 ballXZ = new(0f, 8f);
        PerceivedEntity runner = Mate(new Vector2(6f, 8f));
        FootballerObservation observation = Observation(
            Player(new Vector2(0f, 8f)), runner, Opponent(20, new Vector2(-8f, 2f), Vector2.Zero));
        int meetTick = ObservationTick + 108; // 1.8 s
        MatchCommunicationContext communication = AnnouncedRun(MateId, new Vector2(16f, 8f), meetTick);
        var generator = new CandidateGenerator(Pitch, communication, PlainPicture()) { TimeOnBall = 0.8f };

        generator.GenerateOnBallCandidates(observation, new Vector3(Pitch.HalfLength, 0f, 0f), 40f);

        UtilityCandidate run = Single(generator, CandidateGenerator.RunPassTag, MateId);
        Vector2 meetXZ = new(16f, 8f);
        float distance = Vector2.Distance(ballXZ, meetXZ);
        float crispFloor = Planner.MinCrispPower01(distance);
        Assert.Equal(0.80f, crispFloor, 3);
        Assert.Equal(0.80f, run.Command.KickPower01, 3);
        Assert.True(run.Command.KickPower01 >= crispFloor - 1e-3f,
            $"the advertised run ball was struck at {run.Command.KickPower01:F2}; the crisp floor for " +
            $"{distance:F2} m is {crispFloor:F2}");

        PassPlan preFix = Planner.PlanGroundPassToRegion(ballXZ, meetXZ, arriveInSeconds: 1.8f);
        Assert.True(preFix.Feasible);
        Assert.Equal(0.50f, preFix.Power01, 3);
        Assert.True(preFix.FlightTimeSeconds > 1.7f,
            $"the pre-fix timed plan must be the slow roller the audit names (flight {preFix.FlightTimeSeconds:F3} s)");
        Assert.Equal(1.80f, preFix.FlightTimeSeconds, 3);
        Assert.True(run.Command.KickPower01 > preFix.Power01 + 0.25f,
            $"the crisp floor only moved the timed ball {preFix.Power01:F2} → {run.Command.KickPower01:F2}");

        Assert.True(Planner.TryGetFlightTime(run.Command.KickPower01, distance, out float firmFlight),
            "the planner table must have a flight for the emitted command");
        Assert.True(firmFlight <= preFix.FlightTimeSeconds - 0.5f,
            $"the firm run ball must arrive materially sooner ({firmFlight:F3} s against the roller's " +
            $"{preFix.FlightTimeSeconds:F3} s)");
        Assert.True(run.Terms.Probability01 > CandidateGenerator.MinViableDeliveryCompletion01,
            $"the firm run ball must survive the delivery floor (p {run.Terms.Probability01:F3})");
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
    /// Replicates the pre-fix sampled-run test exactly: the minimum distance between the defender's
    /// constant-velocity run and the ball's own sampled path, with no reach and no doubt. The fixed model
    /// returns the same geometry against its documented reach; the test asserts the pre-fix verdict from
    /// this number, so the fixture cannot stop exercising the defect silently.
    /// </summary>
    private static float MinSampledDistance(
        PerceivedEntity defender,
        Vector2 from,
        Vector2 to,
        float power,
        float flight,
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
            Vector2 ball = from + direction * MathF.Min(Planner.BallDistanceAtTime(power, t), length);
            float distance = Vector2.Distance(position + velocity * t, ball);
            if (distance >= best) continue;
            best = distance;
            atTime = t;
        }

        return best;
    }

    /// <summary>
    /// The pre-fix step-in test on this defender: the perpendicular foot projection, the full reaction, and
    /// the one reach query. Used only to assert that the fixture is out of the old model's reach.
    /// </summary>
    private static ReachEstimate StepIn(
        PerceivedEntity defender,
        Vector2 from,
        Vector2 to,
        float power,
        float availableSeconds)
    {
        Vector2 segment = to - from;
        float length = segment.Length();
        Vector2 direction = segment / length;
        Vector2 relative = new Vector2(defender.EstimatedPosition.X, defender.EstimatedPosition.Z) - from;
        float projection = Math.Clamp(Vector2.Dot(relative, direction), 0f, length);
        return TimeToReachModel.ToPoint(
            ReachKinematics.FromEntity(defender), from + direction * projection, availableSeconds);
    }

    private static bool OpponentStepInReachable(
        PerceivedEntity defender,
        Vector2 from,
        Vector2 to,
        float power,
        float reactionSeconds)
    {
        Vector2 segment = to - from;
        float length = segment.Length();
        Vector2 direction = segment / length;
        Vector2 relative = new Vector2(defender.EstimatedPosition.X, defender.EstimatedPosition.Z) - from;
        float projection = Math.Clamp(Vector2.Dot(relative, direction), 0f, length);
        if (!Planner.TryGetFlightTime(power, projection, out float timeToPoint)) return false;
        float available = timeToPoint - reactionSeconds;
        if (available <= 0f) return false;
        ReachEstimate reach = TimeToReachModel.ToPoint(
            ReachKinematics.FromEntity(defender), from + direction * projection, available);
        return reach.Reachable && reach.Seconds <= available;
    }

    private static PerceivedEntity Opponent(int id, Vector2 position, Vector2 velocity) =>
        new(id, TeamId.Away, TacticalRole.CenterBack,
            new Vector3(position.X, 0f, position.Y), new Vector3(velocity.X, 0f, velocity.Y), 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };

    private static PerceivedEntity Mate(Vector2 position) =>
        new(MateId, TeamId.Home, TacticalRole.CentralMidfielder,
            new Vector3(position.X, 0f, position.Y), Vector3.Zero, 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };

    private static FootballerObservation Observation(FootballerState self, params PerceivedEntity[] visible) => new()
    {
        SelfId = self.Id,
        SelfTeam = self.Team,
        SelfRole = self.Role,
        SelfPosition = self.Position,
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
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

    private static FootballerState Player(Vector2 position) => new()
    {
        Id = CarrierId,
        Team = TeamId.Home,
        ShirtNumber = CarrierId,
        Role = TacticalRole.CentralMidfielder,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(TacticalRole.CentralMidfielder),
        Position = new Vector3(position.X, 0f, position.Y),
        FacingXZ = new Vector2(1f, 0f)
    };

    /// <summary>A communication context in which the given team-mate has announced a meeting region.</summary>
    private static MatchCommunicationContext AnnouncedRun(int runnerId, Vector2 region, int meetTick)
    {
        var communication = new MatchCommunicationContext();
        int publishedTick = ObservationTick - RunAdvertisementBoard.CommunicationDelayTicks;
        var path = new RunPath(
            RunKind.InBehind,
            region - new Vector2(6f, 0f),
            region,
            MeetRadiusMeters: 5f,
            publishedTick,
            meetTick,
            meetTick + 60,
            HoldUntilTick: publishedTick,
            region - new Vector2(6f, 0f),
            0.85f);
        communication.Runs.Publish(
            TeamId.Home, runnerId, new RunIntent(path, TacticalJob.DepthRun, publishedTick), publishedTick);
        return communication;
    }

    private static UtilityCandidate Single(CandidateGenerator generator, string tag, int targetPlayerId)
    {
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            UtilityCandidate candidate = generator.Candidates[i];
            if (candidate.Key.Kind != CandidateKind.Pass) continue;
            if (candidate.Key.TargetPlayerId != targetPlayerId) continue;
            if (string.Equals(candidate.Key.TargetTag, tag, StringComparison.Ordinal)) return candidate;
        }

        var names = new List<string>();
        for (int i = 0; i < generator.CandidateCount; i++) names.Add(generator.Candidates[i].ActionName);
        throw new Xunit.Sdk.XunitException(
            $"no '{tag}' pass to #{targetPlayerId}: {(names.Count == 0 ? "(empty candidate set)" : string.Join(" | ", names))}");
    }
}
