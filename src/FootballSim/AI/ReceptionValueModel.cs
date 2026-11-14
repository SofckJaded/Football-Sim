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
/// only checkable if the parts are visible rather than folded into one number.
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
///   backward reception is worth less than a forward one at the same pressure; that is what stops
///   circulation without a blanket ban.</item>
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
/// <item><b>Structure</b> — does the reception keep the shape the profile asked for? A wide side values a
///   wide reception; a narrow one values a central one. It is the same width expression
///   <see cref="Tactics.JobRegionScoring"/> uses, so the two cannot drift apart.</item>
/// </list>
///
/// <b>Transition risk is not here.</b> The pass score owns it through its loss term, which rises in the
/// passer's own third (<see cref="ActionValueScale.LossCost"/>). This model only adds value; a second
/// subtraction under the name "turnover exposure" is exactly the duplicate penalty the plan forbids, and
/// the reason the loss term is documented as the single owner.
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

    /// <summary>Forward advance that earns full progression credit, in metres.</summary>
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
        float progression = Clamp01((arrivalProjX - BallProjX(obs)) / ProgressionSpanMeters);

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
            shootingChance = ActionValueScale.ShotConversion(toGoal, shotLane);
        }

        float pressure = Clamp01(1f - receptionPressure01);

        float widthScale = 0.55f + 0.55f * Clamp01(obs.TeamProfile.AttackingWidth01);
        float structure = Clamp01(MathF.Abs(arrivalPointXZ.Y) / MathF.Max(1f, _pitch.HalfWidth * widthScale));

        float ownGoalDistance = MathF.Abs(arrivalProjX + _pitch.HalfLength);
        float ballSafety = Clamp01(ownGoalDistance / BallSafetyDistanceMeters);

        float value = BaseValue
                      + ProgressionWeight * progression
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
