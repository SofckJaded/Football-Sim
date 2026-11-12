using System;
using System.Collections.Generic;
using Godot;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Training;

namespace FootballSim.GodotClient;

/// <summary>
/// Renders and drives one <see cref="TrainingSession"/>. Uses the same full pitch, goals and
/// broadcast camera as the match, and draws every participant the drill supplies (trainee,
/// AI teammate, goalkeeper, ...).
/// </summary>
public partial class FreeTrainingNode : Node3D
{
    [Export(PropertyHint.Range, "18,65,0.5")] public float CameraZoomHeight { get; set; } = BroadcastCamera.DefaultZoomHeight;

    private TrainingSession _session = null!;
    private readonly Dictionary<int, Node3D> _playerNodes = new();
    private Node3D _ballNode = null!;
    private MeshInstance3D _ballShadow = null!;
    private Camera3D _camera = null!;
    private Node3D? _humanAimArrow;
    private readonly HumanControlReader _controls = new();
    private float _ballRadius = BallGeometry.DefaultRadius;

    // Audit F22: bounded, diagnosed fixed-step clock instead of an unbounded raw accumulator.
    private readonly FixedStepClock _clock = new();
    private float _mouseAimActiveSeconds;

    public override void _Ready()
    {
        TrainingDrill drill = TrainingDrillRegistry.Find(GameSession.PendingDrillId)
                              ?? TrainingDrillRegistry.Default;
        _session = new TrainingSession(drill);
        _ballRadius = _session.BallRadius;

        // Practice uses exactly the same pitch map as a match (both goals and all markings).
        PitchVisualBuilder.Build(this, _session.Pitch, -1f, 1f);
        BuildPlayerAndBallVisuals();
        BuildCamera();

        SyncVisuals(1f, 0f);
        // Start framed on the trainee instead of gliding in from the centre circle.
        BroadcastCamera.SnapTo(
            _camera,
            new Vector3(_session.Player.Position.X, 0f, _session.Player.Position.Z),
            CameraZoomHeight);
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
        _controls.Read(_session.Human, frameDelta, mouseAimPoint);

        int steps = _clock.Advance(delta);
        for (int i = 0; i < steps; i++)
            _session.Step(SimConstants.FixedDeltaTime);

        SyncVisuals(_clock.Alpha, frameDelta);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.Escape)
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

        foreach (FootballerState player in _session.Players)
        {
            var root = new Node3D();

            Material jerseyMat = player.Team == TeamId.Home
                ? (player.Role == TacticalRole.Goalkeeper ? homeGkMat : homeJerseyMat)
                : (player.Role == TacticalRole.Goalkeeper ? awayGkMat : awayJerseyMat);

            root.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.28f, BottomRadius = 0.22f, Height = 0.72f },
                Position = new Vector3(0f, 0.36f, 0f),
                MaterialOverride = player.Team == TeamId.Home ? shortsWhiteMat : shortsDarkMat
            });

            root.AddChild(new MeshInstance3D
            {
                Mesh = new CapsuleMesh { Radius = 0.32f, Height = 0.92f },
                Position = new Vector3(0f, 1.05f, 0f),
                MaterialOverride = jerseyMat
            });

            root.AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.20f, Height = 0.40f },
                Position = new Vector3(0f, 1.68f, 0f),
                MaterialOverride = skinMat
            });

            root.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.45f, 0.04f, 0.14f) },
                Position = new Vector3(0.42f, 0.04f, 0f),
                MaterialOverride = shortsWhiteMat
            });

            if (player.Id == TrainingSession.HumanPlayerId)
            {
                root.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0.68f, BottomRadius = 0.68f, Height = 0.03f },
                    Position = new Vector3(0f, 0.02f, 0f),
                    MaterialOverride = humanRingMat
                });
            }

            AddChild(root);
            _playerNodes[player.Id] = root;
        }

        // Aim pointer shows the resolved aim direction (movement, or cursor without a gamepad), not
        // the body facing (audit F16).
        var aimPivot = new Node3D();
        var aimBar = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.35f, 0.05f, 0.18f) },
            MaterialOverride = humanRingMat
        };
        aimPivot.AddChild(aimBar);
        aimBar.Position = new Vector3(0.85f, 0.02f, 0f);
        AddChild(aimPivot);
        _humanAimArrow = aimPivot;

        // Audit F22: the same one dimension source as the match — the rendered ball is the physical ball.
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

        _ballShadow = new MeshInstance3D
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
        AddChild(_ballShadow);
    }

    private void BuildCamera()
    {
        _camera = BroadcastCamera.Create(this, CameraZoomHeight);
        _camera.MakeCurrent();
    }

    private void SyncVisuals(float alpha, float deltaSeconds)
    {
        System.Numerics.Vector3 ballPos = System.Numerics.Vector3.Lerp(
            _session.Ball.PreviousPosition, _session.Ball.Position, alpha);
        _ballNode.Position = new Vector3(ballPos.X, ballPos.Y, ballPos.Z);
        _ballShadow.Position = new Vector3(ballPos.X, 0.02f, ballPos.Z);
        float shadowScale = Mathf.Clamp(1.0f - ballPos.Y * 0.08f, 0.35f, 1.0f);
        _ballShadow.Scale = new Vector3(shadowScale, 1.0f, shadowScale);

        foreach (FootballerState player in _session.Players)
        {
            if (!_playerNodes.TryGetValue(player.Id, out Node3D? node)) continue;

            node.Visible = player.IsActiveOnPitch;
            System.Numerics.Vector3 position = System.Numerics.Vector3.Lerp(
                player.PreviousPosition, player.Position, alpha);
            node.Position = new Vector3(position.X, position.Y, position.Z);

            float yaw = MathF.Atan2(-player.FacingXZ.Y, player.FacingXZ.X);
            node.Rotation = new Vector3(0f, yaw, 0f);
        }

        // Practice centres the trainee; only the goal in front of them leaves the frame as they run.
        System.Numerics.Vector3 human = System.Numerics.Vector3.Lerp(
            _session.Player.PreviousPosition, _session.Player.Position, alpha);
        BroadcastCamera.FollowCentered(_camera, new Vector3(human.X, 0f, human.Z), CameraZoomHeight, deltaSeconds);

        if (_humanAimArrow is not null)
        {
            System.Numerics.Vector2 aim = _session.Human.LastResolvedAimDirectionXZ;
            _humanAimArrow.Visible = _session.Player.IsActiveOnPitch;
            _humanAimArrow.Position = new Vector3(human.X, 0.04f, human.Z);
            _humanAimArrow.Rotation = new Vector3(0f, MathF.Atan2(-aim.Y, aim.X), 0f);
        }
    }
}
