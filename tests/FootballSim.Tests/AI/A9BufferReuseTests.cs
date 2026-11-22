using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A9.3 part 2 — the allocation reuses that changed no simulation behaviour. Each test pins the property
/// that made a reuse safe rather than the reuse itself: the engine-owned perception buffer must produce
/// exactly the observation the allocating path produced, the engine must keep one buffer per observer and
/// refill it in place, and the cached label strings must still read as the text they replaced.
/// </summary>
public sealed class A9BufferReuseTests
{
    private const float Dt = SimConstants.FixedDeltaTime;
    private const int SelfId = 2;
    private const int OpponentId = 21;
    private const int TeammateId = 3;

    [Fact]
    public void PerceptionBuffer_ProducesTheSameObservationAsTheAllocatingPath()
    {
        FootballerState[] players = World();
        FootballerObservation expected = Observe(new PerceptionSystem(), players, seed: 20261122, tick: 3);
        var buffer = new List<PerceivedEntity>();
        FootballerObservation actual = Observe(new PerceptionSystem(), players, seed: 20261122, tick: 3, buffer);

        // The buffer is the list the observation publishes; the allocating path keeps its own.
        Assert.Same(buffer, actual.VisiblePlayers);
        Assert.NotSame(expected.VisiblePlayers, actual.VisiblePlayers);

        Assert.Equal(expected.VisiblePlayers.Count, actual.VisiblePlayers.Count);
        for (int index = 0; index < expected.VisiblePlayers.Count; index++)
            Assert.Equal(expected.VisiblePlayers[index], actual.VisiblePlayers[index]);
    }

    [Fact]
    public void PerceptionBuffer_IsClearedBeforeItIsFilled()
    {
        FootballerState[] players = World();
        var buffer = new List<PerceivedEntity>
        {
            new(999, TeamId.Away, TacticalRole.Striker, new Vector3(30f, 0f, 30f), Vector3.Zero, 1f)
        };

        FootballerObservation observation = Observe(new PerceptionSystem(), players, seed: 20261122, tick: 1, buffer);

        Assert.Same(buffer, observation.VisiblePlayers);
        Assert.DoesNotContain(observation.VisiblePlayers, entity => entity.PlayerId == 999);
    }

    [Fact]
    public void Engine_GivesEveryObserverItsOwnBuffer_AndRefillsItInPlace()
    {
        var engine = new MatchSimulationEngine();
        engine.StepTick();

        var distinctLists = new List<IReadOnlyList<PerceivedEntity>>();
        var firstLook = new Dictionary<int, IReadOnlyList<PerceivedEntity>>();
        foreach (FootballerState player in engine.Players)
        {
            if (!player.IsActiveOnPitch) continue;
            FootballerObservation? observation = engine.GetLastObservation(player.Id);
            Assert.NotNull(observation);

            // Two observers in one tick must never share a list: that is the aliasing the per-observer
            // buffer exists to prevent.
            for (int index = 0; index < distinctLists.Count; index++)
                Assert.False(ReferenceEquals(distinctLists[index], observation!.VisiblePlayers),
                    "two observers share one visible-entity buffer");
            distinctLists.Add(observation!.VisiblePlayers);
            firstLook[player.Id] = observation.VisiblePlayers;
        }

        Assert.Equal(engine.Players.Count, firstLook.Count);

        // A rebuild of one observer refills that observer's list; the other observers' views of the
        // previous tick are the only ones the next tick replaces.
        engine.StepTick();
        foreach (KeyValuePair<int, IReadOnlyList<PerceivedEntity>> look in firstLook)
        {
            FootballerObservation? observation = engine.GetLastObservation(look.Key);
            Assert.NotNull(observation);
            Assert.Same(look.Value, observation!.VisiblePlayers);
        }
    }

    [Fact]
    public void DebugSummary_StillFormatsTheChosenActionAndUtility()
    {
        var engine = new MatchSimulationEngine();
        for (int tick = 0; tick < 3; tick++) engine.StepTick();

        int checkedPlayers = 0;
        foreach (FootballerState player in engine.Players)
        {
            if (!player.IsActiveOnPitch) continue;
            if (engine.GetController(player.Id) is not TacticalUtilityAiController controller) continue;
            FootballerObservation? observation = engine.GetLastObservation(player.Id);
            Assert.NotNull(observation);

            string summary = controller.GetDebugSummary();
            if (summary.StartsWith("SetPieceRestart (", StringComparison.Ordinal) ||
                summary.StartsWith("Reset (", StringComparison.Ordinal))
            {
                continue;
            }

            UtilityCandidate selected = controller.LastSelected;
            Assert.Equal(
                $"{selected.ActionName} [{observation!.TacticalJob}] (U={selected.UtilityScore:F2})",
                summary);
            checkedPlayers++;
        }

        Assert.True(checkedPlayers > 0, "the fixture must take at least one ordinary decision");
    }

    [Fact]
    public void JobCandidateNames_StillReadAsTheJobAndItsAdjustedVariant()
    {
        var engine = new MatchSimulationEngine();
        int checkedCandidates = 0;
        for (int tick = 0; tick < 40 && checkedCandidates < 20; tick++)
        {
            engine.StepTick();
            foreach (FootballerState player in engine.Players)
            {
                if (!player.IsActiveOnPitch) continue;
                if (engine.GetController(player.Id) is not TacticalUtilityAiController controller) continue;

                IReadOnlyList<UtilityCandidate> candidates = controller.LastCandidates;
                for (int index = 0; index < candidates.Count; index++)
                {
                    UtilityCandidate candidate = candidates[index];
                    if (candidate.Key.Kind != CandidateKind.Job) continue;
                    // The press model's job keys carry a lower-case tag and a different label; only the
                    // allocator's own job candidates are named after the TacticalJob enum.
                    if (!Enum.TryParse(candidate.Key.TargetTag, out TacticalJob job) ||
                        job.ToString() != candidate.Key.TargetTag)
                    {
                        continue;
                    }

                    string expected = candidate.Key.Adjusted
                        ? "Job:" + candidate.Key.TargetTag + "Adj"
                        : "Job:" + candidate.Key.TargetTag;
                    Assert.Equal(expected, candidate.ActionName);
                    checkedCandidates++;
                }
            }
        }

        Assert.True(checkedCandidates > 0, "the fixture must generate at least one job candidate");
    }

    // ---- plumbing --------------------------------------------------------------------------------

    private static FootballerObservation Observe(
        PerceptionSystem perception,
        FootballerState[] players,
        ulong seed,
        int tick,
        List<PerceivedEntity>? visibleBuffer = null) =>
        perception.BuildObservation(
            players[0], players, Ball(), confirmedPossessingTeam: null, MatchPhase.FirstHalf, tick * Dt,
            attackingDirX: 1f, isSetPieceTaker: false, TeamTacticalProfile.PossessionControl,
            new SeededRandom(seed), tick: tick, visibleBuffer: visibleBuffer);

    private static FootballerState[] World()
    {
        FootballerState self = Player(SelfId, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero,
            new Vector2(1f, 0f));
        FootballerState opponent = Player(OpponentId, TeamId.Away, TacticalRole.CenterBack,
            new Vector3(9f, 0f, -2f), new Vector2(-1f, 0f), new Vector3(-1f, 0f, 0f));
        FootballerState teammate = Player(TeammateId, TeamId.Home, TacticalRole.Striker,
            new Vector3(5f, 0f, 3f), new Vector2(1f, 0f), new Vector3(1f, 0f, 0f));
        return new[] { self, opponent, teammate };
    }

    private static FootballerState Player(
        int id,
        TeamId team,
        TacticalRole role,
        Vector3 position,
        Vector2 facing,
        Vector3 velocity = default) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = new PlayerTendencies(),
        Position = position,
        Velocity = velocity,
        FacingXZ = facing
    };

    private static BallState Ball() => new()
    {
        Position = new Vector3(8f, 0.11f, 0f),
        Velocity = new Vector3(-3f, 0f, 0f)
    };
}
