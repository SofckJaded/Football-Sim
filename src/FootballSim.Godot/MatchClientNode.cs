using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using FootballSim;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Physics;

namespace FootballSim.GodotClient;

public partial class MatchClientNode : Node3D
{
    [Export] public bool ShowDebugOverlay { get; set; } = true;
    [Export] public bool ShowTacticalAnchors { get; set; } = false;

    /// <summary>Camera height; the inspector range matches <see cref="BroadcastCamera"/>'s clamp.</summary>
    [Export(PropertyHint.Range, "18,65,0.5")] public float CameraZoomHeight { get; set; } = 34.0f;

    private MatchSimulationEngine _engine = null!;
    private Node3D _ballNode = null!;
    private MeshInstance3D _ballShadowNode = null!;
    private readonly Dictionary<int, Node3D> _playerNodes = new();
    private readonly Dictionary<int, MeshInstance3D> _anchorNodes = new();
    private Camera3D _camera = null!;
    private Label _hudLabel = null!;
    private readonly HumanControlReader _controls = new();
    private MeshInstance3D _humanAimArrow = null!;
    private int _inspectedPlayerId = 9;
    private int? _humanPlayerId;
    private float _halfDurationSeconds = MatchSetupOptions.DefaultHalfDurationSeconds;
    private float _ballRadius = BallGeometry.DefaultRadius;

    // Audit F22: bounded, diagnosed fixed-step clock instead of an unbounded raw accumulator.
    private readonly FixedStepClock _clock = new();
    private float _mouseAimActiveSeconds;

    public override void _Ready()
    {
        MatchSetupOptions setup = GameSession.PendingMatch;
        _humanPlayerId = setup.HumanPlayerId;
        _halfDurationSeconds = setup.HalfDurationSeconds;

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
        // Audit F22: never index the squad by player id — a short-handed or spectated match has fewer
        // players than the default striker's shirt number.
        _inspectedPlayerId = MatchPositions.ResolveInspectedPlayerId(setup.HumanPlayerId, _engine.Players.Count);
        _ballRadius = _engine.BallRadius;

        BuildLightingAndPitchVisuals();
        BuildPlayerAndBallVisuals();
        BuildCameraAndHud();
    }

    public override void _Process(double delta)
    {
        float frameDelta = (float)delta;

        if (Godot.Input.IsPhysicalKeyPressed(Key.Tab) || (Godot.Input.GetConnectedJoypads().Count > 0 && Godot.Input.IsJoyButtonPressed(0, JoyButton.Back)))
            ShowTacticalAnchors = true;
        else
            ShowTacticalAnchors = false;

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

        _controls.Read(_engine.LockedHumanController, frameDelta, mouseAimPoint);

        double timeScale = _engine.IsSpectatorFastForwardActive ? 4.0 : 1.0;
        int steps = _clock.Advance(delta, (float)timeScale);
        for (int i = 0; i < steps; i++)
            _engine.StepTick();

        SyncVisualTransforms(_clock.Alpha, frameDelta);
        UpdateHudText();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo &&
            keyEvent.Keycode == Key.Escape)
        {
            GetTree().ChangeSceneToFile(GameSession.MainMenuScene);
            GetViewport().SetInputAsHandled();
            return;
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

    private void SyncVisualTransforms(float alpha, float deltaSeconds)
    {
        var bPos = System.Numerics.Vector3.Lerp(_engine.Ball.PreviousPosition, _engine.Ball.Position, alpha);
        _ballNode.Position = new Vector3(bPos.X, bPos.Y, bPos.Z);
        _ballShadowNode.Position = new Vector3(bPos.X, 0.02f, bPos.Z);
        float shadowScale = Mathf.Clamp(1.0f - bPos.Y * 0.08f, 0.35f, 1.0f);
        _ballShadowNode.Scale = new Vector3(shadowScale, 1.0f, shadowScale);

        foreach (var p in _engine.Players)
        {
            if (!_playerNodes.TryGetValue(p.Id, out var node)) continue;
            node.Visible = p.IsActiveOnPitch;
            var interp = System.Numerics.Vector3.Lerp(p.PreviousPosition, p.Position, alpha);
            node.Position = new Vector3(interp.X, interp.Y, interp.Z);
            float yaw = MathF.Atan2(-p.FacingXZ.Y, p.FacingXZ.X);
            node.Rotation = new Vector3(0f, yaw, 0f);

            if (_anchorNodes.TryGetValue(p.Id, out var anchorNode))
            {
                anchorNode.Visible = ShowTacticalAnchors && p.IsActiveOnPitch;
                anchorNode.Position = new Vector3(p.TacticalAnchor.X, 0.03f, p.TacticalAnchor.Z);
            }
        }

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
            _hudLabel.Text = "";
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

        _hudLabel.Text =
            $"[{_engine.Rules.Phase}] {mins:D2}:{secs:D2} | HOME (Blue) {_engine.Rules.HomeScore} - {_engine.Rules.AwayScore} AWAY (Red) | {half}{status}{catchUp}\n" +
            $"{mode}   ·   {powerLine}\n" +
            // Generated from the live bindings (audit F16), so help can never drift from the keys.
            $"Controls: {HumanControlReader.DescribeControls()}\n" +
            $"Player #{inspected.Id} ({inspected.Team} {inspected.Role}): Stamina={inspected.Stamina01 * 100f:F0}% | {ctrl.GetDebugSummary()} | Exec={inspected.LastExecutionDiagnostic}";
    }

    private void BuildLightingAndPitchVisuals()
    {
        PitchVisualBuilder.Build(this, _engine.Pitch, -1f, 1f);
    }

    private void BuildPlayerAndBallVisuals()
    {
        var homeJerseyMat = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.38f, 0.90f) };
        var homeGkMat = new StandardMaterial3D { AlbedoColor = new Color(0.15f, 0.72f, 0.35f) };
        var awayJerseyMat = new StandardMaterial3D { AlbedoColor = new Color(0.88f, 0.16f, 0.16f) };
        var awayGkMat = new StandardMaterial3D { AlbedoColor = new Color(0.92f, 0.55f, 0.12f) };
        var shortsWhiteMat = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.95f, 0.95f) };
        var shortsDarkMat = new StandardMaterial3D { AlbedoColor = new Color(0.14f, 0.15f, 0.18f) };
        var skinMat = new StandardMaterial3D { AlbedoColor = new Color(0.88f, 0.72f, 0.58f) };
        var humanRingMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.0f, 0.95f, 1.0f),
            EmissionEnabled = true,
            Emission = new Color(0.0f, 0.85f, 1.0f),
            EmissionEnergyMultiplier = 1.5f
        };

        foreach (var p in _engine.Players)
        {
            var root = new Node3D();

            Material jerseyMat = p.Team == TeamId.Home
                ? (p.Role == TacticalRole.Goalkeeper ? homeGkMat : homeJerseyMat)
                : (p.Role == TacticalRole.Goalkeeper ? awayGkMat : awayJerseyMat);

            var shorts = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.28f, BottomRadius = 0.22f, Height = 0.72f },
                Position = new Vector3(0f, 0.36f, 0f),
                MaterialOverride = p.Team == TeamId.Home ? shortsWhiteMat : shortsDarkMat
            };
            root.AddChild(shorts);

            var torso = new MeshInstance3D
            {
                Mesh = new CapsuleMesh { Radius = 0.32f, Height = 0.92f },
                Position = new Vector3(0f, 1.05f, 0f),
                MaterialOverride = jerseyMat
            };
            root.AddChild(torso);

            var head = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.20f, Height = 0.40f },
                Position = new Vector3(0f, 1.68f, 0f),
                MaterialOverride = skinMat
            };
            root.AddChild(head);

            var dirPointer = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.45f, 0.04f, 0.14f) },
                Position = new Vector3(0.42f, 0.04f, 0f),
                MaterialOverride = shortsWhiteMat
            };
            root.AddChild(dirPointer);

            if (_humanPlayerId.HasValue && p.Id == _humanPlayerId.Value)
            {
                var ring = new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0.68f, BottomRadius = 0.68f, Height = 0.03f },
                    Position = new Vector3(0f, 0.02f, 0f),
                    MaterialOverride = humanRingMat
                };
                root.AddChild(ring);
            }

            AddChild(root);
            _playerNodes[p.Id] = root;

            var anchor = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.22f, BottomRadius = 0.22f, Height = 0.02f },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = p.Team == TeamId.Home ? new Color(0.3f, 0.6f, 1f, 0.5f) : new Color(1f, 0.4f, 0.4f, 0.5f)
                }
            };
            AddChild(anchor);
            _anchorNodes[p.Id] = anchor;
        }

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

        _ballShadowNode = new MeshInstance3D
        {
            Mesh = new CylinderMesh
            {
                TopRadius = BallGeometry.ShadowRadius(_ballRadius),
                BottomRadius = BallGeometry.ShadowRadius(_ballRadius),
                Height = 0.01f
            },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0f, 0f, 0f, 0.55f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha
            }
        };
        AddChild(_ballShadowNode);

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
        _hudLabel = new Label
        {
            Position = new Vector2(16f, 16f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(1200f, 0f)
        };
        canvas.AddChild(_hudLabel);
        AddChild(canvas);
    }
}
