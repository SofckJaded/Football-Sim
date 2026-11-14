using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Tactics;

namespace FootballSim.AI;

/// <summary>
/// What one defender is expected to do about a run, and what his answer is worth to the attacking team
/// (A5.6).
///
/// A decoy is the one off-ball path whose value is not the space it reaches: it is the space it
/// <b>takes a defender away from</b>. Pinning that down needs the defender's own choice — follow the runner,
/// or hold the ground he is responsible for — and the change a follow makes to a team-mate's opportunity.
/// This type is that answer: <see cref="Follow01"/> is how likely the defender is to commit to the run,
/// <see cref="FreedSpace01"/> is how much room the team gains at a team-mate if he does, and
/// <see cref="Value01"/> is the product the run is priced on.
/// </summary>
public readonly record struct DefenderResponse(
    int DefenderId,
    float Follow01,
    float FreedSpace01,
    float Value01)
{
    /// <summary>No defender is close enough to answer the run: the decoy buys nothing.</summary>
    public static DefenderResponse None => default;

    /// <summary>True when there is a defender who would follow and room for it to matter.</summary>
    public bool IsPresent => DefenderId > 0 && Follow01 > 0f && FreedSpace01 > 0f;

    /// <summary>Compact text form for a trace line or a failure message.</summary>
    public string Describe() =>
        IsPresent
            ? $"#{DefenderId} follows {Follow01:F2} · frees {FreedSpace01:F2} ⇒ {Value01:F2}"
            : "nobody follows (no value)";
}

/// <summary>
/// A5.6: the bounded <b>follow-versus-hold</b> defender response, estimated from what the runner can see.
///
/// The model is deliberately small, because the alternative — predicting a defender's decision from his
/// controller — would be a second world model reading his live state. It answers two questions:
///
/// <list type="bullet">
/// <item><b>Will he follow?</b> A defender commits to a run when he is <i>responsible</i> for the space it
///   attacks (the closer he is, the less he can ignore it) and when that space is <i>dangerous</i> (closer
///   to his own goal is worse). He holds when he is his side's deepest defender, because stepping out of
///   the last line is a bigger decision than tracking a runner — that is the same asymmetry A3.5's coverage
///   request handles, and it is why <see cref="LastLineHoldScale"/> discounts him rather than exempting
///   him.</item>
/// <item><b>What does following free?</b> For every team-mate, the room between him and his nearest believed
///   opponent, measured twice: with this defender in the world, and with him removed. The difference is the
///   room the decoy makes; the best mate's gain is the decoy's value. A defender who was marking nobody frees
///   nobody, which is what stops "run away from the play" from being worth anything by itself.</item>
/// </list>
///
/// <b>The knowledge boundary holds.</b> Every input is the runner's own observation — an opponent seen (or
/// remembered) through <see cref="FootballerObservation.VisiblePlayers"/> and team-mates the same way. No
/// opponent's intent, velocity beyond his belief, or hidden attribute enters the estimate, and the answer is
/// an estimate about belief, not a read of what the defender's controller will actually do.
/// </summary>
public static class DefenderResponseModel
{
    /// <summary>
    /// How far from the run a defender must be to be able to ignore it, in metres. Inside this radius his
    /// readiness to answer falls off linearly; beyond it the model predicts he does not answer the run at all.
    /// </summary>
    public const float FollowRadiusMeters = 12f;

    /// <summary>
    /// Attack-relative projection at which a region is as dangerous as it gets, in metres (22.5 m from the
    /// opponent's goal line). A run toward the halfway line pulls nobody.
    /// </summary>
    public const float DangerProjXMeters = 30f;

    /// <summary>
    /// How much of the follow is "he can be there" against "the space is worth going to": the two multiply,
    /// so a run into a harmless space near a defender and a dangerous space nobody can reach both score low.
    /// </summary>
    public const float DangerWeight = 0.50f;

    /// <summary>
    /// How much a side's deepest defender discounts a follow, as a multiplier. He is still the nearest man to
    /// the space, so he is not exempt; committing the last line is simply the decision a defender makes last,
    /// which is exactly the behaviour a pin exploits.
    /// </summary>
    public const float LastLineHoldScale = 0.55f;

    /// <summary>Room gained at a team-mate that makes him fully free, in metres.</summary>
    public const float FreedSpaceScaleMeters = 9f;

    /// <summary>
    /// Least room a follow must make for a team-mate to count at all, in metres. Below this the defender was
    /// already leaving that man alone, and the decoy is not what freed him.
    /// </summary>
    public const float MinFreedSpaceMeters = 1.5f;

    /// <summary>
    /// Estimates the response to a run arriving in <paramref name="regionXZ"/>. Returns
    /// <see cref="DefenderResponse.None"/> when no believed opponent is close enough to the region, or when
    /// following frees nobody.
    /// </summary>
    public static DefenderResponse Evaluate(in FootballerObservation obs, Vector2 regionXZ)
    {
        if (!SimMath.IsFinite(regionXZ)) return DefenderResponse.None;

        IReadOnlyList<PerceivedEntity> visible = obs.VisiblePlayers;
        if (visible.Count == 0) return DefenderResponse.None;

        float attackDir = obs.AttackingDirectionX;
        float regionProj = regionXZ.X * attackDir;
        float danger = Clamp01(regionProj / DangerProjXMeters);
        float dangerWeight = (1f - DangerWeight) + DangerWeight * danger;

        // The deepest outfield defender: the one nearest his own goal, i.e. the largest attack-relative
        // projection. The keeper is not a line defender and is never the man a decoy pulls.
        int lastLineId = 0;
        float lastLineProj = float.NegativeInfinity;
        for (int i = 0; i < visible.Count; i++)
        {
            PerceivedEntity entity = visible[i];
            if (entity.Team == obs.SelfTeam || entity.Role == TacticalRole.Goalkeeper) continue;

            float proj = entity.EstimatedPosition.X * attackDir;
            if (proj <= lastLineProj) continue;

            lastLineProj = proj;
            lastLineId = entity.PlayerId;
        }

        var best = DefenderResponse.None;
        for (int i = 0; i < visible.Count; i++)
        {
            PerceivedEntity opponent = visible[i];
            if (opponent.Team == obs.SelfTeam || opponent.Role == TacticalRole.Goalkeeper) continue;

            Vector2 opponentXZ = new(opponent.EstimatedPosition.X, opponent.EstimatedPosition.Z);
            if (!SimMath.IsFinite(opponentXZ)) continue;

            float distance = Vector2.Distance(opponentXZ, regionXZ);
            if (distance >= FollowRadiusMeters) continue;

            float readiness = 1f - distance / FollowRadiusMeters;
            float follow = readiness * dangerWeight;
            if (opponent.PlayerId == lastLineId) follow *= LastLineHoldScale;
            follow = Clamp01(follow);
            if (follow <= 0f) continue;

            float freed = FreedSpace01(obs, opponent.PlayerId);
            float value = follow * freed;
            if (value <= best.Value01) continue;

            best = new DefenderResponse(opponent.PlayerId, follow, freed, value);
        }

        return best;
    }

    /// <summary>
    /// The room this defender's absence would create for the best-placed team-mate: the gap between that
    /// team-mate and his nearest believed opponent grows when the defender leaves, and only the growth counts.
    /// Zero when he is not marking anybody.
    /// </summary>
    private static float FreedSpace01(in FootballerObservation obs, int defenderId)
    {
        float best = 0f;
        IReadOnlyList<PerceivedEntity> visible = obs.VisiblePlayers;

        for (int i = 0; i < visible.Count; i++)
        {
            PerceivedEntity mate = visible[i];
            if (mate.Team != obs.SelfTeam || mate.PlayerId == obs.SelfId) continue;
            if (mate.Role == TacticalRole.Goalkeeper) continue;

            // Room is only an opportunity where the ball could be an attacking one: a centre-back with more
            // space gains the team nothing, and neither does a team-mate in his own half.
            if (mate.Role == TacticalRole.CenterBack) continue;
            if (mate.EstimatedPosition.X * obs.AttackingDirectionX < 0f) continue;

            Vector2 mateXZ = new(mate.EstimatedPosition.X, mate.EstimatedPosition.Z);
            if (!SimMath.IsFinite(mateXZ)) continue;

            float roomNow = NearestOpponentDistance(obs, mateXZ, 0);
            if (!float.IsFinite(roomNow)) continue;

            // A team-mate who already has a free man's room cannot be freed any further: the decoy is not
            // what makes him available, and counting him would make any run look valuable.
            if (roomNow >= FreedSpaceScaleMeters) continue;

            float roomFree = NearestOpponentDistance(obs, mateXZ, defenderId);

            // Nobody left to cover him is the most the room can be, not the least: a team-mate with no other
            // opponent around is the one the decoy frees most.
            float gained = float.IsFinite(roomFree) ? roomFree - roomNow : FreedSpaceScaleMeters;
            if (gained < MinFreedSpaceMeters) continue;

            float freed = Clamp01(gained / FreedSpaceScaleMeters);
            if (freed > best) best = freed;
        }

        return best;
    }

    /// <summary>Distance from a point to the nearest believed opponent, ignoring one player id.</summary>
    private static float NearestOpponentDistance(in FootballerObservation obs, Vector2 pointXZ, int ignoredId)
    {
        float nearest = float.PositiveInfinity;
        IReadOnlyList<PerceivedEntity> visible = obs.VisiblePlayers;

        for (int i = 0; i < visible.Count; i++)
        {
            PerceivedEntity opponent = visible[i];
            if (opponent.Team == obs.SelfTeam) continue;
            if (opponent.PlayerId == ignoredId) continue;

            Vector2 opponentXZ = new(opponent.EstimatedPosition.X, opponent.EstimatedPosition.Z);
            if (!SimMath.IsFinite(opponentXZ)) continue;

            float distance = Vector2.Distance(opponentXZ, pointXZ);
            if (distance < nearest) nearest = distance;
        }

        return nearest;
    }

    private static float Clamp01(float value) =>
        !float.IsFinite(value) ? 0f : value < 0f ? 0f : value > 1f ? 1f : value;
}
