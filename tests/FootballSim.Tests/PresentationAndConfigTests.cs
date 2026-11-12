using System;
using System.Numerics;
using Xunit;
using FootballSim;
using FootballSim.Core;
using FootballSim.Physics;
using FootballSim.Rules;
using FootballSim.Training;

namespace FootballSim.Tests;

/// <summary>
/// Regressions for the presentation/configuration part of audit F22 that Batch 19 left open:
/// the ball's render radius disagreeing with physics, frame-rate-dependent camera smoothing with
/// unbounded catch-up work, and unvalidated public configuration plus the inspector's id-as-index
/// assumption.
/// </summary>
public sealed class PresentationAndConfigTests
{
    // =============================================================================================
    // F22 — the ball has one size, and rendering must read it from physics
    // =============================================================================================

    /// <summary>
    /// Audit F22: the match and practice scenes drew a 0.16 m sphere while physics, contacts and the
    /// boundary rules used 0.11 m. Presentation now derives every ball dimension from the solver's
    /// radius, so there is one source of truth.
    /// </summary>
    [Fact]
    public void F22_BallRenderDimensions_ComeFromThePhysicsRadius()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig { Seed = 5UL, PlayersPerTeam = 5 });

        // The engine exposes exactly the radius physics uses (not a second constant).
        float physicsRadius = new BallPhysicsSolver().Parameters.Radius;
        Assert.Equal(physicsRadius, engine.BallRadius);
        Assert.Equal(BallGeometry.DefaultRadius, engine.BallRadius);

        // The defect: rendering used 0.16 m for a 0.11 m ball.
        Assert.True(engine.BallRadius < 0.16f,
            $"the ball is still rendered/physics-sized at {engine.BallRadius:F2} m");

        // Every presentation dimension is derived from that one value.
        Assert.Equal(engine.BallRadius, BallGeometry.SphereRadius(engine.BallRadius));
        Assert.Equal(engine.BallRadius * 2f, BallGeometry.Diameter(engine.BallRadius), 5);
        Assert.Equal(BallGeometry.DefaultRadius * BallGeometry.ShadowRadiusFactor,
            BallGeometry.ShadowRadius(engine.BallRadius), 5);

        // A resting ball sits exactly one radius above the grass, so the drawn sphere touches the pitch.
        Assert.Equal(BallGeometry.RestingCentreHeight(engine.BallRadius), engine.Ball.Position.Y, 5);

        // A custom solver radius flows through the same helper instead of being replaced by a constant.
        Assert.Equal(0.20f, BallGeometry.SphereRadius(0.20f), 5);
        Assert.Equal(0.40f, BallGeometry.Diameter(0.20f), 5);

        // The training drill's resting-spawn radius is the shared value too.
        Assert.Equal(BallGeometry.DefaultRadius, FreeTrainingDrill.BallRadius);
    }

    /// <summary>
    /// The practice session publishes the same authoritative radius, so it cannot draw a second ball.
    /// </summary>
    [Fact]
    public void F22_TrainingSession_UsesTheSharedBallRadius()
    {
        var session = new TrainingSession(TrainingDrillRegistry.Default);

        Assert.Equal(new BallPhysicsSolver().Parameters.Radius, session.BallRadius);
        Assert.Equal(BallGeometry.SphereRadius(session.BallRadius),
            BallGeometry.SphereRadius(new BallPhysicsSolver().Parameters.Radius));
    }

    // =============================================================================================
    // F22 — frame-rate-independent camera smoothing and bounded, diagnosed catch-up
    // =============================================================================================

    private static (int Steps, float Alpha, double Accumulator) ClockAfterOneSecond(float framesPerSecond)
    {
        var clock = new FixedStepClock();
        double frameDelta = 1.0 / framesPerSecond;
        int frames = (int)MathF.Round(framesPerSecond);
        int steps = 0;
        for (int i = 0; i < frames; i++)
            steps += clock.Advance(frameDelta);
        return (steps, clock.Alpha, clock.AccumulatorSeconds);
    }

    /// <summary>
    /// Audit F22: the fixed-step accumulator had no bound, so one long frame simulated a burst. Simulated
    /// time must be the same at 30, 60 and 144 FPS for the same elapsed second.
    /// </summary>
    [Fact]
    public void F22_FixedStepClock_SimulatesTheRequestedTimeAt30_60_144Fps()
    {
        var at30 = ClockAfterOneSecond(30f);
        var at60 = ClockAfterOneSecond(60f);
        var at144 = ClockAfterOneSecond(144f);

        // The same wall-clock second produces exactly one second of fixed steps at every frame rate.
        Assert.Equal(60, at30.Steps);
        Assert.Equal(60, at60.Steps);
        Assert.Equal(60, at144.Steps);

        foreach (var result in new[] { at30, at60, at144 })
        {
            // At most one leftover step, never a growing backlog.
            Assert.True(result.Accumulator < FixedStepClock.StepSeconds,
                $"clock left {result.Accumulator:F6} s accumulated (a whole step)");
        }
    }

    /// <summary>
    /// Audit F22: a large frame delay must not try to simulate the whole stall in one frame, and the
    /// dropped time must be reported instead of silently vanishing.
    /// </summary>
    [Fact]
    public void F22_FixedStepClock_BoundsCatchUpAndReportsDroppedTime()
    {
        var clock = new FixedStepClock();

        int stalledSteps = clock.Advance(3.0);
        Assert.Equal(FixedStepClock.MaxStepsPerFrame, stalledSteps);
        Assert.Equal(1, clock.CappedFrames);
        Assert.True(clock.DroppedSeconds > 0.05,
            $"a 3 s stall dropped only {clock.DroppedSeconds:F3} s of simulated time");
        Assert.True(clock.IsCatchingUp);

        // The clock stays bounded: the next normal frame is a normal frame, not a burst.
        int nextSteps = clock.Advance(SimConstants.FixedDeltaTime);
        Assert.InRange(nextSteps, 0, 2);
        Assert.True(clock.AccumulatorSeconds < SimConstants.FixedDeltaTime + 1e-6);

        // A frame that cannot be simulated at all must not poison the accumulator.
        Assert.Equal(0, clock.Advance(double.NaN));
        Assert.Equal(0, clock.Advance(-1.0));
        Assert.Equal(0, clock.Advance(1.0 / 60.0, timeScale: float.NaN));

        clock.Reset();
        Assert.False(clock.IsCatchingUp);
        Assert.Equal(0d, clock.DroppedSeconds);
    }

    private static Vector3 SettleCamera(float framesPerSecond, float halfLifeSeconds)
    {
        var position = Vector3.Zero;
        Vector3 target = new(10f, 20f, 27f);
        double frameDelta = 1.0 / framesPerSecond;
        int frames = (int)MathF.Round(framesPerSecond);
        for (int i = 0; i < frames; i++)
            position = CameraSmoothing.Step(position, target, halfLifeSeconds, (float)frameDelta);
        return position;
    }

    /// <summary>
    /// Audit F22: the camera lerped a fixed 0.14 per rendered frame, so it converged at wildly different
    /// speeds at 30 vs 144 FPS. Time-based smoothing must land in the same place after the same second.
    /// </summary>
    [Fact]
    public void F22_CameraSmoothing_IsFrameRateIndependent()
    {
        Vector3 at30 = SettleCamera(30f, CameraSmoothing.MatchHalfLifeSeconds);
        Vector3 at60 = SettleCamera(60f, CameraSmoothing.MatchHalfLifeSeconds);
        Vector3 at144 = SettleCamera(144f, CameraSmoothing.MatchHalfLifeSeconds);

        Assert.Equal(at60.X, at30.X, 2);
        Assert.Equal(at60.X, at144.X, 2);
        Assert.Equal(at60.Y, at30.Y, 2);
        Assert.Equal(at60.Y, at144.Y, 2);

        // The old rule, for contrast: one tenth of a second of fixed 0.14 lerps depends on the frame
        // rate (3 frames at 30 FPS vs 14 at 144 FPS), which is the defect this replaces.
        static Vector3 LegacyAfter(Vector3 target, float fps, float seconds)
        {
            var position = Vector3.Zero;
            int frames = Math.Max(1, (int)MathF.Round(fps * seconds));
            for (int i = 0; i < frames; i++)
                position = Vector3.Lerp(position, target, 0.14f);
            return position;
        }

        Vector3 legacyTarget = new(10f, 20f, 27f);
        Assert.True(MathF.Abs(LegacyAfter(legacyTarget, 30f, 0.1f).X - LegacyAfter(legacyTarget, 144f, 0.1f).X) > 3f,
            "the legacy per-frame lerp was expected to be frame-rate dependent");

        // The new half-life is tuned to the old 60 FPS feel, so one 60 Hz frame still moves ~14 %.
        Assert.Equal(CameraSmoothing.LegacyPerFrameWeight,
            CameraSmoothing.Weight(CameraSmoothing.MatchHalfLifeSeconds, SimConstants.FixedDeltaTime), 2);

        // A longer frame moves further than a short one, which is the whole point of delta awareness.
        Assert.True(
            CameraSmoothing.Weight(CameraSmoothing.MatchHalfLifeSeconds, 1f / 30f) >
            CameraSmoothing.Weight(CameraSmoothing.MatchHalfLifeSeconds, 1f / 144f));
    }

    // =============================================================================================
    // F22 — public configuration validation and the inspector's id/index assumption
    // =============================================================================================

    /// <summary>
    /// Audit F22: invalid team sizes, nonpositive half lengths and human ids that are not in the match
    /// were accepted, then failed later as an out-of-range inspection or a nonsense clock.
    /// </summary>
    [Fact]
    public void F22_InvalidMatchConfig_IsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MatchSimulationEngine(new PreMatchConfig { PlayersPerTeam = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MatchSimulationEngine(new PreMatchConfig { PlayersPerTeam = 12 }));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MatchSimulationEngine(new PreMatchConfig { HalfDurationSeconds = 0f }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MatchSimulationEngine(new PreMatchConfig { HalfDurationSeconds = -30f }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MatchSimulationEngine(new PreMatchConfig { HalfDurationSeconds = float.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MatchSimulationEngine(new PreMatchConfig { HalfDurationSeconds = float.PositiveInfinity }));

        // 11-a-side has 22 players: #23 is not one of them, #22 is.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MatchSimulationEngine(new PreMatchConfig { DesignatedHumanPlayerId = 23 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MatchSimulationEngine(new PreMatchConfig { DesignatedHumanPlayerId = 0 }));
        Assert.NotNull(new MatchSimulationEngine(new PreMatchConfig { DesignatedHumanPlayerId = 22 }).LockedHumanController);

        // Valid configurations still construct.
        var fiveASide = new MatchSimulationEngine(new PreMatchConfig { PlayersPerTeam = 5, DesignatedHumanPlayerId = 10 });
        Assert.Equal(10, fiveASide.Players.Count);
        Assert.Equal(10, fiveASide.LockedHumanController!.LockedPlayerId);
    }

    /// <summary>
    /// Degenerate pitch geometry used to be accepted by every consumer (rules, physics, visuals).
    /// </summary>
    [Fact]
    public void F22_DegeneratePitchAndPhysics_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MatchSimulationEngine(new PreMatchConfig(), new PitchDimensions { Width = 0f }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MatchSimulationEngine(new PreMatchConfig(), new PitchDimensions { Length = float.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MatchSimulationEngine(new PreMatchConfig(), new PitchDimensions { GoalWidth = 80f })); // wider than the pitch
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MatchSimulationEngine(new PreMatchConfig(), new PitchDimensions { PenaltyAreaLength = 60f })); // longer than a half

        var pitch = new PitchDimensions();
        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchRulesEngine(pitch, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchRulesEngine(pitch, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BallPhysicsSolver(pitch: new PitchDimensions { GoalPostRadius = 0f }));

        // Physics already refuses a zero ball, but the render radius now comes from it, so it matters.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BallPhysicsSolver(new BallPhysicsParameters { Radius = 0f }));

        // A degenerate practice pitch is refused by the same validator.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TrainingSession(TrainingDrillRegistry.Default, new PitchDimensions { Width = -5f }));
    }

    /// <summary>
    /// Audit F22: player attributes and team profiles feed AI utilities, so a NaN or an out-of-range
    /// blend weight has to be rejected instead of silently disabling a behaviour.
    /// </summary>
    [Fact]
    public void F22_PlayerAttributesTendenciesAndProfiles_AreValidated()
    {
        // The values the engine builds are valid.
        SimulationConfigValidation.ValidatePlayerAttributes(new PlayerAttributes());
        SimulationConfigValidation.ValidateTendencies(new PlayerTendencies());
        SimulationConfigValidation.ValidateTacticalProfile(TeamTacticalProfile.PossessionControl);
        SimulationConfigValidation.ValidateTacticalProfile(TeamTacticalProfile.DirectCounterattack);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SimulationConfigValidation.ValidatePlayerAttributes(new PlayerAttributes { ReachRadius = -1f }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SimulationConfigValidation.ValidatePlayerAttributes(new PlayerAttributes { MaxSprintSpeed = float.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SimulationConfigValidation.ValidatePlayerAttributes(new PlayerAttributes { Strength01 = 2f }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SimulationConfigValidation.ValidatePlayerAttributes(new PlayerAttributes { ReactionDelaySeconds = -0.1f }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SimulationConfigValidation.ValidateTendencies(new PlayerTendencies { RiskTolerance01 = -0.2f }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SimulationConfigValidation.ValidateTacticalProfile(new TeamTacticalProfile { Tempo01 = float.NaN }));

        // A team profile reaches the AI through the match config, so the engine rejects it too.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MatchSimulationEngine(new PreMatchConfig
            {
                HomeProfile = new TeamTacticalProfile { PositionalFreedom01 = 3f }
            }));
    }

    /// <summary>
    /// Audit F22: the inspector assumed <c>DesignatedHumanPlayerId</c> was a valid one-based array
    /// index, so a match smaller than the id inspected the wrong player (or threw). The id must be
    /// resolved against the squad that exists.
    /// </summary>
    [Fact]
    public void F22_InspectorPlayerId_IsResolvedAgainstTheActualSquad()
    {
        // Spectator match: inspect the default striker of a full match.
        Assert.Equal(9, MatchPositions.ResolveInspectedPlayerId(null, 22));

        // A small match: the default id is clamped into the squad instead of indexing past the end.
        Assert.Equal(4, MatchPositions.ResolveInspectedPlayerId(null, 4));
        Assert.Equal(4, MatchPositions.ResolveInspectedPlayerId(9, 4));
        Assert.Equal(1, MatchPositions.ResolveInspectedPlayerId(1, 4));

        // A human id that exists is used as-is.
        Assert.Equal(5, MatchPositions.ResolveInspectedPlayerId(5, 10));
        Assert.Equal(22, MatchPositions.ResolveInspectedPlayerId(22, 22));

        // Ids outside the squad fall back to the default striker, clamped into the real squad.
        Assert.Equal(9, MatchPositions.ResolveInspectedPlayerId(0, 22));
        Assert.Equal(9, MatchPositions.ResolveInspectedPlayerId(99, 22));
        Assert.Throws<ArgumentOutOfRangeException>(() => MatchPositions.ResolveInspectedPlayerId(9, 0));

        // Every id the helper returns is a real player in the engine's squad, which is what the HUD
        // now relies on (it looks the player up by id).
        var engine = new MatchSimulationEngine(new PreMatchConfig { PlayersPerTeam = 5, DesignatedHumanPlayerId = 3 });
        int inspected = MatchPositions.ResolveInspectedPlayerId(3, engine.Players.Count);
        Assert.Contains(engine.Players, p => p.Id == inspected);
    }
}
