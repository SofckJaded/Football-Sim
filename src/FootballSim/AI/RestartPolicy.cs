using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Tactics;

namespace FootballSim.AI;

/// <summary>
/// What the restarting team's shared picture says the routine should be: the restart kind it belongs to, the
/// option chosen from the lawful menu, the three named roles and the receiver that option names.
///
/// It is a plan, not a contract: <see cref="RestartCoordinator"/> raises it as the
/// <see cref="RestartRoutine"/> the players see, and the taker then plays it if the delivery to that receiver
/// can actually be planned from his own picture. A plan that cannot be honoured falls back to the taker's own
/// A4.9 pre-routine choice, so a routine never strands a restart.
/// </summary>
public readonly record struct RestartPlan(
    RestartKind Kind,
    RestartOption Option,
    TeamId Team,
    int TakerId,
    int SupportId,
    int TargetId,
    Vector2 ReceiverXZ,
    float Confidence01,
    float ReceiverSpace01,
    float Progression01)
{
    /// <summary>The player the chosen option is played to, or 0 when that option named nobody.</summary>
    public int ReceiverId => Option == RestartOption.Short ? SupportId : TargetId;
}

/// <summary>
/// The small library of legal restart routines (A8.5) and the one election that picks among them.
///
/// <b>What the rules engine already owns, and what is left.</b> The rules engine places the ball at the lawful
/// spot, decides which restart is in force, designates the taker, pushes the opponents back and arms the Law 11
/// semantics — none of that is duplicated here. What it does not answer is <i>how</i> the restart is taken, and
/// that used to be a per-player heuristic inside the taker: "the most advanced team-mate whose ground route is
/// open, else the most advanced one at all". This type is the team's answer instead: from its own exact
/// positions and its <see cref="TeamBelief"/> of the opponents it chooses between the two lawful options the
/// menu holds, and names a support and a target for the taker to play to.
///
/// <b>The two options.</b>
/// <list type="bullet">
/// <item><b>Short</b> — the support player's feet: the nearest lawful receiver inside the kind's own support
/// bound. A short corner, a throw-in played backwards, a square kick-off, a defender dropping for a goal kick.</item>
/// <item><b>Deliver</b> — the target player, further up: the lawful receiver that best combines progression with
/// space outside the short bound. A throw down the line, a free kick's delivery into the area, a long goal kick,
/// a kick-off played long.</item>
/// </list>
/// The choice is a bounded, stated weight between them (space around the receiver, how short the short option
/// really is, how far up the target is) plus the team profile's own directness — or, for a goal kick, the
/// profile's <see cref="TeamTacticalProfile.ShortGoalkeeperDistribution"/>, because A6.5b already owns that
/// preference and a second one would be a competing model. The menu collapses to one option when the picture only
/// offers one — a kick-off's nearest team-mate is also its most advanced man in this match's own setup, so that
/// restart is a short ball, not a choice.
///
/// <b>The law model.</b> A receiver is only eligible when the restart's own Law 11 semantics allow him: for a
/// throw-in, a goal kick or a corner kick there is no offside offence, so anyone lawful is eligible; for a
/// kick-off, a free kick or an indirect free kick the receiver must be onside by the same believed line every
/// other pass in the match keeps (<see cref="OffsideTactics.BelievedLineProjX(TeamBelief, float, float, float)"/>
/// with <see cref="OffsideTactics.MarginMeters"/> in reserve). That gate is the whole reason this is a
/// <i>legal</i> routine library rather than a preference: the old taker heuristic never asked it, so a free
/// kick could be delivered to a man standing beyond the line and turned into an offside offence against his own
/// team. The rules engine remains the only authority for the placement, the taker and the restart state; this
/// type only refuses a receiver the law would not allow.
///
/// <b>Determinism and cost.</b> A few list-ordered scans of the squad, no allocation, no RNG, ties keep the first
/// candidate. The election runs only while a restart is armed — a handful of ticks per stoppage.
/// </summary>
public static class RestartPolicy
{
    /// <summary>Closest a receiver may stand to the ball, in metres. Inside this the ball is the taker's feet.</summary>
    public const float MinReceiverMeters = 3.5f;

    /// <summary>Furthest a routine may name a receiver, in metres: the taker's own delivery range, with room for
    /// the keeper's 46 m distribution on a goal kick.</summary>
    public const float MaxReceiverMeters = 45f;

    /// <summary>Nearest the longer option may be, in metres: below this it is the short option's job.</summary>
    public const float MinTargetMeters = 10f;

    /// <summary>
    /// Distance over which a short option stops being "short" in the option score, in metres. Measured from
    /// <see cref="MinReceiverMeters"/>: a three-metre square ball is a support option, a twenty-eight-metre one is
    /// the same ball as the delivery but slower, so it scores as a long ball even when it is the nearest.
    /// </summary>
    public const float ShortBallSpanMeters = 25f;

    /// <summary>
    /// Distance from the receiver to the nearest believed opponent at which the receiver counts as completely
    /// free, in metres. A receiver nobody is near scores 1; one with an opponent on top of him scores 0.
    /// </summary>
    public const float SpaceSpanMeters = 8f;

    /// <summary>Short-option ceiling per restart kind, in metres: how far the nearest team-mate may be and still be
    /// the option the team plays to feet. A throw-in's support is beside the thrower, a goal kick's is a defender
    /// dropping towards his own box, and a corner's must be at the flag rather than in the box.</summary>
    public const float KickoffSupportMeters = 24f;
    public const float ThrowInSupportMeters = 20f;
    public const float CornerSupportMeters = 14f;
    public const float FreeKickSupportMeters = 22f;
    public const float GoalKickSupportMeters = 32f;

    /// <summary>Composite confidence below which no routine is raised. Gates before weights, then one floor.</summary>
    public const float MinRoutineConfidence01 = 0.30f;

    /// <summary>Weight of the space around the chosen receiver in the option score.</summary>
    public const float WeightSpace = 0.35f;

    /// <summary>Weight of "how short the short option is" — a 4 m square ball is a better option than a 14 m one.</summary>
    public const float WeightCloseness = 0.30f;

    /// <summary>Weight of "how far up the pitch the target is" in the longer option's score.</summary>
    public const float WeightProgression = 0.30f;

    /// <summary>Weight of the profile's own short-ball preference in the short option's score.</summary>
    public const float WeightShortBias = 0.35f;

    /// <summary>Weight of the profile's own long-ball preference in the longer option's score.</summary>
    public const float WeightLongBias = 0.35f;

    /// <summary>
    /// Split between "how far up the pitch" and "how free" when the longer option picks its man. The old taker
    /// rule was all progression: the most advanced team-mate won even when a defender stood on the pass, so a
    /// contested delivery outranked a free one a few metres deeper. Space is what makes the delivery worth asking
    /// for, so it is not a tie-breaker.
    /// </summary>
    public const float TargetProgressionWeight = 0.6f;

    /// <summary>Space's share of the longer option's receiver choice (see <see cref="TargetProgressionWeight"/>).</summary>
    public const float TargetSpaceWeight = 0.4f;

    /// <summary>
    /// The longest short option each restart kind allows, in metres. A throw-in's short option is a touch
    /// inside the line; a goal kick's is a defender dropping towards the box, which is a longer ball.
    /// </summary>
    public static float SupportBoundMeters(RestartKind kind) => kind switch
    {
        RestartKind.Kickoff => KickoffSupportMeters,
        RestartKind.ThrowIn => ThrowInSupportMeters,
        RestartKind.CornerKick => CornerSupportMeters,
        RestartKind.FreeKick => FreeKickSupportMeters,
        RestartKind.IndirectFreeKick => FreeKickSupportMeters,
        RestartKind.GoalKick => GoalKickSupportMeters,
        _ => 0f
    };

    /// <summary>True for the restart kinds this library has a routine for. A penalty is a single-player act.</summary>
    public static bool Supports(RestartKind kind) => kind is
        RestartKind.Kickoff or RestartKind.ThrowIn or RestartKind.CornerKick or
        RestartKind.FreeKick or RestartKind.IndirectFreeKick or RestartKind.GoalKick;

    /// <summary>
    /// Chooses the routine the restarting team should play. Returns true and fills <paramref name="plan"/> only
    /// when the restart kind has a routine, the designated taker can take it, at least one lawful receiver exists
    /// and the chosen option's composite score clears <see cref="MinRoutineConfidence01"/>.
    /// </summary>
    /// <param name="team">The team the restart was awarded to.</param>
    /// <param name="kind">The armed restart the rules engine owns.</param>
    /// <param name="ballXZ">The ball's placed spot, as the rules engine left it.</param>
    /// <param name="takerId">The player the rules engine designated, or null.</param>
    /// <param name="players">Every player in the match; the restarting team's own are read exactly.</param>
    /// <param name="opponents">The restarting team's shared picture of the opponents (A2.6): the only opponent
    /// information used — for the space around a receiver and the believed offside line.</param>
    /// <param name="profile">The restarting team's tactical profile, which supplies its short/long preference.</param>
    /// <param name="humanPlayerId">The locked human. An AI routine never binds the human's own restart: the AI
    /// must never take that player over.</param>
    /// <param name="attackDirX">The restarting team's attacking direction.</param>
    /// <param name="pitch">Pitch geometry.</param>
    public static bool TryPlan(
        TeamId team,
        RestartKind kind,
        Vector2 ballXZ,
        int? takerId,
        IReadOnlyList<FootballerState> players,
        TeamBelief opponents,
        TeamTacticalProfile profile,
        int? humanPlayerId,
        float attackDirX,
        PitchDimensions pitch,
        out RestartPlan plan)
    {
        plan = default;

        // Gate 1 -- the library: a routine exists for this restart kind and there is a taker to play it.
        if (!Supports(kind)) return false;
        if (takerId is not int taker || taker <= 0) return false;
        if (humanPlayerId is int human && taker == human) return false;
        if (!TryGetPlayer(players, taker, out FootballerState takerPlayer)) return false;
        if (takerPlayer.Team != team || !takerPlayer.IsActiveOnPitch) return false;

        // Gate 2 -- the law: the believed offside line for the restarts that are not exempt, computed the way
        // every other pass in the match computes it. This is the one legality test the library adds.
        float ballProj = ballXZ.X * attackDirX;
        bool offsideExempt = RestartRules.ExemptsOffside(kind);
        float lawLineProj = offsideExempt
            ? float.PositiveInfinity
            : OffsideTactics.BelievedLineProjX(opponents, attackDirX, ballProj, pitch.HalfLength);

        float supportBound = SupportBoundMeters(kind);
        int supportId = 0;
        float supportDistance = 0f;
        float supportProj = float.NegativeInfinity;
        float supportSpace01 = 0f;
        float minProj = float.PositiveInfinity;
        float maxProj = float.NegativeInfinity;
        int legalReceivers = 0;

        // Scan 1 -- every lawful receiver, the nearest short option and the progression range. One list-ordered
        // pass: no allocation, and the first candidate wins a tie.
        for (int i = 0; i < players.Count; i++)
        {
            FootballerState mate = players[i];
            if (!IsEligibleReceiver(mate, team, taker)) continue;

            Vector2 mateXZ = new(mate.Position.X, mate.Position.Z);
            float distance = Vector2.Distance(ballXZ, mateXZ);
            if (distance < MinReceiverMeters || distance > MaxReceiverMeters) continue;

            float mateProj = mateXZ.X * attackDirX;
            if (!offsideExempt && mateProj > lawLineProj - OffsideTactics.MarginMeters) continue;

            legalReceivers++;
            if (mateProj < minProj) minProj = mateProj;
            if (mateProj > maxProj) maxProj = mateProj;

            if (distance <= supportBound && (supportId == 0 || distance < supportDistance))
            {
                supportId = mate.Id;
                supportDistance = distance;
                supportProj = mateProj;
                supportSpace01 = Space01(opponents, mateXZ);
            }
        }

        if (legalReceivers == 0) return false;

        // Scan 2 -- the longer option. "Advance the ball" alone is the old taker's rule and it played a
        // contested ball to the most advanced man when a free one stood a few metres deeper; the target is the
        // best combination of progression and space instead, on one stated split, so the delivery the routine
        // asks for is worth making. Ties keep the first candidate in list order.
        float progressionSpan = maxProj - minProj;
        float targetNorm = 0f;
        int targetId = 0;
        float targetProj = float.NegativeInfinity;
        float targetSpace01 = 0f;
        float targetRank = float.NegativeInfinity;
        if (progressionSpan > 1f)
        {
            for (int i = 0; i < players.Count; i++)
            {
                FootballerState mate = players[i];
                if (!IsEligibleReceiver(mate, team, taker)) continue;

                Vector2 mateXZ = new(mate.Position.X, mate.Position.Z);
                float distance = Vector2.Distance(ballXZ, mateXZ);
                if (distance < MinTargetMeters || distance > MaxReceiverMeters) continue;

                float mateProj = mateXZ.X * attackDirX;
                if (!offsideExempt && mateProj > lawLineProj - OffsideTactics.MarginMeters) continue;

                float norm = Math.Clamp((mateProj - minProj) / progressionSpan, 0f, 1f);
                float space = Space01(opponents, mateXZ);
                float rank = TargetProgressionWeight * norm + TargetSpaceWeight * space;
                if (rank <= targetRank) continue;

                targetId = mate.Id;
                targetProj = mateProj;
                targetSpace01 = space;
                targetNorm = norm;
                targetRank = rank;
            }
        }

        // "Short or long" must be two different men, and the longer option must actually gain ground on the
        // short one — otherwise the menu has one real option and the label would lie about it. A kick-off in
        // this match's own setup (everybody in his own half, the nearest team-mate sixteen metres up the pitch)
        // is exactly that degenerate case: it is a short ball, not a choice.
        if (targetId != 0 && supportId != 0 && (targetId == supportId || targetProj <= supportProj))
        {
            targetId = 0;
            targetNorm = 0f;
        }

        if (supportId == 0 && targetId == 0) return false;

        float closeness01 = supportId != 0
            ? 1f - Math.Clamp((supportDistance - MinReceiverMeters) / ShortBallSpanMeters, 0f, 1f)
            : 0f;

        // The team's stated short/long preference. A goal kick uses A6.5b's own distribution preference
        // instead of directness: the keeper's distribution is the one decision that already owns this choice.
        float shortBias;
        float longBias;
        if (kind == RestartKind.GoalKick)
        {
            shortBias = profile.ShortGoalkeeperDistribution ? 1f : 0f;
            longBias = 1f - shortBias;
        }
        else
        {
            shortBias = 1f - Math.Clamp(profile.Directness01, 0f, 1f);
            longBias = Math.Clamp(profile.Directness01, 0f, 1f);
        }

        float shortScore = supportId != 0
            ? WeightSpace * supportSpace01 + WeightCloseness * closeness01 + WeightShortBias * shortBias
            : float.NegativeInfinity;
        float deliverScore = targetId != 0
            ? WeightSpace * targetSpace01 + WeightProgression * targetNorm + WeightLongBias * longBias
            : float.NegativeInfinity;

        RestartOption option;
        int receiverId;
        float confidence;
        float receiverSpace01;
        float chosenProgression01;
        if (shortScore >= deliverScore)
        {
            option = RestartOption.Short;
            receiverId = supportId;
            confidence = shortScore;
            receiverSpace01 = supportSpace01;
            chosenProgression01 = closeness01;
        }
        else
        {
            option = RestartOption.Deliver;
            receiverId = targetId;
            confidence = deliverScore;
            receiverSpace01 = targetSpace01;
            chosenProgression01 = targetNorm;
        }

        if (confidence < MinRoutineConfidence01) return false;

        Vector2 receiverXZ = Vector2.Zero;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].Id != receiverId) continue;
            receiverXZ = new Vector2(players[i].Position.X, players[i].Position.Z);
            break;
        }

        plan = new RestartPlan(
            Kind: kind,
            Option: option,
            Team: team,
            TakerId: taker,
            SupportId: supportId,
            TargetId: targetId,
            ReceiverXZ: receiverXZ,
            Confidence01: confidence,
            ReceiverSpace01: receiverSpace01,
            Progression01: chosenProgression01);
        return true;
    }

    /// <summary>
    /// True when this player may be a receiver of the routine: his own team, on the pitch, not the taker, and
    /// not the goalkeeper — except for a goal kick, which is the one restart the keeper takes and therefore the
    /// one whose routine may name him only as the taker, never as a receiver (he is the taker by construction).
    /// </summary>
    private static bool IsEligibleReceiver(FootballerState candidate, TeamId team, int takerId) =>
        candidate.Team == team &&
        candidate.IsActiveOnPitch &&
        candidate.Id != takerId &&
        candidate.Role != TacticalRole.Goalkeeper;

    /// <summary>How free a point is from the opponents this team believes in: 1 free, 0 crowded.</summary>
    private static float Space01(TeamBelief opponents, Vector2 pointXZ) =>
        Math.Clamp(opponents.NearestDistance(pointXZ, SpaceSpanMeters) / SpaceSpanMeters, 0f, 1f);

    private static bool TryGetPlayer(IReadOnlyList<FootballerState> players, int playerId, out FootballerState player)
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
}
