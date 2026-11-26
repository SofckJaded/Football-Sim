using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Rules;

namespace FootballSim.Presentation;

/// <summary>
/// Goalkeeper stage (phase P4 of the plan): athletic ready stance, pre-dive coil, height-tiered directional
/// dives with a full lateral/forward body roll, turf-impact bounce and get-up, reflex parry recoil, cross
/// claim / punch clear leaps, clean-catch cradle, 1v1 spread-block crouch, distribution follow-through and
/// holding-ball stance.
///
/// Only runs when <see cref="IsGoalkeeper"/> is true and the keeper stage is enabled. It overrides the
/// locomotion stage's lean through <see cref="ChannelTargets.DiveBlend01"/>, which is how a dive outranks
/// normal running bank without either stage having to know the other.
///
/// <b>Dive trajectory ownership.</b> A dive is a simulation fact (<c>IsDiving</c> plus the 0.55 s
/// <see cref="BallContactRules.KeeperDiveSeconds"/> burst) with no authoritative vertical motion, so every
/// metre of dive height and every degree of dive roll on screen is authored here: three tiers keyed on the
/// shot's intercept height (low-corner skid, mid-height airborne stretch, top-corner leap), a one-shot turf
/// impact, then a damped get-up. No wall clock, no per-frame allocation, no simulation writes.
/// </summary>
public sealed partial class PlayerDynamicsSolver
{
    // --- Tuning: stance, coil, dive tiers ----------------------------------------------------
    private const float KeeperStanceCrouch = -0.10f; // low enough to read as athletic, high enough to move
    private const float KeeperCoilCrouch = -0.18f;   // pre-dive coil: another 8 cm of sink to telegraph
    private const float KeeperStanceSquash = -0.04f, KeeperCoilSquash = -0.08f;

    private static readonly float KeeperStanceTorsoPitch = SpringMath.DegToRad(-10f);
    private static readonly float KeeperCoilTorsoPitch = SpringMath.DegToRad(-14f);

    private const float KeeperLowDiveMaxHeight = 0.75f; // below: going down to the grass (corner save)
    private const float KeeperTopDiveMinHeight = 1.55f; // above: leaving the ground for the top corner
    private const float KeeperDiveMinHeight = 0.15f, KeeperDiveMaxHeight = 2.5f;
    private const float KeeperDiveDuration = BallContactRules.KeeperDiveSeconds; // execution's own dive clock

    private static readonly float KeeperLowDiveRoll = SpringMath.DegToRad(82f), KeeperLowDivePitch = SpringMath.DegToRad(65f);
    private static readonly float KeeperMidDiveRoll = SpringMath.DegToRad(80f), KeeperMidDivePitch = SpringMath.DegToRad(50f);

    /// <summary>
    /// Pivot lift of a fully horizontal dive. The body roll alone already swings the torso down from ~1.05 m to
    /// ~0.15 m because the pivot is at the feet, so the vertical arc must settle at a POSITIVE lift to leave the
    /// keeper's torso just above the turf instead of through it.
    /// </summary>
    private const float KeeperHorizontalPivotLift = 0.22f;

    private const float KeeperCatchSeconds = 0.45f, KeeperClaimSeconds = 0.60f, KeeperPunchSeconds = 0.40f;
    private const float KeeperParrySeconds = 0.25f, KeeperDistributionSeconds = 0.50f;
    private const float KeeperClaimLandingProgress = 0.62f, KeeperDiveBlendHalfLife = 0.055f;

    // --- Keeper-owned state (simulated time only) --------------------------------------------
    private bool _wasDiving;          // previous frame's dive flag: launch / landing edges
    private float _diveElapsed;       // seconds in the current dive; every tier curve is f(this)
    private float _diveRight;         // dive direction on the facing frame: +1 = fully to the right
    private float _diveForward;       // dive direction on the facing frame: +1 = at the striker
    private float _targetHeight = 1f; // absolute intercept height: selects the dive tier
    private bool _turfImpactDone;     // the turf-impact impulse has fired for this dive
    private float _diveBlend;         // smoothed 0..1 hand-over from locomotion lean to dive roll

    private float _keeperGetUpTimer, _keeperGetUpSeconds = 0.45f;
    private float _keeperLastDiveRoll, _keeperLastDivePitch, _keeperLastDiveBodyY;
    private float _keeperCatchTimer, _keeperClaimTimer, _keeperPunchTimer, _keeperParryTimer;
    private bool _keeperClaimLandingDone;
    private float _keeperDistributionTimer;
    private bool _keeperDistributionLong, _keeperWasDistributing;
    private KickIntentType _keeperLastDistributionKick;
    private bool _keeperHeldLastFrame;

    /// <summary>
    /// Phase P4 keeper stage entry point. Resolves the keeper's situation (dive, get-up, holding, block,
    /// distribution, ready stance), layers the pose, then lets the short touch envelopes overlay their
    /// absorption / recoil. A dive outranks everything because <c>IsDiving</c> is the authoritative
    /// execution fact; the get-up outranks a new stance so the keeper cannot snap upright the frame the
    /// dive timer expires.
    /// </summary>
    private void ApplyGoalkeeperDynamics(in PlayerDynamicsInput input, ref ChannelTargets targets)
    {
        bool diving = input.Player.IsDiving;
        UpdateKeeperClocks(in input, diving);
        ConsumeKeeperTouchEvents(in input);

        if (diving)
        {
            ApplyKeeperDive(ref targets);
        }
        else if (_keeperGetUpTimer > 0f)
        {
            ApplyKeeperGetUp(in input, ref targets);
        }
        else if (IsKeeperHolding(in input))
        {
            ApplyKeeperHoldingStance(ref targets);
        }
        else
        {
            if (input.Player.LastCommand.RequestTackle)
                ApplyKeeperBlockCrouch(ref targets);
            else
                ApplyKeeperReadyStance(in input, ref targets);

            if (ShouldKeeperCoil(in input))
                ApplyKeeperPreDiveCoil(in input, ref targets);

            ApplyKeeperDistributionFollowThrough(in input, ref targets);
        }

        ApplyKeeperTouchHoldTargets(ref targets);
        UpdateKeeperDiveBlend(diving, ref targets);
    }

    // --- Clocks and edges --------------------------------------------------------------------

    /// <summary>
    /// Advances every keeper timer by simulated <c>_dt</c> and handles the dive edges: launch parameters are
    /// captured the first frame <c>IsDiving</c> rises, and the get-up curve starts the frame it falls.
    /// Edge detection is on the authoritative flag, so a paused or slow-mo frame cannot double-fire either.
    /// </summary>
    private void UpdateKeeperClocks(in PlayerDynamicsInput input, bool diving)
    {
        if (diving && !_wasDiving)
        {
            CaptureKeeperDiveLaunch(in input);
            _diveElapsed = 0f;
            _turfImpactDone = false;
            // A dive is a burst from standing, not a run: a short pop plus vertical stretch sells the
            // launch before the tier arc takes over.
            SnapBodyOffsetY(0.08f + 0.04f * SpringMath.Clamp01(_targetHeight / 2f));
            SnapSquash(0.07f);
        }
        else if (!diving && _wasDiving)
        {
            StartKeeperGetUp(in input);
        }

        if (diving) _diveElapsed += _dt;

        _keeperCatchTimer = MathF.Max(0f, _keeperCatchTimer - _dt);
        _keeperClaimTimer = MathF.Max(0f, _keeperClaimTimer - _dt);
        _keeperPunchTimer = MathF.Max(0f, _keeperPunchTimer - _dt);
        _keeperParryTimer = MathF.Max(0f, _keeperParryTimer - _dt);
        _keeperDistributionTimer = MathF.Max(0f, _keeperDistributionTimer - _dt);
        if (_keeperGetUpTimer > 0f) _keeperGetUpTimer = MathF.Max(0f, _keeperGetUpTimer - _dt);

        _wasDiving = diving;
    }

    /// <summary>
    /// Projects the dive vector onto the keeper's facing frame and reads the shot height out of it.
    /// <c>KeeperDiveDirection</c> is already <c>interceptPoint - selfPosition</c> (the rules engine builds it
    /// that way), so its Y is the intercept height above the keeper's feet, not a direction to normalize.
    /// </summary>
    private void CaptureKeeperDiveLaunch(in PlayerDynamicsInput input)
    {
        FootballerState player = input.Player;
        Vector3 diveVector = player.LastCommand.KeeperDiveDirection;
        Vector2 diveXZ = new(diveVector.X, diveVector.Z);
        if (diveXZ.LengthSquared() < 1e-6f)
            diveXZ = new Vector2(player.Velocity.X, player.Velocity.Z);
        diveXZ = SafeNormalize(diveXZ, _facing);
        ToLocalAxes(diveXZ, out _diveForward, out _diveRight);

        _targetHeight = SpringMath.Clamp(
            diveVector.Y + player.Position.Y, KeeperDiveMinHeight, KeeperDiveMaxHeight);
        _keeperLastDiveRoll = 0f;
        _keeperLastDivePitch = 0f;
        _keeperLastDiveBodyY = 0f;
    }

    /// <summary>
    /// Starts the damped get-up. A parry leaves the execution layer a <c>KickRecoveryTimer</c> (0.75 s) that
    /// the visual rise must not outrun, so the recovery is stretched toward it - capped at the plan's 0.55 s
    /// so the keeper never looks frozen on the turf.
    /// </summary>
    private void StartKeeperGetUp(in PlayerDynamicsInput input)
    {
        _keeperGetUpSeconds = _targetHeight < KeeperLowDiveMaxHeight
            ? 0.52f
            : _targetHeight <= KeeperTopDiveMinHeight ? 0.45f : 0.50f;
        if (input.Player.KickRecoveryTimer > _keeperGetUpSeconds)
            _keeperGetUpSeconds = SpringMath.Clamp(
                input.Player.KickRecoveryTimer * 0.70f, _keeperGetUpSeconds, 0.55f);

        _keeperGetUpTimer = _keeperGetUpSeconds;
        // The turf rebounds once under him as the dive ends.
        SnapSquash(0.05f);
        SnapBodyOffsetY(0.07f);
    }

    // --- One-shot touch events (parry / punch / catch / claim) -------------------------------

    /// <summary>
    /// Turns the one-shot contact flags into velocity injections and short envelopes. Punch is tested first
    /// because a punch clear is also reported as a parry contact; letting the explicit event win stops it
    /// double-firing. The hold rising edge is a fallback for a clean catch whose visual event is not routed,
    /// and is suppressed while a catch envelope is live so one catch never squashes twice.
    /// </summary>
    private void ConsumeKeeperTouchEvents(in PlayerDynamicsInput input)
    {
        bool punched = false;
        bool parried = false;

        if (_pendingKeeperEvent == KeeperVisualEvent.PunchClear)
        {
            punched = true;
            _keeperPunchTimer = KeeperPunchSeconds;
            SnapSquash(0.10f);                                 // stretch up through the punch
            SnapHandReach(new Vector3(0.02f, 0.45f, 0.06f));   // double fist drives overhead (peak metres)
            SnapTorsoPitch(SpringMath.DegToRad(9f));           // chest recoils backward
            if (_rootHeight < 0.12f) SnapBodyOffsetY(0.35f);   // plan 5.7.4: leap about +0.35 m, from the ground
        }
        else if (_pendingKeeperEvent == KeeperVisualEvent.ReflexParry || (_pendingSave && _pendingParry))
        {
            parried = true;
            _keeperParryTimer = KeeperParrySeconds;
            Vector2 shotXZ = new(input.BallVelocity.X, input.BallVelocity.Z);
            Vector2 shotDirection = SafeNormalize(shotXZ, Vector2.Zero);
            ToLocalAxes(shotDirection, out float shotForward, out float shotRight);
            // The gloves shove through the ball, so their impulse is opposite the shot's travel; the body is
            // torqued the same way, away from the shot - that is what sells a 28 m/s ball on the palms.
            SnapHandReach(new Vector3(-shotForward * 0.18f, 0.10f, -shotRight * 0.25f));
            SnapTorsoRoll(SpringMath.DegToRad(-shotRight * 16f));
            SnapLeanRoll(SpringMath.DegToRad(-shotRight * 10f));
            SnapTorsoPitch(SpringMath.DegToRad(7f));
        }

        bool cleanCatch = _pendingKeeperEvent == KeeperVisualEvent.CleanCatch;
        bool crossClaim = _pendingKeeperEvent == KeeperVisualEvent.CrossClaim;
        bool holdBegan = input.HoldsBall && !_keeperHeldLastFrame && _keeperCatchTimer <= 0f;
        if (!punched && !parried && (cleanCatch || crossClaim || holdBegan))
            BeginKeeperCatch(crossClaim);

        _keeperHeldLastFrame = input.HoldsBall;
    }

    /// <summary>
    /// A clean catch kills the ball's momentum through the body: the squash absorbs it, the chest folds in
    /// around it, and the gloves close behind. A claim also gets the high-point leap, and its envelope fires
    /// the cushioned landing squash later.
    /// </summary>
    private void BeginKeeperCatch(bool crossClaim)
    {
        _keeperCatchTimer = KeeperCatchSeconds;
        SnapSquash(-0.09f); // plan 5.7.5: soft absorption squash
        SnapTorsoPitch(SpringMath.DegToRad(-8f)); // chest folds in around the ball
        SnapHandReach(new Vector3(0.08f, 0.02f, -0.03f));

        if (!crossClaim) return;
        _keeperClaimTimer = KeeperClaimSeconds;
        _keeperClaimLandingDone = false;
        if (_rootHeight < 0.12f) SnapBodyOffsetY(0.30f); // plan 5.7.5: high-point leap to gather the cross
        SnapSquash(0.05f);
    }

    // --- The dive itself: three height tiers -------------------------------------------------

    /// <summary>Dispatches the dive curve for the captured intercept height and stores the settled pose.</summary>
    private void ApplyKeeperDive(ref ChannelTargets targets)
    {
        float u = SpringMath.Clamp01(_diveElapsed / KeeperDiveDuration);
        if (_targetHeight < KeeperLowDiveMaxHeight)
            ApplyKeeperLowDive(u, ref targets);
        else if (_targetHeight <= KeeperTopDiveMinHeight)
            ApplyKeeperMidDive(u, ref targets);
        else
            ApplyKeeperTopDive(u, ref targets);

        // The get-up unwinds out of exactly the pose the dive ended in, so the keeper never snaps from
        // horizontal to upright when the execution timer expires.
        _keeperLastDiveRoll = targets.DiveRoll;
        _keeperLastDivePitch = targets.DivePitch;
        _keeperLastDiveBodyY = targets.BodyOffsetY;

        // Neck stays level against the body's shallow forward fold (the gaze stage may refine it further).
        targets.HeadPitch += SpringMath.DegToRad(6f) * _settings.MotionIntensity;
    }

    /// <summary>
    /// Low corner: the keeper goes down rather than up. The roll snaps to 82 deg and carries the torso to the
    /// grass; the pivot arc settles at a small positive lift by u=0.85 so the torso stays just above the turf.
    /// The gloves shoot "overhead" in body-local space, which points laterally at the post once rolled.
    /// </summary>
    private void ApplyKeeperLowDive(float u, ref ChannelTargets targets)
    {
        float reach = KeeperReachEnvelope(u, 0.20f, 0.75f, 0.25f);
        float rise = SpringMath.SmoothStep01(u / 0.45f);
        float fall = SpringMath.SmoothStep01((u - 0.45f) / 0.40f);

        targets.DiveRoll = _diveRight * KeeperLowDiveRoll * rise;
        targets.DivePitch = -_diveForward * KeeperLowDivePitch * rise;
        // The roll already carries the torso down to the turf; the pivot only lifts it clear of the grass.
        targets.BodyOffsetY = SpringMath.Lerp(0.12f * rise, KeeperHorizontalPivotLift, fall);
        targets.SquashY = 0.10f * reach; // slight shrug-stretch while reaching
        targets.HandReach = new Vector3(0.20f * reach, 0.52f * reach, 0.14f * reach);
        targets.TorsoPitch += SpringMath.DegToRad(-4f) * reach;
    }

    /// <summary>
    /// Mid height: a parabolic airborne arc (sin(pi*u) * 0.45 m) settling to the horizontal pivot lift at turf
    /// contact around u~0.72, with a single damped impact impulse at touchdown. Roll peaks at 80 deg mid-air.
    /// </summary>
    private void ApplyKeeperMidDive(float u, ref ChannelTargets targets)
    {
        float reach = KeeperReachEnvelope(u, 0.22f, 0.72f, 0.16f);
        float rollRise = SpringMath.SmoothStep01(u / 0.50f);
        float airborne = MathF.Sin(SpringMath.Pi * u) * 0.45f;
        float landing = SpringMath.SmoothStep01((u - 0.72f) / 0.13f);

        targets.DiveRoll = _diveRight * KeeperMidDiveRoll * rollRise;
        targets.DivePitch = -_diveForward * KeeperMidDivePitch * rollRise;
        targets.BodyOffsetY = SpringMath.Lerp(airborne, KeeperHorizontalPivotLift, landing);
        targets.SquashY = 0.08f * reach * (1f - landing);
        targets.HandReach = new Vector3(0.30f * reach, 0.46f * reach, 0.16f * reach);
        targets.TorsoPitch += SpringMath.DegToRad(-3f) * reach;

        TryKeeperTurfImpact(u, 0.72f, 1.15f);
    }

    /// <summary>
    /// Top corner: an explosive leap whose apex scales with the intercept height (0.55-0.95 m), roll arcing
    /// through 68-78 deg with maximum overhead glove extension past the head, then a hip/shoulder landing
    /// at u~0.86.
    /// </summary>
    private void ApplyKeeperTopDive(float u, ref ChannelTargets targets)
    {
        float leap = SpringMath.Clamp((_targetHeight - 0.9f) * 0.65f, 0.55f, 0.95f);
        float peakRoll = SpringMath.Lerp(SpringMath.DegToRad(68f), SpringMath.DegToRad(78f),
            SpringMath.Clamp01((_targetHeight - KeeperTopDiveMinHeight) / 0.85f));
        float reach = KeeperReachEnvelope(u, 0.25f, 0.88f, 0.12f);
        float rollRise = SpringMath.SmoothStep01(u / 0.55f);
        float airborne = MathF.Sin(SpringMath.Pi * u) * leap;
        float landing = SpringMath.SmoothStep01((u - 0.86f) / 0.14f);

        targets.DiveRoll = _diveRight * peakRoll * rollRise;
        targets.DivePitch = -_diveForward * KeeperMidDivePitch * rollRise;
        targets.BodyOffsetY = SpringMath.Lerp(airborne, KeeperHorizontalPivotLift, landing);
        targets.SquashY = 0.12f * reach * (1f - landing);
        targets.HandReach = new Vector3(0.12f * reach, 0.72f * reach, 0.20f * reach);
        targets.TorsoPitch += SpringMath.DegToRad(-5f) * reach;

        TryKeeperTurfImpact(u, 0.86f, 0.95f);
    }

    /// <summary>Fires the crisp damped turf impact once, the frame the dive first reaches its landing phase.</summary>
    private void TryKeeperTurfImpact(float u, float impactU, float strength)
    {
        if (_turfImpactDone || u < impactU) return;
        _turfImpactDone = true;
        // Peak amplitudes: the turf absorbs the keeper's weight once, then the underdamped springs rebound.
        SnapSquash(-0.16f * strength);
        SnapBodyOffsetY(-0.07f * strength);
        SnapTorsoPitch(SpringMath.DegToRad(-6f));
    }

    // --- Post-dive get-up --------------------------------------------------------------------

    /// <summary>
    /// Damped get-up over <c>_keeperGetUpSeconds</c>: the dive roll and pitch unwind to zero while the body
    /// rises, the torso pushes up from a folded -20 deg, and a forward surge carries the hips under the body.
    /// A keeper holding the ball hugs it in; otherwise the gloves push off the turf.
    /// </summary>
    private void ApplyKeeperGetUp(in PlayerDynamicsInput input, ref ChannelTargets targets)
    {
        float seconds = MathF.Max(0.01f, _keeperGetUpSeconds);
        float progress = SpringMath.Clamp01(1f - _keeperGetUpTimer / seconds);
        float ease = SpringMath.SmoothStep01(progress);
        float remaining = 1f - ease;

        targets.DiveRoll = _keeperLastDiveRoll * remaining;
        targets.DivePitch = _keeperLastDivePitch * remaining;
        targets.BodyOffsetY = SpringMath.Lerp(_keeperLastDiveBodyY, -0.04f, ease);
        targets.TorsoPitch += SpringMath.DegToRad(-20f) * remaining;
        targets.SquashY += -0.02f * remaining;
        targets.BodyOffsetForward = 0.06f * MathF.Sin(SpringMath.Pi * progress);

        if (IsKeeperHolding(in input))
            targets.HandReach = new Vector3(0.10f, 0.05f, -0.10f); // ball hugged into the chest
        else
            targets.HandReach = new Vector3(0.18f * remaining, -0.14f * remaining, 0.12f * remaining);
    }

    // --- Standing poses: holding, ready stance, coil, block, distribution --------------------

    /// <summary>
    /// Holding the ball: upright and authoritative, ball cradled at chest height with a slow waist scan for
    /// an outlet. No crouch - a keeper with the ball in his hands owns the moment.
    /// </summary>
    private void ApplyKeeperHoldingStance(ref ChannelTargets targets)
    {
        targets.BodyOffsetY += 0.02f;
        targets.SquashY += 0.01f;
        targets.TorsoPitch += SpringMath.DegToRad(-2f);
        targets.TorsoYaw += SpringMath.DegToRad(3.5f) * MathF.Sin(_simTime * 0.9f);
        targets.HandReach += new Vector3(0.22f, 0.10f, 0.10f);
    }

    /// <summary>
    /// Set / angle-bisecting stance: athletic crouch, chest folded 10 deg forward, gloves forward and ready at
    /// chest/waist height tracking the ball's elevation, and a low-amplitude lateral shuffle (minimal vertical
    /// bounce) while the ball is moving. The crouch fades as the keeper sprints, so a sweep does not drag a
    /// ready stance with it.
    /// </summary>
    private void ApplyKeeperReadyStance(in PlayerDynamicsInput input, ref ChannelTargets targets)
    {
        float stanceScale = 1f - 0.60f * SpringMath.Clamp01(_speedXZ / 4.5f);

        targets.BodyOffsetY += KeeperStanceCrouch * stanceScale;
        targets.SquashY += KeeperStanceSquash * stanceScale;
        targets.TorsoPitch += KeeperStanceTorsoPitch * stanceScale;

        float ballHeight = input.BallPosition.Y - input.Player.Position.Y;
        float elevation = SpringMath.Clamp((ballHeight - 1.30f) * 0.14f, -0.14f, 0.22f);
        targets.HandReach += new Vector3(0.30f * stanceScale, -0.05f + elevation, 0.34f * stanceScale);
        targets.HeadPitch += SpringMath.Clamp(elevation * 0.9f, -0.22f, 0.30f) * _settings.MotionIntensity;

        float ballSpeed = SpringMath.HorizontalLength(input.BallVelocity);
        float lateralSpeed = MathF.Abs(_velocityRight);
        float shuffle = ballSpeed > 1f ? MathF.Max(0.35f, SpringMath.Clamp01(lateralSpeed / 2f)) : 0.12f;
        float phase = _simTime * (7f + MathF.Min(lateralSpeed, 4f) * 1.1f);
        float step = MathF.Sin(phase);
        // Foot channels are physical metres: the core applies the global MotionIntensity once at compose time.
        targets.LeftFootSwing += step * 0.05f * shuffle;
        targets.RightFootSwing += -step * 0.05f * shuffle;
        targets.LeftFootLift += MathF.Max(0f, -step) * 0.05f * shuffle;
        targets.RightFootLift += MathF.Max(0f, step) * 0.05f * shuffle;
        targets.BodyOffsetY += MathF.Abs(MathF.Sin(phase * 2f)) * 0.008f * shuffle;
    }

    /// <summary>
    /// True while the keeper is in the last beat before a launch: the decision state says he is going for a
    /// shot, or a fast ball is genuinely closing on him. Distance and closing speed are read from the
    /// authoritative ball, never guessed from animation state.
    /// </summary>
    private bool ShouldKeeperCoil(in PlayerDynamicsInput input)
    {
        if (input.GoalkeeperState == GoalkeeperState.Saving) return true;

        Vector3 toBall = input.BallPosition - input.Player.Position;
        float distance = SpringMath.HorizontalLength(toBall);
        if (distance < 0.9f || distance > 12f) return false;
        Vector2 ballToKeeper = new(
            input.Player.Position.X - input.BallPosition.X,
            input.Player.Position.Z - input.BallPosition.Z);
        Vector2 closingDirection = SafeNormalize(ballToKeeper, Vector2.Zero);
        float closingSpeed =
            input.BallVelocity.X * closingDirection.X + input.BallVelocity.Z * closingDirection.Y;
        return closingSpeed > 8f;
    }

    /// <summary>
    /// Pre-dive coil layered on the ready stance: another 8 cm of sink, tighter squash, more fold and an
    /// aggressive forward lean. The planned dive direction tilts the coil toward the corner he is about to
    /// attack, so the launch is telegraphed before <c>IsDiving</c> rises.
    /// </summary>
    private void ApplyKeeperPreDiveCoil(in PlayerDynamicsInput input, ref ChannelTargets targets)
    {
        targets.BodyOffsetY += KeeperCoilCrouch - KeeperStanceCrouch;
        targets.SquashY += KeeperCoilSquash - KeeperStanceSquash;
        targets.TorsoPitch += KeeperCoilTorsoPitch - KeeperStanceTorsoPitch;
        targets.LeanPitch += SpringMath.DegToRad(-8f);

        Vector3 plannedDive = input.Player.LastCommand.KeeperDiveDirection;
        Vector2 plannedXZ = new(plannedDive.X, plannedDive.Z);
        float plannedRight = 0f;
        if (plannedXZ.LengthSquared() > 1e-6f)
        {
            plannedXZ = SafeNormalize(plannedXZ, _facing);
            ToLocalAxes(plannedXZ, out float plannedForward, out plannedRight);
            targets.BodyOffsetForward += plannedForward * 0.03f;
        }

        targets.LeanRoll += SpringMath.DegToRad(plannedRight * 6f);
        targets.BodyOffsetRight += plannedRight * 0.03f;
        targets.HandReach += new Vector3(0f, -0.05f, plannedRight * 0.10f);
    }

    /// <summary>
    /// 1v1 spread block: the command's tackle request only exists inside ~1.3 m of the striker, so this is a
    /// true last-ditch posture - sink to -0.25 m, compress, lean in, and throw both gloves wide to close the
    /// shooting angle.
    /// </summary>
    private void ApplyKeeperBlockCrouch(ref ChannelTargets targets)
    {
        targets.BodyOffsetY += -0.25f;
        targets.SquashY += -0.06f;
        targets.TorsoPitch += SpringMath.DegToRad(-12f);
        targets.LeanPitch += SpringMath.DegToRad(-7f);
        targets.HandReach += new Vector3(0.28f, 0.02f, 0.55f);
    }

    /// <summary>
    /// Distribution follow-through. The kick whip belongs to the action stage; these are keeper-only body
    /// extras on a short swelled envelope: a bowling action for a ground ball, a full punt lean-back with a
    /// hip sweep for a long ball. A rising edge on the executed kick keeps the envelope one-shot.
    /// </summary>
    private void ApplyKeeperDistributionFollowThrough(in PlayerDynamicsInput input, ref ChannelTargets targets)
    {
        KickIntentType kick = input.Player.LastCommand.KickType;
        bool distributing = input.GoalkeeperState == GoalkeeperState.Distribution && kick != KickIntentType.None;
        bool newRelease = distributing && (!_keeperWasDistributing || kick != _keeperLastDistributionKick);
        _keeperWasDistributing = distributing;
        _keeperLastDistributionKick = kick;

        if (newRelease)
        {
            _keeperDistributionTimer = KeeperDistributionSeconds;
            _keeperDistributionLong = kick is KickIntentType.LoftedPass or KickIntentType.LobbedThroughPass
                or KickIntentType.Cross or KickIntentType.Clearance;
        }
        if (_keeperDistributionTimer <= 0f) return;

        float progress = SpringMath.Clamp01(1f - _keeperDistributionTimer / KeeperDistributionSeconds);
        float envelope = MathF.Sin(SpringMath.Pi * progress);

        if (_keeperDistributionLong)
        {
            targets.LeanPitch += SpringMath.DegToRad(10f) * envelope;
            targets.TorsoPitch += SpringMath.DegToRad(13f) * envelope;
            targets.TorsoYaw += SpringMath.DegToRad(-7f) * envelope;
            targets.BodyOffsetY += 0.02f * envelope;
            targets.HandReach += new Vector3(0.06f, 0.10f, 0.12f) * envelope;
        }
        else
        {
            targets.LeanPitch += SpringMath.DegToRad(-3f) * envelope;
            targets.TorsoPitch += SpringMath.DegToRad(-6f) * envelope;
            targets.TorsoYaw += SpringMath.DegToRad(6f) * envelope;
            targets.BodyOffsetY += -0.03f * envelope;
            targets.HandReach += new Vector3(0.05f, 0f, 0.05f) * envelope;
        }
    }

    // --- Touch envelopes overlay the base pose ----------------------------------------------

    /// <summary>
    /// Overlays the short touch envelopes on whatever base pose was chosen: catch absorption and inward curl
    /// (mid-dive this becomes the tuck into the chest), the claim's high-point leap plus its one-shot cushioned
    /// landing, the punch's overhead double-fist, and the parry's glove follow-through. Simulated-time
    /// envelopes; motion intensity is applied through the spring compose path.
    /// </summary>
    private void ApplyKeeperTouchHoldTargets(ref ChannelTargets targets)
    {
        if (_keeperCatchTimer > 0f)
        {
            float fade = SpringMath.Clamp01(_keeperCatchTimer / KeeperCatchSeconds);
            targets.SquashY += -0.09f * fade;
            targets.TorsoPitch += SpringMath.DegToRad(-8f) * fade;
            targets.HandReach = Vector3.Lerp(
                targets.HandReach, new Vector3(0.10f, 0.03f, -0.09f), fade);
        }

        if (_keeperClaimTimer > 0f)
        {
            float progress = SpringMath.Clamp01(1f - _keeperClaimTimer / KeeperClaimSeconds);
            targets.BodyOffsetY += MathF.Sin(SpringMath.Pi * progress) * 0.32f;
            targets.HandReach = Vector3.Lerp(
                targets.HandReach, new Vector3(0.16f, 0.55f, 0.14f), 1f - progress);
            if (!_keeperClaimLandingDone && progress >= KeeperClaimLandingProgress)
            {
                _keeperClaimLandingDone = true;
                SnapSquash(-0.12f);
                SnapBodyOffsetY(-0.06f);
            }
        }

        if (_keeperPunchTimer > 0f)
        {
            float progress = SpringMath.Clamp01(1f - _keeperPunchTimer / KeeperPunchSeconds);
            float envelope = MathF.Sin(SpringMath.Pi * progress);
            targets.BodyOffsetY += envelope * 0.30f;
            targets.HandReach = Vector3.Lerp(
                targets.HandReach, new Vector3(0.10f, 0.62f, 0.16f), envelope);
            targets.TorsoPitch += SpringMath.DegToRad(-8f) * envelope;
        }

        if (_keeperParryTimer > 0f)
        {
            float fade = _keeperParryTimer / KeeperParrySeconds;
            targets.HandReach = Vector3.Lerp(
                targets.HandReach, new Vector3(0.34f, 0.10f, 0.20f), fade);
        }
    }

    /// <summary>
    /// Eases the hand-over from locomotion lean to the dedicated dive roll/pitch so the launch has no
    /// rotational pop, and fades it back on the get-up. Snaps to exactly zero once visually dead so a settled
    /// keeper never carries a residual blend.
    /// </summary>
    private void UpdateKeeperDiveBlend(bool diving, ref ChannelTargets targets)
    {
        _diveBlend = SpringMath.ExpApproach(_diveBlend, diving ? 1f : 0f, KeeperDiveBlendHalfLife, _dt);
        if (!diving && _diveBlend < 0.002f) _diveBlend = 0f;
        targets.DiveBlend01 = SpringMath.Clamp01(_diveBlend);
    }

    // --- Small helpers ----------------------------------------------------------------------

    /// <summary>True when the ball is in the keeper's hands, by the possession fact or the keeper state.</summary>
    private static bool IsKeeperHolding(in PlayerDynamicsInput input) =>
        input.HoldsBall || input.GoalkeeperState == GoalkeeperState.HoldingBall;

    /// <summary>
    /// Reach envelope for a dive: ramps in over <paramref name="riseSeconds"/>, holds, then falls away from
    /// <paramref name="fallStart"/>, so the gloves' overhead stretch tracks the body's arc.
    /// </summary>
    private static float KeeperReachEnvelope(float u, float riseSeconds, float fallStart, float fallSeconds) =>
        SpringMath.SmoothStep01(u / riseSeconds) *
        (1f - SpringMath.SmoothStep01((u - fallStart) / fallSeconds));
}
