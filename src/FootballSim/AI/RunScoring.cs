using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Tactics;

namespace FootballSim.AI;

/// <summary>
/// The seven terms an off-ball path is priced on (A5.2), plus the travel cost.
///
/// This is the off-ball counterpart of <see cref="ValueTerms"/>: a run is not an action on the ball, so it
/// is not forced into the pass convention (<c>p·gain − (1−p)·loss</c>). It is a bet that a region will be
/// worth occupying when the ball can arrive, and the terms say which part of that bet is weak. A negative
/// term is a cost the path pays (<see cref="TransitionExposure01"/>, <see cref="TravelMeters"/>); the six
/// positive terms sum to at most 0.60, so a run is worth roughly half a clean possession at best.
/// </summary>
public readonly record struct RunScoreTerms(
    float ArrivalAdvantage01,
    float PassAvailability01,
    float ReceivingOrientation01,
    float LineBreaks01,
    float Congestion01,
    float RoleUsefulness01,
    float TransitionExposure01,
    float TravelMeters,
    float Score)
{
    public static RunScoreTerms None => new(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, float.NegativeInfinity);

    /// <summary>Compact text form for a trace line or a failure message.</summary>
    public string Describe() =>
        $"arrive {ArrivalAdvantage01:F2} · pass {PassAvailability01:F2} · orient {ReceivingOrientation01:F2} · " +
        $"break {LineBreaks01:F2} · room {Congestion01:F2} · role {RoleUsefulness01:F2} · " +
        $"exposed {TransitionExposure01:F2} · travel {TravelMeters:F0} m ⇒ {Score:F2}";
}

/// <summary>
/// A5.2: scores one candidate path against what the player can actually see.
///
/// The knowledge boundary holds: opponents enter through the player's own
/// <see cref="FootballerObservation.VisiblePlayers"/> (which already applies sight filtering and memory)
/// and their bodies through <see cref="PerceivedEntity.Capability"/>, never live attributes. The team's
/// rest-defence picture arrives as <see cref="RestDefence"/> from the team job coordinator (A3.6) rather
/// than being re-estimated here.
///
/// <b>The terms, and why each exists:</b>
/// <list type="bullet">
/// <item><b>arrival advantage</b> — can this player be in the region sooner than the defender who would
///   contest it? Measured through the one reach model, against the closest believed opponent to the
///   meeting point. This is the difference between a run and a wish.</item>
/// <item><b>pass availability</b> — a perfectly timed run into a lane nobody can play through is worth
///   nothing: the corridor from the carrier and the distance band both count.</item>
/// <item><b>receiving orientation</b> — the direction the player is running when he arrives. Arriving at
///   the goal is worth more than arriving across it, and far more than arriving with your back to it.</item>
/// <item><b>line breaks</b> — how many believed opponents the ball would have to pass to reach the
///   region. One line broken is real progress; the term saturates at two.</item>
/// <item><b>congestion</b> — the room at the meeting region <i>when the player gets there</i>, from the
///   opponents' predicted positions. A3.4 already scores the space a job region occupies today; this one
///   is the space it will have.</item>
/// <item><b>role usefulness</b> — a striker running in behind is doing his job; a centre-back doing it is
///   leaving one. Same geometry, different value.</item>
/// <item><b>transition exposure</b> — what the run costs if the ball is lost now (A3.6's aggregate). This
///   is the one term that subtracts, and it is the reason a third forward does not go when the side is
///   already stretched.</item>
/// </list>
///
/// Every constant here is hand-set calibration, like the rest of the AI: changing one changes behaviour
/// and needs a version bump plus a fresh tactical gate.
/// </summary>
public static class RunScoring
{
    // ---------------------------------------------------------------------------------------------
    // Weights. The six positive weights sum to 0.60 and the travel cost can take back 0.35, so a run's
    // priced contribution stays comparable with the job base value it is added to in the generator
    // (0.90–1.00), and a run never outweighs the whole on-ball action scale on its own.
    // ---------------------------------------------------------------------------------------------

    public const float ArrivalWeight = 0.15f;
    public const float PassWeight = 0.13f;
    public const float OrientationWeight = 0.08f;
    public const float LineBreakWeight = 0.05f;
    public const float CongestionWeight = 0.09f;
    public const float RoleWeight = 0.10f;
    public const float ExposureWeight = 0.12f;
    public const float TravelWeight = 0.35f;

    /// <summary>Seconds of arrival advantage that count as a fully won race.</summary>
    public const float ArrivalAdvantageScaleSeconds = 1.0f;

    /// <summary>
    /// Longest look-ahead for a run's own arrival. Longer than the interception default because a run is
    /// a two-to-four second intention, not a duel; a path this model cannot reach in this window is not
    /// offered at all.
    /// </summary>
    public const float ArrivalHorizonSeconds = 6f;

    /// <summary>
    /// Fastest a believed opponent may plausibly sprint, used only to skip opponents who cannot reach the
    /// meeting region within the horizon. Deliberately above the real attribute cap (8.4 m/s + fatigue
    /// headroom) so the filter never hides a defender who could get there.
    /// </summary>
    public const float MaxPlausibleSprintMetersPerSecond = 11f;

    /// <summary>Distance at which the arrival race stops caring about a defender.</summary>
    public const float NeutralArrivalAdvantage = 0.5f;

    /// <summary>Closest useful pass into a run: nearer than this is a give-and-go, not a through ball.</summary>
    public const float MinUsefulPassMeters = 6f;

    /// <summary>Furthest useful pass into a run, in metres.</summary>
    public const float MaxUsefulPassMeters = 32f;

    /// <summary>Distance into which a run's meeting region counts as fully roomy, in metres.</summary>
    public const float CongestionSaturationMeters = 8f;

    /// <summary>How far forward a defender may move in the arrival prediction, in seconds.</summary>
    public const float MaxPredictionSeconds = 1.5f;

    /// <summary>Lateral corridor within which an opponent counts as "on the lane", in metres.</summary>
    public const float LineBreakCorridorMeters = 12f;

    /// <summary>Neutral transition exposure when the team has no rest-defence picture yet.</summary>
    public const float NeutralExposure = 0.5f;

    /// <summary>
    /// One path's score, in the same additive shape the generator adds to the duty's base value.
    /// Allocation-free: the opponent scan reads the observation's list in place.
    /// </summary>
    public static RunScoreTerms Score(
        in RunPath path,
        in FootballerObservation obs,
        Vector2 carrierXZ,
        in RestDefence security)
    {
        Vector2 meetXZ = path.MeetXZ;
        if (!SimMath.IsFinite(meetXZ) || !SimMath.IsFinite(carrierXZ)) return RunScoreTerms.None;

        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);

        // The player's own arrival is what the window is built from; a frozen intent is measured by the
        // time left in its own plan, so re-scoring it does not silently re-time the run.
        ReachEstimate own = TimeToReachModel.ToPoint(
            ReachKinematics.FromObservation(obs), meetXZ, ArrivalHorizonSeconds);
        float mySeconds = own.Reachable ? own.Seconds : ArrivalHorizonSeconds;

        float arrival = ArrivalAdvantage(obs, meetXZ, mySeconds);
        float pass = PassAvailability(obs, carrierXZ, meetXZ);
        float orientation = ReceivingOrientation(path, obs.AttackingDirectionX);
        float lineBreaks = LineBreaks(obs, carrierXZ, meetXZ);
        float congestion = Congestion(obs, meetXZ, mySeconds, path.MeetRadiusMeters, out float nearestAtArrival);
        float role = RoleUsefulness(path.Kind, obs.SelfRole);
        float exposure = TransitionExposure(security);
        float travel = Vector2.Distance(selfXZ, meetXZ);

        float score = ArrivalWeight * arrival
                    + PassWeight * pass
                    + OrientationWeight * orientation
                    + LineBreakWeight * lineBreaks
                    + CongestionWeight * congestion
                    + RoleWeight * role
                    - ExposureWeight * exposure
                    - TravelWeight * MathF.Min(travel, 40f) / 40f;

        return new RunScoreTerms(arrival, pass, orientation, lineBreaks, congestion, role, exposure,
            float.IsFinite(travel) ? travel : 0f, score);
    }

    /// <summary>
    /// Can this player be in the meeting region sooner than the defender who would contest it? Measured
    /// against the <i>nearest</i> believed opponent to the region, through the reach model, with the
    /// look-ahead capped just past this player's own arrival — a defender who cannot get there by then
    /// has lost the race by definition.
    /// </summary>
    public static float ArrivalAdvantage(in FootballerObservation obs, Vector2 meetXZ, float mySeconds)
    {
        float horizon = MathF.Min(mySeconds + ArrivalAdvantageScaleSeconds, ArrivalHorizonSeconds + ArrivalAdvantageScaleSeconds);
        float closestStraight = float.PositiveInfinity;
        int candidateIndex = -1;

        IReadOnlyList<PerceivedEntity> visible = obs.VisiblePlayers;
        for (int i = 0; i < visible.Count; i++)
        {
            PerceivedEntity entity = visible[i];
            if (entity.Team == obs.SelfTeam) continue;

            Vector2 entityXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            if (!SimMath.IsFinite(entityXZ)) continue;

            float straight = Vector2.Distance(entityXZ, meetXZ);
            if (straight > horizon * MaxPlausibleSprintMetersPerSecond) continue;
            if (straight >= closestStraight) continue;

            closestStraight = straight;
            candidateIndex = i;
        }

        if (candidateIndex < 0) return NeutralArrivalAdvantage;

        ReachEstimate theirs = TimeToReachModel.ToPoint(
            ReachKinematics.FromEntity(visible[candidateIndex]), meetXZ, horizon);
        float opponentSeconds = theirs.Reachable ? theirs.Seconds : horizon;
        return Clamp01((opponentSeconds - mySeconds) / ArrivalAdvantageScaleSeconds);
    }

    /// <summary>
    /// Is there a pass into this region? The corridor from the carrier (the one lane metric every caller
    /// uses) and the distance band, weighted 60/40: a clear lane at silly range is still not available.
    /// </summary>
    public static float PassAvailability(in FootballerObservation obs, Vector2 carrierXZ, Vector2 meetXZ)
    {
        float lane = CandidateGenerator.EvaluateLaneClearance(carrierXZ, meetXZ, obs.VisiblePlayers, obs.SelfTeam);
        float distance = Vector2.Distance(carrierXZ, meetXZ);

        float range;
        if (distance <= MinUsefulPassMeters)
            range = Clamp01(distance / MinUsefulPassMeters) * 0.5f;   // a give-and-go is a poor run target
        else if (distance <= MaxUsefulPassMeters)
            range = 1f;
        else
            range = Clamp01(1f - (distance - MaxUsefulPassMeters) / 12f);

        return 0.6f * lane + 0.4f * range;
    }

    /// <summary>
    /// The body the run arrives with. 1 when the meeting region is attacked straight at the opponent
    /// goal (the ball played in front of him continues goalward), 0.5 when the run is sideways, 0 when he
    /// arrives with his back to goal and has to turn before he can play.
    /// </summary>
    public static float ReceivingOrientation(in RunPath path, float attackDirX)
    {
        Vector2 runDir = path.RunDirectionXZ(attackDirX);
        float alongGoal = Vector2.Dot(runDir, new Vector2(attackDirX, 0f));
        return Clamp01(0.5f + 0.5f * alongGoal);
    }

    /// <summary>
    /// How many believed opponents the ball would have to pass to reach the region, saturating at two.
    /// Only opponents near the line from the carrier to the region count: a full-back on the far
    /// touchline is not a line the pass breaks.
    /// </summary>
    public static float LineBreaks(in FootballerObservation obs, Vector2 carrierXZ, Vector2 meetXZ)
    {
        Vector2 segment = meetXZ - carrierXZ;
        float length = segment.Length();
        if (length < 1e-3f) return 0f;

        Vector2 direction = segment / length;
        int broken = 0;

        IReadOnlyList<PerceivedEntity> visible = obs.VisiblePlayers;
        for (int i = 0; i < visible.Count; i++)
        {
            PerceivedEntity entity = visible[i];
            if (entity.Team == obs.SelfTeam) continue;

            Vector2 opponentXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            Vector2 relative = opponentXZ - carrierXZ;
            float along = Vector2.Dot(relative, direction);
            if (along <= 1f || along >= length - 0.5f) continue;

            Vector2 onLine = carrierXZ + direction * along;
            if (Vector2.Distance(opponentXZ, onLine) > LineBreakCorridorMeters) continue;
            broken++;
        }

        return Clamp01(broken / 2f);
    }

    /// <summary>
    /// Room at the meeting region when the player gets there, from the opponents' predicted positions
    /// (constant velocity, capped at <see cref="MaxPredictionSeconds"/> so a defender sprinting away does
    /// not manufacture space). An empty belief counts as roomy, the same convention A3.4's space term
    /// uses: nothing known is nothing in the way.
    /// </summary>
    public static float Congestion(
        in FootballerObservation obs,
        Vector2 meetXZ,
        float arrivalSeconds,
        float meetRadiusMeters,
        out float nearestAtArrival)
    {
        float predictionSeconds = MathF.Min(
            float.IsFinite(arrivalSeconds) ? MathF.Max(0f, arrivalSeconds) : MaxPredictionSeconds,
            MaxPredictionSeconds);

        nearestAtArrival = float.PositiveInfinity;
        IReadOnlyList<PerceivedEntity> visible = obs.VisiblePlayers;
        for (int i = 0; i < visible.Count; i++)
        {
            PerceivedEntity entity = visible[i];
            if (entity.Team == obs.SelfTeam) continue;

            Vector2 predicted = new(
                entity.EstimatedPosition.X + entity.EstimatedVelocity.X * predictionSeconds,
                entity.EstimatedPosition.Z + entity.EstimatedVelocity.Z * predictionSeconds);
            if (!SimMath.IsFinite(predicted)) continue;

            float distance = Vector2.Distance(predicted, meetXZ);
            if (distance < nearestAtArrival) nearestAtArrival = distance;
        }

        if (!float.IsFinite(nearestAtArrival)) return 1f;

        float radius = float.IsFinite(meetRadiusMeters) ? MathF.Max(0.5f, meetRadiusMeters) : 5f;
        return Clamp01((nearestAtArrival - radius) / CongestionSaturationMeters);
    }

    /// <summary>
    /// How much of a run this player's role is supposed to make, in [0,1]. Read by the scorer and by the
    /// generator's kind filter, so a centre-back is never offered a box run in the first place.
    /// </summary>
    public static float RoleUsefulness(RunKind kind, TacticalRole role) => kind switch
    {
        RunKind.HoldWidth => role switch
        {
            TacticalRole.Winger => 1.00f,
            TacticalRole.FullBack => 0.60f,
            TacticalRole.CentralMidfielder => 0.45f,
            _ => 0.25f
        },
        RunKind.CheckShort => role switch
        {
            TacticalRole.DefensiveMidfielder => 1.00f,
            TacticalRole.CentralMidfielder => 0.95f,
            TacticalRole.CenterBack => 0.85f,
            TacticalRole.FullBack => 0.80f,
            TacticalRole.Winger => 0.55f,
            _ => 0.45f
        },
        RunKind.InBehind => role switch
        {
            TacticalRole.Striker => 1.00f,
            TacticalRole.Winger => 0.85f,
            TacticalRole.CentralMidfielder => 0.55f,
            TacticalRole.DefensiveMidfielder => 0.30f,
            _ => 0.15f
        },
        RunKind.Overlap => role switch
        {
            TacticalRole.FullBack => 1.00f,
            TacticalRole.Winger => 0.70f,
            TacticalRole.CentralMidfielder => 0.30f,
            _ => 0.15f
        },
        RunKind.Underlap => role switch
        {
            TacticalRole.FullBack => 0.85f,
            TacticalRole.CentralMidfielder => 0.85f,
            TacticalRole.Winger => 0.80f,
            TacticalRole.DefensiveMidfielder => 0.50f,
            _ => 0.25f
        },
        RunKind.RecycleSupport => role switch
        {
            TacticalRole.DefensiveMidfielder => 1.00f,
            TacticalRole.CenterBack => 0.95f,
            TacticalRole.CentralMidfielder => 0.90f,
            TacticalRole.FullBack => 0.70f,
            _ => 0.30f
        },
        RunKind.LateBoxArrival => role switch
        {
            TacticalRole.CentralMidfielder => 1.00f,
            TacticalRole.Winger => 0.95f,
            TacticalRole.Striker => 0.80f,
            TacticalRole.DefensiveMidfielder => 0.45f,
            _ => 0.15f
        },
        RunKind.BlindSideArrival => role switch
        {
            TacticalRole.Winger => 1.00f,
            TacticalRole.Striker => 0.90f,
            TacticalRole.CentralMidfielder => 0.70f,
            _ => 0.20f
        },
        RunKind.Pin => role switch
        {
            TacticalRole.Striker => 1.00f,
            TacticalRole.Winger => 0.70f,
            TacticalRole.CentralMidfielder => 0.60f,
            _ => 0.20f
        },
        _ => 0.10f
    };

    /// <summary>
    /// What the run costs the rest defence if the ball is lost now, in [0,1]. Uses A3.6's aggregate when
    /// the team has one; a team with no picture yet is priced at neutral rather than at "fully exposed",
    /// because an unknown risk is not evidence of a bad one.
    /// </summary>
    public static float TransitionExposure(in RestDefence security)
    {
        float value = security.Security01;
        if (!float.IsFinite(value) || value <= 0f) return NeutralExposure;
        return Clamp01(1f - value);
    }

    private static float Clamp01(float value)
    {
        if (!float.IsFinite(value)) return 0f;
        return value < 0f ? 0f : value > 1f ? 1f : value;
    }
}
