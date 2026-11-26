using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;

namespace FootballSim.Presentation;

/// <summary>
/// Locomotion stage (phase P1 of the plan): idle breathing / fatigue, acceleration &amp; sprint lean, hard
/// braking, centripetal cornering bank, the distance-driven stride cycle (bounce, waist counter-twist, hip
/// sway, boots and hands swing), backpedal / lateral jockey, dribble rhythm and the shielding crouch.
///
/// The stage writes <see cref="ChannelTargets"/> only. It must never touch the spring bank directly except
/// through the <c>Impulse*</c> helpers for a discrete event, and it must derive every oscillation from
/// simulated time (<see cref="_simTime"/>) or integrated distance (its own stride phase) — never wall clock.
/// Continuously-held targets are stated in physical units; the core applies <c>MotionIntensity</c> exactly
/// once when it composes the pose, so scaling a target here would square the global dial.
/// </summary>
public sealed partial class PlayerDynamicsSolver
{
    // --- Idle / breathing tuning -------------------------------------------------------------
    /// <summary>Resting breath rate of a fresh player (plan 5.1: ~0.35 Hz).</summary>
    private const float FreshBreathHertz = 0.35f;

    /// <summary>Winded breath rate at empty stamina (plan 5.1: up to ~0.75 Hz).</summary>
    private const float FatiguedBreathHertz = 0.75f;

    /// <summary>Stamina below which fatigue breathing/slouch begins (plan 5.1).</summary>
    private const float StaminaFatigueOnset01 = 0.50f;

    /// <summary>Idle torso bob amplitude, in metres.</summary>
    private const float IdleBreathBobMeters = 0.015f;

    /// <summary>Breathing SquashY amplitude; additive, the core maps it to <c>BodyScale.Y = 1 + value</c>.</summary>
    private const float IdleBreathSquash = 0.018f;

    /// <summary>Speed at which the idle breath starts fading out, in m/s.</summary>
    private const float BreathIdleFadeStartSpeed = 0.25f;

    /// <summary>Speed span (metres per second) over which the idle breath fades out.</summary>
    private const float BreathIdleFadeSpan = 0.65f;

    /// <summary>Speed below which a fatigued player still shows the slouch, in m/s.</summary>
    private const float SlouchFadeStartSpeed = 1.2f;

    /// <summary>Speed span (metres per second) over which the fatigue slouch fades out.</summary>
    private const float SlouchFadeSpan = 2.2f;

    /// <summary>Forward fatigue slouch at empty stamina (plan 5.1: -4° to -9°).</summary>
    private static readonly float FatigueSlouchMaxRadians = SpringMath.DegToRad(9f);

    // --- Run / sprint lean, braking and cornering tuning ------------------------------------
    /// <summary>Steady forward lean at a jog (plan 5.2.1: ~-10°).</summary>
    private const float JogRunLeanDegrees = 10f;

    /// <summary>Steady forward lean at full sprint (plan 5.2.1: ~-16°).</summary>
    private const float SprintRunLeanDegrees = 16f;

    /// <summary>Extra lean available from positive forward acceleration (explosive first step).</summary>
    private const float AccelRunLeanDegrees = 10f;

    /// <summary>Forward speed ratio treated as "a jog" on the lean curve.</summary>
    private const float RunLeanJogRatio = 0.35f;

    /// <summary>Acceleration at which hard braking begins (plan 5.2.2: -2.5 m/s²).</summary>
    private const float BrakingAccelThreshold = -2.5f;

    /// <summary>Additional deceleration past the threshold that reaches the full braking pose.</summary>
    private const float BrakingAccelSpan = 5f;

    /// <summary>Braking lean-back at the threshold (plan 5.2.2: +6°).</summary>
    private const float MinBrakingLeanDegrees = 6f;

    /// <summary>Braking lean-back at full dig-in (plan 5.2.2: +14°).</summary>
    private const float MaxBrakingLeanDegrees = 14f;

    /// <summary>Downward dip of the braking crouch, in metres (plan 5.2.2: -0.08).</summary>
    private const float BrakingCrouchMeters = 0.08f;

    /// <summary>Squash compression of the braking crouch (plan 5.2.2: slight).</summary>
    private const float BrakingSquash = 0.03f;

    /// <summary>Bank per m/s² of lateral acceleration; a hard cut saturates at the clamp.</summary>
    private const float CornerBankDegreesPerAccel = 3.4f;

    /// <summary>Cornering bank clamp (plan 5.2.3: ~+/-22°).</summary>
    private static readonly float MaxCornerBankRadians = SpringMath.DegToRad(22f);

    /// <summary>Speed at which cornering bank is in full effect, in m/s.</summary>
    private const float CornerBankFullSpeed = 2.5f;

    /// <summary>
    /// Owner feedback 2026-11-26: "they lean too much". Every locomotion body lean and bank is multiplied by
    /// this one dial, so the motion structure (speed ramp, braking cancel, cut direction) is unchanged while the
    /// silhouette stays closer to upright. The plan's 10-16 deg sprint lean reads as ~5.5-8.8 deg.
    /// </summary>
    private const float BodyLeanScale = 0.55f;

    // --- Stride cycle tuning ------------------------------------------------------------------
    /// <summary>Stride length at a walk, in metres (plan 5.2.4: ~0.9 m).</summary>
    private const float WalkStrideLengthMeters = 0.9f;

    /// <summary>Stride length at full sprint, in metres (plan 5.2.4: ~2.2 m).</summary>
    private const float SprintStrideLengthMeters = 2.2f;

    /// <summary>Backpedal / lateral jockey shortens the stride to this fraction.</summary>
    private const float JockeyStrideShortening = 0.55f;

    /// <summary>Stride bounce at a slow walk, in metres (plan 5.2.4: idle-ish 0.02 m).</summary>
    private const float WalkStrideBounceMeters = 0.015f;

    /// <summary>Stride bounce at full sprint, in metres (plan 5.2.4: 0.06-0.10 m).</summary>
    private const float SprintStrideBounceMeters = 0.085f;

    /// <summary>Waist counter-twist at a walk, in degrees (plan 5.2.4: +/-8°).</summary>
    private const float StrideYawMinDegrees = 8f;

    /// <summary>Waist counter-twist at full sprint, in degrees (plan 5.2.4: +/-14°).</summary>
    private const float StrideYawMaxDegrees = 14f;

    /// <summary>Hip sway at a walk, in degrees (plan 5.2.4: +/-3°).</summary>
    private const float StrideRollMinDegrees = 3f;

    /// <summary>Hip sway at full sprint, in degrees (plan 5.2.4: +/-5°).</summary>
    private const float StrideRollMaxDegrees = 5f;

    /// <summary>Boot swing amplitude at a walk, in metres (each side of the rest position).</summary>
    private const float WalkFootSwingMeters = 0.12f;

    /// <summary>Boot swing amplitude at full sprint, in metres; the core clamps it to -0.55..0.85 m.</summary>
    private const float SprintFootSwingMeters = 0.38f;

    /// <summary>Boot lift amplitude at a walk, in metres.</summary>
    private const float WalkFootLiftMeters = 0.02f;

    /// <summary>Boot lift amplitude at full sprint, in metres (plan 5.2.4: &lt;= 0.10 m).</summary>
    private const float SprintFootLiftMeters = 0.095f;

    /// <summary>Hand swing amplitude at a walk, in metres (plan 5.2.4: &lt;= 0.10 m).</summary>
    private const float WalkHandSwingMeters = 0.03f;

    /// <summary>Hand swing amplitude at full sprint, in metres (plan 5.2.4: &lt;= 0.10 m).</summary>
    private const float SprintHandSwingMeters = 0.09f;

    /// <summary>Speed at which the stride cycle starts to blend in, in m/s.</summary>
    private const float StrideStartSpeed = 0.22f;

    /// <summary>Speed span over which the stride cycle blends in from idle.</summary>
    private const float StrideFadeSpan = 1.2f;

    /// <summary>How much of the stride bounce survives a shielding stance (plan 5.3: 60% reduction).</summary>
    private const float ShieldStrideBounceFactor = 0.4f;

    /// <summary>How much of the stride bounce survives a backpedal / jockey shuffle (plan 5.2.4: 50%).</summary>
    private const float JockeyStrideBounceFactor = 0.5f;

    /// <summary>How much of the stride bounce survives an empty stamina bar.</summary>
    private const float FatigueStrideBounceFactor = 0.8f;

    /// <summary>How much of the boot swing survives an empty stamina bar.</summary>
    private const float FatigueFootSwingFactor = 0.85f;

    /// <summary>How much of the boot swing survives a backpedal / lateral jockey shuffle.</summary>
    private const float JockeyFootSwingFactor = 0.6f;

    /// <summary>Half-life of the ground-contact blend that fades the boots out while airborne.</summary>
    private const float StrideContactHalfLifeSeconds = 0.07f;

    // --- Jockey / shield / dribble tuning -----------------------------------------------------
    /// <summary>Defensive crouch of the backpedal / lateral jockey stance, in metres (plan 5.2.4: -0.07).</summary>
    private const float JockeyCrouchMeters = 0.07f;

    /// <summary>Slight backward body lean that keeps a backpedal reading as jockeying, in radians.</summary>
    private static readonly float JockeyLeanBackRadians = SpringMath.DegToRad(4f);

    /// <summary>Upright torso correction of the jockey stance, in radians.</summary>
    private static readonly float JockeyTorsoPitchRadians = SpringMath.DegToRad(2.5f);

    /// <summary>Centre-of-gravity drop of the shielding stance, in metres (plan 5.3: -0.14).</summary>
    private const float ShieldCrouchMeters = 0.14f;

    /// <summary>Planted squash of the shielding stance (plan 5.3: -0.07).</summary>
    private const float ShieldSquash = 0.07f;

    /// <summary>Forward torso pitch of the shielding stance, in radians (plan 5.3: -8°).</summary>
    private static readonly float ShieldTorsoPitchRadians = SpringMath.DegToRad(8f);

    /// <summary>Hand spread (forward, down, outward) of the shielding stance: mirrored by the rig.</summary>
    private static readonly Vector3 ShieldHandSpread = new(0.03f, -0.02f, 0.12f);

    /// <summary>Forward torso lean of a player carrying the ball, in radians (plan 5.2.5: -5°).</summary>
    private static readonly float DribbleTorsoPitchRadians = SpringMath.DegToRad(5f);

    /// <summary>One-shot boot forward peak of a dribble touch, in metres (Snap helper: peak deflection).</summary>
    private const float DribbleFlickSwingMeters = 0.08f;

    /// <summary>One-shot boot lift peak of a dribble touch, in metres.</summary>
    private const float DribbleFlickLiftMeters = 0.03f;

    /// <summary>One-shot waist-twist peak of a dribble touch, in radians.</summary>
    private static readonly float DribbleFlickYawRadians = SpringMath.DegToRad(6f);

    /// <summary>Half-life of the smoothed backpedal / lateral jockey blend.</summary>
    private const float JockeyBlendHalfLifeSeconds = 0.12f;

    // --- State --------------------------------------------------------------------------------
    /// <summary>
    /// Distance-integrated gait phase in radians. Rises by one turn per stride cycle, so integrating it as
    /// <c>speed / strideLength</c> is what ties the footfalls to ground distance instead of wall clock.
    /// </summary>
    private float _stridePhase;

    /// <summary>Simulated-time integrated breathing phase in radians.</summary>
    private float _breathPhase;

    /// <summary>Smoothed 0..1 backpedal / lateral jockey blend, so a direction change never snaps the stance.</summary>
    private float _jockeyBlend;

    /// <summary>Smoothed 0..1 ground-contact blend: fades the gait cycle out while the action stage owns the feet.</summary>
    private float _strideContactBlend = 1f;

    /// <summary>True once the free-running phases were seeded with this player's deterministic offsets.</summary>
    private bool _locomotionPhasesInitialized;

    /// <summary>
    /// Last rendered position this stage saw. The core's teleport guard runs before the stage pipeline and
    /// does not hand the fact down, so the stage recognises the same frame by the same distance rule.
    /// </summary>
    private Vector3 _lastLocomotionPosition;

    /// <summary>
    /// Locomotion stage entry point: derives the shared gait scalars once per frame, then lets each
    /// behaviour add its own contribution to the same <see cref="ChannelTargets"/>.
    ///
    /// Ordering is deliberate: the posture stages (breathing, lean, jockey, shield) only add, while the
    /// stride cycle needs to know how damped the legs must be, hence it receives the shield/jockey state.
    /// </summary>
    private void AccumulateLocomotion(in PlayerDynamicsInput input, ref ChannelTargets targets)
    {
        // The core snaps the pose to rest and zeroes every spring on the first solved frame and on a
        // teleport (kickoff/restart placement, training reset). Stay neutral for that one frame: deriving a
        // breathing bob or a braking crouch from the position/velocity discontinuity would animate the
        // snap itself, and the free-running phases must be re-seeded because no distance was really run.
        if (!_locomotionPhasesInitialized)
        {
            SeedLocomotionPhases();
            _locomotionPhasesInitialized = true;
            _lastLocomotionPosition = input.VisualPosition;
            return;
        }

        bool teleported = Vector3.Distance(input.VisualPosition, _lastLocomotionPosition) >
                          _settings.TeleportSnapDistance;
        _lastLocomotionPosition = input.VisualPosition;
        if (teleported)
        {
            SeedLocomotionPhases();
            return;
        }

        float stamina01 = SpringMath.Clamp01(input.Player.Stamina01);
        float fatigue01 = SpringMath.SmoothStep01(
            (StaminaFatigueOnset01 - stamina01) / StaminaFatigueOnset01);

        // A defender moving backwards or shuffling sideways is a different gait: shorter stride, lower
        // hips, upright torso. Blend it instead of branching hard so a cut from a sprint to a backpedal
        // never pops the stance.
        bool jockeying = _velocityForward < -0.5f ||
                         MathF.Abs(_velocityRight) > MathF.Abs(_velocityForward);
        _jockeyBlend = SpringMath.ExpApproach(
            _jockeyBlend, jockeying ? 1f : 0f, JockeyBlendHalfLifeSeconds, _dt);

        bool shielding = input.Player.LastCommand.ShieldBall;

        AddBreathing(fatigue01, ref targets);
        AddLeanAndBank(in input, _jockeyBlend, ref targets);
        AddStrideCycle(in input, fatigue01, _jockeyBlend, shielding, ref targets);
        AddJockey(_jockeyBlend, ref targets);
        AddDribbleRhythm(in input, ref targets);
        AddShieldStance(shielding, ref targets);
    }

    /// <summary>
    /// (Re-)seeds the free-running phases from the simulation player id and the simulated clock. The
    /// golden-ratio offsets give every player a different breathing and footfall cadence without any
    /// randomness, so eleven chests never rise in lockstep and no RNG or wall clock enters the presentation
    /// layer. The starting breath phase uses <see cref="_simTime"/> because that is the only clock the stage
    /// may follow.
    /// </summary>
    private void SeedLocomotionPhases()
    {
        const float GoldenRatioConjugate = 0.6180339887f;
        float breathOffset01 = PlayerId * GoldenRatioConjugate % 1f;
        float strideOffset01 = PlayerId * (1f - GoldenRatioConjugate) % 1f;

        _breathPhase = SpringMath.WrapAngle(
            _simTime * SpringMath.TwoPi * FreshBreathHertz + breathOffset01 * SpringMath.TwoPi);
        _stridePhase = SpringMath.WrapAngle(strideOffset01 * SpringMath.TwoPi);
        _jockeyBlend = 0f;
        _strideContactBlend = 1f;
    }

    /// <summary>
    /// Plan 5.1: a fresh player breathes slowly and quietly; a tired one breathes faster, twice as deep and
    /// starts to slouch forward at the waist. Everything runs off simulated time, so F1 slow-motion and F2
    /// pause breathe at the rate the match clock actually advances; a wall-clock oscillator would twitch
    /// while paused and drift after a frame hitch.
    /// </summary>
    private void AddBreathing(float fatigue01, ref ChannelTargets targets)
    {
        // The phase is integrated rather than evaluated as t * frequency, because the stamina-driven
        // frequency change would otherwise jump the sine argument and snap the chest mid-breath.
        float breathHertz = SpringMath.Lerp(FreshBreathHertz, FatiguedBreathHertz, fatigue01);
        _breathPhase = SpringMath.WrapAngle(_breathPhase + SpringMath.TwoPi * breathHertz * _dt);
        float breath = MathF.Sin(_breathPhase);

        // Breathing is an idle behaviour: it fades out before the stride cycle takes over, otherwise the
        // chest would keep pumping at a full sprint.
        float idleBlend = 1f - SpringMath.SmoothStep01(
            (_speedXZ - BreathIdleFadeStartSpeed) / BreathIdleFadeSpan);
        float amplitude = SpringMath.Lerp(1f, 2f, fatigue01) * idleBlend;

        targets.BodyOffsetY += breath * IdleBreathBobMeters * amplitude;
        targets.SquashY += breath * IdleBreathSquash * amplitude;

        // The slouch is posture, not breath: it survives a slow jog so a winded player still looks heavy,
        // and only a real run (where the forward lean dominates anyway) fades it out.
        float slouchBlend = 1f - SpringMath.SmoothStep01(
            (_speedXZ - SlouchFadeStartSpeed) / SlouchFadeSpan);
        targets.TorsoPitch -= FatigueSlouchMaxRadians * fatigue01 * slouchBlend;
    }

    /// <summary>
    /// Plan 5.2.1-5.2.3: the body's lean under its own momentum. A steady run pitches forward with speed,
    /// an explosive first step adds more, a hard stop pitches the body back over the planted feet (with a
    /// braking crouch), and a cut banks the body into the turn from the measured centripetal acceleration.
    /// </summary>
    private void AddLeanAndBank(in PlayerDynamicsInput input, float jockeyBlend, ref ChannelTargets targets)
    {
        float maxSpeed = MaxSprintSpeedOf(input.Player);

        // A walk should barely lean; a jog should already reach the ~10° the plan asks for; only a full
        // sprint earns the last 6°.
        float speedRatio = SpringMath.Clamp01(_velocityForward / maxSpeed);
        float jogGate = SpringMath.SmoothStep01(speedRatio / RunLeanJogRatio);
        float sprintRamp = SpringMath.SmoothStep01(
            (speedRatio - RunLeanJogRatio) / (1f - RunLeanJogRatio));
        float runLean = SpringMath.DegToRad(
            SpringMath.Lerp(JogRunLeanDegrees, SprintRunLeanDegrees, sprintRamp) * jogGate) * BodyLeanScale;

        // Explosive first step: normalise the measured acceleration by what this player can actually
        // produce, so a slow centre-back and a quick winger both read as "driving off the back foot".
        float accelCapability = MathF.Max(2f, input.Player.Attributes.Acceleration);
        float accelRatio = SpringMath.SmoothStep01(SpringMath.Clamp01(_accelerationForward / accelCapability));
        float accelLean = SpringMath.DegToRad(AccelRunLeanDegrees) * accelRatio * BodyLeanScale;

        // Hard braking (digging in): the run lean is cancelled as the body pitches back over the planted
        // feet, so the net silhouette leans backwards while the player hauls himself to a stop.
        float brake01 = SpringMath.SmoothStep01(
            (BrakingAccelThreshold - _accelerationForward) / BrakingAccelSpan);
        float brakeLean = SpringMath.DegToRad(
            SpringMath.Lerp(MinBrakingLeanDegrees, MaxBrakingLeanDegrees, brake01)) * brake01 * BodyLeanScale;

        float forwardLean = (runLean + accelLean) * (1f - 0.6f * brake01) * (1f - 0.55f * jockeyBlend);
        targets.LeanPitch += brakeLean - forwardLean;
        targets.BodyOffsetY -= BrakingCrouchMeters * brake01;
        targets.SquashY -= BrakingSquash * brake01;

        // Centripetal cornering: lateral acceleration points into the turn, so leaning toward it is what
        // makes a cut read. It is gated by speed because a first step in place has acceleration but no
        // momentum to bank against.
        float bankSpeed = SpringMath.SmoothStep01(_speedXZ / CornerBankFullSpeed);
        float bank = SpringMath.Clamp(
            SpringMath.DegToRad(CornerBankDegreesPerAccel) * _accelerationRight,
            -MaxCornerBankRadians, MaxCornerBankRadians) * bankSpeed * BodyLeanScale;
        targets.LeanRoll += bank;
    }

    /// <summary>
    /// Plan 5.2.4: a gait whose phase is the distance the player has actually covered. Because the phase
    /// advances by <c>speed / stride length</c>, the boots plant exactly as often as the ground passes
    /// underneath — a wall-clock oscillator would make feet skate the moment a player accelerates. Stride
    /// length grows from a walk to a sprint, the bounce is |sin| (one dip per footfall), and the waist
    /// counter-twists against the arms while the hips sway.
    ///
    /// The foot channels are the one pose channel the core does not scale by MotionIntensity (it clamps
    /// them instead), so the global dial is applied to them here explicitly.
    /// </summary>
    private void AddStrideCycle(in PlayerDynamicsInput input, float fatigue01, float jockeyBlend,
        bool shielding, ref ChannelTargets targets)
    {
        float maxSpeed = MaxSprintSpeedOf(input.Player);
        float sprint01 = SpringMath.SmoothStep01(SpringMath.Clamp01(_speedXZ / maxSpeed));
        float moving01 = SpringMath.SmoothStep01((_speedXZ - StrideStartSpeed) / StrideFadeSpan);

        // The action stage owns the feet once the player leaves the ground (jump tuck, landing squash).
        // Fade the gait out rather than fade it in a frame, so the takeoff does not snap the boots back.
        float contactTarget = input.Player.IsJumping || input.Player.IsDiving ? 0f : 1f;
        _strideContactBlend = SpringMath.ExpApproach(
            _strideContactBlend, contactTarget, StrideContactHalfLifeSeconds, _dt);

        // Distance integration: stride length is a property of the gait (and of the jockey shuffle), never
        // of the frame rate. The phase is wrapped so it can run for a whole match without losing precision.
        float strideLength = SpringMath.Lerp(
            WalkStrideLengthMeters, SprintStrideLengthMeters, sprint01);
        strideLength *= SpringMath.Lerp(1f, JockeyStrideShortening, jockeyBlend);
        _stridePhase = SpringMath.WrapAngle(
            _stridePhase + _speedXZ / strideLength * SpringMath.TwoPi * _dt);

        float amplitude = moving01 * _strideContactBlend;
        if (amplitude <= 1e-4f) return;

        // Bounce is damped when the player is rooted (shielding), shuffling (jockey) or heavy-legged.
        float bounceScale = SpringMath.Lerp(1f, FatigueStrideBounceFactor, fatigue01);
        bounceScale *= SpringMath.Lerp(1f, JockeyStrideBounceFactor, jockeyBlend);
        if (shielding) bounceScale *= ShieldStrideBounceFactor;

        float sinPhase = MathF.Sin(_stridePhase);
        float bounce = MathF.Abs(sinPhase) *
            SpringMath.Lerp(WalkStrideBounceMeters, SprintStrideBounceMeters, sprint01);
        targets.BodyOffsetY += bounce * bounceScale * amplitude;

        // Waist counter-twist and hip sway: the shoulders work against the stride, which is most of what
        // makes a run read as an athletic gait rather than a sliding capsule.
        float yawAmplitude = SpringMath.DegToRad(
            SpringMath.Lerp(StrideYawMinDegrees, StrideYawMaxDegrees, sprint01));
        targets.TorsoYaw += sinPhase * yawAmplitude * amplitude;

        float rollAmplitude = SpringMath.DegToRad(
            SpringMath.Lerp(StrideRollMinDegrees, StrideRollMaxDegrees, sprint01));
        targets.TorsoRoll += MathF.Cos(_stridePhase) * rollAmplitude * amplitude;

        // Boots: anti-phase swing along local +X, with the forward-swinging boot lifting off the turf.
        // Physical metres only — the core applies the global MotionIntensity once to the composed foot pose.
        float jockeyFootScale = SpringMath.Lerp(1f, JockeyFootSwingFactor, jockeyBlend);
        float footSwing = SpringMath.Lerp(WalkFootSwingMeters, SprintFootSwingMeters, sprint01) *
                          amplitude * jockeyFootScale *
                          SpringMath.Lerp(1f, FatigueFootSwingFactor, fatigue01);
        float footLift = SpringMath.Lerp(WalkFootLiftMeters, SprintFootLiftMeters, sprint01) *
                         amplitude * jockeyFootScale;

        targets.LeftFootSwing += sinPhase * footSwing;
        targets.RightFootSwing -= sinPhase * footSwing;
        targets.LeftFootLift += MathF.Max(0f, sinPhase) * footLift;
        targets.RightFootLift += MathF.Max(0f, -sinPhase) * footLift;

        // Hands: the HandReach channel is mirrored across the centre line, so it can only swing both arms
        // together. The left boot's phase is inverted so the upper body counter-swings the leading foot
        // instead of marching with it.
        float handSwing = SpringMath.Lerp(
            WalkHandSwingMeters, SprintHandSwingMeters, sprint01) * amplitude;
        targets.HandReach += new Vector3(-sinPhase * handSwing, 0f, 0f);
    }

    /// <summary>
    /// Plan 5.2.4 (backpedal / lateral jockey): a defender shuffling backwards is not a runner played in
    /// reverse. The hips drop and the torso stays upright — the shorter stride and half bounce live in
    /// <see cref="AddStrideCycle"/> — so the gait reads as jockeying instead of moonwalking.
    /// </summary>
    private void AddJockey(float jockeyBlend, ref ChannelTargets targets)
    {
        if (jockeyBlend <= 1e-3f) return;

        targets.BodyOffsetY -= JockeyCrouchMeters * jockeyBlend;
        targets.LeanPitch += JockeyLeanBackRadians * jockeyBlend;
        targets.TorsoPitch += JockeyTorsoPitchRadians * jockeyBlend;
    }

    /// <summary>
    /// Plan 5.2.5: a carrier stays low over the ball, and each real touch (the one-shot collection contact
    /// the execution layer reported) gets a tiny toe tap and waist flick so close control is visible. The
    /// pending flag is read only — the core clears it after the stage pipeline, so a contact can never be
    /// re-fired from a stale cache (AGENTS.md rule 4).
    /// </summary>
    private void AddDribbleRhythm(in PlayerDynamicsInput input, ref ChannelTargets targets)
    {
        if (!input.ControlsBall) return;

        targets.TorsoPitch -= DribbleTorsoPitchRadians;

        if (!_pendingCollection) return;

        bool rightFoot = input.Player.Attributes.PreferredFoot == Footedness.Right;
        float side = rightFoot ? 1f : -1f;

        // The active-foot channel only reaches the pose when the core knows which boot is working; the
        // action stage owns it whenever a real kick is pending, and a collection is never a kick.
        targets.KickFootSide = side;
        SnapKickFoot(DribbleFlickSwingMeters, DribbleFlickLiftMeters);
        SnapTorsoYaw(DribbleFlickYawRadians * side);
    }

    /// <summary>
    /// Plan 5.3: shielding is a planted, low, wide stance rather than a slow run. The centre of gravity
    /// drops, the body compresses and pitches forward, and the hands come out to hold the shield; the
    /// stride bounce damping lives in <see cref="AddStrideCycle"/>, leaving the player rooted to the turf.
    /// </summary>
    private void AddShieldStance(bool shielding, ref ChannelTargets targets)
    {
        if (!shielding) return;

        targets.BodyOffsetY -= ShieldCrouchMeters;
        targets.SquashY -= ShieldSquash;
        targets.TorsoPitch -= ShieldTorsoPitchRadians;
        targets.HandReach += ShieldHandSpread;
    }

    /// <summary>
    /// Sprint reference speed for the gait curves. A fixture is free to hand in a degenerate value; the
    /// floor keeps the normalisation finite and the pose sane in that case.
    /// </summary>
    private static float MaxSprintSpeedOf(FootballerState player) =>
        MathF.Max(3.5f, player.Attributes.MaxSprintSpeed);
}
