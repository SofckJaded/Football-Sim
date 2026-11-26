using System;
using System.Collections.Generic;
using Godot;
using FootballSim.Core;
using FootballSim.GodotClient.Visuals;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Presentation;
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

    /// <summary>
    /// Presentation-only dial for the articulated model dynamics (1.0 = tuned default). Training exposes the same
    /// dial as the match so a drill can be reviewed at a different motion weight without touching the match scene.
    /// </summary>
    [Export(PropertyHint.Range, "0,2.5,0.05")] public float MotionIntensity { get; set; } = 1.0f;

    private TrainingSession _session = null!;
    /// <summary>Owns every articulated player rig and its presentation-only pose solver (shared with the match).</summary>
    private PlayerVisualManager _visuals = null!;
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

        SyncVisuals(1f, 0f, 0f);
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

        // Simulated time, not wall clock: this freezes cleanly if no tick ran and advances exactly with
        // the ticks that did (same contract as the match's debug pause / fast-forward).
        float simulatedDelta = steps * SimConstants.FixedDeltaTime;
        SyncVisuals(_clock.Alpha, frameDelta, simulatedDelta);
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
        // Phase P0: the same articulated rig factory as the match, so training and match visuals can never
        // drift apart. Rig construction is presentation-only; the solver owns all motion.
        _visuals = new PlayerVisualManager(new PlayerDynamicsSettings { MotionIntensity = MotionIntensity });
        foreach (FootballerState player in _session.Players)
            _visuals.Register(this, player, player.Id == TrainingSession.HumanPlayerId);

        // Aim pointer shows the resolved aim direction (movement, or cursor without a gamepad), not
        // the body facing (audit F16).
        var aimPivot = new Node3D();
        var aimRingMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.0f, 0.95f, 1.0f),
            EmissionEnabled = true,
            Emission = new Color(0.0f, 0.85f, 1.0f),
            EmissionEnergyMultiplier = 1.5f
        };
        var aimBar = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.35f, 0.05f, 0.18f) },
            MaterialOverride = aimRingMat
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

    private void SyncVisuals(float alpha, float deltaSeconds, float simulatedDelta)
    {
        System.Numerics.Vector3 ballPos = System.Numerics.Vector3.Lerp(
            _session.Ball.PreviousPosition, _session.Ball.Position, alpha);
        _ballNode.Position = new Vector3(ballPos.X, ballPos.Y, ballPos.Z);
        _ballShadow.Position = new Vector3(ballPos.X, 0.02f, ballPos.Z);
        float shadowScale = Mathf.Clamp(1.0f - ballPos.Y * 0.08f, 0.35f, 1.0f);
        _ballShadow.Scale = new Vector3(shadowScale, 1.0f, shadowScale);

        // Players: the shared manager steps every presentation spring on simulated time and applies the
        // articulated pose. It is a consumer of the session, never a participant.
        _visuals.Settings.MotionIntensity = MotionIntensity;
        _visuals.UpdateTraining(_session, alpha, simulatedDelta);

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
