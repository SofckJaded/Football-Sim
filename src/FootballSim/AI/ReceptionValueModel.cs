using System;
using System.Numerics;
using FootballSim.Core;

namespace FootballSim.AI;

/// <summary>
/// What one reception is worth (A4.3), term by term, on the documented action-value scale
/// (<see cref="ActionValueScale"/>). Every term is 0..1 and higher is better — except
/// <see cref="ShootingChance01"/>, which since A5.5b is the conversion probability of the shot the
/// reception makes available (also 0..1, also higher-is-better, but a probability the shot model would
/// agree with rather than a distance ramp).
///
/// The terms are exposed because A4.6 has to explain a decision term by term and because the A4 exit gate
/// is stated as comparisons ("a pressured backward-facing receiver &lt; the open equivalent"), which are
/// only checkable if the parts are visible rather than folded into one number. <see cref="Progression01"/>
/// is the forward component only; since AIB-6b the value term is signed, so lost ground is charged inside
/// <see cref="Value"/> rather than shown as a negative 0..1 term.
/// </summary>
public readonly record struct ReceptionValueTerms(
    float Progression01,
    float Turnability01,
    float NextOptions01,
    float ShootingChance01,
    float Pressure01,
    float Structure01,
    float BallSafety01,
    float Value);
/// <summary>
/// A4.3: the value of the state a player is in once he has the ball at a point — used as the gain in the
/// plan's value convention <c>p·gain − (1−p)·loss</c>.
///
/// What it values, and why each part exists:
/// <list type="bullet">
/// <item><b>Progression</b> — how far up the pitch the reception is, relative to where the ball was. A
///   backward reception is worth less than a forward one at the same pressure, and since AIB-6b a
///   reception <i>behind</i> the ball pays a territory loss instead of being scored like a lateral one;
///   that is what stops circulation without a blanket ban.</item>
/// <item><b>Turnability</b> — can he do anything with it? A receiver arriving with momentum toward his
///   own goal, or with an opponent goal-side of him, has to turn first. This is the term that makes a
///   pressured backward-facing receiver worth less than the open equivalent.</item>
/// <item><b>Next options</b> — is there somebody to combine with, or is he isolated?</item>
/// <item><b>Shooting chance</b> — a reception in the box is worth more than one on the halfway line, and
///   since A5.5b it is worth <b>the chance it creates</b>, priced on the shot's own scale
///   (<see cref="ChanceShare"/>), not a normalised "position is promising" ramp. This is the term that
///   makes a met cross into the box worth more than a clean midfield possession.</item>
/// <item><b>Pressure at reception</b> — supplied by the caller from the <i>one</i> pressure estimate it
///   already has (<see cref="PassSuccessTerms.DefenderPressure01"/> for a pass, the local pressure window
///   for a carry). This model never computes a second one.</item>
/// <item><b>Structure</b> — does the reception keep the shape the profile asked for? In build-up it uses
///   the one shared width convention (<see cref="AttackingWidthPreference"/>) that
///   <see cref="Tactics.JobRegionScoring"/> also uses, so the two cannot drift apart: a reception at the
///   profile's own target width scores full credit, a narrow side is not rewarded for hugging the touchline,
///   and a wider side is never scored below a narrower one at the same lateral coordinate (AUD-01).
///   In the final third the width credit is replaced by a central-threat credit (AIB-4b), because raw
///   width credit gave the centre line 0 and the wings 1 for <i>every</i> profile and structurally
///   outscored central and half-space receptions. AUD-P6 / Fix 5.3 moved that exchange forward into
///   midfield (<see cref="FinalThirdBlendStartProjMeters"/> → <see cref="FinalThirdBlendEndProjMeters"/>),
///   because the AIB-4b window began at the final-third line and left the build-up pass — the one a
///   midfielder actually chooses — priced on width, still pulling the attack to the flank.</item>
/// </list>
///
/// <b>Transition risk is not here.</b> The pass score owns it through its loss term, which rises in the
/// passer's own third (<see cref="ActionValueScale.LossCost"/>). This model prices the state once the ball
/// is kept; a second subtraction under the name "turnover exposure" is exactly the duplicate penalty the
/// plan forbids, and the reason the loss term is documented as the single owner. The progression term can
/// be negative (AIB-6b) because a reception behind the ball leaves the side in a worse position — that is
/// a valuation of the kept-ball state, not a second charge for the risk of losing it.
/// </summary>
public sealed class ReceptionValueModel
{
    /// <summary>
    /// Baseline value of simply having the ball at a point, in scale units
    /// (<see cref="ActionValueScale.Unit"/>), before the position on the pitch is counted.
    /// </summary>
    public const float BaseValue = 0.45f;

    /// <summary>Weight of being able to turn with the ball.</summary>
    public const float TurnabilityWeight = 0.12f;

    /// <summary>Weight of having somebody to combine with.</summary>
    public const float NextOptionsWeight = 0.12f;

    /// <summary>
    /// Share of the goal expectation a reception's shooting chance is worth (A5.5b), as a fraction of what
    /// <see cref="ActionValueScale.ShotValue"/> would pay for the same shot.
    ///
    /// A5.5 left the cross permanently on the table and never chosen, and the value stack was the reason:
    /// a reception six metres from goal was priced as "a clean possession in a good place" (at most
    /// 0.18 of the old distance ramp), while the shot it makes available is worth about 2.3 on the same
    /// scale. A met cross that leaves a striker with a sight of goal was therefore worth less than a
    /// carry in midfield — not a calibration detail but the wrong quantity.
    ///
    /// The chance is deliberately <b>less than</b> the shot: receiving the ball is not shooting it. The
    /// receiver has a touch to take, a defender recovering, and no guarantee he pulls the trigger at all,
    /// while <c>conversion × GoalValue</c> is what the shot itself pays. That share is also what keeps the
    /// A4.4 comparison intact: a pass into the box is worth <c>p × ChanceShare</c> of the chance while the
    /// shot is worth all of it, so at any completion below 1 the shot from the same point still wins
    /// (proven in <c>ReceptionValueModelTests.AClearShotInTheBox_StillBeatsAPassToTheSamePlace</c>).
    ///
    /// 0.50 is the midpoint of the window the two constraints leave: above ≈0.39 the cross starts beating
    /// the carry it should beat, below ≈0.63 a shot from the same spot keeps winning. It is hand-set
    /// calibration like every other constant here — A9.2's ablations and A10 own the real fit.
    /// </summary>
    public const float ChanceShare = 0.50f;

    /// <summary>Weight of the reception being uncontested.</summary>
    public const float PressureWeight = 0.15f;

    /// <summary>Weight of the reception holding the profile's width.</summary>
    public const float StructureWeight = 0.08f;

    // ---------------------------------------------------------------------------------------------
    // AIB-4b / AUD-P6 — the final-third structure credit is central threat, not raw width.
    //
    // AttackingWidthPreference.Score gives the centre line 0 and the wings 1 for every profile
    // (reachedWidth = (|z| / HalfWidth) / 0.55), so a completed flank pass collected a width credit a
    // completed central or half-space pass could never collect. Together with the extra pressure freedom
    // on the flanks that is the "always attacking down the wing" finding. The convention itself is right
    // for build-up and for width jobs (AUD-01), so it is kept there; the attacking reception's position is
    // blended toward central threat instead. AUD-P6 / Fix 5.3 moved that blend forward into midfield
    // (see FinalThirdBlendStartProjMeters), because starting it at the final-third line left the build-up
    // pass — the one a midfielder actually chooses — priced on width alone.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// AUD-P6 / Fix 5.3: attack-relative projection, in metres, at which <see cref="Structure01"/> begins
    /// exchanging the build-up width credit for the central-threat credit.
    ///
    /// It used to begin at the final-third line (<c>HalfLength / 3</c> = 17.5 m on the nominal pitch), so
    /// every midfield reception was still priced on raw width — the centre line scored 0 and the wings 1
    /// for <i>every</i> profile — and the attack kept being pulled to the flank through build-up. The
    /// window starts in midfield on purpose: a central reception at +6 m must already be worth slightly
    /// more than the touchline equivalent, not only once the ball has crossed the final-third line.
    /// </summary>
    public const float FinalThirdBlendStartProjMeters = 5f;

    /// <summary>
    /// AUD-P6 / Fix 5.3: attack-relative projection, in metres, at which the width → central-threat
    /// exchange is complete. The span
    /// (<see cref="FinalThirdBlendEndProjMeters"/> − <see cref="FinalThirdBlendStartProjMeters"/> = 13 m)
    /// keeps the credit continuous instead of stepping — at +6 m the blend is 1/13, not 0 or 1 — and +18 m
    /// is already past the final-third line (17.5 m on the nominal pitch), so by the time the ball reaches
    /// the final third the credit is the central threat the AIB-4b fix intended.
    /// </summary>
    public const float FinalThirdBlendEndProjMeters = 18f;

    /// <summary>
    /// AIB-4b: lateral distance from the centre line that still earns the full central-corridor share of
    /// the final-third threat credit, in metres. 12 m keeps both the central corridor and the half-space
    /// (roughly 12–20 m) valuable; only the touchline channel decays toward zero.
    /// </summary>
    public const float CentralCorridorMeters = 12f;

    /// <summary>Weight of the central-corridor share of the final-third threat credit (AIB-4b).</summary>
    public const float CentralCorridorWeight = 0.60f;

    /// <summary>
    /// Weight of the between-the-lines share of the final-third threat credit (AIB-4b): how close the
    /// reception is to the opponent's last defensive line, with the space in behind it the most valuable.
    /// </summary>
    public const float BetweenLinesWeight = 0.40f;

    /// <summary>
    /// AIB-4b: how far in front of the opponent's last defensive line a final-third reception stops
    /// counting as a between-the-lines ball, in metres. At or behind the line earns full credit (that is
    /// the space in behind); a reception this far in front of it earns none.
    /// </summary>
    public const float BetweenLinesSpanMeters = 18f;

    /// <summary>
    /// AIB-4b: back-line proximity assumed when no opponent is visible at all — a neutral read, not full
    /// credit, so an ignorant receiver is not treated as having beaten the line.
    /// </summary>
    public const float UnknownBackLineProximity01 = 0.5f;

    /// <summary>
    /// Weight of the ball being somewhere it can be kept. A reception right in front of your own goal is
    /// worth less than the same reception higher up, because a first touch that goes wrong there is a
    /// goal — measured during A4.4, when removing the backward-pass ban turned deep back-passes into own
    /// goals. It is a value term, not a second risk subtraction: the loss term still owns turnovers.
    /// </summary>
    public const float BallSafetyWeight = 0.10f;

    /// <summary>Distance from the own goal line at which a reception is considered fully safe, in metres.</summary>
    public const float BallSafetyDistanceMeters = 30f;

    /// <summary>
    /// Weight of how far up the pitch the reception is, measured as the advance from where the ball is.
    /// </summary>
    public const float ProgressionWeight = 0.25f;

    /// <summary>
    /// Forward advance that earns full progression credit, in metres. AIB-6b: the same span is the
    /// denominator of the territory <i>loss</i>, so one span of lost ground is charged the full
    /// <see cref="ProgressionWeight"/> below the lateral baseline.
    /// </summary>
    public const float ProgressionSpanMeters = 30f;

    /// <summary>Room goal-side of the reception at which turning is considered free, in metres.</summary>
    public const float TurnSpaceMeters = 10f;

    /// <summary>A team-mate at or inside this distance counts as full support, in metres.</summary>
    public const float SupportNearMeters = 8f;

    /// <summary>Beyond this distance a team-mate is no support at all, in metres.</summary>
    public const float SupportFarMeters = 33f;

    private readonly PitchDimensions _pitch;

    public ReceptionValueModel(PitchDimensions pitch)
    {
        _pitch = pitch ?? throw new ArgumentNullException(nameof(pitch));
    }

    /// <summary>Largest value the combination can produce; the scale's upper bound for a reception.</summary>
    public float MaxValue => BaseValue + ProgressionWeight + TurnabilityWeight + NextOptionsWeight
                             + PressureWeight + StructureWeight + BallSafetyWeight
                             + ChanceShare * ActionValueScale.GoalValue * ActionValueScale.ShotConversionNear;

    /// <summary>
    /// Values the reception at <paramref name="arrivalPointXZ"/> for <paramref name="receiverId"/>.
    /// <paramref name="receptionPressure01"/> is the caller's one pressure estimate for that point
    /// (0 = free, 1 = fully pressed).
    /// </summary>
    public ReceptionValueTerms Evaluate(
        in FootballerObservation obs,
        int receiverId,
        Vector2 arrivalPointXZ,
        float receptionPressure01)
    {
        float attackDir = obs.AttackingDirectionX;
        float arrivalProjX = arrivalPointXZ.X * attackDir;

        // Progression is where the ball ends up, not how far it travelled: receiving in the opponent's box
        // is valuable whether the ball came forty metres or four. How *forward* the pass was is a separate,
        // stated preference (ActionValueScale.ProgressBonus) that keeps the greedy comparison leaning up
        // the pitch. The measured span is reported alongside so a decision can be explained (A4.6).
        //
        // AIB-6b: losing ground costs territory — it is not a lateral pass. The exposed Progression01 stays
        // the forward 0..1 component (its meaning and bounds are unchanged), but the value term is signed:
        // a reception behind the ball pays up to -ProgressionWeight for one span of lost ground. The old
        // expression clamped the raw advance at zero, so a 25 m backward reception scored exactly like a
        // pass to the side and still collected the full pressure and turnability credit on top.
        float advanceMeters = arrivalProjX - BallProjX(obs);
        float progression = Clamp01(advanceMeters / ProgressionSpanMeters);
        float progressionValue01 = Math.Clamp(advanceMeters / ProgressionSpanMeters, -1f, 1f);

        // The receiver's own motion: arriving with momentum toward his own goal costs him a turn, and so
        // does an opponent standing goal-side of the reception.
        Vector2 receiverVelocityXZ = VelocityOf(obs, receiverId);
        float backwardsSpeed = MathF.Max(0f, -Vector2.Dot(receiverVelocityXZ, new Vector2(attackDir, 0f)));
        float momentumCost = Clamp01(backwardsSpeed / 6f);
        float goalSideRoom = Clamp01(NearestGoalSideOpponentDistance(obs, arrivalPointXZ) / TurnSpaceMeters);
        float turnability = Clamp01(0.45f * (1f - momentumCost) + 0.55f * goalSideRoom);

        float nearestMate = NearestTeammateDistance(obs, receiverId, arrivalPointXZ);
        float nextOptions = nearestMate >= SupportFarMeters
            ? 0f
            : Clamp01(1f - (nearestMate - SupportNearMeters) / (SupportFarMeters - SupportNearMeters));

        Vector2 opponentGoalXZ = new(attackDir * _pitch.HalfLength, 0f);
        float toGoal = Vector2.Distance(arrivalPointXZ, opponentGoalXZ);

        // A5.5b: the chance the reception creates, on the shot's own scale. The lane is measured from the
        // reception to the goal (the keeper is skipped by the lane test's own near/far cut-offs), and it is
        // only measured when the position is inside shooting range at all — a reception on the halfway line
        // has no chance to price and pays nothing for the query.
        float shootingChance = 0f;
        if (toGoal <= ActionValueScale.ShotFarMeters)
        {
            float shotLane = CandidateGenerator.EvaluateLaneClearance(
                arrivalPointXZ, opponentGoalXZ, obs.VisiblePlayers, obs.SelfTeam);
            // A9.6d: the same goal-mouth angle a shot from this point would see. A reception at the byline is
            // not a shooting chance just because it is close to the goal — one conversion estimate, not two.
            Vector2 toNearPost = new Vector2(opponentGoalXZ.X, _pitch.HalfGoalWidth) - arrivalPointXZ;
            Vector2 toFarPost = new Vector2(opponentGoalXZ.X, -_pitch.HalfGoalWidth) - arrivalPointXZ;
            float mouthAngle = 180f;
            if (toNearPost.LengthSquared() > 1e-6f && toFarPost.LengthSquared() > 1e-6f)
            {
                float cosine = Math.Clamp(
                    Vector2.Dot(Vector2.Normalize(toNearPost), Vector2.Normalize(toFarPost)), -1f, 1f);
                mouthAngle = MathF.Acos(cosine) * (180f / MathF.PI);
            }
            shootingChance = ActionValueScale.ShotConversion(toGoal, shotLane, mouthAngle);
        }

        float pressure = Clamp01(1f - receptionPressure01);

        // AUD-01: the profile's width preference, shared with job-region scoring. The old expression
        // divided |z| by a widthScale that grew with AttackingWidth01, so a wide side scored HALF of a
        // narrow side's width term at the same coordinate and its target lay past the touchline.
        // AIB-4b: build-up keeps that credit unchanged; in the final third it is exchanged for the
        // central-threat credit (the old raw width credit gave the centre line 0 for every profile).
        float structure = Structure01(obs, arrivalPointXZ, arrivalProjX);

        float ownGoalDistance = MathF.Abs(arrivalProjX + _pitch.HalfLength);
        float ballSafety = Clamp01(ownGoalDistance / BallSafetyDistanceMeters);

        float value = BaseValue
                      + ProgressionWeight * progressionValue01
                      + TurnabilityWeight * turnability
                      + NextOptionsWeight * nextOptions
                      + ChanceShare * ActionValueScale.GoalValue * shootingChance
                      + PressureWeight * pressure
                      + StructureWeight * structure
                      + BallSafetyWeight * ballSafety;

        return new ReceptionValueTerms(
            Progression01: progression,
            Turnability01: turnability,
            NextOptions01: nextOptions,
            ShootingChance01: shootingChance,
            Pressure01: pressure,
            Structure01: structure,
            BallSafety01: ballSafety,
            Value: Math.Clamp(value, 0f, MaxValue));
    }

    /// <summary>
    /// The positional-structure credit (AUD-01, AIB-4b, AUD-P6). Below
    /// <see cref="FinalThirdBlendStartProjMeters"/> it is the shared width convention, unchanged. From +5 m
    /// to +18 m it is blended into a <b>central-threat</b> credit: the raw width credit gave the centre line
    /// 0 and the wings 1 for every profile, which made flank receptions systematically outscore central and
    /// half-space ones — the "always attacking down the wing" finding. AUD-P6 / Fix 5.3 moved that exchange
    /// into midfield (it used to start at the final-third line, 17.5 m, and finish at 25.5 m), because a
    /// midfield reception is exactly where the next pass is chosen and the old window left it priced on
    /// width. The replacement (not an extra bonus) keeps <see cref="StructureWeight"/> the only weight on
    /// positional structure and the term in 0..1, so <see cref="MaxValue"/> stays an upper bound.
    /// </summary>
    private float Structure01(in FootballerObservation obs, Vector2 arrivalPointXZ, float arrivalProjX)
    {
        float widthCredit = AttackingWidthPreference.Score(
            obs.TeamProfile.AttackingWidth01, arrivalPointXZ.Y, _pitch);
        float finalThird01 = FinalThirdBlend01(arrivalProjX);
        if (finalThird01 <= 0f) return widthCredit;

        float centralThreat = CentralThreat01(obs, arrivalPointXZ, arrivalProjX);
        return Clamp01(widthCredit + finalThird01 * (centralThreat - widthCredit));
    }

    /// <summary>
    /// AUD-P6 / Fix 5.3: 0 while the reception is behind <see cref="FinalThirdBlendStartProjMeters"/>,
    /// ramping to 1 over the 13 m to <see cref="FinalThirdBlendEndProjMeters"/>, so the credit changes
    /// continuously and a midfield reception is not abruptly repriced at a boundary. The old window was
    /// <c>(HalfLength / 3 = 17.5 → 25.5 m)</c>; it started too deep in the attacking third for the blend to
    /// affect the pass a midfielder actually chooses.
    /// </summary>
    private float FinalThirdBlend01(float arrivalProjX) =>
        Clamp01((arrivalProjX - FinalThirdBlendStartProjMeters)
                / (FinalThirdBlendEndProjMeters - FinalThirdBlendStartProjMeters));

    /// <summary>
    /// AIB-4b: how threatening a final-third reception point is, independent of the profile's width — the
    /// central corridor (<see cref="CentralCorridorWeight"/>) plus how close the reception is to the
    /// opponent's last defensive line (<see cref="BetweenLinesWeight"/>). The corridor part keeps the
    /// centre and the half-space valuable and lets only the touchline channel decay; the line part is what
    /// makes the between-the-lines ball worth more than a safe ball in front of the block. The same fixed
    /// weight split is used for every profile, so this term cannot reintroduce the AUD-01
    /// inversion: it simply does not read <c>AttackingWidth01</c> at all. Believed opponents only, like
    /// every other spatial term in this model.
    /// </summary>
    private float CentralThreat01(in FootballerObservation obs, Vector2 arrivalPointXZ, float arrivalProjX)
    {
        float halfWidth = _pitch.HalfWidth;
        float lateralAbs = MathF.Abs(arrivalPointXZ.Y);
        float corridorSpan = halfWidth - CentralCorridorMeters;
        float centrality01 = corridorSpan > 0f
            ? Clamp01(1f - MathF.Max(0f, lateralAbs - CentralCorridorMeters) / corridorSpan)
            // A pitch narrower than the nominal corridor is all corridor: fall back to the plain ramp.
            : Clamp01(1f - lateralAbs / MathF.Max(halfWidth, 1e-3f));

        float lineProximity01 = BackLineProximity01(obs, arrivalProjX);
        return Clamp01(CentralCorridorWeight * centrality01 + BetweenLinesWeight * lineProximity01);
    }

    /// <summary>
    /// AIB-4b: 1 when the reception is at or goal-side of the opponent's last defensive line (the space in
    /// behind it), decaying linearly to 0 <see cref="BetweenLinesSpanMeters"/> in front of it — the "in
    /// front of the block" safe ball.
    ///
    /// The last line is the <b>deepest outfield defender</b>: the one nearest his own goal, i.e. the
    /// <b>largest</b> attack-relative projection, with the goalkeeper excluded — the repo's last-line
    /// convention (<see cref="DefenderResponseModel"/>). Taking the smallest projection instead would pick
    /// the opponents' most advanced attacker, leave the gap negative for every attacking-third reception
    /// and pin the credit at 1; including the keeper would make the keeper the line. No outfield opponent
    /// visible means the neutral <see cref="UnknownBackLineProximity01"/>, never full credit.
    /// </summary>
    private float BackLineProximity01(in FootballerObservation obs, float arrivalProjX)
    {
        float attackDir = obs.AttackingDirectionX;
        float backLineProjX = float.NegativeInfinity;
        bool seen = false;

        for (int index = 0; index < obs.VisiblePlayers.Count; index++)
        {
            PerceivedEntity entity = obs.VisiblePlayers[index];
            if (entity.Team == obs.SelfTeam || entity.Role == TacticalRole.Goalkeeper) continue;

            float opponentProjX = entity.EstimatedPosition.X * attackDir;
            if (opponentProjX <= backLineProjX) continue;

            backLineProjX = opponentProjX;
            seen = true;
        }

        if (!seen) return UnknownBackLineProximity01;

        // Positive gap = the reception is still in front of the line; negative = it has beaten it.
        float gapMeters = backLineProjX - arrivalProjX;
        return Clamp01(1f - gapMeters / BetweenLinesSpanMeters);
    }

    /// <summary>
    /// How much room the reception point has on the goal side — the space a player needs to receive and
    /// turn upfield. Measured against the <i>believed</i> opponents only, like every other spatial term.
    ///
    /// An opponent is goal-side when he stands <b>closer to the goal being attacked than the reception
    /// point</b> (a greater attack-relative projection): that is the body a receiver has to turn into.
    /// An opponent behind the reception is a press from behind, which the caller's pressure term already
    /// charges — counting him here as well would answer a different question. (The comparison was
    /// inverted until the A4 pass audit: it kept only the opponents *behind* the reception, so a marked
    /// forward looked free to turn.)
    /// </summary>
    private float NearestGoalSideOpponentDistance(in FootballerObservation obs, Vector2 arrivalPointXZ)
    {
        float attackDir = obs.AttackingDirectionX;
        float arrivalProjX = arrivalPointXZ.X * attackDir;
        float nearest = float.MaxValue;

        for (int index = 0; index < obs.VisiblePlayers.Count; index++)
        {
            PerceivedEntity entity = obs.VisiblePlayers[index];
            if (entity.Team == obs.SelfTeam) continue;

            Vector2 opponentXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            if (opponentXZ.X * attackDir <= arrivalProjX) continue;
            float distance = Vector2.Distance(opponentXZ, arrivalPointXZ);
            if (distance < nearest) nearest = distance;
        }

        return nearest == float.MaxValue ? TurnSpaceMeters : nearest;
    }

    private static float NearestTeammateDistance(
        in FootballerObservation obs,
        int receiverId,
        Vector2 arrivalPointXZ)
    {
        float nearest = float.MaxValue;
        for (int index = 0; index < obs.VisiblePlayers.Count; index++)
        {
            PerceivedEntity entity = obs.VisiblePlayers[index];
            if (entity.Team != obs.SelfTeam || entity.PlayerId == receiverId) continue;
            Vector2 mateXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            float distance = Vector2.Distance(mateXZ, arrivalPointXZ);
            if (distance < nearest) nearest = distance;
        }

        return nearest;
    }

    private static float BallProjX(in FootballerObservation obs) =>
        obs.PerceivedBallPosition.X * obs.AttackingDirectionX;

    private static Vector2 VelocityOf(in FootballerObservation obs, int receiverId)    {
        if (receiverId == obs.SelfId) return new Vector2(obs.SelfVelocity.X, obs.SelfVelocity.Z);
        for (int index = 0; index < obs.VisiblePlayers.Count; index++)
        {
            PerceivedEntity entity = obs.VisiblePlayers[index];
            if (entity.PlayerId != receiverId) continue;
            return new Vector2(entity.EstimatedVelocity.X, entity.EstimatedVelocity.Z);
        }

        return Vector2.Zero;
    }

    private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;
}

/// <summary>
/// AUD-01: the one width-preference convention shared by reception valuation
/// (<see cref="ReceptionValueModel.Evaluate"/>, build-up positions only since AIB-4b) and job-region
/// scoring (<see cref="Tactics.JobRegionScoring.Score"/>), so the two cannot drift apart again.
///
/// It replaces the old expression <c>|z| / (HalfWidth * widthScale)</c> with <c>widthScale</c> growing
/// with <see cref="TeamTacticalProfile.AttackingWidth01"/>. Because that scale sat in the denominator,
/// a wide side scored <b>half</b> a narrow side's width term at the same lateral coordinate (0.40 vs
/// 0.80 at |z| = 15 m on a 68 m pitch), and the widest side's denominator reached 37.4 m — past the
/// 34 m half-width — so it could never reach full credit on the pitch. This convention has the
/// properties the audit requires:
/// <list type="bullet">
/// <item>at a fixed lateral coordinate the credit is monotonically non-decreasing in
///   <c>AttackingWidth01</c>; a wide side is never punished for standing where a narrow side scores
///   well (the core inversion);</item>
/// <item>the credit peaks at the profile's own target width (<see cref="TargetFraction01"/>), so a
///   narrow side loses credit beyond its target instead of treating the touchline as its best
///   position, and a wide side's peak reaches further out than a narrow side's;</item>
/// <item>it is a dimensionless 0..1 credit, continuous, with no denominator that can pass
///   <see cref="PitchDimensions.HalfWidth"/>: the lateral coordinate is normalised by the half-width
///   and clamped, so nothing is asked for beyond the touchline.</item>
/// </list>
/// The shape is a "reached the width" ramp up to <see cref="WidthReachedFraction01"/>, multiplied by
/// a profile penalty once the position is past the profile's target. The ramp is deliberately
/// profile-independent: a profile-dependent ramp would again lower a wide side's score at the same
/// |z| — the inversion this convention exists to remove.
///
/// <b>Scope (AIB-4b, AUD-P6).</b> This class keeps its AUD-01 meaning unchanged and remains the width
/// credit for build-up receptions and for wide jobs (<see cref="Tactics.JobRegionScoring"/>). Only the
/// attacking-reception value differs: <see cref="ReceptionValueModel"/> exchanges this credit for its
/// central-threat credit from <see cref="ReceptionValueModel.FinalThirdBlendStartProjMeters"/> onward, so
/// midfield build-up receptions are no longer priced on width alone (AUD-P6 / Fix 5.3 moved that exchange
/// forward from the final-third line). The reason is structural: raw width credit gives the centre line 0
/// for every profile — a central-reception penalty of its own, not an AUD-01 inversion.
/// </summary>
public static class AttackingWidthPreference
{
    /// <summary>
    /// Lateral utilisation (|z| / half-width) at which the "reaching width" ramp is full. It is a
    /// property of having width at all, not of the profile, so that a wider profile is never behind a
    /// narrower one at the same coordinate (AUD-01). 0.55 is the saturation point the old narrow-side
    /// denominator already had (18.7 m on a 68 m pitch), so the narrow side's ramp is unchanged and
    /// only the profile inversion is removed.
    /// </summary>
    public const float WidthReachedFraction01 = 0.55f;

    /// <summary>Target lateral utilisation of the narrowest profile (<c>AttackingWidth01 = 0</c>).</summary>
    public const float NarrowTargetFraction01 = 0.70f;

    /// <summary>
    /// Target lateral utilisation of the widest profile. It is kept inside the touchline so every
    /// profile's target is reachable on the pitch — the old expression's 37.4 m "target" was not.
    /// </summary>
    public const float WideTargetFraction01 = 0.95f;

    /// <summary>
    /// Share of the width credit a narrow profile loses at the touchline. The credit there is 0.25
    /// rather than 0: working the touchline is over-wide for a narrow shape, not forbidden by it.
    /// </summary>
    public const float OverWidthPenalty01 = 0.75f;

    /// <summary>
    /// The profile's preferred lateral utilisation, in 0..1 of the half-width: 0.70 for the narrowest
    /// side, 0.95 for the widest. It never exceeds 1, so no target is off the pitch; the wide-job
    /// nominal target can be read against this convention.
    /// </summary>
    public static float TargetFraction01(float attackingWidth01) =>
        NarrowTargetFraction01 + (WideTargetFraction01 - NarrowTargetFraction01) * Clamp01(attackingWidth01);

    /// <summary>The profile's preferred target distance from the centre line, in metres.</summary>
    public static float TargetLateralMeters(float attackingWidth01, PitchDimensions pitch)
    {
        ArgumentNullException.ThrowIfNull(pitch);
        return TargetFraction01(attackingWidth01) * pitch.HalfWidth;
    }

    /// <summary>
    /// The 0..1 width credit of a position <paramref name="lateralAbsMeters"/> off the centre line for
    /// a profile of width <paramref name="attackingWidth01"/>. 0 = the centre line (no width used),
    /// 1 = the profile's width is satisfied; a narrow profile pays the over-width penalty past its
    /// target. Plain arithmetic and allocation-free: called per candidate and per reception.
    /// </summary>
    public static float Score(float attackingWidth01, float lateralAbsMeters, PitchDimensions pitch)
    {
        ArgumentNullException.ThrowIfNull(pitch);
        float halfWidth = pitch.HalfWidth;
        if (!(halfWidth > 0f)) return 0f;

        float utilisation = Clamp01(MathF.Abs(lateralAbsMeters) / halfWidth);
        float reachedWidth = Clamp01(utilisation / WidthReachedFraction01);
        float target = TargetFraction01(attackingWidth01);

        // Past its own target the profile is working wider than it asked for; the penalty is relative
        // to the room left to the touchline, so it is 1 at |z| = half-width for every profile, and the
        // divisor cannot vanish because the target stays at or below WideTargetFraction01 < 1.
        float overWidth01 = utilisation <= target
            ? 0f
            : Clamp01((utilisation - target) / (1f - target));

        return Clamp01(
            reachedWidth * (1f - OverWidthPenalty01 * (1f - Clamp01(attackingWidth01)) * overWidth01));
    }

    private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;
}
