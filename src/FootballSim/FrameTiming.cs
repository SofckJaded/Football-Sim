using System;
using System.Numerics;

namespace FootballSim.Core;

/// <summary>
/// The fixed-step frame clock shared by the match and training scenes (audit F22).
///
/// The audit found the presentation layer accumulating raw frame deltas and then simulating every
/// remaining fixed step in a single frame: the response changed with the frame rate, and one large
/// frame delay (a window drag, a stalled device, a scene load) could trigger a long burst of
/// simulation steps in one frame — a visible stall that then starved the next frames.
///
/// This clock bounds the work per frame and records what it had to drop, so a catch-up event is
/// measurable instead of silent. It is deliberately free of Godot types so the timing rules are
/// testable at 30, 60 and 144 FPS.
/// </summary>
public sealed class FixedStepClock
{
    /// <summary>
    /// Step size used to count accumulated wall-clock time.
    ///
    /// Deliberately the exact <c>1/60</c> rather than <see cref="SimConstants.FixedDeltaTime"/>, which
    /// is a float: subtracting the rounded value would make one wall-clock second amount to
    /// 59.9999969 steps, so the number of steps in a second depended on how the frames divided it up.
    /// </summary>
    public const double StepSeconds = 1.0 / 60.0;

    /// <summary>Largest frame delta that is simulated (a longer stall is truncated).</summary>
    public const double MaxFrameDeltaSeconds = 0.25;

    /// <summary>Largest number of fixed steps a single frame may run (0.2 s at 60 Hz).</summary>
    public const int MaxStepsPerFrame = 12;

    /// <summary>Largest simulation speed multiplier (the spectator fast-forward uses 4x).</summary>
    public const float MaxTimeScale = 8f;

    private double _accumulator;

    /// <summary>
    /// Adds one rendered frame and returns how many fixed simulation steps to run now.
    /// </summary>
    /// <param name="frameDeltaSeconds">Rendered frame time, in seconds.</param>
    /// <param name="timeScale">Simulation speed multiplier (1 = real time).</param>
    public int Advance(double frameDeltaSeconds, float timeScale = 1f)
    {
        if (!double.IsFinite(frameDeltaSeconds) || frameDeltaSeconds <= 0d ||
            !float.IsFinite(timeScale) || timeScale <= 0f)
        {
            StepsLastFrame = 0;
            return 0;
        }

        if (frameDeltaSeconds > MaxFrameDeltaSeconds)
        {
            DroppedSeconds += frameDeltaSeconds - MaxFrameDeltaSeconds;
            frameDeltaSeconds = MaxFrameDeltaSeconds;
        }

        _accumulator += frameDeltaSeconds * Math.Clamp(timeScale, 0f, MaxTimeScale);

        double step = StepSeconds;
        // The epsilon absorbs the rounding error of repeated fractional frame deltas (144 x 1/144 s
        // sums to 0.9999999999999999 s, which must still be 60 steps).
        int steps = (int)Math.Floor((_accumulator + 1e-9) / step);

        if (steps > MaxStepsPerFrame)
        {
            int excess = steps - MaxStepsPerFrame;
            double dropped = excess * step;
            DroppedSeconds += dropped;
            _accumulator -= dropped;
            steps = MaxStepsPerFrame;
            CappedFrames++;
        }

        if (steps > 0) _accumulator -= steps * step;
        if (_accumulator < 0d) _accumulator = 0d;

        StepsLastFrame = steps;
        return steps;
    }

    /// <summary>Steps returned by the most recent <see cref="Advance"/> call.</summary>
    public int StepsLastFrame { get; private set; }

    /// <summary>Frames on which the per-frame step cap was reached.</summary>
    public int CappedFrames { get; private set; }

    /// <summary>Simulated time (seconds) the clock refused to run because it was too far behind.</summary>
    public double DroppedSeconds { get; private set; }

    /// <summary>Fixed time accumulated but not yet simulated, in seconds.</summary>
    public double AccumulatorSeconds => _accumulator;

    /// <summary>Interpolation fraction between the previous and current fixed step, in [0, 1).</summary>
    public float Alpha
    {
        get
        {
            float alpha = (float)(_accumulator / StepSeconds);
            if (!float.IsFinite(alpha) || alpha < 0f) return 0f;
            return alpha >= 1f ? 0.9999f : alpha;
        }
    }

    /// <summary>True when the clock has had to drop simulated time (diagnostic/HUD).</summary>
    public bool IsCatchingUp => CappedFrames > 0;

    public void Reset()
    {
        _accumulator = 0d;
        StepsLastFrame = 0;
        CappedFrames = 0;
        DroppedSeconds = 0d;
    }
}

/// <summary>
/// Frame-delta-aware camera smoothing (audit F22).
///
/// The broadcast camera used a fixed 0.14 lerp per rendered frame, so its tracking response depended
/// on the frame rate: snappy at 144 FPS, sluggish at 30 FPS. Smoothing now uses a half-life in
/// seconds, which produces the same motion for the same elapsed time at any frame rate. The default
/// half-life is derived from the old 60 Hz weight, so the 60 FPS feel is unchanged.
/// </summary>
public static class CameraSmoothing
{
    /// <summary>Weight the old fixed-lerp camera applied per 60 Hz frame.</summary>
    public const float LegacyPerFrameWeight = 0.14f;

    /// <summary>Half-life reproducing <see cref="LegacyPerFrameWeight"/> at 60 FPS (~0.0766 s).</summary>
    public static readonly float MatchHalfLifeSeconds =
        HalfLifeForFrameWeight(LegacyPerFrameWeight, SimConstants.FixedDeltaTime);

    /// <summary>
    /// Half-life (seconds) that makes one <paramref name="frameDeltaSeconds"/> frame move
    /// <paramref name="frameWeight"/> of the remaining distance.
    /// </summary>
    public static float HalfLifeForFrameWeight(float frameWeight, float frameDeltaSeconds)
    {
        if (!float.IsFinite(frameWeight) || frameWeight <= 0f || frameWeight >= 1f) return 0f;
        if (!float.IsFinite(frameDeltaSeconds) || frameDeltaSeconds <= 0f) return 0f;
        // 1 - weight = 2^(-delta / halfLife)  =>  halfLife = -delta / log2(1 - weight)
        return (float)(-frameDeltaSeconds / Math.Log2(1.0 - frameWeight));
    }

    /// <summary>
    /// Smoothing weight for one frame. Non-positive or non-finite input snaps (weight 1) so a broken
    /// delta cannot freeze the camera on a stale position.
    /// </summary>
    public static float Weight(float halfLifeSeconds, float deltaSeconds)
    {
        if (!float.IsFinite(halfLifeSeconds) || halfLifeSeconds <= 0f) return 1f;
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0f) return 0f;
        float weight = 1f - MathF.Pow(2f, -deltaSeconds / halfLifeSeconds);
        return float.IsFinite(weight) ? Math.Clamp(weight, 0f, 1f) : 1f;
    }

    /// <summary>Moves <paramref name="current"/> towards <paramref name="target"/> for one frame.</summary>
    public static Vector3 Step(Vector3 current, Vector3 target, float halfLifeSeconds, float deltaSeconds)
    {
        float weight = Weight(halfLifeSeconds, deltaSeconds);
        if (weight <= 0f) return current;
        if (weight >= 1f) return target;

        Vector3 next = current + (target - current) * weight;
        return SimMath.IsFinite(next) ? next : target;
    }
}
