using System;
using FootballSim.Core;

namespace FootballSim.AI;

/// <summary>
/// The four terms every action's score is built from (A4.4/A4.6):
/// <c>Probability01 × Gain − (1 − Probability01) × Loss + Preference</c>.
///
/// It exists so a decision can be explained without re-deriving it: the carrier's chosen candidate carries
/// these four numbers, the decision trace records them, and the inspector prints them (A4.6). A heuristic
/// curve that is not probability-weighted stores <c>Probability01 = 1</c>, its whole value in
/// <see cref="Gain"/> and zero loss — the arithmetic stays the same, and the numbers do not pretend to be a
/// decomposition they are not.
/// </summary>
public readonly record struct ValueTerms(float Probability01, float Gain, float Loss, float Preference)
{
    /// <summary>No terms: a candidate that was built without an explanation.</summary>
    public static ValueTerms None => new(-1f, 0f, 0f, 0f);

    /// <summary>True when this carries a real decomposition.</summary>
    public bool IsPresent => Probability01 >= 0f;

    /// <summary>The value the terms compose to, on the scale's unit.</summary>
    public float Value => Probability01 >= 0f
        ? ActionValueScale.ExpectedValue(Probability01, Gain, Loss) + Preference
        : 0f;

    /// <summary>Compact text form for a trace line or the inspector.</summary>
    public string Describe() => IsPresent
        ? $"p {Probability01:F2} × {Gain:F2} − {(1f - Probability01):F2} × {Loss:F2} + {Preference:F2}"
        : "no terms";
}

/// <summary>
/// A candidate the carrier's action set refused to offer, and why (A4.6). Bounded by the generator's
/// rejection buffer; the strings are interned literals so the record stays allocation-free.
/// </summary>
public readonly record struct CandidateRejection(int TargetPlayerId, string Reason);

/// <summary>
/// A4.4: the one scale pass, carry and shot utilities are expressed on, and the conversion each action
/// uses to get there.
///
/// <b>The unit.</b> <see cref="Unit"/> = 1.0 is a clean possession retained in midfield: a completed pass
/// to an unmarked team-mate who can play on, a carry into open space with the ball under control. Actions
/// are then all of the form <c>value = reach × gain − (1 − reach) × loss</c>, where <c>reach</c> is the
/// action's own probability of keeping the ball, <c>gain</c> is the state it produces measured by
/// <see cref="ReceptionValueModel"/> (or the shot's own conversion value), and <c>loss</c> is what losing
/// the ball there costs (<see cref="LossCost"/>).
///
/// That structure is the plan's value convention, applied to every action rather than only to passes:
/// <list type="bullet">
/// <item><b>pass</b> — <c>pComplete × receptionValue − (1 − pComplete) × LossCost(passer)</c>;</item>
/// <item><b>carry</b> — <c>retention × receptionValue(at the carry target) − (1 − retention) × LossCost(passer)</c>;</item>
/// <item><b>shot</b> — <c>conversion × GoalValue − (1 − conversion) × ShotMissCost</c>.</item>
/// </list>
///
/// <b>Ownership of risk.</b> The loss term is the single home of transition risk for passes and carries:
/// it rises in the player's own third and it is why a forced ball deep is expensive. The reception value
/// (A4.3) therefore adds value only and must not subtract risk again. A shot's risk is its own:
/// <see cref="ShotMissCost"/> is the possession the attempt costs, which is deliberately smaller than a
/// turnover in midfield because the ball is lost far from your own goal.
///
/// <b>Calibration status.</b> These are documented, hand-set constants measured against the tactical gate
/// and the reference 11v11 match — not fitted to outcome data. <see cref="ShotConversion"/> in particular
/// is a stated heuristic curve, not a measured one; A9.2's ablations and A10's calibration are where any
/// of it gets tested. Changing a value here changes behaviour and needs a version bump plus a fresh gate.
/// </summary>
public static class ActionValueScale
{
    /// <summary>
    /// The scale's unit: one clean possession retained in midfield. Every action's value is expressed in
    /// multiples of it, so a shot at 0.9 and a pass at 1.1 are directly comparable.
    /// </summary>
    public const float Unit = 1f;

    // ---------------------------------------------------------------------------------------------
    // Losing the ball.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Cost of losing the ball in a neutral position, in scale units.</summary>
    /// <remarks>
    /// Lower than the value of keeping it, deliberately: a 50/50 forward ball has to be worth playing, and
    /// with a loss cost equal to the gain it never is. Measured during A4.4 — with 0.80 the side circulated
    /// for a ten-minute match without a shot. Losing the ball in midfield is a setback, not a catastrophe.
    /// </remarks>
    public const float LossBase = 0.80f;

    /// <summary>Extra cost of losing the ball at your own goal line, in scale units.</summary>
    public const float LossOwnThird = 0.55f;

    /// <summary>Attack-relative projection over which the own-third surcharge ramps in, in metres.</summary>
    public const float LossReferenceProjX = 35f;

    /// <summary>
    /// What losing the ball at <paramref name="ballProjX"/> (metres, attack-relative) costs. This is the
    /// only transition-risk term for passes and carries.
    /// </summary>
    public static float LossCost(float ballProjX)
    {
        if (!float.IsFinite(ballProjX)) return LossBase;
        return LossBase + LossOwnThird * Clamp01(-ballProjX / LossReferenceProjX);
    }

    // ---------------------------------------------------------------------------------------------
    // Carrying, shielding, clearing and taking a man on.
    //
    // These keep their pre-A4 heuristic curves, expressed here in the scale's unit so that the numbers live
    // in one place and can be compared. A4.4 tried to put carrying on the same probability-weighted shape
    // as a pass (`ReceptionValueModel` for the gain, retention for the probability) and the reference match
    // collapsed: the carry became weaker than any pass, the side played one-touch balls continuously and
    // the 10-minute probe produced no shot at all. The curves below are the tested alternative; A9.2's
    // ablations and A5.3's pass-power work are where the probability-weighted version is revisited.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Weight on simply carrying the ball, in scale units.</summary>
    public const float CarryBaseValue = 0.50f;

    /// <summary>Weight on the freedom ahead of the carry.</summary>
    public const float CarrySpaceBonus = 0.40f;

    /// <summary>Bonus for carrying early in a touch (before the side settles into passing).</summary>
    /// <remarks>
    /// AIB-12a (audit 2026-11-23, finding 5): lowered 0.25 → 0.10 and the window 0.70 s → 0.35 s. The audit
    /// measured the carrier holding the ball at <c>DriveWithBall = 1.37</c> for 0.70 s — above every forward
    /// pass — and recommended 0.10–0.12 / 0.35–0.40 s.
    ///
    /// <para>The audit's claim that this alone puts the carry at 0.95–1.05 is <b>arithmetically wrong for its
    /// own decomposition</b>, and the corrected arithmetic is the reason the constants are the low end rather
    /// than a tuned total: the cut removes exactly 0.15, so the same full-space early carry is
    /// <c>0.50 base + 0.40 space + 0.10 early + ~0.08 progress + ~0.14 dribble ≈ 1.22</c> (a neutral player
    /// with no tendency preference ≈ 1.08), not ≈ 1.00. The fix is therefore an <i>ordering</i> fix against a
    /// clean pass after AIB-08/AIB-10/AIB-11 re-priced the pass set, plus AIB-07b's advance term when the
    /// grass ahead is open; the measured early/settled gap is exactly this constant (regression
    /// <c>Aib3CarryShotTests.AIB12a_TheEarlyCarryBonus_IsBoundedAndExpires</c>).</para>
    /// </remarks>
    public const float CarryEarlyBonus = 0.10f;

    /// <summary>Seconds on the ball after which the early-carry bonus stops applying.</summary>
    /// <remarks>
    /// AIB-12a: shortened 0.70 s → 0.35 s. The audit's defect was not only the bonus size but its duration:
    /// for 0.70 s no midfield pass could outbid the carry, so the first touch was locked into dribbling
    /// whatever the picture. 0.35 s is the audit's low end — about two 0.18 s reaction commitments, long
    /// enough for the tempo trait to show, short enough that a clean forward pass can win once the first
    /// touch has settled.
    /// </remarks>
    public const float CarryEarlySeconds = 0.35f;

    /// <summary>Penalty for carrying the ball into a touchline or goal line.</summary>
    public const float CarryLinePenalty = 0.30f;

    // ---------------------------------------------------------------------------------------------
    // AIB-07b (audit 2026-11-23, finding 1) — the advance prospect.
    //
    // The carry kept its pre-A4 heuristic shape (see the block comment above), so it had no idea that the
    // ground it gains is worth more than the ground it stands on: at 20 m an unpressed 1v1 striker valued
    // the carry at ≈0.90 and shot from outside the box instead of driving into it (audit images 1–2). The
    // term below is the *change in shot value* the carry's own 4.5 m target would produce —
    // (GoalValue + ShotMissCost) × ΔConversion — normalized to one reference probe and capped. It is paid
    // once, in the carry's gain, only while the grass the carry would take is actually open, and it is
    // withdrawn (exactly 0) the moment an opponent can arrive at the carrier or at the target inside the
    // stated windows. The existing contact-risk deduction stays the only pressure penalty: this is a
    // prospect that does not exist when closed, never a second discount of the same risk.
    //
    // 2026-11-24 owner-review correction (still AIB-07b): the first cut priced the prospect on a
    // hardcoded 100 %-open shooting lane and switched it on in one step at ShotRangeMeters, so a compact
    // block between the carry target and goal paid nothing and a carrier crossing 23.5 m gained 0.65 in a
    // single tick. The prospect is now multiplied by the real outfield lane clearance from the carry target
    // to the goal (keeper excluded — the AIB-05 convention: the keeper is the shot's save risk, not an
    // outfield lane blocker), fades in over CarryAdvanceRampMeters, and its cap sits in the reviewer's
    // 0.35–0.45 band. Measured on the neutral 20 m 1v1 fixture (settled touch): open lane 1.00 and the
    // 4 m ramp at 0.875 give an advance of 0.35, carry 1.3287 against the keeper-aware shot 0.9478 (the
    // drive still wins by 0.38; shot/carry crossover 16.8 m); the same picture with a three-body block on
    // the target→goal ray reads lane 0.00 and pays exactly nothing; a closing chaser still pays nothing.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The most an open carry's forward ground can add to its value, in scale units (AIB-07b).
    /// </summary>
    /// <remarks>
    /// The audit's own unit: with the shipped curve a clean shot from 20 m is worth
    /// <see cref="ShotValue"/>(20, 1, 21°) ≈ 0.98 and the same shot from 15.5 m — one 4.5 m carry target
    /// closer — ≈ 1.60, so a full reference probe is worth ≈ 0.62.
    ///
    /// <para><b>2026-11-24 review correction (AIB-07b).</b> Lowered 0.65 → 0.40, inside the reviewer's
    /// 0.35–0.45 band, and chosen by measurement rather than by taste: 0.40 is the largest value in the band
    /// that keeps an open square/cutback pass in the box able to win. A settled open-lane carry in the box
    /// band now tops out at <c>0.90 base+space + 0.40 = 1.30 gain</c>, i.e. 1.3787 utility with the 0.0787
    /// forward-progress preference, below the top of the reviewer's typical 1.10–1.40 box-pass band; at 0.45
    /// it would be 1.4287 and the pass could no longer win. The audit's finding-1 case is preserved: with
    /// the lane gate and the <see cref="CarryAdvanceRampMeters"/> ramp the open 20 m 1v1 still drives
    /// (carry 1.3287 vs keeper-aware shot 0.9478), and the measured shot/carry crossover moved 15.1 m →
    /// 16.8 m (the shot now wins in the 15–16.8 m band where the old drive did). Regression
    /// <c>Aib3CarryShotTests.AIB07b_AnOpenOneOnOneAtTwentyMetres_DrivesInsteadOfShooting</c> pins it.</para>
    /// </remarks>
    public const float CarryAdvanceValue = 0.40f;

    /// <summary>
    /// Distance, in metres, over which the advance prospect fades in as the carrier enters
    /// <see cref="ShotRangeMeters"/> (AIB-07b, 2026-11-24 owner-review correction).
    /// </summary>
    /// <remarks>
    /// The first cut gated the prospect with <c>distanceToGoal &lt; ShotRangeMeters</c>, which is a hard
    /// 0 → 0.65 step at 23.5 m: one tick outside the range paid nothing, the next tick paid the full term,
    /// so a carrier could be pulled into a ball-hog by a single metre. The term now ramps linearly from 0 at
    /// 23.5 m to full at 19.5 m (4 m ≈ the carry's own 4.5 m target, so the fade covers the distance one
    /// carry probe closes). At 20 m the ramp reads 0.875 and the open-lane advance is 0.35 of the 0.40 cap.
    /// The gate is a ramp, not a wider range: beyond 23.5 m the term is still exactly zero.
    /// </remarks>
    public const float CarryAdvanceRampMeters = 4f;

    /// <summary>
    /// Shot-value improvement that counts as one full reference probe, in scale units (AIB-07b).
    /// </summary>
    /// <remarks>
    /// <c>ShotValue(d − 4.5) − ShotValue(d)</c> is ≈ 0.6156 at d = 20 m on the shipped curve, so 0.60 makes
    /// the term saturate at the reference distance and stay saturated nearer the box (the conversion slope
    /// flattens there). It normalises the prospect onto <see cref="CarryAdvanceValue"/>; it is not a second
    /// tuning knob.
    /// </remarks>
    public const float CarryAdvanceReferenceUnits = 0.60f;

    /// <summary>
    /// Room the carry's own probe must have (the same space unit <c>ScoreCarryDirection</c> reports) before
    /// the advance prospect is paid (AIB-07b).
    /// </summary>
    /// <remarks>
    /// Below this the direction is not "open grass": with an opponent within ~5 m of the probe the carry is a
    /// challenge rather than an advance, and the contact-risk term prices it. Equal to the sprint gate's own
    /// free-space threshold so "open enough to run into" and "open enough to be worth running into" are the
    /// same statement.
    /// </remarks>
    public const float CarryAdvanceMinSpace01 = 0.55f;

    /// <summary>
    /// Opponent arrival time at the carry's <i>target</i>, in seconds, beyond which the target is open ground
    /// for the advance prospect (AIB-07b).
    /// </summary>
    /// <remarks>
    /// The target is 4.5 m ahead of the carrier, so a presser who is 0.5 s from the carrier's feet (the
    /// contact window) is still behind the ground the carry takes. 0.60 s is the stated line at which even
    /// the target is contested; inside it the prospect is exactly zero rather than reduced, because the
    /// contact risk already charges the threat against the carrier.
    /// </remarks>
    public const float CarryAdvancePathArrivalSeconds = 0.60f;

    // ---------------------------------------------------------------------------------------------
    // A9.6a — the contact risk a carry has always ignored (rendered-review finding 1).
    //
    // The carry kept its pre-A4 heuristic shape (probability 1, loss 0), so it could never lose value to a
    // closing defender: the only pressure it read was the freedom of the point 5 m ahead. This puts the
    // *imminent tackle* on the same scale — the one A2.4 pressure estimate, read at the direction's probe, is
    // subtracted from the carry's gain. The gain shape is deliberately kept: A4.4's probability/loss carry
    // collapsed the reference match (see the block comment above), so this is a stated deduction.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// What an opponent who is on the carrier is worth to a carry, in scale units. Sized so a defender at
    /// ~1-2 m makes the observed 0.85 carry fall below a real 0.70 pass.
    /// </summary>
    public const float CarryContactRiskCost = 0.45f;

    // AIB-01 (audit 2026-11-22): the risk is the tackle arriving, not the static distance. The old curve
    // only started inside ContactRiskMeters = 2.2 m and ramped over 1.0 m, so at 2.3 m a defender closing at
    // 8 m/s — 0.13 s from tackle reach — cost the carry exactly nothing, and a sprinting presser crossed the
    // whole ring in the 0.18 s the carrier was committed to his carry (report §1). The two constants below
    // price the carry from the same local-pressure arrival estimate the rest of the file already builds:
    // zero at ContactRiskArrivalSeconds, full at ContactRiskFullArrivalSeconds.

    /// <summary>Opponent arrival time, in seconds, at which carry contact risk starts.</summary>
    /// <remarks>
    /// Half a second is the calibrated line: a sprinting presser at 8 m/s arrives from 4.5 m in ~0.3 s and
    /// pays real risk, while a settled build-up (a defender ~2.5 m away who is not closing) estimates at
    /// ~0.55 s and keeps its sequence. The old 2.2 m ring charged nothing at all for the presser.
    /// </remarks>
    public const float ContactRiskArrivalSeconds = 0.50f;

    /// <summary>Opponent arrival time, in seconds, at which the full contact risk applies.</summary>
    public const float ContactRiskFullArrivalSeconds = 0.22f;

    /// <summary>
    /// Opponent arrival time, in seconds, below which the early-carry bonus is not paid (AIB-01): the tempo
    /// bonus paid for driving into a touch is exactly the wrong advice once the contact-risk window is open.
    /// Equal to <see cref="ContactRiskArrivalSeconds"/> so "risk is being charged" and "bonus is withdrawn"
    /// are the same statement.
    /// </summary>
    public const float CarryEarlySuppressArrivalSeconds = 0.50f;

    /// <summary>Weight on shielding the ball at the base of the range.</summary>
    public const float ShieldBaseValue = 0.30f;

    /// <summary>Added to a shield by how close the opponent is.</summary>
    public const float ShieldCrowdingBonus = 0.22f;

    /// <summary>Added to a shield by how early it happens in the player's touch.</summary>
    public const float ShieldPatienceBonus = 0.10f;

    /// <summary>Weight on clearing the ball at the base of the range.</summary>
    public const float ClearanceBaseValue = 0.42f;

    /// <summary>Added to a clearance by how little time the carrier has.</summary>
    public const float ClearanceUrgencyBonus = 0.20f;

    /// <summary>Weight on a take-on at the base of the range.</summary>
    public const float TakeOnBaseValue = 0.50f;

    /// <summary>Added to a take-on by the space past the defender.</summary>
    public const float TakeOnSpaceBonus = 0.40f;

    /// <summary>Charged to a take-on for the chance of being tackled.</summary>
    public const float TakeOnRiskCost = 0.12f;

    /// <summary>
    /// What scoring is worth in possession units. Higher than any reception because a goal ends the
    /// attack. Calibrated so that a plausible chance beats the carry and pass alternatives that sit at
    /// 0.8–1.2: with the conversion curve, a clear sight of goal from eight metres (conversion ≈ 0.42) is
    /// worth ≈ 2.6 here, and a heavily covered shot from twenty metres (≈ 0.15) is worth ≈ 0.54.
    /// </summary>
    public const float GoalValue = 7.0f;

    /// <summary>
    /// What a missed or saved shot costs. Smaller than <see cref="LossBase"/> because a shot is lost in the
    /// opponent's half, which is the cheapest place on the pitch to lose the ball.
    /// </summary>
    public const float ShotMissCost = 0.60f;

    /// <summary>Conversion baseline just outside the six-yard box, as a probability.</summary>
    /// <remarks>
    /// AIB-05: raised from 0.42 to 0.46. The audit's arithmetic had a clear in-box chance losing to a 96 %
    /// backward recycle pass (§5): conversion × 7 lost to 0.89 because the finish curve undervalued the
    /// close-range chance by both the mouth-angle discount and this baseline. The curve still bottoms out at
    /// <see cref="ShotConversionFar"/>, so a covered twenty-metre shot remains cheap.
    /// </remarks>
    public const float ShotConversionNear = 0.46f;

    /// <summary>Conversion at the edge of the shooting range, as a probability.</summary>
    public const float ShotConversionFar = 0.10f;

    /// <summary>Distance at which the conversion curve starts, in metres.</summary>
    public const float ShotNearMeters = 6f;

    /// <summary>Distance at which the conversion curve bottoms out, in metres.</summary>
    public const float ShotFarMeters = 26f;

    /// <summary>How much of the conversion the lane clearance carries versus the distance.</summary>
    public const float ShotLaneWeight = 0.40f;

    /// <summary>Distance inside which a shot is considered at all, in metres.</summary>
    public const float ShotRangeMeters = 23.5f;

    // ---------------------------------------------------------------------------------------------
    // A9.6d — the finish, not just the distance (rendered-review finding 4).
    //
    // The old AI shot was priced by distance and lane alone and struck at 0.80-0.90 power, whose execution
    // height is 2.69-3.10 m against a 2.44 m bar — every shot was at the top edge or over it. These add the
    // one thing the execution layer cannot infer: how much of the goal the shooter can actually see.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Goal-mouth angle, in degrees, at which the whole goal is visible and the angle costs nothing.
    /// </summary>
    /// <remarks>
    /// AIB-05: lowered from 30° to 19°. The full 7.32 m goal subtends only ~25° from dead centre at the edge
    /// of the penalty box, so the 30° threshold discounted every central shot from inside the box (0.83× at
    /// 16.5 m, 0.72× from (39, 9)) for not seeing more goal than the pitch geometrically allows. At 19° a
    /// central edge-of-box finish pays no angle tax, while a true byline sliver (&lt;12°) is still penalised
    /// and the <see cref="ShotMouthAngleFloor"/> keeps it from being worth nothing.
    /// </remarks>
    public const float ShotMouthAngleFullDegrees = 19f;

    /// <summary>Floor on the goal-mouth angle factor, so a sliver of a goal is not worth literally nothing.</summary>
    public const float ShotMouthAngleFloor = 0.25f;

    /// <summary>Shot power at the near end of the range: low and placed rather than lashed.</summary>
    public const float ShotPowerNear = 0.45f;

    /// <summary>Shot power at the far end of the range: enough reach, still under the bar.</summary>
    public const float ShotPowerFar = 0.70f;

    /// <summary>
    /// AI shot power for a range. Execution maps power to a goal-line target height (<c>ShotTargetHeight</c>):
    /// up to ~0.73 the ball stays under the 2.44 m bar, so the whole band is a placed shot with enough pace to
    /// arrive. The old 0.80-0.90 band aimed 2.69-3.10 m high — over the bar in the execution model's own terms.
    /// </summary>
    public static float ShotPowerForDistance(float distanceMeters)
    {
        if (!float.IsFinite(distanceMeters)) return ShotPowerNear;
        float range = Clamp01((distanceMeters - ShotNearMeters) / (ShotRangeMeters - ShotNearMeters));
        return ShotPowerNear + (ShotPowerFar - ShotPowerNear) * range;
    }

    /// <summary>
    /// Stated heuristic conversion curve: a function of range, of how clear the shooting lane is, and (A9.6d) of
    /// how much of the goal mouth the shooter can see. <b>Not</b> a measured model — A10 owns replacing it with
    /// data. It is the curve A4.4 asks for ("a better chance beats a poor shot") and was put back once the
    /// reception value stopped outbidding the shot from inside the box: with the earlier, larger reception
    /// weights the carrier preferred passing the ball into the box to shooting from it.
    /// </summary>
    /// <param name="goalMouthAngleDegrees">
    /// Angle the goal mouth subtends from the shooter's position. The default (180°) applies no angle penalty,
    /// which keeps every existing caller that has no shooter position bit-identical.
    /// </param>
    public static float ShotConversion(
        float distanceMeters,
        float laneClearance01,
        float goalMouthAngleDegrees = 180f)
    {
        if (!float.IsFinite(distanceMeters)) return 0f;
        float range = Clamp01(1f - (distanceMeters - ShotNearMeters) / (ShotFarMeters - ShotNearMeters));
        float lane = 1f - ShotLaneWeight + ShotLaneWeight * Clamp01(laneClearance01);
        float mouthAngle = float.IsFinite(goalMouthAngleDegrees) ? goalMouthAngleDegrees : 180f;
        float angle = Math.Clamp(mouthAngle / ShotMouthAngleFullDegrees, ShotMouthAngleFloor, 1f);
        return Clamp01((ShotConversionFar + (ShotConversionNear - ShotConversionFar) * range) * lane * angle);
    }

    /// <summary>The shot's value on the documented scale: conversion × goal − (1 − conversion) × miss cost.</summary>
    public static float ShotValue(float distanceMeters, float laneClearance01, float goalMouthAngleDegrees = 180f) =>
        ExpectedValue(ShotConversion(distanceMeters, laneClearance01, goalMouthAngleDegrees), GoalValue, ShotMissCost);

    // ---------------------------------------------------------------------------------------------
    // AIB-07a (audit 2026-11-23, finding 1) — the keeper-aware finish.
    //
    // AddShotCandidate scores the three goal targets with an outfield-only lane (AIB-05), and
    // ShotConversion is a distance/lane/angle curve with no keeper in it. When the outfield lane was equally
    // clear for all three targets every target scored exactly the same, so strict `>` always kept index 0 =
    // -cornerZ whatever the keeper was doing — the audit's striker shooting straight at the man covering that
    // side (images 1–2). The constants below are the one keeper term: each target loses a bounded share of
    // its value for the part of the goal the keeper's believed lateral reach covers before the ball arrives.
    // It is not a second save model: it is an expected-value reduction, applied inside the shot's gain.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Speed the ball is assumed to travel from shooter to goal line in the keeper-coverage model, m/s
    /// (AIB-07a).
    /// </summary>
    /// <remarks>
    /// 18 m/s is a placed shot in the shipped 0.45–0.70 power band's typical arrival speed. The model is a
    /// coverage window, not a physics solve: it decides whether the keeper's lateral reach can reach the
    /// target's side of the goal before the ball does.
    /// </remarks>
    public const float KeeperShotTravelMetersPerSecond = 18f;

    /// <summary>
    /// Share of the keeper's scouted sprint band that can become lateral dive distance in the available
    /// flight (AIB-07a).
    /// </summary>
    /// <remarks>
    /// A keeper does not translate laterally like an outfielder under sprint, so 0.35 ×
    /// <see cref="PerceivedEntity.Capability"/>'s <c>MaxSprintSpeed</c> is the stated conversion from a
    /// straight-line speed band to a dive's lateral reach. It is a heuristic bound used only to order the
    /// three goal targets; A9.2/A10 own the measured keeper model.
    /// </remarks>
    public const float KeeperLateralDiveShare = 0.35f;

    /// <summary>Hard cap on the keeper's lateral reach in the coverage model, in metres (AIB-07a).</summary>
    /// <remarks>
    /// 2.40 m: a full-stretch dive reaches roughly a third of the goal and no further. Without the cap a
    /// long flight (&gt;1.5 s) would let the model "cover" every target in the goal and delete the open far
    /// post from a shot that is genuinely on.
    /// </remarks>
    public const float KeeperLateralDiveMaxMeters = 2.40f;

    /// <summary>Most positional uncertainty, in metres, the keeper-coverage model charges (AIB-07a).</summary>
    /// <remarks>
    /// A stale sighting is widened, not treated as exact: the keeper may be up to the sighting's
    /// <c>PositionUncertaintyMeters</c> from his predicted position, so his reach grows by exactly that
    /// doubt, capped here so a very old sighting cannot cover the whole mouth with a fiction.
    /// </remarks>
    public const float KeeperUncertaintyCapMeters = 1.0f;

    /// <summary>Gap, in metres, past the keeper's reach at which the coverage term falls to zero (AIB-07a).</summary>
    /// <remarks>
    /// 0.60 m: a target just outside the reach is still a covered one (body and arm span are wide compared
    /// with the goal), and a target two metres clear is not. The linear falloff keeps the three-target
    /// ordering stable as the keeper drifts rather than flipping on a hard ring.
    /// </remarks>
    public const float KeeperCoverageFalloffMeters = 0.60f;

    /// <summary>What a fully covered goal target loses, in scale units (AIB-07a).</summary>
    /// <remarks>
    /// 0.22: enough to move the chosen finish from a covered corner to an open one at the ≈0.98 value of a
    /// 20 m shot (the audit's image 2), and small enough that an otherwise clear chance still beats a
    /// 0.9–1.1 pass. The term is an expected-value reduction, so it lands in the shot's gain
    /// (<see cref="ValueTerms.Gain"/>), never in its preference — the trace stays
    /// <c>utility = gain + preference</c>.
    /// </remarks>
    public const float KeeperCoveragePenalty = 0.22f;

    /// <summary>
    /// How much one unit of forward progress is worth as a <i>preference</i>, in scale units, on top of what
    /// the reception is worth. The value model is greedy — a completed sideways pass and a completed ball
    /// into the final third are both "one clean possession" — so a forward preference keeps the comparison
    /// leaning up the pitch. It is a stated preference, not a second value term.
    /// </summary>
    public const float ProgressBonus = 0.35f;

    /// <summary>
    /// Bounded territory-loss preference charged to a backward recycle pass when the same decision already
    /// offered a forward pass (AIB-06a).
    /// </summary>
    /// <remarks>
    /// <see cref="ProgressBonus"/> alone left a 96 %-complete backward ball at ~0.89 and a realistic 75 %
    /// progressive ball at ~0.74 in midfield (report §6.3), so sterile circulation still won. This is paid
    /// once, only when a forward option actually exists in the candidate set, and only on a ball whose aim
    /// point is behind the carrier — a safe outlet under pressure with nothing ahead pays nothing.
    /// </remarks>
    public const float BackwardTerritoryCost = 0.12f;

    // ---------------------------------------------------------------------------------------------
    // A7.2 — team-call preferences.
    //
    // A call is information the player did not have, so it moves the value of the actions that information
    // is about, and nothing else: a man-on shout makes a shield or a bounce pass worth more and a turn into
    // the pressure worth less (the carrier still decides, with his own picture, which action to take); a
    // turn shout makes turning worth a little more; a switch request lifts the pass to the team-mate who
    // asked; a run request lifts the runner's own run candidates. The magnitudes are hand-set and modest on
    // purpose: a call is a nudge on the same scale, not a command that overrides the model.
    //
    // Every term is paid exactly once, in the preference slot (`p × gain − (1 − p) × loss + preference`), so
    // the decision trace can explain a call-driven choice with the same four terms as any other.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Preference added to a shield after a man-on call: the body gets between ball and man.</summary>
    public const float CallShieldBonus = 0.30f;

    /// <summary>Preference added to a pass after a man-on call: the bounce / first-time return.</summary>
    public const float CallBouncePassBonus = 0.12f;

    /// <summary>
    /// Longest backward ball, in metres, that can still collect <see cref="CallBouncePassBonus"/> (AIB-06a).
    /// </summary>
    /// <remarks>
    /// The man-on bonus is for the short first-time return out of pressure, not for a long backward launch.
    /// Report §6.4 measured 14,347 man-on calls a match and noted that the ball with the highest completion —
    /// the deepest, clearest backward one — collected the bonus most reliably. A pass that loses ground and
    /// travels further than this is not the bounce the call is about, so it does not collect it.
    /// </remarks>
    public const float CallBouncePassMaxMeters = 14f;

    /// <summary>Preference removed from a carry after a man-on call: turning into the pressure.</summary>
    public const float CallCarryPenalty = 0.22f;

    /// <summary>Preference removed from a take-on after a man-on call: the defender is already on him.</summary>
    public const float CallTakeOnPenalty = 0.30f;

    /// <summary>Preference added to a carry after a turn call: the caller says there is time to turn.</summary>
    public const float CallTurnCarryBonus = 0.16f;

    /// <summary>Preference added to the pass to the team-mate who asked for a switch.</summary>
    public const float CallSwitchPassBonus = 0.22f;

    /// <summary>Preference added to a run candidate when the carrier has asked this player to run.</summary>
    public const float CallRunBonus = 0.18f;

    // ---------------------------------------------------------------------------------------------
    // Composition.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The common shape: keep-the-ball value minus what losing it costs. The single place the convention
    /// `reach × gain − (1 − reach) × loss` is implemented.
    /// </summary>
    public static float ExpectedValue(float reach01, float gain, float loss)
    {
        float reach = Clamp01(reach01);
        float safeGain = float.IsFinite(gain) ? gain : 0f;
        float safeLoss = float.IsFinite(loss) ? loss : 0f;
        return reach * safeGain - (1f - reach) * safeLoss;
    }

    private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;
}
