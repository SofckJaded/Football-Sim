using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Tactics;

namespace FootballSim.AI;

public readonly record struct UtilityCandidate(
    string ActionName,
    float UtilityScore,
    FootballerCommand Command
)
{
    /// <summary>
    /// Target point the action is trying to reach, when it has one (movement candidates). Exposed so
    /// behaviour fields (discipline, freedom) and tests can check where the AI is actually going, not
    /// just which direction it faces (audit F18).
    /// </summary>
    public Vector3? TargetXZ { get; init; }
}

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

public sealed class TacticalUtilityAiController : IFootballerController
{
    private const float MaxReceivableSpeed = 8.0f;   // bounded receiver sprint assumption
    private const float ThroughLeadMeters = 2.5f;    // space beyond the runner for through balls

    /// <summary>How far ahead a carry direction is probed against the pitch lines, in metres.</summary>
    private const float CarryProbeMeters = 5.0f;

    /// <summary>Safety margin kept between the carry target and a touch/goal line, in metres.</summary>
    private const float CarryLineMargin = 1.0f;

    /// <summary>
    /// Minimum time between this player's tackle attempts. AI players used to request a tackle on
    /// every tick the ball was in reach, which produced the reported tackle spam between two close
    /// players (the ball went back and forth without either player moving).
    /// </summary>
    private const float TackleCooldownSeconds = 0.55f;

    /// <summary>
    /// How close to the pass line an opponent's body has to be for the ball to run into their reach.
    /// Execution lets an opponent whose body is within <c>ReachRadius × 1.10 ≈ 0.83 m</c> of the ball
    /// play it (a plain touch; see <see cref="BallContactRules"/>), so this leaves a small margin for
    /// the predicted positions.
    /// </summary>
    private const float PassLaneContactReach = 0.9f;

    /// <summary>
    /// How long an opponent needs to notice the pass before they can move toward it. A defender does
    /// not start closing the instant the ball leaves the passer's foot.
    /// </summary>
    private const float PassLaneReactionSeconds = 0.25f;

    /// <summary>
    /// Pace at which an opponent can start closing a pass line even when their current run is not
    /// heading that way (a turn plus the first steps). Added on top of the closing speed they already
    /// carry (itself capped at <see cref="OpponentSprintSpeed"/>).
    /// </summary>
    private const float PassLaneTurnSpeed = 1.5f;

    /// <summary>
    /// Top running pace assumed for an opponent closing a pass lane. Matches the default
    /// <see cref="PlayerAttributes.MaxSprintSpeed"/>: perception carries no attributes for other
    /// players, and assuming the default is deliberately conservative about the passer's options.
    /// </summary>
    private const float OpponentSprintSpeed = 8.4f;

    /// <summary>
    /// Time step used to sample the ball's flight against an opponent's run in the pass-lane test.
    /// 0.1 s keeps the sampled gap well inside the 0.9 m touch reach of a crossing defender.
    /// </summary>
    private const float PassLaneSampleSeconds = 0.1f;

    private readonly PitchDimensions _pitch;
    private readonly MatchCommunicationContext? _communication;
    private readonly PassPlanner _passPlanner;
    private readonly AiRuntimeConfig _ai;
    private string _committedActionName = "Idle";
    private float _commitmentTimer;
    private float _tackleCooldown;
    private float _timeOnBall;
    private string _debugSummary = "Uninitialized";
    private readonly List<UtilityCandidate> _lastCandidates = new();
    private UtilityCandidate _lastSelected;
    private UtilityCandidate? _lastRunnerUp;
    private int _lastCandidateCount;

    public TacticalUtilityAiController(
        PitchDimensions pitch,
        MatchCommunicationContext? communication = null,
        AiRuntimeConfig? ai = null)
    {
        _pitch = pitch;
        _communication = communication;
        _passPlanner = new PassPlanner(pitch);
        _ai = ai ?? AiRuntimeConfig.Default;
    }

    public ControllerSourceType SourceType => ControllerSourceType.AI;
    public IReadOnlyList<UtilityCandidate> LastCandidates => _lastCandidates;

    /// <summary>
    /// The candidate this controller actually chose on its last decision, including the commitment
    /// bonus that was applied to it. Diagnostics read this instead of re-deriving the winner from
    /// <see cref="LastCandidates"/>, which would report the raw table and miss every committed holding.
    /// </summary>
    public UtilityCandidate LastSelected => _lastSelected;

    /// <summary>Best candidate that did not win, after the same bonus was applied.</summary>
    public UtilityCandidate? LastRunnerUp => _lastRunnerUp;

    /// <summary>Number of candidates generated on the last decision.</summary>
    public int LastCandidateCount => _lastCandidateCount;

    /// <summary>
    /// Seconds left on the current commitment window. Exposed so configuration can be proven to reach
    /// the decision path: immediately after a decision that changed action, this equals the configured
    /// job or reaction window exactly.
    /// </summary>
    public float CommitmentRemainingSeconds => _commitmentTimer;

    /// <summary>Name of the action currently held by commitment, or "Idle" before the first decision.</summary>
    public string CommittedActionName => _committedActionName;

    public AiRuntimeConfig RuntimeConfig => _ai;
    public string GetDebugSummary() => _debugSummary;

    public FootballerCommand Decide(in FootballerObservation obs, float dt)
    {
        _commitmentTimer = MathF.Max(0f, _commitmentTimer - dt);
        _tackleCooldown = MathF.Max(0f, _tackleCooldown - dt);
        _lastCandidates.Clear();

        Vector3 oppGoalCenter = new(obs.AttackingDirectionX * _pitch.HalfLength, 0f, 0f);
        Vector3 ownGoalCenter = new(-obs.AttackingDirectionX * _pitch.HalfLength, 0f, 0f);

        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        Vector2 ballVelXZ = new(obs.PerceivedBallVelocity.X, obs.PerceivedBallVelocity.Z);
        float distToBall = Vector2.Distance(selfXZ, ballXZ);

        if (obs.IsSetPieceTaker && distToBall < 4.5f)
        {
            var restartCmd = EvaluateSetPieceRestart(obs, oppGoalCenter);
            _debugSummary = $"SetPieceRestart ({obs.CurrentMatchPhase})";
            return restartCmd;
        }

        // A teammate is genuinely carrying the ball (authoritative control, not mere proximity).
        // Audit F10: the old test was "a teammate is within 1.85 m and the ball is slow", which
        // also matched a defender standing next to a loose ball.
        bool teammateHasControl = obs.BallControlState is BallControlState.Controlled or BallControlState.KeeperHeld
                                  && obs.PossessingTeam == obs.SelfTeam
                                  && obs.BallControllingPlayerId is int controllingId
                                  && controllingId != obs.SelfId;

        // Only treat the ball as a kicked pass if it is traveling faster than sprint speed (> 9.5 m/s) and away from the kicker
        Vector2 myInterceptXZ = obs.PossessingTeam == obs.SelfTeam
            ? ComputeBallInterceptTarget(selfXZ, ballXZ, ballVelXZ, _pitch)
            : ballXZ + ballVelXZ * 0.12f; // Defenders aim directly at the ball carrier!

        // Audit F10: responder election is match-level (one per team, time-to-reach + hysteresis,
        // human not counted as a rival AI). The local estimate is only a fallback for a controller
        // built without a match (probes/tests).
        bool isPrimaryBallResponder = (obs.IsDesignatedBallResponder ?? ComputeLocalResponder(obs, ballXZ, ballVelXZ))
                                      && !teammateHasControl;
        bool onBall = distToBall <= obs.SelfAttributes.ReachRadius * 1.35f && obs.AvailableToKick;

        if (onBall || (obs.BallLastTouchedByPlayerId == obs.SelfId && distToBall < 1.8f))
            _timeOnBall += dt;
        else
            _timeOnBall = 0f;

        float offsideLineProjX = ComputeOffsideLineProjX(obs);

        if (onBall)
        {
            GenerateOnBallCandidates(obs, oppGoalCenter, offsideLineProjX);
        }
        else
        {
            GenerateOffBallCandidates(obs, oppGoalCenter, ownGoalCenter, myInterceptXZ, isPrimaryBallResponder, offsideLineProjX);
        }

        UtilityCandidate best = default;
        UtilityCandidate? runnerUp = null;
        bool hasBest = false;
        foreach (var cand in _lastCandidates)
        {
            float score = cand.UtilityScore;
            if (_commitmentTimer > 0f && cand.ActionName == _committedActionName)
                score += _ai.CommitmentBonus;

            UtilityCandidate scored = cand with { UtilityScore = score };
            if (!hasBest || scored.UtilityScore > best.UtilityScore)
            {
                if (hasBest) runnerUp = best;
                best = scored;
                hasBest = true;
            }
            else if (runnerUp is null || scored.UtilityScore > runnerUp.Value.UtilityScore)
            {
                runnerUp = scored;
            }
        }

        if (!hasBest)
            best = new UtilityCandidate("ActiveSupportMove", -999f, FootballerCommand.Idle);

        // Expose the decision that was actually taken, including the commitment bonus. The trace must
        // report this, not the raw candidate table: the two differ exactly when commitment is doing its
        // job, and a trace that showed the raw winner would describe a decision nobody made.
        _lastSelected = best;
        _lastRunnerUp = runnerUp;
        _lastCandidateCount = _lastCandidates.Count;

        if (best.ActionName != _committedActionName)
        {
            _committedActionName = best.ActionName;
            // A job target is a commitment, not a reflex: hold it longer than a reactive press.
            _commitmentTimer = best.ActionName.StartsWith("Job:", StringComparison.Ordinal)
                ? _ai.JobCommitmentSeconds
                : _ai.ReactionCommitmentSeconds;
        }

        _debugSummary = $"{best.ActionName} [{obs.TacticalJob}] (U={best.UtilityScore:F2})";
        return best.Command;
    }

    /// <summary>
    /// Fallback used only when the observation carries no match-level election: the old
    /// "no closer teammate" estimate. It deliberately excludes the human from being the only
    /// consideration for a loose ball — the match path uses the central election instead.
    /// </summary>
    private bool ComputeLocalResponder(in FootballerObservation obs, Vector2 ballXZ, Vector2 ballVelXZ)
    {
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);

        if (obs.PossessingTeam == obs.SelfTeam)
        {
            foreach (var entity in obs.VisiblePlayers)
            {
                if (entity.Team != obs.SelfTeam) continue;
                float dToBall = Vector2.Distance(new Vector2(entity.EstimatedPosition.X, entity.EstimatedPosition.Z), ballXZ);
                if (dToBall < 1.85f && ballVelXZ.Length() < 9.5f)
                    return false;
            }
        }

        float myInterceptXZ = Vector2.Distance(selfXZ, obs.PossessingTeam == obs.SelfTeam
            ? ComputeBallInterceptTarget(selfXZ, ballXZ, ballVelXZ, _pitch)
            : ballXZ + ballVelXZ * 0.12f);
        bool selfJustPassedBall = obs.BallLastTouchedByPlayerId == obs.SelfId && ballVelXZ.Length() > 9.5f;
        float myEffectiveDist = myInterceptXZ + (selfJustPassedBall ? 18.0f : 0f);

        foreach (var entity in obs.VisiblePlayers)
        {
            if (entity.Team != obs.SelfTeam || entity.Role == TacticalRole.Goalkeeper) continue;
            Vector2 mateXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            Vector2 mateInterceptXZ = obs.PossessingTeam == obs.SelfTeam
                ? ComputeBallInterceptTarget(mateXZ, ballXZ, ballVelXZ, _pitch)
                : ballXZ + ballVelXZ * 0.12f;

            float mateDistToBall = Vector2.Distance(mateXZ, ballXZ);
            bool mateJustPassed = obs.BallLastTouchedByPlayerId == entity.PlayerId && ballVelXZ.Length() > 9.5f && mateDistToBall > 1.2f;
            float mateEffectiveDist = Vector2.Distance(mateXZ, mateInterceptXZ) + (mateJustPassed ? 18.0f : 0f);

            if (mateEffectiveDist < myEffectiveDist - 0.10f ||
                (MathF.Abs(mateEffectiveDist - myEffectiveDist) <= 0.10f && entity.PlayerId < obs.SelfId))
            {
                return false;
            }
        }

        return true;
    }

    private static Vector2 ComputeBallInterceptTarget(Vector2 playerXZ, Vector2 ballXZ, Vector2 ballVelXZ, PitchDimensions pitch)
    {
        float ballSpeed = ballVelXZ.Length();
        if (ballSpeed < 2.2f)
            return ballXZ;

        Vector2 ballDir = ballVelXZ / ballSpeed;
        Vector2 ballToPlayer = playerXZ - ballXZ;
        float projMeters = Vector2.Dot(ballToPlayer, ballDir);

        // If the ball is moving toward the player's general half-plane, step directly onto the perpendicular ray point!
        if (projMeters > 0.5f && projMeters < ballSpeed * 1.8f)
        {
            Vector2 onRay = ballXZ + ballDir * projMeters;
            float perpDist = Vector2.Distance(playerXZ, onRay);
            if (perpDist < 12.0f)
            {
                onRay.X = Math.Clamp(onRay.X, -pitch.HalfLength + 1f, pitch.HalfLength - 1f);
                onRay.Y = Math.Clamp(onRay.Y, -pitch.HalfWidth + 1f, pitch.HalfWidth - 1f);
                return onRay;
            }
        }

        Vector2 fallback = ballXZ + ballVelXZ * 0.45f;
        fallback.X = Math.Clamp(fallback.X, -pitch.HalfLength + 1f, pitch.HalfLength - 1f);
        fallback.Y = Math.Clamp(fallback.Y, -pitch.HalfWidth + 1f, pitch.HalfWidth - 1f);
        return fallback;
    }

    private float ComputeOffsideLineProjX(in FootballerObservation obs)
    {
        var oppCoords = new List<float>();
        foreach (var p in obs.VisiblePlayers)
        {
            if (p.Team == obs.SelfTeam) continue;
            oppCoords.Add(p.EstimatedPosition.X * obs.AttackingDirectionX);
        }
        oppCoords.Sort();
        float secondLastOpponentProj = oppCoords.Count >= 2 ? oppCoords[^2] : _pitch.HalfLength;
        float ballProj = obs.PerceivedBallPosition.X * obs.AttackingDirectionX;
        return MathF.Max(0f, MathF.Max(secondLastOpponentProj, ballProj));
    }

    private void GenerateOnBallCandidates(in FootballerObservation obs, Vector3 oppGoalCenter, float offsideLineProjX)
    {
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 goalXZ = new(oppGoalCenter.X, oppGoalCenter.Z);
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        float distToGoal = Vector2.Distance(selfXZ, goalXZ);
        float myAttackProjX = obs.SelfPosition.X * obs.AttackingDirectionX;
        bool inFinalThird = myAttackProjX > 26.0f;

        // Audit F18: individual tendencies and the team profile now move the utilities. Every term is
        // a delta around the neutral baseline, so default tendencies keep the old behaviour exactly.
        PlayerTendencies tendencies = obs.SelfTendencies;
        TeamTacticalProfile profile = obs.TeamProfile;

        float nearestOppDist = 20.0f;
        foreach (var p in obs.VisiblePlayers)
        {
            if (p.Team == obs.SelfTeam) continue;
            float d = Vector2.Distance(selfXZ, new Vector2(p.EstimatedPosition.X, p.EstimatedPosition.Z));
            if (d < nearestOppDist) nearestOppDist = d;
        }
        bool underHeavyPressure = nearestOppDist < 2.6f;

        // 1. Aggressive Shot Evaluation Inside 23m (Never pass backward in front of goal!)
        if (distToGoal < 23.5f)
        {
            float oppGkZ = 0f;
            foreach (var p in obs.VisiblePlayers)
            {
                if (p.Team != obs.SelfTeam && p.Role == TacticalRole.Goalkeeper)
                {
                    oppGkZ = p.EstimatedPosition.Z;
                    break;
                }
            }

            float targetZ = oppGkZ >= 0f ? -2.95f : 2.95f;
            Vector3 shotTarget = new(oppGoalCenter.X, 1.0f, targetZ);
            Vector2 shotDir2D = Vector2.Normalize(new Vector2(shotTarget.X - obs.SelfPosition.X, shotTarget.Z - obs.SelfPosition.Z));
            float laneClearance = EvaluateLaneClearance(selfXZ, new Vector2(shotTarget.X, shotTarget.Z), obs.VisiblePlayers, obs.SelfTeam);

            // Guarantee high utility inside 18m so players finish chances instead of passing backward.
            // The ball now sticks at the feet (a real first touch), so the carry candidate no longer
            // gives way on its own; finishing has to be worth more than one more touch in the box.
            float baseShotUtility = distToGoal < 16.5f ? 1.30f : 0.95f;
            float shotUtility = baseShotUtility + 0.35f * laneClearance;

            // Shooting appetite: a striker (0.85) pulls the trigger where a defender (0.20) lays it off.
            shotUtility += (tendencies.ShootingAppetite01 - PlayerTendencyBaselines.ShootingAppetite) * 0.40f;
            // Risk tolerance: the more risk-tolerant player takes on the low-percentage shot from range.
            if (distToGoal > 17.0f)
                shotUtility += (tendencies.RiskTolerance01 - PlayerTendencyBaselines.RiskTolerance) * 0.30f;

            var shotCmd = new FootballerCommand(
                shotDir2D, shotDir2D, false, false, false, false, false, Vector3.Zero,
                KickIntentType.Shot,
                new Vector3(shotDir2D.X, 0f, shotDir2D.Y),
                // AI shots sit in the power sweet spot: over ~92 % the new trajectory flies over.
                Math.Clamp(0.80f + distToGoal / 120.0f, 0.80f, 0.90f),
                0f);

            _lastCandidates.Add(new UtilityCandidate("ShootAtGoal", shotUtility, shotCmd)
            {
                TargetXZ = shotTarget
            });
        }

        // 2. Accurate, Onside Passes to Teammate's Feet
        bool humanCalled = _communication?.HasActiveCall == true;
        // Tempo (team): a high-tempo side releases the ball sooner after receiving it.
        float passTriggerSeconds = Math.Clamp(0.45f - 0.30f * (profile.Tempo01 - 0.55f), 0.20f, 0.65f);
        bool readyToPass = _timeOnBall >= passTriggerSeconds || underHeavyPressure || humanCalled;

        if (readyToPass)
        {
            foreach (var mate in obs.VisiblePlayers)
            {
                if (mate.Team != obs.SelfTeam || mate.Role == TacticalRole.Goalkeeper) continue;

                bool isHumanCaller = humanCalled && mate.PlayerId == _communication!.HumanCallForPassPlayerId;
                float mateProjX = mate.EstimatedPosition.X * obs.AttackingDirectionX;

                // STRICT OFFSIDE CHECK: Never pass to an AI teammate who is in an offside position!
                if (!isHumanCaller && mateProjX > offsideLineProjX - 0.35f)
                    continue;

                Vector2 mateCurrXZ = new(mate.EstimatedPosition.X, mate.EstimatedPosition.Z);
                float rawDist = Vector2.Distance(ballXZ, mateCurrXZ);
                if (rawDist < 4.5f || rawDist > 32.0f) continue;

                float forwardMeters = (mate.EstimatedPosition.X - obs.SelfPosition.X) * obs.AttackingDirectionX;

                // BAN backward passes when in front of goal / final third unless heavily surrounded
                if (inFinalThird && forwardMeters < -0.5f && !underHeavyPressure && !isHumanCaller)
                    continue;

                bool playIntoSpace = isHumanCaller && _communication!.HumanCalledThroughBall;
                Vector2 mateVelocityXZ = new(mate.EstimatedVelocity.X, mate.EstimatedVelocity.Z);

                // A through ball is played into the space the runner is moving into, so the lead must
                // follow the runner's run direction - the old code pushed it along the attacking axis,
                // which sent sideways runs the ball toward the goal instead of down the line.
                Vector2 throughLeadDir = mateVelocityXZ.Length() > 0.5f
                    ? Vector2.Normalize(mateVelocityXZ)
                    : new Vector2(obs.AttackingDirectionX, 0f);
                float throughLeadMeters = playIntoSpace ? Math.Clamp(rawDist * 0.28f, 5f, 14f) : 0f;

                // Audit F01: one shared planner decides both the receiving point and the power. It aims
                // from the ball (not the player's centre) and leads the receiver by the modelled flight
                // time — the old code advanced the receiver by at most 0.35 s while the ball was in
                // flight for well over a second (measured 8.07 m miss on a 20 m pass).
                //
                // A human receiver can turn or stop at any moment, so their lead is bounded hard. With
                // an unbounded lead a sprinting caller was "led" 15+ m upfield, which reads in play as
                // the pass being aimed at nobody.
                float maxLeadMeters = isHumanCaller ? (playIntoSpace ? 10f : 5f) : 60f;
                // Scale the minimum power with distance: a crisp pass to a caller keeps the flight (and
                // therefore the unpredictable lead) short, without rocketing a short pass at their shins.
                float callerMinPower = isHumanCaller ? Math.Clamp(0.30f + rawDist / 40f, 0.35f, 0.75f) : 0f;
                var plan = _passPlanner.PlanGroundPass(
                    ballXZ,
                    mate.PlayerId,
                    mateCurrXZ,
                    mateVelocityXZ,
                    MaxReceivableSpeed,
                    throughLeadMeters,
                    throughLeadDir,
                    maxLeadMeters,
                    callerMinPower);

                Vector2 targetXZ;
                float passDist;
                Vector2 aimDir;
                float power01;
                float flightTime = 0f;

                if (plan.Feasible)
                {
                    targetXZ = plan.TargetXZ;
                    passDist = plan.DistanceMeters;
                    aimDir = plan.AimDirectionXZ;
                    power01 = plan.Power01;
                    flightTime = plan.FlightTimeSeconds;
                }
                else if (isHumanCaller)
                {
                    // The caller asked for the ball. Never silently drop the request and pass to
                    // somebody else: fall back to a short, old-style lead so the ball still arrives
                    // within a couple of metres of the caller.
                    Vector2 fallbackLead = mateVelocityXZ * 0.35f;
                    if (fallbackLead.Length() > 2.5f)
                        fallbackLead = SimMath.SafeNormalize(fallbackLead, Vector2.Zero) * 2.5f;

                    targetXZ = mateCurrXZ + fallbackLead;
                    passDist = Vector2.Distance(ballXZ, targetXZ);
                    aimDir = SimMath.SafeNormalize(targetXZ - ballXZ, new Vector2(obs.AttackingDirectionX, 0f));
                    power01 = Math.Clamp(0.25f + passDist / 55f, 0.25f, 0.70f);
                }
                else
                {
                    continue;
                }

                // Lane clearance must be judged against the point the pass is actually aimed at.
                float laneOpenness = EvaluateLaneClearance(ballXZ, targetXZ, obs.VisiblePlayers, obs.SelfTeam);
                if (laneOpenness < 0.52f && !isHumanCaller) continue;

                // Reported from playtesting: the AI still played passes straight into a defender.
                // The blocked-line test knows the flight time, so a slow pass (which gives defenders
                // time to step in) is abandoned from further away than a firm one. A human who
                // called for the ball is still served: their request is never dropped.
                if (!isHumanCaller &&
                    IsPassLaneBlocked(ballXZ, targetXZ, power01, flightTime, obs.VisiblePlayers, obs.SelfTeam))
                {
                    continue;
                }

                float progressionScore = forwardMeters >= 0f
                    ? Math.Clamp(forwardMeters / 20.0f, 0.15f, 0.52f)
                    : -0.30f;

                float passUtility = 0.48f * laneOpenness + progressionScore + (passDist <= 20.0f ? 0.18f : 0.04f);
                if (isHumanCaller) passUtility += 0.75f;

                // Risk tolerance (player + team): a tight lane is a risk worth taking for a risk-taker
                // and a reason to keep the ball for a safe passer. Neutral defaults change nothing.
                float laneRisk = 1f - laneOpenness;
                passUtility += (tendencies.RiskTolerance01 - PlayerTendencyBaselines.RiskTolerance) * 0.40f * laneRisk;
                passUtility += (profile.RiskTolerance01 - 0.50f) * 0.30f * laneRisk;

                // Direct pass preference: how much a forward pass is worth to this player.
                float forwardRatio = Math.Clamp(forwardMeters / 20.0f, -1f, 1f);
                passUtility += (tendencies.DirectPassPreference01 - PlayerTendencyBaselines.DirectPassPreference)
                                * 0.30f * forwardRatio;

                KickIntentType passType = playIntoSpace ? KickIntentType.ThroughPass : KickIntentType.GroundPass;

                var passCmd = new FootballerCommand(
                    aimDir, aimDir, false, false, false, false, false, Vector3.Zero,
                    passType, new Vector3(aimDir.X, 0f, aimDir.Y), power01, 0f)
                {
                    IntendedReceiverId = mate.PlayerId
                };

                _lastCandidates.Add(new UtilityCandidate($"Pass({passType})To#{mate.PlayerId}", passUtility, passCmd));
            }
        }

        // 3. Drive Forward Toward Goal When in Open Space
        Vector2 idealDriveTarget = obs.SelfRole == TacticalRole.Winger && !inFinalThird
            ? new Vector2(oppGoalCenter.X * 0.85f, MathF.Sign(obs.SelfPosition.Z) * (_pitch.HalfWidth - 6.0f))
            : goalXZ;

        Vector2 forwardDir = SimMath.SafeNormalize(idealDriveTarget - selfXZ, new Vector2(obs.AttackingDirectionX, 0f));
        Vector2 bestDribbleDir = forwardDir;
        float bestCarryScore = ScoreCarryDirection(obs, selfXZ, forwardDir, out float bestSpaceScore);

        // Candidate carries around the forward direction. The old set only looked at open space,
        // so near a touchline the *emptiest* direction was out of the pitch and the AI dutifully
        // carried the ball out of play. Boundary safety is part of the score now.
        ReadOnlySpan<float> angles = [-0.45f, 0.45f, -0.85f, 0.85f, -1.25f, 1.25f];
        foreach (float ang in angles)
        {
            Vector2 candDir = RotateXZ(forwardDir, ang);
            float score = ScoreCarryDirection(obs, selfXZ, candDir, out float space);
            if (score > bestCarryScore)
            {
                bestCarryScore = score;
                bestDribbleDir = candDir;
                bestSpaceScore = space;
            }
        }

        // Boxed in (a corner, or pinned on the line): turn back toward the pitch instead of walking
        // straight out of play. A pass or a shot will normally have won the decision by then.
        if (bestCarryScore < 0f)
        {
            bestDribbleDir = SimMath.SafeNormalize(
                new Vector2(-selfXZ.X, -selfXZ.Y), new Vector2(-obs.AttackingDirectionX, 0f));
            bestSpaceScore = 0.5f;
        }

        Vector2 carryTarget = new(
            Math.Clamp(selfXZ.X + bestDribbleDir.X * 4.5f, -_pitch.HalfLength + CarryLineMargin, _pitch.HalfLength - CarryLineMargin),
            Math.Clamp(selfXZ.Y + bestDribbleDir.Y * 4.5f, -_pitch.HalfWidth + CarryLineMargin, _pitch.HalfWidth - CarryLineMargin));
        bestDribbleDir = SimMath.SafeNormalize(carryTarget - selfXZ, bestDribbleDir);

        // The ball sits just ahead of the feet, so being pinned on the line is also a reason to
        // release it: a pass out of the pressure is worth more than another carry into the line.
        float lineMargin = MathF.Min(
            _pitch.HalfLength - MathF.Abs(ballXZ.X),
            _pitch.HalfWidth - MathF.Abs(ballXZ.Y));
        float linePressure = Math.Clamp(1f - lineMargin / 3.0f, 0f, 1f);

        float carryBonus = (_timeOnBall < 0.70f || inFinalThird) ? 0.25f : 0.0f;
        float dribbleUtility = 0.50f + 0.40f * bestSpaceScore + carryBonus;
        dribbleUtility -= 0.30f * linePressure;
        // Dribble preference: a dribbler carries the ball where a passer would have released it.
        dribbleUtility += (tendencies.DribblePreference01 - PlayerTendencyBaselines.DribblePreference) * 0.45f;
        bool sprintDribble = bestSpaceScore > 0.70f && nearestOppDist > 5.0f;

        var dribbleCmd = new FootballerCommand(
            bestDribbleDir, bestDribbleDir, sprintDribble, nearestOppDist < 2.0f,
            false, false, false, Vector3.Zero, KickIntentType.None, Vector3.UnitX, 0f, 0f);

        _lastCandidates.Add(new UtilityCandidate("DriveWithBall", dribbleUtility, dribbleCmd)
        {
            TargetXZ = new Vector3(carryTarget.X, 0f, carryTarget.Y)
        });
    }

    /// <summary>
    /// Scores a candidate carry direction: open space plus how safely it stays inside the pitch.
    /// A direction whose 5 m probe (and the ball just ahead of the player) would leave the field
    /// scores negative, so it can never win. <paramref name="space"/> stays the opponent-distance
    /// term on its own for the sprint decision.
    /// </summary>
    private float ScoreCarryDirection(
        in FootballerObservation obs,
        Vector2 selfXZ,
        Vector2 dir,
        out float space)
    {
        Vector2 probe = selfXZ + dir * CarryProbeMeters;
        Vector2 ballProbe = probe + dir * 0.55f; // the ball is carried just ahead of the feet
        space = EvaluateSpaceFreedom(probe, obs.VisiblePlayers, obs.SelfTeam);

        if (!IsInsidePitch(probe, 1.2f) || !IsInsidePitch(ballProbe, 0.2f))
            return -1f;

        float marginX = _pitch.HalfLength - MathF.Abs(probe.X);
        float marginZ = _pitch.HalfWidth - MathF.Abs(probe.Y);
        float boundarySafety = Math.Clamp(MathF.Min(marginX, marginZ) / 3.0f, 0f, 1f);
        return 0.55f * space + 0.45f * boundarySafety;
    }

    /// <summary>True when a point is inside the pitch with the requested margin from every line.</summary>
    private bool IsInsidePitch(Vector2 pointXZ, float margin) =>
        MathF.Abs(pointXZ.X) <= _pitch.HalfLength - margin &&
        MathF.Abs(pointXZ.Y) <= _pitch.HalfWidth - margin;

    private static Vector2 RotateXZ(Vector2 dir, float radians)
    {
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        return new Vector2(dir.X * cos - dir.Y * sin, dir.X * sin + dir.Y * cos);
    }

    private void GenerateOffBallCandidates(
        in FootballerObservation obs,
        Vector3 oppGoalCenter,
        Vector3 ownGoalCenter,
        Vector2 myInterceptXZ,
        bool isPrimaryBallResponder,
        float offsideLineProjX)
    {
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        Vector2 anchorXZ = new(obs.TacticalAnchorPosition.X, obs.TacticalAnchorPosition.Z);
        Vector2 dirToBall = Vector2.Distance(selfXZ, ballXZ) > 1e-4f ? Vector2.Normalize(ballXZ - selfXZ) : new Vector2(obs.AttackingDirectionX, 0f);

        // 1. Primary Ball Responder: Receive Pass (if in possession) OR Sprint & Tackle Carrier (if defending)
        if (isPrimaryBallResponder)
        {
            float distToBallNow = Vector2.Distance(selfXZ, ballXZ);

            if (obs.PossessingTeam != obs.SelfTeam)
            {
                // Defending: cut the carrier's path off instead of chasing the ball from behind. A
                // pure chase at the same speed can never close the last metre, which is why a single
                // carrier used to run through the whole team.
                Vector2 goalSide = SimMath.SafeNormalize(
                    new Vector2(-obs.AttackingDirectionX * _pitch.HalfLength, 0f) - ballXZ,
                    new Vector2(-obs.AttackingDirectionX, 0f));

                Vector2 chasePoint;
                if (obs.BallControlState == BallControlState.Controlled &&
                    obs.BallControllingPlayerId is int carrierId &&
                    TryGetVisibleEntity(obs, carrierId, out Vector2 carrierXZ, out Vector2 carrierVelXZ))
                {
                    Vector2 selfToCarrier = SimMath.SafeNormalize(carrierXZ - selfXZ, goalSide);
                    bool alreadyGoalSide = Vector2.Dot(selfToCarrier, goalSide) < -0.15f;
                    chasePoint = alreadyGoalSide
                        ? carrierXZ + goalSide * 1.2f
                        : PursuitPoint(selfXZ, carrierXZ, carrierVelXZ, obs.SelfAttributes.MaxSprintSpeed) + goalSide * 1.0f;
                }
                else
                {
                    chasePoint = ballXZ
                                 + new Vector2(obs.PerceivedBallVelocity.X, obs.PerceivedBallVelocity.Z) * 0.15f
                                 + goalSide * 0.6f;
                }

                Vector2 toBallDirect = chasePoint - selfXZ;
                Vector2 pressDir = toBallDirect.LengthSquared() > 1e-4f ? Vector2.Normalize(toBallDirect) : dirToBall;
                // Audit F18: pressing aggression moves the commitment range (~1.24 m at the 0.60 baseline).
                float tackleTriggerMultiplier = 1.65f
                    + 0.35f * (obs.SelfTendencies.PressingAggression01 - PlayerTendencyBaselines.PressingAggression);
                bool attemptTackle = _tackleCooldown <= 0f &&
                                     distToBallNow <= obs.SelfAttributes.ReachRadius * tackleTriggerMultiplier;
                if (attemptTackle) _tackleCooldown = TackleCooldownSeconds;

                var pressCmd = new FootballerCommand(
                    pressDir, dirToBall, true, false,
                    attemptTackle, false, false, Vector3.Zero,
                    KickIntentType.None, Vector3.UnitX, 0f, 0f);

                _lastCandidates.Add(new UtilityCandidate(attemptTackle ? "ExecuteTackle" : "CloseDownCarrier", 0.99f, pressCmd));
                return;
            }

            // Receiving a friendly pass: step onto the pass ray and cushion it
            Vector2 toIntercept = myInterceptXZ - selfXZ;
            float distIntercept = toIntercept.Length();
            Vector2 moveDir = distIntercept > 0.45f
                ? toIntercept / distIntercept
                : dirToBall * 0.35f;

            var receiveCmd = new FootballerCommand(
                moveDir, dirToBall, distIntercept > 1.5f, false,
                false, false, false, Vector3.Zero,
                KickIntentType.None, Vector3.UnitX, 0f, 0f);

            _lastCandidates.Add(new UtilityCandidate("InterceptPassRay", 0.98f, receiveCmd));
            return;
        }

        // 1b. Second defender: the elected responder alone is not a defence. When an opponent is
        //     on the ball, the two closest defenders also step in and challenge; everyone else
        //     keeps the block. This is what stops one carrier walking through the middle.
        if (TryAddCarrierChallenge(obs, ownGoalCenter))
            return;

        // 2. Job-directed movement (audit F04). The team assigns a persistent job (outlet, third
        //    player, depth run, width, overlap, cover, marker...); score a couple of reachable
        //    target points around its anchor instead of a single formation step.
        if (obs.TacticalJobAnchor is Vector3 jobAnchor &&
            obs.TacticalJob is not (TacticalJob.Shape or TacticalJob.DefensiveLine))
        {
            AddJobCandidates(obs, new Vector2(jobAnchor.X, jobAnchor.Z), offsideLineProjX);
        }

        // 3. All other 9 outfield players hold lanes, offer clean angles, and STAY ONSIDE!
        Vector2 targetSpotXZ = anchorXZ;

        if (obs.PossessingTeam == obs.SelfTeam)
        {
            float phaseWave = obs.MatchTimeSeconds * 1.2f + obs.SelfId * 1.7f;
            if (obs.SelfRole is TacticalRole.Striker or TacticalRole.Winger)
            {
                float desiredProjX = (anchorXZ.X * obs.AttackingDirectionX) + 3.5f + 2.5f * MathF.Sin(phaseWave);
                // Strictly clamp forward runs 1.0m behind the offside line so attackers never camp offside!
                float onsideProjX = MathF.Min(desiredProjX, offsideLineProjX - 1.0f);
                targetSpotXZ.X = obs.AttackingDirectionX * Math.Clamp(onsideProjX, -_pitch.HalfLength + 4f, _pitch.HalfLength - 4f);
            }
            else if (obs.SelfRole == TacticalRole.CentralMidfielder)
            {
                float desiredProjX = (anchorXZ.X * obs.AttackingDirectionX) + 2.5f * MathF.Cos(phaseWave);
                float onsideProjX = MathF.Min(desiredProjX, offsideLineProjX - 2.0f);
                targetSpotXZ.X = obs.AttackingDirectionX * Math.Clamp(onsideProjX, -_pitch.HalfLength + 5f, _pitch.HalfLength - 5f);
            }
        }

        // Anti-crowding repulsion so teammates keep clean passing spacing
        Vector2 repulsion = Vector2.Zero;
        foreach (var mate in obs.VisiblePlayers)
        {
            if (mate.Team != obs.SelfTeam || mate.Role == TacticalRole.Goalkeeper) continue;
            Vector2 mateXZ = new(mate.EstimatedPosition.X, mate.EstimatedPosition.Z);
            Vector2 awayFromMate = selfXZ - mateXZ;
            float d = awayFromMate.Length();
            if (d < 8.5f && d > 1e-3f)
            {
                repulsion += (awayFromMate / d) * ((8.5f - d) * 1.25f);
            }
        }

        targetSpotXZ += repulsion;

        // Audit F18: positional discipline (player) and positional freedom (team) bound how far a
        // player strays from the formation anchor when taking up a support spot. At the neutral
        // baselines (discipline 0.70, freedom 0.55) this allows ~7 m of drift; a free role may roam
        // further, a disciplined one holds the structure.
        float anchorDriftMeters = (2.5f + 12f * (1f - obs.SelfTendencies.PositionalDiscipline01))
                                  * (0.70f + 0.80f * obs.TeamProfile.PositionalFreedom01);
        Vector2 driftVec = targetSpotXZ - anchorXZ;
        float driftDist = driftVec.Length();
        if (driftDist > anchorDriftMeters && driftDist > 1e-3f)
            targetSpotXZ = anchorXZ + (driftVec / driftDist) * anchorDriftMeters;

        // Re-enforce onside limit after repulsion when in possession
        if (obs.PossessingTeam == obs.SelfTeam && (targetSpotXZ.X * obs.AttackingDirectionX) > offsideLineProjX - 0.85f)
        {
            targetSpotXZ.X = obs.AttackingDirectionX * (offsideLineProjX - 0.85f);
        }

        targetSpotXZ.X = Math.Clamp(targetSpotXZ.X, -_pitch.HalfLength + 3.5f, _pitch.HalfLength - 3.5f);
        targetSpotXZ.Y = Math.Clamp(targetSpotXZ.Y, -_pitch.HalfWidth + 2.5f, _pitch.HalfWidth - 2.5f);

        Vector2 toTarget = targetSpotXZ - selfXZ;
        float distTarget = toTarget.Length();
        Vector2 moveVec = distTarget > 0.45f
            ? (toTarget / distTarget) * Math.Clamp(distTarget / 3.5f, 0.35f, 1.0f)
            : Vector2.Zero;

        var shapeCmd = new FootballerCommand(
            moveVec, dirToBall, distTarget > 8.0f, false,
            false, false, false, Vector3.Zero, KickIntentType.None, Vector3.UnitX, 0f, 0f);

        _lastCandidates.Add(new UtilityCandidate(obs.PossessingTeam == obs.SelfTeam ? "OnsideSupportLane" : "HoldDefensiveZone", 0.88f, shapeCmd)
        {
            TargetXZ = new Vector3(targetSpotXZ.X, 0f, targetSpotXZ.Y)
        });
    }

    /// <summary>
    /// Adds a "step in and challenge" action for the second-closest defender when an opponent is
    /// carrying the ball. Returns true when this player is one of the two closest defenders within
    /// engagement range, in which case the challenge is his decision for this tick.
    /// </summary>
    private bool TryAddCarrierChallenge(in FootballerObservation obs, Vector3 ownGoalCenter)
    {
        if (obs.PossessingTeam == obs.SelfTeam) return false;
        if (obs.SelfRole == TacticalRole.Goalkeeper) return false;
        // Central cover only: a winger stepping inside to double up would abandon the touchline
        // (and the team's width), which is how a defensive fix becomes an attacking regression.
        if (obs.SelfRole is not (TacticalRole.CenterBack or TacticalRole.DefensiveMidfielder)) return false;
        if (obs.BallControlState != BallControlState.Controlled) return false;
        if (obs.BallControllingPlayerId is not int carrierId || carrierId == obs.SelfId) return false;
        if (!TryGetVisibleEntity(obs, carrierId, out Vector2 carrierXZ, out Vector2 carrierVelXZ)) return false;

        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        float myDistance = Vector2.Distance(selfXZ, carrierXZ);

        // In midfield the elected responder plus the block is enough; doubling up out there turned
        // every attack into a scramble. The second man is for the defensive third, where the line
        // actually needs a body between the carrier and the goal.
        if (carrierXZ.X * obs.AttackingDirectionX > -32f) return false;

        // Only the best-placed defender other than the elected responder engages.
        int closerMates = 0;
        foreach (var mate in obs.VisiblePlayers)
        {
            if (mate.Team != obs.SelfTeam || mate.Role == TacticalRole.Goalkeeper) continue;
            float mateDistance = Vector2.Distance(
                new Vector2(mate.EstimatedPosition.X, mate.EstimatedPosition.Z), carrierXZ);
            if (mateDistance < myDistance - 0.25f) closerMates++;
        }
        if (closerMates > 1) return false;

        const float engageRadius = 9f;
        if (myDistance > engageRadius) return false;

        // Interception line: where the carrier will be, plus a nudge to the goal side so the
        // defender arrives in front of him rather than trailing behind.
        Vector2 goalSide = SimMath.SafeNormalize(
            new Vector2(ownGoalCenter.X, ownGoalCenter.Z) - carrierXZ,
            new Vector2(-obs.AttackingDirectionX, 0f));
        Vector2 targetXZ = carrierXZ + carrierVelXZ * 0.25f + goalSide * 2.0f;

        Vector2 toTarget = targetXZ - selfXZ;
        float distance = toTarget.Length();
        Vector2 moveDir = distance > 0.35f
            ? (toTarget / distance) * Math.Clamp(distance / 2.5f, 0.55f, 1.0f)
            : Vector2.Zero;

        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        // A covering defender jockeys: he only commits to a tackle when the ball is properly in
        // reach, otherwise he would leave the very gap he was sent to close.
        bool attemptTackle = _tackleCooldown <= 0f &&
                             Vector2.Distance(selfXZ, ballXZ) <= obs.SelfAttributes.ReachRadius * 1.35f;
        if (attemptTackle) _tackleCooldown = TackleCooldownSeconds;

        Vector2 faceDir = SimMath.SafeNormalize(carrierXZ - selfXZ, obs.SelfFacingXZ);
        var command = new FootballerCommand(
            moveDir, faceDir, true, false, attemptTackle, false, false, Vector3.Zero,
            KickIntentType.None, Vector3.UnitX, 0f, 0f);

        _lastCandidates.Add(new UtilityCandidate(
            attemptTackle ? "ChallengeCarrierTackle" : "ChallengeCarrier", 0.995f, command)
        {
            TargetXZ = new Vector3(targetXZ.X, 0f, targetXZ.Y)
        });
        return true;
    }

    /// <summary>
    /// Where to run to meet a moving target as early as possible. Iterating the time-to-reach is
    /// enough here: the defender has to arrive at the carrier's *future* position, not chase the
    /// ball's current one.
    /// </summary>
    private static Vector2 PursuitPoint(Vector2 selfXZ, Vector2 targetXZ, Vector2 targetVelXZ, float maxSpeed)
    {
        float time = 0f;
        for (int i = 0; i < 3; i++)
        {
            Vector2 future = targetXZ + targetVelXZ * time;
            time = Math.Clamp(
                Vector2.Distance(selfXZ, future) / MathF.Max(2.0f, maxSpeed),
                0f, 1.6f);
        }
        return targetXZ + targetVelXZ * time;
    }

    private static bool TryGetVisibleEntity(
        in FootballerObservation obs,
        int playerId,
        out Vector2 positionXZ,
        out Vector2 velocityXZ)
    {
        foreach (var entity in obs.VisiblePlayers)
        {
            if (entity.PlayerId != playerId) continue;
            positionXZ = new Vector2(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            velocityXZ = new Vector2(entity.EstimatedVelocity.X, entity.EstimatedVelocity.Z);
            return true;
        }

        positionXZ = default;
        velocityXZ = default;
        return false;
    }

    /// <summary>
    /// Turns one job anchor into one or two reachable movement candidates: the anchor itself and a
    /// variant stepped out of the nearest opponent's cover shadow (audit F04). Utility combines the
    /// job value with passing-lane visibility, opponent separation and travel cost.
    /// </summary>
    private void AddJobCandidates(in FootballerObservation obs, Vector2 jobTargetXZ, float offsideLineProjX)
    {
        bool attackingJob = obs.TacticalJob is TacticalJob.NearOutlet or TacticalJob.ThirdPlayerOption
            or TacticalJob.FarSideWidth or TacticalJob.DepthRun or TacticalJob.Overlap;
        if (attackingJob && obs.PossessingTeam == obs.SelfTeam)
        {
            float maxOnsideProj = offsideLineProjX - 1.0f;
            if (jobTargetXZ.X * obs.AttackingDirectionX > maxOnsideProj)
                jobTargetXZ.X = obs.AttackingDirectionX * MathF.Max(0f, maxOnsideProj);
        }

        jobTargetXZ = new Vector2(
            Math.Clamp(jobTargetXZ.X, -_pitch.HalfLength + 3.5f, _pitch.HalfLength - 3.5f),
            Math.Clamp(jobTargetXZ.Y, -_pitch.HalfWidth + 2.5f, _pitch.HalfWidth - 2.5f));

        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 alternativeXZ = StepOutOfCoverShadow(obs, jobTargetXZ, selfXZ);

        float baseUtility = JobBaseUtility(obs.TacticalJob);
        AddJobCandidate(obs, $"{obs.TacticalJob}", jobTargetXZ, baseUtility);
        if (Vector2.Distance(jobTargetXZ, alternativeXZ) > 1.5f)
            AddJobCandidate(obs, $"{obs.TacticalJob}Adj", alternativeXZ, baseUtility - 0.03f);
    }

    private void AddJobCandidate(in FootballerObservation obs, string actionName, Vector2 targetXZ, float baseUtility)
    {
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        Vector2 toTarget = targetXZ - selfXZ;
        float distance = toTarget.Length();
        Vector2 moveDir = distance > 0.4f
            ? (toTarget / distance) * Math.Clamp(distance / 3.5f, 0.35f, 1.0f)
            : Vector2.Zero;

        bool sprint = obs.TacticalJob is TacticalJob.DepthRun or TacticalJob.Overlap
            ? distance > 4.0f
            : distance > 10.0f;

        float space = EvaluateSpaceFreedom(targetXZ, obs.VisiblePlayers, obs.SelfTeam);
        Vector2 carrierXZ = TryGetCarrierXZ(obs);
        float lane = EvaluateLaneClearance(carrierXZ, targetXZ, obs.VisiblePlayers, obs.SelfTeam);

        float utility = baseUtility
                      + 0.30f * lane
                      + 0.30f * space
                      - MathF.Min(distance, 40f) / 30f;

        Vector2 faceDir = SimMath.SafeNormalize(ballXZ - selfXZ, obs.SelfFacingXZ);
        var command = new FootballerCommand(
            moveDir, faceDir, sprint, false, false, false, false, Vector3.Zero,
            KickIntentType.None, Vector3.UnitX, 0f, 0f);

        _lastCandidates.Add(new UtilityCandidate($"Job:{actionName}", utility, command)
        {
            TargetXZ = new Vector3(targetXZ.X, 0f, targetXZ.Y)
        });
    }

    private static Vector2 TryGetCarrierXZ(in FootballerObservation obs)
    {
        if (obs.BallControllingPlayerId is int carrierId)
        {
            foreach (var entity in obs.VisiblePlayers)
            {
                if (entity.PlayerId == carrierId)
                    return new Vector2(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            }
        }

        // The ball itself is the best available reference for a loose ball or a pass in flight.
        return new Vector2(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
    }

    /// <summary>
    /// Moves a support target out of the nearest opponent's cover shadow, or offers a lateral
    /// alternative when the anchor is already in space.
    /// </summary>
    private static Vector2 StepOutOfCoverShadow(in FootballerObservation obs, Vector2 targetXZ, Vector2 selfXZ)
    {
        float nearestDistance = 12f;
        Vector2 nearestOpponentXZ = Vector2.Zero;
        foreach (var entity in obs.VisiblePlayers)
        {
            if (entity.Team == obs.SelfTeam) continue;
            Vector2 opponentXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            float distance = Vector2.Distance(targetXZ, opponentXZ);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestOpponentXZ = opponentXZ;
            }
        }

        if (nearestDistance > 6f)
            return targetXZ + new Vector2(0f, targetXZ.Y >= selfXZ.Y ? 4f : -4f);

        Vector2 away = SimMath.SafeNormalize(targetXZ - nearestOpponentXZ, new Vector2(obs.AttackingDirectionX, 0f));
        return targetXZ + away * (6f - nearestDistance);
    }

    private static float JobBaseUtility(TacticalJob job) => job switch
    {
        TacticalJob.DepthRun => 1.00f,
        TacticalJob.ThirdPlayerOption => 0.99f,
        TacticalJob.NearOutlet => 0.97f,
        TacticalJob.FarSideWidth => 0.93f,
        TacticalJob.Overlap => 0.94f,
        TacticalJob.DefensiveCover => 0.90f,
        TacticalJob.CoverDefender => 0.99f,
        TacticalJob.Marker => 0.97f,
        _ => 0.90f
    };

    private FootballerCommand EvaluateSetPieceRestart(in FootballerObservation obs, Vector3 oppGoalCenter)
    {
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        if (obs.CurrentMatchPhase == MatchPhase.PenaltyKick)
        {
            Vector2 goalAim = Vector2.Normalize(new Vector2(oppGoalCenter.X - selfXZ.X, 2.1f - selfXZ.Y));
            return new FootballerCommand(
                goalAim, goalAim, false, false, false, false, false, Vector3.Zero,
                KickIntentType.Shot, new Vector3(goalAim.X, 0f, goalAim.Y), 0.78f, 0.35f);
        }

        PerceivedEntity? bestMate = null;
        float bestScore = -999f;
        foreach (var mate in obs.VisiblePlayers)
        {
            if (mate.Team != obs.SelfTeam) continue;
            float d = Vector2.Distance(selfXZ, new Vector2(mate.EstimatedPosition.X, mate.EstimatedPosition.Z));
            if (d < 4.0f || d > 38.0f) continue;
            float score = (mate.EstimatedPosition.X * obs.AttackingDirectionX) - d * 0.35f;
            if (score > bestScore)
            {
                bestScore = score;
                bestMate = mate;
            }
        }

        Vector2 targetXZ = bestMate.HasValue
            ? new Vector2(bestMate.Value.EstimatedPosition.X, bestMate.Value.EstimatedPosition.Z)
            : new Vector2(obs.SelfPosition.X + obs.AttackingDirectionX * 15f, 0f);

        Vector2 dir = Vector2.Normalize(targetXZ - selfXZ);
        float dist = Vector2.Distance(selfXZ, targetXZ);
        KickIntentType kickType = obs.CurrentMatchPhase == MatchPhase.CornerKick || dist > 22.0f
            ? KickIntentType.LoftedPass
            : KickIntentType.GroundPass;

        return new FootballerCommand(
            dir, dir, false, false, false, false, false, Vector3.Zero,
            kickType, new Vector3(dir.X, 0f, dir.Y), Math.Clamp(dist / 32.0f, 0.40f, 0.85f), 0f)
        {
            IntendedReceiverId = bestMate?.PlayerId
        };
    }

    public static float EvaluateLaneClearance(
        Vector2 fromXZ,
        Vector2 toXZ,
        IReadOnlyList<PerceivedEntity> visiblePlayers,
        TeamId selfTeam)
    {
        Vector2 seg = toXZ - fromXZ;
        float segLen = seg.Length();
        if (segLen < 1e-3f) return 1.0f;
        Vector2 segDir = seg / segLen;

        float minClearanceRatio = 1.0f;
        foreach (var p in visiblePlayers)
        {
            if (p.Team == selfTeam) continue;
            Vector2 oppXZ = new(p.EstimatedPosition.X, p.EstimatedPosition.Z);
            Vector2 rel = oppXZ - fromXZ;
            float proj = Vector2.Dot(rel, segDir);
            if (proj <= 0.4f || proj >= segLen - 0.3f) continue;

            Vector2 closest = fromXZ + segDir * proj;
            float perpDist = Vector2.Distance(oppXZ, closest);
            float interceptRadius = 1.1f + (proj / segLen) * 2.4f;
            float ratio = Math.Clamp(perpDist / interceptRadius, 0f, 1f);
            if (ratio < minClearanceRatio)
                minClearanceRatio = ratio;
        }
        return minClearanceRatio;
    }

    /// <summary>
    /// True when the planned pass line is blocked: the AI abandons such a pass instead of playing it
    /// straight into the defender (reported from playtesting: "let them see if the pass line is
    /// blocked, if yes - abandon it").
    ///
    /// <see cref="EvaluateLaneClearance"/> scores how crowded a lane *is right now*: it ignores
    /// opponents close to the passer, only scales its corridor with how near the receiver a defender
    /// stands, and knows nothing about the flight time. With the old minimum-power passes taking
    /// ~2 s, every defender beside the lane had time to step into it, so the pass was cut out even
    /// though the lane scored as perfectly clear. This test instead asks whether an opponent can
    /// physically be where the ball will be:
    ///
    /// <list type="bullet">
    /// <item><b>on their run:</b> the flight is sampled against each opponent's predicted run. A
    ///   defender standing in the lane, next to the ball, or running across it meets the ball where
    ///   it actually will be — including a defender 1.5 m off the line at 6 m/s whose current
    ///   position looks clear;</item>
    /// <item><b>stepping in:</b> an opponent who is not already running into the lane can still
    ///   notice the pass and close it after <see cref="PassLaneReactionSeconds"/>. A slow pass is
    ///   therefore blocked from further away than a firm one, and a defender running away from the
    ///   lane with no time left does not block it.</item>
    /// </list>
    /// </summary>
    /// <param name="power01">Power the pass is played at (used for the ball's arrival time).</param>
    /// <param name="flightTimeSeconds">The plan's flight time (fallback when the table has no sample).</param>
    public bool IsPassLaneBlocked(
        Vector2 fromXZ,
        Vector2 toXZ,
        float power01,
        float flightTimeSeconds,
        IReadOnlyList<PerceivedEntity> visiblePlayers,
        TeamId selfTeam)
    {
        Vector2 seg = toXZ - fromXZ;
        float segLen = seg.Length();
        // A pass backwards into the passer's own feet is never a pass: there is no line to check.
        if (segLen < 0.5f) return true;

        Vector2 segDir = seg / segLen;
        Vector2 segNormal = new(-segDir.Y, segDir.X);
        float flight = flightTimeSeconds > 0f ? MathF.Min(flightTimeSeconds, 6f) : segLen / 12f;
        int samples = Math.Clamp((int)MathF.Ceiling(flight / PassLaneSampleSeconds), 4, 40);

        foreach (var p in visiblePlayers)
        {
            if (p.Team == selfTeam) continue;

            Vector2 oppXZ = new(p.EstimatedPosition.X, p.EstimatedPosition.Z);
            Vector2 oppVelocity = new(p.EstimatedVelocity.X, p.EstimatedVelocity.Z);
            Vector2 rel = oppXZ - fromXZ;
            float signedPerp = Vector2.Dot(rel, segNormal);

            // 1. Their current run, sampled against the ball's own flight: if the two ever come
            //    within touch range, the pass runs straight into them.
            float oppSpeed = oppVelocity.Length();
            Vector2 runVelocity = oppSpeed > OpponentSprintSpeed
                ? oppVelocity * (OpponentSprintSpeed / oppSpeed)
                : oppVelocity;
            for (int i = 0; i <= samples; i++)
            {
                float t = flight * i / samples;
                float ballDistance = MathF.Min(_passPlanner.BallDistanceAtTime(power01, t), segLen);
                Vector2 ballAt = fromXZ + segDir * ballDistance;
                if (Vector2.Distance(oppXZ + runVelocity * t, ballAt) <= PassLaneContactReach)
                    return true;
            }

            // 2. They can also see the pass and step into the lane. Compare what the step actually
            //    needs with what they can produce before the ball has gone by.
            float gap = MathF.Abs(signedPerp) - PassLaneContactReach;
            if (gap <= 0f) return true;

            float proj = Math.Clamp(Vector2.Dot(rel, segDir), 0f, segLen);
            float timeToPoint = BallTimeToDistance(power01, proj, flightTimeSeconds, segLen);
            float available = timeToPoint - PassLaneReactionSeconds;
            if (available <= 0f) continue;

            float closingTowardsLine = -MathF.Sign(signedPerp) * Vector2.Dot(oppVelocity, segNormal);
            float closingSpeed = Math.Clamp(closingTowardsLine, 0f, OpponentSprintSpeed) + PassLaneTurnSpeed;

            if (gap <= closingSpeed * available) return true;
        }

        return false;
    }

    /// <summary>
    /// Time the ball needs to reach <paramref name="distanceMeters"/> along its own flight, from the
    /// planner's calibrated distance/time table. Falls back to a linear share of the plan's flight
    /// time when the table has no sample for that power (it always has one in practice).
    /// </summary>
    private float BallTimeToDistance(float power01, float distanceMeters, float flightTimeSeconds, float segLen)
    {
        if (_passPlanner.TryGetFlightTime(power01, distanceMeters, out float time))
            return time;
        return flightTimeSeconds * Math.Clamp(distanceMeters / MathF.Max(segLen, 0.01f), 0f, 1f);
    }

    private static float EvaluateSpaceFreedom(
        Vector2 spotXZ,
        IReadOnlyList<PerceivedEntity> visiblePlayers,
        TeamId selfTeam)
    {
        float minOppDist = 15.0f;
        foreach (var p in visiblePlayers)
        {
            if (p.Team == selfTeam) continue;
            float d = Vector2.Distance(spotXZ, new Vector2(p.EstimatedPosition.X, p.EstimatedPosition.Z));
            if (d < minOppDist) minOppDist = d;
        }
        return Math.Clamp(minOppDist / 9.0f, 0f, 1f);
    }
}

