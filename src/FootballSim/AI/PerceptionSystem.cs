using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Tactics;

namespace FootballSim.AI;

/// <summary>
/// Builds the controller-facing <see cref="FootballerObservation"/> from the authoritative world:
/// per-player position and velocity estimates with confidence-based noise, a reaction-delayed view of
/// the ball, and the ownership/election facts a controller is allowed to know.
///
/// This is the seam between truth and knowledge (plan §3). The engine hands it the real state; every
/// derived value it publishes is what a controller may act on. Extracted verbatim from
/// <see cref="TacticalUtilityAiController"/> in A1.1d so perception and decision can evolve separately.
/// </summary>
public sealed class PerceptionSystem
{
    /// <summary>Longest reaction delay the perception history can represent (audit F18).</summary>
    private const int MaxReactionDelayTicks = 20;

    /// <summary>A ball teleport (restart, kickoff) is not a reaction-worthy movement.</summary>
    private const float TeleportResetDistance = 15f;

    private readonly Dictionary<int, List<(Vector3 Position, Vector3 Velocity)>> _ballHistory = new();
    private readonly Dictionary<int, float> _historyTimestamp = new();

    public static int ReactionDelayTicks(float reactionDelaySeconds) =>
        Math.Clamp((int)MathF.Round(reactionDelaySeconds / SimConstants.FixedDeltaTime), 0, MaxReactionDelayTicks);

    public FootballerObservation BuildObservation(
        FootballerState self,
        IReadOnlyList<FootballerState> allPlayers,
        BallState ball,
        TeamId? confirmedPossessingTeam,
        MatchPhase matchPhase,
        float matchTimeSeconds,
        float attackingDirX,
        bool isSetPieceTaker,
        TeamTacticalProfile teamProfile,
        SeededRandom rng,
        BallPossessionSnapshot? possession = null,
        bool? isDesignatedBallResponder = null,
        TeamJobAssignment? job = null)
    {
        // Standalone controllers (probes, unit tests) have no match-level ownership model; derive a
        // conservative snapshot from the ball so behaviour stays sensible without inventing control.
        BallPossessionSnapshot ballPossession = possession ?? InferPossession(ball);
        var visible = new List<PerceivedEntity>(allPlayers.Count - 1);
        foreach (var other in allPlayers)
        {
            if (other.Id == self.Id || !other.IsActiveOnPitch) continue;

            Vector2 toOther = new(other.Position.X - self.Position.X, other.Position.Z - self.Position.Z);
            float dist = toOther.Length();
            float facingDot = dist > 1e-3f ? Vector2.Dot(toOther / dist, self.FacingXZ) : 1f;

            bool inFrontCone = facingDot >= -0.25f;
            float distanceFactor = Math.Clamp(1.0f - (dist / 65.0f), 0.20f, 1.0f);
            float confidence = inFrontCone ? distanceFactor : distanceFactor * 0.50f;

            float posNoise = (1.0f - confidence) * 0.30f;
            Vector3 estPos = other.Position + new Vector3(
                rng.NextGaussian(0f, posNoise),
                0f,
                rng.NextGaussian(0f, posNoise));

            Vector3 estVel = other.Velocity + new Vector3(
                rng.NextGaussian(0f, posNoise * 0.45f),
                0f,
                rng.NextGaussian(0f, posNoise * 0.45f));

            visible.Add(new PerceivedEntity(other.Id, other.Team, other.Role, estPos, estVel, confidence));
        }

        // Audit F18: reaction delay is a delayed *view* of the ball, not a smaller velocity-noise
        // scale. The player decides from where the ball was ReactionDelaySeconds ago (the execution
        // layer still applies live positions when the ball is actually played). A small velocity
        // noise scaled by reaction quality is kept on top, as before.
        (Vector3 observedBallPos, Vector3 observedBallVel) =
            ObservedBallState(self, ball, matchTimeSeconds);

        float ballDist = Vector3.Distance(self.Position, observedBallPos);
        float ballNoise = Math.Clamp(ballDist / 90.0f, 0.01f, 0.18f) * self.Attributes.ReactionDelaySeconds;
        Vector3 perceivedBallPos = observedBallPos;
        Vector3 perceivedBallVel = observedBallVel + new Vector3(
            rng.NextGaussian(0f, ballNoise),
            rng.NextGaussian(0f, ballNoise),
            rng.NextGaussian(0f, ballNoise));

        return new FootballerObservation
        {
            SelfId = self.Id,
            SelfTeam = self.Team,
            SelfRole = self.Role,
            SelfPosition = self.Position,
            SelfVelocity = self.Velocity,
            SelfFacingXZ = self.FacingXZ,
            Stamina01 = self.Stamina01,
            AvailableToKick = self.AvailableToKick,
            ControlQuality01 = self.ControlQuality01,
            PerceivedBallPosition = perceivedBallPos,
            PerceivedBallVelocity = perceivedBallVel,
            PossessingTeam = confirmedPossessingTeam ?? ballPossession.PossessionTeam ?? ball.LastTouchedByTeam,
            BallLastTouchedByPlayerId = ball.LastTouchedByPlayerId,
            BallLastTouchedByTeam = ball.LastTouchedByTeam,
            BallControlState = ballPossession.State,
            BallControllingPlayerId = ballPossession.ControllingPlayerId,
            IntendedReceiverId = ballPossession.IntendedReceiverId,
            BallHoldRemainingSeconds = ball.HeldByPlayerId == self.Id ? ball.HeldReleaseDelaySeconds : 0f,
            IsDesignatedBallResponder = isDesignatedBallResponder,
            TacticalJob = job?.Job ?? TacticalJob.Shape,
            TacticalJobAnchor = job?.Anchor,
            JobTargetPlayerId = job?.TargetPlayerId,
            JobReason = job?.Reason ?? string.Empty,
            TacticalAnchorPosition = self.TacticalAnchor,
            AttackingDirectionX = attackingDirX,
            MatchTimeSeconds = matchTimeSeconds,
            CurrentMatchPhase = matchPhase,
            IsSetPieceTaker = isSetPieceTaker,
            VisiblePlayers = visible,
            TeamProfile = teamProfile,
            SelfTendencies = self.Tendencies,
            SelfAttributes = self.Attributes
        };
    }

    /// <summary>
    /// Audit F18: the ball state this player can perceive right now. The perception system keeps a
    /// short per-player history keyed by match time, returns the sample from
    /// <see cref="PlayerAttributes.ReactionDelaySeconds"/> ago and tracks it forward with the
    /// perceived velocity, so the delay shows up as a late reaction to a change (a kick, a bounce, a
    /// reversal) rather than as a permanent lag. Until enough history exists (match start) the live
    /// state is used, and a discontinuous ball jump (restart teleport) clears the history.
    /// </summary>
    private (Vector3 Position, Vector3 Velocity) ObservedBallState(
        FootballerState self,
        BallState ball,
        float matchTimeSeconds)
    {
        if (!_ballHistory.TryGetValue(self.Id, out List<(Vector3 Position, Vector3 Velocity)>? history))
        {
            history = new List<(Vector3, Vector3)>(MaxReactionDelayTicks + 1);
            _ballHistory[self.Id] = history;
        }

        bool newTick = !_historyTimestamp.TryGetValue(self.Id, out float lastTime) ||
                       MathF.Abs(lastTime - matchTimeSeconds) > 1e-6f;

        if (newTick)
        {
            if (history.Count > 0)
            {
                Vector3 lastSeen = history[^1].Position;
                if (Vector3.Distance(lastSeen, ball.Position) > TeleportResetDistance)
                    history.Clear();
            }

            history.Add((ball.Position, ball.Velocity));
            if (history.Count > MaxReactionDelayTicks + 1)
                history.RemoveAt(0);
            _historyTimestamp[self.Id] = matchTimeSeconds;
        }

        int delayTicks = ReactionDelayTicks(self.Attributes.ReactionDelaySeconds);
        int index = history.Count - 1 - delayTicks;
        if (index < 0) return (ball.Position, ball.Velocity);

        (Vector3 samplePosition, Vector3 sampleVelocity) = history[index];

        // Track through the delay with the perceived velocity: a player follows steady motion
        // accurately and only reacts late to a *change* (a kick, a bounce, a sudden reversal). Without
        // this the AI would permanently chase a ball that is one reaction-time behind reality —
        // measured as hundreds of out-of-reach kicks per match.
        float delaySeconds = delayTicks * SimConstants.FixedDeltaTime;
        Vector3 predictedPosition = samplePosition + sampleVelocity * delaySeconds;
        return (predictedPosition, sampleVelocity);
    }

    private static BallPossessionSnapshot InferPossession(BallState ball)
    {
        if (ball.HeldByPlayerId is int heldById)
        {
            // Standalone controllers have no ownership tracker; a held ball is authoritative on
            // its own (audit F03).
            return new BallPossessionSnapshot(BallControlState.KeeperHeld, ball.LastTouchedByTeam, heldById, null);
        }

        if (ball.LastTouchedByPlayerId is int playerId &&
            ball.LastTouchedByTeam is TeamId team &&
            new Vector2(ball.Velocity.X, ball.Velocity.Z).Length() <= 2.5f)
        {
            return new BallPossessionSnapshot(BallControlState.Controlled, team, playerId, null);
        }

        return BallPossessionSnapshot.Loose;
    }
}