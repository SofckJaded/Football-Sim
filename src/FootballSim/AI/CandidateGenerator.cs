using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Tactics;

namespace FootballSim.AI;

/// <summary>
/// Builds the utility candidate set for one decision: passes, carries, shots, presses, interceptions,
/// shape and job movements, each carrying a stable <see cref="CandidateKey"/>.
///
/// Split out of <c>TacticalUtilityAiController</c> in A1.1d.2 so candidate generation and the
/// selection/commitment policy can change independently. Candidate bodies are moved verbatim; the only
/// additions are the state this object now owns and the accessors the controller reads.
///
/// It owns the candidate buffer and the tackle cooldown because those are the only mutable state the
/// generation code touches. Everything else it needs (pitch, pass planner, communication) is supplied
/// once at construction. Selection, commitment and debug reporting stay with the controller.
/// </summary>
public sealed class CandidateGenerator : IAiStateResettable
{
    internal const float MaxReceivableSpeed = 8.0f;   // bounded receiver sprint assumption

    /// <summary>
    /// Extra lead, in metres, that a pass into a run is played beyond where the runner is now (A4.1).
    /// The planner already leads a receiver by the modelled flight time; this pushes the target into the
    /// space the run is heading for, which is what makes the option a pass *to a region* rather than to
    /// feet.
    /// </summary>
    internal const float ThroughLeadMeters = 5.0f;

    /// <summary>How far ahead a carry direction is probed against the pitch lines, in metres.</summary>
    internal const float CarryProbeMeters = 5.0f;

    /// <summary>Safety margin kept between the carry target and a touch/goal line, in metres.</summary>
    internal const float CarryLineMargin = 1.0f;

    /// <summary>
    /// Minimum time between this player's tackle attempts. AI players used to request a tackle on
    /// every tick the ball was in reach, which produced the reported tackle spam between two close
    /// players (the ball went back and forth without either player moving).
    /// </summary>
    internal const float TackleCooldownSeconds = 0.55f;

    /// <summary>
    /// How much better a new carry direction must be before the carrier abandons the one it is already
    /// running. Small by design: it exists to stop the direction flipping left/right every tick when a
    /// defender is closing, not to make the carrier stubborn.
    /// </summary>
    internal const float CarryDirectionHysteresis = 0.04f;

    /// <summary>
    /// AIB-04a: how much a carry direction is worth for heading at the opponent goal, on the same scale as
    /// the freedom and boundary terms. The old score was <c>0.55 x space + 0.45 x boundarySafety</c> with no
    /// goalward term at all, and <c>boundarySafety</c> saturates anywhere three metres inside the touchline —
    /// so the emptiest direction (outward, toward the empty flank) won almost every tick and the side
    /// attacked down the wing by construction.
    /// </summary>
    internal const float CarryGoalwardWeight = 0.26f;

    /// <summary>
    /// AIB-04a: attack-relative projection, in metres, from which the goalward carry term starts to ramp in.
    /// A build-up carry needs room more than it needs the goal line, and a flat goal term in the own third
    /// walked carriers into the press that the build-up gate pins against. Ten metres past the halfway line
    /// was the original start.
    ///
    /// AUD-P6 / Fix 5.3 (2026-11-23): the start moved from <c>+10 m</c> to <c>−10 m</c>. The audit's capture
    /// found a carrier near the centre circle drifting to the empty flank because the old start made the
    /// weight exactly <c>0.0</c> for every projection below <c>+10 m</c>, leaving
    /// <c>0.55 x space + 0.45 x boundarySafety</c> as pure empty-space seeking. The ramp is now
    /// <c>0.26 x clamp((proj − (−10)) / (20 − (−10)), 0, 1)</c> = <c>0.26 x clamp((proj + 10) / 30, 0, 1)</c>:
    /// exactly <c>0.0</c> at and below −10 m, so the own-third build-up behaviour this gate pins is
    /// untouched; ≈0.087 on the halfway line; ≈0.156 at +8 m; and full weight from +20 m, exactly as before.
    /// </summary>
    internal const float CarryGoalwardRampFromProjX = -10f;

    /// <summary>AIB-04a: attack-relative projection, in metres, at which the goalward carry term is at full weight.</summary>
    internal const float CarryGoalwardRampToProjX = 20f;

    /// <summary>
    /// AIB-04a: how much a wide final-third carrier is worth for cutting inside. Applied only in the final
    /// third (past <see cref="CarryFinalThirdProjX"/>) and ramping with how wide the carrier already is, so
    /// build-up carries and central carriers keep the pure space/boundary score.
    /// </summary>
    internal const float CarryFinalThirdInwardWeight = 0.30f;

    /// <summary>
    /// AIB-04a: attack-relative projection, in metres, from which a carry counts as a final-third carry and
    /// the inward cut-inside term applies. The same 26 m gate the carrier decision uses for
    /// <c>inFinalThird</c>; kept as one constant so the two cannot drift apart.
    /// </summary>
    internal const float CarryFinalThirdProjX = 26f;

    /// <summary>
    /// AIB-04a: lateral distance from the centre line, in metres, beyond which a final-third carrier is
    /// "wide" and an inward cut is rewarded. Inside it a central carrier pays no cut-inside term.
    /// </summary>
    internal const float CarryInwardMinLateralMeters = 8f;

    /// <summary>
    /// A9.6d: time after a shot during which the same player may not shoot again. Every AI shot is parried by
    /// the keeper (the catch speed is below the minimum shot speed), the parry comes back at the shooter, and
    /// without this the first touch after a save was another shot from a worse position — the rendered-review
    /// "bounced back to him and he shot out of bound". A reaction timer, not a cadence: it ticks every live
    /// tick like the tackle cooldown.
    /// </summary>
    internal const float ShotRecoverySeconds = 0.55f;

    // ---------------------------------------------------------------------------------------------
    // AIB audit 2026-11-22 — carrier pressure, the finish and the recycle inversion.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// AIB-01: time-to-contact, in seconds, under which the carrier releases the ball without waiting for
    /// his normal touch. The old gate was the static ring <see cref="EmergencyPassStaticMeters"/> (2.6 m),
    /// and a sprinting presser at 8.4 m/s covers the 2.7–5.0 m band in the 0.45 s it takes to reach tackle
    /// reach — so while he closed, the carrier generated zero pass candidates and carried into the tackle.
    /// This reads the local pressure field's arrival estimate at the carrier's own feet instead.
    /// </summary>
    internal const float EmergencyPassArrivalSeconds = 0.55f;

    /// <summary>
    /// AIB-01: the static nearest-opponent distance below which the carrier counts as pressured regardless
    /// of the arrival estimate. Kept because a body standing within this ring blocks the touch even if the
    /// reach model cannot place him "arriving" anywhere.
    /// </summary>
    internal const float EmergencyPassStaticMeters = 2.6f;

    /// <summary>
    /// AIB-05: how far inside each post the two corner finishes aim, in metres. The goal half-width is
    /// 3.66 m, so a 0.75 m inset puts the corner targets at ±2.91 m — inside the frame and away from the
    /// post, with the centre of the goal as the third option.
    /// </summary>
    internal const float ShotCornerInsetMeters = 0.75f;

    /// <summary>
    /// AIB-06a: forward projection, in metres past the carrier, at which a generated pass counts as the
    /// forward option a backward recycle is choosing against. A recycle is only charged the
    /// <see cref="ActionValueScale.BackwardTerritoryCost"/> when such an option exists in this decision's
    /// candidate set; a safe outlet with nothing ahead pays nothing.
    /// </summary>
    internal const float RecycleForwardOptionMinProjMeters = 6f;

    /// <summary>
    /// AIB-01: how fast an opponent must be running at the carrier, in metres per second, before the
    /// early-carry bonus is withdrawn. The audit wording is "drop CarryEarlyBonus whenever an opponent is
    /// <i>closing</i>"; a stationary defender whose body happens to sit inside the contact-arrival window is
    /// charged through the arrival-based contact risk but is not closing in, and withdrawing the tempo bonus
    /// for him released settled possession on the first touch (the A8.3 switch-attribution regression:
    /// the carry fell 1.01 → 0.74 against a held 2.2 m presser).
    /// </summary>
    internal const float ClosingSpeedThresholdMetersPerSecond = 0.75f;

    // ---------------------------------------------------------------------------------------------
    // AIB-12b (audit 2026-11-23, finding 5) — the carrier's sprint gate.
    //
    // `bool sprint = bestSpace > 0.70f && nearestOppDist > 5.0f` dropped the carrier to a jog the instant
    // any opponent came within 5 m, while the presser closing him sprints unconditionally: the audit
    // measured an 8.4 m/s presser against a 6.3 m/s carrier and the ball being caught. The gate below
    // instead asks whether the *path ahead* is open: a chaser behind or beside the carrier does not stop
    // the sprint, a body standing in the corridor the carrier is about to run through does, and a body
    // already inside tackle reach plus a step is a contact, not a sprint. This changes locomotion only
    // (sprint is 1.0 × MaxSprintSpeed and a longer dribble lead; see FootballerLocomotion) — the shield,
    // tackle and contact rules are untouched.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Free-space reading the carry's probe needs before the carrier sprints into it (AIB-12b). The audit's
    /// own threshold; below it the direction is a jostle, not open grass.
    /// </summary>
    public const float SprintGateSpaceThreshold01 = 0.55f;

    /// <summary>
    /// Nearest-opponent distance, in metres, below which the carrier does not sprint (AIB-12b): tackle
    /// reach plus a step, so a body this close is a contact the sprint has to stop for anyway.
    /// </summary>
    public const float SprintGateContactFloorMeters = 2.40f;

    /// <summary>How far ahead of the carrier the sprint path is checked for a blocking body, in metres (AIB-12b).</summary>
    public const float SprintPathHorizonMeters = 6.0f;

    /// <summary>
    /// Half-width, in metres, of the corridor a sprinting carrier would run through (AIB-12b). Narrower than
    /// a passing lane on purpose: a body 2.2 m off the chosen direction is not in the way of a dribble.
    /// </summary>
    public const float SprintPathHalfWidthMeters = 2.2f;

    /// <summary>
    /// Speed, in m/s, at which an opponent is already running out of the sprint path, so he does not block
    /// it (AIB-12b): his believed velocity has at least this component opposite the carry direction.
    /// </summary>
    public const float SprintPathEscapeSpeedMetersPerSecond = 1.0f;

    // ---------------------------------------------------------------------------------------------
    // AIB audit 2026-11-22 — pass cleanliness: multi-defender corridor congestion, no reward for a
    // crowded lane, bounded leads, the delivery floors and the driven ball's origin.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// AIB-02a: share of a lane's own intercept radius inside which a defender counts as <b>crowding</b>
    /// the corridor rather than merely standing near it. The single-worst-defender metric treated three
    /// clustered defenders exactly like one (the report's "passes through 2–3 defenders"); every defender
    /// inside this share now also adds the bounded congestion charge below.
    /// </summary>
    internal const float LaneCongestionRatio01 = 0.85f;

    /// <summary>
    /// AIB-02a: clearance charged for each crowding defender. The charge is weighted by how deep inside
    /// the corridor he stands, so a man on the edge of the lane costs almost nothing and a man halfway into
    /// it costs most of the step. Bounded by <see cref="LaneCongestionMax01"/> so a crowd can never drive a
    /// lane that is otherwise playable to the hard-blocked value.
    /// </summary>
    internal const float LaneCongestionStep01 = 0.18f;

    /// <summary>AIB-02a: most the corridor congestion may subtract, so the metric stays monotone and bounded.</summary>
    internal const float LaneCongestionMax01 = 0.60f;

    /// <summary>
    /// AIB-08 (audit 2026-11-23, finding 3): look-ahead used by <see cref="EvaluateLaneClearance"/>, in
    /// seconds. The corridor metric was 100 % static — a defender closing into the pass lane before the ball
    /// was scored exactly like the same defender standing still — which is the audit's "looked clear at kick
    /// time". Each opponent is therefore scored at the worse of his current position and the position his own
    /// believed velocity reaches in this horizon. 0.35 s is the audit's figure: about a third of a typical
    /// crisp flight, so it is the window in which a defender can take the one or two strides that change the
    /// lane without inventing a second, long-range intercept model (the pass model's own
    /// <c>IsLaneBlocked</c> owns the hard rejection).
    /// </summary>
    public const float LaneProjectionHorizonSeconds = 0.35f;

    /// <summary>
    /// AIB-08: speed, in m/s, below which an opponent's projected position is not evaluated separately in
    /// <see cref="EvaluateLaneClearance"/>: at this pace 0.35 s moves him under 18 cm, which cannot change the
    /// corridor ratio and only duplicates the current-position sample. The threshold keeps a stationary
    /// defender's clearance value bit-for-bit what it was before the projection.
    /// </summary>
    public const float LaneProjectionMinSpeedMetersPerSecond = 0.5f;


    /// <summary>
    /// AIB-02b: preference penalty a fully crowded lane (<c>laneClearance01 = 0</c>) costs a player of
    /// neutral risk tolerance. Risk tolerance scales the share of this cost he feels; it can never turn the
    /// cost into a reward (that sign inversion is what actively encouraged passes through traffic).
    /// </summary>
    public const float LaneRiskPreferencePenalty01 = 0.18f;

    /// <summary>
    /// AIB-02b: span of the risk-appetite term over which the lane-risk penalty share moves from the neutral
    /// 1.0 to its floor/ceiling. The appetite range itself is the audit's old one — (tendency − baseline) x
    /// 0.40 + (profile − 0.50) x 0.30 — now read as a discount factor instead of a bonus multiplier.
    /// </summary>
    internal const float LaneRiskPenaltySpan = 0.33f;

    /// <summary>AIB-02b: least share of the lane-risk penalty a maximally risk-tolerant player still pays.</summary>
    internal const float LaneRiskPenaltyShareMin = 0.35f;

    /// <summary>AIB-02b: most share of the lane-risk penalty a maximally risk-averse player pays.</summary>
    internal const float LaneRiskPenaltyShareMax = 1.35f;

    /// <summary>
    /// AIB-02c: extra lead, in metres, a <c>ToRun</c> ball may push into space <b>without</b> an advertised
    /// run. The uncommitted variant used to add <see cref="ThroughLeadMeters"/> (5 m) on top of the
    /// velocity x flight lead, and the whole lead was bounded only by <see cref="RunPassMaxLeadMeters"/>
    /// (25 m) — the report's 25 m punt at a man who had promised nothing. A committed run is a promise and
    /// keeps its long leash; an uncommitted one gets this hedge plus the planner's own uncommitted cap.
    /// Public because it is a stated bound on the carrier's action set, quoted by the regression suite.
    /// </summary>
    public const float UncommittedThroughLeadMeters = 1.5f;

    /// <summary>
    /// AIB-02c: a receiver must be moving at least this fast, in m/s, to be offered a <c>ToRun</c> ball with
    /// no advertised run. <see cref="MinRunIntoSpaceSpeed"/> (1.5 m/s) is the floor at which a jog is still a
    /// run; a ball into space needs a man actually sprinting into it, otherwise the lead is a guess with a
    /// long flight to be wrong over. Public because it is a stated gate on the carrier's action set.
    /// </summary>
    public const float MinUncommittedRunSpeed = 3.0f;

    /// <summary>
    /// AIB-11 (audit 2026-11-23, finding 6) / 2026-11-24 owner-review correction (review item B): the arrival
    /// pace floor, in m/s, of a <b>promised</b> <c>ToRun</c> ball — the advertised-run and one-two paths, which
    /// are timed by <see cref="PassPlanner.PlanGroundPassToRegion"/>.
    ///
    /// The first AIB-11 cut forced the 9.0 m/s crisp floor onto every path; measured on the review's own
    /// geometry, a normal advertised run 16–20 m away with a meet tick 2.0–2.3 s away got a 1.15–1.30 s
    /// flight, so the pass model's promise mistiming was <c>|1.2 − 2.2| − 0.385 ≈ 0.63 s</c>, its timing term
    /// clamped to 0 and the completion to ≈0.02 — the candidate was then rejected as `low-completion`, i.e. the
    /// fix starved exactly the run ball it was meant to serve. The floor is now a stated <b>pace</b> instead of
    /// a crisp <i>range</i>: firm enough not to be a 2 s roller (this is about twice the arrival pace a roller
    /// that late would still have), loose enough not to arrive a second before the meet. 6.0 m/s is also below
    /// <see cref="PassSuccessModel.PromisedRegionCrossSpeed"/> (6.5 m/s), the pace at which the promise's own
    /// timing tolerance is defined, so the ball's remaining roll at the meet stays a region-sized quantity; the
    /// shared roll-out criterion (<see cref="PromiseCatchRadiusMeters"/> via
    /// <see cref="RolledPastPromiseMeters"/>) is what refuses a ball that still over-runs its region.
    ///
    /// <para><b>2026-11-24 refinement — the pace floor is a fallback, not a replacement.</b> A lower floor makes
    /// a <i>short</i> meeting's ball gentler (measured: 11.2 m, 1.02 s meet plans 0.55 power / 1.00 s where the
    /// crisp floor planned 0.60 / 0.93 s), which reintroduces a slower, more blockable ball and flipped the
    /// `box-cross` A3.4 gate. <see cref="ChoosePromisedRunPower"/> therefore keeps the crisp floor while it is
    /// on time and uses this pace floor only when the crisp ball would arrive more than the promise's own
    /// timing tolerance early — the review's starved case. The uncommitted variant is deliberately <b>not</b>
    /// given this floor: it has no promise, its plan is the untimed
    /// <see cref="PassPlanner.PlanGroundPass"/> solve, and a pace floor measured at a distance it never
    /// aims at only over-firms it (its timed-region equivalent, the far-meeting floor, was a measured defect).</para>
    /// </summary>
    public const float RunPassMinArrivalSpeedMetersPerSecond = 6.0f;

    /// <summary>
    /// AIB-02c: bounded lead, in metres, that an unled raised ball gives a moving receiver. When a ground
    /// route is blocked and nobody has announced a run, the raised alternative used to aim at the receiver's
    /// <i>current</i> position for a 1.5–2.5 s hang; this is the sensible middle: his own velocity for the
    /// modelled hang, bounded so the ball still lands near a man who may stop or turn. Public because it is a
    /// stated bound on the carrier's action set.
    /// </summary>
    public const float LoftedUncommittedLeadMaxMeters = 5f;

    /// <summary>
    /// AIB-02e: the floor the <b>deliveries</b> keep — a cross, a driven ball, a raised alternative, a
    /// cutback and a ball to an announced/promised arrival. They are low-percentage by design and carry
    /// their value in the box or behind the line reception, so raising the ordinary floor must not silently
    /// delete them. Under heavy pressure the ordinary ground set falls back to this floor too: a pressured
    /// release is a clearance, not a sterile pass.
    /// </summary>
    public const float MinViableDeliveryCompletion01 = 0.30f;

    /// <summary>
    /// AIB-04a: preference, on the action-value scale, a line-breaking ball into the half-space can earn at
    /// most — the symmetric counterpart of <see cref="SwitchRecognition.MaxPreference"/>'s wide-switch
    /// nudge. Bounded well under one scale unit: it is a choice among passes that already exist.
    /// </summary>
    public const float LineBreakPreferenceMax = 0.22f;

    /// <summary>
    /// AIB-04a: central/half-space channel, in metres from the centre line, inside which a penetrative pass
    /// competes for the line-breaking preference. Beyond it the ball is the wide switch's job (A8.3), which
    /// already has its own bounded preference; both are worth the same at most, so neither geometry is
    /// structurally favoured.
    /// </summary>
    public const float LineBreakHalfSpaceMeters = 16f;

    /// <summary>
    /// AIB-04a: attack-relative projection, in metres, the aim point must reach for a ball to be a
    /// line-breaking pass rather than a square ball (the attacking half, where lines exist to break).
    ///
    /// AUD-P6 / Fix 5.3 (2026-11-23): lowered from <c>12 m</c> to <c>4 m</c>. The 12 m floor rewarded central
    /// progression only once the ball was already deep, so a line-breaking ball played in midfield or in
    /// transition — the moment the audited attack actually needed to go forward — earned nothing while the
    /// wide switch's own preference (A8.3) was available at every projection. The bounded
    /// <see cref="LineBreakPreferenceMax"/> cap, the <see cref="LineBreakMinLaneClearance01"/> lane gate, the
    /// half-space width limit and the separate <c>targetProj &gt; ballProj + 2 m</c> forward-ball gate are all
    /// unchanged, so this only lets the same rule see earlier — not a bigger bonus.
    /// </summary>
    public const float LineBreakMinTargetProjMeters = 4f;

    /// <summary>
    /// AIB-04a: lateral half-width, in metres, of the channel around the aim point an opponent has to stand
    /// in to count as a line the ball would break. Roughly a passing lane: a defender further sideways than
    /// this is not in front of the ball.
    /// </summary>
    public const float LineBreakChannelMeters = 9f;

    /// <summary>
    /// AIB-04a: penetration, in metres past the deepest beaten opponent, at which the line-breaking
    /// preference is fully earned. Under it the preference scales with how far past the line the ball goes.
    /// </summary>
    public const float LineBreakFullPenetrationMeters = 10f;

    /// <summary>
    /// AIB-04a: lane clearance a line-breaking ball needs before it earns the preference. The nudge is for a
    /// penetrative pass that is actually on, not for a ball through a crowd (which is priced as the audit
    /// requires: a crowded lane is a cost, never a reward).
    /// </summary>
    public const float LineBreakMinLaneClearance01 = 0.60f;

    // ---------------------------------------------------------------------------------------------
    // A4.1 — the explicit on-ball action set.
    //
    // Every action the carrier can choose is generated by one named builder below, carries a stable
    // CandidateKey, and is bounded twice: a cheap pre-rank decides which receivers get a trajectory
    // planned (AiRuntimeConfig.MaxPassTargets) and the finished set is trimmed to
    // AiRuntimeConfig.MaxCarrierCandidates. The point is that "what can the carrier do?" has an answer
    // that can be listed and tested, instead of being the union of whatever each branch happened to
    // append.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Nearest opponent at which a carrier has somebody to protect the ball from.</summary>
    internal const float ShieldRadiusMeters = 4.0f;

    /// <summary>
    /// How long a carrier may shield instead of releasing the ball. Without a bound, shielding always
    /// beats a risky pass and the side simply stands still with the ball. Public because it is a stated
    /// bound on the carrier's action set rather than an internal tuning number: the inspector and the
    /// regression suite both quote it.
    /// </summary>
    public const float ShieldMaxTimeOnBall = 1.6f;

    /// <summary>Nearest opponent inside which clearing the ball is worth considering.</summary>
    internal const float ClearancePressureMeters = 5.0f;

    /// <summary>Own-half projection (metres, attack-relative) from which a clearance is considered.</summary>
    internal const float ClearanceOwnThirdProjX = -25.0f;

    /// <summary>Earliest time on the ball before a clearance is allowed, in seconds.</summary>
    internal const float ClearanceMinTimeOnBall = 0.35f;

    /// <summary>How close the nearest opponent must be for taking him on to be an option.</summary>
    internal const float DribbleRadiusMeters = 8.0f;

    /// <summary>Distance past the beaten opponent the dribble aims for, in metres.</summary>
    internal const float DribbleBeyondMeters = 3.5f;

    /// <summary>COSINE above which a dribble direction counts as the ordinary carry, not a take-on.</summary>
    internal const float DribbleDistinctDirectionDot = 0.94f;

    /// <summary>Furthest behind the carrier a recycle target may stand, in metres.</summary>
    internal const float RecycleMaxBehindMeters = 45.0f;

    /// <summary>Projection (attack-relative) before which the keeper is offered as a support option.</summary>
    internal const float KeeperSupportOwnThirdProjX = -18.0f;

    /// <summary>
    /// How far off his own goal line a keeper must be before a ball back to him is offered, in metres. A
    /// keeper standing on his line is not a pass target: the ball reaches the goal behind him if the
    /// contact is missed, and measured own goals made that option net-negative (A4.4).
    /// </summary>
    internal const float KeeperSupportStandoffMeters = 12f;

    /// <summary>Longest pass offered by the ordinary pass set, in metres.</summary>
    internal const float MaxPassDistanceMeters = 32.0f;

    /// <summary>A pass closer than this is a give-and-go, not a pass to a teammate, in metres.</summary>
    internal const float MinPassDistanceMeters = 4.5f;

    /// <summary>
    /// A pass target must stay at least this far from the passer's own goal line, in metres. A ground ball
    /// aimed inside that margin is a ball at your own goal: if the first touch misses it, nothing stops it
    /// and the pass model has no way to know. Measured during A4.4 — the reference match produced own goals
    /// from back-passes at this range — so the option is not offered, like a target outside the pitch.
    /// </summary>
    internal const float OwnGoalPassMarginMeters = 8f;

    /// <summary>
    /// AIB-02e: lowest completion probability still worth generating an <b>ordinary</b> ground pass into a
    /// crowded corridor for (raised from 0.30). The report's low-percentage balls through traffic were offered
    /// at 0.30 and then competed on equal terms. It is the pass-side counterpart of AIB-02a's corridor
    /// congestion: a corridor with nobody in it is not "a pass through 2–3 defenders", and a clean-lane pass
    /// whose completion is discounted at the arrival end keeps the delivery floor (see
    /// <see cref="MinViableDeliveryCompletion01"/>) because its contest is already priced by the reception
    /// value — without that distinction the raise silently deleted A8.3's ordinary crossfield ball. The
    /// deliveries keep their own floor because raising this number must not delete the cross, cutback, raised
    /// alternative or committed through ball. Public because it is a stated bound on the carrier's action set,
    /// like <see cref="FeetPassMaxLeadMeters"/>.
    /// </summary>
    public const float MinViableCompletion01 = 0.45f;

    /// <summary>
    /// Longest lead a normal pass may give a moving receiver, in metres (A4.2). The planner solves the
    /// earliest meeting for the power it chose, and for a fast runner heading away from the ball the only
    /// meeting left is at maximum power — measured as a 43 m punt to a team-mate twelve metres away. A
    /// pass is played to where the runner will be when the ball arrives, not to where a five-second
    /// ball would put him, so the lead is bounded. A through ball gets a longer leash
    /// (<see cref="RunPassMaxLeadMeters"/>) because it is explicitly played into space.
    /// </summary>
    /// <summary>
    /// Longest lead a normal pass may give a moving receiver, in metres.
    ///
    /// A pass is played to where the runner will be when the ball arrives, and the planner refuses a
    /// meeting beyond this bound instead of freezing the runner at it (A4.11). The value is deliberately
    /// small: a "pass to feet" that led a runner twelve metres completed 79 % of the time and read, in a
    /// rendered session, as a ball that had gone to nobody. At four metres the reference match completes
    /// **88 %/89 %** on both sides, because the ball now arrives where the man is rather than where he
    /// would be if he sprinted on. A ball into real space is the `ToRun` variant, which has its own,
    /// longer leash and pays the lead charge in the pass model.
    ///
    /// Public because it is a stated bound on the carrier's action set (like <see cref="ShieldMaxTimeOnBall"/>),
    /// not an internal tuning number: the recycle pass and the regression suite both quote it.
    ///
    /// AIB-02c: tightened from six metres. A pass to feet is a bet that the receiver keeps running on his
    /// current velocity for the whole flight, and nothing in the simulation makes that happen without a
    /// promise — the report measured a team-mate jogging sideways at 5 m/s being aimed 5.5 m away over a
    /// 1.1 s flight, with the ball rolling into empty grass when he turned. A feet pass is now planned with
    /// <c>receiverCommittedToRun: false</c>, so the planner freezes the modelled receiver at this same bound
    /// instead of refusing the pass; a receiver who has promised a run is served by the <c>ToRun</c> variant
    /// (<see cref="RunPassMaxLeadMeters"/>) or by his own advertised region.
    /// </summary>
    public const float FeetPassMaxLeadMeters = 2.5f;

    /// <summary>
    /// AIB-02c: longest lead, in metres, a set-piece delivery gives a moving receiver. Restarts are not the
    /// audited carrier path and keep the pre-AIB six-metre bound: a throw-in or a free kick regularly meets a
    /// runner, and tightening it here would be a second, silent behaviour change.
    /// </summary>
    internal const float SetPieceRaisedLeadMaxMeters = 6f;

    /// <summary>Longest lead a pass into a run region may give, in metres (A4.1's ToRun variant).</summary>
    internal const float RunPassMaxLeadMeters = 25f;

    /// <summary>
    /// Candidate tag of a pass played into a run region (A4.1's <c>ToRun</c> variant, A5.3's promised
    /// variant). Public because it is a candidate identity — commitment, the claim path and the inspector
    /// all recognise the action by it — not a tuning number.
    /// </summary>
    public const string RunPassTag = "ToRun";

    // A4.4: every action's value is expressed in ActionValueScale's documented unit — one clean possession
    // retained in midfield. A pass composes `reach × receptionValue − (1 − reach) × loss`, with the loss
    // term as the single owner of transition risk; the carry, shield, clearance and take-on curves keep
    // their tested heuristic shape and their constants live in the same file so the scales can be compared.
    internal const float HumanCallPassBonus = 0.75f;

    /// <summary>Raised alternatives a single decision may offer (a bounded hoof budget).</summary>
    internal const int MaxLoftedCandidates = 2;

    /// <summary>Fixed capacity of the pass-target pre-rank buffer (16 covers eleven-a-side).</summary>
    internal const int PassTargetCapacity = 16;

    /// <summary>
    /// How many refused candidates one decision records with a reason (A4.6). Bounded like every other
    /// diagnostic buffer: a carrier with eleven team-mates can be refused by all of them.
    ///
    /// AUD-P3 (2026-11-23) raised it 8 → 16: the carrier-initiated space probe can spend up to two refusals
    /// per planned receiver (crowded grass, race lost) before the ordinary lane/completion refusals, so eight
    /// could be filled by probes alone and hide `lane-blocked`/`low-completion` for other targets from the
    /// inspector. The buffer stores 16 entries regardless of use; this is a diagnostic-size change only.
    /// </summary>
    public const int MaxRejections = 16;

    /// <summary>Reason text for a pass the model priced as not worth offering.</summary>
    public const string RejectionLowCompletion = "low-completion";

    /// <summary>Reason text for a pass whose line a defender physically blocks.</summary>
    public const string RejectionLaneBlocked = "lane-blocked";

    /// <summary>Reason text for a receiver in an offside position.</summary>
    public const string RejectionOffside = "offside";

    /// <summary>Reason text for a receiver outside the pass distance band.</summary>
    public const string RejectionOutOfRange = "out-of-range";

    /// <summary>Reason text for a receiver too close to the passer's own goal line.</summary>
    public const string RejectionOwnGoalMargin = "own-goal-margin";

    /// <summary>Reason text for a human caller whose request could not be planned.</summary>
    public const string RejectionUnplannable = "unplannable";

    /// <summary>
    /// AUD-P3 (audit 2026-11-23): reason text for a space probe whose grass is occupied — a believed
    /// opponent is standing in it (<see cref="SpaceProbeCrowdRadiusMeters"/>) or the probe's
    /// <see cref="EvaluateSpaceFreedom"/> is too low for it to be called space.
    /// </summary>
    public const string RejectionSpaceContested = "space-contested";

    /// <summary>
    /// AUD-P3: reason text for a space probe the receiver cannot win: either he cannot reach the point
    /// inside the reach horizon at all, or the nearest believed opponent would also be there and the
    /// receiver's arrival does not beat him by <see cref="SpaceRaceMarginSeconds"/>.
    /// </summary>
    public const string RejectionSpaceRaceLost = "space-race-lost";

    /// <summary>
    /// AIB-10 (audit 2026-11-23, finding 4): reason text for a space probe whose ball has already rolled out
    /// of the promised region by the time the receiver can get there — the audit's "ball 0.80 s vs receiver
    /// 1.72 s, stopped 29.2 m past" impossible pass. The promise's arrival is the ball's flight by design, so
    /// <see cref="RolledPastPromiseMeters"/> measures the receiver's real arrival through the promise's
    /// contest moment; a ball past the shared <see cref="PromiseCatchRadiusMeters"/> is refused here.
    /// </summary>
    public const string RejectionSpaceRolledPast = "space-rolled-past";

    /// <summary>
    /// AIB-10 / 2026-11-24 owner-review correction (review item A2): reason text for a space probe whose
    /// calibrated roll would take the <b>ball out of the pitch</b> before the receiver can get there. Probes
    /// are clamped <see cref="SpaceProbePitchMarginMeters"/> inside every line, but the ball is not: a probe
    /// aimed near a touchline or goal line that then rolls on can cross the line before the meeting, and a
    /// ball out of play is not a meeting at all. <see cref="PassSuccessModel"/>'s
    /// <see cref="Physics.PassPlanner.BoundaryExitRisk01"/> prices that exit in the completion; it never
    /// refuses it, so the physical feasibility question is asked here.
    /// </summary>
    public const string RejectionSpaceRollsOut = "space-rolls-out";

    /// <summary>
    /// AIB-11 (audit 2026-11-23, finding 6) / 2026-11-24 owner-review correction: reason text for any promised
    /// ground pass whose ball would be more than <see cref="PromiseCatchRadiusMeters"/> past its promised
    /// region at the moment the receiver is there. The advertised-run ball used to have no roll-out check at
    /// all, so a fast ball timed 1 s before a far meet rolled 6–7 m past <c>MeetXZ</c> and the promise still
    /// read as met; this is the same criterion <see cref="RejectionSpaceRolledPast"/> applies to a space probe,
    /// now shared by every promise through <see cref="RolledPastPromiseMeters"/>.
    /// </summary>
    public const string RejectionRolledPastPromise = "rolled-past-promise";

    // ---------------------------------------------------------------------------------------------
    // A8.1 — bounded combinations. The numbers are the hand-set calibration of the trigger, the two
    // preference bonuses and the return run's utility; the contract's own lifecycle (delay, arrival,
    // grace, capacity) lives in CombinationCoordinator. Nothing here creates a new action capability:
    // the pass is still one pass, the run is still one movement, and execution still decides contacts.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Candidate tag of a one-two's return run (A8.1). Interned: a candidate is built per decision.</summary>
    public const string CombinationReturnTag = "CombinationReturn";

    /// <summary>Display name of the return-run candidate (diagnostics only).</summary>
    public const string CombinationReturnActionName = "Run:CombinationReturn";

    /// <summary>
    /// Preference a candidate pass earns when a combination is offered from it, on the action-value scale.
    /// Deliberately a nudge, not a decision: a pass still has to beat every other action on its own
    /// reception value, and the bonus is smaller than the progression preference's own range (A4.4).
    /// </summary>
    public const float CombinationInitiationBonus = 0.12f;

    /// <summary>
    /// Preference the first receiver's continuation pass earns. Larger than the initiation bonus because the
    /// receiver is honouring a promise (the contract names the region and the time), but still bounded so a
    /// blocked or badly positioned return is not forced.
    /// </summary>
    public const float CombinationContinuationBonus = 0.20f;

    /// <summary>
    /// Pre-rank bonus that keeps the continuation target inside <see cref="AiRuntimeConfig.MaxPassTargets"/>
    /// so the promise is priced rather than pruned. Same size and same reason as
    /// <see cref="AnnouncedRunRankBonus"/> and <see cref="SwitchCallRankBonus"/>: communication that is
    /// dropped before it is read is not communication.
    /// </summary>
    public const float CombinationRankBonus = 0.35f;

    /// <summary>Shortest pass a one-two can be built from, in metres (below this it is a touch, not a pass).</summary>
    internal const float OneTwoMinPassMeters = 5f;

    /// <summary>Longest pass a one-two can be built from, in metres (a give-and-go is a short move).</summary>
    internal const float OneTwoMaxPassMeters = 20f;

    /// <summary>Pressure on the carrier that makes the bounce worth offering, in metres to the nearest opponent.</summary>
    internal const float OneTwoCarrierPressureMeters = 4.5f;

    /// <summary>Pressure on the receiver that makes the one-touch return natural, in metres to the nearest opponent.</summary>
    internal const float OneTwoReceiverPressureMeters = 5.5f;

    /// <summary>How far up the pitch the return region is placed from the initiator, in metres.</summary>
    public const float OneTwoReturnForwardMeters = 8f;

    /// <summary>How far sideways (toward the receiver's side) the return region is placed, in metres.</summary>
    public const float OneTwoReturnLateralMeters = 2.5f;

    /// <summary>Maximum behind-the-offside-line depth a one-two return region may target, in metres (Law 11).</summary>
    internal const float OneTwoBehindLineMaxMeters = 6.0f;

    /// <summary>Shortest progression the return region must offer for the move to be worth making, in metres.</summary>
    internal const float OneTwoMinProgressionMeters = 5f;

    /// <summary>How wide the return region is, in metres. A region, not a point: the return is met there.</summary>
    internal const float OneTwoReturnRadiusMeters = 3f;

    /// <summary>Room the return region must have around it (0..1, see EvaluateSpaceFreedom) to be a lane.</summary>
    internal const float OneTwoMinSpace01 = 0.30f;

    /// <summary>Clearance the receiver-to-region lane must have (0..1) before it is offered as a return.</summary>
    internal const float OneTwoMinLaneClearance01 = 0.40f;

    /// <summary>Speed the initiator's travel time to the return region is estimated at, in m/s (acceleration-aware).</summary>
    internal const float OneTwoRunnerSpeedMetersPerSecond = 5.6f;

    /// <summary>Time the receiver is assumed to need to control the pass before he can return it, in seconds.</summary>
    internal const float OneTwoReceiverSetupSeconds = 0.45f;

    /// <summary>Speed the return pass is estimated at for the meeting time, in m/s.</summary>
    internal const float OneTwoReturnPassSpeedMetersPerSecond = 11f;

    /// <summary>Longest meeting a one-two may plan for, in seconds. Beyond this it is not a combination.</summary>
    internal const float OneTwoMaxArrivalSeconds = 3.2f;

    /// <summary>One-two confidence: the floor before the lane/space/progression terms are added.</summary>
    internal const float OneTwoConfidenceBase = 0.35f;

    /// <summary>Lane term of the one-two confidence (multiplied by lane clearance 0..1).</summary>
    internal const float OneTwoConfidenceLaneWeight = 0.35f;

    /// <summary>Space term of the one-two confidence (multiplied by space 0..1).</summary>
    internal const float OneTwoConfidenceSpaceWeight = 0.20f;

    /// <summary>Progression term of the one-two confidence (multiplied by progression/10, capped 0..1).</summary>
    internal const float OneTwoConfidenceProgressionWeight = 0.10f;

    /// <summary>Shortest layoff a third-player contract is built from, in metres.</summary>
    internal const float ThirdPlayerMinLayoffMeters = 5f;

    /// <summary>Longest layoff a third-player contract is built from, in metres.</summary>
    internal const float ThirdPlayerMaxLayoffMeters = 24f;

    /// <summary>Clearance the receiver-to-runner lane must have before the third-player move is offered.</summary>
    internal const float ThirdPlayerMinLaneClearance01 = 0.40f;

    /// <summary>Earliest the third runner may be due to arrive, in ticks, for the layoff to be timely.</summary>
    internal const int ThirdPlayerMinLeadTicks = 10;

    /// <summary>
    /// How early a third-player contract's layoff may arrive before the runner's advertised meeting tick,
    /// in ticks. An early ball into a region a man is still running into is a normal through ball; a ball a
    /// second and a half early is a ball to where he was not.
    /// </summary>
    internal const int ThirdPlayerEarlyArrivalTicks = 45;

    /// <summary>Speed the layoff to the third runner is estimated at for the meeting time, in m/s.</summary>
    internal const float ThirdPlayerLayoffSpeedMetersPerSecond = 12f;

    /// <summary>Space the first pass of a third-player move must have behind it, i.e. the receiver's room.</summary>
    internal const float ThirdPlayerConfidenceBase = 0.40f;

    /// <summary>Utility floor of the one-two return run before the contract's confidence and the room are added.</summary>
    internal const float CombinationReturnRunBaseUtility = 0.92f;

    /// <summary>How much of the contract's remaining promise the return run's utility carries.</summary>
    internal const float CombinationReturnRunConfidenceWeight = 0.28f;

    /// <summary>How much the room at the return region adds to the return run's utility.</summary>
    internal const float CombinationReturnRunSpaceWeight = 0.12f;

    /// <summary>Below this room the return region is no longer a lane and the initiator abandons it.</summary>
    internal const float CombinationReturnRunMinSpace01 = 0.25f;

    // ---------------------------------------------------------------------------------------------
    // A8.3 — switch / overload-to-isolate recognition. One bounded preference on a pass that already exists,
    // priced from the carrier's own picture by AI/SwitchRecognition. Nothing here creates a capability.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Pre-rank bonus that keeps the far-side target of a recognized switch inside
    /// <see cref="AiRuntimeConfig.MaxPassTargets"/>, so the opportunity is priced rather than pruned. Same size
    /// and same reason as the advertisement, switch-call and combination bonuses: an option dropped before it
    /// is read was never an option. The rank bonus is a bounded rescue, never the decision.
    /// </summary>
    internal const float SwitchRecognitionRankBonus = 0.35f;

    // ---------------------------------------------------------------------------------------------
    // AUD-P3 (audit 2026-11-23) — the carrier-initiated pass into space (`ToSpace`).
    //
    // Every space pass used to be receiver-initiated. `TryBuildRunPassOption` needs an advertised run or a
    // team-mate already sprinting at `MinUncommittedRunSpeed`, and even then the uncommitted lead is
    // hedged to 1.5 m on top of the planner's 2.5 m cap. A carrier could therefore not look at a
    // team-mate with empty onside grass in front of him, play the ball into that grass, and thereby
    // trigger the run — the audit's Capture 4. The builder below does exactly that: 1–2 bounded probes in
    // the receiver's own lane, a physical race test over the one reach model, and a *firm* (crisp-floor)
    // ball timed to the grass rather than a slow roller aimed at where the receiver is standing.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// AUD-P3: candidate tag of a carrier-initiated pass into empty grass ahead of a team-mate. Public
    /// because it is a candidate identity — commitment, the claim path and the inspector recognise the
    /// action by it. It is deliberately <b>not</b> in <see cref="IsDeliveredToRun"/>: the carrier chose
    /// the grass, no team-mate announced that meeting, so this ball must not claim an advertisement.
    /// </summary>
    public const string SpacePassTag = "ToSpace";

    /// <summary>
    /// AUD-P3: the nearer of the two space probes, in metres ahead of the receiver along the attack axis.
    /// The pair (8 m, 14 m) is the audit's own suggestion: 8 m is a ball the receiver attacks in a stride,
    /// 14 m is the penetrative ball that actually turns him round and goes past a line.
    /// </summary>
    public const float SpaceProbeNearMeters = 8f;

    /// <summary>
    /// AUD-P3: the farther of the two space probes, in metres ahead of the receiver along the attack axis.
    /// It is tried first — if the far grass is onside and won, it is the more penetrative ball; the near
    /// probe is the bounded fallback when the far one is offside-limited or crowded. Two probes, never more.
    /// </summary>
    public const float SpaceProbeFarMeters = 14f;

    /// <summary>
    /// AUD-P3: smallest forward gain, in metres, a probe must still offer after the offside/pitch clamp
    /// before it is a pass into space at all. Without it a receiver standing on the line would be "played
    /// into space" half a metre ahead of his own feet — a duplicate of the feet pass wearing a new tag.
    /// </summary>
    public const float SpaceProbeMinForwardMeters = 3f;

    /// <summary>
    /// AUD-P3: margin every probe keeps from the goal line and both touchlines, in metres. The audit's own
    /// figure (">= 3.0 m inside touchlines"): a firm ball into grass three metres from the line is a ball
    /// the receiver can still run onto, not a ball aimed out of play.
    /// </summary>
    public const float SpaceProbePitchMarginMeters = 3f;

    /// <summary>
    /// AUD-P3: radius around a probe inside which a believed opponent makes the grass crowded, in metres.
    /// Two bodies inside four metres are not "space": the arena the receiver would run into is occupied.
    /// </summary>
    public const float SpaceProbeCrowdRadiusMeters = 4f;

    /// <summary>
    /// AUD-P3: minimum <see cref="EvaluateSpaceFreedom"/> at the probe (0..1, saturating at
    /// <see cref="SpaceFreedomSaturationMeters"/> = 9 m), so 0.55 is roughly five metres of room. The
    /// hard radius above is the "nobody is standing there" test; this is the "there is grass to run into"
    /// test, because the ball is played to a region the receiver has to attack.
    /// </summary>
    public const float SpaceProbeMinFreedom01 = 0.55f;

    /// <summary>
    /// AUD-P3: race margin, in seconds, by which the receiver's real arrival at the probe must beat the
    /// nearest believed opponent's. 0.35 s is a clear stride, not a photo finish: below it the "space" is
    /// a 50/50 and a firm ball into it is a pass into a contest.
    /// </summary>
    public const float SpaceRaceMarginSeconds = 0.35f;

    /// <summary>
    /// AUD-P3: horizon of the race query, in seconds. The shared reach model's own default — a race that
    /// cannot be decided inside it is not a race the carrier can plan against.
    /// </summary>
    public const float SpaceRaceHorizonSeconds = TimeToReachModel.DefaultHorizonSeconds;

    /// <summary>
    /// AUD-P3: radius of the promised meeting region, in metres. The promise is the grass, not a dot: the
    /// ball is played to arrive in a region the receiver runs into, and the pass model's promise timing
    /// tolerance scales with it (<c>radius x 0.5 / PromisedRegionCrossSpeed</c> = 0.31 s here).
    /// </summary>
    public const float SpacePromiseRadiusMeters = 4f;

    /// <summary>
    /// AIB-10 (audit 2026-11-23, finding 4) / AIB-11 (finding 6) / 2026-11-24 owner-review correction: the
    /// <b>one shared roll-out bound</b>, in metres, that every promised ground pass must respect — how far
    /// past the promised region the rolling ball may be at the moment the receiver is there before the promise
    /// is a fiction.
    ///
    /// The space promise's <c>ArrivalSeconds</c> is deliberately the ball's own flight (AUD-P3: the timing
    /// term is exactly 1 and the lead is not double-charged), so the pass model cannot see that a ball struck
    /// well before the receiver's arrival has already rolled out of the region; the advertised-run promise has
    /// the same failure from the other side (a firm ball timed seconds early rolls past the meet). The measured
    /// form of both is <see cref="RolledPastPromiseMeters"/>: the ball must still be within the promise region
    /// <b>plus one receiving reach</b> at the promise's contest moment, or the pass is refused. The reach is
    /// the pass model's own intercept-reach floor (<see cref="PassSuccessModel.LaneStepInReachMeters"/>, 1.22 m;
    /// execution plays the ball at ~1.24 m), because the promise names the region the receiver will be in, not
    /// the exact point: a ball just past the region's edge is still a ball the man in it can touch. Measured:
    /// a legitimate 21.8 m space ball sits 4.1 m past its aim point when the receiver arrives (inside the
    /// 5.22 m bound), while the audit's impossible ball is ~16 m past. The audit's blunt alternative
    /// (<c>receiverArrival &gt; flight + 0.35 s</c>) was not used: the roll-out is the quantity the promise
    /// is actually about, and 0.35 s is a 4–6 m lag at a crisp ball's arrival speed.
    ///
    /// <para><b>2026-11-24 review correction:</b> named <c>SpacePromiseCatchRadiusMeters</c> when only the
    /// space probe used it. It is now the single criterion for every promised pass — the advertised and one-two
    /// <c>ToRun</c> paths reject through it in <see cref="TryAddGroundPass"/> — so it carries the shared name.
    /// The value is unchanged: lowering it to 4 m without new lane evidence flipped a legitimate 21.8 m ball
    /// and three fixtures (AIB-10 batch), and an advertised run's 5 m meeting region is wider than the 4 m space
    /// one, so this bound is if anything conservative there.</para>
    /// </summary>
    public const float PromiseCatchRadiusMeters =
        SpacePromiseRadiusMeters + PassSuccessModel.LaneStepInReachMeters;

    /// <summary>
    /// ai63 (Task 2.3): distance-scaled catch bound, in metres. On a short 6–8 m pass, the flat 5.22 m
    /// radius allows a ball to arrive early and roll nearly another full pass length past the receiver;
    /// scaling with <c>0.35 × distance</c> (floored at <c>LaneStepInReachMeters + 0.5</c> = 1.72 m, capped at
    /// <see cref="PromiseCatchRadiusMeters"/>) keeps short passes tight (~2.1 m at 6 m, ~3.5 m at 10 m) while
    /// preserving the exact 5.22 m bound for passes ≥ 15 m.
    /// </summary>
    public static float EffectivePromiseCatchRadiusMeters(float passDistanceMeters)
    {
        if (!float.IsFinite(passDistanceMeters) || passDistanceMeters <= 0f)
            return PromiseCatchRadiusMeters;

        return MathF.Min(
            PromiseCatchRadiusMeters,
            MathF.Max(PassSuccessModel.LaneStepInReachMeters + 0.5f, 0.35f * passDistanceMeters));
    }

    /// <summary>
    /// AUD-P3: base confidence of a space promise. The confidence is derived from the measured race
    /// margin — <c>base + weight x clamp(margin / scale, 0, 1)</c>, clamped into
    /// <c>[floor, 1]</c> — so a receiver who wins by the minimum 0.35 s gets
    /// <c>0.5 + 0.5 x 0.35 = 0.675</c> and one who wins by a second or more gets 1.0. It is never the
    /// arbitrary 1.0 that would make an unraced ball look certain, and it is the only place the race
    /// enters the pass model (the promise replaces the receiver-arrival estimate).
    /// </summary>
    public const float SpacePromiseConfidenceBase = 0.5f;

    /// <summary>AUD-P3: how much of a full second's race margin the promise confidence carries.</summary>
    public const float SpacePromiseConfidenceMarginWeight = 0.5f;

    /// <summary>AUD-P3: race margin, in seconds, at which the confidence term saturates (a clear win).</summary>
    public const float SpacePromiseConfidenceMarginScaleSeconds = 1f;

    /// <summary>
    /// AUD-P3: floor of the derived space-promise confidence. Unreachable with the 0.35 s race margin
    /// (<c>base + weight x 0.35 = 0.675</c>); kept as the stated lower bound of the derivation so a later
    /// relaxation of the margin cannot produce a zero-confidence "promise" that the pass model would
    /// silently treat as unpromised.
    /// </summary>
    public const float SpacePromiseConfidenceFloor = 0.2f;

    /// <summary>
    /// AUD-P6 / Fix 5.1: the fraction of the routine pass trigger a penetrative ball gets to wait, as a
    /// first-touch release window. The audit asked for "<b>immediately</b>, without waiting out the 0.45 s
    /// lockout", and that was implemented first — but a zero lockout is contradicted by two recorded
    /// invariants: AUD-F18 pins the team tempo trait ("tempo must change how quickly a carrier releases the
    /// ball") and <c>ScheduledDecisionTests</c> pins that a scheduled decision's first scored command is a
    /// movement command, not a kick. At a zero lockout every tempo released a pass on tick 1, deleting the
    /// tempo ordering the trait exists for. The measured compromise is this scale: a penetrative ball waits
    /// <c>passTriggerSeconds x 0.35</c> (the trigger band is 0.315–0.615 s over the whole tempo range, so the
    /// window is ≈0.11–0.22 s, i.e. 7–13 ticks) instead of 0.315–0.615 s, which keeps the trait's ordering
    /// (fast &lt; slow) while releasing the one-touch through ball well before the routine lockout. The routine,
    /// lateral and safe passes keep the full delay.
    /// <para>Note that the <b>unit</b> of the audit's own fix is preserved: inside the window only the
    /// penetrative subset is built (promised/advertised run, <c>ToSpace</c>, a geometrically line-breaking
    /// feet ball), never the routine set.</para>
    /// </summary>
    public const float PenetrativeReleaseScale = 0.35f;

    private readonly PitchDimensions _pitch;
    private readonly MatchCommunicationContext? _communication;
    private readonly PassPlanner _passPlanner;
    private readonly PassSuccessModel _passSuccess;
    // A8.2: the shallow continuation search. Owned here because it is the carrier's own examination of its
    // own pass options; it reads the same pass planner and success model the decision prices with.
    private readonly ShallowActionSearch _search;
    private readonly ReceptionValueModel _receptionValue;
    private readonly ReceptionPlanner _receptionPlanner;
    private readonly RunCandidateGenerator _runs;
    private readonly AiRuntimeConfig _ai;
    private readonly LocalPressureField _pressure = new();
    private readonly List<UtilityCandidate> _lastCandidates = new();

    // A7.3 reception plans of the most recent decision, indexed the same way the reception candidates
    // were added. Reused: a decision that is not a reception leaves the count at zero.
    private readonly CandidateKey[] _receptionKeys = new CandidateKey[ReceptionPlanner.MaxOptions];
    private readonly ReceptionPlan[] _receptionPlans = new ReceptionPlan[ReceptionPlanner.MaxOptions];
    private int _receptionPlanCount;

    // Pass-target pre-rank buffer (A4.1 bound): ids in descending rank order, plus their ranks. Reused
    // so a decision allocates nothing.
    private readonly int[] _passTargetIds = new int[PassTargetCapacity];
    private readonly float[] _passTargetRank = new float[PassTargetCapacity];
    private int _passTargetCount;

    // Refused candidates and why (A4.6). Reused, so a decision allocates nothing to explain itself.
    private readonly CandidateRejection[] _rejections = new CandidateRejection[MaxRejections];
    private int _rejectionCount;

    // A8.3: the switch opportunities the most recent decision recognized. Reused like the other buffers: a
    // decision with no far-side opportunity leaves the count at zero.
    private int _switchPreferencesApplied;
    private SwitchOpportunity _lastSwitchOpportunity;

    // A6.1: the defensive policy's answer for this decision. Reused like the other buffers.
    private readonly PressCandidate[] _defenceBuffer = new PressCandidate[PressPolicy.MaxCandidates];

    /// <summary>
    /// A8.1: one bounding contract the carrier was offering on a pass, before the decision is made. The
    /// proposal is remembered per receiver so the chosen candidate can publish exactly the contract it was
    /// priced with — the pass that won is the commitment, not every pass that was considered.
    /// </summary>
    private readonly record struct CombinationProposal(
        CombinationKind Kind,
        int ReceiverId,
        int ThirdPlayerId,
        Vector2 ReturnXZ,
        float ReturnRadiusMeters,
        int ExpectedArrivalTick,
        float Confidence01);

    // A8.1: the combination proposals of the most recent decision, indexed by receiver id. Reused: a
    // decision with no combination offer leaves the count at zero.
    private readonly int[] _combinationMateIds = new int[PassTargetCapacity];
    private readonly CombinationProposal[] _combinationProposals = new CombinationProposal[PassTargetCapacity];
    private int _combinationProposalCount;

    private Vector2 _lastCarryDirection;
    private bool _hasLastCarryDirection;
    private float _tackleCooldown;
    private float _shotCooldown;
    private float _timeOnBall;

    public CandidateGenerator(
        PitchDimensions pitch,
        MatchCommunicationContext? communication = null,
        AiRuntimeConfig? ai = null)
    {
        _pitch = pitch;
        _communication = communication;
        _ai = ai ?? AiRuntimeConfig.Default;
        _passPlanner = new PassPlanner(pitch);
        _passSuccess = new PassSuccessModel(_passPlanner);
        _search = new ShallowActionSearch(pitch, _passPlanner, _passSuccess);
        _receptionValue = new ReceptionValueModel(pitch);
        _receptionPlanner = new ReceptionPlanner(pitch);
        // A5.3: the runs this player adopts are announced on the match's own communication board (null
        // when there is no match communication: a probe or a unit test built without one).
        _runs = new RunCandidateGenerator(pitch, communication?.Runs);
    }

    /// <summary>
    /// The off-ball path set for the most recent decision (A5.1/A5.2), including the run this player has
    /// adopted. Diagnostics, tests and the A5.3 advertisement read it; nothing in the decision path does.
    /// </summary>
    public RunCandidateGenerator Runs => _runs;

    /// <summary>Candidates generated by the most recent call.</summary>
    public IReadOnlyList<UtilityCandidate> Candidates => _lastCandidates;

    /// <summary>Number of candidates generated by the most recent call.</summary>
    public int CandidateCount => _lastCandidates.Count;

    /// <summary>
    /// A9.3: cumulative generation work this player's decisions have done. Diagnostics and the perf
    /// evidence probe read it; nothing in the decision path does.
    /// </summary>
    public AiWorkStats Work { get; } = new();

    /// <summary>
    /// A9.3: cumulative trajectory work done by this player's reception planner (one estimate per planned
    /// first touch). Merged with <see cref="Work"/> by the engine for the match-level picture.
    /// </summary>
    public AiWorkStats ReceptionWork => _receptionPlanner.Work;

    /// <summary>
    /// A9.3: how many off-ball candidates the last decision's cap removed (0 when the configured
    /// <see cref="AiRuntimeConfig.MaxOffBallCandidates"/> did not bind). Diagnostics only.
    /// </summary>
    public int OffBallCandidatesTrimmed { get; private set; }

    /// <summary>
    /// How many candidates the most recent decision refused, with a reason (A4.6). Bounded by
    /// <see cref="MaxRejections"/>; a decision that refused nothing reports zero.
    /// </summary>
    public int RejectionCount => _rejectionCount;

    /// <summary>One refused candidate and why. <paramref name="index"/> must be below <see cref="RejectionCount"/>.</summary>
    public CandidateRejection GetRejection(int index) => _rejections[index];

    /// <summary>
    /// A8.3: pass candidates in the most recent decision that carried a recognized switch preference. This is
    /// the count of opportunities the carrier's own picture supported, not of passes chosen; diagnostics read it
    /// (the <c>--switches</c> probe), the decision path never does.
    /// </summary>
    public int SwitchPreferencesApplied => _switchPreferencesApplied;

    /// <summary>
    /// A8.3: the best recognized switch opportunity of the most recent decision (the one with the largest
    /// preference), or a default record when nothing was recognized. Diagnostics and tests only.
    /// </summary>
    public SwitchOpportunity LastSwitchOpportunity => _lastSwitchOpportunity;

    /// <summary>
    /// Local pressure window around this player's last on-ball decision (A2.4). Exposed so diagnostics
    /// and tests can see what the carry choice was actually reading.
    /// </summary>
    public LocalPressureField PressureField => _pressure;

    /// <summary>Seconds the current player has been on the ball; set by the controller each decision.</summary>
    public float TimeOnBall { set => _timeOnBall = value; }

    /// <summary>Seconds remaining before this player may request another tackle.</summary>
    public float TackleCooldown => _tackleCooldown;

    /// <summary>Drops the previous decision candidate set. Called once per decision.</summary>
    /// <remarks>
    /// The pass-target pre-rank is dropped with it (M1): <see cref="WasOfferedAsPassTarget"/> answers a
    /// question about *this* decision, so a decision that built no pass set — the carrier is not yet
    /// ready to release the ball — must not inherit the previous passage's buffer and filter the recycle
    /// options with it. Reset here rather than only in the ranking pass, which the early-possession path
    /// skips.
    /// </remarks>
    public void Clear()
    {
        _lastCandidates.Clear();
        _rejectionCount = 0;
        _passTargetCount = 0;
        _receptionPlanCount = 0;
        _combinationProposalCount = 0;
        _switchPreferencesApplied = 0;
        _lastSwitchOpportunity = default;
    }

    /// <summary>
    /// Advances the timers this object owns. Called once per live tick, not once per decision (L7): the
    /// tackle cooldown is a reaction timer, and slowing the utility cadence must not slow it down.
    /// </summary>
    public void TickTimers(float dt)
    {
        _tackleCooldown = MathF.Max(0f, _tackleCooldown - dt);
        // A9.6d: the shot recovery is a reaction timer too (L7: it ticks every live tick, not per decision).
        _shotCooldown = MathF.Max(0f, _shotCooldown - dt);
    }

    /// <summary>
    /// Drops the candidate set and the tackle cooldown (A1.5). The cooldown is passage-of-play state: a
    /// player who had just tackled may challenge again from a restart, and a cooldown that leaked across
    /// a stoppage would make a defender passive for a reason nobody could see in the log.
    /// </summary>
    public void ResetForPhase(AiResetReason reason)
    {
        _lastCandidates.Clear();
        _passTargetCount = 0;
        _receptionPlanCount = 0;
        _combinationProposalCount = 0;
        _switchPreferencesApplied = 0;
        _lastSwitchOpportunity = default;
        _tackleCooldown = 0f;
        _shotCooldown = 0f;
        _timeOnBall = 0f;
        _hasLastCarryDirection = false;
        _lastCarryDirection = Vector2.Zero;
        // A run chosen before a restart attacks a passage of play that has ended (A1.5/A5.2).
        _runs.Reset();
    }

    /// <summary>
    /// Tells the run generator what this decision actually chose (A5.2), and raises the claim that keeps a
    /// promised runner committed (A5.3).
    /// </summary>
    public void NoteDecision(in FootballerObservation obs, in CandidateKey key)
    {
        // A9.6d: a shot is a one-off act, and the body needs a moment before the next strike — otherwise the
        // rebound of a parry (which comes straight back at the shooter) was shot again from a worse angle.
        if (key.Kind == CandidateKind.Shot)
            _shotCooldown = ShotRecoverySeconds;

        _runs.NoteSelected(obs, key);

        // A pass into an advertised region is a promise in the other direction: the runner has announced
        // he will be there, the carrier has played the ball to that region, and the runner holds it until
        // the ball has had time to arrive. Raised once per selection from the region's own meeting tick, so
        // it is a statement about a ball rather than a per-tick re-issue.
        //
        // Every delivery aimed at an announced region claims it (A5.5b), not just the ground pass: a cross,
        // a driven ball and a cutback are all promises to a runner, and a runner who dropped his run because
        // only the ground variant could claim would not be there for the ball that was actually played.
        if (key.Kind == CandidateKind.Pass && key.TargetPlayerId > 0 &&
            IsDeliveredToRun(key.TargetTag) &&
            _communication is not null &&
            _communication.Runs.TryGetAdvertisement(obs.SelfTeam, key.TargetPlayerId, obs.Tick, out RunAdvertisement advert))
        {
            _communication.Runs.Claim(obs.SelfTeam, key.TargetPlayerId, obs.SelfId, advert.MeetTick, obs.Tick);
        }

        // A8.1: the pass the carrier actually chose is the contract he commits to. Only a winning
        // combination pass publishes — a pass that merely carried the initiation preference does not, and a
        // re-decided identical pass is suppressed by the board rather than restarting its delay.
        if (!_ai.EnableCombinations) return;

        // AUD-P3: a ToSpace ball is delivered into grass ahead of the receiver, not to the feet/region the
        // contract's return was timed from — the receiver cannot meet a one-two's return moment from a
        // sprint onto a ball he has not reached yet. The space candidate therefore never publishes a
        // contract, even when a proposal was formed for the same receiver by the fullest pass set. (The
        // receiver-side abandonment below still runs: choosing the space ball over the contract ends it.)
        bool publishesContract = !string.Equals(key.TargetTag, SpacePassTag, StringComparison.Ordinal);
        if (publishesContract && key.Kind == CandidateKind.Pass && key.TargetPlayerId > 0 &&
            TryGetCombinationProposal(key.TargetPlayerId, out CombinationProposal proposal) &&
            _communication is not null)
        {
            _communication.Combinations.Publish(
                proposal.Kind, obs.SelfTeam, obs.SelfId, proposal.ReceiverId, proposal.ThirdPlayerId,
                proposal.ReturnXZ, proposal.ReturnRadiusMeters, proposal.ExpectedArrivalTick,
                proposal.Confidence01, obs.Tick);
        }

        // A8.1 receiver-side: the chosen action either honours the contract or visibly ends it. The
        // continuation itself is attributed by the execution contacts (CombinationCoordinator.ApplyContacts);
        // this only abandons a move the receiver has declined by playing the ball elsewhere.
        if (obs.Combination.IsReceiver && key.Kind == CandidateKind.Pass &&
            key.TargetPlayerId != obs.Combination.ContinuationTargetId)
        {
            AbandonCombination(obs, CombinationEndReason.AbandonedByReceiver);
        }
    }

    /// <summary>True when this candidate tag names a ball played into a team-mate's announced region.</summary>
    /// <remarks>
    /// The raised alternative belongs here (M2). It is aimed at the advertisement's meeting point when the
    /// team has heard one — the same promise a cross or a driven ball makes — and it has the longest flight
    /// of any delivery, so leaving it out meant a lofted ball could land in a region whose announcement had
    /// already been withdrawn because nothing had claimed it.
    /// </remarks>
    private static bool IsDeliveredToRun(string? tag) =>
        string.Equals(tag, RunPassTag, StringComparison.Ordinal) ||
        string.Equals(tag, CrossPassTag, StringComparison.Ordinal) ||
        string.Equals(tag, DrivenPassTag, StringComparison.Ordinal) ||
        string.Equals(tag, CutbackPassTag, StringComparison.Ordinal) ||
        string.Equals(tag, LoftedPassTag, StringComparison.Ordinal);

    /// <summary>
    /// Generates the carrier's candidate set for one decision (A4.1).
    ///
    /// The set is an explicit list of named actions — shot, passes to feet, passes into a run region,
    /// raised alternatives where the ground lane is physically blocked, recycle/keeper support, carry,
    /// take-on, shield and clearance — appended by one builder each. Every candidate carries a stable
    /// <see cref="CandidateKey"/> so commitment and tracing can tell them apart, and the finished set is
    /// bounded by configuration rather than by how many teammates happen to be visible.
    /// </summary>
    public void GenerateOnBallCandidates(in FootballerObservation obs, Vector3 oppGoalCenter, float offsideLineProjX)
    {
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 goalXZ = new(oppGoalCenter.X, oppGoalCenter.Z);
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        float distToGoal = Vector2.Distance(selfXZ, goalXZ);
        float myAttackProjX = obs.SelfPosition.X * obs.AttackingDirectionX;
        bool inFinalThird = myAttackProjX > CarryFinalThirdProjX;

        float nearestOppDist = NearestOpponent(obs, selfXZ, out Vector2 nearestOppXZ);

        // AIB-01: the local pressure field is built once per decision, before the pass trigger, because the
        // emergency release is now gated on when an opponent can actually reach the carrier's own feet.
        // ChooseCarry reuses this build (the field caches on tick, observer and centre).
        _pressure.TryBuild(obs, selfXZ, _pitch);
        float opponentArrivalSeconds = _pressure.Read(selfXZ, out _).OpponentArrivalSeconds;
        // The static ring stays as the "a body is on him" floor; the arrival clause is the fix: a presser
        // closing from 5 m is an emergency 0.4 s before the old 2.6 m ring would ever see him.
        bool underHeavyPressure = nearestOppDist < EmergencyPassStaticMeters ||
                                  (opponentArrivalSeconds >= 0f &&
                                   opponentArrivalSeconds < EmergencyPassArrivalSeconds);

        AddShotCandidate(obs, distToGoal, oppGoalCenter);
        AddPassCandidates(obs, ballXZ, selfXZ, offsideLineProjX, underHeavyPressure);
        AddCrossCandidate(obs, ballXZ, selfXZ);
        AddCutbackCandidate(obs, ballXZ, selfXZ);
        AddDrivenCandidate(obs, ballXZ);
        AddRecycleCandidates(obs, ballXZ, myAttackProjX);
        CarryChoice carry = ChooseCarry(
            obs, selfXZ, ballXZ, goalXZ, oppGoalCenter, inFinalThird, nearestOppDist, opponentArrivalSeconds);
        AddCarryCandidate(carry, nearestOppDist);
        AddDribbleCandidate(obs, selfXZ, goalXZ, carry, nearestOppXZ, nearestOppDist);
        AddShieldCandidate(obs, selfXZ, nearestOppXZ, nearestOppDist);
        AddClearanceCandidate(obs, selfXZ, myAttackProjX, nearestOppDist);
        // A8.1: the receiver's continuation is judged against the full priced action set, before the carrier
        // cap trims it — a continuation that was priced but lost the cap is still a promise the team holds.
        CheckCombinationContinuation(obs, offsideLineProjX);
        TrimToCarrierCap();
    }

    /// <summary>
    /// Action: strike at the opponent goal, on the same value scale as a pass (A4.4):
    /// <c>conversion × GoalValue − (1 − conversion) × ShotMissCost</c>, with the conversion curve documented
    /// in <see cref="ActionValueScale.ShotConversion"/>. The old guaranteed close-range bonus is gone: a
    /// shot now has to beat a team-mate in a better position, which is the A4.4 condition for removing it.
    ///
    /// AIB-05: the finish is chosen from three goal targets (near post, centre, far post) and the keeper is
    /// not an outfield lane blocker — the old code charged the keeper's body against the lane on top of the
    /// chance the conversion curve already prices, so an open central goal was worth less than a covered
    /// corner and a clear in-box chance lost to a 96 % backward recycle.
    ///
    /// AIB-07a (audit 2026-11-23, finding 1): keeping the keeper out of the <i>outfield lane</i> left the
    /// three targets scoring exactly the same whenever that lane was equally clear, so strict <c>&gt;</c>
    /// always kept index 0 = <c>-cornerZ</c> and the shot went straight at the side the keeper was covering
    /// (audit images 1–2). Each target now also pays one keeper-coverage term — the share of the target the
    /// keeper's believed lateral reach covers over the ball's flight (<see
    /// cref="ActionValueScale.KeeperCoveragePenalty"/>). It is a single term, not a second save model, and it
    /// is an expected-value reduction: it lands inside <c>bestShotValue</c>, i.e. the candidate's
    /// <see cref="ValueTerms.Gain"/>, so the trace still satisfies <c>utility = gain + preference</c> exactly
    /// (probability 1, loss 0). The keeper is read from perception only — believed position, its uncertainty
    /// and the scouted sprint band; his velocity is deliberately not read as a dive intent.
    /// </summary>
    private void AddShotCandidate(in FootballerObservation obs, float distToGoal, Vector3 oppGoalCenter)
    {
        if (distToGoal >= ActionValueScale.ShotRangeMeters) return;
        // A9.6d: the body is still recovering from the last strike — the rebound of a parry (which comes
        // straight back at the shooter) is not a second shot.
        if (_shotCooldown > 0f) return;

        PlayerTendencies tendencies = obs.SelfTendencies;
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);

        // A9.6d: how much goal the shooter can actually see. A shot from the byline subtends a sliver; the
        // same distance centrally sees the whole mouth. The execution layer prices the physical launch but
        // cannot know this geometry, so the value must.
        float goalMouthAngle = GoalMouthAngleDegrees(selfXZ, oppGoalCenter);

        // AIB-07a: find the one perceived goalkeeper the finish is measured against. One index loop, nearest
        // to the goal centre, lowest player id on an exact tie so the choice cannot depend on the perception
        // list's order. This is perception data only: the believed position's Z, the sighting's uncertainty
        // and the scouted sprint band. Nothing reads the live attributes or treats velocity as a dive.
        bool keeperVisible = false;
        int keeperId = int.MaxValue;
        float keeperZ = 0f;
        float keeperReach = 0f;
        float nearestKeeperGoalDistance = float.PositiveInfinity;
        Vector2 goalCentreXZ = new(oppGoalCenter.X, oppGoalCenter.Z);
        for (int index = 0; index < obs.VisiblePlayers.Count; index++)
        {
            PerceivedEntity entity = obs.VisiblePlayers[index];
            if (entity.Team == obs.SelfTeam) continue;
            if (entity.Role != TacticalRole.Goalkeeper) continue;

            Vector2 keeperXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            float goalDistance = Vector2.Distance(keeperXZ, goalCentreXZ);
            if (keeperVisible && (goalDistance > nearestKeeperGoalDistance ||
                                  (goalDistance == nearestKeeperGoalDistance && entity.PlayerId > keeperId)))
            {
                continue;
            }

            keeperVisible = true;
            keeperId = entity.PlayerId;
            nearestKeeperGoalDistance = goalDistance;
            keeperZ = keeperXZ.Y;

            // Flight of a placed shot to the goal line, the keeper's scouted sprint band converted to lateral
            // dive distance over that flight (capped), plus the bounded doubt in where the sighting puts him.
            float flightSeconds = distToGoal / ActionValueScale.KeeperShotTravelMetersPerSecond;
            float lateralReach = MathF.Min(
                entity.Capability.MaxSprintSpeed * ActionValueScale.KeeperLateralDiveShare * flightSeconds,
                ActionValueScale.KeeperLateralDiveMaxMeters);
            float uncertainty = MathF.Max(0f, entity.PositionUncertaintyMeters);
            keeperReach = lateralReach + MathF.Min(uncertainty, ActionValueScale.KeeperUncertaintyCapMeters);
        }

        // AIB-05: evaluate all three finishes and keep the clearest. The lane is measured on outfield
        // players only: the keeper is the AIB-07a coverage term below, and the conversion curve's own save
        // risk is not priced here, so counting him as an outfield lane blocker too would be a duplicate.
        float cornerZ = _pitch.HalfGoalWidth - ShotCornerInsetMeters;
        // AIB-07a: the tie-break order prefers the far post on (near-)ties — the corner on the opposite side
        // of the goal from the shooter. With index 0 first and a strict `>`, an exact tie (no keeper in the
        // picture, or two equally covered corners) keeps the far post; a central carrier (selfZ == 0) keeps
        // -cornerZ, the old deterministic default. That is also why a short-flight shot picks the far corner
        // without a distance constant: over a few metres of flight the keeper cannot cover either corner, so
        // the tie resolves to the far one.
        float farPostZ = selfXZ.Y >= 0f ? -cornerZ : cornerZ;
        float bestShotValue = float.NegativeInfinity;
        Vector3 shotTarget = new(oppGoalCenter.X, 1.0f, 0f);
        for (int index = 0; index < 3; index++)
        {
            float targetZ = index switch { 0 => farPostZ, 1 => 0f, _ => -farPostZ };
            Vector3 target = new(oppGoalCenter.X, 1.0f, targetZ);
            float laneClearance = EvaluateLaneClearance(
                selfXZ, new Vector2(target.X, target.Z), obs.VisiblePlayers, obs.SelfTeam,
                excludeGoalkeeper: true);
            float value = ActionValueScale.ShotValue(distToGoal, laneClearance, goalMouthAngle);
            // AIB-07a: one keeper term, paid once and inside the gain — the expected-value reduction for the
            // share of this target the keeper's reach covers.
            if (keeperVisible)
                value -= ActionValueScale.KeeperCoveragePenalty * KeeperCoverage01(targetZ, keeperZ, keeperReach);
            if (value > bestShotValue)
            {
                bestShotValue = value;
                shotTarget = target;
            }
        }

        // L9: SafeNormalize, not a raw Normalize — a shooter standing exactly on the aim point would otherwise
        // put a NaN direction into the command (measure-zero, but every other aim in this file is guarded).
        Vector2 shotDir2D = SimMath.SafeNormalize(
            new Vector2(shotTarget.X - obs.SelfPosition.X, shotTarget.Z - obs.SelfPosition.Z),
            new Vector2(obs.AttackingDirectionX, 0f));

        // Shooting appetite: a striker (0.85) pulls the trigger where a defender (0.20) lays it off.
        float shotPreference = (tendencies.ShootingAppetite01 - PlayerTendencyBaselines.ShootingAppetite) * 0.40f;
        // Risk tolerance: the more risk-tolerant player takes on the low-percentage shot from range.
        if (distToGoal > 17.0f)
            shotPreference += (tendencies.RiskTolerance01 - PlayerTendencyBaselines.RiskTolerance) * 0.30f;
        float shotUtility = bestShotValue + shotPreference;

        var shotCmd = new FootballerCommand(
            shotDir2D, shotDir2D, false, false, false, false, false, Vector3.Zero,
            KickIntentType.Shot,
            new Vector3(shotDir2D.X, 0f, shotDir2D.Y),
            // A9.6d: a placed shot band that stays under the bar. Execution's ShotTargetHeight puts the old
            // 0.80-0.90 band at 2.69-3.10 m against a 2.44 m bar, so every AI shot was at the top edge or over.
            ActionValueScale.ShotPowerForDistance(distToGoal),
            0f);

        _lastCandidates.Add(new UtilityCandidate("ShootAtGoal", shotUtility, shotCmd)
        {
            TargetXZ = shotTarget,
            Key = new CandidateKey(CandidateKind.Shot, TargetTag: "goal"),
            // AIB-07a: the keeper-coverage reduction is inside the gain (it is an expected-value reduction),
            // so the trace stays utility = gain + preference exactly.
            Terms = new ValueTerms(1f, bestShotValue, 0f, shotPreference)
        });
    }

    /// <summary>
    /// AIB-07a: share of one goal target the keeper's believed reach covers, 0–1. A target inside the reach
    /// is fully covered (1); outside it coverage falls linearly to 0 over
    /// <see cref="ActionValueScale.KeeperCoverageFalloffMeters"/>. Deterministic, allocation-free, and used
    /// only to order the three goal targets in <see cref="AddShotCandidate"/>.
    /// </summary>
    private static float KeeperCoverage01(float targetZ, float keeperZ, float keeperReach)
    {
        float gap = MathF.Abs(targetZ - keeperZ) - keeperReach;
        if (gap <= 0f) return 1f;
        return Math.Clamp(1f - gap / ActionValueScale.KeeperCoverageFalloffMeters, 0f, 1f);
    }

    /// <summary>
    /// A9.6d: the angle the goal mouth subtends at the shooter, in degrees. Uses the pitch's real goal width,
    /// so a sliver of goal from the byline is the same number the physics and the rules see.
    /// </summary>
    private float GoalMouthAngleDegrees(Vector2 selfXZ, Vector3 oppGoalCenter)
    {
        Vector2 toNearPost = new Vector2(oppGoalCenter.X, _pitch.HalfGoalWidth) - selfXZ;
        Vector2 toFarPost = new Vector2(oppGoalCenter.X, -_pitch.HalfGoalWidth) - selfXZ;
        if (toNearPost.LengthSquared() < 1e-6f || toFarPost.LengthSquared() < 1e-6f) return 180f;

        float cosine = Math.Clamp(
            Vector2.Dot(Vector2.Normalize(toNearPost), Vector2.Normalize(toFarPost)), -1f, 1f);
        return MathF.Acos(cosine) * (180f / MathF.PI);
    }

    // ---------------------------------------------------------------------------------------------
    // Passes: to feet, into a run region, a raised alternative where the ground line is blocked.
    // ---------------------------------------------------------------------------------------------

    /// <summary>One pass variant: how it leads the receiver, what it is called and who asked for it.</summary>
    private readonly record struct PassOption(
        Vector2 LeadDirection,
        float LeadMeters,
        float MaxLeadMeters,
        float MinPower01,
        KickIntentType PassType,
        string Tag,
        bool HumanCaller,
        PassArrivalPromise Promise = default,
        // A8.1: the combination this pass would start, if any, as a preference on the action's score.
        // Zero for every pass that is not a combination (and for every decision with combinations off).
        float CombinationPreference = 0f,
        // AIB-02c: whether the receiver has actually committed to the run the lead assumes — an announced
        // meeting region, a promise, or a human who called for the ball. A feet pass is by definition not a
        // promise, so it plans with the planner's uncommitted hedge (PassPlanner.UncommittedLeadMaxMeters);
        // an unadvertised ToRun gets the same hedge plus its own small extra lead.
        bool ReceiverCommittedToRun = true,
        // AUD-P2 / Fix 1.2 (2026-11-23): which progression the pass preference is scored on. A run or
        // through ball is played into the space the runner is heading for, so its progression is the plan's
        // <b>aim point</b> relative to the carrier (<see cref="TryAddGroundPass"/> computes it once the plan
        // exists). A ball to feet keeps the receiver's own ground. Both variants used to read the receiver's
        // feet, so a through ball aimed 12 m past a runner earned exactly the same progress and directness
        // preference as the same ball rolled to his feet — the audit's ToRun-loses-to-ToFeet inversion.
        bool ProgressFromTarget = false,
        // AIB-11 (audit 2026-11-23, finding 6) / 2026-11-24 owner-review correction: the arrival-pace floor,
        // in m/s, a promised pass is timed with. Passed straight into
        // <see cref="PassPlanner.PlanGroundPassToRegion"/>'s <c>minArrivalSpeedMetersPerSecond</c>, which was
        // the dead parameter the AIB-11 fix should have used: a firm *pace* lets the timing solve place the
        // ball near the meet, where a crisp *range* floor struck it seconds early. Zero (the default) means
        // "no pace floor", the planner's pre-AUD-P5 behaviour, and is what every unpromised pass uses.
        float MinArrivalSpeedMetersPerSecond = 0f);

    /// <summary>
    /// Every feasible pass the carrier can play (A4.1), scored with A4.2's success model.
    ///
    /// Two bounds: a cheap pre-rank decides which receivers get a trajectory planned
    /// (<see cref="AiRuntimeConfig.MaxPassTargets"/>), and at most <see cref="MaxLoftedCandidates"/>
    /// raised alternatives are offered per decision.
    /// </summary>
    private void AddPassCandidates(
        in FootballerObservation obs,
        Vector2 ballXZ,
        Vector2 selfXZ,
        float offsideLineProjX,
        bool underHeavyPressure)
    {
        // A call is this team's instruction only. An opponent's call must not release this carrier's
        // normal pass delay (audit: identical away observations generated a pass only when home called).
        bool humanCalled = _communication?.IsActiveCallFor(obs.SelfTeam) == true;
        // Tempo (team): a high-tempo side releases the ball sooner after receiving it.
        float passTriggerSeconds = Math.Clamp(0.45f - 0.30f * (obs.TeamProfile.Tempo01 - 0.55f), 0.20f, 0.65f);
        // AUD-P6 / Fix 5.1 (2026-11-23): the tempo delay is kept for routine, lateral and safe passes —
        // it is the calibrated release lever and is neither deleted nor lowered. What changes is that a
        // *penetrative* ball is evaluated on the first touch instead of being suppressed with everything
        // else: a promised/advertised run, the carrier-initiated ToSpace pass (AUD-P3), and a plain pass
        // whose geometric aim point would already be a line break (gated cheaply below, before planning).
        // The alternative — a receiver taking a settling touch while a wide-open runner goes — is the
        // audit's Capture 1: the first touch generated zero pass candidates while the carry bonus won.
        //
        // The penetrative window is *scaled*, not zero (see PenetrativeReleaseScale): a zero lockout was
        // tried first and deleted the AUD-F18 tempo trait (every tempo released on tick 1), so a
        // penetrative ball waits a fraction of the team's own routine trigger instead.
        bool tempoReady = _timeOnBall >= passTriggerSeconds || underHeavyPressure || humanCalled;
        bool penetrativeReady = _timeOnBall >= passTriggerSeconds * PenetrativeReleaseScale;
        if (!tempoReady && !penetrativeReady) return;
        bool penetrativeOnly = !tempoReady;

        RankPassTargets(obs, ballXZ, offsideLineProjX, humanCalled);

        // A8.2: the shallow search examines the best few first actions from the carrier's own picture and
        // fills the proposal table before the pass candidates are priced. Receivers it did not propose for
        // keep A8.1's heuristic trigger below, so the search is a bounded improvement on the best options
        // rather than a replacement that silences the rest of the pass set.
        //
        // AUD-P6 / Fix 5.1: the search stays behind the tempo gate. It is the expensive branch (a bounded
        // tree of continuations, each a fresh plan and estimate) and a first-touch release is a one-touch
        // ball, not a combination the carrier has had time to set up; making it first-touch too would spend
        // the A8.2 budget on a decision that is by definition taken before the touch settles.
        if (!penetrativeOnly && _ai.EnableCombinations && _ai.EnableShallowSearch)
            RunCombinationSearch(obs, ballXZ, offsideLineProjX);

        int loftedBudget = MaxLoftedCandidates;
        // AIB-02e: under heavy pressure the ordinary ground set falls back to the delivery floor. The
        // emergency release exists because the alternative is a tackle, so filtering every pressured outlet
        // at the raised ordinary floor would re-create the report's "carrier does nothing" defect through a
        // different gate.
        float groundCompletionFloor = underHeavyPressure
            ? MinViableDeliveryCompletion01
            : MinViableCompletion01;
        for (int index = 0; index < _passTargetCount; index++)
        {
            if (!TryGetVisibleEntity(obs, _passTargetIds[index], out PerceivedEntity mate)) continue;
            AddPassesToMate(
                obs, mate, ballXZ, selfXZ, offsideLineProjX, humanCalled, groundCompletionFloor,
                penetrativeOnly, ref loftedBudget);
        }

        // M1 / AUD-P6: a settling decision still builds no *routine* pass set. The pre-rank above was
        // filled only to choose which receivers get a penetrative probe, and the recycle filter reads this
        // buffer as "the ordinary pass set already offered him" (WasOfferedAsPassTarget) — so restoring the
        // empty state keeps M1's "a carrier who has just collected the ball still has his safe ball
        // backwards" property exactly. A penetrative ball that was actually offered is a different delivery
        // from the backward recycle (forward grass vs a ball to the man's feet), so it must not filter it.
        if (penetrativeOnly) _passTargetCount = 0;
    }

    /// <summary>
    /// Adds the feet and run-region variants for one receiver, plus the carrier-initiated space ball
    /// (AUD-P3) and its raised alternative.
    ///
    /// <paramref name="penetrativeOnly"/> is AUD-P6 / Fix 5.1's tempo answer: when the carrier is inside the
    /// routine pass delay only the high-value penetrative balls are built (a promised/advertised run, the
    /// new <c>ToSpace</c> probe and — cheaply pre-gated on pure geometry — a line-breaking feet ball). The
    /// routine feet pass, the uncommitted sprint ball, the combination proposal and the raised alternative
    /// keep waiting, which is what preserves the calibrated tempo behaviour.
    /// </summary>
    private void AddPassesToMate(
        in FootballerObservation obs,
        in PerceivedEntity mate,
        Vector2 ballXZ,
        Vector2 selfXZ,
        float offsideLineProjX,
        bool humanCalled,
        float groundCompletionFloor,
        bool penetrativeOnly,
        ref int loftedBudget)
    {
        bool isHumanCaller = humanCalled && mate.PlayerId == _communication!.HumanCallForPassPlayerId;
        if (!IsPassTargetEligible(obs, mate, ballXZ, offsideLineProjX, humanCalled, out float forwardMeters, out _))
            return;

        Vector2 mateVelocityXZ = new(mate.EstimatedVelocity.X, mate.EstimatedVelocity.Z);
        Vector2 mateXZ = new(mate.EstimatedPosition.X, mate.EstimatedPosition.Z);
        float rawDistance = Vector2.Distance(ballXZ, mateXZ);

        bool anyGroundPass = false;
        bool anyLaneBlocked = false;

        if (isHumanCaller)
        {
            bool playIntoSpace = _communication!.HumanCalledThroughBall;
            // A through ball is played into the space the runner is moving into, so the lead follows the
            // runner's run direction — pushing it along the attacking axis sent sideways runs the ball
            // toward the goal instead of down the line.
            Vector2 leadDirection = mateVelocityXZ.Length() > 0.5f
                ? Vector2.Normalize(mateVelocityXZ)
                : new Vector2(obs.AttackingDirectionX, 0f);
            var callerOption = new PassOption(
                LeadDirection: leadDirection,
                LeadMeters: playIntoSpace ? Math.Clamp(rawDistance * 0.28f, 5f, 14f) : 0f,
                // A human receiver can turn or stop at any moment, so their lead is bounded hard. An
                // unbounded lead "led" a sprinting caller 15+ m upfield and read as a pass at nobody.
                MaxLeadMeters: playIntoSpace ? 10f : 5f,
                // A crisp pass keeps the flight (and therefore the unpredictable lead) short without
                // rocketing a short pass at their shins.
                MinPower01: Math.Clamp(0.30f + rawDistance / 40f, 0.35f, 0.75f),
                PassType: playIntoSpace ? KickIntentType.ThroughPass : KickIntentType.GroundPass,
                Tag: playIntoSpace ? "Through" : "ToFeet",
                HumanCaller: true,
                // AUD-P2: a caller's through ball is a ball into space, so it is scored on where it is aimed.
                ProgressFromTarget: playIntoSpace);
            anyGroundPass = TryAddGroundPass(
                obs, mate, callerOption, ballXZ, selfXZ, forwardMeters, groundCompletionFloor, out anyLaneBlocked);
        }
        else
        {
            // A8.1/A8.2: what combination, if any, this pass could start. Computed once per receiver so the
            // candidate that wins publishes exactly the contract it was priced with — and only a winning
            // pass creates a contract, never every pass that was merely considered.
            //
            // A8.2: the shallow search has already proposed for the first actions it examined; those
            // proposals are used as they stand. Every other receiver — and every receiver at all with the
            // search off — falls through to A8.1's heuristic trigger, which is unchanged.
            //
            // AUD-P6: on a first touch no contract is proposed. A one-two's return region and arrival are
            // set up by a pass the carrier has already made; inside the tempo delay the ball is being
            // released as a one-touch, penetrative act, not as the first half of a combination, and the
            // contract machinery must not publish one from it. (A ToSpace ball never publishes a contract
            // either — NoteDecision's winning-tag guard covers the full pass set, where a proposal may have
            // been formed for the same receiver before the space candidate was added.)
            float combinationPreference = 0f;
            if (!penetrativeOnly && _ai.EnableCombinations)
            {
                if (!TryGetCombinationProposal(mate.PlayerId, out CombinationProposal proposal) &&
                    TryBuildCombinationProposal(obs, mate, ballXZ, selfXZ, offsideLineProjX, rawDistance,
                        out proposal))
                {
                    RememberCombinationProposal(proposal);
                }

                if (TryGetCombinationProposal(mate.PlayerId, out proposal))
                    combinationPreference = CombinationInitiationBonus * proposal.Confidence01;
            }

            bool feetBlocked = false;
            // AUD-P6: the routine ball to feet keeps the tempo delay. The one exception is the cheap
            // geometric line-break gate: a feet pass whose own aim point already breaks a line is exactly
            // the penetrative ball the fix is about, so it may be evaluated on the first touch — the check
            // is passed to the planner and the pass model, which still price the real ball.
            if (!penetrativeOnly || IsFirstTouchLineBreak(obs, ballXZ, mateXZ))
            {
                // AIB-02c: a ball to feet is not a promise. Planned with the uncommitted hedge, the receiver
                // is frozen at FeetPassMaxLeadMeters (2.5 m) instead of the planner refusing every receiver
                // who happens to be jogging away from the ball — the report's ball aimed metres past a man
                // who had no reason to keep running.
                var feet = new PassOption(
                    Vector2.Zero, 0f, FeetPassMaxLeadMeters, 0f, KickIntentType.GroundPass, "ToFeet", false,
                    CombinationPreference: combinationPreference, ReceiverCommittedToRun: false);
                anyGroundPass = TryAddGroundPass(
                    obs, mate, feet, ballXZ, selfXZ, forwardMeters, groundCompletionFloor, out bool blocked);
                feetBlocked = blocked;
            }

            if (TryBuildRunPassOption(obs, mate, offsideLineProjX, out PassOption run))
            {
                // AUD-P6: on a first touch only a *promised* run ball is released — an advertisement or a
                // one-two region is a team-mate's commitment, which is exactly the penetrative ball the
                // delay was suppressing. The uncommitted sprint variant hedges its lead to 1.5 m and waits
                // for the tempo, like the feet pass it resembles.
                if (!penetrativeOnly || run.Promise.IsPresent)
                {
                    // A8.1: the run-region variant is the same pass, so it earns the same initiation
                    // preference (zero on a first touch, by the no-contract rule above).
                    run = run with { CombinationPreference = combinationPreference };
                    anyGroundPass |= TryAddGroundPass(
                        obs, mate, run, ballXZ, selfXZ, forwardMeters, groundCompletionFloor, out bool runBlocked);
                    feetBlocked |= runBlocked;
                }
            }

            // AUD-P3: the carrier-initiated ball into the grass ahead of this receiver. It is a different
            // delivery from the ball to his feet — and from the raised alternative, which is aimed at the
            // receiver's own position — so it is offered in addition to them. It deliberately does not set
            // anyGroundPass: that flag is the raised alternative's gate ("no ground ball to the receiver
            // survived"), and a ball into the grass ahead does not open the route to his feet.
            if (TryBuildSpacePassOption(obs, mate, ballXZ, offsideLineProjX, out PassOption space))
            {
                _ = TryAddGroundPass(
                    obs, mate, space, ballXZ, selfXZ, forwardMeters, groundCompletionFloor, out _);
            }

            anyLaneBlocked = feetBlocked;
        }

        // The raised alternative is offered when the ground route is physically blocked and no ground
        // pass to this receiver survived. It is priced on its own flight, never on a ground flight time,
        // and it is aimed at the meeting region the runner announced when the team has heard one (A5.3).
        //
        // AUD-P6: it is also part of the *routine* set and keeps the tempo delay. A first-touch release is a
        // ground ball by design (a promised run, the space probe, or a line-breaking feet pass); a lofted
        // fallback is the settling player's alternative, not a one-touch penetrative act.
        if (!penetrativeOnly && anyLaneBlocked && !anyGroundPass && loftedBudget > 0 && !isHumanCaller)
        {
            Vector2 raisedTarget = mateXZ;
            RunAdvertisement advert = default;
            bool announced = false;
            if (_communication is not null)
            {
                announced = _communication.Runs.TryGetAdvertisement(
                                obs.SelfTeam, mate.PlayerId, obs.Tick, out advert) &&
                            advert.MeetTick > obs.Tick;
            }

            if (announced)
            {
                raisedTarget = advert.MeetXZ;
            }
            else
            {
                // AIB-02c: an unled raised ball used to aim at the receiver's CURRENT position while the
                // ball hangs for 1.5–2.5 s, which is a ball to where he was. Without a promise the honest
                // compromise is his own velocity over the modelled hang, bounded — not zero and not his
                // whole flight-time lead.
                raisedTarget = LeadRaisedTarget(obs, mate, ballXZ, mateXZ);
            }

            if (AddLoftedPass(obs, mate, ballXZ, raisedTarget)) loftedBudget--;
        }
    }

    /// <summary>
    /// AIB-02c: where an unled raised ball should be aimed — the receiver's current position plus his own
    /// velocity over the flight the raised model expects for that ball, capped by
    /// <see cref="LoftedUncommittedLeadMaxMeters"/>. The hang time is estimated at his current position
    /// (first order: the lead only has to be right to within a metre or so, and re-estimating it after the
    /// lead would make the aim depend on its own output). No opponent data and no promise are used; a
    /// stationary receiver is aimed at his feet exactly as before.
    /// </summary>
    private Vector2 LeadRaisedTarget(
        in FootballerObservation obs,
        in PerceivedEntity mate,
        Vector2 ballXZ,
        Vector2 mateXZ)
    {
        Vector2 velocity = new(mate.EstimatedVelocity.X, mate.EstimatedVelocity.Z);
        float speed = velocity.Length();
        if (speed < 0.5f) return mateXZ;

        float lane = EvaluateLaneClearance(ballXZ, mateXZ, obs.VisiblePlayers, obs.SelfTeam);
        LoftedPassTerms nominal = _passSuccess.EstimateLofted(obs, mate, Vector2.Distance(ballXZ, mateXZ), lane);
        if (!nominal.Feasible) return mateXZ;

        float leadLength = MathF.Min(speed * nominal.FlightTimeSeconds, LoftedUncommittedLeadMaxMeters);
        Vector2 target = mateXZ + (velocity / speed) * leadLength;
        target.X = Math.Clamp(target.X, -_pitch.HalfLength + 1f, _pitch.HalfLength - 1f);
        target.Y = Math.Clamp(target.Y, -_pitch.HalfWidth + 1f, _pitch.HalfWidth - 1f);
        return target;
    }

    /// <summary>
    /// Builds the pass into the runner (A4.1's <c>ToRun</c>), in two forms (A5.3).
    ///
    /// When the team has heard this receiver advertise a run, the option is that region and that arrival:
    /// the ball is timed to where he said he will be, and the pass model prices the promise instead of
    /// guessing a lead from his velocity. Nothing is added on top of the advertised region — a ten-metre
    /// push past it would be exactly the blindness A5.3 removes.
    ///
    /// Without an advertisement the pre-A5.3 variant stands, tightened by AIB-02c: a genuine run (a
    /// receiver <b>sprinting</b>, not coming back, with room ahead) led by
    /// <see cref="UncommittedThroughLeadMeters"/> along his run and planned with the planner's uncommitted
    /// hedge. It is kept because most off-ball players have no advertised region at any given instant — the
    /// run set is bounded per duty — and a side that only ever passed to announced runs would stop playing
    /// into space entirely. What it no longer is: a full <see cref="ThroughLeadMeters"/> push on top of the
    /// velocity x flight lead, up to <see cref="RunPassMaxLeadMeters"/> (25 m), for a man who promised
    /// nothing.
    ///
    /// <b>AIB-11 (audit 2026-11-23, finding 6), corrected by the 2026-11-24 owner review (item B).</b> Pre-fix
    /// all three variants passed <c>MinPower01: 0f</c>, so the <i>promised</i> variant — whose plan is the
    /// timed-region planner — could only be as firm as the meeting time demanded: an advertised run 2 s away
    /// produced a 0.25–0.35-power roller whose flight the lane test then hard-rejected, exactly the audit's
    /// "still fires slow rollers and gets blocked" finding. The first fix forced <see cref="PassPlanner.MinCrispPower01"/>
    /// (the 9.0 m/s crisp <i>range</i> floor) onto every path, which over-corrected: measured on the review's
    /// geometry, a normal advertised run 16–20 m away with a meet 2.0–2.3 s away was struck to arrive 1.15–1.30 s,
    /// so its promise mistiming was ≈0.63 s, its timing term 0 and its completion ≈0.02 — rejected as
    /// `low-completion`, i.e. the run ball was starved out by the very floor meant to serve it.
    ///
    /// <para><b>2026-11-24 review correction, second cut:</b> the promised paths (advertised run, one-two
    /// return) now choose their power with <see cref="ChoosePromisedRunPower"/>: the <b>crisp floor</b> is
    /// kept while it is on time, and the stated <b>arrival pace</b>
    /// (<see cref="RunPassMinArrivalSpeedMetersPerSecond"/>, 6.0 m/s) is used only when the crisp floor would
    /// arrive more than the promise's own timing tolerance early — the review's starved far-meet case. The
    /// literal "paced floor always" line was measured and refined: for a <i>short</i> advertised meet the
    /// paced floor strikes <b>gentler</b> than the crisp one (11.2 m ball, 1.02 s meet: 0.55 power / 1.00 s
    /// against 0.60 / 0.93 s), so the ball is later and the lane test can reject it — the slow-roller defect
    /// AIB-11 exists to remove, in miniature; the same change flipped the `box-cross` passage and tripped the
    /// protected A3.4 off-ball-target gate at 4.93 m. Keeping the crisp ball when it is on time also keeps the
    /// review's fix product-shaped: only the balls that were actually starved change.
    ///
    /// The remaining over-run of a paced ball is refused by the one shared roll-out criterion
    /// (<see cref="RolledPastPromiseMeters"/> in <see cref="TryAddGroundPass"/>), which is stronger than any
    /// power floor. The uncommitted variant is <c>MinPower01: 0f</c>: it has no promise, its plan is the untimed
    /// <see cref="PassPlanner.PlanGroundPass"/> solve (not <see cref="PassPlanner.PlanGroundPassToRegion"/>), so
    /// a floor measured at the furthest legal meeting point only over-firms a ball whose meeting is unknown —
    /// that was the review's "far-meeting floor" defect. Its lead hedge, the offside gate and the commitment
    /// rules are untouched.</para>
    /// </summary>
    private bool TryBuildRunPassOption(
        in FootballerObservation obs,
        in PerceivedEntity mate,
        float offsideLineProjX,
        out PassOption option)
    {
        option = default;
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);

        if (_communication is not null &&
            _communication.Runs.TryGetAdvertisement(obs.SelfTeam, mate.PlayerId, obs.Tick, out RunAdvertisement advert) &&
            advert.MeetTick > obs.Tick)
        {
            float arriveInSeconds = advert.TicksUntilMeeting(obs.Tick) * SimConstants.FixedDeltaTime;
            ChoosePromisedRunPower(
                ballXZ,
                advert.MeetXZ,
                arriveInSeconds,
                advert.MeetRadiusMeters,
                out float runMinPower01,
                out float runMinArrivalSpeed);
            option = new PassOption(
                LeadDirection: Vector2.Zero,
                LeadMeters: 0f,
                MaxLeadMeters: RunPassMaxLeadMeters,
                // 2026-11-24 review correction (AIB-11): the crisp floor while it is on time, the paced floor
                // only when the crisp ball would be more than the promise's tolerance early (see the helper).
                MinPower01: runMinPower01,
                MinArrivalSpeedMetersPerSecond: runMinArrivalSpeed,
                PassType: KickIntentType.ThroughPass,
                Tag: RunPassTag,
                HumanCaller: false,
                Promise: new PassArrivalPromise(
                    advert.MeetXZ,
                    arriveInSeconds,
                    advert.MeetRadiusMeters,
                    advert.Promise01(obs.Tick)),
                // AUD-P2: a promised ball into a run is scored on the announced region, not on the feet.
                ProgressFromTarget: true);
            return true;
        }

        // A8.1: a one-two contract is its own promise. The initiator named the return region and the meeting
        // tick when he made the pass, and there is no advertisement to read, so the ball is timed to that
        // region instead of to his current feet. A third-player contract deliberately does not do this: its
        // continuation belongs to the third man's advertised run, so if that announcement is gone the receiver
        // abandons the contract rather than playing the ball into a region nobody is running into.
        if (_ai.EnableCombinations && obs.Combination.IsReceiver &&
            obs.Combination.Kind == CombinationKind.OneTwo &&
            obs.Combination.ContinuationTargetId == mate.PlayerId &&
            obs.Combination.ExpectedArrivalTick > obs.Tick + 2)
        {
            float arriveInSeconds = (obs.Combination.ExpectedArrivalTick - obs.Tick) * SimConstants.FixedDeltaTime;
            ChoosePromisedRunPower(
                ballXZ,
                obs.Combination.ReturnXZ,
                arriveInSeconds,
                obs.Combination.ReturnRadiusMeters,
                out float oneTwoMinPower01,
                out float oneTwoMinArrivalSpeed);
            option = new PassOption(
                LeadDirection: Vector2.Zero,
                LeadMeters: 0f,
                MaxLeadMeters: RunPassMaxLeadMeters,
                // 2026-11-24 review correction (AIB-11): the same crisp-while-on-time / paced-when-starved rule
                // as the advertised path; the return region is a promise too, so it is timed the same way and
                // rolled out the same way.
                MinPower01: oneTwoMinPower01,
                MinArrivalSpeedMetersPerSecond: oneTwoMinArrivalSpeed,
                PassType: KickIntentType.ThroughPass,
                Tag: RunPassTag,
                HumanCaller: false,
                Promise: new PassArrivalPromise(
                    obs.Combination.ReturnXZ,
                    arriveInSeconds,
                    obs.Combination.ReturnRadiusMeters,
                    obs.Combination.Promise01(obs.Tick)),
                // AUD-P2: the one-two's return region is a run target, so the progression follows the region.
                ProgressFromTarget: true);
            return true;
        }

        if (!RunsIntoSpace(obs, mate, offsideLineProjX)) return false;

        Vector2 mateVelocityXZ = new(mate.EstimatedVelocity.X, mate.EstimatedVelocity.Z);
        // AIB-02c: a ball into space needs a man actually sprinting into it when nobody has promised the run.
        // RunsIntoSpace's floor (1.5 m/s) is a jog; a lead that assumes a jog keeps going for a whole flight
        // is exactly the guess the audit found punishing the side, so the uncommitted variant is stricter.
        if (mateVelocityXZ.Length() < MinUncommittedRunSpeed) return false;

        option = new PassOption(
            LeadDirection: Vector2.Normalize(mateVelocityXZ),
            LeadMeters: UncommittedThroughLeadMeters,
            MaxLeadMeters: PassPlanner.UncommittedLeadMaxMeters,
            // 2026-11-24 review correction (AIB-11): back to 0. This variant has no promise and its plan is the
            // untimed `PlanGroundPass` solve, so `PlanGroundPassToRegion` — and its pace parameter — is not even
            // used here. The old crisp floor was measured at the furthest legal meeting (current distance + both
            // lead bounds), which over-firmed a ball whose meeting is unknown; a far-meeting floor on an untimed
            // solve is a guess paid as pace. The lead hedge and the sprint gate are unchanged.
            MinPower01: 0f,
            PassType: KickIntentType.ThroughPass,
            Tag: RunPassTag,
            HumanCaller: false,
            ReceiverCommittedToRun: false,
            // AUD-P2: an uncommitted run ball is still played into space ahead of the runner, so its
            // progression is the aim point the planner selects, not the runner's current feet.
            ProgressFromTarget: true);
        return true;
    }

    /// <summary>
    /// AIB-11 (audit 2026-11-23, finding 6) / 2026-11-24 owner-review correction: chooses the minimum power
    /// mode for a <b>promised</b> run ball — the advertised run and the one-two return.
    ///
    /// <para>The review measured a starved ball: the crisp 9.0 m/s floor struck a normal 16–20 m advertised run
    /// (meet 2.0–2.3 s away) to arrive 1.15–1.30 s, so the promise's mistiming was ≈0.63 s, its timing term 0
    /// and its completion ≈0.02 — rejected `low-completion`. The fix is an arrival-<b>pace</b> floor
    /// (<see cref="RunPassMinArrivalSpeedMetersPerSecond"/>) that lets the timing solve place the ball near the
    /// meet instead of a second early.</para>
    ///
    /// <para><b>Why not always paced (the 2026-11-24 refinement).</b> A pace floor is a <i>lower</i> power floor,
    /// so for a <b>short</b> meeting it strikes gentler than the crisp floor: an 11.2 m ball with a 1.02 s meet
    /// plans at 0.55 power / 1.00 s flight where the crisp floor planned 0.60 / 0.93 s. The slower, longer ball
    /// is what the lane test then rejects — the slow-roller defect AIB-11 exists to remove, in miniature — and
    /// the changed winner flipped the `box-cross` passage enough to trip the protected A3.4 off-ball-target
    /// gate. So the crisp floor is kept whenever it is <b>on time</b>: its flight for this distance is read from
    /// the planner's own table and compared with the meet, and the crisp floor is used unless it arrives more
    /// than the promise's own <see cref="PassSuccessModel.PromiseTimingToleranceSeconds"/> early — the only
    /// case where the firm ball is the starved one. The threshold is that existing promise tolerance, not a new
    /// tuning knob: the same number the estimator charges mistiming against, so plan choice and pricing agree.
    /// The paced mode is the fallback and also the answer when the table has no flight for the crisp floor.</para>
    /// </summary>
    private void ChoosePromisedRunPower(
        Vector2 ballXZ,
        Vector2 targetXZ,
        float arriveInSeconds,
        float meetRadiusMeters,
        out float minPower01,
        out float minArrivalSpeedMetersPerSecond)
    {
        minPower01 = 0f;
        minArrivalSpeedMetersPerSecond = 0f;

        float distance = Vector2.Distance(ballXZ, targetXZ);
        if (!float.IsFinite(distance) || distance <= 0f) return;

        float crispFloor = _passPlanner.MinCrispPower01(distance);
        if (!_passPlanner.TryGetFlightTime(crispFloor, distance, out float crispFlight)) return;

        float tolerance = PassSuccessModel.PromiseTimingToleranceSeconds(meetRadiusMeters);
        bool crispIsOnTime = arriveInSeconds - crispFlight <= tolerance;
        if (crispIsOnTime)
        {
            minPower01 = crispFloor;
            return;
        }

        minArrivalSpeedMetersPerSecond = RunPassMinArrivalSpeedMetersPerSecond;
    }

    /// <summary>
    /// AUD-P3 / Fix 2 (audit 2026-11-23): the <b>carrier-initiated</b> pass into empty onside grass ahead of
    /// a team-mate — the missing builder behind the audit's Capture 4. Until this existed every space pass
    /// was receiver-initiated: an advertised run (<see cref="TryBuildRunPassOption"/>'s promised variant) or
    /// a team-mate already sprinting at <see cref="MinUncommittedRunSpeed"/> (whose uncommitted lead is
    /// hedged to 1.5 m on top of the planner's 2.5 m cap). A carrier could therefore not play a team-mate
    /// into the grass in front of him and trigger the run.
    ///
    /// <b>When.</b> The receiver must be a non-keeper in the middle or attacking third (attack-relative
    /// projection from the team's own third boundary, <c>mateProjX &gt;= -HalfLength / 3</c>), with no
    /// <i>live</i> run advertisement <b>or</b> not already sprinting — a live advertisement on a sprinting
    /// runner is served by the promised <c>ToRun</c> ball, while a receiver who has stopped is exactly who
    /// this pass is for.
    ///
    /// <b>Where.</b> Two probes only (14 m then 8 m ahead along the attack axis, keeping his lane), each
    /// clamped 3 m inside every pitch line and onside to <c>offsideLineProjX - OffsideTactics.MarginMeters</c>.
    /// A probe that is crowded (a believed opponent inside <see cref="SpaceProbeCrowdRadiusMeters"/>, or
    /// <see cref="EvaluateSpaceFreedom"/> under <see cref="SpaceProbeMinFreedom01"/>) is refused with a stated
    /// reason; a probe left less than <see cref="SpaceProbeMinForwardMeters"/> ahead of the receiver after the
    /// clamp is not space at all. The farther probe is tried first because it is the more penetrative ball;
    /// the near one is the bounded fallback.
    ///
    /// <b>Race.</b> The receiver's own <see cref="TimeToReachModel"/> arrival at the probe must beat the
    /// nearest believed opponent's by <see cref="SpaceRaceMarginSeconds"/>. An unreachable estimate is not an
    /// exception and not a NaN: an unreachable receiver refuses the probe, and an unreachable defender is
    /// treated as the horizon itself (the conservative "nobody can be there" bound), which is deterministic.
    ///
    /// <b>Firm ball.</b> The plan is solved to the receiver's real arrival with
    /// <see cref="PassPlanner.MinCrispPower01"/> as the power floor, so the ball arrives earlier than he does
    /// and rolls into the grass in front of him — the audit's core complaint about slow timed rollers.
    ///
    /// <b>Promise.</b> The candidate carries a <see cref="PassArrivalPromise"/> at the probe whose
    /// <c>ArrivalSeconds</c> is the probe plan's <i>own flight time</i>: the pass model's promise timing term
    /// is then exactly 1 (the ball is certain to be there at its own flight time), the promise confidence
    /// carries the race (derived from the measured margin below), and the model does not charge the lead
    /// again — a promised arrival is never double-charged (<c>PassSuccessModel</c>'s A5.3 rule).
    /// <see cref="TryAddGroundPass"/> replans the ball from the promise itself, which returns the same plan
    /// (same fixed target, same crisp floor, same wanted flight), and prices it with
    /// <see cref="PassSuccessModel.Estimate"/> and the shared <c>PassPreference</c> on the aim point.
    ///
    /// <b>AIB-10 (audit 2026-11-23, finding 4): the promise is only allowed if the ball is still in it.</b>
    /// Keeping <c>ArrivalSeconds</c> as the ball's flight blinds the pass model to a ball that has rolled far
    /// past the aim point before the receiver can get there, so the builder measures the ball's own calibrated
    /// roll-out at the receiver's real arrival and refuses the probe once it exceeds
    /// <see cref="PromiseCatchRadiusMeters"/> (the promise region plus one receiving reach). That is
    /// the measured form of
    /// the audit's "ball 0.80 s vs receiver 1.72 s, stopped 29.2 m past" finding; writing
    /// <c>receiverReach.Seconds</c> into <c>ArrivalSeconds</c> was rejected because it re-introduces AUD-P3's
    /// lead double-charge — the receiver's moment is carried on
    /// <see cref="PassArrivalPromise.ContestArrivalSeconds"/> instead, so the pass model's contest is priced at
    /// it while the timing term stays on the ball's own flight.
    ///
    /// <b>2026-11-24 owner-review corrections (A1 and A2).</b> (A1) The promise now states
    /// <see cref="PassArrivalPromise.ContestArrivalSeconds"/> = the receiver's real arrival, so the AIB-10
    /// contest fix is not collapsed back to <c>max(flight, flight)</c> for this candidate. (A2) The roll-out
    /// guard also asks where the <b>ball</b> is at that arrival, not only how far past the aim point it rolled:
    /// the probe is clamped <see cref="SpaceProbePitchMarginMeters"/> inside every line but the ball is not, so
    /// a ball aimed near a line can be out of play before the receiver gets there. A ball out of play is not a
    /// meeting at all, so the probe is refused with <see cref="RejectionSpaceRollsOut"/> — and that check runs
    /// before the roll-out bound, because "out of the pitch" is the stronger physical fact. The roll-out
    /// criterion itself is the shared <see cref="RolledPastPromiseMeters"/> every promised pass uses. The pace
    /// floor, offside clamp, crowd gate and race margin are all untouched.
    /// </summary>
    private bool TryBuildSpacePassOption(
        in FootballerObservation obs,
        in PerceivedEntity mate,
        Vector2 ballXZ,
        float offsideLineProjX,
        out PassOption option)
    {
        option = default;

        // Feasibility mirrors IsPassTargetEligible; kept explicit so a later caller cannot feed the probe an
        // opponent or a keeper.
        if (mate.Team != obs.SelfTeam || mate.PlayerId == obs.SelfId) return false;
        if (mate.Role == TacticalRole.Goalkeeper) return false;
        if (obs.AttackingDirectionX == 0f) return false;

        float mateProjX = mate.EstimatedPosition.X * obs.AttackingDirectionX;
        if (mateProjX < -_pitch.HalfLength / 3f) return false;
        if (mateProjX > OffsideTactics.MaxAttackingProjection(offsideLineProjX))
        {
            Reject(mate.PlayerId, RejectionOffside);
            return false;
        }

        Vector2 mateXZ = new(mate.EstimatedPosition.X, mate.EstimatedPosition.Z);
        if (!SimMath.IsFinite(ballXZ) || !SimMath.IsFinite(mateXZ)) return false;

        Vector2 mateVelocityXZ = new(mate.EstimatedVelocity.X, mate.EstimatedVelocity.Z);
        bool hasForwardDepthAdvert =
            _communication is not null &&
            _communication.Runs.TryGetAdvertisement(
                obs.SelfTeam, mate.PlayerId, obs.Tick, out RunAdvertisement advertisement) &&
            advertisement.MeetTick > obs.Tick &&
            advertisement.Kind == RunKind.InBehind &&
            (advertisement.MeetXZ.X - mateXZ.X) * obs.AttackingDirectionX >= SpaceProbeMinForwardMeters;
        // A committed, already-sprinting forward depth runner is served by the promised ToRun ball; an
        // onside HoldWidth/Pin advert or a receiver who has stopped still gets the space probe behind the line.
        if (hasForwardDepthAdvert && mateVelocityXZ.Length() >= MinUncommittedRunSpeed) return false;

        float maxPitchProjX = _pitch.HalfLength - SpaceProbePitchMarginMeters;
        float laneMarginZ = _pitch.HalfWidth - SpaceProbePitchMarginMeters;

        // Two probes, farthest first: the penetrative ball when the far grass is won, the near
        // fallback when it is not. Under Law 11 the receiver is already verified onside at kick time;
        // spaceXZ itself is clamped only to the pitch boundary so through balls can go behind the line.
        ReadOnlySpan<float> probes = [SpaceProbeFarMeters, SpaceProbeNearMeters];
        foreach (float forwardMeters in probes)
        {
            Vector2 spaceXZ = new(
                mate.EstimatedPosition.X + obs.AttackingDirectionX * forwardMeters,
                Math.Clamp(mate.EstimatedPosition.Z, -laneMarginZ, laneMarginZ));

            if (spaceXZ.X * obs.AttackingDirectionX > maxPitchProjX)
                spaceXZ.X = obs.AttackingDirectionX * maxPitchProjX;

            float forwardGain = (spaceXZ.X - mateXZ.X) * obs.AttackingDirectionX;
            if (forwardGain < SpaceProbeMinForwardMeters)
                continue;

            // Crowded grass is not space: a body in the landing area, or too little room to attack the
            // point, refuses the probe rather than turning this into a hopeful punt.
            float nearestOpponentMeters = NearestOpponentDistanceTo(spaceXZ, obs.VisiblePlayers, obs.SelfTeam);
            float freedom01 = EvaluateSpaceFreedom(spaceXZ, obs.VisiblePlayers, obs.SelfTeam);
            if (nearestOpponentMeters < SpaceProbeCrowdRadiusMeters || freedom01 < SpaceProbeMinFreedom01)
            {
                Reject(mate.PlayerId, RejectionSpaceContested);
                continue;
            }

            // The race, over the one reach model. Both estimates are measured from the same tick and the
            // same reach horizon, so the margin is a like-for-like quantity.
            ReachEstimate receiverReach = TimeToReachModel.ToPoint(
                ReachKinematics.FromEntity(mate), spaceXZ, SpaceRaceHorizonSeconds);
            if (!receiverReach.Reachable)
            {
                Reject(mate.PlayerId, RejectionSpaceRaceLost);
                continue;
            }

            float defenderArrivalSeconds = SpaceRaceHorizonSeconds;
            for (int index = 0; index < obs.VisiblePlayers.Count; index++)
            {
                PerceivedEntity opponent = obs.VisiblePlayers[index];
                if (opponent.Team == obs.SelfTeam) continue;

                ReachEstimate opponentReach = TimeToReachModel.ToPoint(
                    ReachKinematics.FromEntity(opponent), spaceXZ, SpaceRaceHorizonSeconds);
                // An unreachable defender is the horizon itself: nobody in the picture can be there inside
                // the decision's own look-ahead, which is the conservative bound (not "free").
                if (!opponentReach.Reachable) continue;
                if (opponentReach.Seconds < defenderArrivalSeconds)
                    defenderArrivalSeconds = opponentReach.Seconds;
            }

            float raceMarginSeconds = defenderArrivalSeconds - receiverReach.Seconds;
            if (raceMarginSeconds < SpaceRaceMarginSeconds)
            {
                Reject(mate.PlayerId, RejectionSpaceRaceLost);
                continue;
            }

            // The promise confidence is the measured race, not a nominal 1.0: a minimum-margin ball
            // (0.35 s) promises 0.675, a full second's margin or more promises 1.0, and the stated floor
            // never lets the derivation produce a zero-confidence "promise" later. This is the only route
            // the race takes into the pass model (the promise replaces the receiver-arrival term). Built
            // before the plan so the roll-out guards below can read the exact promise the candidate carries.
            float confidence01 = Math.Clamp(
                SpacePromiseConfidenceBase + SpacePromiseConfidenceMarginWeight * Math.Clamp(
                    raceMarginSeconds / SpacePromiseConfidenceMarginScaleSeconds, 0f, 1f),
                SpacePromiseConfidenceFloor,
                1f);

            float probeDistanceMeters = Vector2.Distance(ballXZ, spaceXZ);
            if (probeDistanceMeters < MinPassDistanceMeters || probeDistanceMeters > MaxPassDistanceMeters)
            {
                Reject(mate.PlayerId, RejectionOutOfRange);
                continue;
            }

            // ai63 follow-up: try the crisp floor first so firm through balls keep their pace; if the crisp
            // ball would leave the pitch or roll past EffectivePromiseCatchRadiusMeters before the runner
            // arrives, fall back to the 6.0 m/s paced mode (matching ChoosePromisedRunPower) instead of
            // self-rejecting behind-the-line probes.
            float minPower01 = _passPlanner.MinCrispPower01(probeDistanceMeters);
            float minArrivalSpeed = 0f;
            PassPlan probePlan = _passPlanner.PlanGroundPassToRegion(
                ballXZ, spaceXZ, receiverReach.Seconds, minPower01);
            bool crispRollsOut = false;
            bool crispRolledPast = false;
            bool crispChecked = probePlan.Feasible && CheckSpaceBallCatchable(
                ballXZ, probePlan, receiverReach.Seconds, probeDistanceMeters,
                out crispRollsOut, out crispRolledPast);
            if (!crispChecked)
            {
                if (crispRollsOut && !crispRolledPast)
                {
                    Reject(mate.PlayerId, RejectionSpaceRollsOut);
                    continue;
                }

                minPower01 = 0f;
                minArrivalSpeed = RunPassMinArrivalSpeedMetersPerSecond;
                PassPlan pacedPlan = _passPlanner.PlanGroundPassToRegion(
                    ballXZ, spaceXZ, receiverReach.Seconds, minPower01, minArrivalSpeed);
                bool pacedRollsOut = false;
                if (pacedPlan.Feasible && CheckSpaceBallCatchable(
                        ballXZ, pacedPlan, receiverReach.Seconds, probeDistanceMeters,
                        out pacedRollsOut, out _))
                {
                    probePlan = pacedPlan;
                }
                else
                {
                    if (!probePlan.Feasible && !pacedPlan.Feasible)
                    {
                        Reject(mate.PlayerId, RejectionUnplannable);
                    }
                    else
                    {
                        bool anyRollsOut = (probePlan.Feasible && crispRollsOut) ||
                                           (pacedPlan.Feasible && pacedRollsOut);
                        Reject(mate.PlayerId, anyRollsOut ? RejectionSpaceRollsOut : RejectionSpaceRolledPast);
                    }
                    continue;
                }
            }

            var promise = new PassArrivalPromise(
                spaceXZ,
                probePlan.FlightTimeSeconds,
                SpacePromiseRadiusMeters,
                confidence01,
                ContestArrivalSeconds: receiverReach.Seconds);

            option = new PassOption(
                LeadDirection: Vector2.Zero,
                LeadMeters: 0f,
                MaxLeadMeters: RunPassMaxLeadMeters,
                MinPower01: minPower01,
                MinArrivalSpeedMetersPerSecond: minArrivalSpeed,
                PassType: KickIntentType.ThroughPass,
                Tag: SpacePassTag,
                HumanCaller: false,
                // The plan's own flight time: the promise is "the ball is in this grass at the instant the
                // plan says it is", which is exact, so the model's timing term is 1 and no lead is charged.
                Promise: promise,
                // AUD-P2: a ball into space is scored on where it is aimed, not on the receiver's feet.
                ProgressFromTarget: true);
            return true;
        }

        return false;
    }

    private bool CheckSpaceBallCatchable(
        Vector2 ballXZ,
        in PassPlan plan,
        float receiverArrivalSeconds,
        float probeDistanceMeters,
        out bool rollsOut,
        out bool rolledPast)
    {
        float ballDistanceAtReceiverArrival = _passPlanner.BallDistanceAtTime(
            plan.Power01, receiverArrivalSeconds);
        Vector2 ballAtReceiverArrival = ballXZ + plan.AimDirectionXZ * ballDistanceAtReceiverArrival;
        rollsOut = MathF.Abs(ballAtReceiverArrival.X) > _pitch.HalfLength ||
                   MathF.Abs(ballAtReceiverArrival.Y) > _pitch.HalfWidth;
        float rolledPastMeters = ballDistanceAtReceiverArrival - probeDistanceMeters;
        rolledPast = rolledPastMeters > EffectivePromiseCatchRadiusMeters(probeDistanceMeters);
        return !rollsOut && !rolledPast;
    }

    /// <summary>
    /// AIB-10 (audit 2026-11-23, finding 4) / AIB-11 (finding 6) / 2026-11-24 owner-review correction: the
    /// <b>one roll-out criterion every promised ground pass is measured by</b> — how many metres past
    /// <see cref="PassArrivalPromise.TargetXZ"/> the rolling ball is at the promise's contest moment
    /// (<see cref="PassArrivalPromise.ContestMomentSeconds"/>: the receiver's real arrival when the builder
    /// states one, otherwise the promised arrival). A positive value means the ball has over-run its region;
    /// the callers refuse the pass once it exceeds the shared <see cref="PromiseCatchRadiusMeters"/>.
    ///
    /// <para>Why the contest moment and not <c>ArrivalSeconds</c>: for the ToSpace promise
    /// <c>ArrivalSeconds</c> is deliberately the ball's own flight (AUD-P3), so the ball is exactly at its aim
    /// point then and a check at that instant always passes — the review's impossible ball rolled ~16 m past by
    /// the time the receiver arrived. The contest moment is the instant the receiver can actually play the
    /// ball, which is the instant the promise is about. For an advertised run or a one-two the two coincide
    /// (the promised arrival is the receiver's), so the same call is the pre-existing check.</para>
    ///
    /// <para>Uses the planner's own calibrated roll (<see cref="PassPlanner.BallDistanceAtTime"/>), the same
    /// table the plan and the lane test use — never a second trajectory. Deterministic and allocation-free:
    /// two table lookups. Returns zero for a non-finite input or an absent promise, so a caller can never
    /// produce a NaN rejection.</para>
    /// </summary>
    public float RolledPastPromiseMeters(in PassPlan plan, Vector2 ballXZ, in PassArrivalPromise promise)
    {
        if (!plan.Feasible || !promise.IsPresent || !SimMath.IsFinite(ballXZ)) return 0f;

        float aimDistance = Vector2.Distance(ballXZ, promise.TargetXZ);
        if (!float.IsFinite(aimDistance)) return 0f;

        float covered = _passPlanner.BallDistanceAtTime(plan.Power01, promise.ContestMomentSeconds);
        if (!float.IsFinite(covered)) return 0f;

        return covered - aimDistance;
    }

    /// <summary>
    /// AUD-P6 / Fix 5.1: the cheap geometric half of "a plain pass to this receiver would break a line",
    /// asked before any trajectory is planned so a first-touch decision can include that one penetrative
    /// ball without waiting out the tempo delay. It is the same <see cref="LineBreakPreference"/> the scorer
    /// will run — on the receiver's current position, which is where a feet pass is aimed, and with the lane
    /// clearance the scorer's target will approximate — and completion is passed as 1 because the question
    /// here is only geometry: the planner and the pass model price the real ball (and its real completion)
    /// immediately afterwards, so this gate can admit a ball but can never score one.
    /// </summary>
    private bool IsFirstTouchLineBreak(
        in FootballerObservation obs,
        Vector2 ballXZ,
        Vector2 mateXZ)
    {
        float lane01 = EvaluateLaneClearance(ballXZ, mateXZ, obs.VisiblePlayers, obs.SelfTeam);
        return LineBreakPreference(
            ballXZ,
            mateXZ,
            obs.AttackingDirectionX,
            obs.VisiblePlayers,
            obs.SelfTeam,
            lane01,
            completionProbability01: 1f) > 0f;
    }

    /// <summary>
    /// The cheap geometric gates every pass target must pass, applied identically by the pre-rank and by
    /// the planner so the two can never disagree about who is a candidate.
    ///
    /// A4.4 removed the blanket "no backward pass in the final third" rule: the value scale now prices a
    /// backward reception by its progression, so a safe recycle simply loses to a better forward option
    /// instead of being forbidden. The offside rule, the distance band and the goalkeeper exclusion stay —
    /// they are feasibility, not policy.
    /// </summary>
    private bool IsPassTargetEligible(
        in FootballerObservation obs,
        in PerceivedEntity mate,
        Vector2 ballXZ,
        float offsideLineProjX,
        bool humanCalled,
        out float forwardMeters,
        out string? refusal)
    {
        forwardMeters = 0f;
        refusal = null;

        bool isHumanCaller = humanCalled && mate.PlayerId == _communication!.HumanCallForPassPlayerId;
        if (mate.PlayerId == obs.SelfId) return false;
        if (mate.Team != obs.SelfTeam || mate.Role == TacticalRole.Goalkeeper) return false;

        float mateProjX = mate.EstimatedPosition.X * obs.AttackingDirectionX;
        // A5.4: one tactical margin, and the same number the run generator keeps — the law's own threshold
        // is the contact snapshot (0.15 m), so a ball played to a man a hand's width inside the line is a
        // guess on the passer's own perception rather than a fact. A human caller is exempt because the
        // request is theirs to make and the law is judged at the contact.
        if (!isHumanCaller && mateProjX > offsideLineProjX - OffsideTactics.MarginMeters)
        {
            refusal = RejectionOffside;
            return false;
        }

        Vector2 mateXZ = new(mate.EstimatedPosition.X, mate.EstimatedPosition.Z);
        float rawDistance = Vector2.Distance(ballXZ, mateXZ);
        if (rawDistance < MinPassDistanceMeters || rawDistance > MaxPassDistanceMeters)
        {
            refusal = RejectionOutOfRange;
            return false;
        }

        // Feasibility, not policy: a ball played into your own goal is not a pass. The line to an outfield
        // target that close to the own goal line would run at the goal if the first touch misses, and
        // neither the planner nor the success model can rule that out. The goalkeeper is exempt: he is the
        // one player standing there on purpose, and he can handle a ball that reaches him.
        if (mate.Role != TacticalRole.Goalkeeper &&
            mateProjX < -_pitch.HalfLength + OwnGoalPassMarginMeters)
        {
            refusal = RejectionOwnGoalMargin;
            return false;
        }

        forwardMeters = (mate.EstimatedPosition.X - obs.SelfPosition.X) * obs.AttackingDirectionX;
        return true;
    }

    /// <summary>Lower bound on the pre-rank of a receiver who has announced a run (A5.3).</summary>
    /// <remarks>
    /// The pre-rank is progression plus standing space, both in roughly [-1, 1]. The bonus is deliberately
    /// smaller than either term: it rescues an announced runner from the <see cref="AiRuntimeConfig.MaxPassTargets"/>
    /// cut, it does not promote him above a genuinely better placed team-mate. With a budget of two and three
    /// eligible receivers it is the difference between the advertisement being read and the runner being
    /// pruned before any trajectory exists, which a regression pins from both sides.
    /// </remarks>
    internal const float AnnouncedRunRankBonus = 0.35f;

    /// <summary>
    /// A7.2: the pre-rank bonus given to the team-mate who asked for a switch. Same job as
    /// <see cref="AnnouncedRunRankBonus"/> — keep the team-mate the call is about inside
    /// <see cref="AiRuntimeConfig.MaxPassTargets"/> so the preference is actually read — and deliberately
    /// the same size, because both are communication that must not be pruned before it is priced.
    /// </summary>
    internal const float SwitchCallRankBonus = 0.35f;

    /// <summary>
    /// Fills the bounded pass-target buffer with the most promising eligible receivers. The rank is
    /// deliberately cheap (progression, the room the receiver stands in, and whether he has announced a
    /// run): it exists to bound the expensive trajectory work, not to make the decision.
    /// </summary>
    private void RankPassTargets(
        in FootballerObservation obs,
        Vector2 ballXZ,
        float offsideLineProjX,
        bool humanCalled)
    {
        _passTargetCount = 0;
        int capacity = Math.Clamp(_ai.MaxPassTargets, 1, _passTargetIds.Length);
        int callerId = humanCalled ? _communication!.HumanCallForPassPlayerId ?? 0 : 0;

        for (int index = 0; index < obs.VisiblePlayers.Count; index++)
        {
            PerceivedEntity mate = obs.VisiblePlayers[index];
            if (!IsPassTargetEligible(obs, mate, ballXZ, offsideLineProjX, humanCalled,
                    out float forwardMeters, out string? refusal))
            {
                if (refusal is not null) Reject(mate.PlayerId, refusal);
                continue;
            }

            Vector2 mateXZ = new(mate.EstimatedPosition.X, mate.EstimatedPosition.Z);
            float space = EvaluateSpaceFreedom(mateXZ, obs.VisiblePlayers, obs.SelfTeam);
            float rank = Math.Clamp(forwardMeters / 20f, -1f, 1f) + space;

            // A5.3: a receiver who has announced a meeting region is not an anonymous target — the ball can
            // be timed to him. A modest bonus keeps him inside the bounded set, so the advertisement is never
            // pruned before it is read. It does not change who *should* get the ball: the pass model and the
            // value scale still decide that, and a crowded or useless announced run is still priced as one.
            bool announcedRun = _communication is not null &&
                                _communication.Runs.TryGetAdvertisement(
                                    obs.SelfTeam, mate.PlayerId, obs.Tick, out _);
            if (announcedRun)
            {
                rank += AnnouncedRunRankBonus;
            }

            // A7.2: a team-mate who asked for the switch is not an anonymous target either. Like the run
            // advertisement's bonus this only rescues him from the bounded pre-rank; the pass model and the
            // value scale still decide whether the ball should go there.
            if (obs.Calls.Kind == TeamCallKind.Switch && obs.Calls.NamedPlayerId == mate.PlayerId)
                rank += SwitchCallRankBonus;

            // A8.3: the far-side target of a switch the carrier recognizes must survive the pre-rank too. The
            // cheap geometry half of the recognition is used here because a trajectory (and its completion
            // probability) does not exist until this budget has chosen who gets planned — the same split the
            // run advertisement and A8.1's continuation target use.
            if (_ai.EnableSwitchRecognition && SwitchRecognition.IsSwitchGeometry(obs, mateXZ, ballXZ))
                rank += SwitchRecognitionRankBonus;

            // A8.1: the first receiver's continuation target must survive the pre-rank too, or the contract
            // would be abandoned before its promised ball was ever priced.
            if (_ai.EnableCombinations && obs.Combination.IsReceiver &&
                obs.Combination.ContinuationTargetId == mate.PlayerId)
            {
                rank += CombinationRankBonus;
            }

            // The human's request is planned whatever its rank: a caller is never silently dropped.
            if (mate.PlayerId == callerId) rank = float.MaxValue;

            InsertPassTarget(mate.PlayerId, rank, capacity);
        }
    }

    /// <summary>
    /// Stable bounded insertion into the descending pre-rank buffer. Ties keep the perception order, so
    /// the survivor set never depends on a sort implementation.
    /// </summary>
    private void InsertPassTarget(int playerId, float rank, int capacity)
    {
        int position = _passTargetCount;
        while (position > 0 && _passTargetRank[position - 1] < rank)
        {
            if (position < capacity)
            {
                _passTargetIds[position] = _passTargetIds[position - 1];
                _passTargetRank[position] = _passTargetRank[position - 1];
            }

            position--;
        }

        if (position >= capacity) return;
        _passTargetIds[position] = playerId;
        _passTargetRank[position] = rank;
        if (_passTargetCount < capacity) _passTargetCount++;
    }

    /// <summary>
    /// Plans one ground pass variant, estimates it with the success model and scores it. Returns false
    /// when the pass was rejected; <paramref name="laneBlocked"/> reports whether a defender physically
    /// blocked the line (which is what offers the raised alternative).
    /// </summary>
    private bool TryAddGroundPass(
        in FootballerObservation obs,
        in PerceivedEntity mate,
        in PassOption option,
        Vector2 ballXZ,
        Vector2 selfXZ,
        float forwardMeters,
        float minCompletion01,
        out bool laneBlocked)
    {
        laneBlocked = false;

        Vector2 mateXZ = new(mate.EstimatedPosition.X, mate.EstimatedPosition.Z);
        Vector2 mateVelocityXZ = new(mate.EstimatedVelocity.X, mate.EstimatedVelocity.Z);

        // Audit F01: one shared planner decides both the receiving point and the power. It aims from the
        // ball (not the player's centre) and leads the receiver by the modelled flight time — the old
        // code advanced the receiver by at most 0.35 s while the ball was in flight for well over a
        // second (measured 8.07 m miss on a 20 m pass).
        //
        // A5.3: when the receiver has advertised a meeting region there is nothing to lead. The target is
        // that region and the power is whatever gets the ball there when he does; a region no power can
        // reach in time is not offered at all rather than played as a different ball.
        //
        // AIB-11 (audit 2026-11-23, finding 6) / 2026-11-24 owner-review correction: a promised pass states its
        // minimum arrival <b>pace</b>, which is the planner's own (previously dead) timed-plan parameter. It is
        // a floor the timing solve can work with — a firm ball whose flight still lands near the meet — where
        // the crisp *range* floor struck the ball so early that the promise mistiming zeroed the completion.
        PassPlan plan = option.Promise.IsPresent
            ? _passPlanner.PlanGroundPassToRegion(
                ballXZ,
                option.Promise.TargetXZ,
                option.Promise.ArrivalSeconds,
                option.MinPower01,
                option.MinArrivalSpeedMetersPerSecond)
            : _passPlanner.PlanGroundPass(
                ballXZ,
                mate.PlayerId,
                mateXZ,
                mateVelocityXZ,
                MaxReceivableSpeed,
                option.LeadMeters,
                option.LeadDirection,
                option.MaxLeadMeters,
                option.MinPower01,
                // A human receiver may turn or stop at any moment, so his ball is aimed no further ahead
                // than the bound; an AI receiver is solved honestly and refused beyond it (A4.11).
                freezeReceiverAtLeadBound: option.HumanCaller,
                // AIB-02c: an uncommitted receiver is hedged instead — the planner freezes him at
                // PassPlanner.UncommittedLeadMaxMeters rather than pretending he will run the whole flight.
                receiverCommittedToRun: option.ReceiverCommittedToRun);

        Vector2 targetXZ;
        Vector2 aimDirection;
        float power01;

        if (plan.Feasible)
        {
            targetXZ = plan.TargetXZ;
            aimDirection = plan.AimDirectionXZ;
            power01 = plan.Power01;
        }
        else if (option.HumanCaller)
        {
            // The caller asked for the ball. Never silently drop the request and pass to somebody else:
            // fall back to a short, old-style lead so the ball still arrives near the caller. The plan is
            // rebuilt on the fallback flight so the success estimate still runs on real numbers.
            Vector2 fallbackLead = mateVelocityXZ * 0.35f;
            if (fallbackLead.Length() > 2.5f)
                fallbackLead = SimMath.SafeNormalize(fallbackLead, Vector2.Zero) * 2.5f;

            targetXZ = mateXZ + fallbackLead;
            aimDirection = SimMath.SafeNormalize(targetXZ - ballXZ, new Vector2(obs.AttackingDirectionX, 0f));
            power01 = Math.Clamp(0.25f + Vector2.Distance(ballXZ, targetXZ) / 55f, 0.25f, 0.70f);
            plan = new PassPlan(
                Feasible: true,
                AimDirectionXZ: aimDirection,
                TargetXZ: targetXZ,
                Power01: power01,
                FlightTimeSeconds: Vector2.Distance(ballXZ, targetXZ) / PassPlanner.GroundPassLaunchSpeed(power01),
                DistanceMeters: Vector2.Distance(ballXZ, targetXZ));
        }
        else
        {
            return false;
        }

        // Lane clearance must be judged against the point the pass is actually aimed at, and (ai62) a
        // remembered defender must be charged the positional doubt the sighting carries — the same
        // `chargeUncertainty` that `ShallowActionSearch` and `TryAddGroundPass` pass `true` for
        // (lofted/cross/driven/cutback/recycle still pass `false`). Pre-fix this
        // call used the unladen metric, so a stale presser 2.37 m off the lane with 1.49 m of doubt
        // scored the corridor 1.000 and the delivery was priced as open (the 2026-11-24 live Pass 3).
        float laneOpenness = EvaluateLaneClearance(
            ballXZ, targetXZ, obs.VisiblePlayers, obs.SelfTeam, chargeUncertainty: true);
        PassSuccessTerms terms = _passSuccess.Estimate(obs, mate, plan, laneOpenness, option.Promise);
        laneBlocked = terms.LaneBlocked;

        // Hard rejection for a clearly blocked ground pass survives (A4.2), and a pass the model prices
        // as hopeless is not offered at all. A caller's request is never dropped by either rule.
        if (terms.LaneBlocked && !option.HumanCaller)
        {
            Reject(mate.PlayerId, RejectionLaneBlocked);
            return false;
        }

        // AIB-11 (audit 2026-11-23, finding 6) / 2026-11-24 owner-review correction: ONE roll-out criterion
        // for every promised ground pass, in one place. A promised ball is struck to be near its meet, but a
        // time solve that has to be early (or a paced floor that cannot be that slow) still leaves it rolling:
        // the advertised-run defect the review measured was a ball 6–7 m past MeetXZ at the meet tick with no
        // check at all. The criterion is the same measured roll-out the space probe uses
        // (<see cref="RolledPastPromiseMeters"/>), against the same shared <see cref="PromiseCatchRadiusMeters"/>.
        // It runs after the plan exists and before the completion floor so the reason names the physical fact
        // rather than being hidden behind the probability gate.
        if (!option.HumanCaller)
        {
            float rolledPastMeters = option.Promise.IsPresent
                ? RolledPastPromiseMeters(plan, ballXZ, option.Promise)
                : terms.ReceiverArrivalSeconds > plan.FlightTimeSeconds
                    ? _passPlanner.BallDistanceAtTime(plan.Power01, terms.ReceiverArrivalSeconds) - plan.DistanceMeters
                    : 0f;
            if (rolledPastMeters > EffectivePromiseCatchRadiusMeters(plan.DistanceMeters))
            {
                Reject(mate.PlayerId, RejectionRolledPastPromise);
                return false;
            }
        }

        // AIB-02e: an ordinary ball to feet has to clear the raised ordinary floor — except where the corridor
        // is genuinely clean. The raise is the pass-side counterpart of AIB-02a: it exists for the ball into
        // traffic, and a corridor with nobody in it is not that ball. A clean-lane pass whose completion is
        // discounted at the arrival end (receiver control plus a defender's forecast arrival) keeps the
        // delivery floor: the reception value already prices that contest, and applying the traffic floor to
        // it silently deleted A8.3's ordinary wide crossfield pass, which the regression pins as a ball that
        // exists with or without the switch recognition. A promised/announced arrival is a delivery anyway.
        bool cleanCorridor = terms.LaneClearance01 >= LaneCongestionRatio01;
        float completionFloor = option.Promise.IsPresent || cleanCorridor
            ? MinViableDeliveryCompletion01
            : minCompletion01;
        if (!option.HumanCaller && terms.CompletionProbability01 < completionFloor)
        {
            Reject(mate.PlayerId, RejectionLowCompletion);
            return false;
        }

        // AUD-P2 / Fix 1.2 (2026-11-23): the progression a pass is scored on is the aim point relative to the
        // carrier when it is played into a run, and the receiver's own ground for a ball to feet. The plan
        // exists now, so the aim point is known; a feet pass is aimed at the receiver by construction (its
        // lead is bounded at 2.5 m), so the feet-based `forwardMeters` is still its honest progression.
        // Both variants used to read the receiver's feet, which is why a through ball 12 m beyond the runner
        // got zero extra progress or directness preference over the ball to his feet.
        float progressMeters = option.ProgressFromTarget
            ? (targetXZ.X - obs.SelfPosition.X) * obs.AttackingDirectionX
            : forwardMeters;
        float score = ScorePass(obs, mate, terms, progressMeters, targetXZ, option, out ValueTerms valueTerms);

        var passCommand = new FootballerCommand(
            aimDirection, aimDirection, false, false, false, false, false, Vector3.Zero,
            option.PassType, new Vector3(aimDirection.X, 0f, aimDirection.Y), power01, 0f)
        {
            IntendedReceiverId = mate.PlayerId
        };

        _lastCandidates.Add(new UtilityCandidate($"Pass({option.PassType})To#{mate.PlayerId}", score, passCommand)
        {
            Key = new CandidateKey(CandidateKind.Pass, mate.PlayerId, option.Tag),
            Terms = valueTerms,
            // Diagnostic only: publishes the exact point the planner aimed at, so the decision trace and
            // the rendered debug overlay can show the intended target without re-deriving it from power.
            TargetXZ = new Vector3(targetXZ.X, 0f, targetXZ.Y)
        });
        return true;
    }

    /// <summary>
    /// The documented pass score (A4.2 value convention, A4.3 gain, A4.4 scale):
    /// <c>pComplete × receptionValue − (1 − pComplete) × lossCost + preference</c>.
    ///
    /// Completion probability is applied exactly once, and the loss term is the single owner of transition
    /// risk (it rises in the passer's own third). The reception value only adds value. The remaining terms
    /// are preferences (risk tolerance, directness) — the audit requires them to be visible, and they are
    /// additive around the neutral baselines so a default player is unaffected.
    ///
    /// <paramref name="progressMeters"/> is the pass's progression source (AUD-P2 / Fix 1.2): the aim point
    /// relative to the carrier for a ball played into a run, the receiver's own ground for a ball to feet.
    /// It drives the forward preference and the man-on bounce scope; the reception value is evaluated at
    /// <paramref name="targetXZ"/> in both cases, so a through ball's arrival is still priced where it lands.
    /// </summary>
    private float ScorePass(
        in FootballerObservation obs,
        in PerceivedEntity mate,
        in PassSuccessTerms terms,
        float progressMeters,
        Vector2 targetXZ,
        in PassOption option,
        out ValueTerms valueTerms)
    {
        int receiverId = mate.PlayerId;

        // The one pressure estimate the pass model produced is what the reception is valued under.
        ReceptionValueTerms reception = _receptionValue.Evaluate(
            obs, receiverId, targetXZ, terms.DefenderPressure01);

        float loss = ActionValueScale.LossCost(obs.PerceivedBallPosition.X * obs.AttackingDirectionX);

        float preference = PassPreference(obs, progressMeters, terms.LaneClearance01);
        if (option.HumanCaller) preference += HumanCallPassBonus;

        // A7.2: a call moves the preference of the action it is about, exactly once.
        // - "man on"  → the bounce / first-time return is the ball out of the pressure;
        // - "switch"  → the pass to the team-mate who asked for it is worth more than the others.
        // AIB-06a: the bounce bonus is scoped to the short return the call is about. A 15+ m backward ball
        // is not a bounce out of pressure, it is the long backward launch report §6.4 found collecting the
        // bonus most reliably because it also has the highest completion probability.
        if (obs.Calls.Kind == TeamCallKind.ManOn && !IsLongBackwardLaunch(obs, progressMeters, targetXZ))
            preference += ActionValueScale.CallBouncePassBonus;
        if (obs.Calls.Kind == TeamCallKind.Switch && obs.Calls.NamedPlayerId == receiverId)
            preference += ActionValueScale.CallSwitchPassBonus;

        // A8.3: a switch / overload-to-isolate opportunity the carrier recognizes in his own picture earns one
        // bounded preference. It is a <i>choice among passes that already exist</i>: no candidate, sight, reach,
        // transmission range or contact rule is added, the ball is still planned by PassPlanner and priced by
        // the pass model above (whose completion probability is the recognition's feasibility input), and
        // execution still decides the contact. This is deliberately separate from A7.2's switch call: the call
        // is a team-mate's report that he is free, the recognition is the carrier's own reading of the far side,
        // and the two bounded nudges together still cannot outbid a clearly better action on the scale.
        if (_ai.EnableSwitchRecognition)
        {
            SwitchOpportunity opportunity = SwitchRecognition.Evaluate(
                obs, mate, targetXZ, terms.CompletionProbability01);
            if (opportunity.IsRecognized)
            {
                preference += opportunity.Preference;
                _switchPreferencesApplied++;
                if (!_lastSwitchOpportunity.IsRecognized ||
                    opportunity.Preference > _lastSwitchOpportunity.Preference)
                {
                    _lastSwitchOpportunity = opportunity;
                }
            }
        }

        // AIB-04a: the line-breaking half-space ball is the switch's symmetric counterpart. A8.3 gives a
        // recognised wide switch a pre-rank rescue and up to SwitchRecognition.MaxPreference; a central
        // penetrative pass between the lines had no equivalent nudge at all, which is one of the four
        // cumulative biases that funnelled every attack down the wing. The same kind of statement about a
        // pass that already exists: no candidate, no reach, priced by the same pass model, and bounded to the
        // same order as the switch preference so neither geometry is structurally favoured.
        preference += LineBreakPreference(
            new Vector2(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z),
            targetXZ,
            obs.AttackingDirectionX,
            obs.VisiblePlayers,
            obs.SelfTeam,
            terms.LaneClearance01,
            terms.CompletionProbability01);

        // A8.1: the two combination preferences, each applied exactly once. The initiation bonus is the
        // carrier's own offer on this candidate (zero when no contract was proposed); the continuation bonus
        // is the receiver honouring a live contract that names this receiver as the next ball, scaled by how
        // much of the promise is still believable. Neither can force a pass: both are preferences on the
        // action-value scale, and a blocked or unreachable return is still rejected before it is scored.
        preference += option.CombinationPreference;
        if (_ai.EnableCombinations && obs.Combination.IsReceiver &&
            obs.Combination.ContinuationTargetId == receiverId)
        {
            preference += CombinationContinuationBonus * obs.Combination.Promise01(obs.Tick);
        }

        valueTerms = new ValueTerms(
            terms.CompletionProbability01, reception.Value, loss, preference);
        return valueTerms.Value;
    }

    /// <summary>
    /// AIB-06a: true when a pass loses ground and travels further than
    /// <see cref="ActionValueScale.CallBouncePassMaxMeters"/>, i.e. it is a long backward launch rather
    /// than the short first-time return a man-on call is about.
    /// </summary>
    private static bool IsLongBackwardLaunch(
        in FootballerObservation obs,
        float forwardMeters,
        Vector2 targetXZ)
    {
        if (forwardMeters >= 0f) return false;
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        return Vector2.Distance(ballXZ, targetXZ) > ActionValueScale.CallBouncePassMaxMeters;
    }

    /// <summary>
    /// The preference half of every pass score (A4.4/A5.5b): progress, the risk-tolerance-scaled lane cost and
    /// directness.
    ///
    /// It is one method because <b>every</b> pass has to pay it. A5.5 built the cross, the driven ball and the
    /// raised alternative with <c>preference = 0</c> while the ground passes next to them collected up to
    /// <see cref="ActionValueScale.ProgressBonus"/> (0.35) of forward-progress preference — so a delivery was
    /// compared against a carry and a ground pass with a handicap nobody had stated, larger than the margin
    /// the deliveries were losing by. A delivery into the box is a forward pass; it earns the same preference.
    ///
    /// Public because the scoring rule is quoted by the regression suite and the probe, not because the
    /// decision path needs it from outside: it is a pure function of the carrier's own observation.
    /// </summary>
    public static float PassPreference(
        in FootballerObservation obs,
        float forwardMeters,
        float laneClearance01)
    {
        PlayerTendencies tendencies = obs.SelfTendencies;
        TeamTacticalProfile profile = obs.TeamProfile;

        float forwardRatio = Math.Clamp(forwardMeters / 20f, -1f, 1f);
        float preference = ActionValueScale.ProgressBonus * forwardRatio;

        // AIB-02b: a crowded lane is a cost for every passer; risk tolerance only scales the share of that
        // cost the player feels. The old formula was a bonus multiplier —
        // (tendency − baseline) x 0.40 + (profile − 0.50) x 0.30, times laneRisk — so every risk-taker
        // (tendency above 0.55 or profile above 0.50) was PAID for a crowded lane and the scorer actively
        // preferred traffic. Risk tolerance may lower a penalty or a threshold; it may never reward a
        // covered target over an open one. The ordering of an open lane above a crowded one is therefore
        // invariant to the profile, which the regression pins.
        float laneRisk = 1f - laneClearance01;
        if (laneRisk > 0f)
        {
            float riskAppetite =
                (tendencies.RiskTolerance01 - PlayerTendencyBaselines.RiskTolerance) * 0.40f +
                (profile.RiskTolerance01 - 0.50f) * 0.30f;
            float penaltyShare = Math.Clamp(
                1f - riskAppetite / LaneRiskPenaltySpan,
                LaneRiskPenaltyShareMin,
                LaneRiskPenaltyShareMax);
            preference -= LaneRiskPreferencePenalty01 * laneRisk * penaltyShare;
        }

        // Direct pass preference: how much a forward pass is worth to this player.
        preference += (tendencies.DirectPassPreference01 - PlayerTendencyBaselines.DirectPassPreference) * 0.30f * forwardRatio;

        // A9.1: team directness. The profile's Directness01 used to reach only the counter-attack transition
        // gate (A3.2) and the restart menu, so a "direct" side did not actually value a forward ball more
        // than a possession side did. It now pays the same forward-progress preference a player's own
        // direct-pass tendency pays, symmetric around the neutral 0.50: a balanced profile adds exactly
        // nothing, a possession side discounts the forward ball, a direct side premiums it.
        preference += (profile.Directness01 - 0.50f) * 0.30f * forwardRatio;
        return preference;
    }

    /// <summary>
    /// AIB-04a: the bounded preference a central, line-breaking pass earns — the symmetric counterpart of
    /// <see cref="SwitchRecognition"/>'s wide-switch nudge (A8.3).
    ///
    /// <b>Why it exists.</b> The report's fourth cluster: a switch carries a pre-rank rescue
    /// (<see cref="SwitchRecognitionRankBonus"/>) plus up to <see cref="SwitchRecognition.MaxPreference"/>,
    /// and there was no equivalent for the ball played into the half-space or between the opponent's
    /// midfield and defensive lines — so the one geometry with a stated bonus was the one that goes wide,
    /// and the attack went wide. This is a preference on a pass that already exists, not a capability: the
    /// ball is planned, priced and executed exactly as before.
    ///
    /// <b>What "line-breaking" means from the carrier's own picture.</b> The aim point must be central
    /// (inside <see cref="LineBreakHalfSpaceMeters"/>), forward of the ball, advanced
    /// (<see cref="LineBreakMinTargetProjMeters"/>), and past at least one visible opponent standing in its
    /// channel — the deepest such opponent is the line the ball would break, and the preference scales with
    /// how far past it the ball goes. Everything is read from <paramref name="visiblePlayers"/>; nothing
    /// here reads the match. No opponent in the channel means "cannot say" → no preference.
    ///
    /// <b>Why the lane and completion gates.</b> A penetrative pass through traffic is exactly the ball the
    /// rest of AIB-02 makes less attractive; the nudge is only for a line-breaking ball that is actually on,
    /// so it requires a genuinely open lane (<see cref="LineBreakMinLaneClearance01"/>) and a completion
    /// probability above the ordinary offer floor.
    /// </summary>
    /// <param name="ballXZ">Where the ball is played from (the perceived ball position).</param>
    /// <param name="targetXZ">Aim point the planner chose (the lead, not necessarily the receiver's feet).</param>
    /// <param name="attackingDirectionX">+1 when this team attacks toward +X, -1 otherwise.</param>
    /// <param name="visiblePlayers">The carrier's picture; only the opponents in it are read.</param>
    /// <param name="selfTeam">This team, so team-mates in the same picture are skipped.</param>
    /// <param name="laneClearance01">The pass model's lane clearance for this ball (0..1).</param>
    /// <param name="completionProbability01">The pass model's own completion estimate for this ball.</param>
    /// <returns>A preference in <c>[0, LineBreakPreferenceMax]</c>.</returns>
    public static float LineBreakPreference(
        Vector2 ballXZ,
        Vector2 targetXZ,
        float attackingDirectionX,
        IReadOnlyList<PerceivedEntity> visiblePlayers,
        TeamId selfTeam,
        float laneClearance01,
        float completionProbability01)
    {
        if (visiblePlayers is null) return 0f;
        if (!float.IsFinite(laneClearance01) || laneClearance01 < LineBreakMinLaneClearance01) return 0f;
        if (!float.IsFinite(completionProbability01) || completionProbability01 < MinViableCompletion01) return 0f;

        // Central/half-space only. A ball further wide than this is the switch's job (A8.3), which already
        // carries its own bounded preference; the two geometries are worth the same at most.
        if (MathF.Abs(targetXZ.Y) > LineBreakHalfSpaceMeters) return 0f;

        float targetProj = targetXZ.X * attackingDirectionX;
        if (targetProj < LineBreakMinTargetProjMeters) return 0f;

        float ballProj = ballXZ.X * attackingDirectionX;
        // A square ball breaks no line; require the ball to gain ground, not merely to be advanced.
        if (targetProj <= ballProj + 2f) return 0f;

        // The deepest visible opponent standing in the aim point's channel that the ball would go past. No
        // picture of a line means no claim to have broken one.
        float deepestBeatenProj = float.NegativeInfinity;
        for (int index = 0; index < visiblePlayers.Count; index++)
        {
            PerceivedEntity opponent = visiblePlayers[index];
            if (opponent.Team == selfTeam) continue;
            if (opponent.Role == TacticalRole.Goalkeeper) continue;
            if (MathF.Abs(opponent.EstimatedPosition.Z - targetXZ.Y) > LineBreakChannelMeters) continue;

            float opponentProj = opponent.EstimatedPosition.X * attackingDirectionX;
            if (opponentProj <= ballProj) continue;
            if (opponentProj >= targetProj) continue;
            if (opponentProj > deepestBeatenProj) deepestBeatenProj = opponentProj;
        }

        if (!float.IsFinite(deepestBeatenProj)) return 0f;

        float penetrationShare = Math.Clamp(
            (targetProj - deepestBeatenProj) / LineBreakFullPenetrationMeters, 0f, 1f);
        float centralShare = 1f - MathF.Abs(targetXZ.Y) / LineBreakHalfSpaceMeters;
        return LineBreakPreferenceMax * centralShare * penetrationShare;
    }

    /// <summary>
    /// The raised alternative for a blocked ground route (A4.2/A5.5). Its flight, landing and completion
    /// come from <see cref="PassSuccessModel.EstimateRaised"/> — the real airborne trajectory — so a ball
    /// the execution layer cannot actually drop near the target is not offered at all.
    ///
    /// <paramref name="targetXZ"/> is the meeting point the ball is aimed at: the receiver's announced
    /// region when the team has heard one (A5.3/A5.5 "cross targets are future meet points"), his current
    /// position otherwise.
    /// </summary>
    private bool AddLoftedPass(
        in FootballerObservation obs,
        in PerceivedEntity mate,
        Vector2 ballXZ,
        Vector2 targetXZ)
    {
        Vector2 mateXZ = new(mate.EstimatedPosition.X, mate.EstimatedPosition.Z);
        float distance = Vector2.Distance(ballXZ, targetXZ);
        if (distance > MaxPassDistanceMeters + 8f) return false;

        float lane = EvaluateLaneClearance(ballXZ, targetXZ, obs.VisiblePlayers, obs.SelfTeam);
        LoftedPassTerms terms = _passSuccess.EstimateRaised(
            obs, mate, targetXZ, RaisedPassProfile.Lofted, lane);
        if (!terms.Feasible) return false;

        // AIB-02e: the raised alternative keeps the deliveries' floor. It is a raised ball by definition —
        // low completion is the price it pays for going over the block — and A4.2's rule against offering a
        // hopeless raised ball still holds at the delivery floor: a ball the model calls lost is refused.
        if (terms.CompletionProbability01 < MinViableDeliveryCompletion01)
        {
            Reject(mate.PlayerId, RejectionLowCompletion);
            return false;
        }

        // Same scale as a ground pass (A4.4): the reception at the landing point, the pass score's own
        // completion estimate, and the passer's loss cost.
        ReceptionValueTerms reception = _receptionValue.Evaluate(
            obs, mate.PlayerId, terms.LandingXZ, terms.LandingPressure01);
        float loss = ActionValueScale.LossCost(obs.PerceivedBallPosition.X * obs.AttackingDirectionX);
        float forwardMeters = (targetXZ.X - obs.SelfPosition.X) * obs.AttackingDirectionX;
        ValueTerms valueTerms = new(
            terms.CompletionProbability01, reception.Value, loss,
            PassPreference(obs, forwardMeters, lane));
        float score = valueTerms.Value;

        // Aim at the meeting point; the power is what the model says puts the landing there.
        Vector2 aimDirection = SimMath.SafeNormalize(targetXZ - ballXZ, new Vector2(obs.AttackingDirectionX, 0f));
        var command = new FootballerCommand(
            aimDirection, aimDirection, false, false, false, false, false, Vector3.Zero,
            KickIntentType.LoftedPass, new Vector3(aimDirection.X, 0f, aimDirection.Y), terms.Power01, 0f)
        {
            IntendedReceiverId = mate.PlayerId
        };

        _lastCandidates.Add(new UtilityCandidate($"Pass(LoftedPass)To#{mate.PlayerId}", score, command)
        {
            Key = new CandidateKey(CandidateKind.Pass, mate.PlayerId, LoftedPassTag),
            Terms = valueTerms,
            // Diagnostic only: the exact aim point, mirroring the ground pass candidate.
            TargetXZ = new Vector3(targetXZ.X, 0f, targetXZ.Y)
        });
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // A8.1 — bounded combinations: the proposal behind a pass, the receiver's continuation and the
    // initiator's return run. A8.2 — the shallow search that chooses among a few continuations from the
    // carrier's own picture. The contract itself (delay, expiry, capacity, lifecycle) is owned by
    // CombinationCoordinator; this section only decides what is worth offering and what the options cost.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A8.2: runs the shallow search over the decision's ranked pass targets and remembers one proposal per
    /// examined first action, in A8.1's own proposal shape. The search decides which continuation is worth a
    /// promise (see <see cref="ShallowActionSearch"/>); this method only moves its answer into the table the
    /// pass candidates read, so the carrier's choice stays the one A4 scorer it has always been.
    /// </summary>
    private void RunCombinationSearch(in FootballerObservation obs, Vector2 ballXZ, float offsideLineProjX)
    {
        _search.Search(
            obs,
            _passTargetIds.AsSpan(0, _passTargetCount),
            ballXZ,
            offsideLineProjX,
            _communication?.Runs);
        // A9.3: the A8.2 node budget (3 first actions × 3 continuations) measured at the one place the
        // search is called. The counters come from the search itself; this file only aggregates them.
        Work.RecordSearch(_search.FirstActionsEvaluated, _search.ContinuationsEvaluated);

        for (int index = 0; index < _search.ProposalCount; index++)
        {
            ShallowContinuation proposal = _search.ProposalAt(index);
            RememberCombinationProposal(new CombinationProposal(
                proposal.Kind,
                proposal.ReceiverId,
                proposal.ThirdPlayerId,
                proposal.RegionXZ,
                proposal.RegionRadiusMeters,
                proposal.ArrivalTick,
                proposal.Confidence01));
        }
    }


    /// <summary>
    /// Works out what combination — if any — a pass to <paramref name="mate"/> could start, from this
    /// carrier's own observation: a one-two out of local pressure, or a third-player move onto a team-mate's
    /// already-advertised run (A5.3). The better-supported of the two is offered, and the pass candidate
    /// carries a bounded preference for it.
    ///
    /// A proposal is not a contract: only a pass that actually wins the decision publishes one (see
    /// <see cref="NoteDecision"/>). That is what keeps "the carrier considered a combination" and "the team is
    /// committed to one" from being the same statement.
    /// </summary>
    private bool TryBuildCombinationProposal(
        in FootballerObservation obs,
        in PerceivedEntity mate,
        Vector2 ballXZ,
        Vector2 selfXZ,
        float offsideLineProjX,
        float rawDistance,
        out CombinationProposal proposal)
    {
        proposal = default;
        if (rawDistance < OneTwoMinPassMeters || rawDistance > OneTwoMaxPassMeters) return false;

        bool hasProposal = false;
        float bestConfidence = 0f;

        if (TryBuildOneTwoProposal(obs, mate, ballXZ, selfXZ, offsideLineProjX, out CombinationProposal oneTwo) &&
            oneTwo.Confidence01 > bestConfidence)
        {
            proposal = oneTwo;
            bestConfidence = oneTwo.Confidence01;
            hasProposal = true;
        }

        if (TryBuildThirdPlayerProposal(obs, mate, out CombinationProposal thirdPlayer) &&
            thirdPlayer.Confidence01 > bestConfidence)
        {
            proposal = thirdPlayer;
            hasProposal = true;
        }

        return hasProposal;
    }

    /// <summary>
    /// A one-two out of pressure: the carrier is pressed (or the receiver is), and there is a return region
    /// ahead of the carrier that is inside the pitch, onside, in room, with an open lane from the receiver.
    /// The meeting tick is the later of the carrier's travel time and the receiver's realistic ready-and-
    /// return time, so the contract is a time both players can actually make.
    /// </summary>
    private bool TryBuildOneTwoProposal(
        in FootballerObservation obs,
        in PerceivedEntity mate,
        Vector2 ballXZ,
        Vector2 selfXZ,
        float offsideLineProjX,
        out CombinationProposal proposal)
    {
        proposal = default;

        Vector2 mateXZ = new(mate.EstimatedPosition.X, mate.EstimatedPosition.Z);
        float carrierPressure = NearestOpponent(obs, selfXZ, out _);
        float receiverPressure = NearestOpponent(obs, mateXZ, out _);

        // A one-two is a way out of pressure. Without pressure on either end the ordinary pass and the
        // ordinary shape are already the right actions, and a contract would only add noise.
        if (carrierPressure > OneTwoCarrierPressureMeters && receiverPressure > OneTwoReceiverPressureMeters)
            return false;

        // A8.2: one region formula, shared with the shallow search — the search's deeper examination and this
        // heuristic must place the same promise in the same place, or the two would disagree about the move.
        Vector2 region = ShallowActionSearch.OneTwoRegion(
            selfXZ, mateXZ, obs.AttackingDirectionX, OneTwoReturnForwardMeters, OneTwoReturnLateralMeters);
        region = new Vector2(
            Math.Clamp(region.X,
                -_pitch.HalfLength + ShallowActionSearch.ReturnRegionPitchMarginX,
                _pitch.HalfLength - ShallowActionSearch.ReturnRegionPitchMarginX),
            Math.Clamp(region.Y,
                -_pitch.HalfWidth + ShallowActionSearch.ReturnRegionPitchMarginY,
                _pitch.HalfWidth - ShallowActionSearch.ReturnRegionPitchMarginY));

        float progression = (region.X - selfXZ.X) * obs.AttackingDirectionX;
        if (progression < OneTwoMinProgressionMeters) return false;

        if (selfXZ.X * obs.AttackingDirectionX > OffsideTactics.MaxAttackingProjection(offsideLineProjX))
            return false;

        float maxReturnProj = ShallowActionSearch.MaxOneTwoReturnProjection(offsideLineProjX, _pitch.HalfLength);
        if (region.X * obs.AttackingDirectionX > maxReturnProj) return false;

        float space = EvaluateSpaceFreedom(region, obs.VisiblePlayers, obs.SelfTeam);
        if (space < OneTwoMinSpace01) return false;

        float lane = EvaluateLaneClearance(mateXZ, region, obs.VisiblePlayers, obs.SelfTeam);
        if (lane < OneTwoMinLaneClearance01) return false;

        float travelSeconds = Vector2.Distance(selfXZ, region) / OneTwoRunnerSpeedMetersPerSecond;
        float passFlightSeconds = Vector2.Distance(ballXZ, mateXZ) /
                                  MathF.Max(1f, PassPlanner.GroundPassLaunchSpeed(0.5f));
        float returnFlightSeconds = Vector2.Distance(mateXZ, region) / OneTwoReturnPassSpeedMetersPerSecond;
        float arrivalSeconds = MathF.Max(
            travelSeconds, passFlightSeconds + OneTwoReceiverSetupSeconds + returnFlightSeconds);
        if (!float.IsFinite(arrivalSeconds) || arrivalSeconds <= 0f || arrivalSeconds > OneTwoMaxArrivalSeconds)
            return false;

        float confidence = Math.Clamp(
            OneTwoConfidenceBase
            + OneTwoConfidenceLaneWeight * lane
            + OneTwoConfidenceSpaceWeight * space
            + OneTwoConfidenceProgressionWeight * Math.Clamp(progression / 10f, 0f, 1f),
            0f, 1f);
        if (confidence <= 0f) return false;

        int arrivalTick = obs.Tick + Math.Max(6, (int)MathF.Round(arrivalSeconds * 60f));
        proposal = new CombinationProposal(
            CombinationKind.OneTwo,
            mate.PlayerId,
            ThirdPlayerId: 0,
            region,
            OneTwoReturnRadiusMeters,
            arrivalTick,
            confidence);
        return true;
    }

    /// <summary>
    /// A third-player move: the receiver is close enough to a team-mate's advertised run to lay the ball off
    /// to it. The run itself is A5.3's — nothing here adds a run — and the contract only says "the first pass
    /// goes to you because the layoff to him is on". The layoff's lane is judged from this carrier's own
    /// picture, and the confidence is the runner's own promise scaled by how open the lane is.
    /// </summary>
    private bool TryBuildThirdPlayerProposal(
        in FootballerObservation obs,
        in PerceivedEntity mate,
        out CombinationProposal proposal)
    {
        proposal = default;
        if (_communication is null) return false;

        Vector2 mateXZ = new(mate.EstimatedPosition.X, mate.EstimatedPosition.Z);
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        float mateProj = mateXZ.X * obs.AttackingDirectionX;
        float passFlightSeconds = Vector2.Distance(ballXZ, mateXZ) /
                                  MathF.Max(1f, PassPlanner.GroundPassLaunchSpeed(0.5f));
        int advertCount = _communication.Runs.VisibleCount(obs.SelfTeam, obs.Tick);
        if (advertCount == 0) return false;

        float bestConfidence = 0f;
        for (int i = 0; i < advertCount; i++)
        {
            if (!_communication.Runs.TryGetVisibleAt(obs.SelfTeam, i, obs.Tick, out RunAdvertisement advert)) continue;
            if (advert.PlayerId == obs.SelfId || advert.PlayerId == mate.PlayerId) continue;
            if (advert.MeetTick <= obs.Tick + ThirdPlayerMinLeadTicks) continue;

            Vector2 region = advert.MeetXZ;
            float layoffMeters = Vector2.Distance(mateXZ, region);
            if (layoffMeters < ThirdPlayerMinLayoffMeters || layoffMeters > ThirdPlayerMaxLayoffMeters) continue;

            // The layoff must go forward of the first receiver; a third man arriving behind him is not a
            // continuation, it is a second outlet.
            if (region.X * obs.AttackingDirectionX <= mateProj - 2f) continue;

            // The advertised meeting tick assumed a *direct* pass from the initiator. The third-player
            // continuation is the second pass, so the contract names when that ball can actually arrive —
            // the first pass's flight, the receiver's control, then the layoff's flight — and only accepts a
            // run whose advertised hold window still contains it. A ball later than the window is a ball to
            // where the runner has stopped holding.
            float layoffFlight = layoffMeters / ThirdPlayerLayoffSpeedMetersPerSecond;
            float arrivalSeconds = passFlightSeconds + OneTwoReceiverSetupSeconds + layoffFlight;
            if (!float.IsFinite(arrivalSeconds) || arrivalSeconds <= 0f || arrivalSeconds > OneTwoMaxArrivalSeconds)
                continue;

            int arrivalTick = obs.Tick + Math.Max(ThirdPlayerMinLeadTicks,
                (int)MathF.Round(arrivalSeconds * 60f));
            if (arrivalTick > advert.ExpiresTick) continue;
            if (arrivalTick < advert.MeetTick - ThirdPlayerEarlyArrivalTicks) continue;

            float lateness = Math.Clamp(
                (arrivalTick - advert.MeetTick) /
                (float)Math.Max(1, advert.ExpiresTick - advert.MeetTick), 0f, 1f);

            float lane = EvaluateLaneClearance(mateXZ, region, obs.VisiblePlayers, obs.SelfTeam);
            if (lane < ThirdPlayerMinLaneClearance01) continue;

            float promise = advert.Promise01(obs.Tick);
            if (promise <= 0f) continue;

            // The runner's own confidence in his advertised region is the main term; the layoff lane and the
            // room at the region are what the first receiver will actually find when he looks up. A layoff
            // later than the runner's advertised meeting is still on if he is holding, but it is worth less.
            float confidence = Math.Clamp(
                ThirdPlayerConfidenceBase * promise
                + 0.45f * lane
                + 0.15f * EvaluateSpaceFreedom(region, obs.VisiblePlayers, obs.SelfTeam)
                - 0.25f * lateness,
                0f, 1f);
            if (confidence <= bestConfidence) continue;

            bestConfidence = confidence;
            proposal = new CombinationProposal(
                CombinationKind.ThirdPlayer,
                mate.PlayerId,
                advert.PlayerId,
                region,
                Math.Clamp(advert.MeetRadiusMeters,
                    CombinationCoordinator.MinReturnRadiusMeters,
                    CombinationCoordinator.MaxReturnRadiusMeters),
                arrivalTick,
                confidence);
        }

        return bestConfidence > 0f;
    }

    /// <summary>Remembers the best combination offer for a receiver in this decision's bounded proposal table.</summary>
    private void RememberCombinationProposal(in CombinationProposal proposal)
    {
        for (int i = 0; i < _combinationProposalCount; i++)
        {
            if (_combinationMateIds[i] != proposal.ReceiverId) continue;
            if (_combinationProposals[i].Confidence01 >= proposal.Confidence01) return;
            _combinationProposals[i] = proposal;
            return;
        }

        if (_combinationProposalCount >= _combinationProposals.Length) return;
        _combinationMateIds[_combinationProposalCount] = proposal.ReceiverId;
        _combinationProposals[_combinationProposalCount] = proposal;
        _combinationProposalCount++;
    }

    /// <summary>The combination proposal the last decision made for this receiver, if any.</summary>
    private bool TryGetCombinationProposal(int receiverId, out CombinationProposal proposal)
    {
        for (int i = 0; i < _combinationProposalCount; i++)
        {
            if (_combinationMateIds[i] != receiverId) continue;
            proposal = _combinationProposals[i];
            return true;
        }

        proposal = default;
        return false;
    }

    /// <summary>
    /// A8.1's receiver-side closure. The contract ends when its <b>lane</b> is gone, not when the ball cannot
    /// be timed to it yet: the promise names a meeting tick in the future, so the continuation pass may be
    /// legitimately mistimed on the first tick the receiver is ready — it becomes playable as that tick
    /// approaches. A blocked lane or a lost target is different: the receiver then abandons the contract so
    /// the initiator stops running for a ball nobody will play (the exit gate's "a blocked return aborts
    /// cleanly"). A third-player contract also ends if the third man's advertised run has gone — there is then
    /// nothing left to lay the ball off to.
    /// </summary>
    private void CheckCombinationContinuation(in FootballerObservation obs, float offsideLineProjX)
    {
        if (!_ai.EnableCombinations || !obs.Combination.IsReceiver) return;

        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);

        if (obs.Combination.Kind == CombinationKind.ThirdPlayer)
        {
            if (_communication is null ||
                !_communication.Runs.TryGetAdvertisement(obs.SelfTeam, obs.Combination.ThirdPlayerId, obs.Tick, out _) ||
                !TryGetVisibleEntity(obs, obs.Combination.ThirdPlayerId, out _))
            {
                AbandonCombination(obs, CombinationEndReason.AbandonedByThirdRunner);
                return;
            }

            if (EvaluateLaneClearance(ballXZ, obs.Combination.ReturnXZ, obs.VisiblePlayers, obs.SelfTeam) <
                ThirdPlayerMinLaneClearance01)
            {
                AbandonCombination(obs, CombinationEndReason.AbandonedByReceiver);
            }

            return;
        }

        // A one-two: the initiator must still be a legal pass target and the return lane must still exist.
        if (!TryGetVisibleEntity(obs, obs.Combination.InitiatorId, out PerceivedEntity initiator) ||
            !IsPassTargetEligible(obs, initiator, ballXZ, offsideLineProjX, humanCalled: false, out _, out _))
        {
            AbandonCombination(obs, CombinationEndReason.AbandonedByReceiver);
            return;
        }

        if (EvaluateLaneClearance(ballXZ, obs.Combination.ReturnXZ, obs.VisiblePlayers, obs.SelfTeam) <
            OneTwoMinLaneClearance01)
        {
            AbandonCombination(obs, CombinationEndReason.AbandonedByReceiver);
        }
    }

    private void AbandonCombination(in FootballerObservation obs, CombinationEndReason reason)
    {
        if (!obs.Combination.IsActive) return;

        _communication?.Combinations.Abandon(obs.Combination.ContractId, obs.SelfId, reason, obs.Tick);
    }

    /// <summary>
    /// A8.1's off-ball half. A one-two initiator runs into the frozen return region (the contract's region and
    /// meeting tick, never a moving point), and any participant whose contract has been overtaken by the ball
    /// abandons it. The run is a normal movement candidate with a stable identity, so commitment and the
    /// switch rule work exactly as they do for every other off-ball action.
    /// </summary>
    private void AddCombinationCandidates(in FootballerObservation obs, float offsideLineProjX)
    {
        CombinationContext combination = obs.Combination;
        if (!combination.IsActive) return;

        // The ball has moved to somebody who is not part of the contract's plan: the move it was made for
        // has gone. A ball in flight is not controlled, so a return on its way to the region never hits this.
        if (obs.PossessingTeam == obs.SelfTeam && obs.BallControlState == BallControlState.Controlled &&
            obs.BallControllingPlayerId is int carrierId)
        {
            if (combination.Role == CombinationRole.Receiver && carrierId != obs.SelfId)
            {
                AbandonCombination(obs, CombinationEndReason.AbandonedByReceiver);
                return;
            }

            if (combination.Role == CombinationRole.Initiator &&
                carrierId != obs.SelfId && carrierId != combination.ReceiverId)
            {
                AbandonCombination(obs, CombinationEndReason.AbandonedByInitiator);
                return;
            }
        }

        if (!combination.IsReturnRunner) return;

        Vector2 region = combination.ReturnXZ;
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);

        // "Any participant may abandon an invalid lane": the return region was chosen from this player's
        // picture when he made the pass. If the return region exceeds the bounded behind-the-line depth,
        // has slid off the pitch or a defender has taken the room, the lane is no longer valid and he stops
        // running it. Before release, his actual run target holds onside at MaxAttackingProjection so he is
        // not caught offside before the return pass is kicked (Law 11).
        float maxOnsideProj = OffsideTactics.MaxAttackingProjection(offsideLineProjX);
        float maxReturnProj = ShallowActionSearch.MaxOneTwoReturnProjection(offsideLineProjX, _pitch.HalfLength);
        bool insidePitch = MathF.Abs(region.X) <= _pitch.HalfLength - 3f &&
                           MathF.Abs(region.Y) <= _pitch.HalfWidth - 2f;
        bool released = OffsideTactics.IsReleased(obs);
        bool validDepth = released || region.X * obs.AttackingDirectionX <= maxReturnProj;
        float space = EvaluateSpaceFreedom(region, obs.VisiblePlayers, obs.SelfTeam);
        if (!insidePitch || !validDepth || space < CombinationReturnRunMinSpace01)
        {
            AbandonCombination(obs, CombinationEndReason.AbandonedByInitiator);
            return;
        }

        Vector2 runTarget = region;
        if (!released && runTarget.X * obs.AttackingDirectionX > maxOnsideProj)
            runTarget.X = obs.AttackingDirectionX * maxOnsideProj;

        Vector2 toRegion = runTarget - selfXZ;
        float distance = toRegion.Length();
        Vector2 moveDir = distance > 0.4f
            ? (toRegion / distance) * Math.Clamp(distance / 3.5f, 0.35f, 1.0f)
            : Vector2.Zero;
        Vector2 faceDir = SimMath.SafeNormalize(ballXZ - selfXZ, obs.SelfFacingXZ);
        float utility = CombinationReturnRunBaseUtility
                        + CombinationReturnRunConfidenceWeight * combination.Promise01(obs.Tick)
                        + CombinationReturnRunSpaceWeight * space;

        var command = new FootballerCommand(
            moveDir, faceDir, distance > 3.5f, false, false, false, false, Vector3.Zero,
            KickIntentType.None, Vector3.UnitX, 0f, 0f);

        _lastCandidates.Add(new UtilityCandidate(CombinationReturnActionName, utility, command)
        {
            TargetXZ = new Vector3(runTarget.X, 0f, runTarget.Y),
            Key = new CandidateKey(CandidateKind.Run, TargetTag: CombinationReturnTag)
        });
    }

    // ---------------------------------------------------------------------------------------------
    // The cross (A5.5): an airborne delivery to an announced box arrival.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// How wide the carrier must be for a cross to be the natural delivery, in metres from the centre.
    /// A cross from the middle of the pitch is a lofted pass; the wide lane is what makes it a cross.
    /// </summary>
    internal const float CrossMinLateralMeters = 15f;

    /// <summary>Candidate tag of the cross (A5.5). Interned: a candidate is built every tick.</summary>
    internal const string CrossPassTag = "Cross";

    /// <summary>
    /// A7.6: how much of the player's cross appetite becomes preference on the cross candidate, in scale
    /// units. The appetite is a 0..1 tendency with a neutral of 0.55, so a touchline winger (≈0.75, i.e.
    /// +0.20 above neutral) prices a cross ≈0.08 higher and an inside forward (≈0.40, −0.15) ≈0.06 lower —
    /// comparable with the A7.2 call preferences, and deliberately smaller than the gap between a delivery and
    /// a good carry, because an archetype is a lean, not an instruction.
    /// </summary>
    internal const float CrossPreferenceScale = 0.40f;

    /// <summary>Candidate tag of the driven delivery (A5.5b).</summary>
    internal const string DrivenPassTag = "Driven";

    /// <summary>Candidate tag of the cutback (A5.5b).</summary>
    internal const string CutbackPassTag = "Cutback";

    /// <summary>Candidate tag of the raised alternative to feet (A5.5).</summary>
    internal const string LoftedPassTag = "Lofted";

    /// <summary>
    /// How far into the attacking half the carrier must be before a cross is offered, in metres
    /// (attack-relative projection). Crossing from the halfway line is a hopeful punt; the box must be
    /// within reach of a delivery the model can actually time.
    ///
    /// A5.5b: this used to be 8 m, and the measured offers were long diagonals played from the halfway line
    /// towards a region 28 m from goal — a cross in name only, priced against a carry the carrier had every
    /// reason to keep making. A cross is the delivery of a carrier who has run out of pitch, so the carrier
    /// has to be in the final third.
    /// </summary>
    internal const float CrossMinProjX = RunCandidateGenerator.FinalThirdProjX;

    /// <summary>
    /// Attack-relative projection a box arrival must reach to be a cross target, in metres (A5.5b).
    ///
    /// 32 m is 20.5 m from the goal line: the penalty area and a stride beyond its edge. The bar the model
    /// needs is the one where a reception's <b>shooting chance</b> is real
    /// (<see cref="AI.ReceptionValueModel.ChanceShare"/> is only paid inside
    /// <see cref="ActionValueScale.ShotFarMeters"/> of the goal). It was 24 m in A5.5, which admitted a "box
    /// arrival" 28 m from goal where the chance term pays nothing — measured, every one of those crosses was
    /// priced against a carry it had no way to beat.
    /// </summary>
    internal const float CrossBoxProjX = 32f;

    /// <summary>
    /// Offers one <b>cross</b> into the box, aimed at a team-mate's announced arrival (A5.5).
    ///
    /// This is the delivery half of "cross targets are future meet points": the ball is planned to the
    /// region the runner has advertised — A5.3's meeting region — and its flight is the real airborne
    /// trajectory, so the price is comparable with the carry, the recycle and the ground pass on one scale.
    /// Without an announced box arrival there is no target: a cross into a crowd with nobody attacking it is
    /// exactly the ball the A4 audit measured as a turnover every time, so it is not offered.
    /// </summary>
    private void AddCrossCandidate(in FootballerObservation obs, Vector2 ballXZ, Vector2 selfXZ)
    {
        if (_communication is null) return;
        if (MathF.Abs(selfXZ.Y) < CrossMinLateralMeters) return;
        if (selfXZ.X * obs.AttackingDirectionX < CrossMinProjX) return;

        int bestMateId = 0;
        Vector2 bestRegion = default;
        float bestProj = float.NegativeInfinity;

        for (int i = 0; i < obs.VisiblePlayers.Count; i++)
        {
            PerceivedEntity mate = obs.VisiblePlayers[i];
            if (mate.Team != obs.SelfTeam || mate.PlayerId == obs.SelfId) continue;
            if (mate.Role == TacticalRole.Goalkeeper) continue;

            if (!_communication.Runs.TryGetAdvertisement(
                    obs.SelfTeam, mate.PlayerId, obs.Tick, out RunAdvertisement advert))
            {
                continue;
            }

            if (advert.Kind is not (RunKind.LateBoxArrival or RunKind.BlindSideArrival)) continue;
            if (advert.MeetTick <= obs.Tick) continue;

            float proj = advert.MeetXZ.X * obs.AttackingDirectionX;
            if (proj < CrossBoxProjX) continue;
            if (proj <= bestProj) continue;

            bestProj = proj;
            bestMateId = mate.PlayerId;
            bestRegion = advert.MeetXZ;
        }

        if (bestMateId == 0) return;
        if (!TryGetVisibleEntity(obs, bestMateId, out PerceivedEntity target)) return;

        float lane = EvaluateLaneClearance(ballXZ, bestRegion, obs.VisiblePlayers, obs.SelfTeam);
        LoftedPassTerms terms = _passSuccess.EstimateRaised(
            obs, target, bestRegion, RaisedPassProfile.Cross, lane);
        if (!terms.Feasible) return;

        // AIB-02e: a cross is a delivery. It keeps the delivery floor — a cross is a low-percentage ball by
        // nature and its value is the box reception it creates — but a ball the raised model prices as lost is
        // still refused at that floor.
        if (terms.CompletionProbability01 < MinViableDeliveryCompletion01)
        {
            Reject(bestMateId, RejectionLowCompletion);
            return;
        }

        ReceptionValueTerms reception = _receptionValue.Evaluate(
            obs, bestMateId, terms.LandingXZ, terms.LandingPressure01);
        float loss = ActionValueScale.LossCost(obs.PerceivedBallPosition.X * obs.AttackingDirectionX);
        float crossForward = (bestRegion.X - obs.SelfPosition.X) * obs.AttackingDirectionX;
        // A7.6: the archetype's delivery appetite is a preference on the cross it already prices — a
        // touchline winger wants it, an inside forward does not. The candidate itself is offered on exactly
        // the same feasibility rules for both.
        float crossPreference = PassPreference(obs, crossForward, lane)
            + (obs.SelfTendencies.CrossPreference01 - PlayerTendencyBaselines.CrossPreference)
            * CrossPreferenceScale;
        ValueTerms valueTerms = new(
            terms.CompletionProbability01, reception.Value, loss, crossPreference);

        Vector2 aim = SimMath.SafeNormalize(bestRegion - ballXZ, new Vector2(obs.AttackingDirectionX, 0f));
        var command = new FootballerCommand(
            aim, aim, false, false, false, false, false, Vector3.Zero,
            KickIntentType.Cross, new Vector3(aim.X, 0f, aim.Y), terms.Power01, 0f)
        {
            IntendedReceiverId = bestMateId
        };

        _lastCandidates.Add(new UtilityCandidate($"CrossTo#{bestMateId}", valueTerms.Value, command)
        {
            Key = new CandidateKey(CandidateKind.Pass, bestMateId, CrossPassTag),
            Terms = valueTerms,
            TargetXZ = new Vector3(terms.LandingXZ.X, 0f, terms.LandingXZ.Y)
        });
    }

    // ---------------------------------------------------------------------------------------------
    // The driven ball (A5.5b): a hard flat delivery to an announced arrival.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Shortest ball worth driving, in metres. Below this a rolled pass is already at the receiver almost as
    /// quickly, so the driven ball's only advantage (it gets there before a defender can) buys nothing.
    /// </summary>
    internal const float DrivenMinDistanceMeters = 12f;

    /// <summary>
    /// Offers at most one <b>driven</b> delivery: a flat, hard ball struck to be at a team-mate's announced
    /// meeting region <b>when he gets there</b> (A5.5b).
    ///
    /// This is the delivery the model could not see before. A driven ball first touches the ground inside ten
    /// metres and reaches a man twenty metres away by skipping and rolling, so the landing-based raised
    /// estimate could only ever offer it to somebody standing within reach of the first bounce. With arrival
    /// semantics (<see cref="RaisedPassModel.TryPowerForArrival"/>) the ball is timed to the promise, and its
    /// own profile prices the pace: it arrives sooner than a rolled ball, which is why it beats a defender,
    /// and it is harder to kill, which is what it pays. Like the cross it is a delivery to an announced
    /// arrival — the run board is the targeting, not a second channel.
    ///
    /// AIB-02f: the ball is aimed and measured from <paramref name="ballXZ"/>, the perceived ball position,
    /// not the player's centre. The old code planned from <c>selfXZ</c>, which put an angular offset on the
    /// aim whenever the ball sat 0.5–0.9 m off the carrier — the same origin defect the ground passes had
    /// already been fixed for (audit F01).
    /// </summary>
    private void AddDrivenCandidate(in FootballerObservation obs, Vector2 ballXZ)
    {
        if (_communication is null) return;

        int bestMateId = 0;
        Vector2 bestRegion = default;
        LoftedPassTerms bestTerms = default;
        ValueTerms bestValueTerms = default;
        float bestScore = float.NegativeInfinity;
        float loss = ActionValueScale.LossCost(obs.PerceivedBallPosition.X * obs.AttackingDirectionX);

        for (int i = 0; i < obs.VisiblePlayers.Count; i++)
        {
            PerceivedEntity mate = obs.VisiblePlayers[i];
            if (mate.Team != obs.SelfTeam || mate.PlayerId == obs.SelfId) continue;
            if (mate.Role == TacticalRole.Goalkeeper) continue;

            if (!_communication.Runs.TryGetAdvertisement(
                    obs.SelfTeam, mate.PlayerId, obs.Tick, out RunAdvertisement advert))
            {
                continue;
            }

            if (advert.MeetTick <= obs.Tick) continue;

            float distance = Vector2.Distance(ballXZ, advert.MeetXZ);
            if (distance < DrivenMinDistanceMeters || distance > MaxPassDistanceMeters) continue;

            float lane = EvaluateLaneClearance(ballXZ, advert.MeetXZ, obs.VisiblePlayers, obs.SelfTeam);
            var promise = new PassArrivalPromise(
                advert.MeetXZ,
                advert.TicksUntilMeeting(obs.Tick) * SimConstants.FixedDeltaTime,
                advert.MeetRadiusMeters,
                advert.Promise01(obs.Tick));

            LoftedPassTerms terms = _passSuccess.EstimateRaised(
                obs, mate, advert.MeetXZ, RaisedPassProfile.Driven, lane, promise);
            if (!terms.Feasible) continue;
            // AIB-02e: a driven ball is a delivery to an announced arrival — it keeps the delivery floor.
            if (terms.CompletionProbability01 < MinViableDeliveryCompletion01) continue;

            ReceptionValueTerms reception = _receptionValue.Evaluate(
                obs, mate.PlayerId, terms.LandingXZ, terms.LandingPressure01);
            float forward = (advert.MeetXZ.X - obs.SelfPosition.X) * obs.AttackingDirectionX;
            var valueTerms = new ValueTerms(
                terms.CompletionProbability01, reception.Value, loss, PassPreference(obs, forward, lane));

            if (valueTerms.Value <= bestScore) continue;
            bestScore = valueTerms.Value;
            bestValueTerms = valueTerms;
            bestMateId = mate.PlayerId;
            bestRegion = advert.MeetXZ;
            bestTerms = terms;
        }

        if (bestMateId == 0) return;

        // AIB-02f: aimed from the ball, matching the plan and the lane measurement above.
        Vector2 aim = SimMath.SafeNormalize(bestRegion - ballXZ, new Vector2(obs.AttackingDirectionX, 0f));
        var command = new FootballerCommand(
            aim, aim, false, false, false, false, false, Vector3.Zero,
            KickIntentType.DrivenPass, new Vector3(aim.X, 0f, aim.Y), bestTerms.Power01, 0f)
        {
            IntendedReceiverId = bestMateId
        };

        _lastCandidates.Add(new UtilityCandidate($"DrivenTo#{bestMateId}", bestScore, command)
        {
            Key = new CandidateKey(CandidateKind.Pass, bestMateId, DrivenPassTag),
            Terms = bestValueTerms,
            TargetXZ = new Vector3(bestTerms.LandingXZ.X, 0f, bestTerms.LandingXZ.Y)
        });
    }

    // ---------------------------------------------------------------------------------------------
    // The cutback (A5.5b): the ground delivery into the box from the byline.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Attack-relative projection the carrier must reach for a cutback, in metres (22.5 m out).
    ///
    /// The strict byline (46 m and beyond) was measured at zero offers in three matches: this side does not
    /// carry the ball into the corner often enough for a rule that only fires there to be evidence of
    /// anything. The wide final third is where the cutback's situation actually arises — a carrier who has
    /// run out of room wide, and a runner arriving in the box in front of him but behind the ball.
    /// </summary>
    internal const float CutbackCarrierMinProjX = 30f;

    /// <summary>How wide the carrier must be for a cutback, in metres from the centre.</summary>
    internal const float CutbackCarrierMinLateralMeters = 12f;

    /// <summary>
    /// How far behind the ball the announced region must be for the ball to be a cutback, in metres.
    ///
    /// Three metres, not a token margin: the standard box arrival sits 11 m from the goal line, so a carrier
    /// on the byline is only a few metres in front of it. The bar exists to tell a cutback from a ball played
    /// sideways into the same region, not to demand a long backward pass.
    /// </summary>
    internal const float CutbackMinBehindMeters = 3f;

    /// <summary>Attack-relative projection the announced region must still reach, in metres.</summary>
    internal const float CutbackMinRegionProjX = 24f;

    /// <summary>
    /// Offers a <b>cutback</b>: a ground ball pulled back from the byline into a team-mate's announced arrival
    /// (A5.5b).
    ///
    /// The cutback is the delivery that solves the situation the cross cannot: against a defence that sits
    /// behind the ball, no forward run can arrive inside the box legally — the run generator clamps a box
    /// arrival to the believed offside line (A5.4), so the announced region is at the edge of the box rather
    /// than in it. A carrier who has taken the ball to the byline can still play backwards into that region,
    /// because a ball played from beyond the receiver is onside by construction. Measured, that is exactly
    /// where the box arrivals are sitting: the cross is only ever offered when the defensive line is deep
    /// enough to leave the box itself onside, and the cutback covers the other case.
    ///
    /// It is a ground pass to a promised region and is planned and priced exactly like one
    /// (<see cref="PassPlanner.PlanGroundPassToRegion"/> plus the promise); what makes it its own candidate is
    /// that the generic run pass is pre-ranked by forward progress, and a region <b>behind</b> the carrier
    /// ranks last by construction — it would be pruned before it was ever planned.
    /// </summary>
    private void AddCutbackCandidate(in FootballerObservation obs, Vector2 ballXZ, Vector2 selfXZ)
    {
        if (_communication is null) return;

        float selfProj = selfXZ.X * obs.AttackingDirectionX;
        if (selfProj < CutbackCarrierMinProjX) return;
        if (MathF.Abs(selfXZ.Y) < CutbackCarrierMinLateralMeters) return;

        int bestMateId = 0;
        Vector2 bestRegion = default;
        RunAdvertisement bestAdvert = default;
        float bestRegionProj = float.NegativeInfinity;

        for (int i = 0; i < obs.VisiblePlayers.Count; i++)
        {
            PerceivedEntity mate = obs.VisiblePlayers[i];
            if (mate.Team != obs.SelfTeam || mate.PlayerId == obs.SelfId) continue;
            if (mate.Role == TacticalRole.Goalkeeper) continue;

            if (!_communication.Runs.TryGetAdvertisement(
                    obs.SelfTeam, mate.PlayerId, obs.Tick, out RunAdvertisement advert))
            {
                continue;
            }

            if (advert.Kind is not (RunKind.LateBoxArrival or RunKind.BlindSideArrival)) continue;
            if (advert.MeetTick <= obs.Tick) continue;

            float regionProj = advert.MeetXZ.X * obs.AttackingDirectionX;
            if (regionProj < CutbackMinRegionProjX) continue;
            if (regionProj > selfProj - CutbackMinBehindMeters) continue;
            if (regionProj <= bestRegionProj) continue;

            bestRegionProj = regionProj;
            bestMateId = mate.PlayerId;
            bestRegion = advert.MeetXZ;
            bestAdvert = advert;
        }

        if (bestMateId == 0) return;
        if (!TryGetVisibleEntity(obs, bestMateId, out PerceivedEntity target)) return;

        float arriveInSeconds = bestAdvert.TicksUntilMeeting(obs.Tick) * SimConstants.FixedDeltaTime;
        PassPlan plan = _passPlanner.PlanGroundPassToRegion(ballXZ, bestRegion, arriveInSeconds);
        if (!plan.Feasible)
        {
            Reject(bestMateId, RejectionUnplannable);
            return;
        }

        float lane = EvaluateLaneClearance(ballXZ, plan.TargetXZ, obs.VisiblePlayers, obs.SelfTeam);
        var promise = new PassArrivalPromise(
            bestRegion, arriveInSeconds, bestAdvert.MeetRadiusMeters, bestAdvert.Promise01(obs.Tick));
        PassSuccessTerms terms = _passSuccess.Estimate(obs, target, plan, lane, promise);

        if (terms.LaneBlocked)
        {
            Reject(bestMateId, RejectionLaneBlocked);
            return;
        }

        // AIB-02e: the cutback is a delivery to an announced arrival into the box — it keeps the delivery
        // floor, so raising the ordinary ground-pass floor cannot delete it.
        if (terms.CompletionProbability01 < MinViableDeliveryCompletion01)
        {
            Reject(bestMateId, RejectionLowCompletion);
            return;
        }

        ReceptionValueTerms reception = _receptionValue.Evaluate(
            obs, bestMateId, plan.TargetXZ, terms.DefenderPressure01);
        float loss = ActionValueScale.LossCost(obs.PerceivedBallPosition.X * obs.AttackingDirectionX);
        float forward = (plan.TargetXZ.X - obs.SelfPosition.X) * obs.AttackingDirectionX;
        var valueTerms = new ValueTerms(
            terms.CompletionProbability01, reception.Value, loss, PassPreference(obs, forward, lane));

        var command = new FootballerCommand(
            plan.AimDirectionXZ, plan.AimDirectionXZ, false, false, false, false, false, Vector3.Zero,
            KickIntentType.GroundPass,
            new Vector3(plan.AimDirectionXZ.X, 0f, plan.AimDirectionXZ.Y), plan.Power01, 0f)
        {
            IntendedReceiverId = bestMateId
        };

        _lastCandidates.Add(new UtilityCandidate($"CutbackTo#{bestMateId}", valueTerms.Value, command)
        {
            Key = new CandidateKey(CandidateKind.Pass, bestMateId, CutbackPassTag),
            Terms = valueTerms,
            TargetXZ = new Vector3(plan.TargetXZ.X, 0f, plan.TargetXZ.Y)
        });
    }

    /// <summary>Slowest a receiver must be moving to be offered a ball into the space ahead of him.</summary>
    internal const float MinRunIntoSpaceSpeed = 1.5f;

    /// <summary>
    /// True when a pass into the space ahead of this teammate is a genuinely different option from a
    /// pass to his feet (A4.1). Three things make it real: he is actually running (a stationary player has
    /// no run to be played into, and the through lead then puts the ball five metres past a man who was
    /// never going anywhere), he is not running back toward the passer, and there is room in front of him
    /// to play into. A runner who is already sprinting forward usually ends up with the same target as a
    /// feet pass (the planner leads him by the flight time anyway), and the success model then rejects the
    /// doubled lead — which is the correct answer, not a gap.
    /// </summary>
    private bool RunsIntoSpace(in FootballerObservation obs, in PerceivedEntity mate, float offsideLineProjX)
    {
        Vector2 velocity = new(mate.EstimatedVelocity.X, mate.EstimatedVelocity.Z);
        if (velocity.Length() < MinRunIntoSpaceSpeed) return false;
        if (velocity.X * obs.AttackingDirectionX < -0.5f) return false;

        // Eligibility is judged where the runner is when the ball is played, not where the lead lands.
        float mateProjX = mate.EstimatedPosition.X * obs.AttackingDirectionX;
        if (mateProjX > offsideLineProjX - 1.75f) return false;

        Vector2 aheadXZ = new(
            mate.EstimatedPosition.X + obs.AttackingDirectionX * ThroughLeadMeters,
            mate.EstimatedPosition.Z);
        // A4.11: the room ahead was 5 m before the feet-pass lead was tightened. Refusing a feet pass to
        // a runner removed the AI's forward option in the final third, where the room ahead is rarely
        // 5 m, and the side stopped shooting in short matches; 3.6 m of room is enough for a ball into
        // the space ahead to be worth offering.
        return EvaluateSpaceFreedom(aheadXZ, obs.VisiblePlayers, obs.SelfTeam) > 0.40f;
    }

    // ---------------------------------------------------------------------------------------------
    // Carry, take-on, shield, clearance, recycle (A4.1's remaining on-ball actions).
    // ---------------------------------------------------------------------------------------------

    /// <summary>The carry decision: direction, target, score, its freedom term and whether to sprint.</summary>
    private readonly record struct CarryChoice(
        Vector2 Direction,
        Vector2 Target,
        float Utility,
        float Space,
        bool Sprint,
        ValueTerms Terms);

    /// <summary>
    /// Chooses the carry direction: forward first, then the ring of directions around it, holding the
    /// direction already chosen unless another is clearly better (A2.4 pressure plus boundary safety).
    ///
    /// AIB-07b (audit 2026-11-23, finding 1) adds the advance prospect: the value of the better shot the
    /// carry's own target would produce, paid in the gain only while that ground is open. The 2026-11-24
    /// owner-review correction keeps the term but multiplies it by the outfield lane clearance from the carry
    /// target to the goal and ramps it in over <see cref="ActionValueScale.CarryAdvanceRampMeters"/> instead
    /// of gating it in one step at <see cref="ActionValueScale.ShotRangeMeters"/>. AIB-12b (finding 5)
    /// decides the sprint from the path ahead (see <see cref="IsCarryPathBlocked"/>) instead of from the
    /// static nearest-opponent ring, so a chaser behind the carrier no longer forces a jog.
    /// </summary>
    /// <param name="nearestOppDist">Nearest believed opponent, in metres (the static pressure floor).</param>
    /// <param name="opponentArrivalSeconds">
    /// AIB-01: how soon the local pressure field says the best opponent can arrive at the carrier's own
    /// feet, or −1 when nobody can inside the model's horizon. This is the carry's contact risk now; the
    /// static distance alone let a sprinting presser close from 5 m for free.
    /// </param>
    private CarryChoice ChooseCarry(
        in FootballerObservation obs,
        Vector2 selfXZ,
        Vector2 ballXZ,
        Vector2 goalXZ,
        Vector3 oppGoalCenter,
        bool inFinalThird,
        float nearestOppDist,
        float opponentArrivalSeconds)
    {
        // A9.6b: outside the final third a winger drives into the half-space, not at the touchline. The old
        // target was `HalfWidth - 6`, which made every wide attack a run at the line and left the flank as
        // the only place a carry could go.
        Vector2 idealDriveTarget = obs.SelfRole == TacticalRole.Winger && !inFinalThird
            ? new Vector2(oppGoalCenter.X * 0.85f, MathF.Sign(obs.SelfPosition.Z) * (_pitch.HalfWidth * 0.50f))
            : goalXZ;

        Vector2 forwardDir = SimMath.SafeNormalize(idealDriveTarget - selfXZ, new Vector2(obs.AttackingDirectionX, 0f));

        // A2.4: the one local pressure window per decision is built in GenerateOnBallCandidates (AIB-01 moved
        // it ahead of the pass trigger so the emergency release can read it); every carry direction and the
        // shield action reuse that build.

        // A9.6a/AIB-01: how soon an opponent can arrive at the carrier's own feet. The field was always built
        // here but read only 5 m ahead, so a carry into a closing defender cost nothing (rendered-review
        // finding 1). The risk is contact, not shadow: it is read from the arrival estimate rather than the
        // old 2.2 m static ring, because a presser at 8.4 m/s covers that ring in a seventh of a second.
        float ContactRisk(float arrivalSeconds)
        {
            if (arrivalSeconds < 0f || arrivalSeconds >= ActionValueScale.ContactRiskArrivalSeconds)
                return 0f;

            float closeness = Math.Clamp(
                (ActionValueScale.ContactRiskArrivalSeconds - arrivalSeconds) /
                (ActionValueScale.ContactRiskArrivalSeconds - ActionValueScale.ContactRiskFullArrivalSeconds),
                0f, 1f);
            return ActionValueScale.CarryContactRiskCost * closeness;
        }

        float contactRisk = ContactRisk(opponentArrivalSeconds);

        Vector2 bestDir = forwardDir;
        float bestScore = ScoreCarryDirection(obs, selfXZ, forwardDir, out float bestSpace);

        // Candidate carries around the forward direction. The old set only looked at open space, so near
        // a touchline the *emptiest* direction was out of the pitch and the AI dutifully carried the ball
        // out of play. Boundary safety and the contact risk are part of the score now.
        ReadOnlySpan<float> angles = [-0.45f, 0.45f, -0.85f, 0.85f, -1.25f, 1.25f];
        foreach (float ang in angles)
        {
            Vector2 candidate = RotateXZ(forwardDir, ang);
            float score = ScoreCarryDirection(obs, selfXZ, candidate, out float space);
            if (score > bestScore)
            {
                bestScore = score;
                bestDir = candidate;
                bestSpace = space;
            }
        }

        // Keep the carry direction the player already chose unless another is clearly better. Without
        // this a defender standing on the carrier flips the pressure term left and right every tick and
        // the carrier runs on the spot: measured at 0.1 m of travel in two seconds in a 1v1 face-off.
        if (_hasLastCarryDirection)
        {
            float previousScore = ScoreCarryDirection(obs, selfXZ, _lastCarryDirection, out float previousSpace);
            if (previousScore >= 0f && previousScore >= bestScore - CarryDirectionHysteresis)
            {
                bestScore = previousScore;
                bestDir = _lastCarryDirection;
                bestSpace = previousSpace;
            }
        }

        _lastCarryDirection = bestDir;
        _hasLastCarryDirection = true;

        // Boxed in (a corner, or pinned on the line): turn back toward the pitch instead of walking
        // straight out of play. A pass or a shot will normally have won the decision by then.
        if (bestScore < 0f)
        {
            bestDir = SimMath.SafeNormalize(
                new Vector2(-selfXZ.X, -selfXZ.Y), new Vector2(-obs.AttackingDirectionX, 0f));
            bestSpace = 0.5f;
        }

        Vector2 carryTarget = new(
            Math.Clamp(selfXZ.X + bestDir.X * 4.5f, -_pitch.HalfLength + CarryLineMargin, _pitch.HalfLength - CarryLineMargin),
            Math.Clamp(selfXZ.Y + bestDir.Y * 4.5f, -_pitch.HalfWidth + CarryLineMargin, _pitch.HalfWidth - CarryLineMargin));
        bestDir = SimMath.SafeNormalize(carryTarget - selfXZ, bestDir);

        // The ball sits just ahead of the feet, so being pinned on the line is also a reason to release
        // it: a pass out of the pressure is worth more than another carry into the line.
        float lineMargin = MathF.Min(
            _pitch.HalfLength - MathF.Abs(ballXZ.X),
            _pitch.HalfWidth - MathF.Abs(ballXZ.Y));
        float linePressure = Math.Clamp(1f - lineMargin / 3.0f, 0f, 1f);

        // AIB-07b (audit 2026-11-23, finding 1): the ground ahead is worth more than the ground underfoot.
        // The term is the *change in shot value* the carry's own 4.5 m target would produce, paid once in the
        // gain and only while that ground is actually open — nobody can arrive at the carrier inside the
        // contact window, and nobody can reach the target inside CarryAdvancePathArrivalSeconds. Closed
        // ground pays exactly zero: the contact risk below stays the only pressure deduction, so this is a
        // prospect that does not exist under pressure, never a second discount of the same risk. It is not
        // in ScoreCarryDirection on purpose — the direction fixtures and the sprint decision are about the
        // physical space, and the advance is about what the space is worth.
        //
        // 2026-11-24 owner-review correction (AIB-07b): the first cut paid the prospect on a hardcoded
        // 100 %-open shooting lane and switched it on in one 0.65 step at ShotRangeMeters, so a compact
        // block between the carry target and goal was free and one metre of forward movement could flip a
        // release into a ball-hog. The prospect is now (a) multiplied by the real outfield lane clearance
        // from the carry target to the goal — keeper excluded, the AIB-05 convention for a shot lane — so a
        // block in front of the target collapses it, and (b) faded in over CarryAdvanceRampMeters, so it
        // grows as the carrier enters shooting range instead of stepping on. The ramp is read from the
        // carrier's distance now, like the old gate, and it is exactly 0 beyond ShotRangeMeters.
        float advanceProspect = 0f;
        float distanceToGoalNow = Vector2.Distance(selfXZ, goalXZ);
        float shotRangeRamp01 = Math.Clamp(
            (ActionValueScale.ShotRangeMeters - distanceToGoalNow) / ActionValueScale.CarryAdvanceRampMeters,
            0f, 1f);
        if (shotRangeRamp01 > 0f &&
            bestSpace >= ActionValueScale.CarryAdvanceMinSpace01 &&
            (opponentArrivalSeconds < 0f ||
             opponentArrivalSeconds >= ActionValueScale.ContactRiskArrivalSeconds))
        {
            float targetArrival = _pressure.Read(carryTarget, out _).OpponentArrivalSeconds;
            if (targetArrival < 0f || targetArrival >= ActionValueScale.CarryAdvancePathArrivalSeconds)
            {
                // The prospect is priced on the pitch's real goal geometry: it asks "what would this ground
                // be worth", not "what would this shot do", so it must not re-price the shot's keeper a
                // second time. The lane is the exception and it is outfield-only: a wall of bodies between
                // the target and the goal is what the ground is worth, and the keeper is not part of it.
                float laneClearance01 = EvaluateLaneClearance(
                    carryTarget, goalXZ, obs.VisiblePlayers, obs.SelfTeam, excludeGoalkeeper: true);
                float shotValueGain = ActionValueScale.ShotValue(
                        Vector2.Distance(carryTarget, goalXZ), 1f,
                        GoalMouthAngleDegrees(carryTarget, oppGoalCenter))
                    - ActionValueScale.ShotValue(
                        distanceToGoalNow, 1f,
                        GoalMouthAngleDegrees(selfXZ, oppGoalCenter));
                advanceProspect = ActionValueScale.CarryAdvanceValue
                                  * laneClearance01
                                  * shotRangeRamp01
                                  * Math.Clamp(shotValueGain / ActionValueScale.CarryAdvanceReferenceUnits, 0f, 1f);
            }
        }

        // A4.4: the carry's value lives in ActionValueScale with the unit documented. See the block
        // comment there for why this stays a heuristic curve rather than the probability-weighted shape a
        // pass uses — measured, not assumed.
        //
        // A5.5b: the bonus is the documented one — tempo, a player who drives before the side settles into
        // passing — and being in the final third is no longer an alternative reason to award it. The
        // OR-clause used to hand every final-third carry an unconditional quarter of a scale unit, which is
        // larger than the whole value of a box delivery: measured, it was the difference between the cross
        // scoring 1.09 against a carry of 1.24 and the delivery being the better ball.
        //
        // AIB-01: the early bonus paid for driving into a touch, which is exactly the wrong advice when an
        // opponent is closing on the carrier (report §1: the bonus was +0.25 and the contact penalty at 1.6 m
        // was only 0.27). Only an actual closing run withdraws it: a stationary defender inside the contact
        // window is priced by the arrival-based contact risk and keeps the tempo bonus, because driving past a
        // player who is standing still is exactly what the bonus is for.
        //
        // AIB-12a (audit 2026-11-23, finding 5): the bonus fell 0.25 → 0.10 and the window 0.70 s → 0.35 s
        // (ActionValueScale carries the corrected arithmetic — the audit's own 1.37 decomposition drops to
        // ≈1.22, not to the 0.95–1.05 the audit quoted, and the remaining ordering is meant to be decided by
        // AIB-07b's advance term and by the re-priced pass set, not by a nominal total).
        bool opponentClosing = IsOpponentClosingOnCarrier(obs, selfXZ, opponentArrivalSeconds);
        float carryBonus = _timeOnBall < ActionValueScale.CarryEarlySeconds && !opponentClosing
            ? ActionValueScale.CarryEarlyBonus
            : 0f;
        float carryGain = ActionValueScale.CarryBaseValue
                          + ActionValueScale.CarrySpaceBonus * bestSpace
                          + carryBonus
                          // AIB-07b: what the open ground ahead is worth, exactly zero when closed.
                          + advanceProspect
                          - ActionValueScale.CarryLinePenalty * linePressure
                          // A9.6a: carrying into a closing defender costs the ball, so it costs value.
                          - contactRisk;
        // Preferences: forward progress (the same one a pass carries) and this player's dribbling.
        float carryPreference = ActionValueScale.ProgressBonus
                                * Math.Clamp((carryTarget.X - selfXZ.X) * obs.AttackingDirectionX / 20f, -1f, 1f);
        carryPreference += (obs.SelfTendencies.DribblePreference01 - PlayerTendencyBaselines.DribblePreference) * 0.45f;
        // A7.2: a team-mate's call is information about the action it is about, paid in the preference slot.
        // A man-on shout makes the turn into the pressure worth less (the carrier cannot see what the caller
        // saw, but he can act on "somebody is on you"); a turn shout makes turning worth a little more.
        if (obs.Calls.Kind == TeamCallKind.ManOn)
            carryPreference -= ActionValueScale.CallCarryPenalty;
        else if (obs.Calls.Kind == TeamCallKind.Turn)
            carryPreference += ActionValueScale.CallTurnCarryBonus;

        float utility = carryGain + carryPreference;
        // AIB-12b (audit 2026-11-23, finding 5): the old gate was `bestSpace > 0.70 && nearestOppDist > 5.0`,
        // so a chaser 5 m behind forced a jog while he sprinted (audit: 8.4 m/s against 6.3 m/s, caught in
        // images 6–7). Sprinting is now about the path ahead, not about how close the nearest body is on any
        // side: the grass must still be open (0.55), no body may be inside tackle reach plus a step (2.40),
        // and the 6 m × 2.2 m corridor the carrier would run through must be clear of anyone who is still
        // there when he arrives.
        bool sprint = bestSpace > SprintGateSpaceThreshold01 &&
                      nearestOppDist > SprintGateContactFloorMeters &&
                      !IsCarryPathBlocked(obs, selfXZ, bestDir);

        return new CarryChoice(
            bestDir, carryTarget, utility, bestSpace, sprint, new ValueTerms(1f, carryGain, 0f, carryPreference));
    }

    /// <summary>
    /// AIB-01: true when an opponent that can arrive at the carrier inside
    /// <see cref="ActionValueScale.CarryEarlySuppressArrivalSeconds"/> is also running at him at least
    /// <see cref="ClosingSpeedThresholdMetersPerSecond"/> fast. The test is on the perceived velocity — the
    /// same belief the decision is allowed to use — so a stationary or retreating defender never withdraws the
    /// early-carry tempo bonus; he is already priced by the arrival-based contact risk.
    /// </summary>
    private static bool IsOpponentClosingOnCarrier(
        in FootballerObservation obs,
        Vector2 selfXZ,
        float opponentArrivalSeconds)
    {
        // Nothing inside the contact window means no withdrawal at all; the field's own estimate is the
        // cheapest guard, so a settled picture does not pay for the per-opponent arrival recomputation.
        if (opponentArrivalSeconds < 0f ||
            opponentArrivalSeconds >= ActionValueScale.CarryEarlySuppressArrivalSeconds)
        {
            return false;
        }

        float fastestClose = 0f;
        for (int index = 0; index < obs.VisiblePlayers.Count; index++)
        {
            PerceivedEntity opponent = obs.VisiblePlayers[index];
            if (opponent.Team == obs.SelfTeam) continue;

            Vector2 opponentXZ = new(opponent.EstimatedPosition.X, opponent.EstimatedPosition.Z);
            float arrival = LocalPressureField.EstimateArrivalSeconds(
                ReachKinematics.FromEntity(opponent), selfXZ);
            if (arrival < 0f || arrival >= ActionValueScale.CarryEarlySuppressArrivalSeconds) continue;

            Vector2 toCarrier = SimMath.SafeNormalize(selfXZ - opponentXZ, Vector2.Zero);
            Vector2 velocityXZ = new(opponent.EstimatedVelocity.X, opponent.EstimatedVelocity.Z);
            float closingSpeed = Vector2.Dot(velocityXZ, toCarrier);
            if (closingSpeed > fastestClose) fastestClose = closingSpeed;
        }

        return fastestClose >= ClosingSpeedThresholdMetersPerSecond;
    }

    /// <summary>
    /// AIB-12b: true when a believed opponent stands in the corridor a sprinting carrier would run through —
    /// up to <see cref="SprintPathHorizonMeters"/> ahead of him and within
    /// <see cref="SprintPathHalfWidthMeters"/> of the chosen direction. Deterministic index loop over the
    /// perceived picture, no allocation.
    ///
    /// The exemptions are the point of the helper: an opponent behind or beside the path
    /// (<c>dot(rel, dir) &lt;= 0</c>), one further than the horizon, one wider than the half-width, and one
    /// genuinely <b>leaving</b> that ground do not stop the sprint. That is what lets the carrier keep the ball
    /// moving at a chaser's own pace instead of dropping to a jog the moment anyone comes within five metres.
    ///
    /// <b>Review correction (2026-11-24):</b> the first cut exempted any body whose velocity along the carry
    /// axis was <c>&lt;= -1 m/s</c> — i.e. one running <i>straight at the carrier</i>. The sign was backwards:
    /// a head-on charge is the most blocking case there is, and it was being treated as "vacating the
    /// corridor", so the carrier sprinted into a tackle. A body vacates the corridor only in the two physical
    /// senses below; along-axis motion towards the carrier can never exempt it. (The relaxed A4 build-up
    /// possession clause is <b>not</b> this bug: measurement keeps that fixture at 260/600 ticks with the sign
    /// fixed, and at 260/600 with the AIB-07b advance term zeroed — the cost is the sprint decision itself; see
    /// <c>CarrierDecisionGateTests</c>.)
    ///
    /// <para><b>Known precision limit (2026-11-24 owner review, note-level).</b> Exemption (a) is a pure speed
    /// test, so a body almost <i>on</i> the carry axis (e.g. 0.05 m off it) drifting sideways at the 1 m/s
    /// threshold is exempt even though he needs (2.2 − 0.05) / 1.0 ≈ 2.2 s to clear the corridor while the
    /// carrier reaches him in a fraction of a second. The exact form would compare the time to clear the
    /// corridor (<c>(SprintPathHalfWidthMeters − |lateral|) / lateral-out speed</c>) with the time to contact
    /// (<c>ahead / max(0, MaxSprintSpeed − along-speed)</c>) and exempt only when he clears first. It is
    /// deferred rather than shipped: it changes behaviour in a rare geometry and `ai59`'s evidence set is the
    /// one under rendered review. Recorded in `docs/KNOWN_ISSUES.md`.</para>
    /// </summary>
    private static bool IsCarryPathBlocked(in FootballerObservation obs, Vector2 selfXZ, Vector2 direction)
    {
        Vector2 perpendicular = new(-direction.Y, direction.X);
        for (int index = 0; index < obs.VisiblePlayers.Count; index++)
        {
            PerceivedEntity opponent = obs.VisiblePlayers[index];
            if (opponent.Team == obs.SelfTeam) continue;

            Vector2 relative = new(opponent.EstimatedPosition.X - selfXZ.X,
                opponent.EstimatedPosition.Z - selfXZ.Y);
            float ahead = Vector2.Dot(relative, direction);
            if (ahead <= 0f) continue;
            if (relative.Length() > SprintPathHorizonMeters) continue;

            float lateral = Vector2.Dot(relative, perpendicular);
            if (MathF.Abs(lateral) > SprintPathHalfWidthMeters) continue;

            Vector2 velocityXZ = new(opponent.EstimatedVelocity.X, opponent.EstimatedVelocity.Z);

            // (a) He is stepping out of the corridor on his own side of the axis: the ground he occupies
            //     now is not the ground he will occupy when the carrier gets there. Signed by which side of
            //     the axis he stands on, so drifting towards the axis is never an escape.
            float lateralSpeed = Vector2.Dot(velocityXZ, perpendicular);
            if (MathF.Sign(lateral) * lateralSpeed >= SprintPathEscapeSpeedMetersPerSecond) continue;

            // (b) He is outrunning the carrier along the carry axis — the gap does not close, so there is no
            //     contact to sprint into. Only a strictly faster body qualifies; a body moving towards the
            //     carrier has a closing speed above the carrier's own and is blocked below.
            if (Vector2.Dot(velocityXZ, direction) >= obs.SelfAttributes.MaxSprintSpeed) continue;

            return true;
        }

        return false;
    }

    /// <summary>Action: carry the ball into the chosen space.</summary>
    private void AddCarryCandidate(in CarryChoice carry, float nearestOppDist)
    {
        var command = new FootballerCommand(
            carry.Direction, carry.Direction, carry.Sprint, nearestOppDist < 2.0f,
            false, false, false, Vector3.Zero, KickIntentType.None, Vector3.UnitX, 0f, 0f);

        _lastCandidates.Add(new UtilityCandidate("DriveWithBall", carry.Utility, command)
        {
            TargetXZ = new Vector3(carry.Target.X, 0f, carry.Target.Y),
            Key = new CandidateKey(CandidateKind.Carry, TargetTag: "forward"),
            Terms = carry.Terms
        });
    }

    /// <summary>
    /// Action: take the nearest opponent on. A genuinely different act from the open-space carry — it
    /// aims past the defender toward his goal — so it is its own candidate with its own key. On the
    /// documented scale it is a carry with a lower retention: the defender can win the ball.
    /// </summary>
    private void AddDribbleCandidate(
        in FootballerObservation obs,
        Vector2 selfXZ,
        Vector2 goalXZ,
        in CarryChoice carry,
        Vector2 nearestOppXZ,
        float nearestOppDist)
    {
        if (nearestOppDist < 0.8f || nearestOppDist > DribbleRadiusMeters) return;

        Vector2 pastTarget = nearestOppXZ
            + SimMath.SafeNormalize(goalXZ - nearestOppXZ, new Vector2(obs.AttackingDirectionX, 0f)) * DribbleBeyondMeters;
        if (!IsInsidePitch(pastTarget, CarryLineMargin)) return;

        Vector2 direction = SimMath.SafeNormalize(pastTarget - selfXZ, carry.Direction);
        // If "past him" is where the ordinary carry already goes, there is no second action to offer.
        if (Vector2.Dot(direction, carry.Direction) > DribbleDistinctDirectionDot) return;

        float score = ScoreCarryDirection(obs, selfXZ, direction, out float space);
        if (score < 0f) return;

        float utility = ActionValueScale.TakeOnBaseValue
                        + ActionValueScale.TakeOnSpaceBonus * space
                        - ActionValueScale.TakeOnRiskCost;
        float takeOnPreference = (obs.SelfTendencies.DribblePreference01 - PlayerTendencyBaselines.DribblePreference) * 0.45f;
        // A7.2: man-on — the caller saw somebody already on him, so committing to beat that man is worse.
        if (obs.Calls.Kind == TeamCallKind.ManOn)
            takeOnPreference -= ActionValueScale.CallTakeOnPenalty;
        utility += takeOnPreference;

        var command = new FootballerCommand(
            direction, direction, false, nearestOppDist < 1.6f,
            false, false, false, Vector3.Zero, KickIntentType.None, Vector3.UnitX, 0f, 0f);

        _lastCandidates.Add(new UtilityCandidate("TakeOnDefender", utility, command)
        {
            TargetXZ = new Vector3(pastTarget.X, 0f, pastTarget.Y),
            Key = new CandidateKey(CandidateKind.Dribble, TargetTag: "beat"),
            Terms = new ValueTerms(1f, utility - takeOnPreference, 0f, takeOnPreference)
        });
    }

    /// <summary>
    /// Action: shield the ball. The body goes between the ball and the nearest opponent and the carrier
    /// waits for an option instead of forcing one. Bounded in time on purpose: an action that always
    /// keeps the ball would otherwise beat every risky pass forever and the side would stand still. Its
    /// value is a carry that goes nowhere: high retention, no progression.
    /// </summary>
    private void AddShieldCandidate(
        in FootballerObservation obs,
        Vector2 selfXZ,
        Vector2 nearestOppXZ,
        float nearestOppDist)
    {
        if (nearestOppDist > ShieldRadiusMeters) return;
        if (_timeOnBall > ShieldMaxTimeOnBall) return;

        Vector2 away = SimMath.SafeNormalize(selfXZ - nearestOppXZ, new Vector2(obs.AttackingDirectionX, 0f));
        Vector2 hold = IsInsidePitch(selfXZ + away * 1.5f, 0.6f) ? away * 0.35f : Vector2.Zero;

        float crowding = 1f - nearestOppDist / ShieldRadiusMeters;
        float patience = 1f - Math.Clamp(_timeOnBall / ShieldMaxTimeOnBall, 0f, 1f);
        float utility = ActionValueScale.ShieldBaseValue
                        + ActionValueScale.ShieldCrowdingBonus * crowding
                        + ActionValueScale.ShieldPatienceBonus * patience;
        // A7.2: "man on" is exactly the event shielding exists for — the caller's shout raises it.
        float callPreference = obs.Calls.Kind == TeamCallKind.ManOn ? ActionValueScale.CallShieldBonus : 0f;

        var command = new FootballerCommand(
            hold, away, false, true, false, false, false, Vector3.Zero,
            KickIntentType.None, Vector3.UnitX, 0f, 0f);

        _lastCandidates.Add(new UtilityCandidate("ShieldBall", utility + callPreference, command)
        {
            Key = new CandidateKey(CandidateKind.Shield, TargetTag: "pressure"),
            Terms = new ValueTerms(1f, utility, 0f, callPreference)
        });
    }

    /// <summary>
    /// Action: clear the ball out of danger. Fires in a player's own third under pressure after a first touch,
    /// or (ai63 Task 4) as a bounded escape clearance when boxed in outside the own third (near a touchline
    /// or surrounded by ≥ 2 close opponents) with no viable pass candidate. Aimed upfield along the touchline
    /// channel so a pinned carrier has an escape rather than stalling.
    /// </summary>
    private void AddClearanceCandidate(
        in FootballerObservation obs,
        Vector2 selfXZ,
        float myAttackProjX,
        float nearestOppDist)
    {
        bool ownThirdClearance = myAttackProjX <= ClearanceOwnThirdProjX &&
                                 nearestOppDist <= ClearancePressureMeters &&
                                 _timeOnBall >= ClearanceMinTimeOnBall;
        bool boxedInEscape = false;
        if (!ownThirdClearance)
        {
            if (nearestOppDist > 2.5f) return;
            bool nearTouchline = _pitch.HalfWidth - MathF.Abs(selfXZ.Y) <= 4.0f;
            if (!nearTouchline && CountOpponentsWithin(obs, selfXZ, 3.5f) < 2) return;
            if (HasAnyViablePassCandidate()) return;
            boxedInEscape = true;
        }

        float lateral = obs.SelfPosition.Z >= 0f ? 1f : -1f;
        float touchlineMarginZ = boxedInEscape ? 3.0f : 1.5f;
        Vector2 aimTarget = new(
            Math.Clamp(selfXZ.X + obs.AttackingDirectionX * 25f, -_pitch.HalfLength + 2f, _pitch.HalfLength - 2f),
            Math.Clamp(selfXZ.Y + lateral * 10f, -_pitch.HalfWidth + touchlineMarginZ, _pitch.HalfWidth - touchlineMarginZ));
        Vector2 direction = SimMath.SafeNormalize(aimTarget - selfXZ, new Vector2(obs.AttackingDirectionX, 0f));

        float utility = boxedInEscape
            ? 0.52f + 0.08f * (1f - Math.Clamp(nearestOppDist / 2.5f, 0f, 1f))
            : ActionValueScale.ClearanceBaseValue +
              ActionValueScale.ClearanceUrgencyBonus * (1f - nearestOppDist / ClearancePressureMeters);

        var command = new FootballerCommand(
            direction, direction, false, false, false, false, false, Vector3.Zero,
            KickIntentType.Clearance, new Vector3(direction.X, 0f, direction.Y), 0.85f, 0f);

        _lastCandidates.Add(new UtilityCandidate("ClearBall", utility, command)
        {
            TargetXZ = new Vector3(aimTarget.X, 0f, aimTarget.Y),
            Key = new CandidateKey(CandidateKind.Clearance, TargetTag: "touchline"),
            Terms = new ValueTerms(1f, utility, 0f, 0f)
        });
    }

    private bool HasAnyViablePassCandidate()
    {
        for (int i = 0; i < _lastCandidates.Count; i++)
        {
            UtilityCandidate candidate = _lastCandidates[i];
            if (candidate.Key.Kind == CandidateKind.Pass && candidate.UtilityScore >= 0.55f)
                return true;
        }
        return false;
    }

    private static int CountOpponentsWithin(in FootballerObservation obs, Vector2 pointXZ, float radiusMeters)
    {
        float radiusSq = radiusMeters * radiusMeters;
        int count = 0;
        for (int i = 0; i < obs.VisiblePlayers.Count; i++)
        {
            PerceivedEntity entity = obs.VisiblePlayers[i];
            if (entity.Team == obs.SelfTeam) continue;
            Vector2 posXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            if (Vector2.DistanceSquared(posXZ, pointXZ) <= radiusSq) count++;
        }
        return count;
    }

    /// <summary>
    /// Action: recycle — a safe ball backwards, including to the keeper when the carrier is deep. Only
    /// teammates the ordinary pass set did not already offer are considered, so a recycle is a second
    /// option and not the same pass under a different label (which would also make commitment flicker
    /// between two identities for one decision).
    /// </summary>
    private void AddRecycleCandidates(
        in FootballerObservation obs,
        Vector2 ballXZ,
        float myAttackProjX)
    {
        int deepestId = 0;
        int deeperId = 0;
        float deepestProj = float.MaxValue;
        float deeperProj = float.MaxValue;

        for (int index = 0; index < obs.VisiblePlayers.Count; index++)
        {
            PerceivedEntity mate = obs.VisiblePlayers[index];
            if (mate.Team != obs.SelfTeam || mate.PlayerId == obs.SelfId) continue;
            if (WasOfferedAsPassTarget(mate.PlayerId)) continue;

            float mateProj = mate.EstimatedPosition.X * obs.AttackingDirectionX;
            if (mateProj > myAttackProjX - 2f) continue;
            if (myAttackProjX - mateProj > RecycleMaxBehindMeters) continue;
            if (mate.Role != TacticalRole.Goalkeeper &&
                mateProj < -_pitch.HalfLength + OwnGoalPassMarginMeters) continue;

            if (mate.Role == TacticalRole.Goalkeeper)
            {
                // Keeper support is an own-third option, and only *off his line*: five metres in front of
                // his own goal a ground ball at him is a ground ball at the goal, which is what the pass
                // margin above exists to stop. A keeper who has swept up the pitch is a safe outlet.
                if (myAttackProjX > KeeperSupportOwnThirdProjX) continue;
                if (mateProj < -_pitch.HalfLength + KeeperSupportStandoffMeters) continue;
                if (Vector2.Distance(ballXZ, new Vector2(mate.EstimatedPosition.X, mate.EstimatedPosition.Z)) > 48f)
                    continue;
            }

            if (mateProj < deepestProj)
            {
                deeperProj = deepestProj;
                deeperId = deepestId;
                deepestProj = mateProj;
                deepestId = mate.PlayerId;
            }
            else if (mateProj < deeperProj)
            {
                deeperProj = mateProj;
                deeperId = mate.PlayerId;
            }
        }

        // AIB-06a: a recycle only pays the territory-loss preference when this decision already offered a
        // forward pass. Reading the finished candidate set (the forward builders ran before this one) is the
        // honest test of "a forward option exists": a pass that was refused by the success model or the
        // offside rule is not an option, and the recycle should not be charged for declining it.
        bool forwardOptionExists = HasForwardPassOption(obs, myAttackProjX);
        if (deepestId != 0) AddRecyclePass(obs, deepestId, ballXZ, forwardOptionExists);
        if (deeperId != 0) AddRecyclePass(obs, deeperId, ballXZ, forwardOptionExists);
    }

    /// <summary>
    /// AIB-06a: true when the carrier's candidate set for this decision already contains a pass whose aim
    /// point gains at least <see cref="RecycleForwardOptionMinProjMeters"/> of territory.
    /// </summary>
    private bool HasForwardPassOption(in FootballerObservation obs, float myAttackProjX)
    {
        float minimumProj = myAttackProjX + RecycleForwardOptionMinProjMeters;
        for (int index = 0; index < _lastCandidates.Count; index++)
        {
            UtilityCandidate candidate = _lastCandidates[index];
            if (candidate.Key.Kind != CandidateKind.Pass) continue;
            if (candidate.TargetXZ is not Vector3 target) continue;
            if (target.X * obs.AttackingDirectionX >= minimumProj) return true;
        }

        return false;
    }

    /// <summary>
    /// Plans and scores one recycle pass, on the same success estimate and the same scale as a forward
    /// pass (A4.4). There is no special "safe ball" bonus any more: the reception value already prices the
    /// progression that recycling gives up, so a recycle wins exactly when the forward options are worse.
    ///
    /// AIB-06a: it pays <see cref="PassPreference"/> like every other pass. It used to hardcode
    /// <c>preference = 0f</c>, so a team-mate 25 m behind the ball collected <b>+0.35</b> more preference
    /// than a team-mate 10 m behind who happened to make the pass-target pre-rank — the report's inversion.
    /// It is charged <see cref="ActionValueScale.BackwardTerritoryCost"/> too when a forward option exists
    /// in this decision, and its lead is bounded by <see cref="FeetPassMaxLeadMeters"/> instead of silently
    /// falling back to <see cref="PassPlanner.DefaultMaxLeadMeters"/> (12 m of lead to nowhere).
    /// </summary>
    private void AddRecyclePass(
        in FootballerObservation obs,
        int mateId,
        Vector2 ballXZ,
        bool forwardOptionExists)
    {
        if (!TryGetVisibleEntity(obs, mateId, out PerceivedEntity mate)) return;

        Vector2 mateXZ = new(mate.EstimatedPosition.X, mate.EstimatedPosition.Z);
        Vector2 mateVelocityXZ = new(mate.EstimatedVelocity.X, mate.EstimatedVelocity.Z);
        PassPlan plan = _passPlanner.PlanGroundPass(
            ballXZ, mateId, mateXZ, mateVelocityXZ, MaxReceivableSpeed,
            extraLeadMeters: 0f,
            extraLeadDirection: Vector2.Zero,
            maxLeadMeters: FeetPassMaxLeadMeters,
            minPower01: 0f,
            // AIB-02c: a recycle is the definition of a pass to a receiver with no run commitment — the
            // planner hedges him at UncommittedLeadMaxMeters instead of letting a 2-5 m jog turn the "safe"
            // backward ball into a lead to nowhere.
            receiverCommittedToRun: false);
        if (!plan.Feasible) return;

        float lane = EvaluateLaneClearance(ballXZ, plan.TargetXZ, obs.VisiblePlayers, obs.SelfTeam);
        PassSuccessTerms terms = _passSuccess.Estimate(obs, mate, plan, lane);
        if (terms.LaneBlocked || terms.CompletionProbability01 < MinViableCompletion01) return;

        ReceptionValueTerms reception = _receptionValue.Evaluate(
            obs, mateId, plan.TargetXZ, terms.DefenderPressure01);
        float loss = ActionValueScale.LossCost(obs.PerceivedBallPosition.X * obs.AttackingDirectionX);

        // AIB-06a: the same preference every pass pays (progress, risk tolerance, directness). The
        // forwardMeters convention is the ordinary pass set's: receiver aim point relative to the carrier.
        float forwardMeters = (plan.TargetXZ.X - obs.SelfPosition.X) * obs.AttackingDirectionX;
        float preference = PassPreference(obs, forwardMeters, terms.LaneClearance01);
        if (forwardOptionExists && forwardMeters < 0f)
            preference -= ActionValueScale.BackwardTerritoryCost;

        ValueTerms valueTerms = new(terms.CompletionProbability01, reception.Value, loss, preference);
        float score = valueTerms.Value;

        bool keeper = mate.Role == TacticalRole.Goalkeeper;
        var command = new FootballerCommand(
            plan.AimDirectionXZ, plan.AimDirectionXZ, false, false, false, false, false, Vector3.Zero,
            KickIntentType.GroundPass, new Vector3(plan.AimDirectionXZ.X, 0f, plan.AimDirectionXZ.Y),
            plan.Power01, 0f)
        {
            IntendedReceiverId = mateId
        };

        _lastCandidates.Add(new UtilityCandidate($"Pass(GroundPass)To#{mateId}", score, command)
        {
            Key = new CandidateKey(CandidateKind.Pass, mateId, keeper ? "Keeper" : "Recycle"),
            Terms = valueTerms,
            // Diagnostic only: the exact recycle aim point.
            TargetXZ = new Vector3(plan.TargetXZ.X, 0f, plan.TargetXZ.Y)
        });
    }

    /// <summary>
    /// Plans the delivery a restart taker plays to <paramref name="mate"/> (A4.9).
    ///
    /// A restart is a free touch, so it gets the same trajectory the carrier uses: the receiver is led to
    /// where he will be when the ball arrives, and the ball stays on the deck whenever the ground route
    /// can reach him. Before this the taker aimed at the receiver's *current feet* with a linear power
    /// ramp (<c>distance / 32</c>): measured on a thirty-minute 11v11 segment, throw-ins completed 29 %
    /// and turned over 57 %, and every long one was a lofted ball that flew 10–20 m past its receiver
    /// (a 22 m throw travelled 40 m, a 30 m throw 50 m).
    ///
    /// Returns false when there is no honest delivery to this mate — the caller keeps its safe fallback,
    /// so a restart is never stranded. The raised ball is only used when the ground route cannot reach
    /// the receiver, and its power comes from <see cref="PassSuccessModel.EstimateLofted"/>, which picks
    /// the lowest power that actually covers the distance.
    /// </summary>
    internal bool TryPlanSetPieceDelivery(
        in FootballerObservation obs,
        in PerceivedEntity mate,
        out FootballerCommand command,
        out bool laneOpen)
    {
        command = default;
        laneOpen = false;

        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        Vector2 mateXZ = new(mate.EstimatedPosition.X, mate.EstimatedPosition.Z);
        Vector2 mateVelocityXZ = new(mate.EstimatedVelocity.X, mate.EstimatedVelocity.Z);
        float distance = Vector2.Distance(ballXZ, mateXZ);

        PassPlan ground = _passPlanner.PlanGroundPass(
            ballXZ, mate.PlayerId, mateXZ, mateVelocityXZ, MaxReceivableSpeed,
            extraLeadMeters: 0f, extraLeadDirection: Vector2.Zero,
            // AIB-02c: restarts are not the audited carrier path and keep their pre-AIB lead bound.
            maxLeadMeters: SetPieceRaisedLeadMaxMeters, minPower01: 0f);

        if (ground.Feasible)
        {
            // The ground route is preferred even when the lane scores as crowded. A flat ball the
            // receiver can actually play beats a lofted one he cannot reach: measured on the same
            // segment, a raised ball into a contest was a turnover every time (0 of 13), while the
            // ground ball at least stays playable. The lane verdict is still reported, so the taker can
            // look for a receiver whose route is open before settling for a crowded one.
            float lane = EvaluateLaneClearance(ballXZ, ground.TargetXZ, obs.VisiblePlayers, obs.SelfTeam);
            PassSuccessTerms terms = _passSuccess.Estimate(obs, mate, ground, lane);
            laneOpen = !terms.LaneBlocked;
            command = BuildPassCommand(ground.AimDirectionXZ, ground.Power01, KickIntentType.GroundPass, mate.PlayerId);
            return true;
        }

        float raisedLane = EvaluateLaneClearance(ballXZ, mateXZ, obs.VisiblePlayers, obs.SelfTeam);
        LoftedPassTerms raised = _passSuccess.EstimateLofted(obs, mate, distance, raisedLane);
        if (!raised.Feasible) return false;

        // The raised route reports its own lane verdict (L8). Without it the caller's "open route first"
        // tier could not see a raised delivery at all, so a blocked lofted ball ranked ahead of an open
        // to-feet ball to another team-mate — the ordering the tier list exists to prevent.
        laneOpen = !raised.LaneBlocked;

        // Aim at where the receiver will be when the ball comes down, bounded like any other lead, and
        // never outside the pitch.
        float speed = mateVelocityXZ.Length();
        float leadSeconds = raised.FlightTimeSeconds;
        float leadLength = MathF.Min(speed * leadSeconds, SetPieceRaisedLeadMaxMeters);
        Vector2 aimPoint = mateXZ + (speed > 1e-4f ? mateVelocityXZ * (leadLength / speed) : Vector2.Zero);
        aimPoint.X = Math.Clamp(aimPoint.X, -_pitch.HalfLength + 1f, _pitch.HalfLength - 1f);
        aimPoint.Y = Math.Clamp(aimPoint.Y, -_pitch.HalfWidth + 1f, _pitch.HalfWidth - 1f);

        Vector2 aim = SimMath.SafeNormalize(aimPoint - ballXZ, new Vector2(obs.AttackingDirectionX, 0f));
        command = BuildPassCommand(aim, raised.Power01, KickIntentType.LoftedPass, mate.PlayerId);
        return true;
    }

    private static FootballerCommand BuildPassCommand(
        Vector2 aimDirection, float power01, KickIntentType kickType, int receiverId) =>
        new(aimDirection, aimDirection, false, false, false, false, false, Vector3.Zero,
            kickType, new Vector3(aimDirection.X, 0f, aimDirection.Y), power01, 0f)
        {
            IntendedReceiverId = receiverId
        };

    /// <summary>Records one refused candidate and the reason, bounded by <see cref="MaxRejections"/>.</summary>
    private void Reject(int playerId, string reason)
    {
        if (_rejectionCount >= _rejections.Length) return;
        _rejections[_rejectionCount++] = new CandidateRejection(playerId, reason);
    }

    /// <summary>True when the pass pre-rank already offered this player a pass this decision.</summary>
    private bool WasOfferedAsPassTarget(int playerId)    {
        for (int index = 0; index < _passTargetCount; index++)
        {
            if (_passTargetIds[index] == playerId) return true;
        }
        return false;
    }

    /// <summary>
    /// Applies <see cref="AiRuntimeConfig.MaxCarrierCandidates"/> by keeping the best-scoring candidates.
    /// A selection sort on scores (ties keep the earlier candidate) rather than <c>List.Sort</c>, which is
    /// unstable and would make the surviving set depend on the sort implementation.
    /// </summary>
    private void TrimToCarrierCap()
    {
        int cap = Math.Max(1, _ai.MaxCarrierCandidates);
        int generated = _lastCandidates.Count;
        TrimCandidatesTo(cap);
        Work.RecordCarrierDecision(generated, _lastCandidates.Count, cap);
    }

    /// <summary>
    /// A9.3: applies <see cref="AiRuntimeConfig.MaxOffBallCandidates"/> to the off-ball set, the way
    /// <see cref="TrimToCarrierCap"/> already bounds the carrier set — best score first, ties keeping the
    /// earlier candidate. Called at the end of every off-ball generation path (the early returns emit a
    /// press, a reception plan, an interception or a defensive act and then leave), so the bound holds for
    /// the whole decision and not only for the shape-following tail.
    ///
    /// At the shipped default (12) the off-ball set is structurally smaller — at most four run paths, a
    /// combination return, a defensive act and the shape candidate — so this trims nothing unless a
    /// configuration lowers the cap; the counter is what lets the evidence say that rather than assume it.
    /// </summary>
    private void TrimToOffBallCap()
    {
        int cap = Math.Max(1, _ai.MaxOffBallCandidates);
        int generated = _lastCandidates.Count;
        TrimCandidatesTo(cap);
        OffBallCandidatesTrimmed = generated - _lastCandidates.Count;
        Work.RecordOffBallDecision(generated, _lastCandidates.Count, cap);
    }

    /// <summary>
    /// Keeps the best-scoring <paramref name="cap"/> candidates, in score order, by selection sort on
    /// scores so equal scores keep the earlier candidate. Shared by both caps (A9.3) so the carrier and
    /// off-ball sets are bounded by the same rule rather than by two near-copies of it.
    /// </summary>
    private void TrimCandidatesTo(int cap)
    {
        int count = _lastCandidates.Count;
        if (count <= cap) return;

        for (int i = 0; i < cap; i++)
        {
            int best = i;
            for (int j = i + 1; j < count; j++)
            {
                if (_lastCandidates[j].UtilityScore > _lastCandidates[best].UtilityScore) best = j;
            }

            if (best == i) continue;
            UtilityCandidate swap = _lastCandidates[i];
            _lastCandidates[i] = _lastCandidates[best];
            _lastCandidates[best] = swap;
        }

        _lastCandidates.RemoveRange(cap, count - cap);
    }

    /// <summary>Distance to the nearest believed opponent, clamped to 20 m when none is in the picture.</summary>
    private static float NearestOpponent(in FootballerObservation obs, Vector2 selfXZ, out Vector2 nearestOppXZ)
    {
        float nearest = 20f;
        nearestOppXZ = selfXZ;
        for (int index = 0; index < obs.VisiblePlayers.Count; index++)
        {
            PerceivedEntity entity = obs.VisiblePlayers[index];
            if (entity.Team == obs.SelfTeam) continue;
            Vector2 opponentXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            float distance = Vector2.Distance(selfXZ, opponentXZ);
            if (distance >= nearest) continue;
            nearest = distance;
            nearestOppXZ = opponentXZ;
        }
        return nearest;
    }

    /// <summary>
    /// AUD-P3: distance from a spot to the nearest believed opponent, or <see cref="float.PositiveInfinity"/>
    /// when the picture contains none. Unlike <see cref="NearestOpponent"/> this is not clamped to a tactical
    /// default — the space probe's crowd radius is an absolute "somebody is standing in that grass" test, so
    /// an absent opponent must read as absent rather than as 20 m.
    /// </summary>
    private static float NearestOpponentDistanceTo(
        Vector2 spotXZ,
        IReadOnlyList<PerceivedEntity> visiblePlayers,
        TeamId selfTeam)
    {
        float nearest = float.PositiveInfinity;
        for (int index = 0; index < visiblePlayers.Count; index++)
        {
            PerceivedEntity entity = visiblePlayers[index];
            if (entity.Team == selfTeam) continue;
            float distance = Vector2.Distance(spotXZ, new Vector2(
                entity.EstimatedPosition.X, entity.EstimatedPosition.Z));
            if (distance < nearest) nearest = distance;
        }
        return nearest;
    }

    /// <summary>
    /// Scores a candidate carry direction: freedom ahead, how safely it stays inside the pitch, and (AIB-04a)
    /// how much it penetrates toward the opponent goal. A direction whose 5 m probe (and the ball just ahead
    /// of the player) would leave the field scores negative, so it can never win. <paramref name="space"/>
    /// stays the freedom term on its own for the sprint decision.
    ///
    /// A2.4: freedom is now a blend of the old distance-to-nearest-opponent measure and the local
    /// pressure field's arrival-based read of the same point. They are blended rather than multiplied on
    /// purpose — both answer "how free is this spot", so applying both in full would charge the same
    /// crowding twice.
    ///
    /// AIB-04a: the old score was only <c>0.55 x space + 0.45 x boundarySafety</c>. Because boundary safety
    /// saturates three metres inside the line and the central lanes are where defenders stand, the emptiest
    /// direction — outward, toward the empty flank — won almost every tick: the reported "always attacking
    /// down the wing". A goalward term is now part of the direction score, plus a final-third inward cut that
    /// ramps with how wide the carrier already is. The room and boundary terms are kept in full: this adds a
    /// direction preference, it does not overrule the physical read of the space.
    /// </summary>
    private float ScoreCarryDirection(
        in FootballerObservation obs,
        Vector2 selfXZ,
        Vector2 dir,
        out float space)
    {
        Vector2 probe = selfXZ + dir * CarryProbeMeters;
        Vector2 ballProbe = probe + dir * 0.55f; // the ball is carried just ahead of the feet

        float distanceFreedom = EvaluateSpaceFreedom(probe, obs.VisiblePlayers, obs.SelfTeam);
        PressureCell cell = _pressure.Read(probe, out bool insideWindow);
        space = insideWindow && _pressure.HasBuild
            ? 0.6f * distanceFreedom + 0.4f * (1f - cell.Pressure01)
            : distanceFreedom;

        if (!IsInsidePitch(probe, 1.2f) || !IsInsidePitch(ballProbe, 0.2f))
            return -1f;

        float marginX = _pitch.HalfLength - MathF.Abs(probe.X);
        float marginZ = _pitch.HalfWidth - MathF.Abs(probe.Y);
        float boundarySafety = Math.Clamp(MathF.Min(marginX, marginZ) / 3.0f, 0f, 1f);
        float score = 0.55f * space + 0.45f * boundarySafety;

        // AIB-04a: the opponent goal is the one fixed point the old score ignored. Everything here is derived
        // from the carrier's own observation and the pitch dimensions — no match state is read. The weight
        // ramps in with pitch position: in the own third the room term must dominate (a flat goal term there
        // walked carriers into the press the build-up gate pins against), while in the final third the goal is
        // what the carry is for.
        Vector2 goalXZ = new(obs.AttackingDirectionX * _pitch.HalfLength, 0f);
        Vector2 toGoal = SimMath.SafeNormalize(goalXZ - selfXZ, new Vector2(obs.AttackingDirectionX, 0f));
        float myProjX = selfXZ.X * obs.AttackingDirectionX;
        float goalwardWeight = CarryGoalwardWeight * Math.Clamp(
            (myProjX - CarryGoalwardRampFromProjX) / (CarryGoalwardRampToProjX - CarryGoalwardRampFromProjX),
            0f, 1f);
        score += goalwardWeight * Math.Clamp(Vector2.Dot(dir, toGoal), -1f, 1f);

        // AIB-04a: in the final third a wide carrier cuts inside. The term ramps with how wide he already is,
        // so a central carrier (or a build-up carry, which is not in the final third) is unaffected; only the
        // inward component is rewarded, so drifting further out never earns it.
        float width = MathF.Abs(selfXZ.Y);
        if (myProjX > CarryFinalThirdProjX && width > CarryInwardMinLateralMeters)
        {
            float width01 = Math.Clamp(
                (width - CarryInwardMinLateralMeters) /
                (_pitch.HalfWidth - CarryInwardMinLateralMeters), 0f, 1f);
            float inwardSign = selfXZ.Y >= 0f ? -1f : 1f;
            Vector2 inward = SimMath.SafeNormalize(
                new Vector2(obs.AttackingDirectionX, inwardSign), new Vector2(obs.AttackingDirectionX, 0f));
            score += CarryFinalThirdInwardWeight * width01 * Math.Clamp(Vector2.Dot(dir, inward), 0f, 1f);
        }

        return score;
    }

    /// <summary>True when a point is inside the pitch with the requested margin from every line.</summary>
    private bool IsInsidePitch(Vector2 pointXZ, float margin) =>
        MathF.Abs(pointXZ.X) <= _pitch.HalfLength - margin &&
        MathF.Abs(pointXZ.Y) <= _pitch.HalfWidth - margin;

    private static Vector2 RotateXZ(Vector2 dir, float radians)
    {
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        return new Vector2(dir.X * cos - dir.Y * sin, dir.X * sin + dir.Y * cos);
    }

    /// <summary>Generates the candidate set for a player who does not have the ball.</summary>
    public void GenerateOffBallCandidates(
        in FootballerObservation obs,
        Vector3 oppGoalCenter,
        Vector3 ownGoalCenter,
        Vector2 myInterceptXZ,
        bool isPrimaryBallResponder,
        float offsideLineProjX)
    {
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        Vector2 anchorXZ = new(obs.TacticalAnchorPosition.X, obs.TacticalAnchorPosition.Z);
        Vector2 dirToBall = Vector2.Distance(selfXZ, ballXZ) > 1e-4f ? Vector2.Normalize(ballXZ - selfXZ) : new Vector2(obs.AttackingDirectionX, 0f);

        // 1. Primary Ball Responder: Receive Pass (if in possession) OR Sprint & Tackle Carrier (if defending)
        if (isPrimaryBallResponder)
        {
            float distToBallNow = Vector2.Distance(selfXZ, ballXZ);

            if (obs.PossessingTeam != obs.SelfTeam)
            {
                // A6.1/A6.2: the press is angled and its depth follows the cover behind it. A6.2's angle
                // is chosen around the carrier — inside a touchline carrier, because the line is then the
                // second defender, and goal-side of a central one, because that is the exit that hurts.
                // The depth is the balance: a presser nobody covers jockeys instead of committing, so an
                // unsupported press loses to delay rather than to a beaten tackle.
                Vector2 ballVelocityXZ = new(obs.PerceivedBallVelocity.X, obs.PerceivedBallVelocity.Z);
                PressSituation press = PressPolicy.BuildSituation(obs, _pitch);
                Vector2 ownGoalSide = PressPolicy.GoalSideXZ(ballXZ, obs, _pitch);

                Vector2 chasePoint;
                if (press.HasCarrier)
                {
                    // A presser still behind a moving carrier has to cut him off before any angle matters:
                    // one fixed-speed chase can never close the last metre. Once he is goal-side, the
                    // approach is A6.2's angled one.
                    float carrierSpeed = press.CarrierVelocityXZ.Length();
                    bool goalSideOfCarrier =
                        Vector2.Dot(selfXZ - press.CarrierXZ, ownGoalSide) > 0f;
                    if (!goalSideOfCarrier && carrierSpeed > 1.5f)
                    {
                        chasePoint = PursuitPoint(
                              ReachKinematics.FromObservation(obs), press.CarrierXZ, press.CarrierVelocityXZ) +
                          ownGoalSide * 0.8f;
                    }
                    else if (obs.Trap.IsPresser)
                    {
                        // A8.4: the trap names the pressure angle for the whole team (A6.2's own angled-point
                        // scoring, evaluated once from the shared picture). The presser applies the offset to
                        // the carrier in his own picture: a standing point, never a reach, a speed or a
                        // tackle; the tackle is still the execution layer's contact.
                        chasePoint = new Vector2(
                            Math.Clamp(press.CarrierXZ.X + obs.Trap.PressOffsetXZ.X,
                                -_pitch.HalfLength + 1f, _pitch.HalfLength - 1f),
                            Math.Clamp(press.CarrierXZ.Y + obs.Trap.PressOffsetXZ.Y,
                                -_pitch.HalfWidth + 1f, _pitch.HalfWidth - 1f));
                    }
                    else
                    {
                        chasePoint = PressPolicy.PressTarget(
                            obs, press, _pitch, PressPolicy.PressStandoffFor(press), support: false);
                    }
                }
                else
                {
                    chasePoint = PursuitPoint(
                        ReachKinematics.FromObservation(obs), ballXZ, ballVelocityXZ) + ownGoalSide * 0.6f;
                }

                Vector2 toBallDirect = chasePoint - selfXZ;
                Vector2 pressDir = toBallDirect.LengthSquared() > 1e-4f ? Vector2.Normalize(toBallDirect) : dirToBall;
                // Audit F18: pressing aggression moves the commitment range (~1.24 m at the 0.60 baseline).
                float tackleTriggerMultiplier = 1.65f
                    + 0.35f * (obs.SelfTendencies.PressingAggression01 - PlayerTendencyBaselines.PressingAggression);
                bool attemptTackle = _tackleCooldown <= 0f &&
                                     distToBallNow <= obs.SelfAttributes.ReachRadius * tackleTriggerMultiplier;
                if (attemptTackle) _tackleCooldown = TackleCooldownSeconds;

                var pressCmd = new FootballerCommand(
                    pressDir, dirToBall, true, false,
                    attemptTackle, false, false, Vector3.Zero,
                    KickIntentType.None, Vector3.UnitX, 0f, 0f);

                _lastCandidates.Add(new UtilityCandidate(attemptTackle ? "ExecuteTackle" : "CloseDownCarrier", 0.99f, pressCmd)
                {
                    // Where the press is going (A6.2). The trace and the inspector show the approach point,
                    // which is what makes "angled pressure" observable rather than a claim about a direction.
                    TargetXZ = new Vector3(chasePoint.X, 0f, chasePoint.Y),
                    // The target is the carrier: pressing a different player is a different act, and a
                    // commitment must not survive a change of who is being pressed.
                    Key = new CandidateKey(CandidateKind.Press, obs.BallControllingPlayerId ?? 0,
                        attemptTackle ? "tackle" : "close")
                });
                TrimToOffBallCap();
                return;
            }

            // Receiving a friendly pass.
            //
            // A7.3: the intended receiver plans the first touch before the ball arrives. When the plan
            // exists it replaces the single fixed intercept ray with the set of feasible
            // (body orientation, touch) pairs — kill, open body, stride touch, shield, escape — each
            // carrying its own meeting point, facing and sprint/shield flags. The command vocabulary is
            // unchanged: A7.4 owns the command/execution boundary, so a plan can only express itself
            // through movement and body orientation the player already has.
            if (_ai.EnableReceptionPlanning && AddReceptionCandidates(obs, selfXZ, dirToBall))
            {
                TrimToOffBallCap();
                return;
            }

            // The pre-A7.3 path, kept as fallback when reception planning is off or returns no feasible plan:
            // use the same tight approach deadband and sprint cutoff as AddReceptionCandidates so a
            // receiver does not jog inside 1.5 m on a fast ball.
            Vector2 toIntercept = myInterceptXZ - selfXZ;
            float distIntercept = toIntercept.Length();
            Vector2 moveDir = distIntercept > ReceptionArrivalDeadbandMeters
                ? toIntercept / distIntercept
                : dirToBall * 0.35f;

            var receiveCmd = new FootballerCommand(
                moveDir, dirToBall, distIntercept > ReceptionSprintDeadbandMeters, false,
                false, false, false, Vector3.Zero,
                KickIntentType.None, Vector3.UnitX, 0f, 0f);

            _lastCandidates.Add(new UtilityCandidate("InterceptPassRay", 0.98f, receiveCmd)
            {
                Key = new CandidateKey(CandidateKind.Intercept, TargetTag: "ray")
            });
            TrimToOffBallCap();
            return;
        }

        // 1b. A6.1: the defensive half of the candidate set. The old second-defender challenge is replaced
        //     by the team's pressure-cover-balance: exactly one team-mate may step in beside the presser,
        //     only when the space behind the press is covered (or he is himself goal-side, so stepping
        //     forward abandons nothing), and everybody else gets screen/track/drop alternatives instead
        //     of independently abandoning the same space. A primary act is this player's defensive
        //     decision for the tick and replaces the duty; a supporting act competes with it.
        if (AddDefensiveCandidates(obs))
        {
            TrimToOffBallCap();
            return;
        }

        // A8.1: a live combination can ask this player for one more thing — a one-two's initiator must run
        // into the frozen return region — and a participant whose move has gone abandons it. The candidate
        // competes with the duty below rather than replacing it, so shape still has a say in whether the
        // player actually goes.
        if (_ai.EnableCombinations)
            AddCombinationCandidates(obs, offsideLineProjX);

        // 2. Job-directed movement (audit F04). The team assigns a persistent job (outlet, third
        //    player, depth run, width, overlap, cover, marker, the defensive line...); score a couple
        //    of reachable target points around its anchor instead of a single formation step.
        if (obs.TacticalJobAnchor is Vector3 jobAnchor &&
            obs.TacticalJob is not TacticalJob.Shape)
        {
            // A5.1/A5.2: a receiving duty's options are the bounded run paths, not one anchor. The duty
            // still decides *who* runs; the path decides where and when.
            bool receivingDuty = JobRegionScoring.IsReceiving(obs.TacticalJob) &&
                                 obs.TacticalJob != TacticalJob.BallResponder &&
                                 obs.PossessingTeam == obs.SelfTeam;
            if (receivingDuty)
                AddRunPathCandidates(obs, new Vector2(jobAnchor.X, jobAnchor.Z), offsideLineProjX);
            else
                AddJobCandidates(obs, new Vector2(jobAnchor.X, jobAnchor.Z), offsideLineProjX);
        }

        // 3. All other 9 outfield players hold lanes, offer clean angles, and STAY ONSIDE — until the ball
        //    has been played to them (A5.4), at which point the line is history and pulling them back
        //    would be cancelling a legal run.
        Vector2 targetSpotXZ = anchorXZ;

        if (obs.TacticalJob == TacticalJob.DefensiveLine && obs.TacticalJobAnchor is Vector3 lineTarget)
        {
            // The allocator already placed this duty on the shared band, accounting for the ball side,
            // the far side and the line's caps. Repulsion and formation-anchor drift belong to a player
            // taking up a support spot; applying them here is what moved line defenders up to 13.9 m off
            // their assigned depth while their job said otherwise.
            targetSpotXZ = new Vector2(lineTarget.X, lineTarget.Z);
        }
        else
        {
            if (obs.PossessingTeam == obs.SelfTeam && !OffsideTactics.IsReleased(obs))
            {
                float phaseWave = obs.MatchTimeSeconds * 1.2f + obs.SelfId * 1.7f;
                if (obs.SelfRole is TacticalRole.Striker or TacticalRole.Winger)
                {
                    float desiredProjX = (anchorXZ.X * obs.AttackingDirectionX) + 3.5f + 2.5f * MathF.Sin(phaseWave);
                    // Hold the tactical margin so attackers never camp offside (A5.4).
                    float onsideProjX = MathF.Min(desiredProjX, OffsideTactics.MaxAttackingProjection(offsideLineProjX));
                    targetSpotXZ.X = obs.AttackingDirectionX * Math.Clamp(onsideProjX, -_pitch.HalfLength + 4f, _pitch.HalfLength - 4f);
                }
                else if (obs.SelfRole == TacticalRole.CentralMidfielder)
                {
                    float desiredProjX = (anchorXZ.X * obs.AttackingDirectionX) + 2.5f * MathF.Cos(phaseWave);
                    float onsideProjX = MathF.Min(desiredProjX, offsideLineProjX - OffsideTactics.MidfieldMarginMeters);
                    targetSpotXZ.X = obs.AttackingDirectionX * Math.Clamp(onsideProjX, -_pitch.HalfLength + 5f, _pitch.HalfLength - 5f);
                }
            }

            // Anti-crowding repulsion so teammates keep clean passing spacing
            Vector2 repulsion = Vector2.Zero;
            foreach (var mate in obs.VisiblePlayers)
            {
                if (mate.Team != obs.SelfTeam || mate.Role == TacticalRole.Goalkeeper) continue;
                Vector2 mateXZ = new(mate.EstimatedPosition.X, mate.EstimatedPosition.Z);
                Vector2 awayFromMate = selfXZ - mateXZ;
                float d = awayFromMate.Length();
                if (d < 8.5f && d > 1e-3f)
                {
                    repulsion += (awayFromMate / d) * ((8.5f - d) * 1.25f);
                }
            }

            targetSpotXZ += repulsion;

            // Audit F18: positional discipline (player) and positional freedom (team) bound how far a
            // player strays from the formation anchor when taking up a support spot. At the neutral
            // baselines (discipline 0.70, freedom 0.55) this allows ~7 m of drift; a free role may roam
            // further, a disciplined one holds the structure.
            float anchorDriftMeters = (2.5f + 12f * (1f - obs.SelfTendencies.PositionalDiscipline01))
                                      * (0.70f + 0.80f * obs.TeamProfile.PositionalFreedom01);
            Vector2 driftVec = targetSpotXZ - anchorXZ;
            float driftDist = driftVec.Length();
            if (driftDist > anchorDriftMeters && driftDist > 1e-3f)
                targetSpotXZ = anchorXZ + (driftVec / driftDist) * anchorDriftMeters;

            // Re-enforce the tactical margin after repulsion when in possession — and only before release: a
            // receiver the ball is travelling to is not dragged back behind a line the law has already judged
            // (A5.4).
            if (obs.PossessingTeam == obs.SelfTeam && !OffsideTactics.IsReleased(obs) &&
                (targetSpotXZ.X * obs.AttackingDirectionX) > OffsideTactics.MaxAttackingProjection(offsideLineProjX))
            {
                targetSpotXZ.X = obs.AttackingDirectionX * OffsideTactics.MaxAttackingProjection(offsideLineProjX);
            }
        }

        targetSpotXZ.X = Math.Clamp(targetSpotXZ.X, -_pitch.HalfLength + 3.5f, _pitch.HalfLength - 3.5f);
        targetSpotXZ.Y = Math.Clamp(targetSpotXZ.Y, -_pitch.HalfWidth + 2.5f, _pitch.HalfWidth - 2.5f);

        Vector2 toTarget = targetSpotXZ - selfXZ;
        float distTarget = toTarget.Length();
        Vector2 moveVec = distTarget > 0.45f
            ? (toTarget / distTarget) * Math.Clamp(distTarget / 3.5f, 0.35f, 1.0f)
            : Vector2.Zero;

        var shapeCmd = new FootballerCommand(
            moveVec, dirToBall, distTarget > 8.0f, false,
            false, false, false, Vector3.Zero, KickIntentType.None, Vector3.UnitX, 0f, 0f);

        _lastCandidates.Add(new UtilityCandidate(obs.PossessingTeam == obs.SelfTeam ? "OnsideSupportLane" : "HoldDefensiveZone", 0.88f, shapeCmd)
        {
            TargetXZ = new Vector3(targetSpotXZ.X, 0f, targetSpotXZ.Y),
            // The possession side is part of the identity: supporting the carrier and holding a
            // defensive zone are different duties, and a commitment must not carry across a turnover.
            Key = new CandidateKey(CandidateKind.Shape,
                TargetTag: obs.PossessingTeam == obs.SelfTeam ? "support" : "defend")
        });

        TrimToOffBallCap();
    }

    /// <summary>
    /// AUD-P4 / Fix 3.3 (2026-11-23): planar deadband, in metres, inside which the receiver stops steering
    /// for the plan's meeting point and uses the near-field fallback. It used to be a bare <c>0.45f</c>.
    ///
    /// The other half of Fix 3.3 keeps every body-offset meeting point within <c>0.55 m</c> of the ball's
    /// predicted path (<see cref="ReceptionPlanner"/>), against the <c>1.24 m</c> execution receiving reach
    /// (<c>BallContactRules.ReachRadius</c>: 0.75 m x 1.65). A 0.45 m deadband could therefore leave the
    /// receiver's body <c>0.45 + 0.55 = 1.00 m</c> off the path while the pass was still arriving — 0.24 m
    /// inside the reach, which an 18 m/s ball covers in 13 ms — so he stood still and watched it roll past
    /// him. At 0.15 m the worst case is <c>0.15 + 0.55 = 0.70 m</c>, 0.54 m inside the reach.
    /// </summary>
    internal const float ReceptionArrivalDeadbandMeters = 0.15f;

    /// <summary>ai63 (Task 2.2): sprint cutoff distance for reception approach; lowers the old 1.5 m cutoff so a tight reception keeps sprinting into trap reach.</summary>
    internal const float ReceptionSprintDeadbandMeters = 0.35f;

    /// <summary>
    /// A7.3: turns the reception plan into this decision's candidate set. Every feasible
    /// (orientation, touch) pair becomes one candidate — the receiver's options are now the touches he
    /// could take, not a single intercept ray — and the pair's score is its utility.
    ///
    /// Returns false when there is nothing to plan (not the intended receiver, the flight cannot be
    /// predicted, or no pair is feasible). The caller then uses the exact pre-A7.3 command, so the
    /// ablation reproduces the old build rather than a near-miss of it.
    /// </summary>
    private bool AddReceptionCandidates(in FootballerObservation obs, Vector2 selfXZ, Vector2 dirToBall)
    {
        if (!_receptionPlanner.TryPlan(obs, out _)) return false;

        ReachKinematics reach = ReachKinematics.FromObservation(obs);
        float jogSpeed = MathF.Max(0.5f, TimeToReachModel.SpeedCap(reach, sprint: false));
        float sprintSpeed = MathF.Max(0.5f, TimeToReachModel.SpeedCap(reach, sprint: true));
        float brakeDecel = MathF.Max(1f, obs.SelfAttributes.Acceleration * 1.45f * obs.SelfBalance01);
        Vector2 selfVelXZ = new(obs.SelfVelocity.X, obs.SelfVelocity.Z);
        float speed = selfVelXZ.Length();
        Vector2 perceivedBallXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        float distToBallNow = Vector2.Distance(selfXZ, perceivedBallXZ);
        Vector2 ballVelXZ = new(obs.PerceivedBallVelocity.X, obs.PerceivedBallVelocity.Z);
        float ballSpeedXZ = ballVelXZ.Length();
        Vector2 ballDirXZ = ballSpeedXZ > 1e-3f ? ballVelXZ / ballSpeedXZ : dirToBall;

        bool added = false;
        for (int i = 0; i < _receptionPlanner.OptionCount; i++)
        {
            ReceptionOption option = _receptionPlanner.GetOption(i);
            if (!option.Feasible) continue;

            ReceptionPlan plan = _receptionPlanner.PlanForOption(i);
            Vector2 toMeeting = plan.MeetingPointXZ - selfXZ;
            float distance = toMeeting.Length();

            // ai64 (Task 3.3): engage ShieldBall stance slowdown only once actually at the reception zone,
            // so a pressed receiver still sprints to meet the pass early.
            bool shieldMove = plan.Shield &&
                              ((distance <= 1.25f && plan.ArrivalSeconds <= 0.35f) || distToBallNow <= 2.5f);

            // ai64 (Task 2.1): velocity-matched arrival braking so a fast receiver does not blast past
            // MeetingPointXZ. StrideTouch keeps running unless inside 0.8 m and running across the pass line.
            bool applyArrivalControl = plan.TouchKind != ReceptionTouchKind.StrideTouch;
            if (!applyArrivalControl && distance < 0.8f && speed > 1.5f)
            {
                float alongBallAxis = MathF.Abs(Vector2.Dot(selfVelXZ / speed, ballDirXZ));
                if (alongBallAxis < 0.5f)
                    applyArrivalControl = true;
            }

            Vector2 moveDir;
            bool sprint;
            if (!applyArrivalControl)
            {
                moveDir = distance > ReceptionArrivalDeadbandMeters
                    ? toMeeting / distance
                    : dirToBall * 0.35f;
                sprint = plan.Sprint && distance > ReceptionSprintDeadbandMeters;
            }
            else if (distance <= ReceptionArrivalDeadbandMeters)
            {
                // Already at the meeting point: hold position (zero target velocity triggers max braking
                // if still carrying momentum).
                moveDir = Vector2.Zero;
                sprint = false;
            }
            else
            {
                Vector2 dirToMeeting = toMeeting / distance;
                float vAlong = Vector2.Dot(selfVelXZ, dirToMeeting);
                float maxStopSpeed = MathF.Sqrt(
                    2f * brakeDecel * MathF.Max(0f, distance - ReceptionArrivalDeadbandMeters));
                float pacedSpeed = plan.ArrivalSeconds > 0.08f
                    ? (distance / plan.ArrivalSeconds) * 1.15f
                    : sprintSpeed;
                float targetSpeedLimit = plan.Pressure01 >= 0.35f || plan.Sprint
                    ? maxStopSpeed
                    : MathF.Min(maxStopSpeed, MathF.Max(pacedSpeed, 1.5f));

                bool overshooting = vAlong > maxStopSpeed || (distance <= 1.2f && vAlong < -0.5f);
                bool fastEnoughToThrottle = speed >= maxStopSpeed * 0.75f ||
                                            (vAlong * plan.ArrivalSeconds > distance + 0.5f && speed > 3.5f);

                if (overshooting)
                {
                    sprint = false;
                    // Velocity-correcting low-magnitude command toward MeetingPointXZ: StepPlayerLocomotion
                    // fires full 1.45 * Acceleration braking against both excess forward speed and lateral drift.
                    float brakeCap = MathF.Min(targetSpeedLimit * 0.5f, jogSpeed * 0.5f);
                    moveDir = dirToMeeting * (brakeCap / jogSpeed);
                }
                else if (fastEnoughToThrottle)
                {
                    sprint = plan.Sprint && distance > ReceptionSprintDeadbandMeters && targetSpeedLimit > jogSpeed;
                    float speedCap = sprint ? sprintSpeed : jogSpeed;
                    float throttle = Math.Clamp(targetSpeedLimit / speedCap, 0.15f, 1f);
                    moveDir = dirToMeeting * throttle;
                }
                else
                {
                    sprint = plan.Sprint && distance > ReceptionSprintDeadbandMeters;
                    moveDir = dirToMeeting;
                }
            }

            var command = new FootballerCommand(
                moveDir, plan.FacingXZ, sprint, shieldMove,
                false, false, false, Vector3.Zero,
                KickIntentType.None, Vector3.UnitX, 0f, 0f)
            {
                // A7.4: the plan crosses the command boundary here, as the shape of a legal contact.
                // It never buys the contact itself — execution still resolves reach, height, facing and
                // arbitration, and a poor first touch still only gets a fraction of the intended touch.
                // A7.5: it carries the plan's meeting point too, so execution can tell a touch that met the
                // ball where the receiver expected it from one built on a wrong guess.
                ReceptionIntent = new ReceptionIntent(plan.TouchKind)
                {
                    MeetingPointXZ = plan.MeetingPointXZ
                }
            };

            CandidateKey key = new(CandidateKind.Intercept, TargetTag: ReceptionPlanner.TagOf(plan.TouchKind));
            _lastCandidates.Add(new UtilityCandidate(ReceptionPlanner.NameOf(plan.TouchKind), plan.Score, command)
            {
                TargetXZ = new Vector3(plan.MeetingPointXZ.X, 0f, plan.MeetingPointXZ.Y),
                Key = key
            });

            _receptionKeys[_receptionPlanCount] = key;
            _receptionPlans[_receptionPlanCount] = plan;
            _receptionPlanCount++;
            added = true;
        }

        return added;
    }

    /// <summary>
    /// A7.3: the plan behind one of this decision's reception candidates, if <paramref name="key"/>
    /// names one. The controller records it on the intent (A7.4's seam) and the inspector/probe read it.
    /// </summary>
    public bool TryGetReceptionPlan(in CandidateKey key, out ReceptionPlan plan)
    {
        for (int i = 0; i < _receptionPlanCount; i++)
        {
            if (!_receptionKeys[i].Equals(key)) continue;
            plan = _receptionPlans[i];
            return true;
        }

        plan = ReceptionPlan.None;
        return false;
    }

    /// <summary>
    /// A6.1/A6.2: turns <see cref="PressPolicy"/>'s answer into this player's defensive candidates.
    ///
    /// Returns true when the policy offered a <b>primary</b> act — support press, delay, cover or hold.
    /// A primary act *is* the player's defensive decision for the tick (the balance already chose him as
    /// the second man, the cover or the holder), so the caller stops here instead of also offering his
    /// team duty; that is what keeps two defenders from independently abandoning the same space. A
    /// supporting act (screen, track, drop) is a candidate on the same scale as the duty and is offered
    /// alongside it, so it has to earn the movement.
    /// </summary>
    private bool AddDefensiveCandidates(in FootballerObservation obs)
    {
        PressSituation situation = PressPolicy.BuildSituation(obs, _pitch);
        int count = PressPolicy.Build(obs, situation, _pitch, _defenceBuffer);
        if (count == 0) return false;

        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        Vector2 toBall = ballXZ - selfXZ;
        Vector2 faceDir = toBall.LengthSquared() > 1e-4f ? Vector2.Normalize(toBall) : obs.SelfFacingXZ;
        bool primary = false;

        for (int i = 0; i < count; i++)
        {
            PressCandidate candidate = _defenceBuffer[i];
            if (!SimMath.IsFinite(candidate.TargetXZ)) continue;

            Vector2 toTarget = candidate.TargetXZ - selfXZ;
            float distance = toTarget.Length();
            Vector2 moveDir = distance > 0.35f
                ? (toTarget / distance) * Math.Clamp(distance / 3.0f, 0.45f, 1.0f)
                : Vector2.Zero;

            // A body challenge is a discrete act: it is emitted once, on the cooldown, and the execution
            // layer still decides whether the contact is in reach (AGENTS §4.4, audit F18).
            bool attemptTackle = candidate.WantsTackle && _tackleCooldown <= 0f;
            if (attemptTackle) _tackleCooldown = TackleCooldownSeconds;

            bool urgent = distance > 4f && candidate.Action is PressAction.SupportPress
                or PressAction.Cover or PressAction.Track or PressAction.Drop;
            var command = new FootballerCommand(
                moveDir, faceDir, urgent, false, attemptTackle, false, false, Vector3.Zero,
                KickIntentType.None, Vector3.UnitX, 0f, 0f);

            _lastCandidates.Add(new UtilityCandidate(
                PressPolicy.ActionNameOf(candidate.Action, attemptTackle), candidate.Score, command)
            {
                TargetXZ = new Vector3(candidate.TargetXZ.X, 0f, candidate.TargetXZ.Y),
                Key = DefensiveKey(candidate, attemptTackle)
            });

            if (candidate.IsPrimary) primary = true;
        }

        return primary;
    }

    /// <summary>
    /// The stable identity of a defensive act, so a commitment survives the target moving but not a
    /// change of act or of target player. The support press keeps the old challenge's identity
    /// (<see cref="CandidateKind.Challenge"/> against the carrier) because it <i>is</i> the old challenge,
    /// now resolved by the balance instead of by "I happen to be second nearest".
    /// </summary>
    private static CandidateKey DefensiveKey(in PressCandidate candidate, bool attemptTackle) =>
        candidate.Action switch
        {
            PressAction.SupportPress => new CandidateKey(
                CandidateKind.Challenge, candidate.TargetPlayerId, attemptTackle ? "tackle" : "body"),
            PressAction.Delay => new CandidateKey(CandidateKind.Press, candidate.TargetPlayerId, "delay"),
            PressAction.Cover => new CandidateKey(CandidateKind.Shape, TargetTag: "cover"),
            PressAction.Screen => new CandidateKey(CandidateKind.Shape, TargetTag: "screen"),
            PressAction.Track => new CandidateKey(CandidateKind.Job, TargetTag: "track"),
            PressAction.Drop => new CandidateKey(CandidateKind.Job, TargetTag: "drop"),
            PressAction.Hold => new CandidateKey(CandidateKind.Shape, TargetTag: "hold"),
            _ => CandidateKey.None
        };

    /// <summary>
    /// Where to run to meet a moving carrier (A2.1). Three "distance ÷ max speed" iterations used to
    /// stand in for this; the reach model knows whether this defender is already running at the
    /// carrier or turning from a standstill, so the lead is no longer the same for both.
    /// </summary>
    private static Vector2 PursuitPoint(in ReachKinematics kinematics, Vector2 targetXZ, Vector2 targetVelXZ)
    {
        if (!SimMath.IsFinite(targetVelXZ) || targetVelXZ.LengthSquared() < 0.25f) return targetXZ;

        return TimeToReachModel.TrySolveInterception(
            kinematics, targetXZ, targetVelXZ, out Vector2 meeting, out _,
            TimeToReachModel.DefaultHorizonSeconds)
            ? meeting
            : targetXZ;
    }

    private static bool TryGetVisibleEntity(
        in FootballerObservation obs,
        int playerId,
        out Vector2 positionXZ,
        out Vector2 velocityXZ)
    {
        foreach (var entity in obs.VisiblePlayers)
        {
            if (entity.PlayerId != playerId) continue;
            positionXZ = new Vector2(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            velocityXZ = new Vector2(entity.EstimatedVelocity.X, entity.EstimatedVelocity.Z);
            return true;
        }

        positionXZ = default;
        velocityXZ = default;
        return false;
    }

    /// <summary>
    /// The whole believed entity, for callers that need its capability band or its sighting age — the
    /// pass-success estimate measures an opponent with <see cref="PerceivedEntity.Capability"/>, never
    /// a live attribute.
    /// </summary>
    private static bool TryGetVisibleEntity(in FootballerObservation obs, int playerId, out PerceivedEntity entity)
    {
        for (int index = 0; index < obs.VisiblePlayers.Count; index++)
        {
            PerceivedEntity candidate = obs.VisiblePlayers[index];
            if (candidate.PlayerId != playerId) continue;
            entity = candidate;
            return true;
        }

        entity = default;
        return false;
    }

    /// <summary>
    /// A5.1/A5.2: emits this decision's run paths as movement candidates.
    ///
    /// The utility is the duty's own base value plus the path's seven scored terms, the same shape the
    /// anchor candidates used (base + lane + space − travel), so a run competes with the shape candidate
    /// and with the other paths on one scale instead of in its own units. The path's current target (its
    /// hold point while the run is timed, its meeting region afterwards) is what the feet are asked for;
    /// <see cref="UtilityCandidate.TargetXZ"/> publishes it so a test can see where the player was sent.
    ///
    /// With no path available for this duty (no space behind the line, nobody to pin, too far from the
    /// box) the duty's anchor is offered instead: a duty with nowhere to run still has a place to stand.
    /// </summary>
    private void AddRunPathCandidates(in FootballerObservation obs, Vector2 dutyAnchorXZ, float offsideLineProjX)
    {
        int count = _runs.Generate(obs, offsideLineProjX, obs.Tick, _ai.MaxRunCandidates);
        if (count == 0)
        {
            AddJobCandidates(obs, dutyAnchorXZ, offsideLineProjX);
            return;
        }

        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        float baseUtility = JobBaseUtility(obs.TacticalJob);
        Vector2 faceDir = SimMath.SafeNormalize(ballXZ - selfXZ, obs.SelfFacingXZ);

        for (int i = 0; i < count; i++)
        {
            RunPath path = _runs.PathAt(i);
            RunScoreTerms terms = _runs.TermsAt(i);
            if (!float.IsFinite(terms.Score)) continue;

            Vector2 targetXZ = path.TargetFor(selfXZ, obs.Tick);
            Vector2 toTarget = targetXZ - selfXZ;
            float distance = toTarget.Length();
            Vector2 moveDir = distance > 0.4f
                ? (toTarget / distance) * Math.Clamp(distance / 3.5f, 0.35f, 1.0f)
                : Vector2.Zero;

            // A forward run is a sprint from the first metre; a hold is a jog at most, and never while the
            // run is being timed into the box.
            bool forward = path.Kind is RunKind.InBehind or RunKind.Overlap or RunKind.Underlap
                or RunKind.LateBoxArrival or RunKind.BlindSideArrival;
            bool sprint = forward && !path.IsHolding(obs.Tick) && distance > 3.0f;

            var command = new FootballerCommand(
                moveDir, faceDir, sprint, false, false, false, false, Vector3.Zero,
                KickIntentType.None, Vector3.UnitX, 0f, 0f);

            _lastCandidates.Add(new UtilityCandidate(
                RunTags.ActionNameOf(path.Kind),
                // A7.2: a team-mate asked this player to run. The request raises the run candidates' value;
                // it does not invent a run for a duty that has none, and it is not a claim on the ball —
                // A5.3's board is still the only place a pass to a run is promised.
                baseUtility + terms.Score + (obs.Calls.Kind == TeamCallKind.Run ? ActionValueScale.CallRunBonus : 0f),
                command)
            {
                TargetXZ = new Vector3(targetXZ.X, 0f, targetXZ.Y),
                Key = RunTags.KeyOf(path.Kind)
            });
        }
    }

    /// <summary>
    /// A9.3 part 2: the job candidates' display names, built once. A job candidate's identity is its
    /// <see cref="CandidateKey"/> (kind + target tag + adjusted flag); the action name is a label for the
    /// trace, the inspector and the probes, so caching the text cannot change a decision. Indexed by the
    /// enum's own value: <see cref="TacticalJob"/> is contiguous from <see cref="TacticalJob.Shape"/>.
    /// </summary>
    private static readonly string[] JobActionNames = BuildJobNameTable(prefix: "Job:", suffix: string.Empty);
    private static readonly string[] JobAdjustedActionNames = BuildJobNameTable(prefix: "Job:", suffix: "Adj");
    private static readonly string[] JobTargetTags = BuildJobNameTable(prefix: string.Empty, suffix: string.Empty);

    private static string[] BuildJobNameTable(string prefix, string suffix)
    {
        TacticalJob[] jobs = Enum.GetValues<TacticalJob>();
        var names = new string[jobs.Length];
        for (int index = 0; index < jobs.Length; index++)
            names[(int)jobs[index]] = prefix + jobs[index] + suffix;
        return names;
    }

    /// <summary>
    /// Turns one job anchor into this duty's movement candidates: the anchor itself and, for the duties
    /// that support an attack, a variant stepped out of the nearest opponent's cover shadow (audit F04).
    /// Utility combines the job value with the duty's own position terms and travel cost.
    ///
    /// AIB-09: the two families of duty are classified once (<see cref="IsDefensiveJob"/>) and are scored
    /// on different terms. An attacking duty is asked to be findable and in space; a defensive duty is
    /// asked to sit goal-side of the danger and block the ball/runner lane. Running the attacking path
    /// for a defender was the defect the audit reported: <see cref="StepOutOfCoverShadow"/> pushed a
    /// <c>COVER_GAP</c> protector (and a marker, a cover defender, a line defender) up to six metres away
    /// from the man he was there to cover, and the opened-space reward paid him for the empty grass he
    /// stepped into — so the attacker could run in behind his back.
    ///
    /// 2026-11-24 owner-review note (AIB-09): the depth clamp still applies to every defensive duty. The review
    /// asked to free <see cref="TacticalJob.Marker"/> and <see cref="TacticalJob.CoverDefender"/> from the line
    /// ceiling; three forms of that were measured and none ships without breaking the 5 m
    /// `JobTargetStructureTests` floor — see <see cref="ClampDefensiveJobDepth"/> and `docs/KNOWN_ISSUES.md`.
    /// </summary>
    private void AddJobCandidates(in FootballerObservation obs, Vector2 jobTargetXZ, float offsideLineProjX)
    {
        bool defensiveJob = IsDefensiveJob(obs.TacticalJob);
        bool attackingJob = obs.TacticalJob is TacticalJob.NearOutlet or TacticalJob.ThirdPlayerOption
            or TacticalJob.FarSideWidth or TacticalJob.DepthRun or TacticalJob.Overlap;
        // A5.4: the margin applies before release. A player the ball is already travelling to may work
        // beyond the line — the law settled his position at the contact — so his job target is not pulled
        // back behind it.
        if (attackingJob && obs.PossessingTeam == obs.SelfTeam && !OffsideTactics.IsReleased(obs))
        {
            float maxOnsideProj = OffsideTactics.MaxAttackingProjection(offsideLineProjX);
            if (jobTargetXZ.X * obs.AttackingDirectionX > maxOnsideProj)
                jobTargetXZ.X = obs.AttackingDirectionX * MathF.Max(0f, maxOnsideProj);
        }

        jobTargetXZ = new Vector2(
            Math.Clamp(jobTargetXZ.X, -_pitch.HalfLength + 3.5f, _pitch.HalfLength - 3.5f),
            Math.Clamp(jobTargetXZ.Y, -_pitch.HalfWidth + 2.5f, _pitch.HalfWidth - 2.5f));

        // AIB-09: no defensive duty's target may run forward of the live last line. The allocator's own anchors
        // already live inside the band, but a duty kept from an earlier pass or a fixture that places its own
        // anchor can be forward of it, and the movement point is what the feet obey.
        if (defensiveJob) jobTargetXZ = ClampDefensiveJobDepth(obs, jobTargetXZ);

        float baseUtility = JobBaseUtility(obs.TacticalJob);
        // A9.3 part 2: the names are cached by job, not interpolated per candidate per decision.
        AddJobCandidate(obs, adjusted: false, jobTargetXZ, baseUtility);

        // AIB-09: no stepped-out variant for a defensive duty. Its target is the allocator's goal-side
        // position (the band for the line, the region a line player's slot protects, the standoff beside
        // the marked man); moving that point away from the opponent is the exact failure being fixed.
        if (defensiveJob) return;

        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 alternativeXZ = StepOutOfCoverShadow(obs, jobTargetXZ, selfXZ);
        if (Vector2.Distance(jobTargetXZ, alternativeXZ) > 1.5f)
            AddJobCandidate(obs, adjusted: true, alternativeXZ, baseUtility - 0.03f);
    }

    private void AddJobCandidate(
        in FootballerObservation obs,
        bool adjusted,
        Vector2 targetXZ,
        float baseUtility)
    {
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        Vector2 toTarget = targetXZ - selfXZ;
        float distance = toTarget.Length();
        Vector2 moveDir = distance > 0.4f
            ? (toTarget / distance) * Math.Clamp(distance / 3.5f, 0.35f, 1.0f)
            : Vector2.Zero;

        bool sprint = obs.TacticalJob is TacticalJob.DepthRun or TacticalJob.Overlap
            ? distance > 4.0f
            : distance > 10.0f;

        // AIB-09: one scale, two rules. Both families score the job's own usefulness in [0, 0.60] on top
        // of the 0.90–1.00 job base and pay the same travel cost, so a defensive duty still competes with
        // the shape candidate and with the defensive acts on the scale the rest of the decision uses.
        float travelCost = MathF.Min(distance, 40f) / 30f;
        float utility;
        if (IsDefensiveJob(obs.TacticalJob))
        {
            // AIB-09: goal-side of the man he covers, and standing on a lane that leads to this goal. Each
            // term keeps half credit at the 0.5 "cannot say" neutral, so the score judges how well the
            // assigned position is defended — up to the same 0.60 the attacking terms offered — instead of
            // letting a mediocre but legal position make the player abandon the duty for his shape.
            float goalSide = DefensiveHalfCredit01(EvaluateDefensiveGoalSide01(obs, targetXZ));
            float screen = DefensiveHalfCredit01(EvaluateDefensiveScreen01(obs, targetXZ));
            utility = baseUtility
                      + DefensivePositionWeight * (goalSide + screen)
                      - travelCost;
        }
        else
        {
            float space = EvaluateSpaceFreedom(targetXZ, obs.VisiblePlayers, obs.SelfTeam);
            Vector2 carrierXZ = TryGetCarrierXZ(obs);
            float lane = EvaluateLaneClearance(carrierXZ, targetXZ, obs.VisiblePlayers, obs.SelfTeam);

            utility = baseUtility
                      + 0.30f * lane
                      + 0.30f * space
                      - travelCost;
        }

        Vector2 faceDir = SimMath.SafeNormalize(ballXZ - selfXZ, obs.SelfFacingXZ);
        var command = new FootballerCommand(
            moveDir, faceDir, sprint, false, false, false, false, Vector3.Zero,
            KickIntentType.None, Vector3.UnitX, 0f, 0f);

        _lastCandidates.Add(new UtilityCandidate(
            adjusted ? JobAdjustedActionNames[(int)obs.TacticalJob] : JobActionNames[(int)obs.TacticalJob],
            utility, command)
        {
            TargetXZ = new Vector3(targetXZ.X, 0f, targetXZ.Y),
            // The job is the target identity, and the adjusted point is a variant of the same job, not a
            // different action — that is what lets commitment distinguish "still marking" from "shifted
            // the mark two metres" without inventing a new action every tick.
            Key = new CandidateKey(CandidateKind.Job, TargetTag: JobTargetTags[(int)obs.TacticalJob], Adjusted: adjusted)
        });
    }

    private static Vector2 TryGetCarrierXZ(in FootballerObservation obs)
    {
        if (obs.BallControllingPlayerId is int carrierId)
        {
            foreach (var entity in obs.VisiblePlayers)
            {
                if (entity.PlayerId == carrierId)
                    return new Vector2(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            }
        }

        // The ball itself is the best available reference for a loose ball or a pass in flight.
        return new Vector2(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
    }

    /// <summary>
    /// Moves a support target out of the nearest opponent's cover shadow, or offers a lateral
    /// alternative when the anchor is already in space.
    /// </summary>
    private static Vector2 StepOutOfCoverShadow(in FootballerObservation obs, Vector2 targetXZ, Vector2 selfXZ)
    {
        float nearestDistance = 12f;
        Vector2 nearestOpponentXZ = Vector2.Zero;
        foreach (var entity in obs.VisiblePlayers)
        {
            if (entity.Team == obs.SelfTeam) continue;
            Vector2 opponentXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            float distance = Vector2.Distance(targetXZ, opponentXZ);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestOpponentXZ = opponentXZ;
            }
        }

        if (nearestDistance > 6f)
            return targetXZ + new Vector2(0f, targetXZ.Y >= selfXZ.Y ? 4f : -4f);

        Vector2 away = SimMath.SafeNormalize(targetXZ - nearestOpponentXZ, new Vector2(obs.AttackingDirectionX, 0f));
        return targetXZ + away * (6f - nearestDistance);
    }

    private static float JobBaseUtility(TacticalJob job) => job switch
    {
        TacticalJob.DepthRun => 1.00f,
        TacticalJob.ThirdPlayerOption => 0.99f,
        TacticalJob.NearOutlet => 0.97f,
        TacticalJob.FarSideWidth => 0.93f,
        TacticalJob.Overlap => 0.94f,
        TacticalJob.DefensiveCover => 0.90f,
        TacticalJob.CoverDefender => 0.99f,
        TacticalJob.ProtectGap => 0.98f,
        TacticalJob.Marker => 0.97f,
        _ => 0.90f
    };

    // ---------------------------------------------------------------------------------------------
    // AIB-09: defensive jobs are asked to defend, not to support.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// AIB-09: the jobs whose point is to stop the opponent, so their movement target must be scored on
    /// goal-side position and lane blocking rather than on being findable and in space.
    ///
    /// <para><see cref="TacticalJob.DefensiveCover"/> is the in-possession rest-defence cover, so it is
    /// listed with the out-of-possession duties the audit named (<see cref="TacticalJob.ProtectGap"/>,
    /// <see cref="TacticalJob.CoverDefender"/>, <see cref="TacticalJob.Marker"/>): its whole purpose is to
    /// be behind the ball if possession turns over, and an open-space reward is the same mistake for it.
    /// <see cref="TacticalJob.DefensiveLine"/> is here too because it was already special-cased in
    /// <see cref="AddJobCandidates"/> for the same reason — its target is the allocator's band, not a
    /// support point. Classifying the line here as well keeps one table instead of a per-consumer list,
    /// which is how the line kept the special case while the cover duties did not.</para>
    ///
    /// <para><see cref="TacticalJob.Presser"/> is deliberately <b>not</b> in the set. A presser's act is
    /// produced by <see cref="PressPolicy"/> (always a primary act when there is a carrier, so this path is
    /// not reached); the residual loose-ball case is a movement toward the ball, not a cover position, and
    /// has no meaning to score goal-side. Nothing is added to the set without evidence.</para>
    ///
    /// <para><b>Owner-review item, measured and deferred (2026-11-24, AIB-09).</b> The review asked to stop
    /// clamping the man-oriented duties (<see cref="TacticalJob.Marker"/>, <see cref="TacticalJob.CoverDefender"/>)
    /// to the line, so a midfielder marking a midfielder or a cover man behind a high presser keeps his job. Three
    /// forms were measured and none ships: fully freeing them, an 8 m ahead-of-line allowance and a
    /// man-relative goal-side bound all move the `box-cross` structural fixture from 5.18 m to 4.6 m of anchor
    /// separation (below the 5 m `JobTargetStructureTests` floor this batch must not lower) and open the
    /// reference match. That fixture's anchors are hypersensitive to the marker's movement because the anchors
    /// follow the ball. The clamp therefore stays uniform — see `docs/KNOWN_ISSUES.md` and the fix report for the
    /// owner decision this needs. The table below is the <i>scoring</i> set; it is still the five duties.</para>
    ///
    /// Public and pure so a regression test can pin the classification table itself; no decision path
    /// iterates or allocates.
    /// </summary>
    public static bool IsDefensiveJob(TacticalJob job) => job is
        TacticalJob.ProtectGap or TacticalJob.CoverDefender or TacticalJob.Marker
        or TacticalJob.DefensiveCover or TacticalJob.DefensiveLine;

    /// <summary>
    /// AIB-09: maps a 0–1 defensive metric onto the half-credit scale the defensive score sums. The duty
    /// itself is the allocator's decision (<see cref="JobBaseUtility"/> already sits above the 0.88 shape
    /// candidate), so these terms judge <i>how well</i> the assigned position is defended; a neutral 0.5
    /// metric still earns half the term, and a genuinely wrong position (the wrong side of the man, off the
    /// lane) loses the other half. The floor is bounded and never pays for emptiness more than for a correct
    /// position — goal-side and on-lane still earn twice the neutral credit — but it keeps a legal duty
    /// target competitive: without it a far-but-legal assignment could score below the shape candidate on
    /// travel alone and the player would abandon the duty for his formation anchor.
    /// </summary>
    private static float DefensiveHalfCredit01(float metric01) => 0.5f + 0.5f * metric01;

    /// <summary>
    /// AIB-09: how much each defensive position term is worth, on the same scale as the two 0.30 attacking
    /// terms it replaces for these duties. With each term's half-credit floor the pair contributes between
    /// 0.30 and 0.60 on top of the 0.90–1.00 job base and pays the same travel cost, so the total stays in
    /// the range <see cref="JobBaseUtility"/> already uses and no new scale enters the decision.
    /// </summary>
    public const float DefensivePositionWeight = 0.30f;

    /// <summary>
    /// AIB-09: attack-relative metres of goal-side margin at which the goal-side term saturates. It sets
    /// the slope of <see cref="EvaluateDefensiveGoalSide01"/> only: one metre behind the man scores
    /// 0.625, two metres 0.75, four or more 1.0; the same numbers apply the other way round, so a target a
    /// metre ball-side scores 0.375 and one four metres ball-side scores 0.
    /// </summary>
    public const float DefensiveGoalSideSaturationMeters = 4f;

    /// <summary>
    /// AIB-09: metres off a lane to this goal at which the screening term reaches zero. Inside it the term
    /// is linear, so a body on the ball→goal or runner→goal line scores 1.0 and one four metres wide of it
    /// scores 0. Four metres is roughly two body widths plus the reach a pass needs to pass a man; wider
    /// than that and the duty is not screening that lane at all.
    /// </summary>
    public const float DefensiveScreenLaneMeters = 4f;

    /// <summary>
    /// AIB-09: how far from the target a visible outfield opponent still counts as "the man this duty is
    /// against". Beyond it there is nobody to be goal-side of and the goal-side term reads the neutral
    /// 0.5 — the same "cannot say" value <see cref="DefensiveLineModel.PasserPressure01"/> uses — instead
    /// of paying the duty for empty space.
    /// </summary>
    private const float DefensiveThreatRangeMeters = 16f;

    /// <summary>
    /// AIB-09: bounds a defensive duty's movement target at the live line's ceiling, in attack-relative
    /// projection.
    ///
    /// A <see cref="TacticalJob.ProtectGap"/> or <see cref="TacticalJob.Marker"/> target forward of
    /// <see cref="DefensiveLineBand.MaxProjX"/> is forward of the last line the team is defending, i.e. in
    /// front of the man it is there to cover — the audit's <c>COVER_GAP</c> running past
    /// <c>THIRD_OPTION</c>. The band's ceiling already contains the credible-runner cap
    /// (<see cref="DefensiveLineModel.LineToRunnerMeters"/> goal-side of the deepest believed run) and the
    /// keeper cap, so one projection covers "at or goal-side of the last line and of nearby last-line
    /// runners" without a second model.
    ///
    /// With no valid band (<see cref="FootballerObservation.TeamLineBand"/> defaults to "unknown") the
    /// target is returned unchanged: a standalone controller must not invent a depth it was never given,
    /// which is also what keeps a fixture or a legacy observation on the pre-AIB-09 behaviour.
    ///
    /// <para><b>Owner-review item, measured and deferred (2026-11-24).</b> The review asked to scope this
    /// clamp to the back-line duties so a <see cref="TacticalJob.Marker"/> on a midfielder 15 m up (or a
    /// <see cref="TacticalJob.CoverDefender"/> behind a high presser) is not dragged back to the back four.
    /// Three forms were implemented and measured — fully freeing the two man-oriented duties, an 8 m
    /// ahead-of-line allowance, and a man-relative goal-side bound — and <b>all three</b> move the
    /// `box-cross` fixture's closest refined anchor pair from 5.18 m to 4.6 m, below the 5 m
    /// `JobTargetStructureTests` floor that this batch must not lower, and the freed marker also opens the
    /// reference 11v11 up. The fixture is hypersensitive here because the marker's movement changes the
    /// passage of play by t=144 and the structural anchors follow the ball. The clamp therefore stays
    /// uniform, and the scoping is a documented residual for the owner: it needs either a gate re-baseline
    /// (their call, not this batch's) or a structural guard that holds anchors apart independently of the
    /// ball's phase.</para>
    /// </summary>
    private static Vector2 ClampDefensiveJobDepth(in FootballerObservation obs, Vector2 targetXZ)
    {
        DefensiveLineBand band = obs.TeamLineBand;
        if (!band.IsValid) return targetXZ;

        float targetProj = targetXZ.X * obs.AttackingDirectionX;
        if (targetProj <= band.MaxProjX) return targetXZ;

        targetXZ.X = obs.AttackingDirectionX * band.MaxProjX;
        return targetXZ;
    }

    /// <summary>
    /// AIB-09: how goal-side of the man he covers a defensive target stands, 0–1, with 0.5 meaning level.
    ///
    /// Goal-side is the attack-relative projection comparison the line model and the offside line already
    /// use: smaller projection = nearer this team's own goal. The nearest visible outfield opponent to
    /// the target is the man the duty is against; the margin saturates at
    /// <see cref="DefensiveGoalSideSaturationMeters"/>, so the term is bounded and being one metre behind
    /// him is worth less than being four, which is what makes "step in front of him" score badly instead
    /// of well.
    ///
    /// The opponent scan is over <see cref="FootballerObservation.VisiblePlayers"/> only — perception is
    /// the knowledge boundary — and index-looped with no allocation, like the rest of the off-ball path.
    ///
    /// <para><b>Measured rejection (2026-11-24 owner review).</b> Reading
    /// <see cref="FootballerObservation.JobTargetPlayerId"/> instead of the nearest opponent was implemented
    /// and measured, and <b>not shipped</b>: with the marker's own man driving his goal-side score, committed
    /// man-markers track their man away from the block, and the reference 11v11 match opens up badly — the
    /// same build goes 11–10 with 57/47 shots without this reading and 24–11 with 72/38 shots with it (a
    /// further 13 goals on one fixed seed, and 21–35 total goals across the probe seeds). A "minor nit" that
    /// costs that much is a match-level behaviour change, not a correction, so the nearest-opponent reading
    /// stays. If it is ever revisited, bound the marker's depth first and prove the goal volume separately.</para>
    /// </summary>
    private static float EvaluateDefensiveGoalSide01(in FootballerObservation obs, Vector2 targetXZ)
    {
        float attackDirX = obs.AttackingDirectionX;
        float targetProj = targetXZ.X * attackDirX;

        float nearestDistance = DefensiveThreatRangeMeters;
        float nearestProj = 0f;
        for (int index = 0; index < obs.VisiblePlayers.Count; index++)
        {
            PerceivedEntity entity = obs.VisiblePlayers[index];
            if (entity.Team == obs.SelfTeam) continue;
            if (entity.Role == TacticalRole.Goalkeeper) continue;

            Vector2 opponentXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            if (!SimMath.IsFinite(opponentXZ)) continue;

            float distance = Vector2.Distance(targetXZ, opponentXZ);
            if (distance >= nearestDistance) continue;
            nearestDistance = distance;
            nearestProj = opponentXZ.X * attackDirX;
        }

        // No opponent in range: the duty is standing in space, which is neither goal-side nor ball-side.
        if (nearestDistance >= DefensiveThreatRangeMeters) return 0.5f;

        float margin = nearestProj - targetProj;
        return Math.Clamp(
            0.5f + margin / (2f * DefensiveGoalSideSaturationMeters), 0f, 1f);
    }

    /// <summary>
    /// AIB-09: whether a defensive target sits on a lane that matters to this goal, 0–1: the ball's own
    /// lane to our goal (the pass the block exists to stop) or the nearest threat's lane to it (the
    /// runner's path the duty exists to cover), whichever the target does better on.
    ///
    /// This replaces the attacking lane term for defensive duties. That term asked whether the carrier
    /// could find the target — the opposite question to whether the target is standing where the ball
    /// would go — so a defender who stepped into passing space scored better the further he abandoned the
    /// man he was marking. The reference geometry is this team's own goal centre, which is a fixed fact of
    /// the pitch and the attacking direction, not hidden state; the ball and the threat come from the
    /// observation, and the two candidates are only the endpoints of a segment.
    /// </summary>
    private float EvaluateDefensiveScreen01(in FootballerObservation obs, Vector2 targetXZ)
    {
        Vector2 ownGoalXZ = new(-obs.AttackingDirectionX * _pitch.HalfLength, 0f);
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        float best = ScreenOnLane01(targetXZ, ballXZ, ownGoalXZ);

        float nearestDistance = DefensiveThreatRangeMeters;
        Vector2 nearestThreatXZ = Vector2.Zero;
        for (int index = 0; index < obs.VisiblePlayers.Count; index++)
        {
            PerceivedEntity entity = obs.VisiblePlayers[index];
            if (entity.Team == obs.SelfTeam) continue;
            if (entity.Role == TacticalRole.Goalkeeper) continue;

            Vector2 opponentXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            if (!SimMath.IsFinite(opponentXZ)) continue;

            float distance = Vector2.Distance(targetXZ, opponentXZ);
            if (distance >= nearestDistance) continue;
            nearestDistance = distance;
            nearestThreatXZ = opponentXZ;
        }

        if (nearestDistance < DefensiveThreatRangeMeters)
            best = MathF.Max(best, ScreenOnLane01(targetXZ, nearestThreatXZ, ownGoalXZ));

        return best;
    }

    /// <summary>
    /// AIB-09: 1 when <paramref name="targetXZ"/> lies on the straight segment from
    /// <paramref name="fromXZ"/> to <paramref name="toXZ"/>, falling linearly to 0 at
    /// <see cref="DefensiveScreenLaneMeters"/> off it. The projection is clamped to the segment, so a body
    /// behind the reference point or past the goal is scored as off the lane rather than as on an infinite
    /// line; allocates nothing.
    /// </summary>
    private static float ScreenOnLane01(Vector2 targetXZ, Vector2 fromXZ, Vector2 toXZ)
    {
        Vector2 segment = toXZ - fromXZ;
        float length = segment.Length();
        if (length < 1e-3f) return 0f;

        Vector2 direction = segment / length;
        Vector2 offset = targetXZ - fromXZ;
        float projection = Math.Clamp(Vector2.Dot(offset, direction), 0f, length);
        float perpendicular = Vector2.Distance(targetXZ, fromXZ + direction * projection);
        return Math.Clamp(1f - perpendicular / DefensiveScreenLaneMeters, 0f, 1f);
    }

    public static float EvaluateLaneClearance(
        Vector2 fromXZ,
        Vector2 toXZ,
        IReadOnlyList<PerceivedEntity> visiblePlayers,
        TeamId selfTeam) =>
        EvaluateLaneClearance(fromXZ, toXZ, visiblePlayers, selfTeam, chargeUncertainty: false);

    /// <summary>
    /// AIB-05: the same lane metric with opposing goalkeepers excluded from the corridor. A shot's keeper
    /// save risk is already priced inside <see cref="ActionValueScale.ShotConversion"/>, so counting the
    /// keeper as an outfield lane blocker as well was a duplicate penalty: a keeper standing 1–2 m off his
    /// line at z ≈ 0 sat within the widened goal-line intercept radius of both corner rays and cut the
    /// lane clearance of shots at the corners he had not yet covered.
    /// </summary>
    public static float EvaluateLaneClearance(
        Vector2 fromXZ,
        Vector2 toXZ,
        IReadOnlyList<PerceivedEntity> visiblePlayers,
        TeamId selfTeam,
        bool excludeGoalkeeper) =>
        EvaluateLaneClearance(fromXZ, toXZ, visiblePlayers, selfTeam, chargeUncertainty: false, excludeGoalkeeper);

    /// <summary>
    /// The same lane metric with the option to charge each defender's believed positional uncertainty into the
    /// corridor (A8.2): a remembered defender may be nearer the line than his stale position says, so the
    /// perpendicular distance is shortened by <see cref="PerceivedEntity.PositionUncertaintyMeters"/> before
    /// it is scored.
    ///
    /// <c>ShallowActionSearch</c> and (since ai62) every ground pass built through <see cref="TryAddGroundPass"/>
    /// pass <c>chargeUncertainty: true</c> (lofted/cross/driven/cutback/recycle still pass <c>false</c>). The A8.1 heuristic and the structural lane tests keep the
    /// unladen metric exactly — a fresh sighting has zero uncertainty, so the two agree, and the difference
    /// is a deliberately more conservative second ball rather than a re-tuned first one. Public so a
    /// regression or a probe can price the same corridor the carrier does (ai62: the opening-pass replay
    /// pins that a 1.49 m stale sighting 2.37 m off the lane costs most of the corridor).
    ///
    /// AIB-02a: the corridor is a <b>lane</b>, not one defender's shadow. The old metric kept only the worst
    /// defender (<c>minClearanceRatio</c>), so three men clustered around the same passing corridor scored
    /// exactly like one — the report's "passing through 3 defenders". Every defender inside the corridor now
    /// also adds a bounded, monotone congestion charge: three crowding defenders score strictly worse than one,
    /// and a lane with nobody in it still scores exactly 1.0.
    ///
    /// This is complementary to <see cref="PassSuccessModel"/>'s arrival-point crowd factor (AIB-02b), not a
    /// duplicate of it: the pass model counts defenders who can reach the <i>landing point</i> within the
    /// flight window, while this counts bodies <i>along the corridor</i> — a man halfway down the line who can
    /// step into the ball's path is corridor congestion here and may never reach the arrival point. The two
    /// geometries are composed by the pass model's own weights; this metric deliberately does not attempt to
    /// price the arrival contest itself.
    ///
    /// <para><b>AIB-08 (audit 2026-11-23, finding 3): the corridor is no longer static.</b> Each opponent is
    /// scored at the worse of his believed position and the position his own believed velocity reaches in
    /// <see cref="LaneProjectionHorizonSeconds"/>, so a defender closing into the lane before the ball is
    /// scored as the threat he is instead of as a bystander. The projection uses the same cap convention as
    /// the pass model (<see cref="PassSuccessModel.OpponentSprintSpeed"/>), contributes one body once, and
    /// leaves a stationary defender's value bit-for-bit unchanged.</para>
    /// </summary>
    public static float EvaluateLaneClearance(
        Vector2 fromXZ,
        Vector2 toXZ,
        IReadOnlyList<PerceivedEntity> visiblePlayers,
        TeamId selfTeam,
        bool chargeUncertainty,
        bool excludeGoalkeeper = false)
    {
        Vector2 seg = toXZ - fromXZ;
        float segLen = seg.Length();
        if (segLen < 1e-3f) return 1.0f;
        Vector2 segDir = seg / segLen;

        float minClearanceRatio = 1.0f;
        float congestion01 = 0f;
        // A9.3 part 2: index loop, same order and skips — foreach over the interface type boxes the
        // list's enumerator, and this runs several times per off-ball decision.
        for (int index = 0; index < visiblePlayers.Count; index++)
        {
            PerceivedEntity p = visiblePlayers[index];
            if (p.Team == selfTeam) continue;
            // AIB-05: the keeper is a save risk, not an outfield lane blocker (see the public overload).
            if (excludeGoalkeeper && p.Role == TacticalRole.Goalkeeper) continue;
            Vector2 oppXZ = new(p.EstimatedPosition.X, p.EstimatedPosition.Z);
            Vector2 oppVelocityXZ = new(p.EstimatedVelocity.X, p.EstimatedVelocity.Z);

            // AIB-08 (audit 2026-11-23, finding 3): the corridor was scored 100 % statically, so a defender
            // closing into the lane before the ball was scored exactly like one standing still. Score the
            // worse of where he is and where his own believed velocity puts him one short horizon later; the
            // speed is capped at the pass model's own sprint convention so a bad velocity cannot project a
            // defender across the whole pitch. One body contributes one ratio (the minimum), never two
            // congestion charges. The current-position band gate is kept, so the projection cannot resurrect
            // a body behind the passer that this metric deliberately ignores.
            Vector2 rel = oppXZ - fromXZ;
            float proj = Vector2.Dot(rel, segDir);
            if (proj <= 0.4f) continue;

            // ai64 (Task 1.2): keep standing/receding past-target defenders uncharged (A6.B8), but score
            // a presser past targetXZ whose run is carrying him back up-lane into the corridor.
            bool pastTargetUpLaneSprinter = proj >= segLen - 0.3f &&
                                            Vector2.Dot(oppVelocityXZ, segDir) < -LaneProjectionMinSpeedMetersPerSecond;
            if (proj >= segLen - 0.3f && !pastTargetUpLaneSprinter) continue;

            float speed = oppVelocityXZ.Length();
            if (speed > PassSuccessModel.OpponentSprintSpeed)
                oppVelocityXZ *= PassSuccessModel.OpponentSprintSpeed / speed;
            int positions = speed >= LaneProjectionMinSpeedMetersPerSecond ? 2 : 1;

            float ratio = 1f;
            for (int position = 0; position < positions; position++)
            {
                if (position == 0 && pastTargetUpLaneSprinter) continue;
                Vector2 pointXZ = position == 0
                    ? oppXZ
                    : oppXZ + oppVelocityXZ * LaneProjectionHorizonSeconds;
                Vector2 sampleRel = pointXZ - fromXZ;
                float sampleProj = Vector2.Dot(sampleRel, segDir);
                if (sampleProj <= 0.4f || sampleProj >= segLen - 0.3f) continue;

                Vector2 closest = fromXZ + segDir * sampleProj;
                float perpDist = Vector2.Distance(pointXZ, closest);
                if (chargeUncertainty && p.PositionUncertaintyMeters > 0f)
                    perpDist = MathF.Max(0f, perpDist - p.PositionUncertaintyMeters);
                float interceptRadius = 1.1f + (sampleProj / segLen) * 2.4f;
                float sampleRatio = Math.Clamp(perpDist / interceptRadius, 0f, 1f);
                if (sampleRatio < ratio) ratio = sampleRatio;
            }

            if (ratio < minClearanceRatio)
                minClearanceRatio = ratio;

            // AIB-02a: everything already inside the corridor contributes to its congestion. The weight is
            // the old 1.0 worst-defender term plus a bounded charge for the other bodies in the same lane.
            if (ratio < LaneCongestionRatio01)
                congestion01 += (LaneCongestionRatio01 - ratio) / LaneCongestionRatio01;
        }

        // AIB-02a: monotone in defender count and bounded, so a crowd is always worse than one man but the
        // metric never reaches the "physically blocked" zero (which is the lane-block test's verdict).
        float congestionFactor = 1f - MathF.Min(LaneCongestionMax01, LaneCongestionStep01 * congestion01);
        return Math.Clamp(minClearanceRatio * congestionFactor, 0f, 1f);
    }

    /// <summary>
    /// True when the planned pass line is blocked: the AI abandons such a pass instead of playing it
    /// straight into the defender (reported from playtesting: "let them see if the pass line is
    /// blocked, if yes - abandon it").
    ///
    /// <see cref="EvaluateLaneClearance"/> scores how crowded a lane *is right now*: it ignores
    /// opponents close to the passer, only scales its corridor with how near the receiver a defender
    /// stands, and knows nothing about the flight time. With the old minimum-power passes taking
    /// ~2 s, every defender beside the lane had time to step into it, so the pass was cut out even
    /// though the lane scored as perfectly clear. This test instead asks whether an opponent can
    /// physically be where the ball will be:
    ///
    /// <list type="bullet">
    /// <item><b>on their run:</b> the flight is sampled against each opponent's predicted run. A
    ///   defender standing in the lane, next to the ball, or running across it meets the ball where
    ///   it actually will be — including a defender 1.5 m off the line at 6 m/s whose current
    ///   position looks clear;</item>
    /// <item><b>stepping in:</b> an opponent who is not already running into the lane can still
    ///   notice the pass and close it after <see cref="PassSuccessModel.OpponentReactionSeconds"/>. A
    ///   slow pass is therefore blocked from further away than a firm one, and a defender running away
    ///   from the lane with no time left does not block it.</item>
    /// </list>
    /// </summary>
    /// <param name="power01">Power the pass is played at (used for the ball's arrival time).</param>
    /// <param name="flightTimeSeconds">The plan's flight time (fallback when the table has no sample).</param>
    public bool IsPassLaneBlocked(
        Vector2 fromXZ,
        Vector2 toXZ,
        float power01,
        float flightTimeSeconds,
        IReadOnlyList<PerceivedEntity> visiblePlayers,
        TeamId selfTeam) =>
        // The test itself moved to the pass-success model in A4.2, next to the completion estimate that
        // depends on it; this façade stays because the audit probe and the goalkeeper path call it here.
        _passSuccess.IsLaneBlocked(fromXZ, toXZ, power01, flightTimeSeconds, visiblePlayers, selfTeam);

    /// <summary>
    /// Nearest-opponent distance at which <see cref="EvaluateSpaceFreedom"/> saturates, in metres: the one space
    /// scale the pass pre-rank, A8.1's region scoring and A8.3's switch recognition all read. Exposed because a
    /// gate expressed as "five metres of room" has to be converted to that scale in one place.
    /// </summary>
    public const float SpaceFreedomSaturationMeters = 9.0f;

    internal static float EvaluateSpaceFreedom(
        Vector2 spotXZ,
        IReadOnlyList<PerceivedEntity> visiblePlayers,
        TeamId selfTeam)
    {
        float minOppDist = 15.0f;
        // A9.3 part 2: index loop; see EvaluateLaneClearance for why the interface foreach is avoided.
        for (int index = 0; index < visiblePlayers.Count; index++)
        {
            PerceivedEntity p = visiblePlayers[index];
            if (p.Team == selfTeam) continue;
            float d = Vector2.Distance(spotXZ, new Vector2(p.EstimatedPosition.X, p.EstimatedPosition.Z));
            if (d < minOppDist) minOppDist = d;
        }
        return Math.Clamp(minOppDist / SpaceFreedomSaturationMeters, 0f, 1f);
    }
}

