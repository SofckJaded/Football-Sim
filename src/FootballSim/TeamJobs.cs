using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;

namespace FootballSim.Tactics;

/// <summary>
/// A persistent off-ball job (audit F04). Before this, every non-responder generated exactly one
/// "support/hold" command from a formation anchor, so ~90 % of outfield AI decisions were the same
/// action with no situational alternative.
/// </summary>
public enum TacticalJob
{
    /// <summary>No special assignment: hold the formation anchor.</summary>
    Shape,
    /// <summary>Possession: the planned receiver of the pass in flight / nearest collector.</summary>
    BallResponder,
    /// <summary>Possession: short, safe outlet behind the ball.</summary>
    NearOutlet,
    /// <summary>Possession: diagonal third-player option on the opposite side.</summary>
    ThirdPlayerOption,
    /// <summary>Possession: hold the far-side touchline width.</summary>
    FarSideWidth,
    /// <summary>Possession: contextual run in behind, kept onside.</summary>
    DepthRun,
    /// <summary>Possession: full-back/winger overlap beyond a wide carrier.</summary>
    Overlap,
    /// <summary>Possession: cover behind the ball in case possession turns over.</summary>
    DefensiveCover,
    /// <summary>Out of possession: press the ball carrier / loose ball.</summary>
    Presser,
    /// <summary>Out of possession: cover behind the presser.</summary>
    CoverDefender,
    /// <summary>Out of possession: mark a dangerous attacker goal-side.</summary>
    Marker,
    /// <summary>Out of possession: hold the defensive line.</summary>
    DefensiveLine
}

/// <summary>An assigned job with the target point the player should work toward.</summary>
public readonly record struct TeamJobAssignment(
    TacticalJob Job,
    Vector3 Anchor,
    int? TargetPlayerId,
    string Reason);

/// <summary>
/// Assigns persistent, team-level off-ball jobs relative to the actual ball/carrier and keeps them
/// for a bounded period (audit F04). Jobs are recomputed every few ticks; a possession change
/// interrupts immediately. Formation anchors stay the constraint/fallback.
/// </summary>
public sealed class TeamJobCoordinator
{
    private const int RecomputeIntervalTicks = 6;
    private const int MinimumHoldTicks = 42;

    private readonly PitchDimensions _pitch;
    private readonly Dictionary<int, TeamJobAssignment> _assignments = new();
    private readonly Dictionary<int, int> _assignedAtTick = new();
    private TeamId? _lastPossessionTeam;
    private int _lastRecomputeTick = -RecomputeIntervalTicks;

    public TeamJobCoordinator(PitchDimensions pitch)
    {
        _pitch = pitch;
    }

    public IReadOnlyDictionary<int, TeamJobAssignment> Assignments => _assignments;

    public TeamJobAssignment? Get(int playerId) =>
        _assignments.TryGetValue(playerId, out TeamJobAssignment assignment) ? assignment : null;

    /// <summary>
    /// True when this tick's call would actually recompute the assignments. The engine uses it to rebuild
    /// the teams' belief views on the cadence they are read (A2.6) instead of every tick.
    /// </summary>
    public bool WillRecompute(TeamId? possessingTeam, int tick) =>
        possessingTeam != _lastPossessionTeam || tick - _lastRecomputeTick >= RecomputeIntervalTicks;

    public void Update(
        IReadOnlyList<FootballerState> players,
        BallState ball,
        BallPossessionSnapshot possession,
        TeamId? possessingTeam,
        int? homeResponderId,
        int? awayResponderId,
        float homeDirX,
        int tick,
        TeamTacticalProfile homeProfile,
        TeamTacticalProfile awayProfile,
        TeamBelief homeOpponents,
        TeamBelief awayOpponents)
    {
        bool urgent = possessingTeam != _lastPossessionTeam;
        if (!urgent && tick - _lastRecomputeTick < RecomputeIntervalTicks)
            return;

        _lastRecomputeTick = tick;
        _lastPossessionTeam = possessingTeam;

        if (urgent)
        {
            _assignments.Clear();
            _assignedAtTick.Clear();
        }

        AssignTeam(TeamId.Home, players, ball, possession, possessingTeam, homeResponderId, homeDirX, tick, homeProfile, homeOpponents);
        AssignTeam(TeamId.Away, players, ball, possession, possessingTeam, awayResponderId, -homeDirX, tick, awayProfile, awayOpponents);
    }

    private void AssignTeam(
        TeamId team,
        IReadOnlyList<FootballerState> players,
        BallState ball,
        BallPossessionSnapshot possession,
        TeamId? possessingTeam,
        int? responderId,
        float attackDirX,
        int tick,
        TeamTacticalProfile profile,
        TeamBelief opponents)
    {
        Vector2 ballXZ = new(ball.Position.X, ball.Position.Z);
        // Width jobs must respect the team profile: a possession side stretches the pitch, a direct
        // side stays compact (the old constant touchline target erased the profile difference).
        float wideZ = (_pitch.HalfWidth - 3f) * (0.55f + 0.55f * profile.AttackingWidth01);
        bool inPossession = possessingTeam == team;

        FootballerState? carrier = null;
        if (possession.IsControlledBy(team) && possession.ControllingPlayerId is int carrierId)
            carrier = Find(players, carrierId);
        else if (possession.State == BallControlState.InFlightPass &&
                 possession.PossessionTeam == team &&
                 possession.IntendedReceiverId is int receiverId)
            carrier = Find(players, receiverId);

        Vector2 referenceXZ = carrier is null
            ? ballXZ
            : new Vector2(carrier.Position.X, carrier.Position.Z);

        // Drop assignments for players who left the pitch.
        List<int>? stale = null;
        foreach (int id in _assignments.Keys)
        {
            FootballerState? player = Find(players, id);
            if (player is null || !player.IsActiveOnPitch)
                (stale ??= new List<int>()).Add(id);
        }
        if (stale is not null)
        {
            foreach (int id in stale)
            {
                _assignments.Remove(id);
                _assignedAtTick.Remove(id);
            }
        }

        var counts = new Dictionary<TacticalJob, int>();
        var taken = new HashSet<int>();
        var available = new List<FootballerState>();

        // Keep incumbents on their job for a bounded period, unless the phase no longer fits.
        foreach (FootballerState player in players)
        {
            if (player.Team != team || !player.IsActiveOnPitch) continue;

            if (player.Role == TacticalRole.Goalkeeper)
            {
                Assign(player, TacticalJob.Shape, AnchorOf(player), null, "Goalkeeper", tick);
                taken.Add(player.Id);
                continue;
            }

            // The player on the ball (or the pass's intended receiver) is not an off-ball job.
            if (carrier is not null && player.Id == carrier.Id)
            {
                Assign(player, inPossession ? TacticalJob.BallResponder : TacticalJob.Shape,
                    AnchorOf(player), null, "On the ball", tick);
                taken.Add(player.Id);
                continue;
            }

            if (_assignments.TryGetValue(player.Id, out TeamJobAssignment existing) &&
                IsResponderJob(existing.Job) == false &&
                tick - _assignedAtTick.GetValueOrDefault(player.Id, int.MinValue) < MinimumHoldTicks &&
                JobMatchesPhase(existing.Job, inPossession) &&
                counts.GetValueOrDefault(existing.Job) < MaxPerJob(existing.Job) &&
                Vector2.Distance(
                    new Vector2(player.Position.X, player.Position.Z),
                    new Vector2(existing.Anchor.X, existing.Anchor.Z)) <= MaxAssignmentTravel(existing.Job) + 6f)
            {
                counts[existing.Job] = counts.GetValueOrDefault(existing.Job) + 1;
                taken.Add(player.Id);
                continue;
            }

            available.Add(player);
        }

        // The elected responder is authoritative: press (out of possession) or collect the pass.
        if (responderId is int responderIdValue &&
            Find(players, responderIdValue) is { } responder &&
            responder.Team == team && responder.IsActiveOnPitch &&
            responder.Role != TacticalRole.Goalkeeper)
        {
            TacticalJob responderJob = inPossession ? TacticalJob.BallResponder : TacticalJob.Presser;
            Vector2 responderTarget = inPossession ? referenceXZ : ballXZ;
            Assign(responder, responderJob, new Vector3(responderTarget.X, 0f, responderTarget.Y), null,
                inPossession ? "Collect the pass" : "Press the ball", tick);
            counts[responderJob] = counts.GetValueOrDefault(responderJob) + 1;
            taken.Add(responder.Id);
            available.RemoveAll(p => p.Id == responder.Id);
        }

        float offsideLineProj = ComputeOffsideLineProj(opponents, ball, attackDirX);
        float referenceProj = referenceXZ.X * attackDirX;
        Vector2 ownGoalXZ = new(-attackDirX * _pitch.HalfLength, 0f);

        if (inPossession)
        {
            AssignUniqueJob(available, counts, taken, TacticalJob.DefensiveCover, opponents, referenceXZ, attackDirX, offsideLineProj, tick,
                _ => ClampToPitch(referenceXZ + SimMath.SafeNormalize(ownGoalXZ - referenceXZ, new Vector2(-attackDirX, 0f)) * 11f));

            AssignUniqueJob(available, counts, taken, TacticalJob.NearOutlet, opponents, referenceXZ, attackDirX, offsideLineProj, tick,
                player =>
                {
                    float side = player.Position.Z - referenceXZ.Y;
                    side = MathF.Abs(side) > 2f ? MathF.Sign(side) : -MathF.Sign(referenceXZ.Y >= 0f ? 1f : -1f);
                    return ClampToPitch(referenceXZ + new Vector2(-attackDirX * 5f, side * 7f));
                });

            AssignUniqueJob(available, counts, taken, TacticalJob.ThirdPlayerOption, opponents, referenceXZ, attackDirX, offsideLineProj, tick,
                _ =>
                {
                    float side = referenceXZ.Y >= 0f ? -1f : 1f;
                    Vector2 target = referenceXZ + new Vector2(attackDirX * 12f, side * 9f);
                    return ClampToPitch(Onside(target, offsideLineProj - 1.5f, attackDirX));
                });

            AssignUniqueJob(available, counts, taken, TacticalJob.FarSideWidth, opponents, referenceXZ, attackDirX, offsideLineProj, tick,
                _ =>
                {
                    float side = referenceXZ.Y >= 0f ? -1f : 1f;
                    Vector2 target = new(referenceXZ.X + attackDirX * 6f, side * wideZ);
                    return ClampToPitch(Onside(target, offsideLineProj - 1.5f, attackDirX));
                });

            // A depth run only makes sense when there is space behind the line to attack.
            bool depthRunPossible = offsideLineProj - referenceProj > 4f && offsideLineProj > 10f;
            if (depthRunPossible)
            {
                AssignUniqueJob(available, counts, taken, TacticalJob.DepthRun, opponents, referenceXZ, attackDirX, offsideLineProj, tick,
                    player =>
                    {
                        if (player.Role is not (TacticalRole.Striker or TacticalRole.Winger or TacticalRole.CentralMidfielder))
                            return null;

                        float runProj = MathF.Max(2f, MathF.Min(offsideLineProj - 1.5f,
                            MathF.Max(referenceProj + 8f, offsideLineProj - 6f)));

                        // Run in the player's own lane (their formation anchor), not wherever they
                        // happen to have drifted: a winger must stay wide while running in behind.
                        float laneZ = player.TacticalAnchor == Vector3.Zero
                            ? player.Position.Z
                            : player.TacticalAnchor.Z;
                        float z = Math.Clamp(laneZ, -_pitch.HalfWidth + 6f, _pitch.HalfWidth - 6f);
                        return new Vector2(attackDirX * runProj, z);
                    }, baseScore: 0.05f);
            }

            AssignUniqueJob(available, counts, taken, TacticalJob.Overlap, opponents, referenceXZ, attackDirX, offsideLineProj, tick,
                player =>
                {
                    if (MathF.Abs(referenceXZ.Y) < 8f) return null;
                    if (player.Position.X * attackDirX > referenceProj - 1f) return null;
                    Vector2 target = new(
                        referenceXZ.X + attackDirX * 7f,
                        MathF.Sign(referenceXZ.Y) * wideZ);
                    return ClampToPitch(Onside(target, offsideLineProj - 1.5f, attackDirX));
                });
        }
        else
        {
            FootballerState? presser = Find(players, responderId ?? -1);
            Vector2 pressReference = presser is null
                ? referenceXZ
                : new Vector2(presser.Position.X, presser.Position.Z);
            Vector2 coverAnchor = ClampToPitch(
                pressReference + SimMath.SafeNormalize(ownGoalXZ - pressReference, new Vector2(-attackDirX, 0f)) * 7f);

            AssignUniqueJob(available, counts, taken, TacticalJob.CoverDefender, opponents, referenceXZ, attackDirX, offsideLineProj, tick,
                _ => coverAnchor, baseScore: 0.05f);

            AssignMarkers(available, counts, taken, opponents, ownGoalXZ, attackDirX, tick, 2);

            // The remaining defenders hold the line; everyone else keeps team shape.
            foreach (FootballerState player in available.ToArray())
            {
                if (player.Role is not (TacticalRole.CenterBack or TacticalRole.FullBack)) continue;
                Assign(player, TacticalJob.DefensiveLine, AnchorOf(player), null, "Hold the defensive line", tick);
                taken.Add(player.Id);
                available.Remove(player);
                counts[TacticalJob.DefensiveLine] = counts.GetValueOrDefault(TacticalJob.DefensiveLine) + 1;
            }
        }

        foreach (FootballerState player in available)
        {
            Assign(player, TacticalJob.Shape, AnchorOf(player), null, "Team shape", tick);
        }
    }

    private void AssignMarkers(
        List<FootballerState> available,
        Dictionary<TacticalJob, int> counts,
        HashSet<int> taken,
        TeamBelief opponents,
        Vector2 ownGoalXZ,
        float attackDirX,
        int tick,
        int maxMarkers)
    {
        // A2.6: markers are picked from what the team believes, so an opponent nobody has seen cannot
        // pull a defender out of shape.
        var threats = new List<TeamBelief.Opponent>();
        for (int i = 0; i < opponents.Opponents.Count; i++)
        {
            TeamBelief.Opponent opponent = opponents.Opponents[i];
            // A keeper is never a marking job: he stays in his own half and marking him would pull a
            // defender out of the line for nobody.
            if (opponent.Role == TacticalRole.Goalkeeper) continue;
            if (Vector2.Distance(opponent.PositionXZ, ownGoalXZ) < 45f)
                threats.Add(opponent);
        }

        threats.Sort((a, b) => Vector2.Distance(a.PositionXZ, ownGoalXZ)
            .CompareTo(Vector2.Distance(b.PositionXZ, ownGoalXZ)));

        int assigned = 0;
        foreach (TeamBelief.Opponent threat in threats)
        {
            if (assigned >= maxMarkers) break;
            if (counts.GetValueOrDefault(TacticalJob.Marker) >= maxMarkers) break;

            FootballerState? best = null;
            float bestScore = float.MinValue;
            Vector2 threatXZ = threat.PositionXZ;
            Vector2 goalSide = SimMath.SafeNormalize(ownGoalXZ - threatXZ, new Vector2(-attackDirX, 0f));
            Vector3 anchor = new(threatXZ.X + goalSide.X * 1.9f, 0f, threatXZ.Y + goalSide.Y * 1.9f);

            foreach (FootballerState player in available)
            {
                if (taken.Contains(player.Id)) continue;
                if (player.Role is not (TacticalRole.CenterBack or TacticalRole.FullBack or TacticalRole.DefensiveMidfielder))
                    continue;

                float distance = Vector2.Distance(new Vector2(player.Position.X, player.Position.Z), threatXZ);
                if (distance > MaxAssignmentTravel(TacticalJob.Marker)) continue;

                float score = RoleFit(TacticalJob.Marker, player.Role) * 1.2f - distance / 30f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = player;
                }
            }

            if (best is null) break;

            Assign(best, TacticalJob.Marker, anchor, threat.PlayerId, $"Mark #{threat.PlayerId}", tick);
            taken.Add(best.Id);
            available.Remove(best);
            counts[TacticalJob.Marker] = counts.GetValueOrDefault(TacticalJob.Marker) + 1;
            assigned++;
        }
    }

    private void AssignUniqueJob(
        List<FootballerState> available,
        Dictionary<TacticalJob, int> counts,
        HashSet<int> taken,
        TacticalJob job,
        TeamBelief opponents,
        Vector2 referenceXZ,
        float attackDirX,
        float offsideLineProj,
        int tick,
        Func<FootballerState, Vector2?> anchorFactory,
        float baseScore = 0f)
    {
        if (counts.GetValueOrDefault(job) >= MaxPerJob(job)) return;

        FootballerState? best = null;
        Vector3 bestAnchor = default;
        float bestScore = float.MinValue;

        foreach (FootballerState player in available)
        {
            if (taken.Contains(player.Id)) continue;
            Vector2? maybeAnchor = anchorFactory(player);
            if (maybeAnchor is not Vector2 anchorXZ) continue;
            Vector3 anchor = new(anchorXZ.X, 0f, anchorXZ.Y);

            // Hard guard: a job target must never be clearly beyond the offside line.
            bool attackingJob = job is TacticalJob.NearOutlet or TacticalJob.ThirdPlayerOption
                or TacticalJob.FarSideWidth or TacticalJob.DepthRun or TacticalJob.Overlap;
            if (attackingJob && anchor.X * attackDirX > offsideLineProj + 2f)
                continue;

            float space = opponents.NearestDistance(new Vector2(anchor.X, anchor.Z));
            float travel = Vector2.Distance(new Vector2(player.Position.X, player.Position.Z), new Vector2(anchor.X, anchor.Z));

            // A job more than ~20 m away would drag the player across the pitch and leave a hole in
            // the shape (an overlap assigned to a full-back 50 m behind, a far-side width run from
            // the wrong flank). Leave the player on his shape instead.
            if (travel > MaxAssignmentTravel(job)) continue;

            float score = baseScore
                        + RoleFit(job, player.Role) * 1.25f
                        + Math.Clamp(space / 10f, 0f, 1f) * 0.85f
                        - travel / 40f;

            if (job is TacticalJob.DepthRun or TacticalJob.ThirdPlayerOption or TacticalJob.FarSideWidth)
                score += Math.Clamp((anchor.X - referenceXZ.X) * attackDirX / 15f, -0.2f, 0.4f) * 0.5f;

            if (score > bestScore)
            {
                bestScore = score;
                best = player;
                bestAnchor = anchor;
            }
        }

        if (best is null) return;

        Assign(best, job, bestAnchor, null, JobReason(job), tick);
        taken.Add(best.Id);
        available.Remove(best);
        counts[job] = counts.GetValueOrDefault(job) + 1;
    }

    private void Assign(FootballerState player, TacticalJob job, Vector3 anchor, int? targetPlayerId, string reason, int tick)
    {
        _assignments[player.Id] = new TeamJobAssignment(job, anchor, targetPlayerId, reason);
        _assignedAtTick[player.Id] = tick;
    }

    private static int MaxPerJob(TacticalJob job) => job switch
    {
        TacticalJob.Shape => 64,
        TacticalJob.DefensiveLine => 4,
        TacticalJob.Marker => 2,
        _ => 1
    };

    /// <summary>
    /// Largest distance a player may travel to take up a job. Contextual jobs are worth offering
    /// when the player is already near them; beyond that they are just a reason to leave formation.
    /// </summary>
    private static float MaxAssignmentTravel(TacticalJob job) => job switch
    {
        TacticalJob.Overlap => 24f,
        TacticalJob.FarSideWidth => 26f,
        TacticalJob.DepthRun => 26f,
        TacticalJob.ThirdPlayerOption => 22f,
        TacticalJob.NearOutlet => 20f,
        TacticalJob.DefensiveCover => 20f,
        TacticalJob.CoverDefender => 18f,
        TacticalJob.Marker => 18f,
        _ => 30f
    };

    private static bool IsResponderJob(TacticalJob job) =>
        job is TacticalJob.BallResponder or TacticalJob.Presser;

    private static bool JobMatchesPhase(TacticalJob job, bool inPossession) =>
        inPossession
            ? job is TacticalJob.BallResponder or TacticalJob.NearOutlet or TacticalJob.ThirdPlayerOption
                or TacticalJob.FarSideWidth or TacticalJob.DepthRun or TacticalJob.Overlap
                or TacticalJob.DefensiveCover or TacticalJob.Shape
            : job is TacticalJob.Presser or TacticalJob.CoverDefender or TacticalJob.Marker
                or TacticalJob.DefensiveLine or TacticalJob.Shape;

    private static float RoleFit(TacticalJob job, TacticalRole role) => job switch
    {
        TacticalJob.DefensiveCover => role is TacticalRole.CenterBack ? 1.0f
            : role is TacticalRole.DefensiveMidfielder ? 0.9f
            : role is TacticalRole.FullBack ? 0.6f : 0.2f,
        TacticalJob.NearOutlet => role is TacticalRole.DefensiveMidfielder or TacticalRole.CenterBack or TacticalRole.CentralMidfielder ? 1.0f
            : role is TacticalRole.FullBack ? 0.7f : 0.4f,
        TacticalJob.ThirdPlayerOption => role is TacticalRole.CentralMidfielder ? 1.0f
            : role is TacticalRole.DefensiveMidfielder ? 0.7f
            : role is TacticalRole.Winger ? 0.65f : 0.4f,
        TacticalJob.FarSideWidth => role is TacticalRole.Winger ? 1.0f
            : role is TacticalRole.FullBack ? 0.55f
            : role is TacticalRole.CentralMidfielder ? 0.5f : 0.3f,
        TacticalJob.DepthRun => role is TacticalRole.Striker ? 1.0f
            : role is TacticalRole.Winger ? 0.85f
            : role is TacticalRole.CentralMidfielder ? 0.5f : 0.2f,
        TacticalJob.Overlap => role is TacticalRole.FullBack ? 0.95f
            : role is TacticalRole.Winger ? 0.7f : 0.2f,
        TacticalJob.CoverDefender => role is TacticalRole.CenterBack or TacticalRole.DefensiveMidfielder ? 1.0f
            : role is TacticalRole.FullBack ? 0.6f : 0.2f,
        TacticalJob.Marker => role is TacticalRole.CenterBack or TacticalRole.FullBack or TacticalRole.DefensiveMidfielder ? 1.0f : 0.4f,
        TacticalJob.DefensiveLine => role is TacticalRole.CenterBack or TacticalRole.FullBack ? 1.0f : 0.3f,
        _ => 0.3f
    };

    private static string JobReason(TacticalJob job) => job switch
    {
        TacticalJob.NearOutlet => "Offer a short outlet",
        TacticalJob.ThirdPlayerOption => "Offer a diagonal third-player option",
        TacticalJob.FarSideWidth => "Hold far-side width",
        TacticalJob.DepthRun => "Run in behind",
        TacticalJob.Overlap => "Overlap the wide carrier",
        TacticalJob.DefensiveCover => "Cover behind the ball",
        TacticalJob.CoverDefender => "Cover behind the presser",
        TacticalJob.Presser => "Press the ball",
        TacticalJob.BallResponder => "Collect the pass",
        TacticalJob.Marker => "Mark an attacker",
        TacticalJob.DefensiveLine => "Hold the defensive line",
        _ => "Team shape"
    };

    /// <summary>
    /// Offside line from what the team believes (A2.6). Fewer than two known opponents is not enough to
    /// draw a line, so the conservative fallback is the halfway line rather than an invented defender.
    /// </summary>
    private float ComputeOffsideLineProj(TeamBelief opponents, BallState ball, float attackDirX)
    {
        float secondLast = opponents.TryGetSecondLastProjX(attackDirX, out float believed)
            ? believed
            : _pitch.HalfLength;

        float ballProj = ball.Position.X * attackDirX;
        return MathF.Max(0f, MathF.Max(secondLast, ballProj));
    }

    private static Vector3 AnchorOf(FootballerState player) =>
        player.TacticalAnchor == Vector3.Zero ? player.Position : player.TacticalAnchor;

    private Vector2 Onside(Vector2 target, float maxProj, float attackDirX)
    {
        if (target.X * attackDirX > maxProj)
            target.X = attackDirX * MathF.Max(0f, maxProj);
        return target;
    }

    private Vector2 ClampToPitch(Vector2 point) => new(
        Math.Clamp(point.X, -_pitch.HalfLength + 3f, _pitch.HalfLength - 3f),
        Math.Clamp(point.Y, -_pitch.HalfWidth + 3f, _pitch.HalfWidth - 3f));

    private static FootballerState? Find(IReadOnlyList<FootballerState> players, int playerId)
    {
        foreach (FootballerState player in players)
            if (player.Id == playerId) return player;
        return null;
    }
}
