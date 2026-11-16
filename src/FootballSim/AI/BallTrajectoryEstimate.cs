using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Physics;

namespace FootballSim.AI;

/// <summary>
/// Where the ball will be, and when — for the decisions that have to meet it (A6.5).
///
/// The keeper's sweep used to race a ball as if it travelled in a straight line at constant speed, the dive
/// save extrapolated the flight linearly (no gravity, no drag, no bounce) and nothing at all predicted a cross.
/// All three are the same question — "can I be at the ball's real future position before it is not worth
/// catching?" — so they now share one answer.
///
/// <b>What "shared" means here.</b> The estimate is not a second physics model: it rolls a copy of the
/// authorised <see cref="BallState"/> through the *same* <see cref="BallPhysicsSolver"/> the match uses, at a
/// decimated step (the solver's own adaptive substepping keeps a larger dt accurate — it substeps so the ball
/// never travels more than 0.45 radii at a time). The trajectory is therefore the trajectory, sampled, not an
/// approximation of it built from a different set of equations.
///
/// <b>Cost and determinism.</b> One reusable instance per consumer: the shadow ball, the sample arrays and the
/// solver are allocated once, and a rebuild copies fields rather than cloning. Nothing here consumes
/// simulation randomness, and the live ball is never stepped — only the copy.
/// </summary>
public sealed class BallTrajectoryEstimate
{
    /// <summary>Samples kept per estimate. 32 at <see cref="SampleStepSeconds"/> is a 2.1 s horizon.</summary>
    public const int MaxSamples = 32;

    /// <summary>Time between samples, in seconds. Coarse enough to be cheap, fine enough for a 0.07 s miss.</summary>
    public const float SampleStepSeconds = 1f / 15f;

    /// <summary>Default horizon: past this a keeper's decision is a guess, not a race.</summary>
    public const float DefaultHorizonSeconds = 1.2f;

    private readonly BallPhysicsSolver _solver;
    private readonly BallState _shadow = new();
    private readonly BallState _scratch = new();
    private readonly float[] _times = new float[MaxSamples];
    private readonly Vector3[] _positions = new Vector3[MaxSamples];

    private int _count;
    private Vector3 _origin;
    private Vector3 _originVelocity;

    /// <summary>One estimate, bound to the solver of the world it predicts in.</summary>
    public BallTrajectoryEstimate(BallPhysicsSolver? solver = null, PitchDimensions? pitch = null) =>
        _solver = solver ?? new BallPhysicsSolver(pitch: pitch);

    /// <summary>True when the last <see cref="TryBuild"/> produced samples.</summary>
    public bool IsBuilt => _count > 0;

    /// <summary>
    /// Drops the current samples. A consumer that keeps one estimate per decision should call this at the start
    /// of every decision, so a trajectory built for last tick's question cannot answer this tick's.
    /// </summary>
    public void Invalidate() => _count = 0;

    /// <summary>Number of samples in the last estimate.</summary>
    public int Count => _count;

    /// <summary>Where the ball was when the estimate was built.</summary>
    public Vector3 Origin => _origin;

    /// <summary>Time of sample <paramref name="index"/>, measured from the build instant.</summary>
    public float TimeAt(int index) => _times[index];

    /// <summary>Position of sample <paramref name="index"/>.</summary>
    public Vector3 PositionAt(int index) => _positions[index];

    /// <summary>
    /// Rolls a ball forward from a position and velocity alone — the shape a player's observation gives. Spin is
    /// not in the perception picture (A2.2 carries position and velocity), so a predicted flight is the
    /// spin-free version of the same equations; the samples are still produced by the real solver.
    /// </summary>
    public bool TryBuild(
        Vector3 position,
        Vector3 velocity,
        float horizonSeconds = DefaultHorizonSeconds,
        float stepSeconds = SampleStepSeconds)
    {
        _scratch.Position = position;
        _scratch.PreviousPosition = position;
        _scratch.Velocity = velocity;
        _scratch.AngularVelocity = Vector3.Zero;
        _scratch.ContactState = position.Y > BallGeometry.RestingCentreHeight(_solver.Parameters.Radius) + 1e-3f
            ? BallContactSurface.Air
            : BallContactSurface.RollingOrSlidingGround;
        _scratch.HeldByPlayerId = null;

        return TryBuild(_scratch, horizonSeconds, stepSeconds);
    }

    /// <summary>
    /// Rolls the ball forward from its current state. Returns false when there is nothing to predict (a held
    /// ball, a non-finite state, or a horizon that yields no samples).
    /// </summary>
    public bool TryBuild(
        in BallState ball,
        float horizonSeconds = DefaultHorizonSeconds,
        float stepSeconds = SampleStepSeconds)
    {
        _count = 0;
        if (ball.HeldByPlayerId.HasValue) return false;
        if (!float.IsFinite(horizonSeconds) || horizonSeconds <= 0f) return false;
        if (!float.IsFinite(stepSeconds) || stepSeconds <= 0f) return false;

        float horizon = MathF.Min(horizonSeconds, MaxSamples * stepSeconds);
        int wanted = Math.Min(MaxSamples, (int)MathF.Ceiling(horizon / stepSeconds));
        if (wanted <= 0) return false;

        _origin = ball.Position;
        _originVelocity = ball.Velocity;

        _shadow.Position = ball.Position;
        _shadow.PreviousPosition = ball.Position;
        _shadow.Velocity = ball.Velocity;
        _shadow.AngularVelocity = ball.AngularVelocity;
        _shadow.ContactState = ball.ContactState;
        _shadow.HeldByPlayerId = null;
        _shadow.HeldReleaseDelaySeconds = 0f;

        for (int i = 0; i < wanted; i++)
        {
            _solver.Step(_shadow, stepSeconds);

            _times[i] = (i + 1) * stepSeconds;
            _positions[i] = _shadow.Position;

            if (!SimMath.IsFinite(_shadow.Position)) break;
            _count = i + 1;
        }

        return _count > 0;
    }

    /// <summary>Interpolated ball position at <paramref name="seconds"/> after the build, clamped to the samples.</summary>
    public Vector3 PositionAtTime(float seconds)
    {
        if (_count == 0) return _origin;
        if (!float.IsFinite(seconds) || seconds <= 0f) return _origin;
        if (seconds >= _times[_count - 1]) return _positions[_count - 1];

        // Linear between two samples: the samples are 1/15 s apart and the ball's own path between them is
        // nearly straight, so the interpolation error is far below the reach this is compared against.
        for (int i = 0; i < _count; i++)
        {
            if (_times[i] < seconds) continue;
            if (i == 0) return _positions[0];

            float span = _times[i] - _times[i - 1];
            float fraction = span > 1e-5f ? (seconds - _times[i - 1]) / span : 0f;
            return Vector3.Lerp(_positions[i - 1], _positions[i], Math.Clamp(fraction, 0f, 1f));
        }

        return _positions[_count - 1];
    }

    /// <summary>
    /// The closest approach of the ball's path to a point within a horizon: how near the ball comes, when, and
    /// where. This is the question a dive asks — how far does the body have to go, and how long does it have.
    /// </summary>
    public bool TryNearestApproach(
        Vector2 fromXZ,
        float horizonSeconds,
        out float distanceMeters,
        out float seconds,
        out Vector3 point)
    {
        distanceMeters = float.MaxValue;
        seconds = 0f;
        point = default;
        if (_count == 0) return false;

        bool found = false;
        for (int i = 0; i < _count; i++)
        {
            float time = _times[i];
            if (time > horizonSeconds) break;

            Vector3 sample = _positions[i];
            float distance = Vector2.Distance(fromXZ, new Vector2(sample.X, sample.Z));
            if (found && distance >= distanceMeters) continue;

            found = true;
            distanceMeters = distance;
            seconds = time;
            point = sample;
        }

        return found;
    }

    /// <summary>
    /// Sampled velocity at one sample: the finite difference of the neighbouring positions. A6.5b needs it
    /// because "will he hold it or punch it" is decided by the pace the ball carries when it arrives, and that
    /// pace has to come from the same path the arrival was compared against.
    /// </summary>
    public Vector3 VelocityAt(int index)
    {
        if (_count == 0) return Vector3.Zero;

        int i = Math.Clamp(index, 0, _count - 1);
        if (i == 0)
        {
            float first = _times[0];
            return first > 1e-5f ? (_positions[0] - _origin) / first : _originVelocity;
        }

        float span = _times[i] - _times[i - 1];
        return span > 1e-5f ? (_positions[i] - _positions[i - 1]) / span : Vector3.Zero;
    }

    /// <summary>
    /// The time at which a player could first be at one of the samples: the earliest sample he can reach by the
    /// time the ball is there, ignoring any sample whose ball height is above <paramref name="maxHeight"/>.
    ///
    /// The reach test is an optimistic planar bound (can he cover the ground at his sprint cap?) followed by one
    /// <see cref="TimeToReachModel"/> solve only on the samples that pass it, so a hopeless race costs a few
    /// multiplications rather than a solve per sample.
    /// </summary>
    public bool TryMeet(
        in ReachKinematics player,
        float horizonSeconds,
        float maxHeight,
        out float meetSeconds,
        out Vector3 meetPoint) =>
        TryMeet(player, horizonSeconds, maxHeight, out meetSeconds, out meetPoint, out _);

    /// <summary>
    /// <see cref="TryMeet(in ReachKinematics, float, float, out float, out Vector3)"/> plus the index of the
    /// sample that was met, so a caller can also read the ball's pace there (<see cref="VelocityAt"/>).
    /// </summary>
    public bool TryMeet(
        in ReachKinematics player,
        float horizonSeconds,
        float maxHeight,
        out float meetSeconds,
        out Vector3 meetPoint,
        out int meetIndex) =>
        TryMeet(player, horizonSeconds, float.MinValue, maxHeight, out meetSeconds, out meetPoint, out meetIndex);

    /// <summary>
    /// The same query inside a <b>height band</b>: A6.5b needs "which samples could this keeper play with his
    /// hands?", and the hands' band has a floor (a ball below the torso is a foot or a chest, not a catch) as
    /// well as a ceiling. Both bounds are absolute ball heights.
    /// </summary>
    public bool TryMeet(
        in ReachKinematics player,
        float horizonSeconds,
        float minHeight,
        float maxHeight,
        out float meetSeconds,
        out Vector3 meetPoint,
        out int meetIndex)
    {
        meetSeconds = 0f;
        meetPoint = default;
        meetIndex = 0;

        float sprintCap = MathF.Max(0.5f, TimeToReachModel.SpeedCap(player, sprint: true));
        float reach = MathF.Max(0.05f, player.ReachRadius);

        for (int i = 0; i < _count; i++)
        {
            float time = _times[i];
            if (time > horizonSeconds) break;

            Vector3 point = _positions[i];
            if (point.Y > maxHeight || point.Y < minHeight) continue;

            float planar = MathF.Max(0f,
                Vector2.Distance(player.PositionXZ, new Vector2(point.X, point.Z)) - reach);
            if (planar / sprintCap > time) continue;

            ReachEstimate arrival = TimeToReachModel.ToPoint(
                player, new Vector2(point.X, point.Z), horizonSeconds);
            if (!arrival.Reachable || arrival.Seconds > time) continue;

            meetSeconds = time;
            meetPoint = point;
            meetIndex = i;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The first time the ball crosses the plane <c>x = planeX</c> inside the samples, with the point it crossed
    /// at (a shot's arrival at a goal line). Returns false when the ball never reaches that plane inside the
    /// horizon.
    /// </summary>
    public bool TryFirstPlaneCrossing(float planeX, out float seconds, out Vector3 point)
    {
        seconds = 0f;
        point = default;
        if (_count == 0) return false;

        float previousOffset = _origin.X - planeX;
        Vector3 previousPoint = _origin;

        for (int i = 0; i < _count; i++)
        {
            float offset = _positions[i].X - planeX;
            if (previousOffset == 0f && i == 0) return false;

            if ((previousOffset < 0f && offset >= 0f) || (previousOffset > 0f && offset <= 0f))
            {
                float span = previousOffset - offset;
                float fraction = MathF.Abs(span) > 1e-6f ? previousOffset / span : 0f;
                seconds = i == 0 ? _times[0] : _times[i - 1] + (_times[i] - _times[i - 1]) * Math.Clamp(fraction, 0f, 1f);
                point = Vector3.Lerp(previousPoint, _positions[i], Math.Clamp(fraction, 0f, 1f));
                return true;
            }

            previousOffset = offset;
            previousPoint = _positions[i];
        }

        return false;
    }
}
