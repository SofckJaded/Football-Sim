using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Physics;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// Regression tests for AUD-P5 / AUD-P7 ("Fix 4") of the 2026-11-23 passing-and-space audit — the
/// ground-pass physics the audit's forensic traces measured.
///
/// <b>The pace story (recorded here because the numbers are the argument).</b> AUD-P5 fix 4.2 proposed
/// lowering the crisp arrival pace from 9.0 to ~6.5–7.0 m/s to shorten the overhit roll. That was tried and
/// <b>reversed on measurement</b>: at 7.0 m/s a 17 m pass anchored at 0.70 power instead of 0.80 and its
/// flight lengthened from 1.317 s to 1.483 s (20 m: 1.433 s → 1.683 s); the longer flight made clean passes
/// interceptable under the AIB-02a physics lane model (two AIB-02b congestion fixtures flipped to
/// lane-blocked merely from a 10 m flight going 0.867 s → 1.033 s). The audit's own evidence records slow
/// rollers costing 26.8 % interceptions while <c>ai55</c> completes 80–83 % of passes at the 9.0 m/s pace.
/// So the pace is <b>restored to 9.0 m/s</b> and the overhit half of AUD-P5 is owned by the AUD-P7 stop cap
/// (a fast ball aimed near a line is power-capped, and the plan is kept rather than refused).
///
/// Tests 1–3 pin that restored 9.0 baseline as regression guards: they pass on the pre-fix code by design,
/// and a later pace change must justify itself by updating them with new measurement. The rule tests
/// (AUD-P7 caps, fix-4.1 minimum arrival pace, <see cref="PassPlanner.MinCrispPower01"/>) are new
/// behaviour and fail without the change (the pace parameter does not exist pre-fix; the uncapped plan is
/// the pre-fix answer). Every fixture states the arithmetic in its comment.
/// </summary>
public sealed class Aib2PassPhysicsTests
{
    private static readonly PitchDimensions Pitch = new();
    private static readonly PassPlanner Planner = new(Pitch);
    private const float Dt = SimConstants.FixedDeltaTime;

    // ---------------------------------------------------------------------------------------------
    // AUD-P5 pace baseline (restored 9.0 m/s) — regression guards, not defect repros
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Regression guard for the restored AUD-P5 pace: a 17 m crisp pass is anchored to power 0.80 (the
    /// lowest power whose 9.0 m/s crisp range reaches 17 m — 17.13 m), a 17.20 m/s launch, and its
    /// closest-approach flight is 1.317 s. The ball's calibrated resting point is 48.04 m from the passer,
    /// i.e. it rolls 31.04 m past a 17 m target if nobody touches it — the overhit that AUD-P7's stop cap
    /// handles when the target is near a line (the central target here is not, so nothing is capped).
    ///
    /// These are the pre-fix numbers on purpose: fix 4.2's experimental 7.0 m/s pace moved them to 0.70 /
    /// 16.05 m/s / 1.483 s and was reversed because the longer flight increased lane blocking (see the
    /// class comment). A future pace change must update this fixture with measurement evidence.
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_SeventeenMetreCrispPass_PinsTheNinePace()
    {
        const float target = 17f;
        PassPlan plan = Planner.PlanGroundPass(Vector2.Zero, 9, new Vector2(target, 0f), Vector2.Zero, 8f);
        Assert.True(plan.Feasible);

        Assert.Equal(0.80f, plan.Power01, 3);
        Assert.Equal(17.20f, PassPlanner.GroundPassLaunchSpeed(plan.Power01), 2);
        Assert.Equal(1.3167f, plan.FlightTimeSeconds, 3);

        Vector2 rest = MeasureRestPosition(Vector2.Zero, plan.AimDirectionXZ, plan.Power01, out _);
        float overrun = Vector2.Distance(Vector2.Zero, rest) - plan.DistanceMeters;
        Assert.True(MathF.Abs(overrun - 31.04f) <= 0.1f,
            $"a 17 m crisp pass rolls {overrun:F2} m past the target; the 9.0 m/s baseline is 31.04 m " +
            "(this is the roll AUD-P7's cap owns near a line)");
    }

    /// <summary>
    /// Regression guard for the restored AUD-P5 pace, the 20 m case: anchor power 0.95 (the lowest power
    /// whose 9.0 m/s crisp range reaches 20 m — 19.81 m), 18.92 m/s launch, 1.433 s flight. The ball is
    /// still rolling when it reaches the 52 m stop-calibration clearance (measured rest 52.01 m there), so
    /// the AUD-P7 calibration marks this power saturated and treats it as stopping far away (180 m) — the
    /// explicit saturation check that stops the old 45 m table cap hiding a real overrun. On a near-line
    /// target that is what triggers the cap (tested below); this central target is untouched.
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_TwentyMetreCrispPass_PinsTheNinePace()
    {
        const float target = 20f;
        PassPlan plan = Planner.PlanGroundPass(Vector2.Zero, 9, new Vector2(target, 0f), Vector2.Zero, 8f);
        Assert.True(plan.Feasible);

        Assert.Equal(0.95f, plan.Power01, 3);
        Assert.Equal(18.92f, PassPlanner.GroundPassLaunchSpeed(plan.Power01), 2);
        Assert.Equal(1.4333f, plan.FlightTimeSeconds, 3);

        Vector2 rest = MeasureRestPosition(Vector2.Zero, plan.AimDirectionXZ, plan.Power01, out _);
        float restDistance = Vector2.Distance(Vector2.Zero, rest);
        Assert.True(restDistance >= 51.9f,
            $"the fixture expects a saturated stop (still rolling at the 52 m clearance); rest was {restDistance:F2} m");
        Assert.True(restDistance - plan.DistanceMeters >= 31.9f,
            $"the 20 m ball rolls {restDistance - plan.DistanceMeters:F2} m past the target (baseline ≥32.0 m)");
    }

    /// <summary>
    /// The restored 9.0 m/s calibration is the pre-fix one: the trapable reach is still 37.63 m and the
    /// full-power crisp range is 22.57 m (the rejected 7.0 m/s experiment would have grown the latter to
    /// 28.35 m, which is how an "innocent" pace change silently moves every power anchor). Reach must not
    /// be shortened by any of this: <see cref="PassPlanner.MaxPassDistance"/> is the same 37.63 m.
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_Calibration_ReachIsPreserved()
    {
        Assert.True(Planner.MaxPassDistance >= 37.6f,
            $"the trapable reach was shortened to {Planner.MaxPassDistance:F2} m (baseline 37.63 m)");
        Assert.Equal(22.57f, Planner.CrispRangeFor(1f), 1);
    }

    // ---------------------------------------------------------------------------------------------
    // AUD-P5 / fix 4.1 — MinCrispPower01 is the firm-ball floor for a candidate-side caller
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// <see cref="PassPlanner.MinCrispPower01"/> is the lowest grid power whose crisp range still reaches
    /// the distance at the restored 9.0 m/s pace: 8 m ⇒ 0.45 (crisp range 8.06 m), 15 m ⇒ 0.75 (15.75 m),
    /// 20 m ⇒ 0.95 (21.25 m), 22 m ⇒ 1.00 (21.25 m is short, full power reaches 22.57 m). It must be
    /// monotone (a longer pass can never need a gentler firm ball), each answer must actually reach while
    /// some power still can, and the next gentler grid step must not. (Pre-fix this API does not exist.)
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_MinCrispPower_IsLowestFirmPowerAndMonotone()
    {
        float[] distances = { 8f, 15f, 20f, 22f };
        float[] expected = { 0.45f, 0.75f, 0.95f, 1.00f };

        float previous = 0f;
        for (int i = 0; i < distances.Length; i++)
        {
            float power = Planner.MinCrispPower01(distances[i]);
            Assert.Equal(expected[i], power, 3);
            Assert.True(power >= previous - 1e-4f,
                $"MinCrispPower01 is not monotone: {power:F2} at {distances[i]} m after {previous:F2}");
            previous = power;

            Assert.True(Planner.CrispRangeFor(power) >= distances[i],
                $"power {power:F2} reaches only {Planner.CrispRangeFor(power):F2} m at a crisp pace, " +
                $"not the requested {distances[i]} m");
            if (power > 0.20f + 1e-4f)
            {
                Assert.True(Planner.CrispRangeFor(power - 0.05f) < distances[i],
                    $"power {power - 0.05f:F2} also reaches {distances[i]} m: the floor is not the lowest");
            }
        }

        // Past the full-power crisp range (22.57 m) the floor stays clamped at the firmest power.
        Assert.Equal(1.00f, Planner.MinCrispPower01(25f), 3);
        Assert.True(Planner.MinCrispPower01(25f) >= Planner.MinCrispPower01(22f));
    }

    /// <summary>
    /// Beyond the full-power crisp range the method clamps to the grid's firmest power (1.00), exactly like
    /// the private anchor it mirrors. At the restored 9.0 m/s pace that range is only 22.57 m, so a 25 m or
    /// 30 m request reports 1.00 and <see cref="PassPlanner.CrispRangeFor"/> honestly reports 22.57 m — the
    /// "reaches at crisp pace" property only holds up to ~22.6 m, and the clamp is the documented answer
    /// beyond it. A non-finite distance is clamped to the grid too, never returned as NaN.
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_MinCrispPower_BeyondFullPowerCrispRange_ClampsToFullPower()
    {
        Assert.Equal(1.00f, Planner.MinCrispPower01(25f), 3);
        Assert.Equal(1.00f, Planner.MinCrispPower01(30f), 3);
        Assert.Equal(1.00f, Planner.MinCrispPower01(float.PositiveInfinity), 3);
        Assert.Equal(1.00f, Planner.MinCrispPower01(float.NaN), 3);
        Assert.Equal(0.20f, Planner.MinCrispPower01(float.NegativeInfinity), 3);
        Assert.Equal(0.20f, Planner.MinCrispPower01(0f), 3);
        Assert.Equal(22.57f, Planner.CrispRangeFor(1f), 1);
    }

    // ---------------------------------------------------------------------------------------------
    // AUD-P5 / fix 4.1 — PlanGroundPassToRegion minimum arrival pace
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The audit's slow-roller defect: a timed 20 m ball wanted at the runner's 2.0 s meeting is struck at
    /// power 0.65 (14.90 m/s launch, 2.067 s flight) when the caller asks for nothing, and arrives at only
    /// ~6.08 m/s — the roller defenders walk across. A requested 7 m/s arrival pace raises the effective
    /// minimum power to 0.80 (17.20 m/s, 1.700 s flight); a requested 9 m/s pace raises it to 0.95
    /// (18.92 m/s, 1.433 s flight). The ball arrives early; <see cref="PassSuccessModel"/>'s promise timing
    /// term prices that, and the caller decides whether the interception-risk trade is worth it.
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_TimedRegion_MinArrivalPace_UsesAFirmerShorterBall()
    {
        PassPlan slow = Planner.PlanGroundPassToRegion(
            Vector2.Zero, new Vector2(20f, 0f), arriveInSeconds: 2.0f);
        PassPlan seven = Planner.PlanGroundPassToRegion(
            Vector2.Zero, new Vector2(20f, 0f), arriveInSeconds: 2.0f,
            minPower01: 0f, minArrivalSpeedMetersPerSecond: 7f);
        PassPlan nine = Planner.PlanGroundPassToRegion(
            Vector2.Zero, new Vector2(20f, 0f), arriveInSeconds: 2.0f,
            minPower01: 0f, minArrivalSpeedMetersPerSecond: 9f);

        Assert.True(slow.Feasible);
        Assert.True(seven.Feasible);
        Assert.True(nine.Feasible);
        Assert.Equal(0.65f, slow.Power01, 3);
        Assert.Equal(0.80f, seven.Power01, 3);
        Assert.Equal(0.95f, nine.Power01, 3);

        Assert.True(nine.Power01 >= seven.Power01 - 1e-4f && seven.Power01 > slow.Power01,
            $"a higher arrival floor must never give a gentler ball ({slow.Power01:F2} → {seven.Power01:F2} → {nine.Power01:F2})");
        Assert.True(nine.Power01 >= slow.Power01 + 0.25f,
            $"a 9 m/s arrival floor only moved the 20 m timed ball from {slow.Power01:F2} to {nine.Power01:F2}");
        Assert.True(nine.FlightTimeSeconds <= slow.FlightTimeSeconds - 0.5f,
            $"the firmer ball must arrive sooner ({nine.FlightTimeSeconds:F2} s against {slow.FlightTimeSeconds:F2} s)");
    }

    /// <summary>
    /// The pace floor is a physical promise, not a label: the arrival speed derived from the calibrated
    /// distance/time samples at the 20 m target is ≥ the requested 9 m/s (measured 9.61 m/s), while the
    /// default plan arrives at 6.08 m/s — the defect the parameter exists to let a caller avoid. The 7 m/s
    /// floor lands at 7.28 m/s, so the floor is neither overshot into a full-power ball nor undershot.
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_TimedRegion_RequestedArrivalPace_IsMet()
    {
        const float distance = 20f;
        PassPlan slow = Planner.PlanGroundPassToRegion(
            Vector2.Zero, new Vector2(distance, 0f), arriveInSeconds: 2.0f);
        PassPlan seven = Planner.PlanGroundPassToRegion(
            Vector2.Zero, new Vector2(distance, 0f), arriveInSeconds: 2.0f,
            minPower01: 0f, minArrivalSpeedMetersPerSecond: 7f);
        PassPlan nine = Planner.PlanGroundPassToRegion(
            Vector2.Zero, new Vector2(distance, 0f), arriveInSeconds: 2.0f,
            minPower01: 0f, minArrivalSpeedMetersPerSecond: 9f);
        Assert.True(slow.Feasible && seven.Feasible && nine.Feasible);

        float slowSpeed = ArrivalSpeed(slow.Power01, distance);
        float sevenSpeed = ArrivalSpeed(seven.Power01, distance);
        float nineSpeed = ArrivalSpeed(nine.Power01, distance);

        Assert.True(nineSpeed >= 9f - 0.05f,
            $"a ball asked to arrive at 9 m/s actually arrives at {nineSpeed:F2} m/s");
        Assert.True(sevenSpeed >= 7f - 0.05f && sevenSpeed < 9f,
            $"a 7 m/s floor arrived at {sevenSpeed:F2} m/s");
        Assert.True(slowSpeed < 7f,
            $"the fixture's default plan already arrives at {slowSpeed:F2} m/s: it does not exercise the defect");
    }

    /// <summary>
    /// Default-argument compatibility: a three-argument call (and an explicit 0 for the new parameter)
    /// produce exactly the plan the pre-fix code produced — power 0.65, 2.067 s flight, same target and
    /// aim. Every existing call site is unchanged.
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_TimedRegion_DefaultArrivalPace_KeepsTheExistingPlan()
    {
        Vector2 target = new(20f, 0f);
        PassPlan threeArgument = Planner.PlanGroundPassToRegion(Vector2.Zero, target, arriveInSeconds: 2.0f);
        PassPlan explicitZero = Planner.PlanGroundPassToRegion(
            Vector2.Zero, target, arriveInSeconds: 2.0f,
            minPower01: 0f, minArrivalSpeedMetersPerSecond: 0f);

        Assert.True(threeArgument.Feasible);
        Assert.True(explicitZero.Feasible);
        Assert.Equal(threeArgument.Power01, explicitZero.Power01);
        Assert.Equal(threeArgument.FlightTimeSeconds, explicitZero.FlightTimeSeconds);
        Assert.Equal(threeArgument.TargetXZ, explicitZero.TargetXZ);
        Assert.Equal(threeArgument.AimDirectionXZ, explicitZero.AimDirectionXZ);

        // The shared default is the pre-fix plan, verified against the tables.
        Assert.Equal(0.65f, threeArgument.Power01, 3);
        Assert.Equal(2.0667f, threeArgument.FlightTimeSeconds, 3);
    }

    // ---------------------------------------------------------------------------------------------
    // AUD-P7 / fix 4.3 — touchline-overrun cap in PlanGroundPass
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A pass from (0, −28) to a receiver at (18, −31) — a target 3 m inside the touchline, with the aim
    /// carrying on outward — is planned uncapped at power 0.85 (the 9.0 m/s anchor: crisp range 18.53 m ≥
    /// 18.25 m; a 17.78 m/s launch). That ball's calibrated resting point is 49.80 m, so its projected stop
    /// is at z = −31 − 0.1644 × (49.80 − 18.25) = <b>−36.19, i.e. 2.19 m outside the touchline</b>: the
    /// audit's "simple pass rolls out of bounds". The cap walks the power down to 0.55 (14.33 m/s), whose
    /// projected stop is z = −34.40, inside the allowed 34.5 m. The plan is kept (never refused), only the
    /// power is capped, and the assertion compares against the uncapped anchor so it fails with the cap
    /// disabled.
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_TouchlineOverrun_IsCappedInsideThePitch()
    {
        Vector2 origin = new(0f, -28f);
        Vector2 receiver = new(18f, -31f);
        float expectedUncappedPower = AnchoredPower(Planner, Vector2.Distance(origin, receiver));
        Assert.Equal(0.85f, expectedUncappedPower, 3);

        PassPlan plan = Planner.PlanGroundPass(origin, 9, receiver, Vector2.Zero, 8f);
        Assert.True(plan.Feasible);
        Vector2 projected = ProjectedStop(plan, origin, out float stopDistance);
        Assert.True(plan.Power01 < expectedUncappedPower - 0.05f,
            $"a pass that would roll {MathF.Abs(projected.Y) - Pitch.HalfWidth:F2} m past the " +
            $"touchline was planned at {plan.Power01:F2}, not capped below the uncapped " +
            $"{expectedUncappedPower:F2}");

        Assert.Equal(0.55f, plan.Power01, 3);
        Assert.Equal(2.0500f, plan.FlightTimeSeconds, 3);
        Assert.True(stopDistance > plan.DistanceMeters,
            $"the fixture's ball must overrun the receiver to exercise the cap ({stopDistance:F2} m rest)");
        Assert.True(MathF.Abs(projected.Y) <= Pitch.HalfWidth + PassPlanner.BoundaryStopMarginMeters + 0.01f,
            $"the capped ball's projected stop is z = {projected.Y:F2}, " +
            $"{MathF.Abs(projected.Y) - Pitch.HalfWidth:F2} m outside the touchline");
    }

    /// <summary>
    /// The guard against over-capping: a central 20 m pass has no boundary within 4 m of the target, so the
    /// projected-stop rule must not fire even though the 0.95-power ball's stop is saturated (treated as
    /// stopping 180 m away — well outside any pitch). The plan is the plain 9.0 m/s anchor: power 0.95,
    /// 18.92 m/s launch, 1.433 s closest-approach flight, i.e. exactly the pre-safeguard plan.
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_CentralPass_IsNotCapped()
    {
        PassPlan plan = Planner.PlanGroundPass(Vector2.Zero, 9, new Vector2(20f, 0f), Vector2.Zero, 8f);
        Assert.True(plan.Feasible);

        Assert.Equal(0.95f, plan.Power01, 3);
        Assert.Equal(ExpectedStationaryFlight(Planner, 0.95f, 20f), plan.FlightTimeSeconds, 3);

        Vector2 rest = MeasureRestPosition(Vector2.Zero, plan.AimDirectionXZ, plan.Power01, out _);
        Assert.True(Vector2.Distance(Vector2.Zero, rest) >= 51.9f,
            "the fixture expects a saturated stop, which is what the cap must ignore off the line");
    }

    /// <summary>
    /// The other half of the guard: a pass aimed from near the touchline <b>toward the middle</b> is not
    /// touched even though it starts near the line. Origin (0, −28), receiver (15, −22): the 0.80-power
    /// anchor (9.0 m/s crisp range 17.13 m ≥ 16.16 m) is kept, and the projected stop lands at
    /// (44.60, −10.16) — inside. A policy that caps on "origin near the line" instead of "aim outward"
    /// would fail this.
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_TouchlineInwardPass_IsNotCapped()
    {
        Vector2 origin = new(0f, -28f);
        PassPlan plan = Planner.PlanGroundPass(origin, 9, new Vector2(15f, -22f), Vector2.Zero, 8f);
        Assert.True(plan.Feasible);

        Assert.Equal(0.80f, plan.Power01, 3);

        Vector2 projected = ProjectedStop(plan, origin, out _);
        Assert.True(MathF.Abs(projected.Y) < Pitch.HalfWidth,
            $"an inward pass was capped: projected stop z = {projected.Y:F2}");
        Assert.True(MathF.Abs(projected.X) < Pitch.HalfLength);
    }

    /// <summary>
    /// "Never refuse a plan that the cap can rescue; cap the power instead" — and when <b>no</b> allowed
    /// power keeps the ball in play, take the gentlest power and keep the plan. A receiver standing 1 m
    /// inside the touchline at (18, −33) is unreachable for every power: the gentlest 0.20-power ball still
    /// rolls 26.39 m and its projected stop is z = −35.06, 1.06 m outside the line. The uncapped anchor here
    /// is 0.90 power (9.0 m/s crisp range 19.81 m ≥ 18.68 m); the capped plan must stay feasible at 0.20
    /// rather than being refused.
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_TouchlineOverrun_WithNoRescuingPower_TakesTheGentlestBall()
    {
        Vector2 origin = new(0f, -28f);
        Vector2 receiver = new(18f, -33f);
        Assert.Equal(0.90f, AnchoredPower(Planner, Vector2.Distance(origin, receiver)), 3);

        PassPlan plan = Planner.PlanGroundPass(origin, 9, receiver, Vector2.Zero, 8f);
        Assert.True(plan.Feasible,
            "the overrun cap refused a pass instead of taking the gentlest power that still reaches");
        Assert.Equal(0.20f, plan.Power01, 3);
        Assert.True(plan.FlightTimeSeconds > 0f);

        // Prove the fixture really has no rescuing power: the capped plan's own ball still leaves the pitch.
        Vector2 projected = ProjectedStop(plan, origin, out float stopDistance);
        Assert.True(stopDistance > plan.DistanceMeters);
        Assert.True(MathF.Abs(projected.Y) > Pitch.HalfWidth + PassPlanner.BoundaryStopMarginMeters + 0.05f,
            $"fixture no longer exercises the no-rescue path: projected stop z = {projected.Y:F2}");
    }

    // ---------------------------------------------------------------------------------------------
    // AUD-P7 / fix 4.3 — touchline-overrun cap in PlanGroundPassToRegion
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The same cap on the region planner: a timed ball from (−5, −28) to a region at (13, −30.5), 3.5 m
    /// inside the touchline, wanted at 1.0 s. Uncapped the bisection chooses power 1.00 (a saturated stop =
    /// stopping far away, the audit's 100 %-power overhit; 1.200 s flight), whose projected stop is well
    /// outside. The cap finds 0.75 (46.25 m rest ⇒ projected z = −34.36, inside) and re-solves the flight
    /// at that power against the same "closest to the wanted meeting" objective: 1.550 s. The plan is kept.
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_TimedRegion_TouchlineOverrun_IsCapped()
    {
        Vector2 origin = new(-5f, -28f);
        Vector2 region = new(13f, -30.5f);
        PassPlan plan = Planner.PlanGroundPassToRegion(origin, region, arriveInSeconds: 1.0f);

        Assert.True(plan.Feasible);
        Assert.Equal(0.75f, plan.Power01, 3);
        Assert.Equal(1.5500f, plan.FlightTimeSeconds, 3);

        Vector2 projected = ProjectedStop(plan, origin, out float stopDistance);
        Assert.True(stopDistance > plan.DistanceMeters,
            $"the fixture's ball must overrun the region to exercise the cap ({stopDistance:F2} m rest)");
        Assert.True(MathF.Abs(projected.Y) <= Pitch.HalfWidth + PassPlanner.BoundaryStopMarginMeters + 0.01f,
            $"the capped timed ball's projected stop is z = {projected.Y:F2}, outside the touchline");
        Assert.True(plan.FlightTimeSeconds <= 1.0f + PassPlanner.MaxTimedArrivalLateSeconds);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures and helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Flies a cloned ball at the given power/aim through the real solver until it comes to rest (or the
    /// AUD-P7 calibration's 52 m goal-structure clearance), returning the rest position. This is the same
    /// roll <see cref="PassPlanner"/>'s one-time calibration performs, so the tests read the physics the
    /// planner plans with instead of hard-coding a stop distance.
    /// </summary>
    private static Vector2 MeasureRestPosition(Vector2 origin, Vector2 direction, float power01, out float seconds)
    {
        Vector2 aim = SimMath.SafeNormalize(direction, new Vector2(1f, 0f));
        float launch = PassPlanner.GroundPassLaunchSpeed(power01);
        var ball = new BallState
        {
            Position = new Vector3(origin.X, 0.11f, origin.Y),
            Velocity = new Vector3(aim.X * launch, 0.05f, aim.Y * launch)
        };
        var solver = new BallPhysicsSolver();
        int steps = 0;
        while (steps < 1200 &&
               ball.Velocity.Length() > 0.031f &&
               MathF.Abs(ball.Position.X) < 52f)
        {
            solver.Step(ball, Dt);
            steps++;
        }

        seconds = steps * Dt;
        return new Vector2(ball.Position.X, ball.Position.Z);
    }

    /// <summary>The projected final stop point of a plan if the receiver never touched the ball (AUD-P7).</summary>
    private static Vector2 ProjectedStop(PassPlan plan, Vector2 origin, out float stopDistance)
    {
        Vector2 rest = MeasureRestPosition(origin, plan.AimDirectionXZ, plan.Power01, out _);
        stopDistance = Vector2.Distance(origin, rest);
        return plan.TargetXZ + plan.AimDirectionXZ * (stopDistance - plan.DistanceMeters);
    }

    /// <summary>
    /// Arrival speed derived from the planner's own calibrated distance/time samples by finite difference —
    /// the public-API form of the derivation the fix-4.1 pace floor uses internally.
    /// </summary>
    private static float ArrivalSpeed(float power01, float distanceMeters)
    {
        Assert.True(Planner.TryGetFlightTime(power01, distanceMeters, out float flight));
        float before = Planner.BallDistanceAtTime(power01, flight - Dt);
        float after = Planner.BallDistanceAtTime(power01, flight + Dt);
        return (after - before) / (2f * Dt);
    }

    /// <summary>
    /// The documented <see cref="PassPlanner"/> power anchor: the crisper of the lowest trapable-reach power
    /// and the lowest crisp-pace power. Mirrors the planner's private anchor so the cap tests can name the
    /// power the *uncapped* code would have returned.
    /// </summary>
    private static float AnchoredPower(PassPlanner planner, float distance)
    {
        float trapable = 1f;
        float crisp = 1f;
        bool foundTrapable = false;
        bool foundCrisp = false;
        for (int index = 0; index < 17; index++)
        {
            float power = 0.20f + 0.05f * index;
            if (!foundTrapable && planner.RangeFor(power) >= distance)
            {
                trapable = power;
                foundTrapable = true;
            }

            if (!foundCrisp && planner.CrispRangeFor(power) >= distance)
            {
                crisp = power;
                foundCrisp = true;
            }
        }

        return MathF.Max(trapable, crisp);
    }

    /// <summary>
    /// The flight time the planner's closest-approach solve picks for a stationary receiver: the first
    /// sample at which the calibrated ball distance is closest to the requested distance.
    /// </summary>
    private static float ExpectedStationaryFlight(PassPlanner planner, float power01, float distance)
    {
        float bestMiss = float.MaxValue;
        float bestTime = 0f;
        float previous = 0f;
        for (int k = 1; k < 480; k++)
        {
            float t = k * Dt;
            float ballDistance = planner.BallDistanceAtTime(power01, t);
            if (ballDistance <= previous + 1e-4f && k > 2) break;
            previous = ballDistance;

            float miss = MathF.Abs(distance - ballDistance);
            if (miss < bestMiss)
            {
                bestMiss = miss;
                bestTime = t;
            }
        }

        return bestTime;
    }
}
