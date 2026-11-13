using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Physics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A4.2 — the pass-success estimate.
///
/// These are model tests, not match tests: each one varies exactly one input that a player could
/// actually observe and asserts the direction the estimate must move. The properties pinned here are
/// the ones the A4 exit gate depends on — a free receiver beating a marked one, a scouted *faster*
/// defender never improving the estimate, a stale sighting being charged against the passer rather
/// than ignored — plus the bounds any probability reaching a utility must satisfy.
/// </summary>
public sealed class PassSuccessModelTests
{
    private const int SelfId = 8;
    private const int MateId = 9;
    private static readonly Vector2 BallXZ = new(0.4f, 0f);
    private static readonly PassPlanner Planner = new(new PitchDimensions());

    // ---------------------------------------------------------------------------------------------
    // Arrival margins: the receiver
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The receiver's arrival margin is what separates a pass into a runner's path from a pass into
    /// where he was two seconds ago. A plan aimed at the space a stationary receiver already occupies
    /// must beat the same plan aimed eight metres behind a runner.
    /// </summary>
    [Fact]
    public void AReceiverWhoHasToTurnBackToTheBall_IsWorthLess()
    {
        PerceivedEntity receiver = Mate(MateId, new Vector2(15f, 0f), new Vector2(7f, 0f));
        FootballerObservation observation = Observation(receiver);

        PassPlan goalSide = ManualPlan(new Vector2(21f, 0f), 0.6f, 17.5f);
        PassPlan behindHim = ManualPlan(new Vector2(7f, 0f), 0.6f, 7.0f);

        PassSuccessTerms ahead = Model.Estimate(observation, receiver, goalSide, 1f);
        PassSuccessTerms behind = Model.Estimate(observation, receiver, behindHim, 1f);

        Assert.True(ahead.ReceiverMarginSeconds > behind.ReceiverMarginSeconds);
        Assert.True(ahead.CompletionProbability01 > behind.CompletionProbability01,
            $"a pass into the run ({ahead.CompletionProbability01:F2}) must beat one behind the runner ({behind.CompletionProbability01:F2})");
    }

    // ---------------------------------------------------------------------------------------------
    // Arrival margins: the defender, and the scouting boundary
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The A2 exit gate's property, restated on this model: a defender scouted as faster can never make
    /// the same pass look *safer*. The comparison is the same position and the same plan, so the only
    /// difference is the capability band the observation carries.
    /// </summary>
    [Fact]
    public void AFasterScoutedDefender_NeverImprovesTheEstimate()
    {
        PerceivedEntity receiver = Mate(MateId, new Vector2(16f, 0f));
        PerceivedEntity slow = Opponent(14, new Vector2(9f, 5f), speed: 6.0f);
        PerceivedEntity fast = Opponent(14, new Vector2(9f, 5f), speed: 11.0f);

        PassPlan plan = Planner.PlanGroundPass(BallXZ, MateId, new Vector2(16f, 0f), Vector2.Zero, 8f);
        Assert.True(plan.Feasible);

        PassSuccessTerms slower = Model.Estimate(Observation(receiver, slow), receiver, plan, 1f);
        PassSuccessTerms faster = Model.Estimate(Observation(receiver, fast), receiver, plan, 1f);

        Assert.True(faster.DefenderArrivalSeconds <= slower.DefenderArrivalSeconds,
            $"the faster defender must be believed to arrive no later ({faster.DefenderArrivalSeconds:F2}s vs {slower.DefenderArrivalSeconds:F2}s)");
        Assert.True(faster.CompletionProbability01 <= slower.CompletionProbability01 + 1e-4f,
            $"a faster defender improved the estimate ({faster.CompletionProbability01:F3} vs {slower.CompletionProbability01:F3})");
    }

    /// <summary>A defender standing on the receiver must cost more than the same defender fifty metres away.</summary>
    [Fact]
    public void AFreeReceiver_BeatsAMarkedReceiver()
    {
        PerceivedEntity receiver = Mate(MateId, new Vector2(15f, 0f));
        PerceivedEntity marker = Opponent(14, new Vector2(15.6f, 0.6f));
        PerceivedEntity farAway = Opponent(14, new Vector2(40f, -30f));

        PassPlan plan = Planner.PlanGroundPass(BallXZ, MateId, new Vector2(15f, 0f), Vector2.Zero, 8f);
        Assert.True(plan.Feasible);

        PassSuccessTerms free = Model.Estimate(Observation(receiver, farAway), receiver, plan, 1f);
        PassSuccessTerms marked = Model.Estimate(Observation(receiver, marker), receiver, plan, 1f);

        Assert.True(free.CompletionProbability01 > marked.CompletionProbability01 + 0.15f,
            $"a free receiver ({free.CompletionProbability01:F2}) must beat a marked one ({marked.CompletionProbability01:F2})");
        Assert.True(marked.DefenderPressure01 > free.DefenderPressure01);
    }

    /// <summary>
    /// A remembered position may be wrong in the defender's favour, so a stale sighting has to be charged
    /// against the pass instead of being trusted as exact.
    /// </summary>
    [Fact]
    public void AStaleSighting_IsChargedAgainstThePass()
    {
        PerceivedEntity receiver = Mate(MateId, new Vector2(16f, 0f));
        PerceivedEntity fresh = Opponent(14, new Vector2(10f, 4f));
        PerceivedEntity stale = Opponent(14, new Vector2(10f, 4f)) with { PositionUncertaintyMeters = 3f };

        PassPlan plan = Planner.PlanGroundPass(BallXZ, MateId, new Vector2(16f, 0f), Vector2.Zero, 8f);
        Assert.True(plan.Feasible);

        PassSuccessTerms known = Model.Estimate(Observation(receiver, fresh), receiver, plan, 1f);
        PassSuccessTerms remembered = Model.Estimate(Observation(receiver, stale), receiver, plan, 1f);

        Assert.True(remembered.DefenderUncertaintySeconds > 0f);
        Assert.True(remembered.CompletionProbability01 <= known.CompletionProbability01,
            $"an uncertain sighting ({remembered.CompletionProbability01:F3}) must not read safer than a fresh one ({known.CompletionProbability01:F3})");
    }

    // ---------------------------------------------------------------------------------------------
    // The lane: hard rejection, not a threshold
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The retained hard rejection: a ball rolled straight into a defender standing on the line is not
    /// a low-probability pass, it is not a pass. The estimate marks it blocked and caps the probability.
    /// </summary>
    [Fact]
    public void ABlockedLane_IsAHardRejection()
    {
        PerceivedEntity receiver = Mate(MateId, new Vector2(15f, 0f));
        PerceivedEntity blocker = Opponent(14, new Vector2(0.6f, 0.55f));

        PassPlan plan = Planner.PlanGroundPass(BallXZ, MateId, new Vector2(15f, 0f), Vector2.Zero, 8f);
        Assert.True(plan.Feasible);

        PassSuccessTerms terms = Model.Estimate(Observation(receiver, blocker), receiver, plan, 1f);

        Assert.True(terms.LaneBlocked);
        Assert.True(terms.CompletionProbability01 <= 0.05f);
    }

    /// <summary>And the opposite: an open lane is never rejected merely for being average.</summary>
    [Fact]
    public void AnOpenLane_IsNotRejected()
    {
        PerceivedEntity receiver = Mate(MateId, new Vector2(15f, 0f));
        PerceivedEntity awayFromTheLine = Opponent(14, new Vector2(8f, -12f));

        PassPlan plan = Planner.PlanGroundPass(BallXZ, MateId, new Vector2(15f, 0f), Vector2.Zero, 8f);
        Assert.True(plan.Feasible);

        PassSuccessTerms terms = Model.Estimate(Observation(receiver, awayFromTheLine), receiver, plan, 1f);

        Assert.False(terms.LaneBlocked);
        Assert.True(terms.CompletionProbability01 > 0.5f,
            $"an open lane scored {terms.CompletionProbability01:F2}");
    }

    /// <summary>The generator's public façade and the model must answer the same question identically.</summary>
    [Fact]
    public void TheGeneratorFacade_AgreesWithTheModel()
    {
        PerceivedEntity receiver = Mate(MateId, new Vector2(15f, 0f));
        PerceivedEntity blocker = Opponent(14, new Vector2(0.6f, 0.55f));
        FootballerObservation observation = Observation(receiver, blocker);

        PassPlan plan = Planner.PlanGroundPass(BallXZ, MateId, new Vector2(15f, 0f), Vector2.Zero, 8f);
        var generator = new CandidateGenerator(new PitchDimensions());

        bool fromModel = Model.IsLaneBlocked(plan, observation);
        bool fromGenerator = generator.IsPassLaneBlocked(
            BallXZ, plan.TargetXZ, plan.Power01, plan.FlightTimeSeconds,
            observation.VisiblePlayers, observation.SelfTeam);

        Assert.True(fromModel);
        Assert.Equal(fromModel, fromGenerator);
    }

    /// <summary>
    /// The hard lane test samples the ball's own travel, not a fixed slice of time. A 19 m/s pass used to
    /// move up to 1.94 m between samples — more than twice the 0.9 m contact radius — so a defender
    /// standing on the line could be stepped straight over between two samples. Walking one defender along
    /// the line of a full-power pass must find him everywhere, which a time-sliced sampler cannot guarantee.
    /// </summary>
    [Fact]
    public void AFastPass_SeesADefenderStandingAnywhereOnItsLine()
    {
        PassPlan plan = ManualPlan(new Vector2(22f, 0f), power01: 1f, flightSeconds: 1.2f);
        PerceivedEntity receiver = Mate(MateId, new Vector2(22f, 0f));

        for (float along = 2f; along <= 20f; along += 0.7f)
        {
            PerceivedEntity blocker = Opponent(14, new Vector2(along, 0.5f));
            bool blocked = Model.IsLaneBlocked(
                BallXZ, plan.TargetXZ, plan.Power01, plan.FlightTimeSeconds,
                new[] { receiver, blocker }, TeamId.Home);

            Assert.True(blocked,
                $"a defender standing half a metre off the line {along:F1} m into a full-power pass was not seen");
        }
    }

    /// <summary>
    /// A ball aimed ahead of a moving receiver is a bet on a run nobody has committed to (A4.10), so it
    /// must price below the same ball played to a receiver who is already on the target point. Before the
    /// lead charge, a 12 m lead to a jogging receiver scored 0.92 — the model simply assumed he would
    /// sprint onto it, which is the assumption the rendered session broke.
    /// </summary>
    [Fact]
    public void APassLedAheadOfTheReceiver_IsPricedBelowOneAtHisFeet()
    {
        PassPlan plan = ManualPlan(new Vector2(20f, 0f), power01: 0.7f, flightSeconds: 1.4f);
        PerceivedEntity atTheTarget = Mate(MateId, new Vector2(20f, 0f));
        PerceivedEntity sixMetersBehind = Mate(MateId, new Vector2(14f, 0f));

        PassSuccessTerms atFeet = Model.Estimate(Observation(atTheTarget), atTheTarget, plan, 1f);
        PassSuccessTerms led = Model.Estimate(Observation(sixMetersBehind), sixMetersBehind, plan, 1f);

        Assert.True(atFeet.CompletionProbability01 > led.CompletionProbability01 + 0.05f,
            $"a ball at his feet ({atFeet.CompletionProbability01:F2}) must beat the same ball led six " +
            $"metres ahead of him ({led.CompletionProbability01:F2})");
    }

    // ---------------------------------------------------------------------------------------------
    // Bounds, determinism and the raised alternative
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void EveryTerm_IsFiniteAndBounded()
    {
        PerceivedEntity receiver = Mate(MateId, new Vector2(20f, 3f), new Vector2(2f, 1f));
        var entities = new List<PerceivedEntity>
        {
            receiver,
            Opponent(14, new Vector2(12f, 6f), speed: 9.5f),
            Opponent(15, new Vector2(24f, -2f))
        };

        PassPlan plan = Planner.PlanGroundPass(BallXZ, MateId, new Vector2(20f, 3f), new Vector2(2f, 1f), 8f);
        Assert.True(plan.Feasible);

        PassSuccessTerms terms = Model.Estimate(Observation(entities.ToArray()), receiver, plan, 0.4f);

        Assert.InRange(terms.CompletionProbability01, PassSuccessModel.MinCompletion01, PassSuccessModel.MaxCompletion01);
        Assert.InRange(terms.LaneClearance01, 0f, 1f);
        Assert.InRange(terms.DefenderPressure01, 0f, 1f);
        Assert.InRange(terms.ReceiverApproachDot, -1f, 1f);
        Assert.True(float.IsFinite(terms.ReceiverArrivalSeconds));
        Assert.True(float.IsFinite(terms.DefenderArrivalSeconds));
        Assert.True(terms.FlightTimeSeconds > 0f);
    }

    [Fact]
    public void TheEstimate_IsDeterministic()
    {
        PerceivedEntity receiver = Mate(MateId, new Vector2(20f, 3f), new Vector2(2f, 1f));
        PerceivedEntity defender = Opponent(14, new Vector2(12f, 6f));

        PassPlan plan = Planner.PlanGroundPass(BallXZ, MateId, new Vector2(20f, 3f), new Vector2(2f, 1f), 8f);
        PassSuccessTerms first = Model.Estimate(Observation(receiver, defender), receiver, plan, 0.6f);
        PassSuccessTerms second = Model.Estimate(Observation(receiver, defender), receiver, plan, 0.6f);

        Assert.Equal(first.CompletionProbability01, second.CompletionProbability01);
        Assert.Equal(first.DefenderMarginSeconds, second.DefenderMarginSeconds);
    }

    /// <summary>
    /// The raised alternative is its own physical problem: it is not offered where the execution layer's
    /// raised band cannot reach, it is offered inside it, its flight is the raised trajectory's rather than
    /// the rolling ball's for the same distance, and it is priced below the ground route.
    /// </summary>
    [Fact]
    public void TheRaisedAlternative_HasItsOwnFlight()
    {
        PerceivedEntity receiver = Mate(MateId, new Vector2(32f, 0f));
        FootballerObservation observation = Observation(receiver);

        LoftedPassTerms outOfRange = Model.EstimateLofted(observation, receiver, 70f, 1f);
        LoftedPassTerms airborne = Model.EstimateLofted(observation, receiver, 32f, 1f);

        Assert.False(outOfRange.Feasible);
        Assert.True(airborne.Feasible);
        Assert.InRange(airborne.CompletionProbability01, 0f, 0.97f);
        Assert.True(airborne.FlightRangeMeters >= 32f);

        PassPlan ground = Planner.PlanGroundPass(BallXZ, MateId, new Vector2(32f, 0f), Vector2.Zero, 8f);
        Assert.True(ground.Feasible);
        Assert.True(ground.FlightTimeSeconds - airborne.FlightTimeSeconds > 0.3f,
            $"the raised flight ({airborne.FlightTimeSeconds:F2}s) must not be the ground flight " +
            $"({ground.FlightTimeSeconds:F2}s) for the same distance");

        // Same distance, same open lane: the raised route is charged the execution penalty, because the
        // airborne trajectory is not modelled yet (A5.5 extends it before raised passes are scored as
        // equals).
        PassSuccessTerms groundTerms = Model.Estimate(observation, receiver, ground, 1f);
        Assert.True(airborne.CompletionProbability01 < groundTerms.CompletionProbability01);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static PassSuccessModel Model => new(Planner);

    private static PassPlan ManualPlan(Vector2 targetXZ, float power01, float flightSeconds) => new(
        Feasible: true,
        AimDirectionXZ: SimMath.SafeNormalize(targetXZ - BallXZ, new Vector2(1f, 0f)),
        TargetXZ: targetXZ,
        Power01: power01,
        FlightTimeSeconds: flightSeconds,
        DistanceMeters: Vector2.Distance(BallXZ, targetXZ));

    private static PerceivedEntity Mate(int id, Vector2 position, Vector2 velocity = default, float speed = 8.4f) => new(
        id,
        TeamId.Home,
        TacticalRole.CentralMidfielder,
        new Vector3(position.X, 0f, position.Y),
        new Vector3(velocity.X, 0f, velocity.Y),
        1f)
    {
        Capability = new CapabilityEstimate(speed, 6.8f, 9.5f, 0.75f, ScoutingReport.ScoutingConfidence01)
    };

    private static PerceivedEntity Opponent(int id, Vector2 position, float speed = 8.4f) => new(
        id,
        TeamId.Away,
        TacticalRole.CenterBack,
        new Vector3(position.X, 0f, position.Y),
        Vector3.Zero,
        1f)
    {
        Capability = new CapabilityEstimate(speed, 6.8f, 9.5f, 0.75f, ScoutingReport.ScoutingConfidence01)
    };

    private static FootballerObservation Observation(params PerceivedEntity[] visible) => new()
    {
        SelfId = SelfId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CentralMidfielder,
        SelfPosition = Vector3.Zero,
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = new Vector3(BallXZ.X, 0f, BallXZ.Y),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = SelfId,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = SelfId,
        TacticalAnchorPosition = Vector3.Zero,
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
