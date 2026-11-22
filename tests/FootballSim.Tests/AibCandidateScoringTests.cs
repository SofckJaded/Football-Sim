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
/// AIB-2026-11-22 audit — the candidate-scoring package (<b>AIB-02a</b>, <b>AIB-02b</b>, <b>AIB-02c</b>,
/// <b>AIB-02e</b>, <b>AIB-02f</b>, <b>AIB-04a</b>): the pass-cleanliness and carry-direction findings the
/// own-side candidate generator is responsible for.
///
/// Every assertion is written so the <i>pre-fix</i> formula fails it, and the comment above each assertion
/// states the old formula and the arithmetic why. Fixtures are built from public members only (there is no
/// <c>InternalsVisibleTo</c> in this repository): candidate sets are generated with
/// <see cref="CandidateGenerator.GenerateOnBallCandidates"/> and the resulting
/// <see cref="UtilityCandidate"/> scores/terms are read directly, which is the same route the decision path
/// takes.
/// </summary>
public sealed class AibCandidateScoringTests
{
    private static readonly PitchDimensions Pitch = new();
    private const int CarrierId = 8;
    private const int MateId = 9;
    private const int RunnerId = 11;
    private const float ObservationTick = 500f;

    // ---------------------------------------------------------------------------------------------
    // AIB-02a — a corridor is a lane, not one defender's shadow.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Report section 2.A.2: <c>EvaluateLaneClearance</c> kept only the single worst defender
    /// (<c>minClearanceRatio</c>), so three men clustered around the same passing corridor scored exactly
    /// like one. This pins the monotone congestion charge added in AIB-02a from both sides: an empty lane
    /// still scores exactly 1.0, and each additional body inside the corridor scores strictly worse.
    ///
    /// Fixture: a 20 m lane on the x axis with every defender 1.5 m off it at 10 m. The old metric returned
    /// the same 1.5 / (1.1 + 0.5 x 2.4) = 0.625 for the one-, two- and three-defender cases, because the
    /// minimum cannot see the extra bodies.
    /// </summary>
    [Fact]
    public void AIB02a_ACrowdedCorridor_ScoresWorseThanOneDefender_WhileAnEmptyLaneStaysClear()
    {
        Vector2 from = new(0f, 0f);
        Vector2 to = new(20f, 0f);

        float open = CandidateGenerator.EvaluateLaneClearance(
            from, to, Array.Empty<PerceivedEntity>(), TeamId.Home);
        float one = CandidateGenerator.EvaluateLaneClearance(
            from, to, [Opponent(20, 10f, 1.5f)], TeamId.Home);
        float two = CandidateGenerator.EvaluateLaneClearance(
            from, to, [Opponent(20, 10f, 1.5f), Opponent(21, 10.4f, 1.6f)], TeamId.Home);
        float three = CandidateGenerator.EvaluateLaneClearance(
            from, to, [Opponent(20, 10f, 1.5f), Opponent(21, 10.4f, 1.6f), Opponent(22, 9.7f, 1.4f)],
            TeamId.Home);

        // Pre-fix all three returned 0.625 — the worst defender's clearance, with no charge for the crowd.
        Assert.True(three < two,
            $"two crowding defenders ({two:F3}) must score worse than one ({one:F3}); three scored {three:F3}");
        Assert.True(two < one, $"a second body in the corridor must cost something ({two:F3} vs {one:F3})");
        Assert.True(one < 1f, "a body in the corridor is not an open lane");
        Assert.True(three > 0f, "the congestion charge is bounded: it never becomes the hard-blocked zero");
        Assert.Equal(1f, open, precision: 5);
    }

    // ---------------------------------------------------------------------------------------------
    // AIB-02b — risk tolerance may only shrink a penalty, never reward a crowded lane.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Report section 2.A.3: <c>PassPreference</c> added
    /// <c>(tendency − 0.55) x 0.40 x laneRisk + (profile − 0.50) x 0.30 x laneRisk</c> — a <b>positive</b>
    /// utility for a crowded lane whenever either risk tolerance is above its baseline. The report measured
    /// exactly that: the risk-tolerant player scored a covered target <i>above</i> an open one.
    ///
    /// New rule (AIB-02b): a tight lane is a cost every passer pays (<see
    /// cref="CandidateGenerator.LaneRiskPreferencePenalty01"/> x laneRisk), and risk tolerance only scales the
    /// share of that cost he feels, inside a bounded share. Ordering — open above crowded — is therefore the
    /// same for every profile, while the tolerant profile pays less and the averse profile pays more.
    ///
    /// Arithmetic for the fixture (forwardMeters = 10, so the progress term is 0.175 for all profiles):
    /// neutral pays 0.180 x 0.5 x 1.00 = 0.090; tolerant pays 0.180 x 0.5 x 0.35 = 0.0315; averse pays
    /// 0.180 x 0.5 x 1.35 = 0.1215. Pre-fix: neutral paid 0.000 (both lanes 0.175), and the tolerant profile
    /// paid −0.1325, i.e. the crowded lane scored 0.3075 against 0.175 for the open one.
    /// </summary>
    [Fact]
    public void AIB02b_RiskTolerance_NeverRewardsACrowdedLane_ItOnlyShrinksThePenalty()
    {
        FootballerObservation tolerant = ObservationWithRisk(0.95f, 0.85f);
        FootballerObservation neutral = ObservationWithRisk(0.55f, 0.50f);
        FootballerObservation averse = ObservationWithRisk(0.10f, 0.15f);

        const float openLane = 1.0f;
        const float crowdedLane = 0.5f;

        float tolerantOpen = CandidateGenerator.PassPreference(tolerant, 10f, openLane);
        float tolerantCrowded = CandidateGenerator.PassPreference(tolerant, 10f, crowdedLane);
        float neutralOpen = CandidateGenerator.PassPreference(neutral, 10f, openLane);
        float neutralCrowded = CandidateGenerator.PassPreference(neutral, 10f, crowdedLane);
        float averseOpen = CandidateGenerator.PassPreference(averse, 10f, openLane);
        float averseCrowded = CandidateGenerator.PassPreference(averse, 10f, crowdedLane);

        // The same ordering for every profile: an open lane outranks a crowded one. Pre-fix the tolerant
        // profile inverted this (crowded 0.3075 > open 0.175).
        Assert.True(tolerantOpen > tolerantCrowded + 0.02f,
            $"a risk-taker must still prefer the open lane ({tolerantOpen:F3} vs {tolerantCrowded:F3})");
        Assert.True(neutralOpen > neutralCrowded + 0.02f,
            $"a neutral passer must pay for a crowded lane ({neutralOpen:F3} vs {neutralCrowded:F3})");
        Assert.True(averseOpen > averseCrowded + 0.02f,
            $"a risk-averse passer must pay the most ({averseOpen:F3} vs {averseCrowded:F3})");

        float tolerantPenalty = tolerantOpen - tolerantCrowded;
        float neutralPenalty = neutralOpen - neutralCrowded;
        float aversePenalty = averseOpen - averseCrowded;

        Assert.Equal(CandidateGenerator.LaneRiskPreferencePenalty01 * 0.5f, neutralPenalty, precision: 4);
        Assert.Equal(0.0315f, tolerantPenalty, precision: 4);
        Assert.Equal(0.1215f, aversePenalty, precision: 4);
        Assert.True(tolerantPenalty < neutralPenalty,
            $"the risk-taker must feel a smaller share of the cost ({tolerantPenalty:F4} vs {neutralPenalty:F4})");
        Assert.True(neutralPenalty < aversePenalty,
            $"the risk-averse passer must feel a larger share ({neutralPenalty:F4} vs {aversePenalty:F4})");
    }

    // ---------------------------------------------------------------------------------------------
    // AIB-02c — bounded leads: feet, uncommitted runs and unled raised balls.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Report section 2.B.1: a <c>ToFeet</c> pass used a six-metre maximum lead, so a team-mate jogging
    /// sideways at 5 m/s over a 1.1 s flight had the ball aimed 5.5 m away and it rolled into empty grass when
    /// he stopped. The lead bound is tightened to 2.5 m and the feet pass is planned with the planner's
    /// uncommitted hedge instead of the honest (and so refused) solve.
    ///
    /// Fixture: receiver at (15, 12) moving (4, 1). The planner aims (17.43, 12.61): a 2.51 m lead.
    /// Pre-fix the bound was 6 m and the same solve aimed the ball up to six metres ahead.
    /// </summary>
    [Fact]
    public void AIB02c_APassToFeet_NeverLeadsBeyondTheTightenedBound()
    {
        PerceivedEntity mate = TeamMate(MateId, 15f, 12f, new Vector3(4f, 0f, 1f));
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        FootballerObservation observation = Observation(new Vector2(0.4f, 8f), new Vector2(0f, 8f), mate);

        generator.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 46f);

        UtilityCandidate feet = Single(generator, "ToFeet", MateId);
        Assert.True(feet.TargetXZ.HasValue);
        float lead = Vector2.Distance(new Vector2(15f, 12f), XZ(feet.TargetXZ!.Value));

        Assert.Equal(2.5f, CandidateGenerator.FeetPassMaxLeadMeters);
        Assert.True(lead > 1f, $"the fixture must actually lead a moving receiver, got {lead:F2} m");
        Assert.True(lead <= CandidateGenerator.FeetPassMaxLeadMeters + 0.05f,
            $"a pass to feet led {lead:F2} m; the bound is {CandidateGenerator.FeetPassMaxLeadMeters:F1} m " +
            "(pre-fix: the same bound was 6 m)");
    }

    /// <summary>
    /// Report section 2.B.1: an unpromised <c>ToRun</c> added <c>ThroughLeadMeters = 5 m</c> on top of the
    /// velocity x flight lead with only the 25 m run cap, even though the receiver had no
    /// <see cref="RunAdvertisement"/> commitment — the report's 25 m punt at a man who promised nothing.
    /// AIB-02c keeps the uncommitted variant but hedges it: a strictly sprinting receiver (>= 3 m/s) is led by
    /// the planner's uncommitted cap plus a 1.5 m push.
    ///
    /// Fixture: receiver moving (4, 1). The planet aims (18.88, 12.97): a 4.0 m lead = 2.5 + 1.5. Pre-fix the
    /// same fixture's extra push alone was 5 m and the velocity lead was capped at 25 m (measured ~9.8 m).
    /// </summary>
    [Fact]
    public void AIB02c_AnUncommittedRunPass_GetsTheHedgedLead_NotTheOldThroughPush()
    {
        PerceivedEntity mate = TeamMate(MateId, 15f, 12f, new Vector3(4f, 0f, 1f));
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        FootballerObservation observation = Observation(new Vector2(0.4f, 8f), new Vector2(0f, 8f), mate);

        generator.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 46f);

        UtilityCandidate run = Single(generator, CandidateGenerator.RunPassTag, MateId);
        Assert.True(run.TargetXZ.HasValue);
        float lead = Vector2.Distance(new Vector2(15f, 12f), XZ(run.TargetXZ!.Value));
        float bound = PassPlanner.UncommittedLeadMaxMeters + CandidateGenerator.UncommittedThroughLeadMeters;

        Assert.True(lead > PassPlanner.UncommittedLeadMaxMeters,
            $"a ball into space must still lead the runner ({lead:F2} m)");
        Assert.True(lead <= bound + 0.1f,
            $"the uncommitted run ball led {lead:F2} m; the hedge is {bound:F1} m " +
            "(pre-fix: 5 m of extra lead on top of a 25 m velocity lead)");
    }

    /// <summary>
    /// AIB-02c: the long committed through-ball still works. A receiver who <b>has</b> announced his run is
    /// played to the region and the moment he promised instead of to a guessed lead — even at jogging pace the
    /// advertisement is the commitment. Without the advertisement the same receiver is not offered a
    /// <c>ToRun</c> at all, because a ball into space needs a man actually sprinting into it.
    /// </summary>
    [Fact]
    public void AIB02c_ARunAdvertisement_KeepsTheCommittedThroughBallAlive()
    {
        Vector2 region = new(20f, 14f);
        PerceivedEntity joggingRunner = TeamMate(RunnerId, 15f, 12f, new Vector3(2f, 0f, 0f));
        FootballerObservation observation = Observation(new Vector2(0.4f, 8f), new Vector2(0f, 8f), joggingRunner);

        var announced = new CandidateGenerator(Pitch, AnnouncedRun(RunnerId, region, meetTick: 560))
            { TimeOnBall = 0.8f };
        announced.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 46f);

        UtilityCandidate committedRun = Single(announced, CandidateGenerator.RunPassTag, RunnerId);
        Assert.True(committedRun.TargetXZ.HasValue);
        Assert.True(Vector2.Distance(region, XZ(committedRun.TargetXZ!.Value)) <= 0.5f,
            $"the committed ball must be played into the announced region, aimed {committedRun.TargetXZ}");

        // No advertisement, jogging at 2 m/s: below MinUncommittedRunSpeed, so the uncommitted variant is
        // not offered (pre-fix RunsIntoSpace accepted anything above 1.5 m/s and led it).
        var silent = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        silent.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 46f);
        Assert.DoesNotContain(silent.Candidates,
            c => c.Key.Kind == CandidateKind.Pass && c.Key.TargetTag == CandidateGenerator.RunPassTag);
    }

    /// <summary>
    /// Report section 2.B.2: when the ground lane is blocked and the receiver has announced nothing,
    /// <c>AddLoftedPass</c> aimed at the receiver's <b>current</b> position for a 1.5–2.5 s hang — a ball to
    /// where he was. AIB-02c leads the raised target by his own velocity over the modelled hang, bounded by
    /// <see cref="CandidateGenerator.LoftedUncommittedLeadMaxMeters"/>.
    ///
    /// Fixture: receiver at (15, 12) moving (3, 1.5), with a defender standing in the ground lane. The raised
    /// aim is (19.30, 14.15): a 4.81 m lead along the velocity (bounded at 5 m). Pre-fix the aim was the
    /// receiver's current point — a lead of exactly 0.
    /// </summary>
    [Fact]
    public void AIB02c_AnUnledRaisedBall_IsLedBoundedly_InsteadOfAimedAtTheFeet()
    {
        PerceivedEntity mate = TeamMate(MateId, 15f, 12f, new Vector3(3f, 0f, 1.5f));
        PerceivedEntity blocker = Opponent(14, 0.6f, 9.0f);
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        FootballerObservation observation = Observation(
            new Vector2(0.4f, 8f), new Vector2(0f, 8f), mate, blocker);

        generator.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 46f);

        UtilityCandidate raised = Single(generator, "Lofted", MateId);
        Assert.True(raised.TargetXZ.HasValue);
        Vector2 aim = XZ(raised.TargetXZ!.Value);
        Vector2 mateXZ = new(15f, 12f);
        Vector2 velocity = new(3f, 1.5f);
        float lead = Vector2.Distance(mateXZ, aim);

        Assert.True(lead > 0.5f,
            $"an unled raised ball must be led, not aimed at the receiver's current feet (lead {lead:F2} m)");
        Assert.True(lead <= CandidateGenerator.LoftedUncommittedLeadMaxMeters + 0.2f,
            $"the unled raised lead is {lead:F2} m, over the {CandidateGenerator.LoftedUncommittedLeadMaxMeters:F1} m bound");
        Assert.True(Vector2.Dot(SimMath.SafeNormalize(aim - mateXZ, Vector2.UnitX), Vector2.Normalize(velocity)) > 0.9f,
            "the raised lead must follow the receiver's own run, not an invented direction");
    }

    // ---------------------------------------------------------------------------------------------
    // AIB-02e — the floors: ordinary passes are raised, intended deliveries survive.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Report section 2.A.2: <c>MinViableCompletion01 = 0.30</c> still permitted low-percentage balls to be
    /// generated and chosen. The ordinary floor is now 0.45; the deliveries keep their own 0.30 floor so the
    /// raise cannot silently delete a cross, cutback, raised alternative or committed through ball.
    /// </summary>
    [Fact]
    public void AIB02e_TheOrdinaryFloor_IsRaised_AndTheDeliveriesKeepTheirOwn()
    {
        Assert.Equal(0.45f, CandidateGenerator.MinViableCompletion01);
        Assert.Equal(0.30f, CandidateGenerator.MinViableDeliveryCompletion01);
        Assert.True(CandidateGenerator.MinViableDeliveryCompletion01 < CandidateGenerator.MinViableCompletion01);
    }

    /// <summary>
    /// Report section 2.A.2, second half: three defenders clustered around the corridor used to score exactly
    /// like one (the single-worst-defender metric), and the 0.30 floor then let the ball compete. AIB-02a
    /// charges the corridor and AIB-02e raises the floor for a crowded lane; the pass to the crowded receiver
    /// is refused with a stated reason while the open receiver's pass survives at 0.97 completion.
    ///
    /// Fixture: two team-mates from the ball at (0.4, 8) — one at (14, 8) with three defenders on that
    /// corridor, one at (12, 20) with the same defenders nowhere near its lane.
    /// </summary>
    [Fact]
    public void AIB02e_APassThroughThreeDefenders_IsFiltered_WhileTheOpenPassStays()
    {
        const int openMateId = 10;
        PerceivedEntity crowdedMate = TeamMate(MateId, 14f, 8f);
        PerceivedEntity openMate = TeamMate(openMateId, 12f, 20f);
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        FootballerObservation observation = Observation(
            new Vector2(0.4f, 8f), new Vector2(0f, 8f),
            crowdedMate, openMate,
            Opponent(14, 5f, 6.6f),
            Opponent(15, 8f, 9.4f),
            Opponent(16, 11f, 7.4f),
            Opponent(17, 30f, -8f));

        generator.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 46f);

        Assert.DoesNotContain(generator.Candidates,
            c => c.Key.Kind == CandidateKind.Pass && c.Key.TargetPlayerId == MateId);

        UtilityCandidate openPass = Single(generator, "ToFeet", openMateId);
        Assert.True(openPass.Terms.Probability01 >= CandidateGenerator.MinViableCompletion01,
            $"the open pass is a real ball at {openPass.Terms.Probability01:F3} completion");

        bool refusedWithAReason = false;
        for (int i = 0; i < generator.RejectionCount; i++)
        {
            CandidateRejection rejection = generator.GetRejection(i);
            if (rejection.TargetPlayerId != MateId) continue;
            if (rejection.Reason is CandidateGenerator.RejectionLaneBlocked or CandidateGenerator.RejectionLowCompletion)
                refusedWithAReason = true;
        }

        Assert.True(refusedWithAReason,
            "the crowded pass must be refused with a stated reason (lane-blocked / low-completion)");
    }

    /// <summary>
    /// AIB-02e: raising the ordinary floor must not delete the raised alternative. The fixture's only offered
    /// ball to the receiver is a lofted one whose model completion is 0.34 — below the ordinary 0.45 floor and
    /// above the delivery 0.30 floor. Pre-fix (floor 0.30) this ball was offered for a different reason; the
    /// point of the assertion is that the raised floor is not applied to it.
    /// </summary>
    [Fact]
    public void AIB02e_TheRaisedDelivery_SurvivesBelowTheRaisedOrdinaryFloor()
    {
        PerceivedEntity mate = TeamMate(MateId, 15f, 12f, new Vector3(3f, 0f, 1.5f));
        PerceivedEntity blocker = Opponent(14, 0.6f, 9.0f);
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        FootballerObservation observation = Observation(
            new Vector2(0.4f, 8f), new Vector2(0f, 8f), mate, blocker);

        generator.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 46f);

        UtilityCandidate raised = Single(generator, "Lofted", MateId);
        Assert.True(raised.Terms.Probability01 >= CandidateGenerator.MinViableDeliveryCompletion01);
        Assert.True(raised.Terms.Probability01 < CandidateGenerator.MinViableCompletion01,
            $"the fixture must sit between the two floors, got {raised.Terms.Probability01:F3}");
    }

    // ---------------------------------------------------------------------------------------------
    // AIB-02f — the driven ball is aimed from the ball, not the carrier's centre.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Report section 2.B.3: <c>AddDrivenCandidate</c> measured the distance, the lane and the aim from
    /// <c>selfXZ</c> instead of the perceived ball position, so a ball 0.5–0.9 m off the player's centre put an
    /// angular offset on the delivery. AIB-02f aims from the ball.
    ///
    /// Fixture: carrier at (14, 16) with the ball at (14, 14.6) and an announced run at (30, 6). From the ball
    /// the line is (0.881, −0.474); from the centre it is (0.848, −0.530) — dot 0.9982, which the pre-fix aim
    /// produced and which the ball-origin aim beats.
    /// </summary>
    [Fact]
    public void AIB02f_TheDrivenBall_IsAimedFromTheBall_NotTheCarriersCentre()
    {
        Vector2 ballXZ = new(14f, 14.6f);
        Vector2 selfXZ = new(14f, 16f);
        Vector2 region = new(30f, 6f);
        PerceivedEntity runner = TeamMate(RunnerId, 30f, 6f, new Vector3(1f, 0f, 0f));
        var generator = new CandidateGenerator(Pitch, AnnouncedRun(RunnerId, region, meetTick: 560))
            { TimeOnBall = 0.8f };
        FootballerObservation observation = Observation(ballXZ, selfXZ, runner,
            Opponent(25, TacticalRole.Goalkeeper, 50f, 0f),
            Opponent(26, 44f, -4f));

        generator.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 46f);

        UtilityCandidate driven = Single(generator, "Driven", RunnerId);
        Vector2 aim = new(driven.Command.KickAimDirectionXZ.X, driven.Command.KickAimDirectionXZ.Z);
        Vector2 fromBall = Vector2.Normalize(region - ballXZ);
        Vector2 fromCentre = Vector2.Normalize(region - selfXZ);

        Assert.True(Vector2.Dot(aim, fromBall) > 0.9999f,
            $"the driven ball must be aimed along the ball's own line, dot {Vector2.Dot(aim, fromBall):F5}");
        Assert.True(Vector2.Dot(aim, fromCentre) < 0.9995f,
            "the fixture's ball offset must be large enough for the two origins to be distinguishable");
    }

    // ---------------------------------------------------------------------------------------------
    // AIB-04a — carries go goalward / inside instead of drifting to the empty flank.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Report section 4.1: <c>ScoreCarryDirection</c> returned <c>0.55 x space + 0.45 x boundarySafety</c>
    /// with no goalward term, <c>boundarySafety</c> saturates 3 m inside the touchline, and the cones sample up
    /// to ±72°. The emptiest direction — outward, to the empty flank — therefore won almost every tick.
    ///
    /// Fixture: a winger in the final third at (35, −22) with the central lane congested and the flank empty.
    /// Post-fix the carry aims at (37.80, −18.48): dot with the goal direction 1.00 and 3.5 m inward. Pre-fix
    /// the same fixture carried to (39.23, −23.55) — outward, dot with the goal direction 0.315.
    /// </summary>
    [Fact]
    public void AIB04a_AFinalThirdCarrier_CutsInside_InsteadOfDriftingToTheEmptyFlank()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        FootballerObservation observation = Observation(
            new Vector2(35.4f, -22f), new Vector2(35f, -22f),
            Opponent(14, 40f, -4f),
            Opponent(15, 42f, -14f),
            Opponent(16, 36f, -10f),
            Opponent(17, 50f, 4f),
            TeamMate(MateId, 44f, 2f),
            TeamMate(10, 30f, 4f));

        generator.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 46f);

        UtilityCandidate carry = SingleByKind(generator, CandidateKind.Carry);
        Assert.True(carry.TargetXZ.HasValue);
        Vector2 selfXZ = new(35f, -22f);
        Vector2 aim = XZ(carry.TargetXZ!.Value);
        Vector2 direction = SimMath.SafeNormalize(aim - selfXZ, Vector2.UnitX);
        Vector2 toGoal = Vector2.Normalize(new Vector2(Pitch.HalfLength, 0f) - selfXZ);

        Assert.True(Vector2.Dot(direction, toGoal) > 0.7f,
            $"the final-third carry must go goalward (dot {Vector2.Dot(direction, toGoal):F2}; " +
            "pre-fix the same fixture carried outward at 0.315)");
        Assert.True(MathF.Abs(aim.Y) < MathF.Abs(selfXZ.Y) - 1f,
            $"the carry must cut inside: receiver z {aim.Y:F1} vs carrier z {selfXZ.Y:F1} " +
            "(pre-fix it went to z = −23.6, further out)");
        Assert.True(aim.X > selfXZ.X,
            "the existing forward-progress behaviour is kept: the carry still gains ground");
    }

    /// <summary>
    /// AIB-04a: the goalward term ramps in with pitch position, so the build-up carry keeps its progress and
    /// space read. This is the fixture shape the A4 exit gate's build-up scenario uses — the carry still
    /// progresses up the pitch rather than walking into the press for the sake of a goal term.
    /// </summary>
    [Fact]
    public void AIB04a_BuildUpCarries_KeepTheirForwardProgress()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        FootballerObservation observation = Observation(
            new Vector2(-20.4f, 15f), new Vector2(-20f, 15f),
            Opponent(14, 18f, 4f),
            Opponent(15, 10f, -8f),
            TeamMate(MateId, 2f, 8f));

        generator.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 46f);

        UtilityCandidate carry = SingleByKind(generator, CandidateKind.Carry);
        Assert.True(carry.TargetXZ.HasValue);
        Vector2 selfXZ = new(-20f, 15f);
        Vector2 aim = XZ(carry.TargetXZ!.Value);
        Vector2 direction = SimMath.SafeNormalize(aim - selfXZ, Vector2.UnitX);

        Assert.True(direction.X > 0.9f,
            $"the build-up carry must still progress up the pitch, direction {direction}");
        Assert.True(aim.X > selfXZ.X, "the build-up carry still gains ground");
    }

    // ---------------------------------------------------------------------------------------------
    // AIB-04a — the line-breaking half-space pass, symmetric to the wide switch.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Report section 4.3: <c>SwitchRecognition</c> gives a recognised wide switch a bounded preference (up to
    /// <see cref="SwitchRecognition.MaxPreference"/> 0.35) while a central ball between the lines had no
    /// equivalent nudge — one of the four cumulative biases that funnelled every attack down the wing. AIB-04a
    /// adds the symmetric, equally bounded preference for the central penetrative pass.
    ///
    /// This is the pure function: a target in the half-space past a visible opponent line earns a preference
    /// that grows with how far past the line it goes and shrinks toward the touchline; a crowded lane, a wide
    /// target, a square/backward ball and a picture with no line at all earn exactly nothing.
    /// </summary>
    [Fact]
    public void AIB04a_ALineBreakingHalfSpacePass_EarnsABoundedPreference_SymmetricToTheSwitch()
    {
        PerceivedEntity[] picture =
        [
            Opponent(14, 20f, 4f),
            Opponent(15, 34f, -2f),
            Opponent(16, 40f, 10f)
        ];
        Vector2 ballXZ = new(0f, 0f);

        float betweenLines = CandidateGenerator.LineBreakPreference(
            ballXZ, new Vector2(26f, 4f), 1f, picture, TeamId.Home, laneClearance01: 0.9f, completionProbability01: 0.8f);
        float behindTheLine = CandidateGenerator.LineBreakPreference(
            ballXZ, new Vector2(44f, 4f), 1f, picture, TeamId.Home, laneClearance01: 0.9f, completionProbability01: 0.8f);
        float widerInHalfSpace = CandidateGenerator.LineBreakPreference(
            ballXZ, new Vector2(26f, 14f), 1f, picture, TeamId.Home, laneClearance01: 0.9f, completionProbability01: 0.8f);

        Assert.True(betweenLines > 0f && betweenLines <= CandidateGenerator.LineBreakPreferenceMax,
            $"between-the-lines preference out of bounds: {betweenLines:F3}");
        Assert.True(behindTheLine > 0f && behindTheLine <= CandidateGenerator.LineBreakPreferenceMax,
            $"behind-the-line preference out of bounds: {behindTheLine:F3}");
        Assert.True(betweenLines > widerInHalfSpace,
            $"a central ball must earn more than the same ball further wide ({betweenLines:F3} vs {widerInHalfSpace:F3})");

        // Symmetric magnitude: the line-breaking nudge is worth exactly the switch-call nudges, and it is
        // bounded by the switch recognition's own maximum so neither geometry is structurally favoured.
        Assert.Equal(ActionValueScale.CallSwitchPassBonus, CandidateGenerator.LineBreakPreferenceMax);
        Assert.True(CandidateGenerator.LineBreakPreferenceMax <= SwitchRecognition.MaxPreference);

        // Never a reward for the balls AIB-02 exists to stop.
        Assert.Equal(0f, CandidateGenerator.LineBreakPreference(
            ballXZ, new Vector2(26f, 24f), 1f, picture, TeamId.Home, 0.9f, 0.8f));   // wide: the switch's job
        Assert.Equal(0f, CandidateGenerator.LineBreakPreference(
            ballXZ, new Vector2(26f, 4f), 1f, picture, TeamId.Home, 0.3f, 0.8f));    // crowded lane
        Assert.Equal(0f, CandidateGenerator.LineBreakPreference(
            ballXZ, new Vector2(26f, 4f), 1f, picture, TeamId.Home, 0.9f, 0.4f));    // below the offer floor
        Assert.Equal(0f, CandidateGenerator.LineBreakPreference(
            new Vector2(24f, 0f), new Vector2(26f, 4f), 1f, picture, TeamId.Home, 0.9f, 0.8f)); // square ball
        Assert.Equal(0f, CandidateGenerator.LineBreakPreference(
            ballXZ, new Vector2(26f, 4f), 1f, Array.Empty<PerceivedEntity>(), TeamId.Home, 0.9f, 0.8f)); // no picture
    }

    /// <summary>
    /// AIB-04a, the wiring half: the preference the pure function returns is what a generated central
    /// penetrative pass actually pays. Combos and the switch recognition are disabled so the candidate's
    /// preference is exactly <c>PassPreference + LineBreakPreference</c>.
    ///
    /// Fixture: a short forward ball from (0.4, 8) to a receiver at (13, 6) between a visible midfield line at
    /// (7, 14) and a defensive line at (34, −2)/(40, 12). The comparison ball scores 0.227 base preference and
    /// 0.083 line-breaking preference, so the generated candidate reads 0.310.
    /// </summary>
    [Fact]
    public void AIB04a_AHalfSpaceBall_BetweenTheLines_PaysThePreferenceOnTheGeneratedCandidate()
    {
        PerceivedEntity mate = TeamMate(MateId, 13f, 6f);
        FootballerObservation observation = Observation(
            new Vector2(0.4f, 8f), new Vector2(0f, 8f), mate,
            Opponent(14, 7f, 14f),
            Opponent(15, 34f, -2f),
            Opponent(16, 40f, 12f));
        var ai = new AiRuntimeConfig { EnableCombinations = false, EnableSwitchRecognition = false };
        var generator = new CandidateGenerator(Pitch, communication: null, ai) { TimeOnBall = 0.8f };

        generator.GenerateOnBallCandidates(observation, OpponentGoal, offsideLineProjX: 46f);

        UtilityCandidate pass = Single(generator, "ToFeet", MateId);
        Assert.True(pass.TargetXZ.HasValue);
        Vector2 ballXZ = new(0.4f, 8f);
        Vector2 targetXZ = XZ(pass.TargetXZ!.Value);
        float forwardMeters = (targetXZ.X - observation.SelfPosition.X) * observation.AttackingDirectionX;
        float lane = CandidateGenerator.EvaluateLaneClearance(ballXZ, targetXZ, observation.VisiblePlayers,
            observation.SelfTeam);

        float basePreference = CandidateGenerator.PassPreference(observation, forwardMeters, lane);
        float lineBreak = CandidateGenerator.LineBreakPreference(
            ballXZ, targetXZ, observation.AttackingDirectionX, observation.VisiblePlayers, observation.SelfTeam,
            lane, pass.Terms.Probability01);

        Assert.True(lineBreak > 0.05f, $"the fixture must earn a real line-breaking preference, got {lineBreak:F3}");
        Assert.Equal(basePreference + lineBreak, pass.Terms.Preference, precision: 3);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures and helpers.
    // ---------------------------------------------------------------------------------------------

    private static readonly Vector3 OpponentGoal = new(Pitch.HalfLength, 0f, 0f);

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
        params PerceivedEntity[] visible) =>
        ObservationWithRisk(0.55f, 0.50f, ballXZ, selfXZ, visible);

    private static FootballerObservation ObservationWithRisk(
        float riskTolerance01,
        float profileRiskTolerance01,
        Vector2 ballXZ = default,
        Vector2 selfXZ = default,
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
            TeamProfile = new TeamTacticalProfile { RiskTolerance01 = profileRiskTolerance01 },
            SelfTendencies = new PlayerTendencies { RiskTolerance01 = riskTolerance01 },
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
}
