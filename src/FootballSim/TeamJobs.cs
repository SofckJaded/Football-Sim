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
    /// <summary>
    /// Out of possession: temporarily protect the region a line player vacated when he stepped out
    /// (A3.3). This is a *temporary responsibility*, not the owner's own duty: he holds it until
    /// handback is safe and then returns to his own job.
    /// </summary>
    ProtectGap,
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
    string Reason)
{
    /// <summary>
    /// A3.3: the teammate whose vacated region this job protects, when the job is a temporary
    /// responsibility. Null for the player's own duty.
    /// </summary>
    public int? SourcePlayerId { get; init; }

    /// <summary>A3.3: true when the job is an exception layered on another player's region.</summary>
    public bool IsTemporary { get; init; }
}

/// <summary>
/// A3.3: a region held on behalf of a teammate who had to leave it. The owner keeps it until handback
/// is safe — the source is back in the region, the press that pulled him out has ended, the ball has
/// left the area, the owner or source left the pitch, or the hold expires. A3.5 generalises this into
/// coverage requests with bids and cascades; this is the single bounded instance the CB step-out gate
/// needs, and it is deliberately one per team so it cannot reshuffle the side.
/// </summary>
public readonly record struct TemporaryResponsibility(
    TeamId Team,
    int OwnerPlayerId,
    int SourcePlayerId,
    Vector3 Region,
    int AcquiredTick,
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

    /// <summary>A temporary responsibility never outlives this window, whatever else happens (A3.3).</summary>
    private const int MaxResponsibilityHoldTicks = 720;

    /// <summary>
    /// How far out of the line a pressing defender must be before his slot counts as vacated, measured
    /// from the shape anchor he was given (metres). Below this the press has not really left a hole.
    /// </summary>
    private const float VacateDistanceMeters = 7f;

    /// <summary>The source is back in the region once he is this close to it, in metres.</summary>
    private const float HandbackDistanceMeters = 6f;

    /// <summary>A gap further than this from the ball is not in play and is not worth covering.</summary>
    private const float MaxVacancyDistanceMeters = 32f;

    /// <summary>The slot must be at least this far goal-side of the ball to be a gap, in metres.</summary>
    private const float SlotGoalSideMarginMeters = 4f;

    /// <summary>Largest distance a line player may travel to take a protection job.</summary>
    private const float MaxProtectorTravelMeters = 20f;

    /// <summary>
    /// How much better a refined job region has to score before the duty's nominal target is moved
    /// (A3.4). Without a margin the probe would follow the score's noise and the anchor would jitter.
    /// </summary>
    private const float RegionSwitchMargin = 0.05f;

    private readonly PitchDimensions _pitch;
    private readonly Dictionary<int, TeamJobAssignment> _assignments = new();
    private readonly Dictionary<int, int> _assignedAtTick = new();
    private readonly Dictionary<TeamId, TemporaryResponsibility> _responsibilities = new();

    /// <summary>
    /// The regions already handed out in this team's assignment pass (A3.4). Reused, because the
    /// staggering and channel terms need the teammates' targets and a per-pass list would allocate.
    /// </summary>
    private readonly List<AssignedRegion> _assignedRegions = new();

    /// <summary>
    /// How wide the team currently being assigned wants to be (A3.4). Set at the start of its pass so the
    /// width and channel terms score regions against the profile rather than against the full pitch.
    /// </summary>
    private float _attackingWidth01 = 0.55f;

    private TeamId? _lastPossessionTeam;
    private int _lastRecomputeTick = -RecomputeIntervalTicks;

    public TeamJobCoordinator(PitchDimensions pitch)
    {
        _pitch = pitch;
    }

    public IReadOnlyDictionary<int, TeamJobAssignment> Assignments => _assignments;

    /// <summary>The temporary responsibility <paramref name="team"/> is currently holding, if any (A3.3).</summary>
    public TemporaryResponsibility? ResponsibilityOf(TeamId team) =>
        _responsibilities.TryGetValue(team, out TemporaryResponsibility responsibility) ? responsibility : null;

    /// <summary>Lifetime count of temporary responsibilities created. Diagnostics and tests.</summary>
    public int ResponsibilityAssignments { get; private set; }

    /// <summary>Lifetime count of temporary responsibilities released. Diagnostics and tests.</summary>
    public int ResponsibilityHandbacks { get; private set; }

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
        TeamBelief awayOpponents,
        TeamFormation homeFormation,
        TeamFormation awayFormation)
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
            // A turnover invalidates every temporary responsibility: the region it protected belonged
            // to the passage that just ended (A3.3, A3 exit gate "turnovers void duties").
            if (_responsibilities.Count > 0)
            {
                ResponsibilityHandbacks += _responsibilities.Count;
                _responsibilities.Clear();
            }
        }

        AssignTeam(TeamId.Home, players, ball, possession, possessingTeam, homeResponderId, homeDirX, tick,
            homeProfile, homeOpponents, homeFormation);
        AssignTeam(TeamId.Away, players, ball, possession, possessingTeam, awayResponderId, -homeDirX, tick,
            awayProfile, awayOpponents, awayFormation);
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
        TeamBelief opponents,
        TeamFormation formation)
    {
        Vector2 ballXZ = new(ball.Position.X, ball.Position.Z);

        // A3.4: each pass scores regions against the teammates already placed, so the list is cleared
        // per team and reused; the width the team wants is part of the score.
        _assignedRegions.Clear();
        _attackingWidth01 = profile.AttackingWidth01;

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

        // A responsibility whose owner or source is no longer on the pitch cannot be held.
        if (_responsibilities.TryGetValue(team, out TemporaryResponsibility held) &&
            (Find(players, held.OwnerPlayerId) is not { IsActiveOnPitch: true } ||
             Find(players, held.SourcePlayerId) is not { IsActiveOnPitch: true }))
        {
            ReleaseResponsibility(team);
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
                // A3.4: a kept assignment is still a region the later jobs have to work around.
                RecordRegion(player.Id, existing.Job, existing.Anchor);
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

        // A3.3: if the press has pulled a line player out of his line, a teammate takes a temporary
        // responsibility for the slot he left and holds it until handback is safe. This runs before the
        // normal jobs so the responsibility can claim a player the incumbent pass kept on the line: the
        // line has to slide, and that is the point of the mechanism.
        MaintainResponsibility(team, players, formation, ballXZ, attackDirX, tick, inPossession,
            responderId, taken, available);

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

        // A3.3: slow structural reassignment. Only a player whose duty *is* his own nominal shape can
        // be mismatched: a contextual job is a duty elsewhere, not an ownership error. The review is
        // per-player, bounded and triggered by a long stationary displacement, never by a tick's drift.
        foreach (FootballerState player in players)
        {
            if (player.Team != team || !player.IsActiveOnPitch) continue;
            if (!_assignments.TryGetValue(player.Id, out TeamJobAssignment duty)) continue;

            bool onOwnDuty = !duty.IsTemporary &&
                             duty.Job is TacticalJob.Shape or TacticalJob.DefensiveLine;
            formation.ReviewMismatch(player, attackDirX, tick, onOwnDuty);
        }
    }

    /// <summary>
    /// A3.3: creates, keeps or hands back the team's temporary responsibility.
    ///
    /// A line player pulled more than <see cref="VacateDistanceMeters"/> out of the shape he was given,
    /// by the press job, leaves a gap behind him: the slot he owns, provided that slot is goal-side of
    /// the ball and inside play range. One teammate — a line player who can slide across, chosen
    /// deterministically — owns that region until handback is safe. Keeping the incumbent owner while
    /// the gap is still vacated is what makes the hold stable; releasing on handback is what returns
    /// him to his own duty.
    /// </summary>
    private void MaintainResponsibility(
        TeamId team,
        IReadOnlyList<FootballerState> players,
        TeamFormation formation,
        Vector2 ballXZ,
        float attackDirX,
        int tick,
        bool inPossession,
        int? responderId,
        HashSet<int> taken,
        List<FootballerState> available)
    {
        bool held = _responsibilities.TryGetValue(team, out TemporaryResponsibility responsibility);

        if (inPossession)
        {
            // We have the ball again: nothing about the old passage's gap is a duty any more.
            if (held) ReleaseResponsibility(team);
            return;
        }

        if (held)
        {
            FootballerState? source = Find(players, responsibility.SourcePlayerId);
            FootballerState? owner = Find(players, responsibility.OwnerPlayerId);
            bool stillVacated = source is { IsActiveOnPitch: true } &&
                                owner is { IsActiveOnPitch: true } &&
                                PressesStill(source.Id) &&
                                tick - responsibility.AcquiredTick <= MaxResponsibilityHoldTicks &&
                                PlanarDistance(new Vector2(source.Position.X, source.Position.Z),
                                    AnchorXZ(source)) > HandbackDistanceMeters &&
                                Vector2.Distance(ballXZ, new Vector2(responsibility.Region.X, responsibility.Region.Z)) <=
                                MaxVacancyDistanceMeters;

            if (stillVacated)
            {
                // Sticky: the same owner keeps the same region while it is still vacated. The gap is the
                // slot he owns, and a slot does not move, so nothing jitters while he is away.
                Assign(owner!, TacticalJob.ProtectGap, responsibility.Region, null, responsibility.Reason, tick,
                    sourcePlayerId: responsibility.SourcePlayerId, temporary: true);
                taken.Add(owner!.Id);
                available.RemoveAll(p => p.Id == owner.Id);
                return;
            }

            ReleaseResponsibility(team);
        }

        Vacancy? vacancy = FindVacancy(team, players, formation, ballXZ, attackDirX);
        if (vacancy is not Vacancy gap) return;

        FootballerState? protector = ChooseProtector(team, players, formation, gap, attackDirX, responderId);
        if (protector is null) return;

        string reason = $"Protect #{gap.SourcePlayerId}'s vacated gap";
        var assigned = new TemporaryResponsibility(team, protector.Id, gap.SourcePlayerId, gap.Region, tick, reason);
        _responsibilities[team] = assigned;
        ResponsibilityAssignments++;

        Assign(protector, TacticalJob.ProtectGap, gap.Region, null, reason, tick,
            sourcePlayerId: gap.SourcePlayerId, temporary: true);
        taken.Add(protector.Id);
        available.RemoveAll(p => p.Id == protector.Id);
    }

    private bool PressesStill(int playerId) =>
        _assignments.TryGetValue(playerId, out TeamJobAssignment assignment) &&
        assignment.Job == TacticalJob.Presser;

    /// <summary>
    /// The most consequential vacated gap: the nearest one to the ball, ties by player id.
    ///
    /// The two references are deliberately different. *Is he out of the line* is measured against the
    /// shape anchor he was given, because that anchor already carries the line height and the ball
    /// terms the side is playing with — a high line is not a vacancy. *What must be protected* is the
    /// slot he owns, because that is the region of the formation that is now empty and it does not move
    /// while the ball does.
    /// </summary>
    private Vacancy? FindVacancy(
        TeamId team,
        IReadOnlyList<FootballerState> players,
        TeamFormation formation,
        Vector2 ballXZ,
        float attackDirX)
    {
        Vacancy? best = null;
        float bestSlotToBall = float.MaxValue;

        foreach (FootballerState player in players)
        {
            if (player.Team != team || !player.IsActiveOnPitch) continue;
            if (player.Role is not (TacticalRole.CenterBack or TacticalRole.FullBack or TacticalRole.DefensiveMidfielder))
                continue;
            if (!PressesStill(player.Id)) continue;
            if (!formation.TryGetWorldAnchor(player.Id, attackDirX, out Vector3 region)) continue;

            float slotToBall = Vector2.Distance(ballXZ, new Vector2(region.X, region.Z));
            if (slotToBall > MaxVacancyDistanceMeters) continue;

            // The slot must be goal-side of the ball, or there is no gap behind the press to cover.
            if (region.X * attackDirX >= ballXZ.X * attackDirX - SlotGoalSideMarginMeters) continue;

            float displaced = PlanarDistance(new Vector2(player.Position.X, player.Position.Z), AnchorXZ(player));
            if (displaced < VacateDistanceMeters) continue;

            if (slotToBall >= bestSlotToBall) continue;
            bestSlotToBall = slotToBall;
            best = new Vacancy(player.Id, region);
        }

        return best;
    }

    /// <summary>The nearest eligible line player who can slide into the gap, deterministically.</summary>
    private FootballerState? ChooseProtector(
        TeamId team,
        IReadOnlyList<FootballerState> players,
        TeamFormation formation,
        Vacancy gap,
        float attackDirX,
        int? responderId)
    {
        Vector2 regionXZ = new(gap.Region.X, gap.Region.Z);
        FootballerState? best = null;
        float bestScore = float.MinValue;

        foreach (FootballerState player in players)
        {
            if (player.Team != team || !player.IsActiveOnPitch) continue;
            if (player.Id == gap.SourcePlayerId || player.Id == responderId) continue;
            if (player.Role is not (TacticalRole.CenterBack or TacticalRole.FullBack or TacticalRole.DefensiveMidfielder))
                continue;
            if (_assignments.TryGetValue(player.Id, out TeamJobAssignment duty) &&
                duty.Job is TacticalJob.Marker or TacticalJob.BallResponder or TacticalJob.ProtectGap)
                continue;

            Vector2 playerXZ = new(player.Position.X, player.Position.Z);
            float travel = Vector2.Distance(playerXZ, regionXZ);
            if (travel > MaxProtectorTravelMeters) continue;

            float score = RoleFit(TacticalJob.ProtectGap, player.Role) * 1.25f +
                          (1f - travel / MaxProtectorTravelMeters) * 0.8f;

            // Prefer the man whose own slot is nearest: he slides along the line rather than
            // abandoning the far side of it.
            if (formation.TryGetWorldAnchor(player.Id, attackDirX, out Vector3 ownSlot))
            {
                score += Math.Clamp(
                    1f - Vector2.Distance(new Vector2(ownSlot.X, ownSlot.Z), regionXZ) / 25f, 0f, 1f) * 0.5f;
            }

            if (score > bestScore || (MathF.Abs(score - bestScore) < 1e-4f && best is not null && player.Id < best.Id))
            {
                bestScore = score;
                best = player;
            }
        }

        return best;
    }

    private void ReleaseResponsibility(TeamId team)
    {
        if (_responsibilities.Remove(team)) ResponsibilityHandbacks++;
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

        bool attackingJob = IsAttackingJob(job);
        FootballerState? best = null;
        Vector2 bestAnchor = default;
        float bestScore = float.MinValue;

        foreach (FootballerState player in available)
        {
            if (taken.Contains(player.Id)) continue;
            Vector2? maybeAnchor = anchorFactory(player);
            if (maybeAnchor is not Vector2 anchorXZ) continue;

            // Hard guard: a job target must never be clearly beyond the offside line.
            if (attackingJob && anchorXZ.X * attackDirX > offsideLineProj + 2f)
                continue;

            float travel = Vector2.Distance(new Vector2(player.Position.X, player.Position.Z), anchorXZ);

            // A job more than ~20 m away would drag the player across the pitch and leave a hole in
            // the shape (an overlap assigned to a full-back 50 m behind, a far-side width run from
            // the wrong flank). Leave the player on his shape instead.
            if (travel > MaxAssignmentTravel(job)) continue;

            // A3.4: the duty's target is a region, and the region is scored against the ball, the
            // teammates already placed, the opponents the team believes in and the space around it.
            float score = baseScore
                        + RoleFit(job, player.Role) * 1.25f
                        + ScoreJobRegion(job, anchorXZ, referenceXZ, player.Id, opponents, attackDirX, _attackingWidth01)
                        - travel / 40f;

            if (score > bestScore)
            {
                bestScore = score;
                best = player;
                bestAnchor = anchorXZ;
            }
        }

        if (best is null) return;

        // Then slide the chosen target inside a bounded neighbourhood of where the duty wants it. The
        // probe is deliberately small: a job target can be refined by metres, not re-invented, and it
        // only moves when it scores better by a margin.
        Vector2 anchor = RefineJobRegion(job, bestAnchor, referenceXZ, best.Id, opponents, attackDirX, offsideLineProj, _attackingWidth01);

        Assign(best, job, new Vector3(anchor.X, 0f, anchor.Y), null, JobReason(job), tick);
        taken.Add(best.Id);
        available.Remove(best);
        counts[job] = counts.GetValueOrDefault(job) + 1;
    }

    /// <summary>
    /// A3.4: one region's structure score, with the teammates placed so far in this pass. Pure and
    /// allocation-free; the terms live in <see cref="JobRegionScoring"/> so they can be tested alone.
    /// </summary>
    private float ScoreJobRegion(
        TacticalJob job,
        Vector2 centre,
        Vector2 referenceXZ,
        int playerId,
        TeamBelief opponents,
        float attackDirX,
        float attackingWidth01)
    {
        var region = new JobRegion(new Vector3(centre.X, 0f, centre.Y), JobRegionScoring.RadiusOf(job));
        return JobRegionScoring.Score(
            region, job, referenceXZ, _assignedRegions, playerId, opponents, attackDirX, _pitch,
            attackingWidth01);
    }

    /// <summary>
    /// A3.4: bounded local search for a better region around the duty's nominal target — two rings of
    /// lateral and depth probes, clamped to the pitch (and to the offside line when the duty is an
    /// attacking one). Two rings are deliberate: the scoring penalises stacking on a teammate, and a
    /// single ring cannot always pay the separation back, so the search has to be able to reach the
    /// next channel. Fixed probe order and a required margin keep it deterministic and stable.
    /// </summary>
    private Vector2 RefineJobRegion(
        TacticalJob job,
        Vector2 nominal,
        Vector2 referenceXZ,
        int playerId,
        TeamBelief opponents,
        float attackDirX,
        float offsideLineProj,
        float attackingWidth01)
    {
        bool attackingJob = IsAttackingJob(job);
        float lateral = job is TacticalJob.FarSideWidth or TacticalJob.Overlap ? 6f : 4f;
        float depth = job is TacticalJob.DepthRun or TacticalJob.Overlap ? 6f : 4f;

        Vector2 best = nominal;
        float bestScore = ScoreJobRegion(job, nominal, referenceXZ, playerId, opponents, attackDirX, attackingWidth01);

        for (int probe = 0; probe < 8; probe++)
        {
            float ring = probe < 4 ? 1f : 2f;
            Vector2 candidate = nominal;
            switch (probe % 4)
            {
                case 0: candidate.Y += lateral * ring; break;
                case 1: candidate.Y -= lateral * ring; break;
                case 2: candidate.X += attackDirX * depth * ring; break;
                default: candidate.X -= attackDirX * depth * ring; break;
            }

            candidate = ClampToPitch(candidate);
            if (attackingJob) candidate = Onside(candidate, offsideLineProj - 1.5f, attackDirX);

            float score = ScoreJobRegion(job, candidate, referenceXZ, playerId, opponents, attackDirX, attackingWidth01);
            if (score <= bestScore + RegionSwitchMargin) continue;

            bestScore = score;
            best = candidate;
        }

        return best;
    }

    private static bool IsAttackingJob(TacticalJob job) => job is TacticalJob.NearOutlet
        or TacticalJob.ThirdPlayerOption or TacticalJob.FarSideWidth or TacticalJob.DepthRun
        or TacticalJob.Overlap;

    private void Assign(
        FootballerState player,
        TacticalJob job,
        Vector3 anchor,
        int? targetPlayerId,
        string reason,
        int tick,
        int? sourcePlayerId = null,
        bool temporary = false)
    {
        _assignments[player.Id] = new TeamJobAssignment(job, anchor, targetPlayerId, reason)
        {
            SourcePlayerId = sourcePlayerId,
            IsTemporary = temporary
        };
        _assignedAtTick[player.Id] = tick;
        RecordRegion(player.Id, job, anchor);
    }

    /// <summary>Keeps the pass's region list in step with the assignments (A3.4).</summary>
    private void RecordRegion(int playerId, TacticalJob job, Vector3 anchor)
    {
        var region = new JobRegion(anchor, JobRegionScoring.RadiusOf(job));
        for (int i = 0; i < _assignedRegions.Count; i++)
        {
            if (_assignedRegions[i].PlayerId != playerId) continue;
            _assignedRegions[i] = new AssignedRegion(playerId, region);
            return;
        }

        _assignedRegions.Add(new AssignedRegion(playerId, region));
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
        TacticalJob.ProtectGap => MaxProtectorTravelMeters,
        _ => 30f
    };

    /// <summary>
    /// Jobs that follow the ball and must never be kept by incumbency: the responder jobs, and a
    /// temporary responsibility, whose life is owned by <see cref="MaintainResponsibility"/> and must
    /// end with the handback rather than outlive it (A3.3).
    /// </summary>
    private static bool IsResponderJob(TacticalJob job) =>
        job is TacticalJob.BallResponder or TacticalJob.Presser or TacticalJob.ProtectGap;

    private static bool JobMatchesPhase(TacticalJob job, bool inPossession) =>
        inPossession
            ? job is TacticalJob.BallResponder or TacticalJob.NearOutlet or TacticalJob.ThirdPlayerOption
                or TacticalJob.FarSideWidth or TacticalJob.DepthRun or TacticalJob.Overlap
                or TacticalJob.DefensiveCover or TacticalJob.Shape
            : job is TacticalJob.Presser or TacticalJob.CoverDefender or TacticalJob.Marker
                or TacticalJob.DefensiveLine or TacticalJob.ProtectGap or TacticalJob.Shape;

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
        TacticalJob.ProtectGap => role is TacticalRole.CenterBack ? 1.0f
            : role is TacticalRole.DefensiveMidfielder ? 0.9f
            : role is TacticalRole.FullBack ? 0.75f : 0.2f,
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
        TacticalJob.ProtectGap => "Protect a vacated gap",
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

    /// <summary>A part of the shape a teammate had to leave, and the world region that stands in for it.</summary>
    private readonly record struct Vacancy(int SourcePlayerId, Vector3 Region);

    /// <summary>The point the shape wants this player at, for "is he out of the line" tests.</summary>
    private static Vector2 AnchorXZ(FootballerState player) =>
        new(player.TacticalAnchor.X, player.TacticalAnchor.Z);

    private static float PlanarDistance(Vector2 a, Vector2 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

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
