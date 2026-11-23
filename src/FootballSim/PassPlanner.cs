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
    /// pace to ~6.5–7.0 m/s to reduce the overhit roll. That outcome is already owned by the AUD-P7 stop
    /// cap (a fast ball aimed near a line is power-capped, below), and the pace change measured badly:
    /// at 7.0 m/s a 17 m pass anchored at 0.70 power instead of 0.80 and its flight lengthened from
    /// 1.317 s to 1.483 s (20 m: 1.433 s to 1.683 s). A longer flight is exactly what makes a ball
    /// interceptable under this repo's AIB-02a physics lane model — the 2026-11-22 evidence recorded
    /// slow rollers costing 26.8 % interceptions, and <c>ai55</c> completes 80–83 % of passes with the
    /// 9.0 m/s pace. Concretely, the 7.0 experiment turned two clean AIB-02b congestion fixtures into
    /// lane-blocked passes merely by lengthening their 10 m flight from 0.867 s to 1.033 s. So the
    /// overhit half of AUD-P5 is fixed by the AUD-P7 stop cap and the pace stays at 9.0 m/s; lowering it
    /// lengthens the flight and increases lane blocking (measured).
    /// </summary>
    private const float CrispArrivalSpeed = 9.0f;

    private const float MaxMeetingMiss = 1.0f;        // reception reach is ~1.24 m, leave margin

    /// <summary>
    /// AUD-P7 / fix 4.3: how far a projected stop point may sit past a goal line or touchline and still
    /// count as inside the pitch. The touchline-overrun cap only needs to stop a ball that visibly
    /// leaves the field; a stop point half a metre over the line is a near-miss, so the tolerance keeps
    /// float noise at the boundary from throttling an otherwise fine pass.
    /// </summary>
    public const float BoundaryStopMarginMeters = 0.5f;

    /// <summary>
    /// AUD-P7 / fix 4.3: only a target this close to a goal line or touchline is exposed to the audit's
    /// overrun — the fast pass "aimed 1.0 m inside a boundary" whose ball rolls straight out of play just
    /// past the receiver. The audit scopes the safeguard to <c>margin &lt; 4 m</c>, and the scoping is
    /// load-bearing: a rolling ground pass always stops 26–54 m from the passer (the gentlest 0.20-power
    /// ball still runs ~26 m), so an un-scoped projection would cap every forward ball in the attacking
    /// third whose roll crosses the goal line — including short passes nowhere near a touchline. Past
    /// this margin the receiver has room to move onto the ball, and the completion model already prices
    /// the miss; near the line the ball is simply out of play.
    /// </summary>
    private const float BoundaryTargetMarginMeters = 4f;

    /// <summary>
    /// AUD-P7: how long the one-time calibration keeps rolling a cloned ball to find where it actually
    /// comes to rest. The 8 s flight table is not long enough for the slow tail of the roll (the gentlest
    /// ground pass is still creeping at 8 s and only stops after ~9–12 s), and the AUD-P7 overrun check
    /// is a question about the final rest position, not about the position after eight seconds.
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
    /// AUD-P7: the projected stop distance of a saturated power — one whose calibration never saw the
    /// ball come to rest before <see cref="MaxStopCalibrationDistance"/>. Deliberately far outside any
    /// pitch (the diagonal is ~125 m) so the overrun check cannot hide a real overrun: a saturated power
    /// is treated as "stopping far away", exactly as the fix requires.
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

        // AUD-P7 / fix 4.3: a ball that would roll out of play just past a receiver near the line is
        // capped *after* the power was chosen — the plan is kept and only the pace is reduced. This is a
        // no-op for every plan whose projected stop point is already inside the pitch, which is most
        // passes; see <see cref="TryCapPowerForBoundaryOverrun"/> for the exact scoping.
        Vector2 aimDirection = SimMath.SafeNormalize(target - originXZ, new Vector2(1f, 0f));
        if (!ProjectedStopStaysInside(target, aimDirection, distance, powerIndex))
        {
            int minAllowedIndex = minPower01 > 0f
                ? Math.Clamp((int)MathF.Round((minPower01 - MinPower) / PowerStep), 0, PowerSamples - 1)
                : 0;

            if (TryCapPowerForBoundaryOverrun(
                    originXZ, receiverPositionXZ, velocity, extraLead, leadCap, freezeLead,
                    minAllowedIndex, powerIndex,
                    out int cappedIndex, out float cappedFlight, out Vector2 cappedTarget))
            {
                powerIndex = cappedIndex;
                flightTime = cappedFlight;
                target = cappedTarget;
                distance = Vector2.Distance(originXZ, target);
                aimDirection = SimMath.SafeNormalize(target - originXZ, new Vector2(1f, 0f));
            }
        }

        return new PassPlan(
            Feasible: true,
            AimDirectionXZ: aimDirection,
            TargetXZ: target,
            Power01: MinPower + powerIndex * PowerStep,
            FlightTimeSeconds: flightTime,
            DistanceMeters: distance);
    }

    /// <summary>
    /// AUD-P7 / fix 4.3, planner side: walks the power down from <paramref name="powerIndex"/> to find the
    /// highest power whose projected stop point stays inside the pitch when the pass is aimed near a
    /// boundary. The gentlest power that still solves the meeting is the documented fallback when no
    /// allowed power keeps the ball in play, so a plan is never refused just because it is overhit.
    ///
    /// The search is bounded by the 17-power grid and does not allocate; it only runs when
    /// <see cref="ProjectedStopStaysInside"/> has already failed, which is the rare near-line case.
    /// A candidate is only accepted when the arrival solve still finds a real meeting at that slower
    /// pace — if every slower ball is a punt (the receiver has run past the lead bound) the original
    /// plan is kept, because refusing the pass or replacing it with an unreachable lead would be worse
    /// than an overhit that the receiver may still cut out. The caller's own power floor
    /// (<paramref name="minAllowedIndex"/>) is the lower bound of the search, so a human caller's
    /// explicitly requested firm ball is not silently turned into a dribble; if even that floor overruns,
    /// the floor is the "gentlest" allowed power.
    /// </summary>
    private bool TryCapPowerForBoundaryOverrun(
        Vector2 originXZ,
        Vector2 receiverPositionXZ,
        Vector2 receiverVelocityXZ,
        Vector2 extraLead,
        float maxLeadMeters,
        bool freezeReceiverAtLeadBound,
        int minAllowedIndex,
        int powerIndex,
        out int cappedIndex,
        out float cappedFlight,
        out Vector2 cappedTarget)
    {
        cappedIndex = powerIndex;
        cappedFlight = 0f;
        cappedTarget = default;

        bool haveGentlest = false;
        int gentlestIndex = powerIndex;
        float gentlestFlight = 0f;
        Vector2 gentlestTarget = default;

        for (int candidate = powerIndex - 1; candidate >= minAllowedIndex; candidate--)
        {
            if (!TrySolveArrivalTime(
                    candidate, originXZ, receiverPositionXZ, receiverVelocityXZ, extraLead,
                    maxLeadMeters, freezeReceiverAtLeadBound, out float candidateFlight))
            {
                continue;
            }

            Vector2 candidateTarget = ClampToPitch(receiverPositionXZ
                + ClampVectorLength(receiverVelocityXZ * candidateFlight, maxLeadMeters)
                + extraLead);
            float candidateDistance = Vector2.Distance(originXZ, candidateTarget);
            if (candidateDistance < 0.5f || candidateDistance > MaxCalibrationDistance) continue;

            Vector2 candidateAim = SimMath.SafeNormalize(candidateTarget - originXZ, new Vector2(1f, 0f));
            gentlestIndex = candidate;
            gentlestFlight = candidateFlight;
            gentlestTarget = candidateTarget;
            haveGentlest = true;

            if (!ProjectedStopStaysInside(candidateTarget, candidateAim, candidateDistance, candidate))
                continue;

            cappedIndex = candidate;
            cappedFlight = candidateFlight;
            cappedTarget = candidateTarget;
            return true;
        }

        if (!haveGentlest) return false;

        // No allowed power keeps the ball inside: take the gentlest power that still makes the pass, the
        // documented AUD-P7 fallback, so the overrun is as short as the caller's own floor allows.
        cappedIndex = gentlestIndex;
        cappedFlight = gentlestFlight;
        cappedTarget = gentlestTarget;
        return true;
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
        if (!TrySolveTimedPower(distance, wanted, gentlest, PowerSamples - 1, out int bestIndex, out float bestFlight))
            return Infeasible(distance);

        if (bestFlight > wanted + MaxTimedArrivalLateSeconds) return Infeasible(distance);

        // AUD-P7 / fix 4.3: a timed ball that would roll out just past a target near the line is capped and
        // the arrival re-solved at the capped power, keeping the same "closest to the wanted meeting"
        // objective. When the cap would make the plan infeasible (the slower ball arrives too late) the
        // original plan is kept: losing the pass entirely is worse than an overhit the receiver may cut out.
        Vector2 aimDirection = SimMath.SafeNormalize(target - originXZ, new Vector2(1f, 0f));
        if (!ProjectedStopStaysInside(target, aimDirection, distance, bestIndex))
        {
            int cappedIndex = -1;
            for (int candidate = bestIndex - 1; candidate >= gentlest; candidate--)
            {
                if (ProjectedStopStaysInside(target, aimDirection, distance, candidate))
                {
                    cappedIndex = candidate;
                    break;
                }
            }

            if (cappedIndex < 0)
            {
                // No allowed power keeps the projected stop inside: take the gentlest power that still
                // reaches (the caller's pace floor included), which minimises the overrun.
                cappedIndex = gentlest;
            }

            if (cappedIndex < bestIndex &&
                TrySolveTimedPower(distance, wanted, gentlest, cappedIndex, out int cappedBest, out float cappedFlight) &&
                cappedFlight <= wanted + MaxTimedArrivalLateSeconds)
            {
                bestIndex = cappedBest;
                bestFlight = cappedFlight;
            }
        }

        return new PassPlan(
            Feasible: true,
            AimDirectionXZ: aimDirection,
            TargetXZ: target,
            Power01: MinPower + bestIndex * PowerStep,
            FlightTimeSeconds: bestFlight,
            DistanceMeters: distance);
    }

    /// <summary>
    /// The power whose flight time lands closest to <paramref name="wantedSeconds"/> for a fixed distance,
    /// searched by bisection between <paramref name="gentlestIndex"/> and <paramref name="ceilingIndex"/>.
    /// The same routine serves the uncapped plan (<paramref name="ceilingIndex"/> = the firmest power) and
    /// the AUD-P7 re-solve (<paramref name="ceilingIndex"/> = the boundary-capped power), so the capped
    /// arrival is measured against exactly the same objective as the uncapped one.
    /// Returns false only when the gentlest allowed power cannot reach the distance at all.
    /// </summary>
    private bool TrySolveTimedPower(
        float distanceMeters,
        float wantedSeconds,
        int gentlestIndex,
        int ceilingIndex,
        out int bestIndex,
        out float bestFlight)
    {
        bestIndex = -1;
        bestFlight = 0f;
        int lo = Math.Max(0, gentlestIndex);
        int hi = Math.Clamp(ceilingIndex, lo, PowerSamples - 1);
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (!TryGetFlightTimeAt(mid, distanceMeters, out float midTime)) { hi = mid; continue; }
            if (midTime > wantedSeconds) lo = mid;
            else hi = mid;
        }

        if (!TryGetFlightTimeAt(lo, distanceMeters, out float flightLo)) return false;

        bestIndex = lo;
        bestFlight = flightLo;
        if (hi != lo && TryGetFlightTimeAt(hi, distanceMeters, out float flightHi) &&
            MathF.Abs(flightHi - wantedSeconds) < MathF.Abs(flightLo - wantedSeconds) - TimedArrivalTieSeconds)
        {
            bestIndex = hi;
            bestFlight = flightHi;
        }

        return true;
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

    /// <summary>
    /// AUD-P7 / fix 4.3: true when the ball's projected final stop point stays inside the pitch through the
    /// boundary (or boundaries) the <paramref name="targetXZ"/> is near. The projected stop point is
    /// <c>target + aimDirection × (stopDistance − distance)</c>, i.e. where the ball would come to rest if the
    /// receiver never touched it. The check only applies to a target within
    /// <see cref="BoundaryTargetMarginMeters"/> of a goal line or touchline and only to the direction the ball
    /// is actually travelling: a pass aimed back inside from the line, or a central pass, is never touched.
    /// This is the referee-vision version of the audit's "aimed 1.0 m inside a boundary" case: an overhit
    /// there is simply out of play, so the planner caps its pace instead of pretending the ball stops.
    /// </summary>
    private bool ProjectedStopStaysInside(
        Vector2 targetXZ,
        Vector2 aimDirectionXZ,
        float distanceMeters,
        int powerIndex)
    {
        bool nearPositiveX = _pitch.HalfLength - targetXZ.X <= BoundaryTargetMarginMeters;
        bool nearNegativeX = targetXZ.X + _pitch.HalfLength <= BoundaryTargetMarginMeters;
        bool nearPositiveZ = _pitch.HalfWidth - targetXZ.Y <= BoundaryTargetMarginMeters;
        bool nearNegativeZ = targetXZ.Y + _pitch.HalfWidth <= BoundaryTargetMarginMeters;
        if (!nearPositiveX && !nearNegativeX && !nearPositiveZ && !nearNegativeZ) return true;

        float stopDistance = StopDistanceForIndex(powerIndex);
        float beyondTarget = stopDistance - distanceMeters;
        if (beyondTarget <= 0f) return true; // the ball stops at or before the target: no overrun

        Vector2 projected = targetXZ + aimDirectionXZ * beyondTarget;
        float insideX = _pitch.HalfLength + BoundaryStopMarginMeters;
        float insideZ = _pitch.HalfWidth + BoundaryStopMarginMeters;

        if (nearPositiveX && aimDirectionXZ.X > 0f && projected.X > insideX) return false;
        if (nearNegativeX && aimDirectionXZ.X < 0f && projected.X < -insideX) return false;
        if (nearPositiveZ && aimDirectionXZ.Y > 0f && projected.Y > insideZ) return false;
        if (nearNegativeZ && aimDirectionXZ.Y < 0f && projected.Y < -insideZ) return false;
        return true;
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
    /// unchanged. The overhit AUD-P5 measured is handled by the AUD-P7 stop cap instead.
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
    /// distance</b>: the overrun check asks where the ball would come to rest, and the eight-second table
    /// cannot answer it (the slowest balls are still creeping at 8 s). The roll continues until the solver
    /// stops the ball or it reaches <see cref="MaxStopCalibrationDistance"/>, short of the goal net. The
    /// distance/time table itself keeps its original 45 m cap so the planning path is unchanged, and a row
    /// still rolling at the stop cap is flagged as saturated (reported as
    /// <see cref="FarStopDistanceMeters"/>), so that cap cannot hide a real overrun.
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
