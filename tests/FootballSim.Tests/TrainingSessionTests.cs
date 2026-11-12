using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;
using FootballSim;
using FootballSim.Core;
using FootballSim.Physics;
using FootballSim.Training;

namespace FootballSim.Tests;

public sealed class TrainingSessionTests
{
    private const float Dt = SimConstants.FixedDeltaTime;

    [Fact]
    public void MatchSetup_DefaultsToFourMinuteHalvesAndStriker()
    {
        var setup = new MatchSetupOptions();

        Assert.Equal(240f, setup.HalfDurationSeconds);
        Assert.Equal(4, setup.HalfLengthMinutes);
        Assert.Equal(9, setup.HumanPlayerId);
        Assert.Equal("Striker", setup.Position.Label);
        Assert.False(setup.IsSpectatorMatch);
    }

    [Fact]
    public void MatchSetup_NoneMeansFullAiSimulation()
    {
        var setup = new MatchSetupOptions { HumanPlayerId = null };

        Assert.True(setup.IsSpectatorMatch);
        Assert.Equal("None — full AI simulation", setup.Position.Label);
        Assert.Null(setup.Position.PlayerId);
    }

    [Fact]
    public void MatchPositions_CoverEveryOutfieldShirtAndNone()
    {
        Assert.Null(MatchPositions.Find(null).PlayerId);
        Assert.Equal(1, MatchPositions.Find(1)!.PlayerId);
        Assert.Equal("Right Back", MatchPositions.Find(2)!.Label);
        Assert.Equal("Left Wing", MatchPositions.Find(11)!.Label);
        Assert.Equal(9, MatchPositions.DefaultPlayerId);
        Assert.Equal(MatchPositions.IndexOf(9), MatchPositions.IndexOf(MatchPositions.Default.PlayerId));
    }

    [Fact]
    public void FreeTraining_SpawnsPlayerAndBallNearTheBoxButOutsideIt()
    {
        var pitch = new PitchDimensions();
        var drill = new FreeTrainingDrill();
        TrainingSpawn spawn = drill.CreateSpawn(pitch);

        float boxEdgeX = pitch.HalfLength - pitch.PenaltyAreaLength; // 36.0 m
        float boxHalfWidth = pitch.PenaltyAreaWidth * 0.5f;

        Assert.True(spawn.BallPosition.X < boxEdgeX);
        Assert.True(spawn.PlayerPosition.X < boxEdgeX);
        Assert.True(boxEdgeX - spawn.BallPosition.X <= 5f);
        Assert.True(boxEdgeX - spawn.PlayerPosition.X <= 6f);
        Assert.False(IsInsidePenaltyArea(spawn.BallPosition, pitch, boxEdgeX, boxHalfWidth));
        Assert.False(IsInsidePenaltyArea(spawn.PlayerPosition, pitch, boxEdgeX, boxHalfWidth));
        Assert.True(spawn.PlayerPosition.X < spawn.BallPosition.X);
    }

    [Fact]
    public void FreeTraining_SessionStartsOnSpawnWithOnePlayer()
    {
        var session = new TrainingSession(new FreeTrainingDrill());
        TrainingSpawn spawn = session.Drill.CreateSpawn(session.Pitch);

        Assert.Single(session.Players);
        Assert.Equal(spawn.PlayerPosition, session.Player.Position);
        Assert.Equal(spawn.BallPosition, session.Ball.Position);
        Assert.Equal(0, session.GoalsScored);
        Assert.Equal(0, session.BallsOutOfPlay);
        Assert.Equal(0, session.Resets);
        Assert.Null(session.ActiveOutcome);
    }

    [Fact]
    public void FreeTraining_HumanInputMovesTheTrainee()
    {
        var session = new TrainingSession(new FreeTrainingDrill());
        float startX = session.Player.Position.X;

        session.Human.MoveStickXZ = new Vector2(1f, 0f);
        for (int i = 0; i < 60; i++)
            session.Step(Dt);

        Assert.True(session.Player.Position.X > startX + 1f,
            $"expected the trainee to advance, started at {startX:F2} and ended at {session.Player.Position.X:F2}");
    }

    [Fact]
    public void FreeTraining_GoalCountsAndRespawnsBallAndPlayer()
    {
        var session = new TrainingSession(new FreeTrainingDrill(), resetDelaySeconds: 0.5f);
        TrainingSpawn spawn = session.Drill.CreateSpawn(session.Pitch);

        session.Ball.Position = new Vector3(session.Pitch.HalfLength + 1f, 0.11f, 0f);
        session.Ball.PreviousPosition = session.Ball.Position;
        session.Ball.Velocity = Vector3.Zero;
        session.Ball.AngularVelocity = Vector3.Zero;

        session.Step(Dt);

        Assert.Equal(1, session.GoalsScored);
        Assert.Equal(TrainingEventKind.Goal, session.ActiveOutcome);
        Assert.True(session.IsResetting);

        // Play is frozen out of the way; once the celebration timer elapses both respawn.
        for (int i = 0; i < 40; i++)
            session.Step(Dt);

        Assert.Equal(1, session.Resets);
        Assert.Null(session.ActiveOutcome);
        Assert.Equal(spawn.BallPosition, session.Ball.Position);
        Assert.Equal(spawn.PlayerPosition, session.Player.Position);
        Assert.Equal(Vector3.Zero, session.Ball.Velocity);
    }

    [Fact]
    public void FreeTraining_GoalSettlesTheBallInsideTheNet()
    {
        var session = new TrainingSession(new FreeTrainingDrill(), resetDelaySeconds: 2f);

        session.Ball.Position = new Vector3(session.Pitch.HalfLength + 0.2f, 0.4f, 0f);
        session.Ball.PreviousPosition = session.Ball.Position;
        session.Ball.Velocity = new Vector3(20f, 0f, 0f);

        session.Step(Dt);

        Assert.Equal(1, session.GoalsScored);
        Assert.Equal(Vector3.Zero, session.Ball.Velocity);
        Assert.True(session.Ball.Position.X > session.Pitch.HalfLength);

        for (int i = 0; i < 20; i++)
            session.Step(Dt);

        Assert.True(session.Ball.Position.X > session.Pitch.HalfLength,
            "the ball bounced back out of the goal after scoring");
    }

    [Fact]
    public void FreeTraining_BallOutOfPlayRespawnsBallAndPlayer()
    {
        var session = new TrainingSession(new FreeTrainingDrill(), resetDelaySeconds: 0.5f);
        TrainingSpawn spawn = session.Drill.CreateSpawn(session.Pitch);

        session.Ball.Position = new Vector3(0f, 0.11f, session.Pitch.HalfWidth + 1.5f);
        session.Ball.PreviousPosition = session.Ball.Position;
        session.Ball.Velocity = Vector3.Zero;

        session.Step(Dt);

        Assert.Equal(0, session.GoalsScored);
        Assert.Equal(1, session.BallsOutOfPlay);
        Assert.Equal(TrainingEventKind.BallOutOfPlay, session.ActiveOutcome);

        for (int i = 0; i < 40; i++)
            session.Step(Dt);

        Assert.Equal(1, session.Resets);
        Assert.Equal(spawn.BallPosition, session.Ball.Position);
        Assert.Equal(spawn.PlayerPosition, session.Player.Position);
    }

    [Fact]
    public void FreeTraining_LiveShotScoresThroughTheNormalInputPath()
    {
        var session = new TrainingSession(new FreeTrainingDrill(), resetDelaySeconds: 0.5f);
        TrainingSpawn spawn = session.Drill.CreateSpawn(session.Pitch);

        // Stand within striking distance of the ball, facing the goal, and take a charged shot.
        session.Player.Position = new Vector3(spawn.BallPosition.X - 0.4f, 0f, spawn.BallPosition.Z);
        session.Player.PreviousPosition = session.Player.Position;
        session.Player.FacingXZ = new Vector2(1f, 0f);
        session.Ball.Position = spawn.BallPosition;
        session.Ball.PreviousPosition = spawn.BallPosition;
        session.Ball.Velocity = Vector3.Zero;

        session.Human.MoveStickXZ = new Vector2(1f, 0f);
        session.Human.UpdateButtonCharge(KickIntentType.Shot, 0.1f);
        session.Human.UpdateButtonCharge(KickIntentType.Shot, 0.4f);
        // 85 % is the power sweet spot: 95 % now deliberately clears the crossbar.
        session.Human.ReleaseChargedKick(KickIntentType.Shot, 0.85f);

        for (int i = 0; i < 240 && session.GoalsScored == 0; i++)
            session.Step(Dt);

        Assert.Equal(1, session.GoalsScored);
        Assert.Equal(TrainingEventKind.Goal, session.ActiveOutcome);
    }

    [Fact]
    public void FreeTraining_ResetCancelsAPendingChargedKick()
    {
        var session = new TrainingSession(new FreeTrainingDrill(), resetDelaySeconds: 0.5f);

        session.Human.UpdateButtonCharge(KickIntentType.Shot, 0.1f);
        session.Human.UpdateButtonCharge(KickIntentType.Shot, 0.4f);
        session.Human.QueueTackle();
        Assert.True(session.Human.ChargedPower01 > 0.2f);

        session.ResetToSpawn();

        Assert.Equal(0f, session.Human.ChargedPower01);
        Assert.False(session.Human.ShootPressed);
        Assert.False(session.Human.TacklePressed);
    }

    [Fact]
    public void Registry_ExposesFreeTrainingAndFindsItById()
    {
        Assert.Contains(TrainingDrillRegistry.All, d => d.Id == FreeTrainingDrill.DrillId);
        Assert.Same(TrainingDrillRegistry.All[0], TrainingDrillRegistry.Default);
        Assert.NotNull(TrainingDrillRegistry.Find("FREE-TRAINING"));
        Assert.Null(TrainingDrillRegistry.Find("does-not-exist"));
    }

    [Fact]
    public void TrainingSession_AcceptsCustomDrillsWithoutChanges()
    {
        var drill = new CenterSpotDrill();
        var session = new TrainingSession(drill);

        Assert.Equal(new Vector3(0f, 0f, 0f), session.Player.Position);
        Assert.Equal(new Vector3(0f, 0.11f, 1f), session.Ball.Position);
        Assert.Equal(drill, session.Drill);
        Assert.Equal(1, session.Player.Id);
    }

    private static bool IsInsidePenaltyArea(Vector3 position, PitchDimensions pitch, float boxEdgeX, float boxHalfWidth) =>
        position.X >= boxEdgeX && position.X <= pitch.HalfLength && MathF.Abs(position.Z) <= boxHalfWidth;

    private sealed class CenterSpotDrill : TrainingDrill
    {
        public override string Id => "test-center";
        public override string Title => "Center Spot";
        public override string Description => "Test drill";
        public override float AttackingDirectionX => -1f;

        public override TrainingSpawn CreateSpawn(PitchDimensions pitch) => new(
            new Vector3(0f, 0f, 0f),
            new Vector3(0f, 0.11f, 1f),
            new Vector2(-1f, 0f));

        public override bool IsGoal(BallState ball, PitchDimensions pitch) => false;
        public override bool IsBallOutOfBounds(BallState ball, PitchDimensions pitch) => false;
    }
}
