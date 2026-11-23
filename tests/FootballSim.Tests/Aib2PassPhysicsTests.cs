using System;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// Regression tests for AUD-P5 / AUD-P7 ("Fix 4") of the 2026-11-23 passing-and-space audit.
///
/// <b>AUD-P5 pace story.</b> The audit's fix 4.2 proposed lowering the crisp arrival pace from 9.0 to
/// ~6.5–7.0 m/s to shorten the overhit roll. That was tried and <b>reversed on measurement</b>: at 7.0 m/s a
/// 17 m pass anchored at 0.70 power instead of 0.80 and its flight lengthened from 1.317 s to 1.483 s (20 m:
/// 1.433 s → 1.683 s); the longer flight made clean passes interceptable under the AIB-02a physics lane
/// model (two AIB-02b congestion fixtures flipped to lane-blocked merely from a 10 m flight going
/// 0.867 s → 1.033 s). The audit's own evidence records slow rollers costing 26.8 % interceptions while
/// <c>ai55</c> completes 80–83 % of passes at the 9.0 m/s pace. The pace is <b>restored to 9.0 m/s</b>.
///
/// <b>AUD-P7 story.</b> Fix 4.3 offered two implementations: cap the pass power so the ball stops inside
/// the pitch, or price the boundary-exit risk in <see cref="PassSuccessModel"/>. The cap was built and
/// removed: a ground ball always rolls 26–54 m (the gentlest 0.20-power ball rests at 26.4 m), so "keep the
/// projected rest point inside the pitch" could essentially never be satisfied for a target nearer than
/// that, and the only way to satisfy it was to turn near-line passes into 0.20-power slow rollers — the
/// exact AUD-P5 defect class this batch removes. Concretely, the audit shape (15,−15)→(24,−30) went from
/// 0.85 power / 1.30 s to 0.20 power / 3.33 s, and a 31 m goal-line ball from 1.00 / 2.78 s to 0.35 / 7.98 s
/// (both still rolled out if untrapped). The planner now never touches the ball: both planners produce the
/// pre-AUD-P7 plan for every input, and <see cref="PassSuccessModel.Estimate"/> charges a bounded
/// boundary-exit risk through <see cref="PassPlanner.BoundaryExitRisk01"/> on the untimed <i>and</i>
/// timed/promised path.
///
/// Tests 1–3 pin the restored 9.0 baseline as regression guards (they pass on the pre-fix code by design; a
/// later pace change must justify them with new measurement). The fix-4.1 tests and the AUD-P7 risk tests
/// are new behaviour (the pace parameter / risk API do not exist pre-fix; the cap-era behaviour is the
/// recorded failure above). Every fixture states its arithmetic in the comment.
/// </summary>
public sealed class Aib2PassPhysicsTests
{
    private static readonly PitchDimensions Pitch = new();
    private static readonly PassPlanner Planner = new(Pitch);
    private static readonly PassSuccessModel Model = new(Planner);
    private const float Dt = SimConstants.FixedDeltaTime;

    // ---------------------------------------------------------------------------------------------
    // AUD-P5 pace baseline (restored 9.0 m/s) — regression guards, not defect repros
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Regression guard for the restored AUD-P5 pace: a 17 m crisp pass is anchored to power 0.80 (the
    /// lowest power whose 9.0 m/s crisp range reaches 17 m — 17.13 m), a 17.20 m/s launch, and its
    /// closest-approach flight is 1.317 s. The ball's calibrated resting point is 48.04 m from the passer,
    /// i.e. it rolls 31.04 m past a 17 m target if nobody touches it — the overhit AUD-P7 prices when it
    /// matters (a near-line exit); a central target is not penalised.
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
            $"a 17 m crisp pass rolls {overrun:F2} m past the target; the 9.0 m/s baseline is 31.04 m");
    }

    /// <summary>
    /// Regression guard for the restored AUD-P5 pace, the 20 m case: anchor power 0.95 (the lowest power
    /// whose 9.0 m/s crisp range reaches 20 m — 19.81 m), 18.92 m/s launch, 1.433 s flight. The ball is
    /// still rolling when it reaches the 52 m stop-calibration clearance (measured rest 52.01 m there), so
    /// the AUD-P7 calibration marks this power saturated and reports it as stopping far away (180 m) — the
    /// explicit saturation check that stops the old 45 m table cap hiding a real overrun.
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
    /// 28.35 m, which is how an "innocent" pace change silently moves every power anchor).
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
    // AUD-P7 / fix 4.3 — the planner never touches the ball for boundary safety
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The reviewer's blocker: the cap experiment drove the audit shape (15,−15)→(24,−30) from 0.85 power /
    /// 1.30 s down to <b>0.20 power / 3.33 s</b> (the gentlest solving power) because no ground ball's rest
    /// point can stay inside the pitch from there (0.20 power alone rests at 26.4 m). The cap is gone: the
    /// plan must be the pre-AUD-P7 plan, the same power and flight as the identical geometry translated away
    /// from the line (same displacement at Z = 0), and at least the firm-ball floor
    /// <see cref="PassPlanner.MinCrispPower01"/> for its distance. The anchor here is 0.85 because the
    /// calibrated 9.0 m/s crisp range at 0.85 power is 18.53 m ≥ 17.49 m. The 31 m goal-line shape is pinned
    /// too — cap-era it was 0.35 power / 7.98 s.
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_NearLinePass_KeepsItsFirmPace()
    {
        Vector2 origin = new(15f, -15f);
        Vector2 target = new(24f, -30f);
        PassPlan nearLine = Planner.PlanGroundPass(origin, 9, target, Vector2.Zero, 8f);
        Assert.True(nearLine.Feasible);

        // Same relative displacement (9, −15) but away from the line: the plan must be identical.
        PassPlan central = Planner.PlanGroundPass(new Vector2(15f, 15f), 9, new Vector2(24f, 0f), Vector2.Zero, 8f);
        Assert.True(central.Feasible);

        Assert.Equal(0.85f, nearLine.Power01, 3);
        Assert.Equal(1.3000f, nearLine.FlightTimeSeconds, 3);
        Assert.Equal(central.Power01, nearLine.Power01);
        Assert.Equal(central.FlightTimeSeconds, nearLine.FlightTimeSeconds);
        Assert.True(nearLine.Power01 >= Planner.MinCrispPower01(nearLine.DistanceMeters) - 1e-4f,
            $"plan {nearLine.Power01:F2} is below the firm-ball floor " +
            $"{Planner.MinCrispPower01(nearLine.DistanceMeters):F2}");

        // Explicitly not the cap-era roller (0.20 power / 3.33 s).
        Assert.True(nearLine.Power01 > 0.50f,
            $"the near-line pass is a {nearLine.Power01:F2}-power roller (cap-era was 0.20)");
        Assert.True(nearLine.FlightTimeSeconds < 2f,
            $"the near-line pass takes {nearLine.FlightTimeSeconds:F2} s of flight (cap-era was 3.33 s)");

        // The 31 m goal-line shape: cap-era 0.35 power / 7.98 s, now the plain full-power plan.
        PassPlan goalLine = Planner.PlanGroundPass(new Vector2(20f, 0f), 9, new Vector2(51f, 0f), Vector2.Zero, 8f);
        Assert.True(goalLine.Feasible);
        Assert.Equal(31f, goalLine.DistanceMeters, 2);
        Assert.Equal(1.00f, goalLine.Power01, 3);
        Assert.Equal(19.50f, PassPlanner.GroundPassLaunchSpeed(goalLine.Power01), 2);
        Assert.Equal(2.7833f, goalLine.FlightTimeSeconds, 3);
    }

    /// <summary>
    /// AUD-P7: the audit shape's ball does leave the pitch just past its aim point, so the risk is charged at
    /// the documented ramp. Arithmetic: the 0.85-power ball rests at
    /// <see cref="PassPlanner.PredictStopDistance"/> = 49.79 m; the aim is (9, −15)/17.493 = (0.5145, −0.8575);
    /// the negative-Z touchline (z = −34) is crossed at t = (−34 + 15)/−0.8575 = 22.16 m from the passer,
    /// i.e. 22.16 − 17.49 = <b>4.66 m past the aim point</b>; the ramp gives
    /// 0.35 × (1 − 4.66/6) = <b>0.0779</b>. The identical central geometry is 0 (its roll stays in play).
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_BoundaryExitRisk_IsPricedJustPastTheTarget()
    {
        Vector2 origin = new(15f, -15f);
        PassPlan plan = Planner.PlanGroundPass(origin, 9, new Vector2(24f, -30f), Vector2.Zero, 8f);
        Assert.True(plan.Feasible);
        Assert.Equal(0.85f, plan.Power01, 3);

        float risk = Planner.BoundaryExitRisk01(origin, plan.TargetXZ, plan.Power01);
        Assert.Equal(0.0779f, risk, 3);

        // Re-derive the ramp from the published constants and the calibrated stop distance.
        float stopDistance = Planner.PredictStopDistance(plan.Power01);
        Assert.Equal(49.79f, stopDistance, 2);
        Vector2 aim = Vector2.Normalize(plan.TargetXZ - origin);
        float exitDistance = (-Pitch.HalfWidth - origin.Y) / aim.Y;
        float pastAim = exitDistance - plan.DistanceMeters;
        Assert.True(pastAim > 0f && pastAim < PassPlanner.BoundaryExitPenaltyRadiusMeters,
            $"the fixture must exit inside the pricing radius (pastAim was {pastAim:F2} m)");
        float expected = PassPlanner.BoundaryExitMaxRisk01 *
                         (1f - pastAim / PassPlanner.BoundaryExitPenaltyRadiusMeters);
        Assert.Equal(expected, risk, 4);
        Assert.True(risk > 0f);

        // The same relative geometry far from any line is untouched.
        Assert.Equal(0f, Planner.BoundaryExitRisk01(
            new Vector2(15f, 15f), new Vector2(24f, 0f), plan.Power01));
    }

    /// <summary>
    /// The scoping half of the risk rule: it is zero when the ball stays in play (the central plan), zero
    /// when the exit is further than <see cref="PassPlanner.BoundaryExitPenaltyRadiusMeters"/> past the aim
    /// point (ordinary long forward ball: here the 0.30-power ball exits z = 34 ten metres past a z = 24
    /// aim, i.e. 0.30 rests at 29.97 m), and zero when the exit is inside the <b>goal mouth</b> (a ball
    /// between the posts is a goal, not out of play). A goal-line exit outside the posts is still charged,
    /// which proves the exemption is the goal mouth and not the whole goal line. Non-finite and
    /// zero-length inputs are deterministic zero.
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_BoundaryExitRisk_IsZeroFarFromTheLineAndInTheGoalMouth()
    {
        // Central: inside the pitch for bounds of reasons.
        Assert.Equal(0f, Planner.BoundaryExitRisk01(Vector2.Zero, new Vector2(20f, 0f), 0.95f));

        // Exit 10 m past the aim point: outside the 6 m pricing radius.
        Assert.Equal(0f, Planner.BoundaryExitRisk01(Vector2.Zero, new Vector2(0f, 24f), 0.30f));

        // Goal mouth: a ball rolled into the opponent goal (z = 0, between the posts) is a goal, not out.
        Assert.Equal(0f, Planner.BoundaryExitRisk01(Vector2.Zero, new Vector2(50f, 0f), 1.00f));
        PassPlan goalLine = Planner.PlanGroundPass(new Vector2(20f, 0f), 9, new Vector2(51f, 0f), Vector2.Zero, 8f);
        Assert.Equal(0f, Planner.BoundaryExitRisk01(new Vector2(20f, 0f), goalLine.TargetXZ, goalLine.Power01));

        // ... but the goal line outside the posts is out of play and charged (z = 10 at the crossing).
        Assert.True(Planner.BoundaryExitRisk01(
            new Vector2(20f, 0f), new Vector2(51f, 10f), 1.00f) > 0.2f,
            "a goal-line exit outside the goal mouth must still be priced");

        // Deterministic handling of nonsense inputs.
        Assert.Equal(0f, Planner.BoundaryExitRisk01(new Vector2(float.NaN, 0f), new Vector2(10f, 0f), 0.5f));
        Assert.Equal(0f, Planner.BoundaryExitRisk01(new Vector2(5f, 5f), new Vector2(5f, 5f), 0.5f));
        Assert.Equal(0f, Planner.BoundaryExitRisk01(new Vector2(15f, -15f), new Vector2(24f, -30f), float.NaN));
    }

    /// <summary>
    /// AUD-P7 on the timed/promised path: the audit's own (15,−15)→(24,−30) wanted-1.4 s plan was
    /// <b>byte-identical before and after the cap</b> — the cap was inert here, so the reviewer's finding
    /// held: the timed path had no pricing at all. With the cap removed the plan is the pre-fix timed plan
    /// (0.80 power / 1.3833 s; the uncapped best arrival closest to 1.4 s) and the completion estimate now
    /// carries the positive exit risk, so P7 is live on the timed/promised path.
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_TimedBall_ExitRisk_IsPriced()
    {
        Vector2 origin = new(15f, -15f);
        PassPlan timed = Planner.PlanGroundPassToRegion(origin, new Vector2(24f, -30f), arriveInSeconds: 1.4f);
        Assert.True(timed.Feasible);
        Assert.Equal(0.80f, timed.Power01, 3);
        Assert.Equal(1.3833f, timed.FlightTimeSeconds, 3);

        float risk = Planner.BoundaryExitRisk01(origin, timed.TargetXZ, timed.Power01);
        Assert.True(risk > 0f, "the timed near-line plan must carry the boundary-exit risk");

        // The same timed plan translated away from the line: no risk, and the completion is lower near
        // the line by exactly the risk fraction (all other model terms are translation-invariant).
        PassPlan centralTimed = Planner.PlanGroundPassToRegion(
            new Vector2(15f, 15f), new Vector2(24f, 0f), arriveInSeconds: 1.4f);
        Assert.True(centralTimed.Feasible);
        Assert.Equal(timed.Power01, centralTimed.Power01);

        PassSuccessTerms near = Estimate(origin, timed, risk);
        PassSuccessTerms away = Estimate(new Vector2(15f, 15f), centralTimed, 0f);
        Assert.Equal(0f, away.BoundaryExitRisk01);
        Assert.True(near.BoundaryExitRisk01 > 0f);
        Assert.Equal(away.CompletionProbability01 * (1f - risk), near.CompletionProbability01, 5);
    }

    /// <summary>
    /// Saturation cannot hide the overrun: powers 0.95 and 1.00 are still rolling at the 52 m stop
    /// calibration clearance, so <see cref="PassPlanner.PredictStopDistance"/> reports them as stopping far
    /// away (180 m) instead of the old table's 45 m cap. A full-power ball at the touchline therefore still
    /// gets the ramp: aim (0, 30) from the centre, power 1.00, exits z = 34 at 34 m, i.e. 4 m past the aim
    /// point ⇒ 0.35 × (1 − 4/6) = 0.1167.
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_SaturatedPower_ExitRisk_IsPriced()
    {
        Assert.Equal(51.59f, Planner.PredictStopDistance(0.90f), 2); // measured, not saturated
        Assert.Equal(Planner.PredictStopDistance(0.95f), Planner.PredictStopDistance(1.00f));
        Assert.True(Planner.PredictStopDistance(1.00f) > 100f,
            $"a saturated power must be reported as stopping far away, not at the table cap " +
            $"({Planner.PredictStopDistance(1.00f):F2} m)");

        float riskFull = Planner.BoundaryExitRisk01(Vector2.Zero, new Vector2(0f, 30f), 1.00f);
        float riskNineFive = Planner.BoundaryExitRisk01(Vector2.Zero, new Vector2(0f, 30f), 0.95f);
        Assert.Equal(0.1167f, riskFull, 3);
        Assert.Equal(riskFull, riskNineFive, 5);
        Assert.True(riskFull > 0f, "saturation must not excise the exit risk");
    }

    /// <summary>
    /// The completion pricing through <see cref="PassSuccessModel.Estimate"/>: with an open, unblocked,
    /// unpromised fixture the central plan's completion is 0.850000 (receiver term 1, defender term 1, lane
    /// weight 0.30 at clearance 0.5, control 1: 0.7 + 0.3 × 0.5 = 0.85). The near-line plan's completion is
    /// 0.850000 × (1 − 0.077889) = <b>0.783794</b> — all other terms are translation-invariant, so the
    /// ratio is exactly the risk; the risk is published in the terms and the lead charge stays 0 (this is a
    /// distinct physical outcome, not a second lead charge). The central plan's completion is unchanged.
    /// </summary>
    [Fact]
    public void AIB2_PassPhysics_Estimate_PricesBoundaryExitRisk()
    {
        Vector2 origin = new(15f, -15f);
        PassPlan nearPlan = Planner.PlanGroundPass(origin, 9, new Vector2(24f, -30f), Vector2.Zero, 8f);
        Assert.True(nearPlan.Feasible);

        Vector2 centralOrigin = new(15f, 15f);
        PassPlan centralPlan = Planner.PlanGroundPass(centralOrigin, 9, new Vector2(24f, 0f), Vector2.Zero, 8f);
        Assert.True(centralPlan.Feasible);

        float risk = Planner.BoundaryExitRisk01(origin, nearPlan.TargetXZ, nearPlan.Power01);
        Assert.Equal(0.0779f, risk, 3);

        PassSuccessTerms central = Estimate(centralOrigin, centralPlan, 0f);
        PassSuccessTerms nearLine = Estimate(origin, nearPlan, risk);

        Assert.Equal(0.850000f, central.CompletionProbability01, 4);
        Assert.Equal(0f, central.BoundaryExitRisk01);
        Assert.Equal(risk, nearLine.BoundaryExitRisk01, 5);
        Assert.Equal(0f, nearLine.LeadCharge01); // a distinct outcome, not a lead/territory charge
        Assert.Equal(central.CompletionProbability01 * (1f - risk), nearLine.CompletionProbability01, 5);

        // Both are inside the clamp band, so the ratio above is not a clamp artefact.
        Assert.True(nearLine.CompletionProbability01 > PassSuccessModel.MinCompletion01 &&
                    nearLine.CompletionProbability01 < PassSuccessModel.MaxCompletion01);
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
    /// Estimates a plan with one open, unblocked, unpromised receiver at the plan's own target: the
    /// canonical fixture for isolating the AUD-P7 completion multiplier (all other terms equal).
    /// </summary>
    private static PassSuccessTerms Estimate(Vector2 origin, PassPlan plan, float expectRisk)
    {
        FootballerState self = Player(origin);
        PerceivedEntity receiver = Mate(plan.TargetXZ);
        FootballerObservation observation = Observation(self, receiver);
        PassSuccessTerms terms = Model.Estimate(observation, receiver, plan, laneClearance01: 0.5f);
        Assert.Equal(expectRisk, terms.BoundaryExitRisk01, 5);
        Assert.False(terms.LaneBlocked, "the Estimate fixture must be an open lane");
        return terms;
    }

    private static FootballerState Player(Vector2 position) => new()
    {
        Id = 8,
        Team = TeamId.Home,
        ShirtNumber = 8,
        Role = TacticalRole.CentralMidfielder,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(TacticalRole.CentralMidfielder),
        Position = new Vector3(position.X, 0f, position.Y),
        FacingXZ = new Vector2(1f, 0f)
    };

    private static PerceivedEntity Mate(Vector2 position) => new(
        9, TeamId.Home, TacticalRole.CentralMidfielder,
        new Vector3(position.X, 0f, position.Y), Vector3.Zero, 0.95f)
    {
        Capability = CapabilityEstimate.Generic
    };

    private static FootballerObservation Observation(FootballerState self, params PerceivedEntity[] visible) => new()
    {
        SelfId = self.Id,
        SelfTeam = self.Team,
        SelfRole = self.Role,
        SelfPosition = self.Position,
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = self.Position,
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = self.Team,
        BallLastTouchedByPlayerId = self.Id,
        BallLastTouchedByTeam = self.Team,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = self.Id,
        TacticalAnchorPosition = self.Position,
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 10f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = 100,
        VisiblePlayers = visible,
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = new PlayerTendencies(),
        SelfAttributes = new PlayerAttributes()
    };
}
