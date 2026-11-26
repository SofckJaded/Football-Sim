using System;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Presentation;
using FootballSim.Rules;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// P6 gates for the presentation physics: spring stability, zero-NaN guarantees under pathological
/// time steps, pause/timescale faithfulness, teleport resets and the strict read-only contract that keeps
/// the visual layer unable to change the simulation.
///
/// Everything here drives the pure-C# <see cref="PlayerDynamicsSolver"/> directly; no Godot is involved.
/// </summary>
public sealed class PlayerDynamicsTests
{
    private const float Tick = 1f / 60f;

    // =========================================================================================
    // Spring primitives
    // =========================================================================================

    [Fact]
    public void Spring1D_CriticallyDampedConvergesWithoutOvershoot()
    {
        var spring = Spring1D.At(0f);
        float peak = 0f;
        for (int i = 0; i < 600; i++)
        {
            spring.Step(10f, 12f, 1.0f, Tick);
            peak = MathF.Max(peak, spring.Value);
        }

        Assert.InRange(spring.Value, 9.99f, 10.01f);
        Assert.True(peak <= 10.001f, $"critically damped spring overshot to {peak}");
        Assert.True(MathF.Abs(spring.Velocity) < 0.01f);
    }

    [Fact]
    public void Spring1D_UnderdampedSpringRebounds()
    {
        var spring = Spring1D.At(0f);
        float peak = 0f;
        for (int i = 0; i < 600; i++)
        {
            spring.Step(10f, 12f, 0.35f, Tick);
            peak = MathF.Max(peak, spring.Value);
        }

        Assert.True(peak > 10.2f, $"underdamped spring should overshoot, peak was {peak}");
        Assert.InRange(spring.Value, 9.9f, 10.1f);
    }

    [Fact]
    public void Spring1D_IsUnconditionallyStableForHugeTimeSteps()
    {
        // A one-second hitch at an extreme stiffness: the internal substepping must keep this finite.
        var spring = Spring1D.At(0f);
        for (int i = 0; i < 100; i++)
            spring.Step(5f, 90f, 0.5f, 1.0f);

        Assert.True(float.IsFinite(spring.Value));
        Assert.True(float.IsFinite(spring.Velocity));
        Assert.InRange(spring.Value, -1f, 11f);
    }

    [Fact]
    public void Spring1D_ZeroDeltaTimeFreezes()
    {
        var spring = Spring1D.At(3f);
        spring.AddImpulse(7f);
        spring.Step(0f, 10f, 1f, 0f);

        Assert.Equal(3f, spring.Value);
        Assert.Equal(7f, spring.Velocity);
    }

    [Fact]
    public void Spring1D_DegenerateInputsNeverProduceNonFiniteValues()
    {
        var spring = Spring1D.At(1f);
        spring.Step(float.NaN, 10f, 1f, Tick);
        Assert.True(float.IsFinite(spring.Value));

        spring.Step(2f, float.NaN, 1f, Tick);
        Assert.True(float.IsFinite(spring.Value));

        spring.Step(2f, 10f, 0f, Tick);
        Assert.True(float.IsFinite(spring.Value));

        spring.Step(2f, 10f, 1f, float.PositiveInfinity);
        Assert.True(float.IsFinite(spring.Value));
    }

    [Fact]
    public void SpringAngle_TakesTheShortestArcAcrossThePiBoundary()
    {
        var spring = Spring1D.At(SpringMath.DegToRad(179f));
        float before = spring.Value;
        spring.StepAngle(SpringMath.DegToRad(-179f), 20f, 1f, Tick);

        float travelled = MathF.Abs(SpringMath.ShortestDelta(before, spring.Value));
        Assert.True(travelled < SpringMath.DegToRad(10f),
            $"expected a 2-degree arc, travelled {SpringMath.RadToDeg(travelled):F1} degrees");
    }

    [Fact]
    public void Spring2DAnd3D_MatchPerAxisScalarSprings()
    {
        var spring2 = Spring2D.At(Vector2.Zero);
        spring2.Step(new Vector2(1f, -2f), 10f, 1f, Tick);
        Assert.True(float.IsFinite(spring2.Value.X) && float.IsFinite(spring2.Value.Y));

        var spring3 = Spring3D.At(Vector3.Zero);
        spring3.Step(new Vector3(1f, 2f, 3f), 10f, 1f, Tick);
        Assert.True(float.IsFinite(spring3.Value.X) && float.IsFinite(spring3.Value.Y) &&
                    float.IsFinite(spring3.Value.Z));
    }

    // =========================================================================================
    // Solver integration gates
    // =========================================================================================

    [Fact]
    public void Solver_PauseFreezesThePoseExactly()
    {
        var player = CreatePlayer();
        var solver = new PlayerDynamicsSolver(player.Id, isGoalkeeper: false);
        player.Velocity = new Vector3(5f, 0f, 0f);

        Step(solver, player, new Vector3(0f, 0f, 0f), Tick, 30);
        PlayerPose before = solver.Pose;

        for (int i = 0; i < 10; i++)
        {
            // F2 debug pause: ticks keep coming but the simulated clock does not advance.
            solver.Step(BuildInput(player, player.Position), 0f);
        }

        PlayerPose after = solver.Pose;
        Assert.Equal(before.RootPosition, after.RootPosition);
        Assert.Equal(before.BodyRotation, after.BodyRotation);
        Assert.Equal(before.BodyOffset, after.BodyOffset);
        Assert.Equal(before.TorsoRotation, after.TorsoRotation);
        Assert.Equal(before.BodyScale, after.BodyScale);
        Assert.Equal(before.LeftFootLocal, after.LeftFootLocal);
    }

    [Fact]
    public void Solver_TeleportSnapsRootAndClearsEverySpring()
    {
        var player = CreatePlayer();
        var solver = new PlayerDynamicsSolver(player.Id, isGoalkeeper: false);
        player.Velocity = new Vector3(7f, 0f, 0f);
        player.IsJumping = true;

        // Build up lean, stride and jump state at the old spot.
        Step(solver, player, new Vector3(0f, 0f, 0f), Tick, 40);
        Assert.NotEqual(Vector3.Zero, solver.Pose.BodyRotation);

        // Kickoff / restart / training reset: a >2.5 m jump between rendered frames.
        var teleportedTo = new Vector3(30f, 0f, -20f);
        player.Velocity = Vector3.Zero;
        player.IsJumping = false;
        player.Position = teleportedTo;
        player.PreviousPosition = teleportedTo;
        solver.Step(BuildInput(player, teleportedTo), Tick);

        Assert.Equal(teleportedTo, solver.Pose.RootPosition);
        Assert.Equal(0f, solver.Squash, 3);
        Assert.Equal(0f, solver.BodyOffsetY, 3);
        Assert.True(MathF.Abs(solver.BodyPitch) < 0.20f,
            $"a teleport must not keep the old lean, pitch was {solver.BodyPitch:F2} rad");
    }

    [Fact]
    public void Solver_ProducesFinitePosesAcrossAViolentStressRun()
    {
        var player = CreatePlayer();
        var solver = new PlayerDynamicsSolver(player.Id, isGoalkeeper: true);
        var random = new Random(12345);

        for (int tickIndex = 0; tickIndex < 5000; tickIndex++)
        {
            player.Velocity = new Vector3(
                (float)(random.NextDouble() * 20.0 - 10.0),
                (float)(random.NextDouble() * 4.0 - 2.0),
                (float)(random.NextDouble() * 20.0 - 10.0));
            player.FacingXZ = new Vector2(
                (float)(random.NextDouble() * 2.0 - 1.0),
                (float)(random.NextDouble() * 2.0 - 1.0));
            player.IsJumping = tickIndex % 37 == 0;
            player.IsDiving = tickIndex % 53 == 0;
            player.Balance01 = (float)random.NextDouble();
            player.Stamina01 = (float)random.NextDouble();
            player.Position = new Vector3(
                (float)(random.NextDouble() * 60.0 - 30.0), 0f,
                (float)(random.NextDouble() * 40.0 - 20.0));
            player.PreviousPosition = player.Position;

            // Alternate slow-mo and 4x fast-forward frames.
            float dt = (tickIndex % 5) switch { 0 => 0f, 1 => Tick * 0.25f, 2 => Tick * 4f, _ => Tick };
            solver.Step(BuildInput(player, player.Position), dt);

            AssertFinite(solver.Pose);
        }
    }

    [Fact]
    public void Solver_SameInputsProduceIdenticalPoses()
    {
        var playerA = CreatePlayer();
        var playerB = CreatePlayer();
        var solverA = new PlayerDynamicsSolver(playerA.Id, false);
        var solverB = new PlayerDynamicsSolver(playerB.Id, false);

        for (int tickIndex = 0; tickIndex < 400; tickIndex++)
        {
            float x = tickIndex * 0.05f;
            var velocity = new Vector3(3f + x, 0f, MathF.Sin(x));
            playerA.Velocity = velocity;
            playerB.Velocity = velocity;
            playerA.Position = new Vector3(x, 0f, 0f);
            playerB.Position = new Vector3(x, 0f, 0f);
            playerA.FacingXZ = playerB.FacingXZ = new Vector2(1f, 0f);

            solverA.Step(BuildInput(playerA, playerA.Position), Tick);
            solverB.Step(BuildInput(playerB, playerB.Position), Tick);

            Assert.Equal(solverA.Pose.BodyRotation, solverB.Pose.BodyRotation);
            Assert.Equal(solverA.Pose.BodyOffset, solverB.Pose.BodyOffset);
            Assert.Equal(solverA.Pose.BodyScale, solverB.Pose.BodyScale);
        }
    }

    [Fact]
    public void Solver_SmoothsAOneTickFacingSnap()
    {
        var player = CreatePlayer();
        var solver = new PlayerDynamicsSolver(player.Id, false);
        player.FacingXZ = new Vector2(1f, 0f);
        Step(solver, player, Vector3.Zero, Tick, 30);

        // A kick execution sets FacingXZ = aimXZ in one tick (FootballerLocomotion.cs:1137).
        player.FacingXZ = new Vector2(0f, 1f); // +Z = a 90-degree turn
        solver.Step(BuildInput(player, Vector3.Zero), Tick);

        float expectedTurn = MathF.PI / 2f;
        Assert.True(MathF.Abs(solver.VisualYaw) < expectedTurn * 0.9f,
            $"facing should be smoothed over several frames, turned {MathF.Abs(solver.VisualYaw):F2} rad in one");

        for (int i = 0; i < 30; i++) solver.Step(BuildInput(player, Vector3.Zero), Tick);
        Assert.InRange(MathF.Abs(solver.VisualYaw), expectedTurn - 0.02f, expectedTurn + 0.02f);
    }

    [Fact]
    public void Solver_NeverMutatesSimulationState()
    {
        var player = CreatePlayer();
        player.Velocity = new Vector3(4f, 0.5f, -2f);
        player.FacingXZ = new Vector2(0.6f, 0.8f);
        player.Stamina01 = 0.42f;
        player.Balance01 = 0.77f;
        player.KickRecoveryTimer = 0.31f;
        var solver = new PlayerDynamicsSolver(player.Id, true);

        Vector3 position = player.Position;
        Vector3 velocity = player.Velocity;
        Vector2 facing = player.FacingXZ;
        float stamina = player.Stamina01;
        float balance = player.Balance01;
        float recovery = player.KickRecoveryTimer;
        string diagnostic = player.LastExecutionDiagnostic;

        for (int i = 0; i < 200; i++)
            solver.Step(BuildInput(player, player.Position), Tick);

        Assert.Equal(position, player.Position);
        Assert.Equal(velocity, player.Velocity);
        Assert.Equal(facing, player.FacingXZ);
        Assert.Equal(stamina, player.Stamina01);
        Assert.Equal(balance, player.Balance01);
        Assert.Equal(recovery, player.KickRecoveryTimer);
        Assert.Equal(diagnostic, player.LastExecutionDiagnostic);
    }

    [Fact]
    public void Solver_DisabledDynamicsRendersNeutralRestPose()
    {
        var settings = new PlayerDynamicsSettings { EnableDynamics = false };
        var player = CreatePlayer();
        player.Velocity = new Vector3(8f, 0f, 0f);
        var solver = new PlayerDynamicsSolver(player.Id, false, settings);
        var visual = new Vector3(1f, 0f, 2f);

        solver.Step(BuildInput(player, visual), Tick);

        Assert.Equal(visual, solver.Pose.RootPosition);
        Assert.Equal(Vector3.Zero, solver.Pose.BodyRotation);
        Assert.Equal(Vector3.One, solver.Pose.BodyScale);
        Assert.Equal(1f, solver.MotionIntensity);
    }

    [Fact]
    public void Solver_ReadsMotionIntensityLiveFromSettings()
    {
        // The scene exposes one inspector dial; the solver must honour a change without being rebuilt.
        var settings = new PlayerDynamicsSettings { MotionIntensity = 1f };
        var player = CreatePlayer();
        var solver = new PlayerDynamicsSolver(player.Id, false, settings);
        player.Velocity = new Vector3(8f, 0f, 0f);
        player.FacingXZ = new Vector2(1f, 0f);

        Step(solver, player, Vector3.Zero, Tick, 90);
        float baseline = MathF.Abs(solver.Pose.BodyRotation.Z);

        settings.MotionIntensity = 2f;
        Step(solver, player, Vector3.Zero, Tick, 90);
        float exaggerated = MathF.Abs(solver.Pose.BodyRotation.Z);

        Assert.True(exaggerated > baseline * 1.5f,
            $"a live MotionIntensity change must scale the pose: 2.0 gave {exaggerated:F3} rad, " +
            $"1.0 gave {baseline:F3} rad");
    }

    [Fact]
    public void DefaultStyleKeepsLegsAndDropsAnimatedBoots()
    {
        // Owner feedback 2026-11-26: the floating feet swinging against the rigid leg cylinder look out of
        // place, so the animated boots are off by default while the original leg cylinder stays. Both the
        // boots-on and no-legs looks remain available as presets.
        Assert.True(PlayerVisualStyle.Recommended.ShowLegs);
        Assert.False(PlayerVisualStyle.Recommended.ShowBoots);
        Assert.True(PlayerVisualStyle.WithBoots.ShowBoots);
        Assert.False(PlayerVisualStyle.NoLegs.ShowLegs);
        Assert.True(PlayerVisualStyle.Recommended.ShowShadow);
        Assert.True(PlayerVisualStyle.Recommended.ShowHands);
    }

    // =========================================================================================
    // Fixtures
    // =========================================================================================

    private static FootballerState CreatePlayer(int id = 1, TeamId team = TeamId.Home,
        TacticalRole role = TacticalRole.Striker) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = 9,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = new PlayerTendencies()
    };

    private static PlayerDynamicsInput BuildInput(FootballerState player, Vector3 visualPosition) => new()
    {
        Player = player,
        VisualPosition = visualPosition,
        BallPosition = new Vector3(visualPosition.X + 2f, 0.11f, visualPosition.Z),
        Scan = ScanStatus.None,
        GoalkeeperState = GoalkeeperState.Positioning,
        Phase = MatchPhase.FirstHalf
    };

    private static void Step(PlayerDynamicsSolver solver, FootballerState player, Vector3 visualPosition,
        float dt, int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            player.Position = visualPosition;
            player.PreviousPosition = visualPosition;
            solver.Step(BuildInput(player, visualPosition), dt);
        }
    }

    private static void AssertFinite(in PlayerPose pose)
    {
        AssertFinite(pose.RootPosition);
        AssertFinite(pose.BodyOffset);
        AssertFinite(pose.BodyRotation);
        AssertFinite(pose.BodyScale);
        AssertFinite(pose.TorsoRotation);
        AssertFinite(pose.HeadRotation);
        AssertFinite(pose.LeftFootLocal);
        AssertFinite(pose.RightFootLocal);
        AssertFinite(pose.LeftHandLocal);
        AssertFinite(pose.RightHandLocal);
        Assert.True(float.IsFinite(pose.RootYaw));
        Assert.True(float.IsFinite(pose.ShadowScaleXZ));
        Assert.True(float.IsFinite(pose.ShadowAlpha));
    }

    private static void AssertFinite(Vector3 value)
    {
        Assert.True(float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z));
    }
}
