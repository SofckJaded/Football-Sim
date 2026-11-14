using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Physics;

namespace FootballSim.Locomotion;

public sealed class FootballerState
{
    public required int Id { get; init; }
    public required TeamId Team { get; init; }
    public required int ShirtNumber { get; init; }
    public required TacticalRole Role { get; set; }
    public required PlayerAttributes Attributes { get; init; }
    public required PlayerTendencies Tendencies { get; init; }

    public Vector3 Position { get; set; }
    public Vector3 PreviousPosition { get; set; }
    public Vector3 Velocity { get; set; }
    public Vector2 FacingXZ { get; set; } = new(1f, 0f);
    public float Stamina01 { get; set; } = 1.0f;
    public float Balance01 { get; set; } = 1.0f;
    public float ControlQuality01 { get; set; } = 0.0f;
    public float KickRecoveryTimer { get; set; } = 0.0f;
    public float DribbleTouchTimer { get; set; } = 0.0f;
    public float TackleRecoveryTimer { get; set; } = 0.0f;
    public bool IsJumping { get; set; }
    public bool IsDiving { get; set; }
    public int YellowCards { get; set; }
    public bool IsSentOff { get; set; }
    public bool IsSubstitutedOrInjured { get; set; }
    public Vector3 TacticalAnchor { get; set; }
    public FootballerCommand LastCommand { get; set; } = FootballerCommand.Idle;
    public string LastExecutionDiagnostic { get; set; } = "Idle";

    public const float CapsuleRadius = 0.36f;
    public const float PlayerHeight = 1.82f;

    public bool IsActiveOnPitch => !IsSentOff && !IsSubstitutedOrInjured;
    // Dribble micro-touches never block a player from executing a pass or shot
    public bool AvailableToKick => IsActiveOnPitch && KickRecoveryTimer <= 0f && TackleRecoveryTimer <= 0f;
}

/// <summary>
/// The physical rules for touching the ball: how far a player can reach, how high, and whether their
/// body is facing it. Shared by the execution layer (which applies them) and by controllers (which use
/// them to decide whether a press can be executed at all).
///
/// Audit F17: the human controller had its own 1.125 m planar "strike range" and no vertical check,
/// while execution accepted 0.825–1.2375 m depending on the situation and refused balls above
/// 1.45 m. A press could therefore be "fired" in a place where execution provably could not play the
/// ball, and analytics counted it as an attempt.
/// </summary>
public static class BallContactRules
{
    /// <summary>Lowest accepted ball height relative to the player's feet (a ball on the grass).</summary>
    public const float MinContactHeightDelta = -0.05f;

    /// <summary>Standing torso/leg reach: a ball higher than this cannot be played without a jump.</summary>
    public const float TorsoContactHeight = 1.45f;

    /// <summary>Reach while jumping or heading (the ball can be met above the head).</summary>
    public const float HeaderContactHeight = FootballerState.PlayerHeight + 0.45f;

    /// <summary>Goalkeeper handling height (hands, not feet).</summary>
    public const float KeeperContactHeight = 2.42f;

    /// <summary>
    /// Contest bonus for a player using their body to shield the ball (audit F18). Shielding used to
    /// be written onto the command and never read, so holding the shield button did nothing.
    /// </summary>
    public const float ShieldContestBonus = 0.18f;

    /// <summary>Contest penalty for an opponent trying to take a shielded ball.</summary>
    public const float ShieldOpponentPenalty = 0.40f;

    /// <summary>A tackling opponent suffers only half the shielding penalty — a real tackle still works.</summary>
    public const float ShieldTacklePenaltyFactor = 0.50f;

    /// <summary>Movement speed while shielding: the player uses their body instead of running.</summary>
    public const float ShieldSpeedFactor = 0.72f;

    /// <summary>
    /// Top-speed factor while the ball is at the player's feet. A dribbler is slower than a
    /// defender running without the ball — without this a carrier at full sprint could never be
    /// caught from behind (which made one player able to run through a whole defence).
    /// </summary>
    public const float DribbleSpeedFactor = 0.93f;

    /// <summary>
    /// True when the ball belongs to this player's team and was last played by someone else (a pass
    /// to them), or was last touched by them. This extends the reaching tolerance in execution.
    /// </summary>
    public static bool IsReceivingOrDribbling(
        TeamId? lastTouchedByTeam,
        int? lastTouchedByPlayerId,
        TeamId playerTeam,
        int playerId) =>
        (lastTouchedByTeam == playerTeam && lastTouchedByPlayerId != playerId) ||
        lastTouchedByPlayerId == playerId;

    /// <summary>Planar reach for the player's current action, in metres.</summary>
    public static float ReachRadius(
        float baseReachRadius,
        bool isGoalkeeper,
        bool isDiving,
        bool requestingTackle,
        bool receivingOrDribbling) =>
        isGoalkeeper
            ? baseReachRadius * (isDiving ? 1.55f : 1.12f)
            : requestingTackle
                ? baseReachRadius * 1.60f
                : baseReachRadius * (receivingOrDribbling ? 1.65f : 1.10f);

    /// <summary>Highest ball height (relative to the player's feet) the player can play.</summary>
    public static float MaxContactHeight(bool isGoalkeeper, bool jumpingOrHeading) =>
        isGoalkeeper
            ? KeeperContactHeight
            : jumpingOrHeading
                ? HeaderContactHeight
                : TorsoContactHeight;

    /// <summary>True when the ball's height is inside the player's reach window.</summary>
    public static bool IsHeightReachable(float ballY, float playerY, float maxContactHeight)
    {
        float delta = ballY - playerY;
        return delta >= MinContactHeightDelta && delta <= maxContactHeight;
    }

    /// <summary>
    /// True when a non-keeper can physically play a ball at this planar direction: execution refuses
    /// a ball that is mostly behind the player unless it was played by their own team or by them.
    /// </summary>
    public static bool IsFacingTheBall(float facingDot, bool receivingOrDribbling, bool isGoalkeeper) =>
        facingDot >= -0.55f || receivingOrDribbling || isGoalkeeper;
}

public sealed class FootballerExecutionSystem
{
    /// <summary>
    /// Short exclusive claim the player who just controlled the ball has on it. It only stops a
    /// plain touch, never a requested tackle, so pressing is not weakened.
    /// </summary>
    private const float ControlLockSeconds = 0.12f;

    /// <summary>
    /// After winning a tackle the new carrier is protected from being immediately tackled back.
    /// Without this two AI players facing each other traded the ball every half second (the reported
    /// "tackle spam"): each tackle put the ball at the winner's feet, the dispossessed player
    /// recovered first and poked it straight back, forever, before either could start moving.
    /// </summary>
    private const float TackleShieldSeconds = 0.4f;

    private readonly PitchDimensions _pitch;
    private int _currentTick;
    private int _controlLockPlayerId = -1;
    private float _controlLockSeconds;
    private int _tackleShieldPlayerId = -1;
    private float _tackleShieldSeconds;

    public FootballerExecutionSystem(PitchDimensions pitch)
    {
        _pitch = pitch;
    }

    /// <summary>
    /// Plays/touches produced by the most recent <see cref="UpdateLocomotionAndContacts"/> call.
    /// Rebuilt every tick; the engine drains this into the rules engine (audit F08).
    /// </summary>
    public List<BallContactEvent> ContactEvents { get; } = new();

    /// <summary>
    /// The ball contacts players asked for in the most recent update, including the ones that were
    /// refused and why (audit F17). A command is not an attempt: only an entry with
    /// <see cref="BallContactAttempt.Executed"/> means the ball was actually played.
    /// </summary>
    public List<BallContactAttempt> ContactAttempts { get; } = new();

    public void UpdateLocomotionAndContacts(
        IReadOnlyList<FootballerState> players,
        BallState ball,
        SeededRandom rng,
        float dt,
        Func<int, int, Vector3, float, bool>? onTackleContactCallback = null,
        int tick = 0,
        ReceptionAssistContext? receptionAssist = null)
    {
        _currentTick = tick;
        ContactEvents.Clear();
        ContactAttempts.Clear();

        if (_controlLockSeconds > 0f)
        {
            _controlLockSeconds = MathF.Max(0f, _controlLockSeconds - dt);
            if (_controlLockSeconds <= 0f) _controlLockPlayerId = -1;
        }

        if (_tackleShieldSeconds > 0f)
        {
            _tackleShieldSeconds = MathF.Max(0f, _tackleShieldSeconds - dt);
            if (_tackleShieldSeconds <= 0f) _tackleShieldPlayerId = -1;
        }

        foreach (var p in players)
        {
            if (!p.IsActiveOnPitch) continue;
            p.PreviousPosition = p.Position;
            p.KickRecoveryTimer = MathF.Max(0f, p.KickRecoveryTimer - dt);
            p.DribbleTouchTimer = MathF.Max(0f, p.DribbleTouchTimer - dt);
            p.TackleRecoveryTimer = MathF.Max(0f, p.TackleRecoveryTimer - dt);
            p.Balance01 = Math.Clamp(p.Balance01 + 0.65f * dt, 0.2f, 1.0f);

            StepPlayerLocomotion(p, players, ball, dt, receptionAssist);
        }

        // Audit F03: keep a held ball attached to its holder (and tick its release delay) before
        // any collision or contact resolution can treat it as a free ball.
        UpdateHeldBall(players, ball, dt);

        ResolvePlayerCollisions(players);
        ResolveBallInteractions(players, ball, rng, dt, onTackleContactCallback);
    }

    private static void UpdateHeldBall(IReadOnlyList<FootballerState> players, BallState ball, float dt)
    {
        if (!ball.HeldByPlayerId.HasValue) return;

        FootballerState? holder = FindActivePlayer(players, ball.HeldByPlayerId.Value);
        if (holder is null)
        {
            ReleaseHeldBall(ball);
            return;
        }

        ball.HeldReleaseDelaySeconds = MathF.Max(0f, ball.HeldReleaseDelaySeconds - dt);
        ball.Position = HeldBallPosition(holder);
        ball.PreviousPosition = ball.Position;
        ball.Velocity = Vector3.Zero;
        ball.AngularVelocity = Vector3.Zero;
    }

    private static Vector3 HeldBallPosition(FootballerState holder) => new(
        holder.Position.X + holder.FacingXZ.X * 0.30f,
        holder.Position.Y + 1.05f,
        holder.Position.Z + holder.FacingXZ.Y * 0.30f);

    private static void ReleaseHeldBall(BallState ball)
    {
        ball.HeldByPlayerId = null;
        ball.HeldReleaseDelaySeconds = 0f;
        ball.Position = new Vector3(ball.Position.X, 0.11f, ball.Position.Z);
        ball.Velocity = Vector3.Zero;
        ball.AngularVelocity = Vector3.Zero;
    }

    private static FootballerState? FindActivePlayer(IReadOnlyList<FootballerState> players, int playerId)
    {
        foreach (FootballerState player in players)
            if (player.Id == playerId && player.IsActiveOnPitch) return player;
        return null;
    }

    private void StepPlayerLocomotion(
        FootballerState p,
        IReadOnlyList<FootballerState> players,
        BallState ball,
        float dt,
        ReceptionAssistContext? receptionAssist)
    {
        var cmd = p.LastCommand;

        bool sprinting = cmd.Sprint && cmd.DesiredMoveDirectionXZ.LengthSquared() > 0.05f && p.TackleRecoveryTimer <= 0f;
        if (sprinting)
            p.Stamina01 = MathF.Max(0.15f, p.Stamina01 - (0.011f / p.Attributes.StaminaCapacity) * dt);
        else
            p.Stamina01 = MathF.Min(1.0f, p.Stamina01 + 0.0045f * dt);

        Vector2 desiredFace = cmd.DesiredFacingDirectionXZ.LengthSquared() > 1e-4f
            ? Vector2.Normalize(cmd.DesiredFacingDirectionXZ)
            : (cmd.DesiredMoveDirectionXZ.LengthSquared() > 1e-4f
                ? Vector2.Normalize(cmd.DesiredMoveDirectionXZ)
                : p.FacingXZ);

        float currentAngle = MathF.Atan2(p.FacingXZ.Y, p.FacingXZ.X);
        float targetAngle = MathF.Atan2(desiredFace.Y, desiredFace.X);
        float angleDiff = WrapAngle(targetAngle - currentAngle);
        float speedRatio = new Vector2(p.Velocity.X, p.Velocity.Z).Length() / p.Attributes.MaxSprintSpeed;
        float maxTurn = (p.Attributes.AgilityTurnRate * 1.25f) * (1.0f - 0.25f * speedRatio) * dt;
        float newAngle = currentAngle + Math.Clamp(angleDiff, -maxTurn, maxTurn);
        p.FacingXZ = new Vector2(MathF.Cos(newAngle), MathF.Sin(newAngle));

        Vector2 moveInput = cmd.DesiredMoveDirectionXZ;
        if (moveInput.LengthSquared() > 1f)
            moveInput = Vector2.Normalize(moveInput);

        Vector2 toBallXZ = new(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z);
        Vector2 ballVelXZ = new(ball.Velocity.X, ball.Velocity.Z);
        float distToBallXZ = toBallXZ.Length();
        float ballSpeedXZ = ballVelXZ.Length();

        float fatigueFactor = 0.68f + 0.32f * p.Stamina01;
        float baseMaxSpeed = sprinting
            ? p.Attributes.MaxSprintSpeed * fatigueFactor
            : p.Attributes.MaxSprintSpeed * 0.75f * fatigueFactor;

        // Carrying the ball costs a little top speed, but only when an opponent is close enough to
        // contest it: a carrier in open space must still be able to break away (a blanket slow-down
        // made every attack stall in midfield), while a pressed carrier can be caught.
        bool carryingBall = !ball.HeldByPlayerId.HasValue &&
                            ball.LastTouchedByPlayerId == p.Id &&
                            distToBallXZ <= p.Attributes.ReachRadius * 1.8f;
        if (carryingBall && NearestOpponentDistance(players, p) <= 4.5f)
            baseMaxSpeed *= BallContactRules.DribbleSpeedFactor;

        // Audit F18: shielding is a real protective stance — the carrier is slower while using their
        // body to keep the ball away from an opponent.
        if (cmd.ShieldBall)
            baseMaxSpeed *= BallContactRules.ShieldSpeedFactor;

        if (p.TackleRecoveryTimer > 0f)
            baseMaxSpeed *= 0.55f;

        float alignment = moveInput.LengthSquared() > 1e-4f
            ? Vector2.Dot(Vector2.Normalize(moveInput), p.FacingXZ)
            : 1.0f;
        float directionalMultiplier = 0.85f + 0.15f * MathF.Max(0f, alignment);

        // Pass Reception Assistance, scoped to the designated receiver (audit F11). It used to fire
        // for ANY teammate within 6.5 m of any moving friendly ball — including around a teammate's
        // dribble — and could drag a human off their own input. Now it needs an actual pass in
        // flight with this player as its planned receiver, predicts the closest approach with the
        // ball's real velocity, and gives explicit human input priority.
        bool isDesignatedReceiver = receptionAssist is { ControlState: BallControlState.InFlightPass } assistContext
                                    && assistContext.IntendedReceiverId == p.Id;
        bool incomingPassToMe = isDesignatedReceiver &&
                                ball.LastTouchedByTeam == p.Team &&
                                ball.LastTouchedByPlayerId != p.Id &&
                                distToBallXZ < 6.5f &&
                                ballSpeedXZ > 2.0f &&
                                Vector2.Dot(
                                    SimMath.SafeNormalize(ballVelXZ, Vector2.Zero),
                                    SimMath.SafeNormalize(-toBallXZ, Vector2.Zero)) > 0.35f;

        if (incomingPassToMe)
        {
            // Closest approach of the ball's current trajectory: t* = -(relative · v) / |v|².
            Vector2 relativeToBall = new(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z);
            float closestApproachTime = -Vector2.Dot(relativeToBall, ballVelXZ) / MathF.Max(1e-4f, ballSpeedXZ * ballSpeedXZ);
            closestApproachTime = Math.Clamp(closestApproachTime, 0f, 2.0f);
            Vector2 interceptSpotXZ = new Vector2(ball.Position.X, ball.Position.Z) + ballVelXZ * closestApproachTime;
            Vector2 toIntercept = interceptSpotXZ - new Vector2(p.Position.X, p.Position.Z);
            float interceptDist = toIntercept.Length();

            if (interceptDist > 0.20f && interceptDist < 3.8f)
            {
                Vector2 assistDir = toIntercept / interceptDist;
                float assistBlend = 0.78f;

                if (receptionAssist is { } context && context.HumanPlayerId == p.Id)
                {
                    if (moveInput.LengthSquared() > 1e-4f)
                    {
                        float directionAgreement = Vector2.Dot(Vector2.Normalize(moveInput), assistDir);
                        // Running away from the pass: the human declined the ball. Do nothing.
                        assistBlend = directionAgreement < -0.35f ? 0f : 0.30f;
                    }
                }

                if (assistBlend > 0f)
                {
                    moveInput = moveInput.LengthSquared() > 1e-4f
                        ? Vector2.Normalize(Vector2.Lerp(moveInput, assistDir, assistBlend))
                        : assistDir;
                }
            }
            else if (distToBallXZ < 2.2f)
            {
                // Already on the pass line: slow down to cushion the ball cleanly
                baseMaxSpeed *= 0.50f;
            }
        }

        Vector2 targetVelXZ = moveInput * (baseMaxSpeed * directionalMultiplier);
        Vector2 currentVelXZ = new(p.Velocity.X, p.Velocity.Z);
        Vector2 deltaV = targetVelXZ - currentVelXZ;
        float deltaLen = deltaV.Length();

        bool isBraking = Vector2.Dot(targetVelXZ, currentVelXZ) < 0f || targetVelXZ.LengthSquared() < currentVelXZ.LengthSquared();
        float maxAccel = (isBraking ? p.Attributes.Acceleration * 1.45f : p.Attributes.Acceleration * 1.15f) * p.Balance01;
        if (deltaLen > maxAccel * dt && deltaLen > 1e-5f)
            currentVelXZ += (deltaV / deltaLen) * (maxAccel * dt);
        else
            currentVelXZ = targetVelXZ;

        // GK Dive: use DribbleTouchTimer for dive cooldown so KickRecoveryTimer stays 0 and GK can actually save the ball!
        if (p.Role == TacticalRole.Goalkeeper && cmd.RequestKeeperDive && p.DribbleTouchTimer <= 0f)
        {
            Vector2 diveXZ = new(cmd.KeeperDiveDirection.X, cmd.KeeperDiveDirection.Z);
            if (diveXZ.LengthSquared() > 1e-4f)
            {
                currentVelXZ = Vector2.Normalize(diveXZ) * (7.2f * p.Attributes.GoalkeepingReflex01);
                p.DribbleTouchTimer = 0.55f;
                p.IsDiving = true;
            }
        }
        else if (p.DribbleTouchTimer <= 0f)
        {
            p.IsDiving = false;
        }

        float velY = p.Velocity.Y;
        float posY = p.Position.Y;
        if (cmd.RequestJumpHeader && posY <= 1e-3f && p.KickRecoveryTimer <= 0f)
        {
            velY = 3.6f;
            p.IsJumping = true;
        }

        posY += velY * dt;
        if (posY > 0f)
        {
            velY -= SimConstants.Gravity * dt;
        }
        else
        {
            posY = 0f;
            velY = 0f;
            p.IsJumping = false;
        }

        p.Velocity = new Vector3(currentVelXZ.X, velY, currentVelXZ.Y);
        Vector3 nextPos = p.Position + p.Velocity * dt;
        nextPos.Y = MathF.Max(0f, posY); // Never allow falling through the floor
        nextPos.X = Math.Clamp(nextPos.X, -_pitch.HalfLength - 3.0f, _pitch.HalfLength + 3.0f);
        nextPos.Z = Math.Clamp(nextPos.Z, -_pitch.HalfWidth - 3.0f, _pitch.HalfWidth + 3.0f);
        p.Position = nextPos;
    }

    /// <summary>Planar distance to the nearest active opponent (for the pressed-carrier slow-down).</summary>
    private static float NearestOpponentDistance(IReadOnlyList<FootballerState> players, FootballerState self)
    {
        float nearest = float.MaxValue;
        foreach (var other in players)
        {
            if (!other.IsActiveOnPitch || other.Team == self.Team) continue;
            float distance = Vector2.Distance(
                new Vector2(self.Position.X, self.Position.Z),
                new Vector2(other.Position.X, other.Position.Z));
            if (distance < nearest) nearest = distance;
        }
        return nearest;
    }

    private static void ResolvePlayerCollisions(IReadOnlyList<FootballerState> players)
    {
        float minDist = FootballerState.CapsuleRadius * 2.0f;
        int count = players.Count;
        for (int i = 0; i < count; i++)
        {
            var a = players[i];
            if (!a.IsActiveOnPitch) continue;
            for (int j = i + 1; j < count; j++)
            {
                var b = players[j];
                if (!b.IsActiveOnPitch) continue;

                // Audit F22: an airborne player whose body clears the other's head is not a
                // horizontal contact. Position.Y is the feet height, so the gap is the vertical
                // separation of the two standing bodies (a real jump still collides).
                if (MathF.Abs(a.Position.Y - b.Position.Y) >= FootballerState.PlayerHeight) continue;

                Vector2 diff = new(b.Position.X - a.Position.X, b.Position.Z - a.Position.Z);
                float dist = diff.Length();

                // Audit F22: exactly coincident centres were skipped, so two players could stand on
                // the same spot forever. Separate them along their relative motion when there is any,
                // otherwise along a fixed pitch axis, so the outcome is deterministic.
                Vector2 normal;
                if (dist > 1e-5f)
                {
                    normal = diff / dist;
                }
                else
                {
                    Vector2 relativeMotion = new(b.Velocity.X - a.Velocity.X, b.Velocity.Z - a.Velocity.Z);
                    normal = relativeMotion.LengthSquared() > 0.0025f
                        ? Vector2.Normalize(relativeMotion)
                        : new Vector2(1f, 0f);
                    dist = 0f;
                }

                if (dist >= minDist) continue;

                float overlap = minDist - dist;
                float totalStrength = a.Attributes.Strength01 + b.Attributes.Strength01;
                // Degenerate attributes must not divide by zero and poison both positions with NaNs.
                float shareA = totalStrength > 1e-4f ? b.Attributes.Strength01 / totalStrength : 0.5f;
                float shareB = totalStrength > 1e-4f ? a.Attributes.Strength01 / totalStrength : 0.5f;

                a.Position -= new Vector3(normal.X * overlap * shareA, 0f, normal.Y * overlap * shareA);
                b.Position += new Vector3(normal.X * overlap * shareB, 0f, normal.Y * overlap * shareB);

                Vector2 relV = new(b.Velocity.X - a.Velocity.X, b.Velocity.Z - a.Velocity.Z);
                float vn = Vector2.Dot(relV, normal);
                if (vn < 0f)
                {
                    Vector2 impulse = normal * (vn * 0.45f);
                    a.Velocity += new Vector3(impulse.X * shareA, 0f, impulse.Y * shareA);
                    b.Velocity -= new Vector3(impulse.X * shareB, 0f, impulse.Y * shareB);
                }
            }
        }
    }

    private void ResolveBallInteractions(
        IReadOnlyList<FootballerState> players,
        BallState ball,
        SeededRandom rng,
        float dt,
        Func<int, int, Vector3, float, bool>? onTackleContactCallback)
    {
        ContactAttempts.Clear();

        // Audit F03: a held ball can only be released by its holder, and only with a kick once the
        // hold delay has elapsed. No opponent tackle, deflection or foot contact may win it.
        if (ball.HeldByPlayerId is int heldById)
        {
            FootballerState? holder = FindActivePlayer(players, heldById);
            if (holder is null)
            {
                ReleaseHeldBall(ball);
                return;
            }

            bool keeperMayRelease = holder.LastCommand.KickType != KickIntentType.None &&
                                    holder.AvailableToKick &&
                                    ball.HeldReleaseDelaySeconds <= 0f;
            if (!keeperMayRelease)
            {
                RecordWantedAttempts(players, ContactRejectionReason.BallHeld);
                return;
            }

            RecordWantedAttempts(players, ContactRejectionReason.BallHeld, holder.Id);
            NoteAttempt(holder, ContactRejectionReason.None,
                holder.LastCommand.KickType != KickIntentType.None || holder.LastCommand.RequestTackle);
            ExecuteBallContact(holder, players, ball, rng);
            MarkAttemptExecuted(holder.Id);
            return;
        }

        FootballerState? bestChallenger = null;
        float bestChallengeScore = float.MinValue;

        // Audit F18: shielding is authoritative now. The holder protecting the ball with their body
        // wins more contests, and opponents contesting a shielded ball lose part of their challenge
        // score (a real tackle only half of it). Without this, ShieldBall was written by the
        // controllers and never read.
        int? shieldingHolderId = FindShieldingHolder(players, ball, out TeamId shieldingTeam);

        foreach (var p in players)
        {
            if (!p.IsActiveOnPitch) continue;

            bool wantedContact = p.LastCommand.KickType != KickIntentType.None || p.LastCommand.RequestTackle;

            // The player who just controlled the ball has a short exclusive claim on it: a plain
            // touch from anybody else (team-mate or opponent) is not allowed; a deliberate kick or a
            // tackle is. This is what stops two players on the same spot from taking turns to
            // teleport the ball between their feet on consecutive ticks.
            bool plainTouchOnly = p.LastCommand.KickType == KickIntentType.None && !p.LastCommand.RequestTackle;
            if (plainTouchOnly && _controlLockSeconds > 0f && _controlLockPlayerId >= 0 &&
                p.Id != _controlLockPlayerId)
            {
                continue;
            }

            // A team-mate never nicks the ball off a player who is still on it. Team-mates used to
            // compete in the same arbitration as opponents, so a second AI arriving at a loose ball
            // could repeatedly steal it from its own carrier.
            if (plainTouchOnly &&
                p.Id != ball.LastTouchedByPlayerId &&
                ball.LastTouchedByTeam == p.Team &&
                ball.LastTouchedByPlayerId is int currentOwnerId &&
                OwnerIsStillOnTheBall(players, currentOwnerId, ball))
            {
                continue;
            }

            // The player who just won a tackle cannot be tackled straight back: they get a short
            // protected window to take the ball away or play it. This is what breaks the two-player
            // tackle exchange (each winner was poked by the dispossessed player once he recovered).
            if (_tackleShieldSeconds > 0f &&
                _tackleShieldPlayerId >= 0 &&
                p.Id != _tackleShieldPlayerId &&
                p.Team != ball.LastTouchedByTeam &&
                ball.LastTouchedByPlayerId == _tackleShieldPlayerId)
            {
                continue;
            }

            Vector2 toBallXZ = new(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z);
            float planarDist = toBallXZ.Length();

            // Mild reception reach extension when receiving a pass from a teammate or controlling the ball (Section 3.2)
            bool isReceivingOrDribbling = BallContactRules.IsReceivingOrDribbling(
                ball.LastTouchedByTeam, ball.LastTouchedByPlayerId, p.Team, p.Id);

            float effectiveReach = BallContactRules.ReachRadius(
                p.Attributes.ReachRadius,
                p.Role == TacticalRole.Goalkeeper,
                p.IsDiving,
                p.LastCommand.RequestTackle,
                isReceivingOrDribbling);

            float maxVerticalReach = BallContactRules.MaxContactHeight(
                p.Role == TacticalRole.Goalkeeper,
                p.IsJumping || p.LastCommand.KickType == KickIntentType.Header);

            p.ControlQuality01 = 0f;

            if (planarDist > effectiveReach)
            {
                NoteAttempt(p, ContactRejectionReason.OutOfReach, wantedContact);
                continue;
            }

            float verticalDelta = ball.Position.Y - p.Position.Y;
            if (!BallContactRules.IsHeightReachable(ball.Position.Y, p.Position.Y, maxVerticalReach))
            {
                NoteAttempt(
                    p,
                    verticalDelta < BallContactRules.MinContactHeightDelta
                        ? ContactRejectionReason.OutOfReach
                        : ContactRejectionReason.BallTooHigh,
                    wantedContact);
                continue;
            }

            float facingAlignment = planarDist > 1e-4f
                ? Vector2.Dot(toBallXZ / planarDist, p.FacingXZ)
                : 1.0f;

            if (!BallContactRules.IsFacingTheBall(facingAlignment, isReceivingOrDribbling, p.Role == TacticalRole.Goalkeeper))
            {
                NoteAttempt(p, ContactRejectionReason.FacingAway, wantedContact);
                continue;
            }

            float proximityScore = 1.0f - (planarDist / effectiveReach);
            float control = Math.Clamp(
                0.45f * proximityScore + 0.35f * p.Attributes.FirstTouch01 + 0.20f * p.Balance01,
                0.15f, 1.0f);
            p.ControlQuality01 = control;

            if (!p.AvailableToKick)
            {
                NoteAttempt(p, ContactRejectionReason.InRecovery, wantedContact);
                continue;
            }

            bool wantsImmediateStrike = p.LastCommand.KickType != KickIntentType.None ||
                                        p.LastCommand.RequestTackle ||
                                        p.Role == TacticalRole.Goalkeeper;
            if (!wantsImmediateStrike && p.DribbleTouchTimer > 0f && ball.LastTouchedByPlayerId == p.Id)
                continue;

            // Give active tacklers a decisive +0.65 priority so defenders actually dispossess dribblers!
            float shieldAdjustment = 0f;
            if (p.LastCommand.ShieldBall)
                shieldAdjustment += BallContactRules.ShieldContestBonus;
            if (shieldingHolderId is int shieldedId && p.Id != shieldedId && p.Team != shieldingTeam)
            {
                shieldAdjustment -= BallContactRules.ShieldOpponentPenalty *
                                    (p.LastCommand.RequestTackle ? BallContactRules.ShieldTacklePenaltyFactor : 1f);
            }

            float challengeScore = control
                + (p.LastCommand.RequestTackle ? 0.65f : 0f)
                + (p.Role == TacticalRole.Goalkeeper ? 0.35f : 0f)
                + (ball.LastTouchedByPlayerId == p.Id ? 0.12f : 0f)
                + shieldAdjustment
                + rng.NextRange(-0.02f, 0.02f);

            // Provisional: still in the arbitration. It becomes Executed or LostArbitration below.
            NoteAttempt(p, ContactRejectionReason.None, wantedContact);

            if (challengeScore > bestChallengeScore)
            {
                bestChallengeScore = challengeScore;
                bestChallenger = p;
            }
        }

        if (bestChallenger == null)
            return;

        bool restartTriggered = false;
        if (bestChallenger.LastCommand.RequestTackle)
        {
            // A tackle is a short commitment, not a 0.4 s freeze. It used to slow the winner to 30 %
            // for 0.38 s and block their touch for the whole window, so the player who had just won
            // the ball walked away from it and the dispossessed player (recovering at 0.45 s) simply
            // picked it back up. Both players then kept trading the ball every half second.
            bestChallenger.TackleRecoveryTimer = 0.08f;
            foreach (var other in players)
            {
                if (!other.IsActiveOnPitch || other.Team == bestChallenger.Team) continue;
                float distToOther = Vector3.Distance(bestChallenger.Position, other.Position);
                float ballDistOther = Vector3.Distance(other.Position, ball.Position);
                if (distToOther < 1.35f && ballDistOther < 1.50f)
                {
                    // Stun the dispossessed ball carrier so they don't immediately steal the ball back
                    other.KickRecoveryTimer = 0.55f;
                    other.Balance01 = MathF.Max(0.25f, other.Balance01 - 0.40f);
                    float speed = bestChallenger.Velocity.Length();
                    float severity = (speed / 8.5f) * (1.05f - bestChallenger.ControlQuality01);
                    restartTriggered |= onTackleContactCallback?.Invoke(bestChallenger.Id, other.Id, other.Position, severity) == true;
                }
            }
        }

        // Audit F13: a foul may have just reset the ball and players for a restart. Executing the
        // normal ball contact here would overwrite that setup with a tackle outcome.
        if (restartTriggered)
        {
            ResolveProvisionalAttempts(ContactRejectionReason.RestartTriggered);
            return;
        }

        ExecuteBallContact(bestChallenger, players, ball, rng);
        MarkAttemptExecuted(bestChallenger.Id);
    }

    /// <summary>
    /// The player currently shielding the ball, if any: the last toucher, still on the ball and
    /// actively shielding with their body (audit F18).
    /// </summary>
    private static int? FindShieldingHolder(
        IReadOnlyList<FootballerState> players,
        BallState ball,
        out TeamId shieldingTeam)
    {
        shieldingTeam = default;
        if (ball.LastTouchedByPlayerId is not int holderId) return null;

        foreach (var p in players)
        {
            if (p.Id != holderId || !p.IsActiveOnPitch || !p.LastCommand.ShieldBall) continue;
            if (Vector3.Distance(p.Position, ball.Position) > p.Attributes.ReachRadius * 2.0f) continue;
            shieldingTeam = p.Team;
            return p.Id;
        }

        return null;
    }

    /// <summary>
    /// Records that a player asked for a ball contact. Only requested contacts are recorded: a plain
    /// first touch by a player who was not trying to kick or tackle is not an attempt (audit F17).
    /// </summary>
    private void NoteAttempt(FootballerState player, ContactRejectionReason reason, bool wantedContact)
    {
        if (!wantedContact) return;
        ContactAttempts.Add(new BallContactAttempt(
            _currentTick,
            player.Id,
            player.Team,
            player.LastCommand.KickType,
            player.LastCommand.RequestTackle,
            Executed: false,
            reason));
    }

    private void RecordWantedAttempts(
        IReadOnlyList<FootballerState> players,
        ContactRejectionReason reason,
        int exceptPlayerId = -1)
    {
        foreach (var p in players)
        {
            if (!p.IsActiveOnPitch || p.Id == exceptPlayerId) continue;
            bool wantedContact = p.LastCommand.KickType != KickIntentType.None || p.LastCommand.RequestTackle;
            NoteAttempt(p, reason, wantedContact);
        }
    }

    /// <summary>Marks the contact this player actually executed and settles the arbitration for the rest.</summary>
    private void MarkAttemptExecuted(int playerId)
    {
        for (int i = 0; i < ContactAttempts.Count; i++)
        {
            BallContactAttempt attempt = ContactAttempts[i];
            if (attempt.PlayerId == playerId)
            {
                ContactAttempts[i] = attempt with { Executed = true, Reason = ContactRejectionReason.None };
            }
            else if (!attempt.Executed && attempt.Reason == ContactRejectionReason.None)
            {
                ContactAttempts[i] = attempt with { Reason = ContactRejectionReason.LostArbitration };
            }
        }
    }

    private void ResolveProvisionalAttempts(ContactRejectionReason reason)
    {
        for (int i = 0; i < ContactAttempts.Count; i++)
        {
            BallContactAttempt attempt = ContactAttempts[i];
            if (!attempt.Executed && attempt.Reason == ContactRejectionReason.None)
                ContactAttempts[i] = attempt with { Reason = reason };
        }
    }

    private void ExecuteBallContact(
        FootballerState player,
        IReadOnlyList<FootballerState> allPlayers,
        BallState ball,
        SeededRandom rng)
    {
        var cmd = player.LastCommand;
        Vector2 preContactFacing = player.FacingXZ;

        // Audit F03: releasing a held ball. The ball leaves the hands onto the foot contact point
        // and the normal kick path below plays it; from this tick it is a free ball again.
        if (ball.HeldByPlayerId == player.Id)
        {
            Vector2 releaseAim = new(cmd.KickAimDirectionXZ.X, cmd.KickAimDirectionXZ.Z);
            releaseAim = SimMath.SafeNormalize(releaseAim, player.FacingXZ);

            ball.HeldByPlayerId = null;
            ball.HeldReleaseDelaySeconds = 0f;
            ball.Position = new Vector3(
                player.Position.X + releaseAim.X * 0.45f,
                0.11f,
                player.Position.Z + releaseAim.Y * 0.45f);
            ball.PreviousPosition = ball.Position;
            ball.Velocity = Vector3.Zero;
            ball.AngularVelocity = Vector3.Zero;
        }

        // Goalkeeper Save & Parry.
        // Audit F12: a save requires a ball actually travelling toward the keeper's own goal and
        // inside the keeper's own penalty area. The old check accepted any fast ball with an
        // opposing last touch, so a keeper could "save" a rebound or a back-pass moving away.
        if (player.Role == TacticalRole.Goalkeeper && ball.LastTouchedByTeam != player.Team && ball.Velocity.Length() > 6.5f)
        {
            float ownGoalX = player.Position.X >= 0f ? _pitch.HalfLength : -_pitch.HalfLength;
            bool movingTowardOwnGoal = ownGoalX > 0f ? ball.Velocity.X > 0f : ball.Velocity.X < 0f;
            bool insideOwnPenaltyArea =
                MathF.Abs(ball.Position.X - ownGoalX) <= _pitch.PenaltyAreaLength &&
                MathF.Abs(ball.Position.Z) <= _pitch.PenaltyAreaWidth * 0.5f;

            if (movingTowardOwnGoal && insideOwnPenaltyArea)
            {
                float shotSpeed = ball.Velocity.Length();
                if (shotSpeed < 17.5f * player.Attributes.GoalkeepingReflex01)
                {
                    // Clean Catch — audit F03: the ball is authoritatively held in the hands, not a
                    // loose ball nudged 0.42 m in front. It follows the keeper, is excluded from
                    // physics and from every contact, and is only released by a deliberate kick.
                    ball.Position = HeldBallPosition(player);
                    ball.PreviousPosition = ball.Position;
                    ball.Velocity = Vector3.Zero;
                    ball.AngularVelocity = Vector3.Zero;
                    ball.HeldByPlayerId = player.Id;
                    ball.HeldReleaseDelaySeconds = 0.55f;
                    ball.LastTouchedByPlayerId = player.Id;
                    ball.LastTouchedByTeam = player.Team;
                    player.KickRecoveryTimer = 0.15f;
                    player.DribbleTouchTimer = 0f;
                    player.LastExecutionDiagnostic = "GK:CleanCatch";
                    EmitContact(player, BallContactKind.Save, ball);
                }
                else
                {
                    // Audit F02: a parry used to keep the incoming rolling spin, which then drove the
                    // ball back into the goal. Drive the exit from the ball's own incoming path (back
                    // out of the goal) plus lateral clearance, and give it a small controlled spin.
                    Vector2 incomingXZ = new(ball.Velocity.X, ball.Velocity.Z);
                    Vector2 backOut = -SimMath.SafeNormalize(incomingXZ, player.FacingXZ);
                    float sideSign = ball.Position.Z >= player.Position.Z ? 1f : -1f;
                    Vector2 parryDir = SimMath.SafeNormalize(backOut + new Vector2(0f, sideSign * 0.85f), player.FacingXZ);

                    float parrySpeed = Math.Clamp(shotSpeed * 0.45f, 6.0f, 12.0f);
                    ball.Velocity = new Vector3(parryDir.X * parrySpeed, 1.4f, parryDir.Y * parrySpeed);
                    // Controlled rolling spin matching the new direction; incoming spin is discarded.
                    ball.AngularVelocity = new Vector3(parryDir.Y * 6f, 0f, -parryDir.X * 6f);

                    ball.LastTouchedByPlayerId = player.Id;
                    ball.LastTouchedByTeam = player.Team;
                    player.KickRecoveryTimer = 0.75f;
                    player.LastExecutionDiagnostic = "GK:ReflexParry";
                    EmitContact(player, BallContactKind.Save, ball, isParry: true);
                }
                return;
            }
        }

        float pressure01 = 0f;
        foreach (var opp in allPlayers)
        {
            if (!opp.IsActiveOnPitch || opp.Team == player.Team) continue;
            float d = Vector3.Distance(player.Position, opp.Position);
            if (d < 3.0f)
                pressure01 = MathF.Max(pressure01, (3.0f - d) / 3.0f);
        }

        // Case 1: Explicit Kick Intention (Passes & Shots)
        if (cmd.KickType != KickIntentType.None)
        {
            Vector2 aimXZ = new(cmd.KickAimDirectionXZ.X, cmd.KickAimDirectionXZ.Z);
            if (aimXZ.LengthSquared() < 1e-4f)
                aimXZ = player.FacingXZ;
            else
                aimXZ = Vector2.Normalize(aimXZ);

            // Alignment is judged on the body facing BEFORE the kick turns the player (audit F18):
            // the old code assigned FacingXZ = aim first, so the dot product was always 1.
            float bodyAlignment = Math.Clamp(Vector2.Dot(aimXZ, preContactFacing), -1f, 1f);
            float alignmentQuality = 0.75f + 0.25f * bodyAlignment;

            // Strong/weak-side strike: shooting across the body to the weak-foot side is less
            // accurate (an outside-of-the-foot or weak-foot contact).
            bool isShotIntent = cmd.KickType is KickIntentType.Shot or KickIntentType.CurledShot
                or KickIntentType.ChipShot or KickIntentType.TrivelaShot;
            float footPenalty = 1f;
            string strikeNote = string.Empty;
            if (isShotIntent)
            {
                Vector2 rightOfBody = new(-preContactFacing.Y, preContactFacing.X);
                float aimSide = Vector2.Dot(aimXZ, rightOfBody);
                bool leftFooted = player.Attributes.PreferredFoot == Footedness.Left;
                bool acrossBody = aimSide * (leftFooted ? -1f : 1f) < -0.22f;
                footPenalty = acrossBody ? 0.84f : 1f;
                strikeNote = acrossBody ? " OffFoot" : " StrongFoot";
            }

            float effectiveAccuracy = player.Attributes.FootAccuracy01 *
                                      alignmentQuality *
                                      footPenalty *
                                      (1.0f - 0.15f * pressure01 * (1.0f - player.Attributes.Composure01));

            player.FacingXZ = aimXZ;

            float maxAngularErrorRad = (1.0f - effectiveAccuracy) * 0.04f;
            float angularError = rng.NextGaussian(0f, maxAngularErrorRad * 0.4f);
            float cosE = MathF.Cos(angularError);
            float sinE = MathF.Sin(angularError);
            Vector2 actualDirXZ = new(
                aimXZ.X * cosE - aimXZ.Y * sinE,
                aimXZ.X * sinE + aimXZ.Y * cosE);

            float power01 = Math.Clamp(cmd.KickPower01, 0.15f, 1.0f);

            Vector3 launchVel;
            Vector3 launchSpin = Vector3.Zero;

            switch (cmd.KickType)
            {
                case KickIntentType.GroundPass:
                case KickIntentType.ThroughPass:
                {
                    // Launch geometry lives in KickLaunchModel (A5.5) so the AI's flight prediction and the
                    // execution cannot drift apart.
                    KickLaunch launch = KickLaunchModel.For(cmd.KickType, power01, actualDirXZ, cmd.RequestedCurveSpin);
                    launchVel = launch.Velocity;
                    launchSpin = launch.Spin;
                    player.KickRecoveryTimer = 0.26f;
                    break;
                }
                case KickIntentType.DrivenPass:
                {
                    KickLaunch launch = KickLaunchModel.For(cmd.KickType, power01, actualDirXZ, cmd.RequestedCurveSpin);
                    launchVel = launch.Velocity;
                    launchSpin = launch.Spin;
                    player.KickRecoveryTimer = 0.28f;
                    break;
                }
                case KickIntentType.LoftedPass:
                case KickIntentType.LobbedThroughPass:
                {
                    KickLaunch launch = KickLaunchModel.For(cmd.KickType, power01, actualDirXZ, cmd.RequestedCurveSpin);
                    launchVel = launch.Velocity;
                    launchSpin = launch.Spin;
                    player.KickRecoveryTimer = 0.30f;
                    break;
                }
                case KickIntentType.Cross:
                {
                    KickLaunch launch = KickLaunchModel.For(cmd.KickType, power01, actualDirXZ, cmd.RequestedCurveSpin);
                    launchVel = launch.Velocity;
                    launchSpin = launch.Spin;
                    player.KickRecoveryTimer = 0.32f;
                    break;
                }
                case KickIntentType.Shot:
                case KickIntentType.CurledShot:
                case KickIntentType.ChipShot:
                case KickIntentType.TrivelaShot:
                {
                    // FIFA-style power curve: a tap is a low driven shot, 80–90 % is the sweet spot
                    // under the bar, and full power sails over it. The launch height is solved from
                    // the distance to the goal line, so the same power behaves sensibly at any range.
                    launchVel = ResolveShotLaunch(player, ball, actualDirXZ, power01, cmd.KickType,
                        cmd.RequestedCurveSpin, out launchSpin);
                    player.KickRecoveryTimer = 0.35f;
                    break;
                }
                case KickIntentType.Header:
                {
                    float speed = Math.Clamp(12.0f + power01 * 8.0f, 11.0f, 20.0f);
                    launchVel = new Vector3(actualDirXZ.X * speed, 0.5f + power01 * 1.5f, actualDirXZ.Y * speed);
                    player.KickRecoveryTimer = 0.32f;
                    break;
                }
                case KickIntentType.Clearance:
                default:
                {
                    float horizSpeed = 19.0f + power01 * 9.0f;
                    launchVel = new Vector3(actualDirXZ.X * horizSpeed, 8.5f + power01 * 4.5f, actualDirXZ.Y * horizSpeed);
                    player.KickRecoveryTimer = 0.35f;
                    break;
                }
            }

            ball.Velocity = launchVel;
            ball.AngularVelocity = launchSpin;
            ball.LastTouchedByPlayerId = player.Id;
            ball.LastTouchedByTeam = player.Team;
            // A deliberate kick ends the post-tackle protection: the pass/shot is a normal loose
            // ball the opponent may read and intercept.
            if (player.Id == _tackleShieldPlayerId)
            {
                _tackleShieldPlayerId = -1;
                _tackleShieldSeconds = 0f;
            }
            player.LastExecutionDiagnostic =
                $"Kick:{cmd.KickType} Pwr={power01 * 100f:F0}% Align={alignmentQuality * 100f:F0}%{strikeNote}";
            EmitContact(player, BallContactKind.Kick, ball, cmd.KickType, cmd.IntendedReceiverId);
            return;
        }

        // Case 2: Standing Tackle Dispossession (Cleanly wins the ball into the tackler's stride)
        if (cmd.RequestTackle)
        {
            Vector2 winDir = cmd.DesiredMoveDirectionXZ.LengthSquared() > 1e-4f
                ? Vector2.Normalize(cmd.DesiredMoveDirectionXZ)
                : player.FacingXZ;

            // Place the won ball just in front of the tackler's stride so they take control. The
            // ball keeps only a fraction of the tackler's run: the old +1.8 m/s on top of full sprint
            // launched it 2 m away from the player who had just won it.
            ball.Position = new Vector3(
                player.Position.X + winDir.X * 0.45f,
                0.11f,
                player.Position.Z + winDir.Y * 0.45f);
            ball.Velocity = new Vector3(
                player.Velocity.X * 0.55f + winDir.X * 0.6f,
                0.1f,
                player.Velocity.Z * 0.55f + winDir.Y * 0.6f);
            ball.AngularVelocity = Vector3.Zero;
            ball.LastTouchedByPlayerId = player.Id;
            ball.LastTouchedByTeam = player.Team;
            // The winner must be able to take the ball they just won: a long touch lock here is what
            // let the ball roll away towards the recovering victim.
            player.KickRecoveryTimer = 0.08f;
            player.LastExecutionDiagnostic = "TackleWonBall!";
            LockBallToToucher(player.Id);
            _tackleShieldPlayerId = player.Id;
            _tackleShieldSeconds = TackleShieldSeconds;
            EmitContact(player, BallContactKind.Tackle, ball);
            return;
        }

        // Case 3: controlled touch — a cushioned first touch and a dribble that keeps the ball at
        // the feet. A plain touch used to teleport the ball up to 1.2 m toward a "sweet spot" and
        // hand it the carrier's whole speed plus a 2.6 m/s spring kick; that read as the AI kicking
        // the ball away on its first touch and made the ball jitter whenever players converged.
        bool isMoving = cmd.DesiredMoveDirectionXZ.LengthSquared() > 1e-4f;
        Vector2 controlDirXZ = isMoving ? Vector2.Normalize(cmd.DesiredMoveDirectionXZ) : player.FacingXZ;
        float sweetSpotLeadDist = !isMoving ? 0.30f : (cmd.Sprint ? 0.53f : 0.42f);

        Vector2 ballXZ = new(ball.Position.X, ball.Position.Z);
        Vector2 playerXZ = new(player.Position.X, player.Position.Z);
        Vector2 playerVelXZ = new(player.Velocity.X, player.Velocity.Z);
        Vector2 sweetSpotXZ = playerXZ + controlDirXZ * sweetSpotLeadDist;
        Vector2 toSweetSpot = sweetSpotXZ - ballXZ;

        bool isFirstTouchReception = ball.LastTouchedByPlayerId != player.Id;

        // First-touch quality: a sure touch kills almost all of the incoming pace and keeps the ball
        // on the player's foot; a poor one lets the ball run away from them.
        float controlQuality = Math.Clamp(
            0.50f * player.Attributes.FirstTouch01 +
            0.30f * player.Balance01 +
            0.20f * player.ControlQuality01, 0.15f, 1f);

        if (isFirstTouchReception)
        {
            Vector2 incomingXZ = new(ball.Velocity.X, ball.Velocity.Z);
            float incomingSpeed = incomingXZ.Length();

            // Incoming pace retained by the touch: near zero for a clean trap, up to ~30 % for a
            // bad one — never the whole pass speed.
            Vector2 retained = incomingSpeed > 1e-4f
                ? incomingXZ * ((1f - controlQuality) * 0.30f)
                : Vector2.Zero;

            // Gentle settle velocity toward the sweet spot (no position jump): a ball already past
            // the foot is checked back, a ball short of it is nudged on.
            float toSweetSpotLen = toSweetSpot.Length();
            Vector2 settleDir = toSweetSpotLen > 1e-4f ? toSweetSpot / toSweetSpotLen : Vector2.Zero;
            float settleSpeed = MathF.Min(0.9f, 0.35f + toSweetSpotLen * 1.2f);

            Vector2 cushionedVel = playerVelXZ * 0.95f + settleDir * settleSpeed + retained;
            float maxFirstTouchSpeed = playerVelXZ.Length() + 1.2f + (1f - controlQuality) * incomingSpeed * 0.15f;
            cushionedVel = ClampVectorLength(cushionedVel, maxFirstTouchSpeed);

            ball.Velocity = new Vector3(cushionedVel.X, MathF.Min(ball.Velocity.Y * 0.10f, 0.20f), cushionedVel.Y);

            // The cushion may nudge the ball a few centimetres onto the foot, never by the old 55 %
            // lerp (that was a ~1 m teleport, which the eye reads as a kick).
            Vector2 correction = ClampVectorLength(toSweetSpot, 0.15f);
            ball.Position = new Vector3(ballXZ.X + correction.X, MathF.Min(ball.Position.Y, 0.15f), ballXZ.Y + correction.Y);

            player.DribbleTouchTimer = 0.07f;
            player.LastExecutionDiagnostic = "FirstTouchCushion";
        }
        else
        {
            // Dribble touch: a spring that keeps the ball inside a short carry distance. The result
            // is capped so a touch can never launch the ball faster than the player plus the spring
            // allowance — the ball has to stay *with* the dribbler, not race ahead of them.
            const float springGain = 6.0f;
            const float maxSpringSpeed = 2.6f;
            Vector2 spring = ClampVectorLength(toSweetSpot * springGain, maxSpringSpeed);
            Vector2 targetVel = ClampVectorLength(
                playerVelXZ + spring, playerVelXZ.Length() + maxSpringSpeed);

            ball.Velocity = new Vector3(targetVel.X, MathF.Min(ball.Velocity.Y * 0.12f, 0.20f), targetVel.Y);

            Vector2 correction = ClampVectorLength(toSweetSpot, 0.10f);
            ball.Position = new Vector3(ballXZ.X + correction.X, MathF.Min(ball.Position.Y, 0.15f), ballXZ.Y + correction.Y);

            player.DribbleTouchTimer = 0.04f;
            player.LastExecutionDiagnostic = cmd.Sprint ? "SprintDribble" : "CloseDribble";
        }

        ball.AngularVelocity = new Vector3(controlDirXZ.Y * 6f, 0f, -controlDirXZ.X * 6f);
        ball.LastTouchedByPlayerId = player.Id;
        ball.LastTouchedByTeam = player.Team;
        LockBallToToucher(player.Id);
        EmitContact(player, BallContactKind.Collection, ball);
    }

    /// <summary>
    /// Gives the player who just controlled the ball a very short exclusive claim on it. Without
    /// this, two players standing on the same spot re-touched the ball on alternating ticks and
    /// threw it back and forth between their feet (the "something weird" and camera shake when two
    /// AI players get close). Only an opponent's tackle can break the claim.
    /// </summary>
    private void LockBallToToucher(int playerId)
    {
        _controlLockPlayerId = playerId;
        _controlLockSeconds = ControlLockSeconds;
    }

    private static Vector2 ClampVectorLength(Vector2 v, float maxLength)
    {
        float length = v.Length();
        if (!float.IsFinite(length) || length <= maxLength || length <= 1e-5f) return v;
        return v * (maxLength / length);
    }

    /// <summary>True while <paramref name="ownerId"/> is still physically on the ball.</summary>
    private static bool OwnerIsStillOnTheBall(
        IReadOnlyList<FootballerState> players,
        int ownerId,
        BallState ball)
    {
        FootballerState? owner = FindActivePlayer(players, ownerId);
        if (owner is null || !owner.AvailableToKick) return false;

        float reach = BallContactRules.ReachRadius(
            owner.Attributes.ReachRadius,
            owner.Role == TacticalRole.Goalkeeper,
            owner.IsDiving,
            requestingTackle: false,
            receivingOrDribbling: true);
        return Vector2.Distance(
            new Vector2(owner.Position.X, owner.Position.Z),
            new Vector2(ball.Position.X, ball.Position.Z)) <= reach;
    }

    /// <summary>
    /// FIFA-style shot launch: horizontal speed grows with power while the height at the goal line
    /// follows a curve whose sweet spot is ~80–90 %; 100 % clears the bar. The vertical speed is
    /// solved from the distance to the goal line so the same power works at any range.
    /// </summary>
    private Vector3 ResolveShotLaunch(
        FootballerState player,
        BallState ball,
        Vector2 dir,
        float power01,
        KickIntentType type,
        float curveRequest,
        out Vector3 spin)
    {
        float speed = type switch
        {
            KickIntentType.CurledShot => Math.Clamp(19f + 8f * power01, 22f, 28f),
            KickIntentType.TrivelaShot => Math.Clamp(21f + 9f * power01, 24f, 30f),
            KickIntentType.ChipShot => Math.Clamp(13f + 6f * power01, 14f, 20f),
            _ => Math.Clamp(20f + 10f * power01, 24f, 31f)
        };

        // Audit F18: ShotPowerMax was another advertised-but-inert attribute (the shot speed was
        // hard-coded). Scaling by it keeps the 31 m/s default identical and makes a weaker shooter
        // weaker; the launch height below is solved from the resulting speed, so aim stays honest.
        speed *= player.Attributes.ShotPowerMax / 31.0f;

        float goalLineX = dir.X >= 0f ? _pitch.HalfLength : -_pitch.HalfLength;
        float dx = goalLineX - player.Position.X;
        float distanceToGoal = Math.Clamp(
            MathF.Abs(dx / MathF.Max(0.2f, MathF.Abs(dir.X))), 3f, 60f);

        float targetHeight = ShotTargetHeight(power01);
        if (type == KickIntentType.CurledShot)
            targetHeight += 0.10f;   // slower flight drops more, so aim a little higher
        else if (type == KickIntentType.TrivelaShot)
            targetHeight += 0.0f;
        else if (type == KickIntentType.ChipShot)
            targetHeight = 1.20f;    // a chip must drop under the bar, not clear it

        float flightTime = MathF.Max(0.16f, distanceToGoal / speed * 1.10f);
        float vertical = (targetHeight - ball.Position.Y
                          + 0.5f * SimConstants.Gravity * flightTime * flightTime) / flightTime;
        vertical = Math.Clamp(vertical, 0.5f, 20f);
        if (type == KickIntentType.ChipShot)
            vertical = MathF.Max(vertical, 5.5f);

        // Sidespin bends the ball back toward the goal centre from the aimed corner; a trivela
        // bends the other way. The controller already signs the request.
        float curve = MathF.Abs(curveRequest) > 0.05f ? curveRequest : 0f;
        spin = type switch
        {
            // A visible curler that bends back toward the goal centre from the aimed corner.
            KickIntentType.CurledShot => new Vector3(dir.Y * 10f, (curve == 0f ? 1f : curve) * 55f, -dir.X * 10f),
            // Trivela: outside-foot contact, a flatter and slightly weaker bend than the finesse.
            KickIntentType.TrivelaShot => new Vector3(dir.Y * 10f, (curve == 0f ? 1f : curve) * 50f, -dir.X * 10f),
            KickIntentType.ChipShot => new Vector3(-dir.Y * 18f, 0f, dir.X * 18f),
            _ => new Vector3(dir.Y * 12f, 0f, -dir.X * 12f)
        };

        return new Vector3(dir.X * speed, vertical, dir.Y * speed);
    }

    /// <summary>
    /// Height the shot should reach at the goal line for a given power. Low power stays on the deck,
    /// ~85 % is just under the crossbar, and 100 % flies over.
    /// </summary>
    private static float ShotTargetHeight(float power01)
    {
        if (power01 <= 0.36f) return 0.28f;
        if (power01 <= 0.75f) return 0.28f + (power01 - 0.36f) / 0.39f * 2.20f;
        if (power01 <= 0.92f) return 2.48f + (power01 - 0.75f) / 0.17f * 0.70f;
        return 3.18f + (power01 - 0.92f) / 0.08f * 1.50f;
    }

    /// <summary>
    /// Publishes a play/touch for the rules engine. Emitted exactly where the ball's
    /// <see cref="BallState.LastTouchedByPlayerId"/> is mutated, so a contact can never be missed.
    /// </summary>
    private void EmitContact(
        FootballerState player,
        BallContactKind kind,
        BallState ball,
        KickIntentType intent = KickIntentType.None,
        int? intendedReceiverId = null,
        bool isParry = false)
    {
        ContactEvents.Add(new BallContactEvent(_currentTick, kind, player.Id, player.Team, ball.Position)
        {
            Intent = intent,
            IntendedReceiverId = intendedReceiverId,
            IsParry = isParry
        });
    }

    private static float WrapAngle(float radians)
    {
        while (radians > MathF.PI) radians -= 2.0f * MathF.PI;
        while (radians < -MathF.PI) radians += 2.0f * MathF.PI;
        return radians;
    }
}
