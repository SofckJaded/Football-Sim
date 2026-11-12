using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Physics;

namespace FootballSim.Training;

public enum TrainingEventKind
{
    Goal,
    BallOutOfPlay
}

public readonly record struct TrainingEvent(float SessionSeconds, TrainingEventKind Kind, string Label);

/// <summary>
/// Runs a single <see cref="TrainingDrill"/> on the shared ball physics, execution system and human
/// input pipeline. The session owns the reset loop and can host AI participants supplied by the
/// drill (a passer, a goalkeeper, ...).
/// </summary>
public sealed class TrainingSession
{
    public const int HumanPlayerId = 1;
    public const float DefaultResetDelaySeconds = 1.1f;

    private readonly PitchDimensions _pitch;
    private readonly BallPhysicsSolver _ballSolver;
    private readonly FootballerExecutionSystem _executionSystem;
    private readonly SeededRandom _rng;
    private readonly MatchCommunicationContext _communication = new();
    private readonly BallPossessionTracker _possession = new();
    private readonly Dictionary<int, IFootballerController> _controllers = new();
    private readonly List<FootballerState> _players = new();
    private readonly List<TrainingActorSpec> _aiActors = new();
    private readonly float _resetDelaySeconds;

    private float _resetTimer;
    private float _clockSeconds;
    private int _tick;

    public TrainingSession(
        TrainingDrill drill,
        PitchDimensions? pitch = null,
        ulong seed = 20261107UL,
        float resetDelaySeconds = DefaultResetDelaySeconds)
    {
        Drill = drill ?? throw new ArgumentNullException(nameof(drill));
        _pitch = pitch ?? new PitchDimensions();
        // Audit F22: practice accepts a public pitch override, so it applies the same validation the
        // match engine applies instead of building a drill on a degenerate pitch.
        SimulationConfigValidation.ValidatePitch(_pitch);
        _resetDelaySeconds = MathF.Max(0.2f, resetDelaySeconds);
        _ballSolver = new BallPhysicsSolver(null, _pitch);
        _executionSystem = new FootballerExecutionSystem(_pitch);
        _rng = new SeededRandom(seed);
        Ball = new BallState();
        Human = new HumanInputController(HumanPlayerId, _communication);
        _controllers[HumanPlayerId] = Human;
        _players.Add(CreatePlayer(HumanPlayerId, TeamId.Home, 9, TacticalRole.Striker));

        TrainingSpawn humanSpawn = Drill.CreateSpawn(_pitch);
        foreach (TrainingActorSpec actor in Drill.CreateAiActors(_pitch, humanSpawn, _communication))
        {
            if (actor.Id == HumanPlayerId || _controllers.ContainsKey(actor.Id))
                throw new InvalidOperationException($"Training actor id {actor.Id} is already in use.");
            if (actor.Controller is null)
                throw new InvalidOperationException($"Training actor {actor.Id} has no controller.");

            _aiActors.Add(actor);
            _players.Add(CreatePlayer(actor.Id, actor.Team, actor.ShirtNumber, actor.Role));
            _controllers[actor.Id] = actor.Controller;
        }

        ResetToSpawn();
    }

    public TrainingDrill Drill { get; }
    public PitchDimensions Pitch => _pitch;

    /// <summary>Authoritative ball radius; practice draws the ball from this value (audit F22).</summary>
    public float BallRadius => _ballSolver.Parameters.Radius;
    public BallState Ball { get; }
    public FootballerState Player => _players[0];
    public IReadOnlyList<FootballerState> Players => _players;
    public HumanInputController Human { get; }
    public MatchCommunicationContext Communication => _communication;

    public float ClockSeconds => _clockSeconds;
    public int GoalsScored { get; private set; }
    public int BallsOutOfPlay { get; private set; }
    public int Resets { get; private set; }

    /// <summary>The goal/out event currently being celebrated; null once play resumes.</summary>
    public TrainingEventKind? ActiveOutcome { get; private set; }

    public float ActiveOutcomeRemainingSeconds => MathF.Max(0f, _resetTimer);
    public TrainingEvent? LastEvent { get; private set; }
    public bool IsResetting => _resetTimer > 0f;
    public bool HumanTouchedBall => Ball.LastTouchedByPlayerId == HumanPlayerId;

    public FootballerState? GetPlayer(int id)
    {
        foreach (FootballerState player in _players)
        {
            if (player.Id == id) return player;
        }
        return null;
    }

    public void ResetToSpawn()
    {
        TrainingSpawn spawn = Drill.CreateSpawn(_pitch);

        ResetPlayer(Player, spawn.PlayerPosition, spawn.PlayerFacingXZ, Player.Team);

        Ball.Position = spawn.BallPosition;
        Ball.PreviousPosition = spawn.BallPosition;
        Ball.Velocity = Vector3.Zero;
        Ball.AngularVelocity = Vector3.Zero;
        Ball.ContactState = BallContactSurface.Air;
        Ball.HeldByPlayerId = null;
        Ball.HeldReleaseDelaySeconds = 0f;
        Ball.LastTouchedByPlayerId = null;
        Ball.LastTouchedByTeam = null;
        _possession.Reset();

        foreach (TrainingActorSpec actor in _aiActors)
        {
            FootballerState? player = GetPlayer(actor.Id);
            if (player != null)
                ResetPlayer(player, actor.Position, actor.FacingXZ, actor.Team);
        }

        // A call-for-pass belongs to one repetition; never leak it into the next one.
        _communication.ClearCallForPass();

        // A1.5: AI working state belongs to one repetition too. A commitment or a tackle cooldown from
        // the previous attempt would make the next one start from a decision it never made.
        foreach (IFootballerController controller in _controllers.Values)
        {
            if (controller is IAiStateResettable resettable)
                resettable.ResetForPhase(AiResetReason.TrainingReset);
        }

        Drill.OnReset();
    }

    public void Step(float dt)
    {
        if (!float.IsFinite(dt) || dt <= 0f) return;

        if (_resetTimer > 0f)
        {
            _resetTimer = MathF.Max(0f, _resetTimer - dt);
            AdvanceSimulation(dt, controlEnabled: false);

            if (_resetTimer <= 0f)
            {
                ResetToSpawn();
                Resets++;
                ActiveOutcome = null;
            }
            return;
        }

        AdvanceSimulation(dt, controlEnabled: true);

        BallState ball = Ball;
        if (Drill.IsGoal(ball, _pitch))
        {
            GoalsScored++;

            // The goal is scored: settle the ball inside the net instead of letting it bounce back
            // out through the goal frame.
            float goalLineX = Drill.AttackingDirectionX * (_pitch.HalfLength + 0.45f);
            ball.Position = new Vector3(
                goalLineX,
                Math.Clamp(ball.Position.Y, 0.11f, _pitch.GoalHeight - 0.2f),
                Math.Clamp(ball.Position.Z, -_pitch.HalfGoalWidth + 0.3f, _pitch.HalfGoalWidth - 0.3f));
            ball.PreviousPosition = ball.Position;
            ball.Velocity = Vector3.Zero;
            ball.AngularVelocity = Vector3.Zero;

            BeginOutcome(TrainingEventKind.Goal, "GOAL!");
        }
        else if (Drill.IsBallOutOfBounds(ball, _pitch))
        {
            BallsOutOfPlay++;
            BeginOutcome(TrainingEventKind.BallOutOfPlay, "Ball out of play");
        }
        else
        {
            Drill.OnStep(this, dt);
        }
    }

    private void BeginOutcome(TrainingEventKind kind, string label)
    {
        ActiveOutcome = kind;
        _resetTimer = _resetDelaySeconds;
        LastEvent = new TrainingEvent(_clockSeconds, kind, label);
    }

    private void AdvanceSimulation(float dt, bool controlEnabled)
    {
        BallPossessionSnapshot possession = _possession.Current;

        foreach (FootballerState player in _players)
        {
            if (!player.IsActiveOnPitch) continue;

            FootballerObservation observation = BuildObservation(player, possession);
            player.LastCommand = controlEnabled
                ? _controllers[player.Id].Decide(observation, dt)
                : FootballerCommand.Idle;
        }

        _executionSystem.UpdateLocomotionAndContacts(
            _players, Ball, _rng, dt, null, ++_tick,
            new ReceptionAssistContext(possession.State, possession.IntendedReceiverId, HumanPlayerId));
        _possession.Update(_players, Ball, _executionSystem.ContactEvents, dt);
        _ballSolver.Step(Ball, dt);
        _clockSeconds += dt;
    }

    private FootballerObservation BuildObservation(FootballerState player, BallPossessionSnapshot possession)
    {
        var visible = new List<PerceivedEntity>(Math.Max(0, _players.Count - 1));
        foreach (FootballerState other in _players)
        {
            if (other.Id == player.Id) continue;
            visible.Add(new PerceivedEntity(
                other.Id,
                other.Team,
                other.Role,
                other.Position,
                other.Velocity,
                1f));
        }

        float attackDirection = player.Team == TeamId.Home
            ? Drill.AttackingDirectionX
            : -Drill.AttackingDirectionX;

        return new FootballerObservation
        {
            SelfId = player.Id,
            SelfTeam = player.Team,
            SelfRole = player.Role,
            SelfPosition = player.Position,
            SelfVelocity = player.Velocity,
            SelfFacingXZ = player.FacingXZ,
            Stamina01 = player.Stamina01,
            AvailableToKick = player.AvailableToKick,
            ControlQuality01 = player.ControlQuality01,
            PerceivedBallPosition = Ball.Position,
            PerceivedBallVelocity = Ball.Velocity,
            PossessingTeam = possession.PossessionTeam ?? Ball.LastTouchedByTeam,
            BallLastTouchedByPlayerId = Ball.LastTouchedByPlayerId,
            BallControlState = possession.State,
            BallControllingPlayerId = possession.ControllingPlayerId,
            IntendedReceiverId = possession.IntendedReceiverId,
            BallHoldRemainingSeconds = Ball.HeldByPlayerId == player.Id ? Ball.HeldReleaseDelaySeconds : 0f,
            TacticalAnchorPosition = player.TacticalAnchor,
            AttackingDirectionX = attackDirection,
            MatchTimeSeconds = _clockSeconds,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = player.Tendencies,
            SelfAttributes = player.Attributes
        };
    }

    private static FootballerState CreatePlayer(int id, TeamId team, int shirtNumber, TacticalRole role) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = shirtNumber,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = new PlayerTendencies()
    };

    private void ResetPlayer(FootballerState player, Vector3 position, Vector2 facing, TeamId team)
    {
        player.Position = position;
        player.PreviousPosition = position;
        player.Velocity = Vector3.Zero;
        player.FacingXZ = facing;
        player.Stamina01 = 1f;
        player.Balance01 = 1f;
        player.ControlQuality01 = 0f;
        player.KickRecoveryTimer = 0f;
        player.DribbleTouchTimer = 0f;
        player.TackleRecoveryTimer = 0f;
        player.IsJumping = false;
        player.IsDiving = false;
        player.LastCommand = FootballerCommand.Idle;
        player.LastExecutionDiagnostic = "Ready";
        player.TacticalAnchor = new Vector3(
            (team == TeamId.Home ? Drill.AttackingDirectionX : -Drill.AttackingDirectionX)
                * (_pitch.HalfLength - _pitch.PenaltyAreaLength * 0.5f),
            0f,
            0f);

        if (player.Id == HumanPlayerId)
            Human.ClearBufferedActions();
    }
}
