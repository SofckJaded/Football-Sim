using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Tactics;

namespace FootballSim.AI;

/// <summary>
/// The defensive acts A6.1 generates, on one vocabulary.
///
/// Before this the defending half of the candidate set had two acts with names and nothing else: the
/// elected responder's <c>CloseDownCarrier</c> and the second defender's <c>ChallengeCarrier</c>. There was
/// no delay, no screen, no explicit tracking of a marked runner and no drop, so "defend" meant "run at the
/// carrier" for two players and "walk to the anchor" for everybody else. The plan asks for the whole list
/// to exist, and — more importantly — for the acts to be *balanced*: two defenders must not
/// independently abandon the same dangerous space.
/// </summary>
public enum PressAction
{
    /// <summary>Close the carrier down. The elected responder's act, from the goal side (A6.2's angle).</summary>
    Press,

    /// <summary>
    /// A second defender stepping in front of the carrier, from the opposite side to the presser, so the
    /// two bodies shade both exits instead of stacking on one. A6.1's shape of the old body challenge.
    /// </summary>
    SupportPress,

    /// <summary>Hold the carrier at a jockeying distance instead of diving in, when nobody covers behind.</summary>
    Delay,

    /// <summary>Drop behind the presser onto the lane that matters, scored by the cover shadow (A6.2).</summary>
    Cover,

    /// <summary>Stand on the opponent's most dangerous forward pass lane instead of pressing.</summary>
    Screen,

    /// <summary>Stay goal-side of a marked runner and move with him, rather than at his stale job anchor.</summary>
    Track,

    /// <summary>Attack a pass in flight (the existing interceptor / presser chase). Declared for the one vocabulary.</summary>
    Intercept,

    /// <summary>Keep the ground the duty already owns: the line and the shape hold.</summary>
    Hold,

    /// <summary>Retreat toward the own goal when the press has been beaten and a runner is beyond it.</summary>
    Drop
}

/// <summary>
/// What one defending player can see that says "this is the moment to press" (A6.2).
///
/// Each field is an observable, not an intention: a pass travelling back toward the opponent's own goal, a
/// ball that has squirted away from the opponent who just touched it, a carrier running toward his own
/// goal (the honest read of "rear-facing" without a perception field for an opponent's body), a slow pass,
/// and the touchline standing next to the carrier. The plan says these are *evidence* and that cover and
/// bypass risk moderate it — so they are summed here, once, and never used as a licence to press alone.
/// </summary>
public readonly record struct PressEvidence(
    float BackPass01,
    float PoorTouch01,
    float RearFacing01,
    float SlowPass01,
    float Touchline01)
{
    /// <summary>Nothing supports pressing: the moment is only as good as the carrier's position.</summary>
    public static PressEvidence None => default;

    /// <summary>True when every signal is zero.</summary>
    public bool IsEmpty =>
        BackPass01 <= 0f && PoorTouch01 <= 0f && RearFacing01 <= 0f && SlowPass01 <= 0f && Touchline01 <= 0f;

    /// <summary>Compact text form for a failure message or a trace line.</summary>
    public string Describe() =>
        IsEmpty
            ? "no evidence"
            : $"back {BackPass01:F2} · touch {PoorTouch01:F2} · rear {RearFacing01:F2} · " +
              $"slow {SlowPass01:F2} · line {Touchline01:F2}";
}

/// <summary>
/// The believing team's pressing picture for one player, built only from his own observation (A6.1).
///
/// It answers the questions the balance needs, and it answers them the same way for every member of the
/// team because every member sees the same team-mates (A2.2 observes team-mates in full):
/// <list type="bullet">
/// <item>who is pressing (<see cref="PresserId"/> — the allocator's elected responder);</item>
/// <item>who is the one defender allowed to step in beside him (<see cref="IsSupport"/>);</item>
/// <item>who is the designated cover behind the press (<see cref="IsCover"/>);</item>
/// <item>whether anybody is actually behind the press (<see cref="HasCoverBehind"/>);</item>
/// <item>how exposed the goal-side space already is (<see cref="BypassRisk01"/>);</item>
/// <item>how much the situation itself argues for pressing (<see cref="Urgency01"/>, moderated by
///   cover and bypass risk).</item>
/// </list>
/// </summary>
public readonly record struct PressSituation(
    bool HasCarrier,
    int CarrierId,
    Vector2 CarrierXZ,
    Vector2 CarrierVelocityXZ,
    int PresserId,
    Vector2 PresserXZ,
    bool IsPresser,
    bool IsSupport,
    bool IsCover,
    bool HasCoverBehind,
    float BypassRisk01,
    float Danger01,
    float Urgency01,
    PressEvidence Evidence)
{
    /// <summary>No carrier in the picture: nothing here applies.</summary>
    public static PressSituation None => default;

    /// <summary>Compact text form for a failure message or a trace line.</summary>
    public string Describe() =>
        !HasCarrier
            ? "no carrier"
            : $"carrier #{CarrierId} at ({CarrierXZ.X:F1}, {CarrierXZ.Y:F1}) · presser #{PresserId}" +
              $" · cover {(HasCoverBehind ? "behind" : "none")} · bypass {BypassRisk01:F2}" +
              $" · urgency {Urgency01:F2} · {Evidence.Describe()}";
}

/// <summary>
/// One defensive movement candidate (A6.1/A6.2): what to do, where, and why (the score, the cover shadow
/// it casts and how urgent the situation was).
///
/// <see cref="IsPrimary"/> separates the one act that *is* this player's defensive decision this tick
/// (press, support, delay, cover, hold) from the supporting acts that compete with his team duty
/// (screen, track, drop). A primary act replaces the duty for the tick because the balance already
/// decided that this player is the one whose movement is the defence; a supporting act is an extra
/// candidate on the same scale as the job anchor.
/// </summary>
public readonly record struct PressCandidate(
    PressAction Action,
    Vector2 TargetXZ,
    int TargetPlayerId,
    float Score,
    float Shadow01,
    bool IsPrimary)
{
    /// <summary>True when the arrival is close enough for the execution layer to accept a tackle.</summary>
    public bool WantsTackle { get; init; }

    /// <summary>The player's own label for the act, used by the trace and the inspector.</summary>
    public string ActionName => PressPolicy.ActionNameOf(Action, WantsTackle);
}

/// <summary>
/// A6.1/A6.2: the team's pressure-cover-balance, as one bounded, belief-only policy.
///
/// This is deliberately not a second allocator. Team roles stay where they were — the match elects one
/// ball responder (<see cref="TacticalJob.Presser"/>) and <see cref="TeamJobCoordinator"/> keeps assigning
/// the cover, the markers and the line. What this type adds is the *local* answer the old body challenge
/// never asked for: given who is already committing, may this defender commit too, and if not, what does
/// "defend" mean for him? The rules are small on purpose:
///
/// <list type="bullet">
/// <item><b>one second man.</b> Exactly one team-mate may step in beside the presser: the nearest
///   central defender or holding midfielder to the carrier. Everybody else keeps the structure.</item>
/// <item><b>cover first.</b> Stepping in is allowed when somebody is already goal-side of the press (the
///   line is the cover) or when the player is himself goal-side of the carrier — a defender stepping
///   forward from in front of the ball abandons nothing. With no cover and no goal-side position, the act
///   is <see cref="PressAction.Delay"/> (hold him up) or <see cref="PressAction.Hold"/> (keep the ground),
///   never a dive in.</item>
/// <item><b>opposite sides.</b> The second man takes the side of the carrier the presser is not on, so
///   between the two of them both lateral exits are shaded and they cannot be beaten by a single step.</item>
/// <item><b>the cover shadows the lane.</b> The player the allocator made the cover drops behind the
///   presser to the point that blocks the opponent's most dangerous forward pass, scored by
///   <see cref="CoverShadow01"/> instead of by a fixed distance (A6.2).</item>
/// </list>
///
/// <b>Knowledge boundary.</b> Every input is the player's own <see cref="FootballerObservation"/>: the
/// carrier's believed position and velocity, team-mates observed in full (A2.2), opponents as beliefs with
/// their age and uncertainty. No opponent's intent, hidden attribute or live truth is read, and the
/// answer is an estimate about belief, not a read of another player's controller.
/// </summary>
public static class PressPolicy
{
    // ---------------------------------------------------------------------------------------------
    // Engagement geometry.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Furthest a second defender travels to step in beside the presser, in metres.</summary>
    public const float SupportEngageMeters = 12f;

    /// <summary>Attack-relative projection inside which a second defender is always worth engaging
    /// (14 m inside the team's own half). Higher up the pitch the evidence has to argue for it.</summary>
    public const float SupportOwnHalfProjX = -14f;

    /// <summary>Urgency at which a second defender engages even outside his own half (A6.2's evidence).</summary>
    public const float SupportUrgencyGate = 0.52f;

    /// <summary>
    /// Furthest a defender may be goal-side of the presser and still count as the cover behind it, in
    /// metres. This is the depth of a normal defensive block behind a press at the halfway line, not the
    /// length of a back four's own third: set it any tighter and a press with a full line fifteen metres
    /// behind it reads as "alone in the world" and stops closing the carrier down.
    /// </summary>
    public const float CoverLinkMeters = 20f;

    /// <summary>Goal-side distance at which a team-mate counts as behind the press, in metres.</summary>
    public const float CoverBehindMarginMeters = 0.5f;

    /// <summary>
    /// Depth of the presser's standing point when somebody covers behind him, in metres. Just outside the
    /// execution layer's contact reach (≈1.24 m), so the presser arrives able to play the ball rather than
    /// standing in the carrier's body.
    /// </summary>
    public const float PressStandoffMeters = 1.25f;

    /// <summary>
    /// Depth of the presser's standing point when he is alone, in metres. A lone defender jockeys: he
    /// keeps himself between the carrier and the goal instead of committing his body, because losing the
    /// tackle with nobody behind is how an unsupported press is beaten.
    /// </summary>
    public const float DelayStandoffMeters = 3.2f;

    /// <summary>Bypass risk above which the lone presser jockeys instead of closing to contact range.</summary>
    public const float JockeyBypassRisk = 0.30f;

    /// <summary>Depth of the second defender's standing point, in metres.</summary>
    public const float SupportStandoffMeters = 1.8f;

    /// <summary>How far a jockeying second defender keeps from the carrier, in metres.</summary>
    public const float SupportDelayStandoffMeters = 3.8f;

    /// <summary>Furthest a second defender jockeys from; beyond this he holds his ground instead.</summary>
    public const float DelayRangeMeters = 13f;

    /// <summary>Distance at which a marked runner is tracked on his goal side, in metres.</summary>
    public const float TrackStandoffMeters = 1.4f;

    /// <summary>Depth a beaten line drops toward its own goal, in metres.</summary>
    public const float DropMeters = 2.5f;

    /// <summary>Bypass risk at which a line duty adds a drop candidate.</summary>
    public const float DropBypassRisk = 0.45f;

    // ---------------------------------------------------------------------------------------------
    // Evidence and urgency (A6.2).
    // ---------------------------------------------------------------------------------------------

    /// <summary>Start of the touchline band inside which the line counts as a defender, in metres.</summary>
    public const float TouchlineWarnMeters = 10f;

    /// <summary>Ball speed at which a pass is no longer "slow", in metres per second.</summary>
    public const float SlowPassSpeedMeters = 11f;

    /// <summary>How far from the last toucher a loose ball still reads as a poor touch, in metres.</summary>
    public const float PoorTouchRadiusMeters = 6f;

    /// <summary>Slowest loose ball that reads as a touch getting away from the player, in metres per second.</summary>
    public const float PoorTouchMinSpeed = 1.5f;

    /// <summary>How much urgency is discounted when nobody is behind the press.</summary>
    public const float UncoveredUrgencyScale = 0.55f;

    /// <summary>How much an already-bypassed press discounts the urgency.</summary>
    public const float BypassedUrgencyScale = 0.30f;

    /// <summary>Evidence weights, in the order the evidence is recorded. They sum to 1.</summary>
    private const float BackPassWeight = 0.20f;
    private const float PoorTouchWeight = 0.28f;
    private const float RearFacingWeight = 0.16f;
    private const float SlowPassWeight = 0.16f;
    private const float TouchlineWeight = 0.20f;

    /// <summary>How much the evidence weighs against the carrier's own danger in the urgency.</summary>
    private const float DangerUrgencyWeight = 0.42f;
    private const float EvidenceUrgencyWeight = 0.38f;
    private const float ProfileUrgencyWeight = 0.20f;

    // ---------------------------------------------------------------------------------------------
    // Angled pressure and the cover shadow (A6.2).
    // ---------------------------------------------------------------------------------------------

    /// <summary>Number of approach angles scanned around the carrier (45° apart).</summary>
    private const int ApproachAngles = 8;

    /// <summary>Weight of standing goal-side of the carrier in the approach score.</summary>
    private const float ApproachGoalSideWeight = 0.52f;

    /// <summary>Weight of blocking the carrier's useful exits in the approach score.</summary>
    private const float ApproachExitWeight = 0.36f;

    /// <summary>Weight of taking the side of the carrier the presser is not on.</summary>
    private const float ApproachSpacingWeight = 0.12f;

    /// <summary>How much an exit toward the nearer touchline is discounted, at the line itself.</summary>
    private const float TouchlineExitSafety = 0.55f;

    /// <summary>Corridor width inside which a body blocks a pass lane, in metres.</summary>
    public const float CoverCorridorMeters = 2.4f;

    /// <summary>Depth from the carrier at which a forward option is worth shadowing, in metres.</summary>
    public const float ScreenDangerMeters = 24f;

    /// <summary>Closest a shadowed receiver may be to the carrier to count as a forward option, in metres.</summary>
    public const float ScreenMinForwardMeters = 2f;

    /// <summary>How far along the carrier-to-receiver line the screen stands.</summary>
    public const float ScreenFraction = 0.55f;

    /// <summary>Base depth of the cover behind the presser, in metres.</summary>
    public const float CoverBaseDepthMeters = 5.5f;

    /// <summary>Extra depth a compact side puts between the press and its cover, in metres.</summary>
    public const float CoverDepthSpreadMeters = 5f;

    /// <summary>How far from the presser the cover may be placed laterally, in metres.</summary>
    private const float CoverLateralMeters = 4f;

    /// <summary>Largest number of candidates one player can be offered by this policy.</summary>
    public const int MaxCandidates = 1;

    // ---------------------------------------------------------------------------------------------
    // Builders.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds this player's pressing picture from his own observation. Pure: no match state, no shared
    /// cache, no clock. Every role is resolved the same way for every member of the team, because every
    /// member observes the team in full.
    /// </summary>
    public static PressSituation BuildSituation(in FootballerObservation obs, PitchDimensions pitch)
    {
        Vector2 ownGoalXZ = new(-obs.AttackingDirectionX * pitch.HalfLength, 0f);
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);

        bool hasCarrier = false;
        int carrierId = 0;
        Vector2 carrierXZ = ballXZ;
        Vector2 carrierVel = Vector2.Zero;

        if (obs.BallControlState == BallControlState.Controlled &&
            obs.BallControllingPlayerId is int controlling &&
            controlling != obs.SelfId &&
            TryFind(obs, controlling, out PerceivedEntity carrier) &&
            carrier.Team != obs.SelfTeam)
        {
            carrierId = controlling;
            carrierXZ = new Vector2(carrier.EstimatedPosition.X, carrier.EstimatedPosition.Z);
            carrierVel = new Vector2(carrier.EstimatedVelocity.X, carrier.EstimatedVelocity.Z);
            hasCarrier = true;
        }

        Vector2 goalSide = SimMath.SafeNormalize(
            ownGoalXZ - carrierXZ, new Vector2(-obs.AttackingDirectionX, 0f));

        int presserId = ResolvePresser(obs, ballXZ, selfXZ);
        bool isPresser = presserId == obs.SelfId;
        Vector2 presserXZ = selfXZ;
        if (!isPresser && presserId > 0 && TryFind(obs, presserId, out PerceivedEntity presser))
            presserXZ = new Vector2(presser.EstimatedPosition.X, presser.EstimatedPosition.Z);

        int supportId = hasCarrier && !isPresser
            ? ResolveSupport(obs, carrierXZ, presserId)
            : 0;
        // A temporary responsibility (A3.3) is an exception layered on a team-mate's vacated slot: it takes
        // precedence over taking a second pressing role, or the gap it exists to protect opens again.
        bool isSupport = supportId == obs.SelfId && !obs.IsTemporaryResponsibility;

        bool isCover = !isPresser &&
                       !obs.IsTemporaryResponsibility &&
                       obs.TacticalJob == TacticalJob.CoverDefender;

        bool hasCoverBehind = HasTeammateBehind(obs, presserXZ, goalSide);
        float bypass = BypassRisk(
            obs, presserXZ, carrierXZ, carrierId, hasCarrier, goalSide, pitch);
        float danger = Clamp01(1f - Vector2.Distance(carrierXZ, ownGoalXZ) / pitch.HalfLength);
        PressEvidence evidence = ReadEvidence(obs, hasCarrier, carrierVel, carrierXZ, ballXZ, pitch);

        float urgency = Clamp01(
            DangerUrgencyWeight * danger +
            EvidenceUrgencyWeight * EvidenceStrength(evidence) +
            ProfileUrgencyWeight * Clamp01(obs.TeamProfile.PressingIntensity01));
        if (!hasCoverBehind) urgency *= UncoveredUrgencyScale;
        urgency *= 1f - BypassedUrgencyScale * bypass;
        urgency = Clamp01(urgency);

        return new PressSituation(
            hasCarrier, carrierId, carrierXZ, carrierVel,
            presserId, presserXZ, isPresser, isSupport, isCover,
            hasCoverBehind, bypass, danger, urgency, evidence);
    }

    /// <summary>
    /// Offers this player's defensive candidates. Returns how many were written (0–<see cref="MaxCandidates"/>).
    ///
    /// The primary acts are returned alone because they *are* this player's defensive decision for the
    /// tick; the supporting acts are extra candidates that compete with the player's duty on the same
    /// scale. Order is deterministic: primary first, then at most one supporting act.
    /// </summary>
    public static int Build(
        in FootballerObservation obs,
        in PressSituation situation,
        PitchDimensions pitch,
        Span<PressCandidate> buffer)
    {
        if (buffer.Length == 0) return 0;
        if (!situation.HasCarrier) return 0;

        // The presser is the other path's business: he closes down, optionally at jockeying depth.
        if (situation.IsPresser) return 0;

        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 goalSide = GoalSideXZ(situation.CarrierXZ, obs, pitch);
        float carrierDistance = Vector2.Distance(selfXZ, situation.CarrierXZ);

        // A6.1: the allocator's cover holds the lane behind the press, scored by what it shadows.
        if (situation.IsCover)
        {
            Vector2 cover = CoverPoint(obs, situation, pitch);
            float shadow = CoverShadow01(
                cover, situation, obs.VisiblePlayers, obs.SelfTeam, obs.AttackingDirectionX, pitch);
            buffer[0] = new PressCandidate(
                PressAction.Cover, cover, situation.CarrierId,
                0.99f + 0.10f * shadow, shadow, IsPrimary: true);
            return 1;
        }

        // A6.1/A6.2: exactly one team-mate may be the second man. He steps in only if the space behind
        // the press is covered (or he is himself goal-side, so stepping forward abandons nothing).
        if (situation.IsSupport)
        {
            bool goalSideOfCarrier = Vector2.Dot(selfXZ - situation.CarrierXZ, goalSide) > 0f;
            bool balanceHolds = situation.HasCoverBehind || goalSideOfCarrier;
            float carrierProj = situation.CarrierXZ.X * obs.AttackingDirectionX;
            bool worthEngaging = carrierProj <= SupportOwnHalfProjX ||
                                 situation.Urgency01 >= SupportUrgencyGate ||
                                 EvidenceStrength(situation.Evidence) >= 0.5f;

            if (balanceHolds && worthEngaging && carrierDistance <= SupportEngageMeters)
            {
                Vector2 target = PressTarget(
                    obs, situation, pitch, SupportStandoffMeters, support: true);
                float reach = obs.SelfAttributes.ReachRadius * 1.35f;
                float ballDistance = Vector2.Distance(
                    selfXZ, new Vector2(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z));

                buffer[0] = new PressCandidate(
                    PressAction.SupportPress, target, situation.CarrierId,
                    0.995f + 0.10f * situation.Urgency01 + 0.05f * situation.BypassRisk01,
                    0f, IsPrimary: true)
                {
                    WantsTackle = ballDistance <= reach
                };
                return 1;
            }

            if (carrierDistance <= DelayRangeMeters)
            {
                Vector2 jockey = PressTarget(
                    obs, situation, pitch, SupportDelayStandoffMeters, support: true);
                buffer[0] = new PressCandidate(
                    PressAction.Delay, jockey, situation.CarrierId,
                    0.93f + 0.06f * situation.Urgency01, 0f, IsPrimary: true);
                return 1;
            }

            Vector2 hold = obs.TacticalJobAnchor is Vector3 anchor
                ? new Vector2(anchor.X, anchor.Z)
                : selfXZ + goalSide * 1.5f;
            buffer[0] = new PressCandidate(PressAction.Hold, hold, 0, 0.90f, 0f, IsPrimary: true);
            return 1;
        }

        // Supporting acts: never a second bodies-on decision, always an alternative that has to beat the
        // player's own duty on the same scale.
        int count = 0;
        if (obs.TacticalJob == TacticalJob.Marker &&
            obs.JobTargetPlayerId is int marked &&
            TryFind(obs, marked, out PerceivedEntity runner) &&
            runner.Team != obs.SelfTeam)
        {
            Vector2 runnerXZ = new(runner.EstimatedPosition.X, runner.EstimatedPosition.Z);
            Vector2 runnerVel = new(runner.EstimatedVelocity.X, runner.EstimatedVelocity.Z);
            Vector2 runnerGoalSide = GoalSideXZ(runnerXZ, obs, pitch);
            Vector2 target = ClampToPitch(
                runnerXZ + runnerVel * 0.12f + runnerGoalSide * TrackStandoffMeters, pitch);
            float shadow = CoverShadow01(
                target, situation, obs.VisiblePlayers, obs.SelfTeam, obs.AttackingDirectionX, pitch);
            buffer[count++] = new PressCandidate(
                PressAction.Track, target, marked,
                0.97f + 0.15f * shadow - 0.05f * Clamp01(carrierDistance / 40f),
                shadow, IsPrimary: false);
            return count;
        }

        if (obs.TacticalJob == TacticalJob.DefensiveLine && situation.BypassRisk01 >= DropBypassRisk)
        {
            Vector2 anchor = obs.TacticalJobAnchor is Vector3 line
                ? new Vector2(line.X, line.Z)
                : selfXZ;
            Vector2 target = ClampToPitch(
                anchor - new Vector2(obs.AttackingDirectionX * DropMeters, 0f), pitch);
            buffer[count++] = new PressCandidate(
                PressAction.Drop, target, 0,
                0.90f + 0.12f * situation.BypassRisk01, 0f, IsPrimary: false);
            return count;
        }

        if (obs.TacticalJob == TacticalJob.Shape &&
            obs.SelfRole is TacticalRole.DefensiveMidfielder or TacticalRole.CentralMidfielder &&
            TryFindScreen(obs, situation, pitch, out Vector2 screenXZ, out int screenId, out float shadow01))
        {
            float travel = Clamp01(Vector2.Distance(selfXZ, screenXZ) / 30f);
            buffer[count++] = new PressCandidate(
                PressAction.Screen, screenXZ, screenId,
                0.90f + 0.16f * shadow01 - 0.05f * travel, shadow01, IsPrimary: false);
            return count;
        }

        return count;
    }

    // ---------------------------------------------------------------------------------------------
    // A6.2: angled pressure.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The point the presser should stand on: scanned around the carrier, scoring how goal-side the
    /// position is, how much of the carrier's useful space it takes away, and — for the second man —
    /// whether it takes the side the presser is not already on.
    ///
    /// This is what makes pressure *angled* rather than a straight run at the ball. A carrier on the
    /// touchline is pressed from the inside, so his only remaining exit is the line, which is already a
    /// defender; a central carrier is pressed from the goal side, because that is the exit that hurts.
    /// </summary>
    public static Vector2 PressTarget(
        in FootballerObservation obs,
        in PressSituation situation,
        PitchDimensions pitch,
        float standoffMeters,
        bool support)
    {
        if (!situation.HasCarrier) return situation.CarrierXZ;

        return PressTarget(
            obs, situation.CarrierXZ, situation.CarrierVelocityXZ, situation.PresserXZ,
            situation.Evidence.Touchline01, pitch, standoffMeters, support);
    }

    /// <summary>Press point without a situation: the presser's own chase, from the carrier he can see.</summary>
    public static Vector2 PressTarget(
        in FootballerObservation obs,
        Vector2 carrierXZ,
        Vector2 carrierVelocityXZ,
        PitchDimensions pitch,
        float standoffMeters,
        bool support)
    {
        Vector2 presserXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        float touchline = Touchline01(carrierXZ, pitch);
        return PressTarget(
            obs, carrierXZ, carrierVelocityXZ, presserXZ, touchline, pitch, standoffMeters, support);
    }

    private static Vector2 PressTarget(
        in FootballerObservation obs,
        Vector2 carrierXZ,
        Vector2 carrierVelocityXZ,
        Vector2 presserXZ,
        float touchline01,
        PitchDimensions pitch,
        float standoffMeters,
        bool support)
    {
        Vector2 ownGoalXZ = new(-obs.AttackingDirectionX * pitch.HalfLength, 0f);
        Vector2 goalSide = SimMath.SafeNormalize(
            ownGoalXZ - carrierXZ, new Vector2(-obs.AttackingDirectionX, 0f));
        Vector2 toPresser = support
            ? SimMath.SafeNormalize(presserXZ - carrierXZ, Vector2.Zero)
            : Vector2.Zero;

        // The carrier is moving: stand where he is going rather than where he was.
        Vector2 lead = carrierVelocityXZ * 0.10f;

        float bestScore = float.NegativeInfinity;
        Vector2 bestPoint = carrierXZ + goalSide * standoffMeters;

        for (int i = 0; i < ApproachAngles; i++)
        {
            float angle = i * (MathF.PI * 2f / ApproachAngles);
            Vector2 direction = Rotate(goalSide, angle);
            Vector2 point = ClampToPitch(carrierXZ + lead + direction * standoffMeters, pitch);

            Vector2 offset = point - carrierXZ;
            Vector2 actual = offset.LengthSquared() > 1e-6f
                ? offset / offset.Length()
                : direction;

            float goalSideAlignment = Vector2.Dot(actual, goalSide);
            float exits = ExitCost(actual, goalSide, carrierXZ.Y, touchline01);
            float spacing = toPresser.LengthSquared() > 1e-6f
                ? Clamp01(-Vector2.Dot(actual, toPresser))
                : 0f;

            float score = ApproachGoalSideWeight * goalSideAlignment +
                          ApproachExitWeight * (1f - exits) +
                          ApproachSpacingWeight * spacing;
            if (score <= bestScore + 1e-5f) continue;

            bestScore = score;
            bestPoint = point;
        }

        return bestPoint;
    }

    /// <summary>
    /// How much of the carrier's useful space a standing point takes away: 0 when the position blocks
    /// every worthwhile exit, 1 when it blocks none.
    ///
    /// "Useful" is direction-dependent: progress toward the goal we are defending is what hurts, a lateral
    /// exit is half as bad, and an exit toward the nearer touchline is discounted because the line itself
    /// is a defender there (A6.2's touchline-as-evidence).
    /// </summary>
    private static float ExitCost(Vector2 standDirection, Vector2 goalSide, float carrierZ, float touchline01)
    {
        float totalDanger = 0f;
        float blockedDanger = 0f;
        float lineSign = carrierZ >= 0f ? 1f : -1f;

        for (int i = 0; i < ApproachAngles; i++)
        {
            Vector2 exit = Rotate(goalSide, i * (MathF.PI * 2f / ApproachAngles));
            float towardGoal = Clamp01(Vector2.Dot(exit, goalSide));
            float lateral = MathF.Abs(exit.Y);
            float danger = 0.70f * towardGoal + 0.30f * lateral;

            if (exit.Y * lineSign > 0f)
                danger *= 1f - TouchlineExitSafety * Clamp01(touchline01);

            totalDanger += danger;
            blockedDanger += danger * Clamp01(Vector2.Dot(standDirection, exit));
        }

        return totalDanger <= 1e-5f ? 1f : Clamp01(1f - blockedDanger / totalDanger);
    }

    // ---------------------------------------------------------------------------------------------
    // A6.2: cover shadow and the points it chooses.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// How much of the opponent's most dangerous forward option this body blocks: 1 when standing on the
    /// lane to the receiver furthest up the pitch, 0 when it shadows nothing.
    ///
    /// A cover defender's value is not that he is seven metres behind the presser — it is that a pass into
    /// the space the press is trying to win back has to beat him. That is the shadow, and the point is
    /// chosen to maximise it rather than by a fixed depth.
    /// </summary>
    public static float CoverShadow01(
        Vector2 pointXZ,
        in PressSituation situation,
        IReadOnlyList<PerceivedEntity> visible,
        TeamId selfTeam,
        float attackDirX,
        PitchDimensions pitch)
    {
        if (!situation.HasCarrier || visible.Count == 0) return 0f;

        Vector2 goalSide = SimMath.SafeNormalize(
            new Vector2(-attackDirX * pitch.HalfLength, 0f) - situation.CarrierXZ,
            new Vector2(-attackDirX, 0f));

        float best = 0f;
        for (int i = 0; i < visible.Count; i++)
        {
            PerceivedEntity entity = visible[i];
            if (entity.Team == selfTeam) continue;
            if (entity.PlayerId == situation.CarrierId) continue;
            if (entity.Role == TacticalRole.Goalkeeper) continue;

            Vector2 receiverXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            float forward = Vector2.Dot(receiverXZ - situation.CarrierXZ, goalSide);
            if (forward < ScreenMinForwardMeters) continue;

            float blocked = LaneBlockage(pointXZ, situation.CarrierXZ, receiverXZ);
            if (blocked <= 0f) continue;

            float danger = Clamp01(forward / ScreenDangerMeters);
            float shadow = blocked * danger;
            if (shadow > best) best = shadow;
        }

        return best;
    }

    /// <summary>
    /// Where the allocated cover should stand: behind the presser, on the depth the profile's
    /// compactness asks for, at the lateral offset that shadows the most dangerous lane. Nine bounded
    /// probes, deterministic tie-break (the first candidate wins an exact tie).
    /// </summary>
    public static Vector2 CoverPoint(
        in FootballerObservation obs,
        in PressSituation situation,
        PitchDimensions pitch)
    {
        Vector2 goalSide = GoalSideXZ(situation.CarrierXZ, obs, pitch);
        Vector2 tangent = new(-goalSide.Y, goalSide.X);
        Vector2 presserXZ = situation.PresserXZ;

        float compactness = Clamp01(obs.TeamProfile.Compactness01);
        float preferredDepth = CoverBaseDepthMeters + CoverDepthSpreadMeters * compactness;
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);

        float bestScore = float.NegativeInfinity;
        Vector2 bestPoint = ClampToPitch(presserXZ + goalSide * preferredDepth, pitch);

        for (int depthStep = 0; depthStep < 3; depthStep++)
        {
            float depth = CoverBaseDepthMeters + depthStep * 2.25f;
            for (int lateralStep = -1; lateralStep <= 1; lateralStep++)
            {
                float lateral = lateralStep * CoverLateralMeters;
                Vector2 point = ClampToPitch(
                    presserXZ + goalSide * depth + tangent * lateral, pitch);

                float shadow = CoverShadow01(
                    point, situation, obs.VisiblePlayers, obs.SelfTeam, obs.AttackingDirectionX, pitch);
                float depthFit = Clamp01(1f - MathF.Abs(depth - preferredDepth) / 6f);
                float central = Clamp01(
                    Vector2.Dot(point - situation.CarrierXZ, goalSide) / 20f);
                float travel = Clamp01(1f - Vector2.Distance(selfXZ, point) / 35f);

                float score = 0.42f * shadow + 0.28f * depthFit + 0.18f * central + 0.12f * travel;
                if (score <= bestScore + 1e-5f) continue;

                bestScore = score;
                bestPoint = point;
            }
        }

        return bestPoint;
    }

    /// <summary>
    /// The point a shape-duty player stands on to screen the opponent's most dangerous forward pass: part
    /// way along the lane from the carrier to the receiver who is furthest up the pitch.
    /// </summary>
    public static bool TryFindScreen(
        in FootballerObservation obs,
        in PressSituation situation,
        PitchDimensions pitch,
        out Vector2 screenXZ,
        out int receiverId,
        out float shadow01)
    {
        screenXZ = default;
        receiverId = 0;
        shadow01 = 0f;
        if (!situation.HasCarrier) return false;

        Vector2 goalSide = GoalSideXZ(situation.CarrierXZ, obs, pitch);
        IReadOnlyList<PerceivedEntity> visible = obs.VisiblePlayers;
        float bestDanger = 0f;
        Vector2 bestReceiver = default;

        for (int i = 0; i < visible.Count; i++)
        {
            PerceivedEntity entity = visible[i];
            if (entity.Team == obs.SelfTeam) continue;
            if (entity.PlayerId == situation.CarrierId) continue;
            if (entity.Role == TacticalRole.Goalkeeper) continue;

            Vector2 receiverXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            float forward = Vector2.Dot(receiverXZ - situation.CarrierXZ, goalSide);
            if (forward < ScreenMinForwardMeters) continue;

            float centrality = 1f - 0.35f * Clamp01(MathF.Abs(receiverXZ.Y) / pitch.HalfWidth);
            float danger = Clamp01(forward / ScreenDangerMeters) * centrality;
            if (danger <= bestDanger + 1e-5f) continue;

            bestDanger = danger;
            bestReceiver = receiverXZ;
            receiverId = entity.PlayerId;
        }

        if (receiverId == 0) return false;

        Vector2 lane = bestReceiver - situation.CarrierXZ;
        screenXZ = ClampToPitch(situation.CarrierXZ + lane * ScreenFraction, pitch);
        shadow01 = LaneBlockage(screenXZ, situation.CarrierXZ, bestReceiver) * bestDanger;
        return true;
    }

    /// <summary>How much of the straight line <paramref name="fromXZ"/>→<paramref name="toXZ"/> a body blocks.</summary>
    private static float LaneBlockage(Vector2 pointXZ, Vector2 fromXZ, Vector2 toXZ)
    {
        Vector2 segment = toXZ - fromXZ;
        float length = segment.Length();
        if (length < 1f) return 0f;

        Vector2 direction = segment / length;
        float projection = Vector2.Dot(pointXZ - fromXZ, direction);
        if (projection <= 0.5f || projection >= length) return 0f;

        float perpendicular = Vector2.Distance(pointXZ, fromXZ + direction * projection);
        return Clamp01(1f - perpendicular / CoverCorridorMeters);
    }

    // ---------------------------------------------------------------------------------------------
    // Evidence, urgency and the role resolution.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The composite strength of the A6.2 evidence, in 0–1.</summary>
    public static float EvidenceStrength(in PressEvidence evidence) => Clamp01(
        BackPassWeight * Clamp01(evidence.BackPass01) +
        PoorTouchWeight * Clamp01(evidence.PoorTouch01) +
        RearFacingWeight * Clamp01(evidence.RearFacing01) +
        SlowPassWeight * Clamp01(evidence.SlowPass01) +
        TouchlineWeight * Clamp01(evidence.Touchline01));

    /// <summary>
    /// The depth the presser stands at: close enough to tackle when somebody covers behind him, a jockey
    /// when he is alone in front of his own goal. This is the whole "an unsupported press can lose to
    /// delay" rule, expressed as a distance rather than as a different action, because the act — closing
    /// the carrier down — is the same either way.
    /// </summary>
    public static float PressStandoffFor(in PressSituation situation) =>
        !situation.HasCoverBehind && situation.BypassRisk01 > JockeyBypassRisk
            ? DelayStandoffMeters
            : PressStandoffMeters;

    /// <summary>The direction from a point toward this player's own goal.</summary>
    public static Vector2 GoalSideXZ(Vector2 fromXZ, in FootballerObservation obs, PitchDimensions pitch) =>
        SimMath.SafeNormalize(
            new Vector2(-obs.AttackingDirectionX * pitch.HalfLength, 0f) - fromXZ,
            new Vector2(-obs.AttackingDirectionX, 0f));

    /// <summary>The human-readable label of an act, as the trace and the inspector show it.</summary>
    public static string ActionNameOf(PressAction action, bool tackle) => action switch
    {
        PressAction.Press => tackle ? "ExecuteTackle" : "CloseDownCarrier",
        PressAction.SupportPress => tackle ? "ChallengeCarrierTackle" : "ChallengeCarrier",
        PressAction.Delay => "DelayCarrier",
        PressAction.Cover => "CoverTheLane",
        PressAction.Screen => "ScreenThePass",
        PressAction.Track => "TrackTheRunner",
        PressAction.Intercept => "InterceptPass",
        PressAction.Drop => "DropAndHold",
        _ => "HoldTheGround"
    };

    // ---------------------------------------------------------------------------------------------
    // Internals.
    // ---------------------------------------------------------------------------------------------

    private static int ResolvePresser(in FootballerObservation obs, Vector2 ballXZ, Vector2 selfXZ)
    {
        if (obs.IsDesignatedBallResponder == true) return obs.SelfId;
        if (obs.BallResponderPlayerId is int responderId && responderId > 0) return responderId;

        // No match-level election behind this controller (standalone probes, fixtures): the nearest
        // outfield team-mate to the ball is the local estimate the controller itself uses.
        int bestId = obs.SelfId;
        float best = Vector2.Distance(selfXZ, ballXZ);

        IReadOnlyList<PerceivedEntity> visible = obs.VisiblePlayers;
        for (int i = 0; i < visible.Count; i++)
        {
            PerceivedEntity entity = visible[i];
            if (entity.Team != obs.SelfTeam) continue;
            if (entity.Role == TacticalRole.Goalkeeper) continue;

            Vector2 mateXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            float distance = Vector2.Distance(mateXZ, ballXZ);
            if (distance < best - 0.05f ||
                (MathF.Abs(distance - best) <= 0.05f && entity.PlayerId < bestId))
            {
                best = distance;
                bestId = entity.PlayerId;
            }
        }

        return bestId;
    }

    private static int ResolveSupport(
        in FootballerObservation obs,
        Vector2 carrierXZ,
        int presserId)
    {
        int bestId = 0;
        float bestDistance = float.MaxValue;

        if (IsSupportEligible(obs.SelfRole) && obs.SelfId != presserId)
        {
            bestId = obs.SelfId;
            bestDistance = Vector2.Distance(
                new Vector2(obs.SelfPosition.X, obs.SelfPosition.Z), carrierXZ);
        }

        IReadOnlyList<PerceivedEntity> visible = obs.VisiblePlayers;
        for (int i = 0; i < visible.Count; i++)
        {
            PerceivedEntity entity = visible[i];
            if (entity.Team != obs.SelfTeam) continue;
            if (entity.PlayerId == presserId) continue;
            if (!IsSupportEligible(entity.Role)) continue;

            Vector2 mateXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            float distance = Vector2.Distance(mateXZ, carrierXZ);
            if (distance < bestDistance - 0.05f ||
                (MathF.Abs(distance - bestDistance) <= 0.05f && entity.PlayerId < bestId))
            {
                bestDistance = distance;
                bestId = entity.PlayerId;
            }
        }

        return bestId;
    }

    /// <summary>
    /// The roles allowed to be the second man. The old body challenge was a centre-back or a holding
    /// midfielder, and that is still the right list: a winger or a striker stepping in beside the press
    /// abandons the part of the pitch the press is trying to win the ball back for.
    /// </summary>
    private static bool IsSupportEligible(TacticalRole role) =>
        role is TacticalRole.CenterBack or TacticalRole.DefensiveMidfielder;

    private static bool HasTeammateBehind(
        in FootballerObservation obs,
        Vector2 presserXZ,
        Vector2 goalSide)
    {
        IReadOnlyList<PerceivedEntity> visible = obs.VisiblePlayers;

        for (int i = 0; i < visible.Count; i++)
        {
            PerceivedEntity entity = visible[i];
            if (entity.Team != obs.SelfTeam) continue;
            if (entity.PlayerId == obs.SelfId) continue;
            if (entity.Role == TacticalRole.Goalkeeper) continue;

            Vector2 mateXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            if (Vector2.Distance(mateXZ, presserXZ) > CoverLinkMeters) continue;

            float behind = Vector2.Dot(mateXZ - presserXZ, goalSide);
            if (behind > CoverBehindMarginMeters) return true;
        }

        return false;
    }

    private static float BypassRisk(
        in FootballerObservation obs,
        Vector2 presserXZ,
        Vector2 carrierXZ,
        int carrierId,
        bool hasCarrier,
        Vector2 goalSide,
        PitchDimensions pitch)
    {
        float risk = 0f;

        IReadOnlyList<PerceivedEntity> visible = obs.VisiblePlayers;
        for (int i = 0; i < visible.Count; i++)
        {
            PerceivedEntity entity = visible[i];
            if (entity.Team == obs.SelfTeam) continue;
            if (entity.Role == TacticalRole.Goalkeeper) continue;
            if (hasCarrier && entity.PlayerId == carrierId) continue;

            Vector2 opponentXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            float beyond = Vector2.Dot(opponentXZ - presserXZ, goalSide);
            if (beyond <= 0f) continue;

            float centrality = 1f - 0.35f * Clamp01(MathF.Abs(opponentXZ.Y) / pitch.HalfWidth);
            risk = MathF.Max(risk, Clamp01(beyond / 25f) * centrality);
        }

        if (hasCarrier && Vector2.Dot(carrierXZ - presserXZ, goalSide) > 0f)
            risk = MathF.Max(risk, 0.5f);

        return Clamp01(risk);
    }

    private static PressEvidence ReadEvidence(
        in FootballerObservation obs,
        bool hasCarrier,
        Vector2 carrierVelocityXZ,
        Vector2 carrierXZ,
        Vector2 ballXZ,
        PitchDimensions pitch)
    {
        bool opponentBall = obs.BallLastTouchedByTeam is TeamId touchingTeam &&
                            touchingTeam != obs.SelfTeam;

        Vector2 ballVelocity = new(obs.PerceivedBallVelocity.X, obs.PerceivedBallVelocity.Z);
        float ballSpeed = ballVelocity.Length();

        float backPass = 0f;
        float slowPass = 0f;
        if (obs.BallControlState == BallControlState.InFlightPass && opponentBall)
        {
            // The opponents attack toward +attackDirX * halfLength (this player's attacking direction is
            // the opposite one), so a pass travelling toward their own goal travels *with* attackDirX.
            backPass = ballVelocity.X * obs.AttackingDirectionX > 0.5f ? 1f : 0f;
            slowPass = Clamp01(1f - ballSpeed / SlowPassSpeedMeters);
        }

        float poorTouch = 0f;
        if (obs.BallControlState == BallControlState.Loose &&
            opponentBall &&
            obs.BallLastTouchedByPlayerId is int toucher &&
            TryFind(obs, toucher, out PerceivedEntity lastToucher) &&
            ballSpeed > PoorTouchMinSpeed)
        {
            Vector2 toucherXZ = new(lastToucher.EstimatedPosition.X, lastToucher.EstimatedPosition.Z);
            float distance = Vector2.Distance(ballXZ, toucherXZ);
            if (distance < PoorTouchRadiusMeters)
            {
                poorTouch = Clamp01(1f - distance / PoorTouchRadiusMeters) *
                            Clamp01(ballSpeed / 6f);
            }
        }

        float rearFacing = 0f;
        if (hasCarrier)
        {
            // The opponent's forward is -attackDirX; moving toward his own goal is moving with attackDirX.
            float backwards = carrierVelocityXZ.X * obs.AttackingDirectionX;
            rearFacing = Clamp01((backwards - 0.5f) / 3.5f);
        }

        Vector2 touchlinePosition = hasCarrier ? carrierXZ : ballXZ;
        float touchline = Touchline01(touchlinePosition, pitch);

        return new PressEvidence(backPass, poorTouch, rearFacing, slowPass, touchline);
    }

    private static float Touchline01(Vector2 positionXZ, PitchDimensions pitch) =>
        Clamp01(1f - (pitch.HalfWidth - MathF.Abs(positionXZ.Y)) / TouchlineWarnMeters);

    private static bool TryFind(in FootballerObservation obs, int playerId, out PerceivedEntity entity)
    {
        IReadOnlyList<PerceivedEntity> visible = obs.VisiblePlayers;
        for (int i = 0; i < visible.Count; i++)
        {
            if (visible[i].PlayerId != playerId) continue;
            entity = visible[i];
            return true;
        }

        entity = default;
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

    private static Vector2 ClampToPitch(Vector2 pointXZ, PitchDimensions pitch) => new(
        Math.Clamp(pointXZ.X, -pitch.HalfLength + 1f, pitch.HalfLength - 1f),
        Math.Clamp(pointXZ.Y, -pitch.HalfWidth + 1f, pitch.HalfWidth - 1f));

    private static float Clamp01(float value) =>
        !float.IsFinite(value) ? 0f : value < 0f ? 0f : value > 1f ? 1f : value;
}
