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

    /// <summary>
    /// Stable identity of this candidate: kind + target + variant (A1.1c). <see cref="ActionName"/> is a
    /// label for humans and may change; this is what commitment, tracing and deduplication compare.
    /// Defaults to <see cref="CandidateKey.None"/> for a candidate built without one.
    /// </summary>
    public CandidateKey Key { get; init; }

    /// <summary>Text form of <see cref="Key"/>, falling back to the action name when no key was set.</summary>
    public string Identity => Key.IsNone ? ActionName : Key.Describe();
}


public sealed class TacticalUtilityAiController : IFootballerController
{
    private readonly PitchDimensions _pitch;
    private readonly CandidateGenerator _generator;
    private readonly AiRuntimeConfig _ai;
    private string _committedActionName = "Idle";
    private CandidateKey _committedKey;
    private float _commitmentTimer;
    private float _timeOnBall;
    private string _debugSummary = "Uninitialized";
    private UtilityCandidate _lastSelected;
    private UtilityCandidate? _lastRunnerUp;
    private int _lastCandidateCount;
    public TacticalUtilityAiController(
        PitchDimensions pitch,
        MatchCommunicationContext? communication = null,
        AiRuntimeConfig? ai = null)
    {
        _pitch = pitch;
        _generator = new CandidateGenerator(pitch, communication);
        _ai = ai ?? AiRuntimeConfig.Default;
    }

    public ControllerSourceType SourceType => ControllerSourceType.AI;
    public IReadOnlyList<UtilityCandidate> LastCandidates => _generator.Candidates;

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

    /// <summary>
    /// Stable identity currently held by commitment (A1.1c). <see cref="CommittedActionName"/> is its
    /// human-readable label; this is what the controller actually compares.
    /// </summary>
    public CandidateKey CommittedKey => _committedKey;

    public AiRuntimeConfig RuntimeConfig => _ai;
    public string GetDebugSummary() => _debugSummary;

    public FootballerCommand Decide(in FootballerObservation obs, float dt)
    {
        _commitmentTimer = MathF.Max(0f, _commitmentTimer - dt);
        _generator.TickTimers(dt);
        _generator.Clear();

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
        _generator.TimeOnBall = _timeOnBall;

        if (onBall)
        {
            _generator.GenerateOnBallCandidates(obs, oppGoalCenter, offsideLineProjX);
        }
        else
        {
            _generator.GenerateOffBallCandidates(obs, oppGoalCenter, ownGoalCenter, myInterceptXZ, isPrimaryBallResponder, offsideLineProjX);
        }

        UtilityCandidate best = default;
        UtilityCandidate? runnerUp = null;
        bool hasBest = false;
        foreach (var cand in _generator.Candidates)
        {
            float score = cand.UtilityScore;
            // Identity, not label: the same act against the same target keeps its commitment even if the
            // display name is reworded or the target point moves by centimetres.
            if (_commitmentTimer > 0f && cand.Key == _committedKey)
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
        _lastCandidateCount = _generator.CandidateCount;

        if (best.Key != _committedKey)
        {
            _committedKey = best.Key;
            _committedActionName = best.ActionName;
            // A job target is a commitment, not a reflex: hold it longer than a reactive press.
            _commitmentTimer = best.Key.Kind == CandidateKind.Job
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

    /// <summary>
    /// Lane clearance for a would-be receiver. Public and static because the goalkeeper controller and the
    /// audit probe score the same question; the implementation lives with the pass candidates that use it
    /// most.
    /// </summary>
    public static float EvaluateLaneClearance(
        Vector2 fromXZ,
        Vector2 toXZ,
        IReadOnlyList<PerceivedEntity> visiblePlayers,
        TeamId passerTeam) =>
        CandidateGenerator.EvaluateLaneClearance(fromXZ, toXZ, visiblePlayers, passerTeam);
}