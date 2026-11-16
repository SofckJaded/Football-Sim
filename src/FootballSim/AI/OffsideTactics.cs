using System;
using FootballSim.Core;

namespace FootballSim.AI;

/// <summary>
/// The AI's side of Law 11 (A5.4): the tactical margin it keeps <b>before</b> the ball is played, and the
/// one moment it must stop caring about the line at all.
///
/// Two facts this type exists to keep apart:
///
/// <list type="bullet">
/// <item><b>Before release</b> the line is a <i>tactical</i> budget, not the law. The law's own test is the
/// contact snapshot's 0.15 m tolerance — the rules engine freezes who was in an offside position when the
/// ball was played — while the AI's line is built from its own picture: noisy estimates, remembered
/// positions for players out of sight, and a delayed view of the ball. A ball played to a man standing a
/// centimetre inside that line is a coin flip on the AI's own perception, so every point the AI intends to
/// use keeps <see cref="MarginMeters"/> in reserve. One number, used by the pass-target gate, the run
/// region and the job target, so the three can never disagree about whether a receiver is available.</item>
/// <item><b>After release</b> the line stops mattering. <see cref="IsReleased"/> is true once a team-mate
/// has played a pass with this player as its intended receiver: the law judged him at that contact, so he
/// may run beyond the line freely and the AI must not clamp him back. The predicate deliberately says
/// nothing about the line — it is a fact about the ball — which is why a defensive line that stepped up
/// after the pass cannot cancel a legal run.</item>
/// </list>
/// </summary>
public static class OffsideTactics
{
    /// <summary>
    /// Gap the AI keeps between the offside line and a point it intends to play into, in metres.
    ///
    /// Larger than the law's 0.15 m tolerance on purpose. The AI's line comes from what the player can see
    /// of the last two opponents, so it carries a perception error the law's authoritative snapshot does
    /// not; one metre is roughly a stride, which absorbs that error without parking runners behind the
    /// line. It is a policy number, not a physical one — do not widen it to "be safe", and do not shrink it
    /// to the law's tolerance to "be aggressive".
    /// </summary>
    public const float MarginMeters = 1.0f;

    /// <summary>
    /// Extra slack allowed on the meeting region of an <i>adopted</i> run, in metres (A5.2).
    ///
    /// Once a run is adopted, the line moving by a few tens of centimetres must not restart it: a run that
    /// restarts every second is not a run. This is not an eligibility margin — a <b>new</b> ball is judged
    /// by <see cref="MarginMeters"/> — and it is why <see cref="IsReleased"/> exists rather than a tighter
    /// clamp.
    /// </summary>
    public const float AdoptionToleranceMeters = 1.5f;

    /// <summary>
    /// How far behind the line a midfielder's support position is held, in metres. A shape choice (he sits
    /// deeper by design, so the pass back is always on), never an eligibility test.
    /// </summary>
    public const float MidfieldMarginMeters = 2.0f;

    /// <summary>
    /// True when a team-mate has already played a pass with this player as its intended receiver and the
    /// ball is still travelling (A5.4).
    ///
    /// From this moment the offside question is settled: his position when the ball was played is what the
    /// law judged, so he may continue beyond the line and the AI must stop clamping him to it. The predicate
    /// reads the possession model's intended receiver — the one place the simulation states who a pass is
    /// travelling to — so a test can state the fact without a match, and a deflected or loose ball (which
    /// clears the receiver) does not grant it.
    /// </summary>
    public static bool IsReleased(in FootballerObservation obs) =>
        obs.BallControlState == BallControlState.InFlightPass &&
        obs.IntendedReceiverId == obs.SelfId;

    /// <summary>The furthest attack-relative projection a new point may be given before release.</summary>
    public static float MaxAttackingProjection(float offsideLineProjX) => offsideLineProjX - MarginMeters;

    /// <summary>
    /// The offside line as <b>this player's picture</b> puts it: the second-last believed opponent's
    /// projection along the attacking direction, raised to the ball's own projection (a player can never be
    /// offside behind the ball), and never behind the own goal line.
    ///
    /// One implementation for every pass-giver, because they must agree about the same receiver: the carrier
    /// (A5.4), and since A6.B9 the keeper's distribution, which had no offside gate at all and could give away
    /// an indirect free kick in his own half. `fallbackProjX` is the permissive answer used when fewer than
    /// two opponents are believed (the caller passes its pitch's half length), matching the rule the AI has
    /// always used rather than inventing a tighter one from a picture this method cannot trust.
    /// </summary>
    public static float BelievedLineProjX(in FootballerObservation obs, float fallbackProjX)
    {
        float secondLastOpponentProj = fallbackProjX;
        float best = float.NegativeInfinity;
        float secondBest = float.NegativeInfinity;
        int believed = 0;

        // Two largest projections without sorting: no allocation, and the order is the arithmetic's.
        for (int i = 0; i < obs.VisiblePlayers.Count; i++)
        {
            PerceivedEntity entity = obs.VisiblePlayers[i];
            if (entity.Team == obs.SelfTeam) continue;

            float proj = entity.EstimatedPosition.X * obs.AttackingDirectionX;
            believed++;
            if (proj > best)
            {
                secondBest = best;
                best = proj;
            }
            else if (proj > secondBest)
            {
                secondBest = proj;
            }
        }

        if (believed >= 2 && float.IsFinite(secondBest)) secondLastOpponentProj = secondBest;

        float ballProj = obs.PerceivedBallPosition.X * obs.AttackingDirectionX;
        return MathF.Max(0f, MathF.Max(secondLastOpponentProj, ballProj));
    }
}
