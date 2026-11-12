using System;
using System.Collections.Generic;
using System.Linq;
using FootballSim;
using FootballSim.Core;
using FootballSim.Scenarios;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A1.3 — separate random streams.
///
/// The property that matters is isolation: a consumer's draw sequence must depend only on its own
/// calls. Before this change a shared stream meant every perception query shifted the physics sequence,
/// so perception work and execution work could not be developed or replayed independently.
/// </summary>
public sealed class RandomStreamTests
{
    [Fact]
    public void Derive_IsDeterministic()
    {
        foreach (RandomStream stream in Enum.GetValues<RandomStream>())
        {
            Assert.Equal(RandomStreams.Derive(12345UL, stream), RandomStreams.Derive(12345UL, stream));
        }
    }

    [Fact]
    public void Derive_GivesEveryStreamADifferentSeed()
    {
        ulong execution = RandomStreams.Derive(2026UL, RandomStream.Execution);
        ulong perception = RandomStreams.Derive(2026UL, RandomStream.Perception);
        ulong decision = RandomStreams.Derive(2026UL, RandomStream.Decision);

        Assert.NotEqual(execution, perception);
        Assert.NotEqual(execution, decision);
        Assert.NotEqual(perception, decision);
        Assert.NotEqual(0UL, execution);
        Assert.NotEqual(0UL, perception);
        Assert.NotEqual(0UL, decision);
    }

    [Fact]
    public void Derive_SeparatesAdjacentSeeds()
    {
        // Adjacent match seeds must not produce correlated streams; that is the whole point of mixing
        // rather than adding a small constant.
        var seen = new HashSet<ulong>();
        for (ulong seed = 1UL; seed <= 512UL; seed++)
            Assert.True(seen.Add(RandomStreams.Derive(seed, RandomStream.Execution)),
                $"seed {seed} collided with an earlier stream seed");
    }

    [Fact]
    public void Streams_DoNotShareState()
    {
        SeededRandom execution = RandomStreams.Create(7UL, RandomStream.Execution);
        SeededRandom perception = RandomStreams.Create(7UL, RandomStream.Perception);

        // Drain the perception stream; the execution stream must be untouched.
        ulong executionBefore = execution.State;
        for (int i = 0; i < 1000; i++) perception.NextFloat();

        Assert.Equal(executionBefore, execution.State);
        Assert.NotEqual(executionBefore, perception.State);
    }

    [Fact]
    public void PerceptionDraws_DoNotChangeTheExecutionSequence()
    {
        SeededRandom execution = RandomStreams.Create(99UL, RandomStream.Execution);
        SeededRandom perception = RandomStreams.Create(99UL, RandomStream.Perception);

        // Reference sequence from a fresh execution stream.
        SeededRandom reference = RandomStreams.Create(99UL, RandomStream.Execution);
        var expected = new uint[32];
        for (int i = 0; i < expected.Length; i++) expected[i] = reference.NextUInt();

        // The same sequence, with perception draws interleaved.
        for (int i = 0; i < expected.Length; i++)
        {
            perception.NextGaussian();
            Assert.Equal(expected[i], execution.NextUInt());
        }
    }

    [Fact]
    public void SameSeedStillProducesAnIdenticalMatch()
    {
        var a = new MatchSimulationEngine(new PreMatchConfig { Seed = 555UL, PlayersPerTeam = 11 });
        var b = new MatchSimulationEngine(new PreMatchConfig { Seed = 555UL, PlayersPerTeam = 11 });

        for (int tick = 0; tick < 600; tick++)
        {
            a.StepTick();
            b.StepTick();
        }

        Assert.Equal(a.Ball.Position, b.Ball.Position);
        Assert.Equal(a.Ball.Velocity, b.Ball.Velocity);
        Assert.Equal(a.Possession, b.Possession);
        for (int i = 0; i < a.Players.Count; i++)
            Assert.Equal(a.Players[i].Position, b.Players[i].Position);
    }

    [Fact]
    public void InterleavedMatchesDoNotInterfere()
    {
        var alone = new MatchSimulationEngine(new PreMatchConfig { Seed = 808UL });
        var interleaved = new MatchSimulationEngine(new PreMatchConfig { Seed = 808UL });
        var distractor = new MatchSimulationEngine(new PreMatchConfig { Seed = 909UL });

        for (int tick = 0; tick < 400; tick++)
        {
            alone.StepTick();

            interleaved.StepTick();
            distractor.StepTick();
            distractor.StepTick();
        }

        Assert.Equal(alone.Ball.Position, interleaved.Ball.Position);
        Assert.Equal(alone.Possession, interleaved.Possession);
        for (int i = 0; i < alone.Players.Count; i++)
        {
            Assert.Equal(alone.Players[i].Position, interleaved.Players[i].Position);
            Assert.Equal(alone.Players[i].Velocity, interleaved.Players[i].Velocity);
        }
    }

    [Fact]
    public void DifferentSeedsProduceDifferentMatches()
    {
        ScenarioRunResult first = ScenarioRunner.Run(WithSeed(ScenarioLibrary.BuildUpThreeVersusTwo(), 1UL), 200);
        ScenarioRunResult second = ScenarioRunner.Run(WithSeed(ScenarioLibrary.BuildUpThreeVersusTwo(), 2UL), 200);

        // Not every seed pair must diverge in 200 ticks, but these two do — and if they ever stop doing
        // so, the seed is no longer reaching perception and the stream split quietly stopped mattering.
        Assert.True(
            first.Engine.Players.Any(p => second.Engine.Players.First(q => q.Id == p.Id).Position != p.Position) ||
            first.BallPosition != second.BallPosition,
            "different seeds produced an identical 200-tick scenario run");
    }

    [Fact]
    public void ScenarioRunsStayIdenticalWithTheSplitStreams()
    {
        ScenarioRunResult a = ScenarioRunner.Run(ScenarioLibrary.HighLineRun(), 240);
        ScenarioRunResult b = ScenarioRunner.Run(ScenarioLibrary.HighLineRun(), 240);

        Assert.Equal(a.BallPosition, b.BallPosition);
        Assert.Equal(a.ContactAttempts.Count, b.ContactAttempts.Count);
        Assert.Equal(a.Possession, b.Possession);
    }

    private static MatchScenario WithSeed(MatchScenario source, ulong seed)
    {
        var scenario = new MatchScenario
        {
            Name = source.Name,
            Seed = seed,
            Pitch = source.Pitch,
            HomeProfile = source.HomeProfile,
            AwayProfile = source.AwayProfile,
            HalfDurationSeconds = source.HalfDurationSeconds,
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
