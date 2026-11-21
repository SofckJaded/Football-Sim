using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Tactics;

namespace FootballSim.AI;

/// <summary>
/// A8.4: one recognized short trap on a carrier pinned to a touchline, as the defending team's shared picture
/// describes it.
///
/// The record exists so the trap is <b>explainable</b> rather than a single opaque decision: who does what
/// (press, screen, cover), the points the trap asks them to take, how strongly each of the plan's facts holds,
/// and the confidence the whole picture earns. It carries only belief: the carrier's believed position, the
/// believed threats a screen can block, and the participants' own exact positions. No opponent velocity,
/// intent or attribute is read.
/// </summary>
/// <param name="Kind">Which short trap this is; A8.4 ships the touchline trap.</param>
/// <param name="CarrierId">The opponent in possession the trap is built around.</param>
/// <param name="CarrierXZ">The carrier's believed position when the trap is recognized (the trap point).</param>
/// <param name="PresserId">The teammate who closes the carrier down (the elected ball responder).</param>
/// <param name="PressOffsetXZ">Where the presser stands, relative to the carrier (the angle that funnels him
/// toward the line and into the screen). Applied to the player's own believed carrier each decision, so the
/// trap names an angle rather than a stale absolute point.</param>
/// <param name="ScreenId">The teammate who blocks the carrier's best believed forward pass (the try's screener).</param>
/// <param name="ScreenOffsetXZ">Where the screen stands, relative to the carrier.</param>
/// <param name="CoverId">The teammate the allocator already made the cover behind the press; the trap never
/// raises without one, which is what makes "the trap preserves cover" structural.</param>
/// <param name="Pressure01">How tight the press is inside <see cref="TrapPolicy.TrapPresserEngageMeters"/>.</param>
/// <param name="Touchline01">How pinned to the line the carrier is, on the one touchline scale.</param>
/// <param name="CoverFit01">How close the cover already stands to the depth a trap wants behind the press.</param>
/// <param name="Screen01">How dangerous the believed forward option the screen stands on is.</param>
/// <param name="Confidence01">The weighted combination of the four terms; the trap is only raised above
/// <see cref="TrapPolicy.MinTrapConfidence01"/>.</param>
public readonly record struct TrapOpportunity(
    TrapKind Kind,
    int CarrierId,
    Vector2 CarrierXZ,
    int PresserId,
    Vector2 PressOffsetXZ,
    int ScreenId,
    Vector2 ScreenOffsetXZ,
    int CoverId,
    float Pressure01,
    float Touchline01,
    float CoverFit01,
    float Screen01,
    float Confidence01)
{
    /// <summary>True when the shared picture supports raising this trap.</summary>
    public bool IsRecognized => Confidence01 > 0f;

    /// <summary>Compact text form for a trace line, a probe or a test failure message.</summary>
    public string Describe() =>
        $"trap #{CarrierId} press #{PresserId} screen #{ScreenId} cover #{CoverId} conf {Confidence01:F2}" +
        $" (pressure {Pressure01:F2}, line {Touchline01:F2}, cover {CoverFit01:F2}, screen {Screen01:F2})" +
        $" press off ({PressOffsetXZ.X:F1},{PressOffsetXZ.Y:F1}) screen off" +
        $" ({ScreenOffsetXZ.X:F1},{ScreenOffsetXZ.Y:F1})";
}

/// <summary>
/// A8.4: the short trap contract, as the defending team's own shared picture recognizes it.
///
/// <b>A contract, not a capability.</b> The trap adds no reach, speed, contact rule or candidate: it names
/// participants and points inside the vocabulary A6.1/A6.2 already own (press, screen, cover) and asks each
/// participant to take his point. The presser still closes the carrier down through the same locomotion and
/// the same tackle arbitration, the screener still offers the same <see cref="PressAction.Screen"/> act as a
/// bounded preference that his duty can outbid, and the cover is the allocator's own cover, not a new job.
///
/// <b>Why the team elects it.</b> The trap is a local fact (a carrier pinned on a line) whose answer only
/// works if three players answer it together: the presser must close from the only side that leaves the line
/// as the second defender, the screener must stand on the one pass that gets the carrier out, and the cover
/// must stay behind the press. Deciding that independently, per player, per tick, is exactly what produced
/// three uncoordinated presses. The election runs from the same shared inputs A6.1's second man uses: the
/// team's own exact player positions, its <see cref="TeamBelief"/> picture of the opponents, the match's
/// elected ball responder and the allocator's own cover assignment.
///
/// <b>The five gates the plan names.</b>
/// <list type="bullet">
/// <item><b>Pressure</b> — the elected responder is close enough for the press to be set
/// (<see cref="TrapPresserEngageMeters"/>); a trap that starts with the presser ten metres away is not a trap.</item>
/// <item><b>The line</b> — the carrier is inside the touchline band (<see cref="TrapTouchline01Gate"/>, on
/// A6.2's own touchline scale), so the line is the second defender the trap plays off.</item>
/// <item><b>Cover</b> — the allocator has a cover behind the press, goal-side of the presser and inside
/// A6.2's <see cref="PressPolicy.CoverLinkMeters"/>. Without it the trap is refused: the milestone is
/// "extend basic press coordination", and the one thing that must survive it is the cover.</item>
/// <item><b>Screening</b> — the carrier has a believed forward option worth blocking, and there is a defender
/// close enough to stand on the lane (<see cref="TrapScreenEngageMeters"/>).</item>
/// <item><b>Picture</b> — the carrier's belief is fresh enough to coordinate on (<see cref="TrapMaxCarrierAgeTicks"/>)
/// and the composite confidence clears <see cref="MinTrapConfidence01"/>. A stale or thin picture never buys a
/// trap.</item>
/// </list>
///
/// <b>Determinism and cost.</b> Everything is read from the match's shared, already-built inputs: no RNG is
/// touched, players are scanned in list order and the belief in ascending player id, ties keep the first
/// candidate, and nothing is allocated.
/// </summary>
public static class TrapPolicy
{
    /// <summary>
    /// Furthest the elected presser may be from the carrier for the press to count as "set", in metres. Inside
    /// it the presser can reach contact range within A6.2's standing depth in about a second; beyond it the
    /// trap would be announced before it exists.
    /// </summary>
    public const float TrapPresserEngageMeters = 8f;

    /// <summary>
    /// How close to the touchline the carrier must be, on A6.2's own touchline scale
    /// (<see cref="PressPolicy.Touchline01"/>, zero at ten metres). 0.30 keeps the carrier inside seven metres
    /// of the line, where the line is genuinely the second defender.
    /// </summary>
    public const float TrapTouchline01Gate = 0.30f;

    /// <summary>
    /// Oldest carrier belief a trap may be raised from, in ticks (0.4 s). A trap coordinates three players
    /// around where the carrier is; a stale picture would coordinate them around where he was.
    /// </summary>
    public const int TrapMaxCarrierAgeTicks = 24;

    /// <summary>
    /// Furthest a defender may be from the screen point and still take the screen role, in metres. A trap is a
    /// short commitment, so the man named has to be able to be on the lane while it lives: this is roughly
    /// 2.5 s of travel, and the screen starts counting from the moment he is told.
    /// </summary>
    public const float TrapScreenEngageMeters = 15f;

    /// <summary>
    /// How far the carrier must move from the trap point before the trap counts as <b>escaped</b>, in metres.
    /// Short on purpose: a touchline trap is a small region; the next few metres are the escape.
    /// </summary>
    public const float TrapEscapeMeters = 6.5f;

    /// <summary>Composite confidence below which no trap is raised. Gates before weights, then one floor.</summary>
    public const float MinTrapConfidence01 = 0.30f;

    /// <summary>
    /// Preference the trap's named screener earns on the existing Screen act, on the action-value scale. A
    /// bounded nudge like A8.1's continuation bonus: the screen has to beat his own duty, and a blocked lane or
    /// a better duty still wins. This is the screen's entire mechanical contribution.
    /// </summary>
    public const float ScreenPreference = 0.14f;

    /// <summary>Weight of the press on the carrier in the trap confidence.</summary>
    public const float WeightPressure = 0.30f;

    /// <summary>Weight of the touchline in the trap confidence (the line is the second defender).</summary>
    public const float WeightTouchline = 0.25f;

    /// <summary>Weight of the cover's depth in the trap confidence.</summary>
    public const float WeightCover = 0.20f;

    /// <summary>Weight of the screened lane's danger in the trap confidence.</summary>
    public const float WeightScreen = 0.25f;

    /// <summary>
    /// Depth error at which the cover-fit term reaches zero, in metres: far enough to let a cover at the
    /// normal A6.2 depth score well, tight enough that a cover standing on the presser's heels scores poorly.
    /// </summary>
    public const float CoverFitSpanMeters = 10f;

    /// <summary>
    /// Recognizes a short touchline trap from the defending team's shared picture. Returns true and fills
    /// <paramref name="opportunity"/> only when every gate holds and the composite confidence clears the floor.
    /// </summary>
    /// <param name="team">The defending team the trap would belong to.</param>
    /// <param name="players">Every player in the match; the team's own are read exactly (a team knows its own
    /// players), opponents only through <paramref name="opponents"/>.</param>
    /// <param name="opponents">The team's shared picture of its opponents (A2.6): never live opponent state.</param>
    /// <param name="possession">Authoritative ball ownership, used only to establish who has the ball.</param>
    /// <param name="presserId">The match's elected ball responder, or null when nobody has been elected.</param>
    /// <param name="supportPresserId">The match's elected second man (A6.1), or null. He is already committed to
    /// stepping in beside the press, so the trap does not also name him its screener: one defender, one job.</param>
    /// <param name="jobs">The allocator's assignments. The trap reads them for the two roles it may not invent:
    /// the cover it needs behind the press, and the shape-duty player it may ask to screen. A man the allocator
    /// has committed to a line, a mark, a gap or the cover is never re-pointed by a short trap.</param>
    /// <param name="humanPlayerId">The locked human, never named in a trap: the AI does not move or re-point him.</param>
    /// <param name="attackDirX">The defending team's attacking direction (the trap plays off the opposite goal).</param>
    /// <param name="compactness01">The team profile's compactness, which sets the preferred cover depth.</param>
    /// <param name="pitch">Pitch geometry.</param>
    public static bool TryEvaluate(
        TeamId team,
        IReadOnlyList<FootballerState> players,
        TeamBelief opponents,
        BallPossessionSnapshot possession,
        int? presserId,
        int? supportPresserId,
        TeamJobCoordinator jobs,
        int? humanPlayerId,
        float attackDirX,
        float compactness01,
        PitchDimensions pitch,
        out TrapOpportunity opportunity)
    {
        opportunity = default;

        // Gate 1 -- the picture: an opponent actually controls the ball, and the team believes in him freshly
        // enough to coordinate around him. A keeper in possession is not a trap: his distribution is A6.5b's.
        if (possession.State != BallControlState.Controlled) return false;
        if (possession.PossessionTeam is not TeamId holder || holder == team) return false;
        if (possession.ControllingPlayerId is not int carrierId || carrierId <= 0) return false;
        if (!TryFindBelieved(opponents, carrierId, out TeamBelief.Opponent carrier)) return false;
        if (carrier.AgeTicks > TrapMaxCarrierAgeTicks) return false;
        if (carrier.Role == TacticalRole.Goalkeeper) return false;

        Vector2 carrierXZ = carrier.PositionXZ;

        // Gate 2 -- the line: the carrier is pinned to a touchline, on A6.2's one touchline scale.
        float touchline01 = PressPolicy.Touchline01(carrierXZ, pitch);
        if (touchline01 < TrapTouchline01Gate) return false;

        // Gate 3 -- pressure: the elected responder is close enough that the press is set, and he is a player
        // the AI actually drives (never the locked human).
        if (presserId is not int presser || presser <= 0) return false;
        if (humanPlayerId is int human && presser == human) return false;
        if (!TryGetPlayer(players, presser, out FootballerState presserPlayer)) return false;
        if (presserPlayer.Team != team || !presserPlayer.IsActiveOnPitch) return false;
        if (presserPlayer.Role == TacticalRole.Goalkeeper) return false;

        Vector2 presserXZ = new(presserPlayer.Position.X, presserPlayer.Position.Z);
        float presserDistance = Vector2.Distance(presserXZ, carrierXZ);
        if (presserDistance > TrapPresserEngageMeters) return false;

        Vector2 ownGoalXZ = new(-attackDirX * pitch.HalfLength, 0f);
        Vector2 goalSide = SimMath.SafeNormalize(
            ownGoalXZ - carrierXZ, new Vector2(-attackDirX, 0f));

        // Gate 4 -- cover: the allocator's cover stands behind the press and can be linked to it. The trap
        // declines to raise without cover rather than trading it away: the exit gate is that a trap preserves
        // cover, not that it creates one.
        if (!TryFindCover(players, jobs, team, presser, humanPlayerId, out int coverId,
                out Vector2 coverXZ))
        {
            return false;
        }

        if (Vector2.Distance(coverXZ, presserXZ) > PressPolicy.CoverLinkMeters) return false;
        float coverBehind = Vector2.Dot(coverXZ - presserXZ, goalSide);
        if (coverBehind <= PressPolicy.CoverBehindMarginMeters) return false;

        // Gate 5 -- screening: the carrier has a believed forward option worth blocking, and a shape-duty
        // defender can take the lane. The screen point is the same shape PressPolicy.TryFindScreen uses
        // (fraction of the carrier-to-receiver lane, danger weighted by centrality); here it is computed from
        // the shared belief so every participant means the same point.
        if (!TryFindScreenPoint(opponents, carrierId, carrierXZ, goalSide, pitch,
                out Vector2 screenXZ, out float screenDanger01))
        {
            return false;
        }

        if (!TryFindScreener(players, jobs, team, humanPlayerId, presser, supportPresserId ?? 0, coverId,
                screenXZ, out int screenId))
        {
            return false;
        }

        // The terms, then the composite floor. Gates are gates; only inside them are the facts weighted.
        float pressure01 = Clamp01(1f - presserDistance / TrapPresserEngageMeters);
        float preferredDepth = PressPolicy.CoverBaseDepthMeters +
                               PressPolicy.CoverDepthSpreadMeters * Clamp01(compactness01);
        float coverFit01 = Clamp01(1f - MathF.Abs(coverBehind - preferredDepth) / CoverFitSpanMeters);
        if (screenDanger01 <= 0f) return false;

        float confidence = Math.Clamp(
            WeightPressure * pressure01 +
            WeightTouchline * touchline01 +
            WeightCover * coverFit01 +
            WeightScreen * screenDanger01,
            0f, 1f);
        if (confidence < MinTrapConfidence01) return false;

        // The presser's angle is A6.2's own angled-pressure scoring, evaluated once for the whole team from the
        // shared picture; the participants apply the resulting offset to their own believed carrier position.
        Vector2 pressTarget = PressPolicy.AngledPressPoint(
            carrierXZ, carrier.VelocityXZ, presserXZ, touchline01, attackDirX, pitch,
            PressPolicy.PressStandoffMeters, support: false);
        Vector2 pressOffset = pressTarget - carrierXZ;
        Vector2 screenOffset = screenXZ - carrierXZ;
        if (!SimMath.IsFinite(pressOffset) || !SimMath.IsFinite(screenOffset)) return false;

        opportunity = new TrapOpportunity(
            TrapKind.Touchline, carrierId, carrierXZ, presser, pressOffset, screenId, screenOffset, coverId,
            pressure01, touchline01, coverFit01, screenDanger01, confidence);
        return true;
    }

    /// <summary>
    /// True when the trapped carrier has left the trap's region: the escape condition the contract is written
    /// against. A pure geometry answer — the caller decides whether the ball is still his.
    /// </summary>
    public static bool IsEscaped(in TrapContract contract, Vector2 believedCarrierXZ) =>
        Vector2.Distance(believedCarrierXZ, contract.CarrierXZ) > TrapEscapeMeters;

    // ---------------------------------------------------------------------------------------------
    // Internals.
    // ---------------------------------------------------------------------------------------------

    private static bool TryFindBelieved(TeamBelief opponents, int playerId, out TeamBelief.Opponent believed)
    {
        IReadOnlyList<TeamBelief.Opponent> list = opponents.Opponents;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].PlayerId != playerId) continue;
            believed = list[i];
            return true;
        }

        believed = default;
        return false;
    }

    /// <summary>
    /// The dangerous forward option the screen should block, from the shared belief: the believed opponent
    /// furthest goal-side of the carrier, scored by distance up the pitch and centrality. Same expression as
    /// <see cref="PressPolicy.TryFindScreen"/>, from the team's belief rather than one player's sight.
    /// </summary>
    private static bool TryFindScreenPoint(
        TeamBelief opponents,
        int carrierId,
        Vector2 carrierXZ,
        Vector2 goalSide,
        PitchDimensions pitch,
        out Vector2 screenXZ,
        out float danger01)
    {
        screenXZ = default;
        danger01 = 0f;

        IReadOnlyList<TeamBelief.Opponent> list = opponents.Opponents;
        float bestDanger = 0f;
        Vector2 bestReceiver = default;

        for (int i = 0; i < list.Count; i++)
        {
            TeamBelief.Opponent entity = list[i];
            if (entity.PlayerId == carrierId) continue;
            if (entity.Role == TacticalRole.Goalkeeper) continue;

            Vector2 receiverXZ = entity.PositionXZ;
            float forward = Vector2.Dot(receiverXZ - carrierXZ, goalSide);
            if (forward < PressPolicy.ScreenMinForwardMeters) continue;

            float centrality = 1f - 0.35f * Clamp01(MathF.Abs(receiverXZ.Y) / pitch.HalfWidth);
            float danger = Clamp01(forward / PressPolicy.ScreenDangerMeters) * centrality;
            if (danger <= bestDanger + 1e-5f) continue;

            bestDanger = danger;
            bestReceiver = receiverXZ;
        }

        if (bestDanger <= 0f) return false;

        screenXZ = ClampToPitch(
            carrierXZ + (bestReceiver - carrierXZ) * PressPolicy.ScreenFraction, pitch);
        danger01 = bestDanger;
        return true;
    }

    /// <summary>
    /// The allocator's cover behind the press, if it assigned one. The trap does not pick a cover: the milestone
    /// extends the existing pressure-cover balance, so it reads the <see cref="TacticalJob.CoverDefender"/>
    /// assignment the allocator made (and never a temporary responsibility, which protects a vacated slot).
    /// </summary>
    private static bool TryFindCover(
        IReadOnlyList<FootballerState> players,
        TeamJobCoordinator jobs,
        TeamId team,
        int presserId,
        int? humanPlayerId,
        out int coverId,
        out Vector2 coverXZ)
    {
        coverId = 0;
        coverXZ = default;

        for (int i = 0; i < players.Count; i++)
        {
            FootballerState player = players[i];
            if (player.Team != team || !player.IsActiveOnPitch) continue;
            if (player.Id == presserId) continue;
            if (humanPlayerId is int human && player.Id == human) continue;
            if (player.Role == TacticalRole.Goalkeeper) continue;

            TeamJobAssignment? assignment = jobs.Get(player.Id);
            if (assignment is not { Job: TacticalJob.CoverDefender } cover || cover.IsTemporary) continue;

            coverId = player.Id;
            coverXZ = new Vector2(player.Position.X, player.Position.Z);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The nearest eligible defender to the screen point, excluding the presser (already committing), the
    /// elected second man (committed to stepping in beside the press), the cover (whose role is to stay behind)
    /// and the locked human (the AI never re-points him). Nearest-to-the-point rather than nearest-to-the-
    /// carrier: the man who can actually be on the lane in time is the screener.
    ///
    /// <b>Only a shape-duty player may be named.</b> The screener's ordinary duty has to be one the bounded
    /// screen preference can legitimately outbid, so the trap may only ask a player whose assignment is the
    /// formation shape (which is exactly the population A6.1's own screen act serves). A man the allocator has
    /// committed to a line, a mark, a gap or the cover keeps that duty: the trap does not become a second
    /// allocator by re-pointing him.
    /// </summary>
    private static bool TryFindScreener(
        IReadOnlyList<FootballerState> players,
        TeamJobCoordinator jobs,
        TeamId team,
        int? humanPlayerId,
        int presserId,
        int supportPresserId,
        int coverId,
        Vector2 screenXZ,
        out int screenId)
    {
        screenId = 0;
        float bestDistance = TrapScreenEngageMeters;

        for (int i = 0; i < players.Count; i++)
        {
            FootballerState player = players[i];
            if (player.Team != team || !player.IsActiveOnPitch) continue;
            if (player.Id == presserId || player.Id == coverId) continue;
            if (supportPresserId > 0 && player.Id == supportPresserId) continue;
            if (humanPlayerId is int human && player.Id == human) continue;
            if (player.Role == TacticalRole.Goalkeeper) continue;

            TeamJobAssignment? assignment = jobs.Get(player.Id);
            if (assignment is not { Job: TacticalJob.Shape }) continue;

            Vector2 positionXZ = new(player.Position.X, player.Position.Z);
            float distance = Vector2.Distance(positionXZ, screenXZ);
            if (distance > bestDistance) continue;
            if (distance >= bestDistance - 1e-4f && screenId != 0 && player.Id >= screenId) continue;

            bestDistance = distance;
            screenId = player.Id;
        }

        return screenId > 0;
    }

    private static bool TryGetPlayer(
        IReadOnlyList<FootballerState> players, int playerId, out FootballerState player)
    {
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].Id != playerId) continue;
            player = players[i];
            return true;
        }

        player = null!;
        return false;
    }

    private static Vector2 ClampToPitch(Vector2 pointXZ, PitchDimensions pitch) => new(
        Math.Clamp(pointXZ.X, -pitch.HalfLength + 1f, pitch.HalfLength - 1f),
        Math.Clamp(pointXZ.Y, -pitch.HalfWidth + 1f, pitch.HalfWidth - 1f));

    private static float Clamp01(float value) =>
        !float.IsFinite(value) ? 0f : value < 0f ? 0f : value > 1f ? 1f : value;
}
