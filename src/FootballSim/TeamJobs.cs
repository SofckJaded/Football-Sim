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
/// A6.3: one explicit transfer of marking responsibility. The threat crossed into another defender's zone,
/// so the job moved from one player to another on a stated tick, with both slots' distances recorded — a
/// handover that can be explained, rather than a silent re-selection when the hold happened to expire.
/// </summary>
public readonly record struct MarkingHandover(
    int Tick,
    int ThreatPlayerId,
    int FromPlayerId,
    int ToPlayerId,
    float FromSlotDistance,
    float ToSlotDistance)
{
    /// <summary>Compact text form for a probe or a failure message.</summary>
    public string Describe() =>
        $"tick {Tick}: #{ThreatPlayerId} from #{FromPlayerId} ({FromSlotDistance:F1} m slot)" +
        $" to #{ToPlayerId} ({ToSlotDistance:F1} m slot)";
}


/// <summary>
/// Assigns persistent, team-level off-ball jobs relative to the actual ball/carrier and keeps them
/// for a bounded period (audit F04). Jobs are recomputed every few ticks; a possession change
/// interrupts immediately. Formation anchors stay the constraint/fallback.
/// </summary>
public sealed class TeamJobCoordinator
{
    private const int RecomputeIntervalTicks = 6;
    private const int MinimumHoldTicks = 42;

    /// <summary>A coverage request never outlives this window, whatever else happens (A3.5).</summary>
    private const int MaxCoverageHoldTicks = 720;

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

    /// <summary>
    /// How much closer to the threat another defender's *slot* has to be before marking responsibility is
    /// handed over (A6.3), in metres. A threat drifting between two centre-backs must not change hands on
    /// every recompute, so the zone owner has to win by a real margin.
    /// </summary>
    private const float HandoverZoneMarginMeters = 3f;

    /// <summary>
    /// How close the new owner's slot must be to the threat for a handover to be worth making, in metres.
    /// Beyond this nobody owns him and the current marker keeps the job.
    /// </summary>
    private const float MaxZoneHandoverMeters = 14f;

    /// <summary>Handovers allowed per assignment pass. One, so the line can absorb it without cascading.</summary>
    private const int MaxHandoversPerPass = 1;

    /// <summary>How many handover events the log keeps, so a match cannot grow it without limit.</summary>
    public const int MaxHandoverLogEntries = 128;

    private readonly PitchDimensions _pitch;
    private readonly Dictionary<int, TeamJobAssignment> _assignments = new();
    private readonly Dictionary<int, int> _assignedAtTick = new();
    /// <summary>One coverage request per team: reusing it is the cascade bound (A3.5).</summary>
    private readonly Dictionary<TeamId, CoverageRequest> _coverage = new();

    /// <summary>The defensive line band each team is currently standing in (A6.3).</summary>
    private readonly Dictionary<TeamId, DefensiveLineBand> _lineBands = new();

    /// <summary>Bounded log of explicit marking handovers (A6.3), oldest first.</summary>
    private readonly List<MarkingHandover> _handovers = new();

    /// <summary>
    /// Markers this profile allows in one pass (A6.3's zone/man mix). Set at the start of each team's pass
    /// so the keep loop and the assignment loop cannot disagree about the cap.
    /// </summary>
    private int _markerCap = 2;

    /// <summary>Handovers already made in this team's pass, against <see cref="MaxHandoversPerPass"/>.</summary>
    private int _handoversThisPass;

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

    /// <summary>The rest-defence picture from each team's last in-possession pass (A3.6).</summary>
    private readonly Dictionary<TeamId, RestDefence> _security = new();

    private TeamId? _lastPossessionTeam;
    private int _lastRecomputeTick = -RecomputeIntervalTicks;

    public TeamJobCoordinator(PitchDimensions pitch)
    {
        _pitch = pitch;
        _coverage[TeamId.Home] = new CoverageRequest(TeamId.Home);
        _coverage[TeamId.Away] = new CoverageRequest(TeamId.Away);
    }

    public IReadOnlyDictionary<int, TeamJobAssignment> Assignments => _assignments;

    /// <summary>
    /// The team's coverage request (A3.5). There is exactly one per team and it is reused, so a caller
    /// reads <see cref="CoverageRequest.IsLive"/> and <see cref="CoverageRequest.State"/> rather than
    /// testing for null. Its bid table and close reason are the evidence for how it ended.
    /// </summary>
    public CoverageRequest CoverageOf(TeamId team) => _coverage[team];

    /// <summary>
    /// The rest-defence picture from <paramref name="team"/>'s most recent in-possession pass (A3.6).
    /// Diagnostics and tests read it to explain why a forward duty was or was not offered.
    /// </summary>
    public RestDefence SecurityOf(TeamId team) =>
        _security.TryGetValue(team, out RestDefence security) ? security : default;

    /// <summary>
    /// The depth band <paramref name="team"/>'s defensive line is standing in (A6.3), or an invalid band
    /// while that team has the ball or has no line. The line's own job targets are placed from it, so this
    /// is the same fact the players' observations carry.
    /// </summary>
    public DefensiveLineBand LineBandOf(TeamId team) =>
        _lineBands.TryGetValue(team, out DefensiveLineBand band) ? band : DefensiveLineBand.None;

    /// <summary>
    /// Explicit marking handovers this match has made, oldest first (A6.3), bounded to
    /// <see cref="MaxHandoverLogEntries"/>. Diagnostics and tests read it; no decision does.
    /// </summary>
    public IReadOnlyList<MarkingHandover> Handovers => _handovers;

    /// <summary>Lifetime count of marking handovers, including any dropped from the bounded log.</summary>
    public int HandoverCount { get; private set; }

    /// <summary>Lifetime count of coverage requests raised. Diagnostics and tests.</summary>
    public int CoverageRequestsOpened { get; private set; }

    /// <summary>Lifetime count of coverage requests closed, by any reason. Diagnostics and tests.</summary>
    public int CoverageRequestsClosed { get; private set; }

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
            // A turnover invalidates every coverage request: the region it protected belonged to the
            // passage that just ended (A3, exit gate "turnovers void duties").
            CloseCoverage(TeamId.Home, CoverageRequestState.PassageEnded, tick, "turnover");
            CloseCoverage(TeamId.Away, CoverageRequestState.PassageEnded, tick, "turnover");
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

        // A6.3: one shared depth band for the line, from field position, passer pressure, credible runs and
        // the keeper's sweep cover. It is evaluated once per pass, before the keep loop can re-anchor a line
        // player, because a line that half-moved together is not a line.
        _markerCap = DefensiveLineModel.MarkerCount(profile);
        _handoversThisPass = 0;
        DefensiveLineBand band = inPossession
            ? DefensiveLineBand.None
            : DefensiveLineModel.Evaluate(team, players, ball, possession, opponents, profile, attackDirX, _pitch);
        _lineBands[team] = band;

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
                counts.GetValueOrDefault(existing.Job) < JobCapacity(existing.Job) &&
                Vector2.Distance(
                    new Vector2(player.Position.X, player.Position.Z),
                    new Vector2(existing.Anchor.X, existing.Anchor.Z)) <= MaxAssignmentTravel(existing.Job) + 6f)
            {
                counts[existing.Job] = counts.GetValueOrDefault(existing.Job) + 1;
                taken.Add(player.Id);

                // A6.3: a kept line duty still takes the band's depth. Keeping the old anchor would leave
                // one defender holding last pass's line while the rest moved, which is exactly the failure
                // the shared band exists to remove.
                if (existing.Job == TacticalJob.DefensiveLine && band.IsValid)
                {
                    float lineProj = DefensiveLineModel.ProjForPlayer(band, player, ballXZ);
                    Assign(player, existing.Job,
                        new Vector3(attackDirX * lineProj, 0f, existing.Anchor.Z),
                        existing.TargetPlayerId, existing.Reason, tick);
                    continue;
                }

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
        MaintainCoverage(team, players, formation, ballXZ, attackDirX, tick, inPossession,
            responderId, taken, available);

        float offsideLineProj = ComputeOffsideLineProj(opponents, ball, attackDirX);
        float referenceProj = referenceXZ.X * attackDirX;
        Vector2 ownGoalXZ = new(-attackDirX * _pitch.HalfLength, 0f);

        if (inPossession)
        {
            // A3.6: how exposed the side would be if the ball were lost here. One evaluation per pass;
            // the forward duties below are gated on it rather than on a fixed count behind the ball.
            _security[team] = TransitionSecurityModel.Evaluate(players, team, ball, opponents, attackDirX, _pitch);
            int forwardAllowance = _security[team].ForwardAllowance;
            int forwardCommitted = 0;

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

            // A depth run (a box run) only makes sense when there is space behind the line to attack,
            // *and* the side can afford to send somebody: the transition picture decides that, not a
            // fixed count behind the ball (A3.6).
            bool depthRunPossible = forwardCommitted < forwardAllowance &&
                                    offsideLineProj - referenceProj > 4f && offsideLineProj > 10f;
            if (depthRunPossible)
            {
                if (AssignUniqueJob(available, counts, taken, TacticalJob.DepthRun, opponents, referenceXZ, attackDirX, offsideLineProj, tick,
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
                    }, baseScore: 0.05f))
                {
                    forwardCommitted++;
                }
            }

            if (forwardCommitted < forwardAllowance &&
                AssignUniqueJob(available, counts, taken, TacticalJob.Overlap, opponents, referenceXZ, attackDirX, offsideLineProj, tick,
                player =>
                {
                    if (MathF.Abs(referenceXZ.Y) < 8f) return null;
                    if (player.Position.X * attackDirX > referenceProj - 1f) return null;
                    Vector2 target = new(
                        referenceXZ.X + attackDirX * 7f,
                        MathF.Sign(referenceXZ.Y) * wideZ);
                    return ClampToPitch(Onside(target, offsideLineProj - 1.5f, attackDirX));
                }))
            {
                forwardCommitted++;
            }
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

            // A6.3: how many opponents this profile marks man-for-man is a style decision, not a constant.
            AssignMarkers(players, available, counts, taken, opponents, ownGoalXZ, attackDirX, tick, formation);

            // A6.3: the remaining defenders hold one shared depth band — the whole line steps together,
            // each man in his own lane, with the ball-side and far-side exceptions the band allows.
            foreach (FootballerState player in available.ToArray())
            {
                if (player.Role is not (TacticalRole.CenterBack or TacticalRole.FullBack)) continue;

                Vector3 anchor = AnchorOf(player);
                if (band.IsValid)
                {
                    float lineProj = DefensiveLineModel.ProjForPlayer(band, player, ballXZ);
                    anchor = new Vector3(attackDirX * lineProj, 0f, anchor.Z);
                }

                Assign(player, TacticalJob.DefensiveLine, anchor, null, "Hold the defensive line", tick);
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
    /// A3.5: creates, maintains, hands back or expires the team's coverage request.
    ///
    /// A line player pulled more than <see cref="VacateDistanceMeters"/> out of the shape he was given,
    /// by the press job, leaves a gap behind him: the slot he owns, provided that slot is goal-side of
    /// the ball and inside play range. Nearby line players bid for it, the deterministic winner accepts,
    /// and the request stays live until it ends with a stated reason — the source is back in his line
    /// (handback), the press ended, the ball left the area, the hold expired, the owner left the pitch,
    /// or the passage turned over. One request per team is the cascade bound.
    /// </summary>
    private void MaintainCoverage(
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
        CoverageRequest request = _coverage[team];

        if (inPossession)
        {
            // We have the ball again: nothing about the old passage's gap is a duty any more.
            CloseCoverage(team, CoverageRequestState.PassageEnded, tick, "we have the ball again");
            return;
        }

        if (request.IsLive)
        {
            FootballerState? source = Find(players, request.SourcePlayerId);
            FootballerState? owner = Find(players, request.OwnerPlayerId);

            if (source is not { IsActiveOnPitch: true })
            {
                CloseCoverage(team, CoverageRequestState.PassageEnded, tick,
                    $"source #{request.SourcePlayerId} left the pitch");
            }
            else if (owner is not { IsActiveOnPitch: true })
            {
                CloseCoverage(team, CoverageRequestState.OwnerRemoved, tick,
                    $"owner #{request.OwnerPlayerId} left the pitch");
            }
            else if (!PressesStill(source.Id))
            {
                CloseCoverage(team, CoverageRequestState.PassageEnded, tick, "the press ended");
            }
            else if (tick - request.CreatedTick > MaxCoverageHoldTicks)
            {
                CloseCoverage(team, CoverageRequestState.Expired, tick, "the hold expired");
            }
            else if (PlanarDistance(new Vector2(source.Position.X, source.Position.Z), AnchorXZ(source)) <=
                     HandbackDistanceMeters)
            {
                CloseCoverage(team, CoverageRequestState.HandedBack, tick, $"#{source.Id} is back in his line");
            }
            else if (Vector2.Distance(ballXZ, new Vector2(request.Region.X, request.Region.Z)) >
                     MaxVacancyDistanceMeters)
            {
                CloseCoverage(team, CoverageRequestState.PassageEnded, tick, "the ball left the area");
            }
            else
            {
                // Sticky: the same owner keeps the same region while it is still vacated. The gap is the
                // slot he owns, and a slot does not move, so nothing jitters while he is away.
                Assign(owner, TacticalJob.ProtectGap, request.Region, null, request.Reason, tick,
                    sourcePlayerId: request.SourcePlayerId, temporary: true);
                taken.Add(owner.Id);
                available.RemoveAll(p => p.Id == owner.Id);
                return;
            }
        }

        Vacancy? vacancy = FindVacancy(team, players, formation, ballXZ, attackDirX);
        if (vacancy is not Vacancy gap) return;

        // Nearby bids, then the deterministic winner (best score, ties by lower player id).
        request.Open(gap.SourcePlayerId, gap.Region, tick, $"Protect #{gap.SourcePlayerId}'s vacated gap");
        CoverageRequestsOpened++;
        CollectBids(team, players, formation, gap, attackDirX, responderId, request);

        int winner = request.SelectWinner();
        FootballerState? protector = winner > 0 ? Find(players, winner) : null;
        if (protector is null || !protector.IsActiveOnPitch)
        {
            CloseCoverage(team, CoverageRequestState.NoBid, tick, "no eligible teammate could take the region");
            return;
        }

        request.Accept(winner, tick, request.Reason);
        Assign(protector, TacticalJob.ProtectGap, gap.Region, null, request.Reason, tick,
            sourcePlayerId: gap.SourcePlayerId, temporary: true);
        taken.Add(protector.Id);
        available.RemoveAll(p => p.Id == protector.Id);
    }

    /// <summary>Closes the team's request if it is open or live, counting only real closures.</summary>
    private void CloseCoverage(TeamId team, CoverageRequestState state, int tick, string reason)
    {
        CoverageRequest request = _coverage[team];
        if (!request.Close(state, tick, reason)) return;

        CoverageRequestsClosed++;

        // The owner's ProtectGap duty ends with the request. Leaving it in `_assignments` would bar him
        // from bidding for the next request in the same pass (a stale contextual duty, not a real one).
        if (_assignments.TryGetValue(request.OwnerPlayerId, out TeamJobAssignment duty) &&
            duty.Job == TacticalJob.ProtectGap)
        {
            _assignments.Remove(request.OwnerPlayerId);
            _assignedAtTick.Remove(request.OwnerPlayerId);
        }
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

    /// <summary>
    /// A3.5: collects the bids for a vacated region — every eligible line player who could slide across,
    /// with the score, the travel, the role fit and how near his own slot is. The request keeps them, so
    /// the accepted owner can be explained after the fact.
    /// </summary>
    private void CollectBids(
        TeamId team,
        IReadOnlyList<FootballerState> players,
        TeamFormation formation,
        Vacancy gap,
        float attackDirX,
        int? responderId,
        CoverageRequest request)
    {
        Vector2 regionXZ = new(gap.Region.X, gap.Region.Z);

        foreach (FootballerState player in players)
        {
            if (player.Team != team || !player.IsActiveOnPitch) continue;
            if (player.Id == gap.SourcePlayerId || player.Id == responderId) continue;

            if (player.Role is not (TacticalRole.CenterBack or TacticalRole.FullBack or TacticalRole.DefensiveMidfielder))
                continue;

            // A player holding a contextual duty of his own does not bid: a marker is on his man, a
            // receiver is collecting, and the current owner of a coverage request is already covering.
            // A line or shape duty *is* what the mechanism is allowed to rearrange. (The responder is
            // excluded by id and the keeper by role.)
            if (_assignments.TryGetValue(player.Id, out TeamJobAssignment committed) &&
                committed.Job is TacticalJob.Marker or TacticalJob.BallResponder or TacticalJob.ProtectGap)
            {
                continue;
            }

            Vector2 playerXZ = new(player.Position.X, player.Position.Z);
            float travel = Vector2.Distance(playerXZ, regionXZ);
            if (travel > MaxProtectorTravelMeters) continue;

            float roleFit = RoleFit(TacticalJob.ProtectGap, player.Role);
            float ownRegionDistance = 0f;
            if (formation.TryGetWorldAnchor(player.Id, attackDirX, out Vector3 ownSlot))
            {
                ownRegionDistance = Vector2.Distance(new Vector2(ownSlot.X, ownSlot.Z), regionXZ);
            }

            // A line player whose own slot is near the gap slides along the line instead of abandoning
            // the far side of it, so proximity of his *slot* is worth as much as proximity of his body.
            float score = roleFit * 1.25f +
                          (1f - travel / MaxProtectorTravelMeters) * 0.8f +
                          Math.Clamp(1f - ownRegionDistance / 25f, 0f, 1f) * 0.5f;

            request.AddBid(new CoverageBid(player.Id, score, travel, roleFit, ownRegionDistance));
        }
    }

    private void AssignMarkers(
        IReadOnlyList<FootballerState> players,
        List<FootballerState> available,
        Dictionary<TacticalJob, int> counts,
        HashSet<int> taken,
        TeamBelief opponents,
        Vector2 ownGoalXZ,
        float attackDirX,
        int tick,
        TeamFormation formation)
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

        foreach (TeamBelief.Opponent threat in threats)
        {
            // A handover transfers an existing responsibility, so a full marking budget must not hide
            // it: check the held marker before the cap that only governs *new* marking assignments.
            if (TryFindHeldMarker(threat.PlayerId, players, taken, out FootballerState? incumbent))
            {
                TryHandOverMarking(threat, incumbent!, players, available, counts, taken,
                    formation, ownGoalXZ, attackDirX, tick);
                continue;
            }

            if (counts.GetValueOrDefault(TacticalJob.Marker) >= _markerCap) continue;

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

            if (best is null) continue;

            Assign(best, TacticalJob.Marker, anchor, threat.PlayerId, $"Mark #{threat.PlayerId}", tick);
            taken.Add(best.Id);
            available.Remove(best);
            counts[TacticalJob.Marker] = counts.GetValueOrDefault(TacticalJob.Marker) + 1;
        }
    }

    /// <summary>
    /// A6.3: hands a threat over from his held marker to the defender whose *zone* now owns him.
    ///
    /// A held marker runs with the man he picked up, so the two stay close and a distance test would never
    /// move the job — but responsibility for a threat belongs to the defender whose place in the formation is
    /// nearest to him, not to whoever happened to pick him up first. When the threat crosses far enough into
    /// another slot — by <see cref="HandoverZoneMarginMeters"/> and within <see cref="MaxZoneHandoverMeters"/>
    /// — the responsibility is transferred explicitly: the new owner is assigned the marking job, the old
    /// marker is released back into the shape on the same pass, and the event is recorded with both slots'
    /// distances so the handover can be explained after the fact. At most
    /// <see cref="MaxHandoversPerPass"/> per pass, so the line absorbs one change at a time.
    /// </summary>
    /// <summary>
    /// The held marker of one threat, found in player order so the scan cannot depend on hash order. Only a
    /// player still in <paramref name="taken"/> holds a job in this pass; anyone else is being re-selected.
    /// </summary>
    private bool TryFindHeldMarker(
        int threatPlayerId,
        IReadOnlyList<FootballerState> players,
        HashSet<int> taken,
        out FootballerState? holder)
    {
        holder = null;
        for (int i = 0; i < players.Count; i++)
        {
            FootballerState state = players[i];
            if (!state.IsActiveOnPitch || !taken.Contains(state.Id)) continue;
            if (!_assignments.TryGetValue(state.Id, out TeamJobAssignment duty)) continue;
            if (duty.Job != TacticalJob.Marker || duty.TargetPlayerId != threatPlayerId) continue;

            holder = state;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A6.3: hands a threat over from his held marker to the defender whose *zone* now owns him.
    ///
    /// A held marker runs with the man he picked up, so the two stay close and a distance test would never
    /// move the job — but responsibility for a threat belongs to the defender whose place in the formation is
    /// nearest to him, not to whoever happened to pick him up first. When the threat crosses far enough into
    /// another slot — by <see cref="HandoverZoneMarginMeters"/> and within <see cref="MaxZoneHandoverMeters"/>
    /// — the responsibility is transferred explicitly: the new owner is assigned the marking job, the old
    /// marker is released back into the shape on the same pass, and the event is recorded with both slots'
    /// distances so the handover can be explained after the fact. At most
    /// <see cref="MaxHandoversPerPass"/> per pass, so the line absorbs one change at a time.
    /// </summary>
    private bool TryHandOverMarking(
        TeamBelief.Opponent threat,
        FootballerState incumbent,
        IReadOnlyList<FootballerState> players,
        List<FootballerState> available,
        Dictionary<TacticalJob, int> counts,
        HashSet<int> taken,
        TeamFormation formation,
        Vector2 ownGoalXZ,
        float attackDirX,
        int tick)
    {
        if (_handoversThisPass >= MaxHandoversPerPass) return false;
        if (!formation.TryGetWorldAnchor(incumbent.Id, attackDirX, out Vector3 incumbentSlot)) return false;

        float incumbentSlotDistance = Vector2.Distance(
            new Vector2(incumbentSlot.X, incumbentSlot.Z), threat.PositionXZ);

        // The zone owner is normally a defender *on the line* — held this pass, so not in `available`. The
        // handover is allowed to take him off the line (the released marker takes his place on it), which is
        // what "pick him up" means; anyone with a contextual duty of his own is not a candidate.
        FootballerState? owner = null;
        bool ownerOnTheLine = false;
        float ownerSlotDistance = float.MaxValue;
        for (int i = 0; i < players.Count; i++)
        {
            FootballerState candidate = players[i];
            if (!candidate.IsActiveOnPitch || candidate.Id == incumbent.Id) continue;
            if (candidate.Role is not (TacticalRole.CenterBack or TacticalRole.FullBack or TacticalRole.DefensiveMidfielder))
                continue;

            bool free = available.Contains(candidate);
            bool onTheLine = false;
            if (!free)
            {
                if (!taken.Contains(candidate.Id)) continue;
                if (!_assignments.TryGetValue(candidate.Id, out TeamJobAssignment duty)) continue;
                if (duty.Job is not (TacticalJob.DefensiveLine or TacticalJob.Shape)) continue;
                onTheLine = true;
            }

            if (!formation.TryGetWorldAnchor(candidate.Id, attackDirX, out Vector3 slot)) continue;

            float distance = Vector2.Distance(new Vector2(slot.X, slot.Z), threat.PositionXZ);
            if (distance > MaxZoneHandoverMeters) continue;
            if (distance >= ownerSlotDistance - 0.05f) continue;

            ownerSlotDistance = distance;
            owner = candidate;
            ownerOnTheLine = onTheLine;
        }

        if (owner is null) return false;
        if (ownerSlotDistance + HandoverZoneMarginMeters >= incumbentSlotDistance) return false;

        // The old marker goes back into the shape this pass; the new owner takes the man.
        _assignments.Remove(incumbent.Id);
        _assignedAtTick.Remove(incumbent.Id);
        taken.Remove(incumbent.Id);
        available.Add(incumbent);

        if (ownerOnTheLine)
        {
            _assignments.Remove(owner.Id);
            _assignedAtTick.Remove(owner.Id);
            taken.Remove(owner.Id);
        }
        else
        {
            available.Remove(owner);
        }

        Vector2 threatXZ = threat.PositionXZ;
        Vector2 goalSide = SimMath.SafeNormalize(ownGoalXZ - threatXZ, new Vector2(-attackDirX, 0f));
        Vector3 anchor = new(threatXZ.X + goalSide.X * 1.9f, 0f, threatXZ.Y + goalSide.Y * 1.9f);

        Assign(owner, TacticalJob.Marker, anchor, threat.PlayerId,
            $"Mark #{threat.PlayerId} (handed over from #{incumbent.Id})", tick);
        taken.Add(owner.Id);
        counts[TacticalJob.Marker] = counts.GetValueOrDefault(TacticalJob.Marker) + 1;
        _handoversThisPass++;

        RecordHandover(new MarkingHandover(
            tick, threat.PlayerId, incumbent.Id, owner.Id, incumbentSlotDistance, ownerSlotDistance));
        return true;
    }

    /// <summary>Keeps the bounded handover log in order, dropping the oldest entry past its cap.</summary>
    private void RecordHandover(MarkingHandover handover)
    {
        HandoverCount++;
        if (_handovers.Count >= MaxHandoverLogEntries) _handovers.RemoveAt(0);
        _handovers.Add(handover);
    }

    private bool AssignUniqueJob(
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
        if (counts.GetValueOrDefault(job) >= MaxPerJob(job)) return false;

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

        if (best is null) return false;

        // Then slide the chosen target inside a bounded neighbourhood of where the duty wants it. The
        // probe is deliberately small: a job target can be refined by metres, not re-invented, and it
        // only moves when it scores better by a margin.
        Vector2 anchor = RefineJobRegion(job, bestAnchor, referenceXZ, best.Id, opponents, attackDirX, offsideLineProj, _attackingWidth01);

        Assign(best, job, new Vector3(anchor.X, 0f, anchor.Y), null, JobReason(job), tick);
        taken.Add(best.Id);
        available.Remove(best);
        counts[job] = counts.GetValueOrDefault(job) + 1;
        return true;
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
        float nominalSeparation = JobRegionScoring.NearestTeammateDistance(nominal, _assignedRegions, playerId);

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

            // A probe may move the target, but it may not slide it onto a teammate's target: below this
            // floor the staggering term alone can lose to connection and lane openness, and the duty is
            // then no longer a position of its own.
            if (nominalSeparation >= JobRegionScoring.MinRefinedSeparationMeters &&
                JobRegionScoring.NearestTeammateDistance(candidate, _assignedRegions, playerId) <
                JobRegionScoring.MinRefinedSeparationMeters)
            {
                continue;
            }

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
        TacticalJob.Marker => 3,
        _ => 1
    };

    /// <summary>
    /// How many players may hold a job in this pass. Markers are the one duty whose number is a style
    /// choice (A6.3): the profile's zone/man mix sets it at the start of the pass, and the structural
    /// maximum in <see cref="MaxPerJob"/> is only the ceiling that mix can reach.
    /// </summary>
    private int JobCapacity(TacticalJob job) =>
        job == TacticalJob.Marker ? _markerCap : MaxPerJob(job);

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
    /// temporary responsibility, whose life is owned by <see cref="MaintainCoverage"/> and must
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
