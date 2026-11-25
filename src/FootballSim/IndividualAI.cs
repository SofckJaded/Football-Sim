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
    /// Target point the action is trying to reach, when it has one. For movement candidates it is where the
    /// body was sent; for pass candidates it is the meeting point the planner actually aimed at (the lead or
    /// the announced run region, not necessarily the receiver's current position). Exposed so behaviour
    /// fields (discipline, freedom), tests and the read-only debug overlay can check where the AI is going
    /// and what it aimed at, not just which direction it faces (audit F18; debug overlay).
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

    /// <summary>
    /// The four terms the score was built from (A4.6), so a trace or the inspector can explain the
    /// decision instead of only reporting its total. <see cref="ValueTerms.IsPresent"/> is false for a
    /// candidate built without one.
    /// </summary>
    public ValueTerms Terms { get; init; } = ValueTerms.None;
}


public sealed class TacticalUtilityAiController : IFootballerController, IAiStateResettable
{
    private readonly PitchDimensions _pitch;
    private readonly CandidateGenerator _generator;
    private readonly AiRuntimeConfig _ai;
    private readonly AiScheduler _scheduler;
    private readonly IntentExecutor _executor = new();
    private int _decisionsComputed;
    private string _committedActionName = "Idle";
    private CandidateKey _committedKey;
    private float _commitmentTimer;
    private float _timeOnBall;
    /// <summary>
    /// A9.3 part 2: a preformatted summary for the rare paths (no decision yet, a phase reset, a
    /// set-piece restart). Null on the ordinary path, where the getter formats the summary lazily from
    /// the three fields below instead of allocating a string on every decision.
    /// </summary>
    private string? _debugSummaryOverride = "Uninitialized";
    private string _debugActionName = "Idle";
    private TacticalJob _debugJob;
    private float _debugUtility;
    private UtilityCandidate _lastSelected = new("Idle", 0f, FootballerCommand.Idle) { Key = CandidateKey.None };
    private UtilityCandidate? _lastRunnerUp;
    private int _lastCandidateCount;
    public TacticalUtilityAiController(
        PitchDimensions pitch,
        MatchCommunicationContext? communication = null,
        AiRuntimeConfig? ai = null)
    {
        _pitch = pitch;
        _generator = new CandidateGenerator(pitch, communication, ai);
        _ai = ai ?? AiRuntimeConfig.Default;
        _scheduler = new AiScheduler(_ai);
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

    /// <summary>
    /// A7.5: true when this decision's evaluation error changed which candidate scored highest — the player
    /// misread the value of what he was choosing between. Diagnostics for the trace and the cognitive probe;
    /// false whenever the error is zero (full stamina, reference composure, or the feature switched off).
    /// </summary>
    public bool LastEvaluationChangedChoice { get; private set; }

    /// <summary>A7.5: the action that would have scored highest with no evaluation error (diagnostics).</summary>
    public string LastRawBestActionName { get; private set; } = "none";

    /// <summary>
    /// A7.5: how many decisions this controller has made in which the evaluation error changed which
    /// candidate scored highest. A match-level diagnostic for the cognitive probe; the decision path never
    /// reads it.
    /// </summary>
    public int EvaluationChangedDecisions => _evaluationChangedDecisions;

    private int _evaluationChangedDecisions;

    /// <summary>Number of candidates generated on the last decision.</summary>
    public int LastCandidateCount => _lastCandidateCount;

    /// <summary>
    /// Candidates the last decision refused, with a reason (A4.6). Bounded, allocated once, and only read by
    /// the trace and the inspector — the decision path never consults it.
    /// </summary>
    public int RejectionCount => _generator.RejectionCount;

    /// <summary>One refused candidate from the last decision; <paramref name="index"/> below <see cref="RejectionCount"/>.</summary>
    public CandidateRejection GetRejection(int index) => _generator.GetRejection(index);

    /// <summary>
    /// A8.3: pass candidates in the last decision that carried a recognized switch preference. A match-level
    /// diagnostic for the <c>--switches</c> probe; the decision path never reads it.
    /// </summary>
    public int SwitchPreferencesApplied => _generator.SwitchPreferencesApplied;

    /// <summary>
    /// A8.3: the best switch opportunity the last decision recognized, or a default record. Diagnostics and
    /// tests; a decision that recognized nothing reports <see cref="SwitchOpportunity.IsRecognized"/> false.
    /// </summary>
    public SwitchOpportunity LastSwitchOpportunity => _generator.LastSwitchOpportunity;

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

    /// <summary>
    /// The local pressure window this player last decided with (A2.4/A2.6). Diagnostics and the
    /// inspector overlays read it; nothing in the match consumes it.
    /// </summary>
    public LocalPressureField PressureField => _generator.PressureField;

    /// <summary>
    /// This player's off-ball path set and adopted run (A5.1/A5.2). Diagnostics and tests read it; A5.3
    /// publishes the adopted intent to the team's board.
    /// </summary>
    public RunCandidateGenerator Runs => _generator.Runs;

    /// <summary>
    /// A9.3 part 2: the one-line decision summary the inspector and the probes read. Formatted on demand —
    /// the decision path only stores the three values — so ~20 decisions per tick no longer allocate a
    /// string that nothing reads. The rare paths keep their preformatted text verbatim.
    /// </summary>
    public string GetDebugSummary() =>
        _debugSummaryOverride ?? $"{_debugActionName} [{_debugJob}] (U={_debugUtility:F2})";

    /// <summary>
    /// A9.3: cumulative candidate-generation work this controller has done (carrier and off-ball
    /// decisions, the generated/kept counts, shallow-search branches). Diagnostics only.
    /// </summary>
    public AiWorkStats CandidateWork => _generator.Work;

    /// <summary>
    /// A9.3: cumulative trajectory work this controller's reception planner has done. Diagnostics only.
    /// </summary>
    public AiWorkStats ReceptionWork => _generator.ReceptionWork;

    /// <summary>
    /// Drops the commitment and the time-on-ball accumulation (A1.5).
    ///
    /// A committed action is a guess that the situation will last; a restart is the authoritative
    /// statement that it did not. Keeping the commitment would make the first decision after a stoppage
    /// defend a position chosen for a passage of play that has ended.
    /// </summary>
    public void ResetForPhase(AiResetReason reason)
    {
        _committedActionName = "Idle";
        _committedKey = CandidateKey.None;
        _commitmentTimer = 0f;
        _timeOnBall = 0f;
        _lastSelected = new UtilityCandidate("Idle", 0f, FootballerCommand.Idle) { Key = CandidateKey.None };
        _lastRunnerUp = null;
        _lastCandidateCount = 0;
        // A7.3: a plan is about one arriving ball; a restart means that ball is history.
        LastReceptionPlan = ReceptionPlan.None;
        _generator.ResetForPhase(reason);
        _executor.Clear();
        _debugSummaryOverride = $"Reset ({reason})";
        LastResetReason = reason;
    }

    /// <summary>
    /// Why this controller was last reset, or null if it never has been (A1.5). Diagnostics only: the
    /// engine re-decides on the very next tick, so a caller cannot infer a reset from the live state.
    /// </summary>
    public AiResetReason? LastResetReason { get; private set; }

    public FootballerCommand Decide(in FootballerObservation obs, float dt)
    {
        // Cadence is not a timer: the commitment window and the tackle cooldown advance on every live
        // tick, whether or not this tick re-scores candidates (A1.2).
        _commitmentTimer = MathF.Max(0f, _commitmentTimer - dt);
        _generator.TickTimers(dt);

        Vector3 oppGoalCenter = new(obs.AttackingDirectionX * _pitch.HalfLength, 0f, 0f);
        Vector3 ownGoalCenter = new(-obs.AttackingDirectionX * _pitch.HalfLength, 0f, 0f);

        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        Vector2 ballVelXZ = new(obs.PerceivedBallVelocity.X, obs.PerceivedBallVelocity.Z);
        float distToBall = Vector2.Distance(selfXZ, ballXZ);

        // A7.3: a ball above torso height is not "on the ball" — execution would refuse every kick for it
        // as BallTooHigh. Recognising the height here is what sends the receiver of a dropping pass into
        // the receiving branch (which plans the meeting) instead of asking for kicks it cannot execute.
        // The gate is the execution layer's own contact rule, not a second reach number.
        //
        // AUD-08: resolved before the restart branch because that branch stores an intent too, and the
        // on-ball state is what makes a cached intent ineligible once possession has changed.
        bool ballAtFeetHeight = BallContactRules.IsHeightReachable(
            obs.PerceivedBallPosition.Y, obs.SelfPosition.Y,
            BallContactRules.MaxContactHeight(keeperHandling: false, jumpingOrHeading: false));
        bool anotherControlsBall = obs.BallControlState is BallControlState.Controlled or BallControlState.KeeperHeld
                                   && obs.BallControllingPlayerId is int ballOwnerId
                                   && ballOwnerId != obs.SelfId;
        bool onBall = !anotherControlsBall
                      && distToBall <= obs.SelfAttributes.ReachRadius * 1.35f
                      && obs.AvailableToKick
                      && ballAtFeetHeight;

        if (obs.IsSetPieceTaker && distToBall < 4.5f)
        {
            var restartCmd = EvaluateSetPieceRestart(obs, oppGoalCenter);
            _debugSummaryOverride = $"SetPieceRestart ({obs.CurrentMatchPhase})";
            // A restart taker has exactly one thing to do, but the trace still has to be able to name
            // it. Until the A4 pass audit this path left the *previous* decision — or, after a reset,
            // the zero value of the field — as LastSelected, so the recorder published a stale utility
            // (and a fabricated p = 0 decomposition) for the restart.
            _lastSelected = new UtilityCandidate("SetPieceRestart", 0f, restartCmd) { Key = CandidateKey.None };
            _lastRunnerUp = null;
            _lastCandidateCount = 0;
            StoreIntent(obs.Tick, CandidateKey.None, "SetPieceRestart", restartCmd, isJob: false,
                onBallAtDecision: onBall);
            // A restart is not the passage of play the run was chosen for: drop any adopted run.
            _generator.NoteDecision(obs, CandidateKey.None);
            return restartCmd;
        }

        // A teammate is genuinely carrying the ball (authoritative control, not mere proximity).
        // Audit F10: the old test was "a teammate is within 1.85 m and the ball is slow", which
        // also matched a defender standing next to a loose ball.
        bool teammateHasControl = obs.BallControlState is BallControlState.Controlled or BallControlState.KeeperHeld
                                  && obs.PossessingTeam == obs.SelfTeam
                                  && obs.BallControllingPlayerId is int controllingId
                                  && controllingId != obs.SelfId;

        // Only the receiving branch of the off-ball candidate set reads this (a press target comes from the
        // press model), so the defending fallback is the ball's own position and carries no lead.
        Vector2 myInterceptXZ = obs.PossessingTeam == obs.SelfTeam
            ? ComputeBallInterceptTarget(ReachKinematics.FromObservation(obs), ballXZ, ballVelXZ, _pitch)
            : ballXZ;

        // Audit F10: responder election is match-level (one per team, time-to-reach + hysteresis,
        // human not counted as a rival AI). The local estimate is only a fallback for a controller
        // built without a match (probes/tests).
        bool isPrimaryBallResponder = (obs.IsDesignatedBallResponder ?? ComputeLocalResponder(obs, ballXZ, ballVelXZ))
                                      && !teammateHasControl;

        if (onBall || (obs.BallLastTouchedByPlayerId == obs.SelfId && distToBall < 1.8f))
            _timeOnBall += dt;
        else
            _timeOnBall = 0f;

        float offsideLineProjX = ComputeOffsideLineProjX(obs);
        _generator.TimeOnBall = _timeOnBall;

        // A1.2: re-emit the live intent instead of re-scoring. This path is unreachable unless scheduled
        // decisions are switched on, so the default build still decides every tick.
        //
        // AUD-08: the executor refuses to re-emit across a possession change (onBall differing from the
        // state stored with the intent), which forces this player to decide instead of carrying an off-ball
        // movement plan onto the ball or an on-ball plan past the touch. It also clears the one-shot
        // discrete actions from every re-emitted command (Architecture Rule 4), so a cadence skip can never
        // re-issue a kick or tackle that execution refused.
        if (!_scheduler.ShouldRecompute(obs.Tick, obs.SelfId, onBall) &&
            _executor.TryGetLiveCommand(obs.Tick, onBall, out FootballerCommand liveCommand))
        {
            return liveCommand;
        }

        _generator.Clear();

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
        UtilityCandidate current = default;
        bool hasBest = false;
        bool hasCurrent = false;

        // A7.5: a fatigued or rattled player misreads the value of what he is choosing between. The error
        // is one deterministic hash per (player, tick, candidate) — never a random stream, whose
        // advancement would then depend on how many candidates were scored — and it is exactly zero at
        // full stamina with reference composure, so an unfatigued fixture decides as it always did.
        CognitiveState cognitive = _ai.EnableCognitiveError
            ? PlayerCognitiveState.Compute(obs.SelfAttributes, obs.Stamina01)
            : CognitiveState.Neutral(obs.SelfAttributes.ReactionDelaySeconds);
        float evaluationSpan = cognitive.EvaluationError01 * PlayerCognitiveState.EvaluationUtilitySpan;

        // The best raw candidate this decision, alongside the best one the player *believed* before
        // commitment was added — the pair is what makes "the misjudgement changed the choice" a measured
        // fact instead of an assumption. Comparing against the committed winner would confuse a commitment
        // hold with a misjudgement.
        UtilityCandidate rawBest = default;
        float rawBestScore = float.NegativeInfinity;
        CandidateKey believedBestKey = CandidateKey.None;
        float believedBestScore = float.NegativeInfinity;

        foreach (var cand in _generator.Candidates)
        {
            float score = cand.UtilityScore;
            if (score > rawBestScore)
            {
                rawBestScore = score;
                rawBest = cand;
            }

            if (evaluationSpan > 0f)
            {
                score += PlayerCognitiveState.EvaluationBias(obs.SelfId, obs.Tick, cand.Key) * evaluationSpan;
            }

            if (score > believedBestScore)
            {
                believedBestScore = score;
                believedBestKey = cand.Key;
            }

            // Identity, not label: the same act against the same target keeps its commitment even if the
            // display name is reworded or the target point moves by centimetres.
            // A9.2: the bonus is part of the hold this switch owns. Off is the ablation — every decision is
            // taken on this tick's raw scores — so no bonus is added even if a window were somehow live.
            if (_ai.EnableCommitment && _commitmentTimer > 0f && cand.Key == _committedKey)
                score += _ai.CommitmentBonus;

            UtilityCandidate scored = cand with { UtilityScore = score };
            if (!hasCurrent && scored.Key == _committedKey)
            {
                current = scored;
                hasCurrent = true;
            }

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

        // A7.5's evidence hook: did the misjudgement change which candidate looked best? Recorded before the
        // switch rule, because the claim is about the ranking the error acted on, not about the final
        // committed action. Diagnostics only — nothing in the decision path reads it.
        LastEvaluationChangedChoice = evaluationSpan > 0f && hasBest && !believedBestKey.Equals(rawBest.Key);
        LastRawBestActionName = hasBest ? rawBest.ActionName : "none";
        if (LastEvaluationChangedChoice) _evaluationChangedDecisions++;

        // A4.5: keep doing what we are doing unless the alternative is *clearly* better. This is the plan's
        // rule, on the absolute scale of the scores:
        //     switch only when best - current > absolute + relative * max(|current|, epsilon).
        // A5-style proportions (multiplying the utilities) invert the decision for negative scores, which
        // is why the magnitude is used instead. The current action is held only while it is still a valid
        // candidate this tick: an invalid action (the ball changed hands, the duty changed, a restart) is
        // not held, because there is nothing to re-evaluate.
        //
        // AUD-10: the commitment switch owns both halves of the hold — the live window/bonus above and this
        // switch hysteresis. With it off the "raw scores" ablation must really decide on this tick's raw
        // scores, so no current action is tracked or defended below; before the audit the ablation removed
        // the window and bonus but kept holding the chosen action against marginal challengers.
        bool commitmentEnabled = _ai.EnableCommitment;
        if (commitmentEnabled && hasCurrent && hasBest && !best.Key.Equals(current.Key) &&
            !ShouldSwitch(best.UtilityScore, current.UtilityScore, _ai.SwitchAbsoluteMargin, _ai.SwitchRelativeMargin))
        {
            runnerUp = best;
            best = current;
        }

        // Expose the decision that was actually taken, including the commitment bonus and any held action.
        // The trace must report this, not the raw candidate table: the two differ exactly when commitment is
        // doing its job, and a trace that showed the raw winner would describe a decision nobody made.
        _lastSelected = best;
        _lastRunnerUp = runnerUp;
        _lastCandidateCount = _generator.CandidateCount;

        // AUD-10: with the switch off nothing is committed, so the diagnostics stay at "no hold" instead of
        // naming an action the ablation is not defending. Reporting a committed key here would also let a
        // later decision score a stale "current" action against the challenger.
        if (commitmentEnabled && best.Key != _committedKey)
        {
            _committedKey = best.Key;
            _committedActionName = best.ActionName;
            // A9.2: with the commitment switch off the window is never started, so a chosen action is
            // unprotected and CommitmentRemainingSeconds stays zero for the whole match.
            // A job target is a commitment, not a reflex: hold it longer than a reactive press. A run is
            // the same kind of promise (A5.2): the player has adopted a meeting region and holds it.
            _commitmentTimer = best.Key.Kind is CandidateKind.Job or CandidateKind.Run
                ? _ai.JobCommitmentSeconds
                : _ai.ReactionCommitmentSeconds;
        }

        // A5.2: tell the run generator what was chosen, so an adopted path is frozen for its window and
        // any other choice ends it. A5.3: the adopted run is announced to the team on the same call.
        _generator.NoteDecision(obs, best.Key);

        // A7.3: record the reception plan the candidate carried, if any (A7.4's seam). It is stored on
        // the intent rather than only on the observation so the decision about the first touch survives
        // the tick that made it — the ball may arrive ten ticks later.
        ReceptionPlan receptionPlan = _generator.TryGetReceptionPlan(best.Key, out ReceptionPlan planned)
            ? planned
            : ReceptionPlan.None;
        LastReceptionPlan = receptionPlan;

        // A9.3 part 2: store the three summary values; GetDebugSummary formats them only when read.
        _debugSummaryOverride = null;
        _debugActionName = best.ActionName;
        _debugJob = obs.TacticalJob;
        _debugUtility = best.UtilityScore;
        StoreIntent(obs.Tick, best.Key, best.ActionName, best.Command, best.Key.Kind == CandidateKind.Job,
            onBallAtDecision: onBall, reception: receptionPlan);
        return best.Command;
    }

    /// <summary>
    /// The A4.5 switch rule, pure and testable: is <paramref name="best"/> better than
    /// <paramref name="current"/> by more than the configured margin?
    ///
    /// <c>best − current &gt; absolute + relative × max(|current|, ε)</c>. The relative term scales with how
    /// much is at stake in the current action — a large positive score is stickier than a small one — and
    /// the magnitude keeps the rule correct when the current score is negative, where a proportional
    /// comparison (the report's multiplicative example) would make switching *easier* precisely when the
    /// player is doing something already known to be bad.
    /// </summary>
    public static bool ShouldSwitch(float bestScore, float currentScore, float absoluteMargin, float relativeMargin)
    {
        float current = float.IsFinite(currentScore) ? currentScore : 0f;
        float candidate = float.IsFinite(bestScore) ? bestScore : float.NegativeInfinity;
        float absolute = float.IsFinite(absoluteMargin) ? MathF.Max(0f, absoluteMargin) : 0f;
        float relative = float.IsFinite(relativeMargin) ? MathF.Max(0f, relativeMargin) : 0f;

        float required = absolute + relative * MathF.Max(MathF.Abs(current), SwitchEpsilon);
        return candidate - current > required;
    }

    /// <summary>Floor on the magnitude in the switch rule, so a zero-scoring action is not free to change.</summary>
    public const float SwitchEpsilon = 1e-3f;

    /// <summary>
    /// A7.3: the reception plan carried by the action this controller last chose, or
    /// <see cref="ReceptionPlan.None"/>. Diagnostics, the inspector and the <c>--receptions</c> probe read
    /// it; nothing in the decision path does. A7.4 publishes the same plan on the command boundary.
    /// </summary>
    public ReceptionPlan LastReceptionPlan { get; private set; } = ReceptionPlan.None;

    /// <summary>
    /// Publishes the chosen action as an intent (A1.2). Recorded even when scheduling is off, so the
    /// executor's state always describes the last real decision rather than lagging behind a feature
    /// switch.
    ///
    /// AUD-08: <paramref name="onBallAtDecision"/> is the possession side the decision was taken on. The
    /// executor compares it with the live state before re-emitting a cached command, so a possession
    /// change always forces a fresh decision.
    /// </summary>
    private void StoreIntent(
        int tick,
        CandidateKey key,
        string actionName,
        FootballerCommand command,
        bool isJob,
        bool onBallAtDecision,
        ReceptionPlan reception = default)
    {
        _decisionsComputed++;
        float seconds = isJob ? _ai.JobCommitmentSeconds : _ai.ReactionCommitmentSeconds;
        int ticks = Math.Max(1, (int)MathF.Round(seconds / SimConstants.FixedDeltaTime));

        // Ticks are absolute, so a suspended tick cannot extend an intent by accident.
        int start = Math.Max(0, tick);
        _executor.Set(new PlayerIntent(key, actionName, command, start, start + ticks)
        {
            Reception = reception,
            OnBallAtDecision = onBallAtDecision
        });
    }

    /// <summary>
    /// How many times this controller has generated and scored a candidate set. Cheap decisions (re-emitting
    /// a live intent) do not count, which is how a test proves the scheduler is doing anything.
    /// </summary>
    public int DecisionsComputed => _decisionsComputed;

    /// <summary>The intent currently held by the executor, if any (A1.2).</summary>
    public PlayerIntent? CurrentIntent => _executor.Current;

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
            ? ComputeBallInterceptTarget(ReachKinematics.FromObservation(obs), ballXZ, ballVelXZ, _pitch)
            : ballXZ + ballVelXZ * 0.12f);
        bool selfJustPassedBall = obs.BallLastTouchedByPlayerId == obs.SelfId && ballVelXZ.Length() > 9.5f;
        float myEffectiveDist = myInterceptXZ + (selfJustPassedBall ? 18.0f : 0f);

        foreach (var entity in obs.VisiblePlayers)
        {
            if (entity.Team != obs.SelfTeam || entity.Role == TacticalRole.Goalkeeper) continue;
            Vector2 mateXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            Vector2 mateInterceptXZ = obs.PossessingTeam == obs.SelfTeam
                ? ComputeBallInterceptTarget(ReachKinematics.FromEntity(entity), ballXZ, ballVelXZ, _pitch)
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

    private static Vector2 ComputeBallInterceptTarget(
        in ReachKinematics kinematics,
        Vector2 ballXZ,
        Vector2 ballVelXZ,
        PitchDimensions pitch)
    {
        // A ball barely moving is not a moving target: aiming at where it is keeps the chase honest
        // and avoids solving a meeting that the reach model would report as already reached.
        if (!SimMath.IsFinite(ballVelXZ) || ballVelXZ.Length() < 0.75f)
            return ClampToPitch(ballXZ, pitch);

        // A2.1: the meeting is solved with the same reach model the responder election uses, so the
        // player who was elected is also the player who was measured as able to get there.
        if (TimeToReachModel.TrySolveInterception(
                kinematics, ballXZ, ballVelXZ, out Vector2 meeting, out _,
                TimeToReachModel.DefaultHorizonSeconds))
        {
            return ClampToPitch(meeting, pitch);
        }

        // Constant-velocity interception returned unreachable (often because grass deceleration is not
        // modelled in TrySolveInterception). Never chase the ball's tail behind its path: converge onto
        // the ball's forward ray at/ahead of the player's own projection so the receiver sprints into the
        // decelerating ball's lane instead of running backward toward the passer.
        float ballSpeed = ballVelXZ.Length();
        Vector2 ballDir = ballVelXZ / ballSpeed;
        float along = Vector2.Dot(kinematics.PositionXZ - ballXZ, ballDir);
        float maxLead = Math.Clamp(ballSpeed * 1.25f, 3.0f, 18.0f);
        float leadAhead = Math.Clamp(MathF.Max(along, 0f) + 3.0f, 3.0f, maxLead);
        return ClampToPitch(ballXZ + ballDir * leadAhead, pitch);
    }

    private static Vector2 ClampToPitch(Vector2 point, PitchDimensions pitch) => new(
        Math.Clamp(point.X, -pitch.HalfLength + 1f, pitch.HalfLength - 1f),
        Math.Clamp(point.Y, -pitch.HalfWidth + 1f, pitch.HalfWidth - 1f));

    /// <summary>
    /// The believed offside line, from this player's own picture of the opponents. A5.4's one margin is
    /// applied by the callers; the line itself is <see cref="OffsideTactics.BelievedLineProjX"/>, shared with
    /// the keeper's distribution since A6.B9 so the two cannot disagree about the same receiver.
    /// </summary>
    private float ComputeOffsideLineProjX(in FootballerObservation obs) =>
        OffsideTactics.BelievedLineProjX(obs, _pitch.HalfLength);

    private FootballerCommand EvaluateSetPieceRestart(in FootballerObservation obs, Vector3 oppGoalCenter)
    {
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);

        // AUD-13b: the restart trigger above fires up to 4.5 m from the ball, but every branch below returns
        // the pass/kick aim vector as the movement direction. A taker displaced 1.5-4.4 m from the ball was
        // therefore walking *away* from it while waiting to kick. Outside the execution layer's own reach the
        // taker closes the gap and emits no kick; inside it the aim vectors below are the real kick. The
        // reach is the same rule the execution layer applies to a deliberate kick (and the one
        // HumanInputController uses for the human's strike), so this cannot promise a contact execution would
        // refuse.
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        float distToBall = Vector2.Distance(selfXZ, ballXZ);
        bool receivingOrDribbling = BallContactRules.IsReceivingOrDribbling(
            obs.BallLastTouchedByTeam,
            obs.BallLastTouchedByPlayerId,
            obs.SelfTeam,
            obs.SelfId);
        float kickReach = BallContactRules.ReachRadius(
            obs.SelfAttributes.ReachRadius,
            keeperHandling: false,
            isDiving: false,
            requestingTackle: false,
            receivingOrDribbling);
        if (distToBall > kickReach)
        {
            Vector2 toBall = distToBall > 1e-4f
                ? (ballXZ - selfXZ) / distToBall
                : new Vector2(obs.AttackingDirectionX, 0f);
            return new FootballerCommand(
                toBall, toBall, false, false, false, false, false, Vector3.Zero,
                KickIntentType.None, new Vector3(toBall.X, 0f, toBall.Y), 0f, 0f);
        }

        // M5: the phase checks below used to be dead — a restart's pause resets the phase before the first
        // live tick, so a controller never observes MatchPhase.PenaltyKick or CornerKick. The armed restart
        // is what the player actually knows he is taking.
        RestartContext restart = obs.TacticalContext.Restart;
        if (restart.IsFor(obs.SelfTeam) && restart.Kind == RestartKind.PenaltyKick)
        {
            Vector2 goalAim = Vector2.Normalize(new Vector2(oppGoalCenter.X - selfXZ.X, 2.1f - selfXZ.Y));
            return new FootballerCommand(
                goalAim, goalAim, false, false, false, false, false, Vector3.Zero,
                KickIntentType.Shot, new Vector3(goalAim.X, 0f, goalAim.Y), 0.78f, 0.35f);
        }

        // A8.5: a live restart routine is the team's choice of option — play the short ball to the support, or
        // deliver to the target. The taker plays it when the delivery to that receiver can actually be planned
        // from his own picture; otherwise he keeps the A4.9 heuristic below, so a routine never strands a restart.
        if (obs.Routine.IsTaker &&
            TryPlanRestartRoutineDelivery(obs, obs.Routine, out FootballerCommand routineCommand))
        {
            return routineCommand;
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

        // A4.9: the taker is a passer, so he plans like one. Measured before this, throw-ins completed
        // 29 % and every long one was a lofted ball that flew 10-20 m over its receiver's head.
        //
        // The choice is deliberately ordered: the most advanced receiver whose ground route is *open*
        // first, then the most advanced one whose ground route is merely reachable (a flat contested ball
        // is still better than a lofted one the receiver cannot head), and only then a raised delivery.
        bool bestMatePlanned = false;
        FootballerCommand bestMateDelivery = default;
        FootballerCommand openDelivery = default;
        float openScore = float.NegativeInfinity;

        foreach (PerceivedEntity mate in obs.VisiblePlayers)
        {
            if (mate.Team != obs.SelfTeam) continue;
            Vector2 mateXZ = new(mate.EstimatedPosition.X, mate.EstimatedPosition.Z);
            float mateDistance = Vector2.Distance(selfXZ, mateXZ);
            if (mateDistance < 4.0f || mateDistance > 38.0f) continue;

            bool planned = _generator.TryPlanSetPieceDelivery(
                obs, mate, out FootballerCommand delivery, out bool laneOpen);

            if (bestMate.HasValue && mate.PlayerId == bestMate.Value.PlayerId)
            {
                bestMatePlanned = planned;
                bestMateDelivery = delivery;
            }

            if (!planned || !laneOpen) continue;
            float score = mate.EstimatedPosition.X * obs.AttackingDirectionX - mateDistance * 0.35f;
            if (score > openScore)
            {
                openScore = score;
                openDelivery = delivery;
            }
        }

        if (openScore > float.NegativeInfinity) return openDelivery;
        if (bestMatePlanned) return bestMateDelivery;

        Vector2 dir = Vector2.Normalize(targetXZ - selfXZ);
        float dist = Vector2.Distance(selfXZ, targetXZ);
        KickIntentType kickType = (restart.IsFor(obs.SelfTeam) && restart.Kind == RestartKind.CornerKick) ||
                                  dist > 22.0f
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
    /// Plays the restart options the team's routine named (A8.5): the taker delivers to the receiver the chosen
    /// option is written for, using A4.9's own set-piece planning.
    ///
    /// The routine is a team decision about <i>which lawful option</i> a restart is taken with; it is not a
    /// guarantee that the ball can be played. If the named receiver is not in this player's picture, or the
    /// passer cannot plan a delivery to him, the caller falls back to the pre-routine heuristic — the same rule
    /// A8.4's participants follow ("each player applies the contract to his own picture").
    /// </summary>
    private bool TryPlanRestartRoutineDelivery(
        in FootballerObservation obs,
        in RestartRoutineContext routine,
        out FootballerCommand command)
    {
        command = default;
        int receiverId = routine.ReceiverId;
        if (receiverId <= 0) return false;

        IReadOnlyList<PerceivedEntity> visible = obs.VisiblePlayers;
        for (int i = 0; i < visible.Count; i++)
        {
            PerceivedEntity mate = visible[i];
            if (mate.PlayerId != receiverId || mate.Team != obs.SelfTeam) continue;

            return _generator.TryPlanSetPieceDelivery(obs, mate, out command, out _);
        }

        return false;
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