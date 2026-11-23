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
    /// Pace a pass should still have when it reaches the receiver. Playtests reported that nearly every
    /// AI pass was struck with almost no power: the planner used to pick the *lowest* power whose trapable
    /// range merely reached the target, so a 15 m pass crawled there in ~2 s at the 5 m/s trapable floor.
    /// Over eight 11v11 matches that cost 26.8 % of passes to interceptions, and passes at 50–59 % power
    /// completed only 66 %. A normal firm pass arrives at roughly this pace, so the power is anchored to
    /// it and the flight is ~40 % shorter. It is only a *preference*: beyond the crisp range of a
    /// full-power pass (~22.6 m here) the planner falls back to the same lowest-power-that-reaches rule.
    ///
    /// <b>AUD-P5 fix 4.2 was tried and reversed on measurement.</b> The audit recommended lowering this
    /// pace to ~6.5–7.0 m/s to reduce the overhit roll. The overhit is now priced where it belongs — the
    /// AUD-P7 boundary-exit risk in <see cref="AI.PassSuccessModel"/> — and the pace change measured badly:
    /// at 7.0 m/s a 17 m pass anchored at 0.70 power instead of 0.80 and its flight lengthened from
    /// 1.317 s to 1.483 s (20 m: 1.433 s to 1.683 s). A longer flight is exactly what makes a ball
    /// interceptable under this repo's AIB-02a physics lane model — the 2026-11-22 evidence recorded
    /// slow rollers costing 26.8 % interceptions, and <c>ai55</c> completes 80–83 % of passes with the
    /// 9.0 m/s pace. Concretely, the 7.0 experiment turned two clean AIB-02b congestion fixtures into
    /// lane-blocked passes merely by lengthening their 10 m flight from 0.867 s to 1.033 s. So the overhit
    /// half of AUD-P5 stays a probabilistic pricing problem and the pace stays at 9.0 m/s; lowering it
    /// lengthens the flight and increases lane blocking (measured).
    /// </summary>
    private const float CrispArrivalSpeed = 9.0f;

    private const float MaxMeetingMiss = 1.0f;        // reception reach is ~1.24 m, leave margin

    /// <summary>
    /// AUD-P7 / fix 4.3: how far past its aim point a ground pass may leave the pitch before the exit is
    /// considered harmless, in metres. Only an overhit that runs out of play <i>just past the receiver</i>
    /// is the audit's defect; a long forward ball whose roll leaves the pitch after the receiver had ample
    /// room to move onto it is ordinary play, so the risk ramps to zero at this distance. The same radius
    /// is the ramp length of <see cref="BoundaryExitRisk01"/>'s linear charge.
    /// </summary>
    public const float BoundaryExitPenaltyRadiusMeters = 6f;

    /// <summary>
    /// AUD-P7 / fix 4.3: the largest share of a ground pass's completion charged for a boundary exit.
    /// Fix 4.3 offered two implementations — cap the power so the ball stops inside, or price the exit.
    /// The cap was built first and removed: a ground ball always rolls 26.4–51.6 m before resting (even the
    /// gentlest 0.20-power ball rolls 26.4 m), so "keep the rest point inside the pitch" could essentially
    /// never be satisfied for a target nearer than that, and the only way to satisfy it was to turn the
    /// pass into a 0.20-power slow roller — the exact AUD-P5 defect class this batch removes. The bounded
    /// pricing below is honest, applies to the untimed and the timed/promised plan alike, and never
    /// deletes the pass. 0.35 is deliberately smaller than the loss cost's own share of an ordinary
    /// turnover so an otherwise good near-line ball is charged, not vetoed.
    /// </summary>
    public const float BoundaryExitMaxRisk01 = 0.35f;

    /// <summary>
    /// AUD-P7: how long the one-time calibration keeps rolling a cloned ball to find where it actually
    /// comes to rest. The 8 s flight table is not long enough for the slow tail of the roll (the gentlest
    /// ground pass is still creeping at 8 s and only stops after ~9–12 s), and the boundary-exit risk asks
    /// where the ball would rest, not where it is after eight seconds.
    /// </summary>
    private const int StopSamples = 1200;             // 20 s of roll

    /// <summary>
    /// AUD-P7: the stop-distance calibration stops tracking a ball that is still rolling this far from
    /// the passer. It is deliberately short of the goal net (the ball rolls straight down the middle at
    /// Z = 0, so it would enter the net at <c>HalfLength + radius = 52.61 m</c> and the net damping would
    /// corrupt the measurement); a ball that reaches it is saturated and treated as stopping far away.
    /// </summary>
    private const float MaxStopCalibrationDistance = 52f;

    /// <summary>
    /// AUD-P7: ball speed below which the calibration counts the roll as finished. Mirrors
    /// <see cref="BallPhysicsParameters.StopVelocityEpsilon"/> (the solver zeroes the velocity there);
    /// the small slack makes the measurement order-independent.
    /// </summary>
    private const float StopSpeedMetersPerSecond = 0.031f;

    /// <summary>
    /// AUD-P7: the reported stop distance of a saturated power — one whose calibration never saw the ball
    /// come to rest before <see cref="MaxStopCalibrationDistance"/>. Deliberately far outside any pitch
    /// (the diagonal is ~125 m) so a saturation cap cannot hide a real overrun: a saturated power is
    /// treated as "stopping far away" by <see cref="PredictStopDistance"/> and
    /// <see cref="BoundaryExitRisk01"/>, and the boundary-exit risk is then charged at its ramp value.
    /// </summary>
    private const float FarStopDistanceMeters = 4f * MaxCalibrationDistance;

    private sealed record Calibration(
        float[,] DistanceAtTime,
        float[] RangeAtArrivalSpeed,
        float[] CrispRange,
        float[] StopDistance,
        bool[] StopDistanceSaturated);

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
    /// Longest lead a normal pass gives a moving receiver, in metres. A pass is played to where the
    /// runner will be when the ball arrives, not to where a five-second ball would put him, so the lead
    /// has to be bounded. The default is the same 12 m the AI's feet pass uses: callers that do not
    /// think about the lead (the assisted human pass, the keeper's distribution, a recycle) used to get
    /// a 60 m leash, which is how a "pass to feet" turned into a punt at a runner — measured as a 43 m
    /// ball to a team-mate twelve metres away before A4.2 bounded the AI path.
    /// </summary>
    public const float DefaultMaxLeadMeters = 12f;

    /// <summary>
    /// Longest lead a pass may give a receiver who has <b>not</b> committed to a run, in metres
    /// (AIB-02d, audit 2026-11-22).
    ///
    /// The lead is the bet that the receiver keeps running on his current velocity for the whole flight.
    /// Without a promise (no advertised run, no human call) nothing in the simulation makes that happen: a
    /// team-mate jogging sideways or back toward his shape anchor at 5 m/s during a 1.1 s flight gets the
    /// ball aimed 5.5 m away, and when he stops or turns back the pass rolls into empty grass — the audit's
    /// "pass to absolutely nowhere". This cap is the planner's side of the hedge: aim no further ahead than
    /// this, even though the receiver would run past it. It is applied only when the caller says the
    /// receiver is uncommitted (<c>receiverCommittedToRun: false</c>); a committed runner keeps his own
    /// <c>maxLeadMeters</c> (six metres to feet, twenty-five into an announced run), because for him the
    /// lead is the point of the pass, not a guess.
    /// </summary>
    public const float UncommittedLeadMaxMeters = 2.5f;

    /// <summary>
    /// Plans a pass to a moving receiver. <paramref name="extraLeadMeters"/> pushes the target
    /// further into space along <paramref name="extraLeadDirection"/> (through balls / runs).
    ///
    /// <paramref name="receiverCommittedToRun"/> (AIB-02d) says whether the receiver has actually committed
    /// to the run the lead assumes — an advertised meeting region, a called-for ball, or a caller that knows
    /// he will keep going. The default is <c>true</c> so every existing caller keeps its current plan
    /// exactly; a caller that cannot promise the run passes <c>false</c> and gets the
    /// <see cref="UncommittedLeadMaxMeters"/> hedge instead of a ball aimed metres away.
    ///
    /// <b>AUD-P7 does not touch the plan.</b> The boundary-exit defect (a fast pass aimed 1.0 m inside a
    /// line that rolls out of play just past the receiver) is priced by
    /// <see cref="AI.PassSuccessModel.Estimate"/> through <see cref="BoundaryExitRisk01"/>, not by altering
    /// the launch. This method therefore produces the same plan the pre-AUD-P7 planner produced for every
    /// input; the cap experiment that used to live here was removed because a ground ball always rolls
    /// 26.4–51.6 m, so the only way to satisfy it was a 0.20-power slow roller (the AUD-P5 defect class).
    /// </summary>
    public PassPlan PlanGroundPass(
        Vector2 originXZ,
        int receiverId,
        Vector2 receiverPositionXZ,
        Vector2 receiverVelocityXZ,
        float receiverMaxSpeed,
        float extraLeadMeters = 0f,
        Vector2 extraLeadDirection = default,
        float maxLeadMeters = DefaultMaxLeadMeters,
        float minPower01 = 0f,
        bool freezeReceiverAtLeadBound = false,
        bool receiverCommittedToRun = true)
    {
        _ = receiverId; // kept so callers can tag plans with their receiver

        Vector2 velocity = ClampSpeed(receiverVelocityXZ, receiverMaxSpeed);
        Vector2 extraLead = extraLeadMeters > 0f ? extraLeadDirection * extraLeadMeters : Vector2.Zero;

        // AIB-02d: an uncommitted receiver is hedged exactly like a human one — the solve freezes the
        // receiver at the (smaller) lead bound instead of refusing the plan, because refusing would make
        // every pass to a jogging team-mate infeasible rather than merely less ambitious.
        float leadCap = receiverCommittedToRun
            ? maxLeadMeters
            : MathF.Min(maxLeadMeters, UncommittedLeadMaxMeters);
        bool freezeLead = freezeReceiverAtLeadBound || !receiverCommittedToRun;

        float straightLineDistance = Vector2.Distance(originXZ, receiverPositionXZ);
        // Anchor the power to the *crisper* of the two tables: the lowest power that reaches the
        // distance at a trapable pace and the lowest power that reaches it at a firm, normal pass
        // pace. Using only the trapable table made almost every AI pass a slow roller.
        int anchorIndex = AnchoredPowerIndex(straightLineDistance, minPower01);
        int powerIndex = anchorIndex;

        if (!TrySolveArrivalTime(powerIndex, originXZ, receiverPositionXZ, velocity, extraLead, leadCap, freezeLead, out float flightTime))
        {
            // AIB-02d (audit 2026-11-22): retry *firmer*, above the anchored power. A gentler ball lengthens
            // the flight, and a longer flight is exactly what makes the lead too long and the meeting miss
            // too big — the two reasons the anchor failed — so stepping down can never rescue the solve; it
            // can only turn the pass into a slow roller. The old loop started at
            // PowerIndexForDistance(straightLineDistance) + 1, which is often *below* the crisp and
            // min-power anchor, so a failed crisp pass was retried as a 0.35-power dribble and a human
            // caller's requested power was discarded.
            powerIndex = -1;
            for (int candidate = anchorIndex + 1; candidate < PowerSamples; candidate++)
            {
                if (!TrySolveArrivalTime(candidate, originXZ, receiverPositionXZ, velocity, extraLead, leadCap, freezeLead, out flightTime))
                    continue;

                powerIndex = candidate;
                break;
            }

            if (powerIndex < 0) return Infeasible(straightLineDistance);
        }

        Vector2 target = ClampToPitch(receiverPositionXZ
            + ClampVectorLength(velocity * flightTime, leadCap)
            + extraLead);
        float distance = Vector2.Distance(originXZ, target);

        // Clamping to the pitch can move the target beyond what this power reaches. Re-anchor both
        // tables together so the re-planned pass keeps its pace as well as its reach (and keeps any
        // caller-requested power floor, AIB-02d).
        if (distance > _calibration.RangeAtArrivalSpeed[powerIndex] + 0.5f)
        {
            powerIndex = AnchoredPowerIndex(distance, minPower01);
            if (!TrySolveArrivalTime(powerIndex, originXZ, receiverPositionXZ, velocity, extraLead, leadCap, freezeLead, out flightTime))
                return Infeasible(distance);

            target = ClampToPitch(receiverPositionXZ
                + ClampVectorLength(velocity * flightTime, leadCap)
                + extraLead);
            distance = Vector2.Distance(originXZ, target);
        }

        if (distance < 0.5f || distance > MaxCalibrationDistance)
            return Infeasible(distance);

        // AUD-P7: the planner deliberately does not touch the ball for boundary safety. Capping the power
        // so the projected rest point stays inside the pitch was built and removed: a ground ball always
        // rolls 26.4–51.6 m, so the cap could only be satisfied by turning near-line passes into 0.20-power
        // rollers (the AUD-P5 defect class). The boundary-exit risk is priced in
        // <see cref="AI.PassSuccessModel.Estimate"/> instead, so this method produces the same plan the
        // pre-AUD-P7 planner produced for every input.
        return new PassPlan(
            Feasible: true,
            AimDirectionXZ: SimMath.SafeNormalize(target - originXZ, new Vector2(1f, 0f)),
            TargetXZ: target,
            Power01: MinPower + powerIndex * PowerStep,
            FlightTimeSeconds: flightTime,
            DistanceMeters: distance);
    }

    /// <summary>Ball speed leaving the foot for a ground pass at the given power (mirrors the kick model).</summary>
    public static float GroundPassLaunchSpeed(float power01) => KickLaunchModel.GroundPassSpeed(power01);

    /// <summary>
    /// The latest a timed pass may arrive after the meeting it is timed to, in seconds (A5.3). A ball that
    /// arrives later than this is not a pass to the run — by then the runner has been and gone and the
    /// defender has had all the time in the world. The mirror case (an early ball) is not refused here: the
    /// success model prices it, because a ball arriving early into the region is a normal through ball.
    /// </summary>
    public const float MaxTimedArrivalLateSeconds = 1.6f;

    /// <summary>
    /// Margin within which two powers count as arriving together, in seconds. Below it the gentler ball
    /// wins, so a timed pass is never struck harder than the meeting needs.
    /// </summary>
    public const float TimedArrivalTieSeconds = 0.02f;

    /// <summary>
    /// Plans a ground pass to a <b>fixed region</b> that arrives when the runner plans to be there (A5.3).
    ///
    /// This is the planner's half of "match the pass to the run": the target is not the receiver's future
    /// position from his current velocity (the old <c>ThroughLeadMeters</c> placeholder), it is the region
    /// he has announced, and the power is chosen so the ball gets there at the announced meeting rather than
    /// as fast as it can. The search is over the calibrated distance/time table — a bisection of the power
    /// whose flight time crosses the meeting, since flight time falls as power rises — so the answer is the
    /// same physics every other pass uses.
    ///
    /// Returns an infeasible plan when the region is out of range or no power can get the ball there within
    /// <see cref="MaxTimedArrivalLateSeconds"/> of the meeting. <paramref name="arriveInSeconds"/> is
    /// clamped to the table's own window rather than trusted, so a nonsense tick cannot index past it.
    ///
    /// <b>AUD-P5 / fix 4.1 — minimum arrival pace.</b> <paramref name="minArrivalSpeedMetersPerSecond"/> is
    /// the caller's firm-ball mode: when it is positive the effective minimum power is raised to the
    /// gentlest grid power whose arrival speed at this distance is still at least that pace, so a through
    /// ball into a 15–25 m run is no longer a 0.20–0.40-power roller that defenders walk across. The
    /// arrival speed comes from the same calibrated distance/time samples every other query reads (finite
    /// difference of consecutive samples; see <see cref="TryGetArrivalSpeedAt"/>), never from a second
    /// table. The trade is stated honestly: a firmer ball arrives <b>earlier</b> than the advertised
    /// meeting, and <see cref="AI.PassSuccessModel"/>'s promise timing term prices that mistiming — the
    /// caller decides whether the interception-risk trade is worth it. The default of <c>0</c> means
    /// "exactly today's behaviour", so every existing call site is unchanged.
    ///
    /// <b>AUD-P7 does not touch the timed plan either.</b> The boundary-exit risk of a timed/promised ball
    /// is priced by <see cref="AI.PassSuccessModel.Estimate"/> through
    /// <see cref="BoundaryExitRisk01"/> — the same place the untimed plan's risk is priced — so this method
    /// produces the same plan the pre-AUD-P7 timed planner produced for every input, including the audit's
    /// own (15,−15)→(24,−30) wanted-1.4 s geometry.
    /// </summary>
    public PassPlan PlanGroundPassToRegion(
        Vector2 originXZ,
        Vector2 targetXZ,
        float arriveInSeconds,
        float minPower01 = 0f,
        float minArrivalSpeedMetersPerSecond = 0f)
    {
        if (!SimMath.IsFinite(originXZ) || !SimMath.IsFinite(targetXZ)) return Infeasible(0f);

        Vector2 target = ClampToPitch(targetXZ);
        float distance = Vector2.Distance(originXZ, target);
        if (distance < 0.5f || distance > MaxCalibrationDistance) return Infeasible(distance);

        float wanted = float.IsFinite(arriveInSeconds) ? Math.Clamp(arriveInSeconds, 0.05f, 8f) : 1f;
        int minIndex = minPower01 > 0f
            ? Math.Clamp((int)MathF.Round((minPower01 - MinPower) / PowerStep), 0, PowerSamples - 1)
            : 0;

        // AUD-P5 / fix 4.1: a requested arrival pace is a floor on the power grid. Arrival speed rises
        // monotonically with power, so a bounded binary search over the 17 powers finds the first one that
        // still has the pace at the target distance. It only runs when the caller asked for a pace; the
        // default 0 skips it entirely and the plan is byte-for-byte what it was before the parameter.
        if (minArrivalSpeedMetersPerSecond > 0f && float.IsFinite(minArrivalSpeedMetersPerSecond))
        {
            minIndex = Math.Max(minIndex, FirstPowerIndexForArrivalSpeed(distance, minArrivalSpeedMetersPerSecond));
        }

        if (!TryFirstPowerThatReaches(distance, minIndex, out int gentlest)) return Infeasible(distance);

        // Flight time for a fixed distance is strictly decreasing in power, so the arrival closest to the
        // meeting is found by bisecting the crossing of `wanted` instead of scanning all seventeen powers.
        // A carrier plans one timed pass per receiver per decision, and each scan is a 480-sample lookup.
        int lo = gentlest;
        int hi = PowerSamples - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (!TryGetFlightTimeAt(mid, distance, out float midTime)) { hi = mid; continue; }
            if (midTime > wanted) lo = mid;
            else hi = mid;
        }

        if (!TryGetFlightTimeAt(lo, distance, out float flightLo)) return Infeasible(distance);

        int bestIndex = lo;
        float bestFlight = flightLo;
        if (hi != lo && TryGetFlightTimeAt(hi, distance, out float flightHi) &&
            MathF.Abs(flightHi - wanted) < MathF.Abs(flightLo - wanted) - TimedArrivalTieSeconds)
        {
            bestIndex = hi;
            bestFlight = flightHi;
        }

        if (bestFlight > wanted + MaxTimedArrivalLateSeconds) return Infeasible(distance);

        // AUD-P7: the timed plan is not power-capped either. The boundary-exit risk of a ball that would
        // leave the pitch just past the region is priced by
        // <see cref="AI.PassSuccessModel.Estimate"/> (the same place the untimed plan's risk is priced),
        // so this method is byte-identical to the pre-AUD-P7 timed planner for every input.
        return new PassPlan(
            Feasible: true,
            AimDirectionXZ: SimMath.SafeNormalize(target - originXZ, new Vector2(1f, 0f)),
            TargetXZ: target,
            Power01: MinPower + bestIndex * PowerStep,
            FlightTimeSeconds: bestFlight,
            DistanceMeters: distance);
    }

    /// <summary>
    /// AUD-P7: the calibrated distance a ground pass struck at <paramref name="power01"/> travels before
    /// it comes to rest. This is the rest-position table the one-time calibration builds with the real
    /// solver (it continues the roll past the 8 s flight table); a power whose calibration never saw the
    /// ball stop is <b>saturated</b> and returns <see cref="FarStopDistanceMeters"/>, deliberately far
    /// outside any pitch (the diagonal is ~125 m), so the measurement's cap cannot hide a real overrun.
    /// Deterministic, allocation-free, one table lookup. Non-finite powers are clamped to the grid.
    /// </summary>
    public float PredictStopDistance(float power01)
    {
        float power = float.IsFinite(power01) ? power01 : MinPower;
        int index = Math.Clamp((int)MathF.Round((power - MinPower) / PowerStep), 0, PowerSamples - 1);
        return StopDistanceForIndex(index);
    }

    /// <summary>
    /// AUD-P7 / fix 4.3: the bounded share of a ground pass's completion that is charged because the ball
    /// would leave the pitch <b>just past its aim point</b> — the audit's "fast pass aimed 1.0 m inside a
    /// boundary rolls out of play" defect, priced instead of capped (the cap experiment is recorded in
    /// <see cref="BoundaryExitMaxRisk01"/>'s doc; a ground ball always rolls 26.4–51.6 m, so the only way to
    /// keep its rest point inside was to turn the pass into a slow roller, the AUD-P5 defect class).
    ///
    /// The rules, in order:
    /// <list type="bullet">
    /// <item>The projected rest point is <c>origin + aim × PredictStopDistance(power)</c> (equivalently
    /// <c>target + aim × (stop − distance)</c>). If the ball stops before or on the pitch boundary the
    /// risk is <b>0</b> — the ball stays in play.</item>
    /// <item>The first pitch boundary the straight roll crosses is located. If the exit is more than
    /// <see cref="BoundaryExitPenaltyRadiusMeters"/> past the aim point the risk is <b>0</b>: the roll
    /// leaves the pitch only after the receiver had that much room to move onto it, which is ordinary
    /// play, not the audit's overrun.</item>
    /// <item>Otherwise the risk ramps linearly from <b>0</b> at the radius to
    /// <see cref="BoundaryExitMaxRisk01"/> as the exit point reaches the aim point (0 m past it); an aim
    /// point already past the line takes the maximum.</item>
    /// <item>An exit on a goal line <b>between the posts</b> (|coordinate| ≤ the pitch's half goal width)
    /// is a goal, not out of play, so the risk is <b>0</b>. The ball rolls on the deck, far below the
    /// crossbar, so the 2D goal mouth is the whole question.</item>
    /// </list>
    /// Non-finite origins/targets, a zero-length aim (target within 0.5 m of the origin) and a non-finite
    /// power all return <b>0</b> deterministically — the caller's plan is not a roll in that case, and the
    /// estimator must never produce a NaN completion. Deterministic and allocation-free: one stop-table
    /// lookup plus a rectangle ray test, no per-call state.
    /// </summary>
    public float BoundaryExitRisk01(Vector2 originXZ, Vector2 targetXZ, float power01)
    {
        if (!SimMath.IsFinite(originXZ) || !SimMath.IsFinite(targetXZ)) return 0f;
        if (!float.IsFinite(power01)) return 0f; // no meaningful roll to price; never produce NaN

        Vector2 segment = targetXZ - originXZ;
        float distance = segment.Length();
        if (!float.IsFinite(distance) || distance < 0.5f) return 0f;
        Vector2 aim = segment / distance;

        float stopDistance = PredictStopDistance(power01);
        if (!(stopDistance > distance)) return 0f; // the ball stops at or before its aim point

        float exitDistance = FirstPitchExitDistance(originXZ, aim, out bool goalLineExit, out float perpendicular);
        if (!(exitDistance < stopDistance)) return 0f; // the projected rest point is inside the pitch

        if (goalLineExit && MathF.Abs(perpendicular) <= _pitch.HalfGoalWidth) return 0f; // a goal, not out

        float pastAim = MathF.Max(0f, exitDistance - distance);
        if (pastAim >= BoundaryExitPenaltyRadiusMeters) return 0f;

        float risk = BoundaryExitMaxRisk01 * (1f - pastAim / BoundaryExitPenaltyRadiusMeters);
        return Math.Clamp(risk, 0f, BoundaryExitMaxRisk01);
    }

    /// <summary>
    /// AUD-P7: distance along <paramref name="direction"/> (from a point inside the pitch) to the first
    /// pitch line crossed, or <see cref="float.PositiveInfinity"/> when the ray never leaves the rectangle.
    /// Reports whether that line is a goal line and the perpendicular coordinate at the crossing (z for a
    /// goal line, x for a touchline) so <see cref="BoundaryExitRisk01"/> can exempt the goal mouth.
    /// A rectangle is convex, so one crossing is the whole story. Allocation-free, no allocation in the
    /// per-pass path.
    /// </summary>
    private float FirstPitchExitDistance(
        Vector2 originXZ,
        Vector2 directionXZ,
        out bool goalLineExit,
        out float perpendicularAtExit)
    {
        goalLineExit = false;
        perpendicularAtExit = 0f;
        float exitDistance = float.PositiveInfinity;

        if (MathF.Abs(directionXZ.X) > 1e-6f)
        {
            float goalLineX = directionXZ.X > 0f ? _pitch.HalfLength : -_pitch.HalfLength;
            float t = (goalLineX - originXZ.X) / directionXZ.X;
            if (t >= 0f)
            {
                float z = originXZ.Y + directionXZ.Y * t;
                if (z >= -_pitch.HalfWidth && z <= _pitch.HalfWidth && t < exitDistance)
                {
                    exitDistance = t;
                    goalLineExit = true;
                    perpendicularAtExit = z;
                }
            }
        }

        if (MathF.Abs(directionXZ.Y) > 1e-6f)
        {
            float touchLineZ = directionXZ.Y > 0f ? _pitch.HalfWidth : -_pitch.HalfWidth;
            float t = (touchLineZ - originXZ.Y) / directionXZ.Y;
            if (t >= 0f)
            {
                float x = originXZ.X + directionXZ.X * t;
                if (x >= -_pitch.HalfLength && x <= _pitch.HalfLength && t < exitDistance)
                {
                    exitDistance = t;
                    goalLineExit = false;
                    perpendicularAtExit = x;
                }
            }
        }

        return exitDistance;
    }

    /// <summary>
    /// AUD-P5 / fix 4.1: the first power on the 0.20/0.05 grid whose arrival speed at
    /// <paramref name="distanceMeters"/> is at least <paramref name="minArrivalSpeedMetersPerSecond"/>.
    /// Arrival speed rises monotonically with power (a firmer ball is faster at the same distance), so this
    /// is a bounded binary search over the 17 powers with a bounded scan of the ≤480 samples each — no
    /// second calibration table and no allocation. If even the firmest power is slower than requested the
    /// firmest power is returned; the caller's late-arrival bound then refuses the plan if it cannot work.
    /// </summary>
    private int FirstPowerIndexForArrivalSpeed(float distanceMeters, float minArrivalSpeedMetersPerSecond)
    {
        if (!TryGetArrivalSpeedAt(PowerSamples - 1, distanceMeters, out float fastest) ||
            fastest < minArrivalSpeedMetersPerSecond)
        {
            return PowerSamples - 1;
        }

        int lo = 0;
        int hi = PowerSamples - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (TryGetArrivalSpeedAt(mid, distanceMeters, out float speed) &&
                speed >= minArrivalSpeedMetersPerSecond)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }

    /// <summary>
    /// AUD-P5 / fix 4.1: arrival speed at a distance, derived from the existing
    /// <see cref="Calibration.DistanceAtTime"/> samples by finite difference. The flight sample at which
    /// the ball first covers the distance is the same sample <see cref="TryGetFlightTimeAt"/> reports, so
    /// the speed is read across that sample — the derivation adds no calibration table, and the result is
    /// a physical quantity already in the planner's own physics rather than a heuristic guess.
    /// Returns false when the power cannot reach the distance at all.
    /// </summary>
    private bool TryGetArrivalSpeedAt(
        int powerIndex,
        float distanceMeters,
        out float arrivalSpeedMetersPerSecond)
    {
        arrivalSpeedMetersPerSecond = 0f;
        if (!TryGetFlightTimeAt(powerIndex, distanceMeters, out float flightTimeSeconds)) return false;

        int index = Math.Clamp(powerIndex, 0, PowerSamples - 1);
        int sample = Math.Clamp((int)MathF.Round(flightTimeSeconds / TimeStep), 0, TimeSamples - 1);
        int before = Math.Max(0, sample - 1);
        int after = Math.Min(TimeSamples - 1, sample + 1);
        if (after <= before) return false;

        float speed = (_calibration.DistanceAtTime[index, after] - _calibration.DistanceAtTime[index, before]) /
                      ((after - before) * TimeStep);
        if (!float.IsFinite(speed) || speed < 0f) return false;

        arrivalSpeedMetersPerSecond = speed;
        return true;
    }

    /// <summary>
    /// AUD-P7 / fix 4.3: the calibrated final resting distance of a pass struck at <paramref name="powerIndex"/>.
    /// A power whose calibration never saw the ball stop (it was still rolling at
    /// <see cref="MaxStopCalibrationDistance"/>) is reported as <see cref="FarStopDistanceMeters"/> — treated
    /// as stopping far away, so the saturation of the measurement can never hide a real overrun.
    /// </summary>
    private float StopDistanceForIndex(int powerIndex)
    {
        int index = Math.Clamp(powerIndex, 0, PowerSamples - 1);
        return _calibration.StopDistanceSaturated[index]
            ? FarStopDistanceMeters
            : _calibration.StopDistance[index];
    }

    /// <summary>Lowest power index at which a ground pass covers <paramref name="distanceMeters"/>.</summary>
    private bool TryFirstPowerThatReaches(float distanceMeters, int minIndex, out int powerIndex)
    {
        for (int i = Math.Max(0, minIndex); i < PowerSamples; i++)
        {
            if (!TryGetFlightTimeAt(i, distanceMeters, out _)) continue;
            powerIndex = i;
            return true;
        }

        powerIndex = -1;
        return false;
    }

    private bool TryGetFlightTimeAt(int powerIndex, float distanceMeters, out float flightTimeSeconds)
    {
        flightTimeSeconds = 0f;
        if (distanceMeters <= 0f) return false;
        int index = Math.Clamp(powerIndex, 0, PowerSamples - 1);

        for (int k = 1; k < TimeSamples; k++)
        {
            if (_calibration.DistanceAtTime[index, k] >= distanceMeters)
            {
                flightTimeSeconds = k * TimeStep;
                return true;
            }
        }

        return false;
    }

    /// <summary>Flight time for a power/distance pair; exposed for diagnostics and tests.</summary>
    public bool TryGetFlightTime(float power01, float distanceMeters, out float flightTimeSeconds)
    {
        int powerIndex = Math.Clamp((int)MathF.Round((power01 - MinPower) / PowerStep), 0, PowerSamples - 1);
        return TryGetFlightTimeAt(powerIndex, distanceMeters, out flightTimeSeconds);
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
    /// at that pace. AUD-P5 fix 4.2 proposed lowering the pace to 7.0 m/s, which would have grown these
    /// ranges (the full-power crisp range from ~22.6 m to ~28.3 m); that change was reversed on
    /// measurement (see <see cref="CrispArrivalSpeed"/>), so the ranges are the 9.0 m/s ones and reach is
    /// unchanged. The overhit AUD-P5 measured is priced as the AUD-P7 boundary-exit risk in
    /// <see cref="AI.PassSuccessModel.Estimate"/> (see <see cref="BoundaryExitRisk01"/>), not by capping
    /// the power here.
    /// </summary>
    public float CrispRangeFor(float power01)
    {
        int powerIndex = Math.Clamp((int)MathF.Round((power01 - MinPower) / PowerStep), 0, PowerSamples - 1);
        return _calibration.CrispRange[powerIndex];
    }

    /// <summary>
    /// AUD-P5 / fix 4.1: the lowest power on the planner's 0.20/0.05 grid whose calibrated crisp range
    /// still reaches <paramref name="distanceMeters"/> at <see cref="CrispArrivalSpeed"/> — i.e. the
    /// gentlest ball that is still a firm pass when it arrives. It is the public form of the planner's own
    /// <see cref="PowerIndexForCrispDistance"/> anchor, clamped to the grid: a distance of zero or less
    /// gives <see cref="MinPower"/> (0.20), and a distance beyond the full-power crisp range (~22.6 m at
    /// the restored 9.0 m/s pace) gives the firmest power (1.00) because no power keeps crisp pace over
    /// that far. The genuine defect fix 4.1 targets is the timed-region slow roller, not the crisp pace:
    /// pair this floor (or <c>minArrivalSpeedMetersPerSecond</c>) with
    /// <see cref="PlanGroundPassToRegion"/> when a firm ball is wanted.
    ///
    /// <b>How a candidate-side caller uses it:</b> pass the result as <c>minPower01</c> to
    /// <see cref="PlanGroundPassToRegion"/> (or <see cref="PlanGroundPass"/>) when the intended ball is a
    /// <i>firm</i> one — a through ball into space, or a ball a runner must attack at pace — so the planner
    /// cannot fall back to a slow roller. It is a floor only: the planner may still strike firmer.
    /// Passing <c>0</c> as <c>minPower01</c>, or not calling this method at all, keeps every existing
    /// caller's plan unchanged (the min-power floor is applied identically to a caller-supplied value).
    /// </summary>
    public float MinCrispPower01(float distanceMeters)
    {
        // Clamp to the grid's domain: a non-positive distance needs the gentlest power, a NaN is not a
        // distance at all so it takes the safe firmest answer, and anything past the full-power crisp
        // range falls through to the grid clamp inside PowerIndexForCrispDistance.
        float distance = float.IsNaN(distanceMeters)
            ? float.PositiveInfinity
            : distanceMeters <= 0f
                ? 0f
                : distanceMeters;
        return MinPower + PowerIndexForCrispDistance(distance) * PowerStep;
    }

    /// <summary>
    /// Picks the arrival time with the smallest miss between the distance the ball can cover by then
    /// and the distance to where the receiver will be. A perpendicular runner can never be met
    /// exactly (their radial speed approaches the decaying ball speed), so "closest approach" — the
    /// same quantity the acceptance criteria measure — is the right objective.
    ///
    /// The receiver is modelled at his real velocity for the whole flight, and the resulting lead is
    /// then checked against <paramref name="maxLeadMeters"/>. It used to be the other way round — the
    /// receiver was *frozen* at the bound — which made the planner claim it could meet a sprinter it had
    /// actually left behind (the ball was aimed at the frozen point while the runner kept going), and
    /// let a pass to an uncatchable runner be reported feasible. A lead beyond the bound is a punt, so it
    /// is refused and the power search tries a firmer ball, which shortens the flight and the lead.
    /// </summary>
    private bool TrySolveArrivalTime(
        int powerIndex,
        Vector2 originXZ,
        Vector2 receiverPositionXZ,
        Vector2 receiverVelocityXZ,
        Vector2 extraLead,
        float maxLeadMeters,
        bool freezeReceiverAtLeadBound,
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

            // A human receiver may turn or stop at any moment, so his caller's pass is aimed no further
            // ahead than the bound even though he would run past it — the hedge is the point. An AI
            // receiver is solved honestly: a lead beyond the bound is a punt, and it is refused rather
            // than frozen, because freezing the runner planned a ball he had already left behind.
            Vector2 lead = freezeReceiverAtLeadBound
                ? ClampVectorLength(receiverVelocityXZ * t, maxLeadMeters) + extraLead
                : receiverVelocityXZ * t + extraLead;

            float required = Vector2.Distance(originXZ, receiverPositionXZ + lead);
            float miss = MathF.Abs(required - ballDistance);

            if (miss < bestMiss)
            {
                bestMiss = miss;
                bestTime = t;
            }
        }

        if (bestMiss > MaxMeetingMiss) return false;

        if (!freezeReceiverAtLeadBound)
        {
            float lead = Vector2.Distance(
                receiverPositionXZ, receiverPositionXZ + receiverVelocityXZ * bestTime + extraLead);
            if (lead > maxLeadMeters) return false;
        }

        flightTime = bestTime;
        return true;
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

    /// <summary>
    /// AIB-02d: the power a pass to a receiver at this straight-line distance is anchored to — whichever is
    /// crisper of the lowest power that reaches the distance at a trapable pace and the lowest power that
    /// reaches it at a firm pass pace (9.0 m/s; AUD-P5 fix 4.2's 7.0 m/s experiment was reversed on
    /// measurement), raised to the caller's requested floor. The first solve, the firmer retry and the
    /// pitch-clamp re-anchor all read it, so a caller's floor is never silently dropped.
    /// </summary>
    private int AnchoredPowerIndex(float distanceMeters, float minPower01)
    {
        int index = Math.Max(
            PowerIndexForDistance(distanceMeters),
            PowerIndexForCrispDistance(distanceMeters));

        if (minPower01 > 0f)
        {
            int minPowerIndex = Math.Clamp(
                (int)MathF.Round((minPower01 - MinPower) / PowerStep), 0, PowerSamples - 1);
            index = Math.Max(index, minPowerIndex);
        }

        return index;
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
    /// A full-power ground pass carries the crisp 9.0 m/s pace for ~22.6 m, so anything longer than that
    /// falls back to the maximum (the ball still arrives trapable, just later) rather than being struck
    /// gently the whole way. The caller-facing form of this anchor is <see cref="MinCrispPower01"/>.
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
    ///
    /// AUD-P7 extends the same roll past the eight-second flight table to find the ball's <b>final resting
    /// distance</b>: the boundary-exit risk asks where the ball would come to rest, and the eight-second
    /// table cannot answer it (the slowest balls are still creeping at 8 s). The roll continues until the
    /// solver stops the ball or it reaches <see cref="MaxStopCalibrationDistance"/>, short of the goal net.
    /// The distance/time table itself keeps its original 45 m cap so the planning path is unchanged, and a
    /// row still rolling at the stop cap is flagged as saturated (reported as
    /// <see cref="FarStopDistanceMeters"/> by <see cref="PredictStopDistance"/>), so that cap cannot hide a
    /// real overrun.
    /// </summary>
    private static Calibration Calibrate()
    {
        var distanceAtTime = new float[PowerSamples, TimeSamples];
        var range = new float[PowerSamples];
        var crispRange = new float[PowerSamples];
        var stopDistance = new float[PowerSamples];
        var stopSaturated = new bool[PowerSamples];

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
            float stop = 0f;
            bool capturedRange = false;
            bool capturedCrisp = false;
            bool tableComplete = false;
            bool saturated = false;

            for (int k = 1; k <= StopSamples; k++)
            {
                solver.Step(ball, TimeStep);

                // The true roll keeps increasing monotonically while the ball travels forward.
                stop = MathF.Max(stop, ball.Position.X);

                if (k < TimeSamples && !tableComplete)
                {
                    float distance = MathF.Min(stop, MaxCalibrationDistance);
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

                    if (stop >= MaxCalibrationDistance - 0.01f)
                    {
                        for (int rest = k + 1; rest < TimeSamples; rest++)
                            distanceAtTime[powerIndex, rest] = distance;
                        tableComplete = true;
                    }
                }

                if (stop >= MaxStopCalibrationDistance)
                {
                    saturated = true;
                    stop = MaxStopCalibrationDistance;
                    break;
                }

                if (ball.Velocity.LengthSquared() <= StopSpeedMetersPerSecond * StopSpeedMetersPerSecond)
                    break; // the solver has zeroed the roll: this is the final rest position
            }

            if (!capturedRange) range[powerIndex] = lastDistance;
            if (!capturedCrisp) crispRange[powerIndex] = lastDistance;
            stopDistance[powerIndex] = stop;
            stopSaturated[powerIndex] = saturated;
        }

        return new Calibration(distanceAtTime, range, crispRange, stopDistance, stopSaturated);
    }
}
