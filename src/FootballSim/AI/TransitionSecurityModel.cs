using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;

namespace FootballSim.AI;

/// <summary>
/// A3.6: what a team has behind the ball while it attacks, as one picture.
///
/// Rest defence is not a head-count: three players behind the ball all thirty metres apart are not safer
/// than two compact ones, and a side whose deepest man cannot get back has none. So the model reports
/// the parts — and an aggregate — and the allocator gates forward commitments on the aggregate rather
/// than on "a fixed count behind the ball".
/// </summary>
public readonly record struct RestDefence(
    /// <summary>Outfielders goal-side of the ball, at the moment of the evaluation.</summary>
    int BehindBall,
    /// <summary>Slowest of the players ahead of the ball to get goal-side of it, in seconds (0 when nobody is ahead).</summary>
    float WorstRecoverySeconds,
    /// <summary>How cheaply a believed opponent can reach the central corridor unmarked (0 safe, 1 open).</summary>
    float CentralAccess01,
    /// <summary>How open the opponent's first outlet — from the ball to their most advanced teammate — is.</summary>
    float OutletExposure01,
    /// <summary>Share of the believed opponents goal-side of the ball and near our goal.</summary>
    float Threat01,
    /// <summary>Aggregate: 1 fully secure, 0 completely exposed.</summary>
    float Security01)
{
    /// <summary>True when the defence is good enough to commit another player forward.</summary>
    public bool AllowsForwardCommitment => Security01 >= TransitionSecurityModel.MinSecurityForForwardRun;

    /// <summary>
    /// How many forward duties the side can afford right now (A3.6): 0 when exposed, 1 when marginal, 2
    /// when secure. A count that follows the transition picture, not a fixed number behind the ball.
    /// </summary>
    public int ForwardAllowance => Security01 >= TransitionSecurityModel.SecureForTwoForwardRuns
        ? 2
        : Security01 >= TransitionSecurityModel.MinSecurityForForwardRun ? 1 : 0;
}

/// <summary>
/// A3.6: models transition exposure for a team that is in possession.
///
/// The knowledge boundary holds: opponents enter only through <see cref="TeamBelief"/>; our own players
/// are authoritative because they are ours, and the reach estimates go through the one reach model
/// (<see cref="TimeToReachModel"/>) rather than a fifth estimate of how fast a footballer is.
///
/// The three threatening terms are deliberately different questions:
/// <list type="bullet">
/// <item><b>central access</b> — can a believed opponent receive in the middle without a marker?</item>
/// <item><b>outlet exposure</b> — if the ball is lost here, is their first pass out of the area open?</item>
/// <item><b>threat</b> — how many believed opponents are already goal-side of the ball near our goal?</item>
/// </list>
/// </summary>
public static class TransitionSecurityModel
{
    /// <summary>Below this aggregate the allocator stops sending an extra player forward (A3.6).</summary>
    public const float MinSecurityForForwardRun = 0.45f;

    /// <summary>At or above this aggregate two forward duties may be committed; between the thresholds, one.</summary>
    public const float SecureForTwoForwardRuns = 0.65f;

    /// <summary>Recovery time that still counts as safe — a defender who needs longer is a liability.</summary>
    private const float SafeRecoverySeconds = 1.5f;

    /// <summary>Recovery time that counts as fully exposed.</summary>
    private const float ExposedRecoverySeconds = 6.0f;

    /// <summary>Players behind the ball at which the structural term saturates.</summary>
    private const int BehindBallSaturation = 3;

    /// <summary>Central corridor half-width, in metres.</summary>
    private const float CentralCorridorHalfWidth = 12f;

    /// <summary>An unmarked opponent closer than this to our goal counts as a threat.</summary>
    private const float ThreatRangeFromGoal = 42f;

    /// <summary>Nearest-team-mate distance at which a central opponent counts as unmarked.</summary>
    private const float MarkedDistance = 12f;

    /// <summary>Corridor half-width used to judge whether an outlet pass is open, in metres.</summary>
    private const float OutletCorridorHalfWidth = 4f;

    /// <summary>How far goal-side of the ball a recovering player is asked to get, in metres.</summary>
    private const float RecoveryDepthMeters = 10f;

    /// <summary>
    /// Horizon for the recovery estimate. Longer than an interception horizon on purpose: "can he get
    /// back?" is a question about several seconds, not about the next duel.
    /// </summary>
    private const float RecoveryHorizonSeconds = 8f;

    /// <summary>Security is capped here when nobody at all is behind the ball (A3.6).</summary>
    private const float NoCoverCeiling = 0.40f;

    public static RestDefence Evaluate(
        IReadOnlyList<FootballerState> players,
        TeamId team,
        BallState ball,
        TeamBelief opponents,
        float attackDirX,
        PitchDimensions pitch)
    {
        float ballProgress = ball.Position.X * attackDirX;
        Vector2 ballXZ = new(ball.Position.X, ball.Position.Z);
        Vector2 ownGoalXZ = new(-attackDirX * pitch.HalfLength, 0f);

        int behind = 0;
        float worstRecovery = 0f;

        foreach (FootballerState player in players)
        {
            if (player.Team != team || !player.IsActiveOnPitch) continue;
            if (player.Role == TacticalRole.Goalkeeper) continue;

            float progress = player.Position.X * attackDirX;
            if (progress <= ballProgress)
            {
                behind++;
                continue;
            }

            // Ahead of the ball: how long does he need to get back behind it? The target is goal-side of
            // the ball at his own lane, because that is the space a counter-attack runs into.
            Vector2 recoveryPoint = new(
                Math.Clamp(ball.Position.X - attackDirX * RecoveryDepthMeters,
                    -pitch.HalfLength + 2f, pitch.HalfLength - 2f),
                player.Position.Z);
            float seconds = TimeToReachModel
                .ToPoint(ReachKinematics.From(player), recoveryPoint, RecoveryHorizonSeconds)
                .Seconds;
            if (seconds > worstRecovery) worstRecovery = seconds;
        }

        float centralAccess = CentralAccess(players, team, opponents, attackDirX, ballProgress, ownGoalXZ);
        float outletExposure = OutletExposure(players, team, opponents, ballXZ, attackDirX);
        float threat = ThreatShare(opponents, attackDirX, ballProgress, ownGoalXZ);

        float behindFactor = Math.Clamp((float)behind / BehindBallSaturation, 0f, 1f);

        // With nobody behind the ball there is no rest defence to speak of: the recovery term cannot
        // award safety for a side that has nobody back, and the aggregate is capped.
        float recoveryFactor = behind == 0
            ? 0f
            : Math.Clamp(1f - (worstRecovery - SafeRecoverySeconds) / (ExposedRecoverySeconds - SafeRecoverySeconds),
                0f, 1f);

        float security = 0.30f * behindFactor
                       + 0.25f * recoveryFactor
                       + 0.20f * (1f - centralAccess)
                       + 0.15f * (1f - outletExposure)
                       + 0.10f * (1f - threat);

        if (behind == 0) security = MathF.Min(security, NoCoverCeiling);

        return new RestDefence(
            behind,
            worstRecovery,
            centralAccess,
            outletExposure,
            threat,
            Math.Clamp(security, 0f, 1f));
    }

    /// <summary>
    /// How unmarked a believed opponent in the central corridor is, among those goal-side of the ball.
    /// The measure is the best (largest) space any one of them has, because one free central receiver is
    /// enough to break a defence.
    /// </summary>
    private static float CentralAccess(
        IReadOnlyList<FootballerState> players,
        TeamId team,
        TeamBelief opponents,
        float attackDirX,
        float ballProgress,
        Vector2 ownGoalXZ)
    {
        float best = 0f;
        IReadOnlyList<TeamBelief.Opponent> believed = opponents.Opponents;

        for (int i = 0; i < believed.Count; i++)
        {
            TeamBelief.Opponent opponent = believed[i];
            Vector2 position = opponent.PositionXZ;

            if (MathF.Abs(position.Y) > CentralCorridorHalfWidth) continue;
            if (position.X * attackDirX >= ballProgress - 2f) continue;              // not goal-side of the ball
            if (Vector2.Distance(position, ownGoalXZ) > ThreatRangeFromGoal + 18f) continue;

            // Accessibility rises with the distance to the nearest of our players: a central opponent
            // nobody is within marking range of can receive, one with a defender on him cannot.
            float markerDistance = NearestTeammateDistance(players, team, position);
            float access = Math.Clamp(markerDistance / MarkedDistance, 0f, 1f);
            if (access > best) best = access;
        }

        return best;
    }

    /// <summary>
    /// If the ball is lost here, is the opponent's first pass out of the area open? The outlet is the
    /// lane from the ball to their most advanced teammate (furthest from their own goal), judged against
    /// where our own players actually are.
    /// </summary>
    private static float OutletExposure(
        IReadOnlyList<FootballerState> players,
        TeamId team,
        TeamBelief opponents,
        Vector2 ballXZ,
        float attackDirX)
    {
        IReadOnlyList<TeamBelief.Opponent> believed = opponents.Opponents;
        if (believed.Count == 0) return 0f;

        Vector2? outlet = null;
        float bestProgress = float.MinValue;
        for (int i = 0; i < believed.Count; i++)
        {
            float progress = believed[i].PositionXZ.X * attackDirX;
            if (progress <= bestProgress) continue;
            bestProgress = progress;
            outlet = believed[i].PositionXZ;
        }

        if (outlet is not Vector2 target) return 0f;
        if (Vector2.Distance(target, ballXZ) < 1e-3f) return 0f;

        return 1f - LaneOpennessAgainstOurPlayers(players, team, ballXZ, target);
    }

    /// <summary>Share of the believed opponents that are goal-side of the ball and near our goal.</summary>
    private static float ThreatShare(
        TeamBelief opponents,
        float attackDirX,
        float ballProgress,
        Vector2 ownGoalXZ)
    {
        IReadOnlyList<TeamBelief.Opponent> believed = opponents.Opponents;
        if (believed.Count == 0) return 0f;

        int threatening = 0;
        for (int i = 0; i < believed.Count; i++)
        {
            TeamBelief.Opponent opponent = believed[i];
            if (opponent.PositionXZ.X * attackDirX >= ballProgress + 2f) continue;
            if (Vector2.Distance(opponent.PositionXZ, ownGoalXZ) > ThreatRangeFromGoal) continue;
            threatening++;
        }

        return Math.Clamp(threatening / 3f, 0f, 1f);
    }

    private static float NearestTeammateDistance(
        IReadOnlyList<FootballerState> players,
        TeamId team,
        Vector2 point)
    {
        float nearest = float.MaxValue;
        foreach (FootballerState player in players)
        {
            if (player.Team != team || !player.IsActiveOnPitch) continue;
            if (player.Role == TacticalRole.Goalkeeper) continue;

            float distance = Vector2.Distance(new Vector2(player.Position.X, player.Position.Z), point);
            if (distance < nearest) nearest = distance;
        }

        return nearest;
    }

    /// <summary>1 when nothing of ours is within the corridor of the segment, 0 when somebody is on it.</summary>
    private static float LaneOpennessAgainstOurPlayers(
        IReadOnlyList<FootballerState> players,
        TeamId team,
        Vector2 fromXZ,
        Vector2 toXZ)
    {
        Vector2 segment = toXZ - fromXZ;
        float lengthSquared = segment.LengthSquared();
        float openness = 1f;

        foreach (FootballerState player in players)
        {
            if (player.Team != team || !player.IsActiveOnPitch) continue;

            Vector2 point = new(player.Position.X, player.Position.Z);
            float t = lengthSquared > 1e-6f
                ? Math.Clamp(Vector2.Dot(point - fromXZ, segment) / lengthSquared, 0f, 1f)
                : 0f;
            float distance = Vector2.Distance(point, fromXZ + segment * t);
            float clearance = Math.Clamp(distance / OutletCorridorHalfWidth, 0f, 1f);
            if (clearance < openness) openness = clearance;
        }

        return openness;
    }
}
