using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;

namespace FootballSim.Rules;

/// <summary>Explicit goalkeeper lifecycle (audit F03).</summary>
public enum GoalkeeperState
{
    Positioning,
    Saving,
    HoldingBall,
    Distribution,
    Recovering
}

public sealed class GoalkeeperController : IFootballerController
{
    private const float MaxReceivableSpeed = 8.0f;

    private readonly PitchDimensions _pitch;
    private readonly PassPlanner _passPlanner;
    private string _debugSummary = "GK:Positioning";

    public GoalkeeperController(PitchDimensions pitch)
    {
        _pitch = pitch;
        _passPlanner = new PassPlanner(pitch);
    }

    public ControllerSourceType SourceType => ControllerSourceType.AI;
    public string GetDebugSummary() => _debugSummary;

    /// <summary>Current goalkeeper lifecycle state (audit F03), for tests and diagnostics.</summary>
    public GoalkeeperState State { get; private set; } = GoalkeeperState.Positioning;

    public FootballerCommand Decide(in FootballerObservation obs, float dt)
    {
        float ownGoalX = -obs.AttackingDirectionX * _pitch.HalfLength;
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        Vector2 ballVelXZ = new(obs.PerceivedBallVelocity.X, obs.PerceivedBallVelocity.Z);

        float distToBall = Vector2.Distance(selfXZ, ballXZ);
        float verticalDelta = obs.PerceivedBallPosition.Y - obs.SelfPosition.Y;

        // Audit F03: the keeper is holding the ball in its hands. This must be handled before any
        // reach/recovery gate: while holding, the keeper cannot rush, dive or tackle, and the only
        // legal action is a single, deliberate release once the hold delay has elapsed.
        bool isHoldingBall = obs.BallControlState == BallControlState.KeeperHeld &&
                             obs.BallControllingPlayerId == obs.SelfId;
        if (isHoldingBall)
            return DecideHeldBall(obs, ownGoalX);

        State = obs.AvailableToKick ? GoalkeeperState.Positioning : GoalkeeperState.Recovering;

        // Audit F12: decision and execution must agree on reach. The keeper can only handle the ball
        // inside its physical standing reach (ReachRadius * 1.12 ≈ 0.84 m) and below its handling
        // height — the old gate used the wider approach radius (≈1.24 m), so the keeper would start
        // distribution movement while still unable to touch the ball.
        const float keeperHandlingHeight = 2.42f;
        bool ballWithinHandlingReach = distToBall <= obs.SelfAttributes.ReachRadius * 1.12f
                                       && verticalDelta >= -0.05f
                                       && verticalDelta <= keeperHandlingHeight;
        if (obs.AvailableToKick && ballWithinHandlingReach)
        {
            State = GoalkeeperState.Distribution;
            return DecideDistributionOrSave(obs, ownGoalX);
        }

        // 1. Incoming Fast Shot Detection -> Dive along the goal line only when shot is close (< 0.45s away)
        float speedTowardGoal = -obs.PerceivedBallVelocity.X * obs.AttackingDirectionX;
        if (speedTowardGoal > 11.0f)
        {
            // Audit F12: only a ball that is still approaching along X can be a future threat, and it
            // must arrive below the crossbar. The old code used |ballX - keeperX|, so a ball that had
            // already passed the keeper still looked like a future shot.
            float velocityX = obs.PerceivedBallVelocity.X;
            float timeToPlane = MathF.Abs(velocityX) > 1e-3f
                ? (obs.SelfPosition.X - obs.PerceivedBallPosition.X) / velocityX
                : float.MaxValue;

            if (timeToPlane > 0.02f && timeToPlane < 0.48f)
            {
                float predictedZ = obs.PerceivedBallPosition.Z + obs.PerceivedBallVelocity.Z * timeToPlane;
                float predictedY = obs.PerceivedBallPosition.Y + obs.PerceivedBallVelocity.Y * timeToPlane;
                if (MathF.Abs(predictedZ) <= _pitch.HalfGoalWidth + 0.6f &&
                    predictedY <= _pitch.GoalHeight + 0.35f)
                {
                    Vector3 interceptPoint = new(
                        ownGoalX + obs.AttackingDirectionX * 0.9f,
                        0.11f,
                        Math.Clamp(predictedZ, -_pitch.HalfGoalWidth + 0.3f, _pitch.HalfGoalWidth - 0.3f));
                    Vector3 diveVec = interceptPoint - obs.SelfPosition;
                    Vector2 diveXZ = new(diveVec.X, diveVec.Z);
                    float lateralOffset = diveXZ.Length();
                    if (lateralOffset > 1e-4f) diveXZ /= lateralOffset;

                    State = GoalkeeperState.Saving;
                    _debugSummary = $"GK:DiveSave(z={predictedZ:F1}m)";
                    return new FootballerCommand(
                        diveXZ, new Vector2(obs.AttackingDirectionX, 0f), true, false, false, false,
                        lateralOffset > 0.85f && lateralOffset < 2.6f, diveVec,
                        KickIntentType.None, Vector3.UnitX, 0f, 0f);
                }
            }
        }

        // 2. 1v1 Rush / Sweeping inside Penalty Area.
        // Audit F03: only ever sweep an opponent's ball. A friendly loose ball (a back-pass, or the
        // keeper's own parry) must be played with the feet or left to a defender — never charged
        // down and tackled toward its own goal.
        bool opponentHasBall = obs.PossessingTeam != obs.SelfTeam;
        bool ballInBox = MathF.Abs(ballXZ.X - ownGoalX) < _pitch.PenaltyAreaLength &&
                         MathF.Abs(ballXZ.Y) < _pitch.PenaltyAreaWidth * 0.45f;
        if (opponentHasBall && ballInBox && distToBall < 12.5f)
        {
            // Audit F09: when the keeper is exactly over the ball this vector is zero; a bare
            // Vector2.Normalize produced NaN and the command carried it into the locomotion step.
            Vector2 dirToBall = SimMath.SafeNormalize(ballXZ - selfXZ, new Vector2(obs.AttackingDirectionX, 0f));
            _debugSummary = "GK:Rush1v1Sweep";
            return new FootballerCommand(
                dirToBall, dirToBall, true, false, distToBall < 1.3f, false, false, Vector3.Zero,
                KickIntentType.None, Vector3.UnitX, 0f, 0f);
        }

        // 3. Tight Angle-Bisecting Arc Positioning
        Vector2 goalCenter = new(ownGoalX, 0f);
        Vector2 goalToBall = ballXZ - goalCenter;
        float distGoalToBall = goalToBall.Length();
        Vector2 dirGoalToBall = SimMath.SafeNormalize(goalToBall, new Vector2(obs.AttackingDirectionX, 0f));

        float standoff = Math.Clamp(distGoalToBall * 0.10f, 1.1f, 4.2f);
        Vector2 idealSpot = goalCenter + dirGoalToBall * standoff;
        idealSpot.Y = Math.Clamp(idealSpot.Y, -_pitch.HalfGoalWidth * 0.78f, _pitch.HalfGoalWidth * 0.78f);

        Vector2 move = idealSpot - selfXZ;
        float moveDist = move.Length();
        Vector2 moveDir = moveDist > 0.15f ? (move / moveDist) * Math.Clamp(moveDist / 1.8f, 0.45f, 1.0f) : Vector2.Zero;

        _debugSummary = "GK:AngleBisect";
        return new FootballerCommand(
            moveDir, dirGoalToBall, moveDist > 2.0f, false, false, false, false, Vector3.Zero,
            KickIntentType.None, Vector3.UnitX, 0f, 0f);
    }

    /// <summary>
    /// A caught ball is held, not chased. Audit F03: the keeper stood still for a beat, then chose
    /// distribution — never <c>GK:Rush1v1Sweep</c> on its own catch, and never the generic tackle
    /// branch that used to propel the ball toward its own goal.
    /// </summary>
    private FootballerCommand DecideHeldBall(in FootballerObservation obs, float ownGoalX)
    {
        Vector2 upfield = new(obs.AttackingDirectionX, 0f);

        if (obs.BallHoldRemainingSeconds > 0f || !obs.AvailableToKick)
        {
            State = obs.BallHoldRemainingSeconds > 0f
                ? GoalkeeperState.HoldingBall
                : GoalkeeperState.Recovering;
            _debugSummary = $"GK:HoldingBall({obs.BallHoldRemainingSeconds:F2}s)";
            return new FootballerCommand(
                Vector2.Zero, upfield, false, false, false, false, false, Vector3.Zero,
                KickIntentType.None, Vector3.UnitX, 0f, 0f);
        }

        State = GoalkeeperState.Distribution;
        return DecideDistributionOrSave(obs, ownGoalX);
    }

    private FootballerCommand DecideDistributionOrSave(in FootballerObservation obs, float ownGoalX)
    {
        State = GoalkeeperState.Distribution;
        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);

        // Always look for best open teammate (FullBack, CenterBack, or Midfielder) before kicking long
        PerceivedEntity? bestMate = null;
        float bestScore = -999f;

        foreach (var mate in obs.VisiblePlayers)
        {
            if (mate.Team != obs.SelfTeam) continue;
            Vector2 mateXZ = new(mate.EstimatedPosition.X, mate.EstimatedPosition.Z);
            float d = Vector2.Distance(selfXZ, mateXZ);
            if (d is < 6.0f or > 42.0f) continue;

            float clearance = TacticalUtilityAiController.EvaluateLaneClearance(selfXZ, mateXZ, obs.VisiblePlayers, obs.SelfTeam);
            if (clearance < 0.45f) continue;

            float score = clearance * 2.0f - (d / 45.0f) * (obs.TeamProfile.ShortGoalkeeperDistribution ? 0.8f : -0.4f);
            if (score > bestScore)
            {
                bestScore = score;
                bestMate = mate;
            }
        }

        if (bestMate.HasValue)
        {
            Vector2 mateXZ = new(bestMate.Value.EstimatedPosition.X, bestMate.Value.EstimatedPosition.Z);
            Vector2 mateVelocityXZ = new(bestMate.Value.EstimatedVelocity.X, bestMate.Value.EstimatedVelocity.Z);
            Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);

            // Audit F01: keeper distribution uses the same planner, so it is aimed at where the
            // teammate will be when the ball arrives rather than at the teammate's current spot.
            var plan = _passPlanner.PlanGroundPass(ballXZ, bestMate.Value.PlayerId, mateXZ, mateVelocityXZ, MaxReceivableSpeed);

            // Audit F03/F12: validate the outgoing route against the keeper's own goal. A
            // distribution that would run through its own goal mouth is never a safe outlet.
            if (plan.Feasible && !AimCrossesOwnGoal(ballXZ, plan.AimDirectionXZ, ownGoalX))
            {
                _debugSummary = $"GK:PassTo#{bestMate.Value.PlayerId}";
                return new FootballerCommand(
                    plan.AimDirectionXZ, plan.AimDirectionXZ, false, false, false, false, false, Vector3.Zero,
                    KickIntentType.GroundPass,
                    new Vector3(plan.AimDirectionXZ.X, 0f, plan.AimDirectionXZ.Y), plan.Power01, 0f)
                {
                    IntendedReceiverId = bestMate.Value.PlayerId
                };
            }
        }

        Vector2 wideAim = SimMath.SafeNormalize(
            new Vector2(obs.AttackingDirectionX, obs.SelfPosition.Z >= 0f ? 0.35f : -0.35f),
            new Vector2(obs.AttackingDirectionX, 0f));
        _debugSummary = "GK:SafeDistribution";
        return new FootballerCommand(
            wideAim, wideAim, false, false, false, false, false, Vector3.Zero,
            KickIntentType.LoftedPass, new Vector3(wideAim.X, 0f, wideAim.Y), 0.72f, 0f);
    }

    /// <summary>
    /// True when a kick from <paramref name="fromXZ"/> along <paramref name="aimDirXZ"/> would cross
    /// the goalkeeper's own goal line inside (or just beside) the goal mouth.
    /// </summary>
    private bool AimCrossesOwnGoal(Vector2 fromXZ, Vector2 aimDirXZ, float ownGoalX)
    {
        float towardOwnGoal = ownGoalX > 0f ? aimDirXZ.X : -aimDirXZ.X;
        if (towardOwnGoal <= 0.05f) return false;

        float distanceToGoalLine = MathF.Abs(ownGoalX - fromXZ.X);
        float timeToGoalLine = distanceToGoalLine / MathF.Max(0.05f, MathF.Abs(aimDirXZ.X));
        float zAtGoalLine = fromXZ.Y + aimDirXZ.Y * timeToGoalLine;
        return MathF.Abs(zAtGoalLine) <= _pitch.HalfGoalWidth + 0.6f;
    }
}

public readonly record struct MatchRuleEvent(
    float MatchTimeSeconds,
    string EventType,
    TeamId Team,
    int? PlayerId,
    Vector3 Location,
    string Details
)
{
    /// <summary>
    /// The attacking player credited with a goal, or null for an own goal (audit F20). <see cref="PlayerId"/>
    /// stays the physical last toucher, so a keeper's failed save is no longer indistinguishable from a
    /// defender's own goal or from an attacker's goal after a save.
    /// </summary>
    public int? ScorerPlayerId { get; init; }

    /// <summary>The defending player who put the ball into their own goal, when that is how it went in.</summary>
    public int? OwnGoalPlayerId { get; init; }

    /// <summary>True when the goal is credited as an own goal.</summary>
    public bool IsOwnGoal => OwnGoalPlayerId.HasValue;
}

/// <summary>
/// The world as it stood when the ball was played, captured by the engine before the physics step and
/// handed to the rules engine with the tick's contacts.
///
/// Why it exists: <see cref="MatchRulesEngine.StepRules"/> runs *after* <c>BallPhysicsSolver.Step</c>,
/// so the live <see cref="BallState"/> has already moved when a contact is judged. A laws decision
/// read from a post-integration ball is decided by where the ball is going, not by where it was played.
///
/// The buffer is reused: <see cref="Capture"/> overwrites a preallocated array, so turning this on
/// costs no per-tick allocation. Player geometry is recorded even though locomotion and contact
/// resolution currently happen in one call, because A1.2 moves kicks to the intent executor and the
/// positions can then legitimately differ between the play and the rules pass.
/// </summary>
public sealed class ContactGeometrySnapshot
{
    private readonly int[] _playerIds;
    private readonly TeamId[] _playerTeams;
    private readonly Vector3[] _playerPositions;
    private readonly bool[] _playerActive;

    public ContactGeometrySnapshot(int capacity = 24)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        _playerIds = new int[capacity];
        _playerTeams = new TeamId[capacity];
        _playerPositions = new Vector3[capacity];
        _playerActive = new bool[capacity];
    }

    /// <summary>True once <see cref="Capture"/> ran for the current tick.</summary>
    public bool IsCaptured { get; private set; }

    public int Tick { get; private set; }
    public Vector3 BallPosition { get; private set; }
    public int Count { get; private set; }

    public void Reset()
    {
        IsCaptured = false;
        Tick = 0;
        BallPosition = default;
        Count = 0;
    }

    /// <summary>Overwrites the buffer with the players and ball position of one play.</summary>
    public void Capture(int tick, IReadOnlyList<FootballerState> players, Vector3 ballPosition)
    {
        Tick = tick;
        BallPosition = ballPosition;
        Count = 0;

        for (int i = 0; i < players.Count && Count < _playerIds.Length; i++)
        {
            FootballerState player = players[i];
            _playerIds[Count] = player.Id;
            _playerTeams[Count] = player.Team;
            _playerPositions[Count] = player.Position;
            _playerActive[Count] = player.IsActiveOnPitch;
            Count++;
        }

        IsCaptured = true;
    }

    public int PlayerIdAt(int index) => _playerIds[index];
    public TeamId TeamAt(int index) => _playerTeams[index];
    public Vector3 PositionAt(int index) => _playerPositions[index];
    public bool IsActiveAt(int index) => _playerActive[index];
}

public sealed class MatchRulesEngine
{
    private readonly PitchDimensions _pitch;
    private readonly float _ballRadius;
    private readonly HashSet<int> _playersInOffsidePositionAtLastPass = new();
    private TeamId? _lastPassTeam;
    private bool _restartAwaitingKick;

    // Audit F14: a restart that cannot score directly (an indirect free kick, a throw-in) is armed
    // when its first play is made and satisfied by the next touch of any player.
    private bool _secondTouchPending;
    private TeamId _secondTouchRestartTeam;

    // Audit F20: goal attribution is tracked separately from the ball's last touch. The shot survives
    // a save (a parried shot that still goes in is the shooter's goal, not the keeper's own goal) but
    // is cleared by any other touch.
    private BallContactEvent? _lastShotContact;

    public MatchRulesEngine(PitchDimensions pitch, float ballRadius = BallGeometry.DefaultRadius)
    {
        ArgumentNullException.ThrowIfNull(pitch);
        // Audit F22: the rules' whole-ball plane comes from this radius, so it is validated instead
        // of silently replaced, and the pitch must be a real pitch.
        SimulationConfigValidation.ValidatePitch(pitch);
        if (!float.IsFinite(ballRadius) || ballRadius <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ballRadius), ballRadius, "The ball radius must be a finite, positive number.");
        }

        _pitch = pitch;
        _ballRadius = ballRadius;
    }

    public MatchPhase Phase { get; private set; } = MatchPhase.PreKickoff;

    /// <summary>Half currently being played. Independent of <see cref="Phase"/> (audit F05).</summary>
    public MatchPeriod Period { get; private set; } = MatchPeriod.FirstHalf;

    public float MatchClockSeconds { get; private set; }
    public int HomeScore { get; private set; }
    public int AwayScore { get; private set; }
    public TeamId RestartTeam { get; private set; } = TeamId.Home;
    public Vector3 RestartSpot { get; private set; } = Vector3.Zero;
    public int? DesignatedSetPieceTakerId { get; private set; }
    public float PhasePauseTimer { get; private set; }
    public List<MatchRuleEvent> Events { get; } = new();

    /// <summary>
    /// The restart that put the ball back into play and has not had its first touch yet (audit F14).
    /// It is set when the restart is set up, stamped onto that first contact, and then cleared, so
    /// open play can never inherit a restart's exemptions.
    /// </summary>
    public RestartKind ActiveRestart { get; private set; } = RestartKind.None;

    /// <summary>
    /// The restart stamped onto the most recent restart's first contact (audit F14). Diagnostic only:
    /// it proves what the match engine actually handed to the rules engine, while
    /// <see cref="ActiveRestart"/> is the live value.
    /// </summary>
    public RestartKind LastStampedRestartOrigin { get; private set; } = RestartKind.None;

    /// <summary>
    /// True while an indirect restart (an offside free kick or a throw-in) still needs a second touch
    /// before a goal can be awarded (audit F14).
    /// </summary>
    public bool AwaitingSecondTouchForGoal => _secondTouchPending;

    /// <summary>
    /// True while a whistle/setup delay keeps play stopped. The engine must not run controllers,
    /// contacts or ball physics while this is set (audit F06).
    /// </summary>
    public bool IsPlaySuspended => PhasePauseTimer > 0f;

    /// <summary>
    /// True only while a designated taker still has to take the restart. It is cleared by the
    /// taker's first real kick, so it cannot leak into open play (audit F07).
    /// </summary>
    public bool RestartAwaitingKick => _restartAwaitingKick;

    /// <summary>
    /// Stamps this tick's first contact with <see cref="ActiveRestart"/> (audit F14). The ball is
    /// stationary at a restart, so the first touch after it is the restart's first play. The engine
    /// calls this after execution and before <see cref="StepRules"/> so the rules engine can apply
    /// the Law 11 restart exemptions to the touch that actually made the restart.
    /// </summary>
    public void StampRestartOrigin(IList<BallContactEvent> contacts)
    {
        if (contacts.Count == 0 || ActiveRestart == RestartKind.None) return;

        RestartKind origin = ActiveRestart;
        ActiveRestart = RestartKind.None;
        LastStampedRestartOrigin = origin;
        contacts[0] = contacts[0] with { RestartOrigin = origin };
    }

    public float GetHomeAttackDirectionX() => Period == MatchPeriod.SecondHalf ? -1.0f : 1.0f;

    public void StartMatch(IReadOnlyList<FootballerState> players, BallState ball)
    {
        Phase = MatchPhase.FirstHalf;
        Period = MatchPeriod.FirstHalf;
        MatchClockSeconds = 0f;
        RestartTeam = TeamId.Home;
        DesignatedSetPieceTakerId = null;
        _restartAwaitingKick = false;
        SetupKickoffPositions(players, ball, TeamId.Home, GetHomeAttackDirectionX());
        Events.Add(new MatchRuleEvent(0f, "Kickoff", TeamId.Home, null, Vector3.Zero, "First Half Kickoff"));
    }

    /// <summary>
    /// Starts a match fragment from an already-described world (a scenario), instead of from a
    /// kickoff. The caller has already placed the players and the ball, so this only establishes the
    /// phase, clock and scoreboard, and clears every restart/offside trace that a kickoff would have
    /// reset.
    ///
    /// No restart is active: a fragment starts in open play. A fixture that wants a throw-in or a
    /// corner should ask for one rather than inherit an exemption by accident.
    /// </summary>
    public void StartScenario(
        float clockSeconds,
        MatchPeriod period,
        int homeScore,
        int awayScore,
        string label)
    {
        if (!float.IsFinite(clockSeconds) || clockSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(clockSeconds), clockSeconds,
                "A scenario clock must be finite and non-negative.");
        if (homeScore < 0) throw new ArgumentOutOfRangeException(nameof(homeScore), homeScore, "Score cannot be negative.");
        if (awayScore < 0) throw new ArgumentOutOfRangeException(nameof(awayScore), awayScore, "Score cannot be negative.");

        Phase = MatchPhase.FirstHalf;
        Period = period;
        MatchClockSeconds = clockSeconds;
        HomeScore = homeScore;
        AwayScore = awayScore;
        RestartTeam = TeamId.Home;
        RestartSpot = Vector3.Zero;
        PhasePauseTimer = 0f;
        DesignatedSetPieceTakerId = null;
        _restartAwaitingKick = false;
        ActiveRestart = RestartKind.None;
        LastStampedRestartOrigin = RestartKind.None;
        ClearSecondTouchRequirement();
        ClearOffsidePhase();
        _lastShotContact = null;
        Events.Clear();
        Events.Add(new MatchRuleEvent(clockSeconds, "ScenarioStart", TeamId.Home, null, Vector3.Zero,
            string.IsNullOrWhiteSpace(label) ? "Scenario start" : label));
    }

    public void StepRules(
        IReadOnlyList<FootballerState> players,
        BallState ball,
        float halfDurationSeconds,
        float dt,
        IReadOnlyList<BallContactEvent>? contactEvents = null,
        ContactGeometrySnapshot? contactGeometry = null)
    {
        if (Phase == MatchPhase.FullTime)
            return;

        ConsumeRestartIfTaken(players, ball);

        if (PhasePauseTimer > 0f)
        {
            PhasePauseTimer -= dt;
            if (PhasePauseTimer <= 0f)
                ResumePlayFromRestart(players, ball);
            return;
        }

        MatchClockSeconds += dt;
        float homeDirX = GetHomeAttackDirectionX();

        if (Phase == MatchPhase.FirstHalf && MatchClockSeconds >= halfDurationSeconds)
        {
            Phase = MatchPhase.HalfTime;
            PhasePauseTimer = 0.5f;
            RestartTeam = TeamId.Away;
            Events.Add(new MatchRuleEvent(MatchClockSeconds, "HalfTime", TeamId.Away, null, Vector3.Zero, $"HT {HomeScore}-{AwayScore}"));
            return;
        }
        if (Phase == MatchPhase.SecondHalf && MatchClockSeconds >= halfDurationSeconds * 2.0f)
        {
            Phase = MatchPhase.FullTime;
            Events.Add(new MatchRuleEvent(MatchClockSeconds, "FullTime", TeamId.Home, null, Vector3.Zero, $"FT {HomeScore}-{AwayScore}"));
            return;
        }

        if (contactEvents is { Count: > 0 } && ProcessContactEvents(contactEvents, players, ball, homeDirX, contactGeometry))
            return;

        TryResolveBoundaryEvent(ball, players, homeDirX);
    }

    /// <summary>
    /// Classifies the ball's first exit from the field of play, or a goal (audit F20). The crossing the
    /// physics solver recorded on its substeps is authoritative: it carries the position at the moment
    /// the whole ball crossed, so the geometry is read there (a high shot descending behind the goal is
    /// not a goal) and the first event of the tick wins (a diagonal exit can cross a touchline before a
    /// goal line). Only a ball moved without physics falls back to the endpoint check.
    /// </summary>
    private bool TryResolveBoundaryEvent(
        BallState ball,
        IReadOnlyList<FootballerState> players,
        float homeDirX)
    {
        if (!TryGetBoundaryEvent(ball, out BallBoundary boundary, out Vector3 at))
            return false;

        if (boundary is BallBoundary.GoalLineNegativeX or BallBoundary.GoalLinePositiveX)
        {
            bool crossedPositiveX = boundary == BallBoundary.GoalLinePositiveX;
            TeamId defendingEndTeam = (crossedPositiveX && homeDirX > 0f) || (!crossedPositiveX && homeDirX < 0f)
                ? TeamId.Away
                : TeamId.Home;

            // Whole-ball geometry (Law 9/10): a goal needs the ball's whole body past the line, between
            // the posts and under the crossbar. The frame's swept collision already rejects contact, so
            // this only decides balls that cleared the frame.
            bool inGoalMouth = MathF.Abs(at.Z) + _ballRadius <= _pitch.HalfGoalWidth &&
                               at.Y + _ballRadius <= _pitch.GoalHeight;

            if (inGoalMouth)
            {
                // Audit F14: an indirect restart (an offside free kick, or a throw-in) cannot score
                // until another player has touched the ball. A ball that goes straight in is a goal
                // kick - or a corner kick if the restart team put it into its own goal.
                if (_secondTouchPending)
                {
                    TeamId restartTeam = _secondTouchRestartTeam;
                    ClearSecondTouchRequirement();
                    if (restartTeam == defendingEndTeam)
                    {
                        TeamId attackingTeam = restartTeam == TeamId.Home ? TeamId.Away : TeamId.Home;
                        float ownGoalCornerX = crossedPositiveX ? _pitch.HalfLength - 0.5f : -_pitch.HalfLength + 0.5f;
                        float ownGoalCornerZ = at.Z >= 0f ? _pitch.HalfWidth - 0.5f : -_pitch.HalfWidth + 0.5f;
                        TriggerSetPiece(MatchPhase.CornerKick, attackingTeam,
                            new Vector3(ownGoalCornerX, _ballRadius, ownGoalCornerZ), players, ball,
                            "Corner Kick (indirect restart into own goal)", RestartKind.CornerKick);
                    }
                    else
                    {
                        float restartGoalKickX = crossedPositiveX ? _pitch.HalfLength - 5.5f : -_pitch.HalfLength + 5.5f;
                        TriggerSetPiece(MatchPhase.GoalKick, defendingEndTeam,
                            new Vector3(restartGoalKickX, _ballRadius, 0f), players, ball,
                            "Goal Kick (indirect restart cannot score directly)", RestartKind.GoalKick);
                    }
                    return true;
                }

                TeamId scoringTeam = defendingEndTeam == TeamId.Home ? TeamId.Away : TeamId.Home;

                if (scoringTeam == TeamId.Home) HomeScore++;
                else AwayScore++;

                // Audit F20: the goal event keeps the physical last toucher in PlayerId but carries the
                // attribution separately, so a failed save is not reported as an own goal.
                AttributeGoal(ball, scoringTeam, out int? scorerId, out int? ownGoalId);
                string credit = scorerId is int scorer
                    ? $"scorer #{scorer}"
                    : ownGoalId is int ownGoal
                        ? $"own goal #{ownGoal}"
                        : "unattributed";
                Events.Add(new MatchRuleEvent(MatchClockSeconds, "Goal", scoringTeam, ball.LastTouchedByPlayerId, at,
                    $"Goal! Score {HomeScore}-{AwayScore} ({credit})")
                {
                    ScorerPlayerId = scorerId,
                    OwnGoalPlayerId = ownGoalId
                });

                RestartTeam = scoringTeam == TeamId.Home ? TeamId.Away : TeamId.Home;
                Phase = MatchPhase.GoalScored;
                PhasePauseTimer = 0.5f;
                return true;
            }

            if (ball.LastTouchedByTeam == defendingEndTeam)
            {
                TeamId attackingTeam = defendingEndTeam == TeamId.Home ? TeamId.Away : TeamId.Home;
                float cornerX = crossedPositiveX ? _pitch.HalfLength - 0.5f : -_pitch.HalfLength + 0.5f;
                float cornerZ = at.Z >= 0f ? _pitch.HalfWidth - 0.5f : -_pitch.HalfWidth + 0.5f;
                TriggerSetPiece(MatchPhase.CornerKick, attackingTeam, new Vector3(cornerX, _ballRadius, cornerZ), players, ball, "Corner Kick", RestartKind.CornerKick);
            }
            else
            {
                float gkX = crossedPositiveX ? _pitch.HalfLength - 5.5f : -_pitch.HalfLength + 5.5f;
                TriggerSetPiece(MatchPhase.GoalKick, defendingEndTeam, new Vector3(gkX, _ballRadius, 0f), players, ball, "Goal Kick", RestartKind.GoalKick);
            }
            return true;
        }

        TeamId throwTeam = ball.LastTouchedByTeam == TeamId.Home ? TeamId.Away : TeamId.Home;
        float clampedX = Math.Clamp(at.X, -_pitch.HalfLength + 1f, _pitch.HalfLength - 1f);
        float touchZ = boundary == BallBoundary.TouchlinePositiveZ ? _pitch.HalfWidth - 0.2f : -_pitch.HalfWidth + 0.2f;
        TriggerSetPiece(MatchPhase.ThrowIn, throwTeam, new Vector3(clampedX, _ballRadius, touchZ), players, ball, "Throw-In", RestartKind.ThrowIn);
        return true;
    }

    /// <summary>
    /// The first boundary event of this tick (audit F20): the physics crossing when there is one,
    /// otherwise the endpoint of a ball that was moved without physics (a teleport, a test fixture, a
    /// drill reset) - classified with the same whole-ball rule.
    /// </summary>
    private bool TryGetBoundaryEvent(BallState ball, out BallBoundary boundary, out Vector3 at)
    {
        if (ball.BoundaryCrossings.Count > 0)
        {
            BallBoundaryCrossing crossing = ball.BoundaryCrossings[0];
            boundary = crossing.Boundary;
            at = crossing.CrossingPosition;
            return true;
        }

        float goalLinePlane = _pitch.HalfLength + _ballRadius;
        float touchLinePlane = _pitch.HalfWidth + _ballRadius;
        Vector3 position = ball.Position;
        if (MathF.Abs(position.X) > goalLinePlane)
            boundary = position.X > 0f ? BallBoundary.GoalLinePositiveX : BallBoundary.GoalLineNegativeX;
        else if (MathF.Abs(position.Z) > touchLinePlane)
            boundary = position.Z > 0f ? BallBoundary.TouchlinePositiveZ : BallBoundary.TouchlineNegativeZ;
        else
        {
            boundary = default;
            at = default;
            return false;
        }

        at = position;
        return true;
    }

    /// <summary>
    /// Credits a goal (audit F20). The attacking scorer and an own goal are different outcomes:
    /// a ball put in by the scoring team's own last touch is that player's goal; a ball a save only
    /// deflected in stays the shooter's goal; anything else that a defender last played is an own goal.
    /// </summary>
    private void AttributeGoal(BallState ball, TeamId scoringTeam, out int? scorerId, out int? ownGoalId)
    {
        scorerId = null;
        ownGoalId = null;

        if (ball.LastTouchedByTeam == scoringTeam)
        {
            scorerId = ball.LastTouchedByPlayerId;
        }
        else if (_lastShotContact is { } shot && shot.Team == scoringTeam)
        {
            // A save is a deflection, not a deliberate play: the shot was still the play that beat the
            // keeper, so the goal is credited to the shooter rather than to the keeper as an own goal.
            scorerId = shot.PlayerId;
        }
        else
        {
            ownGoalId = ball.LastTouchedByPlayerId;
        }
    }

    /// <summary>
    /// Applies a tackle foul. Returns true when the foul produced a restart, which tells the
    /// execution layer to cancel the pending ball contact instead of overwriting the new setup
    /// (audit F13).
    /// </summary>
    public bool EvaluateTackleFoul(
        int tacklerId,
        int victimId,
        Vector3 contactLocation,
        float severity,
        IReadOnlyList<FootballerState> players,
        BallState ball)
    {
        if (severity < 0.72f) return false;

        FootballerState? tackler = null;
        FootballerState? victim = null;
        foreach (var p in players)
        {
            if (p.Id == tacklerId) tackler = p;
            if (p.Id == victimId) victim = p;
        }
        if (tackler == null || victim == null) return false;

        float homeDirX = GetHomeAttackDirectionX();
        float tacklerOwnGoalX = tackler.Team == TeamId.Home ? -homeDirX * _pitch.HalfLength : homeDirX * _pitch.HalfLength;
        bool inPenaltyBox = MathF.Abs(contactLocation.X - tacklerOwnGoalX) <= _pitch.PenaltyAreaLength &&
                            MathF.Abs(contactLocation.Z) <= _pitch.PenaltyAreaWidth * 0.5f;

        if (severity > 1.15f)
        {
            tackler.YellowCards++;
            if (severity > 1.55f || tackler.YellowCards >= 2)
            {
                tackler.IsSentOff = true;
                Events.Add(new MatchRuleEvent(MatchClockSeconds, "RedCard", tackler.Team, tackler.Id, contactLocation, "Sent Off"));
            }
            else
            {
                Events.Add(new MatchRuleEvent(MatchClockSeconds, "YellowCard", tackler.Team, tackler.Id, contactLocation, "Caution"));
            }
        }

        if (inPenaltyBox)
        {
            float penSpotX = tacklerOwnGoalX + (tacklerOwnGoalX < 0 ? _pitch.PenaltySpotDistance : -_pitch.PenaltySpotDistance);
            TriggerSetPiece(MatchPhase.PenaltyKick, victim.Team, new Vector3(penSpotX, _ballRadius, 0f), players, ball, "Penalty Kick", RestartKind.PenaltyKick);
        }
        else
        {
            TriggerSetPiece(MatchPhase.FreeKick, victim.Team, new Vector3(contactLocation.X, _ballRadius, contactLocation.Z), players, ball, "Free Kick", RestartKind.FreeKick);
        }

        return true;
    }

    /// <summary>
    /// Applies plays/touches reported by the execution layer. Offside eligibility is snapshotted at
    /// the moment the ball is played — including a second touch by the same player — instead of when
    /// the "last toucher" id happens to change (audit F08). Returns true if play was restarted.
    /// </summary>
    private bool ProcessContactEvents(
        IReadOnlyList<BallContactEvent> contacts,
        IReadOnlyList<FootballerState> players,
        BallState ball,
        float homeDirX,
        ContactGeometrySnapshot? contactGeometry)
    {
        foreach (var contact in contacts)
        {
            // Audit F20: attribution state follows the play, independently of the ball's last touch.
            // A shot survives a save (a parried shot that still goes in stays the shooter's goal) and
            // is cleared by any other touch, so an old shot can never be credited much later.
            if (contact.Kind == BallContactKind.Kick && KickIntents.IsShot(contact.Intent))
                _lastShotContact = contact;
            else if (!contact.IsSaveOrDeflection)
                _lastShotContact = null;

            // An offence is judged at the touch itself, before this touch's own phase transition
            // (audit F08/F14). A save no longer clears the restriction, so an attacker who was in an
            // offside position when the ball was played is still penalised if the keeper parries it
            // and they then play it.
            if (_lastPassTeam == contact.Team && _playersInOffsidePositionAtLastPass.Contains(contact.PlayerId))
            {
                TeamId defendingTeam = contact.Team == TeamId.Home ? TeamId.Away : TeamId.Home;
                // Audit F14: an offside offence is restarted with an INDIRECT free kick, so the ball
                // must be touched again before a goal can count (see StepRules).
                // The spot is where the offence happened — the touch — not where the ball has since
                // been integrated to (A1.4).
                Vector3 offenceSpot = contactGeometry is { IsCaptured: true }
                    ? contactGeometry.BallPosition
                    : contact.BallPosition;
                TriggerSetPiece(MatchPhase.FreeKick, defendingTeam,
                    new Vector3(offenceSpot.X, _ballRadius, offenceSpot.Z), players, ball,
                    $"Offside against Player #{contact.PlayerId}", RestartKind.IndirectFreeKick);
                return true;
            }

            bool armsSecondTouch = ApplyContactToOffsidePhase(contact, players, ball, homeDirX, contactGeometry);

            // Any touch other than the restart kick itself satisfies an indirect restart's
            // second-touch requirement, after which a goal is allowed again.
            if (_secondTouchPending && !armsSecondTouch)
                ClearSecondTouchRequirement();
        }

        return false;
    }

    /// <summary>
    /// Applies one touch to the offside phase (audit F08/F14) and returns true when that touch is the
    /// first play of an indirect restart, i.e. when it arms the second-touch requirement rather than
    /// satisfying it.
    /// </summary>
    private bool ApplyContactToOffsidePhase(
        BallContactEvent contact,
        IReadOnlyList<FootballerState> players,
        BallState ball,
        float homeDirX,
        ContactGeometrySnapshot? contactGeometry)
    {
        // Law 11 restart exemptions: no offside offence can be committed from a throw-in, a goal
        // kick or a corner kick, so the restarting team's receivers are deliberately not snapshot.
        // A kickoff, a direct free kick and a penalty are normal play and do snapshot.
        if (contact.Kind == BallContactKind.Kick && contact.RestartOrigin != RestartKind.None)
        {
            if (RestartRules.ExemptsOffside(contact.RestartOrigin))
            {
                ClearOffsidePhase();
                return false;
            }

            SnapshotOffsidePositions(contact.PlayerId, contact.Team, players, ball, homeDirX, contactGeometry);

            if (RestartRules.RequiresSecondTouchBeforeGoal(contact.RestartOrigin))
            {
                _secondTouchPending = true;
                _secondTouchRestartTeam = contact.Team;
                return true;
            }

            return false;
        }

        // Audit F14: a deliberate save is not a deliberate play under Law 11. It cannot remove an
        // existing offside restriction, so the snapshot is left exactly as it was.
        if (contact.IsSaveOrDeflection || !contact.IsDeliberatePlay)
            return false;

        if (contact.Kind == BallContactKind.Collection)
        {
            // A controlled touch ends the phase without starting a new one.
            ClearOffsidePhase();
        }
        else
        {
            // A kick or a tackle is a deliberate play: it ends the previous offside phase and opens
            // a new one for the team that just played the ball.
            SnapshotOffsidePositions(contact.PlayerId, contact.Team, players, ball, homeDirX, contactGeometry);
        }

        return false;
    }

    private void ClearOffsidePhase()
    {
        _playersInOffsidePositionAtLastPass.Clear();
        _lastPassTeam = null;
    }

    private void ClearSecondTouchRequirement()
    {
        _secondTouchPending = false;
        _secondTouchRestartTeam = default;
    }

    private void SnapshotOffsidePositions(
        int passerId,
        TeamId passerTeam,
        IReadOnlyList<FootballerState> players,
        BallState ball,
        float homeDirX,
        ContactGeometrySnapshot? contactGeometry)
    {
        _lastPassTeam = passerTeam;
        _playersInOffsidePositionAtLastPass.Clear();

        float attackDirX = passerTeam == TeamId.Home ? homeDirX : -homeDirX;

        // A1.4: judge the play by the geometry that existed when the ball was played. Without a
        // snapshot this falls back to live state, which is what every pre-A1.4 caller and test does.
        bool useSnapshot = contactGeometry is { IsCaptured: true };

        var oppCoords = new List<float>();
        if (useSnapshot)
        {
            for (int i = 0; i < contactGeometry!.Count; i++)
            {
                if (!contactGeometry.IsActiveAt(i) || contactGeometry.TeamAt(i) == passerTeam) continue;
                oppCoords.Add(contactGeometry.PositionAt(i).X * attackDirX);
            }
        }
        else
        {
            foreach (var p in players)
            {
                if (!p.IsActiveOnPitch || p.Team == passerTeam) continue;
                oppCoords.Add(p.Position.X * attackDirX);
            }
        }

        oppCoords.Sort();
        float secondLastOpponentProj = oppCoords.Count >= 2 ? oppCoords[^2] : _pitch.HalfLength;
        Vector3 ballAtPlay = useSnapshot ? contactGeometry!.BallPosition : ball.Position;
        float ballProj = ballAtPlay.X * attackDirX;
        float offsideLineProj = MathF.Max(0f, MathF.Max(secondLastOpponentProj, ballProj));

        if (useSnapshot)
        {
            for (int i = 0; i < contactGeometry!.Count; i++)
            {
                if (!contactGeometry.IsActiveAt(i) || contactGeometry.TeamAt(i) != passerTeam) continue;
                if (contactGeometry.PlayerIdAt(i) == passerId) continue;
                if (contactGeometry.PositionAt(i).X * attackDirX > offsideLineProj + 0.15f)
                    _playersInOffsidePositionAtLastPass.Add(contactGeometry.PlayerIdAt(i));
            }

            return;
        }

        foreach (var mate in players)
        {
            if (!mate.IsActiveOnPitch || mate.Team != passerTeam || mate.Id == passerId) continue;
            if (mate.Position.X * attackDirX > offsideLineProj + 0.15f)
            {
                _playersInOffsidePositionAtLastPass.Add(mate.Id);
            }
        }
    }

    private void TriggerSetPiece(
        MatchPhase restartPhase,
        TeamId awardedTeam,
        Vector3 spot,
        IReadOnlyList<FootballerState> players,
        BallState ball,
        string reason,
        RestartKind restartKind)
    {
        Phase = restartPhase;
        RestartTeam = awardedTeam;
        RestartSpot = spot;
        PhasePauseTimer = 0.25f;
        _playersInOffsidePositionAtLastPass.Clear();

        // Audit F13: a restart is an atomic state transition. Anything the previous passage of
        // play left behind (stale toucher, stale offside state, ball history, recovery timers,
        // residual velocity) must not leak into the restart.
        _lastPassTeam = null;
        ClearSecondTouchRequirement();
        _lastShotContact = null;

        // Audit F14: carry the restart's Law 11 semantics until its first touch is played. A corner
        // kick that never happens (the phase is replaced again) must not leave an exemption behind.
        ActiveRestart = restartKind;
        LastStampedRestartOrigin = RestartKind.None;

        ball.Position = spot;
        ball.PreviousPosition = spot;
        ball.Velocity = Vector3.Zero;
        ball.AngularVelocity = Vector3.Zero;
        ball.HeldByPlayerId = null;
        ball.HeldReleaseDelaySeconds = 0f;
        ball.LastTouchedByTeam = awardedTeam;
        ball.LastTouchedByPlayerId = null;

        ResetTransientPlayerState(players);

        FootballerState? taker = null;
        float bestDist = float.MaxValue;
        foreach (var p in players)
        {
            if (!p.IsActiveOnPitch || p.Team != awardedTeam) continue;
            if (restartPhase == MatchPhase.GoalKick && p.Role == TacticalRole.Goalkeeper)
            {
                taker = p;
                break;
            }
            if (restartPhase != MatchPhase.GoalKick && p.Role == TacticalRole.Goalkeeper)
                continue;

            float d = Vector3.Distance(p.Position, spot);
            if (d < bestDist)
            {
                bestDist = d;
                taker = p;
            }
        }

        // Always refresh the designation: keeping a stale taker here is what let a sent-off
        // (or distant) player stay designated for the rest of the match (audit F07).
        DesignatedSetPieceTakerId = taker?.Id;
        _restartAwaitingKick = taker != null;

        if (taker != null)
        {
            // Audit F13: stand behind the ball relative to the taker's OWN attacking direction.
            // The old code used the home direction for both teams, so away restarts faced backwards.
            float attackDirX = taker.Team == TeamId.Home ? GetHomeAttackDirectionX() : -GetHomeAttackDirectionX();
            taker.Position = new Vector3(spot.X - attackDirX * 0.45f, 0f, spot.Z);
            taker.PreviousPosition = taker.Position;
            taker.Velocity = Vector3.Zero;
            taker.FacingXZ = new Vector2(attackDirX, 0f);
        }

        foreach (var p in players)
        {
            if (!p.IsActiveOnPitch || p.Team == awardedTeam) continue;
            Vector2 diff = new(p.Position.X - spot.X, p.Position.Z - spot.Z);
            float d = diff.Length();

            // Audit F13: exactly coincident opponents used to be skipped entirely, leaving them
            // standing on the ball. Push them toward the centre of the pitch with a deterministic
            // fallback direction (pushing along the touchline would just clamp back onto the spot).
            Vector2 pushDir = d > 1e-4f ? diff / d : new Vector2(0f, spot.Z >= 0f ? -1f : 1f);
            if (d < 9.15f)
            {
                Vector2 pushed = new Vector2(spot.X, spot.Z) + pushDir * 9.5f;
                p.Position = new Vector3(
                    Math.Clamp(pushed.X, -_pitch.HalfLength, _pitch.HalfLength),
                    0f,
                    Math.Clamp(pushed.Y, -_pitch.HalfWidth, _pitch.HalfWidth));
                p.PreviousPosition = p.Position;
            }
        }

        Events.Add(new MatchRuleEvent(MatchClockSeconds, restartPhase.ToString(), awardedTeam, taker?.Id, spot, reason));
    }

    /// <summary>
    /// Clears transient contact/movement state so a restart starts from a known baseline (audit F13).
    /// </summary>
    private static void ResetTransientPlayerState(IReadOnlyList<FootballerState> players)
    {
        foreach (var p in players)
        {
            if (!p.IsActiveOnPitch) continue;
            p.Velocity = Vector3.Zero;
            p.KickRecoveryTimer = 0f;
            p.TackleRecoveryTimer = 0f;
            p.DribbleTouchTimer = 0f;
            p.ControlQuality01 = 0f;
        }
    }

    /// <summary>
    /// A restart setup leaves the ball stationary, so any touch of a moving ball means the restart
    /// has been taken (by the taker, a short-corner receiver, or an opponent intercepting it).
    /// Consuming the designation here is what stops the old taker from hijacking open play
    /// (audit F07).
    /// </summary>
    private void ConsumeRestartIfTaken(IReadOnlyList<FootballerState> players, BallState ball)
    {
        if (!_restartAwaitingKick) return;

        // A designated taker who is no longer on the pitch must not strand the restart.
        if (DesignatedSetPieceTakerId is int takerId && !IsPlayerActive(players, takerId))
        {
            _restartAwaitingKick = false;
            DesignatedSetPieceTakerId = null;
            // Audit F14: the restart's exemptions only exist for its own first touch. If that touch
            // can no longer be taken, open play must not inherit them.
            ActiveRestart = RestartKind.None;
            return;
        }

        if (!ball.LastTouchedByPlayerId.HasValue) return;
        if (ball.Velocity.LengthSquared() <= 0.01f) return;

        _restartAwaitingKick = false;
        DesignatedSetPieceTakerId = null;
        // Normally the engine already stamped this tick's first contact (F14); this covers a touch
        // that produced no event so the exemption cannot survive into open play.
        ActiveRestart = RestartKind.None;
    }

    private static bool IsPlayerActive(IReadOnlyList<FootballerState> players, int playerId)
    {
        foreach (var p in players)
            if (p.Id == playerId) return p.IsActiveOnPitch;
        return false;
    }

    private void ResumePlayFromRestart(IReadOnlyList<FootballerState> players, BallState ball)
    {
        if (Phase == MatchPhase.HalfTime)
            Period = MatchPeriod.SecondHalf;

        if (Phase is MatchPhase.HalfTime or MatchPhase.GoalScored)
        {
            Phase = Period == MatchPeriod.SecondHalf ? MatchPhase.SecondHalf : MatchPhase.FirstHalf;
            SetupKickoffPositions(players, ball, RestartTeam, GetHomeAttackDirectionX());
            return;
        }

        // Audit F13: the taker designation must survive the resume. Clearing it here (the old
        // behaviour) meant the restart was never actually taken by the designated player: it was
        // dropped at the exact moment play became live, and the AI reverted to generic chasing.
        Phase = Period == MatchPeriod.SecondHalf ? MatchPhase.SecondHalf : MatchPhase.FirstHalf;
    }

    public void SetupKickoffPositions(
        IReadOnlyList<FootballerState> players,
        BallState ball,
        TeamId kickoffTeam,
        float homeAttackDirX)
    {
        ball.Position = new Vector3(0f, _ballRadius, 0f);
        ball.PreviousPosition = ball.Position;
        ball.Velocity = Vector3.Zero;
        ball.AngularVelocity = Vector3.Zero;
        ball.HeldByPlayerId = null;
        ball.HeldReleaseDelaySeconds = 0f;
        ball.LastTouchedByTeam = kickoffTeam;
        ball.LastTouchedByPlayerId = null;
        _playersInOffsidePositionAtLastPass.Clear();
        DesignatedSetPieceTakerId = null;
        _restartAwaitingKick = false;
        ClearOffsidePhase();
        ClearSecondTouchRequirement();
        _lastShotContact = null;

        // Audit F14: the kickoff is the restart in play until its first touch. It is NOT an offside
        // exemption (a player can be offside from a kickoff), so it is informational only.
        ActiveRestart = RestartKind.Kickoff;
        LastStampedRestartOrigin = RestartKind.None;

        int teamSize = players.Count / 2;
        bool assignedKickoffTaker = false;
        FootballerState? fallbackTaker = null;

        foreach (var p in players)
        {
            if (!p.IsActiveOnPitch) continue;
            float sideDir = p.Team == TeamId.Home ? -homeAttackDirX : homeAttackDirX;
            var (_, normX, normZ) = FootballSim.Tactics.TeamTacticalCoordinator.GetFormationRoleAndSlot(p.ShirtNumber, teamSize);

            float halfX = sideDir * ((1.0f - (normX + 1.0f) * 0.42f) * (_pitch.HalfLength * 0.85f));
            float z = normZ * (_pitch.HalfWidth * 0.72f);

            // Use Shirt #8 (Central Midfielder) or #4 in 5v5 to take the kickoff so #9 is free to make an immediate run or be human-controlled
            bool isKickoffCandidate = teamSize >= 8 ? p.ShirtNumber == 8 : p.ShirtNumber == 4;
            if (p.Team == kickoffTeam && fallbackTaker == null)
                fallbackTaker = p;

            if (p.Team == kickoffTeam && isKickoffCandidate && !assignedKickoffTaker)
            {
                halfX = sideDir * 0.45f;
                z = 0.15f;
                DesignatedSetPieceTakerId = p.Id;
                assignedKickoffTaker = true;
            }

            p.Position = new Vector3(halfX, 0f, z);
            p.PreviousPosition = p.Position;
            p.Velocity = Vector3.Zero;
            p.FacingXZ = new Vector2(-sideDir, 0f);
        }

        // A sent-off or missing preferred taker must not leave the restart without a taker.
        if (DesignatedSetPieceTakerId == null && fallbackTaker != null)
            DesignatedSetPieceTakerId = fallbackTaker.Id;

        _restartAwaitingKick = DesignatedSetPieceTakerId.HasValue;
    }
}
