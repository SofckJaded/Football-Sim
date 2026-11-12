using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using FootballSim.Core;

namespace FootballSim.Physics;

public enum BallContactSurface
{
    Air,
    RollingOrSlidingGround,
    GoalPostOrCrossbar,
    GoalNet
}

/// <summary>
/// The one source of truth for the ball's physical size (audit F22).
///
/// The audit found the match and practice scenes drawing a 0.16 m-radius sphere while physics,
/// contacts and the boundary rules all used 0.11 m, so apparent contact and reach disagreed with the
/// simulated ones. Presentation now derives every ball dimension from the live
/// <see cref="BallPhysicsParameters.Radius"/> through these helpers instead of owning constants.
/// </summary>
public static class BallGeometry
{
    /// <summary>Default physical radius (IFAB Law 2: circumference 0.68-0.70 m).</summary>
    public const float DefaultRadius = 0.11f;

    /// <summary>
    /// The ground shadow is drawn wider than the ball so it reads at broadcast zoom. It is a
    /// presentation-only factor applied to the physical radius, never to physics.
    /// </summary>
    public const float ShadowRadiusFactor = 2.2f;

    /// <summary>Sphere radius to render for a physical ball radius.</summary>
    public static float SphereRadius(float physicsRadius) =>
        float.IsFinite(physicsRadius) && physicsRadius > 0f ? physicsRadius : DefaultRadius;

    /// <summary>Full sphere height (diameter) to render for a physical ball radius.</summary>
    public static float Diameter(float physicsRadius) => SphereRadius(physicsRadius) * 2f;

    /// <summary>Ground-shadow radius drawn for a physical ball radius.</summary>
    public static float ShadowRadius(float physicsRadius) => SphereRadius(physicsRadius) * ShadowRadiusFactor;

    /// <summary>Height of the ball's centre when it rests on the grass.</summary>
    public static float RestingCentreHeight(float physicsRadius) => SphereRadius(physicsRadius);
}

public sealed class BallPhysicsParameters
{
    // IFAB Law 2 official parameters: mass 0.41-0.45 kg, circumference 0.68-0.70 m (radius ~0.11 m)
    public float Mass { get; init; } = 0.43f;

    /// <summary>
    /// Physical ball radius, in metres. Rendering must read this through
    /// <see cref="BallGeometry"/> instead of hard-coding its own sphere size (audit F22).
    /// </summary>
    public float Radius { get; init; } = BallGeometry.DefaultRadius;
    public float AirDensity { get; init; } = 1.225f;
    public float DragCoeffSubcritical { get; init; } = 0.42f;
    public float DragCoeffSupercritical { get; init; } = 0.24f;
    public float CriticalSpeed { get; init; } = 12.5f;
    public float MagnusLiftScale { get; init; } = 1.15f;
    public float MaxMagnusCoeff { get; init; } = 0.36f;
    public float AirSpinDecayRate { get; init; } = 0.18f;
    public float GroundRestitution { get; init; } = 0.60f;
    public float PostRestitution { get; init; } = 0.72f;
    public float SlidingFriction { get; init; } = 0.34f;
    public float RollingResistance { get; init; } = 0.048f;
    public float BounceTangentialGrip { get; init; } = 0.28f;
    public float RestBounceVelocityThreshold { get; init; } = 0.20f;
    public float StopVelocityEpsilon { get; init; } = 0.03f;

    public float CrossSectionArea => MathF.PI * Radius * Radius;
    public float MomentOfInertia => (2.0f / 3.0f) * Mass * Radius * Radius;
}

public readonly record struct BallCollisionEvent(
    float TimeSeconds,
    string SurfaceType,
    Vector3 ContactPoint,
    Vector3 IncomingVelocity,
    Vector3 OutgoingVelocity
);

/// <summary>Which pitch line the ball crossed (audit F20).</summary>
public enum BallBoundary
{
    /// <summary>The goal line at X = -HalfLength.</summary>
    GoalLineNegativeX,
    /// <summary>The goal line at X = +HalfLength.</summary>
    GoalLinePositiveX,
    /// <summary>The touchline at Z = -HalfWidth.</summary>
    TouchlineNegativeZ,
    /// <summary>The touchline at Z = +HalfWidth.</summary>
    TouchlinePositiveZ
}

/// <summary>
/// The first moment the WHOLE ball crossed a pitch line (IFAB Law 9), captured from the solver's
/// substep trajectory instead of the tick's end position (audit F20). <see cref="CrossingPosition"/>
/// is the ball's centre when its leading edge passed the line, <see cref="LineCoordinate"/> is the
/// signed line coordinate the plane sat at (the line offset by the ball radius), and
/// <see cref="ElapsedSeconds"/> orders several crossings inside one tick.
/// </summary>
public readonly record struct BallBoundaryCrossing(
    BallBoundary Boundary,
    Vector3 CrossingPosition,
    float LineCoordinate,
    float ElapsedSeconds,
    float BallRadius
);

public sealed class BallState
{
    public Vector3 Position { get; set; }
    public Vector3 PreviousPosition { get; set; }
    public Vector3 Velocity { get; set; }
    public Vector3 AngularVelocity { get; set; } // rad/s; Y = sidespin, X/Z = topspin/backspin
    public BallContactSurface ContactState { get; set; } = BallContactSurface.Air;
    public int? LastTouchedByPlayerId { get; set; }
    public TeamId? LastTouchedByTeam { get; set; }
    public List<BallCollisionEvent> RecentCollisions { get; } = new();

    /// <summary>
    /// Boundary crossings produced by <see cref="BallPhysicsSolver.Step"/> for the current tick, in
    /// the order they happened (audit F20). Cleared at the start of every step, so it always describes
    /// the tick the rules engine is about to judge.
    /// </summary>
    public List<BallBoundaryCrossing> BoundaryCrossings { get; } = new();

    /// <summary>
    /// Player authoritatively holding the ball in their hands (a goalkeeper catch, audit F03).
    /// While set, the ball is attached to the holder, excluded from physics integration and from
    /// every player contact until the holder releases it.
    /// </summary>
    public int? HeldByPlayerId { get; set; }

    /// <summary>Seconds the holder must wait before being allowed to release (audit F03).</summary>
    public float HeldReleaseDelaySeconds { get; set; }

    /// <summary>
    /// Number of times the solver had to replace a non-finite state with a safe one.
    /// Non-zero means something upstream is emitting invalid vectors (audit F09).
    /// </summary>
    public int InvalidStateRecoveries { get; set; }

    public BallState Clone() => new()
    {
        Position = Position,
        PreviousPosition = PreviousPosition,
        Velocity = Velocity,
        AngularVelocity = AngularVelocity,
        ContactState = ContactState,
        LastTouchedByPlayerId = LastTouchedByPlayerId,
        LastTouchedByTeam = LastTouchedByTeam,
        HeldByPlayerId = HeldByPlayerId,
        HeldReleaseDelaySeconds = HeldReleaseDelaySeconds,
        InvalidStateRecoveries = InvalidStateRecoveries
    };
}

/// <summary>
/// Authoritative 3D ball physics solver implementing gravity, Reynolds-dependent drag,
/// spin-dependent Magnus force, bounce restitution with spin-velocity coupling,
/// slide-to-roll transition, rolling resistance, and continuous/swept collision checks.
/// </summary>
public sealed class BallPhysicsSolver
{
    private readonly BallPhysicsParameters _params;
    private readonly PitchDimensions _pitch;
    private float _elapsedTime;

    public BallPhysicsSolver(BallPhysicsParameters? parameters = null, PitchDimensions? pitch = null)
    {
        _params = parameters ?? new BallPhysicsParameters();
        _pitch = pitch ?? new PitchDimensions();
        // Audit F22: the radius is shared with rendering, so a degenerate parameter set is rejected
        // here instead of drawing a wrong ball or dividing by a zero radius.
        SimulationConfigValidation.ValidateBallPhysics(_params);
        SimulationConfigValidation.ValidatePitch(_pitch);
    }

    public BallPhysicsParameters Parameters => _params;

    public void Step(BallState ball, float dt)
    {
        ball.RecentCollisions.Clear();
        ball.BoundaryCrossings.Clear();

        // Audit F03: a held ball is attached to the player's hands, not a free body. It must not
        // fall, bounce, roll or be moved by any aerodynamic/ground force until it is released.
        if (ball.HeldByPlayerId.HasValue)
        {
            ball.PreviousPosition = ball.Position;
            ball.Velocity = Vector3.Zero;
            ball.AngularVelocity = Vector3.Zero;
            return;
        }

        // Reject a non-finite or non-positive timestep before it can poison the integrator.
        if (!float.IsFinite(dt) || dt <= 0f) return;
        ball.PreviousPosition = ball.Position;

        // Adaptive substepping so the ball never travels more than half its radius per substep
        float speed = ball.Velocity.Length();
        int adaptiveSteps = Math.Clamp((int)MathF.Ceiling((speed * dt) / (_params.Radius * 0.45f)), SimConstants.PhysicsSubsteps, 24);
        float subDt = dt / adaptiveSteps;

        float tickElapsed = 0f;
        for (int i = 0; i < adaptiveSteps; i++)
        {
            _elapsedTime += subDt;
            StepSubtick(ball, subDt, tickElapsed);
            tickElapsed += subDt;
        }
    }

    private void StepSubtick(BallState ball, float dt, float tickElapsed)
    {
        Vector3 startPos = ball.Position;
        Vector3 v = ball.Velocity;
        Vector3 omega = ball.AngularVelocity;

        bool isGrounded = startPos.Y <= _params.Radius + 1e-4f && MathF.Abs(v.Y) < _params.RestBounceVelocityThreshold;

        // 1. Compute Aerodynamic Forces (Drag + Magnus)
        float speed = v.Length();
        Vector3 accel = Vector3.Zero;

        if (!isGrounded)
        {
            accel.Y -= SimConstants.Gravity;
            ball.ContactState = BallContactSurface.Air;
        }
        else
        {
            ball.ContactState = BallContactSurface.RollingOrSlidingGround;
        }

        if (speed > 1e-4f)
        {
            // Smooth Reynolds transition between subcritical and supercritical drag coefficient
            float blend = 1.0f / (1.0f + MathF.Exp(-(speed - _params.CriticalSpeed) * 0.6f));
            float cd = _params.DragCoeffSubcritical * (1.0f - blend) + _params.DragCoeffSupercritical * blend;
            float dragMag = 0.5f * _params.AirDensity * cd * _params.CrossSectionArea * speed * speed;
            accel += (-v / speed) * (dragMag / _params.Mass);

            // Spin-dependent Magnus force: F_m = 0.5 * rho * C_L * A * v^2 * unit(omega x v)
            float omegaMag = omega.Length();
            if (omegaMag > 1e-3f)
            {
                Vector3 cross = Vector3.Cross(omega, v);
                float crossLen = cross.Length();
                if (crossLen > 1e-5f)
                {
                    float spinRatio = (_params.Radius * omegaMag) / speed;
                    float cl = MathF.Min(_params.MaxMagnusCoeff, _params.MagnusLiftScale * spinRatio);
                    float magnusMag = 0.5f * _params.AirDensity * cl * _params.CrossSectionArea * speed * speed;
                    accel += (cross / crossLen) * (magnusMag / _params.Mass);
                }
            }
        }

        // Semi-implicit Euler velocity update
        v += accel * dt;
        omega *= MathF.Exp(-_params.AirSpinDecayRate * dt);

        // 2. Ground Rolling / Sliding Mechanics when in ground contact
        if (isGrounded)
        {
            v.Y = 0f;
            Vector3 groundNormal = Vector3.UnitY;
            Vector3 contactOffset = new(0f, -_params.Radius, 0f);
            // Contact point velocity relative to ground: v_slip = v_cm + omega x r_contact
            Vector3 vSlip = new Vector3(v.X, 0f, v.Z) + Vector3.Cross(omega, contactOffset);
            float slipSpeed = new Vector2(vSlip.X, vSlip.Z).Length();

            if (slipSpeed > 0.08f)
            {
                // Sliding friction decelerates slip and torques spin toward pure rolling
                Vector3 slipDir = new Vector3(vSlip.X, 0f, vSlip.Z) / slipSpeed;
                float frictionDecel = _params.SlidingFriction * SimConstants.Gravity;
                float deltaV = MathF.Min(slipSpeed / 3.5f, frictionDecel * dt);
                v -= slipDir * deltaV;

                Vector3 frictionForce = -slipDir * (_params.Mass * frictionDecel);
                Vector3 torque = Vector3.Cross(contactOffset, frictionForce);
                omega += (torque / _params.MomentOfInertia) * dt;
            }
            else
            {
                // Pure rolling resistance
                Vector3 horizV = new(v.X, 0f, v.Z);
                float horizSpeed = horizV.Length();
                if (horizSpeed > _params.StopVelocityEpsilon)
                {
                    float rollDecel = _params.RollingResistance * SimConstants.Gravity;
                    float newSpeed = MathF.Max(0f, horizSpeed - rollDecel * dt);
                    v.X = (horizV.X / horizSpeed) * newSpeed;
                    v.Z = (horizV.Z / horizSpeed) * newSpeed;
                    // Lock rolling angular velocity: omega = (n x v) / R
                    omega.X = v.Z / _params.Radius;
                    omega.Z = -v.X / _params.Radius;
                    omega.Y *= MathF.Exp(-2.5f * dt);
                }
                else
                {
                    v = Vector3.Zero;
                    omega = Vector3.Zero;
                }
            }
        }

        Vector3 nextPos = startPos + v * dt;

        // 3. Continuous Ground Bounce Resolution
        if (nextPos.Y < _params.Radius)
        {
            float tHit = startPos.Y > _params.Radius
                ? Math.Clamp((startPos.Y - _params.Radius) / MathF.Max(1e-6f, startPos.Y - nextPos.Y), 0f, 1f)
                : 0f;

            Vector3 hitPoint = Vector3.Lerp(startPos, nextPos, tHit);
            hitPoint.Y = _params.Radius;

            Vector3 incomingV = v;
            if (v.Y < 0f)
            {
                if (MathF.Abs(v.Y) <= _params.RestBounceVelocityThreshold)
                {
                    v.Y = 0f;
                    ball.ContactState = BallContactSurface.RollingOrSlidingGround;
                }
                else
                {
                    v.Y = -v.Y * _params.GroundRestitution;
                    // Tangential spin-ground coupling (topspin accelerates forward bounce, backspin checks/slows bounce)
                    Vector3 contactOffset = new(0f, -_params.Radius, 0f);
                    Vector3 vSlip = new Vector3(v.X, 0f, v.Z) + Vector3.Cross(omega, contactOffset);
                    Vector3 deltaTangential = -new Vector3(vSlip.X, 0f, vSlip.Z) * _params.BounceTangentialGrip;
                    v.X += deltaTangential.X;
                    v.Z += deltaTangential.Z;

                    Vector3 impulse = deltaTangential * _params.Mass;
                    omega += Vector3.Cross(contactOffset, impulse) / _params.MomentOfInertia;

                    ball.RecentCollisions.Add(new BallCollisionEvent(_elapsedTime, "Ground", hitPoint, incomingV, v));
                }
            }
            nextPos = hitPoint + v * ((1.0f - tHit) * dt);
            if (nextPos.Y < _params.Radius)
                nextPos.Y = _params.Radius;
        }

        // 4. Swept Collision Against Both Goals (Posts, Crossbar, and Net Interior)
        ResolveGoalStructureCollisions(ball, startPos, ref nextPos, ref v, ref omega);

        // 5. Enforce Numerical Safety (never NaN or energy gain)
        // Audit F09: only the X component used to be checked, so a ball could keep a NaN Z or a
        // NaN/infinite spin forever. Validate every component of position, velocity and spin, and
        // count recoveries instead of hiding a recurring fault.
        if (!SimMath.IsFinite(nextPos) || !SimMath.IsFinite(v) || !SimMath.IsFinite(omega))
        {
            ball.InvalidStateRecoveries++;
            nextPos = new Vector3(0f, _params.Radius, 0f);
            v = Vector3.Zero;
            omega = Vector3.Zero;
        }

        // 6. Audit F20: record the moment the WHOLE ball left the field of play. This runs on the
        // integrator's substep segment, so a crossing cannot be missed because the tick ended back
        // inside the field, and the first crossing of the tick keeps its order.
        RecordBoundaryCrossings(ball, startPos, nextPos, tickElapsed, dt);

        ball.Position = nextPos;
        ball.Velocity = v;
        ball.AngularVelocity = omega;
    }

    /// <summary>
    /// Appends this substep's boundary crossings, earliest first (audit F20). A ball is out of play
    /// only once the WHOLE ball has crossed the line (IFAB Law 9), so the planes are the touchlines and
    /// goal lines offset by the configured ball radius. Crossing position and order come from the
    /// substep segment, not from the tick's end state.
    /// </summary>
    private void RecordBoundaryCrossings(BallState ball, Vector3 startPos, Vector3 nextPos, float tickElapsed, float subDt)
    {
        float goalLinePlane = _pitch.HalfLength + _params.Radius;
        float touchLinePlane = _pitch.HalfWidth + _params.Radius;

        Span<BallBoundaryCrossing> crossings = stackalloc BallBoundaryCrossing[4];
        int count = 0;

        if (TryCrossPlane(startPos, nextPos, goalLinePlane, BallBoundary.GoalLinePositiveX, tickElapsed, subDt, out var crossing))
            crossings[count++] = crossing;
        if (TryCrossPlane(startPos, nextPos, -goalLinePlane, BallBoundary.GoalLineNegativeX, tickElapsed, subDt, out crossing))
            crossings[count++] = crossing;
        if (TryCrossPlane(startPos, nextPos, touchLinePlane, BallBoundary.TouchlinePositiveZ, tickElapsed, subDt, out crossing))
            crossings[count++] = crossing;
        if (TryCrossPlane(startPos, nextPos, -touchLinePlane, BallBoundary.TouchlineNegativeZ, tickElapsed, subDt, out crossing))
            crossings[count++] = crossing;

        // Insertion sort by crossing time: a corner exit can cross both a touchline and a goal line.
        for (int i = 1; i < count; i++)
        {
            BallBoundaryCrossing item = crossings[i];
            int j = i - 1;
            while (j >= 0 && crossings[j].ElapsedSeconds > item.ElapsedSeconds)
            {
                crossings[j + 1] = crossings[j];
                j--;
            }
            crossings[j + 1] = item;
        }

        for (int i = 0; i < count; i++)
            ball.BoundaryCrossings.Add(crossings[i]);
    }

    private bool TryCrossPlane(
        Vector3 startPos,
        Vector3 nextPos,
        float signedPlane,
        BallBoundary boundary,
        float tickElapsed,
        float subDt,
        out BallBoundaryCrossing crossing)
    {
        bool onXAxis = boundary is BallBoundary.GoalLineNegativeX or BallBoundary.GoalLinePositiveX;
        float from = onXAxis ? startPos.X : startPos.Z;
        float to = onXAxis ? nextPos.X : nextPos.Z;

        // Only a genuine crossing of this plane counts; a ball already beyond it was judged earlier.
        bool crossed = signedPlane > 0f
            ? from < signedPlane && to >= signedPlane
            : from > signedPlane && to <= signedPlane;
        if (!crossed)
        {
            crossing = default;
            return false;
        }

        float delta = to - from;
        float t = MathF.Abs(delta) < 1e-9f ? 0f : Math.Clamp((signedPlane - from) / delta, 0f, 1f);
        Vector3 point = Vector3.Lerp(startPos, nextPos, t);
        crossing = new BallBoundaryCrossing(boundary, point, signedPlane, tickElapsed + t * subDt, _params.Radius);
        return true;
    }

    private void ResolveGoalStructureCollisions(
        BallState ball,
        Vector3 startPos,
        ref Vector3 nextPos,
        ref Vector3 v,
        ref Vector3 omega)
    {
        float halfL = _pitch.HalfLength;
        float halfGW = _pitch.HalfGoalWidth;
        float gh = _pitch.GoalHeight;
        float combinedRadius = _params.Radius + _pitch.GoalPostRadius;

        ReadOnlySpan<float> goalSigns = [-1.0f, 1.0f];
        foreach (float sign in goalSigns)
        {
            float goalX = sign * halfL;
            if (MathF.Abs( nextPos.X - goalX ) > 4.0f && MathF.Abs( startPos.X - goalX ) > 4.0f)
                continue;

            // Left Post, Right Post, and Crossbar segments
            Vector3 postLeftA = new(goalX, 0f, -halfGW);
            Vector3 postLeftB = new(goalX, gh, -halfGW);
            Vector3 postRightA = new(goalX, 0f, halfGW);
            Vector3 postRightB = new(goalX, gh, halfGW);
            Vector3 crossbarA = new(goalX, gh, -halfGW);
            Vector3 crossbarB = new(goalX, gh, halfGW);

            CheckCylinderSegmentCollision(ball, startPos, ref nextPos, ref v, ref omega, postLeftA, postLeftB, combinedRadius, "GoalPost");
            CheckCylinderSegmentCollision(ball, startPos, ref nextPos, ref v, ref omega, postRightA, postRightB, combinedRadius, "GoalPost");
            CheckCylinderSegmentCollision(ball, startPos, ref nextPos, ref v, ref omega, crossbarA, crossbarB, combinedRadius, "Crossbar");

            // Net damping if ball is inside the goal mouth behind the goal line
            bool behindLine = sign > 0 ? nextPos.X > goalX + _params.Radius : nextPos.X < goalX - _params.Radius;
            if (behindLine && MathF.Abs(nextPos.Z) < halfGW && nextPos.Y <= gh)
            {
                float backNetX = sign * (halfL + _pitch.GoalDepth);
                if ((sign > 0 && nextPos.X >= backNetX - _params.Radius) ||
                    (sign < 0 && nextPos.X <= backNetX + _params.Radius))
                {
                    nextPos.X = backNetX - sign * _params.Radius;
                    v.X = -v.X * 0.15f;
                    v.Z *= 0.4f;
                    omega *= 0.3f;
                    ball.ContactState = BallContactSurface.GoalNet;
                }
            }
        }
    }

    private void CheckCylinderSegmentCollision(
        BallState ball,
        Vector3 p0,
        ref Vector3 p1,
        ref Vector3 v,
        ref Vector3 omega,
        Vector3 segA,
        Vector3 segB,
        float radiusSum,
        string label)
    {
        // Closest point between motion segment [p0, p1] and post/crossbar segment [segA, segB]
        ClosestPointsBetweenSegments(p0, p1, segA, segB, out Vector3 onMotion, out Vector3 onPost);
        Vector3 delta = onMotion - onPost;
        float dist = delta.Length();

        if (dist < radiusSum && dist > 1e-6f)
        {
            Vector3 normal = delta / dist;
            float vn = Vector3.Dot(v, normal);
            if (vn < 0f)
            {
                Vector3 incoming = v;
                Vector3 vNormal = vn * normal;
                Vector3 vTangent = v - vNormal;
                v = -_params.PostRestitution * vNormal + 0.82f * vTangent;
                omega *= 0.65f;
                p1 = onPost + normal * (radiusSum + 1e-3f);
                ball.ContactState = BallContactSurface.GoalPostOrCrossbar;
                ball.RecentCollisions.Add(new BallCollisionEvent(_elapsedTime, label, onPost, incoming, v));
            }
        }
    }

    private static void ClosestPointsBetweenSegments(
        Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2,
        out Vector3 c1, out Vector3 c2)
    {
        Vector3 d1 = q1 - p1;
        Vector3 d2 = q2 - p2;
        Vector3 r = p1 - p2;
        float a = Vector3.Dot(d1, d1);
        float e = Vector3.Dot(d2, d2);
        float f = Vector3.Dot(d2, r);

        float s, t;
        if (a <= 1e-6f && e <= 1e-6f)
        {
            c1 = p1;
            c2 = p2;
            return;
        }
        if (a <= 1e-6f)
        {
            s = 0.0f;
            t = Math.Clamp(f / e, 0.0f, 1.0f);
        }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= 1e-6f)
            {
                t = 0.0f;
                s = Math.Clamp(-c / a, 0.0f, 1.0f);
            }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denom = a * e - b * b;
                s = denom != 0.0f ? Math.Clamp((b * f - c * e) / denom, 0.0f, 1.0f) : 0.0f;
                t = (b * s + f) / e;
                if (t < 0.0f)
                {
                    t = 0.0f;
                    s = Math.Clamp(-c / a, 0.0f, 1.0f);
                }
                else if (t > 1.0f)
                {
                    t = 1.0f;
                    s = Math.Clamp((b - c) / a, 0.0f, 1.0f);
                }
            }
        }
        c1 = p1 + d1 * s;
        c2 = p2 + d2 * t;
    }

    /// <summary>
    /// Exports a CSV trajectory trace for the Phase 1 Physics Test Lab (§3.3).
    /// </summary>
    public static string RunTrajectoryLabCsv(
        Vector3 initialPosition,
        Vector3 initialVelocity,
        Vector3 initialSpinRadPerSec,
        float durationSeconds,
        float stepDt = SimConstants.FixedDeltaTime)
    {
        var solver = new BallPhysicsSolver();
        var ball = new BallState
        {
            Position = initialPosition,
            PreviousPosition = initialPosition,
            Velocity = initialVelocity,
            AngularVelocity = initialSpinRadPerSec
        };

        var sb = new StringBuilder();
        sb.AppendLine("time_s,pos_x,pos_y,pos_z,vel_x,vel_y,vel_z,spin_x,spin_y,spin_z,contact_state");

        int steps = (int)MathF.Ceiling(durationSeconds / stepDt);
        for (int i = 0; i <= steps; i++)
        {
            float t = i * stepDt;
            sb.AppendLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:F4},{1:F4},{2:F4},{3:F4},{4:F4},{5:F4},{6:F4},{7:F2},{8:F2},{9:F2},{10}",
                t,
                ball.Position.X, ball.Position.Y, ball.Position.Z,
                ball.Velocity.X, ball.Velocity.Y, ball.Velocity.Z,
                ball.AngularVelocity.X, ball.AngularVelocity.Y, ball.AngularVelocity.Z,
                ball.ContactState));

            if (i < steps)
                solver.Step(ball, stepDt);
        }
        return sb.ToString();
    }
}
