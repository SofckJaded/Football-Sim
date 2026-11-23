using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Physics;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// AIB-2026-11-22 audit — the carrier-pressure, shot-value and recycle-inversion packages
/// (<b>AIB-01</b>, <b>AIB-05</b>, <b>AIB-06a</b>), the three highest-gameplay-impact findings in the
/// owner's report.
///
/// Every assertion is written so that the <i>pre-fix</i> formula fails it, and the comment above each
/// assertion states that formula and the arithmetic why. The fixtures are built on public members only
/// (there is no <c>InternalsVisibleTo</c> in this repository): a carrier's candidate set is generated with
/// <see cref="CandidateGenerator.GenerateOnBallCandidates"/> and the resulting <see cref="UtilityCandidate"/>
/// scores/terms are read directly, which is the same route the decision path takes.
/// </summary>
public sealed class AibCarrierShotRecycleTests
{
    private const int CarrierId = 8;
    private const int MateId = 9;
    private const int DeepMateId = 10;
    private const int BehindMateId = 11;
    private const int DefenderId = 12;
    private const int KeeperId = 50;

    private static readonly PitchDimensions Pitch = new();
    private static readonly Vector3 GoalCentre = new(Pitch.HalfLength, 0f, 0f);
    private static readonly TeamCallContext ManOnCall = new(TeamCallKind.ManOn, 7, 0, TeamCallKind.None);

    // ---------------------------------------------------------------------------------------------
    // AIB-06a — the recycle inversion (audit report sections 5 and 6).
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Report section 6.1, the inversion itself: <c>AddRecyclePass</c> hardcoded
    /// <c>new ValueTerms(..., 0f)</c>, so a recycle dodged <c>PassPreference</c> while a ranked backward
    /// target paid the -0.35 progress preference. A team-mate 26 m behind the ball therefore had +0.35
    /// <i>higher</i> preference than one 10 m behind who made the pass-target pre-rank.
    ///
    /// Fixture: a forward ball is available (so the drop is a choice), and <c>MaxPassTargets = 1</c> keeps
    /// the two backward team-mates out of the ordinary pass set so they can only appear as recycles.
    /// </summary>
    [Fact]
    public void AIB06a_ARecyclePass_PaysTheBackwardPassPreference()
    {
        FootballerObservation observation = Observation(
            new Vector2(0f, 0f),
            Mate(MateId, new Vector2(12f, 0f)),
            Mate(BehindMateId, new Vector2(-12f, 6f)),
            Mate(DeepMateId, new Vector2(-26f, -8f)));

        CandidateGenerator generator = Generate(
            observation, timeOnBall: 0.8f, new AiRuntimeConfig { MaxPassTargets = 1 });

        UtilityCandidate nearRecycle = PassTo(generator, BehindMateId, "Recycle");
        UtilityCandidate deepRecycle = PassTo(generator, DeepMateId, "Recycle");

        // Pre-fix both preferences were exactly 0.00. Now the near recycle pays
        // ProgressBonus x (-12 / 20) = -0.21 plus BackwardTerritoryCost = -0.12 because a forward pass
        // exists in the set: -0.33. (Lane clearance is 1.0 — no opponents — so the risk terms are zero.)
        Assert.Equal(-0.33f, nearRecycle.Terms.Preference, precision: 2);

        // The deeper ball loses ground further, so it must carry the larger penalty. Pre-fix the two were
        // identical (both 0.00), which is exactly the "deeper is free" inversion.
        Assert.True(deepRecycle.Terms.Preference < nearRecycle.Terms.Preference - 0.10f,
            $"the 26 m backward recycle ({deepRecycle.Terms.Preference:F2}) must lose more than the " +
            $"12 m one ({nearRecycle.Terms.Preference:F2})");
    }

    /// <summary>
    /// Report section 2.B.1: <c>AddRecyclePass</c> called <c>PlanGroundPass</c> without
    /// <c>maxLeadMeters</c>, so it silently used <see cref="PassPlanner.DefaultMaxLeadMeters"/> (12 m) while
    /// every ordinary feet pass bounded the lead at
    /// <see cref="CandidateGenerator.FeetPassMaxLeadMeters"/> (6 m).
    ///
    /// The receiver here is 12 m behind the ball and jogging away at 4 m/s. Pre-fix the planner solved that
    /// meeting with a 9.53 m lead (a ball rolled into empty grass behind a man who is not going to keep
    /// running — the report's "12 m backward lead to nowhere"); with the feet bound the same fixture is
    /// planned with a 5.87 m lead. The assertion fails on the old 12 m default.
    /// </summary>
    [Fact]
    public void AIB06a_ARecyclePass_NeverLeadsBeyondTheFeetPassBound()
    {
        Vector2 matePosition = new(-12f, 0f);
        FootballerObservation observation = Observation(
            new Vector2(0f, 0f),
            Mate(MateId, new Vector2(12f, 0f)),
            Mate(DeepMateId, matePosition, new Vector2(-4f, 0f)));

        CandidateGenerator generator = Generate(
            observation, timeOnBall: 0.8f, new AiRuntimeConfig { MaxPassTargets = 1 });

        UtilityCandidate recycle = PassTo(generator, DeepMateId, "Recycle");
        Assert.True(recycle.TargetXZ.HasValue);
        Vector2 aim = new(recycle.TargetXZ!.Value.X, recycle.TargetXZ.Value.Z);
        float lead = Vector2.Distance(matePosition, aim);
        Assert.True(lead <= CandidateGenerator.FeetPassMaxLeadMeters + 0.05f,
            $"the recycle aimed {lead:F2} m beyond a receiver jogging away; the feet bound is " +
            $"{CandidateGenerator.FeetPassMaxLeadMeters:F1} m (pre-fix: a 9.5 m lead from the 12 m default)");
    }

    /// <summary>
    /// Report section 6.4: <c>CallBouncePassBonus</c> was added to <b>every</b> pass while a man-on call was
    /// live, so the highest-completion ball — the deepest, clearest backward one — captured it most reliably.
    /// The bonus is now scoped to the short first-time return it is about.
    ///
    /// The two observations differ only in <see cref="FootballerObservation.Calls"/>, so the utility delta of
    /// each pass is exactly the preference the call paid it. Pre-fix both deltas were +0.12.
    /// </summary>
    [Fact]
    public void AIB06a_ALongBackwardBall_DoesNotCollectTheManOnBounceBonus()
    {
        PerceivedEntity shortMate = Mate(MateId, new Vector2(8f, 0f));
        PerceivedEntity longBackwardMate = Mate(DeepMateId, new Vector2(-20f, 0f));

        CandidateGenerator control = Generate(
            Observation(new Vector2(0f, 0f), shortMate, longBackwardMate), timeOnBall: 0.8f);
        CandidateGenerator treated = Generate(
            Observation(new Vector2(0f, 0f), ManOnCall, shortMate, longBackwardMate), timeOnBall: 0.8f);

        float shortForwardDelta = PassTo(treated, MateId, "ToFeet").UtilityScore -
                                  PassTo(control, MateId, "ToFeet").UtilityScore;
        float longBackwardDelta = PassTo(treated, DeepMateId, "ToFeet").UtilityScore -
                                  PassTo(control, DeepMateId, "ToFeet").UtilityScore;

        Assert.Equal(ActionValueScale.CallBouncePassBonus, shortForwardDelta, precision: 3);
        // Pre-fix this was also +0.12: the call paid a 20.4 m backward launch the bounce bonus.
        Assert.Equal(0f, longBackwardDelta, precision: 3);
    }

    // ---------------------------------------------------------------------------------------------
    // AIB-05 — the shot model (report section 5).
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Report section 5.2: <see cref="ActionValueScale.ShotMouthAngleFullDegrees"/> was 30 degrees, but the
    /// whole 7.32 m goal subtends only ~25 degrees from dead centre at the edge of the penalty box. Every
    /// central shot from inside the box therefore carried an angle discount for not seeing more goal than
    /// the geometry allows (25/30 = 0.83 at 16.5 m). At 19 degrees the whole mouth is full credit, while a
    /// real byline sliver (9 degrees) is still heavily discounted.
    /// </summary>
    [Fact]
    public void AIB05_AFullGoalMouth_NoLongerCarriesAnAngleDiscount()
    {
        float centralEdgeOfBox = 2f * MathF.Atan(Pitch.HalfGoalWidth / 16.5f) * (180f / MathF.PI);

        float noAngle = ActionValueScale.ShotConversion(16.5f, 1f);
        float withAngle = ActionValueScale.ShotConversion(16.5f, 1f, centralEdgeOfBox);
        Assert.Equal(noAngle, withAngle, precision: 3);

        // Pre-fix: 25.06/30 = 0.835, so this comparison was 0.17 of the conversion apart.
        Assert.True(ActionValueScale.ShotConversion(16.5f, 1f, 9f) < 0.6f * noAngle,
            "a nine-degree byline sliver must stay worth far less than a full mouth");
    }

    /// <summary>
    /// Report section 5.2: the opposing keeper was counted by <c>EvaluateLaneClearance</c> as an outfield
    /// lane blocker — his widened goal-line intercept radius covered both corner rays — even though the save
    /// chance is already inside the conversion curve (a duplicate penalty). The public overload added for
    /// the shot path excludes him; this fixture's only defender is the keeper.
    ///
    /// This test does not compile before the overload exists, which is precisely what AIB-05(a) adds (the
    /// same convention the AUD-01(d) shared-helper regression uses).
    /// </summary>
    [Fact]
    public void AIB05_TheGoalkeeper_IsNotAnOutfieldLaneBlocker()
    {
        PerceivedEntity keeper = Opponent(KeeperId, TacticalRole.Goalkeeper, new Vector2(51.5f, 0f));
        Vector2 from = new(39f, 9f);
        Vector2 corner = new(Pitch.HalfLength, -(Pitch.HalfGoalWidth - 0.75f));

        float counted = CandidateGenerator.EvaluateLaneClearance(from, corner, [keeper], TeamId.Home);
        float excluded = CandidateGenerator.EvaluateLaneClearance(
            from, corner, [keeper], TeamId.Home, excludeGoalkeeper: true);

        // A keeper 1.5 m off his line at z = 0 sits ~1.5 m from the corner ray and the intercept radius
        // there is ~3.1 m, so the pre-fix metric read the lane as half covered.
        Assert.True(counted < 0.75f, $"the pre-fix behaviour must be the crowded reading, got {counted:F2}");
        Assert.Equal(1f, excluded, precision: 4);
    }

    /// <summary>
    /// Report section 5, the whole comparison: a clear in-box chance must beat the 96 % backward recycle
    /// that took it in the shipped match. The recycle is worth 0.96 x 0.96 - 0.04 x 0.80 = 0.89; the shot
    /// from (39, 9) — 16.2 m out, the corner lane half-covered, 22 degrees of goal visible — was priced
    /// 0.257 x 0.82 x 0.72 x 7 - (1 - conversion) x 0.60 = 0.64 pre-fix. With the 19-degree mouth threshold,
    /// the raised close-range conversion and the backward territory cost it is worth &gt; 1.2.
    /// </summary>
    [Fact]
    public void AIB05_AClearInBoxChance_OutscoresA96PercentRecycle()
    {
        float recycle = ActionValueScale.ExpectedValue(0.96f, 0.96f, ActionValueScale.LossCost(-20f)) -
                        ActionValueScale.BackwardTerritoryCost;
        float shot = ActionValueScale.ShotValue(16.2f, 0.66f, 22f);

        Assert.True(shot > recycle + 0.15f,
            $"the in-box chance ({shot:F2}) must beat the 96 % recycle ({recycle:F2}); " +
            "pre-fix the shot was 0.64 against 0.89");
    }

    /// <summary>
    /// Report section 5.1, the end-to-end version: the only outfield defender stands on the -Z corner ray
    /// and the opposing keeper stands centrally. The old single-ray lane collapsed and the shot lost to the
    /// recycle; the fixed shot picks the open corner and its value beats the backward ball.
    ///
    /// Pre-fix: laneClearance ~ 0.01 on the fixed -2.91 m corner ray, conversion
    /// 0.257 x 0.60 x 0.73 = 0.113, so the shot was worth ~0.26 while the recycle scored ~0.63.
    ///
    /// AIB-07a re-baseline (2026-11-23, finding 1): the shot used to be the centre target (|z| &lt;= 1.5),
    /// because with the outfield lane equal for centre and far corner strict `&gt;` kept the centre. The keeper
    /// now carries one coverage term: a central keeper covers the centre (0.22) and the far post is blocked by
    /// the outfield defender, so the +Z near-post corner — fully open on both counts — wins at z = +2.91. The
    /// assertion is therefore "the finish picks the open corner", which is the AIB-05 property itself; the
    /// shot still beats the recycle. Positive assertion added with the re-baseline: the chosen corner is the
    /// +Z one the defender is NOT covering.
    /// </summary>
    [Fact]
    public void AIB05_AnOpenFinisherInsideTheBox_BeatsTheBackwardRecycle()
    {
        FootballerObservation observation = Observation(
            new Vector2(39f, 9f),
            Mate(MateId, new Vector2(46f, -2f)),
            Mate(DeepMateId, new Vector2(18f, -2f)),
            Opponent(KeeperId, TacticalRole.Goalkeeper, new Vector2(51.5f, 0f)),
            Opponent(DefenderId, TacticalRole.CenterBack, new Vector2(51.75f, -2.25f)));

        CandidateGenerator generator = Generate(
            observation,
            timeOnBall: 0.8f,
            new AiRuntimeConfig { MaxPassTargets = 1 },
            offsideLineProjX: 50f);

        UtilityCandidate shot = UtilityOf(generator, CandidateKind.Shot);
        UtilityCandidate recycle = PassTo(generator, DeepMateId, "Recycle");

        Assert.True(shot.TargetXZ.HasValue);
        // The open finish is the +Z near-post corner: the defender covers the -Z corner and the central keeper
        // covers the centre (AIB-07a coverage). Pre-AIB-07a the centre was chosen on the tied outfield lane.
        Assert.True(shot.TargetXZ!.Value.Z >= 1.5f,
            $"the shot aimed at z = {shot.TargetXZ.Value.Z:F2}; the defender-covered corner is z = -2.91 and " +
            "the central keeper covers the centre, so the open finish is the +Z corner");
        Assert.True(shot.UtilityScore > recycle.UtilityScore + 0.30f,
            $"the open in-box chance ({shot.UtilityScore:F2}) must beat the backward recycle " +
            $"({recycle.UtilityScore:F2})");
    }

    // ---------------------------------------------------------------------------------------------
    // AIB-01 — the carrier's awareness of a closing defender (report section 1).
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Report section 1.1: <c>readyToPass</c> was gated on the static ring
    /// <c>nearestOppDist &lt; 2.6 m</c>. A presser 4.5 m away closing at 8 m/s is ~0.3 s from the carrier's
    /// feet — inside the tackle window — yet the static ring emitted zero pass candidates while he closed.
    /// The arrival gate now releases the ball in that emergency window; the static-distance and receding
    /// controls must still stay silent.
    /// </summary>
    [Fact]
    public void AIB01_AClosingPresser_UnlocksTheEmergencyPass()
    {
        PerceivedEntity mate = Mate(MateId, new Vector2(10f, 0f));
        const float settleOnly = 0.05f; // below the 0.45 s neutral-tempo pass trigger

        CandidateGenerator closing = Generate(
            Observation(new Vector2(0f, 0f), mate,
                Opponent(DefenderId, TacticalRole.CenterBack, new Vector2(-4.5f, 0f), new Vector2(8f, 0f))),
            settleOnly);
        CandidateGenerator receding = Generate(
            Observation(new Vector2(0f, 0f), mate,
                Opponent(DefenderId, TacticalRole.CenterBack, new Vector2(-4.5f, 0f), new Vector2(-6f, 0f))),
            settleOnly);
        CandidateGenerator standing = Generate(
            Observation(new Vector2(0f, 0f), mate,
                Opponent(DefenderId, TacticalRole.CenterBack, new Vector2(-4.5f, 0f))),
            settleOnly);

        Assert.Contains(closing.Candidates,
            c => c.Key.Kind == CandidateKind.Pass && c.Key.TargetPlayerId == MateId);
        // The same static distance is not an emergency: pre-fix both of these also emitted no pass, so the
        // first assertion fails on the old formula, not these controls.
        Assert.DoesNotContain(receding.Candidates, c => c.Key.Kind == CandidateKind.Pass);
        Assert.DoesNotContain(standing.Candidates, c => c.Key.Kind == CandidateKind.Pass);
    }

    /// <summary>
    /// Report section 1.2: carry contact risk only started inside <c>ContactRiskMeters = 2.2 m</c> of static
    /// distance, so a defender 4.5 m away closing at 8 m/s imposed exactly 0.00 on the carry. The risk is
    /// now read from the same local-pressure arrival estimate: arrival ~ 0.30 s gives closeness
    /// (0.50 - 0.30) / (0.50 - 0.22) = 0.71, so 0.45 x 0.71 ~ 0.32 comes off the carry's gain. The receding
    /// control has no arrival inside the horizon and so no deduction.
    /// </summary>
    [Fact]
    public void AIB01_AClosingPresser_CostsTheCarryItsValue()
    {
        PerceivedEntity mate = Mate(MateId, new Vector2(10f, 0f));

        CandidateGenerator closing = Generate(
            Observation(new Vector2(0f, 0f), mate,
                Opponent(DefenderId, TacticalRole.CenterBack, new Vector2(-4.5f, 0f), new Vector2(8f, 0f))),
            timeOnBall: 0.8f);
        CandidateGenerator receding = Generate(
            Observation(new Vector2(0f, 0f), mate,
                Opponent(DefenderId, TacticalRole.CenterBack, new Vector2(-4.5f, 0f), new Vector2(-6f, 0f))),
            timeOnBall: 0.8f);

        float closingCarry = UtilityOf(closing, CandidateKind.Carry).UtilityScore;
        float recedingCarry = UtilityOf(receding, CandidateKind.Carry).UtilityScore;

        // Pre-fix the deduction was 0 (4.5 m is outside the 2.2 m ring), so the only difference was ~0.05
        // of probe-space noise; the 0.25 margin fails on the old formula.
        Assert.True(closingCarry < recedingCarry - 0.25f,
            $"carrying at a presser ~0.3 s away ({closingCarry:F2}) must lose more than 0.25 to carrying " +
            $"away from him ({recedingCarry:F2})");
    }

    /// <summary>
    /// Report section 1.2, the second half: <c>CarryEarlyBonus</c> was paid to every early touch no matter who
    /// was closing. It is now dropped when an opponent can arrive inside
    /// <see cref="ActionValueScale.CarryEarlySuppressArrivalSeconds"/>, so an early touch and a settled one are
    /// worth the same under pressure — while a free carrier keeps the documented tempo bonus.
    ///
    /// AIB-12a re-baseline (2026-11-23, finding 5): the bonus was lowered 0.25 → 0.10, so the free-carrier gap
    /// is now exactly <see cref="ActionValueScale.CarryEarlyBonus"/> (measured 1.08 vs 0.98) instead of the old
    /// +0.25. The AIB-01 property — "closing withdraws it, nobody closing keeps it" — is unchanged and is
    /// re-asserted against the new constant, which is the positive assertion: the gap is the full bonus, not a
    /// fraction of it.
    /// </summary>
    [Fact]
    public void AIB01_TheEarlyCarryBonus_DisappearsUnderAClosingPresser()
    {
        PerceivedEntity mate = Mate(MateId, new Vector2(10f, 0f));
        PerceivedEntity closingDefender = Opponent(DefenderId, TacticalRole.CenterBack,
            new Vector2(-4.5f, 0f), new Vector2(8f, 0f));
        PerceivedEntity recedingDefender = Opponent(DefenderId, TacticalRole.CenterBack,
            new Vector2(-4.5f, 0f), new Vector2(-6f, 0f));

        float earlyClosing = UtilityOf(
            Generate(Observation(new Vector2(0f, 0f), mate, closingDefender), timeOnBall: 0.1f),
            CandidateKind.Carry).UtilityScore;
        float settledClosing = UtilityOf(
            Generate(Observation(new Vector2(0f, 0f), mate, closingDefender), timeOnBall: 0.8f),
            CandidateKind.Carry).UtilityScore;

        // A closing presser withdraws the bonus entirely: early == settled (pre-fix this held too).
        Assert.Equal(settledClosing, earlyClosing, precision: 2);

        float earlyFree = UtilityOf(
            Generate(Observation(new Vector2(0f, 0f), mate, recedingDefender), timeOnBall: 0.1f),
            CandidateKind.Carry).UtilityScore;
        float settledFree = UtilityOf(
            Generate(Observation(new Vector2(0f, 0f), mate, recedingDefender), timeOnBall: 0.8f),
            CandidateKind.Carry).UtilityScore;

        // The tempo bonus was not deleted: with nobody who can arrive the early touch still pays the full
        // (AIB-12a) bonus. Pre-fix the gap was the old 0.25.
        Assert.Equal(ActionValueScale.CarryEarlyBonus, earlyFree - settledFree, precision: 3);
    }

    /// <summary>
    /// Task-7 regression (A8.3 switch attribution): a presser standing 2 m away with <b>zero</b> velocity
    /// still gets an arrival estimate inside the contact window (2.0 m is ~0.44 s against the 0.50 s start),
    /// so keying the early-bonus withdrawal on arrival alone withdrew the tempo bonus against a defender who
    /// is not closing. On the first touch that released the ball to the open far-side pass: the carry fell
    /// 1.01 -> 0.74 and the pre-recognition build played the switch. The audit wording is "drop
    /// CarryEarlyBonus whenever an opponent is <i>closing</i>"; zero closing velocity must keep the bonus.
    ///
    /// AIB-12a re-baseline (2026-11-23, finding 5): the bonus is 0.10, so the stationary-presser gap is
    /// exactly <see cref="ActionValueScale.CarryEarlyBonus"/> (measured 0.78 vs 0.68). The property under test —
    /// the stationary presser is not treated as closing — is unchanged; the positive assertion is that the gap
    /// is the full current bonus.
    /// </summary>
    [Fact]
    public void AIB01_AStationaryPresser_KeepsTheEarlyCarryBonus()
    {
        PerceivedEntity mate = Mate(MateId, new Vector2(10f, 0f));
        PerceivedEntity planted = Opponent(
            DefenderId, TacticalRole.DefensiveMidfielder, new Vector2(2f, 0f));

        float early = UtilityOf(
            Generate(Observation(new Vector2(0f, 0f), mate, planted), timeOnBall: 0.1f),
            CandidateKind.Carry).UtilityScore;
        float settled = UtilityOf(
            Generate(Observation(new Vector2(0f, 0f), mate, planted), timeOnBall: 0.8f),
            CandidateKind.Carry).UtilityScore;

        // Pre-fix (arrival-only withdrawal) early == settled: the bonus was suppressed by a defender who
        // never moved. The contact risk is charged in both fixtures, so the only difference is the bonus.
        Assert.Equal(ActionValueScale.CarryEarlyBonus, early - settled, precision: 3);
    }

    /// <summary>
    /// The counterpart of the stationary fixture at the <b>same 2 m</b> — only the velocity differs: an
    /// 8 m/s presser closing head-on both withdraws the tempo bonus and opens the emergency release window
    /// (the AIB-1 behaviour). Kept together with the stationary case so "gated on closing motion" cannot
    /// silently become "never suppressed".
    /// </summary>
    [Fact]
    public void AIB01_AClosingPresser_BothSuppressesTheBonusAndUnlocksThePass()
    {
        PerceivedEntity mate = Mate(MateId, new Vector2(10f, 0f));
        PerceivedEntity closingDefender = Opponent(
            DefenderId, TacticalRole.DefensiveMidfielder, new Vector2(2f, 0f), new Vector2(-8f, 0f));

        CandidateGenerator early = Generate(
            Observation(new Vector2(0f, 0f), mate, closingDefender), timeOnBall: 0.1f);
        CandidateGenerator settled = Generate(
            Observation(new Vector2(0f, 0f), mate, closingDefender), timeOnBall: 0.8f);

        float earlyCarry = UtilityOf(early, CandidateKind.Carry).UtilityScore;
        float settledCarry = UtilityOf(settled, CandidateKind.Carry).UtilityScore;
        Assert.True(MathF.Abs(earlyCarry - settledCarry) < 0.02f,
            $"a closing presser must withdraw the tempo bonus even on the first touch: " +
            $"early {earlyCarry:F2} vs settled {settledCarry:F2}");

        Assert.Contains(early.Candidates,
            c => c.Key.Kind == CandidateKind.Pass && c.Key.TargetPlayerId == MateId);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures and helpers.
    // ---------------------------------------------------------------------------------------------

    private static CandidateGenerator Generate(
        FootballerObservation observation,
        float timeOnBall,
        AiRuntimeConfig? ai = null,
        float offsideLineProjX = 34f)
    {
        var generator = new CandidateGenerator(Pitch, communication: null, ai) { TimeOnBall = timeOnBall };
        generator.GenerateOnBallCandidates(observation, GoalCentre, offsideLineProjX);
        return generator;
    }

    private static FootballerObservation Observation(
        Vector2 carrierXZ,
        params PerceivedEntity[] visible) => Observation(carrierXZ, default, visible);

    private static FootballerObservation Observation(
        Vector2 carrierXZ,
        TeamCallContext calls,
        params PerceivedEntity[] visible) => new()
    {
        Calls = calls,
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
        // Neutral team and player tendencies make the preference arithmetic in the assertions exact: a
        // balanced profile and a baseline player add no risk/directness term of their own.
        TeamProfile = new TeamTacticalProfile(),
        SelfTendencies = new PlayerTendencies(),
        SelfAttributes = new PlayerAttributes()
    };

    private static PerceivedEntity Mate(int id, Vector2 position, Vector2 velocity = default) => new(
        id, TeamId.Home, TacticalRole.CentralMidfielder,
        new Vector3(position.X, 0f, position.Y),
        new Vector3(velocity.X, 0f, velocity.Y),
        1f)
    {
        Capability = CapabilityEstimate.Generic
    };

    private static PerceivedEntity Opponent(
        int id,
        TacticalRole role,
        Vector2 position,
        Vector2 velocity = default) => new(
        id, TeamId.Away, role,
        new Vector3(position.X, 0f, position.Y),
        new Vector3(velocity.X, 0f, velocity.Y),
        0.95f)
    {
        Capability = CapabilityEstimate.Generic
    };

    private static UtilityCandidate UtilityOf(CandidateGenerator generator, CandidateKind kind)
    {
        for (int index = 0; index < generator.CandidateCount; index++)
        {
            if (generator.Candidates[index].Key.Kind == kind) return generator.Candidates[index];
        }

        throw new Xunit.Sdk.XunitException($"no {kind} candidate: {Describe(generator)}");
    }

    private static UtilityCandidate PassTo(CandidateGenerator generator, int playerId, string tag)
    {
        if (TryFindPass(generator, playerId, tag, out UtilityCandidate candidate)) return candidate;
        throw new Xunit.Sdk.XunitException(
            $"no \"{tag}\" pass to #{playerId}: {Describe(generator)}");
    }

    private static bool TryFindPass(
        CandidateGenerator generator,
        int playerId,
        string tag,
        out UtilityCandidate candidate)
    {
        for (int index = 0; index < generator.CandidateCount; index++)
        {
            UtilityCandidate current = generator.Candidates[index];
            if (current.Key.Kind == CandidateKind.Pass &&
                current.Key.TargetPlayerId == playerId &&
                string.Equals(current.Key.TargetTag, tag, StringComparison.Ordinal))
            {
                candidate = current;
                return true;
            }
        }

        candidate = default;
        return false;
    }

    private static string Describe(CandidateGenerator generator)
    {
        var names = new List<string>();
        for (int index = 0; index < generator.CandidateCount; index++)
            names.Add($"{generator.Candidates[index].Identity}={generator.Candidates[index].UtilityScore:F2}");
        return names.Count == 0 ? "(empty)" : string.Join(" | ", names);
    }
}
