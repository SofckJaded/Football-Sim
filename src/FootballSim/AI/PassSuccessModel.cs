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

    /// <summary>
    /// The share of completion this pass lost to the lead (A4.10), after A5.3's promise discount. Zero for
    /// a promised ball, because the promise is priced in the completion itself; exposed so a trace can say
    /// which of the two terms charged what.
    /// </summary>
    public float LeadCharge01 { get; init; }

    /// <summary>True when the receiver's arrival was announced by an advertised run (A5.3).</summary>
    public bool PromisedArrival { get; init; }

    /// <summary>The runner's own confidence in the advertised meeting (0 when nothing was promised).</summary>
    public float PromisedConfidence01 { get; init; }
}

/// <summary>
/// A team-mate's promise (A5.3): the region he has committed to meet the ball in, when he plans to be
/// there, how wide that region is, and how sure he is of it.
///
/// This is the carrier's side of a run advertisement, expressed in the pass model's own terms so nothing
/// else in the model has to know what a run is. It is a statement by a team-mate — permitted
/// communication — and carries no opponent position, no ball trajectory and no live attribute. Its
/// absence (<see cref="None"/>) is the pre-A5.3 world: the model estimates the receiver's arrival itself.
/// </summary>
public readonly record struct PassArrivalPromise(
    Vector2 TargetXZ,
    float ArrivalSeconds,
    float MeetRadiusMeters,
    float Confidence01)
{
    /// <summary>No promise: the pass model falls back to estimating the receiver's arrival itself.</summary>
    public static PassArrivalPromise None => default;

    /// <summary>True when there is a promise worth pricing.</summary>
    public bool IsPresent =>
        Confidence01 > 0f &&
        float.IsFinite(ArrivalSeconds) &&
        float.IsFinite(MeetRadiusMeters) &&
        SimMath.IsFinite(TargetXZ);
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
/// (A4.3) adds value on top and must not subtract transition risk again. The lead is the same rule: an
/// unpromised lead is charged here (<see cref="LeadRisk01"/>), and a promised one is priced through the
/// promise's own confidence instead — never both (A5.3).</item>
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
    /// margin for the predicted positions. A *pressing* defender can reach further (~1.21 m) because he
    /// asks for a tackle, and A4.8 records that mismatch: widening this to 1.25 m halved the source-block
    /// interceptions on a thirty-minute segment (eight within two metres of the passer to one) but also
    /// flagged stationary defenders 1.5 m off the lane, and the A/B did not improve completion, so the
    /// number stays and A6's press policy owns it.
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

    /// <summary>
    /// Share of an airborne delivery's completion that survives the harder control it demands (A5.5).
    ///
    /// The flight is now the real trajectory (<see cref="RaisedPassModel"/>), so this is not an apology for
    /// an unmodelled ball. It is the control charge: a cross or a lob arrives out of the air, and the
    /// receiver plays it with a chest, a thigh or a head he never asked for — the execution layer has no
    /// header request from an AI receiver.
    /// </summary>
    public const float AirborneControl01 = 0.68f;

    /// <summary>
    /// Share of a <b>driven</b> delivery's completion that survives its pace (A5.5b).
    ///
    /// A driven ball is not an airborne ball and must not be charged as one: it is struck flat and fast
    /// (14–23 m/s against a rolled pass's 8–19.5) so that it arrives on the deck, and a receiver kills it
    /// with his foot. What it costs him is the pace — a bouncing ball at 20 m/s is harder to take than a
    /// rolled one — not the awkward body part <see cref="AirborneControl01"/> charges for. The driven
    /// delivery's real trade-off is already in the model: it gets there sooner, so the defender margin is
    /// better; it pays for that here.
    /// </summary>
    public const float DrivenControl01 = 0.86f;

    /// <summary>
    /// How much wider an airborne defender's arrival margin is, as a multiple of
    /// <see cref="DefenderMarginScaleSeconds"/> (A5.5).
    ///
    /// The ground estimate asks "can a defender be at the point when the ball is", and for a rolling ball
    /// that is the whole question. For a ball arriving two to four metres up it is not: a defender who can
    /// run to the landing point still has to out-jump the man the ball was aimed at, and that contest is
    /// priced on the reception side (the runner's finishing/aerial capability, A5.5). Without this the
    /// defender term collapsed to zero for any cross into a box containing a defender, which is every cross,
    /// and the delivery could never be offered at all — measured as zero crosses in a full reference match.
    /// </summary>
    public const float AirborneContestScale = 2.0f;

    /// <summary>
    /// Height below which an airborne ball can be stopped by a defender standing in its path, in metres. A
    /// defender's head is about 1.9 m and a jump reaches roughly 2.4 m; 2.2 m is the stated line between "he
    /// can get something on it" and "it has gone over him". The ground lane test is the wrong question for a
    /// ball this high, which is why airborne deliveries are tested against the calibrated height profile
    /// instead (A5.5).
    /// </summary>
    public const float AirborneBlockHeightMeters = 2.2f;

    /// <summary>
    /// How close to the landing an airborne defender is treated as contesting the ball rather than blocking
    /// the flight, in metres (A5.5).
    ///
    /// The distinction matters because the two are already paid for by different terms: a defender under the
    /// early flight can head the ball out (the lane), while a defender at the landing is in an aerial contest
    /// with the receiver (the defender term). Counting the second man in both terms is exactly the duplicate
    /// penalty the standing brief forbids, and it was enough to make every cross into a defended box
    /// impossible.
    /// </summary>
    public const float AirborneLandingContestMeters = 6f;

    /// <summary>
    /// Largest share of a pass's completion that is charged for leading a runner (A4.10).
    ///
    /// A pass is aimed at where the receiver will be when the ball arrives, which for a moving receiver is
    /// metres ahead of him. That is a bet that the run continues, and without a promise nothing in the
    /// simulation makes it continue: the runner's own controller is pursuing his duty, not the pass.
    /// Measured on the reference segment, a ball led 10–12 m completed worse than the same ball played to a
    /// man who was already there, and the failures were the runs that stopped. The model used to assume the
    /// run for free and rated those balls at p ≈ 0.92.
    ///
    /// A5.3 made the bet unnecessary where it can be made explicit: a runner who has advertised his meeting
    /// region is priced by that promise (confidence × timing) and this charge is not applied at all, because
    /// the same doubt must not cost twice. This constant therefore governs the <b>unpromised</b> lead only —
    /// a ball to feet, and a ball into space nobody has announced.
    /// </summary>
    public const float LeadRisk01 = 0.20f;

    /// <summary>Lead at which half of <see cref="LeadRisk01"/> is charged, in metres.</summary>
    public const float LeadRiskScaleMeters = 8f;

    /// <summary>
    /// Pace at which a promised meeting region is crossed, in m/s. It turns the region's width into a
    /// timing tolerance: a ball may arrive half a region early or late and still meet the runner inside the
    /// space he promised, which is a fraction of a second at this pace.
    /// </summary>
    public const float PromisedRegionCrossSpeed = 6.5f;

    /// <summary>Smallest timing tolerance a promised meeting gets, in seconds (a dot of a region).</summary>
    public const float MinPromisedArrivalToleranceSeconds = 0.15f;

    /// <summary>Largest timing tolerance a promised meeting gets, in seconds (a very wide region).</summary>
    public const float MaxPromisedArrivalToleranceSeconds = 0.50f;

    private readonly PassPlanner _planner;

    public PassSuccessModel(PassPlanner planner)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
    }

    /// <summary>
    /// Estimates a planned ground pass. <paramref name="laneClearance01"/> is the caller's lane metric
    /// (<see cref="CandidateGenerator.EvaluateLaneClearance"/>); it is passed in rather than recomputed so
    /// a decision scores one lane once. <paramref name="promise"/> is an advertised meeting region when
    /// one exists (A5.3); without it the arrival is estimated from the receiver's own velocity.
    /// </summary>
    public PassSuccessTerms Estimate(
        in FootballerObservation obs,
        in PerceivedEntity receiver,
        in PassPlan plan,
        float laneClearance01,
        PassArrivalPromise promise = default)
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

        bool promised = promise.IsPresent;
        float receiverArrival;
        float receiverTerm;

        if (promised)
        {
            // A5.3: the runner has said where and when he will be, and that is a better answer than the
            // earliest he could possibly arrive — a timed run deliberately holds back, which the reach
            // model has no way to know. So the arrival is the promise; what is left to price is how well
            // the ball's own flight matches it (inside the region's width) and how much the promise is
            // worth. A mistimed ball is charged by the same per-second scale as any other margin.
            receiverArrival = Math.Clamp(promise.ArrivalSeconds, 0f, 6f);
            float tolerance = Math.Clamp(
                MathF.Abs(promise.MeetRadiusMeters) * 0.5f / PromisedRegionCrossSpeed,
                MinPromisedArrivalToleranceSeconds, MaxPromisedArrivalToleranceSeconds);
            float mistiming = MathF.Max(0f, MathF.Abs(flight - receiverArrival) - tolerance);
            float timingTerm = Math.Clamp(1f - mistiming / ReceiverMarginScaleSeconds, 0f, 1f);
            receiverTerm = Math.Clamp(promise.Confidence01, 0f, 1f) * timingTerm;
        }
        else
        {
            receiverArrival = receiverReach.Reachable ? receiverReach.Seconds : flight + 1f;
            receiverTerm = Math.Clamp(0.5f + (flight - receiverArrival) / ReceiverMarginScaleSeconds, 0f, 1f);
        }

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

        // 3. The three sides of the estimate, each clamped to a probability. The receiver side was already
        // decided in step 1: either from the reach model, or from the promise that replaced it (A5.3).
        float defenderTerm = Math.Clamp(0.5f + defenderMargin / DefenderMarginScaleSeconds, 0f, 1f);

        Vector2 receiverXZ = new(receiver.EstimatedPosition.X, receiver.EstimatedPosition.Z);
        Vector2 receiverVelocityXZ = new(receiver.EstimatedVelocity.X, receiver.EstimatedVelocity.Z);
        float approachDot = ApproachAlignment(receiverXZ, target, receiverVelocityXZ);

        float controlPenalty = ReceiverControlPenalty(
            receiverReach.SpeedAtArrival, approachDot, receiver.PositionUncertaintyMeters);
        float control = 1f - controlPenalty;

        float completion = receiverTerm * ((1f - LaneWeight) * defenderTerm + LaneWeight * lane) * control;

        // A4.10/A5.3: the lead is charged once, by whichever owner knows about it.
        //
        // Without a promise the ball is aimed ahead of a moving receiver and the run that would take him
        // there is committed by nothing, so the completion is discounted by how far ahead the ball is.
        // With a promise the lead is exactly the region he announced: the uncertainty is the promise's own
        // confidence, already in `receiverTerm`, and charging the lead again would make the same doubt
        // cost twice (rule 6 of the standing brief). A promised ball is therefore not discounted here.
        float leadCharge = 0f;
        if (!promised)
        {
            Vector2 receiverNowXZ = new(receiver.EstimatedPosition.X, receiver.EstimatedPosition.Z);
            float leadMeters = Vector2.Distance(receiverNowXZ, target);
            if (leadMeters > 0f && float.IsFinite(leadMeters))
            {
                leadCharge = LeadRisk01 * leadMeters / (leadMeters + LeadRiskScaleMeters);
                completion *= 1f - leadCharge;
            }
        }

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
            DefenderUncertaintySeconds: defenderUncertainty)
        {
            LeadCharge01 = leadCharge,
            PromisedArrival = promised,
            PromisedConfidence01 = promised ? Math.Clamp(promise.Confidence01, 0f, 1f) : 0f
        };
    }

    /// <summary>
    /// Estimates a raised (lofted) pass to a team-mate at a stated distance (A4.2/A5.5). The flight comes
    /// from <see cref="RaisedPassModel"/> — the real airborne trajectory, not a drag-free approximation.
    /// </summary>
    public LoftedPassTerms EstimateLofted(
        in FootballerObservation obs,
        in PerceivedEntity receiver,
        float distanceMeters,
        float laneClearance01)
    {
        if (!float.IsFinite(distanceMeters) || distanceMeters <= 1f)
            return LoftedPassTerms.None;

        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 receiverXZ = new(receiver.EstimatedPosition.X, receiver.EstimatedPosition.Z);
        Vector2 aim = SimMath.SafeNormalize(receiverXZ - selfXZ, new Vector2(obs.AttackingDirectionX, 0f));
        Vector2 target = selfXZ + aim * distanceMeters;

        return EstimateRaised(obs, receiver, target, RaisedPassProfile.Lofted, laneClearance01);
    }

    /// <summary>
    /// Estimates an airborne delivery to a <b>future meeting point</b> (A5.5): the ball's real landing where
    /// the receiver is going, not where he is.
    ///
    /// The model is the same three-sided estimate the ground pass uses — can the receiver be at the landing
    /// when the ball is, can an opponent beat him there, is the lane there — with the flight and the landing
    /// taken from <see cref="RaisedPassModel"/>, which is calibrated from the execution layer's own launch
    /// through the real solver. Two differences from the ground estimate are deliberate: the landing is the
    /// point the physics produces, not the point that was aimed at, and the completion carries the control
    /// charge of the profile, because a ball arriving out of the air is harder to control than one arriving
    /// on the deck. The flight time is never a ground time: that was the A4 defect.
    ///
    /// <b>The power solve decides which doubt is priced (A5.5b).</b> A lofted or whipped ball is aimed at the
    /// place the runner is going — the region is the aim, and the power is whatever drops the ball there —
    /// so the remaining question is whether he can be at that landing in time, which is the reach estimate.
    /// A <b>driven</b> ball is scheduled instead: it is struck to be at the point when he gets there, so the
    /// promise is honoured by the power itself and what remains is the runner's own confidence in the
    /// announcement — exactly the substitution A5.3 made for the ground ball. A driven delivery without an
    /// announced arrival is not offered at all: there is nothing to time it to.
    /// </summary>
    public LoftedPassTerms EstimateRaised(
        in FootballerObservation obs,
        in PerceivedEntity receiver,
        Vector2 targetXZ,
        RaisedPassProfile profile,
        float laneClearance01,
        PassArrivalPromise promise = default)
    {
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        if (!SimMath.IsFinite(selfXZ) || !SimMath.IsFinite(targetXZ)) return LoftedPassTerms.None;

        float distance = Vector2.Distance(selfXZ, targetXZ);
        if (!float.IsFinite(distance) || distance <= 1f) return LoftedPassTerms.None;

        RaisedPassModel flights = RaisedPassModel.Default;
        bool promised = promise.IsPresent;
        float power;
        float range;
        float flight;

        if (profile == RaisedPassProfile.Driven)
        {
            if (!promised) return LoftedPassTerms.None;
            if (!flights.TryPowerForArrival(
                    profile, distance, promise.ArrivalSeconds, out power, out range, out flight))
            {
                return LoftedPassTerms.None;
            }
        }
        else if (!flights.TryPowerForLanding(profile, distance, out power, out range, out flight))
        {
            return LoftedPassTerms.None;
        }

        // The real landing, not the aimed point.
        Vector2 aim = SimMath.SafeNormalize(targetXZ - selfXZ, new Vector2(obs.AttackingDirectionX, 0f));
        Vector2 landing = selfXZ + aim * range;

        float receiverTerm;
        if (profile == RaisedPassProfile.Driven)
        {
            // Solved from the promised arrival: what is left to price is the runner's own confidence and
            // how well the ball's flight matched the moment he said he would be there.
            float tolerance = Math.Clamp(
                MathF.Abs(promise.MeetRadiusMeters) * 0.5f / PromisedRegionCrossSpeed,
                MinPromisedArrivalToleranceSeconds, MaxPromisedArrivalToleranceSeconds);
            float mistiming = MathF.Max(0f, MathF.Abs(flight - Math.Clamp(promise.ArrivalSeconds, 0f, 6f)) - tolerance);
            float timingTerm = Math.Clamp(1f - mistiming / ReceiverMarginScaleSeconds, 0f, 1f);
            receiverTerm = Math.Clamp(promise.Confidence01, 0f, 1f) * timingTerm;
        }
        else
        {
            ReachEstimate receiverReach = TimeToReachModel.ToPoint(
                ReachKinematics.FromEntity(receiver), landing, MathF.Max(flight, 1f));
            float receiverArrival = receiverReach.Reachable ? receiverReach.Seconds : flight + 1f;
            receiverTerm = Math.Clamp(0.5f + (flight - receiverArrival) / ReceiverMarginScaleSeconds, 0f, 1f);
        }

        float defenderTerm = 1f;
        float horizon = MathF.Max(flight + DefenderHorizonSlackSeconds, 1f);
        float defenderScale = profile == RaisedPassProfile.Driven
            ? DefenderMarginScaleSeconds
            : DefenderMarginScaleSeconds * AirborneContestScale;
        foreach (PerceivedEntity entity in obs.VisiblePlayers)
        {
            if (entity.Team == obs.SelfTeam) continue;
            ReachEstimate reach = TimeToReachModel.ToPoint(
                ReachKinematics.FromEntity(entity), landing, horizon);
            float arrival = (reach.Reachable ? reach.Seconds : horizon) + OpponentReactionSeconds;
            float term = Math.Clamp(0.5f + (arrival - flight) / defenderScale, 0f, 1f);
            if (term < defenderTerm) defenderTerm = term;
        }

        float lane = Math.Clamp(float.IsFinite(laneClearance01) ? laneClearance01 : 0f, 0f, 1f);

        // A5.5: the ground lane clearance is the wrong question for a ball two to four metres up. Every
        // raised profile is judged from the calibrated height profile instead — a defender only blocks if
        // the ball is low enough to be played where he stands. A driven ball is low at the point it passes
        // most defenders, which is exactly why it needs a lane, and the test says so.
        lane = IsAirborneLaneBlocked(
            selfXZ, targetXZ, profile, power, obs.VisiblePlayers, obs.SelfTeam)
            ? 0f
            : 1f;

        float control = profile == RaisedPassProfile.Driven ? DrivenControl01 : AirborneControl01;
        float completion = receiverTerm
                           * ((1f - LaneWeight) * defenderTerm + LaneWeight * lane)
                           * control;

        return new LoftedPassTerms(
            Feasible: true,
            Power01: power,
            FlightTimeSeconds: flight,
            FlightRangeMeters: range,
            DistanceMeters: distance,
            CompletionProbability01: Math.Clamp(completion, MinCompletion01, MaxCompletion01),
            LandingPressure01: Math.Clamp(1f - defenderTerm, 0f, 1f))
        {
            LandingXZ = landing,
            TargetXZ = targetXZ,
            Profile = profile
        };
    }

    /// <summary>
    /// True when a defender the airborne ball would pass low enough to touch is standing in its path (A5.5).
    ///
    /// The ground lane test samples a rolling ball against bodies; an aerial ball clears most of them. This
    /// one reads the calibrated height at the point where the ball crosses each defender's line and only
    /// counts him when that height is below <see cref="AirborneBlockHeightMeters"/> — so a centre-back
    /// standing on the cross's line 4 m short of the landing, where the ball is still 2.5 m up, is not a
    /// block, while the same man at the near post, where it is coming down, is.
    /// </summary>
    public bool IsAirborneLaneBlocked(
        Vector2 fromXZ,
        Vector2 toXZ,
        RaisedPassProfile profile,
        float power01,
        IReadOnlyList<PerceivedEntity> visiblePlayers,
        TeamId selfTeam)
    {
        Vector2 segment = toXZ - fromXZ;
        float length = segment.Length();
        if (length < 0.5f) return false;

        Vector2 direction = segment / length;
        Vector2 normal = new(-direction.Y, direction.X);
        RaisedPassModel flights = RaisedPassModel.Default;

        for (int i = 0; i < visiblePlayers.Count; i++)
        {
            PerceivedEntity opponent = visiblePlayers[i];
            if (opponent.Team == selfTeam) continue;

            Vector2 relative = new Vector2(
                opponent.EstimatedPosition.X, opponent.EstimatedPosition.Z) - fromXZ;
            float along = Vector2.Dot(relative, direction);
            if (along <= 0.5f || along >= length - AirborneLandingContestMeters) continue;
            if (MathF.Abs(Vector2.Dot(relative, normal)) > LaneContactReachMeters + 0.35f) continue;

            float height = flights.HeightAtDistance(profile, power01, along);
            if (height > AirborneBlockHeightMeters) continue;
            return true;
        }

        return false;
    }

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
/// Separate answer for an airborne delivery (A4.2/A5.5): the flight is a different physical problem from a
/// rolling one, so it has its own estimate rather than a ground flight time dressed up as an aerial ball.
/// <see cref="LandingXZ"/> is the point the ball is over when it has covered the delivery distance — where it
/// comes down for a lofted or whipped ball, and where it is at the promised moment for a driven one — read
/// from the calibrated real trajectory, not from a drag-free approximation. <see cref="TargetXZ"/> is the
/// meeting point it was aimed at, so a caller can see the difference between the aim and the delivery.
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
    /// <summary>Where the ball actually comes down, in pitch XZ (the point the receiver attacks).</summary>
    public Vector2 LandingXZ { get; init; }

    /// <summary>The meeting point the delivery was aimed at, in pitch XZ.</summary>
    public Vector2 TargetXZ { get; init; }

    /// <summary>Which airborne profile produced this estimate (A5.5).</summary>
    public RaisedPassProfile Profile { get; init; }

    /// <summary>No raised route to this receiver: out of the model's band, or landing too far away.</summary>
    public static LoftedPassTerms None => new(false, 0f, 0f, 0f, 0f, 0f, 1f);
}
