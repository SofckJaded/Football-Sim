using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Rules;

namespace FootballSim.Tests;

public sealed class SimulationPhaseGatesTests
{
    // =========================================================================
    // PHASE 0 GATE: Determinism, Fixed Step, and Control-Source Parity
    // =========================================================================

    /// <summary>
    /// Same seed, two fresh engines, identical AI decisions: bitwise identical state. This is a
    /// *determinism* gate, not a replay — a real recorded-command replay (record every decided command
    /// and re-run it into fresh engines) is
    /// <see cref="ReplayAndCoverageTests.F23_RecordedCommandReplay_ReproducesEveryTickOfAMatch"/>; the
    /// old name claimed a replay this test never performed (audit F23).
    /// </summary>
    [Fact]
    public void Phase0_SameSeed_ProducesBitwiseIdenticalAiMatchState()
    {
        var config = new PreMatchConfig { Seed = 42UL, PlayersPerTeam = 5 };
        var engineA = new MatchSimulationEngine(config);
        var engineB = new MatchSimulationEngine(config);

        for (int i = 0; i < 300; i++)
        {
            engineA.StepTick();
            engineB.StepTick();
        }

        Assert.Equal(engineA.Ball.Position, engineB.Ball.Position);
        Assert.Equal(engineA.Ball.Velocity, engineB.Ball.Velocity);
        for (int i = 0; i < engineA.Players.Count; i++)
        {
            Assert.Equal(engineA.Players[i].Position, engineB.Players[i].Position);
            Assert.Equal(engineA.Players[i].Stamina01, engineB.Players[i].Stamina01);
        }
    }

    // =========================================================================
    // PHASE 1 GATE: All 10 Ball Physics Test Lab Requirements (§3.3)
    // =========================================================================

    [Fact]
    public void Phase1_Lab1_BallReleasedFromHeight_FallsUnderGravityAndSettlesStably()
    {
        var solver = new BallPhysicsSolver();
        var ball = new BallState
        {
            Position = new Vector3(0f, 5.0f, 0f),
            Velocity = Vector3.Zero
        };

        for (int i = 0; i < 600; i++)
            solver.Step(ball, SimConstants.FixedDeltaTime);

        Assert.InRange(ball.Position.Y, solver.Parameters.Radius - 1e-3f, solver.Parameters.Radius + 1e-3f);
        Assert.True(ball.Velocity.Length() < 1e-3f, "Ball should settle to rest without infinite bouncing.");
    }

    [Fact]
    public void Phase1_Lab2_GroundPass_RollsAndDeceleratesToFullStopWithoutJitter()
    {
        var solver = new BallPhysicsSolver();
        var ball = new BallState
        {
            Position = new Vector3(0f, 0.11f, 0f),
            Velocity = new Vector3(12.0f, 0f, 0f)
        };

        for (int i = 0; i < 1200; i++)
            solver.Step(ball, SimConstants.FixedDeltaTime);

        Assert.True(ball.Position.X > 15.0f && ball.Position.X < 65.0f);
        Assert.Equal(Vector3.Zero, ball.Velocity);
        Assert.Equal(Vector3.Zero, ball.AngularVelocity);
    }

    [Fact]
    public void Phase1_Lab3_And_Lab4_DrivenAndLoftedPasses_FollowPlausibleArcsAndBounces()
    {
        var solver = new BallPhysicsSolver();
        var lofted = new BallState
        {
            Position = new Vector3(0f, 0.11f, 0f),
            Velocity = new Vector3(18.0f, 9.5f, 0f)
        };

        float maxApexY = 0f;
        int groundBounceCount = 0;
        for (int i = 0; i < 300; i++)
        {
            solver.Step(lofted, SimConstants.FixedDeltaTime);
            maxApexY = MathF.Max(maxApexY, lofted.Position.Y);
            groundBounceCount += lofted.RecentCollisions.Count(c => c.SurfaceType == "Ground");
        }

        Assert.InRange(maxApexY, 3.0f, 6.5f);
        Assert.True(groundBounceCount >= 1);
        Assert.True(lofted.Position.X > 25.0f);
    }

    [Fact]
    public void Phase1_Lab5_HighSpeedShot_SweptCollisionDetectsGoalpostWithoutTunneling()
    {
        var pitch = new PitchDimensions();
        var solver = new BallPhysicsSolver(null, pitch);
        // Aim a 36 m/s (130 km/h) shot directly at the right goalpost at (X = 52.5, Z = 3.66)
        var ball = new BallState
        {
            Position = new Vector3(45.0f, 1.2f, 3.66f),
            Velocity = new Vector3(36.0f, 0f, 0f)
        };

        bool hitPost = false;
        for (int i = 0; i < 30; i++)
        {
            solver.Step(ball, SimConstants.FixedDeltaTime);
            if (ball.RecentCollisions.Any(c => c.SurfaceType == "GoalPost"))
            {
                hitPost = true;
                break;
            }
        }

        Assert.True(hitPost, "High-speed shot must collide with the goalpost rather than tunneling through.");
        Assert.True(ball.Velocity.X < 0f, "Ball should rebound off the front of the goalpost.");
    }

    [Fact]
    public void Phase1_Lab6_MagnusForce_EqualAndOppositeSidespin_ProducesMirroredCurvature()
    {
        var solver = new BallPhysicsSolver();
        var leftSpin = new BallState
        {
            Position = new Vector3(0f, 0.11f, 0f),
            Velocity = new Vector3(24.0f, 6.0f, 0f),
            AngularVelocity = new Vector3(0f, 55.0f, 0f)
        };
        var rightSpin = new BallState
        {
            Position = new Vector3(0f, 0.11f, 0f),
            Velocity = new Vector3(24.0f, 6.0f, 0f),
            AngularVelocity = new Vector3(0f, -55.0f, 0f)
        };
        var zeroSpin = new BallState
        {
            Position = new Vector3(0f, 0.11f, 0f),
            Velocity = new Vector3(24.0f, 6.0f, 0f),
            AngularVelocity = Vector3.Zero
        };

        for (int i = 0; i < 90; i++)
        {
            solver.Step(leftSpin, SimConstants.FixedDeltaTime);
            solver.Step(rightSpin, SimConstants.FixedDeltaTime);
            solver.Step(zeroSpin, SimConstants.FixedDeltaTime);
        }

        Assert.True(MathF.Abs(zeroSpin.Position.Z) < 1e-4f, "Zero sidespin must have zero lateral drift.");
        Assert.True(MathF.Abs(leftSpin.Position.Z) > 1.2f, "Sidespin must produce noticeable lateral Magnus swerve.");
        Assert.Equal(-leftSpin.Position.Z, rightSpin.Position.Z, precision: 3);
    }

    [Fact]
    public void Phase1_Lab7_And_Lab8_TopspinBackspinAffectFlightAndBounce_WithoutEnergyGain()
    {
        var solver = new BallPhysicsSolver();
        var topspin = new BallState
        {
            Position = new Vector3(0f, 0.11f, 0f),
            Velocity = new Vector3(22.0f, 7.5f, 0f),
            AngularVelocity = new Vector3(0f, 0f, -60.0f) // Topspin for +X travel
        };
        var backspin = new BallState
        {
            Position = new Vector3(0f, 0.11f, 0f),
            Velocity = new Vector3(22.0f, 7.5f, 0f),
            AngularVelocity = new Vector3(0f, 0f, 60.0f)  // Backspin for +X travel
        };

        float initialSpeed = topspin.Velocity.Length();
        for (int i = 0; i < 60; i++)
        {
            solver.Step(topspin, SimConstants.FixedDeltaTime);
            solver.Step(backspin, SimConstants.FixedDeltaTime);
            Assert.True(topspin.Velocity.Length() <= initialSpeed + 1e-3f);
        }

        // Topspin dips faster (lower Y at t=1.0s) than backspin
        Assert.True(topspin.Position.Y < backspin.Position.Y);
    }

    // =========================================================================
    // PHASE 2 GATE: Locomotion, Reach Restrictions, and Control-Source Swap Parity
    // =========================================================================

    [Fact]
    public void Phase2_DistantPlayerCannotKickBall_And_SwappedAdaptersProduceIdenticalOutcome()
    {
        var pitch = new PitchDimensions();
        var exec = new FootballerExecutionSystem(pitch);
        var ball = new BallState { Position = new Vector3(10.0f, 0.11f, 0f) };
        var distantPlayer = new FootballerState
        {
            Id = 1, Team = TeamId.Home, ShirtNumber = 9, Role = TacticalRole.Striker,
            Attributes = new PlayerAttributes(), Tendencies = new PlayerTendencies(),
            Position = new Vector3(0f, 0f, 0f), // 10 meters away from ball
            LastCommand = new FootballerCommand(
                Vector2.Zero, new Vector2(1f, 0f), false, false, false, false, false,
                Vector3.Zero, KickIntentType.Shot, Vector3.UnitX, 1.0f, 0f)
        };

        exec.UpdateLocomotionAndContacts(new[] { distantPlayer }, ball, new SeededRandom(1UL), SimConstants.FixedDeltaTime);
        Assert.Equal(Vector3.Zero, ball.Velocity); // Cannot teleport or kick from outside reach radius!

        // Now test swapping AI-labeled vs Human-labeled RecordedCommandControllers with identical pre-recorded commands
        var recorded = new Dictionary<int, FootballerCommand>
        {
            [1] = new FootballerCommand(new Vector2(1f, 0f), new Vector2(1f, 0f), true, false, false, false, false, Vector3.Zero, KickIntentType.GroundPass, Vector3.UnitX, 0.7f, 0f),
            [2] = new FootballerCommand(new Vector2(1f, 0.2f), new Vector2(1f, 0.2f), true, false, false, false, false, Vector3.Zero, KickIntentType.None, Vector3.UnitX, 0f, 0f)
        };

        var simA = new MatchSimulationEngine(new PreMatchConfig { Seed = 99UL, PlayersPerTeam = 5 });
        var simB = new MatchSimulationEngine(new PreMatchConfig { Seed = 99UL, PlayersPerTeam = 5 });

        simA.OverrideController(5, new RecordedCommandController(ControllerSourceType.AI, recorded, () => simA.CurrentTick));
        simB.OverrideController(5, new RecordedCommandController(ControllerSourceType.HumanPreMatchLocked, recorded, () => simB.CurrentTick));

        for (int t = 0; t < 30; t++)
        {
            simA.StepTick();
            simB.StepTick();
        }

        Assert.Equal(simA.Ball.Position, simB.Ball.Position);
        Assert.Equal(simA.Players[4].Position, simB.Players[4].Position);
    }

    // =========================================================================
    // PHASE 3, 4 & 5 GATES: 3v2 Decisions, 5v5 Transitions, 11v11 Full Match
    // =========================================================================

    [Fact]
    public void Phase3_And_Phase4_UtilityAI_ProducesExplainableCandidates_And_5v5RunsCleanly()
    {
        var sim = new MatchSimulationEngine(new PreMatchConfig { Seed = 777UL, PlayersPerTeam = 5, HalfDurationSeconds = 60f });
        for (int i = 0; i < 600; i++)
            sim.StepTick();

        var strikerCtrl = sim.GetController(5) as TacticalUtilityAiController;
        Assert.NotNull(strikerCtrl);
        Assert.NotEmpty(strikerCtrl!.LastCandidates);
        Assert.False(string.IsNullOrWhiteSpace(strikerCtrl.GetDebugSummary()));
        Assert.True(sim.Analytics.HomePassesAttempted + sim.Analytics.AwayPassesAttempted > 0);
    }

    // =========================================================================
    // PHASE 6 GATE: Recognizable Tactical Differentiation (Equal Player Ratings)
    // =========================================================================

    /// <summary>
    /// The possession profile must show a wider shape than the direct-counter profile. The name says
    /// only what is asserted: the old name also claimed a "higher pass ratio" that was never checked
    /// (audit F23). The multi-seed version of this gate is
    /// <see cref="ReplayAndCoverageTests.F23_MultiSeedTacticalReport_HoldsAcrossTheSeedSet"/>.
    ///
    /// The measure is the possession-scoped span (A4): the width profile is a statement about the shape a
    /// team holds *with the ball*, and the unconditional span mostly reported who was attacking (it
    /// correlated with possession share instead of the profile). The sample is the configured half
    /// (180 s) rather than its first 90 s, because individual short windows invert on one seed
    /// (PROGRESS.md warning 3) even on the attacking measure.
    /// </summary>
    [Fact]
    public void Phase6_PossessionTeam_ExhibitsWiderShape_ThanDirectCounterTeam()
    {
        var sim = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = 2026UL,
            PlayersPerTeam = 11,
            HalfDurationSeconds = 180f,
            HomeProfile = TeamTacticalProfile.PossessionControl,
            AwayProfile = TeamTacticalProfile.DirectCounterattack
        });

        for (int i = 0; i < 10800; i++)
            sim.StepTick();

        // Possession profile has AttackingWidth=0.82 vs DirectCounter AttackingWidth=0.52
        Assert.True(sim.Analytics.HomeAttackingWidthMeters > 0f && sim.Analytics.AwayAttackingWidthMeters > 0f,
            "both teams must actually have had the ball for the comparison to mean anything");
        Assert.True(
            sim.Analytics.HomeAttackingWidthMeters > sim.Analytics.AwayAttackingWidthMeters,
            $"Expected Home Possession attacking width ({sim.Analytics.HomeAttackingWidthMeters:F1}m) > " +
            $"Away Direct attacking width ({sim.Analytics.AwayAttackingWidthMeters:F1}m)");
    }

    // =========================================================================
    // PHASE 7 & 8 GATES: Rules Engine & Pre-Match Human Controller Lock
    // =========================================================================

    [Fact]
    public void Phase8_PreMatchHumanLock_RejectsMidMatchSwitch_And_EntersSpectatorOnRedCard()
    {
        var sim = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = 123UL,
            PlayersPerTeam = 11,
            DesignatedHumanPlayerId = 9
        });

        Assert.NotNull(sim.LockedHumanController);
        Assert.Equal(9, sim.LockedHumanController!.LockedPlayerId);
        sim.StepTick();

        // Attempting to switch human control to Player #10 mid-match must throw
        Assert.Throws<InvalidOperationException>(() =>
            sim.OverrideController(10, new HumanInputController(10)));

        // If designated human player is sent off or injured, spectator fast-forward engages automatically
        var humanPlayer = sim.Players.First(p => p.Id == 9);
        humanPlayer.IsSentOff = true;
        sim.StepTick();

        Assert.True(sim.IsSpectatorFastForwardActive);
    }

    [Fact]
    public void Phase8_HumanInputController_TranslatesDirectionalAndActionTriggersIntoCommands()
    {
        var sim = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = 999UL,
            PlayersPerTeam = 11,
            DesignatedHumanPlayerId = 9
        });

        var human = sim.LockedHumanController!;
        var player = sim.Players.First(p => p.Id == 9);

        // 1. Directional Movement and Sprint
        human.MoveStickXZ = new Vector2(0f, -1f); // Moving "Up" / -Z on pitch
        human.SprintHeld = true;
        sim.StepTick();

        Assert.Equal(new Vector2(0f, -1f), player.LastCommand.DesiredMoveDirectionXZ);
        Assert.True(player.LastCommand.Sprint);

        // 2. Pass action: the press becomes exactly one command for this tick.
        human.PassPressed = true;
        sim.StepTick();

        Assert.Equal(KickIntentType.GroundPass, player.LastCommand.KickType);
        // Audit F17: this player is nowhere near the ball at kickoff, so execution refuses the
        // contact and the press is re-queued for one short retry instead of vanishing.
        Assert.True(human.HasBufferedKickRequest, "A refused kick must be retried, not dropped");
        Assert.True(human.PassPressed);
        human.ClearBufferedActions();
        Assert.False(human.PassPressed);

        // 3. Shoot action with Curve modifier
        human.ShootPressed = true;
        human.CurveModifierHeld = true;
        sim.StepTick();

        Assert.Equal(KickIntentType.CurledShot, player.LastCommand.KickType);
        // The curl is signed to bend back toward the goal centre from the aimed side.
        Assert.NotEqual(0f, player.LastCommand.RequestedCurveSpin);
        human.ClearBufferedActions();
        Assert.False(human.ShootPressed);
        human.CurveModifierHeld = false;

        // 4. Tackle request (also outside reach at kickoff, so execution refuses and it is retried)
        human.TacklePressed = true;
        sim.StepTick();

        Assert.True(player.LastCommand.RequestTackle);
        Assert.True(human.TacklePressed, "A refused tackle must stay queued for a short retry");
        human.ClearBufferedActions();
        Assert.False(human.TacklePressed);    }
}
