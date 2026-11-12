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
    // Carrying, shielding, clearing and shooting.
    //
    // These actions keep their pre-A4 heuristic curves, expressed here in the scale's unit so that the
    // numbers live in one place and can be compared. A4.4 tried to put carrying and shooting on the same
    // probability-weighted shape as a pass (`ReceptionValueModel` for the gain, `ShotConversion` for the
    // shot) and the reference match collapsed: the carry became weaker than any pass, the side played
    // one-touch balls continuously (a 2.7 s flight each), the ball was almost never under control and the
    // 10-minute probe produced no shot at all. The curves below are the tested alternative; A9.2's
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

    /// <summary>Weight on a shot at the base of the range.</summary>
    public const float ShotBaseValue = 0.95f;

    /// <summary>Bonus for a shot taken inside <see cref="ShotCloseDistanceMeters"/>.</summary>
    public const float ShotCloseBonus = 0.35f;

    /// <summary>Distance inside which the close-range shot bonus applies, in metres.</summary>
    public const float ShotCloseDistanceMeters = 16.5f;

    /// <summary>Added to a shot by how clear the lane to goal is.</summary>
    public const float ShotLaneBonus = 0.35f;

    /// <summary>
    /// Distance inside which a shot is considered at all, in metres. Kept because the shot's value curve is
    /// flat in this range (see the block comment above): with a measured conversion curve the AI stopped
    /// shooting entirely, and a measured curve is only worth having once chance creation is stable.
    /// </summary>
    public const float ShotRangeMeters = 23.5f;

    /// <summary>The shot's value on the documented scale: a documented flat curve with a lane term.</summary>
    public static float ShotValue(float distanceMeters, float laneClearance01)
    {
        float lane = Clamp01(laneClearance01);
        float close = distanceMeters < ShotCloseDistanceMeters ? ShotCloseBonus : 0f;
        return ShotBaseValue + close + ShotLaneBonus * lane;
    }

    /// <summary>
    /// How much one unit of forward progress is worth as a <i>preference</i>, in scale units, on top of what
    /// the reception is worth. The value model is greedy — a completed sideways pass and a completed ball
    /// into the final third are both "one clean possession" — so a forward preference keeps the comparison
    /// leaning up the pitch. It is a stated preference, not a second value term.
    /// </summary>
    public const float ProgressBonus = 0.35f;

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
