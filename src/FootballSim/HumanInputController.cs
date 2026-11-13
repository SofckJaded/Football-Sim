using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;

namespace FootballSim.Input;

/// <summary>
/// Per-match communication state shared between the locked human controller and the
/// AI controllers of the same engine. Owning it on the match (instead of in statics)
/// keeps independent matches isolated for deterministic replays (audit F19).
/// </summary>
public sealed class MatchCommunicationContext
{
    public int? HumanCallForPassPlayerId { get; private set; }
    public float HumanCallForPassTimer { get; private set; }
    public bool HumanCalledThroughBall { get; private set; }

    public bool HasActiveCall => HumanCallForPassPlayerId.HasValue && HumanCallForPassTimer > 0f;

    public void RaiseCallForPass(int playerId, float durationSeconds, bool throughBall)
    {
        HumanCallForPassPlayerId = playerId;
        HumanCallForPassTimer = MathF.Max(0f, durationSeconds);
        HumanCalledThroughBall = throughBall;
    }

    public void TickCallForPassTimer(float dt)
    {
        if (HumanCallForPassTimer <= 0f) return;
        HumanCallForPassTimer = MathF.Max(0f, HumanCallForPassTimer - dt);
        if (HumanCallForPassTimer <= 0f) ClearCallForPass();
    }

    public void ClearCallForPass()
    {
        HumanCallForPassPlayerId = null;
        HumanCallForPassTimer = 0f;
        HumanCalledThroughBall = false;
    }
}

public sealed class HumanInputController : IFootballerController
{
    private const float MaxReceivableSpeed = 8.0f;

    public int LockedPlayerId { get; }
    public bool IsSpectatorMode { get; private set; }

    private readonly PassPlanner _passPlanner = new(new PitchDimensions());

    public Vector2 MoveStickXZ { get; set; }

    /// <summary>
    /// World-space pitch point under the mouse cursor, supplied by the Godot layer while the mouse is
    /// the active aim device (audit F16). Null when aiming with movement.
    /// </summary>
    public Vector2? AimPointXZ { get; set; }

    /// <summary>Chip-shot modifier held (RB / V). Resolves a shot into a lofted chip.</summary>
    public bool ChipShotHeld { get; set; }
    public bool SprintHeld { get; set; }
    public bool ShieldHeld { get; set; }
    public bool HighModifierHeld { get; set; } // Modifies Pass/ThroughPass to High/Air, and Shot to Curled Shot
    public bool CurveModifierHeld { get => HighModifierHeld; set => HighModifierHeld = value; }
    public float ChargedPower01 { get; set; } = 0.0f;
    public KickIntentType ChargingIntent { get; private set; } = KickIntentType.None;

    // Compatibility properties for unit tests
    private bool _immediateKickRequested;
    private bool _immediateTackleRequested;

    public bool PassPressed
    {
        get => _bufferedKick == KickIntentType.GroundPass;
        set
        {
            if (value)
            {
                _chargeDurationSeconds = 0.5f;
                ReleaseChargedKick(KickIntentType.GroundPass, 0.55f);
                _immediateKickRequested = true;
            }
        }
    }

    public bool ThroughLobPressed
    {
        get => _bufferedKick == KickIntentType.ThroughPass;
        set
        {
            if (value)
            {
                _chargeDurationSeconds = 0.5f;
                ReleaseChargedKick(KickIntentType.ThroughPass, 0.65f);
                _immediateKickRequested = true;
            }
        }
    }

    public bool ShootPressed
    {
        get => _bufferedKick is KickIntentType.Shot or KickIntentType.CurledShot
            or KickIntentType.ChipShot or KickIntentType.TrivelaShot;
        set
        {
            if (value)
            {
                _chargeDurationSeconds = 0.5f;
                ReleaseChargedKick(KickIntentType.Shot, 0.70f);
                _immediateKickRequested = true;
            }
        }
    }

    public bool TacklePressed
    {
        get => _bufferedTackleTimer > 0f;
        set
        {
            if (value)
            {
                QueueTackle();
                _immediateTackleRequested = true;
            }
        }
    }

    private KickIntentType _bufferedKick = KickIntentType.None;
    private float _bufferedPower01 = 0.55f;
    private float _bufferedKickTimer;
    private float _chargeDurationSeconds;
    private Vector2 _bufferedAimDirectionXZ;
    private float _bufferedTackleTimer;

    /// <summary>
    /// Match-owned communication state. Audit F19: this used to be static, so one
    /// match could raise or clear the call of a different, live match.
    /// </summary>
    public MatchCommunicationContext Communication { get; }

    public Vector2 LastResolvedAimDirectionXZ { get; private set; } = new(1f, 0f);

    /// <summary>
    /// True when the locked player is in control of the ball this tick (their team has it and it is
    /// inside striking reach). Used to make the shared gamepad X button mean Cross with the ball and
    /// Tackle without it.
    /// </summary>
    public bool HasBallAtFeet { get; private set; }

    public HumanInputController(int preMatchDesignatedPlayerId, MatchCommunicationContext? communication = null)
    {
        LockedPlayerId = preMatchDesignatedPlayerId;
        Communication = communication ?? new MatchCommunicationContext();
    }

    public ControllerSourceType SourceType => ControllerSourceType.HumanPreMatchLocked;

    public void NotifyPlayerUnavailableForMatch() => IsSpectatorMode = true;

    public void UpdateButtonCharge(KickIntentType baseIntent, float dt)
    {
        // Audit F16: one charge at a time. Holding a second kick button used to switch the intent
        // and reset the timer every frame, so releasing either one fired with the other's charge.
        if (ChargingIntent != KickIntentType.None && ChargingIntent != baseIntent)
            return;

        if (ChargingIntent != baseIntent)
        {
            ChargingIntent = baseIntent;
            _chargeDurationSeconds = dt;
            ChargedPower01 = 0.20f;
        }
        else
        {
            _chargeDurationSeconds += dt;
            ChargedPower01 = Math.Clamp(0.20f + _chargeDurationSeconds * 1.45f, 0.20f, 1.0f);
        }

        if (AimPointXZ.HasValue)
            _bufferedAimDirectionXZ = SimMath.SafeNormalize(AimPointXZ.Value - _lastSelfXZ, _bufferedAimDirectionXZ);
        else if (MoveStickXZ.LengthSquared() > 1e-4f)
            _bufferedAimDirectionXZ = Vector2.Normalize(MoveStickXZ);
    }

    /// <summary>Last observed self position, used only to turn a cursor world point into a direction.</summary>
    private Vector2 _lastSelfXZ;

    public void ReleaseChargedKick(KickIntentType baseIntent, float power01)
    {
        KickIntentType resolved = baseIntent switch
        {
            KickIntentType.GroundPass => HighModifierHeld ? KickIntentType.LoftedPass : KickIntentType.GroundPass,
            KickIntentType.ThroughPass => HighModifierHeld ? KickIntentType.LobbedThroughPass : KickIntentType.ThroughPass,
            KickIntentType.Cross => KickIntentType.Cross,
            // Shot modifiers (priority: chip > finesse > trivela). Sprint doubles as the trivela
            // modifier, matching "hold R2 while shooting"; the right stick is not an aim input.
            KickIntentType.Shot => ChipShotHeld
                ? KickIntentType.ChipShot
                : HighModifierHeld
                    ? KickIntentType.CurledShot
                    : SprintHeld
                        ? KickIntentType.TrivelaShot
                        : KickIntentType.Shot,
            _ => baseIntent
        };

        if (baseIntent == KickIntentType.Shot)
        {
            // One quick tap (< 0.18s) = Grounded Low Driven Shot; Hold = Normal Rising Shot
            _bufferedPower01 = _chargeDurationSeconds < 0.18f
                ? 0.25f
                : Math.Clamp(power01, 0.48f, 0.96f);
        }
        else
        {
            // Tap (< 0.18s) = minimal power (0.20f); Hold = scales smoothly with hold time up to 0.96f
            _bufferedPower01 = _chargeDurationSeconds < 0.18f
                ? 0.20f
                : Math.Clamp(power01, 0.22f, 0.96f);
        }

        _bufferedKick = resolved;
        _bufferedKickTimer = 0.45f;
        ChargingIntent = KickIntentType.None;
        ChargedPower01 = 0f;
        _chargeDurationSeconds = 0f;
    }

    /// <summary>
    /// Releases the notch that is actually charging. Audit F16: with four kick buttons sharing one
    /// charge timer, a release that does not match the active charge must not fire (and must not
    /// steal the other button's power).
    /// </summary>
    public void ReleaseActiveCharge(KickIntentType baseIntent, float power01)
    {
        if (ChargingIntent != KickIntentType.None && ChargingIntent != baseIntent)
            return;

        ReleaseChargedKick(baseIntent, power01);
    }

    public void QueueTackle() => _bufferedTackleTimer = 0.25f;

    /// <summary>
    /// Drops any held-charge or buffered kick/tackle. Used when play restarts from a neutral state
    /// (for example a training drill reset) so a shot queued before the whistle cannot fire after it.
    /// </summary>
    public void ClearBufferedActions()
    {
        _bufferedKick = KickIntentType.None;
        _bufferedKickTimer = 0f;
        _bufferedTackleTimer = 0f;
        _chargeDurationSeconds = 0f;
        _bufferedAimDirectionXZ = Vector2.Zero;
        ChargingIntent = KickIntentType.None;
        ChargedPower01 = 0f;
        _immediateKickRequested = false;
        _immediateTackleRequested = false;
    }

    /// <summary>
    /// How long a refused kick/tackle stays queued for a retry. Short on purpose: the intent must
    /// survive a lost single-contact arbitration or a ball that moved a few centimetres, not fire
    /// seconds later in a completely different situation.
    /// </summary>
    public const float ContactRetryWindowSeconds = 0.22f;

    /// <summary>True while a kick press is queued and has not been played yet (audit F17).</summary>
    public bool HasBufferedKickRequest => _bufferedKick != KickIntentType.None;

    /// <summary>Kick intent currently queued for a retry, or <see cref="KickIntentType.None"/>.</summary>
    public KickIntentType BufferedKickIntent => _bufferedKick;

    /// <summary>
    /// Audit F17: <see cref="Decide"/> consumes the queued kick/tackle when it emits the command, but
    /// the execution layer can still refuse it — a rival wins the tick's single-contact arbitration,
    /// the ball rolls out of reach between decision and execution, the ball is overhead, a foul
    /// restarts play. The match feeds those outcomes back here so a refused input is retried inside a
    /// short window instead of vanishing while the player watches the ball roll past.
    /// </summary>
    public void AcknowledgeContactAttempt(in BallContactAttempt attempt)
    {
        if (attempt.Executed || attempt.PlayerId != LockedPlayerId || !attempt.WantedContact)
            return;

        // A whistle or a keeper's held ball is a new situation; do not resurrect the input into it.
        if (attempt.Reason is ContactRejectionReason.RestartTriggered or ContactRejectionReason.BallHeld)
            return;

        if (attempt.WantedKick && _bufferedKick == KickIntentType.None)
        {
            _bufferedKick = attempt.Intent;
            _bufferedKickTimer = ContactRetryWindowSeconds;
        }

        if (attempt.WantedTackle && _bufferedTackleTimer <= 0f)
            _bufferedTackleTimer = ContactRetryWindowSeconds;
    }

    public FootballerCommand Decide(in FootballerObservation observation, float dt)
    {
        if (observation.SelfId != LockedPlayerId)
            throw new InvalidOperationException(
                $"Pre-match lock violation: HumanInputController is locked to Player #{LockedPlayerId} and cannot control Player #{observation.SelfId}.");

        if (IsSpectatorMode)
            return FootballerCommand.Idle;

        bool immediateKick = _immediateKickRequested;
        _immediateKickRequested = false;
        bool immediateTackle = _immediateTackleRequested;
        _immediateTackleRequested = false;

        _bufferedKickTimer = MathF.Max(0f, _bufferedKickTimer - dt);
        _bufferedTackleTimer = MathF.Max(0f, _bufferedTackleTimer - dt);
        Communication.TickCallForPassTimer(dt);

        if (_bufferedKickTimer <= 0f)
            _bufferedKick = KickIntentType.None;

        Vector2 moveDir = MoveStickXZ.LengthSquared() > 1f ? Vector2.Normalize(MoveStickXZ) : MoveStickXZ;
        Vector2 faceDir = moveDir.LengthSquared() > 1e-4f ? Vector2.Normalize(moveDir) : observation.SelfFacingXZ;

        Vector2 selfXZ = new(observation.SelfPosition.X, observation.SelfPosition.Z);
        Vector2 ballXZ = new(observation.PerceivedBallPosition.X, observation.PerceivedBallPosition.Z);
        float distToBall = Vector2.Distance(selfXZ, ballXZ);
        _lastSelfXZ = selfXZ;

        // Audit F16: one current aim source, in priority order — mouse cursor raycast, then the
        // dedicated aim stick, then movement direction, then the buffered charge aim, then facing.
        // "hasDirectionalAim" distinguishes a real aim request from the facing fallback (which is
        // the only case where shot assistance may pick a corner for the player).
        bool hasDirectionalAim = false;
        Vector2 rawAimDir;
        if (AimPointXZ is Vector2 aimPoint && Vector2.Distance(aimPoint, selfXZ) > 0.75f)
        {
            rawAimDir = SimMath.SafeNormalize(aimPoint - selfXZ, observation.SelfFacingXZ);
            hasDirectionalAim = true;
        }
        else if (moveDir.LengthSquared() > 0.04f)
        {
            rawAimDir = Vector2.Normalize(moveDir);
            hasDirectionalAim = true;
        }
        else if (_bufferedAimDirectionXZ.LengthSquared() > 0.04f)
        {
            rawAimDir = _bufferedAimDirectionXZ;
            hasDirectionalAim = true;
        }
        else
        {
            rawAimDir = observation.SelfFacingXZ;
        }

        rawAimDir = SimMath.SafeNormalize(rawAimDir, new Vector2(observation.AttackingDirectionX, 0f));
        LastResolvedAimDirectionXZ = rawAimDir;

        // Audit F17: use the same physical reach the execution layer applies, instead of a private
        // 1.125 m planar range that let a press be "fired" where execution could never play the ball.
        bool receivingOrDribbling = BallContactRules.IsReceivingOrDribbling(
            observation.BallLastTouchedByTeam,
            observation.BallLastTouchedByPlayerId,
            observation.SelfTeam,
            observation.SelfId);
        bool isGoalkeeper = observation.SelfRole == TacticalRole.Goalkeeper;
        float strikeReach = BallContactRules.ReachRadius(
            observation.SelfAttributes.ReachRadius,
            isGoalkeeper,
            isDiving: false,
            requestingTackle: false,
            receivingOrDribbling);

        HasBallAtFeet = (observation.PossessingTeam == observation.SelfTeam ||
                         observation.BallLastTouchedByPlayerId == observation.SelfId) &&
                        distToBall <= strikeReach &&
                        observation.AvailableToKick;

        // Off-ball Call for Pass when teammate has possession
        if (_bufferedKick is KickIntentType.GroundPass or KickIntentType.LoftedPass or KickIntentType.ThroughPass or KickIntentType.LobbedThroughPass &&
            distToBall > 2.8f && observation.PossessingTeam == observation.SelfTeam)
        {
            Communication.RaiseCallForPass(
                LockedPlayerId,
                2.5f,
                _bufferedKick is KickIntentType.ThroughPass or KickIntentType.LobbedThroughPass);
            if (!immediateKick)
            {
                _bufferedKick = KickIntentType.None;
                _bufferedKickTimer = 0f;
            }
        }

        KickIntentType activeKick = KickIntentType.None;
        Vector2 finalKickAimXZ = rawAimDir;
        float finalPower01 = _bufferedPower01;
        int? intendedReceiverId = null;
        float curveSpin = 0f;

        bool inStrikeRange = distToBall <= strikeReach && observation.AvailableToKick;
        if (_bufferedKick != KickIntentType.None && (inStrikeRange || immediateKick))
        {
            activeKick = _bufferedKick;

            // A shot resolves against the modifiers held at execution time too, so pressing shoot a
            // frame before the chip/finesse modifier still does what the player intended.
            if (activeKick is KickIntentType.Shot or KickIntentType.CurledShot
                or KickIntentType.ChipShot or KickIntentType.TrivelaShot)
            {
                activeKick = ChipShotHeld
                    ? KickIntentType.ChipShot
                    : HighModifierHeld
                        ? KickIntentType.CurledShot
                        : SprintHeld
                            ? KickIntentType.TrivelaShot
                            : KickIntentType.Shot;
            }

            if (activeKick is KickIntentType.GroundPass or KickIntentType.LoftedPass)
            {
                // Pass Type 1 (Assisted to Feet): Tap = closest teammate in aim direction; Hold = further teammate in aim direction
                if (TryFindPassToFeetTarget(observation, selfXZ, rawAimDir, finalPower01,
                    out Vector2 assistedDir, out float assistedPower, out int receiverId))
                {
                    finalKickAimXZ = assistedDir;
                    finalPower01 = assistedPower;
                    intendedReceiverId = receiverId;
                }
            }
            else if (activeKick is KickIntentType.ThroughPass or KickIntentType.LobbedThroughPass)
            {
                // Pass Type 2 (Semi-Assisted Through Pass): Preserves your tap/hold power and aim direction
                ComputeSemiAssistedThroughPass(observation, selfXZ, rawAimDir, finalPower01,
                    out finalKickAimXZ, out finalPower01, out intendedReceiverId);
            }
            else if (activeKick == KickIntentType.Cross)
            {
                // Pass Type 3: Cross into the opponent penalty box
                Vector2 boxTarget = FindBestCrossTargetInBox(observation, selfXZ, finalPower01, out intendedReceiverId);
                finalKickAimXZ = SimMath.SafeNormalize(boxTarget - selfXZ, rawAimDir);
            }
            else if (activeKick is KickIntentType.Shot or KickIntentType.CurledShot
                     or KickIntentType.ChipShot or KickIntentType.TrivelaShot)
            {
                // Shoot where the player points: the aim ray is the movement direction (or the
                // cursor when no gamepad is connected), intersected with the goal line.
                finalKickAimXZ = ResolveShotAim(observation, selfXZ, rawAimDir, hasDirectionalAim);
            }

            LastResolvedAimDirectionXZ = finalKickAimXZ;

            // Curled/trivela/cross spin bends the ball back toward the goal centre from the aimed
            // side (an outside-foot trivela bends the opposite way). The execution scales it.
            if (activeKick is KickIntentType.CurledShot or KickIntentType.Cross or KickIntentType.TrivelaShot)
            {
                float side = finalKickAimXZ.X * finalKickAimXZ.Y;
                curveSpin = MathF.Abs(side) > 0.005f ? MathF.Sign(side) : 1f;
            }

            _bufferedKick = KickIntentType.None;
            _bufferedKickTimer = 0f;
        }

        bool jumpHeader = activeKick != KickIntentType.None && observation.PerceivedBallPosition.Y > 1.25f;
        if (jumpHeader)
            activeKick = KickIntentType.Header;

        // Audit F15: standing-tackle execution uses ReachRadius * 1.60f (~1.20 m) of planar
        // reach. A buffered press must fire anywhere inside that reach — point blank included —
        // and stay buffered while out of reach so a slightly early press can still land.
        float tackleReach = BallContactRules.ReachRadius(
            observation.SelfAttributes.ReachRadius,
            isGoalkeeper,
            isDiving: false,
            requestingTackle: true,
            receivingOrDribbling: false);
        bool executeTackle = (_bufferedTackleTimer > 0f && distToBall <= tackleReach) || immediateTackle;
        if (executeTackle)
            _bufferedTackleTimer = 0f;

        return new FootballerCommand(
            DesiredMoveDirectionXZ: moveDir,
            DesiredFacingDirectionXZ: faceDir,
            Sprint: SprintHeld,
            ShieldBall: ShieldHeld,
            RequestTackle: executeTackle,
            RequestJumpHeader: jumpHeader,
            RequestKeeperDive: false,
            KeeperDiveDirection: Vector3.Zero,
            KickType: activeKick,
            KickAimDirectionXZ: new Vector3(finalKickAimXZ.X, 0f, finalKickAimXZ.Y),
            KickPower01: Math.Clamp(finalPower01, 0.20f, 0.98f),
            RequestedCurveSpin: curveSpin
        )
        {
            IntendedReceiverId = intendedReceiverId
        };
    }

    /// <summary>
    /// Audit F16: resolves a shot from the player's current aim source. The aim ray is intersected
    /// with the opponent goal line and clamped just inside the posts, so a shot goes where the
    /// player points it instead of always snapping to a fixed corner. Only a player with no
    /// directional input at all gets the assisted "corner away from the keeper" fallback.
    /// </summary>
    private Vector2 ResolveShotAim(
        in FootballerObservation obs,
        Vector2 selfXZ,
        Vector2 aimDir,
        bool hasDirectionalAim)
    {
        const float halfGoalInside = 3.66f - 0.35f;
        float goalLineX = obs.AttackingDirectionX * (52.5f - 0.35f);
        float dx = goalLineX - selfXZ.X;

        bool towardGoal = MathF.Abs(dx) > 1e-3f && dx * aimDir.X > 0f;
        if (hasDirectionalAim && towardGoal)
        {
            float t = dx / aimDir.X;
            float zAtGoalLine = selfXZ.Y + aimDir.Y * t;
            float clampedZ = Math.Clamp(zAtGoalLine, -halfGoalInside, halfGoalInside);
            return SimMath.SafeNormalize(new Vector2(goalLineX, clampedZ) - selfXZ, aimDir);
        }

        // No aim, or aiming away from goal: assisted shot to the corner furthest from the keeper.
        float goalkeeperZ = 0f;
        foreach (var p in obs.VisiblePlayers)
        {
            if (p.Team == obs.SelfTeam || p.Role != TacticalRole.Goalkeeper) continue;
            goalkeeperZ = p.EstimatedPosition.Z;
            break;
        }

        float assistedZ = goalkeeperZ >= 0f ? -halfGoalInside : halfGoalInside;
        return SimMath.SafeNormalize(new Vector2(goalLineX, assistedZ) - selfXZ, aimDir);
    }

    private bool TryFindPassToFeetTarget(
        in FootballerObservation obs,
        Vector2 selfXZ,
        Vector2 rawAimDir,
        float userPower01,
        out Vector2 assistedDir,
        out float assistedPower,
        out int receiverId)
    {
        assistedDir = rawAimDir;
        assistedPower = userPower01;
        receiverId = 0;

        bool isQuickTap = userPower01 <= 0.28f;
        // Quick tap targets closest teammate (~5m); holding targets progressively further teammates (up to 42m)
        float desiredDist = isQuickTap ? 5.0f : (8.0f + (userPower01 - 0.20f) * 42.0f);

        float bestScore = -999f;
        Vector2 bestTargetXZ = Vector2.Zero;
        float bestDist = 0f;
        int bestMateId = 0;
        Vector2 bestMateVelocityXZ = Vector2.Zero;
        bool found = false;

        foreach (var mate in obs.VisiblePlayers)
        {
            if (mate.Team != obs.SelfTeam) continue;

            // The planner leads the receiver by the modelled flight time, so the selection point is his
            // actual position: adding a second, fixed 0.25 s lead here double-counted the run (A4.11).
            Vector3 targetPos = mate.EstimatedPosition;
            Vector2 mateXZ = new(targetPos.X, targetPos.Z);
            Vector2 toMate = mateXZ - selfXZ;
            float dist = toMate.Length();
            if (dist < 2.0f || dist > 48.0f) continue;

            Vector2 dirToMate = toMate / dist;
            float cosAngle = Vector2.Dot(rawAimDir, dirToMate);

            // Must be in the direction the user is aiming (~60 deg cone; cos(60) = 0.50)
            if (cosAngle < 0.50f) continue;

            float score;
            if (isQuickTap)
            {
                // One tap: strongly prefer the CLOSEST teammate in the aimed direction
                score = cosAngle * 1.8f - (dist / 12.0f) * 1.5f;
            }
            else
            {
                // Held button: prefer the teammate in the aimed direction whose distance matches your hold power
                float distDiff = MathF.Abs(dist - desiredDist);
                score = cosAngle * 2.0f - (distDiff / 18.0f) * 1.4f;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestTargetXZ = mateXZ;
                bestDist = dist;
                bestMateId = mate.PlayerId;
                bestMateVelocityXZ = new Vector2(mate.EstimatedVelocity.X, mate.EstimatedVelocity.Z);
                found = true;
            }
        }

        if (!found) return false;

        // Audit F01: use the shared planner instead of a fixed 0.25 s lead and a distance-scaled power,
        // so an assisted pass actually reaches the receiver at the moment the ball arrives. A4.11: the
        // lead is bounded by the planner's default (12 m) — this call used to rely on the old 60 m
        // default, which turned an assisted ball to a runner into a punt he could never reach.
        var plan = _passPlanner.PlanGroundPass(
            new Vector2(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z),
            bestMateId,
            bestTargetXZ,
            bestMateVelocityXZ,
            MaxReceivableSpeed,
            // The human is passing to a human-shaped receiver: aim no further ahead than the bound even
            // if he would run past it, because a human may stop. A4.11 bounded the default lead; this
            // flag keeps the friendly hedge that the old 60 m default accidentally provided.
            freezeReceiverAtLeadBound: true);

        if (!plan.Feasible)
            return false;

        assistedDir = plan.AimDirectionXZ;
        assistedPower = plan.Power01;
        receiverId = bestMateId;
        return true;
    }

    private static void ComputeSemiAssistedThroughPass(
        in FootballerObservation obs,
        Vector2 selfXZ,
        Vector2 rawAimDir,
        float userPower01,
        out Vector2 semiAssistedDir,
        out float semiAssistedPower,
        out int? runnerId)
    {
        // Keep the user's exact tap/hold power (Tap = 0.20f minimal power, Hold = up to 0.96f)
        semiAssistedPower = userPower01;
        semiAssistedDir = rawAimDir;
        runnerId = null;

        float targetPassDistance = 6.0f + userPower01 * 32.0f;
        float bestScore = -999f;
        Vector2 bestSpaceSpotXZ = Vector2.Zero;
        bool foundRunner = false;
        int bestRunnerId = 0;

        foreach (var mate in obs.VisiblePlayers)
        {
            if (mate.Team != obs.SelfTeam || mate.Role == TacticalRole.Goalkeeper) continue;

            // Project a space spot ahead of the teammate along the attacking direction
            float leadMeters = 3.0f + userPower01 * 7.0f;
            Vector3 spacePos = mate.EstimatedPosition
                             + mate.EstimatedVelocity * 0.55f
                             + new Vector3(obs.AttackingDirectionX * leadMeters, 0f, 0f);

            spacePos.X = Math.Clamp(spacePos.X, -50f, 50f);
            spacePos.Z = Math.Clamp(spacePos.Z, -32f, 32f);

            Vector2 spaceXZ = new(spacePos.X, spacePos.Z);
            Vector2 toSpace = spaceXZ - selfXZ;
            float dist = toSpace.Length();
            if (dist < 3.0f || dist > 46.0f) continue;

            Vector2 dirToSpace = toSpace / dist;
            float cosAngle = Vector2.Dot(rawAimDir, dirToSpace);

            // Narrow 32-degree semi-assist cone (cos(32 deg) ~= 0.848) so it NEVER fights your directional input
            if (cosAngle < 0.84f) continue;

            float distMatch = 1.0f - MathF.Abs(dist - targetPassDistance) / 35.0f;
            float score = cosAngle * 2.5f + distMatch;
            if (score > bestScore)
            {
                bestScore = score;
                bestSpaceSpotXZ = spaceXZ;
                bestRunnerId = mate.PlayerId;
                foundRunner = true;
            }
        }

        if (foundRunner)
        {
            Vector2 idealDir = Vector2.Normalize(bestSpaceSpotXZ - selfXZ);
            // Semi-assisted: blend at most 30% toward the runner's lane, keeping 70% pure user aim
            semiAssistedDir = Vector2.Normalize(Vector2.Lerp(rawAimDir, idealDir, 0.30f));
            runnerId = bestRunnerId;
        }
    }

    private static Vector2 FindBestCrossTargetInBox(
        in FootballerObservation obs,
        Vector2 selfXZ,
        float power01,
        out int? receiverId)
    {
        float boxX = obs.AttackingDirectionX * 41.5f;
        Vector2 defaultSpot = new(boxX, (power01 - 0.5f) * 12.0f);
        receiverId = null;

        float bestScore = -999f;
        foreach (var mate in obs.VisiblePlayers)
        {
            if (mate.Team != obs.SelfTeam || mate.Role == TacticalRole.Goalkeeper) continue;
            float forwardX = mate.EstimatedPosition.X * obs.AttackingDirectionX;
            if (forwardX > 25.0f && MathF.Abs(mate.EstimatedPosition.Z) < 18.0f)
            {
                float score = forwardX;
                if (score > bestScore)
                {
                    bestScore = score;
                    receiverId = mate.PlayerId;
                    defaultSpot = new Vector2(
                        mate.EstimatedPosition.X + obs.AttackingDirectionX * 2.5f,
                        mate.EstimatedPosition.Z);
                }
            }
        }
        return defaultSpot;
    }

    public string GetDebugSummary()
    {
        if (IsSpectatorMode) return $"Human(#{LockedPlayerId}):SPECTATOR";
        if (ChargingIntent != KickIntentType.None)
            return $"CHARGING {ChargingIntent}{(HighModifierHeld ? "+HIGH" : "")}: {ChargedPower01 * 100f:F0}%";
        if (Communication.HumanCallForPassPlayerId == LockedPlayerId) return $"Human(#{LockedPlayerId}):CallForPass!";
        return $"Human(#{LockedPlayerId}):Ready";
    }
}

public sealed class RecordedCommandController : IFootballerController
{
    private readonly IReadOnlyDictionary<int, FootballerCommand> _commandsByTick;
    private readonly Func<int> _currentTickProvider;

    public RecordedCommandController(
        ControllerSourceType labelSourceType,
        IReadOnlyDictionary<int, FootballerCommand> commandsByTick,
        Func<int> currentTickProvider)
    {
        SourceType = labelSourceType;
        _commandsByTick = commandsByTick;
        _currentTickProvider = currentTickProvider;
    }

    public ControllerSourceType SourceType { get; }

    public FootballerCommand Decide(in FootballerObservation observation, float dt)
    {
        int tick = _currentTickProvider();
        return _commandsByTick.TryGetValue(tick, out var cmd) ? cmd : FootballerCommand.Idle;
    }

    public string GetDebugSummary() => $"Recorded({SourceType})";
}
