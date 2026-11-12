using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;

namespace FootballSim.Tactics;

/// <summary>
/// A job target as a region rather than a point (A3.4): the centre an occupant works from and the
/// radius he is expected to cover. A region is what lets a target be scored relative to the ball, his
/// teammates, the opponents the team believes in and the space around it — a bare point can only be
/// scored by distance.
/// </summary>
public readonly record struct JobRegion(Vector3 Centre, float Radius)
{
    public Vector2 CentreXZ => new(Centre.X, Centre.Z);
}

/// <summary>A teammate's already-assigned target, for the staggering and complementary-channel terms.</summary>
public readonly record struct AssignedRegion(int PlayerId, JobRegion Region);

/// <summary>
/// A3.4: scores one candidate job region against ball, teammates, opponents and space.
///
/// The terms are deliberately soft and relative. Nothing here assigns a lane by index: a region is
/// rewarded for having room, for being findable from the carrier, and for being laterally and
/// longitudinally staggered against the teammates who already have a region. With three attackers that
/// produces three useful channels; with eleven it produces a shape, not an eleven-lane grid.
///
/// The knowledge boundary holds: opponents enter only through <see cref="TeamBelief"/> (what the team
/// has actually seen), exactly as the marker and offside-line code already works.
/// </summary>
public static class JobRegionScoring
{
    /// <summary>Distance at which a region is considered to have as much room as it needs, in metres.</summary>
    private const float SpaceSaturationMeters = 10f;

    /// <summary>Opponent distances are capped here so an empty belief does not read as infinite room.</summary>
    private const float SpaceCapMeters = 22f;

    /// <summary>How close a teammate has to be for a region to be crowded, in metres.</summary>
    private const float CrowdingRadiusMeters = 12f;

    /// <summary>Lateral separation at which two teammates count as holding different channels, in metres.</summary>
    private const float ChannelSeparationMeters = 10f;

    /// <summary>Longitudinal band inside which two targets are compared laterally, in metres.</summary>
    private const float ChannelBandMeters = 16f;

    /// <summary>Corridor width that a pass lane is judged by, in metres.</summary>
    private const float LaneCorridorMeters = 4f;

    /// <summary>Terms. They sum to ≈1.75 when every term is perfect, which keeps the region smaller than
    /// the role-fit and travel terms it is combined with in the allocator. Connection is weighted at
    /// least as heavily as raw space on purpose: a target nobody can reach is not a target, and
    /// space-dominant weights measurably flattened the tactical gate once kept assignments were visible
    /// to the scorer (see PROGRESS.md).</summary>
    private const float SpaceWeight = 0.35f;
    private const float ConnectionWeight = 0.35f;
    private const float LaneWeight = 0.25f;
    private const float WidthWeight = 0.25f;
    private const float DepthWeight = 0.15f;
    private const float StaggerWeight = 0.30f;
    private const float ChannelWeight = 0.15f;

    /// <summary>
    /// The radius a duty is expected to work inside. A press or a collection is a point-sized job; a
    /// width holder or a depth runner owns a bigger piece of ground.
    /// </summary>
    public static float RadiusOf(TacticalJob job) => job switch
    {
        TacticalJob.Presser => 2.5f,
        TacticalJob.BallResponder => 2.5f,
        TacticalJob.Marker => 3f,
        TacticalJob.CoverDefender => 4f,
        TacticalJob.ProtectGap => 4f,
        TacticalJob.NearOutlet => 4f,
        TacticalJob.DefensiveCover => 5f,
        TacticalJob.DefensiveLine => 5f,
        TacticalJob.ThirdPlayerOption => 5f,
        TacticalJob.Overlap => 5f,
        TacticalJob.FarSideWidth => 6f,
        TacticalJob.DepthRun => 6f,
        _ => 5f
    };

    /// <summary>Duties that want to be wide: the region is rewarded for hugging its touchline.</summary>
    public static bool WantsWidth(TacticalJob job) => job is TacticalJob.FarSideWidth or TacticalJob.Overlap;

    /// <summary>Duties that want to be ahead of the ball: the region is rewarded for progress.</summary>
    public static bool WantsDepth(TacticalJob job) =>
        job is TacticalJob.DepthRun or TacticalJob.Overlap or TacticalJob.ThirdPlayerOption;

    /// <summary>Duties whose whole point is being findable by a teammate with the ball.</summary>
    public static bool IsReceiving(TacticalJob job) => job is TacticalJob.NearOutlet
        or TacticalJob.ThirdPlayerOption or TacticalJob.FarSideWidth or TacticalJob.DepthRun
        or TacticalJob.Overlap or TacticalJob.BallResponder;

    /// <summary>
    /// Minimum centre-to-centre distance a refined target may leave to a teammate's already-assigned
    /// target. The refinement is a local search, and a search with only a soft staggering term can pay
    /// for connection or lane openness by sliding one duty onto another — measured during A4 as a
    /// 3.7 m pair (an outlet refined onto the cover behind the ball). The soft term stays what it is;
    /// this is the floor the probe may not cross when the nominal target was above it.
    /// </summary>
    public const float MinRefinedSeparationMeters = 6f;

    /// <summary>
    /// Distance from a candidate centre to the nearest teammate's assigned target, or
    /// <see cref="float.MaxValue"/> when the candidate has no company. Allocation-free so the
    /// refinement can call it per probe.
    /// </summary>
    public static float NearestTeammateDistance(
        Vector2 centre,
        IReadOnlyList<AssignedRegion> teammates,
        int candidatePlayerId)
    {
        float nearest = float.MaxValue;
        for (int i = 0; i < teammates.Count; i++)
        {
            AssignedRegion mate = teammates[i];
            if (mate.PlayerId == candidatePlayerId) continue;
            float distance = Vector2.Distance(centre, mate.Region.CentreXZ);
            if (distance < nearest) nearest = distance;
        }
        return nearest;
    }

    /// <summary>
    /// One region's score. Higher is better; the value is dimensionless and only meaningful against
    /// another region scored with the same inputs. <paramref name="attackingWidth01"/> is the team
    /// profile's width: a narrow side wants narrow channels, so both the width reward and the lateral
    /// separation it is looking for scale with it.
    /// </summary>
    public static float Score(
        in JobRegion region,
        TacticalJob job,
        Vector2 referenceXZ,
        IReadOnlyList<AssignedRegion> teammates,
        int candidatePlayerId,
        TeamBelief opponents,
        float attackDirX,
        PitchDimensions pitch,
        float attackingWidth01)
    {
        Vector2 centre = region.CentreXZ;

        // Room: how far the nearest believed opponent is from the edge of the region.
        float nearestOpponent = opponents.NearestDistance(centre, SpaceCapMeters);
        float space = Clamp01((nearestOpponent - region.Radius) / SpaceSaturationMeters);

        float score = SpaceWeight * space;

        // Connection: the region must stay a plausible target for the duty — not on top of the carrier,
        // not unreachably far from him.
        float distanceToReference = Vector2.Distance(centre, referenceXZ);
        float tooClose = Clamp01((4f - distanceToReference) / 4f);
        float tooFar = Clamp01((distanceToReference - 32f) / 8f);
        float connection = 1f - 0.5f * tooClose - tooFar;
        score += ConnectionWeight * connection;

        // Connection in the other direction: can a pass from the carrier even reach it?
        if (IsReceiving(job) && opponents.Count > 0)
            score += LaneWeight * LaneOpenness(referenceXZ, centre, opponents);

        // Width how this profile means it: the same expression the width jobs use for their nominal,
        // so a direct side is not rewarded for hugging the touchline it never asked for.
        float widthScale = 0.55f + 0.55f * Clamp01(attackingWidth01);
        if (WantsWidth(job))
            score += WidthWeight * Clamp01(MathF.Abs(centre.Y) / MathF.Max(1f, (pitch.HalfWidth - 3f) * widthScale));
        if (WantsDepth(job))
            score += DepthWeight * Clamp01((centre.X * attackDirX) / MathF.Max(1f, pitch.HalfLength));

        // Staggering: do not stack on a teammate who already has a region. The kernel is linear in the
        // centre-to-centre distance and saturates at CrowdingRadiusMeters, deliberately with *no* dead
        // zone at the region radius: two duties handed targets a few metres apart have to score
        // measurably worse than the same pair ten metres apart, or the refinement has no gradient to
        // separate them with.
        float nearestTeammate = float.MaxValue;
        float nearestLateral = float.MaxValue;
        bool lateralComparable = false;
        for (int i = 0; i < teammates.Count; i++)
        {
            AssignedRegion mate = teammates[i];
            if (mate.PlayerId == candidatePlayerId) continue;

            Vector2 mateCentre = mate.Region.CentreXZ;
            float distance = Vector2.Distance(centre, mateCentre);
            if (distance < nearestTeammate) nearestTeammate = distance;

            if (MathF.Abs(mateCentre.X - centre.X) <= ChannelBandMeters)
            {
                float lateral = MathF.Abs(mateCentre.Y - centre.Y);
                if (lateral < nearestLateral) nearestLateral = lateral;
                lateralComparable = true;
            }
        }

        float staggering = nearestTeammate == float.MaxValue
            ? 1f
            : Clamp01(nearestTeammate / CrowdingRadiusMeters);
        score += StaggerWeight * staggering;

        // Complementary channels: being in the same lateral band as a teammate is worth less than
        // holding a different one, and what counts as "a different one" follows the profile's width.
        // With nobody nearby the constraint simply does not apply — that is the "no compulsory
        // five-lane occupancy" part.
        score += ChannelWeight * (lateralComparable
            ? Clamp01(nearestLateral / (ChannelSeparationMeters * widthScale))
            : 1f);

        return score;
    }

    /// <summary>
    /// How open the straight line from <paramref name="fromXZ"/> to <paramref name="toXZ"/> is against
    /// what the team believes: 1 when nothing is within the corridor, 0 when an opponent is on it.
    /// </summary>
    public static float LaneOpenness(Vector2 fromXZ, Vector2 toXZ, TeamBelief opponents)
    {
        Vector2 segment = toXZ - fromXZ;
        float lengthSquared = segment.LengthSquared();
        float openness = 1f;

        IReadOnlyList<TeamBelief.Opponent> believed = opponents.Opponents;
        for (int i = 0; i < believed.Count; i++)
        {
            Vector2 point = believed[i].PositionXZ;
            float t = lengthSquared > 1e-6f
                ? Clamp01(Vector2.Dot(point - fromXZ, segment) / lengthSquared)
                : 0f;
            float distance = Vector2.Distance(point, fromXZ + segment * t);
            float clearance = Clamp01(distance / LaneCorridorMeters);
            if (clearance < openness) openness = clearance;
        }

        return openness;
    }

    private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;
}
