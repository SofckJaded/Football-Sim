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

    /// <summary>
    /// AIB-02b: additional defenders beyond the first whose arrival window overlapped the ball's, clamped
    /// to <see cref="PassSuccessModel.MaxCongestionExtraDefenders"/>. Zero when the reception is a
    /// one-on-one. Published so a decision trace can say how much of the completion the crowd cost.
    /// </summary>
    public int CongestionExtraDefenders { get; init; }

    /// <summary>
    /// AIB-02b: the bounded multiplier those extra defenders applied to the completion,
    /// <c>1 − DefenderCongestionStep01 × CongestionExtraDefenders</c> (1 when none). Set by every feasible
    /// estimate; a default-constructed term reports 0, not 1.
    /// </summary>
    public float CongestionFactor01 { get; init; }

    /// <summary>
    /// AUD-P7 / fix 4.3: the bounded share of completion removed because the ground ball would leave the
    /// pitch <b>just past its aim point</b> before anyone could trap it (0 when it stays in play, the goal
    /// mouth, or the exit is far enough past the aim point to be ordinary play). It is a distinct physical
    /// outcome — the ball is out of play, not intercepted — priced inside the completion probability by
    /// <see cref="Physics.PassPlanner.BoundaryExitRisk01"/>; it is <b>not</b> a lead/territory charge
    /// (that is <see cref="LeadCharge01"/>) and the loss term <c>1−p</c> still owns the turnover. Published
    /// so a decision trace can show the charge.
    /// </summary>
    public float BoundaryExitRisk01 { get; init; }
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
    ///
    /// <para>AIB-08 (audit 2026-11-23): this is no longer the radius of the <i>moving-run</i> intercept —
    /// a body that is already moving into the ball plays it at the real receiving/dribbling reach
    /// (<see cref="LaneStepInReachMeters"/> / the capability's reach radius), and the sampled run uses
    /// that. This margin survives as the true-contact distance for the segment-band edge, the
    /// already-there fast path and the airborne corridor, where a smaller, strictly geometric "is he on
    /// the line" question is the right one.</para>
    /// </summary>
    public const float LaneContactReachMeters = 0.9f;

    /// <summary>
    /// How long an opponent needs to notice the pass before starting to close it. A defender does not
    /// begin moving the instant the ball leaves the passer's foot.
    /// </summary>
    public const float OpponentReactionSeconds = 0.25f;

    /// <summary>
    /// AIB-02a (audit 2026-11-22): the step-in test is a <see cref="TimeToReachModel"/> query against the
    /// nearest point of the lane, not a flat turn speed. The removed constant was
    /// <c>LaneTurnSpeed = 1.5 m/s</c>: it modelled a defender who has noticed the pass as able to close
    /// the lane at only 1.5 m/s on top of their own run, so over a 1.0 s flight a *stationary* defender
    /// covered <c>0.90 + 1.5 × 0.75 = 2.02 m</c> when the locomotion model gives him
    /// <c>0.5 × (6.8 × 1.15) × 0.75² ≈ 2.20 m</c> of acceleration before turn cost plus the real ~1.24 m
    /// receive reach. The reach
    /// model already owns acceleration, turn cost, current velocity and contact reach (its own doc: "one
    /// reach model"), so the second estimate is gone rather than re-tuned.
    ///
    /// <para>AIB-08 (audit 2026-11-23): this is the reach that decides whether a <i>moving</i> defender's
    /// sampled run plays the ball, not only whether he can step in. The sampled run used
    /// <see cref="LaneContactReachMeters"/> (0.90 m) while execution lets a receiving/dribbling body play a
    /// ball at <c>ReachRadius × 1.65 ≈ 1.24 m</c>, so a defender crossing the corridor 0.95–1.22 m from the
    /// ball was invisible to the test that is supposed to see him.</para>
    /// </summary>
    public const float LaneStepInReachMeters = 1.22f;

    /// <summary>
    /// AIB-08 (audit 2026-11-23, finding 3): the largest share of a remembered defender's
    /// <c>PositionUncertaintyMeters</c> that widens the <i>moving-run</i> intercept reach. The step-in test
    /// has always charged the uncertainty against the arrival (AUD-06), but the sampled run did not, so a
    /// stale sighting of a defender crossing the corridor was treated as exact while the same defender's
    /// step-in was widened. The doubt is capped here rather than added raw: a badly remembered picture
    /// must not silently widen the lane into rejecting every pass, and 0.25 m is roughly one stride of
    /// positional doubt — a fifth of the 1.24 m contact reach, and well inside the gap between the old
    /// 0.90 m margin and the real reach. Deterministic, allocation-free constant.
    /// </summary>
    public const float LaneReachUncertaintyCapMeters = 0.25f;

    /// <summary>
    /// AIB-08: speed, in m/s, at which a defender counts as <b>already closing into the corridor</b>. Above
    /// it the step-in does not charge the full <see cref="OpponentReactionSeconds"/>: a defender who is
    /// already moving into the lane has done the noticing the reaction models, and charging it again is the
    /// same duplicate penalty the standing brief forbids. 1.0 m/s is the audit's own threshold — below it
    /// the movement into the lane is drift, not a decision, and the full reaction stands.
    /// </summary>
    public const float ClosingIntoReactionSpeedMetersPerSecond = 1.0f;

    /// <summary>
    /// AIB-08: share of <see cref="OpponentReactionSeconds"/> still charged when the defender is already
    /// closing into the corridor (0.40 × 0.25 s = 0.10 s). The remainder is the part of the reaction that is
    /// genuinely still to come — noticing the ball has been struck — while the time to start moving is
    /// already in the reach model's own acceleration ramp. 0.40 is a third of the full delay: enough to keep
    /// charging a defender who happens to be drifting lane-ward, not enough to excuse one already stepping in.
    /// </summary>
    public const float ClosingIntoReactionScale = 0.40f;

    /// <summary>
    /// AIB-08: number of extra lane points the step-in test tries, beyond the perpendicular foot projection,
    /// when the defender is running down the lane. The audit asks for "2–3 lookahead points"; two is the
    /// bound that catches the diagonal runner (the foot is the earliest, hardest point for him) without
    /// turning the hot lane test into a per-opponent search. Each point is a bounded
    /// <see cref="TimeToReachModel"/> query, guarded first by the same sprint/time floor the foot point uses,
    /// and the whole loop is capped at three reach queries per defender.
    /// </summary>
    public const int LaneLookaheadSamples = 2;

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

    /// <summary>
    /// AIB-02b (audit 2026-11-22): share of the completion charged for one <b>additional</b> defender who
    /// can also be at the arrival point around the time the ball is.
    ///
    /// The estimate used to take only the single worst defender (<c>effective &lt; defenderArrival</c>), so
    /// three defenders clustered around the reception scored exactly like one — the audit's "passes
    /// through 2–3 defenders". A defender counts as congestion when his arrival is at most one arrival
    /// scale after the ball (half a second for a ground pass; a full second for the raised alternative,
    /// whose arrival scale is widened by <see cref="AirborneContestScale"/>). The term is deliberately small
    /// and bounded: crowding is a real cost (a second man can take the loose ball, block the receive, or
    /// press the first touch), but it is not a second completion model, and the corridor itself is already
    /// charged by the caller's lane metric.
    /// </summary>
    public const float DefenderCongestionStep01 = 0.12f;

    /// <summary>
    /// AIB-02b: most additional defenders charged as congestion. Two is enough to separate one man from a
    /// crowd (one extra ⇒ ×0.88, two or more ⇒ ×0.76) without letting a crowded lane drive a pass that the
    /// lane and arrival terms already price as ordinary to zero.
    /// </summary>
    public const int MaxCongestionExtraDefenders = 2;

    /// <summary>Seconds of receiver margin at which the receiver term is neutral (0.5).</summary>
    public const float ReceiverMarginScaleSeconds = 0.60f;

    /// <summary>Share of the defender-side confidence that comes from the lane rather than arrival.</summary>
    public const float LaneWeight = 0.30f;

    /// <summary>Hard floor on the reported probability; a feasible pass is never exactly impossible.</summary>
    public const float MinCompletion01 = 0.02f;

    /// <summary>
    /// AUD-06 (audit 2026-11-22): completion ceiling when the lane is physically blocked. The driven ball
    /// carries no raised-ball escape: it is struck flat and fast, so a body the height-aware lane test finds
    /// in its path has the ball at playable height for most of the flight. It therefore takes the cap the
    /// ground estimate has always applied — the driven case intentionally mirrors the ground ball.
    /// </summary>
    public const float BlockedLaneCompletionCap01 = 0.05f;

    /// <summary>
    /// AUD-06: share of a raised delivery's control charge that survives a body the height-aware lane test can
    /// play. A lofted or whipped ball climbs and can clear a body that stands near the launch or under the
    /// early flight — the flagged defender is a contest, not a wall — so it keeps half of its control share
    /// rather than the near-zero a blocked ground or driven ball takes. (The driven case deliberately mirrors
    /// the ground cap; see <see cref="BlockedLaneCompletionCap01"/>.)
    /// </summary>
    public const float BlockedRaisedControlShare01 = 0.5f;

    /// <summary>
    /// AUD-06: completion ceiling for a blocked <b>lofted</b> or <b>whipped</b> delivery — half of that
    /// profile's own control share. Deliberately above <see cref="CandidateGenerator.MinViableCompletion01"/>
    /// so the documented A4 case (a blocked ground lane whose raised alternative is the only route to the same
    /// receiver) is still offered, while the ~0.60 a blocked raised ball used to report is impossible.
    /// </summary>
    public const float BlockedRaisedCompletionCap01 = AirborneControl01 * BlockedRaisedControlShare01;

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
    ///
    /// <b>AUD-P7 / fix 4.3 — boundary-exit risk.</b> After the completion is assembled (and after the lead
    /// charge and the blocked-lane cap, before the final clamp) the estimate asks
    /// <see cref="Physics.PassPlanner.BoundaryExitRisk01"/> whether the ground ball would leave the pitch
    /// just past its aim point — the audit's "fast pass aimed 1.0 m inside a boundary rolls out of play"
    /// defect — and applies <c>completion ×= 1 − risk</c>. This is a <b>distinct physical outcome</b> (the
    /// ball is out of play before it can be trapped, not intercepted), bounded at
    /// <see cref="Physics.PassPlanner.BoundaryExitMaxRisk01"/>, and it is <b>not</b> a second lead or
    /// territory charge: the unpromised lead is charged once via <see cref="LeadCharge01"/>, the promised
    /// lead through the promise, and the loss term <c>1−p</c> still owns the turnover. It applies to the
    /// untimed and the timed/promised plan alike because both are estimated here, and the charge is zero
    /// for a central ball, for a ball that stays in play, and once the exit is more than
    /// <see cref="Physics.PassPlanner.BoundaryExitPenaltyRadiusMeters"/> past the aim point (an ordinary
    /// long forward ball whose roll ends far beyond the receiver).
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
        //
        // AIB-10 (audit 2026-11-23, finding 4): the contest is measured against the moment the ball can
        // actually be played — the later of the ball's own flight and the receiver's real arrival — not
        // against the flight alone. Pre-fix `defenderMargin = effective - flight`, so a promised/timed ball
        // whose receiver arrives after the ball (a driven or floored timed pass) let a defender who arrives
        // between the two score a full defender term: the ball is there, but nobody can play it and the
        // defender simply waits. A defender who arrives after both is still untouched (margin positive).
        float contestSeconds = MathF.Max(flight, receiverArrival);
        float defenderMargin = float.PositiveInfinity;
        float defenderArrival = float.PositiveInfinity;
        float defenderUncertainty = 0f;
        float horizon = Math.Clamp(contestSeconds + DefenderHorizonSlackSeconds, 1f, 3.5f);
        // AIB-02b: the arrival window a defender has to overlap to be crowding the reception, not just
        // losing the race for it: one arrival scale after the ball (AIB-10: and after the receiver's
        // arrival, the same "when can the ball be played" moment the defender term uses).
        float congestionWindow = contestSeconds + DefenderMarginScaleSeconds;
        int contestingDefenders = 0;

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
                defenderMargin = effective - contestSeconds;
                defenderUncertainty = uncertainty;
            }

            if (effective <= congestionWindow) contestingDefenders++;
        }

        if (!float.IsFinite(defenderArrival))
        {
            defenderArrival = horizon + OpponentReactionSeconds;
            defenderMargin = defenderArrival - contestSeconds;
        }

        // 3. The three sides of the estimate, each clamped to a probability. The receiver side was already
        // decided in step 1: either from the reach model, or from the promise that replaced it (A5.3).
        float defenderTerm = Math.Clamp(0.5f + defenderMargin / DefenderMarginScaleSeconds, 0f, 1f);

        // AIB-02b: everybody beyond the worst defender who can also be there is charged a bounded crowd
        // factor. Before this, the loop above kept only the minimum arrival, so a reception with three men
        // around it was priced exactly like the same reception with one.
        int congestionExtra = CongestionExtraDefenders(contestingDefenders);
        float congestion = CongestionFactor(congestionExtra);

        Vector2 receiverXZ = new(receiver.EstimatedPosition.X, receiver.EstimatedPosition.Z);
        Vector2 receiverVelocityXZ = new(receiver.EstimatedVelocity.X, receiver.EstimatedVelocity.Z);
        float approachDot = ApproachAlignment(receiverXZ, target, receiverVelocityXZ);

        float controlPenalty = ReceiverControlPenalty(
            receiverReach.SpeedAtArrival, approachDot, receiver.PositionUncertaintyMeters);
        float control = 1f - controlPenalty;

        float completion = receiverTerm
                           * ((1f - LaneWeight) * defenderTerm + LaneWeight * lane)
                           * control
                           * congestion;

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
        if (blocked) completion = MathF.Min(completion, BlockedLaneCompletionCap01);

        // AUD-P7 / fix 4.3: the ball leaving the pitch just past its aim point is a distinct physical
        // outcome — not an interception, not a lead/territory loss — so it is charged inside the completion
        // probability as a bounded multiplier. The planner does not alter the launch for boundary safety:
        // a ground ball always rolls 26–54 m, so capping the power to keep the rest point inside could only
        // be met by making the pass a 0.20-power roller (the AUD-P5 defect class); pricing the outcome is
        // honest and applies to the untimed and the timed/promised plan alike, because both are estimated
        // through this method. Only exits within BoundaryExitPenaltyRadiusMeters past the aim point are
        // charged, so an ordinary long forward ball whose roll ends far beyond the receiver is untouched.
        // The loss term (1−p) still owns the turnover; this is not a second charge for the same thing.
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        float boundaryExitRisk = _planner.BoundaryExitRisk01(ballXZ, target, plan.Power01);
        if (boundaryExitRisk > 0f) completion *= 1f - boundaryExitRisk;

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
            PromisedConfidence01 = promised ? Math.Clamp(promise.Confidence01, 0f, 1f) : 0f,
            CongestionExtraDefenders = congestionExtra,
            CongestionFactor01 = congestion,
            BoundaryExitRisk01 = boundaryExitRisk
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
    ///
    /// <b>AUD-P7 is deliberately not charged here.</b> The boundary-exit risk exists because a rolled
    /// ground ball keeps travelling for tens of metres after the aim point (26.4–51.6 m to rest on the
    /// calibrated deck), so an overhit near a line runs out of play. An airborne ball does not do that: it
    /// lands at the point the power solve drops it — within <see cref="RaisedPassModel"/>'s own landing band
    /// around the aim, so an overshoot is bounded to a few metres rather than the deck roll, with only a
    /// bounded skip/roll after the landing — and a delivery that overshoots is already priced by that landing
    /// band and by the receiver's arrival term. Charging a ground-roll exit model to a ball that is never on
    /// the ground past its landing would double-count a risk the raised estimate does not have.
    ///
    /// <b>AIB-10 (audit 2026-11-23, finding 4): the defender contest is measured against
    /// <c>max(flight, receiverArrival)</c></b> — the moment the landed ball can actually be played — not
    /// against the ball's flight alone, consistently with <see cref="Estimate"/>. A defender who can reach
    /// the landing after the ball but before the receiver can no longer score a full defender term, and the
    /// congestion window is counted from the same instant so a late contestor is charged once, not excused.
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
        float receiverArrival;
        if (profile == RaisedPassProfile.Driven)
        {
            // Solved from the promised arrival: what is left to price is the runner's own confidence and
            // how well the ball's flight matched the moment he said he would be there.
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
            ReachEstimate receiverReach = TimeToReachModel.ToPoint(
                ReachKinematics.FromEntity(receiver), landing, MathF.Max(flight, 1f));
            receiverArrival = receiverReach.Reachable ? receiverReach.Seconds : flight + 1f;
            receiverTerm = Math.Clamp(0.5f + (flight - receiverArrival) / ReceiverMarginScaleSeconds, 0f, 1f);
        }

        // AIB-10 (audit 2026-11-23, finding 4): the landing contest is measured against the moment the ball
        // can actually be played — the later of the ball's flight and the receiver's arrival — exactly as the
        // ground estimate now measures it. Pre-fix the term used `flight` alone, so a landed ball nobody can
        // reach yet (the receiver is still running to it) read as an uncontested landing, and a defender who
        // arrives between the two scored a full defender term.
        float contestSeconds = MathF.Max(flight, receiverArrival);
        float defenderTerm = 1f;
        float defenderUncertaintySeconds = 0f;
        float horizon = MathF.Max(contestSeconds + DefenderHorizonSlackSeconds, 1f);
        float defenderScale = profile == RaisedPassProfile.Driven
            ? DefenderMarginScaleSeconds
            : DefenderMarginScaleSeconds * AirborneContestScale;
        // AIB-02b: the same crowd window the arrival scale uses — a landing scale for a lofted ball, the
        // ground scale for a driven one (AIB-10: counted from the same "when can it be played" instant).
        float congestionWindow = contestSeconds + defenderScale;
        int contestingDefenders = 0;
        foreach (PerceivedEntity entity in obs.VisiblePlayers)
        {
            if (entity.Team == obs.SelfTeam) continue;
            ReachEstimate reach = TimeToReachModel.ToPoint(
                ReachKinematics.FromEntity(entity), landing, horizon);
            float arrival = (reach.Reachable ? reach.Seconds : horizon) + OpponentReactionSeconds;

            // AUD-06 (audit 2026-11-22): the ground estimate charges a remembered defender's positional
            // uncertainty against the pass; this one did not, so a stale sighting of a defender at the
            // landing read exactly as safe as a fresh one. Same clamp, same direction: the defender may be
            // nearer than remembered, so he is charged as if he can be there earlier.
            float uncertainty = Math.Clamp(
                entity.PositionUncertaintyMeters * UncertaintyArrivalSecondsPerMeter, 0f, MaxUncertaintySeconds);
            float effective = arrival - uncertainty;

            float term = Math.Clamp(0.5f + (effective - contestSeconds) / defenderScale, 0f, 1f);
            if (term < defenderTerm)
            {
                defenderTerm = term;
                defenderUncertaintySeconds = uncertainty;
            }

            if (effective <= congestionWindow) contestingDefenders++;
        }

        // AIB-02b: a raised ball into a landing that several defenders can reach late is a contested ball,
        // not the open one the single-worst defender term implied — the audit's "lofted punt into contested
        // space". Bounded exactly like the ground crowd factor, so one marking defender is unchanged and a
        // genuinely open landing (the lob over a compact block) is not touched.
        //
        // Only the *raised alternative* (the unpromised loft that replaces a blocked ground lane) takes the
        // charge. A cross is a different delivery: A5.5 deliberately prices its aerial contest through the
        // widened arrival scale and the reception side (the runner's aerial capability, where the ball is
        // aimed at the runner rather than at the space), so charging the same late-arriving contestors
        // again here is the duplicate penalty the standing brief forbids. Measured (DeliveryChoiceTests'
        // boxed-in-winger fixture): the 12 % crowd charge took a real box cross from 1.15 to 0.93 and made it
        // lose to a 0.99 carry — the duplicate charge, not the cross, was the defect. A driven ball is timed
        // to an announced meeting and priced through the promise for the same reason.
        int congestionExtra = profile == RaisedPassProfile.Lofted
            ? CongestionExtraDefenders(contestingDefenders)
            : 0;
        float congestion = CongestionFactor(congestionExtra);

        // AUD-06: the clamped caller metric is no longer dead. A5.5 is right that the ground lane clearance is
        // the wrong question for a ball two to four metres up — so the height-aware test above still decides
        // whether the ball is playable at all — but "not blocked" is not the same as "open": a driven ball is
        // struck flat and stays in the ground corridor, so its caller clearance is charged exactly as the
        // ground estimate charges it; a lofted or whipped ball is above the corridor where the height test
        // says so, and the corridor can only trim its lane term by the model's own lane weight.
        float laneClearance = Math.Clamp(float.IsFinite(laneClearance01) ? laneClearance01 : 0f, 0f, 1f);
        bool laneBlocked = IsAirborneLaneBlocked(
            selfXZ, targetXZ, profile, power, obs.VisiblePlayers, obs.SelfTeam, flight, landing);
        float lane = laneBlocked
            ? 0f
            : profile == RaisedPassProfile.Driven
                ? laneClearance
                : 1f - LaneWeight * (1f - laneClearance);

        float control = profile == RaisedPassProfile.Driven ? DrivenControl01 : AirborneControl01;
        float completion = receiverTerm
                           * ((1f - LaneWeight) * defenderTerm + LaneWeight * lane)
                           * control
                           * congestion;

        // AUD-06: a body the height-aware test found where the ball is low enough to be played is a real
        // interception, so a blocked raised ball is near-zero rather than losing only its 30 % lane term. A
        // driven ball is flat for most of its flight and takes the ground cap; a lofted or whipped ball keeps
        // the small chance that it clears the man.
        if (laneBlocked)
        {
            float blockedCap = profile == RaisedPassProfile.Driven
                ? BlockedLaneCompletionCap01
                : BlockedRaisedCompletionCap01;
            completion = MathF.Min(completion, blockedCap);
        }

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
            Profile = profile,
            LaneBlocked = laneBlocked,
            LaneClearance01 = laneClearance,
            DefenderUncertaintySeconds = defenderUncertaintySeconds,
            CongestionExtraDefenders = congestionExtra,
            CongestionFactor01 = congestion
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
    ///
    /// <b>AIB-02c (audit 2026-11-22): the landing contest is exempt, not a blanket skip.</b> The last
    /// <see cref="AirborneLandingContestMeters"/> of the flight used to be skipped unconditionally, on the
    /// correct rule that a defender at the landing is already priced by the arrival term and must not be
    /// charged twice. But the skip also excused a body the arrival term does <i>not</i> price — a defender
    /// who cannot be at the landing when the ball is (too slow into the contest, or charging in late) still
    /// stands on the low part of the line and can play the ball. The exemption is now conditional on the
    /// arrival term actually pricing him, which leaves the legitimate lob over a compact block alone while
    /// no longer excusing a free body under the falling ball.
    ///
    /// <paramref name="flightTimeSeconds"/> and <paramref name="landingXZ"/> are the delivery's real
    /// trajectory and are what makes the conditional exemption checkable; a caller that omits them keeps the
    /// old blanket exemption exactly, so this stays a source-compatible, additive widening.
    /// </summary>
    public bool IsAirborneLaneBlocked(
        Vector2 fromXZ,
        Vector2 toXZ,
        RaisedPassProfile profile,
        float power01,
        IReadOnlyList<PerceivedEntity> visiblePlayers,
        TeamId selfTeam,
        float flightTimeSeconds = 0f,
        Vector2 landingXZ = default)
    {
        Vector2 segment = toXZ - fromXZ;
        float length = segment.Length();
        if (length < 0.5f) return false;

        Vector2 direction = segment / length;
        Vector2 normal = new(-direction.Y, direction.X);
        RaisedPassModel flights = RaisedPassModel.Default;
        bool traceLandingContest = flightTimeSeconds > 0f &&
                                   float.IsFinite(flightTimeSeconds) &&
                                   SimMath.IsFinite(landingXZ);

        for (int i = 0; i < visiblePlayers.Count; i++)
        {
            PerceivedEntity opponent = visiblePlayers[i];
            if (opponent.Team == selfTeam) continue;

            Vector2 relative = new Vector2(
                opponent.EstimatedPosition.X, opponent.EstimatedPosition.Z) - fromXZ;
            float along = Vector2.Dot(relative, direction);
            if (along <= 0.5f || along >= length) continue;
            if (MathF.Abs(Vector2.Dot(relative, normal)) > LaneContactReachMeters + 0.35f) continue;

            // Inside the landing contest, a defender is excused from the block only when the arrival term
            // is already charging him for that contest. A body the contest does not price is a plain block
            // when the ball is low enough at his place on the line.
            if (along >= length - AirborneLandingContestMeters &&
                (!traceLandingContest || LandingContestPricesDefender(opponent, landingXZ, flightTimeSeconds)))
            {
                continue;
            }

            float height = flights.HeightAtDistance(profile, power01, along);
            if (height > AirborneBlockHeightMeters) continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// AIB-02c: true when <see cref="EstimateRaised"/>'s arrival term already charges this defender for the
    /// landing contest — he can be at the landing within one widened arrival scale of the ball, which is the
    /// same window the defender term prices. Those defenders are exempt from the height-aware block so the
    /// contest is not charged twice; everybody else is not.
    /// </summary>
    private static bool LandingContestPricesDefender(
        PerceivedEntity defender,
        Vector2 landingXZ,
        float flightSeconds)
    {
        float contestWindow = DefenderMarginScaleSeconds * AirborneContestScale;
        float horizon = MathF.Max(flightSeconds + contestWindow, 1f);
        ReachEstimate reach = TimeToReachModel.ToPoint(
            ReachKinematics.FromEntity(defender), landingXZ, horizon);
        float arrival = (reach.Reachable ? reach.Seconds : horizon) + OpponentReactionSeconds;
        float uncertainty = Math.Clamp(
            defender.PositionUncertaintyMeters * UncertaintyArrivalSecondsPerMeter, 0f, MaxUncertaintySeconds);
        return arrival - uncertainty <= flightSeconds + contestWindow;
    }

    /// <summary>
    /// True when a defender the ball would physically run into blocks the planned line. This is the
    /// "hard rejection for a clearly blocked ground pass" the plan requires: it is a physical test
    /// (sampled flight against each opponent's predicted run, plus a reaction-and-step estimate), not a
    /// probability threshold, so an open lane is never rejected for being merely average.
    ///
    /// The line tested is the <b>segment</b> from the ball to the receiver, not the infinite line through
    /// them (A6.B8): an opponent behind the passer, or past the receiver, is not somewhere the ball
    /// travels and cannot block it.
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

            // The lane is the segment between the ball and the receiver, not the infinite line through
            // them. A defender *behind* the ball (the man chasing the carrier) or one standing *past* the
            // receiver is not on the ball's path at all: the ball never travels through him, so neither
            // test below may call it a block. Before this guard the step-in test used the perpendicular
            // distance alone, so a chaser 1.5 m behind the passer and 0.4 m off the line hard-rejected
            // every forward ball, and a defender eight metres past the receiver did the same (A6.B8).
            if (projectionRaw < -LaneContactReachMeters ||
                projectionRaw > length + LaneContactReachMeters)
            {
                continue;
            }

            // AIB-02a (audit 2026-11-22): the one reach model, not a second distance/speed estimate. An
            // opponent who can be within the real receive/tackle reach (~1.24 m) of the ball's line before
            // the ball goes past that point can intercept it: acceleration ramp, turn cost, current closing
            // velocity and contact reach all come from TimeToReachModel, exactly as they do for the arrival
            // term above (and for every other arrival in this class). The old code replaced all of that
            // with `gap <= (closingSpeed + 1.5 m/s) × available`, so a stationary defender covered only
            // 0.90 + 1.5 × 0.75 = 2.02 m in a 1.0 s pass where the locomotion model gives him
            // 0.5 × 6.8 × 0.75² = 1.91 m of acceleration plus the same reach.
            ReachKinematics opponentKinematics = ReachKinematics.FromEntity(opponent);

            // AIB-08 (audit 2026-11-23, finding 3): both lane tests use this reach. It is the reach a body
            // actually plays the ball with — the receiving/dribbling contact reach, floored at the
            // step-in's stated 1.22 m — plus the bounded positional doubt of the sighting. Pre-fix the
            // moving-run sample used the bare 0.90 m true-contact margin, so a defender passing 0.95–1.22 m
            // from the ball's line escaped the test that exists to catch him.
            float interceptReach = MathF.Max(LaneStepInReachMeters, opponentKinematics.ReachRadius);
            float sightDoubt = Math.Clamp(
                opponent.PositionUncertaintyMeters, 0f, LaneReachUncertaintyCapMeters);

            // Cheap reachability bound before the dense sampling and before the reach queries: nobody covers
            // more ground than a full sprint (plus whatever closing speed he already carries) plus his own
            // contact reach, so a defender beyond that cannot be on the ball in time. The bound is
            // deliberately generous — it never skips a defender either test below could have found.
            float reachable = interceptReach + sightDoubt + OpponentSprintSpeed * flight;
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
                if (Vector2.Distance(opponentXZ + runVelocity * t, ballPath[sample]) <= interceptReach + sightDoubt)
                    return true;
            }

            // 2. They can also see the pass and step into the lane. Compare what the step actually needs with
            //    how long the ball takes to the nearest point of the *segment*, so a defender who could only
            //    reach the line behind the passer or beyond the receiver is not counted.
            //
            //    AIB-08: the foot point is the earliest (hardest) point of the lane, which is the wrong
            //    question for a defender already running along the corridor — his shortest intercept is
            //    metres further down the lane, where the ball arrives later. Up to
            //    LaneLookaheadSamples lookahead points along the segment, in the direction of his own run,
            //    are tested too; each is guarded by the same cheap sprint/time floor before its reach query,
            //    so the extra cost is bounded at three queries per relevant defender.
            float alongRun = Vector2.Dot(runVelocity, direction);
            float closingInto = -MathF.Sign(signedPerpendicular) *
                                Vector2.Dot(runVelocity, normal);
            float reaction = closingInto >= ClosingIntoReactionSpeedMetersPerSecond
                ? OpponentReactionSeconds * ClosingIntoReactionScale
                : OpponentReactionSeconds;

            for (int look = 0; look <= LaneLookaheadSamples; look++)
            {
                float projectionCandidate;
                if (look == 0)
                {
                    projectionCandidate = projectionRaw;
                }
                else
                {
                    // Lookahead only makes sense when the run carries him down the lane; a defender running
                    // back toward the passer is already covered by the foot point.
                    if (alongRun <= 0f) break;
                    float lookaheadSeconds = flight * look / (LaneLookaheadSamples + 1f);
                    projectionCandidate = projectionRaw + alongRun * lookaheadSeconds;
                }

                float projection = Math.Clamp(projectionCandidate, 0f, length);
                Vector2 pointOnLane = fromXZ + direction * projection;
                float distanceToLanePoint = Vector2.Distance(opponentXZ, pointOnLane);
                if (look == 0 && distanceToLanePoint <= LaneContactReachMeters) return true;

                float timeToPoint = BallTimeToDistance(power01, projection, flightTimeSeconds, length);
                float available = timeToPoint - reaction;
                if (available <= 0f) continue;

                // Another cheap floor: even at a dead sprint from his current momentum nobody covers the
                // gap faster than (current speed + sprint cap) × available, so skip the reach query when he
                // cannot.
                float mustRun = distanceToLanePoint - interceptReach;
                if (mustRun > (speed + OpponentSprintSpeed) * available) continue;

                ReachEstimate stepIn = TimeToReachModel.ToPoint(opponentKinematics, pointOnLane, available);
                if (stepIn.Reachable && stepIn.Seconds <= available) return true;
            }
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
    /// AIB-02b: additional defenders charged as crowding the arrival, i.e. everybody beyond the first who
    /// can also be at the arrival point around the time the ball is. Bounded so that a crowded reception is
    /// distinguished from a one-on-one without a second probability model deciding the pass.
    /// </summary>
    private static int CongestionExtraDefenders(int contestingDefenders) =>
        Math.Clamp(contestingDefenders - 1, 0, MaxCongestionExtraDefenders);

    /// <summary>AIB-02b: the bounded completion multiplier for that crowding.</summary>
    private static float CongestionFactor(int extraDefenders) =>
        1f - DefenderCongestionStep01 * extraDefenders;

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

    /// <summary>
    /// True when a defender stands where the airborne ball is still low enough to be played (A5.5). The
    /// estimate already charges this in <see cref="CompletionProbability01"/>; it is published separately so
    /// a caller ordering deliveries — the restart taker's "open route first" tier — asks the same question
    /// for a raised ball that it asks for a ground one (L8).
    /// </summary>
    public bool LaneBlocked { get; init; }

    /// <summary>
    /// The caller's ground lane clearance, clamped to 0..1, as used by the estimate (AUD-06). Published so a
    /// trace can show that the metric the caller paid to compute reached the completion instead of being
    /// overwritten by the block flag.
    /// </summary>
    public float LaneClearance01 { get; init; }

    /// <summary>
    /// Worst-case arrival time the estimate deducted from the closest defender for a stale sighting, in
    /// seconds (AUD-06). Zero when every visible opponent was freshly seen.
    /// </summary>
    public float DefenderUncertaintySeconds { get; init; }

    /// <summary>
    /// AIB-02b: additional defenders beyond the first whose arrival window overlapped the landing, clamped to
    /// <see cref="PassSuccessModel.MaxCongestionExtraDefenders"/>. Zero when the landing is a one-on-one.
    /// </summary>
    public int CongestionExtraDefenders { get; init; }

    /// <summary>
    /// AIB-02b: the bounded multiplier those extra defenders applied to the completion, 1 when none. Set by
    /// every feasible estimate; a default-constructed term reports 0, not 1.
    /// </summary>
    public float CongestionFactor01 { get; init; }

    /// <summary>No raised route to this receiver: out of the model's band, or landing too far away.</summary>
    public static LoftedPassTerms None => new(false, 0f, 0f, 0f, 0f, 0f, 1f);
}
