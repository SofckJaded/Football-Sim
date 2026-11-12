using System;
using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Physics;

/// <summary>
/// Shared pass planner (audit F01). Replaces the old "lead the receiver by a fraction of a second"
/// heuristics with one model of how long a pass actually takes to arrive.
/// </summary>
public readonly record struct PassPlan(
    bool Feasible,
    Vector2 AimDirectionXZ,
    Vector2 TargetXZ,
    float Power01,
    float FlightTimeSeconds,
    float DistanceMeters);

/// <summary>
/// Plans ground passes: given where the ball is and how the receiver is moving, solve for the point
/// the receiver can actually reach when the ball gets there, and the power that gets the ball there.
///
/// The distance/time table is calibrated once per process by rolling a <b>cloned</b> ball through the
/// real <see cref="BallPhysicsSolver"/> — the live ball, the match RNG and the live solver clock are
/// never touched. Planning then reduces to scanning that table, so it stays cheap enough to run for
/// every passing candidate on every tick.
///
/// The arrival time is the <b>earliest</b> time at which the ball has covered the distance to where
/// the receiver will be at that time. Solving it that way (rather than iterating "lead = speed ×
/// flight time") is what keeps the result stable: naive iteration walks the target further and
/// further away whenever the ball is slow.
/// </summary>
public sealed class PassPlanner
{
    private const int PowerSamples = 17;              // 0.20 .. 1.00 in 0.05 steps
    private const float MinPower = 0.20f;
    private const float PowerStep = 0.05f;
    private const float TimeStep = SimConstants.FixedDeltaTime;
    private const int TimeSamples = 480;              // 8 s of flight
    private const float MaxCalibrationDistance = 45f; // stay clear of the goal structures
    private const float ArrivalSpeedTarget = 5.0f;    // a trapable arrival speed

    /// <summary>
    /// Pace a pass should still have when it reaches the receiver. Playtests reported that nearly
    /// every AI pass was struck with almost no power: the planner used to pick the *lowest* power
    /// whose trapable range merely reached the target, so a 15 m pass crawled there in ~2 s at the
    /// 5 m/s trapable floor. Over eight 11v11 matches that cost 26.8 % of passes to interceptions,
    /// and passes at 50–59 % power completed only 66 %. A normal firm pass arrives at roughly this
    /// pace, so the power is now anchored to it and the flight is ~40 % shorter. It is only a
    /// *preference*: beyond the crisp range of a full-power pass (~22 m here) the planner falls back
    /// to the same lowest-power-that-reaches rule.
    /// </summary>
    private const float CrispArrivalSpeed = 9.0f;

    private const float MaxMeetingMiss = 1.0f;        // reception reach is ~1.24 m, leave margin

    private sealed record Calibration(
        float[,] DistanceAtTime,
        float[] RangeAtArrivalSpeed,
        float[] CrispRange);

    private static readonly Lazy<Calibration> SharedCalibration = new(Calibrate);

    private readonly PitchDimensions _pitch;
    private readonly Calibration _calibration;

    public PassPlanner(PitchDimensions pitch)
    {
        _pitch = pitch;
        _calibration = SharedCalibration.Value;
    }

    /// <summary>Longest distance a ground pass can cover and still arrive at a trapable speed.</summary>
    public float MaxPassDistance => _calibration.RangeAtArrivalSpeed[PowerSamples - 1];

    /// <summary>
    /// Plans a pass to a moving receiver. <paramref name="extraLeadMeters"/> pushes the target
    /// further into space along <paramref name="extraLeadDirection"/> (through balls / runs).
    /// </summary>
    public PassPlan PlanGroundPass(
        Vector2 originXZ,
        int receiverId,
        Vector2 receiverPositionXZ,
        Vector2 receiverVelocityXZ,
        float receiverMaxSpeed,
        float extraLeadMeters = 0f,
        Vector2 extraLeadDirection = default,
        float maxLeadMeters = 60f,
        float minPower01 = 0f)
    {
        _ = receiverId; // kept so callers can tag plans with their receiver

        Vector2 velocity = ClampSpeed(receiverVelocityXZ, receiverMaxSpeed);
        Vector2 extraLead = extraLeadMeters > 0f ? extraLeadDirection * extraLeadMeters : Vector2.Zero;

        float straightLineDistance = Vector2.Distance(originXZ, receiverPositionXZ);
        // Anchor the power to the *crisper* of the two tables: the lowest power that reaches the
        // distance at a trapable pace and the lowest power that reaches it at a firm, normal pass
        // pace. Using only the trapable table made almost every AI pass a slow roller.
        int powerIndex = Math.Max(
            PowerIndexForDistance(straightLineDistance),
            PowerIndexForCrispDistance(straightLineDistance));

        // A crisper pass shortens the flight, and a shorter flight means a shorter (safer) lead.
        // Used for human callers, whose movement cannot be forecast reliably.
        if (minPower01 > 0f)
        {
            int minPowerIndex = Math.Clamp((int)MathF.Round((minPower01 - MinPower) / PowerStep), 0, PowerSamples - 1);
            powerIndex = Math.Max(powerIndex, minPowerIndex);
        }

        if (!TrySolveArrivalTime(powerIndex, originXZ, receiverPositionXZ, velocity, extraLead, maxLeadMeters, out float flightTime))
        {
            // A slow pass can be a metre short of a moving receiver, so the power steps up until one
            // connects. It steps one notch at a time rather than jumping to full power: the old code
            // retried at maximum, which for a runner produced a five-second ball the receiver had to chase
            // (measured: a 43 m punt to a team-mate 12 m away). A firmer pass meets him sooner, and a
            // shorter flight is what makes the ball controllable when it arrives.
            powerIndex = -1;
            for (int candidate = PowerIndexForDistance(straightLineDistance) + 1; candidate < PowerSamples; candidate++)
            {
                if (!TrySolveArrivalTime(candidate, originXZ, receiverPositionXZ, velocity, extraLead, maxLeadMeters, out flightTime))
                    continue;

                powerIndex = candidate;
                break;
            }

            if (powerIndex < 0) return Infeasible(straightLineDistance);
        }

        Vector2 target = ClampToPitch(receiverPositionXZ
            + ClampVectorLength(velocity * flightTime, maxLeadMeters)
            + extraLead);
        float distance = Vector2.Distance(originXZ, target);

        // Clamping to the pitch can move the target beyond what this power reaches. Re-anchor both
        // tables together so the re-planned pass keeps its pace as well as its reach.
        if (distance > _calibration.RangeAtArrivalSpeed[powerIndex] + 0.5f)
        {
            powerIndex = Math.Max(PowerIndexForDistance(distance), PowerIndexForCrispDistance(distance));
            if (!TrySolveArrivalTime(powerIndex, originXZ, receiverPositionXZ, velocity, extraLead, maxLeadMeters, out flightTime))
                return Infeasible(distance);

            target = ClampToPitch(receiverPositionXZ
                + ClampVectorLength(velocity * flightTime, maxLeadMeters)
                + extraLead);
            distance = Vector2.Distance(originXZ, target);
        }

        if (distance < 0.5f || distance > MaxCalibrationDistance)
            return Infeasible(distance);

        return new PassPlan(
            Feasible: true,
            AimDirectionXZ: SimMath.SafeNormalize(target - originXZ, new Vector2(1f, 0f)),
            TargetXZ: target,
            Power01: MinPower + powerIndex * PowerStep,
            FlightTimeSeconds: flightTime,
            DistanceMeters: distance);
    }

    /// <summary>Ball speed leaving the foot for a ground pass at the given power (mirrors the kick model).</summary>
    public static float GroundPassLaunchSpeed(float power01) =>
        Math.Clamp(8.0f + Math.Clamp(power01, 0.15f, 1.0f) * 11.5f, 8.0f, 19.5f);

    /// <summary>Flight time for a power/distance pair; exposed for diagnostics and tests.</summary>
    public bool TryGetFlightTime(float power01, float distanceMeters, out float flightTimeSeconds)
    {
        int powerIndex = Math.Clamp((int)MathF.Round((power01 - MinPower) / PowerStep), 0, PowerSamples - 1);
        flightTimeSeconds = 0f;
        if (distanceMeters <= 0f) return false;

        for (int k = 1; k < TimeSamples; k++)
        {
            if (_calibration.DistanceAtTime[powerIndex, k] >= distanceMeters)
            {
                flightTimeSeconds = k * TimeStep;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Distance the ball has covered <paramref name="timeSeconds"/> after the kick at this power.
    /// The inverse of <see cref="TryGetFlightTime"/>: the pass-lane interception test needs to know
    /// where the rolling ball actually is while a defender runs, not just when it arrives.
    /// </summary>
    public float BallDistanceAtTime(float power01, float timeSeconds)
    {
        int powerIndex = Math.Clamp((int)MathF.Round((power01 - MinPower) / PowerStep), 0, PowerSamples - 1);
        int sample = Math.Clamp((int)MathF.Round(timeSeconds / TimeStep), 0, TimeSamples - 1);
        return _calibration.DistanceAtTime[powerIndex, sample];
    }

    /// <summary>Distance a pass at this power can cover and still arrive at a trapable speed.</summary>
    public float RangeFor(float power01)
    {
        int powerIndex = Math.Clamp((int)MathF.Round((power01 - MinPower) / PowerStep), 0, PowerSamples - 1);
        return _calibration.RangeAtArrivalSpeed[powerIndex];
    }

    /// <summary>
    /// Distance a pass at this power can cover and still arrive at a firm, normal pass pace
    /// (<see cref="CrispArrivalSpeed"/>). Powers whose range is zero cannot reach even a few metres
    /// at that pace.
    /// </summary>
    public float CrispRangeFor(float power01)
    {
        int powerIndex = Math.Clamp((int)MathF.Round((power01 - MinPower) / PowerStep), 0, PowerSamples - 1);
        return _calibration.CrispRange[powerIndex];
    }

    /// <summary>
    /// Picks the arrival time with the smallest miss between the distance the ball can cover by then
    /// and the distance to where the receiver will be. A perpendicular runner can never be met
    /// exactly (their radial speed approaches the decaying ball speed), so "closest approach" — the
    /// same quantity the acceptance criteria measure — is the right objective.
    /// </summary>
    private bool TrySolveArrivalTime(
        int powerIndex,
        Vector2 originXZ,
        Vector2 receiverPositionXZ,
        Vector2 receiverVelocityXZ,
        Vector2 extraLead,
        float maxLeadMeters,
        out float flightTime)
    {
        flightTime = 0f;
        float bestMiss = float.MaxValue;
        float bestTime = 0f;
        float previousBallDistance = 0f;

        for (int k = 1; k < TimeSamples; k++)
        {
            float t = k * TimeStep;
            float ballDistance = _calibration.DistanceAtTime[powerIndex, k];
            if (ballDistance <= previousBallDistance + 1e-4f && k > 2)
                break; // the ball has stopped; later times cannot help
            previousBallDistance = ballDistance;

            // A human receiver may turn or stop at any moment, so the lead is bounded: without this a
            // sprinting caller is "led" 15+ m upfield and the pass looks like it was aimed at nobody.
            Vector2 receiverAtTime = receiverPositionXZ
                + ClampVectorLength(receiverVelocityXZ * t, maxLeadMeters)
                + extraLead;
            float required = Vector2.Distance(originXZ, receiverAtTime);
            float miss = MathF.Abs(required - ballDistance);

            if (miss < bestMiss)
            {
                bestMiss = miss;
                bestTime = t;
            }
        }

        flightTime = bestTime;
        return bestMiss <= MaxMeetingMiss;
    }

    private static Vector2 ClampVectorLength(Vector2 v, float maxLength)
    {
        float length = v.Length();
        if (!float.IsFinite(length) || length <= maxLength || length <= 1e-4f) return v;
        return v * (maxLength / length);
    }

    private PassPlan Infeasible(float distance) =>
        new(false, new Vector2(1f, 0f), Vector2.Zero, 0f, 0f, distance);

    private Vector2 ClampToPitch(Vector2 p) => new(
        Math.Clamp(p.X, -_pitch.HalfLength + 1f, _pitch.HalfLength - 1f),
        Math.Clamp(p.Y, -_pitch.HalfWidth + 1f, _pitch.HalfWidth - 1f));

    private static Vector2 ClampSpeed(Vector2 velocity, float maxSpeed)
    {
        if (!SimMath.IsFinite(velocity)) return Vector2.Zero;
        float speed = velocity.Length();
        if (speed <= maxSpeed || speed <= 1e-4f) return velocity;
        return velocity * (maxSpeed / speed);
    }

    /// <summary>Lowest power whose trapable range still reaches the target distance.</summary>
    private int PowerIndexForDistance(float distance)
    {
        for (int i = 0; i < PowerSamples; i++)
            if (_calibration.RangeAtArrivalSpeed[i] >= distance)
                return i;
        return PowerSamples - 1;
    }

    /// <summary>
    /// Lowest power whose range at <see cref="CrispArrivalSpeed"/> still reaches the target distance.
    /// A full-power ground pass only carries a crisp pace for ~22 m, so anything longer than that
    /// falls back to the maximum (the ball still arrives trapable, just later) rather than being
    /// struck gently the whole way.
    /// </summary>
    private int PowerIndexForCrispDistance(float distance)
    {
        for (int i = 0; i < PowerSamples; i++)
            if (_calibration.CrispRange[i] >= distance)
                return i;
        return PowerSamples - 1;
    }

    /// <summary>
    /// Rolls a cloned ball straight down the pitch with the real solver and records how far it has
    /// travelled at each 60 Hz sample. One-time, isolated from the live simulation.
    /// </summary>
    private static Calibration Calibrate()
    {
        var distanceAtTime = new float[PowerSamples, TimeSamples];
        var range = new float[PowerSamples];
        var crispRange = new float[PowerSamples];

        for (int powerIndex = 0; powerIndex < PowerSamples; powerIndex++)
        {
            float power = MinPower + powerIndex * PowerStep;
            var ball = new BallState
            {
                Position = new Vector3(0f, 0.11f, 0f),
                Velocity = new Vector3(GroundPassLaunchSpeed(power), 0.05f, 0f)
            };

            var solver = new BallPhysicsSolver();
            float lastDistance = 0f;
            bool capturedRange = false;
            bool capturedCrisp = false;

            for (int k = 1; k < TimeSamples; k++)
            {
                solver.Step(ball, TimeStep);

                float distance = MathF.Max(lastDistance, ball.Position.X);
                distance = MathF.Min(distance, MaxCalibrationDistance);
                distanceAtTime[powerIndex, k] = distance;
                lastDistance = distance;

                float speed = ball.Velocity.Length();
                if (!capturedRange && speed <= ArrivalSpeedTarget)
                {
                    range[powerIndex] = distance;
                    capturedRange = true;
                }

                if (!capturedCrisp && speed <= CrispArrivalSpeed)
                {
                    crispRange[powerIndex] = distance;
                    capturedCrisp = true;
                }

                if (distance >= MaxCalibrationDistance - 0.01f)
                {
                    for (int rest = k + 1; rest < TimeSamples; rest++)
                        distanceAtTime[powerIndex, rest] = distance;
                    break;
                }
            }

            if (!capturedRange)
                range[powerIndex] = lastDistance;
            if (!capturedCrisp)
                crispRange[powerIndex] = lastDistance;
        }

        return new Calibration(distanceAtTime, range, crispRange);
    }
}
