using System;
using FootballSim.Core;

namespace FootballSim.AI;

/// <summary>
/// How far off his own goal line the keeper stands, as a duty rather than a leftover (A6.5b).
///
/// Until this the keeper's position was one defensive rule applied in every phase: stand on the line
/// bisecting the angle from the goal centre to the ball, at 10 % of the goal-to-ball distance, clamped to
/// 1.1–4.2 m. That is the right shape for <i>defending</i> a shot. It is the wrong shape for
/// <b>buildup</b>: with the ball in the attacking half the keeper was still four metres off his line, which
/// meant he was never a viable outlet. A4.1 already prices a ball back to the keeper — but only when he is at
/// least <c>CandidateGenerator.KeeperSupportStandoffMeters</c> (12 m) off his line, and the old positioning
/// could not reach that number, so the option existed and could never be used.
///
/// The duty is therefore stated once, here:
/// <list type="bullet">
/// <item><b>Defending.</b> Hold the line and the angle: the defensive standoff, which is the pre-A6.5b rule
/// unchanged.</item>
/// <item><b>In possession.</b> Push up behind the ball so he is a real backward option: a share of the
/// goal-to-ball distance, capped at the depth of his own penalty area. A keeper two metres outside his box
/// is the modern sweeper-keeper, and the cap is the box itself so the law's own boundary is never a surprise:
/// he never steps out of the area to support.</item>
/// </list>
///
/// <b>What this trades.</b> A keeper 16 m off his line has 16 m of goal behind him: a chip over him is a goal,
/// and a turnover takes him two seconds to get back. That is the cost of being an outlet, and it is the
/// keeper's own professional trade — A9.2's ablation is where it gets measured, and this model is the single
/// place the depth is decided, so that ablation can move one number.
///
/// <b>Knowledge boundary.</b> The inputs are the ball's position and whose ball it is — both already in every
/// player's observation. Nothing here reads an opponent's position or intent.
/// </summary>
public static class GoalkeeperSupportModel
{
    /// <summary>
    /// Share of the goal-to-ball distance the keeper stands off his line <b>in possession</b>. With the ball on
    /// the halfway line (about 52 m from his goal) this puts him 10.5 m off it; with the ball in the final
    /// third he is on the edge of his box, which is where A4.1's 12 m support gate starts to offer him.
    /// </summary>
    public const float SupportDepthShare = 0.20f;

    /// <summary>
    /// Deepest support position, in metres: the depth of the penalty area (16.5 m) less a margin, so a
    /// supporting keeper is still inside his own area. He is a keeper first.
    /// </summary>
    public const float MaxSupportDepthMeters = 16f;

    /// <summary>Shallowest support position: he does not leave the goal line for nothing.</summary>
    public const float MinSupportDepthMeters = 2.5f;

    /// <summary>
    /// Share of the goal-to-ball distance the keeper stands off his line <b>defending</b>: the shot-blocking
    /// angle, unchanged from the rule this replaces.
    /// </summary>
    public const float DefensiveDepthShare = 0.10f;

    /// <summary>Shallowest defensive position: on the line is where the goal is.</summary>
    public const float MinDefensiveDepthMeters = 1.1f;

    /// <summary>Deepest defensive position: a keeper who has strayed must not call it a defensive angle.</summary>
    public const float MaxDefensiveDepthMeters = 4.2f;

    /// <summary>
    /// How far off his own goal line the keeper's target sits, in metres. <paramref name="goalToBallMeters"/>
    /// is the planar distance from his own goal centre to the ball.
    /// </summary>
    public static float StandoffMeters(float goalToBallMeters, bool teamHasBall)
    {
        if (!float.IsFinite(goalToBallMeters) || goalToBallMeters <= 0f)
            return teamHasBall ? MinSupportDepthMeters : MinDefensiveDepthMeters;

        return teamHasBall
            ? Math.Clamp(goalToBallMeters * SupportDepthShare, MinSupportDepthMeters, MaxSupportDepthMeters)
            : Math.Clamp(goalToBallMeters * DefensiveDepthShare, MinDefensiveDepthMeters, MaxDefensiveDepthMeters);
    }

    /// <summary>True when this keeper is far enough off his line for a ball back to him to be an outlet.</summary>
    public static bool IsSupportOption(float goalToBallMeters, bool teamHasBall, float requiredDepthMeters) =>
        StandoffMeters(goalToBallMeters, teamHasBall) >= requiredDepthMeters;
}
