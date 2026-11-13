using System;
using System.Numerics;
using FootballSim;
using FootballSim.Core;
using FootballSim.Physics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A4.2 follow-up — the planner's power search for a *moving* receiver.
///
/// The old code retried at maximum power when the crisp power could not meet the receiver, which for a
/// runner produced the longest possible flight (measured as a 43 m punt to a team-mate 12 m away). The
/// search now steps one notch at a time and takes the first power that connects, so the ball arrives as
/// early as it can — which is what makes it controllable when it does.
/// </summary>
public sealed class PassPowerSearchTests
{
    private static readonly PitchDimensions Pitch = new();

    /// <summary>
    /// A4.2's original property: a pass to a jogging receiver does not jump to maximum power, and the
    /// planner's own contract holds — the ball arrives within a metre of where the receiver will be.
    /// </summary>
    [Fact]
    public void APassToAJoggingReceiver_DoesNotJumpToMaximumPower()
    {
        const float runnerSpeed = 5f;
        var planner = new PassPlanner(Pitch);
        Vector2 receiverStart = new(10f, 0f);
        Vector2 receiverVelocity = new(runnerSpeed, 0f);

        PassPlan plan = planner.PlanGroundPass(
            originXZ: Vector2.Zero,
            receiverId: 9,
            receiverPositionXZ: receiverStart,
            receiverVelocityXZ: receiverVelocity,
            receiverMaxSpeed: 8f,
            extraLeadMeters: 0f,
            extraLeadDirection: default,
            maxLeadMeters: 12f);

        Assert.True(plan.Feasible);
        Assert.True(plan.Power01 < 1.0f,
            $"a {runnerSpeed} m/s runner was met with {plan.Power01:P0} power — the maximum-power retry is back");
        Assert.True(plan.FlightTimeSeconds > 0f);

        // The planner's own contract: the ball arrives within a metre of where the receiver will be.
        Vector2 receiverAtArrival = receiverStart + receiverVelocity * plan.FlightTimeSeconds;
        float miss = Vector2.Distance(plan.TargetXZ, receiverAtArrival);
        Assert.True(miss <= 1.0f, $"the planned meeting missed the runner by {miss:F2} m");
    }

    /// <summary>
    /// A4.11 restated the invariant for a sprinter: the planner must never plan a punt. It either finds a
    /// real meeting inside the lead bound or refuses the pass. The old code froze the receiver at the
    /// bound and called that feasible — the ball was aimed at a point the runner had already left, which
    /// is why an "uncatchable" sprinter was reported as catchable and a pass looked like it went nowhere.
    /// </summary>
    [Fact]
    public void APassToASprinter_IsRefusedRatherThanPunted()
    {
        const float maxLead = 12f;
        var planner = new PassPlanner(Pitch);
        Vector2 receiverStart = new(10f, 0f);
        Vector2 receiverVelocity = new(8f, 0f);

        PassPlan plan = planner.PlanGroundPass(
            originXZ: Vector2.Zero,
            receiverId: 9,
            receiverPositionXZ: receiverStart,
            receiverVelocityXZ: receiverVelocity,
            receiverMaxSpeed: 8f,
            extraLeadMeters: 0f,
            extraLeadDirection: default,
            maxLeadMeters: maxLead);

        if (!plan.Feasible) return;   // refused: the honest answer for a ball that only exists as a punt

        float lead = Vector2.Distance(receiverStart, plan.TargetXZ);
        Assert.InRange(lead, 0f, maxLead + 0.5f);
        Vector2 receiverAtArrival = receiverStart + receiverVelocity * plan.FlightTimeSeconds;
        Assert.True(Vector2.Distance(plan.TargetXZ, receiverAtArrival) <= 1.0f,
            "a feasible plan must be a real meeting, not a ball played at a frozen runner");
    }

    /// <summary>
    /// The planner's <b>default</b> lead is bounded (A4.11). The assisted human pass, the keeper's
    /// distribution and a recycle all rely on the default, and it used to be 60 m — so the one path a
    /// player uses to pass did not get the bound A4.2 gave the AI, and an assisted ball to a runner
    /// became a punt. A caller that does not think about the lead must still get a bounded one.
    /// </summary>
    [Fact]
    public void TheDefaultLead_IsBounded()
    {
        var planner = new PassPlanner(Pitch);
        Vector2 receiverStart = new(15f, 0f);

        PassPlan plan = planner.PlanGroundPass(
            originXZ: Vector2.Zero,
            receiverId: 9,
            receiverPositionXZ: receiverStart,
            receiverVelocityXZ: new Vector2(5f, 0f),
            receiverMaxSpeed: 8f);

        Assert.True(plan.Feasible);
        float lead = Vector2.Distance(receiverStart, plan.TargetXZ);
        Assert.InRange(lead, 0f, PassPlanner.DefaultMaxLeadMeters + 0.5f);
        Assert.True(PassPlanner.DefaultMaxLeadMeters <= 15f,
            "an unbounded default is how a pass to feet became a punt");
    }

    /// <summary>A team-mate standing still is unaffected: the pass is still the firm, crisp ball.</summary>
    [Fact]
    public void APassToAStationaryReceiver_StaysFirm()
    {
        var planner = new PassPlanner(Pitch);

        PassPlan plan = planner.PlanGroundPass(
            Vector2.Zero, 9, new Vector2(20f, 0f), Vector2.Zero, 8f);

        Assert.True(plan.Feasible);
        Assert.True(plan.Power01 >= 0.80f,
            $"a 20 m pass to a stationary receiver was struck at {plan.Power01:P0}");
        Assert.True(plan.FlightTimeSeconds < 1.75f,
            $"a 20 m pass took {plan.FlightTimeSeconds:F2}s of flight");
    }

    /// <summary>The step never exceeds what the ball can physically do: an uncatchable runner is infeasible.</summary>
    [Fact]
    public void AnUncatchableRunner_IsInfeasibleRatherThanOverpowered()
    {
        var planner = new PassPlanner(Pitch);

        PassPlan plan = planner.PlanGroundPass(
            Vector2.Zero, 9, new Vector2(30f, 0f), new Vector2(8f, 0f), 8f,
            extraLeadMeters: 0f, extraLeadDirection: default, maxLeadMeters: 0f);

        // Either the planner finds a legal meeting or it reports infeasible; what it must never do is
        // return a plan whose ball cannot reach the receiver.
        if (!plan.Feasible) return;
        Assert.True(plan.DistanceMeters <= planner.MaxPassDistance + 0.5f);
    }
}
