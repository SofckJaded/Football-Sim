using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;

namespace FootballSim.AI;

/// <summary>
/// Why a keeper did or did not commit to a ball (A6.5). The reason travels with the answer so a probe, a trace
/// or a failure message can say which input decided it — the same discipline the candidate rejections use.
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
/// The sweep/claim race for one ball (A6.5): both sides' arrival at the <b>same</b> point of the ball's real
/// trajectory, and the answer that follows from comparing them.
/// </summary>
public readonly record struct GoalkeeperRace(
    bool ShouldGo,
    GoalkeeperRaceReason Reason,
    float KeeperSeconds,
    float OpponentSeconds,
    Vector3 MeetingPoint,
    int OpponentId)
{
    /// <summary>True when both sides have a time, i.e. the comparison is meaningful.</summary>
    public bool IsContested => KeeperSeconds < float.MaxValue && OpponentSeconds < float.MaxValue;

    /// <summary>Compact text form for a probe or a failure message.</summary>
    public string Describe() => Reason switch
    {
        GoalkeeperRaceReason.KeeperFirst or GoalkeeperRaceReason.Contested or GoalkeeperRaceReason.OpponentFirst
            => $"{Reason}: keeper {KeeperSeconds:F2}s vs #{OpponentId} {OpponentSeconds:F2}s" +
               (OpponentId > 0 ? string.Empty : " (nobody)") +
               $" at ({MeetingPoint.X:F1}, {MeetingPoint.Z:F1})",
        _ => Reason.ToString()
    };
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
    float ClosestApproachMeters = 0f)
{
    /// <summary>A shot nobody has seen yet, or one that is not going in.</summary>
    public static GoalkeeperShot None => default;

    /// <summary>Compact text form for a probe or a failure message.</summary>
    public string Describe() => !IsOnTarget
        ? "not on target"
        : $"on target in {GoalLineSeconds:F2}s at z {GoalLinePoint.Z:F1}, nearest approach" +
          $" {ClosestApproachMeters:F1} m" +
          (CanIntercept
              ? $", interceptable in {InterceptSeconds:F2}s at ({InterceptPoint.X:F1}, {InterceptPoint.Z:F1})"
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
    /// <summary>Highest point a keeper can handle the ball at, in metres (the standing save limit).</summary>
    public const float HandlingHeightMeters = 2.42f;

    /// <summary>
    /// How far the keeper looks ahead when deciding whether a sweep is a race he can win, in seconds. Shorter
    /// than a pass flight on purpose: a sweep is a two-second decision, not a plan.
    /// </summary>
    public const float SweepRaceHorizonSeconds = 2f;

    /// <summary>
    /// How much later than the nearest opponent the keeper still accepts, in seconds. Being level with an
    /// opponent is still a contest for the ball; being clearly behind is not.
    /// </summary>
    public const float SweepRaceMarginSeconds = 0.35f;

    /// <summary>How far inside the penalty area (in the width direction) a ball still counts as sweepable.</summary>
    public const float SweepAreaWidthFactor = 0.45f;

    /// <summary>Furthest the keeper travels to a sweep if the race is on, in metres.</summary>
    public const float SweepCommitRangeMeters = 12.5f;

    /// <summary>How long before the goal line a shot stops being a dive and becomes "too late", in seconds.</summary>
    public const float ShotHorizonSeconds = 0.9f;

    /// <summary>How far outside the posts a ball still counts as a shot on target, in metres.</summary>
    public const float GoalMouthSlackMeters = 0.6f;

    /// <summary>How far above the crossbar a ball still counts as a shot on target, in metres.</summary>
    public const float CrossbarSlackMeters = 0.35f;

    /// <summary>
    /// The sweep/claim race for the current ball. <paramref name="trajectory"/> must already have been built for
    /// this tick; a false build yields <see cref="GoalkeeperRaceReason.NoTrajectory"/> rather than a guess.
    /// </summary>
    public static GoalkeeperRace EvaluateSweep(
        in FootballerObservation obs,
        BallTrajectoryEstimate trajectory,
        PitchDimensions pitch)
    {
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        float ownGoalX = -obs.AttackingDirectionX * pitch.HalfLength;

        if (obs.PossessingTeam == obs.SelfTeam)
            return Unavailable(GoalkeeperRaceReason.NotTheOpponentsBall);

        bool ballInArea = MathF.Abs(ballXZ.X - ownGoalX) < pitch.PenaltyAreaLength &&
                          MathF.Abs(ballXZ.Y) < pitch.PenaltyAreaWidth * SweepAreaWidthFactor;
        if (!ballInArea) return Unavailable(GoalkeeperRaceReason.OutsideArea);
        if (Vector2.Distance(selfXZ, ballXZ) > SweepCommitRangeMeters)
            return Unavailable(GoalkeeperRaceReason.OutOfRange);
        if (!trajectory.IsBuilt) return Unavailable(GoalkeeperRaceReason.NoTrajectory);

        // The keeper's own meeting, through the one reach model, against the ball's real path.
        if (!trajectory.TryMeet(
                ReachKinematics.FromObservation(obs), SweepRaceHorizonSeconds, HandlingHeightMeters,
                out float keeperSeconds, out Vector3 meetPoint))
        {
            return Unavailable(GoalkeeperRaceReason.KeeperCannotReach);
        }

        // Every believed opponent races to the same trajectory.
        float opponentSeconds = float.MaxValue;
        int opponentId = 0;

        foreach (PerceivedEntity entity in obs.VisiblePlayers)
        {
            if (entity.Team == obs.SelfTeam) continue;
            if (entity.Role == TacticalRole.Goalkeeper) continue;

            if (!trajectory.TryMeet(
                    ReachKinematics.FromEntity(entity), SweepRaceHorizonSeconds, float.MaxValue,
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
                meetPoint, 0);
        }

        bool first = keeperSeconds <= opponentSeconds;
        bool withinMargin = keeperSeconds <= opponentSeconds + SweepRaceMarginSeconds;
        return new GoalkeeperRace(
            ShouldGo: first || withinMargin,
            first ? GoalkeeperRaceReason.KeeperFirst : GoalkeeperRaceReason.Contested,
            keeperSeconds,
            opponentSeconds,
            // The keeper meets the ball where *he* can meet it; the opponent's point is the opponent's business.
            meetPoint,
            opponentId);
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

        bool canIntercept = distance <= DiveReachMeters(obs, interceptSeconds);

        return new GoalkeeperShot(
            IsOnTarget: true,
            GoalLineSeconds: seconds,
            GoalLinePoint: point,
            CanIntercept: canIntercept,
            InterceptSeconds: canIntercept ? interceptSeconds : float.MaxValue,
            InterceptPoint: canIntercept ? interceptPoint : Vector3.Zero,
            ClosestApproachMeters: distance);
    }

    /// <summary>
    /// How far from where he stands the keeper's dive can put his hands, in metres: the launch
    /// (<see cref="BallContactRules.KeeperDiveLaunchSpeed"/>, scaled by reflexes) for the time available, plus the
    /// diving contact reach. The launch speed is the execution's own constant — the save capability is modelled
    /// once and the AI reads it, rather than the AI assuming a sprint it never gets.
    /// </summary>
    public static float DiveReachMeters(in FootballerObservation obs, float seconds)
    {
        float time = float.IsFinite(seconds) ? MathF.Max(0f, seconds) : 0f;
        float reflex = Math.Clamp(obs.SelfAttributes.GoalkeepingReflex01, 0.1f, 1f);
        float launch = BallContactRules.KeeperDiveLaunchSpeed * reflex * time;
        float reach = BallContactRules.ReachRadius(
            obs.SelfAttributes.ReachRadius,
            isGoalkeeper: true,
            isDiving: true,
            requestingTackle: false,
            receivingOrDribbling: true);
        return launch + reach;
    }

    private static GoalkeeperRace Unavailable(GoalkeeperRaceReason reason) => new(
        ShouldGo: false,
        reason,
        KeeperSeconds: float.MaxValue,
        OpponentSeconds: float.MaxValue,
        MeetingPoint: default,
        OpponentId: 0);
}
