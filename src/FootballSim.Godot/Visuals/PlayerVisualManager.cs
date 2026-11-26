using System;
using System.Collections.Generic;
using Godot;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Presentation;
using FootballSim.Rules;
using FootballSim.Training;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace FootballSim.GodotClient.Visuals;

/// <summary>
/// Owns every articulated player rig and its <see cref="PlayerDynamicsSolver"/>, and is the only thing that
/// moves a footballer's visual nodes at runtime.
///
/// It is strictly a <b>consumer</b>: it reads the authoritative simulation state after the fixed ticks for
/// the frame have run, feeds the contact stream and the head/GK facts into the solvers, and applies the
/// resulting poses. It never writes simulation state, never uses a simulation RNG and never feeds a visual
/// offset back into reach or contact — the presentation cannot change what happens on the pitch.
///
/// Both the match and free training use the same manager, so the two scenes can no longer drift apart.
/// </summary>
public sealed class PlayerVisualManager
{
    private sealed class PlayerEntry
    {
        public PlayerVisualRig Rig = null!;
        public PlayerDynamicsSolver Solver = null!;
        /// <summary>Previous execution diagnostic, for rising-edge keeper event detection.</summary>
        public string LastDiagnostic = string.Empty;
    }

    private readonly PlayerDynamicsSettings _settings;
    private readonly PlayerRigFactory _factory = new();
    private readonly Dictionary<int, PlayerEntry> _entries = new();

    private int _lastConsumedContactTick;
    private int _lastConsumedTrainingTick;
    private MatchPhase _lastGoalPhase = MatchPhase.PreKickoff;
    private TeamId? _scoringTeam;
    private int? _scorerPlayerId;

    public PlayerVisualManager(PlayerDynamicsSettings? settings = null)
    {
        _settings = settings ?? new PlayerDynamicsSettings();
    }

    /// <summary>Live presentation tuning (motion intensity, style, feature toggles).</summary>
    public PlayerDynamicsSettings Settings => _settings;

    /// <summary>Number of registered players; a quick sanity check for the scene's own count.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Builds the articulated rig for one participant, adds it to <paramref name="parent"/> and registers a
    /// solver for it. Called once per participant when the scene is built.
    /// </summary>
    public void Register(Node3D parent, FootballerState player, bool isHuman)
    {
        if (_entries.ContainsKey(player.Id)) return;

        PlayerVisualRig rig = _factory.Create(player, isHuman, _settings.Style);
        parent.AddChild(rig);
        _entries[player.Id] = new PlayerEntry
        {
            Rig = rig,
            Solver = new PlayerDynamicsSolver(player.Id, player.Role == TacticalRole.Goalkeeper, _settings),
            LastDiagnostic = player.LastExecutionDiagnostic
        };
    }

    /// <summary>
    /// One presentation frame of an 11v11 match. <paramref name="simDt"/> is the simulated advance of this
    /// frame (fixed ticks * fixed delta; exactly zero while the debug pause is on), never wall clock.
    /// </summary>
    public void UpdateMatch(MatchSimulationEngine engine, float alpha, float simDt)
    {
        FeedMatchContacts(engine);
        ReadMatchPhase(engine);

        BallState ball = engine.Ball;
        int? controllingPlayerId = engine.Possession.ControllingPlayerId;
        IReadOnlyList<FootballerState> players = engine.Players;

        for (int index = 0; index < players.Count; index++)
        {
            FootballerState player = players[index];
            if (!_entries.TryGetValue(player.Id, out PlayerEntry? entry)) continue;

            NoteKeeperDiagnostic(player, entry);
            FindNearestOpponent(players, player, out float opponentDistance, out NumericsVector2 opponentDirection);

            ScanStatus scan = ScanStatus.None;
            FootballerObservation? observation = engine.GetLastObservation(player.Id);
            if (observation is not null) scan = observation.Scan;

            GoalkeeperState keeperState = GoalkeeperState.Positioning;
            if (engine.GetController(player.Id) is GoalkeeperController keeper) keeperState = keeper.State;

            GoalkeeperController? keeperController = engine.GetController(player.Id) as GoalkeeperController;
            bool humanControlActive = engine.LockedHumanController is { IsSpectatorMode: false };
            HumanInputController? human = humanControlActive ? engine.LockedHumanController : null;

            var input = new PlayerDynamicsInput
            {
                Player = player,
                VisualPosition = NumericsVector3.Lerp(player.PreviousPosition, player.Position, alpha),
                BallPosition = ball.Position,
                BallVelocity = ball.Velocity,
                Scan = scan,
                GoalkeeperState = keeperController?.State ?? GoalkeeperState.Positioning,
                IsHumanControlled = human is not null && human.LockedPlayerId == player.Id,
                ChargedPower01 = human is not null && human.LockedPlayerId == player.Id
                    ? human.ChargedPower01
                    : 0f,
                Phase = engine.Rules.Phase,
                ScoringTeam = _scoringTeam,
                ScorerPlayerId = _scorerPlayerId,
                HoldsBall = ball.HeldByPlayerId == player.Id,
                ControlsBall = controllingPlayerId == player.Id,
                NearestOpponentDistance = opponentDistance,
                NearestOpponentDirectionXZ = opponentDirection
            };

            ApplyPlayer(in input, entry, simDt);
        }
    }

    /// <summary>One presentation frame of a free-training session (same pipeline, no match scan/phase data).</summary>
    public void UpdateTraining(TrainingSession session, float alpha, float simDt)
    {
        FeedTrainingContacts(session);

        BallState ball = session.Ball;
        IReadOnlyList<FootballerState> players = session.Players;
        NumericsVector2 ballXZ = new(ball.Position.X, ball.Position.Z);

        for (int index = 0; index < players.Count; index++)
        {
            FootballerState player = players[index];
            if (!_entries.TryGetValue(player.Id, out PlayerEntry? entry)) continue;

            NoteKeeperDiagnostic(player, entry);
            FindNearestOpponent(players, player, out float opponentDistance, out NumericsVector2 opponentDirection);

            GoalkeeperState keeperState = GoalkeeperState.Positioning;
            if (session.GetController(player.Id) is GoalkeeperController keeper) keeperState = keeper.State;

            NumericsVector2 playerXZ = new(player.Position.X, player.Position.Z);
            bool controlsBall = ball.LastTouchedByPlayerId == player.Id &&
                                NumericsVector2.Distance(playerXZ, ballXZ) < 1.1f;

            var input = new PlayerDynamicsInput
            {
                Player = player,
                VisualPosition = NumericsVector3.Lerp(player.PreviousPosition, player.Position, alpha),
                BallPosition = ball.Position,
                BallVelocity = ball.Velocity,
                Scan = ScanStatus.None,
                GoalkeeperState = keeperState,
                IsHumanControlled = player.Id == TrainingSession.HumanPlayerId,
                ChargedPower01 = player.Id == TrainingSession.HumanPlayerId ? session.Human.ChargedPower01 : 0f,
                Phase = MatchPhase.FirstHalf,
                HoldsBall = ball.HeldByPlayerId == player.Id,
                ControlsBall = controlsBall,
                NearestOpponentDistance = opponentDistance,
                NearestOpponentDirectionXZ = opponentDirection
            };

            ApplyPlayer(in input, entry, simDt);
        }
    }

    // =========================================================================================
    // Per-player application
    // =========================================================================================

    private static void ApplyPlayer(in PlayerDynamicsInput input, PlayerEntry entry, float simDt)
    {
        entry.Solver.Step(in input, simDt);
        entry.Rig.ApplyPose(entry.Solver.Pose);
        entry.Rig.Visible = input.Player.IsActiveOnPitch;
    }

    /// <summary>
    /// Distance and direction to the nearest active opponent. Used by the stumble/collision and
    /// 1v1-block stages; the presentation only needs the geometry, never who the opponent is.
    /// </summary>
    private static void FindNearestOpponent(
        IReadOnlyList<FootballerState> players,
        FootballerState player,
        out float distance,
        out NumericsVector2 direction)
    {
        distance = 0f;
        direction = NumericsVector2.Zero;
        float bestSquared = float.MaxValue;

        for (int index = 0; index < players.Count; index++)
        {
            FootballerState other = players[index];
            if (other.Id == player.Id || other.Team == player.Team || !other.IsActiveOnPitch) continue;
            float dx = other.Position.X - player.Position.X;
            float dz = other.Position.Z - player.Position.Z;
            float squared = dx * dx + dz * dz;
            if (squared >= bestSquared) continue;
            bestSquared = squared;
            distance = MathF.Sqrt(squared);
            direction = squared > 1e-6f
                ? new NumericsVector2(dx / distance, dz / distance)
                : NumericsVector2.Zero;
        }

        if (bestSquared == float.MaxValue) distance = 0f;
    }

    // =========================================================================================
    // Event feeds
    // =========================================================================================

    /// <summary>
    /// Feeds every contact added since the last frame into the owning solver. Scans the bounded log
    /// backwards by tick rather than tracking a list index, so log truncation (4096 entries) cannot make
    /// the renderer replay an old contact or silently skip a new one.
    /// </summary>
    private void FeedMatchContacts(MatchSimulationEngine engine)
    {
        IReadOnlyList<BallContactEvent> log = engine.ContactLog;
        int previouslyConsumed = _lastConsumedContactTick;
        int highestConsumed = previouslyConsumed;

        for (int index = log.Count - 1; index >= 0; index--)
        {
            BallContactEvent contact = log[index];
            if (contact.Tick <= previouslyConsumed) break;

            if (_entries.TryGetValue(contact.PlayerId, out PlayerEntry? entry))
                FeedContact(entry, in contact);
            if (contact.Tick > highestConsumed) highestConsumed = contact.Tick;
        }

        _lastConsumedContactTick = highestConsumed;
    }

    /// <summary>
    /// Feeds the newest training tick's contacts. Practice steps at most once per rendered frame outside a
    /// hitch, and the execution buffer only holds the latest tick, so a tick is consumed at most once by
    /// comparing the tick counter (a repeated read must not re-fire an impulse).
    /// </summary>
    private void FeedTrainingContacts(TrainingSession session)
    {
        int tick = session.CurrentTick;
        if (tick == _lastConsumedTrainingTick) return;
        _lastConsumedTrainingTick = tick;

        IReadOnlyList<BallContactEvent> contacts = session.LastTickContacts;
        for (int index = 0; index < contacts.Count; index++)
        {
            BallContactEvent contact = contacts[index];
            if (_entries.TryGetValue(contact.PlayerId, out PlayerEntry? entry))
                FeedContact(entry, in contact);
        }
    }

    /// <summary>
    /// Feeds one contact into its player's solver. A keeper touch additionally clears the last-seen execution
    /// diagnostic: two consecutive identical touches (a second claim/punch, a catch right after another) produce
    /// the same diagnostic string, and the rising-edge check in <see cref="NoteKeeperDiagnostic"/> would silently
    /// swallow the second visual reaction. The contact itself is the evidence that a new touch happened.
    /// </summary>
    private static void FeedContact(PlayerEntry entry, in BallContactEvent contact)
    {
        entry.Solver.NotifyContact(contact);
        if (contact.Kind == BallContactKind.Save) entry.LastDiagnostic = string.Empty;
    }

    /// <summary>
    /// Detects a keeper touch from the execution diagnostic rising edge. Catches, parries, claims and
    /// punches are already published there by the execution layer, so the presentation needs no second
    /// copy of the keeper's state machine.
    /// </summary>
    private static void NoteKeeperDiagnostic(FootballerState player, PlayerEntry entry)
    {
        string diagnostic = player.LastExecutionDiagnostic;
        if (diagnostic == entry.LastDiagnostic) return;
        entry.LastDiagnostic = diagnostic;

        KeeperVisualEvent visualEvent = diagnostic switch
        {
            "GK:CleanCatch" => KeeperVisualEvent.CleanCatch,
            "GK:ReflexParry" => KeeperVisualEvent.ReflexParry,
            "GK:CrossClaim" => KeeperVisualEvent.CrossClaim,
            "GK:PunchClear" => KeeperVisualEvent.PunchClear,
            _ => KeeperVisualEvent.None
        };
        if (visualEvent != KeeperVisualEvent.None) entry.Solver.NotifyKeeperEvent(visualEvent);
    }

    /// <summary>
    /// Caches the goal that produced the current <see cref="MatchPhase.GoalScored"/> phase so the
    /// celebration knows who scored for which team. Read from the published rules events (no new plumbing).
    /// </summary>
    private void ReadMatchPhase(MatchSimulationEngine engine)
    {
        MatchPhase phase = engine.Rules.Phase;
        if (phase == MatchPhase.GoalScored && phase != _lastGoalPhase)
        {
            IReadOnlyList<MatchRuleEvent> events = engine.Rules.Events;
            for (int index = events.Count - 1; index >= 0; index--)
            {
                MatchRuleEvent ruleEvent = events[index];
                if (ruleEvent.EventType != "Goal") continue;
                _scoringTeam = ruleEvent.Team;
                _scorerPlayerId = ruleEvent.ScorerPlayerId ?? ruleEvent.PlayerId;
                break;
            }
        }

        _lastGoalPhase = phase;
    }
}
