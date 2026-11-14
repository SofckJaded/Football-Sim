using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Physics;

/// <summary>The three pass-shaped airborne deliveries the AI compares (A5.5).</summary>
public enum RaisedPassProfile
{
    /// <summary>A lob to a team-mate's feet — the "raised alternative" when the ground lane is blocked.</summary>
    Lofted,

    /// <summary>A whipped cross into the box, aimed at a team-mate's announced arrival region.</summary>
    Cross,

    /// <summary>A fast, flat ball that skips off the surface — the driven alternative.</summary>
    Driven
}

/// <summary>
/// The AI's prediction of an airborne pass (A5.5): where the ball actually comes down, and when.
///
/// Before this, the raised alternative was priced by a drag-free kinematic range shortened by a flat 15 %
/// (<c>PassSuccessModel.LoftedDragShortfall01</c>), which is why the A4 audit recorded every raised ball
/// from the carrier's own action set being collected by the opposition: the model was aiming at a landing
/// point the ball never reached, and the flight time it charged was not the flight time it took. This type
/// replaces that estimate with a table calibrated from the <b>real</b> solver — the same
/// <see cref="KickLaunchModel"/> launch the execution layer strikes with, rolled through a cloned
/// <see cref="BallPhysicsSolver"/> with the same drag, Magnus spin, bounce and roll, exactly the way
/// <see cref="PassPlanner"/> calibrates its ground table.
///
/// The table is per profile and per power, and records the distance the ball has covered at every 60 Hz
/// sample (monotone, because the ball only ever travels along the aim) plus the first landing — the moment
/// the ball comes back down to the surface — which is the point a receiver attacks. For a driven ball the
/// samples after the landing are the skips and the roll, so a caller can ask "how far has it gone by time t"
/// as well as "where does it first come down".
///
/// Zero spin is used for the side-spin component (the AI commands zero); the back/top spin each profile
/// applies is part of the launch and therefore part of the calibration.
/// </summary>
public sealed class RaisedPassModel
{
    /// <summary>Profiles in the table.</summary>
    public const int ProfileCount = 3;

    /// <summary>Power samples: 0.20 … 1.00 in 0.05 steps, the grid the ground planner uses.</summary>
    public const int PowerSamples = 17;

    /// <summary>Lowest sampled power.</summary>
    public const float MinPower = 0.20f;

    /// <summary>Power step of the table.</summary>
    public const float PowerStep = 0.05f;

    /// <summary>8 s of flight — longer than any pass the AI will strike.</summary>
    public const int TimeSamples = 480;

    /// <summary>
    /// Calibration stop, in metres. Deliberately short of the goal line and the byline so a calibration
    /// ball never meets the pitch boundary logic: the longest pass the AI intends is 32 m, and the longest
    /// ball in the table is a full-power cross at roughly that range.
    /// </summary>
    public const float MaxCalibrationDistance = 48f;

    /// <summary>
    /// How far from the requested point a landing may be before the delivery is not offered, in metres.
    /// A lofted ball that comes down four metres from the man it was meant for is not a pass to him; the
    /// power grid is coarse enough (a step is 1–3 m of range) that a tighter bound would refuse ordinary
    /// deliveries for the grid's sake rather than the ball's.
    /// </summary>
    public const float MaxLandingMissMeters = 4f;

    private static readonly Lazy<RaisedPassModel> Shared = new(Calibrate);

    /// <summary>The shared calibration. Built once, never touches the live ball, the RNG or the match clock.</summary>
    public static RaisedPassModel Default => Shared.Value;

    private readonly float[][,] _distanceAtTime;
    private readonly float[][,] _heightAtTime;
    private readonly float[][] _landingRange;
    private readonly float[][] _landingTime;

    private RaisedPassModel()
    {
        _distanceAtTime = new float[ProfileCount][,];
        _heightAtTime = new float[ProfileCount][,];
        _landingRange = new float[ProfileCount][];
        _landingTime = new float[ProfileCount][];

        for (int profile = 0; profile < ProfileCount; profile++)
        {
            _distanceAtTime[profile] = new float[PowerSamples, TimeSamples];
            _heightAtTime[profile] = new float[PowerSamples, TimeSamples];
            _landingRange[profile] = new float[PowerSamples];
            _landingTime[profile] = new float[PowerSamples];
        }

        CalibrateInto(_distanceAtTime, _heightAtTime, _landingRange, _landingTime);
    }

    /// <summary>Distance the ball has covered <paramref name="seconds"/> after the kick, in metres.</summary>
    public float DistanceAtTime(RaisedPassProfile profile, float power01, float seconds)
    {
        int powerIndex = PowerIndexOf(power01);
        int sample = Math.Clamp((int)MathF.Round(seconds / SimConstants.FixedDeltaTime), 0, TimeSamples - 1);
        return _distanceAtTime[(int)profile][powerIndex, sample];
    }

    /// <summary>
    /// Height of the ball when it is <paramref name="distanceMeters"/> along its flight, in metres. The
    /// airborne lane test reads this: a ball 2.5 m up is not stopped by a defender standing on the line.
    /// </summary>
    public float HeightAtDistance(RaisedPassProfile profile, float power01, float distanceMeters)
    {
        if (!float.IsFinite(distanceMeters)) return 0f;

        int powerIndex = PowerIndexOf(power01);
        int profileIndex = (int)profile;

        for (int k = 1; k < TimeSamples; k++)
        {
            if (_distanceAtTime[profileIndex][powerIndex, k] < distanceMeters) continue;
            return _heightAtTime[profileIndex][powerIndex, k];
        }

        return 0f;
    }

    /// <summary>Where and when one power's ball first comes back down.</summary>
    public bool TryLanding(
        RaisedPassProfile profile,
        float power01,
        out float rangeMeters,
        out float flightSeconds)
    {
        int powerIndex = PowerIndexOf(power01);
        rangeMeters = _landingRange[(int)profile][powerIndex];
        flightSeconds = _landingTime[(int)profile][powerIndex];
        return rangeMeters > 0.5f && flightSeconds > 0f;
    }

    /// <summary>
    /// The power whose landing is closest to <paramref name="targetDistanceMeters"/>, with the landing it
    /// actually produces. Returns false when no power lands within
    /// <see cref="MaxLandingMissMeters"/> of the target — the honest answer for a ball the execution layer
    /// cannot drop where the run is going.
    /// </summary>
    public bool TryPowerForLanding(
        RaisedPassProfile profile,
        float targetDistanceMeters,
        out float power01,
        out float landingRangeMeters,
        out float flightSeconds)
    {
        power01 = 0f;
        landingRangeMeters = 0f;
        flightSeconds = 0f;
        if (!float.IsFinite(targetDistanceMeters) || targetDistanceMeters <= 1f) return false;

        int profileIndex = (int)profile;
        int bestIndex = -1;
        float bestMiss = float.MaxValue;

        for (int i = 0; i < PowerSamples; i++)
        {
            float range = _landingRange[profileIndex][i];
            if (range <= 0.5f) continue;

            float miss = MathF.Abs(range - targetDistanceMeters);
            if (miss >= bestMiss) continue;

            bestMiss = miss;
            bestIndex = i;
        }

        if (bestIndex < 0 || bestMiss > MaxLandingMissMeters) return false;

        power01 = MinPower + bestIndex * PowerStep;
        landingRangeMeters = _landingRange[profileIndex][bestIndex];
        flightSeconds = _landingTime[profileIndex][bestIndex];
        return true;
    }

    /// <summary>Longest landing in the table for this profile, in metres. Diagnostics and bounds.</summary>
    public float MaxLandingRange(RaisedPassProfile profile)
    {
        float best = 0f;
        for (int i = 0; i < PowerSamples; i++)
            best = MathF.Max(best, _landingRange[(int)profile][i]);
        return best;
    }

    private static int PowerIndexOf(float power01)
    {
        float power = float.IsFinite(power01) ? power01 : MinPower;
        return Math.Clamp((int)MathF.Round((power - MinPower) / PowerStep), 0, PowerSamples - 1);
    }

    /// <summary>
    /// Rolls one cloned ball per (profile, power) with the real solver and records the distance/time table and
    /// the first landing. Isolated from the live simulation: a fresh solver, a fresh ball, no RNG.
    /// </summary>
    private static void CalibrateInto(
        float[][,] distanceAtTime,
        float[][,] heightAtTime,
        float[][] landingRange,
        float[][] landingTime)
    {
        const float dt = SimConstants.FixedDeltaTime;

        for (int profileIndex = 0; profileIndex < ProfileCount; profileIndex++)
        {
            RaisedPassProfile profile = (RaisedPassProfile)profileIndex;
            float[,] table = distanceAtTime[profileIndex];
            float[,] heights = heightAtTime[profileIndex];

            for (int powerIndex = 0; powerIndex < PowerSamples; powerIndex++)
            {
                float power = MinPower + powerIndex * PowerStep;
                KickLaunch launch = KickLaunchModel.For(
                    IntentOf(profile), power, new Vector2(1f, 0f));

                var ball = new BallState
                {
                    Position = new Vector3(0f, KickLaunchModel.LaunchHeight, 0f),
                    Velocity = launch.Velocity,
                    AngularVelocity = launch.Spin
                };

                var solver = new BallPhysicsSolver();
                float lastDistance = 0f;
                bool landed = false;
                int k = 1;

                for (; k < TimeSamples; k++)
                {
                    solver.Step(ball, dt);

                    float distance = MathF.Min(MathF.Max(lastDistance, ball.Position.X), MaxCalibrationDistance);
                    table[powerIndex, k] = distance;
                    heights[powerIndex, k] = ball.Position.Y;
                    lastDistance = distance;

                    // The landing is the solver's own first ground contact — a bounce that a 60 Hz sample
                    // may otherwise step over (the sampled height is already on its way back up). The
                    // contact point is the exact place the ball came down.
                    if (!landed && TryFindGroundContact(ball, out Vector3 contact))
                    {
                        landingRange[profileIndex][powerIndex] =
                            MathF.Min(MathF.Max(0f, contact.X), MaxCalibrationDistance);
                        landingTime[profileIndex][powerIndex] = k * dt;
                        landed = true;
                    }

                    if (distance >= MaxCalibrationDistance - 0.01f) break;
                }

                // The tail of the table is the final distance: a ball that has stopped cannot move again
                // inside the window, and a caller reading a late sample must not see a zero.
                for (int rest = k; rest < TimeSamples; rest++)
                    table[powerIndex, rest] = table[powerIndex, k - 1];

                if (landed) continue;

                // A ball that never touches the ground inside the window is scored at the distance it
                // reached, which keeps the table monotone and the caller honest.
                landingRange[profileIndex][powerIndex] = lastDistance;
                landingTime[profileIndex][powerIndex] = (TimeSamples - 1) * dt;
            }
        }
    }

    /// <summary>The first ground contact the solver recorded on the last step, if any.</summary>
    private static bool TryFindGroundContact(BallState ball, out Vector3 contactPoint)
    {
        List<BallCollisionEvent> collisions = ball.RecentCollisions;
        for (int i = 0; i < collisions.Count; i++)
        {
            if (!string.Equals(collisions[i].SurfaceType, "Ground", StringComparison.Ordinal)) continue;
            contactPoint = collisions[i].ContactPoint;
            return true;
        }

        contactPoint = default;
        return false;
    }

    private static KickIntentType IntentOf(RaisedPassProfile profile) => profile switch
    {
        RaisedPassProfile.Cross => KickIntentType.Cross,
        RaisedPassProfile.Driven => KickIntentType.DrivenPass,
        _ => KickIntentType.LoftedPass
    };

    private static RaisedPassModel Calibrate() => new();
}
