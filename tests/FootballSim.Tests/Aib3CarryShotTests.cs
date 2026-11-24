using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// The 2026-11-23 AI audit (docs/AUDIT_AI57_2026-11-23.md) — findings 1 and 5, package <b>AIB-07</b> /
/// <b>AIB-12</b>:
/// <list type="bullet">
/// <item><b>AIB-07a</b> — the finish was picked with an outfield-only lane (AIB-05) and no keeper term, so
/// when that lane was equally clear for all three goal targets strict <c>&gt;</c> always kept index 0
/// (<c>-cornerZ</c>) and the shot went at the side the keeper was covering (audit images 1–2).</item>
/// <item><b>AIB-07b</b> — the carry had no idea that open ground ahead produces a much better shot, so an
/// unpressed 1v1 striker shot from ~20 m instead of driving into the box (finding 1).</item>
/// <item><b>AIB-12a</b> — <c>CarryEarlyBonus</c> 0.25 over 0.70 s locked the first touch into dribbling;
/// it is now 0.10 over 0.35 s (finding 5).</item>
/// <item><b>AIB-12b</b> — the sprint gate dropped the carrier to a jog whenever any opponent came within
/// 5 m; it now follows the path ahead (finding 5).</item>
/// </list>
///
/// <para>Every assertion is written so the <i>pre-fix</i> code fails it, and the comment above the
/// assertion states the old formula and the arithmetic why. Fixtures use public members only (there is no
/// <c>InternalsVisibleTo</c> here): a carrier's candidate set is generated with
/// <see cref="CandidateGenerator.GenerateOnBallCandidates"/> and the resulting <see cref="UtilityCandidate"/>
/// scores/terms are read directly, the same route the decision path takes. Values quoted in comments are the
/// measured ones on this tree at Debug.</para>
/// </summary>
public sealed class Aib3CarryShotTests
{
    private static readonly PitchDimensions Pitch = new();
    private static readonly Vector3 GoalCentre = new(Pitch.HalfLength, 0f, 0f);
    private const int CarrierId = 8;
    private const int MateId = 9;
    private const int KeeperId = 50;
    private const int DefenderId = 20;

    /// <summary>AIB-05's corner inset, repeated here because the constant is internal (no test access).</summary>
    private const float CornerInsetMeters = 0.75f;

    /// <summary>Neutral tendencies and the neutral team profile make the preference arithmetic exact.</summary>
    private const float SettledTimeOnBall = 0.8f;

    // ---------------------------------------------------------------------------------------------
    // AIB-07a — the keeper-aware finish.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Audit finding 1, image 2: with the outfield lane clear for all three targets, every target scored the
    /// same and strict <c>&gt;</c> kept index 0 = <c>-cornerZ</c> whatever the keeper did, so a keeper covering
    /// the -Z side was shot at.
    ///
    /// Fixture: carrier at (32.5, 0), 20 m from the goal centre, only the keeper visible. At 20 m the
    /// keeper's reach is min(8.4 x 0.35 x 20/18, 2.40) = 2.40 m, so a keeper at z = -2.5 covers the -Z
    /// corner and the centre but not +Z. Pre-fix the target was exactly -2.91 and the first assertion fails.
    /// </summary>
    [Fact]
    public void AIB07a_TheShotAimsAwayFromTheKeeper()
    {
        CandidateGenerator keeperOnNegativeSide = Generate(Observation(
            new Vector2(32.5f, 0f), Keeper(new Vector2(51.5f, -2.5f))));
        UtilityCandidate shotAway = Single(keeperOnNegativeSide, CandidateKind.Shot, "keeper covers -Z");
        Assert.True(shotAway.TargetXZ.HasValue);
        Assert.True(shotAway.TargetXZ!.Value.Z > 1.5f,
            $"the keeper covers the -Z side, so the finish must go +Z; it aimed z = {shotAway.TargetXZ.Value.Z:F2} " +
            "(pre-fix: always -cornerZ = -2.91)");

        CandidateGenerator keeperOnPositiveSide = Generate(Observation(
            new Vector2(32.5f, 0f), Keeper(new Vector2(51.5f, 2.5f))));
        UtilityCandidate shotMirrored = Single(keeperOnPositiveSide, CandidateKind.Shot, "keeper covers +Z");
        Assert.True(shotMirrored.TargetXZ.HasValue);
        Assert.True(shotMirrored.TargetXZ!.Value.Z < -1.5f,
            $"the keeper covers the +Z side, so the finish must go -Z; it aimed z = " +
            $"{shotMirrored.TargetXZ.Value.Z:F2} (pre-fix: always -2.91)");
    }

    /// <summary>
    /// Pins the deterministic tie-break (a) and the far-post rule (b).
    ///
    /// (a) selfZ == 0 with a central keeper: both corners are equally (partly) covered, so the far-post-first
    /// order keeps <c>-cornerZ</c> — exactly the old default, so a behaviour that changes nothing about the
    /// picture stays bit-compatible.
    /// (b) A short flight from the -Z side: the far post is +Z. Over 5.8 m of flight the keeper's reach is
    /// min(8.4 x 0.35 x 0.32, 2.40) = 0.95 m, so neither corner is covered and the tie resolves to the far
    /// post, +2.91. Pre-fix the code always returned -2.91, so (b) fails on it.
    /// </summary>
    [Fact]
    public void AIB07a_ACentralKeeperKeepsTheDeterministicFarPostTieBreak()
    {
        float cornerZ = Pitch.HalfGoalWidth - CornerInsetMeters;

        UtilityCandidate central = Single(Generate(Observation(
            new Vector2(32.5f, 0f), Keeper(new Vector2(51.5f, 0f)))), CandidateKind.Shot, "central keeper");
        Assert.True(central.TargetXZ.HasValue);
        Assert.Equal(-cornerZ, central.TargetXZ!.Value.Z, 3);

        // selfZ = -3 < 0, so the far post is +Z; a 5.8 m shot gives the keeper under a metre of reach.
        UtilityCandidate shortFlight = Single(Generate(Observation(
            new Vector2(47.5f, -3f), Keeper(new Vector2(51.5f, 0f)))), CandidateKind.Shot, "short flight");
        Assert.True(shortFlight.TargetXZ.HasValue);
        Assert.Equal(cornerZ, shortFlight.TargetXZ!.Value.Z, 3);
    }

    /// <summary>
    /// The keeper term is <b>one</b> bounded expected-value reduction, read from perception, and it is not
    /// smuggled into the outfield lane.
    ///
    /// (1) The outfield-only lane is exactly 1.0 on all three rays with the keeper visible — the keeper is
    /// not a lane blocker — while the keeper-included metric reads him on the centre ray (his body sits on
    /// it) and near both corners. (2) Removing the keeper from <c>VisiblePlayers</c> raises the shot's gain
    /// by the coverage term only, and never by more than <c>KeeperCoveragePenalty</c>. (3) The terms remain
    /// a real decomposition: probability 1, loss 0, and <c>utility == gain + preference</c> exactly, with the
    /// keeper reduction inside the gain.
    ///
    /// Arithmetic for (2): 20 m flight 1.111 s, reach 2.40 m, corner gap 2.91 - 2.40 = 0.51 m, so coverage
    /// 1 - 0.51/0.60 = 0.15 and the reduction is 0.22 x 0.15 = 0.033.
    /// </summary>
    [Fact]
    public void AIB07a_TheKeeperTermIsOneBoundedReduction_AndTheOutfieldLaneStaysKeeperBlind()
    {
        Vector2 selfXZ = new(32.5f, 0f);
        PerceivedEntity keeper = Keeper(new Vector2(51.5f, 0f));
        float cornerZ = Pitch.HalfGoalWidth - CornerInsetMeters;
        Vector2 nearCorner = new(Pitch.HalfLength, -cornerZ);
        Vector2 centre = new(Pitch.HalfLength, 0f);
        Vector2 farCorner = new(Pitch.HalfLength, cornerZ);

        Assert.Equal(1f, CandidateGenerator.EvaluateLaneClearance(
            selfXZ, nearCorner, [keeper], TeamId.Home, excludeGoalkeeper: true), 5);
        Assert.Equal(1f, CandidateGenerator.EvaluateLaneClearance(
            selfXZ, centre, [keeper], TeamId.Home, excludeGoalkeeper: true), 5);
        Assert.Equal(1f, CandidateGenerator.EvaluateLaneClearance(
            selfXZ, farCorner, [keeper], TeamId.Home, excludeGoalkeeper: true), 5);

        float countedCentre = CandidateGenerator.EvaluateLaneClearance(
            selfXZ, centre, [keeper], TeamId.Home);
        float countedCorner = CandidateGenerator.EvaluateLaneClearance(
            selfXZ, nearCorner, [keeper], TeamId.Home);
        Assert.True(countedCentre < 0.1f,
            $"a keeper standing on the centre ray must still block the lane that counts him; got {countedCentre:F3}");
        Assert.True(countedCorner < 1f,
            $"a keeper off his line sits near both corner rays; got {countedCorner:F3}");

        UtilityCandidate guarded = Single(
            Generate(Observation(selfXZ, keeper)), CandidateKind.Shot, "keeper visible");
        UtilityCandidate open = Single(
            Generate(Observation(selfXZ)), CandidateKind.Shot, "no keeper visible");
        Assert.True(guarded.TargetXZ.HasValue && open.TargetXZ.HasValue);
        Assert.Equal(open.TargetXZ!.Value.Z, guarded.TargetXZ!.Value.Z, 3);

        float reduction = open.Terms.Gain - guarded.Terms.Gain;
        Assert.True(reduction > 0f,
            $"the keeper must be a cost when he covers the target; reduction was {reduction:F4}");
        Assert.True(reduction <= ActionValueScale.KeeperCoveragePenalty + 1e-4f,
            $"the keeper term must stay bounded by {ActionValueScale.KeeperCoveragePenalty:F2}; got {reduction:F4}");

        Assert.Equal(1f, guarded.Terms.Probability01, 5);
        Assert.Equal(0f, guarded.Terms.Loss, 5);
        Assert.Equal(guarded.UtilityScore, guarded.Terms.Gain + guarded.Terms.Preference, 4);
    }

    /// <summary>
    /// A stale keeper sighting is widened by its <c>PositionUncertaintyMeters</c> and the widening is capped,
    /// so remembering where the keeper was is neither treated as exact nor allowed to cover the whole mouth:
    /// fresh (uncertainty 0) charges 0.033, 1.2 m charges the full 0.22, and 5 m charges the same 0.22 as
    /// 1.2 m (the cap is 1.0 m). Pre-fix there was no keeper term at all, so every penalty here was 0.
    /// </summary>
    [Fact]
    public void AIB07a_AStaleKeeperSightingWidensCoverage_ButIsCapped()
    {
        Vector2 carrierXZ = new(32.5f, 0f);
        float baseGain = Single(Generate(Observation(carrierXZ)), CandidateKind.Shot, "no keeper").Terms.Gain;
        float fresh = baseGain - Single(
            Generate(Observation(carrierXZ, Keeper(new Vector2(51.5f, 0f), uncertainty: 0f))),
            CandidateKind.Shot, "fresh keeper").Terms.Gain;
        float stale = baseGain - Single(
            Generate(Observation(carrierXZ, Keeper(new Vector2(51.5f, 0f), uncertainty: 1.2f))),
            CandidateKind.Shot, "stale keeper").Terms.Gain;
        float ancient = baseGain - Single(
            Generate(Observation(carrierXZ, Keeper(new Vector2(51.5f, 0f), uncertainty: 5f))),
            CandidateKind.Shot, "ancient keeper").Terms.Gain;

        Assert.True(fresh > 0f && fresh <= ActionValueScale.KeeperCoveragePenalty + 1e-4f,
            $"a fresh central keeper charges a bounded coverage term; got {fresh:F4}");
        Assert.True(stale > fresh + 0.15f,
            $"a 1.2 m stale sighting must widen coverage materially ({stale:F4} vs fresh {fresh:F4})");
        Assert.True(stale <= ActionValueScale.KeeperCoveragePenalty + 1e-4f,
            $"the stale term must stay bounded by {ActionValueScale.KeeperCoveragePenalty:F2}; got {stale:F4}");
        Assert.Equal(stale, ancient, 4);
    }

    // ---------------------------------------------------------------------------------------------
    // AIB-07b — the advance prospect.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Audit finding 1: an unpressed 1v1 striker at ~20 m shot instead of carrying into the box. Audit §1
    /// measured the shot at 1.12–1.26 against a settled carry capped at 0.90. The fixture is deliberately the
    /// harshest version of that picture for the old code — a neutral player (no shooting preference) at
    /// exactly 20 m with the early bonus expired, where the pre-fix carry was 0.90 + 0.0787 progress = 0.9787
    /// against a 0.9808 shot, i.e. the old code shot by 0.002.
    ///
    /// Post-fix the advance term is lane-gated and ramped (2026-11-24 review correction, still AIB-07b): the
    /// gain is <c>0.50 + 0.40 × space + 0.40 × lane × ramp</c>, with space 1.0, an open lane (1.0) and the
    /// 4 m ramp at 0.875 for 20 m, so exactly <c>0.50 + 0.40 + 0.35 = 1.25</c> and the utility 1.3288 beats
    /// the keeper-aware shot (0.9478) by 0.381. The old hard step at 23.5 m is gone: at 22.5 m the ramp is
    /// 0.25 and the gain is exactly 1.00 (pre-fix the same picture paid the full 1.55). Inside the box the
    /// accounting flips: at 14 m the shot (1.8016) beats the carry (1.3003). Measured shot/carry crossover
    /// 16.8 m (was 15.1 m before the cap was lowered): at 16.5 m the shot still wins, at 17 m the drive does.
    /// </summary>
    [Fact]
    public void AIB07b_AnOpenOneOnOneAtTwentyMetres_DrivesInsteadOfShooting()
    {
        float rampAtTwenty = Math.Clamp(
            (ActionValueScale.ShotRangeMeters - 20f) / ActionValueScale.CarryAdvanceRampMeters, 0f, 1f);
        CandidateGenerator openAtTwenty = Generate(Observation(
            new Vector2(32.5f, 0f), Keeper(new Vector2(51.5f, 0f))));
        UtilityCandidate carry = Single(openAtTwenty, CandidateKind.Carry, "open 1v1 at 20 m");
        UtilityCandidate shot = Single(openAtTwenty, CandidateKind.Shot, "open 1v1 at 20 m");

        Assert.True(carry.UtilityScore > shot.UtilityScore + 0.30f,
            $"the open 20 m drive ({carry.UtilityScore:F3}) must beat the keeper-aware shot " +
            $"({shot.UtilityScore:F3}) by 0.30; pre-fix the carry was 0.9787 against the 0.9808 shot");
        Assert.Equal(
            ActionValueScale.CarryBaseValue + ActionValueScale.CarrySpaceBonus
                + ActionValueScale.CarryAdvanceValue * rampAtTwenty,
            carry.Terms.Gain, 3);

        // 22.5 m, inside ShotRangeMeters but in the ramp's fade: a quarter of the advance, not the old full
        // step. Pre-fix this picture paid the old full term (0.65) and the gain was 1.55.
        CandidateGenerator openAtTwentyTwo = Generate(Observation(
            new Vector2(30f, 0f), Keeper(new Vector2(51.5f, 0f))));
        UtilityCandidate fadingCarry = Single(openAtTwentyTwo, CandidateKind.Carry, "open 1v1 at 22.5 m");
        float rampAtTwentyTwo = Math.Clamp(
            (ActionValueScale.ShotRangeMeters - 22.5f) / ActionValueScale.CarryAdvanceRampMeters, 0f, 1f);
        Assert.Equal(
            ActionValueScale.CarryBaseValue + ActionValueScale.CarrySpaceBonus
                + ActionValueScale.CarryAdvanceValue * rampAtTwentyTwo,
            fadingCarry.Terms.Gain, 3);
        Assert.True(rampAtTwentyTwo > 0f && rampAtTwentyTwo < 1f, "the 22.5 m sample must sit inside the ramp");

        // 14 m: the shot the drive would reach is no longer worth more than the shot on offer now.
        CandidateGenerator openAtFourteen = Generate(Observation(
            new Vector2(38.5f, 0f), Keeper(new Vector2(51.5f, 0f))));
        UtilityCandidate closeShot = Single(openAtFourteen, CandidateKind.Shot, "open 1v1 at 14 m");
        UtilityCandidate closeCarry = Single(openAtFourteen, CandidateKind.Carry, "open 1v1 at 14 m");
        Assert.True(closeShot.UtilityScore > closeCarry.UtilityScore,
            $"inside the box the shot ({closeShot.UtilityScore:F3}) must beat the drive " +
            $"({closeCarry.UtilityScore:F3}); the measured crossover is ≈16.8 m");

        // The measured crossover, pinned either side: shot wins at 16.5 m, the drive wins at 17 m.
        CandidateGenerator atSixteenAndHalf = Generate(Observation(
            new Vector2(36f, 0f), Keeper(new Vector2(51.5f, 0f))));
        Assert.True(
            Single(atSixteenAndHalf, CandidateKind.Shot, "16.5 m").UtilityScore >
            Single(atSixteenAndHalf, CandidateKind.Carry, "16.5 m").UtilityScore,
            "16.5 m is inside the measured 16.8 m shot/carry crossover: the shot must win");

        CandidateGenerator atSeventeen = Generate(Observation(
            new Vector2(35.5f, 0f), Keeper(new Vector2(51.5f, 0f))));
        Assert.True(
            Single(atSeventeen, CandidateKind.Carry, "17 m").UtilityScore >
            Single(atSeventeen, CandidateKind.Shot, "17 m").UtilityScore,
            "17 m is outside the measured 16.8 m shot/carry crossover: the drive must win");
    }

    /// <summary>
    /// The advance prospect exists only while the ground ahead is open: a chaser 4.5 m behind at 8 m/s can
    /// arrive at the carrier in ~0.30 s (inside the 0.50 s contact window), so the prospect is withdrawn and
    /// the contact risk is charged instead — the shot (0.9478) then beats the carry (0.5993). The control is
    /// the same geometry with the same body receding at 8 m/s: nobody can arrive, the prospect is paid
    /// (gain 1.25 = 0.90 + the 0.35 advance at 20 m) and the carry wins again.
    ///
    /// The closing carry's gain (0.5205) stays below base + space (0.90), which the pre-fix carry could only
    /// reach through the 0.25 s early bonus; no duplicate pressure term is being paid.
    /// </summary>
    [Fact]
    public void AIB07b_AClosingChaser_WithdrawsTheAdvanceProspect()
    {
        float rampAtTwenty = Math.Clamp(
            (ActionValueScale.ShotRangeMeters - 20f) / ActionValueScale.CarryAdvanceRampMeters, 0f, 1f);
        float advanceAtTwenty = ActionValueScale.CarryAdvanceValue * rampAtTwenty;
        PerceivedEntity keeper = Keeper(new Vector2(51.5f, 0f));
        CandidateGenerator closing = Generate(Observation(
            new Vector2(32.5f, 0f), keeper, Opponent(new Vector2(28f, 0f), new Vector2(8f, 0f))));
        CandidateGenerator receding = Generate(Observation(
            new Vector2(32.5f, 0f), keeper, Opponent(new Vector2(28f, 0f), new Vector2(-8f, 0f))));

        UtilityCandidate closingCarry = Single(closing, CandidateKind.Carry, "closing chaser");
        UtilityCandidate closingShot = Single(closing, CandidateKind.Shot, "closing chaser");
        UtilityCandidate recedingCarry = Single(receding, CandidateKind.Carry, "receding chaser");

        Assert.Equal(
            ActionValueScale.CarryBaseValue + ActionValueScale.CarrySpaceBonus + advanceAtTwenty,
            recedingCarry.Terms.Gain, 4);
        Assert.True(recedingCarry.Terms.Gain - closingCarry.Terms.Gain > advanceAtTwenty,
            $"the open carry ({recedingCarry.Terms.Gain:F3}) must out-gain the closed one " +
            $"({closingCarry.Terms.Gain:F3}) by at least the advance term ({advanceAtTwenty:F2}) " +
            "plus the contact risk it replaces");
        Assert.True(closingCarry.Terms.Gain < ActionValueScale.CarryBaseValue + ActionValueScale.CarrySpaceBonus,
            $"a closed carry pays no advance prospect; its gain was {closingCarry.Terms.Gain:F3}");
        Assert.True(closingShot.UtilityScore > closingCarry.UtilityScore,
            $"with the ground closed the shot ({closingShot.UtilityScore:F3}) must beat the carry " +
            $"({closingCarry.UtilityScore:F3})");
    }

    /// <summary>
    /// 2026-11-24 owner-review correction (AIB-07b): the prospect must be paid on the <b>real</b> shooting
    /// lane from the carry target, not on a hardcoded 1.0. The block stands on the target→goal ray 11 m
    /// beyond the 4.5 m carry target — far outside the 0.60 s target-arrival window, so the arrival gate
    /// cannot be what zeroes the term — and the outfield lane clearance reads exactly 0, so the advance is
    /// exactly 0 and the carry's gain is exactly base + space (0.90; the 20 m open control gains 1.25).
    /// Pre-fix this picture paid the full 0.65 (the old code measured gain 1.55) and the assertion fails.
    /// The off-centre variant (block four metres nearer, not dead on the ray) keeps a real but reduced lane
    /// (0.53) and pays a real but reduced prospect (1.0108 against the 1.25 open control), so the term is
    /// lane-proportional rather than an on/off test.
    /// </summary>
    [Fact]
    public void AIB07b_ACompactBlockBetweenTheCarryTargetAndGoal_CollapsesTheAdvanceProspect()
    {
        const float openBlockDepth = 48f;   // on the ray, 11 m beyond the carry target at (37, 0)
        const float nearerBlockDepth = 45f; // four metres nearer, off the axis: a realistic box block
        PerceivedEntity keeper = Keeper(new Vector2(51.5f, 0f));
        Vector2 goalXZ = new(Pitch.HalfLength, 0f);

        CandidateGenerator open = Generate(Observation(new Vector2(32.5f, 0f), keeper));
        UtilityCandidate openCarry = Single(open, CandidateKind.Carry, "open control");
        PerceivedEntity[] onTheRay =
        [
            keeper,
            Opponent(new Vector2(openBlockDepth, 0f)),
            Opponent(new Vector2(openBlockDepth, 3f)),
            Opponent(new Vector2(openBlockDepth, -3f))
        ];
        CandidateGenerator blocked = Generate(Observation(new Vector2(32.5f, 0f), onTheRay));
        UtilityCandidate blockedCarry = Single(blocked, CandidateKind.Carry, "compact block");

        Assert.True(blockedCarry.TargetXZ.HasValue);
        Vector2 blockedTargetXZ = XZ(blockedCarry.TargetXZ!.Value);
        float laneToGoal = CandidateGenerator.EvaluateLaneClearance(
            blockedTargetXZ, goalXZ, onTheRay, TeamId.Home, excludeGoalkeeper: true);
        Assert.Equal(0f, laneToGoal, 4);
        Assert.Equal(ActionValueScale.CarryBaseValue + ActionValueScale.CarrySpaceBonus,
            blockedCarry.Terms.Gain, 4);
        Assert.True(openCarry.Terms.Gain - blockedCarry.Terms.Gain > 0.30f,
            $"an open lane must pay the advance and a blocked one nothing: open gain {openCarry.Terms.Gain:F3} " +
            $"vs blocked gain {blockedCarry.Terms.Gain:F3}");

        // Off-centre block: a real lane and a real (smaller) prospect, not an on/off gate.
        PerceivedEntity[] offCentre =
        [
            keeper,
            Opponent(new Vector2(nearerBlockDepth, 0f)),
            Opponent(new Vector2(nearerBlockDepth, 3f)),
            Opponent(new Vector2(nearerBlockDepth, -3f))
        ];
        CandidateGenerator partial = Generate(Observation(new Vector2(32.5f, 0f), offCentre));
        UtilityCandidate partialCarry = Single(partial, CandidateKind.Carry, "partial block");
        Assert.True(partialCarry.TargetXZ.HasValue);
        float partialLane = CandidateGenerator.EvaluateLaneClearance(
            XZ(partialCarry.TargetXZ!.Value), goalXZ, offCentre, TeamId.Home, excludeGoalkeeper: true);
        Assert.True(partialLane > 0.20f && partialLane < 0.80f,
            $"the off-centre block must leave a real, partial lane; it read {partialLane:F3}");
        Assert.True(partialCarry.Terms.Gain < openCarry.Terms.Gain - 0.20f,
            $"a partial block must shrink the prospect ({partialCarry.Terms.Gain:F3} vs open " +
            $"{openCarry.Terms.Gain:F3}); the pre-fix code paid the full term in both pictures");
        Assert.True(partialCarry.Terms.Gain > ActionValueScale.CarryBaseValue + ActionValueScale.CarrySpaceBonus,
            $"a half-clear lane must still pay something ({partialCarry.Terms.Gain:F3})");
    }

    /// <summary>
    /// The outer gate: at 30 m there is no shot at all (beyond <see cref="ActionValueScale.ShotRangeMeters"/>),
    /// so advancing cannot raise a shot value and the advance prospect is <b>exactly</b> zero — the carry's
    /// gain is exactly base + space (0.90), and the ramp is exactly 0 rather than the old hard gate's mirror
    /// image. A clean 12 m pass to a team-mate (1.7505) then beats the carry (0.9787), which is the ordering
    /// audit finding 5 asked for. Pre-fix the same pass also beat the carry, but the fix must not make the
    /// carry beat it by inventing value at a distance it cannot shoot from.
    /// </summary>
    [Fact]
    public void AIB07b_NoShotAvailable_PaysNoAdvanceProspect_AndACleanPassWins()
    {
        CandidateGenerator generator = Generate(Observation(
            new Vector2(22.5f, 0f), Keeper(new Vector2(51.5f, 0f)), Mate(new Vector2(34.5f, 0f))));

        UtilityCandidate carry = Single(generator, CandidateKind.Carry, "30 m from goal");
        Assert.Equal(ActionValueScale.CarryBaseValue + ActionValueScale.CarrySpaceBonus, carry.Terms.Gain, 4);

        bool hasShot = false;
        for (int index = 0; index < generator.CandidateCount; index++)
            hasShot |= generator.Candidates[index].Key.Kind == CandidateKind.Shot;
        Assert.False(hasShot, "a 30 m carrier has no shot candidate");

        UtilityCandidate pass = PassTo(generator, MateId, "ToFeet");
        Assert.True(pass.UtilityScore > carry.UtilityScore,
            $"the clean 12 m pass ({pass.UtilityScore:F3}) must beat a carry with no shot ahead " +
            $"({carry.UtilityScore:F3})");
    }

    // ---------------------------------------------------------------------------------------------
    // AIB-12a — the early-carry bonus.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Audit finding 5: <c>CarryEarlyBonus = 0.25</c> over <c>CarryEarlySeconds = 0.70</c> made the first
    /// touch worth 1.37 and outbid every midfield pass for 0.7 s; the audit's recommendation was
    /// 0.10–0.12 / 0.35–0.40.
    ///
    /// The measured gap between a first touch (0.1 s) and a settled touch (0.6 s) on the same 20 m open 1v1
    /// is exactly the bonus: 1.35 - 1.25 = 0.10 (pre-fix: 0.25, and 0.6 s was still inside the 0.70 s
    /// window so both touches paid it and the old gap at 0.6 s was 0). The window is asserted through the
    /// 0.6 s sample.
    /// </summary>
    [Fact]
    public void AIB12a_TheEarlyCarryBonus_IsBoundedAndExpires()
    {
        Assert.Equal(0.10f, ActionValueScale.CarryEarlyBonus, 3);
        Assert.Equal(0.35f, ActionValueScale.CarryEarlySeconds, 3);

        CandidateGenerator early = Generate(
            Observation(new Vector2(32.5f, 0f), Keeper(new Vector2(51.5f, 0f))), timeOnBall: 0.1f);
        CandidateGenerator settled = Generate(
            Observation(new Vector2(32.5f, 0f), Keeper(new Vector2(51.5f, 0f))), timeOnBall: 0.6f);

        float rampAtTwenty = Math.Clamp(
            (ActionValueScale.ShotRangeMeters - 20f) / ActionValueScale.CarryAdvanceRampMeters, 0f, 1f);
        float earlyGain = Single(early, CandidateKind.Carry, "early touch").Terms.Gain;
        float settledGain = Single(settled, CandidateKind.Carry, "settled touch").Terms.Gain;
        Assert.Equal(ActionValueScale.CarryEarlyBonus, earlyGain - settledGain, 3);
        // Full-space early carry with the advance term: 0.50 + 0.40 + 0.35 + 0.10 = 1.35 (the advance is
        // 0.40 x the 0.875 range ramp at 20 m since the 2026-11-24 review correction). Without the advance
        // term (the audit's midfield decomposition) it is 0.50 + 0.40 + 0.10 ≈ 1.00 before the
        // progress/dribble preferences — the audit's "0.95-1.05 after the cut" omitted those two terms and
        // would be ≈1.22 for its own 1.37 decomposition, which is why the constants are the low end rather
        // than a tuned total.
        Assert.Equal(
            ActionValueScale.CarryBaseValue + ActionValueScale.CarrySpaceBonus
                + ActionValueScale.CarryAdvanceValue * rampAtTwenty + ActionValueScale.CarryEarlyBonus,
            earlyGain, 3);
    }

    // ---------------------------------------------------------------------------------------------
    // AIB-12b — the sprint gate.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Audit finding 5: <c>sprint = bestSpace &gt; 0.70 &amp;&amp; nearestOppDist &gt; 5.0</c> throttled the
    /// carrier to a jog the instant any opponent was within 5 m, while the presser sprinted unconditionally
    /// (8.4 m/s against 6.3 m/s). The gate now follows the path ahead.
    ///
    /// Fixtures (own half, so the goalward carry weight is exactly zero):
    /// (a) a chaser 4 m directly behind at 8 m/s: the path ahead is open, so the carrier sprints — pre-fix
    /// <c>4 &gt; 5</c> is false and the first assertion fails;
    /// (b) five bodies behind (2.41–2.57 m) plus two bodies at rel (1.0, ±2.2) — inside the 6 m x 2.2 m
    /// corridor of the chosen forward direction: no sprint, because the ground it would run through is
    /// occupied;
    /// (c) the same two bodies stepping laterally OUT of the corridor at 6 m/s (each away from the axis on
    /// its own side): the path is no longer blocked and the carrier sprints again;
    /// (d) a body 1.5 m ahead: inside the contact floor, no sprint (a sprint would have to stop immediately);
    /// (e) <b>review correction</b>: the same two corridor bodies <b>charging head-on at the carrier</b>
    /// (6 m/s in the carry direction's negative) must NOT be exempt. The first cut of
    /// <c>IsCarryPathBlocked</c> exempted exactly that case with a backwards sign, so the carrier sprinted
    /// into a tackle; this assertion fails on that code.
    /// </summary>
    [Fact]
    public void AIB12b_TheSprintGate_FollowsThePathAhead_NotTheNearestBody()
    {
        CandidateGenerator chaserBehind = Generate(Observation(
            new Vector2(0f, 0f), Opponent(new Vector2(-4f, 0f), new Vector2(8f, 0f))));
        Assert.True(Single(chaserBehind, CandidateKind.Carry, "chaser behind").Command.Sprint,
            "a chaser 4 m behind must not stop the carrier sprinting into open ground (pre-fix: jog)");

        CandidateGenerator corridorHeld = Generate(Observation(new Vector2(-30f, 0f), CorridorFixture()));
        Assert.False(Single(corridorHeld, CandidateKind.Carry, "corridor held").Command.Sprint,
            "a body in the 6 m x 2.2 m corridor ahead must stop the sprint");

        CandidateGenerator corridorVacated = Generate(Observation(
            new Vector2(-30f, 0f), CorridorFixture(steppingOut: true)));
        Assert.True(Single(corridorVacated, CandidateKind.Carry, "corridor vacated").Command.Sprint,
            "bodies stepping laterally out of the ground ahead do not block the sprint");

        CandidateGenerator inContact = Generate(Observation(
            new Vector2(-30f, 0f), Opponent(new Vector2(-28.5f, 0f))));
        Assert.False(Single(inContact, CandidateKind.Carry, "contact floor").Command.Sprint,
            "a body inside tackle reach plus a step is a contact, not a sprint");

        // (e) Review correction: a head-on charge is the most blocking case there is. The pre-fix helper
        // skipped it (`dot(velocity, direction) <= -1`), so this assertion failed on that code.
        CandidateGenerator headOn = Generate(Observation(
            new Vector2(-30f, 0f), CorridorFixture(chargingAtCarrier: true)));
        Assert.False(Single(headOn, CandidateKind.Carry, "head-on charge").Command.Sprint,
            "an opponent running straight at the carrier must stop the sprint — the sign of the escape " +
            "test must never exempt along-axis motion towards the carrier");
    }

    // ---------------------------------------------------------------------------------------------
    // The pre-fix defects, one per finding.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One test that states the pre-fix formula of each finding and proves the shipped behaviour differs —
    /// the pre-fix expression is re-derived from the same public constants/inputs the old code used, so this
    /// test fails on the pre-fix code for all three reasons it documents.
    ///
    /// <b>AIB-07a</b>: every goal target tied (outfield-only lane), so the old finish was exactly
    /// <c>-cornerZ</c>. With the keeper standing at z = -2.5 the shipped finish is +2.91, 5.4 m from the
    /// keeper instead of 0.4 m.
    /// <b>AIB-07b</b>: the old carry gain at 20 m was base + space x 1.0 = 0.90 (the 0.70 s early bonus has
    /// expired at 0.8 s) and the old shot was <c>ShotValue(20, 1, mouth)</c> = 0.9808 with no keeper term, so
    /// the old code shot by 0.002. The shipped carry gain is exactly one advance term higher — 0.35, which is
    /// <see cref="ActionValueScale.CarryAdvanceValue"/> (0.40) times the 0.875 range ramp at 20 m; the value
    /// was 0.65 before the 2026-11-24 review correction and is quoted at its shipped value here.
    /// <b>AIB-12b</b>: the old gate required <c>nearestOppDist &gt; 5.0</c>; a chaser 4 m behind made the old
    /// predicate false while the shipped carrier sprints.
    /// </summary>
    [Fact]
    public void AIB07_12_ThePreFixFormulas_ProduceTheAuditedDefects()
    {
        // AIB-07a — old aim was always -cornerZ, whatever the keeper covered.
        float cornerZ = Pitch.HalfGoalWidth - CornerInsetMeters;
        const float keeperZ = -2.5f;
        UtilityCandidate shot = Single(Generate(Observation(
            new Vector2(32.5f, 0f), Keeper(new Vector2(51.5f, keeperZ)))), CandidateKind.Shot, "pre-fix 07a");
        Assert.True(shot.TargetXZ.HasValue);
        float shippedGap = MathF.Abs(shot.TargetXZ!.Value.Z - keeperZ);
        float preFixGap = MathF.Abs(-cornerZ - keeperZ);
        Assert.True(shippedGap > preFixGap + 2.0f,
            $"the old finish sat {preFixGap:F2} m from the keeper; the shipped one is {shippedGap:F2} m away");

        // AIB-07b — old carry had no advance term; the old shot had no keeper term.
        CandidateGenerator openAtTwenty = Generate(Observation(
            new Vector2(32.5f, 0f), Keeper(new Vector2(51.5f, 0f))));
        float preFixCarryGain = ActionValueScale.CarryBaseValue + ActionValueScale.CarrySpaceBonus * 1f;
        float goalMouthAngle = 2f * MathF.Atan(Pitch.HalfGoalWidth / 20f) * (180f / MathF.PI);
        float preFixShotValue = ActionValueScale.ShotValue(20f, 1f, goalMouthAngle);
        float shippedCarryGain = Single(openAtTwenty, CandidateKind.Carry, "pre-fix 07b").Terms.Gain;
        Assert.True(preFixShotValue > preFixCarryGain,
            $"the audit's defect must reproduce: the old shot {preFixShotValue:F3} beat the old carry " +
            $"{preFixCarryGain:F3} at 20 m");
        float rampAtTwenty = Math.Clamp(
            (ActionValueScale.ShotRangeMeters - 20f) / ActionValueScale.CarryAdvanceRampMeters, 0f, 1f);
        Assert.Equal(ActionValueScale.CarryAdvanceValue * rampAtTwenty, shippedCarryGain - preFixCarryGain, 4);

        // AIB-12b — old sprint predicate on the same fixture.
        const float chaserDistance = 4f;
        CandidateGenerator chaserBehind = Generate(Observation(
            new Vector2(0f, 0f), Opponent(new Vector2(-chaserDistance, 0f), new Vector2(8f, 0f))));
        bool preFixSprint = chaserDistance > 5.0f; // the old `nearestOppDist > 5.0` half of the gate
        Assert.False(preFixSprint, "the old static ring would have forced a jog at 4 m");
        Assert.True(Single(chaserBehind, CandidateKind.Carry, "pre-fix 12b").Command.Sprint,
            "the shipped carrier sprints away from a chaser behind him");
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures and helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Own-half sprint fixture: five bodies behind the carrier (2.41–2.57 m, so the contact floor of 2.40 is
    /// not what decides) plus two bodies at rel (1.0, ±2.2) — inside the 6 m x 2.2 m corridor of the chosen
    /// forward direction. <paramref name="steppingOut"/> gives the corridor pair the believed velocity that
    /// really vacates that ground (each stepping away from the axis on its own side);
    /// <paramref name="chargingAtCarrier"/> gives them a head-on run instead, which is the opposite of
    /// vacating and must stop the sprint (review correction).
    /// </summary>
    private static PerceivedEntity[] CorridorFixture(
        bool steppingOut = false,
        bool chargingAtCarrier = false)
    {
        // Each corridor body steps away from the axis on its own side (z = +2.2 moves +Z, z = -2.2 moves -Z).
        Vector2 outPositiveZ = steppingOut ? new Vector2(0f, 6f) : Vector2.Zero;
        Vector2 outNegativeZ = steppingOut ? new Vector2(0f, -6f) : Vector2.Zero;
        Vector2 headOn = chargingAtCarrier ? new Vector2(-6f, 0f) : Vector2.Zero;
        return
        [
            Opponent(new Vector2(-32.41f, 0f)),
            Opponent(new Vector2(-32.41f, 0.4f)),
            Opponent(new Vector2(-32.41f, -0.4f)),
            Opponent(new Vector2(-32.41f, 0.9f)),
            Opponent(new Vector2(-32.41f, -0.9f)),
            Opponent(new Vector2(-29f, 2.2f), outPositiveZ + headOn),
            Opponent(new Vector2(-29f, -2.2f), outNegativeZ + headOn)
        ];
    }

    private static CandidateGenerator Generate(
        FootballerObservation observation,
        float timeOnBall = SettledTimeOnBall,
        float offsideLineProjX = 45f)
    {
        var generator = new CandidateGenerator(Pitch, communication: null) { TimeOnBall = timeOnBall };
        generator.GenerateOnBallCandidates(observation, GoalCentre, offsideLineProjX);
        return generator;
    }

    /// <summary>The carrier's observation: neutral player, neutral team, at <paramref name="carrierXZ"/>.</summary>
    private static FootballerObservation Observation(Vector2 carrierXZ, params PerceivedEntity[] visible) => new()
    {
        SelfId = CarrierId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CentralMidfielder,
        SelfPosition = new Vector3(carrierXZ.X, 0f, carrierXZ.Y),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = new Vector3(carrierXZ.X + 0.4f, 0.11f, carrierXZ.Y),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = CarrierId,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = CarrierId,
        TacticalAnchorPosition = new Vector3(carrierXZ.X, 0f, carrierXZ.Y),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 20f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = 500,
        VisiblePlayers = visible,
        TeamProfile = new TeamTacticalProfile(),
        SelfTendencies = new PlayerTendencies(),
        SelfAttributes = new PlayerAttributes()
    };

    private static PerceivedEntity Mate(Vector2 position, Vector2 velocity = default) => new(
        MateId, TeamId.Home, TacticalRole.CentralMidfielder,
        new Vector3(position.X, 0f, position.Y),
        new Vector3(velocity.X, 0f, velocity.Y),
        1f)
    {
        Capability = CapabilityEstimate.Generic
    };

    private static PerceivedEntity Opponent(Vector2 position, Vector2 velocity = default) => new(
        DefenderId, TeamId.Away, TacticalRole.CenterBack,
        new Vector3(position.X, 0f, position.Y),
        new Vector3(velocity.X, 0f, velocity.Y),
        0.95f)
    {
        Capability = CapabilityEstimate.Generic
    };

    private static PerceivedEntity Keeper(Vector2 position, float uncertainty = 0f) => new(
        KeeperId, TeamId.Away, TacticalRole.Goalkeeper,
        new Vector3(position.X, 0f, position.Y),
        Vector3.Zero,
        0.95f)
    {
        Capability = CapabilityEstimate.Generic,
        PositionUncertaintyMeters = uncertainty
    };

    private static Vector2 XZ(Vector3 point) => new(point.X, point.Z);

    private static UtilityCandidate Single(CandidateGenerator generator, CandidateKind kind, string context)
    {
        for (int index = 0; index < generator.CandidateCount; index++)
        {
            UtilityCandidate candidate = generator.Candidates[index];
            if (candidate.Key.Kind == kind) return candidate;
        }

        throw new Xunit.Sdk.XunitException($"no {kind} candidate ({context}): {Describe(generator)}");
    }

    private static UtilityCandidate PassTo(CandidateGenerator generator, int playerId, string tag)
    {
        for (int index = 0; index < generator.CandidateCount; index++)
        {
            UtilityCandidate candidate = generator.Candidates[index];
            if (candidate.Key.Kind == CandidateKind.Pass &&
                candidate.Key.TargetPlayerId == playerId &&
                string.Equals(candidate.Key.TargetTag, tag, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new Xunit.Sdk.XunitException($"no \"{tag}\" pass to #{playerId}: {Describe(generator)}");
    }

    private static string Describe(CandidateGenerator generator)
    {
        var names = new List<string>();
        for (int index = 0; index < generator.CandidateCount; index++)
            names.Add($"{generator.Candidates[index].Identity}={generator.Candidates[index].UtilityScore:F3}");
        return names.Count == 0 ? "(empty candidate set)" : string.Join(" | ", names);
    }
}
