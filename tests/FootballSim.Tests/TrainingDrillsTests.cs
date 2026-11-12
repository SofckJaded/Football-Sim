using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;
using FootballSim;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Training;

namespace FootballSim.Tests;

public sealed class TrainingDrillsTests
{
    private const float Dt = SimConstants.FixedDeltaTime;

    [Fact]
    public void Registry_OffersFreeTrainingGoalkeeperAndBothPassingDrills()
    {
        Assert.Contains(TrainingDrillRegistry.All, d => d.Id == FreeTrainingDrill.DrillId);
        Assert.Contains(TrainingDrillRegistry.All, d => d.Id == FreeTrainingGoalkeeperDrill.DrillId);
        Assert.Contains(TrainingDrillRegistry.All, d => d.Id == PassingReceivingOneDrill.DrillId);
        Assert.Contains(TrainingDrillRegistry.All, d => d.Id == PassingReceivingTwoDrill.DrillId);
        Assert.Equal(4, TrainingDrillRegistry.All.Count);
    }

    [Fact]
    public void FreeTrainingWithGoalkeeper_SpawnsKeeperDefendingTheGoal()
    {
        var session = new TrainingSession(new FreeTrainingGoalkeeperDrill());

        Assert.Equal(2, session.Players.Count);

        FootballerState keeper = session.GetPlayer(FreeTrainingGoalkeeperDrill.GoalkeeperPlayerId)!;
        Assert.Equal(TacticalRole.Goalkeeper, keeper.Role);
        Assert.Equal(TeamId.Away, keeper.Team);
        Assert.True(keeper.Position.X > session.Pitch.HalfLength - 4f);
        Assert.Equal(FreeTrainingGoalkeeperDrill.GoalkeeperPlayerId, session.Players[1].Id);
    }

    [Fact]
    public void FreeTrainingWithGoalkeeper_KeeperStaysNearItsGoal()
    {
        var session = new TrainingSession(new FreeTrainingGoalkeeperDrill());
        FootballerState keeper = session.GetPlayer(FreeTrainingGoalkeeperDrill.GoalkeeperPlayerId)!;

        for (int i = 0; i < 180; i++)
            session.Step(Dt);

        Assert.True(keeper.Position.X > 40f,
            $"keeper drifted off its line toward the ball (x={keeper.Position.X:F1})");
    }

    [Fact]
    public void PassingOne_AiPassesAndNeverResetsOnReception()
    {
        var drill = new PassingReceivingOneDrill();
        var session = new TrainingSession(drill, resetDelaySeconds: 0.3f);

        Assert.Equal(2, session.Players.Count);

        StepUntil(session, () => drill.AiPasses >= 1);
        Assert.True(drill.AiPasses >= 1);

        // The trainee receives and just holds the ball: no respawn, the rally is live.
        StepUntil(session, () => session.HumanTouchedBall, 600);
        Assert.True(session.HumanTouchedBall);

        for (int i = 0; i < 300; i++)
            session.Step(Dt);

        Assert.Equal(0, session.Resets);
        Assert.Equal(0, session.GoalsScored);
    }

    [Fact]
    public void PassingOne_ReturnPassLeadsToAnotherAiPass()
    {
        var drill = new PassingReceivingOneDrill();
        var session = new TrainingSession(drill, resetDelaySeconds: 0.3f);

        StepUntil(session, () => session.HumanTouchedBall, 600);
        Assert.True(session.HumanTouchedBall);

        PassBackToAi(session);

        StepUntil(session, () => drill.AiPasses >= 2, 900);
        Assert.True(drill.AiPasses >= 2,
            $"the AI did not play a second pass after the return (passes={drill.AiPasses})");
    }

    [Fact]
    public void PassingTwo_AiWaitsForTheCallAndConsumesIt()
    {
        var drill = new PassingReceivingTwoDrill();
        var session = new TrainingSession(drill, resetDelaySeconds: 0.3f);

        for (int i = 0; i < 180; i++)
            session.Step(Dt);
        Assert.Equal(0, drill.AiPasses);

        session.Human.PassPressed = true;
        StepUntil(session, () => drill.AiPasses >= 1, 600);
        Assert.True(drill.AiPasses >= 1);

        StepUntil(session, () => session.HumanTouchedBall, 600);
        Assert.True(session.HumanTouchedBall);

        // Return the ball while the old call has already been consumed: the AI must hold it.
        PassBackToAi(session);
        StepUntil(session, () => session.Ball.LastTouchedByPlayerId == PassingDrillBase.AiPlayerId, 600);
        for (int i = 0; i < 300; i++)
            session.Step(Dt);

        Assert.Equal(1, drill.AiPasses);

        session.Human.PassPressed = true;
        StepUntil(session, () => drill.AiPasses >= 2, 600);
        Assert.True(drill.AiPasses >= 2);
    }

    [Fact]
    public void PassingTwo_WithoutACallTheAiNeverReleasesTheBall()
    {
        var drill = new PassingReceivingTwoDrill();
        var session = new TrainingSession(drill, resetDelaySeconds: 0.3f);
        TrainingSpawn spawn = session.Drill.CreateSpawn(session.Pitch);

        for (int i = 0; i < 300; i++)
            session.Step(Dt);

        Assert.Equal(0, drill.AiPasses);
        Assert.True(Vector3.Distance(session.Ball.Position, spawn.BallPosition) < 2.5f,
            $"the ball left the stationary passer without a call (x={session.Ball.Position.X:F1}, z={session.Ball.Position.Z:F1})");
    }

    private static void StepUntil(TrainingSession session, Func<bool> condition, int maxTicks = 600)
    {
        for (int i = 0; i < maxTicks && !condition(); i++)
            session.Step(Dt);
    }

    private static void PassBackToAi(TrainingSession session)
    {
        session.Human.MoveStickXZ = new Vector2(-1f, 0f);
        session.Human.UpdateButtonCharge(KickIntentType.GroundPass, 0.1f);
        session.Human.UpdateButtonCharge(KickIntentType.GroundPass, 0.4f);
        session.Human.ReleaseChargedKick(KickIntentType.GroundPass, 0.6f);

        // Execute the pass, then stop steering so the trainee does not chase the return.
        session.Step(Dt);
        session.Human.MoveStickXZ = Vector2.Zero;
    }
}
