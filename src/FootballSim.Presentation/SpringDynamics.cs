using System;
using System.Numerics;

namespace FootballSim.Presentation;

/// <summary>
/// Scalar helpers shared by every presentation spring. Pure math, no Godot and no simulation state.
/// </summary>
public static class SpringMath
{
    public const float Pi = MathF.PI;
    public const float TwoPi = MathF.PI * 2f;

    public static float DegToRad(float degrees) => degrees * (Pi / 180f);

    public static float RadToDeg(float radians) => radians * (180f / Pi);

    /// <summary>Shortest signed difference <c>to - from</c> mapped into <c>[-pi, pi]</c>.</summary>
    public static float ShortestDelta(float from, float to)
    {
        float delta = (to - from) % TwoPi;
        if (delta > Pi) delta -= TwoPi;
        else if (delta < -Pi) delta += TwoPi;
        return delta;
    }

    /// <summary>Wraps an angle into <c>[-pi, pi]</c>.</summary>
    public static float WrapAngle(float radians)
    {
        float wrapped = radians % TwoPi;
        if (wrapped > Pi) wrapped -= TwoPi;
        else if (wrapped < -Pi) wrapped += TwoPi;
        return wrapped;
    }

    public static float Clamp(float value, float min, float max) =>
        value < min ? min : value > max ? max : value;

    /// <summary>Linear interpolation from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static float Lerp(float from, float to, float t) => from + (to - from) * t;

    public static float Clamp01(float value) => Clamp(value, 0f, 1f);

    public static float SmoothStep01(float t)
    {
        float x = Clamp01(t);
        return x * x * (3f - 2f * x);
    }

    /// <summary>
    /// Frame-rate independent exponential approach: the fraction of <paramref name="current"/> that
    /// survives <paramref name="seconds"/>. Used for sampling filters (velocity smoothing, gaze) where a
    /// spring would add unnecessary state.
    /// </summary>
    public static float ExpApproach(float current, float target, float halfLifeSeconds, float seconds)
    {
        if (seconds <= 0f) return current;
        if (halfLifeSeconds <= 1e-5f) return target;
        float remaining = MathF.Pow(0.5f, seconds / halfLifeSeconds);
        return target + (current - target) * remaining;
    }

    /// <summary>Length of the XZ projection of a world-space vector.</summary>
    public static float HorizontalLength(Vector3 value) => MathF.Sqrt(value.X * value.X + value.Z * value.Z);
}

/// <summary>
/// Second-order damped spring-damper on one scalar degree of freedom:
/// <c>x'' = w^2 (target - x) - 2 z w x'</c>.
///
/// Integrated semi-implicitly, internally substeped so that <c>w * h &lt;= 0.5</c> in every substep.
/// That makes the spring unconditionally stable for any call dt (including a 4x fast-forward frame or
/// a debug slow-motion frame) and prevents NaN/Infinity from leaking into the rig.
///
/// Impulses are a velocity injection (<see cref="AddImpulse"/>), never a discontinuity of position, so
/// overlapping actions blend: a kick recoil on top of a run lean settles through the same spring.
/// </summary>
public struct Spring1D
{
    /// <summary>Maximum <c>w * h</c> accepted per internal substep.</summary>
    public const float MaxOmegaStep = 0.5f;

    /// <summary>Hard cap on internal substeps; only reachable for pathological call dt or stiffness.</summary>
    public const int MaxSubsteps = 12;

    public float Value;
    public float Velocity;

    public static Spring1D At(float value) => new() { Value = value, Velocity = 0f };

    /// <summary>Snaps position and clears velocity (teleport / restart).</summary>
    public void Reset(float value)
    {
        Value = value;
        Velocity = 0f;
    }

    /// <summary>Injects momentum without moving the position (contact impulse).</summary>
    public void AddImpulse(float impulse)
    {
        Velocity += impulse;
    }

    /// <summary>Advances the spring toward <paramref name="target"/>.</summary>
    public void Step(float target, float omega, float zeta, float dt)
    {
        if (dt <= 0f) return;
        if (!float.IsFinite(target)) target = 0f;
        if (!float.IsFinite(omega) || omega <= 0f || !float.IsFinite(zeta) || zeta <= 0f)
        {
            Value = target;
            Velocity = 0f;
            return;
        }

        int substeps = (int)MathF.Ceiling(omega * dt / MaxOmegaStep);
        if (substeps < 1) substeps = 1;
        else if (substeps > MaxSubsteps) substeps = MaxSubsteps;
        float h = dt / substeps;

        float x = Value;
        float v = Velocity;
        for (int i = 0; i < substeps; i++)
        {
            float acceleration = omega * omega * (target - x) - 2f * zeta * omega * v;
            v += acceleration * h;
            x += v * h;
        }

        // Defensive: a caller handing in an absurd target or a degenerate omega must never poison the
        // rig with NaN even though the maths above is provably stable for finite inputs.
        if (!float.IsFinite(x) || !float.IsFinite(v))
        {
            Value = target;
            Velocity = 0f;
            return;
        }

        Value = x;
        Velocity = v;
    }

    /// <summary>
    /// Advances an angle spring: the error is measured along the shortest arc, so a head turn from
    /// +170 deg to -170 deg is a 20 deg turn rather than a 340 deg spin. The value is re-wrapped after
    /// each step.
    /// </summary>
    public void StepAngle(float target, float omega, float zeta, float dt)
    {
        if (dt <= 0f || !float.IsFinite(target)) return;
        float error = SpringMath.ShortestDelta(Value, target);
        Step(Value + error, omega, zeta, dt);
        Value = SpringMath.WrapAngle(Value);
    }
}

/// <summary>Two independent scalar springs for a 2D degree of freedom (local lunge offset).</summary>
public struct Spring2D
{
    public Vector2 Value;
    public Vector2 Velocity;

    public static Spring2D At(Vector2 value) => new() { Value = value, Velocity = Vector2.Zero };

    public void Reset(Vector2 value)
    {
        Value = value;
        Velocity = Vector2.Zero;
    }

    public void AddImpulse(Vector2 impulse) => Velocity += impulse;

    public void Step(Vector2 target, float omega, float zeta, float dt)
    {
        if (dt <= 0f) return;
        Spring1D x = new() { Value = Value.X, Velocity = Velocity.X };
        Spring1D y = new() { Value = Value.Y, Velocity = Velocity.Y };
        x.Step(target.X, omega, zeta, dt);
        y.Step(target.Y, omega, zeta, dt);
        Value = new Vector2(x.Value, y.Value);
        Velocity = new Vector2(x.Velocity, y.Velocity);
    }
}

/// <summary>Three independent scalar springs for a 3D degree of freedom (hand / glove reach).</summary>
public struct Spring3D
{
    public Vector3 Value;
    public Vector3 Velocity;

    public static Spring3D At(Vector3 value) => new() { Value = value, Velocity = Vector3.Zero };

    public void Reset(Vector3 value)
    {
        Value = value;
        Velocity = Vector3.Zero;
    }

    public void AddImpulse(Vector3 impulse) => Velocity += impulse;

    public void Step(Vector3 target, float omega, float zeta, float dt)
    {
        if (dt <= 0f) return;
        Spring1D x = new() { Value = Value.X, Velocity = Velocity.X };
        Spring1D y = new() { Value = Value.Y, Velocity = Velocity.Y };
        Spring1D z = new() { Value = Value.Z, Velocity = Velocity.Z };
        x.Step(target.X, omega, zeta, dt);
        y.Step(target.Y, omega, zeta, dt);
        z.Step(target.Z, omega, zeta, dt);
        Value = new Vector3(x.Value, y.Value, z.Value);
        Velocity = new Vector3(x.Velocity, y.Velocity, z.Velocity);
    }
}
