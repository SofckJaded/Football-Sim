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
    public const float CarryEarlyBonus = 0.25f;

    /// <summary>Seconds on the ball after which the early-carry bonus stops applying.</summary>
    public const float CarryEarlySeconds = 0.70f;

    /// <summary>Penalty for carrying the ball into a touchline or goal line.</summary>
    public const float CarryLinePenalty = 0.30f;

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

    /// <summary>Nearest-opponent distance at which contact risk starts, in metres.</summary>
    public const float ContactRiskMeters = 2.2f;

    /// <summary>Distance over which the contact risk ramps from zero to its full cost, in metres.</summary>
    public const float ContactRiskRampMeters = 1.0f;

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
    /// 0.8–1.2: with the conversion curve, a clear sight of goal from eight metres (conversion ≈ 0.35) is
    /// worth ≈ 2.2 here, and a covered shot from twenty metres (≈ 0.12) is worth ≈ 0.3.
    /// </summary>
    public const float GoalValue = 7.0f;

    /// <summary>
    /// What a missed or saved shot costs. Smaller than <see cref="LossBase"/> because a shot is lost in the
    /// opponent's half, which is the cheapest place on the pitch to lose the ball.
    /// </summary>
    public const float ShotMissCost = 0.60f;

    /// <summary>Conversion baseline just outside the six-yard box, as a probability.</summary>
    public const float ShotConversionNear = 0.42f;

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

    /// <summary>Goal-mouth angle at which the whole goal is visible and the angle costs nothing, in degrees.</summary>
    public const float ShotMouthAngleFullDegrees = 30f;

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

    /// <summary>
    /// How much one unit of forward progress is worth as a <i>preference</i>, in scale units, on top of what
    /// the reception is worth. The value model is greedy — a completed sideways pass and a completed ball
    /// into the final third are both "one clean possession" — so a forward preference keeps the comparison
    /// leaning up the pitch. It is a stated preference, not a second value term.
    /// </summary>
    public const float ProgressBonus = 0.35f;

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
