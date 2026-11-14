using System;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Scenarios;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A2.1 — one reach model, validated against the locomotion it claims to describe.
///
/// The point of these tests is not that a formula returns a number. It is that the number responds to
/// the things a reach estimate has to respond to (turning, acceleration, current velocity, fatigue,
/// an opposing velocity) and that it matches what <c>StepPlayerLocomotion</c> actually does when a
/// player is driven to a point. A model that divides distance by speed passes neither check.
/// </summary>
public sealed class TimeToReachModelTests
{
    private const float Dt = SimConstants.FixedDeltaTime;

    /// <summary>Neutral kinematics, so each test states only what it changes.</summary>
    private static ReachKinematics Kinematics(
        Vector2 position,
        Vector2 facing = default,
        Vector2 velocity = default,
        float maxSprintSpeed = 8.4f,
        float acceleration = 6.8f,
        float stamina = 1f,
        float balance = 1f)
    {
        ReachKinematics neutral = ReachKinematics.Neutral(position, velocity, facing);
        return neutral with
        {
            MaxSprintSpeed = maxSprintSpeed,
            Acceleration = acceleration,
            Stamina01 = stamina,
            Balance01 = balance
        };
    }

    // ---- what the model must respond to ---------------------------------------------------------

    [Fact]
    public void Turning_CostsTime_RunningTheSameWayDoesNot()
    {
        // A long chase, so the alignment penalty on top speed shows up over the cruise rather than
        // being lost in the acceleration ramp.
        Vector2 target = new(40f, 0f);
        ReachEstimate facingTarget = TimeToReachModel.ToPoint(
            Kinematics(Vector2.Zero, new Vector2(1f, 0f)), target, horizonSeconds: 8f);
        ReachEstimate facingAway = TimeToReachModel.ToPoint(
            Kinematics(Vector2.Zero, new Vector2(-1f, 0f)), target, horizonSeconds: 8f);
        ReachEstimate sideways = TimeToReachModel.ToPoint(
            Kinematics(Vector2.Zero, new Vector2(0f, 1f)), target, horizonSeconds: 8f);

        Assert.True(facingTarget.Reachable);
        // The alignment penalty only scales top speed (0.85 → 1.00), so the effect is small by
        // construction; the model must still express it, and in the right order.
        Assert.True(facingAway.Seconds > facingTarget.Seconds + 0.01f,
            $"facing away {facingAway.Seconds:F3}s vs facing the target {facingTarget.Seconds:F3}s");
        // A quarter turn is cheaper than a reversal, which is the whole point of averaging the turn.
        Assert.True(sideways.Seconds > facingTarget.Seconds,
            $"sideways {sideways.Seconds:F3}s vs facing the target {facingTarget.Seconds:F3}s");
        Assert.True(facingAway.Seconds > sideways.Seconds,
            $"reversal {facingAway.Seconds:F3}s vs quarter turn {sideways.Seconds:F3}s");
    }

    [Fact]
    public void Acceleration_MakesArrivalEarlierAndMonotonically()
    {
        Vector2 target = new(14f, 0f);
        float previous = float.MaxValue;
        string trace = string.Empty;
        for (float acceleration = 3f; acceleration <= 12f; acceleration += 1.5f)
        {
            ReachEstimate estimate = TimeToReachModel.ToPoint(
                Kinematics(Vector2.Zero, new Vector2(1f, 0f), acceleration: acceleration), target,
                horizonSeconds: 8f);
            trace += $"{acceleration:F1}m/s²={estimate.Seconds:F4}s ";
            Assert.True(estimate.Reachable, $"acceleration {acceleration:F1} m/s² was unreachable; {trace}");
            Assert.True(estimate.Seconds < previous,
                $"acceleration {acceleration:F1} m/s² gave {estimate.Seconds:F3}s, not below {previous:F3}s; {trace}");
            previous = estimate.Seconds;
        }
    }

    [Fact]
    public void CurrentVelocity_IsUsed_NotIgnored()
    {
        Vector2 target = new(16f, 0f);
        ReachKinematics standing = Kinematics(Vector2.Zero, new Vector2(1f, 0f));
        ReachKinematics alreadyRunning = standing with { VelocityXZ = new Vector2(7.5f, 0f) };
        // Running the wrong way: the model must know the momentum has to be turned around first.
        ReachKinematics runningAway = standing with { VelocityXZ = new Vector2(-7.5f, 0f) };

        float standingTime = TimeToReachModel.ToPoint(standing, target).Seconds;
        float runningTime = TimeToReachModel.ToPoint(alreadyRunning, target).Seconds;
        float awayTime = TimeToReachModel.ToPoint(runningAway, target).Seconds;

        Assert.True(runningTime < standingTime - 0.2f,
            $"already running {runningTime:F3}s vs standing {standingTime:F3}s");
        Assert.True(awayTime > standingTime + 0.2f,
            $"running the wrong way {awayTime:F3}s vs standing {standingTime:F3}s");
    }

    [Fact]
    public void Fatigue_AndRecoveryBalance_SlowThePlayerDown()
    {
        Vector2 target = new(18f, 0f);
        ReachKinematics fresh = Kinematics(Vector2.Zero, new Vector2(1f, 0f));
        ReachKinematics tired = Kinematics(Vector2.Zero, new Vector2(1f, 0f), stamina: 0.3f);
        ReachKinematics offBalance = Kinematics(Vector2.Zero, new Vector2(1f, 0f), balance: 0.4f);

        float freshTime = TimeToReachModel.ToPoint(fresh, target, horizonSeconds: 8f).Seconds;
        float tiredTime = TimeToReachModel.ToPoint(tired, target, horizonSeconds: 8f).Seconds;
        float offBalanceTime = TimeToReachModel.ToPoint(offBalance, target, horizonSeconds: 8f).Seconds;

        Assert.True(tiredTime > freshTime + 0.3f,
            $"fatigued {tiredTime:F3}s vs fresh {freshTime:F3}s");
        Assert.True(offBalanceTime > freshTime + 0.1f,
            $"off balance {offBalanceTime:F3}s vs fresh {freshTime:F3}s");
    }

    [Fact]
    public void OpposingVelocity_ChangesTheMeetingTime()
    {
        ReachKinematics chaser = Kinematics(Vector2.Zero, new Vector2(1f, 0f));
        Vector2 ball = new(6f, 0f);

        ReachEstimate stationary = TimeToReachModel.ToMovingPoint(chaser, ball, Vector2.Zero);
        ReachEstimate rollingAway = TimeToReachModel.ToMovingPoint(chaser, ball, new Vector2(4f, 0f));
        ReachEstimate rollingToward = TimeToReachModel.ToMovingPoint(chaser, ball, new Vector2(-4f, 0f));

        Assert.True(rollingAway.Seconds > stationary.Seconds + 0.2f,
            $"ball rolling away {rollingAway.Seconds:F3}s vs stationary {stationary.Seconds:F3}s");
        Assert.True(rollingToward.Seconds < stationary.Seconds - 0.2f,
            $"ball rolling toward the chaser {rollingToward.Seconds:F3}s vs stationary {stationary.Seconds:F3}s");

        // The meeting point is on the ball's path, ahead of where the ball is now.
        Assert.True(rollingAway.MeetingPointXZ.X > ball.X + 1f,
            $"meeting point {rollingAway.MeetingPointXZ.X:F2} m is not ahead of the ball at {ball.X:F2} m");
    }

    [Fact]
    public void ALateralRunner_IsMetAtTheFuturePosition_NotTheInitialOne()
    {
        // The model used to build the path toward (10, 0) and then test it against a target that had
        // moved to (10, 3t): a player who can reach (10, 7.5) in 1.88 s was declared unable to intercept
        // him at all. Running at the lead point meets him long before the horizon.
        ReachKinematics chaser = Kinematics(Vector2.Zero, new Vector2(1f, 0f));
        Vector2 start = new(10f, 0f);
        Vector2 velocity = new(0f, 3f);

        ReachEstimate estimate = TimeToReachModel.ToMovingPoint(chaser, start, velocity);

        Assert.True(estimate.Reachable, "the lateral runner is interceptable inside the horizon");
        Assert.True(estimate.Seconds < 2.0f, $"intercept took {estimate.Seconds:F2}s");
        Assert.True(estimate.MeetingPointXZ.X is > 9.5f and < 10.5f,
            $"meeting point x={estimate.MeetingPointXZ.X:F2} is not on the runner's line");
        Assert.True(estimate.MeetingPointXZ.Y > 2f,
            $"meeting point y={estimate.MeetingPointXZ.Y:F2} is at the runner's start, not ahead of it");

        // The reported meeting point is consistent with the reported time: the target really is there.
        Vector2 targetAtArrival = start + velocity * estimate.Seconds;
        Assert.True(Vector2.Distance(estimate.MeetingPointXZ, targetAtArrival) < 1e-3f,
            $"meeting point {estimate.MeetingPointXZ} is not the target at {estimate.Seconds:F3}s " +
            $"({targetAtArrival})");
    }

    [Fact]
    public void AReachableLateralRunner_IsNotDeclaredUnreachableByTheInitialAim()
    {
        // The same geometry as the audit reproduction, stated as the consistency that was missing: if the
        // runner's eventual position is reachable before the horizon, the meeting with the runner is too.
        ReachKinematics chaser = Kinematics(Vector2.Zero, new Vector2(1f, 0f));
        Vector2 start = new(10f, 0f);
        Vector2 velocity = new(0f, 3f);
        float horizon = TimeToReachModel.DefaultHorizonSeconds;
        Vector2 eventual = start + velocity * horizon;

        ReachEstimate toEventual = TimeToReachModel.ToPoint(chaser, eventual, horizon);
        ReachEstimate meeting = TimeToReachModel.ToMovingPoint(chaser, start, velocity, horizon);

        Assert.True(toEventual.Reachable);
        Assert.True(meeting.Reachable);
        Assert.True(meeting.Seconds <= toEventual.Seconds,
            $"meeting the runner ({meeting.Seconds:F3}s) cannot be later than reaching his eventual " +
            $"position ({toEventual.Seconds:F3}s)");
    }

    [Fact]
    public void Speed_SearchesStrictlyFaster_SoAFasterDefenderCanNeverLookSafer()
    {
        // The A2 exit gate's mechanism: pass safety is scored from arrival times, so a faster defender
        // must never produce a *better* (larger) arrival time. Monotone in speed.
        Vector2 lanePoint = new(11f, 3.5f);
        float previous = float.MaxValue;
        for (float speed = 5.5f; speed <= 10.5f; speed += 0.5f)
        {
            ReachKinematics defender = Kinematics(new Vector2(0f, -4f), new Vector2(0f, 1f), maxSprintSpeed: speed);
            float arrival = TimeToReachModel.ToPoint(defender, lanePoint).Seconds;
            Assert.True(arrival < previous, $"speed {speed:F1} m/s gave {arrival:F3}s, not below {previous:F3}s");
            previous = arrival;
        }
    }

    [Fact]
    public void Interception_IsUnreachableWhenTheBallOutrunsThePlayer()
    {
        // 14 m/s away from a player who tops out at 8.4 m/s: no meeting exists inside any horizon.
        ReachKinematics chaser = Kinematics(Vector2.Zero, new Vector2(1f, 0f));
        bool caught = TimeToReachModel.TrySolveInterception(
            chaser, new Vector2(2f, 0f), new Vector2(14f, 0f), out _, out float seconds);

        Assert.False(caught);
        Assert.True(seconds > 0f && float.IsFinite(seconds));
    }

    [Fact]
    public void Interception_MeetsAThroughBallFurtherUpfield()
    {
        // A ball rolling away at 5 m/s while the chaser starts 3 m behind it: the meeting must be
        // ahead of the ball, not at the ball's current position.
        ReachKinematics chaser = Kinematics(Vector2.Zero, new Vector2(1f, 0f));
        Assert.True(TimeToReachModel.TrySolveInterception(
            chaser, new Vector2(3f, 0f), new Vector2(5f, 0f), out Vector2 meeting, out float seconds));

        Assert.True(seconds > 0.2f, $"arrival {seconds:F3}s");
        Assert.True(meeting.X > 5.5f, $"meeting point {meeting.X:F2} m, ball started at 3 m");
    }

    [Fact]
    public void AlreadyInReach_ArrivesAtZeroSeconds()
    {
        ReachKinematics chaser = Kinematics(Vector2.Zero, new Vector2(1f, 0f));
        ReachEstimate estimate = TimeToReachModel.ToPoint(chaser, new Vector2(0.5f, 0f));

        Assert.True(estimate.Reachable);
        Assert.Equal(0f, estimate.Seconds);
    }

    [Fact]
    public void NonFiniteInput_IsRejectedInsteadOfReturningNaN()
    {
        ReachKinematics broken = Kinematics(Vector2.Zero, new Vector2(1f, 0f)) with
        {
            PositionXZ = new Vector2(float.NaN, 0f)
        };
        ReachEstimate estimate = TimeToReachModel.ToPoint(broken, new Vector2(10f, 0f));

        Assert.False(estimate.Reachable);
        Assert.True(float.IsFinite(estimate.Seconds));
        Assert.True(float.IsFinite(estimate.MeetingPointXZ.X));
    }

    [Fact]
    public void TurnFactor_AveragesDownToTheAlignedCap()
    {
        // No turn error is no cost; a long trip with a short turn loses only the turn's share.
        Assert.Equal(1f, TimeToReachModel.AverageTurnFactor(0f, 9.5f, 3f));
        float longTrip = TimeToReachModel.AverageTurnFactor(MathF.PI, 9.5f, 3f);
        float shortTrip = TimeToReachModel.AverageTurnFactor(MathF.PI, 9.5f, 0.4f);
        Assert.True(longTrip > 0.95f && longTrip < 1f, $"long trip factor {longTrip:F4}");
        Assert.True(shortTrip < longTrip, $"short trip {shortTrip:F4} vs long trip {longTrip:F4}");
        Assert.True(shortTrip >= 0.85f);
    }

    // ---- against real locomotion ----------------------------------------------------------------

    /// <summary>
    /// One locomotion race: a player is driven at a fixed point for as long as it takes, and the test
    /// records the tick on which the real simulation first had them inside contact reach. Nothing here
    /// asserts behaviour — the fixture exists to be measured.
    /// </summary>
    private static (float Measured, float Predicted) RaceToPoint(
        Vector3 start,
        Vector3 velocity,
        Vector2 facing,
        Vector3 target)
    {
        var scenario = new MatchScenario
        {
            Name = "reach-race",
            BallPosition = new Vector3(20f, 0f, -24f),
            BallSetup = ScenarioBallSetup.Loose
        };

        scenario.Add(new ScenarioPlayer
        {
            Id = 1,
            Team = TeamId.Home,
            Role = TacticalRole.CentralMidfielder,
            ShirtNumber = 5,
            Position = start,
            Velocity = velocity,
            FacingXZ = facing,
            Controller = new PursuitController(target)
        });
        scenario.Add(new ScenarioPlayer
        {
            Id = 2,
            Team = TeamId.Away,
            Role = TacticalRole.CenterBack,
            ShirtNumber = 4,
            Position = new Vector3(30f, 0f, 20f),
            Controller = new FixedCommandController(ScenarioCommands.Hold(), "hold")
        });
        scenario.Validate();

        var engine = new MatchSimulationEngine(scenario);
        FootballerState player = engine.Players[0];
        float reach = BallContactRules.ReachRadius(
            player.Attributes.ReachRadius, isGoalkeeper: false, isDiving: false,
            requestingTackle: false, receivingOrDribbling: true);

        ReachEstimate predicted = TimeToReachModel.ToPoint(
            ReachKinematics.From(player), new Vector2(target.X, target.Z), horizonSeconds: 8f);

        int arrivalTick = -1;
        for (int tick = 1; tick <= 300 && arrivalTick < 0; tick++)
        {
            engine.StepTick();
            if (PlanarDistance(engine.Players[0].Position, target) <= reach)
                arrivalTick = tick;
        }

        Assert.True(arrivalTick > 0, "the player never arrived; the fixture is wrong, not the model");
        Assert.True(predicted.Reachable);
        return (arrivalTick * Dt, predicted.Seconds);
    }

    [Fact]
    public void Prediction_MatchesRealLocomotion_FromRest()
    {
        (float measured, float predicted) = RaceToPoint(
            new Vector3(0f, 0f, 0f), Vector3.Zero, new Vector2(1f, 0f), new Vector3(12f, 0f, 0f));

        AssertWithinLocomotionTolerance(measured, predicted, "straight sprint from rest");
    }

    [Fact]
    public void Prediction_MatchesRealLocomotion_WhenAlreadyRunning()
    {
        (float measured, float predicted) = RaceToPoint(
            new Vector3(0f, 0f, 0f), new Vector3(7.5f, 0f, 0f), new Vector2(1f, 0f), new Vector3(18f, 0f, 0f));

        AssertWithinLocomotionTolerance(measured, predicted, "sprint at speed");
    }

    [Fact]
    public void Prediction_MatchesRealLocomotion_OnAReversal()
    {
        // Facing and running the wrong way: the model must account for stopping and turning around.
        (float measured, float predicted) = RaceToPoint(
            new Vector3(0f, 0f, 0f), new Vector3(-6f, 0f, 0f), new Vector2(-1f, 0f), new Vector3(12f, 0f, 0f));

        AssertWithinLocomotionTolerance(measured, predicted, "reversal");
    }

    [Fact]
    public void Prediction_PreservesTheOrderingOfRealArrivals()
    {
        // Elections only ever compare candidates, so ordering matters more than absolutes. Each case
        // is a different player state and target; the model's order must equal the measured order.
        (Vector3 Start, Vector3 Velocity, Vector2 Facing, Vector3 Target)[] cases =
        [
            (new Vector3(0f, 0f, 0f), Vector3.Zero, new Vector2(1f, 0f), new Vector3(10f, 0f, 0f)),
            (new Vector3(0f, 0f, -6f), Vector3.Zero, new Vector2(1f, 0f), new Vector3(12f, 0f, -6f)),
            (new Vector3(0f, 0f, 6f), new Vector3(4f, 0f, 0f), new Vector2(1f, 0f), new Vector3(12f, 0f, 6f)),
            (new Vector3(0f, 0f, -14f), new Vector3(-5f, 0f, 0f), new Vector2(-1f, 0f), new Vector3(15f, 0f, -14f))
        ];

        float[] measuredTimes = new float[cases.Length];
        float[] predictedTimes = new float[cases.Length];
        for (int i = 0; i < cases.Length; i++)
        {
            (measuredTimes[i], predictedTimes[i]) = RaceToPoint(
                cases[i].Start, cases[i].Velocity, cases[i].Facing, cases[i].Target);
        }

        Assert.Equal(Rank(measuredTimes), Rank(predictedTimes));
    }

    private static void AssertWithinLocomotionTolerance(float measured, float predicted, string label)
    {
        float error = MathF.Abs(predicted - measured);
        // The model is an estimate of a controller that re-aims every tick, not a replay of it; the
        // tolerance is stated in ticks so a failure says how far off it is in simulation terms.
        Assert.True(error <= 0.10f,
            $"{label}: measured {measured:F3}s, predicted {predicted:F3}s, error {error * 60f:F1} ticks");
    }

    private static float PlanarDistance(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// Runs at a fixed point, re-aiming every tick — the same thing the reach model assumes a player
    /// would be told to do, and the fairest possible subject for the comparison.
    /// </summary>
    private sealed class PursuitController : IFootballerController
    {
        private readonly Vector3 _target;

        public PursuitController(Vector3 target) => _target = target;

        public ControllerSourceType SourceType => ControllerSourceType.Scripted;

        public FootballerCommand Decide(in FootballerObservation observation, float dt)
        {
            Vector2 delta = new(_target.X - observation.SelfPosition.X, _target.Z - observation.SelfPosition.Z);
            Vector2 direction = delta.LengthSquared() > 1e-4f ? Vector2.Normalize(delta) : new Vector2(1f, 0f);
            return FootballerCommand.Idle with
            {
                DesiredMoveDirectionXZ = direction,
                DesiredFacingDirectionXZ = direction,
                Sprint = true
            };
        }

        public string GetDebugSummary() => "pursuit";
    }

    /// <summary>Dense ranking of times, so ties do not hide a genuine ordering difference.</summary>
    private static int[] Rank(float[] times)
    {
        int[] order = new int[times.Length];
        for (int i = 0; i < times.Length; i++)
        {
            order[i] = 0;
            for (int j = 0; j < times.Length; j++)
                if (times[j] < times[i]) order[i]++;
        }
        return order;
    }
}
