using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Rules;

namespace FootballSim.Presentation;

/// <summary>Which keeper touch the presentation layer should react to (resolved from the execution diagnostic).</summary>
public enum KeeperVisualEvent
{
    None = 0,
    CleanCatch,
    ReflexParry,
    CrossClaim,
    PunchClear
}

/// <summary>
/// Per-player presentation dynamics: a small bank of second-order damped springs driven from
/// authoritative simulation state, producing one <see cref="PlayerPose"/> per rendered frame.
///
/// <b>Boundaries.</b> This type lives in a zero-Godot project and only ever *reads* the simulation. It owns
/// no simulation state, consumes no simulation randomness and feeds nothing back — reach, contact and
/// decisions are decided before it runs and cannot see it.
///
/// <b>Pipeline per frame.</b>
/// <list type="number">
/// <item><description>teleport guard (snap + zero velocities after a reset),</description></item>
/// <item><description>shared derived state (local velocity/acceleration, smoothed facing, timers),</description></item>
/// <item><description><see cref="AccumulateLocomotion"/>: idle / run / brake / cut / jockey / stride / shield,</description></item>
/// <item><description><see cref="ApplyActions"/>: jump, header, kick impulses, tackle, stumble, collision,</description></item>
/// <item><description><see cref="ApplyGoalkeeperDynamics"/>: stance, dive arc, parry, claim, block,</description></item>
/// <item><description><see cref="AccumulateUpperBody"/>: head scan/gaze, stabilisation, stoppage reactions,</description></item>
/// <item><description>step every spring and compose the pose.</description></item>
/// </list>
///
/// The stages are `partial` members implemented in sibling files so a stage can be tuned or replaced
/// without touching the pipeline. They only write into <see cref="ChannelTargets"/> or inject spring
/// impulses; the spring bank and its tuning stay in this file.
/// </summary>
public sealed partial class PlayerDynamicsSolver
{
    /// <summary>Omega / zeta pair for one spring channel.</summary>
    private readonly record struct SpringTuning(float Omega, float Zeta);

    // --- Body / torso springs ----------------------------------------------------------------
    private static readonly SpringTuning LeanPitchTuning = new(8.0f, 0.78f);
    private static readonly SpringTuning LeanRollTuning = new(9.5f, 0.72f);
    private static readonly SpringTuning TorsoPitchTuning = new(11.0f, 0.72f);
    private static readonly SpringTuning TorsoYawTuning = new(13.0f, 0.62f);
    private static readonly SpringTuning TorsoRollTuning = new(11.0f, 0.55f);
    private static readonly SpringTuning SquashTuning = new(16.0f, 0.62f);
    private static readonly SpringTuning BodyOffsetYTuning = new(14.0f, 0.72f);
    private static readonly SpringTuning BodyOffsetXZTuning = new(13.0f, 0.75f);

    // --- Head springs ------------------------------------------------------------------------
    private static readonly SpringTuning HeadYawTuning = new(18.0f, 0.95f);
    private static readonly SpringTuning HeadPitchTuning = new(16.0f, 0.90f);
    private static readonly SpringTuning HeadRollTuning = new(16.0f, 0.90f);

    // --- Dive springs ------------------------------------------------------------------------
    private static readonly SpringTuning DiveRollTuning = new(10.0f, 0.85f);
    private static readonly SpringTuning DivePitchTuning = new(10.0f, 0.85f);

    // --- Foot / hand springs -----------------------------------------------------------------
    private static readonly SpringTuning KickFootTuning = new(18.0f, 0.50f);
    private static readonly SpringTuning KickLiftTuning = new(18.0f, 0.60f);
    private static readonly SpringTuning HandReachTuning = new(10.0f, 0.85f);

    /// <summary>Yaw spring that removes one-tick facing pops at kick execution (~70 ms settle).</summary>
    private static readonly SpringTuning RootYawTuning = new(22.0f, 1.0f);

    // --- Configuration -----------------------------------------------------------------------
    private readonly PlayerDynamicsSettings _settings;

    // --- Spring bank -------------------------------------------------------------------------
    private Spring1D _leanPitch;
    private Spring1D _leanRoll;
    private Spring1D _torsoPitch;
    private Spring1D _torsoYaw;
    private Spring1D _torsoRoll;
    private Spring1D _squashY;
    private Spring1D _bodyOffsetY;
    private Spring2D _bodyOffsetXZ; // X = local forward, Y = local right
    private Spring1D _headYaw;
    private Spring1D _headPitch;
    private Spring1D _headRoll;
    private Spring1D _diveRoll;
    private Spring1D _divePitch;
    private Spring1D _kickFootSwing;
    private Spring1D _kickFootLift;
    /// <summary>Which boot the sprung kick arc currently belongs to (-1 left, +1 right); latched from the stages.</summary>
    private float _activeKickFootSide = 1f;
    private Spring3D _handReach;
    private Spring1D _rootYaw;

    private PlayerPose _pose = PlayerPose.Neutral;
    private bool _initialized;

    // --- Shared derived state, recomputed once per Step --------------------------------------
    /// <summary>Presentation tick length in simulated seconds; zero while the debug pause is on.</summary>
    private float _dt;
    /// <summary>Accumulated simulated seconds (never wall clock): drives breathing and timers.</summary>
    private float _simTime;

    private Vector2 _facing = Vector2.UnitX;
    private Vector2 _facingRight = new(0f, 1f);
    private Vector3 _velocity;
    private Vector3 _previousVelocity;
    private Vector3 _acceleration;
    private float _speedXZ;
    private float _velocityForward;
    private float _velocityRight;
    private float _accelerationForward;
    private float _accelerationRight;
    private float _rootHeight;
    private float _previousBalance = 1f;
    private float _previousKickRecovery;
    private float _previousStamina = 1f;

    // --- Pending one-shot inputs (cleared after the stage pipeline consumed them) ------------
    private bool _pendingKick;
    private KickIntentType _pendingKickIntent = KickIntentType.None;
    private bool _pendingTackle;
    private bool _pendingCollection;
    private bool _pendingSave;
    private bool _pendingParry;
    private KeeperVisualEvent _pendingKeeperEvent = KeeperVisualEvent.None;

    public PlayerDynamicsSolver(int playerId, bool isGoalkeeper, PlayerDynamicsSettings? settings = null)
    {
        PlayerId = playerId;
        IsGoalkeeper = isGoalkeeper;
        _settings = settings ?? new PlayerDynamicsSettings();
    }

    /// <summary>Simulation player id this solver poses.</summary>
    public int PlayerId { get; }

    /// <summary>True when the player is a goalkeeper (keeper stage is enabled for him).</summary>
    public bool IsGoalkeeper { get; }

    /// <summary>The pose produced by the most recent <see cref="Step"/>.</summary>
    public PlayerPose Pose => _pose;

    /// <summary>
    /// Global exaggeration scalar for every dynamic offset and angle. Reads the live
    /// <see cref="PlayerDynamicsSettings"/> the solver was built with, so a scene can dial motion weight at
    /// runtime (e.g. an exported inspector value) without rebuilding the pose state.
    /// </summary>
    public float MotionIntensity
    {
        get => _settings.MotionIntensity;
        set => _settings.MotionIntensity = float.IsFinite(value) ? MathF.Max(0f, value) : 1f;
    }

    /// <summary>True once at least one frame has been solved (the rig can render <see cref="Pose"/>).</summary>
    public bool IsInitialized => _initialized;

    /// <summary>Current smoothed root yaw (radians), exposed for diagnostics/tests.</summary>
    public float VisualYaw => _rootYaw.Value;

    /// <summary>Current squash-stretch scalar (<c>BodyScale.Y = 1 + Squash</c>), exposed for tests.</summary>
    public float Squash => _squashY.Value;

    /// <summary>Current body pitch (radians; negative = leaning forward), exposed for tests.</summary>
    public float BodyPitch => _leanPitch.Value;

    /// <summary>Current dive roll (radians; + = diving right), exposed for tests.</summary>
    public float DiveRoll => _diveRoll.Value;

    /// <summary>Current body vertical offset in metres (negative = crouched), exposed for tests.</summary>
    public float BodyOffsetY => _bodyOffsetY.Value;

    /// <summary>
    /// Records a contact the execution layer reported for this player. Consumed by the next
    /// <see cref="Step"/>; a contact is one-shot and is never re-applied when a cache goes stale.
    /// </summary>
    public void NotifyContact(in BallContactEvent contact)
    {
        if (contact.PlayerId != PlayerId) return;
        switch (contact.Kind)
        {
            case BallContactKind.Kick:
                _pendingKick = true;
                _pendingKickIntent = contact.Intent;
                break;
            case BallContactKind.Tackle:
                _pendingTackle = true;
                break;
            case BallContactKind.Collection:
                _pendingCollection = true;
                break;
            case BallContactKind.Save:
                _pendingSave = true;
                _pendingParry |= contact.IsParry;
                break;
        }
    }

    /// <summary>Records a keeper touch (catch/parry/claim/punch) for the next <see cref="Step"/>.</summary>
    public void NotifyKeeperEvent(KeeperVisualEvent visualEvent)
    {
        if (visualEvent == KeeperVisualEvent.None) return;
        // A punch is also reported as a parry contact; the explicit event wins.
        if (_pendingKeeperEvent == KeeperVisualEvent.None || visualEvent == KeeperVisualEvent.PunchClear)
            _pendingKeeperEvent = visualEvent;
    }

    /// <summary>
    /// Advances every spring by <paramref name="simDt"/> simulated seconds and composes the pose.
    /// <paramref name="simDt"/> is the presentation's simulated advance, never wall clock: it is exactly
    /// <c>steps * SimConstants.FixedDeltaTime</c>, so the debug pause (0), slow-mo and 4x spectator
    /// fast-forward all produce the same pose as the ticks they represent.
    /// </summary>
    public void Step(in PlayerDynamicsInput input, float simDt)
    {
        if (input.Player.Id != PlayerId) return;

        if (!float.IsFinite(simDt) || simDt < 0f) simDt = 0f;
        if (simDt > 0.25f) simDt = 0.25f; // a hitch must never teleport the springs

        if (!_settings.EnableDynamics)
        {
            _pose = PlayerPose.Neutral;
            _pose.RootPosition = input.VisualPosition;
            _pose.RootYaw = YawOf(input.Player.FacingXZ);
            _initialized = true;
            return;
        }

        // Teleport guard: a position jump between rendered frames is a kickoff/restart placement, a training
        // reset or a scene reload - never real running. It is measured on the ground plane because the pose root
        // is deliberately Y = 0 while the visual position carries the authoritative jump height.
        float deltaX = input.VisualPosition.X - _pose.RootPosition.X;
        float deltaZ = input.VisualPosition.Z - _pose.RootPosition.Z;
        float snapDistance = _settings.TeleportSnapDistance;
        bool teleport = !_initialized ||
                        deltaX * deltaX + deltaZ * deltaZ > snapDistance * snapDistance;

        _dt = simDt;
        _simTime += simDt;

        UpdateDerivedState(in input, teleport);

        if (teleport)
        {
            // Render exactly one clean rest frame at the new spot and skip the stage pipeline: what a reset must
            // never do is replay the action state the player carried into it (a keeper standing up from a dive at
            // kickoff, a celebration still bouncing after a restart, a tackle lunge across half-time).
            ResetDynamics(in input);
            var restTargets = new ChannelTargets { ShadowScaleXZ = 1f, ShadowAlpha = 1f };
            ComposePose(in input, in restTargets);
            ClearPendingInputs();
            _initialized = true;
            return;
        }

        _rootYaw.StepAngle(YawOf(input.Player.FacingXZ), RootYawTuning.Omega, RootYawTuning.Zeta, simDt);

        var targets = new ChannelTargets
        {
            ShadowScaleXZ = 1f,
            ShadowAlpha = 1f
        };

        AccumulateLocomotion(in input, ref targets);
        ApplyActions(in input, ref targets);
        if (IsGoalkeeper && _settings.EnableGoalkeeperDynamics)
            ApplyGoalkeeperDynamics(in input, ref targets);
        if (_settings.EnableGaze)
            AccumulateUpperBody(in input, ref targets);

        StepSprings(in targets, simDt);
        ComposePose(in input, in targets);
        ClearPendingInputs();
        _initialized = true;
    }

    /// <summary>
    /// Forces the pose back to a clean standing rest at the given render position and clears every
    /// spring velocity. Used on scene (re)start/reset and available to the manager for an explicit reset.
    /// </summary>
    public void ResetDynamics(in PlayerDynamicsInput input)
    {
        _leanPitch.Reset(0f);
        _leanRoll.Reset(0f);
        _torsoPitch.Reset(0f);
        _torsoYaw.Reset(0f);
        _torsoRoll.Reset(0f);
        _squashY.Reset(0f);
        _bodyOffsetY.Reset(0f);
        _bodyOffsetXZ.Reset(Vector2.Zero);
        _headYaw.Reset(0f);
        _headPitch.Reset(0f);
        _headRoll.Reset(0f);
        _diveRoll.Reset(0f);
        _divePitch.Reset(0f);
        _kickFootSwing.Reset(0f);
        _kickFootLift.Reset(0f);
        _handReach.Reset(Vector3.Zero);
        _rootYaw.Reset(YawOf(input.Player.FacingXZ));

        _pose = PlayerPose.Neutral;
        _pose.RootPosition = new Vector3(input.VisualPosition.X, 0f, input.VisualPosition.Z);
        _pose.BodyOffset = new Vector3(0f, input.VisualPosition.Y, 0f);
        _pose.RootYaw = _rootYaw.Value;
        _velocity = input.Player.Velocity;
        _previousVelocity = input.Player.Velocity;
        _acceleration = Vector3.Zero;
        _previousBalance = input.Player.Balance01;
        _previousKickRecovery = input.Player.KickRecoveryTimer;
        _previousStamina = input.Player.Stamina01;
        _pendingKick = false;
        _pendingTackle = false;
        _pendingCollection = false;
        _pendingSave = false;
        _pendingParry = false;
        _pendingKeeperEvent = KeeperVisualEvent.None;

        // The stages own their one-shot timers and edge detectors, so a teleport must clear them too. Doing it
        // here (one detection, one reset path) is why no stage re-implements its own teleport heuristic.
        ResetLocomotionState(in input);
        ResetActionState(in input);
        ResetGoalkeeperState(in input);
        ResetUpperBodyState(in input);
        _initialized = true;
    }

    // =========================================================================================
    // Shared derived state
    // =========================================================================================

    /// <summary>
    /// Recomputes the values every stage reads: player-local velocity and acceleration, smoothed
    /// facing, root height and the previous-frame timer snapshots used for one-shot detection.
    /// </summary>
    private void UpdateDerivedState(in PlayerDynamicsInput input, bool teleport)
    {
        FootballerState player = input.Player;
        _facing = SafeNormalize(player.FacingXZ, Vector2.UnitX);
        _facingRight = new Vector2(-_facing.Y, _facing.X);

        _velocity = player.Velocity;
        _rootHeight = input.VisualPosition.Y;
        _speedXZ = SpringMath.HorizontalLength(_velocity);

        if (_dt > 0f)
            _acceleration = (_velocity - _previousVelocity) / _dt;
        else
            _acceleration = Vector3.Zero;

        _velocityForward = _velocity.X * _facing.X + _velocity.Z * _facing.Y;
        _velocityRight = _velocity.X * _facingRight.X + _velocity.Z * _facingRight.Y;
        _accelerationForward = _acceleration.X * _facing.X + _acceleration.Z * _facing.Y;
        _accelerationRight = _acceleration.X * _facingRight.X + _acceleration.Z * _facingRight.Y;

        if (teleport)
        {
            _previousBalance = player.Balance01;
            _previousKickRecovery = player.KickRecoveryTimer;
        }
    }

    /// <summary>Stores the frame's raw facts so the next frame can compute deltas.</summary>
    private void CaptureFrameEnd(in PlayerDynamicsInput input)
    {
        FootballerState player = input.Player;
        _previousVelocity = player.Velocity;
        _previousBalance = player.Balance01;
        _previousKickRecovery = player.KickRecoveryTimer;
        _previousStamina = player.Stamina01;
    }

    private void ClearPendingInputs()
    {
        _pendingKick = false;
        _pendingKickIntent = KickIntentType.None;
        _pendingTackle = false;
        _pendingCollection = false;
        _pendingSave = false;
        _pendingParry = false;
        _pendingKeeperEvent = KeeperVisualEvent.None;
    }

    // =========================================================================================
    // Impulse API for the stage partials
    //
    // A stage states targets for continuous motion, but a *contact* is a velocity injection: the kick
    // whip, the landing thud, the parry recoil. These helpers are the only way a stage can touch a
    // spring's momentum, and they never move a position.
    //
    // SCALING CONTRACT: stages write physically meaningful values (radians, metres, rad/s) and never apply
    // the global MotionIntensity themselves; ComposePose multiplies every output by it exactly once. That
    // keeps the dial linear and impossible to double-apply.
    // =========================================================================================

    private void ImpulseLeanPitch(float delta) => _leanPitch.AddImpulse(delta);
    private void ImpulseLeanRoll(float delta) => _leanRoll.AddImpulse(delta);
    private void ImpulseTorsoPitch(float delta) => _torsoPitch.AddImpulse(delta);
    private void ImpulseTorsoYaw(float delta) => _torsoYaw.AddImpulse(delta);
    private void ImpulseTorsoRoll(float delta) => _torsoRoll.AddImpulse(delta);
    private void ImpulseSquash(float delta) => _squashY.AddImpulse(delta);
    private void ImpulseBodyOffsetY(float delta) => _bodyOffsetY.AddImpulse(delta);
    private void ImpulseBodyOffsetXZ(float forward, float right) =>
        _bodyOffsetXZ.AddImpulse(new Vector2(forward, right));
    private void ImpulseDiveRoll(float delta) => _diveRoll.AddImpulse(delta);
    private void ImpulseDivePitch(float delta) => _divePitch.AddImpulse(delta);
    private void ImpulseKickFoot(float swing, float lift)
    {
        _kickFootSwing.AddImpulse(swing);
        _kickFootLift.AddImpulse(lift);
    }

    private void ImpulseHandReach(Vector3 delta) => _handReach.AddImpulse(delta);

    // --- Peak-deflection snaps ----------------------------------------------------------------
    //
    // A one-shot action states the amplitude it should visibly reach — the plan's "-28° header whip",
    // "-0.14 m landing dip", "+12% takeoff stretch". A raw velocity impulse of that number would be ~25x
    // too weak, because a spring's peak displacement is only about 0.37*v/omega (critically damped).
    // These helpers convert the requested peak into the momentum that actually reaches it, so retuning a
    // spring's stiffness can never silently rescale an action's amplitude.
    //
    // Kick whips deliberately stay on the raw `Impulse*` helpers: their profiles were calibrated as
    // velocity impulses on top of a sustained, amplitude-driven follow-through pose.

    /// <summary>
    /// Momentum (units/second) that makes a spring with the given tuning peak at
    /// <paramref name="peak"/> units away from its current target.
    /// </summary>
    private static float PeakToImpulse(float peak, in SpringTuning tuning)
    {
        float omega = tuning.Omega;
        float zeta = tuning.Zeta;
        if (omega <= 0f || !float.IsFinite(peak)) return 0f;

        float peakFactor;
        if (zeta >= 0.999f)
        {
            // Critically damped: x(t) = v0 * t * e^(-omega t), peak at t = 1/omega.
            peakFactor = 1f / (omega * MathF.E);
        }
        else
        {
            float dampedOmega = omega * MathF.Sqrt(1f - zeta * zeta);
            float peakTime = MathF.Atan(dampedOmega / (zeta * omega)) / dampedOmega;
            peakFactor = MathF.Exp(-zeta * omega * peakTime) * MathF.Sin(dampedOmega * peakTime) / dampedOmega;
        }

        if (peakFactor <= 1e-6f) return 0f;
        return peak / peakFactor;
    }

    private void SnapLeanPitch(float peakRadians) =>
        _leanPitch.AddImpulse(PeakToImpulse(peakRadians, LeanPitchTuning));
    private void SnapLeanRoll(float peakRadians) =>
        _leanRoll.AddImpulse(PeakToImpulse(peakRadians, LeanRollTuning));
    private void SnapTorsoPitch(float peakRadians) =>
        _torsoPitch.AddImpulse(PeakToImpulse(peakRadians, TorsoPitchTuning));
    private void SnapTorsoYaw(float peakRadians) =>
        _torsoYaw.AddImpulse(PeakToImpulse(peakRadians, TorsoYawTuning));
    private void SnapTorsoRoll(float peakRadians) =>
        _torsoRoll.AddImpulse(PeakToImpulse(peakRadians, TorsoRollTuning));
    private void SnapSquash(float peak) =>
        _squashY.AddImpulse(PeakToImpulse(peak, SquashTuning));
    private void SnapBodyOffsetY(float peakMetres) =>
        _bodyOffsetY.AddImpulse(PeakToImpulse(peakMetres, BodyOffsetYTuning));
    private void SnapBodyOffsetXZ(float forwardPeak, float rightPeak)
    {
        float impulse = PeakToImpulse(1f, BodyOffsetXZTuning);
        _bodyOffsetXZ.AddImpulse(new Vector2(forwardPeak * impulse, rightPeak * impulse));
    }

    private void SnapKickFoot(float swingPeakMetres, float liftPeakMetres)
    {
        _kickFootSwing.AddImpulse(PeakToImpulse(swingPeakMetres, KickFootTuning));
        _kickFootLift.AddImpulse(PeakToImpulse(liftPeakMetres, KickLiftTuning));
    }

    private void SnapHeadPitch(float peakRadians) =>
        _headPitch.AddImpulse(PeakToImpulse(peakRadians, HeadPitchTuning));

    private void SnapHandReach(Vector3 peakMetres)
    {
        float impulse = PeakToImpulse(1f, HandReachTuning);
        _handReach.AddImpulse(peakMetres * impulse);
    }

    // =========================================================================================
    // Spring bank
    // =========================================================================================

    /// <summary>
    /// Steps every spring toward the frame's targets. The tuning lives here so a stage can never need to
    /// know how stiff a channel is; a stage only states where the body should go and what it should feel.
    /// </summary>
    private void StepSprings(in ChannelTargets t, float dt)
    {
        _leanPitch.Step(t.LeanPitch, LeanPitchTuning.Omega, LeanPitchTuning.Zeta, dt);
        _leanRoll.Step(t.LeanRoll, LeanRollTuning.Omega, LeanRollTuning.Zeta, dt);
        _torsoPitch.Step(t.TorsoPitch, TorsoPitchTuning.Omega, TorsoPitchTuning.Zeta, dt);
        _torsoYaw.Step(t.TorsoYaw, TorsoYawTuning.Omega, TorsoYawTuning.Zeta, dt);
        _torsoRoll.Step(t.TorsoRoll, TorsoRollTuning.Omega, TorsoRollTuning.Zeta, dt);
        _squashY.Step(t.SquashY, SquashTuning.Omega, SquashTuning.Zeta, dt);
        _bodyOffsetY.Step(t.BodyOffsetY, BodyOffsetYTuning.Omega, BodyOffsetYTuning.Zeta, dt);
        _bodyOffsetXZ.Step(new Vector2(t.BodyOffsetForward, t.BodyOffsetRight),
            BodyOffsetXZTuning.Omega, BodyOffsetXZTuning.Zeta, dt);
        _headYaw.StepAngle(t.HeadYaw, HeadYawTuning.Omega, HeadYawTuning.Zeta, dt);
        _headPitch.Step(t.HeadPitch, HeadPitchTuning.Omega, HeadPitchTuning.Zeta, dt);
        _headRoll.Step(t.HeadRoll, HeadRollTuning.Omega, HeadRollTuning.Zeta, dt);
        _diveRoll.Step(t.DiveRoll, DiveRollTuning.Omega, DiveRollTuning.Zeta, dt);
        _divePitch.Step(t.DivePitch, DivePitchTuning.Omega, DivePitchTuning.Zeta, dt);
        _kickFootSwing.Step(t.KickFootSwing, KickFootTuning.Omega, KickFootTuning.Zeta, dt);
        _kickFootLift.Step(t.KickFootLift, KickLiftTuning.Omega, KickLiftTuning.Zeta, dt);
        // The kick arc lives on a spring that outlasts the single contact frame which chose the foot, so the
        // side is latched here: a stage only has to state it when it starts the action, not every frame.
        if (t.KickFootSide < 0f) _activeKickFootSide = -1f;
        else if (t.KickFootSide > 0f) _activeKickFootSide = 1f;
        _handReach.Step(t.HandReach, HandReachTuning.Omega, HandReachTuning.Zeta, dt);
    }

    /// <summary>
    /// Maps the settled spring bank onto the rig-neutral pose. All clamps live here: even a stage that
    /// asks for an absurd target cannot produce a pose the rig cannot draw.
    /// </summary>
    private void ComposePose(in PlayerDynamicsInput input, in ChannelTargets t)
    {
        float intensity = SpringMath.Clamp(_settings.MotionIntensity, 0f, 4f);
        float diveBlend = SpringMath.Clamp01(t.DiveBlend01);

        float squash = SpringMath.Clamp(_squashY.Value * intensity, -0.45f, 0.45f);
        float scaleY = 1f + squash;
        float scaleXZ = 1f / MathF.Sqrt(scaleY);

        _pose.RootPosition = new Vector3(input.VisualPosition.X, 0f, input.VisualPosition.Z);
        _pose.RootYaw = _rootYaw.Value;
        // The root is the ground plane anchor (so the shadow, ring and facing tick stay flat on the grass);
        // the authoritative jump height is carried by the body pivot, exactly as the plan's hierarchy says.
        float dynamicOffsetY = SpringMath.Clamp(_bodyOffsetY.Value * intensity, -0.8f, 1.0f);
        _pose.BodyOffset = new Vector3(
            SpringMath.Clamp(_bodyOffsetXZ.Value.X * intensity, -0.9f, 0.9f),
            dynamicOffsetY + input.VisualPosition.Y,
            SpringMath.Clamp(_bodyOffsetXZ.Value.Y * intensity, -0.9f, 0.9f));
        _pose.BodyScale = new Vector3(scaleXZ, scaleY, scaleXZ);

        float diveRoll = SpringMath.Clamp(_diveRoll.Value * intensity, -1.60f, 1.60f);
        float divePitch = SpringMath.Clamp(_divePitch.Value * intensity, -1.60f, 1.60f);
        _pose.BodyRotation = new Vector3(
            SpringMath.Lerp(SpringMath.Clamp(_leanRoll.Value * intensity, -0.8f, 0.8f), diveRoll, diveBlend),
            0f,
            SpringMath.Lerp(SpringMath.Clamp(_leanPitch.Value * intensity, -0.9f, 0.9f), divePitch, diveBlend));

        _pose.TorsoRotation = new Vector3(
            SpringMath.Clamp(_torsoRoll.Value * intensity, -1.0f, 1.0f),
            SpringMath.Clamp(_torsoYaw.Value * intensity, -1.4f, 1.4f),
            SpringMath.Clamp(_torsoPitch.Value * intensity, -1.2f, 1.2f));
        // Head: yaw is NOT scaled (it mirrors the simulation's own scan, so the rendered shoulder-check must
        // match the decided one); pitch/roll are presentation-only and follow the global dial like every other
        // body channel.
        _pose.HeadRotation = new Vector3(
            SpringMath.Clamp(_headRoll.Value * intensity, -0.8f, 0.8f),
            SpringMath.Clamp(_headYaw.Value, -1.3f, 1.3f),
            SpringMath.Clamp(_headPitch.Value * intensity, -0.9f, 0.9f));

        // Feet: analytic stride swing from the locomotion stage plus the sprung kick arc on the active foot.
        // MotionIntensity is applied here, once, to the whole foot output (see the scaling contract above).
        float kickSwing = _kickFootSwing.Value;
        float kickLift = _kickFootLift.Value;
        float kickLeftSwing = _activeKickFootSide < 0f ? kickSwing : 0f;
        float kickRightSwing = _activeKickFootSide > 0f ? kickSwing : 0f;
        float kickLeftLift = _activeKickFootSide < 0f ? kickLift : 0f;
        float kickRightLift = _activeKickFootSide > 0f ? kickLift : 0f;
        _pose.LeftFootLocal = RigDimensions.LeftFootRest + new Vector3(
            SpringMath.Clamp((t.LeftFootSwing + kickLeftSwing) * intensity, -0.55f, 0.85f),
            SpringMath.Clamp((t.LeftFootLift + kickLeftLift) * intensity, -0.08f, 0.80f),
            0f);
        _pose.RightFootLocal = RigDimensions.RightFootRest + new Vector3(
            SpringMath.Clamp((t.RightFootSwing + kickRightSwing) * intensity, -0.55f, 0.85f),
            SpringMath.Clamp((t.RightFootLift + kickRightLift) * intensity, -0.08f, 0.80f),
            0f);

        // Hands: the symmetric reach (forward, up, spread) is mirrored across the torso centre line, while the
        // analytic swing offsets let the two arms work in anti-phase during a run. A single mirrored reach alone
        // would move both arms together (a ski-jumper gait) instead of the left/right running swing.
        Vector3 reach = _handReach.Value * intensity;
        _pose.LeftHandLocal = RigDimensions.LeftHandRest + new Vector3(
            reach.X + t.LeftHandSwing * intensity, reach.Y, -reach.Z);
        _pose.RightHandLocal = RigDimensions.RightHandRest + new Vector3(
            reach.X + t.RightHandSwing * intensity, reach.Y, reach.Z);

        // Ground shadow: the only readable height cue from the broadcast camera. It shrinks and fades with
        // the body's height above the turf — jump height, crouch depth and dive arc all feed it.
        float bodyHeight = MathF.Max(0f, _pose.RootPosition.Y + _pose.BodyOffset.Y);
        float shadowScale = SpringMath.Clamp(1f - bodyHeight * 0.30f, 0.40f, 1.05f) * t.ShadowScaleXZ;
        _pose.ShadowScaleXZ = SpringMath.Clamp(shadowScale, 0.20f, 1.2f);
        _pose.ShadowAlpha = SpringMath.Clamp(1f - bodyHeight * 0.45f, 0.18f, 1f) * t.ShadowAlpha;

        CaptureFrameEnd(in input);
    }

    // =========================================================================================
    // Helpers shared with the stage partials
    // =========================================================================================

    private static Vector2 SafeNormalize(Vector2 value, Vector2 fallback)
    {
        float lengthSquared = value.X * value.X + value.Y * value.Y;
        if (lengthSquared < 1e-8f || !float.IsFinite(lengthSquared)) return fallback;
        float inverse = 1f / MathF.Sqrt(lengthSquared);
        return new Vector2(value.X * inverse, value.Y * inverse);
    }

    private static float YawOf(Vector2 facingXZ) => MathF.Atan2(-facingXZ.Y, facingXZ.X);

    /// <summary>Projects a world XZ direction onto the player's forward/right axes.</summary>
    private void ToLocalAxes(Vector2 worldXZ, out float forward, out float right)
    {
        forward = worldXZ.X * _facing.X + worldXZ.Y * _facing.Y;
        right = worldXZ.X * _facingRight.X + worldXZ.Y * _facingRight.Y;
    }

    /// <summary>
    /// Per-frame targets for the spring bank. A stage contributes by writing a field; the spring bank
    /// adds the frame's own impulses on top when a contact lands.
    /// </summary>
    private struct ChannelTargets
    {
        public float LeanPitch;      // BodyRotation.Z: + leans backward, - leans forward
        public float LeanRoll;       // BodyRotation.X: + banks right
        public float TorsoPitch;     // waist bend, same sign convention as LeanPitch
        public float TorsoYaw;       // waist twist: + twists left
        public float TorsoRoll;      // waist side tilt: + tilts right
        public float SquashY;        // additive: BodyScale.Y = 1 + value
        public float BodyOffsetY;    // metres, + up
        public float BodyOffsetForward; // metres along local +X
        public float BodyOffsetRight;   // metres along local +Z
        public float HeadYaw;        // HeadRotation.Y in radians, already in rig sign
        public float HeadPitch;      // HeadRotation.Z in radians, + looks up
        public float HeadRoll;       // HeadRotation.X in radians, + tilts right
        public float DiveRoll;       // radians, + dives right
        public float DivePitch;      // radians, + pitches backward
        public float DiveBlend01;    // 0 = locomotion lean wins, 1 = dive roll/pitch wins
        public float KickFootSwing;  // metres along local +X applied to the active foot
        public float KickFootLift;   // metres along local +Y applied to the active foot
        public float KickFootSide;   // -1 = left foot, +1 = right foot, 0 = no kick
        public Vector3 HandReach;    // torso-local offset: X forward, Y up, Z spread (mirrored)
        public float LeftHandSwing;  // metres along local +X, per-hand (anti-phase running swing)
        public float RightHandSwing; // metres along local +X, per-hand
        public float LeftFootSwing;  // metres along local +X
        public float RightFootSwing;
        public float LeftFootLift;   // metres along local +Y
        public float RightFootLift;
        public float ShadowScaleXZ;  // multiplier on the computed shadow scale
        public float ShadowAlpha;    // multiplier on the computed shadow alpha
    }
}
