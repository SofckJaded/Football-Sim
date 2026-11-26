using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;

namespace FootballSim.Presentation;

/// <summary>
/// Action stage (phases P2/P3 of the plan): jumping &amp; aerial hang / headers / landing, all kick
/// follow-through profiles with the pre-contact wind-up coil, tackles, dispossession stumbles and
/// shoulder-to-shoulder body collisions.
///
/// The stage consumes the one-shot pending inputs set by <see cref="NotifyContact"/>:
/// <c>_pendingKick, _pendingKickIntent, _pendingTackle</c>. They are cleared by the core after the stage
/// pipeline, so an action fires exactly once and is never re-issued from a stale cache (AGENTS.md rule 4:
/// a discrete action is not a cached command). <c>_pendingCollection</c> belongs to the locomotion stage
/// (dribble rhythm) and <c>_pendingSave/_pendingParry</c> to the keeper stage; this file reads neither and
/// never clears any flag.
/// Rising edges (takeoff / landing / was-tackling) are detected with fields declared in this file.
/// </summary>
public sealed partial class PlayerDynamicsSolver
{
    // --- Action-stage tuning ------------------------------------------------------------------
    /// <summary>Visual settle of a tackle lunge; longer than the 0.08 s win lock so the action reads.</summary>
    private const float TackleHoldSeconds = 0.5f;

    /// <summary>Foot-forward lead held after a header contact.</summary>
    private const float HeaderLeadSeconds = 0.18f;

    /// <summary>Length of the dispossession wobble envelope.</summary>
    private const float StumbleSeconds = 0.6f;

    /// <summary>One balance/KickRecovery collapse must not re-arm the wobble every tick.</summary>
    private const float StumbleCooldownSeconds = 0.9f;

    /// <summary>One shoulder collision is one jolt, not one jolt per tick.</summary>
    private const float BumpCooldownSeconds = 0.25f;

    /// <summary>Above this horizontal acceleration a body-to-body separation happened.</summary>
    private const float BumpAccelerationThreshold = 28f;

    // --- Action-stage private state (domain-prefixed: partial files share one class) ------------
    private bool _actionWasJumping;
    private bool _actionWasAirborne;
    private float _actionLastAirborneVelocityY;
    private float _actionHeaderTimer;
    private float _actionWindUp;
    private int _actionKickSign = 1;
    private float _actionKickTimer;
    private float _actionKickDuration = 1f;
    private float _actionKickPitch;
    private float _actionKickYaw;
    private float _actionKickBank;
    private float _actionKickFootSwing;
    private float _actionKickFootLift;
    private float _actionKickRise;
    private bool _actionWasTackling;
    private float _actionTackleTimer;
    private int _actionTackleTwistSign = 1;
    private int _actionTackleBankSign = 1;
    private float _actionStumbleTimer;
    private float _actionStumbleCooldown;
    private float _actionStumbleAwayForward;
    private float _actionStumbleAwayRight;
    private float _actionBumpCooldown;

    /// <summary>
    /// Entry point. Continuous action poses are delivered through <see cref="ChannelTargets"/> while a
    /// private timer runs, so they compose with the locomotion stage's lean; the discrete *contact* (kick
    /// whip, landing thud, tackle lunge, header nod, stumble knock) is a momentum injection through the
    /// <c>Impulse*</c> helpers on the one contact frame only. Never touches the keeper dive channels: the
    /// keeper stage owns those and runs after this one.
    /// </summary>
    private void ApplyActions(in PlayerDynamicsInput input, ref ChannelTargets targets)
    {
        ApplyJumpHeaderAndLanding(in input, ref targets);
        ApplyKickStage(in input, ref targets);
        ApplyTackle(in input, ref targets);
        ApplyStumbleAndBump(in input, ref targets);
    }

    // =========================================================================================
    // Jump / aerial / header / landing (plan 5.4)
    // =========================================================================================

    /// <summary>
    /// Jump / aerial / header / landing sub-stage. Airborne is the sim's own jump fact plus its feet height;
    /// takeoff and landing are the two edges of <c>IsJumping</c>, with the stored last airborne vertical
    /// speed as the only landing-impact evidence left once the sim has zeroed <c>Velocity.Y</c>.
    /// </summary>
    private void ApplyJumpHeaderAndLanding(in PlayerDynamicsInput input, ref ChannelTargets targets)
    {
        FootballerState player = input.Player;
        bool airborne = player.IsJumping || player.Position.Y > 0.01f;
        bool takeoff = player.IsJumping && !_actionWasJumping;
        bool landing = !airborne && _actionWasAirborne;

        if (takeoff) ApplyTakeoffImpulse();

        if (airborne)
        {
            ApplyAirbornePose(ref targets);
            // Remember the last airborne vertical speed for the landing thud; on the landing frame the
            // sim has already zeroed Velocity.Y, so the stored value is the only impact evidence left.
            _actionLastAirborneVelocityY = player.Velocity.Y;
        }
        else if (landing)
        {
            ApplyLandingImpact(_actionLastAirborneVelocityY);
            _actionLastAirborneVelocityY = 0f;
        }

        if (_pendingKick && _pendingKickIntent == KickIntentType.Header)
            ArmHeaderStrike(in input);

        ApplyHeaderLead(ref targets);

        _actionWasJumping = player.IsJumping;
        _actionWasAirborne = airborne;
    }

    /// <summary>
    /// Plan 5.4.1: the leap already reads through <c>RootPosition.Y</c>; the squash stretch adds the body
    /// lengthening that pure translation cannot, and the waist/neck arch opens the chest toward the ball.
    /// </summary>
    private void ApplyTakeoffImpulse()
    {
        // Plan 5.4.1: "upward stretch impulse (+1.8 -> ~+12% vertical)". Peak amplitudes, not raw momentum:
        // these snaps are authored as the deflection the eye should see.
        SnapSquash(0.12f);
        SnapTorsoPitch(SpringMath.DegToRad(10f));
        SnapHeadPitch(SpringMath.DegToRad(8f));
    }

    /// <summary>
    /// Plan 5.4.2: in the air the stride has nothing to push against, so it stops; the boots tuck forward
    /// and the hands spread slightly for balance.
    /// </summary>
    private static void ApplyAirbornePose(ref ChannelTargets targets)
    {
        targets.LeftFootSwing = 0.16f;
        targets.RightFootSwing = 0.10f;
        // The compose clamp floors lift at -0.08 m, so the tuck is a small negative lift plus a forward
        // swing rather than a true knee bend this primitive rig cannot show.
        targets.LeftFootLift = -0.04f;
        targets.RightFootLift = -0.04f;
        targets.HandReach += new Vector3(0.06f, 0.10f, 0.14f);
        targets.HeadPitch += SpringMath.DegToRad(8f);
    }

    /// <summary>
    /// Plan 5.4.4: impact intensity comes from the last airborne vertical speed (a normal jump lands at
    /// roughly 3.6 m/s). The dip is injected as momentum so the underdamped vertical squash spring
    /// absorbs and rebounds instead of easing down like a tween.
    /// </summary>
    private void ApplyLandingImpact(float airborneVelocityY)
    {
        float intensity = SpringMath.Clamp(MathF.Abs(airborneVelocityY) / 3.6f, 0.25f, 1.6f);
        // Plan 5.4.4: dips ~-0.14 m and squashes ~-0.14, absorbed and rebounded by an underdamped spring.
        SnapBodyOffsetY(-0.14f * intensity);
        SnapSquash(-0.14f * intensity);
        SnapTorsoPitch(SpringMath.DegToRad(-7f) * intensity);
        SnapLeanPitch(SpringMath.DegToRad(-4f) * intensity);
    }

    /// <summary>
    /// Plan 5.4.3: the head snaps *through* the ball — a one-frame waist and neck whip plus a lead along
    /// the command's own aim direction, projected onto the body axes so it composes with a turning player.
    /// </summary>
    private void ArmHeaderStrike(in PlayerDynamicsInput input)
    {
        FootballerCommand command = input.Player.LastCommand;
        Vector2 aimXZ = new(command.KickAimDirectionXZ.X, command.KickAimDirectionXZ.Z);
        float aimLengthSquared = aimXZ.LengthSquared();
        aimXZ = aimLengthSquared > 1e-6f ? aimXZ / MathF.Sqrt(aimLengthSquared) : _facing;
        ToLocalAxes(aimXZ, out float forward, out float right);

        SnapTorsoPitch(SpringMath.DegToRad(-28f));
        SnapHeadPitch(SpringMath.DegToRad(-35f));
        SnapLeanPitch(SpringMath.DegToRad(-8f));
        SnapBodyOffsetXZ(forward * 0.10f, right * 0.10f);
        _actionHeaderTimer = HeaderLeadSeconds;
    }

    /// <summary>Brief forward body lead that keeps the header reading after the instantaneous whip.</summary>
    private void ApplyHeaderLead(ref ChannelTargets targets)
    {
        if (_actionHeaderTimer <= 0f) return;
        float fade = SpringMath.Clamp01(_actionHeaderTimer / HeaderLeadSeconds);
        targets.BodyOffsetForward += 0.10f * fade;
        targets.TorsoPitch -= SpringMath.DegToRad(10f) * fade;
        targets.LeanPitch -= SpringMath.DegToRad(5f) * fade;
        _actionHeaderTimer = MathF.Max(0f, _actionHeaderTimer - _dt);
    }

    // =========================================================================================
    // Kicks: wind-up coil, contact whip, sustained follow-through (plan 5.5)
    // =========================================================================================

    /// <summary>
    /// Kick sub-stage: arm the contact profile and shot arc, apply the sustained follow-through, then the
    /// pre-contact coil. The locomotion stage owns the dribble lean, so only genuine kick contacts appear here.
    /// </summary>
    private void ApplyKickStage(in PlayerDynamicsInput input, ref ChannelTargets targets)
    {
        FootballerState player = input.Player;
        FootballerCommand command = player.LastCommand;
        float power = SpringMath.Clamp01(command.KickPower01);
        int kickSign = ResolveKickFootSign(player);

        if (_pendingKick && _pendingKickIntent != KickIntentType.None
            && _pendingKickIntent != KickIntentType.Header)
            ArmKickAction(player, kickSign, power);

        ApplyKickFollowThrough(ref targets);
        UpdateKickWindUp(in input, kickSign, ref targets);
    }

    /// <summary>
    /// The plan's 5.5 strike table as data. Angles are the sustained follow-through amplitudes in degrees;
    /// the whip amplitudes are in the same degree-scale the sibling stages pass to the <c>Impulse*</c>
    /// helpers (a header whip and a goal-celebration impulse therefore use one unit language).
    /// </summary>
    private readonly record struct KickProfile(
        float PitchDeg, float YawDeg, float BankDeg,
        float FootSwing, float FootLift,
        float FootWhipSwing, float FootWhipLift,
        float YawWhipDeg, float BankWhipDeg,
        float BodyRise, float StretchWhip);

    /// <summary>Builds one <see cref="KickProfile"/>; only called on a contact frame, never per frame.</summary>
    private static KickProfile KickProfileFor(KickIntentType intent, float power)
    {
        switch (intent)
        {
            case KickIntentType.GroundPass:
            case KickIntentType.ThroughPass:
                return new KickProfile(PitchDeg: 6f + 4f * power, YawDeg: 18f + 8f * power, BankDeg: 6f,
                    FootSwing: 0.34f, FootLift: 0.06f, FootWhipSwing: 2.4f, FootWhipLift: 0.5f,
                    YawWhipDeg: 18f, BankWhipDeg: 8f, BodyRise: 0f, StretchWhip: 0f);
            case KickIntentType.DrivenPass:
                return new KickProfile(PitchDeg: 10f, YawDeg: 30f, BankDeg: 12f,
                    FootSwing: 0.40f, FootLift: 0.10f, FootWhipSwing: 3.2f, FootWhipLift: 0.7f,
                    YawWhipDeg: 24f, BankWhipDeg: 12f, BodyRise: 0f, StretchWhip: 0f);
            case KickIntentType.LoftedPass:
            case KickIntentType.LobbedThroughPass:
            case KickIntentType.Cross:
                return new KickProfile(PitchDeg: 16f + 6f * power, YawDeg: 32f, BankDeg: 15f,
                    FootSwing: 0.36f, FootLift: 0.24f, FootWhipSwing: 2.6f, FootWhipLift: 2.2f,
                    YawWhipDeg: 26f, BankWhipDeg: 14f, BodyRise: 0.02f, StretchWhip: 0.35f);
            case KickIntentType.Shot:
                return new KickProfile(PitchDeg: 10f + 16f * power, YawDeg: 38f + 12f * power, BankDeg: 18f,
                    FootSwing: 0.52f, FootLift: 0.14f, FootWhipSwing: 4.2f, FootWhipLift: 0.9f,
                    YawWhipDeg: 34f + 16f * power, BankWhipDeg: 18f,
                    BodyRise: power > 0.75f ? 0.06f : 0f, StretchWhip: power > 0.75f ? 0.6f : 0f);
            case KickIntentType.CurledShot:
                return new KickProfile(PitchDeg: 12f, YawDeg: 45f, BankDeg: 22f,
                    FootSwing: 0.44f, FootLift: 0.16f, FootWhipSwing: 3.4f, FootWhipLift: 1.4f,
                    YawWhipDeg: 30f, BankWhipDeg: 20f, BodyRise: 0f, StretchWhip: 0.2f);
            case KickIntentType.TrivelaShot:
                return new KickProfile(PitchDeg: 10f, YawDeg: 35f, BankDeg: 18f,
                    FootSwing: 0.30f, FootLift: 0.12f, FootWhipSwing: 2.8f, FootWhipLift: 0.8f,
                    YawWhipDeg: 26f, BankWhipDeg: 16f, BodyRise: 0f, StretchWhip: 0f);
            case KickIntentType.ChipShot:
                return new KickProfile(PitchDeg: 8f, YawDeg: 12f, BankDeg: 5f,
                    FootSwing: 0.26f, FootLift: 0.20f, FootWhipSwing: 2.0f, FootWhipLift: 1.8f,
                    YawWhipDeg: 10f, BankWhipDeg: 6f, BodyRise: 0f, StretchWhip: 0.15f);
            case KickIntentType.Clearance:
                return new KickProfile(PitchDeg: 24f, YawDeg: 45f, BankDeg: 20f,
                    FootSwing: 0.55f, FootLift: 0.28f, FootWhipSwing: 4.6f, FootWhipLift: 2.0f,
                    YawWhipDeg: 38f, BankWhipDeg: 22f, BodyRise: 0f, StretchWhip: 0.3f);
            default:
                return default;
        }
    }

    /// <summary>
    /// Contact frame: selects the profile, arms the sustained follow-through timer and fires the one-shot
    /// whip. The duration is the authority's own kick-recovery window, so the pose recovers exactly when
    /// the simulation lets the player kick again.
    /// </summary>
    private void ArmKickAction(FootballerState player, int kickSign, float power)
    {
        KickProfile profile = KickProfileFor(_pendingKickIntent, power);

        // The torso unwinds toward the kicking side (opposite the wind-up coil). A curled shot wraps in
        // the spin direction the controller already signed; a trivela is the outside-foot mirror.
        float yawSign = kickSign;
        if (_pendingKickIntent == KickIntentType.CurledShot)
        {
            float spin = player.LastCommand.RequestedCurveSpin;
            yawSign = MathF.Abs(spin) > 0.05f ? MathF.Sign(spin) : kickSign;
        }
        else if (_pendingKickIntent == KickIntentType.TrivelaShot)
        {
            yawSign = -kickSign;
        }

        // The plant leg carries the bank, so the body leans away from the kicking foot; a trivela banks
        // the other way because the contact is on the outside of the boot.
        float bankSign = _pendingKickIntent == KickIntentType.TrivelaShot ? kickSign : -kickSign;
        float effort = 0.65f + 0.45f * power;

        _actionKickDuration = SpringMath.Clamp(player.KickRecoveryTimer, 0.18f, 0.40f);
        _actionKickTimer = _actionKickDuration;
        _actionKickSign = kickSign;
        _actionKickPitch = SpringMath.DegToRad(profile.PitchDeg);
        _actionKickYaw = SpringMath.DegToRad(profile.YawDeg) * yawSign;
        _actionKickBank = SpringMath.DegToRad(profile.BankDeg) * bankSign;
        _actionKickFootSwing = profile.FootSwing;
        _actionKickFootLift = profile.FootLift;
        _actionKickRise = profile.BodyRise;

        ImpulseKickFoot(profile.FootWhipSwing * effort, profile.FootWhipLift * effort);
        ImpulseTorsoYaw(SpringMath.DegToRad(profile.YawWhipDeg) * effort * yawSign);
        ImpulseTorsoRoll(SpringMath.DegToRad(profile.BankWhipDeg) * effort * bankSign);
        // Vertical stretch is authored as peak deflection like the other one-shot amplitudes.
        if (profile.StretchWhip > 0f) SnapSquash(profile.StretchWhip * 0.12f * effort);
    }

    /// <summary>
    /// Sustained follow-through, held through the first ~60 % of the authority's recovery window and then
    /// faded out, so the strike pose is actually reached before it relaxes instead of decaying from frame 0.
    /// </summary>
    private void ApplyKickFollowThrough(ref ChannelTargets targets)
    {
        if (_actionKickTimer <= 0f) return;
        float fade = SpringMath.Clamp01(_actionKickTimer / _actionKickDuration);
        fade = MathF.Min(1f, fade * 2.5f);

        targets.TorsoPitch += _actionKickPitch * fade;
        targets.LeanPitch += _actionKickPitch * 0.45f * fade;
        targets.TorsoYaw += _actionKickYaw * fade;
        targets.LeanRoll += _actionKickBank * fade;
        targets.TorsoRoll += _actionKickBank * 0.35f * fade;
        targets.KickFootSide = _actionKickSign;
        targets.KickFootSwing += _actionKickFootSwing * fade;
        targets.KickFootLift += _actionKickFootLift * fade;
        targets.BodyOffsetY += _actionKickRise * fade;

        _actionKickTimer = MathF.Max(0f, _actionKickTimer - _dt);
    }

    /// <summary>
    /// Plan 5.5 wind-up: while the human holds the charge or an AI is closing the last metre onto the
    /// ball, the torso coils back toward the kicking side and the boot pulls behind the body. Smoothed by
    /// its own scalar so the coil is present before contact and releases in ~40 ms when the whip lands.
    /// </summary>
    private void UpdateKickWindUp(in PlayerDynamicsInput input, int kickSign, ref ChannelTargets targets)
    {
        bool winding = _actionKickTimer <= 0f && !_pendingKick && IsWindingUp(in input);
        _actionWindUp = SpringMath.ExpApproach(_actionWindUp, winding ? 1f : 0f, winding ? 0.07f : 0.04f, _dt);
        if (_actionWindUp <= 0.02f) return;

        FootballerCommand command = input.Player.LastCommand;
        float power = input.IsHumanControlled
            ? SpringMath.Clamp01(input.ChargedPower01)
            : MathF.Max(0.25f, SpringMath.Clamp01(command.KickPower01));
        float coil = _actionWindUp * power;

        targets.KickFootSide = kickSign;
        targets.KickFootSwing += -0.22f * coil;
        targets.TorsoYaw -= SpringMath.DegToRad(18f) * coil * kickSign;
        targets.TorsoRoll -= SpringMath.DegToRad(3f) * coil * kickSign;
        targets.LeanPitch -= SpringMath.DegToRad(3f) * _actionWindUp;
        targets.BodyOffsetY -= 0.03f * _actionWindUp;
        targets.SquashY -= 0.02f * _actionWindUp;
    }

    /// <summary>True while a kick is being anticipated: human charge, or an AI closing to within 1.2 m.</summary>
    private bool IsWindingUp(in PlayerDynamicsInput input)
    {
        if (input.IsHumanControlled) return input.ChargedPower01 > 0.05f;

        FootballerCommand command = input.Player.LastCommand;
        if (command.KickType == KickIntentType.None) return false;

        Vector2 toBall = new(
            input.BallPosition.X - input.Player.Position.X,
            input.BallPosition.Z - input.Player.Position.Z);
        float distance = toBall.Length();
        if (distance > 1.2f) return false;

        Vector2 direction = distance > 1e-4f ? toBall / distance : _facing;
        ToLocalAxes(direction, out float forward, out _);
        if (forward < 0.15f) return false;

        // "Closing it": either already inside the strike window or actively moving onto the ball, so an
        // AI jogging past a stationary ball does not coil as though about to strike it.
        Vector2 velocityXZ = new(input.Player.Velocity.X, input.Player.Velocity.Z);
        float closing = velocityXZ.X * toBall.X + velocityXZ.Y * toBall.Y;
        return distance <= 0.95f || closing > 0f;
    }

    /// <summary>
    /// Ball control side for every kick-family action. The preferred foot sets the side; an aim sharply
    /// across the body flips to the weak/outside foot (the same rule as the execution layer at
    /// <c>FootballerLocomotion.cs:1124-1128</c>).
    /// </summary>
    private int ResolveKickFootSign(FootballerState player)
    {
        int sign = PreferredFootSign(player);
        Vector3 aim = player.LastCommand.KickAimDirectionXZ;
        Vector2 aimXZ = new(aim.X, aim.Z);
        float lengthSquared = aimXZ.LengthSquared();
        if (lengthSquared <= 1e-6f) return sign;

        Vector2 unitAim = aimXZ / MathF.Sqrt(lengthSquared);
        ToLocalAxes(unitAim, out _, out float across);
        if (across * sign < -0.55f) sign = -sign;
        return sign;
    }

    // =========================================================================================
    // Tackle, dispossession stumble and body collision (plan 5.6)
    // =========================================================================================

    /// <summary>
    /// Plan 5.6 tackle: a steep lunge held for half a second, then released so the springs stand the player
    /// back up. Arm only on the rising edge, because <c>LastCommand.RequestTackle</c> may repeat for several
    /// ticks while the AI is committed to the challenge.
    /// </summary>
    private void ApplyTackle(in PlayerDynamicsInput input, ref ChannelTargets targets)
    {
        // A keeper's RequestTackle is a smothering sweep, not an outfielder's lunging tackle: the keeper
        // stage owns that silhouette and runs after this one.
        if (IsGoalkeeper) return;

        bool requested = _pendingTackle || input.Player.LastCommand.RequestTackle;
        if (requested && !_actionWasTackling) ArmTackle(in input);
        _actionWasTackling = requested;

        if (_actionTackleTimer <= 0f) return;

        // Hold the lunge, then ease the last 150 ms out so the springs push the player back upright. The pose is
        // deliberately shallow: from the broadcast camera a steep forward pitch reads as falling flat on the
        // pitch (owner feedback 2026-11-26), so the tackle is sold by the hip drop, the plant and the twist
        // rather than by a big body pitch.
        float hold = _actionTackleTimer > 0.15f ? 1f : _actionTackleTimer / 0.15f;
        targets.LeanPitch -= SpringMath.DegToRad(12f) * hold;
        targets.TorsoPitch -= SpringMath.DegToRad(6f) * hold;
        targets.BodyOffsetY -= 0.14f * hold;
        targets.BodyOffsetForward += 0.12f * hold;
        targets.TorsoYaw += SpringMath.DegToRad(12f) * _actionTackleTwistSign * hold;
        targets.LeanRoll += SpringMath.DegToRad(7f) * _actionTackleBankSign * hold;
        targets.KickFootSide = _actionTackleTwistSign;
        targets.KickFootSwing += 0.22f * hold;
        targets.KickFootLift += 0.06f * hold;
        targets.HandReach += new Vector3(0.03f, 0.01f, 0.08f) * hold;

        _actionTackleTimer = MathF.Max(0f, _actionTackleTimer - _dt);
    }

    /// <summary>One-shot lunge impulse. The twist follows the tackling (preferred) foot; the bank leans into the contact.</summary>
    private void ArmTackle(in PlayerDynamicsInput input)
    {
        int footSign = PreferredFootSign(input.Player);
        _actionTackleTwistSign = footSign;
        _actionTackleBankSign = footSign;
        if (input.NearestOpponentDistance > 0f)
        {
            ToLocalAxes(input.NearestOpponentDirectionXZ, out _, out float opponentRight);
            _actionTackleBankSign = opponentRight >= 0f ? 1 : -1;
        }

        _actionTackleTimer = TackleHoldSeconds;
        // Peak amplitudes: the tackle already holds a sustained low pose, so the contact only adds a snap on top
        // of it instead of doubling the pose angles (and the pitch stays shallow for broadcast readability).
        SnapLeanPitch(SpringMath.DegToRad(-4f));
        SnapTorsoPitch(SpringMath.DegToRad(-3f));
        SnapTorsoYaw(SpringMath.DegToRad(6f) * _actionTackleTwistSign);
        SnapTorsoRoll(SpringMath.DegToRad(4f) * _actionTackleBankSign);
        SnapSquash(-0.05f);
        SnapBodyOffsetY(-0.03f);
        SnapBodyOffsetXZ(0.05f, 0f);
        SnapKickFoot(0.18f, 0.08f);
        SnapHandReach(new Vector3(0.01f, 0.01f, 0.02f));
    }

    /// <summary>
    /// Dispossession wobble and shoulder-bump sub-stage. Both are detected from authoritative evidence
    /// (balance drop / recovery jump, and a collision-sized acceleration beside an opponent), never from a
    /// guessed event, and both are rate-limited so one incident is one knock.
    /// </summary>
    private void ApplyStumbleAndBump(in PlayerDynamicsInput input, ref ChannelTargets targets)
    {
        FootballerState player = input.Player;
        if (_actionStumbleCooldown > 0f) _actionStumbleCooldown -= _dt;
        if (_actionBumpCooldown > 0f) _actionBumpCooldown -= _dt;

        // Plan 5.6: the dispossession signature is a sharp balance loss, or KickRecovery jumping to >=0.5 s
        // without a kick contact on this tick (the exact pair the execution layer writes on a lost ball).
        float balanceDelta = player.Balance01 - _previousBalance;
        bool lostRecoveryWithoutContact =
            player.KickRecoveryTimer >= 0.5f && _previousKickRecovery < 0.5f && !_pendingKick;
        if (_actionStumbleCooldown <= 0f && (balanceDelta < -0.20f || lostRecoveryWithoutContact))
            ArmStumble(in input);

        if (_actionStumbleTimer > 0f)
        {
            float fade = SpringMath.Clamp01(_actionStumbleTimer / StumbleSeconds);
            float age = StumbleSeconds - _actionStumbleTimer;
            targets.TorsoPitch -= SpringMath.DegToRad(3f) * _actionStumbleAwayForward * fade;
            targets.TorsoRoll += SpringMath.DegToRad(3f) * _actionStumbleAwayRight * fade;
            targets.TorsoRoll += SpringMath.DegToRad(2f) * MathF.Sin(age * 16f) * fade;
            targets.BodyOffsetForward += 0.03f * _actionStumbleAwayForward * fade;
            targets.BodyOffsetRight += 0.03f * _actionStumbleAwayRight * fade;
            _actionStumbleTimer = MathF.Max(0f, _actionStumbleTimer - _dt);
        }

        // Arms flare while the balance model says the player is still recovering.
        if (player.Balance01 < 0.75f)
        {
            float flare = SpringMath.Clamp01((0.75f - player.Balance01) / 0.35f);
            targets.HandReach += new Vector3(0.02f, 0.06f, 0.16f) * flare;
        }

        TryBodyBump(in input);
    }

    /// <summary>
    /// Dispossession knock: an impulse away from the nearest opponent. The torso springs are already
    /// underdamped (zeta 0.55/0.72), so the knock reads as a wobble rather than a pose change.
    /// </summary>
    private void ArmStumble(in PlayerDynamicsInput input)
    {
        _actionStumbleTimer = StumbleSeconds;
        _actionStumbleCooldown = StumbleCooldownSeconds;

        float awayForward = -1f;
        float awayRight = 0f;
        if (input.NearestOpponentDistance > 0f)
        {
            Vector2 away = -input.NearestOpponentDirectionXZ;
            ToLocalAxes(away, out awayForward, out awayRight);
        }
        _actionStumbleAwayForward = awayForward;
        _actionStumbleAwayRight = awayRight;

        // Peak amplitudes are modest for the same reason as the tackle: from the broadcast camera a large
        // forward/backward pitch reads as going to ground rather than losing balance.
        SnapTorsoPitch(SpringMath.DegToRad(12f) * -awayForward);
        SnapLeanPitch(SpringMath.DegToRad(6f) * -awayForward);
        SnapTorsoRoll(SpringMath.DegToRad(10f) * awayRight);
        SnapBodyOffsetXZ(awayForward * 0.06f, awayRight * 0.06f);
        SnapBodyOffsetY(-0.05f);
    }

    /// <summary>
    /// Plan 5.6 shoulder bump: only a real capsule separation can produce this much lateral acceleration,
    /// so a spike while an opponent is inside the pair radius is a collision. The acceleration direction is
    /// the separation direction, so it doubles as the jolt direction; a short cooldown keeps it one-to-one.
    ///
    /// Both axes use one physical convention — the torso whips AGAINST the push (inertia lags the new velocity),
    /// so a push to the player's right rolls him left and a push forward pitches him back.
    /// </summary>
    private void TryBodyBump(in PlayerDynamicsInput input)
    {
        if (_actionBumpCooldown > 0f) return;
        float distance = input.NearestOpponentDistance;
        if (distance <= 0.01f || distance >= 0.95f) return;

        float accelerationSquared = _acceleration.LengthSquared();
        if (accelerationSquared <= BumpAccelerationThreshold * BumpAccelerationThreshold) return;

        float inverseMagnitude = 1f / MathF.Sqrt(accelerationSquared);
        float forward = _accelerationForward * inverseMagnitude;
        float right = _accelerationRight * inverseMagnitude;

        _actionBumpCooldown = BumpCooldownSeconds;
        SnapTorsoRoll(SpringMath.DegToRad(-18f) * right);
        SnapTorsoPitch(SpringMath.DegToRad(12f) * forward);
        SnapBodyOffsetXZ(0.06f * forward, 0.06f * right);
    }

    // =========================================================================================
    // Small shared helpers
    // =========================================================================================

    private static int PreferredFootSign(FootballerState player) =>
        player.Attributes.PreferredFoot == Footedness.Left ? -1 : 1;

    /// <summary>
    /// Clears every action-stage timer and edge detector. Called from the core's single teleport/reset path so a
    /// kick, jump or tackle in flight can never be replayed across a kickoff/restart snap.
    /// </summary>
    private void ResetActionState(in PlayerDynamicsInput input)
    {
        _actionWasJumping = false;
        _actionWasAirborne = false;
        _actionLastAirborneVelocityY = 0f;
        _actionHeaderTimer = 0f;
        _actionKickTimer = 0f;
        _actionKickDuration = 1f;
        _actionKickSign = 1;
        _actionKickPitch = 0f;
        _actionKickYaw = 0f;
        _actionKickBank = 0f;
        _actionKickFootSwing = 0f;
        _actionKickFootLift = 0f;
        _actionKickRise = 0f;
        _actionWindUp = 0f;
        _actionWasTackling = false;
        _actionTackleTimer = 0f;
        _actionStumbleTimer = 0f;
        _actionStumbleCooldown = 0f;
        _actionStumbleAwayForward = -1f;
        _actionStumbleAwayRight = 0f;
        _actionBumpCooldown = 0f;
    }
}
