using System;
using System.Collections.Generic;
using System.Linq;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Scenarios;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A1.1 — validated AI runtime configuration.
///
/// Two properties are asserted here rather than assumed: the defaults are exactly the constants the
/// controller used before this type existed (so nothing silently retuned), and changing a field actually
/// changes behaviour (so a settings object that nothing reads cannot pass as configuration).
/// </summary>
public sealed class AiRuntimeConfigTests
{
    [Fact]
    public void Defaults_AreTheHistoricalConstants()
    {
        AiRuntimeConfig defaults = AiRuntimeConfig.Default;

        Assert.Equal(0.10f, defaults.CommitmentBonus);
        Assert.Equal(0.45f, defaults.JobCommitmentSeconds);
        Assert.Equal(0.18f, defaults.ReactionCommitmentSeconds);

        // A fresh instance must equal the shared default: these are value semantics, not identity.
        var fresh = new AiRuntimeConfig();
        Assert.Equal(defaults.CommitmentBonus, fresh.CommitmentBonus);
        Assert.Equal(defaults.JobCommitmentSeconds, fresh.JobCommitmentSeconds);
        Assert.Equal(defaults.ReactionCommitmentSeconds, fresh.ReactionCommitmentSeconds);
    }

    [Fact]
    public void Defaults_MatchTheCadenceTableInThePlan()
    {
        AiRuntimeConfig defaults = AiRuntimeConfig.Default;

        // Plan §3 cadence at 60 Hz. Declared (not yet consumed) — see the type's documentation.
        Assert.Equal(4, defaults.PerceptionRefreshTicks);
        Assert.Equal(6, defaults.CarrierDecisionTicks);
        Assert.Equal(10, defaults.OutfieldDecisionTicks);
        Assert.Equal(6, defaults.LocalSpatialCacheTicks);
        Assert.Equal(12, defaults.TeamBeliefMapTicks);
        Assert.Equal(20, defaults.TacticalStructureTicks);
        Assert.Equal(6, defaults.LocalCoordinationTicks);
        Assert.Equal(60, defaults.StructuralReassessmentTicks);
        Assert.Equal(12, defaults.MaxOffBallCandidates);
        Assert.Equal(20, defaults.MaxCarrierCandidates);
    }

    [Fact]
    public void PreMatchConfig_CarriesTheDefaultWhenUnspecified()
    {
        var config = new PreMatchConfig();

        Assert.NotNull(config.Ai);
        Assert.Equal(AiRuntimeConfig.Default.CommitmentBonus, config.Ai.CommitmentBonus);
    }

    [Fact]
    public void ExplicitDefaults_ProduceTheSameScenarioRunAsTheImplicitDefault()
    {
        ScenarioRunResult implicitDefault = ScenarioRunner.Run(ScenarioLibrary.BuildUpThreeVersusTwo(), 180);
        ScenarioRunResult explicitDefault = ScenarioRunner.Run(WithAi(ScenarioLibrary.BuildUpThreeVersusTwo(),
            new AiRuntimeConfig
            {
                CommitmentBonus = 0.10f,
                JobCommitmentSeconds = 0.45f,
                ReactionCommitmentSeconds = 0.18f
            }), 180);

        Assert.Equal(implicitDefault.BallPosition, explicitDefault.BallPosition);
        Assert.Equal(implicitDefault.Possession, explicitDefault.Possession);
        Assert.Equal(implicitDefault.ContactAttempts.Count, explicitDefault.ContactAttempts.Count);
    }

    [Theory]
    [InlineData(3f, 2f)]
    [InlineData(0.45f, 0.18f)]
    [InlineData(0f, 0f)]
    [InlineData(1.5f, 0.75f)]
    public void FirstDecision_StartsTheConfiguredCommitmentWindow(float jobSeconds, float reactionSeconds)
    {
        // The first decision always changes action (the controller starts committed to "Idle"), so the
        // window it starts is readable immediately and must be exactly one of the configured values.
        var controller = new TacticalUtilityAiController(
            new PitchDimensions(), null,
            new AiRuntimeConfig { JobCommitmentSeconds = jobSeconds, ReactionCommitmentSeconds = reactionSeconds });

        controller.Decide(Observation(1, TeamId.Home, TacticalRole.Winger), SimConstants.FixedDeltaTime);

        Assert.NotEqual("Idle", controller.CommittedActionName);
        float remaining = controller.CommitmentRemainingSeconds;
        Assert.True(remaining == jobSeconds || remaining == reactionSeconds,
            $"commitment window {remaining} is neither the job value {jobSeconds} nor the reaction value {reactionSeconds}");
    }

    [Fact]
    public void DefaultFirstDecision_StartsAPreExistingCommitmentWindow()
    {
        var controller = new TacticalUtilityAiController(new PitchDimensions());

        controller.Decide(Observation(1, TeamId.Home, TacticalRole.Winger), SimConstants.FixedDeltaTime);

        float remaining = controller.CommitmentRemainingSeconds;
        Assert.True(remaining is 0.45f or 0.18f, $"default commitment window was {remaining}");
    }

    [Fact]
    public void Engine_PassesItsConfigurationToEveryAiController()
    {
        var ai = new AiRuntimeConfig { CommitmentBonus = 0.25f, JobCommitmentSeconds = 0.9f };
        MatchScenario scenario = WithAi(ScenarioLibrary.BuildUpThreeVersusTwo(), ai);
        var engine = new MatchSimulationEngine(scenario);

        int aiControllers = 0;
        foreach (FootballerState player in engine.Players)
        {
            if (engine.GetController(player.Id) is not TacticalUtilityAiController controller) continue;
            aiControllers++;
            Assert.Same(ai, controller.RuntimeConfig);
        }

        Assert.True(aiControllers > 0, "the fixture must have at least one utility AI controller");
    }

    [Fact]
    public void ZeroCommitment_IsAllowed()
    {
        // A zero window is a legitimate ablation ("no commitment"), so it must validate rather than be
        // rejected as if it were a typo.
        AiRuntimeConfigValidation.Validate(new AiRuntimeConfig
        {
            CommitmentBonus = 0f,
            JobCommitmentSeconds = 0f,
            ReactionCommitmentSeconds = 0f
        });
    }

    [Theory]
    [InlineData(-1f, 0.18f, 0.10f)]
    [InlineData(0.45f, -1f, 0.10f)]
    [InlineData(99f, 0.18f, 0.10f)]
    [InlineData(0.45f, 99f, 0.10f)]
    [InlineData(0.45f, 0.18f, -0.10f)]
    [InlineData(0.45f, 0.18f, 5f)]
    public void Validator_RejectsOutOfRangeValues(float job, float reaction, float bonus)
    {
        var config = new AiRuntimeConfig
        {
            JobCommitmentSeconds = job,
            ReactionCommitmentSeconds = reaction,
            CommitmentBonus = bonus
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => AiRuntimeConfigValidation.Validate(config));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(601)]
    public void Validator_RejectsAnImpossibleCadence(int ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AiRuntimeConfigValidation.Validate(new AiRuntimeConfig { PerceptionRefreshTicks = ticks }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void Validator_RejectsImpossibleCandidateCaps(int cap)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AiRuntimeConfigValidation.Validate(new AiRuntimeConfig { MaxOffBallCandidates = cap }));
    }

    [Fact]
    public void Validator_RejectsNonFiniteValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AiRuntimeConfigValidation.Validate(new AiRuntimeConfig { CommitmentBonus = float.NaN }));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AiRuntimeConfigValidation.Validate(new AiRuntimeConfig { JobCommitmentSeconds = float.PositiveInfinity }));
    }

    [Fact]
    public void Validator_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => AiRuntimeConfigValidation.Validate(null));
    }

    [Fact]
    public void Engine_RejectsAnInvalidAiConfigurationAtConstruction()
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MatchSimulationEngine(new PreMatchConfig
            {
                Ai = new AiRuntimeConfig { JobCommitmentSeconds = 42f }
            }));

        Assert.Contains("JobCommitmentSeconds", error.Message);
    }

    [Fact]
    public void Scenario_RejectsAnInvalidAiConfigurationAtConstruction()
    {
        MatchScenario scenario = WithAi(ScenarioLibrary.BuildUpThreeVersusTwo(),
            new AiRuntimeConfig { PerceptionRefreshTicks = 0 });

        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchSimulationEngine(scenario));
    }

    [Fact]
    public void Describe_StatesEveryConsumedValue()
    {
        string text = AiRuntimeConfig.Default.Describe();

        Assert.Contains("bonus", text);
        Assert.Contains("job", text);
        Assert.Contains("react", text);
        Assert.Contains("caps", text);
    }

    /// <summary>Minimal valid observation for exercising a standalone controller.</summary>
    private static FootballerObservation Observation(int id, TeamId team, TacticalRole role) => new()
    {
        SelfId = id,
        SelfTeam = team,
        SelfRole = role,
        SelfPosition = new System.Numerics.Vector3(10f, 0f, 10f),
        SelfVelocity = System.Numerics.Vector3.Zero,
        SelfFacingXZ = new System.Numerics.Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 0f,
        PerceivedBallPosition = new System.Numerics.Vector3(20f, 0f, 10f),
        PerceivedBallVelocity = System.Numerics.Vector3.Zero,
        PossessingTeam = TeamId.Away,
        BallLastTouchedByPlayerId = 99,
        TacticalAnchorPosition = new System.Numerics.Vector3(10f, 0f, 10f),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 0f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        VisiblePlayers = Array.Empty<PerceivedEntity>(),
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = new PlayerTendencies(),
        SelfAttributes = new PlayerAttributes()
    };

    private static MatchScenario WithAi(MatchScenario source, AiRuntimeConfig ai)
    {
        var scenario = new MatchScenario
        {
            Name = source.Name,
            Seed = source.Seed,
            Pitch = source.Pitch,
            HomeProfile = source.HomeProfile,
            AwayProfile = source.AwayProfile,
            Ai = ai,
            HalfDurationSeconds = source.HalfDurationSeconds,
            MatchClockSeconds = source.MatchClockSeconds,
            Period = source.Period,
            HomeScore = source.HomeScore,
            AwayScore = source.AwayScore,
            HumanPlayerId = source.HumanPlayerId,
            BallPosition = source.BallPosition,
            BallVelocity = source.BallVelocity,
            BallAngularVelocity = source.BallAngularVelocity,
            BallSetup = source.BallSetup,
            BallOwnerPlayerId = source.BallOwnerPlayerId,
            IntendedReceiverId = source.IntendedReceiverId,
            BallLastTouchedByTeam = source.BallLastTouchedByTeam
        };
        foreach (ScenarioPlayer player in source.Players) scenario.Add(player);
        return scenario;
    }
}
