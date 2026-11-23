using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Physics;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// Regression tests for the carrier-candidate findings of the 2026-11-23 passing/space audit
/// (<c>docs/AUDIT_PASSING_SPACE_2026-11-23.md</c>):
/// <list type="bullet">
/// <item><b>AUD-P2 / Fix 1.2</b> — a pass into a run was scored with the receiver's <i>feet</i> progression,
/// so a through ball aimed past the runner carried no extra forward preference over a ball rolled to his
/// feet. The candidate now declares its progression source (<c>PassOption.ProgressFromTarget</c>) and
/// <c>TryAddGroundPass</c> scores a run ball on its plan's aim point.</item>
/// <item><b>AUD-P4 / Fix 3.3</b> — the reception approach deadband is tightened from 0.45 m to
/// <c>ReceptionArrivalDeadbandMeters</c> (0.15 m). Its arithmetic is stated in the constant's own XML doc;
/// this file pins the pass-side progression and the carry/line-break halves of the same audit.</item>
/// <item><b>AUD-P6 / Fix 5.2</b> — the carry's goalward ramp started at +10 m attack-relative projection, so a
/// carrier near the centre circle reverted to pure empty-space seeking and drifted to the empty flank. The
/// ramp now starts at −10 m and the midfield carrier drives goalward.</item>
/// <item><b>AUD-P6 / Fix 5.3</b> — the line-breaking-preference floor moves from 12 m to 4 m so central
/// progression is rewarded in midfield/transition, while the preference itself stays bounded by
/// <see cref="CandidateGenerator.LineBreakPreferenceMax"/>.</item>
/// </list>
///
/// Every assertion is written so the <i>pre-fix</i> answer is visible from the arithmetic in the comment and
/// the assertion fails on the pre-fix code. Fixtures are built from public members only (there is no
/// <c>InternalsVisibleTo</c> in this repository): candidate sets are generated with
/// <see cref="CandidateGenerator.GenerateOnBallCandidates"/> and the resulting
/// <see cref="UtilityCandidate"/> scores/terms are read directly, which is the same route the decision path
/// takes.
/// </summary>
public sealed class Aib2CandidateSpaceTests
{
    private static readonly PitchDimensions Pitch = new();
    private static readonly Vector3 OpponentGoal = new(Pitch.HalfLength, 0f, 0f);
    private const int CarrierId = 8;
    private const int MateId = 9;
    private const float ObservationTick = 500f;

    // =============================================================================================
    // AUD-P2 / Fix 1.2 — a ball into a run is scored on where it is aimed, not on the receiver's feet.
    // =============================================================================================

    /// <summary>
    /// AUD-P2 / Fix 1.2. Fixture: the carrier is at (0, 8) with the ball at (0.4, 8); the receiver stands
    /// 12 m ahead of him at (12, 8) and has announced a meeting region 12 m <b>beyond</b> himself at (24, 8).
    /// The feet variant plans to his feet; the run variant is timed to the announced region.
    ///
    /// Arithmetic (clean picture — no opponents, so lane clearance is exactly 1.0 and the lane-risk term is
    /// zero; the tendencies are the neutral baselines so the directness terms are zero; combinations and
    /// switch recognition are off and there is no visible opponent line, so
    /// <see cref="CandidateGenerator.LineBreakPreference"/> is zero — the candidate preference is exactly
    /// <see cref="CandidateGenerator.PassPreference"/>):
    /// <code>
    ///   ToFeet: progress = receiver feet = 12 m -> 0.35 x clamp(12 / 20) = 0.35 x 0.60 = 0.210
    ///   ToRun:  progress = aim point     = 24 m -> 0.35 x clamp(24 / 20) = 0.35 x 1.00 = 0.350
    ///   delta  = 0.140 = ActionValueScale.ProgressBonus x (1.00 - 0.60)
    /// </code>
    /// Pre-fix both variants were scored with the same feet-based <c>forwardMeters</c> = 12, so both
    /// preferences were 0.210 and the delta was exactly 0 — the audit's ToRun-loses-to-ToFeet inversion.
    /// </summary>
    [Fact]
    public void AIB2_Candidate_AThroughBall_IsScoredOnItsMeetingRegion_NotTheReceiversFeet()
    {
        Vector2 ballXZ = new(0.4f, 8f);
        Vector2 receiverXZ = new(12f, 8f);
        Vector2 meetingRegion = new(24f, 8f);
        PerceivedEntity receiver = TeamMate(MateId, receiverXZ.X, receiverXZ.Y);
        FootballerObservation observation = Observation(ballXZ, new Vector2(0f, 8f), receiver);
        var ai = new AiRuntimeConfig
        {
            EnableCombinations = false,
            EnableShallowSearch = false,
            EnableSwitchRecognition = false
        };
        var generator = new CandidateGenerator(Pitch, AnnouncedRun(MateId, meetingRegion, meetTick: 560), ai)
        {
            TimeOnBall = 0.8f
        };

        generator.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 46f);

        UtilityCandidate feet = Single(generator, "ToFeet", MateId);
        UtilityCandidate run = Single(generator, CandidateGenerator.RunPassTag, MateId);
        Assert.True(feet.TargetXZ.HasValue, "the feet pass must publish its aim point");
        Assert.True(run.TargetXZ.HasValue, "the run ball must publish its aim point");

        Vector2 feetAim = XZ(feet.TargetXZ!.Value);
        Vector2 runAim = XZ(run.TargetXZ!.Value);
        Assert.True(Vector2.Distance(receiverXZ, feetAim) <= 0.05f,
            $"a ball to feet must be planned at the receiver's feet, aimed {feetAim}");
        Assert.True(Vector2.Distance(meetingRegion, runAim) <= 0.05f,
            $"the announced run ball must be planned into the meeting region, aimed {runAim}");

        // Fixture validation: the picture is clean, so the only preference difference is progress. If either
        // lane or line-break term moved the preference, the delta assertion below would no longer isolate it.
        float feetLane = CandidateGenerator.EvaluateLaneClearance(
            ballXZ, feetAim, observation.VisiblePlayers, observation.SelfTeam);
        float runLane = CandidateGenerator.EvaluateLaneClearance(
            ballXZ, runAim, observation.VisiblePlayers, observation.SelfTeam);
        Assert.Equal(1f, feetLane, precision: 4);
        Assert.Equal(1f, runLane, precision: 4);
        Assert.Equal(0f, CandidateGenerator.LineBreakPreference(
            ballXZ, runAim, observation.AttackingDirectionX, observation.VisiblePlayers,
            observation.SelfTeam, runLane, run.Terms.Probability01));

        // The feet variant is scored on the receiver's ground (the candidate's own forwardMeters), the run
        // variant on the plan's aim point. Both are relative to the carrier at x = 0.
        float feetProgress = (receiverXZ.X - observation.SelfPosition.X) * observation.AttackingDirectionX;
        float runProgress = (runAim.X - observation.SelfPosition.X) * observation.AttackingDirectionX;
        Assert.Equal(12f, feetProgress, precision: 3);
        Assert.True(runProgress > 23f,
            $"the fixture must aim the run ball well past the runner, got {runProgress:F2} m");

        float expectedDelta = CandidateGenerator.PassPreference(observation, runProgress, runLane)
                              - CandidateGenerator.PassPreference(observation, feetProgress, feetLane);

        // Pre-fix: both read the feet (the run preference equalled the feet preference), so this was exactly 0.
        Assert.Equal(expectedDelta, run.Terms.Preference - feet.Terms.Preference, precision: 4);
        Assert.Equal(
            ActionValueScale.ProgressBonus * (1f - 12f / 20f),
            run.Terms.Preference - feet.Terms.Preference,
            precision: 3);
        Assert.True(run.Terms.Preference > feet.Terms.Preference + 0.10f,
            $"the through ball must out-price the same receiver's feet pass on progression: " +
            $"{run.Terms.Preference:F3} vs {feet.Terms.Preference:F3} (pre-fix the two were identical)");
    }

    // =============================================================================================
    // AUD-P6 / Fix 5.2 — a carrier near the centre circle drives goalward, not to the empty flank.
    // =============================================================================================

    /// <summary>
    /// AUD-P6 / Fix 5.2. Fixture: the carrier is at (0, −8) with the ball just ahead at (0.4, −8), a presser
    /// behind-left at (−3, −2) and a defender ahead-right at (11, −13); the empty grass is out toward the
    /// bottom touchline. The forward candidate aims at the goal (52.5, 0) and the outward one is 1.25 rad
    /// away from it.
    ///
    /// Arithmetic. The goalward weight at attack-relative projection 0 is
    /// <c>0.26 x clamp((0 − (−10)) / (20 − (−10)), 0, 1) = 0.26 x 10/30 = 0.0867</c>. Pre-fix the ramp started
    /// at +10 m, so both candidates were scored with the goal term at exactly 0:
    /// <code>
    ///   forward probe (4.94, −7.25): nearest defender 8.35 m -> freedom 8.35/9 = 0.928;
    ///       cell (6, −8) pressure 1 − 1.23/1.5 = 0.179 -> space = 0.6 x 0.928 + 0.4 x 0.821 = 0.885
    ///   outward (−1.25 rad) probe (2.27, −12.45): nearest defender 8.74 m -> 0.972;
    ///       cell (3, −11) pressure 1 − 1.37/1.5 = 0.086 -> space = 0.6 x 0.972 + 0.4 x 0.914 = 0.949
    ///
    ///   pre-fix  (goal term 0):      forward 0.55 x 0.885 + 0.45 = 0.9369
    ///                                outward 0.55 x 0.949 + 0.45 = 0.9718  <- the touchline direction won
    ///   post-fix (goal term added):  forward 0.9369 + 0.0867 x cos 0    = 1.0236
    ///                                outward 0.9718 + 0.0867 x cos 1.25 = 0.9991
    /// </code>
    /// The fix therefore moves the choice from the empty touchline (dot with the goal direction
    /// cos 1.25 = 0.315, 4.0 m further out) to the goalward drive (dot 1.000, 0.7 m inside the carrier's z).
    /// </summary>
    [Fact]
    public void AIB2_Candidate_MidfieldCarrier_DrivesGoalward_InsteadOfDriftingToTheEmptyFlank()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        FootballerObservation observation = Observation(
            new Vector2(0.4f, -8f), new Vector2(0f, -8f),
            Opponent(14, -3f, -2f),
            Opponent(15, 11f, -13f),
            TeamMate(MateId, 4f, -2f));

        generator.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 46f);

        UtilityCandidate carry = SingleByKind(generator, CandidateKind.Carry);
        Assert.True(carry.TargetXZ.HasValue);
        Vector2 selfXZ = new(0f, -8f);
        Vector2 aim = XZ(carry.TargetXZ!.Value);
        Vector2 direction = SimMath.SafeNormalize(aim - selfXZ, Vector2.UnitX);
        Vector2 toGoal = Vector2.Normalize(new Vector2(Pitch.HalfLength, 0f) - selfXZ);

        Assert.True(Vector2.Dot(direction, toGoal) > 0.7f,
            $"the midfield carry must go goalward (dot {Vector2.Dot(direction, toGoal):F3}; " +
            "pre-fix the same fixture carried outward at 0.315)");
        Assert.True(aim.Y > selfXZ.Y + 0.5f,
            $"the midfield carry must not drift to the empty touchline: aim z {aim.Y:F2} vs carrier z " +
            $"{selfXZ.Y:F1} (pre-fix the same fixture went 4.0 m further out)");
        Assert.True(aim.X - selfXZ.X > 3f,
            $"the existing forward-progress behaviour is kept: the carry still gains ground " +
            $"({aim.X - selfXZ.X:F2} m)");
    }

    // =============================================================================================
    // AUD-P6 / Fix 5.3 — line-breaking preference is available in midfield, and still bounded.
    // =============================================================================================

    /// <summary>
    /// AUD-P6 / Fix 5.3: the floor drops from 12 m to 4 m. Fixture: the ball is at (0, 0) and the aim point is
    /// a clean central target at attack-relative projection <b>7 m</b>, with a visible opponent standing in
    /// the channel 2 m off the lane at projection 4 m — the ball gains ground and passes a line.
    ///
    /// Arithmetic:
    /// <code>
    ///   lane:   perpendicular 2.0 m against 1.1 + (4/7) x 2.4 = 2.471 m intercept radius
    ///           -> worst ratio 0.809; one body inside the congestion ratio adds
    ///           (0.85 - 0.809) / 0.85 = 0.048, factor 1 - 0.18 x 0.048 = 0.991 -> lane 0.802
    ///   pref  = LineBreakPreferenceMax 0.22 x centralShare(1 - 0/16 = 1) x
    ///           penetrationShare((7 - 4) / 10 = 0.30) = 0.066
    /// </code>
    /// Pre-fix the target-projection floor was 12 m, so the same 7 m aim point returned exactly 0.
    /// </summary>
    [Fact]
    public void AIB2_Candidate_MidfieldLineBreak_NowEarnsABoundedPreference_WhereItUsedToBeZero()
    {
        Vector2 ballXZ = new(0f, 0f);
        Vector2 targetXZ = new(7f, 0f);
        PerceivedEntity[] picture = [Opponent(14, 4f, 2f)];

        float lane = CandidateGenerator.EvaluateLaneClearance(ballXZ, targetXZ, picture, TeamId.Home);
        Assert.True(lane >= CandidateGenerator.LineBreakMinLaneClearance01,
            $"the fixture lane must clear the gate, got {lane:F3}");

        float preference = CandidateGenerator.LineBreakPreference(
            ballXZ, targetXZ, attackingDirectionX: 1f, picture, TeamId.Home,
            laneClearance01: lane, completionProbability01: 0.8f);

        float expected = CandidateGenerator.LineBreakPreferenceMax
                         * ((7f - 4f) / CandidateGenerator.LineBreakFullPenetrationMeters);
        Assert.True(preference > 0.05f,
            $"a central line-breaking ball at projection 7 m must now earn a preference, got {preference:F3} " +
            "(pre-fix the 12 m floor returned exactly 0)");
        Assert.Equal(expected, preference, precision: 4);
    }

    /// <summary>
    /// AUD-P6 / Fix 5.3, the floor half: lowering the gate must not turn a <i>square</i> or very short ball into
    /// a line break. The ball is at (−3, 0) and the aim point at projection 2 m — 5 m forward, but still below
    /// the new 4 m floor — so the preference is exactly 0 even with an opponent in the channel. Pre-fix the
    /// same 0 came from the old 12 m floor; this guard exists so lowering that floor to 4 m cannot admit a
    /// 2 m ball, and it is the assertion that would fail if the constant were dropped further.
    /// </summary>
    [Fact]
    public void AIB2_Candidate_AimPointBelowTheNewFloor_StillEarnsNoLineBreakPreference()
    {
        Vector2 ballXZ = new(-3f, 0f);
        Vector2 targetXZ = new(2f, 0f);
        PerceivedEntity[] picture = [Opponent(14, 0f, 1.5f)];

        Assert.True(targetXZ.X < CandidateGenerator.LineBreakMinTargetProjMeters,
            "the fixture must aim below the new floor");
        Assert.True(targetXZ.X > ballXZ.X + 2f, "the fixture is still a forward ball (the separate +2 m gate)");

        float preference = CandidateGenerator.LineBreakPreference(
            ballXZ, targetXZ, attackingDirectionX: 1f, picture, TeamId.Home,
            laneClearance01: 1f, completionProbability01: 0.8f);

        Assert.Equal(0f, preference);
    }

    /// <summary>
    /// AUD-P6 / Fix 5.3, the bound half: the change is only a lower gate — the preference keeps its existing
    /// bounded cap. A dead-centre aim point fully past the deepest beaten opponent earns exactly
    /// <see cref="CandidateGenerator.LineBreakPreferenceMax"/> (0.22, the wide switch's own nudge) and never
    /// more, whatever the penetration. Pre-fix the cap was the same 0.22; this pins that widening the gate to
    /// midfield did not also widen or rescale the bonus (a deep target already earned the cap before, so the
    /// assertion passes in both builds — it is the guard against over-reaching the fix).
    /// </summary>
    [Fact]
    public void AIB2_Candidate_MidfieldLineBreak_NeverExceedsItsExistingBound()
    {
        Vector2 ballXZ = new(0f, 0f);
        PerceivedEntity[] picture = [Opponent(14, 5f, 0f), Opponent(15, 12f, 3f)];

        float betweenTheLines = CandidateGenerator.LineBreakPreference(
            ballXZ, new Vector2(30f, 0f), 1f, picture, TeamId.Home, 0.9f, 0.8f);
        float deepBeyondTheLine = CandidateGenerator.LineBreakPreference(
            ballXZ, new Vector2(44f, 0f), 1f, picture, TeamId.Home, 0.9f, 0.8f);

        Assert.Equal(CandidateGenerator.LineBreakPreferenceMax, betweenTheLines, precision: 4);
        Assert.True(deepBeyondTheLine <= CandidateGenerator.LineBreakPreferenceMax,
            $"the line-breaking preference must stay inside its cap: {deepBeyondTheLine:F4}");
        Assert.Equal(0.22f, CandidateGenerator.LineBreakPreferenceMax, precision: 4);
    }

    // =============================================================================================
    // Fixtures and helpers.
    // =============================================================================================

    private static PerceivedEntity Opponent(int id, float x, float z, Vector3? velocity = null) =>
        Opponent(id, TacticalRole.CenterBack, x, z, velocity);

    private static PerceivedEntity Opponent(int id, TacticalRole role, float x, float z, Vector3? velocity = null) =>
        new(id, TeamId.Away, role, new Vector3(x, 0f, z), velocity ?? Vector3.Zero, 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };

    private static PerceivedEntity TeamMate(int id, float x, float z, Vector3? velocity = null) =>
        TeamMate(id, TacticalRole.CentralMidfielder, x, z, velocity);

    private static PerceivedEntity TeamMate(int id, TacticalRole role, float x, float z, Vector3? velocity = null) =>
        new(id, TeamId.Home, role, new Vector3(x, 0f, z), velocity ?? Vector3.Zero, 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };

    /// <summary>
    /// The carrier's observation for a fixture. Defaults are the neutral player and the neutral team profile,
    /// so a preference test reads the scoring rule rather than a role preset.
    /// </summary>
    private static FootballerObservation Observation(
        Vector2 ballXZ,
        Vector2 selfXZ,
        params PerceivedEntity[] visible) => new()
        {
            SelfId = CarrierId,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(ballXZ.X, 0.11f, ballXZ.Y),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = CarrierId,
            BallLastTouchedByTeam = TeamId.Home,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = CarrierId,
            TacticalAnchorPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 20f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = (int)ObservationTick,
            VisiblePlayers = visible,
            TeamProfile = new TeamTacticalProfile(),
            SelfTendencies = new PlayerTendencies(),
            SelfAttributes = new PlayerAttributes()
        };

    /// <summary>A communication context in which the given team-mate has announced a meeting region.</summary>
    private static MatchCommunicationContext AnnouncedRun(int runnerId, Vector2 region, int meetTick)
    {
        var communication = new MatchCommunicationContext();
        int publishedTick = (int)ObservationTick - RunAdvertisementBoard.CommunicationDelayTicks;
        var path = new RunPath(
            RunKind.InBehind,
            region - new Vector2(6f, 0f),
            region,
            MeetRadiusMeters: 5f,
            publishedTick,
            meetTick,
            meetTick + 60,
            HoldUntilTick: publishedTick,
            region - new Vector2(6f, 0f),
            0.85f);
        communication.Runs.Publish(
            TeamId.Home, runnerId, new RunIntent(path, TacticalJob.DepthRun, publishedTick), publishedTick);
        return communication;
    }

    private static Vector2 XZ(Vector3 point) => new(point.X, point.Z);

    private static UtilityCandidate Single(CandidateGenerator generator, string tag, int targetPlayerId)
    {
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            UtilityCandidate candidate = generator.Candidates[i];
            if (candidate.Key.Kind != CandidateKind.Pass) continue;
            if (candidate.Key.TargetPlayerId != targetPlayerId) continue;
            if (string.Equals(candidate.Key.TargetTag, tag, StringComparison.Ordinal)) return candidate;
        }

        throw new Xunit.Sdk.XunitException($"no '{tag}' pass to #{targetPlayerId}: {Describe(generator)}");
    }

    private static UtilityCandidate SingleByKind(CandidateGenerator generator, CandidateKind kind)
    {
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            if (generator.Candidates[i].Key.Kind == kind) return generator.Candidates[i];
        }

        throw new Xunit.Sdk.XunitException($"no {kind} candidate: {Describe(generator)}");
    }

    private static string Describe(CandidateGenerator generator)
    {
        var names = new List<string>();
        for (int i = 0; i < generator.CandidateCount; i++) names.Add(generator.Candidates[i].ActionName);
        return names.Count == 0 ? "(empty candidate set)" : string.Join(" | ", names);
    }

    // =============================================================================================
    // AUD-P3 / Fix 2 — the carrier-initiated pass into space (`ToSpace`), and AUD-P6 / Fix 5.1 — the
    // first touch release of the penetrative balls. The fixtures are the audit's Capture 4 shapes and the
    // arithmetic is quoted from the observed candidate terms; every assertion is placed so the pre-fix
    // candidate set (no space builder at all; no pass set at all inside the tempo delay) fails it.
    // =============================================================================================

    private const int SquareMateId = 10;
    private const int CoverId = 20;

    private static readonly PassPlanner Planner = new(Pitch);

    /// <summary>
    /// Combinations, the shallow search and switch recognition off, so a generated pass's preference is
    /// exactly <see cref="CandidateGenerator.PassPreference"/> (+ a line-break nudge when the fixture earns
    /// one) and the arithmetic in the comments is the scorer's whole answer.
    /// </summary>
    private static AiRuntimeConfig PlainPicture() => new()
    {
        EnableCombinations = false,
        EnableShallowSearch = false,
        EnableSwitchRecognition = false
    };

    private static float MaxUtility(CandidateGenerator generator)
    {
        float best = float.NegativeInfinity;
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            if (generator.Candidates[i].UtilityScore > best) best = generator.Candidates[i].UtilityScore;
        }

        return best;
    }

    /// <summary>
    /// AUD-P3 / Fix 2, the audit's Capture 4. Fixture: the carrier is at (0, 8) with the ball at (0.4, 8);
    /// a half-space team-mate (#9) stands still at (8, −12) with 14 m of empty onside grass in front of him
    /// and <b>no</b> run advertisement; a second, square/backward team-mate (#10) stands at (−10, 4). Two
    /// opponents stand far upfield at (45, −8)/(46, 6) — recorded but unable to reach any probe, so the
    /// picture is empty where it matters (lane clearance exactly 1.0 on both balls).
    ///
    /// Arithmetic (combinations/search/switch recognition off, neutral tendencies, no calls):
    /// <code>
    ///   ToSpace #9: aim = (8 + 14, −12) = (22, −12); progression 22 m
    ///               preference = 0.35 x clamp(22 / 20) = 0.350  (nobody in the aim channel -> no line break)
    ///               promise confidence = 0.5 + 0.5 x clamp(0.65 / 1) = 0.825
    ///                  (receiver reach to the probe ≈ 1.85 s against the 2.5 s race horizon -> margin 0.65 s)
    ///               completion ≈ 0.664; reception gain 1.048; loss 0.80
    ///               value = 0.664 x 1.048 − 0.336 x 0.80 + 0.350 = 0.777
    ///   ToFeet #10 (square/backward): progression −10 m -> preference −0.175; value = 0.570
    ///   delta = 0.207 scale units for the ball into the grass.
    /// </code>
    /// Pre-fix the same fixture generated only the two feet passes and the carry (no space builder exists),
    /// so this test's <c>Single(ToSpace)</c> threw — observed, not asserted.
    /// </summary>
    [Fact]
    public void AIB2_Space_AWideMateWithAnEmptyOnsideChannel_GetsACarrierInitiatedPass()
    {
        const float offsideLineProjX = 40f;
        Vector2 ballXZ = new(0.4f, 8f);
        PerceivedEntity spaceMate = TeamMate(MateId, 8f, -12f);
        PerceivedEntity squareMate = TeamMate(SquareMateId, -10f, 4f);
        FootballerObservation observation = Observation(
            ballXZ, new Vector2(0f, 8f), spaceMate, squareMate,
            Opponent(14, 45f, -8f), Opponent(15, 46f, 6f));
        var generator = new CandidateGenerator(Pitch, communication: null, PlainPicture()) { TimeOnBall = 0.8f };

        generator.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX);

        UtilityCandidate space = Single(generator, CandidateGenerator.SpacePassTag, MateId);
        UtilityCandidate squareFeet = Single(generator, "ToFeet", SquareMateId);
        Assert.True(space.TargetXZ.HasValue, "the space ball must publish the point it is aimed at");
        Vector2 aim = XZ(space.TargetXZ!.Value);

        // The promise target is the far probe, in the receiver's own lane, and inside the onside grass.
        Assert.Equal(8f + CandidateGenerator.SpaceProbeFarMeters, aim.X, precision: 1);
        Assert.Equal(-12f, aim.Y, precision: 1);
        Assert.True(aim.X * observation.AttackingDirectionX
                        <= offsideLineProjX - OffsideTactics.MarginMeters + 0.01f,
            $"the space ball is aimed at projection {aim.X:F2} m, past the onside bound " +
            $"{offsideLineProjX - OffsideTactics.MarginMeters:F2} m");

        // Beats the square/backward safe ball on both the preference it is scored with and the final value.
        Assert.True(space.Terms.Preference > squareFeet.Terms.Preference + 0.40f,
            $"the aim-point preference ({space.Terms.Preference:F3}) must out-price the square ball " +
            $"({squareFeet.Terms.Preference:F3}); pre-fix there was no such candidate at all");
        Assert.True(space.UtilityScore > squareFeet.UtilityScore + 0.10f,
            $"the pass into the grass ({space.UtilityScore:F3}) must beat the square feet pass " +
            $"({squareFeet.UtilityScore:F3}); pre-fix the only options were the two feet passes");
    }

    /// <summary>
    /// AUD-P3: the carrier chose the grass — no team-mate announced that meeting — so the new tag must not be
    /// priced as a delivery to a run (<c>IsDeliveredToRun</c>). Fixture: a live advertisement for #9 at
    /// (26, 8) and the carrier's observation. Choosing a <c>ToRun</c> ball claims the advertisement (claim
    /// count 1, the control that proves the machinery is live); choosing the new <c>ToSpace</c> ball claims
    /// nothing (count stays 0). Claiming the advertisement for a ball that was not played to its region would
    /// hold the runner in a place the ball never goes.
    /// </summary>
    [Fact]
    public void AIB2_Space_TheCarrierInitiatedBall_DoesNotClaimAnAnnouncedRun()
    {
        FootballerObservation observation = Observation(
            new Vector2(0.4f, 8f), new Vector2(0f, 8f), TeamMate(MateId, 12f, 8f));

        MatchCommunicationContext runContext = AnnouncedRun(MateId, new Vector2(26f, 8f), meetTick: 560);
        var runGenerator = new CandidateGenerator(Pitch, runContext, PlainPicture());
        runGenerator.NoteDecision(
            observation, new CandidateKey(CandidateKind.Pass, MateId, CandidateGenerator.RunPassTag));
        Assert.Equal(1, runContext.Runs.ClaimedCount);

        MatchCommunicationContext spaceContext = AnnouncedRun(MateId, new Vector2(26f, 8f), meetTick: 560);
        var spaceGenerator = new CandidateGenerator(Pitch, spaceContext, PlainPicture());
        spaceGenerator.NoteDecision(
            observation, new CandidateKey(CandidateKind.Pass, MateId, CandidateGenerator.SpacePassTag));
        Assert.Equal(0, spaceContext.Runs.ClaimedCount);
    }

    /// <summary>
    /// AUD-P3: contested grass is not space. The same Capture-4 picture with one defender at (24, −10): he is
    /// 2.8 m from the far probe (inside <see cref="CandidateGenerator.SpaceProbeCrowdRadiusMeters"/>, so that
    /// probe is <c>space-contested</c>) and he can be at the near probe before the receiver (the race margin is
    /// under <see cref="CandidateGenerator.SpaceRaceMarginSeconds"/>, so that probe is <c>space-race-lost</c>).
    /// Both probes are refused with a stated reason and no <c>ToSpace</c> candidate exists.
    ///
    /// Pre-fix the space builder does not run at all, so there are no such rejections — the rejection
    /// assertions below fail on the old code even though the "no candidate" assertion passes trivially.
    /// </summary>
    [Fact]
    public void AIB2_Space_AContestedProbe_IsRefused_WithAStatedReason()
    {
        Vector2 ballXZ = new(0.4f, 8f);
        PerceivedEntity spaceMate = TeamMate(MateId, 8f, -12f);
        FootballerObservation observation = Observation(
            ballXZ, new Vector2(0f, 8f), spaceMate,
            Opponent(CoverId, 24f, -10f), Opponent(21, 45f, -8f));
        var generator = new CandidateGenerator(Pitch, communication: null, PlainPicture()) { TimeOnBall = 0.8f };

        generator.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 40f);

        Assert.DoesNotContain(generator.Candidates,
            c => c.Key.Kind == CandidateKind.Pass && c.Key.TargetTag == CandidateGenerator.SpacePassTag);

        bool refusedAsCrowded = false;
        bool refusedByTheRace = false;
        for (int i = 0; i < generator.RejectionCount; i++)
        {
            CandidateRejection rejection = generator.GetRejection(i);
            if (rejection.TargetPlayerId != MateId) continue;
            if (rejection.Reason == CandidateGenerator.RejectionSpaceContested) refusedAsCrowded = true;
            if (rejection.Reason == CandidateGenerator.RejectionSpaceRaceLost) refusedByTheRace = true;
        }

        Assert.True(refusedAsCrowded,
            "the probe standing in the defender's grass must be refused as space-contested");
        Assert.True(refusedByTheRace,
            "the probe the receiver cannot reach before the defender must be refused by the race");
    }

    /// <summary>
    /// AUD-P3: the onside clamp. Fixture: ball at (0.4, 8), receiver at (18, 4), offside line projection 30 m
    /// — so the far probe's unclamped point is projection 18 + 14 = 32 m, a metre past the line. The builder
    /// keeps <see cref="OffsideTactics.MarginMeters"/> in reserve and plays the ball at projection
    /// <c>30 − 1 = 29</c>:
    /// <code>
    ///   unclamped far probe = (32, 4)  -> 32 > 29  -> clamped to (29, 4)
    ///   candidate target   = (29, 4)   (a ball at the line, never past it)
    /// </code>
    /// Pre-fix no space candidate existed, so this fails at <c>Single</c>.
    /// </summary>
    [Fact]
    public void AIB2_Space_AProbeBeyondTheOffsideLine_IsClampedInsideTheOnsideBound()
    {
        const float offsideLineProjX = 30f;
        const float receiverX = 18f;
        Assert.True(receiverX + CandidateGenerator.SpaceProbeFarMeters
                    > offsideLineProjX - OffsideTactics.MarginMeters,
            "the fixture's unclamped far probe must be beyond the onside bound");

        Vector2 ballXZ = new(0.4f, 8f);
        PerceivedEntity mate = TeamMate(MateId, receiverX, 4f);
        var generator = new CandidateGenerator(Pitch, communication: null, PlainPicture()) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(
            Observation(ballXZ, new Vector2(0f, 8f), mate), OpponentGoal, offsideLineProjX);

        UtilityCandidate space = Single(generator, CandidateGenerator.SpacePassTag, MateId);
        Vector2 aim = XZ(space.TargetXZ!.Value);
        float onsideBound = offsideLineProjX - OffsideTactics.MarginMeters;

        Assert.True(aim.X <= onsideBound + 0.01f,
            $"the space ball is aimed at projection {aim.X:F2} m, past the onside bound {onsideBound:F2} m");
        Assert.Equal(onsideBound, aim.X, precision: 1);
        Assert.Equal(4f, aim.Y, precision: 1);
    }

    /// <summary>
    /// AUD-P3: the ball is <b>firm</b>, not a roller — the previous audit's core complaint. Fixture: ball at
    /// (0.4, 8), receiver at (9, 4), offside line 20 m, so the far probe is clamped to (19, 4): a 19.03 m
    /// ball, the distance band where the crisp floor actually bites.
    /// <code>
    ///   MinCrispPower01(19.03) = 0.90   (lowest grid power whose 9.0 m/s crisp range, 18.03 / 19.81 m,
    ///                                    still reaches it) -> the candidate's command power is 0.90
    ///   table flight at 0.90 for 19.03 m = 1.400 s
    ///   a slow timed ball to the same point (wanted 3.0 s, no floor) is planned at power 0.35 with a
    ///   2.950 s flight — the firm ball is 1.55 s (53 %) earlier.
    /// </code>
    /// Both the floor and the slow comparison are read from the planner's public tables, so this test proves
    /// the *pace floor reached the command*, not a pace-dependent literal.
    /// </summary>
    [Fact]
    public void AIB2_Space_TheBallIsFirm_AtLeastTheCrispFloor_AndMuchEarlierThanASlowTimedBall()
    {
        Vector2 ballXZ = new(0.4f, 8f);
        PerceivedEntity mate = TeamMate(MateId, 9f, 4f);
        var generator = new CandidateGenerator(Pitch, communication: null, PlainPicture()) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(
            Observation(ballXZ, new Vector2(0f, 8f), mate), OpponentGoal, offsideLineProjX: 20f);

        UtilityCandidate space = Single(generator, CandidateGenerator.SpacePassTag, MateId);
        Vector2 aim = XZ(space.TargetXZ!.Value);
        float distance = Vector2.Distance(ballXZ, aim);
        float commandPower = space.Command.KickPower01;
        float crispFloor = Planner.MinCrispPower01(distance);

        Assert.True(commandPower >= crispFloor - 1e-3f,
            $"the space ball was struck at {commandPower:F2}; the crisp floor for {distance:F2} m is " +
            $"{crispFloor:F2} (pre-fix the region planner would pick the gentlest roller that reaches)");
        Assert.True(Planner.TryGetFlightTime(commandPower, distance, out float firmFlight),
            $"the planner's table has no flight for power {commandPower:F2} over {distance:F2} m");
        Assert.True(Planner.TryGetFlightTime(1f, distance, out _),
            "the fixture's distance must be inside the planner's full-power table");

        PassPlan slow = Planner.PlanGroundPassToRegion(ballXZ, aim, arriveInSeconds: 3.0f);
        Assert.True(slow.Feasible);
        Assert.True(Planner.TryGetFlightTime(slow.Power01, distance, out float slowFlight));
        Assert.True(slow.Power01 < commandPower,
            $"the slow timed ball ({slow.Power01:F2} power) must be gentler than the firm space ball " +
            $"({commandPower:F2} power)");
        Assert.True(firmFlight < slowFlight - 1.0f,
            $"the firm ball arrives in {firmFlight:F3} s where the slow timed ball takes {slowFlight:F3} s " +
            "(the audit's 'firm, not a roller' requirement)");
        Assert.True(firmFlight <= slowFlight * 0.6f,
            $"the firm ball's flight ({firmFlight:F3} s) must be materially shorter than the slow timed " +
            $"ball's ({slowFlight:F3} s)");
    }

    /// <summary>
    /// AUD-P3 / AUD-P2: the space candidate is scored on its <b>aim point</b>. Fixture is Capture 4 again
    /// (see the first test): no opponent stands in the aim point's channel, so the candidate's preference is
    /// exactly <see cref="CandidateGenerator.PassPreference"/> at the aim point:
    /// <code>
    ///   aim progress = 22 m          -> preference 0.35 x clamp(22 / 20) = 0.350
    ///   feet progress = 8 m          -> preference 0.35 x (8 / 20)     = 0.140
    ///   delta = ProgressBonus x (1.00 − 0.40) = 0.210
    /// </code>
    /// Pre-fix there was no <c>ToSpace</c> candidate at all, so the "aim-point pass" this test is about did
    /// not exist (observed: only the two feet passes and the carry).
    /// </summary>
    [Fact]
    public void AIB2_Space_TheCandidateIsScoredOnItsAimPoint_NotTheReceiversFeet()
    {
        Vector2 ballXZ = new(0.4f, 8f);
        PerceivedEntity spaceMate = TeamMate(MateId, 8f, -12f);
        PerceivedEntity squareMate = TeamMate(SquareMateId, -10f, 4f);
        FootballerObservation observation = Observation(
            ballXZ, new Vector2(0f, 8f), spaceMate, squareMate,
            Opponent(14, 45f, -8f), Opponent(15, 46f, 6f));
        var generator = new CandidateGenerator(Pitch, communication: null, PlainPicture()) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 40f);

        UtilityCandidate space = Single(generator, CandidateGenerator.SpacePassTag, MateId);
        UtilityCandidate feet = Single(generator, "ToFeet", MateId);
        Assert.True(space.TargetXZ.HasValue);

        Vector2 aim = XZ(space.TargetXZ!.Value);
        float aimProgress = (aim.X - observation.SelfPosition.X) * observation.AttackingDirectionX;
        float aimLane = CandidateGenerator.EvaluateLaneClearance(
            ballXZ, aim, observation.VisiblePlayers, observation.SelfTeam);
        Assert.Equal(1f, aimLane, precision: 4);

        float expectedPreference = CandidateGenerator.PassPreference(observation, aimProgress, aimLane);
        Assert.Equal(expectedPreference, space.Terms.Preference, precision: 3);
        Assert.True(space.Terms.Preference > feet.Terms.Preference + 0.15f,
            $"the aim-point preference ({space.Terms.Preference:F3}) must beat the feet-based one " +
            $"({feet.Terms.Preference:F3}) by the progression difference; before AUD-P2 both read the feet");
        Assert.Equal(space.UtilityScore, space.Terms.Value, precision: 3);
        Assert.True(space.Terms.Probability01 >= CandidateGenerator.MinViableDeliveryCompletion01,
            "a promised ball into grass is a delivery: it must clear the delivery floor, not the ordinary one");
    }

    // =============================================================================================
    // AUD-P6 / Fix 5.1 — penetrative balls are released inside the tempo delay; routine balls wait.
    // =============================================================================================

    /// <summary>
    /// AUD-P6 / Fix 5.1, the unlock. Fixture: the carrier at (0, 8) has held the ball for 0.20 s — past the
    /// scaled penetrative window (<c>0.45 × PenetrativeReleaseScale = 0.1575 s</c>) but well below the
    /// neutral-tempo routine trigger of 0.45 s — with a wide-open team-mate at (18, 4) and nobody else in the
    /// picture. The new <c>ToSpace</c> ball is evaluated inside the delay and wins the decision:
    /// <code>
    ///   ToSpace: aim (29, 4) (far probe clamped onside: line 30 − margin 1), value 1.507
    ///   carry  : value 1.228   (its early-touch bonus keeps it close, but the pass is the better ball here)
    /// </code>
    /// Pre-fix <c>AddPassCandidates</c> returned at the tempo gate, so this fixture generated <b>zero</b> pass
    /// candidates (observed: the carry alone, 1.228).
    /// </summary>
    [Fact]
    public void AIB2_FirstTouch_APenetrativeSpaceBall_IsEvaluated_AndWins()
    {
        Vector2 ballXZ = new(0.4f, 8f);
        PerceivedEntity mate = TeamMate(MateId, 18f, 4f);
        FootballerObservation observation = Observation(ballXZ, new Vector2(0f, 8f), mate);
        var generator = new CandidateGenerator(Pitch, communication: null, PlainPicture()) { TimeOnBall = 0.20f };

        generator.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 30f);

        UtilityCandidate space = Single(generator, CandidateGenerator.SpacePassTag, MateId);
        Assert.DoesNotContain(generator.Candidates,
            c => c.Key.Kind == CandidateKind.Pass && c.Key.TargetTag == "ToFeet");
        Assert.Equal(space.UtilityScore, MaxUtility(generator), precision: 3);
        Assert.True(space.UtilityScore > 1.3f,
            $"the first-touch space ball must be priced as the penetrative option it is, got {space.UtilityScore:F3}");
    }

    /// <summary>
    /// AUD-P6 / Fix 5.1, the promised-run half. Fixture: the carrier has held the ball for 0.20 s (inside the
    /// scaled penetrative window, below the 0.45 s routine trigger); team-mate
    /// #9 stands at (12, 8) with a live <see cref="RunAdvertisement"/> to (26, 8) at tick 610 (arrival
    /// 1.83 s), and a defender at (5, 4) covers the carrier's first carry probe (which is what makes the
    /// carry lose this decision, 1.157 against the run ball's 1.286).
    /// <code>
    ///   ToRun: promised flight matched to the advertised arrival -> p = the advertisement's own 0.85
    ///          (the promise timing term is exactly 1: the plan arrives when the run said it would)
    ///          value = 1.286, the highest candidate
    ///   carry: 1.157
    /// </code>
    /// The simulation's own driven delivery (which needs the same advertisement) is generated either way;
    /// pre-fix the tempo gate suppressed the <b>ground</b> ball entirely, so no <c>ToRun</c> existed (observed:
    /// only the advertised <c>Driven</c> delivery and the carry did).
    /// </summary>
    [Fact]
    public void AIB2_FirstTouch_APromisedRunBall_IsReleased_AndWins()
    {
        Vector2 ballXZ = new(0.4f, 8f);
        PerceivedEntity runner = TeamMate(MateId, 12f, 8f);
        FootballerObservation observation = Observation(
            ballXZ, new Vector2(0f, 8f), runner, Opponent(CoverId, 5f, 4f));
        var generator = new CandidateGenerator(Pitch, AnnouncedRun(MateId, new Vector2(26f, 8f), meetTick: 610),
            PlainPicture())
        {
            TimeOnBall = 0.20f
        };

        generator.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 40f);

        UtilityCandidate run = Single(generator, CandidateGenerator.RunPassTag, MateId);
        Assert.Equal(0.85f, run.Terms.Probability01, precision: 2);
        Assert.Equal(run.UtilityScore, MaxUtility(generator), precision: 3);
    }

    /// <summary>
    /// AUD-P6 / Fix 5.1 keeps the other half: the tempo delay still gates routine balls. The exact same
    /// fixture (carrier at (0, 8), a stationary square-ish team-mate at (0, 14) whose forward grass is
    /// occupied by a defender at (11, 14)) generates <b>no pass candidates</b> inside the delay, because it
    /// has no advertised run, no space probe that survives the crowd check and no line-breaking aim point —
    /// while the same picture one settling window later (0.80 s ≥ the neutral 0.45 s trigger) offers the
    /// side-foot ball again. Pre-fix both decisions emitted no pass, so the second assertion fails on the old
    /// code; that is the regression guard against "delete the tempo lever".
    /// <para>AUD-F18's team trait is why the penetrative release is <b>scaled</b> rather than zero-delay
    /// (<see cref="CandidateGenerator.PenetrativeReleaseScale"/>): the same fixture at 0.10 s, below the
    /// scaled 0.1575 s penetrative window, still generates nothing for either tempo.</para>
    /// </summary>
    [Fact]
    public void AIB2_FirstTouch_TheTempoDelay_StillGatesTheRoutinePass()
    {
        Vector2 ballXZ = new(0.4f, 8f);
        PerceivedEntity mate = TeamMate(MateId, 0f, 14f);
        FootballerObservation observation = Observation(
            ballXZ, new Vector2(0f, 8f), mate, Opponent(CoverId, 11f, 14f));

        // Neutral tempo: 0.45 − 0.30 x (0.55 − 0.55) = 0.45 s routine, 0.45 x 0.35 = 0.1575 s penetrative.
        const float firstTouchSeconds = 0.1f;
        const float settledSeconds = 0.8f;
        Assert.True(firstTouchSeconds < 0.45f && settledSeconds >= 0.45f);
        Assert.True(firstTouchSeconds < 0.45f * CandidateGenerator.PenetrativeReleaseScale,
            "the fixture's first-touch sample must sit below the scaled penetrative window too");

        var firstTouch = new CandidateGenerator(Pitch, communication: null, PlainPicture())
        {
            TimeOnBall = firstTouchSeconds
        };
        firstTouch.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 40f);
        Assert.DoesNotContain(firstTouch.Candidates, c => c.Key.Kind == CandidateKind.Pass);

        var settled = new CandidateGenerator(Pitch, communication: null, PlainPicture())
        {
            TimeOnBall = settledSeconds
        };
        settled.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 40f);
        Assert.Contains(settled.Candidates,
            c => c.Key.Kind == CandidateKind.Pass && c.Key.TargetTag == "ToFeet" &&
                 c.Key.TargetPlayerId == MateId);
    }

    /// <summary>
    /// AUD-P6 / Fix 5.1's existing behaviour, unchanged. A heavy press (a defender 2 m from the carrier) and a
    /// human call-for-pass both still bypass the tempo delay exactly as before: the carrier releases the ball
    /// on the first touch. Both assertions pass pre-fix by design — the restructure must not silently narrow
    /// those two release paths.
    /// </summary>
    [Fact]
    public void AIB2_FirstTouch_HeavyPressureAndAHumanCall_StillReleaseImmediately()
    {
        Vector2 ballXZ = new(0.4f, 8f);
        PerceivedEntity mate = TeamMate(MateId, 10f, 0f);

        var pressured = new CandidateGenerator(Pitch, communication: null, PlainPicture()) { TimeOnBall = 0.05f };
        pressured.GenerateOnBallCandidates(
            Observation(ballXZ, new Vector2(0f, 8f), mate, Opponent(CoverId, 2f, 8f)), OpponentGoal, 40f);
        Assert.Contains(pressured.Candidates,
            c => c.Key.Kind == CandidateKind.Pass && c.Key.TargetPlayerId == MateId);

        var communication = new MatchCommunicationContext();
        communication.RaiseCallForPass(TeamId.Home, MateId, durationSeconds: 2.5f, throughBall: false);
        var called = new CandidateGenerator(Pitch, communication, PlainPicture()) { TimeOnBall = 0.05f };
        called.GenerateOnBallCandidates(Observation(ballXZ, new Vector2(0f, 8f), mate), OpponentGoal, 40f);
        Assert.Contains(called.Candidates,
            c => c.Key.Kind == CandidateKind.Pass && c.Key.TargetPlayerId == MateId);
    }
}
