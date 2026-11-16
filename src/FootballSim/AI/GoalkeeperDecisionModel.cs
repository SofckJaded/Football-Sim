using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Rules;

namespace FootballSim.AI;

/// <summary>
/// Which of the keeper's two ground-level commitments a race is (A6.5b). Both are the same comparison — can he
/// be at the ball before an opponent — over the same trajectory; what differs is what the ball is.
/// </summary>
public enum GoalkeeperRaceMode
{
    /// <summary>A loose or moving ball: leave the line and get there first (the sweep).</summary>
    Sweep = 0,

    /// <summary>
    /// An opponent has the ball under control inside the area: close him down and block, feet-first. The
    /// keeper does not dive at a dribbler, and the law has already ruled on his hands (A6.6).
    /// </summary>
    Block = 1
}

/// <summary>
/// The sweep/claim race for one ball (A6.5): both sides' arrival at the <b>same</b> point of the ball's real
/// trajectory, and the answer that follows from comparing them.
/// </summary>
public readonly record struct GoalkeeperRace(
    bool ShouldGo,
    GoalkeeperRaceReason Reason,
    float KeeperSeconds,
    float OpponentSeconds,
    Vector3 MeetingPoint,
    int OpponentId,
    GoalkeeperRaceMode Mode = GoalkeeperRaceMode.Sweep)
{
    /// <summary>True when both sides have a time, i.e. the comparison is meaningful.</summary>
    public bool IsContested => KeeperSeconds < float.MaxValue && OpponentSeconds < float.MaxValue;

    /// <summary>Compact text form for a probe or a failure message.</summary>
    public string Describe() => Reason switch
    {
        GoalkeeperRaceReason.KeeperFirst or GoalkeeperRaceReason.Contested or GoalkeeperRaceReason.OpponentFirst
            => $"{Mode} {Reason}: keeper {KeeperSeconds:F2}s vs #{OpponentId} {OpponentSeconds:F2}s" +
               (OpponentId > 0 ? string.Empty : " (nobody)") +
               $" at ({MeetingPoint.X:F1}, {MeetingPoint.Z:F1})",
        _ => $"{Mode} {Reason}"
    };
}

/// <summary>
/// What the keeper expects to do with a delivery he attacks (A6.5b). The two are the same commitment — he goes
/// to the ball — and what differs is what the contact will produce: a ball he can hold is gathered, and a ball
/// arriving too fast to hold, or one he reaches only level with an attacker, is cleared.
///
/// The hold limit is the <b>execution's own</b> (<see cref="BallContactRules.KeeperCatchSpeed"/>), so the mode
/// is a prediction of the real contact rather than a second rule. Recorded because "went for the cross" is not
/// an answer a coach or a probe can use; "gathered it" and "punched it clear" are.
/// </summary>
public enum GoalkeeperClaimMode
{
    /// <summary>Not attacking this ball.</summary>
    Stay = 0,

    /// <summary>Go and gather it: he is there first and the ball is slow enough to hold.</summary>
    Gather = 1,

    /// <summary>Go and clear it: contested, or arriving faster than he can hold.</summary>
    Punch = 2
}

/// <summary>Why the keeper did or did not attack a delivery (A6.5b).</summary>
public enum GoalkeeperClaimReason
{
    /// <summary>There is no delivery to attack: the ball is on the deck.</summary>
    NoDelivery = 0,

    /// <summary>The ball comes down outside his own penalty area, where his hands are not allowed.</summary>
    OutsideArea = 1,

    /// <summary>
    /// The law bars his hands for this ball even inside the area: a team-mate's deliberate kick, or his own
    /// release coming back to him. He may still play it with his head, but a claim is a handling decision.
    /// </summary>
    HandsBarred = 2,

    /// <summary>The landing is further than he will come for it.</summary>
    OutOfRange = 3,

    /// <summary>No prediction was built, so there is nothing to meet.</summary>
    NoTrajectory = 4,

    /// <summary>Nothing inside the horizon he can be at.</summary>
    KeeperCannotReach = 5,

    /// <summary>He can be at the ball before any attacker.</summary>
    KeeperFirst = 6,

    /// <summary>He is level with an attacker: the touch is a contest, and still worth committing to.</summary>
    Contested = 7,

    /// <summary>An attacker is clearly there first: stay and protect the goal.</summary>
    OpponentFirst = 8
}

/// <summary>
/// A delivery in the air and what the keeper will do about it (A6.5b), with the numbers that decided it.
///
/// Until this the keeper had no answer for a cross at all: the sweep's gates were written for a ball already
/// <i>inside</i> the box, so a ball on its way there (the ordinary cross) was never considered, and the
/// handling branch had no way to gather a ball dropping onto him — the execution "controlled" it with a foot.
/// The decision is the same race as the sweep, asked about the point the ball is <b>going</b> to.
/// </summary>
public readonly record struct GoalkeeperClaim(
    bool ShouldAttack,
    GoalkeeperClaimMode Mode,
    GoalkeeperClaimReason Reason,
    float KeeperSeconds,
    float AttackerSeconds,
    int AttackerId,
    Vector3 MeetPoint,
    float ArrivalSpeedMps)
{
    /// <summary>A ball that is not a delivery, or one he is not attacking.</summary>
    public static GoalkeeperClaim None => new(
        ShouldAttack: false, GoalkeeperClaimMode.Stay, GoalkeeperClaimReason.NoDelivery,
        float.MaxValue, float.MaxValue, 0, default, 0f);

    /// <summary>True when both sides have a time, i.e. the race is a real comparison.</summary>
    public bool IsContested => KeeperSeconds < float.MaxValue && AttackerSeconds < float.MaxValue;

    /// <summary>True when the contact he expects is the one that holds the ball.</summary>
    public bool WillHold => Mode == GoalkeeperClaimMode.Gather;

    /// <summary>Compact text form for a probe or a failure message.</summary>
    public string Describe() => Reason switch
    {
        GoalkeeperClaimReason.KeeperFirst or GoalkeeperClaimReason.Contested
            or GoalkeeperClaimReason.OpponentFirst
            => $"{Mode} {Reason}: keeper {KeeperSeconds:F2}s vs #{AttackerId} {AttackerSeconds:F2}s at" +
               $" ({MeetPoint.X:F1}, {MeetPoint.Z:F1}, {MeetPoint.Y:F1}), ball {ArrivalSpeedMps:F1} m/s",
        _ => Reason.ToString()
    };
}

/// <summary>Why a keeper did or did not commit to a ball (A6.5). The reason travels with the answer so a probe, a
/// trace or a failure message can say which input decided it — the same discipline the candidate rejections use.
/// </summary>
public enum GoalkeeperRaceReason
{
    /// <summary>Nothing to predict (the ball is held, or the estimate could not be built).</summary>
    NoTrajectory = 0,

    /// <summary>The ball is not inside this keeper's penalty area, so leaving the line is not a sweep.</summary>
    OutsideArea = 1,

    /// <summary>The ball is inside the area but further than <see cref="SweepCommitRangeMeters"/> away.</summary>
    OutOfRange = 6,

    /// <summary>A friendly loose ball (a back-pass, the keeper's own parry): play it with the feet, never charge it.</summary>
    NotTheOpponentsBall = 2,

    /// <summary>The keeper can be at the ball's real future position first.</summary>
    KeeperFirst = 3,

    /// <summary>An opponent arrives first, but inside the margin: the touch is contested and worth committing to.</summary>
    Contested = 4,

    /// <summary>An opponent is there clearly before the keeper is: do not leave the line.</summary>
    OpponentFirst = 5,

    /// <summary>No meeting inside the horizon at all: the ball is somewhere the keeper cannot get to in time.</summary>
    KeeperCannotReach = 7
}

/// <summary>
/// A shot's arrival at the keeper's goal line, and whether the keeper's body can get to the ball before it crosses
/// (A6.5). A shot the keeper cannot reach is not dived for: the honest answer is to hold position.
/// </summary>
public readonly record struct GoalkeeperShot(
    bool IsOnTarget,
    float GoalLineSeconds,
    Vector3 GoalLinePoint,
    bool CanIntercept,
    float InterceptSeconds,
    Vector3 InterceptPoint,
    float ClosestApproachMeters = 0f,
    GoalkeeperHandlingReason HandlingBlocked = GoalkeeperHandlingReason.Allowed)
{
    /// <summary>A shot nobody has seen yet, or one that is not going in.</summary>
    public static GoalkeeperShot None => default;

    /// <summary>
    /// A6.6: the save the keeper may actually make — the body can reach the interception point <b>and</b>
    /// the law allows him to handle the ball there. A shot can be physically reachable and still not
    /// saveable (a keeper caught outside his own area, or a ball he may not handle at all).
    /// </summary>
    public bool CanSave => CanIntercept && HandlingBlocked == GoalkeeperHandlingReason.Allowed;

    /// <summary>Compact text form for a probe or a failure message.</summary>
    public string Describe() => !IsOnTarget
        ? "not on target"
        : $"on target in {GoalLineSeconds:F2}s at z {GoalLinePoint.Z:F1}, nearest approach" +
          $" {ClosestApproachMeters:F1} m" +
          (CanSave
              ? $", interceptable in {InterceptSeconds:F2}s at ({InterceptPoint.X:F1}, {InterceptPoint.Z:F1})"
              : CanIntercept
                  ? $", reachable but not handleable ({HandlingBlocked})"
                  : ", out of reach");
}

/// <summary>
/// A6.5: the keeper's movement decisions against the ball's <b>real</b> trajectory, as one pure model.
///
/// Before this the keeper made two separate guesses. The sweep race compared constant-velocity meetings, so a
/// ball dipping or bouncing changed the answer without changing the race. The dive extrapolated the flight in a
/// straight line from the last two positions — no gravity, no drag — and then jumped toward a point on a fixed
/// plane 0.9 m inside the goal line, whether or not the body could get there. Both are now the same question
/// asked once: <i>can the keeper be at a point of the ball's actual future path by the time the ball is there?</i>
///
/// The trajectory comes from <see cref="BallTrajectoryEstimate"/>, which rolls the real solver, so this model
/// owns no physics of its own. It owns the two comparisons and their stated margins:
/// <list type="bullet">
/// <item><b>the sweep</b> is a race against the nearest <i>believed</i> opponent over the same trajectory. The
/// keeper commits when he is first, or within <see cref="SweepRaceMarginSeconds"/> of first (a contested touch
/// is still a touch); he does not when an opponent is clearly there first.</item>
/// <item><b>the dive</b> exists only for a ball that is actually going in. If it is, the keeper dives only when
/// the interception point is reachable inside the flight time; otherwise he holds his angle, because a dive that
/// arrives late is a goal with an empty net behind it.</item>
/// </list>
///
/// <b>Knowledge boundary.</b> Opponents enter only through the observation's <see cref="PerceivedEntity"/> list
/// (belief, scouting capability, no live attribute) and the ball through the perceived state. Nothing here reads
/// the authoritative world.
/// </summary>
public static class GoalkeeperDecisionModel
{
    /// <summary>
    /// Highest point a keeper can handle the ball at, in metres (the standing save limit). A6.6: it is the
    /// execution's own contact height (<see cref="BallContactRules.KeeperContactHeight"/>) rather than a
    /// second copy, so the claim filter and the contact that resolves it cannot disagree.
    /// </summary>
    public const float HandlingHeightMeters = BallContactRules.KeeperContactHeight;

    /// <summary>
    /// How far the keeper looks ahead when deciding whether a race is one he can win, in seconds. Shorter
    /// than a pass flight on purpose: a race is a two-second decision, not a plan. A6.5b: the same horizon is
    /// the one trajectory a keeper builds per decision — the sweep, the claim and the dive all read it — and
    /// it is the longest prediction <see cref="BallTrajectoryEstimate"/> can hold (32 samples at 15 Hz).
    /// </summary>
    public const float TrajectoryHorizonSeconds = 2f;

    /// <summary>
    /// How much later than the nearest opponent the keeper still accepts, in seconds. Being level with an
    /// opponent is still a contest for the ball; being clearly behind is not.
    /// </summary>
    public const float SweepRaceMarginSeconds = 0.35f;

    /// <summary>Furthest the keeper travels to a sweep if the race is on, in metres.</summary>
    public const float SweepCommitRangeMeters = 12.5f;

    /// <summary>
    /// Furthest the keeper comes out to meet a delivery in the air, in metres (A6.5b). A claim is a keeper's
    /// own-box action: the penalty area is 16.5 m deep, and a keeper standing a metre off his line reaches
    /// every point of it. It is larger than <see cref="SweepCommitRangeMeters"/> because a claim is not a
    /// sprint into a race on the deck — it is one step and two hands.
    /// </summary>
    public const float ClaimCommitRangeMeters = 15f;

    /// <summary>
    /// Ball height at which a ball counts as a delivery rather than a ball on the deck, in metres (A6.5b). A
    /// rolling ball's centre is ~0.11 m; a bouncing one clears half a metre. Above this the ball is in flight
    /// and the question is where it is going to land.
    /// </summary>
    public const float ClaimAirborneHeightMeters = 0.75f;

    /// <summary>
    /// How far from his own goal line an opponent's ball still gets a trajectory built, in metres (A6.5b).
    /// The cheap gate in front of the one prediction per decision: a delivery that can land in the box inside
    /// the horizon has to be within about this distance, and a ball in the opponent's half cannot.
    /// </summary>
    public const float TrajectoryBuildRangeMeters = 30f;

    /// <summary>How long before the goal line a shot stops being a dive and becomes "too late", in seconds.</summary>
    public const float ShotHorizonSeconds = 0.9f;

    /// <summary>How far outside the posts a ball still counts as a shot on target, in metres.</summary>
    public const float GoalMouthSlackMeters = 0.6f;

    /// <summary>How far above the crossbar a ball still counts as a shot on target, in metres.</summary>
    public const float CrossbarSlackMeters = 0.35f;

    /// <summary>
    /// The sweep/block race for the current ball, over the point the keeper would actually have to be at.
    ///
    /// <b>A6.5b — the gates moved from the ball to the engagement point.</b> The old test asked whether the
    /// <i>ball</i> was inside a narrowed penalty area (a 0.45 width factor), so a ball rolling in from the
    /// corner, a 1v1 at the edge of the box and a delivery aimed at the penalty spot were invisible
    /// even though the point the keeper would meet them at was inside his area. Now the meeting point of the
    /// ball's real trajectory is the thing that is tested — against the law's own area test
    /// (<see cref="GoalkeeperLawModel.IsInsideOwnPenaltyArea"/>), because that is what decides whether his
    /// hands may be used there — and the commit range is measured from him to that point.
    ///
    /// <b>Block or sweep.</b> An opponent with the ball under control inside the area is a 1v1 block: the same
    /// race to the same engagement point, feet-first, the hands only where the law allows them. A loose or
    /// moving ball is a sweep. Both are reported in <see cref="GoalkeeperRace.Mode"/> so a probe can tell them
    /// apart; the commitment itself is the same.
    /// </summary>
    public static GoalkeeperRace EvaluateSweep(
        in FootballerObservation obs,
        BallTrajectoryEstimate trajectory,
        PitchDimensions pitch)
    {
        GoalkeeperRaceMode mode = obs.BallControlState == BallControlState.Controlled &&
                                  obs.PossessingTeam != obs.SelfTeam
            ? GoalkeeperRaceMode.Block
            : GoalkeeperRaceMode.Sweep;

        float ownGoalX = -obs.AttackingDirectionX * pitch.HalfLength;
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);

        if (obs.PossessingTeam == obs.SelfTeam)
            return Unavailable(GoalkeeperRaceReason.NotTheOpponentsBall, mode);
        if (!trajectory.IsBuilt) return Unavailable(GoalkeeperRaceReason.NoTrajectory, mode);

        // The keeper's own meeting, through the one reach model, against the ball's real path.
        if (!trajectory.TryMeet(
                ReachKinematics.FromObservation(obs), TrajectoryHorizonSeconds, HandlingHeightMeters,
                out float keeperSeconds, out Vector3 meetPoint))
        {
            return Unavailable(GoalkeeperRaceReason.KeeperCannotReach, mode);
        }

        // Where he would meet it decides whether the hands are legal there (A6.6) and whether it is close
        // enough to be worth leaving the line for.
        if (!GoalkeeperLawModel.IsInsideOwnPenaltyArea(pitch, ownGoalX, meetPoint))
            return Unavailable(GoalkeeperRaceReason.OutsideArea, mode);

        Vector2 meetXZ = new(meetPoint.X, meetPoint.Z);
        if (Vector2.Distance(selfXZ, meetXZ) > SweepCommitRangeMeters)
            return Unavailable(GoalkeeperRaceReason.OutOfRange, mode);

        // Every believed opponent races to the same trajectory.
        float opponentSeconds = float.MaxValue;
        int opponentId = 0;

        foreach (PerceivedEntity entity in obs.VisiblePlayers)
        {
            if (entity.Team == obs.SelfTeam) continue;
            if (entity.Role == TacticalRole.Goalkeeper) continue;

            if (!trajectory.TryMeet(
                    ReachKinematics.FromEntity(entity), TrajectoryHorizonSeconds, float.MaxValue,
                    out float seconds, out _))
            {
                continue;
            }

            if (opponentId != 0 && seconds >= opponentSeconds) continue;
            opponentSeconds = seconds;
            opponentId = entity.PlayerId;
        }

        if (opponentId == 0)
        {
            return new GoalkeeperRace(
                ShouldGo: true, GoalkeeperRaceReason.KeeperFirst, keeperSeconds, float.MaxValue,
                meetPoint, 0, mode);
        }

        bool first = keeperSeconds <= opponentSeconds;
        bool withinMargin = keeperSeconds <= opponentSeconds + SweepRaceMarginSeconds;
        return new GoalkeeperRace(
            ShouldGo: first || withinMargin,
            // A6.5b: three answers, not two. The old mapping reported "Contested" for a race the keeper was
            // half a second behind as well as for one he was level in, so the only way to read a decline out of
            // the reason was to compare the times by hand — and the probe counted every decline as a contest.
            first ? GoalkeeperRaceReason.KeeperFirst
                : withinMargin ? GoalkeeperRaceReason.Contested
                : GoalkeeperRaceReason.OpponentFirst,
            keeperSeconds,
            opponentSeconds,
            // The keeper meets the ball where *he* can meet it; the opponent's point is the opponent's business.
            meetPoint,
            opponentId,
            mode);
    }

    /// <summary>
    /// Whether the keeper should come and take a delivery out of the air, and what he will do with it if he
    /// gets there (A6.5b).
    ///
    /// The question the sweep could not ask: a cross is judged by where it is <b>going</b>. The ball may be
    /// thirty metres away and still land on the penalty spot inside the prediction horizon, so this reads the
    /// trajectory's meeting point — not the ball's current position — and asks three things of it:
    /// <list type="number">
    /// <item><b>Are his hands legal there?</b> <see cref="GoalkeeperLawModel.EvaluateHandling"/> at the meeting
    /// point (A6.6): the area, and the two last-play restrictions. A crossing ball whose landing is outside the
    /// area, or one a team-mate deliberately kicked back to him, is not his to claim, whatever his feet could do
    /// about it.</item>
    /// <item><b>Can he be there before an attacker?</b> The same race as the sweep, run against the attackers'
    /// <i>heading</i> reach (<see cref="BallContactRules.HeaderContactHeight"/>) rather than their hands, since
    /// that is what an attacker can meet a ball in the air with.</item>
    /// <item><b>Could he hold it?</b> The arrival pace is read off the trajectory and compared with the
    /// execution's own hold limit (<see cref="BallContactRules.KeeperCatchSpeed"/>): a ball faster than he can
    /// hold, or one he reaches only level with an attacker, is a punch rather than a catch.</item>
    /// </list>
    ///
    /// <b>Not read here: the opponents' run announcements.</b> The A5.3 board carries a team's own promises to
    /// itself; an opponent's announcement is not in the keeper's picture, and reading it would be the
    /// knowledge-boundary defect A2.2/A2.6 exists to prevent. What the keeper knows about the attackers is what
    /// he can see of them, and that is what the race uses.
    ///
    /// <b>Whose ball it is is the law's answer, not a second test.</b> A team-mate's header back to him is a
    /// catchable ball; a team-mate's deliberate kick is not; an opponent's cross is either. Asking
    /// <see cref="GoalkeeperLawModel"/> once is what makes those three different answers, so there is no
    /// possession guard in front of it.
    /// </summary>
    public static GoalkeeperClaim EvaluateClaim(
        in FootballerObservation obs,
        BallTrajectoryEstimate trajectory,
        PitchDimensions pitch)
    {
        if (!(obs.PerceivedBallPosition.Y >= ClaimAirborneHeightMeters))
            return GoalkeeperClaim.None;

        float ownGoalX = -obs.AttackingDirectionX * pitch.HalfLength;
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);

        if (!trajectory.IsBuilt)
            return GoalkeeperClaim.None with { Reason = GoalkeeperClaimReason.NoTrajectory };

        if (!trajectory.TryMeet(
                ReachKinematics.FromObservation(obs), TrajectoryHorizonSeconds,
                obs.SelfPosition.Y + BallContactRules.TorsoContactHeight,
                obs.SelfPosition.Y + HandlingHeightMeters,
                out float keeperSeconds, out Vector3 meetPoint, out int meetIndex))
        {
            return GoalkeeperClaim.None with { Reason = GoalkeeperClaimReason.KeeperCannotReach };
        }

        // A6.6/A6.5b: the law is asked about the point he would meet the ball at, not about where the ball is
        // now — that is the whole difference between "the ball is outside my area" and "it is on its way in".
        GoalkeeperHandlingVerdict verdict = GoalkeeperLawModel.EvaluateHandling(
            pitch, ownGoalX, obs.SelfId, obs.SelfTeam, meetPoint, obs.BallLastPlay);
        if (!verdict.MayUseHands)
        {
            return GoalkeeperClaim.None with
            {
                Reason = verdict.Reason == GoalkeeperHandlingReason.OutsideOwnPenaltyArea
                    ? GoalkeeperClaimReason.OutsideArea
                    : GoalkeeperClaimReason.HandsBarred
            };
        }

        Vector2 meetXZ = new(meetPoint.X, meetPoint.Z);
        if (Vector2.Distance(selfXZ, meetXZ) > ClaimCommitRangeMeters)
            return GoalkeeperClaim.None with { Reason = GoalkeeperClaimReason.OutOfRange };

        // Who gets to the ball first? Both sides race the same band — the ball has to be in the air at hand
        // height for it to be a claim at all, so the attacker is filtered to the same window (his top is a
        // standing header, which is 15 cm lower than the keeper's hands). Racing him over the whole flight
        // instead would compare a keeper's hands with an attacker's touch of a ball on the deck, which is the
        // sweep's question and not this one.
        float bandFloor = obs.SelfPosition.Y + BallContactRules.TorsoContactHeight;
        float attackerCeiling = obs.SelfPosition.Y +
                                MathF.Min(BallContactRules.HeaderContactHeight, HandlingHeightMeters);
        float attackerSeconds = float.MaxValue;
        int attackerId = 0;

        foreach (PerceivedEntity entity in obs.VisiblePlayers)
        {
            if (entity.Team == obs.SelfTeam) continue;
            if (entity.Role == TacticalRole.Goalkeeper) continue;

            if (!trajectory.TryMeet(
                    ReachKinematics.FromEntity(entity), TrajectoryHorizonSeconds, bandFloor, attackerCeiling,
                    out float seconds, out _, out _))
            {
                continue;
            }

            if (attackerId != 0 && seconds >= attackerSeconds) continue;
            attackerSeconds = seconds;
            attackerId = entity.PlayerId;
        }

        float arrivalSpeed = trajectory.VelocityAt(meetIndex).Length();
        bool first = attackerId == 0 || keeperSeconds <= attackerSeconds;
        GoalkeeperClaimMode mode = first && arrivalSpeed < BallContactRules.KeeperCatchSpeed(
                obs.SelfAttributes.GoalkeepingReflex01)
            ? GoalkeeperClaimMode.Gather
            : GoalkeeperClaimMode.Punch;

        if (!first && keeperSeconds > attackerSeconds + SweepRaceMarginSeconds)
        {
            return new GoalkeeperClaim(
                ShouldAttack: false, GoalkeeperClaimMode.Stay, GoalkeeperClaimReason.OpponentFirst,
                keeperSeconds, attackerSeconds, attackerId, meetPoint, arrivalSpeed);
        }

        return new GoalkeeperClaim(
            ShouldAttack: true,
            mode,
            first ? GoalkeeperClaimReason.KeeperFirst : GoalkeeperClaimReason.Contested,
            keeperSeconds, attackerSeconds, attackerId, meetPoint, arrivalSpeed);
    }

    /// <summary>
    /// Whether the ball is on target, and whether the keeper's body can reach it before it crosses the line.
    ///
    /// The reach used here is the <b>dive's</b>, not the running reach: a keeper's save is a launch, not a
    /// sprint (<see cref="BallContactRules.KeeperDiveLaunchSpeed"/> plus the diving contact reach), and the old
    /// decision ignored that difference — it dived at a clamped point on a fixed plane and let the execution
    /// sort it out. A shot with a nearest approach beyond the dive reach inside the flight time is reported as
    /// out of reach, and the caller must not dive for it.
    /// </summary>
    public static GoalkeeperShot EvaluateShot(
        in FootballerObservation obs,
        BallTrajectoryEstimate trajectory,
        PitchDimensions pitch)
    {
        if (!trajectory.IsBuilt) return GoalkeeperShot.None;

        float goalLineX = -obs.AttackingDirectionX * pitch.HalfLength;
        if (!trajectory.TryFirstPlaneCrossing(goalLineX, out float seconds, out Vector3 point))
            return GoalkeeperShot.None;
        if (seconds > ShotHorizonSeconds) return GoalkeeperShot.None;

        bool insideMouth = MathF.Abs(point.Z) <= pitch.HalfGoalWidth + GoalMouthSlackMeters &&
                           point.Y <= pitch.GoalHeight + CrossbarSlackMeters &&
                           point.Y >= -0.05f;
        if (!insideMouth) return GoalkeeperShot.None;

        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        if (!trajectory.TryNearestApproach(selfXZ, seconds, out float distance, out float interceptSeconds,
                out Vector3 interceptPoint))
        {
            return GoalkeeperShot.None;
        }

        // A6.B11 was tried here and deliberately reverted: filtering the nearest approach to the samples inside
        // the keeper's standing hand height is not a correction, it is a regression. An on-target ball is
        // descending, so it enters the hands' band before the line even when the sample the prediction picks is
        // above it, and the execution resolves the live contact after the ball has come down. Measured over
        // 4 x 1800 s: with the filter, 7 of 77 on-target shots were dived for and the reference match finished
        // **13-7**; without it, 36 of 67 and **2-1**. The dive is a planar decision by design until a
        // dive-specific height model exists (A9.2's ablation).
        bool canIntercept = distance <= DiveReachMeters(obs, interceptSeconds);

        // A6.5b: a save happens before the ball has crossed the line. The estimate is sampled every 1/15 s, so a
        // 12 m/s shot's first sample is already eighty centimetres inside the goal — and the nearest approach to
        // that sample is then a "reachable" point behind the line. Diving for it is diving into the net at a ball
        // that has already scored; the old straight-line extrapolation happened not to see this because its
        // answer was a point on a fixed plane in front of the goal.
        if (canIntercept)
        {
            bool inFrontOfLine = (interceptPoint.X - goalLineX) * obs.AttackingDirectionX > 0f;
            if (!inFrontOfLine) canIntercept = false;
        }

        // A6.6: reach is not permission. The dive is a hand save, so it exists only where the keeper may
        // use his hands — his own penalty area, and not a ball he is barred from handling (the back-pass
        // clause, or a ball nobody has touched since his own release).
        GoalkeeperHandlingReason handlingBlocked = GoalkeeperHandlingReason.Allowed;
        if (canIntercept)
        {
            handlingBlocked = GoalkeeperLawModel.EvaluateHandling(
                pitch,
                -obs.AttackingDirectionX * pitch.HalfLength,
                obs.SelfId,
                obs.SelfTeam,
                interceptPoint,
                obs.BallLastPlay).Reason;
        }

        return new GoalkeeperShot(
            IsOnTarget: true,
            GoalLineSeconds: seconds,
            GoalLinePoint: point,
            CanIntercept: canIntercept,
            InterceptSeconds: canIntercept ? interceptSeconds : float.MaxValue,
            InterceptPoint: canIntercept ? interceptPoint : Vector3.Zero,
            ClosestApproachMeters: distance,
            HandlingBlocked: handlingBlocked);
    }

    /// <summary>
    /// How far from where he stands the keeper's dive can put his hands, in metres: the launch
    /// (<see cref="BallContactRules.KeeperDiveLaunchSpeed"/>, scaled by reflexes) for the time available, plus the
    /// diving contact reach. The launch speed is the execution's own constant — the save capability is modelled
    /// once and the AI reads it, rather than the AI assuming a sprint it never gets. This is the reach of a dive
    /// that is <i>allowed</i>: whether the hands may be used at that point is
    /// <see cref="GoalkeeperLawModel.EvaluateHandling"/>'s answer (A6.6), reported as
    /// <see cref="GoalkeeperShot.HandlingBlocked"/>.
    /// </summary>
    public static float DiveReachMeters(in FootballerObservation obs, float seconds)
    {
        float time = float.IsFinite(seconds) ? MathF.Max(0f, seconds) : 0f;
        float reflex = Math.Clamp(obs.SelfAttributes.GoalkeepingReflex01, 0.1f, 1f);
        float launch = BallContactRules.KeeperDiveLaunchSpeed * reflex * time;
        float reach = BallContactRules.ReachRadius(
            obs.SelfAttributes.ReachRadius,
            keeperHandling: true,
            isDiving: true,
            requestingTackle: false,
            receivingOrDribbling: true);
        return launch + reach;
    }

    private static GoalkeeperRace Unavailable(GoalkeeperRaceReason reason, GoalkeeperRaceMode mode) => new(
        ShouldGo: false,
        reason,
        KeeperSeconds: float.MaxValue,
        OpponentSeconds: float.MaxValue,
        MeetingPoint: default,
        OpponentId: 0,
        Mode: mode);
}
