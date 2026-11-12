using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Physics;

namespace FootballSim.AI;

/// <summary>
/// One ground pass's estimated chance of reaching its receiver, with the terms that produced it kept
/// alongside the number (A4.2).
///
/// The terms exist because the plan's value convention needs them: a pass is scored
/// <c>p·gain − (1−p)·loss</c>, so the completion probability has to be one inspectable quantity. It is
/// deliberately not a "pass quality" score — <see cref="DefenderPressure01"/> and
/// <see cref="ReceiverApproachDot"/> are the reception terms A4.3 values, not corrections applied here.
/// </summary>
public readonly record struct PassSuccessTerms(
    float CompletionProbability01,
    bool LaneBlocked,
    float LaneClearance01,
    float FlightTimeSeconds,
    float ReceiverArrivalSeconds,
    float ReceiverMarginSeconds,
    float ReceiverArrivalSpeed,
    float ReceiverApproachDot,
    float DefenderArrivalSeconds,
    float DefenderMarginSeconds,
    float DefenderPressure01,
    float DefenderUncertaintySeconds)
{
    /// <summary>True when the pass is geometrically blocked by a defender the ball would run into.</summary>
    public bool IsBlocked => LaneBlocked;

    /// <summary>Worst-case (nothing known) estimate: no line, no probability.</summary>
    public static PassSuccessTerms None => new(0f, false, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 0f);
}

/// <summary>
/// Estimates how likely a planned ground pass is to reach its receiver (A4.2): the calibrated trajectory
/// from <see cref="PassPlanner"/> plus what happens between the kick and the touch — the receiver's
/// arrival margin, a defender's arrival margin at the same point, the passing lane, the receiver's
/// arriving speed and body orientation, and the uncertainty of a stale sighting.
///
/// Design rules this type exists to enforce:
/// <list type="bullet">
/// <item><b>One reach model.</b> Every arrival here is a <see cref="TimeToReachModel"/> query. There is
/// no second distance/speed estimate in this file, and there is no overload that takes a
/// <c>FootballerState</c> — an opponent is measured through its <see cref="CapabilityEstimate"/> band
/// (A2.3), so a scouted faster defender lowers the estimate and an unscouted one is assumed neutral.</item>
/// <item><b>One risk owner.</b> The completion probability is the only probability this model returns.
/// The pass score multiplies it by a gain and charges <c>1−p</c> at the loss cost; the reception value
/// (A4.3) adds value on top and must not subtract transition risk again.</item>
/// <item><b>No duplicate penalties.</b> The receiver's reaction time is not charged here: the reach
/// model's acceleration ramp is exactly the cost of a player who has not started moving yet. Only an
/// <i>opponent's</i> reaction is charged separately, because a defender who has not read the pass is not
/// modelled by a trajectory at all.</item>
/// <item><b>Uncertainty is charged against the option.</b> A stale sighting widens a defender's
/// reachability; pretending a remembered position is exact is how a scorer talks itself into a pass.</item>
/// </list>
///
/// A ground pass rolls at ball height, so height does not enter a ground estimate. The raised
/// alternative is estimated separately (<see cref="EstimateLofted"/>) because scoring an airborne ball
/// with a ground flight time is exactly the mistake A5.5 warns about.
/// </summary>
public sealed class PassSuccessModel
{
    /// <summary>Assumed top pace of an opponent closing a lane (the default sprint speed).</summary>
    public const float OpponentSprintSpeed = 8.4f;

    /// <summary>
    /// How close to the pass line an opponent's body has to be for the ball to run into their reach.
    /// Execution lets a body within <c>ReachRadius × 1.10 ≈ 0.83 m</c> play the ball; this leaves a
    /// margin for the predicted positions.
    /// </summary>
    public const float LaneContactReachMeters = 0.9f;

    /// <summary>
    /// How long an opponent needs to notice the pass before starting to close it. A defender does not
    /// begin moving the instant the ball leaves the passer's foot.
    /// </summary>
    public const float OpponentReactionSeconds = 0.25f;

    /// <summary>
    /// Pace at which an opponent can start closing a lane even when their current run is elsewhere
    /// (a turn plus the first steps), on top of any closing speed they already carry.
    /// </summary>
    public const float LaneTurnSpeed = 1.5f;

    /// <summary>
    /// Largest gap the ball may travel between two lane samples, in metres. The lane test asks "does the
    /// ball run into a body at some instant", so the sampling step has to be smaller than the contact
    /// radius or the ball can step straight over a defender standing on the line. It used to be a fixed
    /// 0.1 s of flight, which at a 19 m/s pass is a 1.94 m step — more than twice the 0.9 m reach.
    /// </summary>
    public const float LaneSampleStepMeters = 0.45f;

    /// <summary>Lower bound on lane samples: a short pass still has to be checked properly.</summary>
    public const int MinLaneSamples = 8;

    /// <summary>Upper bound on lane samples, so the hard-rejection test stays bounded per candidate.</summary>
    public const int MaxLaneSamples = 64;

    /// <summary>Seconds of defender margin at which the arrival term is neutral (0.5).</summary>
    public const float DefenderMarginScaleSeconds = 0.50f;

    /// <summary>Seconds of receiver margin at which the receiver term is neutral (0.5).</summary>
    public const float ReceiverMarginScaleSeconds = 0.60f;

    /// <summary>Share of the defender-side confidence that comes from the lane rather than arrival.</summary>
    public const float LaneWeight = 0.30f;

    /// <summary>Hard floor on the reported probability; a feasible pass is never exactly impossible.</summary>
    public const float MinCompletion01 = 0.02f;

    /// <summary>
    /// Ceiling on the reported probability. Even a perfect pass can be mis-controlled or miss its
    /// target, and the execution layer's own error terms are charged at the contact, not here.
    /// </summary>
    public const float MaxCompletion01 = 0.97f;

    /// <summary>Largest control penalty the receiver's speed, orientation and uncertainty can charge.</summary>
    public const float MaxControlPenalty01 = 0.18f;

    /// <summary>How much an extra second of defender uncertainty costs in arrival time.</summary>
    public const float UncertaintyArrivalSecondsPerMeter = 0.05f;

    /// <summary>Cap on the uncertainty charged to one defender, in seconds.</summary>
    public const float MaxUncertaintySeconds = 0.35f;

    /// <summary>Extra horizon beyond the flight time within which a defender's arrival still matters.</summary>
    public const float DefenderHorizonSlackSeconds = 0.8f;

    /// <summary>Approach alignment below which the receiver is charged for having to turn back to the ball.</summary>
    public const float ReceiverApproachThreshold = 0.20f;

    /// <summary>Share of a raised pass's completion that survives unmodelled flight behaviour (A5.5).</summary>
    public const float LoftedExecutionPenalty = 0.55f;

    /// <summary>Ground acceleration assumed for the raised alternative's kinematic flight (m/s²).</summary>
    public const float Gravity = 9.81f;

    /// <summary>How much shorter a raised ball actually lands than a drag-free estimate.</summary>
    public const float LoftedDragShortfall01 = 0.15f;

    private readonly PassPlanner _planner;

    public PassSuccessModel(PassPlanner planner)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
    }

    /// <summary>
    /// Estimates a planned ground pass. <paramref name="laneClearance01"/> is the caller's lane metric
    /// (<see cref="CandidateGenerator.EvaluateLaneClearance"/>); it is passed in rather than recomputed so
    /// a decision scores one lane once.
    /// </summary>
    public PassSuccessTerms Estimate(
        in FootballerObservation obs,
        in PerceivedEntity receiver,
        in PassPlan plan,
        float laneClearance01)
    {
        float lane = Math.Clamp(float.IsFinite(laneClearance01) ? laneClearance01 : 0f, 0f, 1f);
        if (!plan.Feasible)
            return PassSuccessTerms.None with { LaneClearance01 = lane };

        float flight = Math.Clamp(plan.FlightTimeSeconds, 0.05f, 6f);
        Vector2 target = plan.TargetXZ;

        // 1. Can the receiver be there when the ball is?
        ReachKinematics receiverState = ReachKinematics.FromEntity(receiver);
        ReachEstimate receiverReach = TimeToReachModel.ToPoint(
            receiverState, target, MathF.Max(flight, 0.8f));
        float receiverArrival = receiverReach.Reachable ? receiverReach.Seconds : flight + 1f;
        float receiverMargin = flight - receiverArrival;

        // 2. Can an opponent beat the ball to the same point? The earliest one decides.
        float defenderMargin = float.PositiveInfinity;
        float defenderArrival = float.PositiveInfinity;
        float defenderUncertainty = 0f;
        float horizon = Math.Clamp(flight + DefenderHorizonSlackSeconds, 1f, 3.5f);

        foreach (PerceivedEntity entity in obs.VisiblePlayers)
        {
            if (entity.Team == obs.SelfTeam) continue;

            ReachEstimate reach = TimeToReachModel.ToPoint(ReachKinematics.FromEntity(entity), target, horizon);
            float arrival = reach.Reachable ? reach.Seconds : horizon;
            arrival += OpponentReactionSeconds;

            // A remembered position may be wrong in the defender's favour. Charge the worst case.
            float uncertainty = Math.Clamp(
                entity.PositionUncertaintyMeters * UncertaintyArrivalSecondsPerMeter, 0f, MaxUncertaintySeconds);
            float effective = arrival - uncertainty;

            if (effective < defenderArrival)
            {
                defenderArrival = effective;
                defenderMargin = effective - flight;
                defenderUncertainty = uncertainty;
            }
        }

        if (!float.IsFinite(defenderArrival))
        {
            defenderArrival = horizon + OpponentReactionSeconds;
            defenderMargin = defenderArrival - flight;
        }

        // 3. The three sides of the estimate, each clamped to a probability.
        float receiverTerm = Math.Clamp(0.5f + receiverMargin / ReceiverMarginScaleSeconds, 0f, 1f);
        float defenderTerm = Math.Clamp(0.5f + defenderMargin / DefenderMarginScaleSeconds, 0f, 1f);

        Vector2 receiverXZ = new(receiver.EstimatedPosition.X, receiver.EstimatedPosition.Z);
        Vector2 receiverVelocityXZ = new(receiver.EstimatedVelocity.X, receiver.EstimatedVelocity.Z);
        float approachDot = ApproachAlignment(receiverXZ, target, receiverVelocityXZ);

        float controlPenalty = ReceiverControlPenalty(
            receiverReach.SpeedAtArrival, approachDot, receiver.PositionUncertaintyMeters);
        float control = 1f - controlPenalty;

        float completion = receiverTerm * ((1f - LaneWeight) * defenderTerm + LaneWeight * lane) * control;
        bool blocked = IsLaneBlocked(plan, obs);
        if (blocked) completion = MathF.Min(completion, 0.05f);

        return new PassSuccessTerms(
            CompletionProbability01: Math.Clamp(completion, MinCompletion01, MaxCompletion01),
            LaneBlocked: blocked,
            LaneClearance01: lane,
            FlightTimeSeconds: flight,
            ReceiverArrivalSeconds: receiverArrival,
            ReceiverMarginSeconds: receiverMargin,
            ReceiverArrivalSpeed: receiverReach.SpeedAtArrival,
            ReceiverApproachDot: approachDot,
            DefenderArrivalSeconds: defenderArrival,
            DefenderMarginSeconds: defenderMargin,
            DefenderPressure01: Math.Clamp(1f - defenderTerm, 0f, 1f),
            DefenderUncertaintySeconds: defenderUncertainty);
    }

    /// <summary>
    /// Estimates a raised (lofted) pass to the same receiver (A4.2). Kept deliberately separate from the
    /// ground estimate because the flight is a different physical problem, and deliberately
    /// <b>conservative</b>: the horizontal range is a drag-free kinematic estimate from the execution
    /// layer's own launch parameters, then shortened by <see cref="LoftedDragShortfall01"/>, and the
    /// completion carries <see cref="LoftedExecutionPenalty"/>. A5.5 replaces this with the real airborne
    /// trajectory before raised deliveries are scored against ground options as equals.
    /// </summary>
    public LoftedPassTerms EstimateLofted(
        in FootballerObservation obs,
        in PerceivedEntity receiver,
        float distanceMeters,
        float laneClearance01)
    {
        if (!float.IsFinite(distanceMeters) || distanceMeters <= 1f)
            return LoftedPassTerms.None;

        float power = LoftedPowerForDistance(distanceMeters, out float flightSeconds, out float range);
        if (power <= 0f)
            return LoftedPassTerms.None;

        // The landing point is where the ball comes down, not where it was aimed.
        Vector2 receiverXZ = new(receiver.EstimatedPosition.X, receiver.EstimatedPosition.Z);
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 aim = SimMath.SafeNormalize(receiverXZ - selfXZ, new Vector2(obs.AttackingDirectionX, 0f));
        Vector2 landing = selfXZ + aim * range;

        ReachEstimate receiverReach = TimeToReachModel.ToPoint(
            ReachKinematics.FromEntity(receiver), landing, MathF.Max(flightSeconds, 1f));
        float receiverArrival = receiverReach.Reachable ? receiverReach.Seconds : flightSeconds + 1f;
        float receiverTerm = Math.Clamp(
            0.5f + (flightSeconds - receiverArrival) / ReceiverMarginScaleSeconds, 0f, 1f);

        float defenderTerm = 1f;
        foreach (PerceivedEntity entity in obs.VisiblePlayers)
        {
            if (entity.Team == obs.SelfTeam) continue;
            ReachEstimate reach = TimeToReachModel.ToPoint(
                ReachKinematics.FromEntity(entity), landing, MathF.Max(flightSeconds, 1f));
            float arrival = (reach.Reachable ? reach.Seconds : flightSeconds + 1f) + OpponentReactionSeconds;
            float term = Math.Clamp(0.5f + (arrival - flightSeconds) / DefenderMarginScaleSeconds, 0f, 1f);
            if (term < defenderTerm) defenderTerm = term;
        }

        float lane = Math.Clamp(float.IsFinite(laneClearance01) ? laneClearance01 : 0f, 0f, 1f);
        float completion = receiverTerm
                           * ((1f - LaneWeight) * defenderTerm + LaneWeight * lane)
                           * LoftedExecutionPenalty;

        return new LoftedPassTerms(
            Feasible: true,
            Power01: power,
            FlightTimeSeconds: flightSeconds,
            FlightRangeMeters: range,
            DistanceMeters: distanceMeters,
            CompletionProbability01: Math.Clamp(completion, MinCompletion01, MaxCompletion01),
            LandingPressure01: Math.Clamp(1f - defenderTerm, 0f, 1f))
        {
            LandingXZ = landing
        };
    }

    /// <summary>
    /// Lowest power whose (drag-shortened) raised flight still reaches the receiver. Returns 0 when no
    /// power in the execution layer's raised-pass band covers the distance.
    /// </summary>
    private static float LoftedPowerForDistance(float distanceMeters, out float flightSeconds, out float rangeMeters)
    {
        flightSeconds = 0f;
        rangeMeters = 0f;

        for (int step = 0; step <= LoftedPowerSteps; step++)
        {
            float power = LoftedMinPower + step * LoftedPowerStep;
            float vertical = Math.Clamp(5.2f + power * 4.5f, 5.0f, 9.8f);
            float horizontal = Math.Clamp(9.5f + power * 11.5f, 9.0f, 21.0f);
            float flight = 2f * vertical / Gravity;
            float range = horizontal * flight * (1f - LoftedDragShortfall01);
            if (range < distanceMeters) continue;

            flightSeconds = flight;
            rangeMeters = range;
            return power;
        }

        return 0f;
    }

    /// <summary>Raised-pass power band the execution layer can actually strike (0.15 … 1.00).</summary>
    public const float LoftedMinPower = 0.15f;

    /// <summary>Power step of the raised-pass search.</summary>
    public const float LoftedPowerStep = 0.05f;

    /// <summary>Number of raised-pass power samples.</summary>
    public const int LoftedPowerSteps = 17;

    /// <summary>
    /// True when a defender the ball would physically run into blocks the planned line. This is the
    /// "hard rejection for a clearly blocked ground pass" the plan requires: it is a physical test
    /// (sampled flight against each opponent's predicted run, plus a reaction-and-step estimate), not a
    /// probability threshold, so an open lane is never rejected for being merely average.
    /// </summary>
    public bool IsLaneBlocked(in PassPlan plan, in FootballerObservation obs) =>
        IsLaneBlocked(
            new Vector2(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z),
            plan.TargetXZ,
            plan.Power01,
            plan.FlightTimeSeconds,
            obs.VisiblePlayers,
            obs.SelfTeam);

    /// <summary>
    /// The lane test on explicit inputs. Public so a probe or a test can ask the question without
    /// building an observation, and because the audit probe and the pass candidates must agree.
    /// </summary>
    public bool IsLaneBlocked(
        Vector2 fromXZ,
        Vector2 toXZ,
        float power01,
        float flightTimeSeconds,
        IReadOnlyList<PerceivedEntity> visiblePlayers,
        TeamId selfTeam)
    {
        Vector2 segment = toXZ - fromXZ;
        float length = segment.Length();
        // A pass backwards into the passer's own feet is never a pass: there is no line to check.
        if (length < 0.5f) return true;

        Vector2 direction = segment / length;
        Vector2 normal = new(-direction.Y, direction.X);
        float flight = flightTimeSeconds > 0f ? MathF.Min(flightTimeSeconds, 6f) : length / 12f;

        // The ball's own travel sets the sampling: one sample every half contact radius, so a defender the
        // ball passes within reach of cannot fall between two samples.
        float travelled = MathF.Min(_planner.BallDistanceAtTime(power01, flight), length);
        int samples = Math.Clamp(
            (int)MathF.Ceiling(MathF.Max(travelled, 1f) / LaneSampleStepMeters),
            MinLaneSamples,
            MaxLaneSamples);

        // One flight path for the whole query: every opponent is tested against the same ball positions,
        // so the trajectory is resolved once instead of once per defender. Stack-allocated and bounded by
        // MaxLaneSamples, so the fine sampling costs no allocation.
        Span<Vector2> ballPath = stackalloc Vector2[MaxLaneSamples + 1];
        for (int sample = 0; sample <= samples; sample++)
        {
            float t = flight * sample / samples;
            float ballDistance = MathF.Min(_planner.BallDistanceAtTime(power01, t), length);
            ballPath[sample] = fromXZ + direction * ballDistance;
        }

        for (int i = 0; i < visiblePlayers.Count; i++)
        {
            PerceivedEntity opponent = visiblePlayers[i];
            if (opponent.Team == selfTeam) continue;

            Vector2 opponentXZ = new(opponent.EstimatedPosition.X, opponent.EstimatedPosition.Z);
            Vector2 opponentVelocity = new(opponent.EstimatedVelocity.X, opponent.EstimatedVelocity.Z);
            Vector2 relative = opponentXZ - fromXZ;
            float signedPerpendicular = Vector2.Dot(relative, normal);
            float perpendicular = MathF.Abs(signedPerpendicular);
            float projectionRaw = Vector2.Dot(relative, direction);

            // Cheap reachability bound *before* the dense sampling: an opponent further from the ball's
            // line than the fastest close he can produce — a full sprint plus the turn allowance the
            // step-in rule below grants — cannot be on the ball within the flight, so sampling him is
            // wasted work. The bound uses the *largest* closing speed the rules below allow, which makes
            // it conservative: it skips a defender only when neither test could have found him.
            float reachable = LaneContactReachMeters + (OpponentSprintSpeed + LaneTurnSpeed) * flight;
            if (perpendicular > reachable) continue;

            // 1. Their current run, sampled against the ball's own flight: if the two ever come
            //    within touch range, the pass runs straight into them.
            float speed = opponentVelocity.Length();
            Vector2 runVelocity = speed > OpponentSprintSpeed
                ? opponentVelocity * (OpponentSprintSpeed / speed)
                : opponentVelocity;
            for (int sample = 0; sample <= samples; sample++)
            {
                float t = flight * sample / samples;
                if (Vector2.Distance(opponentXZ + runVelocity * t, ballPath[sample]) <= LaneContactReachMeters)
                    return true;
            }

            // 2. They can also see the pass and step into the lane. Compare what the step actually
            //    needs with what they can produce before the ball has gone by.
            float gap = perpendicular - LaneContactReachMeters;
            if (gap <= 0f) return true;

            float projection = Math.Clamp(projectionRaw, 0f, length);
            float timeToPoint = BallTimeToDistance(power01, projection, flightTimeSeconds, length);
            float available = timeToPoint - OpponentReactionSeconds;
            if (available <= 0f) continue;

            float closingTowardsLine = -MathF.Sign(signedPerpendicular) * Vector2.Dot(opponentVelocity, normal);
            float closingSpeed = Math.Clamp(closingTowardsLine, 0f, OpponentSprintSpeed) + LaneTurnSpeed;
            if (gap <= closingSpeed * available) return true;
        }

        return false;
    }

    /// <summary>
    /// Time the ball needs to reach <paramref name="distanceMeters"/> along its own flight, from the
    /// planner's calibrated distance/time table, falling back to a linear share of the plan's flight time.
    /// </summary>
    private float BallTimeToDistance(float power01, float distanceMeters, float flightTimeSeconds, float length)
    {
        if (_planner.TryGetFlightTime(power01, distanceMeters, out float time))
            return time;
        return flightTimeSeconds * Math.Clamp(distanceMeters / MathF.Max(length, 0.01f), 0f, 1f);
    }

    /// <summary>
    /// How aligned the receiver's current run is with the arrival point, 1 = running straight at it.
    /// A stationary receiver scores 0.8: he needs no turn to get there, but he has no momentum either.
    /// </summary>
    private static float ApproachAlignment(Vector2 receiverXZ, Vector2 targetXZ, Vector2 receiverVelocityXZ)
    {
        float speed = receiverVelocityXZ.Length();
        if (speed < 0.3f) return 0.8f;

        Vector2 toTarget = targetXZ - receiverXZ;
        float distance = toTarget.Length();
        if (distance < 0.05f) return 1f;

        return Math.Clamp(Vector2.Dot(toTarget / distance, receiverVelocityXZ / speed), -1f, 1f);
    }

    /// <summary>
    /// The receiver's share of the uncertainty: arriving at pace, having to turn back to the ball, or
    /// collecting a ball whose lead was computed from a stale sighting all cost control. Bounded, and
    /// documented as the only place these three terms are charged.
    /// </summary>
    private static float ReceiverControlPenalty(float arrivalSpeed, float approachDot, float uncertaintyMeters)
    {
        float speedFactor = Math.Clamp(arrivalSpeed / 8f, 0f, 1f);
        float turnFactor = Math.Clamp(
            (ReceiverApproachThreshold - approachDot) / 1.2f, 0f, 1f);
        float uncertaintyFactor = Math.Clamp(uncertaintyMeters / 2f, 0f, 1f);

        return Math.Clamp(
            0.08f * speedFactor + 0.10f * turnFactor + 0.06f * uncertaintyFactor,
            0f,
            MaxControlPenalty01);
    }
}

/// <summary>
/// Separate answer for a raised pass (A4.2): the flight is a different physical problem from a rolling
/// one, so it has its own estimate rather than a ground flight time dressed up as an aerial ball.
/// <see cref="LandingXZ"/> is where this estimate thinks the ball comes down (not where it was aimed),
/// which is the point the A4 audit is about: until A5.5 replaces the drag-free range with the real
/// airborne trajectory, a raised ball may land beyond the man it was played to.
/// </summary>
public readonly record struct LoftedPassTerms(
    bool Feasible,
    float Power01,
    float FlightTimeSeconds,
    float FlightRangeMeters,
    float DistanceMeters,
    float CompletionProbability01,
    float LandingPressure01)
{
    /// <summary>
    /// Estimated point where the raised ball comes down, in pitch XZ. Published for the inspector and for
    /// A5.5, and not part of the score until that milestone gives the flight a real prediction.
    /// </summary>
    public Vector2 LandingXZ { get; init; }

    /// <summary>No raised route to this receiver: out of the execution layer's band.</summary>
    public static LoftedPassTerms None => new(false, 0f, 0f, 0f, 0f, 0f, 1f);
}
