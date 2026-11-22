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
/// A9.5 — the release-packaging configuration contract.
///
/// Two statements the release makes must be true in code, not only in a note: the shipped default is the
/// advanced AI (every mechanism switch on, except the deliberately off scheduler), and
/// <see cref="AiRuntimeConfig.Legacy"/> is a legal, named, <b>live</b> comparison build — a configuration a
/// tuning session can play beside the default, not an alias of it and not an inert label. The legacy build is
/// a measurement configuration: nothing in the shipped path constructs it, and these tests are the only place
/// it runs outside the <c>--match-11v11 legacy</c> / <c>--match-5v5 legacy</c> probes.
/// </summary>
public sealed class A9ReleaseConfigTests
{
    private const ulong Seed = 20260818UL;
    private const int PlayersPerTeam = 5;
    private const float HalfSeconds = 45f;

    /// <summary>
    /// A9.5's "enable by default": the shipped configuration has every advanced-AI mechanism on. This is what
    /// makes the release a documentation/versioning step rather than a default flip — the only off switch is
    /// the scheduler A9.3 documented as deliberately inert in the shipped path.
    /// </summary>
    [Fact]
    public void ShippedDefault_HasEveryAdvancedMechanismOn()
    {
        AssertDescribeState(AiRuntimeConfig.Default, expectedOn: true);

        Assert.False(AiRuntimeConfig.Default.EnableScheduledDecisions,
            "the one off-by-default switch must stay the scheduled-decision scheduler (A9.3: inert in the shipped path)");
        Assert.Contains("sched(off)", AiRuntimeConfig.Default.Describe());
    }

    /// <summary>
    /// The legacy build is a stated configuration, not a reflection-derived "everything false": each switch
    /// the milestone added is off by name, and the resulting object is a legal configuration.
    /// </summary>
    [Fact]
    public void Legacy_TurnsOffEverySwitchableMechanism()
    {
        AiRuntimeConfigValidation.Validate(AiRuntimeConfig.Legacy);
        AssertDescribeState(AiRuntimeConfig.Legacy, expectedOn: false);
        Assert.Contains("sched(off)", AiRuntimeConfig.Legacy.Describe());
    }

    /// <summary>
    /// The comparison build is not what a match gets by default: <see cref="PreMatchConfig.Ai"/> hands a
    /// match the shipped instance, and the legacy build is a different configuration with a different
    /// description. A future change that made the legacy object the default would fail here.
    /// </summary>
    [Fact]
    public void Legacy_IsNotTheShippedDefault()
    {
        Assert.NotSame(AiRuntimeConfig.Default, AiRuntimeConfig.Legacy);
        Assert.NotEqual(AiRuntimeConfig.Default.Describe(), AiRuntimeConfig.Legacy.Describe());

        Assert.Same(AiRuntimeConfig.Default, new PreMatchConfig().Ai);
        Assert.NotSame(AiRuntimeConfig.Legacy, new PreMatchConfig().Ai);
    }

    /// <summary>
    /// The legacy build must be <b>live</b>: a comparison configuration that reproduces the shipped world
    /// tick for tick would make every A/B against it a measurement of nothing. Diversifying inside the first
    /// 900 ticks is the cheap proof; A9.2 measured the single-switch builds diverging inside ~100 ticks, so an
    /// all-switch build that never diverges is inert.
    /// </summary>
    [Fact]
    public void Legacy_ChangesTheWorldFromTheShippedDefault()
    {
        MatchSimulationEngine shipped = NewEngine(AiRuntimeConfig.Default);
        MatchSimulationEngine legacy = NewEngine(AiRuntimeConfig.Legacy);

        const int window = 900;
        int divergentAt = -1;
        for (int tick = 1; tick <= window; tick++)
        {
            shipped.StepTick();
            legacy.StepTick();
            if (!SameWorld(shipped, legacy)) { divergentAt = tick; break; }
        }

        Assert.True(divergentAt >= 1,
            $"the legacy build matched the shipped world for {window} ticks — the comparison configuration would be inert");
    }

    /// <summary>
    /// The legacy build is a build a tuning session can actually run: it completes a match (the safe
    /// provenance a comparison needs) and it is deterministic, so a paired run is reproducible.
    /// </summary>
    [Fact]
    public void Legacy_ReachesFullTime_AndIsDeterministic()
    {
        MatchSimulationEngine first = NewEngine(AiRuntimeConfig.Legacy);
        MatchSimulationEngine second = NewEngine(AiRuntimeConfig.Legacy);

        int maxTicks = (int)(HalfSeconds * 2.25f * 60f) + 1200;
        while (first.Rules.Phase != MatchPhase.FullTime && first.CurrentTick < maxTicks)
        {
            first.StepTick();
            second.StepTick();
            Assert.True(SameWorld(first, second),
                $"the legacy build diverged from itself at tick {first.CurrentTick}");
        }

        Assert.True(first.Rules.Phase == MatchPhase.FullTime,
            $"the legacy build did not reach full time: phase {first.Rules.Phase} at tick {first.CurrentTick}");
        Assert.True(first.Analytics.HomePassesAttempted + first.Analytics.AwayPassesAttempted > 0,
            "the legacy build played no passes");
    }

    /// <summary>
    /// The <see cref="AiRuntimeConfig.Describe"/> prefixes of every switch the advanced-AI milestones
    /// introduced (A2.4 through A9.2). The state word follows immediately, so a token is
    /// <c>prefix + "on"</c> or <c>prefix + "off"</c>.
    /// </summary>
    private static readonly string[] AdvancedSwitchPrefixes =
    {
        "scan(",
        "calls(",
        "reception(",
        "cognitive(",
        "archetypes(",
        "combinations(",
        "search(",
        "switchrec(",
        "traps(",
        "routines(",
        "cover(",
        "restdef(",
        "percept(",
        "orient(",
        "commit(",
        "runs(",
        "sweep("
    };

    private static void AssertDescribeState(AiRuntimeConfig config, bool expectedOn)
    {
        string text = config.Describe();
        string state = expectedOn ? "on" : "off";
        foreach (string prefix in AdvancedSwitchPrefixes)
            Assert.Contains(prefix + state, text);
    }

    private static MatchSimulationEngine NewEngine(AiRuntimeConfig ai) => new(new PreMatchConfig
    {
        Seed = Seed,
        PlayersPerTeam = PlayersPerTeam,
        HalfDurationSeconds = HalfSeconds,
        HomeProfile = TeamTacticalProfile.PossessionControl,
        AwayProfile = TeamTacticalProfile.DirectCounterattack,
        Ai = ai
    });

    private static bool SameWorld(MatchSimulationEngine left, MatchSimulationEngine right)
    {
        if (left.Rules.Phase != right.Rules.Phase) return false;
        if (left.CurrentTick != right.CurrentTick) return false;
        if (left.Analytics.HomeGoals != right.Analytics.HomeGoals) return false;
        if (left.Analytics.AwayGoals != right.Analytics.AwayGoals) return false;
        if (!Close(left.Ball.Position, right.Ball.Position)) return false;

        IReadOnlyList<FootballerState> leftPlayers = left.Players;
        IReadOnlyList<FootballerState> rightPlayers = right.Players;
        for (int i = 0; i < leftPlayers.Count; i++)
        {
            if (!Close(leftPlayers[i].Position, rightPlayers[i].Position)) return false;
            if (!Close(leftPlayers[i].Velocity, rightPlayers[i].Velocity)) return false;
        }
        return true;
    }

    private static bool Close(Vector3 left, Vector3 right) =>
        left.X.Equals(right.X) && left.Y.Equals(right.Y) && left.Z.Equals(right.Z);
}
