using System;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;

namespace FootballSim.Presentation;

/// <summary>
/// Upper-body stage: the head (scan, ball gaze, vestibulo-ocular stabilisation) and the stoppage
/// micro-reactions (goal celebration / slump, whistle settle). Split from the locomotion stage because
/// the head must stabilise *against* the body the other stages chose.
/// </summary>
public sealed partial class PlayerDynamicsSolver
{
    /// <summary>Eye height above the player root, used for the ball-bearing gaze.</summary>
    private const float EyeHeight = 1.55f;

    /// <summary>Comfortable neck yaw limit for ball tracking; scans may exceed it because they are deliberate.</summary>
    private static readonly float MaxTrackedYaw = SpringMath.DegToRad(42f);

    /// <summary>Neck look-up limit; a high ball inside 25 m tilts the head up to this.</summary>
    private static readonly float MaxTrackedPitch = SpringMath.DegToRad(30f);

    private const float GoalReactionSeconds = 1.4f;

    private MatchPhase _previousPhase = MatchPhase.PreKickoff;
    private float _stoppageReactionTimer;
    private bool _goalReactionIsCelebration;

    /// <summary>
    /// A7.1: while the player's attention is on a scan, the head is driven by the sim's own head yaw so the
    /// shoulder-check the AI decided is the shoulder-check the viewer sees. Otherwise the head tracks the
    /// ball within a comfortable neck range, and the last part of the body's pitch/roll is cancelled so the
    /// head stays level while the torso works.
    /// </summary>
    private void AccumulateUpperBody(in PlayerDynamicsInput input, ref ChannelTargets targets)
    {
        ScanStatus scan = input.Scan;
        bool scanning = scan.Attention != ScanAttentionKind.None || MathF.Abs(scan.HeadYawDegrees) > 1f;

        if (scanning)
        {
            // Positive ScanStatus.HeadYawDegrees points toward the player's right (+Z); a positive rig
            // rotation about +Y turns left, hence the sign flip (verified in the plan, section 2).
            targets.HeadYaw = -SpringMath.DegToRad(scan.HeadYawDegrees);
        }
        else
        {
            TrackBall(in input, ref targets);
        }

        // Vestibulo-ocular stabilisation: cancel ~45% of the body's pitch/roll on the neck so the head
        // stays readable (and the gaze cue stays level) while the torso leans and banks.
        targets.HeadPitch -= 0.45f * targets.LeanPitch;
        targets.HeadRoll -= 0.45f * targets.LeanRoll;

        AccumulateStoppageReaction(in input, ref targets);
    }

    /// <summary>Subtle ball tracking used when the player is not deliberately scanning.</summary>
    private void TrackBall(in PlayerDynamicsInput input, ref ChannelTargets targets)
    {
        Vector3 eye = input.VisualPosition + new Vector3(0f, EyeHeight, 0f);
        Vector3 toBall = input.BallPosition - eye;
        Vector2 ballXZ = new(toBall.X, toBall.Z);
        float horizontalDistance = ballXZ.Length();
        if (horizontalDistance < 0.2f) return;

        ToLocalAxes(ballXZ, out float forward, out float right);
        // Behind the player there is nothing to look at without a scan; a small backward glance would
        // read as a bug, so tracking only applies in front of the shoulder line.
        if (forward < 0.1f) return;

        float bearing = MathF.Atan2(right, forward);
        targets.HeadYaw = SpringMath.Clamp(-bearing, -MaxTrackedYaw, MaxTrackedYaw);

        if (horizontalDistance <= 25f)
        {
            float elevation = MathF.Atan2(toBall.Y, horizontalDistance);
            targets.HeadPitch = SpringMath.Clamp(elevation * 0.75f, -SpringMath.DegToRad(18f), MaxTrackedPitch);
        }
    }

    /// <summary>
    /// Match-phase micro-reactions. A goal is the only moment the presentation may read the scoreboard:
    /// a short bounce for the scoring team (stronger on the scorer) and a shoulder slump for the conceding
    /// keeper, then the pose settles back on its own springs.
    /// </summary>
    private void AccumulateStoppageReaction(in PlayerDynamicsInput input, ref ChannelTargets targets)
    {
        bool goalJustScored = input.Phase == MatchPhase.GoalScored && _previousPhase != MatchPhase.GoalScored;
        bool stoppageJustStarted = input.Phase != _previousPhase &&
                                   input.Phase is MatchPhase.FreeKick or MatchPhase.PenaltyKick or MatchPhase.HalfTime
                                       or MatchPhase.FullTime;
        _previousPhase = input.Phase;

        if (goalJustScored)
        {
            _stoppageReactionTimer = GoalReactionSeconds;
            _goalReactionIsCelebration =
                input.ScoringTeam.HasValue && input.ScoringTeam.Value == input.Player.Team;
            if (_goalReactionIsCelebration)
            {
                bool isScorer = input.ScorerPlayerId == PlayerId;
                SnapBodyOffsetY(isScorer ? 0.06f : 0.045f);
                SnapSquash(isScorer ? 0.05f : 0.035f);
                SnapTorsoPitch(SpringMath.DegToRad(isScorer ? 18f : 12f));
                SnapHandReach(new Vector3(0.02f, isScorer ? 0.10f : 0.06f, isScorer ? 0.08f : 0.05f));
            }
        }
        else if (stoppageJustStarted && _stoppageReactionTimer <= 0f)
        {
            // Whistle: a brief upright settle, nothing theatrical.
            _stoppageReactionTimer = 0.5f;
            _goalReactionIsCelebration = false;
            SnapTorsoPitch(SpringMath.DegToRad(-8f));
        }

        if (_stoppageReactionTimer <= 0f) return;
        _stoppageReactionTimer = MathF.Max(0f, _stoppageReactionTimer - _dt);
        float progress = 1f - _stoppageReactionTimer / GoalReactionSeconds;

        if (input.Phase != MatchPhase.GoalScored) return;
        bool scored = input.ScoringTeam.HasValue && input.ScoringTeam.Value == input.Player.Team;
        if (scored)
        {
            // Chest open, arms up, and a small bounce while the celebration runs.
            float fade = 1f - SpringMath.Clamp01(progress);
            targets.TorsoPitch += SpringMath.DegToRad(9f) * fade;
            targets.HeadPitch += SpringMath.DegToRad(14f) * fade;
            targets.HandReach += new Vector3(0.05f, 0.26f, 0.12f) * fade;
            if (input.ScorerPlayerId == PlayerId)
                targets.BodyOffsetY += 0.06f * MathF.Abs(MathF.Sin(progress * MathF.PI * 3f)) * fade;
        }
        else if (IsGoalkeeper)
        {
            // The conceding keeper drops his head and shoulders.
            float fade = 1f - SpringMath.Clamp01(progress);
            targets.TorsoPitch -= SpringMath.DegToRad(12f) * fade;
            targets.HeadPitch -= SpringMath.DegToRad(18f) * fade;
            targets.BodyOffsetY -= 0.05f * fade;
        }
    }
}
