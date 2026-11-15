using System;
using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Rules;

/// <summary>
/// Why a goalkeeper may or may not use their hands for a given ball (A6.6, Law 12). The reason travels
/// with the answer so a probe, a trace or a failure message can name the restriction that decided it.
/// </summary>
public enum GoalkeeperHandlingReason
{
    /// <summary>The keeper is inside his own penalty area and no restriction applies: he may handle it.</summary>
    Allowed = 0,

    /// <summary>The ball is not inside the keeper's own penalty area, so he is an ordinary outfield player.</summary>
    OutsideOwnPenaltyArea = 1,

    /// <summary>A team-mate deliberately kicked the ball to him: he must play it with his feet or head.</summary>
    TeamMateDeliberateKick = 2,

    /// <summary>
    /// He released the ball himself and nobody has touched it since: handling it again is a second
    /// handling, whatever the ball is doing (Law 12's "releasing it" clause).
    /// </summary>
    SecondHandling = 3
}

/// <summary>The keeper's handling permission for one ball, with the law that decided it (A6.6).</summary>
public readonly record struct GoalkeeperHandlingVerdict(bool MayUseHands, GoalkeeperHandlingReason Reason)
{
    /// <summary>Hands allowed, nothing restricting.</summary>
    public static GoalkeeperHandlingVerdict Allowed =>
        new(true, GoalkeeperHandlingReason.Allowed);

    /// <summary>Compact text form for a probe or a failure message.</summary>
    public string Describe() => MayUseHands ? "hands allowed" : Reason.ToString();
}

/// <summary>
/// A6.6: the goalkeeper's handling law, in one place, as a pure function of the ball's position and how
/// it was last played.
///
/// Why it is a separate model: the execution layer and the keeper's own decision must agree about what
/// the keeper may do, exactly as they already agree about the dive's launch speed (A6.5). Before this,
/// the answer was spread over a catch branch that only looked at the last toucher's team, a keeper
/// contact reach that applied anywhere on the pitch, and a controller that assumed its handling height
/// everywhere. Three restrictions are modelled, each from Law 12:
///
/// <list type="number">
/// <item><b>Own penalty area.</b> A keeper may use his hands only inside it. Outside it he is an outfield
/// player: the same reach, the same contact height, the same body-orientation rule. This is why the model
/// hands back a <i>verdict</i> instead of a distance — the caller feeds it to
/// <see cref="Locomotion.BallContactRules"/>.</item>
/// <item><b>Deliberate team-mate kick.</b> A ball deliberately kicked (or thrown in) to him by a team-mate
/// may not be handled. A header, a tackle or a deflection off a team-mate is <i>not</i> a kick, so those
/// remain handleable — which is the fidelity the old "any own-team last touch" test could not express.</item>
/// <item><b>Second handling after release.</b> Nobody has touched the ball since the keeper released it,
/// so he may not handle it again, no matter where it is going.</item>
/// </list>
///
/// <b>Enforcement, not adjudication.</b> The restriction is applied as a capability: an illegal handling
/// never happens, so the referee never has to award the indirect free kick it would earn. That is recorded
/// as a law-model limitation in <c>docs/KNOWN_ISSUES.md</c>; it is a decision about the model, not about
/// the tactics that consume it.
///
/// <b>Geometry.</b> "Inside" uses the ball's centre against the area's own extent, lines included, which is
/// the same rule the F12 save gate used. The ball's radius is not subtracted: a ball whose centre is on
/// the area line is inside the area, because the line is part of it.
/// </summary>
public static class GoalkeeperLawModel
{
    /// <summary>
    /// True when <paramref name="location"/> is inside the penalty area in front of
    /// <paramref name="ownGoalX"/> (the keeper's own goal line, signed along X).
    /// </summary>
    public static bool IsInsideOwnPenaltyArea(PitchDimensions pitch, float ownGoalX, Vector3 location)
    {
        ArgumentNullException.ThrowIfNull(pitch);
        return MathF.Abs(location.X - ownGoalX) <= pitch.PenaltyAreaLength &&
               MathF.Abs(location.Z) <= pitch.PenaltyAreaWidth * 0.5f;
    }

    /// <summary>
    /// The keeper's handling permission for this ball. <paramref name="ownGoalX"/> is the X of his own
    /// goal line (<see cref="PitchDimensions.HalfLength"/> or its negative) — the caller owns that fact
    /// because the execution layer reads it from the keeper's position while the AI reads it from the
    /// attacking direction.
    /// </summary>
    public static GoalkeeperHandlingVerdict EvaluateHandling(
        PitchDimensions pitch,
        float ownGoalX,
        int keeperId,
        TeamId keeperTeam,
        Vector3 ballPosition,
        in BallLastPlay lastPlay)
    {
        if (!IsInsideOwnPenaltyArea(pitch, ownGoalX, ballPosition))
            return new GoalkeeperHandlingVerdict(false, GoalkeeperHandlingReason.OutsideOwnPenaltyArea);

        if (lastPlay.IsDeliberateKick && lastPlay.Team == keeperTeam && lastPlay.PlayerId is int playedById)
        {
            return playedById == keeperId
                ? new GoalkeeperHandlingVerdict(false, GoalkeeperHandlingReason.SecondHandling)
                : new GoalkeeperHandlingVerdict(false, GoalkeeperHandlingReason.TeamMateDeliberateKick);
        }

        return GoalkeeperHandlingVerdict.Allowed;
    }
}
