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
    float ArrivalFit01,
    float TravelMeters,
    float Score)
{
    public static RunScoreTerms None => new(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, float.NegativeInfinity);

    /// <summary>
    /// A5.6: the room this path is expected to make for a team-mate by taking a defender off him. Only a
    /// <see cref="RunKind.Decoy"/> prices this; every other kind scores zero, because a real run's value is
    /// the space it reaches rather than the space it vacates.
    /// </summary>
    public float DecoySpace01 { get; init; }

    /// <summary>The defender the decoy expects to pull (0 when none). Diagnostics only.</summary>
    public int DecoyDefenderId { get; init; }

    /// <summary>True when this score came from the decoy composition rather than the six receiving terms.</summary>
    public bool IsDecoy => DecoyDefenderId > 0 || DecoySpace01 > 0f;

    /// <summary>Compact text form for a trace line or a failure message.</summary>
    public string Describe() =>
        IsDecoy
            ? $"decoy pulls #{DecoyDefenderId} frees {DecoySpace01:F2} · exposed {TransitionExposure01:F2} · " +
              $"travel {TravelMeters:F0} m ⇒ {Score:F2}"
            : $"arrive {ArrivalAdvantage01:F2} · pass {PassAvailability01:F2} · orient {ReceivingOrientation01:F2} · " +
              $"break {LineBreaks01:F2} · room {Congestion01:F2} · role {RoleUsefulness01:F2} · " +
              $"fit {ArrivalFit01:F2} · exposed {TransitionExposure01:F2} · travel {TravelMeters:F0} m ⇒ {Score:F2}";
}

/// <summary>
/// The box arrivals the team has already announced (A5.5), as the runner can hear them on the A5.3 board:
/// up to three regions, each with the tick the team-mate plans to be there. Bounded and allocation-free
/// because it is built once per decision and passed to every path's score.
///
/// This is what makes arrivals <b>complementary</b> rather than independent: two runners attacking the same
/// post at the same moment is a congestion problem the team creates for itself, and the one who goes later
/// or wider is worth more. It is deliberately a *team statement*, not a second reading of the world: a
/// region appears here only because its runner announced it, through the same delayed board the carrier uses.
/// </summary>
public readonly record struct TeamBoxArrivals(
    Vector2 FirstRegion,
    int FirstMeetTick,
    Vector2 SecondRegion,
    int SecondMeetTick,
    Vector2 ThirdRegion,
    int ThirdMeetTick,
    int Count)
{
    /// <summary>No team-mate has announced a box arrival: every arrival is fully complementary.</summary>
    public static TeamBoxArrivals None => default;

    /// <summary>How distinct one arrival is from the ones already announced, 1 = free, 0 = a duplicate.</summary>
    public float Spread01(Vector2 regionXZ, int meetTick)
    {
        float worst = 0f;
        for (int i = 0; i < Count; i++)
        {
            Vector2 other;
            int otherTick;
            switch (i)
            {
                case 0: other = FirstRegion; otherTick = FirstMeetTick; break;
                case 1: other = SecondRegion; otherTick = SecondMeetTick; break;
                default: other = ThirdRegion; otherTick = ThirdMeetTick; break;
            }

            float distance = Vector2.Distance(regionXZ, other);
            float time = MathF.Abs(meetTick - otherTick);

            // Both conditions must hold for two arrivals to crowd each other: the same space at a different
            // time is a decoy or a late run, and the same time in a different space is two posts.
            float spaceOverlap = 1f - Math.Clamp(distance / RunScoring.BoxArrivalRadiusMeters, 0f, 1f);
            float timeOverlap = 1f - Math.Clamp(time / RunScoring.BoxArrivalWindowTicks, 0f, 1f);
            float overlap = spaceOverlap * timeOverlap;
            if (overlap > worst) worst = overlap;
        }

        return Clamp01(1f - worst);
    }

    /// <summary>True when this many arrivals are already announced (diagnostics and tests).</summary>
    public bool IsEmpty => Count <= 0;

    private static float Clamp01(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
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
/// <item><b>arrival fit</b> (A5.5) — for a <i>box arrival</i>, how well it fits the ones the team has
///   already announced: two runners into the same post at the same moment create the congestion they are
///   trying to exploit, while a later or wider run complements it. It also carries the player's own
///   finishing/aerial capability, because the arrival is only worth what the man can do with the ball.
///   Deliberately signed around zero (±<see cref="ArrivalFitWeight"/>): other kinds have no arrival to
///   complement and contribute nothing, so adding the term cannot shift them.</item>
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
    // (0.90–1.00), and a run never outweighs the whole on-ball action scale on its own. A5.5's arrival fit
    // is a *signed* term centred on zero, so it can only reorder box arrivals against each other.
    // ---------------------------------------------------------------------------------------------

    public const float ArrivalWeight = 0.15f;
    public const float PassWeight = 0.13f;
    public const float OrientationWeight = 0.08f;
    public const float LineBreakWeight = 0.05f;
    public const float CongestionWeight = 0.09f;
    public const float RoleWeight = 0.10f;
    public const float ExposureWeight = 0.12f;
    public const float TravelWeight = 0.35f;

    /// <summary>
    /// Weight of the room a decoy is expected to make for a team-mate (A5.6). Sized so a decoy that pulls a
    /// marker off a team-mate and stays near the play is worth roughly what the weaker receiving terms pay —
    /// it has to be able to win a duty's own comparison, and it must not outbid a genuine run into space.
    /// </summary>
    public const float DecoyWeight = 0.45f;

    /// <summary>
    /// Weight of how far a decoy takes itself from the ball (A5.6). This replaces the travel cost a receiving
    /// run pays: a decoy does not have to win a race, so the distance is not a timing cost — what it costs is
    /// being somewhere the play is not, which is exactly the distance from the carrier.
    /// </summary>
    public const float DecoyDistanceWeight = 0.20f;

    /// <summary>Weight of the box-arrival fit term (A5.5); it is applied as ±this around a neutral 0.5.</summary>
    public const float ArrivalFitWeight = 0.10f;

    /// <summary>Distance within which two box arrivals are competing for the same space, in metres.</summary>
    public const float BoxArrivalRadiusMeters = 8f;

    /// <summary>Time window within which two box arrivals are competing for the same moment, in ticks.</summary>
    public const int BoxArrivalWindowTicks = 30;

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
        in RestDefence security) =>
        Score(path, obs, carrierXZ, security, TeamBoxArrivals.None);

    /// <summary>
    /// One path's score with the team's already-announced box arrivals (A5.5).
    /// </summary>
    public static RunScoreTerms Score(
        in RunPath path,
        in FootballerObservation obs,
        Vector2 carrierXZ,
        in RestDefence security,
        in TeamBoxArrivals teamArrivals)
    {
        Vector2 meetXZ = path.MeetXZ;
        if (!SimMath.IsFinite(meetXZ) || !SimMath.IsFinite(carrierXZ)) return RunScoreTerms.None;

        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);

        // A5.6: a decoy is not a bet on receiving the ball, and pricing it on "can I be there first" would
        // answer the wrong question — a good decoy is often the run nobody wants to make. It is sold instead
        // on the defender response it provokes, and it keeps only the two costs every run pays: the exposure
        // it adds and the ground it covers.
        if (path.Kind == RunKind.Decoy) return ScoreDecoy(path, obs, security, selfXZ, carrierXZ);

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

        // A5.5: only a box arrival has an arrival to complement, so only a box arrival pays or gains here.
        float fit = IsBoxArrival(path.Kind) ? ArrivalFit01(path, obs, teamArrivals) : 0f;
        float fitContribution = fit <= 0f
            ? 0f
            : ArrivalFitWeight * (fit - NeutralArrivalFit);

        float score = ArrivalWeight * arrival
                    + PassWeight * pass
                    + OrientationWeight * orientation
                    + LineBreakWeight * lineBreaks
                    + CongestionWeight * congestion
                    + RoleWeight * role
                    + fitContribution
                    - ExposureWeight * exposure
                    - TravelWeight * MathF.Min(travel, 40f) / 40f;

        return new RunScoreTerms(arrival, pass, orientation, lineBreaks, congestion, role, exposure,
            fit, float.IsFinite(travel) ? travel : 0f, score);
    }

    /// <summary>
    /// A5.6: what a decoy is worth, on its own composition.
    ///
    /// The six receiving terms are all "can I get the ball here", and a decoy's region is chosen to be
    /// attractive to a <i>defender</i>, so scoring it with them would systematically reject the best decoys.
    /// Its value is the room it makes for somebody else — <see cref="DefenderResponseModel"/> — minus the two
    /// costs it really pays: the transition exposure of another player committed upfield, and the ground he
    /// covers to be somewhere he does not want the ball.
    /// </summary>
    private static RunScoreTerms ScoreDecoy(
        in RunPath path,
        in FootballerObservation obs,
        in RestDefence security,
        Vector2 selfXZ,
        Vector2 carrierXZ)
    {
        DefenderResponse response = DefenderResponseModel.Evaluate(obs, path.MeetXZ);
        float exposure = TransitionExposure(security);
        float travel = Vector2.Distance(selfXZ, path.MeetXZ);
        if (!float.IsFinite(travel)) travel = 0f;

        float awayFromPlay = Vector2.Distance(carrierXZ, path.MeetXZ);
        if (!float.IsFinite(awayFromPlay)) awayFromPlay = 0f;

        float score = DecoyWeight * response.Value01
                      - ExposureWeight * exposure
                      - DecoyDistanceWeight * MathF.Min(awayFromPlay, 40f) / 40f;

        return new RunScoreTerms(
            0f, 0f, 0f, 0f, 0f, 0f, exposure, 0f, travel, score)
        {
            DecoySpace01 = response.FreedSpace01,
            DecoyDefenderId = response.DefenderId
        };
    }

    /// <summary>
    /// A5.5: how well a box arrival fits the team's other announced arrivals and the player's own
    /// finishing/aerial capability. 0.5 is neutral — no announced arrival to compete with and an average
    /// body — so the term can only reorder box arrivals, never shift the rest of the run set.
    /// </summary>
    public static float ArrivalFit01(
        in RunPath path,
        in FootballerObservation obs,
        in TeamBoxArrivals teamArrivals)
    {
        float spread = teamArrivals.Spread01(path.MeetXZ, path.MeetTick);
        float capability = BoxArrivalCapability(obs.SelfAttributes);
        return Clamp01(0.5f * spread + 0.5f * capability);
    }

    /// <summary>
    /// The capability a box arrival is worth, from the attributes that actually decide one: the composure
    /// to finish when the chance comes and the strength to win the ball in the air. There is no separate
    /// aerial attribute — A7 differentiates players more broadly — so this is the stated blend, and it is
    /// only as good as the attributes a squad or fixture supplies.
    /// </summary>
    public static float BoxArrivalCapability(in PlayerAttributes attributes) =>
        Clamp01(0.55f * attributes.Composure01 + 0.45f * attributes.Strength01);

    /// <summary>Kinds whose whole point is arriving in the box for a delivery (A5.1).</summary>
    public static bool IsBoxArrival(RunKind kind) =>
        kind is RunKind.LateBoxArrival or RunKind.BlindSideArrival;

    /// <summary>The mix value that changes nothing: no announced arrival, an average body.</summary>
    public const float NeutralArrivalFit = 0.5f;

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
