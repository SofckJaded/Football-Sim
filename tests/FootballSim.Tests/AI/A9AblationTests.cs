using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A9.2 — the feature ablations, at the level the whole batch rests on: each switch's off build is a
/// <b>legal, deterministic, completable build</b>, and the default configuration is the shipped one.
///
/// The per-mechanism evidence (cover opens no request, the rest-defence picture reads secure, a cone stops
/// hiding a man, commitment never holds, the run channel goes silent, the keeper never sweeps) lives in the
/// feature-specific ablation test files and in the <c>--ablations</c> probe. These tests pin the properties a
/// measurement batch must not get wrong: an off build still plays a match, the off switches do not touch
/// simulation randomness or iteration order, and turning every switch explicitly on reproduces the default
/// world exactly — which is what makes the on column of every A/B the shipped behaviour rather than a
/// near-miss.
/// </summary>
public sealed class A9AblationTests
{
    private const ulong Seed = 20260818UL;
    private const int PlayersPerTeam = 5;
    private const float HalfSeconds = 45f;

    /// <summary>Every ablation the plan names, plus the all-off build, with the switch that defines it.</summary>
    public static IEnumerable<object[]> AblationConfigs()
    {
        yield return new object[] { "local cover", new AiRuntimeConfig { EnableLocalCover = false } };
        yield return new object[] { "rest defence", new AiRuntimeConfig { EnableRestDefence = false } };
        yield return new object[] { "perception limits", new AiRuntimeConfig { EnablePerceptionLimits = false } };
        yield return new object[] { "orientation", new AiRuntimeConfig { EnableOrientation = false } };
        yield return new object[] { "commitment", new AiRuntimeConfig { EnableCommitment = false } };
        yield return new object[] { "run coordination", new AiRuntimeConfig { EnableRunCoordination = false } };
        yield return new object[] { "goalkeeper sweep", new AiRuntimeConfig { EnableGoalkeeperSweep = false } };
        yield return new object[] { "lookahead", new AiRuntimeConfig { EnableShallowSearch = false } };
        yield return new object[] { "all A9.2 ablations", AllA9AblationsOff() };
    }

    /// <summary>The most extreme build the measurement batch uses: all seven A9.2 mechanisms removed at once.</summary>
    private static AiRuntimeConfig AllA9AblationsOff() => new()
    {
        EnableLocalCover = false,
        EnableRestDefence = false,
        EnablePerceptionLimits = false,
        EnableOrientation = false,
        EnableCommitment = false,
        EnableRunCoordination = false,
        EnableGoalkeeperSweep = false
    };

    /// <summary>
    /// Every off build reaches full time. An ablation that only "works" because it stops the build from
    /// completing is not an ablation; it is a broken build, and it would poison every difference the probe
    /// reports.
    /// </summary>
    [Theory]
    [MemberData(nameof(AblationConfigs))]
    public void EveryAblationBuild_ReachesFullTime(string feature, AiRuntimeConfig ai)
    {
        MatchSimulationEngine engine = NewEngine(ai);
        bool fullTime = RunToFullTime(engine);

        Assert.True(fullTime,
            $"the '{feature}' off build did not reach full time: phase {engine.Rules.Phase} at tick {engine.CurrentTick}");
        Assert.True(engine.Analytics.HomePassesAttempted + engine.Analytics.AwayPassesAttempted > 0,
            $"the '{feature}' off build played no passes");
    }

    /// <summary>
    /// An explicitly all-on configuration is the default world, tick for tick. The per-switch "on" column of
    /// every A/B therefore describes the shipped behaviour: the switches are the measurement's lever, not a
    /// hidden second default.
    /// </summary>
    [Fact]
    public void ExplicitlyAllOn_ReproducesTheDefaultWorldExactly()
    {
        var explicitOn = new AiRuntimeConfig
        {
            EnableLocalCover = true,
            EnableRestDefence = true,
            EnablePerceptionLimits = true,
            EnableOrientation = true,
            EnableCommitment = true,
            EnableRunCoordination = true,
            EnableGoalkeeperSweep = true
        };
        MatchSimulationEngine reference = NewEngine(new AiRuntimeConfig());
        MatchSimulationEngine explicitWorld = NewEngine(explicitOn);

        AssertSameWorldAtEveryTick(reference, explicitWorld, 900);
    }

    /// <summary>
    /// An off build is as deterministic as the shipped build. This is the cheap guard against the way an
    /// ablation tends to go wrong: a switch that changes which unordered collection is iterated, or which
    /// stream is consumed, would still "look like" a world while making the A/B irreproducible.
    /// </summary>
    [Theory]
    [MemberData(nameof(AblationConfigs))]
    public void EveryAblationBuild_IsDeterministic(string feature, AiRuntimeConfig ai)
    {
        MatchSimulationEngine first = NewEngine(ai);
        MatchSimulationEngine second = NewEngine(ai);

        AssertSameWorldAtEveryTick(first, second, 360, feature);
    }

    // ---------------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------------

    private static MatchSimulationEngine NewEngine(AiRuntimeConfig ai) => new(new PreMatchConfig
    {
        Seed = Seed,
        PlayersPerTeam = PlayersPerTeam,
        HalfDurationSeconds = HalfSeconds,
        HomeProfile = TeamTacticalProfile.PossessionControl,
        AwayProfile = TeamTacticalProfile.DirectCounterattack,
        Ai = ai
    });

    private static bool RunToFullTime(MatchSimulationEngine engine)
    {
        int maxTicks = (int)(HalfSeconds * 2.25f * 60f) + 1200;
        while (engine.Rules.Phase != MatchPhase.FullTime && engine.CurrentTick < maxTicks)
            engine.StepTick();
        return engine.Rules.Phase == MatchPhase.FullTime;
    }

    /// <summary>
    /// Steps two engines together and requires the whole observable world to agree at every tick: phase,
    /// clock, score, ball and every player's position/velocity. Identical worlds are the only acceptable
    /// result for a configuration that states the shipped values.
    /// </summary>
    private static void AssertSameWorldAtEveryTick(
        MatchSimulationEngine left, MatchSimulationEngine right, int ticks, string label = "all-on")
    {
        for (int tick = 1; tick <= ticks; tick++)
        {
            left.StepTick();
            right.StepTick();

            Assert.True(left.Rules.Phase == right.Rules.Phase,
                $"{label}: phase diverged at tick {tick} ({left.Rules.Phase} vs {right.Rules.Phase})");
            Assert.True(left.CurrentTick == right.CurrentTick,
                $"{label}: tick counter diverged at tick {tick}");
            Assert.True(left.Analytics.HomeGoals == right.Analytics.HomeGoals &&
                        left.Analytics.AwayGoals == right.Analytics.AwayGoals,
                $"{label}: score diverged at tick {tick}");
            Assert.True(Close(left.Ball.Position, right.Ball.Position),
                $"{label}: ball diverged at tick {tick} ({left.Ball.Position} vs {right.Ball.Position})");

            IReadOnlyList<FootballerState> leftPlayers = left.Players;
            IReadOnlyList<FootballerState> rightPlayers = right.Players;
            for (int i = 0; i < leftPlayers.Count; i++)
            {
                Assert.True(Close(leftPlayers[i].Position, rightPlayers[i].Position),
                    $"{label}: player #{leftPlayers[i].Id} diverged at tick {tick}");
                Assert.True(Close(leftPlayers[i].Velocity, rightPlayers[i].Velocity),
                    $"{label}: player #{leftPlayers[i].Id} velocity diverged at tick {tick}");
            }
        }
    }

    private static bool Close(Vector3 left, Vector3 right) =>
        left.X.Equals(right.X) && left.Y.Equals(right.Y) && left.Z.Equals(right.Z);
}
