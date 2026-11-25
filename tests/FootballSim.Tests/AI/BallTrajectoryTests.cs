using System;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A6.5 at the model level: the shared ball-trajectory estimate is the <b>real</b> solver rolled forward, so a
/// keeper's sweep race, dive and claim all answer from the ball's actual future path instead of three separate
/// straight-line guesses.
///
/// The claims here are narrow on purpose: the estimate follows the execution's own physics (within the error
/// that decimating the step introduces), its meeting query returns the earliest reachable sample, its height
/// filter refuses a ball above handling height, and its plane crossing is the ball's own crossing.
/// </summary>
public sealed class BallTrajectoryTests
{
    private static readonly PitchDimensions Pitch = new();

    private static BallState ShotFrom(float x, float z, Vector3 velocity, Vector3? spin = null) => new()
    {
        Position = new Vector3(x, 0.11f, z),
        PreviousPosition = new Vector3(x, 0.11f, z),
        Velocity = velocity,
        AngularVelocity = spin ?? Vector3.Zero
    };

    /// <summary>
    /// The estimate is the real solver's path, sampled: compared with a 60 Hz rollout at the same instants, the
    /// decimated estimate stays far inside the keeper's reach over the horizon it is used for. A different
    /// equation set (the linear extrapolation the dive used before) would not.
    ///
    /// The fixture keeps the ball airborne for the whole horizon on purpose: once it bounces, the two rollouts
    /// are integrating a contact-rich path, and a phase difference at one bounce is not a model difference.
    /// </summary>
    [Fact]
    public void TheEstimate_FollowsTheRealSolversPath()
    {
        BallState ball = ShotFrom(20f, 0f, new Vector3(-14f, 9f, 3f));
        var estimate = new BallTrajectoryEstimate(pitch: Pitch);
        Assert.True(estimate.TryBuild(ball, horizonSeconds: 0.8f, stepSeconds: BallTrajectoryEstimate.SampleStepSeconds));

        // The reference: the same ball stepped at the match's own tick rate.
        BallState reference = ball.Clone();
        var solver = new BallPhysicsSolver(pitch: Pitch);
        const float tick = 1f / 60f;

        float worst = 0f;
        int stepped = 0;
        for (int i = 0; i < estimate.Count; i++)
        {
            float target = estimate.TimeAt(i);
            int wanted = (int)MathF.Round(target / tick);
            while (stepped < wanted)
            {
                solver.Step(reference, tick);
                stepped++;
            }

            Vector3 expected = reference.Position;
            Vector3 actual = estimate.PositionAt(i);
            worst = MathF.Max(worst, Vector3.Distance(expected, actual));
        }

        Assert.True(worst < 0.35f,
            $"the sampled trajectory drifted {worst:F2} m from the 60 Hz rollout over one second");

        // And it is not a straight line: the climb decelerates under gravity, which a linear extrapolation
        // cannot express.
        float earlyRisePerStep = estimate.PositionAt(3).Y - estimate.Origin.Y;
        int last = estimate.Count - 1;
        float lateRisePerStep = estimate.PositionAt(last).Y - estimate.PositionAt(last - 3).Y;

        Assert.True(lateRisePerStep < earlyRisePerStep - 0.3f,
            $"the predicted climb did not decelerate ({earlyRisePerStep:F2} m then {lateRisePerStep:F2} m), " +
            "so this is not a ballistic trajectory");
    }

    /// <summary>
    /// The meeting query returns the earliest instant the player can be at the ball's future position, and the
    /// arrival it re-ports is consistent with that instant.
    /// </summary>
    [Fact]
    public void TheMeetingQuery_ReturnsTheEarliestReachableSample()
    {
        BallState ball = ShotFrom(10f, 0f, new Vector3(-6f, 0f, 0f));
        var estimate = new BallTrajectoryEstimate(pitch: Pitch);
        Assert.True(estimate.TryBuild(ball, horizonSeconds: 2f));

        // A body standing on the ball's path, 4 m ahead of it.
        ReachKinematics player = ReachKinematics.Neutral(new Vector2(6f, 0f), Vector2.Zero);
        Assert.True(estimate.TryMeet(player, horizonSeconds: 2f, maxHeight: float.MaxValue,
            out float seconds, out Vector3 point));

        Assert.True(seconds > 0f);
        Assert.True(Vector2.Distance(new Vector2(point.X, point.Z), new Vector2(6f, 0f)) <=
                    player.ReachRadius + 0.6f,
            $"the meeting point ({point.X:F2}, {point.Z:F2}) is not at the body that was already standing there");

        // Nothing can be met by a body that starts in the opposite corner inside a tenth of a second.
        ReachKinematics farAway = ReachKinematics.Neutral(new Vector2(-40f, 30f), Vector2.Zero);
        Assert.False(estimate.TryMeet(farAway, horizonSeconds: 0.1f, maxHeight: float.MaxValue,
            out _, out _));
    }

    /// <summary>
    /// A ball that sails above the keeper's handling height at the meeting instant is not a claimable ball: the
    /// height filter is what stops a keeper catching a ball over his own head.
    /// </summary>
    [Fact]
    public void ABallAboveTheHandlingHeight_IsNotClaimable()
    {
        // A lob that is still climbing when it passes over the keeper's head.
        BallState ball = ShotFrom(-40f, 0f, new Vector3(9f, 9f, 0f));
        var estimate = new BallTrajectoryEstimate(pitch: Pitch);
        Assert.True(estimate.TryBuild(ball, horizonSeconds: 1f));

        ReachKinematics keeper = ReachKinematics.Neutral(new Vector2(-30f, 0f), Vector2.Zero);

        bool claimable = estimate.TryMeet(keeper, 1f, GoalkeeperDecisionModel.HandlingHeightMeters, out _, out _);
        bool visible = estimate.TryMeet(keeper, 1f, float.MaxValue, out _, out _);

        Assert.True(visible, "the fixture's ball never crosses the keeper's position at all");
        Assert.False(claimable, "a ball above the handling height was reported as claimable");
    }

    /// <summary>
    /// The plane crossing is the ball's own crossing: a shot from twelve metres out crosses the goal line at
    /// the lateral position it was aimed at, in about the time the distance and speed imply.
    /// </summary>
    [Fact]
    public void ThePlaneCrossing_IsTheBallsOwnCrossing()
    {
        float goalLineX = -Pitch.HalfLength;
        BallState ball = ShotFrom(goalLineX + 12f, 0f, new Vector3(-14f, 0f, 2f));
        var estimate = new BallTrajectoryEstimate(pitch: Pitch);
        Assert.True(estimate.TryBuild(ball, horizonSeconds: 1.6f));

        Assert.True(estimate.TryFirstPlaneCrossing(goalLineX, out float seconds, out Vector3 point),
            "the shot never reached the goal line inside the horizon");

        // 12 m at 14 m/s: 0.86 s before the rolling resistance is paid, so a little over a second in practice;
        // the lateral velocity carries the ball about two metres across.
        Assert.InRange(seconds, 0.7f, 1.35f);
        Assert.InRange(point.X, goalLineX - 0.6f, goalLineX + 0.6f);
        Assert.InRange(point.Z, 1f, 2.8f);
        Assert.InRange(point.Y, -0.05f, 0.6f);
    }

    /// <summary>A held ball has no free trajectory, and invalidating an estimate drops its samples.</summary>
    [Fact]
    public void AHeldBall_HasNoTrajectory_AndInvalidationClearsTheSamples()
    {
        var estimate = new BallTrajectoryEstimate(pitch: Pitch);
        BallState held = ShotFrom(-40f, 0f, Vector3.Zero);
        held.HeldByPlayerId = 1;

        Assert.False(estimate.TryBuild(held));
        Assert.False(estimate.IsBuilt);

        Assert.True(estimate.TryBuild(ShotFrom(0f, 0f, new Vector3(5f, 0f, 0f)), horizonSeconds: 0.5f));
        Assert.True(estimate.IsBuilt);

        estimate.Invalidate();
        Assert.False(estimate.IsBuilt);
        Assert.False(estimate.TryFirstPlaneCrossing(10f, out _, out _));
    }

    [Fact]
    public void TryMeet_RejectsUnstoppableFlyBySample_WhenPlayerMomentumCarriesPastReach()
    {
        // Opening Pass 4 (t=288) geometry: Away #20 is flying away at 6.4 m/s while a 0.13 s sample
        // sits 1.09 m away inside reach right now, yet maximum braking still carries him outside reach.
        BallState ball = ShotFrom(-8.49f, -9.29f, new Vector3(-9.32f, 0f, 6.04f));
        var estimate = new BallTrajectoryEstimate(pitch: Pitch);
        Assert.True(estimate.TryBuild(ball, horizonSeconds: 2.0f));

        ReachKinematics player = ReachKinematics.Neutral(
            new Vector2(-10.79f, -8.61f),
            new Vector2(-2.56f, -5.89f));
        float maxHeight = BallContactRules.MaxContactHeight(keeperHandling: false, jumpingOrHeading: false);

        if (estimate.TryMeet(player, 2.0f, maxHeight, out float meetSeconds, out Vector3 meetPoint))
        {
            float aBrake = MathF.Max(1f, player.Acceleration * 1.45f * player.Balance01);
            Vector2 freeDriftPos = player.PositionXZ + player.VelocityXZ * meetSeconds;
            float minDistAtMeet = Vector2.Distance(freeDriftPos, new Vector2(meetPoint.X, meetPoint.Z))
                                  - 0.5f * aBrake * meetSeconds * meetSeconds;
            Assert.True(minDistAtMeet <= player.ReachRadius + 1e-3f,
                $"TryMeet returned unstoppable fly-by sample at t={meetSeconds:F3}s " +
                $"(minDistAtMeet={minDistAtMeet:F3}m > reach={player.ReachRadius:F3}m)");
        }
    }
}
