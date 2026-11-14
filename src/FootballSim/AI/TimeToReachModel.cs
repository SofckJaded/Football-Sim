using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;

namespace FootballSim.AI;

/// <summary>
/// Kinematic starting state for a reach estimate (A2.1): where the player is, how they are already
/// moving and facing, what their body can do, and how close counts as "there".
///
/// Everything here is read from either the authoritative <see cref="FootballerState"/> (the player
/// themselves) or a neutral capability assumption (an opponent seen through perception, which carries
/// no attributes until A2.3 adds scouted capability estimates).
/// </summary>
public readonly record struct ReachKinematics(
    Vector2 PositionXZ,
    Vector2 VelocityXZ,
    Vector2 FacingXZ,
    float MaxSprintSpeed,
    float Acceleration,
    float AgilityTurnRate,
    float Balance01,
    float Stamina01,
    float ReachRadius)
{
    /// <summary>
    /// Kinematics of a player this simulation controls, including their current recovery balance and
    /// stamina. The reach radius is the receiving/dribbling tactical reach
    /// (<see cref="BallContactRules.ReachRadius"/>), i.e. the largest planar distance at which an
    /// arriving player can actually play the ball.
    /// </summary>
    public static ReachKinematics From(FootballerState player) => new(
        new Vector2(player.Position.X, player.Position.Z),
        new Vector2(player.Velocity.X, player.Velocity.Z),
        player.FacingXZ,
        player.Attributes.MaxSprintSpeed,
        player.Attributes.Acceleration,
        player.Attributes.AgilityTurnRate,
        player.Balance01,
        player.Stamina01,
        BallContactRules.ReachRadius(
            player.Attributes.ReachRadius,
            isGoalkeeper: player.Role == TacticalRole.Goalkeeper,
            isDiving: player.IsDiving,
            requestingTackle: false,
            receivingOrDribbling: true));

    /// <summary>
    /// Kinematics for an entity whose attributes are not known — an opponent seen through perception.
    /// Assumes a neutral <see cref="PlayerAttributes"/> and full stamina, which is exactly the
    /// assumption the calibrated pass-lane test already makes; it is deliberately not optimistic.
    /// </summary>
    public static ReachKinematics Neutral(Vector2 positionXZ, Vector2 velocityXZ, Vector2 facingXZ = default)
    {
        var neutral = new PlayerAttributes();
        return new ReachKinematics(
            positionXZ,
            velocityXZ,
            facingXZ,
            neutral.MaxSprintSpeed,
            neutral.Acceleration,
            neutral.AgilityTurnRate,
            Balance01: 1f,
            Stamina01: 1f,
            BallContactRules.ReachRadius(
                neutral.ReachRadius, isGoalkeeper: false, isDiving: false,
                requestingTackle: false, receivingOrDribbling: true));
    }

    /// <summary>Same kinematics with a different contact reach, e.g. a keeper handling versus a foot.</summary>
    public ReachKinematics WithReachRadius(float reachRadius) => this with { ReachRadius = reachRadius };

    /// <summary>
    /// Estimated kinematics of another player (A2.3): position and velocity as the observer believes
    /// them, body limits from the capability band the observation carries. There is deliberately no
    /// overload that takes a <see cref="FootballerState"/>, so a scorer cannot reach live attributes.
    /// </summary>
    public static ReachKinematics FromEntity(in PerceivedEntity entity) =>
        entity.Capability.ToKinematics(
            new Vector2(entity.EstimatedPosition.X, entity.EstimatedPosition.Z),
            new Vector2(entity.EstimatedVelocity.X, entity.EstimatedVelocity.Z),
            goalkeeper: entity.Role == TacticalRole.Goalkeeper);

    /// <summary>
    /// Kinematics of the decision maker itself, built only from what perception published (A2.1). The
    /// controller and candidate generation both use this, so a decision-side reach estimate can never
    /// quietly read the authoritative world for its own numbers.
    /// </summary>
    public static ReachKinematics FromObservation(in FootballerObservation obs) => new(
        new Vector2(obs.SelfPosition.X, obs.SelfPosition.Z),
        new Vector2(obs.SelfVelocity.X, obs.SelfVelocity.Z),
        obs.SelfFacingXZ,
        obs.SelfAttributes.MaxSprintSpeed,
        obs.SelfAttributes.Acceleration,
        obs.SelfAttributes.AgilityTurnRate,
        obs.SelfBalance01,
        obs.Stamina01,
        BallContactRules.ReachRadius(
            obs.SelfAttributes.ReachRadius,
            isGoalkeeper: obs.SelfRole == TacticalRole.Goalkeeper,
            isDiving: false,
            requestingTackle: false,
            receivingOrDribbling: true));
}

/// <summary>
/// Answer to a reach query. <see cref="Seconds"/> is measured from the query instant, and
/// <see cref="MeetingPointXZ"/> is where the *target* is at that instant — not where the player is,
/// because a chaser should run at the ball's future position, not at its own future position.
/// </summary>
public readonly record struct ReachEstimate(
    bool Reachable,
    float Seconds,
    Vector2 MeetingPointXZ,
    float SpeedAtArrival)
{
    /// <summary>Nothing inside the horizon: conventionally the horizon itself, with no meeting point.</summary>
    public static ReachEstimate Unreachable(float horizonSeconds) => new(false, horizonSeconds, Vector2.Zero, 0f);
}

/// <summary>
/// The one reach model every "how long until this player can be there" question goes through (A2.1).
///
/// Before this existed there were four incompatible answers: <see cref="BallResponderElection"/>
/// divided distance by <c>MaxSprintSpeed × 0.85</c>, <c>ComputeBallInterceptTarget</c> used a
/// constant-velocity closest approach, candidate generation led receivers by a flight-time sample, and
/// the goalkeeper swept on a bare 12.5 m radius. The same player could therefore be the quickest to a
/// ball in one place and the slowest in another, and none of the four responded to current velocity,
/// turn cost, acceleration or fatigue.
///
/// The model mirrors <c>StepPlayerLocomotion</c> rather than inventing a second kind of motion:
///
/// <list type="bullet">
/// <item>speed cap = <c>MaxSprintSpeed × (0.68 + 0.32 × stamina) × (sprint ? 1 : 0.75)</c>;</item>
/// <item>the body has to face the way it runs: the cap is scaled by <c>0.85 + 0.15 × cos(turn
///   error)</c>, averaged over the time the turn actually takes
///   (<see cref="PlayerAttributes.AgilityTurnRate"/> × 1.25 rad/s, slower at speed);</item>
/// <item>velocity moves at bounded acceleration toward the new target velocity
///   (<see cref="PlayerAttributes.Acceleration"/> × 1.15, × 1.45 while braking), so a reversal costs
///   roughly twice a standing start and a sideways run has to be killed before it is replaced;</item>
/// <item>arrival is a <i>contact</i>: the estimate ends when the player is inside
///   <see cref="ReachKinematics.ReachRadius"/>.</item>
/// </list>
///
/// The trajectory is closed form for a constant desired direction, so a query costs a few dozen
/// multiply-adds instead of simulating every candidate at 60 Hz. That is a deliberate approximation:
/// real steering re-aims at the target every tick, which curves the path slightly when the player
/// starts badly off line. The property that matters for elections is ordering, and the tests compare
/// the model against actual locomotion on the cases where steering barely curves.
/// </summary>
public static class TimeToReachModel
{
    /// <summary>Default look-ahead. Beyond this a reach estimate is too uncertain to rank candidates.</summary>
    public const float DefaultHorizonSeconds = 2.5f;

    /// <summary>Stamina below which the locomotion model stops paying for fatigue (matches execution).</summary>
    public const float MinFatigueStamina = 0.15f;

    /// <summary>Below this speed a player is treated as standing still (numerical, not tactical).</summary>
    private const float MinSpeed = 0.25f;

    private const float MinAcceleration = 0.5f;
    private const float MinTurnRate = 0.25f;

    /// <summary>
    /// Coarse samples used to bracket the first arrival. 48 samples over a 2.5 s horizon is one every
    /// 52 ms; a closing pair at a combined 20 m/s moves 1 m between samples, inside the ~1.24 m
    /// contact reach, so a crossing cannot be stepped over.
    /// </summary>
    private const int ScanSamples = 48;

    /// <summary>Bisections inside the bracketing interval; 10 takes ~52 ms down to 0.05 ms.</summary>
    private const int RefineIterations = 10;

    /// <summary>
    /// A moving target gets at most this many aim corrections. The lead estimate starts from the
    /// constant-speed pursuit root, so one correction is normally enough; the bound keeps a
    /// badly-conditioned geometry from turning a reach query into an unbounded solve.
    /// </summary>
    private const int InterceptRefinements = 3;

    /// <summary>Target speed below which it is treated as a point (numerical, not tactical).</summary>
    private const float MinMovingTargetSpeedSquared = 0.01f;

    /// <summary>
    /// Fatigue multiplier the locomotion step applies to top speed. Exposed because a squad's reach
    /// model and its execution must use the same curve, or a "fresh" estimate is fiction.
    /// </summary>
    public static float FatigueFactor(float stamina01)
    {
        float stamina = float.IsFinite(stamina01) ? Math.Clamp(stamina01, MinFatigueStamina, 1f) : 1f;
        return 0.68f + 0.32f * stamina;
    }

    /// <summary>Fastest this player can run in a straight line right now, before any turn cost.</summary>
    public static float SpeedCap(in ReachKinematics state, bool sprint)
    {
        float maxSpeed = float.IsFinite(state.MaxSprintSpeed) ? MathF.Max(0.5f, state.MaxSprintSpeed) : 8.4f;
        return maxSpeed * FatigueFactor(state.Stamina01) * (sprint ? 1f : 0.75f);
    }

    /// <summary>
    /// Time for this player to get within contact reach of a stationary point. The one query behind the
    /// responder election's ranking, the second defender's engagement test and the keeper's sweep gate.
    /// </summary>
    public static ReachEstimate ToPoint(
        in ReachKinematics state,
        Vector2 targetXZ,
        float horizonSeconds = DefaultHorizonSeconds,
        bool sprint = true) =>
        Solve(state, targetXZ, Vector2.Zero, horizonSeconds, sprint);

    /// <summary>
    /// Time for this player to meet a target moving at a constant velocity — a rolling or rolling-out
    /// ball, or a carrier who has not yet been closed down. Deceleration is not modelled: a ball that
    /// is slowing can be reached later than this says, which errs toward not over-committing.
    /// </summary>
    public static ReachEstimate ToMovingPoint(
        in ReachKinematics state,
        Vector2 targetXZ,
        Vector2 targetVelocityXZ,
        float horizonSeconds = DefaultHorizonSeconds,
        bool sprint = true) =>
        Solve(state, targetXZ, targetVelocityXZ, horizonSeconds, sprint);

    /// <summary>
    /// The interception query: where and when this player can first play a moving ball. Returns false
    /// when no meeting exists inside the horizon (usually "the ball is travelling faster than I can
    /// run"), which is a different answer from "very late" and must not be collapsed into one.
    /// </summary>
    public static bool TrySolveInterception(
        in ReachKinematics state,
        Vector2 ballXZ,
        Vector2 ballVelocityXZ,
        out Vector2 meetingPointXZ,
        out float seconds,
        float horizonSeconds = DefaultHorizonSeconds,
        bool sprint = true)
    {
        ReachEstimate estimate = Solve(state, ballXZ, ballVelocityXZ, horizonSeconds, sprint);
        meetingPointXZ = estimate.MeetingPointXZ;
        seconds = estimate.Seconds;
        return estimate.Reachable;
    }

    /// <summary>
    /// Averages <c>0.85 + 0.15 × cos(turn error)</c> over a trip of <paramref name="tripSeconds"/>,
    /// which is what the per-tick facing rotation amounts to when it is integrated. Public for the
    /// locomotion comparison test: the turn cost has to be checkable on its own, not just inferred.
    /// </summary>
    public static float AverageTurnFactor(float turnErrorRadians, float turnRateRadiansPerSecond, float tripSeconds)
    {
        float error = MathF.Abs(turnErrorRadians);
        if (error <= 1e-4f) return 1f;
        if (!float.IsFinite(tripSeconds) || tripSeconds <= 1e-4f) return 0.85f;

        float rate = MathF.Max(MinTurnRate, turnRateRadiansPerSecond);
        float turnTime = error / rate;
        float averageCos;

        if (turnTime >= tripSeconds)
        {
            // Mean of cos(error - rate·t) over [0, trip]. ∫cos(a - bt)dt = (sin a - sin(a - bt))/b.
            float end = MathF.Max(0f, error - rate * tripSeconds);
            averageCos = (MathF.Sin(error) - MathF.Sin(end)) / (rate * tripSeconds);
        }
        else
        {
            // The turn completes and the rest of the trip is fully aligned.
            float turned = MathF.Sin(error) / rate;
            averageCos = (turned + (tripSeconds - turnTime)) / tripSeconds;
        }

        averageCos = Math.Clamp(averageCos, 0f, 1f);
        return 0.85f + 0.15f * averageCos;
    }

    private static ReachEstimate Solve(
        in ReachKinematics state,
        Vector2 targetXZ,
        Vector2 targetVelocityXZ,
        float horizonSeconds,
        bool sprint)
    {
        float horizon = float.IsFinite(horizonSeconds) && horizonSeconds > 0f
            ? MathF.Min(horizonSeconds, 10f)
            : DefaultHorizonSeconds;

        Vector2 velocity = SimMath.IsFinite(state.VelocityXZ) ? state.VelocityXZ : Vector2.Zero;
        float speed = velocity.Length();
        if (speed < MinSpeed) velocity = Vector2.Zero;

        ReachEstimate unreachable = ReachEstimate.Unreachable(horizon);
        if (!SimMath.IsFinite(state.PositionXZ) || !SimMath.IsFinite(targetXZ) ||
            !SimMath.IsFinite(targetVelocityXZ))
        {
            return unreachable;
        }

        float reach = float.IsFinite(state.ReachRadius) ? MathF.Max(0.05f, state.ReachRadius) : 0.75f;
        if (Vector2.Distance(state.PositionXZ, targetXZ) <= reach)
            return new ReachEstimate(true, 0f, targetXZ, speed);

        if (targetVelocityXZ.LengthSquared() <= MinMovingTargetSpeedSquared)
            return SolveToward(state, targetXZ, targetXZ, Vector2.Zero, velocity, speed, reach, horizon, sprint);

        // A moving target has to be intercepted, not reached: find the meeting by iterating the aim
        // between the target's path and the player's own arrival to that point (time-to-aim, which
        // includes acceleration and the turn). Then test the resulting path against the target.
        // Aiming a constant direction at his current position can only meet a target that runs into
        // that line — a lateral runner simply moves away from it.
        Vector2 aim = LeadPursuitPoint(state, targetXZ, targetVelocityXZ, horizon, sprint);

        for (int i = 0; i < InterceptRefinements; i++)
        {
            ReachEstimate toAim = SolveToward(
                state, aim, aim, Vector2.Zero, velocity, speed, reach, horizon, sprint);
            if (!toAim.Reachable) break;

            Vector2 nextAim = targetXZ + targetVelocityXZ * toAim.Seconds;
            if (Vector2.DistanceSquared(nextAim, aim) <= 0.0225f)
            {
                aim = nextAim;
                break;
            }

            aim = nextAim;
        }

        ReachEstimate estimate = SolveToward(
            state, aim, targetXZ, targetVelocityXZ, velocity, speed, reach, horizon, sprint);
        if (estimate.Reachable) return estimate;

        // The lead can be past the meeting region when the target is slowing into the player's path;
        // the old current-position path is still a valid fallback and often meets him.
        return SolveToward(
            state, targetXZ, targetXZ, targetVelocityXZ, velocity, speed, reach, horizon, sprint);
    }

    /// <summary>
    /// Builds the path toward <paramref name="aimXZ"/> and tests it against the moving target. The turn
    /// cost is averaged over the trip in two passes, exactly as for a stationary point; only the aim
    /// differs, which is what lets the same model answer both "run to this place" and "meet this man".
    /// </summary>
    private static ReachEstimate SolveToward(
        in ReachKinematics state,
        Vector2 aimXZ,
        Vector2 targetXZ,
        Vector2 targetVelocityXZ,
        Vector2 velocity,
        float speed,
        float reach,
        float horizon,
        bool sprint)
    {
        // Two passes: the turn cost needs a trip length, and the trip length depends on the turn cost.
        // Solving with the optimistic (aligned) speed first gives a length to average the turn over;
        // the second pass uses it. One correction is enough because the cap moves at most 15 %.
        if (!TryBuildTrajectory(state, aimXZ, velocity, sprint, turnFactor: 1f, out Trajectory optimistic))
            return ReachEstimate.Unreachable(horizon);

        float tripGuess = EstimateTripSeconds(optimistic, targetXZ, targetVelocityXZ, reach, horizon);
        Vector2 desired = SimMath.SafeNormalize(aimXZ - state.PositionXZ, new Vector2(1f, 0f));
        Vector2 facing = SimMath.SafeNormalize(state.FacingXZ, SimMath.SafeNormalize(velocity, desired));
        float angle = MathF.Acos(Math.Clamp(Vector2.Dot(facing, desired), -1f, 1f));
        float speedRatio = state.MaxSprintSpeed > 1e-3f ? speed / state.MaxSprintSpeed : 0f;
        float turnRate = float.IsFinite(state.AgilityTurnRate) ? state.AgilityTurnRate : 9.5f;
        float effectiveTurnRate = MathF.Max(MinTurnRate, turnRate * 1.25f * (1f - 0.25f * speedRatio));
        float turnFactor = AverageTurnFactor(angle, effectiveTurnRate, tripGuess);

        if (!TryBuildTrajectory(state, aimXZ, velocity, sprint, turnFactor, out Trajectory trajectory))
            return ReachEstimate.Unreachable(horizon);

        return FindArrival(trajectory, targetXZ, targetVelocityXZ, reach, horizon);
    }

    /// <summary>
    /// First aim point for a moving target: the constant-speed pursuit root, i.e. the first time a
    /// player running at his cap could be level with the target's straight-line path.
    ///
    /// Acceleration and the turn are ignored here on purpose — they are corrected by re-aiming at the
    /// arrival the real trajectory produces. With no positive root (the target outruns the player) the
    /// aim is the target's position at the horizon: either the path still catches him while he turns
    /// into it, or the honest answer is unreachable.
    /// </summary>
    private static Vector2 LeadPursuitPoint(
        in ReachKinematics state,
        Vector2 targetXZ,
        Vector2 targetVelocityXZ,
        float horizon,
        bool sprint)
    {
        float speed = SpeedCap(state, sprint);
        if (!float.IsFinite(speed) || speed <= 1e-3f) return targetXZ;

        Vector2 offset = targetXZ - state.PositionXZ;
        float a = targetVelocityXZ.LengthSquared() - speed * speed;
        float b = 2f * Vector2.Dot(offset, targetVelocityXZ);
        float c = offset.LengthSquared();

        float seconds;
        if (MathF.Abs(a) <= 1e-4f)
        {
            // |v| == the player's cap: the distance is linear in t.
            seconds = MathF.Abs(b) > 1e-4f ? -c / b : horizon;
            if (!float.IsFinite(seconds) || seconds <= 0f) seconds = horizon;
        }
        else
        {
            float discriminant = b * b - 4f * a * c;
            if (discriminant < 0f)
            {
                seconds = horizon;
            }
            else
            {
                float root = MathF.Sqrt(discriminant);
                float first = (-b - root) / (2f * a);
                float second = (-b + root) / (2f * a);
                seconds = float.MaxValue;
                if (float.IsFinite(first) && first > 0f && first < seconds) seconds = first;
                if (float.IsFinite(second) && second > 0f && second < seconds) seconds = second;
                if (seconds == float.MaxValue) seconds = horizon;
            }
        }

        if (!float.IsFinite(seconds) || seconds < 0f) seconds = 0f;
        return targetXZ + targetVelocityXZ * MathF.Min(seconds, horizon);
    }

    /// <summary>
    /// Builds the closed-form path: accelerate (in velocity space) toward the desired velocity, then
    /// run straight at it. Returns false only for a degenerate target.
    /// </summary>
    private static bool TryBuildTrajectory(
        in ReachKinematics state,
        Vector2 targetXZ,
        Vector2 velocity,
        bool sprint,
        float turnFactor,
        out Trajectory trajectory)
    {
        Vector2 toTarget = targetXZ - state.PositionXZ;
        float distance = toTarget.Length();
        if (!float.IsFinite(distance) || distance <= 1e-4f)
        {
            trajectory = default;
            return false;
        }

        Vector2 direction = toTarget / distance;
        float cap = SpeedCap(state, sprint) * Math.Clamp(turnFactor, 0.85f, 1f);

        Vector2 desiredVelocity = direction * cap;
        Vector2 delta = desiredVelocity - velocity;
        float deltaLength = delta.Length();

        // Mirrors StepPlayerLocomotion: braking (a reversal, or asking for less speed than the player
        // already carries) gets the higher acceleration, so a turn costs what it really costs.
        bool braking = Vector2.Dot(desiredVelocity, velocity) < 0f ||
                       desiredVelocity.LengthSquared() < velocity.LengthSquared();
        float balance = float.IsFinite(state.Balance01) ? Math.Clamp(state.Balance01, 0.2f, 1f) : 1f;
        float acceleration = (float.IsFinite(state.Acceleration) ? MathF.Max(0.5f, state.Acceleration) : 6.8f)
                             * (braking ? 1.45f : 1.15f) * balance;
        acceleration = MathF.Max(MinAcceleration, acceleration);

        trajectory = new Trajectory(
            state.PositionXZ, velocity, direction, delta, deltaLength / acceleration, cap);
        return true;
    }

    /// <summary>Rough trip length with no turn cost, used only to average the turn over.</summary>
    private static float EstimateTripSeconds(
        in Trajectory trajectory,
        Vector2 targetXZ,
        Vector2 targetVelocityXZ,
        float reach,
        float horizon)
    {
        for (int i = 1; i <= ScanSamples; i++)
        {
            float t = horizon * i / ScanSamples;
            Vector2 targetAt = targetXZ + targetVelocityXZ * t;
            if (Vector2.DistanceSquared(trajectory.PositionAt(t), targetAt) <= reach * reach)
                return t;
        }
        return horizon;
    }

    /// <summary>
    /// First time the path is inside contact reach of the moving target. The squared distance is
    /// evaluated without a square root; only the final crossing is refined.
    /// </summary>
    private static ReachEstimate FindArrival(
        in Trajectory trajectory,
        Vector2 targetXZ,
        Vector2 targetVelocityXZ,
        float reach,
        float horizon)
    {
        float reachSquared = reach * reach;
        float previous = 0f;

        for (int i = 1; i <= ScanSamples; i++)
        {
            float t = horizon * i / ScanSamples;
            if (SquaredDistanceAt(t, trajectory, targetXZ, targetVelocityXZ) <= reachSquared)
            {
                // Refine between the last outside sample and this one.
                float low = previous;
                float high = t;
                for (int k = 0; k < RefineIterations; k++)
                {
                    float mid = 0.5f * (low + high);
                    if (SquaredDistanceAt(mid, trajectory, targetXZ, targetVelocityXZ) <= reachSquared)
                        high = mid;
                    else
                        low = mid;
                }

                float arrival = high;
                Vector2 meeting = targetXZ + targetVelocityXZ * arrival;
                return new ReachEstimate(true, arrival, meeting, trajectory.SpeedAt(arrival));
            }

            previous = t;
        }

        return ReachEstimate.Unreachable(horizon);
    }

    private static float SquaredDistanceAt(
        float seconds,
        in Trajectory trajectory,
        Vector2 targetXZ,
        Vector2 targetVelocityXZ)
    {
        Vector2 point = trajectory.PositionAt(seconds);
        Vector2 targetAt = targetXZ + targetVelocityXZ * seconds;
        Vector2 offset = point - targetAt;
        return offset.LengthSquared();
    }

    /// <summary>
    /// Closed-form path under a constant desired direction. Everything before <see cref="_tau"/> is the
    /// acceleration ramp in velocity space; afterwards the player runs straight along the desired
    /// direction at its cap.
    /// </summary>
    private readonly struct Trajectory
    {
        private readonly Vector2 _start;
        private readonly Vector2 _velocity;
        private readonly Vector2 _direction;
        private readonly Vector2 _delta;
        private readonly float _rampSeconds;
        private readonly float _cap;

        public Trajectory(
            Vector2 start,
            Vector2 velocity,
            Vector2 direction,
            Vector2 delta,
            float rampSeconds,
            float cap)
        {
            _start = start;
            _velocity = velocity;
            _direction = direction;
            _delta = delta;
            _rampSeconds = float.IsFinite(rampSeconds) ? MathF.Max(0f, rampSeconds) : 0f;
            _cap = cap;
        }

        public Vector2 PositionAt(float seconds)
        {
            if (seconds <= 0f) return _start;
            if (_rampSeconds <= 1e-4f) return _start + _direction * (_cap * seconds);

            if (seconds < _rampSeconds)
            {
                // p = p0 + v0·t + Δ·(t² / 2τ), because the velocity delta shrinks linearly to zero.
                float scale = 0.5f * seconds * seconds / _rampSeconds;
                return _start + _velocity * seconds + _delta * scale;
            }

            Vector2 endOfRamp = _start
                                + _velocity * _rampSeconds
                                + _delta * (0.5f * _rampSeconds);
            return endOfRamp + _direction * (_cap * (seconds - _rampSeconds));
        }

        public float SpeedAt(float seconds)
        {
            if (seconds <= 0f) return _velocity.Length();
            if (seconds >= _rampSeconds) return _cap;
            return (_velocity + _delta * (seconds / _rampSeconds)).Length();
        }
    }
}
