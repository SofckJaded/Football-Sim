using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.GodotClient.Visuals;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Presentation;
using FootballSim.World.Bridge;
using FootballSim.World.Career;
using FootballSim.World.Simulation;

namespace FootballSim.GodotClient;

public partial class MatchClientNode : Node3D
{
    /// <summary>
    /// Developer escape hatch for the old text debug dump. It is off by default and the HUD overhaul
    /// deliberately hides it: the supported debug path is <see cref="MatchDebugView"/> behind the Match
    /// Setup "Debug mode" option (or F5 in a match). The text methods below are kept for now rather
    /// than deleted, as requested.
    /// </summary>
    [Export] public bool ShowDebugOverlay { get; set; } = false;

    /// <summary>
    /// A2.6: labelled belief/pressure readout next to the player line. Text only — the rendereds
    /// heat map needs a human at the machine and belongs to A9.4.
    /// </summary>
    [Export] public bool ShowSpatialOverlay { get; set; } = true;

    /// <summary>Camera height; the inspector range matches <see cref="BroadcastCamera"/>'s clamp.</summary>
    [Export(PropertyHint.Range, "18,65,0.5")] public float CameraZoomHeight { get; set; } = 34.0f;

    /// <summary>
    /// Presentation-only dial for the articulated model dynamics. 1.0 is the tuned grounded-athletic default;
    /// higher exaggerates squash/stretch for readability from the broadcast camera, lower makes it subtler.
    /// It changes nothing in the simulation.
    /// </summary>
    [Export(PropertyHint.Range, "0,2.5,0.05")] public float MotionIntensity { get; set; } = 1.0f;

    private MatchSimulationEngine _engine = null!;
    private Node3D _ballNode = null!;
    /// <summary>Handle to the procedural pitch surface; drives the analytic ball contact shadow.</summary>
    private PitchSurfaceHandle _pitchSurface = null!;
    /// <summary>Owns every articulated player rig and its presentation-only pose solver.</summary>
    private PlayerVisualManager _visuals = null!;
    private Camera3D _camera = null!;
    private Label _legacyHudLabel = null!;
    private MatchHud _hud = null!;
    private MatchDebugView _debugView = null!;
    private bool _debugMode;

    // Debug-only review controls (F1 slow / F2 pause / F3 fast). They change how many fixed ticks run per
    // rendered second — never what the simulation does — so the same tick sequence is replayed either way.
    private const float DebugTimeScaleStep = 0.25f;
    private const float MinDebugTimeScale = 0.25f;
    private const float MaxDebugTimeScale = 2f;
    private float _debugTimeScale = 1f;
    private bool _debugPaused;
    private readonly HumanControlReader _controls = new();
    private MeshInstance3D _humanAimArrow = null!;
    private int _inspectedPlayerId = 9;
    private int? _humanPlayerId;
    private float _halfDurationSeconds = MatchSetupOptions.DefaultHalfDurationSeconds;
    private float _ballRadius = BallGeometry.DefaultRadius;

    // ---------------------------------------------------------------------------------------------
    // W7 career match state. All of it is null/inert for a quick match, which keeps that path identical.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The career match being played, or null for a quick match.</summary>
    private WorldMatchSetup? _careerMatch;

    /// <summary>The career that owns the match.</summary>
    private CareerSession? _careerSession;

    /// <summary>True once the engine reached full time and the result was committed to the career.</summary>
    private bool _careerCommitted;

    /// <summary>The career pause overlay (Esc), or null while not paused.</summary>
    private Control? _careerPauseOverlay;

    /// <summary>True while the pause overlay is up; the match clock does not advance.</summary>
    private bool _careerPaused;

    /// <summary>True while "sim to full time" is fast-forwarding the engine tick-by-tick.</summary>
    private bool _careerFastForward;

    /// <summary>The career player's condition before kick-off (for the post-match fatigue delta).</summary>
    private float _careerConditionBefore = 1f;

    /// <summary>The HUD canvas layer, so career overlays sit above the in-match HUD.</summary>
    private CanvasLayer _hudCanvas = null!;

    /// <summary>True once the career result has been committed (dev boot-check accessor).</summary>
    public bool CareerMatchCommittedForBootCheck => _careerCommitted;

    /// <summary>True when this scene is playing a career match (dev boot-check accessor).</summary>
    public bool IsCareerMatchForBootCheck => _careerMatch is not null;

    // Audit F22: bounded, diagnosed fixed-step clock instead of an unbounded raw accumulator.
    private readonly FixedStepClock _clock = new();
    private float _mouseAimActiveSeconds;

    public override void _Ready()
    {
        // Idempotent: a direct scene launch skips the menu, so the scene itself has to make sure the
        // persisted render settings (pitch detail tier) are loaded before the pitch is built.
        GameSettings.Load();

        // Dev-only scene smoke check (W7 gate): --boot-quick-match / --boot-career-match configure the scene
        // before it builds. With no such user argument this is a no-op.
        if (GameSession.ApplyDevBootArgs(out string devBootError))
        {
            if (devBootError.Length > 0)
            {
                GD.PrintErr($"MATCH_BOOT: dev boot setup failed {devBootError}");
                Callable.From(() => GetTree().Quit(1)).CallDeferred();
                return;
            }
        }

        _careerMatch = GameSession.PendingCareerMatch;
        if (_careerMatch is not null)
        {
            _careerSession = GameSession.ActiveCareer;
            if (_careerSession is null)
            {
                // A career match without its career is a broken handoff; fall back to the entry screen.
                GD.PushError("Career match started without an active career; returning to the career entry screen.");
                Callable.From(() => GetTree().ChangeSceneToFile(GameSession.CareerEntryScene)).CallDeferred();
                return;
            }

            _humanPlayerId = _careerMatch.HumanEnginePlayerId;
            _halfDurationSeconds = _careerMatch.Config.HalfDurationSeconds;
            _debugMode = false;
            _engine = WorldMatchBridge.CreateEngine(_careerMatch);
            _careerConditionBefore = _careerSession.HumanPlayer.Condition01;
        }
        else
        {
            MatchSetupOptions setup = GameSession.PendingMatch;
            _humanPlayerId = setup.HumanPlayerId;
            _halfDurationSeconds = setup.HalfDurationSeconds;
            _debugMode = setup.DebugMode;

            var config = new PreMatchConfig
            {
                Seed = 20260818UL,
                PlayersPerTeam = 11,
                HalfDurationSeconds = setup.HalfDurationSeconds,
                DesignatedHumanPlayerId = setup.HumanPlayerId,
                HomeProfile = TeamTacticalProfile.PossessionControl,
                AwayProfile = TeamTacticalProfile.DirectCounterattack
            };

            _engine = new MatchSimulationEngine(config);
        }

        // Audit F22: never index the squad by player id — a short-handed or spectated match has fewer
        // players than the default striker's shirt number.
        _inspectedPlayerId = MatchPositions.ResolveInspectedPlayerId(_humanPlayerId, _engine.Players.Count);
        _ballRadius = _engine.BallRadius;

        BuildLightingAndPitchVisuals();
        BuildPlayerAndBallVisuals();
        BuildCameraAndHud();
        UpdateDebugStatus();

        if (GameSession.DevBootQuickMatch)
        {
            GD.Print("MATCH_BOOT: quick scene ready");
            SceneTree tree = GetTree();
            tree.CreateTimer(1.0).Timeout += () =>
            {
                GD.Print("MATCH_BOOT: quick scene done");
                tree.Quit();
            };
        }
        else if (_careerMatch is not null && GameSession.DevBootCareerMatch)
        {
            GD.Print(
                $"MATCH_BOOT: career scene ready clubs={_careerMatch.HomeClubName} v " +
                $"{_careerMatch.AwayClubName} humanEngineId={_careerMatch.HumanEnginePlayerId}");
        }
    }

    public override void _Process(double delta)
    {
        float frameDelta = (float)delta;

        if (Godot.Input.IsPhysicalKeyPressed(Key.Equal))
            CameraZoomHeight = Mathf.Clamp(CameraZoomHeight - 15f * frameDelta,
                BroadcastCamera.MinZoomHeight, BroadcastCamera.MaxZoomHeight);
        if (Godot.Input.IsPhysicalKeyPressed(Key.Minus))
            CameraZoomHeight = Mathf.Clamp(CameraZoomHeight + 15f * frameDelta,
                BroadcastCamera.MinZoomHeight, BroadcastCamera.MaxZoomHeight);

        // Cursor aiming (audit F16): active for a couple of seconds after the mouse is used, then
        // keyboard/stick aim takes over again, so an idle cursor cannot hijack movement aim.
        bool mouseInUse = MouseAim.HasRecentMotion() ||
                          Godot.Input.IsMouseButtonPressed(MouseButton.Left) ||
                          Godot.Input.IsMouseButtonPressed(MouseButton.Right);
        if (mouseInUse)
            _mouseAimActiveSeconds = 2.0f;
        else
            _mouseAimActiveSeconds = MathF.Max(0f, _mouseAimActiveSeconds - frameDelta);

        System.Numerics.Vector2? mouseAimPoint = _mouseAimActiveSeconds > 0f
            ? MouseAim.TryGetPitchPoint(_camera)
            : null;

        // W7: the career pause overlay freezes the match clock (the engine is simply not stepped). The
        // quick-match path never sets either flag, so it behaves exactly as before.
        if (_careerMatch is not null && _careerPaused)
        {
            UpdatePresentation();
            return;
        }

        _controls.Read(_engine.LockedHumanController, frameDelta, mouseAimPoint);

        double timeScale = _engine.IsSpectatorFastForwardActive ? 4.0 : 1.0;
        if (_debugMode)
        {
            // Debug review controls. Pause stops the live tick for inspection; slow/fast only change the
            // presentation cadence, so the simulated tick sequence is exactly the same at every speed.
            timeScale = _debugPaused ? 0.0 : timeScale * _debugTimeScale;
        }

        int steps;
        if (_careerMatch is not null && _careerFastForward)
        {
            // "Sim to full time": step the engine as fast as a frame allows (the same fixed ticks, just
            // delivered in bigger batches) instead of waiting on the render clock.
            steps = 0;
            const int ticksPerFrame = 2_500;
            for (int i = 0; i < ticksPerFrame && _engine.Rules.Phase != MatchPhase.FullTime; i++)
            {
                _engine.StepTick();
                steps++;
            }
        }
        else
        {
            steps = _clock.Advance(delta, (float)timeScale);
            for (int i = 0; i < steps; i++)
                _engine.StepTick();
        }

        // The pose solver advances on simulated time, not wall clock: paused frames pass zero, slow-mo a
        // fraction and 4x spectator fast-forward several fixed ticks, so the pose is always a function of
        // the ticks that actually ran.
        float simulatedDelta = steps * SimConstants.FixedDeltaTime;
        SyncVisualTransforms(_clock.Alpha, frameDelta, simulatedDelta);
        UpdatePresentation();

        if (_careerMatch is not null && !_careerCommitted && _engine.Rules.Phase == MatchPhase.FullTime)
        {
            CommitCareerMatch();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            if (keyEvent.Keycode == Key.Escape)
            {
                // W7: a career match opens its own pause menu; Esc never drops the career state on the floor.
                if (_careerMatch is not null)
                {
                    if (_careerCommitted) FinishCareerMatch();
                    else ToggleCareerPause();
                    GetViewport().SetInputAsHandled();
                    return;
                }

                GetTree().ChangeSceneToFile(GameSession.MainMenuScene);
                GetViewport().SetInputAsHandled();
                return;
            }

            // The Match Setup option persists; F5 and F4 are the in-match convenience switches, and while the
            // overlay is on F1/F2/F3 drive the debug-only time controls.
            if (HandleDebugShortcutKey(keyEvent.Keycode))
            {
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (@event is InputEventMouseButton mouseButton && mouseButton.Pressed)
        {
            if (mouseButton.ButtonIndex == MouseButton.WheelUp)
                CameraZoomHeight = Mathf.Clamp(CameraZoomHeight - 2.5f,
                    BroadcastCamera.MinZoomHeight, BroadcastCamera.MaxZoomHeight);
            else if (mouseButton.ButtonIndex == MouseButton.WheelDown)
                CameraZoomHeight = Mathf.Clamp(CameraZoomHeight + 2.5f,
                    BroadcastCamera.MinZoomHeight, BroadcastCamera.MaxZoomHeight);
        }
    }

    /// <summary>
    /// Handles the debug-only shortcut keys. F5 toggles the on-pitch overlay and F4 switches the inspected
    /// team (both convenience, nothing is saved). While the overlay is on, F1 slows the live tick by 0.25,
    /// F2 pauses/unpauses it and F3 speeds it up by 0.25 (0.25×–2×). The controls only change the presentation
    /// cadence: the same fixed ticks are simulated, so pausing and resuming is deterministic and does not
    /// touch the half clock, stamina or any timer.
    /// </summary>
    private bool HandleDebugShortcutKey(Key keycode)
    {
        if (keycode == Key.F5)
        {
            _debugMode = !_debugMode;
            _debugView.SetEnabled(_debugMode);
            if (!_debugMode)
            {
                _debugPaused = false;
                _debugTimeScale = 1f;
            }

            UpdateDebugStatus();
            return true;
        }

        if (!_debugMode) return false;

        switch (keycode)
        {
            case Key.F1:
                _debugPaused = false;
                _debugTimeScale = Mathf.Max(MinDebugTimeScale, _debugTimeScale - DebugTimeScaleStep);
                break;
            case Key.F2:
                _debugPaused = !_debugPaused;
                break;
            case Key.F3:
                _debugPaused = false;
                _debugTimeScale = Mathf.Min(MaxDebugTimeScale, _debugTimeScale + DebugTimeScaleStep);
                break;
            case Key.F4:
                _debugView.ToggleWatchedTeam();
                break;
            default:
                return false;
        }

        UpdateDebugStatus();
        return true;
    }

    /// <summary>
    /// Pushes the debug-only time status to the overlay: nothing at normal speed (no permanent text on the
    /// pitch), the speed multiplier when slowed/sped up, and "PAUSED" while frozen.
    /// </summary>
    private void UpdateDebugStatus()
    {
        if (!_debugMode)
        {
            _debugView.SetStatus(string.Empty);
            return;
        }

        string speed = _debugTimeScale.ToString("0.##");
        string status = _debugPaused
            ? $"PAUSED · {speed}× · F2 resumes"
            : Mathf.Abs(_debugTimeScale - 1f) > 0.001f
                ? $"{speed}× SPEED · F1 SLOW / F3 FAST"
                : string.Empty;
        _debugView.SetStatus(status);
    }

    private void SyncVisualTransforms(float alpha, float deltaSeconds, float simulatedDelta)
    {
        var bPos = System.Numerics.Vector3.Lerp(_engine.Ball.PreviousPosition, _engine.Ball.Position, alpha);
        _ballNode.Position = new Vector3(bPos.X, bPos.Y, bPos.Z);
        // Ball contact shadow is analytic and lives in the pitch shader (one grounding pass, no extra
        // draw call, no floating disc): the ball's height above y = 0 drives size and opacity.
        _pitchSurface.SetBallShadow(new Vector3(bPos.X, bPos.Y, bPos.Z), _ballRadius);

        // Players: the manager reads the authoritative state, steps every presentation spring on the
        // simulated clock and applies the articulated pose. It never writes simulation state.
        _visuals.Settings.MotionIntensity = MotionIntensity;
        _visuals.UpdateMatch(_engine, alpha, simulatedDelta);

        // Aim arrow follows the resolved aim direction from the human controller.
        HumanInputController? humanController = _engine.LockedHumanController;
        if (humanController is { IsSpectatorMode: false } && _humanPlayerId.HasValue)
        {
            FootballerState? humanPlayer = _engine.Players.FirstOrDefault(p => p.Id == humanController.LockedPlayerId);
            if (humanPlayer is not null)
            {
                System.Numerics.Vector2 aim = humanController.LastResolvedAimDirectionXZ;
                _humanAimArrow.Visible = humanPlayer.IsActiveOnPitch;
                _humanAimArrow.Position = new Vector3(
                    humanPlayer.Position.X + aim.X * 0.85f,
                    0.04f,
                    humanPlayer.Position.Z + aim.Y * 0.85f);
                _humanAimArrow.Rotation = new Vector3(0f, MathF.Atan2(-aim.Y, aim.X), 0f);
            }
        }
        else
        {
            _humanAimArrow.Visible = false;
        }

        BroadcastCamera.Follow(_camera, bPos, CameraZoomHeight, deltaSeconds);
    }

    /// <summary>
    /// Pushes the frame's presentation state: the in-match HUD and — when debug mode is on — the
    /// on-pitch AI debug overlay. The old text dump is only refreshed through its developer export
    /// (off by default), and is hidden from the player otherwise.
    /// </summary>
    private void UpdatePresentation()
    {
        FootballerState active = ResolveActivePlayer();
        FootballerState focus = ResolveFocusPlayer(active);
        _inspectedPlayerId = active.Id;

        HumanInputController? human = _engine.LockedHumanController;
        bool humanControlActive = human is { IsSpectatorMode: false };

        _hud.Refresh(
            _engine.Rules,
            active,
            focus,
            human,
            humanControlActive,
            IsBallCarrier(active),
            IsBallCarrier(focus));

        if (_debugMode)
            _debugView.Refresh(_engine, active);

        if (ShowDebugOverlay)
        {
            _legacyHudLabel.Visible = true;
            UpdateHudText();
        }
        else
        {
            _legacyHudLabel.Visible = false;
        }
    }

    private FootballerState ResolveActivePlayer()
    {
        HumanInputController? human = _engine.LockedHumanController;
        if (human is { IsSpectatorMode: false })
        {
            FootballerState? locked = FindPlayer(human.LockedPlayerId);
            if (locked is { IsActiveOnPitch: true }) return locked;
        }

        if (_engine.Possession.ControllingPlayerId is int carrierId &&
            FindPlayer(carrierId) is { IsActiveOnPitch: true } carrier)
        {
            return carrier;
        }

        return NearestPlayer(_engine.Ball.Position, TeamId.Home) ?? _engine.Players[0];
    }

    /// <summary>
    /// The second card is the duel context: the ball carrier if that is somebody other than the active
    /// player, otherwise the nearest opponent. In a spectator match it is the nearest opponent to the
    /// ball carrier, which is what the viewer is watching anyway.
    /// </summary>
    private FootballerState ResolveFocusPlayer(FootballerState active)
    {
        if (_engine.Possession.ControllingPlayerId is int carrierId && carrierId != active.Id &&
            FindPlayer(carrierId) is { IsActiveOnPitch: true } carrier)
        {
            return carrier;
        }

        TeamId otherTeam = active.Team == TeamId.Home ? TeamId.Away : TeamId.Home;
        return NearestPlayer(active.Position, otherTeam) ?? active;
    }

    private FootballerState? FindPlayer(int playerId)
    {
        for (int index = 0; index < _engine.Players.Count; index++)
        {
            FootballerState player = _engine.Players[index];
            if (player.Id == playerId) return player;
        }
        return null;
    }

    private FootballerState? NearestPlayer(System.Numerics.Vector3 point, TeamId team)
    {
        FootballerState? best = null;
        float bestDistance = float.MaxValue;
        foreach (FootballerState player in _engine.Players)
        {
            if (!player.IsActiveOnPitch || player.Team != team) continue;
            float dx = player.Position.X - point.X;
            float dz = player.Position.Z - point.Z;
            float distance = dx * dx + dz * dz;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = player;
        }
        return best;
    }

    private bool IsBallCarrier(FootballerState player) =>
        _engine.Possession.ControllingPlayerId == player.Id;

    /// <summary>
    /// The player shown in the inspector, resolved by id. Audit F22: the HUD indexed
    /// <c>Players[id - 1]</c>, which silently inspected the wrong player whenever the squad was
    /// smaller than the id (5-a-side, a send-off) and threw when it was empty.
    /// </summary>
    private FootballerState InspectedPlayer =>
        _engine.Players.FirstOrDefault(p => p.Id == _inspectedPlayerId) ?? _engine.Players[0];

    private void UpdateHudText()
    {
        if (!ShowDebugOverlay)
        {
            _legacyHudLabel.Text = "";
            return;
        }

        var inspected = InspectedPlayer;
        var ctrl = _engine.GetController(inspected.Id);
        var human = _engine.LockedHumanController;
        int mins = (int)(_engine.Rules.MatchClockSeconds / 60f);
        int secs = (int)(_engine.Rules.MatchClockSeconds % 60f);

        int bars = human != null ? (int)MathF.Round(human.ChargedPower01 * 10f) : 0;
        string powerBar = new string('█', bars) + new string('░', 10 - bars);

        MatchPositionOption position = MatchPositions.Find(_humanPlayerId);
        string mode = _humanPlayerId.HasValue
            ? $"CONTROLLING {position.Label.ToUpperInvariant()} #{_humanPlayerId}"
            : "SPECTATOR — FULL AI SIMULATION";
        string half = $"{_halfDurationSeconds / 60f:F0} min halves";

        string powerLine = human != null
            ? $"POWER BAR: [{powerBar}] {human.ChargedPower01 * 100f:F0}% | High/Air Modifier: {(human.HighModifierHeld ? "ON" : "OFF")}"
            : "POWER BAR: — (spectator)";

        string status = _engine.Rules.Phase == MatchPhase.FullTime
            ? "   ·   FULL TIME — ESC for main menu"
            : string.Empty;

        // Audit F22: a capped catch-up frame is reported instead of being silently swallowed.
        string catchUp = _clock.IsCatchingUp
            ? $"   ·   CATCH-UP CAPPED (frames {_clock.CappedFrames}, dropped {_clock.DroppedSeconds * 1000d:F0} ms)"
            : string.Empty;

        _legacyHudLabel.Text =
            $"[{_engine.Rules.Phase}] {mins:D2}:{secs:D2} | HOME (Blue) {_engine.Rules.HomeScore} - {_engine.Rules.AwayScore} AWAY (Red) | {half}{status}{catchUp}\n" +
            $"{mode}   ·   {powerLine}\n" +
            // Generated from the live bindings (audit F16), so help can never drift from the keys.
            $"Controls: {HumanControlReader.DescribeControls()}\n" +
            $"Player #{inspected.Id} ({inspected.Team} {inspected.Role}): Stamina={inspected.Stamina01 * 100f:F0}% | {ctrl.GetDebugSummary()} | Exec={inspected.LastExecutionDiagnostic}\n" +
            // A4.6: why the inspected AI player chose what it chose — the four value terms, then the
            // candidates its action set refused and why.
            $"{DescribeDecisionTerms(inspected)}" +
            (ShowSpatialOverlay ? DescribeSpatialOverlay(inspected) : string.Empty);
    }

    /// <summary>
    /// A4.6: the term-by-term explanation of the inspected player's last decision, plus the refusals the
    /// action set recorded. Empty for a controller that does not expose candidates (keeper, human, script).
    /// </summary>
    private string DescribeDecisionTerms(FootballerState inspected)
    {
        if (_engine.GetController(inspected.Id) is not TacticalUtilityAiController ai) return string.Empty;

        UtilityCandidate selected = ai.LastSelected;
        var text = new System.Text.StringBuilder();
        text.Append("   chose ").Append(selected.Key.IsNone ? selected.ActionName : selected.Key.Describe());
        if (selected.Terms.IsPresent) text.Append("  [").Append(selected.Terms.Describe()).Append(']');

        int refusals = ai.RejectionCount;
        if (refusals > 0)
        {
            text.Append("   refused: ");
            for (int index = 0; index < refusals; index++)
            {
                CandidateRejection rejection = ai.GetRejection(index);
                if (index > 0) text.Append(", ");
                text.Append('#').Append(rejection.TargetPlayerId).Append(' ').Append(rejection.Reason);
            }
        }

        text.Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// A2.6: the labelled spatial readout — belief age, known opponents, and the pressure/control split at
    /// the ball. It is text rather than a rendered heat map on purpose: a rendered overlay needs a human at
    /// the machine to review, which is A9.4's job. The numbers are the same ones the decisions use.
    /// </summary>
    private string DescribeSpatialOverlay(FootballerState inspected)
    {
        FootballerObservation? observation = _engine.GetLastObservation(inspected.Id);
        TeamBelief belief = _engine.TeamBeliefOf(inspected.Team);
        IFootballerController controller = _engine.GetController(inspected.Id);

        int totalOpponents = 0;
        foreach (FootballerState player in _engine.Players)
            if (player.Team != inspected.Team && player.IsActiveOnPitch && player.Role != TacticalRole.Goalkeeper)
                totalOpponents++;

        string beliefLine = $"\nBELIEF (team, not truth): {belief.Count}/{totalOpponents} opponents known" +
                            $" | oldest {belief.OldestAgeTicks} ticks" +
                            (observation is null ? string.Empty : $" | this look {observation.KnowledgeAgeTicks} ticks");

        // A7.1: what the inspected player's head is doing. The yaw is shown off the body facing because that
        // is what a scan is — a neutral state (yaw 0, attention None) is the pre-A7.1 fixed cone.
        string scanLine = observation is null
            ? "\nSCAN (player belief): no observation yet"
            : $"\nSCAN (player belief): head {observation.Scan.HeadYawDegrees:+0;-0;0}° off facing" +
              $" | attention {observation.Scan.Attention}" +
              (observation.Scan.Attention == ScanAttentionKind.None
                  ? " (neutral)"
                  : observation.Scan.IsScanning ? " (fixed)" : " (turning)");

        // A7.2: what this player hears from his own team. Only calls from his own board can appear here —
        // the delay is already applied by the time the observation is built.
        string callsLine = observation is null
            ? "\nCALLS (team, delayed): no observation yet"
            : $"\nCALLS (team, delayed): {observation.Calls.Describe()}" +
              $" | {_engine.Communication.Calls.VisibleCount(inspected.Team, _engine.CurrentTick)} live on the team board";

        // A7.3: the receiver's plan for the first touch, recorded on the controller's last decision. It
        // is shown as its own line because "which touch, facing where" is the decision A7.3 exists for
        // and the candidate table alone only shows its score.
        string receptionLine = controller is TacticalUtilityAiController aiController
            ? $"\nRECEPTION (first touch): {aiController.LastReceptionPlan.Describe()}"
            : "\nRECEPTION (first touch): not an AI player";

        // A3.1: the team context is several facts, so it is printed as several facts. Collapsing it
        // back to one label is exactly what the old TeamTacticalState did and what this replaced.
        TeamContext context = observation?.TacticalContext ?? _engine.Tactics.ContextFor(inspected.Team);
        string contextLine = $"\nCONTEXT: {context.Phase} | ball {context.Stability}" +
                             $" | claim {(context.ClaimedTeam?.ToString() ?? "none")}" +
                             $" | zone {context.Zone} | transition {context.Transition}" +
                             (context.Restart.IsArmed
                                 ? $" | RESTART {context.Restart.Kind} for {context.Restart.Team}"
                                 : string.Empty) +
                             (context.IsUnderEmergencyDefence ? " | EMERGENCY DEFENCE" : string.Empty);

        // A6.3: the line's shared depth band, with the terms that moved it. A team in possession (or one
        // with no line at all) has no band and says so rather than showing a stale depth.
        AI.DefensiveLineBand band = _engine.TeamJobs.LineBandOf(inspected.Team);
        string bandLine = band.IsValid
            ? $"\nLINE BAND: {band.ProjX:F1} m | range {band.MinProjX:F0}..{band.MaxProjX:F0}" +
              $" | field {band.FieldShiftMeters:+0.0;-0.0} | pressure {band.PasserPressure01:F2}" +
              $" | turnover {band.TurnoverShiftMeters:+0.0;-0.0}" +
              $" | run {(band.HasCredibleRunner ? $"#{band.DeepestRunnerId}" : "none")}" +
              $" | keeper reach {band.KeeperReachMeters:F0} m"
            : "\nLINE BAND: none (in possession or no line)";

        // A6.4: the team's answer to its own turnover, with the four inputs it was compared on. Read from the
        // same object the players' observations carry, so the readout cannot disagree with the decision.
        AI.TurnoverSituation turnover = _engine.TeamJobs.TurnoverOf(inspected.Team);
        string turnoverLine = $"\nTURNOVER: {turnover.Describe()}";

        if (controller is not TacticalUtilityAiController ai || !ai.PressureField.HasBuild)
            return contextLine + bandLine + turnoverLine + scanLine + callsLine + receptionLine + beliefLine;

        PressureCell cell = ai.PressureField.Read(
            new System.Numerics.Vector2(_engine.Ball.Position.X, _engine.Ball.Position.Z), out bool inside);
        string cellLine = inside
            ? $"\nPRESSURE @ball {cell.Pressure01:F2} | mine {cell.InfluenceShare:F2} / theirs {cell.OpponentInfluenceShare:F2} / unclaimed {cell.UnclaimedShare:F2}" +
              $" | arrivals {DescribeArrival(cell.MyArrivalSeconds)} vs {DescribeArrival(cell.OpponentArrivalSeconds)}"
            : "\nPRESSURE @ball — outside this player's local window";

        return contextLine + bandLine + turnoverLine + scanLine + callsLine + receptionLine + beliefLine + cellLine;
        static string DescribeArrival(float seconds) => seconds < 0f ? "out of reach" : $"{seconds:F2}s";
    }

    private void BuildLightingAndPitchVisuals()
    {
        // The Settings page's "Pitch detail" row resolves the surface tier; the tier also owns the
        // anti-aliasing choice (High = SMAA, Medium = FXAA, Low = off) per docs/football-grass.md §5.7.
        PitchQualitySettings quality = PitchQualitySettings.For(GameSettings.PitchDetail);
        _pitchSurface = PitchVisualBuilder.Build(this, _engine.Pitch, quality, -1f, 1f);
        GetViewport().ScreenSpaceAA = quality.ScreenSpaceAA;
    }

    private void BuildPlayerAndBallVisuals()
    {
        // Phase P0: every player is one articulated rig from the shared factory, so the match and free
        // training cannot drift apart. The rig only renders a pose; the solver owns all motion.
        _visuals = new PlayerVisualManager(new PlayerDynamicsSettings { MotionIntensity = MotionIntensity });
        foreach (var p in _engine.Players)
            _visuals.Register(this, p, _humanPlayerId.HasValue && p.Id == _humanPlayerId.Value);

        // Audit F22: one dimension source. The rendered ball is exactly the physical ball, so its
        // apparent size, contact and reach match what the simulation resolves.
        _ballNode = new MeshInstance3D
        {
            Mesh = new SphereMesh
            {
                Radius = BallGeometry.SphereRadius(_ballRadius),
                Height = BallGeometry.Diameter(_ballRadius)
            },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Colors.White, Roughness = 0.3f }
        };
        AddChild(_ballNode);

        // Audit F16: the aim pointer shows the resolved aim direction (movement, or cursor when no
        // gamepad is connected), not the body facing. Draw it as an arrow in front of the human.
        _humanAimArrow = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.35f, 0.05f, 0.18f) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.0f, 0.95f, 1.0f),
                EmissionEnabled = true,
                Emission = new Color(0.0f, 0.85f, 1.0f),
                EmissionEnergyMultiplier = 1.5f
            }
        };
        AddChild(_humanAimArrow);
    }

    private void BuildCameraAndHud()
    {
        _camera = BroadcastCamera.Create(this, CameraZoomHeight);

        var canvas = new CanvasLayer();
        AddChild(canvas);
        _hudCanvas = canvas;

        _hud = new MatchHud();
        if (_careerMatch is not null)
        {
            // W7: the career match shows the real club names on the scoreboard and in the phase banners.
            _hud.SetTeamNames(_careerMatch.HomeClubName, _careerMatch.AwayClubName);
        }

        canvas.AddChild(_hud);

        // Small always-on performance readout; the top-right corner is free of HUD elements.
        FpsCounter.Attach(canvas);

        // Kept (not deleted) for the developer export above; invisible in normal play and in debug mode.
        _legacyHudLabel = new Label
        {
            Position = new Vector2(16f, 16f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(1200f, 0f),
            Visible = false
        };
        canvas.AddChild(_legacyHudLabel);

        _debugView = new MatchDebugView();
        AddChild(_debugView);
        _debugView.SetEnabled(_debugMode);
    }

    // ---------------------------------------------------------------------------------------------
    // W7 career matchday: commit, post-match summary, pause menu and the return to the hub.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Commits the finished career match to the career: the bridge turns the engine output into the living
    /// world's report, the session queues it, and the post-match summary is shown. The queued result is only
    /// applied to the world on <see cref="FinishCareerMatch"/>'s advance, exactly like a quick-simulated match.
    /// </summary>
    private void CommitCareerMatch()
    {
        if (_careerMatch is null || _careerSession is null || _careerCommitted) return;

        _careerFastForward = false;
        HideCareerPause();

        try
        {
            CareerMatchResult result = _careerSession.CommitHumanLiveMatchEngine(_engine, _careerMatch);
            _careerCommitted = true;
            GameSession.LastCareerMatchSummary = result.ToString();

            if (GameSession.DevBootCareerMatch)
            {
                // Dev smoke check: prove the commit applies to the career, then exit (no autosave, so no
                // developer career slots are touched).
                GD.Print(
                    $"MATCH_BOOT: career committed summary={GameSession.LastCareerMatchSummary} " +
                    $"minutes={result.HumanLine?.Minutes.ToString() ?? "-"} " +
                    $"rating={result.HumanLine?.Rating.ToString("0.0") ?? "-"}");
                try
                {
                    _careerSession.AdvanceToNextEventOrMatchday();
                    GD.Print(
                        $"MATCH_BOOT: career applied playerAppearances=" +
                        $"{_careerSession.HumanPlayer.CurrentSeason.Appearances} " +
                        $"canSave={_careerSession.CanSave}");
                }
                catch (InvalidOperationException exception)
                {
                    GD.PrintErr($"MATCH_BOOT: apply failed {exception.Message}");
                    GetTree().Quit(1);
                    return;
                }

                GD.Print("MATCH_BOOT: career done");
                GetTree().Quit();
                return;
            }

            ShowCareerPostMatch(result);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            GD.PushError($"Career match commit failed: {exception.Message}");
            _careerCommitted = true;
            ShowCareerError(exception.Message);
        }
    }

    /// <summary>Shows the career post-match summary overlay.</summary>
    private void ShowCareerPostMatch(CareerMatchResult result)
    {
        VBoxContainer box = CreateCareerOverlay(out _);
        CareerUi.Heading(box, "FULL TIME", 20);
        CareerUi.Paragraph(
            box,
            $"{result.HomeClubName}  {result.HomeGoals} – {result.AwayGoals}  {result.AwayClubName}",
            18, CareerUi.Accent);

        string outcome = result.HumanWon ? "Won" : result.HumanDrew ? "Drew" : "Lost";
        CareerUi.Paragraph(
            box,
            $"{(result.HumanIsHome ? result.HomeClubName : result.AwayClubName)} — {outcome}.",
            14);

        if (result.HumanLine is { } line)
        {
            CareerUi.InfoRow(box, "Match rating", $"{line.Rating:0.0} / 10");
            CareerUi.InfoRow(box, "Minutes", line.Minutes.ToString());
            CareerUi.InfoRow(box, "Goals / assists", $"{line.Goals} / {line.Assists}");
            CareerUi.InfoRow(
                box,
                "Shots / passes",
                $"{line.Shots} shots, {line.PassesCompleted}/{line.PassesAttempted} passes");
            CareerUi.InfoRow(box, "Tackles / saves", $"{line.Tackles} / {line.Saves}");
            CareerUi.InfoRow(
                box,
                "Stamina",
                $"{_careerConditionBefore * 100f:0}% → {line.EndingStamina01 * 100f:0}% " +
                $"(cost {line.ConditionCost01 * 100f:0}%)");
            CareerUi.InfoRow(box, "Manager's reaction", ManagerReaction(result, line));
        }
        else
        {
            CareerUi.Paragraph(
                box,
                "You did not get on the pitch. The manager kept the same eleven to the end.",
                13, CareerUi.WarnColor);
        }

        CareerUi.Spacer(box);
        CareerUi.Button(box, "Continue to Career Hub", FinishCareerMatch, primary: true);
    }

    private static string ManagerReaction(CareerMatchResult result, PlayerMatchLine line)
    {
        string verdict = line.Rating >= 8.0f ? "outstanding"
            : line.Rating >= 7.0f ? "very good"
            : line.Rating >= 6.0f ? "solid"
            : line.Rating >= 5.0f ? "quiet"
            : "below par";
        string resultWord = result.HumanWon ? "the win" : result.HumanDrew ? "the draw" : "the defeat";
        return $"\"A {verdict} personal performance in {resultWord}.\"";
    }

    /// <summary>Shows a commit-failure overlay with a safe way back to the hub.</summary>
    private void ShowCareerError(string message)
    {
        VBoxContainer box = CreateCareerOverlay(out _);
        CareerUi.Heading(box, "MATCH NOT RECORDED", 18);
        CareerUi.Paragraph(box, message, 13, CareerUi.WarnColor);
        CareerUi.Paragraph(
            box,
            "The career is still on this matchday, so you can play or simulate the fixture again from the hub.",
            13, CareerUi.MutedColor);
        CareerUi.Button(box, "Back to Career Hub", ReturnToCareerHubEarly, primary: true);
    }

    /// <summary>Builds a dark, centred overlay panel and returns the VBox its content goes into.</summary>
    private VBoxContainer CreateCareerOverlay(out Control root)
    {
        var overlay = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _hudCanvas.AddChild(overlay);

        var background = new ColorRect { Color = new Color(0f, 0f, 0f, 0.62f) };
        background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        overlay.AddChild(background);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        overlay.AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(660f, 0f) };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.055f, 0.105f, 0.07f, 0.98f),
            BorderColor = new Color(0.18f, 0.43f, 0.26f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1
        };
        style.SetCornerRadiusAll(16);
        panel.AddThemeStyleboxOverride("panel", style);
        center.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 26);
        margin.AddThemeConstantOverride("margin_right", 26);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        panel.AddChild(margin);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        margin.AddChild(box);

        root = overlay;
        return box;
    }

    /// <summary>Esc / pause: show or hide the career pause menu.</summary>
    private void ToggleCareerPause()
    {
        if (_careerPauseOverlay is not null)
        {
            HideCareerPause();
            return;
        }

        VBoxContainer box = CreateCareerOverlay(out Control root);
        _careerPauseOverlay = root;
        _careerPaused = true;

        CareerUi.Heading(box, "PAUSED", 20);
        CareerUi.Paragraph(
            box,
            $"{_careerMatch?.HomeClubName} v {_careerMatch?.AwayClubName}, " +
            $"{_engine.Rules.MatchClockSeconds / 60f:0}' at {_engine.Rules.HomeScore}-{_engine.Rules.AwayScore}.",
            14, CareerUi.MutedColor);
        CareerUi.Button(box, "Resume", HideCareerPause, primary: true);
        CareerUi.Button(box, "Sim to Full Time", () =>
        {
            HideCareerPause();
            _careerFastForward = true;
        });
        CareerUi.Button(box, "Return to Career Hub", ReturnToCareerHubEarly);
    }

    private void HideCareerPause()
    {
        if (_careerPauseOverlay is null) return;

        _careerPauseOverlay.QueueFree();
        _careerPauseOverlay = null;
        _careerPaused = false;
    }

    /// <summary>
    /// Applies the queued career result (the same applier a quick-simmed fixture uses), autosaves and returns
    /// to the hub. Called from the post-match overlay, or from Esc after full time.
    /// </summary>
    private void FinishCareerMatch()
    {
        if (_careerSession is not null)
        {
            if (!_careerCommitted)
            {
                // Leaving before full time: the match is abandoned, not half-applied.
                _careerSession.DiscardLiveMatch();
            }
            else
            {
                try
                {
                    _careerSession.AdvanceToNextEventOrMatchday();
                }
                catch (InvalidOperationException exception)
                {
                    GD.PushError($"Career advance after the match failed: {exception.Message}");
                }
            }

            CareerSaveFiles.TryAutosave(_careerSession, out _);
        }

        GameSession.PendingCareerMatch = null;
        _careerMatch = null;
        GetTree().ChangeSceneToFile(GameSession.CareerHubScene);
    }

    /// <summary>
    /// Leaves a career match without recording a result. The career keeps its live match prepared, so it must
    /// be discarded before the hub takes over, or the career would stay locked (CanSave would be false).
    /// </summary>
    private void ReturnToCareerHubEarly()
    {
        if (_careerSession is not null)
        {
            if (!_careerCommitted) _careerSession.DiscardLiveMatch();
            CareerSaveFiles.TryAutosave(_careerSession, out _);
        }

        GameSession.PendingCareerMatch = null;
        _careerMatch = null;
        GetTree().ChangeSceneToFile(GameSession.CareerHubScene);
    }

    /// <summary>Dev-only hook: continues the career match as if the post-match Continue button was pressed.</summary>
    public void BootCheckContinueToHub() => FinishCareerMatch();
}
