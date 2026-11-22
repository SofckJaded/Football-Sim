using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A9.3 — the Release 11v11 budget, as portable checks instead of a timing claim.
///
/// The milestone's claim is that candidate counts, shallow-search nodes and trajectory samples are
/// <b>bounded</b>, and that tracing off is free. A timing measurement lives in the recorded artifact (it
/// is hardware-specific); these tests assert the work itself, on any machine:
/// every configured cap binds when lowered, every per-decision budget holds in a real match, and a
/// tracing-off match records nothing while its world is identical to a recording one.
/// </summary>
public sealed class A9WorkLimitTests
{
    private const int CarrierId = 8;
    private static readonly PitchDimensions Pitch = new();

    // ---------------------------------------------------------------------------------------------
    // The off-ball cap is live configuration, not documentation
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// <see cref="AiRuntimeConfig.MaxOffBallCandidates"/> was declared for A9.3 and consumed nowhere. It
    /// now bounds the finished off-ball set the way the carrier cap bounds the carrier's: lowering it below
    /// the generated count removes candidates instead of being ignored.
    /// </summary>
    [Fact]
    public void TheConfiguredOffBallCap_BoundsTheOffBallSet()
    {
        var broad = new CandidateGenerator(Pitch);
        broad.GenerateOffBallCandidates(OffBallObservation(), new Vector3(Pitch.HalfLength, 0f, 0f),
            new Vector3(-Pitch.HalfLength, 0f, 0f), myInterceptXZ: new Vector2(30f, 8f),
            isPrimaryBallResponder: false, offsideLineProjX: 20f);

        int generated = broad.CandidateCount;
        Assert.True(generated > 2, $"the fixture must generate more than two off-ball candidates, generated {generated}");
        Assert.Equal(0, broad.Work.OffBallCapTrims);
        Assert.Equal(generated, broad.Work.OffBallCandidatesGenerated);

        int cap = generated - 1;
        var narrow = new CandidateGenerator(Pitch, ai: new AiRuntimeConfig { MaxOffBallCandidates = cap });
        narrow.GenerateOffBallCandidates(OffBallObservation(), new Vector3(Pitch.HalfLength, 0f, 0f),
            new Vector3(-Pitch.HalfLength, 0f, 0f), myInterceptXZ: new Vector2(30f, 8f),
            isPrimaryBallResponder: false, offsideLineProjX: 20f);

        Assert.Equal(cap, narrow.CandidateCount);
        Assert.Equal(1, narrow.OffBallCandidatesTrimmed);
        Assert.Equal(1, narrow.Work.OffBallCapTrims);
        Assert.Equal(generated, narrow.Work.OffBallCandidatesGenerated);

        // The kept set is the best-scoring part of the broad set: no trimmed candidate outscores a kept one.
        HashSet<CandidateKey> keptKeys = narrow.Candidates.Select(c => c.Key).ToHashSet();
        float keptFloor = narrow.Candidates.Min(c => c.UtilityScore);
        float trimmedCeiling = broad.Candidates.Where(c => !keptKeys.Contains(c.Key)).Max(c => c.UtilityScore);
        Assert.True(keptFloor >= trimmedCeiling,
            $"a trimmed candidate scored {trimmedCeiling} above the kept floor {keptFloor}");
    }

    // ---------------------------------------------------------------------------------------------
    // The reference match stays inside every budget
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Steps a short 11v11 match with the shipped configuration and asserts the measured work against the
    /// configured bounds. This is the portable version of the recorded perf artifact: it does not depend on
    /// this machine's speed, only on the match actually generating candidates, search branches and
    /// trajectory samples and on none of them exceeding their cap.
    /// </summary>
    [Fact]
    public void TheReferenceMatch_StaysWithinEveryWorkBudget()
    {
        var config = new PreMatchConfig
        {
            Seed = 20260818UL,
            PlayersPerTeam = 11,
            HalfDurationSeconds = 30f,
            HomeProfile = TeamTacticalProfile.PossessionControl,
            AwayProfile = TeamTacticalProfile.DirectCounterattack
        };
        var engine = new MatchSimulationEngine(config);
        for (int i = 0; i < 1_500; i++) engine.StepTick();

        AiWorkStats work = engine.GetAiWorkStats();
        AiRuntimeConfig ai = config.Ai;

        Assert.True(work.CarrierDecisions > 0, "the sample must contain carrier decisions");
        Assert.True(work.OffBallDecisions > 0, "the sample must contain off-ball decisions");

        // Off-ball: the structural set stays under the shipped cap, so the cap is inert until a
        // configuration lowers it — and the generator never runs unbounded.
        Assert.True(work.MaxOffBallCandidatesGenerated <= ai.MaxOffBallCandidates,
            $"off-ball generated {work.MaxOffBallCandidatesGenerated} > cap {ai.MaxOffBallCandidates}");
        Assert.Equal(0, work.OffBallCapTrims);
        Assert.True(work.OffBallCandidatesKept <= ai.MaxOffBallCandidates * work.OffBallDecisions,
            "off-ball kept count exceeded the cap times the decision count");

        // Carrier: the set is trimmed to the cap when generation exceeds it, and never above it.
        Assert.True(work.CarrierCandidatesKept <= ai.MaxCarrierCandidates * work.CarrierDecisions,
            "carrier kept count exceeded the cap times the decision count");

        // A8.2 search: 3 first actions × 3 continuations is a hard per-decision ceiling.
        Assert.True(work.MaxSearchContinuationsPerDecision <= ShallowActionSearch.MaxContinuationsEvaluated,
            $"search evaluated {work.MaxSearchContinuationsPerDecision} continuations, budget " +
            $"{ShallowActionSearch.MaxContinuationsEvaluated}");
        Assert.True(work.SearchContinuations <= work.SearchFirstActions * ShallowActionSearch.MaxContinuationsPerAction,
            "continuations exceeded the per-action budget summed over the first actions");

        // A2.3/A6.5/A7.3 trajectory: one estimate is capped by MaxSamples.
        Assert.True(work.TrajectoryBuilds > 0, "the sample must build at least one trajectory estimate");
        Assert.True(work.MaxTrajectorySamples <= BallTrajectoryEstimate.MaxSamples,
            $"trajectory produced {work.MaxTrajectorySamples} samples, bound {BallTrajectoryEstimate.MaxSamples}");
        Assert.True(work.TrajectorySamples >= work.TrajectoryBuilds,
            "every recorded estimate produced at least one sample");

        // Team-level refresh is driven by a cadence, not by every tick.
        Assert.True(work.BeliefRebuilds > 0, "the sample must rebuild the team belief map");
        Assert.True(work.BeliefRebuilds <= engine.CurrentTick,
            "the team belief map cannot be rebuilt more often than once per tick");
    }

    // ---------------------------------------------------------------------------------------------
    // Tracing off is free, and recording does not change the world
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The shipped path has the trace buffer disabled: after a match it must hold nothing. A recording
    /// engine with the same config and seed records entries and must reach exactly the same world — the
    /// trace reads the decision, it never feeds it.
    /// </summary>
    [Fact]
    public void TracingOff_RecordsNothing_AndTracingDoesNotChangeTheWorld()
    {
        var config = new PreMatchConfig
        {
            Seed = 20260704UL,
            PlayersPerTeam = 11,
            HalfDurationSeconds = 30f
        };

        var offEngine = new MatchSimulationEngine(config);
        for (int i = 0; i < 300; i++) offEngine.StepTick();

        var onEngine = new MatchSimulationEngine(config);
        onEngine.DecisionTrace.IsEnabled = true;
        for (int i = 0; i < 300; i++) onEngine.StepTick();

        Assert.Equal(0, offEngine.DecisionTrace.Count);
        Assert.True(onEngine.DecisionTrace.Count > 0, "a recording engine must record decisions");
        Assert.Equal(offEngine.Ball.Position, onEngine.Ball.Position);
        Assert.Equal(offEngine.Analytics.HomePassesAttempted, onEngine.Analytics.HomePassesAttempted);
        Assert.Equal(offEngine.Analytics.AwayPassesAttempted, onEngine.Analytics.AwayPassesAttempted);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static PerceivedEntity Home(int id, TacticalRole role, Vector2 position) => new(
        id, TeamId.Home, role,
        new Vector3(position.X, 0f, position.Y),
        Vector3.Zero,
        1f);

    private static PerceivedEntity Away(int id, Vector2 position) => new(
        id, TeamId.Away, TacticalRole.CenterBack,
        new Vector3(position.X, 0f, position.Y),
        Vector3.Zero,
        1f);

    /// <summary>
    /// A home central midfielder holding a depth-run duty while a team-mate carries: the shape-following
    /// tail of the off-ball path runs, so the set is wider than one candidate, and the run paths are the
    /// kind set A5.1 offers a receiving duty.
    /// </summary>
    private static FootballerObservation OffBallObservation() => new()
    {
        SelfId = CarrierId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CentralMidfielder,
        SelfPosition = new Vector3(0f, 0f, 8f),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = new Vector3(30f, 0f, 8f),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = 9,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = 9,
        TacticalAnchorPosition = new Vector3(5f, 0f, 8f),
        TacticalJob = TacticalJob.DepthRun,
        TacticalJobAnchor = new Vector3(18f, 0f, 8f),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 20f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = 500,
        VisiblePlayers = new List<PerceivedEntity>
        {
            Home(9, TacticalRole.CentralMidfielder, new Vector2(30f, 8f)),
            Home(10, TacticalRole.Striker, new Vector2(32f, 8f)),
            Home(11, TacticalRole.Winger, new Vector2(26f, -14f)),
            Away(21, new Vector2(24f, 6f)),
            Away(22, new Vector2(28f, 12f))
        },
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.CentralMidfielder),
        SelfAttributes = new PlayerAttributes(),
        IsDesignatedBallResponder = false
    };
}
