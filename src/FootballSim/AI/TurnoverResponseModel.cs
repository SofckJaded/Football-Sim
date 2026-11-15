using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;

namespace FootballSim.AI;

/// <summary>
/// What a team does in the seconds after it loses the ball (A6.4).
///
/// The plan asks for a comparison rather than an instinct: local arrival numbers, central security, the new
/// carrier's constraint and his escape routes, against counter-pressing, holding him up while the side
/// recovers, or getting everybody back. Before this the answer was implicit — <see cref="TransitionMode"/>
/// said a pressing profile *has* a counter-press window, and the only consumer was nothing at all: no act was
/// gated on it, so "counter-press" was a label the match never used.
/// </summary>
public enum TurnoverResponse
{
    /// <summary>No transition to answer: the team has the ball, or the window has run out.</summary>
    None = 0,

    /// <summary>
    /// Hunt the ball back now. Available only inside the profile's counter-press window, and only when the
    /// numbers and the geometry say the challenge is worth leaving shape for.
    /// </summary>
    CounterPress = 1,

    /// <summary>
    /// Hold the carrier up at a jockeying distance while the side gets back behind the ball. The middle
    /// answer, and the one most turnovers get.
    /// </summary>
    DelayRecover = 2,

    /// <summary>
    /// Nobody steps in front of the ball: the line drops, no second man is committed and the side re-forms
    /// behind it. The answer to a turnover with nothing behind the ball.
    /// </summary>
    Retreat = 3
}

/// <summary>
/// The A6.4 comparison and its answer: the four inputs, the two scores they produce, and the response.
///
/// The four inputs are deliberately separate questions and each one is somebody else's model:
/// <list type="bullet">
/// <item><b>local arrival numbers</b> — how many of ours versus theirs can actually reach the ball inside
///   <see cref="TurnoverResponseModel.LocalWindowSeconds"/>, through the one reach model. A head-count of
///   players standing near the ball is not arrival.</item>
/// <item><b>central security</b> — the A3.6 <see cref="RestDefence"/> of the side that just lost the ball,
///   read from the same <see cref="TransitionSecurityModel"/>. Nobody is behind the ball is the fact that
///   forbids a counter-press and argues for a retreat.</item>
/// <item><b>carrier constraint</b> — of the exits the new carrier can actually use, how many of ours can be
///   there first. Scanned around him through the same reach model.</item>
/// <item><b>escape routes</b> — <see cref="RestDefence.OutletExposure01"/>: how open his first pass out of
///   the area is. This is the same lane the A3.6 model already measures, not a second estimate.</item>
/// </list>
///
/// <b>The knowledge boundary.</b> Opponents come only from <see cref="TeamBelief"/>; the team's own players
/// are its own state and their bodies are read exactly, as the allocator already does for the line band.
/// Nothing here reads an opponent's intent, hidden attribute or live position.
/// </summary>
public readonly record struct TurnoverSituation(
    TurnoverResponse Response,
    bool HasCarrier,
    int CarrierId,
    Vector2 CarrierXZ,
    int OursInWindow,
    int TheirsInWindow,
    float LocalNumbers01,
    float CentralSecurity01,
    int BehindBall,
    float WorstRecoverySeconds,
    float CarrierConstraint01,
    float EscapeRoute01,
    float CounterPressScore,
    float RetreatScore,
    bool CounterPressWindowOpen)
{
    /// <summary>No transition: the team has the ball, or the response window has run out.</summary>
    public static TurnoverSituation None => default;

    /// <summary>True when this turnover has an answer (i.e. the team is in its response window).</summary>
    public bool Responds => Response != TurnoverResponse.None;

    /// <summary>
    /// How far the response moves the shared defensive-line band, in metres, before the A6.3 caps apply: a
    /// counter-press squeezes up, a retreat drops. Zero for the other answers, which hold the band.
    /// </summary>
    public float LineShiftMeters => Response switch
    {
        TurnoverResponse.CounterPress => TurnoverResponseModel.CounterPressLinePushMeters,
        TurnoverResponse.Retreat => -TurnoverResponseModel.RetreatLineDropMeters,
        _ => 0f
    };

    /// <summary>
    /// How many marking slots the response adds to or removes from the profile's style cap (A6.3's zone/man
    /// mix): a counter-press picks up one more man, a retreat gives one back to the block.
    /// </summary>
    public int MarkerAdjustment => Response switch
    {
        TurnoverResponse.CounterPress => 1,
        TurnoverResponse.Retreat => -1,
        _ => 0
    };

    /// <summary>Compact text form for a probe, a trace line or a failure message.</summary>
    public string Describe() => Response switch
    {
        TurnoverResponse.None => "no transition",
        _ => $"{Response} · local {OursInWindow}v{TheirsInWindow} ({LocalNumbers01:F2})" +
             $" · security {CentralSecurity01:F2} ({BehindBall} back, worst {WorstRecoverySeconds:F1} s)" +
             $" · carrier {(HasCarrier ? $"#{CarrierId} constraint {CarrierConstraint01:F2}" : "none")}" +
             $" · escape {EscapeRoute01:F2} · scores {CounterPressScore:F2}/{RetreatScore:F2}" +
             $" · window {(CounterPressWindowOpen ? "open" : "shut")}"
    };
}

/// <summary>
/// A6.4: the turnover response, as one bounded, belief-only comparison.
///
/// Two rules keep this from becoming a fourth press decision:
/// <list type="bullet">
/// <item><b>the counter-press window is a maximum, not an instruction.</b> The profile's window (A3.1) says
///   how long a side is willing to hunt the ball; this model decides whether it actually does. The response
///   window (<see cref="ResponseWindowSeconds"/>) is the outer bound on all three answers: after it the
///   block is the block again.</item>
/// <item><b>it decides, it does not act.</b> The answer is published as a team fact and consumed by the
///   existing owners of movement — <see cref="PressPolicy"/> for the press depth and the second man,
///   <see cref="Tactics.TeamJobCoordinator"/> for the line band and the marking mix. No allocator here.</item>
/// </list>
///
/// A chosen answer is held for <see cref="MinimumHoldTicks"/>, because a team that alternates between
/// counter-pressing and retreating every six ticks is doing neither. It is released early only when the
/// episode changes: a different carrier, the end of the window that allowed it, or the transition ending.
/// </summary>
public static class TurnoverResponseModel
{
    // ---------------------------------------------------------------------------------------------
    // The window.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// How long after the ball is lost a team still answers the turnover as a transition, in seconds. The
    /// profile's own counter-press window is the inner bound for the counter-press answer; this is the outer
    /// bound for all three.
    /// </summary>
    public const float ResponseWindowSeconds = 6f;

    /// <summary>
    /// Shortest time a chosen answer is held, in ticks. Six ticks is the allocator's own cadence, so this is
    /// five passes: long enough that the response is a decision rather than a per-pass vote.
    /// </summary>
    public const int MinimumHoldTicks = 30;

    // ---------------------------------------------------------------------------------------------
    // Local arrival numbers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Radius around the contest point inside which a player is a candidate for the local count, in metres.</summary>
    public const float LocalWindowMeters = 18f;

    /// <summary>Time inside which a candidate must be able to reach the contest point to be counted, in seconds.</summary>
    public const float LocalWindowSeconds = 2.5f;

    /// <summary>Candidates counted per side, so the comparison stays bounded on a crowded ball.</summary>
    public const int LocalPlayerCap = 6;

    // ---------------------------------------------------------------------------------------------
    // Carrier constraint.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Exits scanned around the carrier (45° apart).</summary>
    private const int ExitCount = 8;

    /// <summary>Metres out along each exit direction the race for it is judged.</summary>
    public const float ExitProbeMeters = 6f;

    /// <summary>Time inside which the carrier has to be able to use an exit for it to count, in seconds.</summary>
    public const float ExitWindowSeconds = 2f;

    /// <summary>
    /// How much earlier one of ours has to be at an exit point than the carrier for the exit to be closed,
    /// in seconds. Being level with him is not closing it — he has the ball.
    /// </summary>
    public const float ExitCloserMarginSeconds = 0.15f;

    /// <summary>Own players considered as exit closers, nearest first.</summary>
    private const int ExitCloserCap = 4;

    /// <summary>
    /// The value of "cannot say" for the carrier constraint, used when no carrier is believed: a loose ball
    /// has no constrained carrier, and scoring it zero would read as "he is free", which is a different claim.
    /// </summary>
    public const float NeutralConstraint01 = 0.5f;

    // ---------------------------------------------------------------------------------------------
    // The comparison.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Nobody behind the ball means nobody to press with: the counter-press answer is unavailable.</summary>
    public const int MinBehindBallForCounterPress = 2;

    /// <summary>Composite score at which the counter-press answer is chosen.</summary>
    public const float CounterPressThreshold = 0.55f;

    /// <summary>Composite score at which the retreat answer is chosen.</summary>
    public const float RetreatThreshold = 0.55f;

    /// <summary>Weight of the local numbers in the counter-press score.</summary>
    private const float CounterLocalWeight = 0.34f;

    /// <summary>Weight of the carrier's constraint in the counter-press score.</summary>
    private const float CounterConstraintWeight = 0.30f;

    /// <summary>Weight of the (inverted) escape routes in the counter-press score.</summary>
    private const float CounterEscapeWeight = 0.20f;

    /// <summary>Weight of the profile's own pressing intensity in the counter-press score.</summary>
    private const float CounterProfileWeight = 0.16f;

    /// <summary>Weight of the (inverted) local numbers in the retreat score.</summary>
    private const float RetreatOutnumberedWeight = 0.36f;

    /// <summary>Weight of the (inverted) central security in the retreat score.</summary>
    private const float RetreatSecurityWeight = 0.32f;

    /// <summary>Weight of the escape routes in the retreat score.</summary>
    private const float RetreatEscapeWeight = 0.20f;

    /// <summary>Weight of the (inverted) carrier constraint in the retreat score.</summary>
    private const float RetreatFreeCarrierWeight = 0.12f;

    /// <summary>How far a counter-press squeezes the line up, in metres. Bounded by the A6.3 caps.</summary>
    public const float CounterPressLinePushMeters = 5f;

    /// <summary>How far a retreat drops the line, in metres. Bounded by the A6.3 floor and caps.</summary>
    public const float RetreatLineDropMeters = 5f;

    /// <summary>
    /// Evaluates the response for one defending team. Returns <see cref="TurnoverSituation.None"/> when this
    /// team has the ball, when the transition clock has run past <see cref="ResponseWindowSeconds"/>, when the
    /// keeper is holding the ball (there is nothing to counter-press), or when the claim is not this team's
    /// opponent's either.
    /// </summary>
    /// <param name="transitionSeconds">Seconds since the ball's claim last changed (A3.2's clock).</param>
    /// <param name="counterPressWindowOpen">True while the profile's own counter-press window is live.</param>
    public static TurnoverSituation Evaluate(
        TeamId team,
        IReadOnlyList<FootballerState> players,
        BallState ball,
        BallPossessionSnapshot possession,
        TeamBelief opponents,
        TeamTacticalProfile profile,
        float attackDirX,
        PitchDimensions pitch,
        float transitionSeconds,
        bool counterPressWindowOpen)
    {
        // Having the ball is not a turnover to answer. A keeper holding it is not a contest either: he will
        // put it down and play, and pressing him is a shape mistake rather than a counter-press.
        if (possession.PossessionTeam == team) return TurnoverSituation.None;
        if (possession.State == BallControlState.KeeperHeld) return TurnoverSituation.None;

        // The response is the first seconds after the loss. Past the window the block is the block, and a
        // defence that kept answering a ten-second-old turnover would never settle.
        if (!(transitionSeconds <= ResponseWindowSeconds)) return TurnoverSituation.None;

        Vector2 ballXZ = new(ball.Position.X, ball.Position.Z);

        int carrierId = 0;
        Vector2 carrierXZ = ballXZ;
        Vector2 carrierVelocityXZ = Vector2.Zero;
        bool hasCarrier = false;

        if (possession.State == BallControlState.Controlled &&
            possession.ControllingPlayerId is int controlling &&
            TryBelieved(opponents, controlling, out TeamBelief.Opponent carrier))
        {
            hasCarrier = true;
            carrierId = controlling;
            carrierXZ = carrier.PositionXZ;
            carrierVelocityXZ = carrier.VelocityXZ;
        }

        // 1. Local arrival numbers at the contest point.
        Vector2 contestXZ = hasCarrier ? carrierXZ : ballXZ;
        int ours = CountOurArrivals(players, team, contestXZ);
        int theirs = CountTheirArrivals(opponents, contestXZ);
        float local01 = Clamp01(0.5f + 0.25f * (ours - theirs));

        // 2. Central security: the A3.6 model, asked of the side that just lost the ball. Same model, same
        // inputs, opposite side of the same event — not a second exposure estimate.
        RestDefence security = TransitionSecurityModel.Evaluate(players, team, ball, opponents, attackDirX, pitch);

        // 3. The carrier's own constraint, and 4. his team's escape route out of the area.
        float constraint01 = hasCarrier
            ? CarrierConstraint01(players, team, carrierXZ, carrierVelocityXZ, attackDirX, pitch)
            : NeutralConstraint01;

        // `OutletExposure01` is implemented as *coverage* of the lane from the ball to the opponents'
        // deepest body: it reads 1 when one of ours stands in that lane and 0 when the pass out is clear
        // (the sign is documented in docs/KNOWN_ISSUES.md as an A3.6 wording defect; the aggregate it feeds
        // is unaffected here because A6.4 reads the lane, not the aggregate). The escape route is therefore
        // its complement: a clear pass out is what makes pressing a gamble.
        float escape01 = 1f - Clamp01(security.OutletExposure01);

        float counterScore = Clamp01(
            CounterLocalWeight * local01 +
            CounterConstraintWeight * constraint01 +
            CounterEscapeWeight * (1f - escape01) +
            CounterProfileWeight * Clamp01(profile.PressingIntensity01));

        float retreatScore = Clamp01(
            RetreatOutnumberedWeight * (1f - local01) +
            RetreatSecurityWeight * (1f - Clamp01(security.Security01)) +
            RetreatEscapeWeight * escape01 +
            RetreatFreeCarrierWeight * (1f - constraint01));

        TurnoverResponse response;
        if (counterPressWindowOpen && counterScore >= CounterPressThreshold &&
            security.BehindBall >= MinBehindBallForCounterPress)
        {
            response = TurnoverResponse.CounterPress;
        }
        else if (retreatScore >= RetreatThreshold)
        {
            response = TurnoverResponse.Retreat;
        }
        else
        {
            response = TurnoverResponse.DelayRecover;
        }

        return new TurnoverSituation(
            response, hasCarrier, carrierId, carrierXZ, ours, theirs, local01,
            Clamp01(security.Security01), security.BehindBall, security.WorstRecoverySeconds,
            constraint01, escape01, counterScore, retreatScore, counterPressWindowOpen);
    }

    /// <summary>
    /// The hold rule (A6.4): a chosen answer is a commitment, so a fresh evaluation does not replace it until
    /// the commitment expires or the episode changes. The fresh *inputs* are still published — a trace has to
    /// show what the picture says now, not what it said when the answer was chosen.
    /// </summary>
    /// <param name="held">The answer currently in force.</param>
    /// <param name="fresh">What a fresh evaluation says.</param>
    /// <param name="ticksSinceChosen">Live ticks since the held answer was chosen.</param>
    /// <param name="carrierChanged">True when a different opponent now has the ball: a new question.</param>
    public static TurnoverSituation Hold(
        in TurnoverSituation held,
        in TurnoverSituation fresh,
        int ticksSinceChosen,
        bool carrierChanged)
    {
        // Nothing to hold: the episode ended, or the same answer came back.
        if (!held.Responds || !fresh.Responds) return fresh;
        if (fresh.Response == held.Response) return fresh;
        if (carrierChanged) return fresh;
        if (ticksSinceChosen >= MinimumHoldTicks) return fresh;

        // The counter-press window is a hard maximum: an answer chosen inside it cannot outlive it.
        if (held.Response == TurnoverResponse.CounterPress && !fresh.CounterPressWindowOpen) return fresh;

        return fresh with { Response = held.Response };
    }

    // ---------------------------------------------------------------------------------------------
    // Inputs.
    // ---------------------------------------------------------------------------------------------

    private static int CountOurArrivals(
        IReadOnlyList<FootballerState> players,
        TeamId team,
        Vector2 contestXZ)
    {
        int count = 0;
        int scanned = 0;

        for (int i = 0; i < players.Count; i++)
        {
            FootballerState player = players[i];
            if (player.Team != team || !player.IsActiveOnPitch) continue;
            if (player.Role == TacticalRole.Goalkeeper) continue;

            Vector2 playerXZ = new(player.Position.X, player.Position.Z);
            if (Vector2.Distance(playerXZ, contestXZ) > LocalWindowMeters) continue;
            if (++scanned > LocalPlayerCap) break;

            if (TimeToReachModel.ToPoint(ReachKinematics.From(player), contestXZ, LocalWindowSeconds).Reachable)
                count++;
        }

        return count;
    }

    private static int CountTheirArrivals(TeamBelief opponents, Vector2 contestXZ)
    {
        int count = 0;
        int scanned = 0;

        IReadOnlyList<TeamBelief.Opponent> believed = opponents.Opponents;
        for (int i = 0; i < believed.Count; i++)
        {
            TeamBelief.Opponent opponent = believed[i];
            if (opponent.Role == TacticalRole.Goalkeeper) continue;
            if (Vector2.Distance(opponent.PositionXZ, contestXZ) > LocalWindowMeters) continue;
            if (++scanned > LocalPlayerCap) break;

            ReachEstimate arrival = TimeToReachModel.ToPoint(
                ReachKinematics.Neutral(opponent.PositionXZ, opponent.VelocityXZ),
                contestXZ, LocalWindowSeconds);
            if (arrival.Reachable) count++;
        }

        return count;
    }

    /// <summary>
    /// Of the exits the carrier can actually use inside <see cref="ExitWindowSeconds"/>, the share one of ours
    /// would reach first. An exit whose probe point is out of play is not an escape, and one the carrier
    /// cannot reach inside the window is not one he can use — neither is judged.
    /// </summary>
    private static float CarrierConstraint01(
        IReadOnlyList<FootballerState> players,
        TeamId team,
        Vector2 carrierXZ,
        Vector2 carrierVelocityXZ,
        float attackDirX,
        PitchDimensions pitch)
    {
        Span<ReachKinematics> closers = stackalloc ReachKinematics[ExitCloserCap];
        int closerCount = CollectClosers(players, team, carrierXZ, closers);
        if (closerCount == 0) return 0f;

        ReachKinematics carrier = ReachKinematics.Neutral(carrierXZ, carrierVelocityXZ);
        Vector2 goalSide = SimMath.SafeNormalize(
            new Vector2(-attackDirX * pitch.HalfLength, 0f) - carrierXZ, new Vector2(-attackDirX, 0f));

        int judged = 0;
        int closed = 0;

        for (int i = 0; i < ExitCount; i++)
        {
            Vector2 exit = Rotate(goalSide, i * (MathF.PI * 2f / ExitCount));
            Vector2 probe = carrierXZ + exit * ExitProbeMeters;

            // Out of play is not an escape route: the ball would be out before it was useful.
            if (MathF.Abs(probe.X) > pitch.HalfLength || MathF.Abs(probe.Y) > pitch.HalfWidth) continue;

            ReachEstimate carrierArrival = TimeToReachModel.ToPoint(carrier, probe, ExitWindowSeconds);
            if (!carrierArrival.Reachable) continue;

            int nearest = -1;
            float nearestDistance = float.MaxValue;
            for (int c = 0; c < closerCount; c++)
            {
                float distance = Vector2.Distance(closers[c].PositionXZ, probe);
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                nearest = c;
            }

            if (nearest < 0) continue;
            judged++;

            ReachEstimate ourArrival = TimeToReachModel.ToPoint(closers[nearest], probe, ExitWindowSeconds);
            if (ourArrival.Reachable &&
                ourArrival.Seconds + ExitCloserMarginSeconds <= carrierArrival.Seconds)
            {
                closed++;
            }
        }

        return judged == 0 ? NeutralConstraint01 : Clamp01((float)closed / judged);
    }

    /// <summary>
    /// The nearest <see cref="ExitCloserCap"/> of this team's outfielders inside
    /// <see cref="LocalWindowMeters"/> of the carrier, nearest first. Bounded so a crowded ball cannot turn
    /// the exit scan into a whole-team solve.
    /// </summary>
    private static int CollectClosers(
        IReadOnlyList<FootballerState> players,
        TeamId team,
        Vector2 carrierXZ,
        Span<ReachKinematics> buffer)
    {
        Span<float> distances = stackalloc float[buffer.Length];
        int count = 0;

        for (int i = 0; i < players.Count; i++)
        {
            FootballerState player = players[i];
            if (player.Team != team || !player.IsActiveOnPitch) continue;
            if (player.Role == TacticalRole.Goalkeeper) continue;

            float distance = Vector2.Distance(new Vector2(player.Position.X, player.Position.Z), carrierXZ);
            if (distance > LocalWindowMeters) continue;

            int slot;
            if (count < buffer.Length)
            {
                slot = count;
                count++;
            }
            else
            {
                if (distance >= distances[count - 1]) continue;
                slot = count - 1;
            }

            while (slot > 0 && distances[slot - 1] > distance)
            {
                buffer[slot] = buffer[slot - 1];
                distances[slot] = distances[slot - 1];
                slot--;
            }

            buffer[slot] = ReachKinematics.From(player);
            distances[slot] = distance;
        }

        return count;
    }

    private static bool TryBelieved(TeamBelief belief, int playerId, out TeamBelief.Opponent opponent)
    {
        IReadOnlyList<TeamBelief.Opponent> believed = belief.Opponents;
        for (int i = 0; i < believed.Count; i++)
        {
            if (believed[i].PlayerId != playerId) continue;
            opponent = believed[i];
            return true;
        }

        opponent = default;
        return false;
    }

    private static Vector2 Rotate(Vector2 direction, float radians)
    {
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        return new Vector2(
            direction.X * cos - direction.Y * sin,
            direction.X * sin + direction.Y * cos);
    }

    private static float Clamp01(float value) =>
        !float.IsFinite(value) ? 0f : value < 0f ? 0f : value > 1f ? 1f : value;
}
