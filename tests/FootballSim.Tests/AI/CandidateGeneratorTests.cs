using System;
using System.Collections.Generic;
using System.Linq;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A1.1d.2 — the candidate generator is a module of its own.
///
/// These tests cover the seam the extraction created (a public type with a small surface), not the
/// generation rules themselves: those are already pinned by the scenario suite and by the byte-identical
/// 11v11 reference match recorded in `artifacts/advanced-ai/`.
/// </summary>
public sealed class CandidateGeneratorTests
{
    [Fact]
    public void GenerateOnBall_ProducesKeyedCandidates()
    {
        var generator = new CandidateGenerator(new PitchDimensions());

        generator.GenerateOnBallCandidates(OnBallObservation(), new System.Numerics.Vector3(52.5f, 0f, 0f), 0f);

        Assert.NotEmpty(generator.Candidates);
        Assert.Equal(generator.Candidates.Count, generator.CandidateCount);
        Assert.All(generator.Candidates, candidate => Assert.False(candidate.Key.IsNone));
        Assert.Contains(generator.Candidates, c => c.Key.Kind == CandidateKind.Carry);
    }

    [Fact]
    public void GenerateOffBall_ProducesKeyedCandidates()
    {
        var generator = new CandidateGenerator(new PitchDimensions());

        generator.GenerateOffBallCandidates(
            OffBallObservation(),
            oppGoalCenter: new System.Numerics.Vector3(52.5f, 0f, 0f),
            ownGoalCenter: new System.Numerics.Vector3(-52.5f, 0f, 0f),
            myInterceptXZ: new System.Numerics.Vector2(0f, 0f),
            isPrimaryBallResponder: false,
            offsideLineProjX: 0f);

        Assert.NotEmpty(generator.Candidates);
        Assert.All(generator.Candidates, candidate => Assert.False(candidate.Key.IsNone));
    }

    [Fact]
    public void Clear_DropsThePreviousSet()
    {
        var generator = new CandidateGenerator(new PitchDimensions());
        generator.GenerateOnBallCandidates(OnBallObservation(), new System.Numerics.Vector3(52.5f, 0f, 0f), 0f);
        Assert.NotEmpty(generator.Candidates);

        generator.Clear();

        Assert.Empty(generator.Candidates);
        Assert.Equal(0, generator.CandidateCount);
    }

    [Fact]
    public void Generation_IsDeterministicForTheSameInput()
    {
        var first = new CandidateGenerator(new PitchDimensions());
        var second = new CandidateGenerator(new PitchDimensions());

        first.GenerateOnBallCandidates(OnBallObservation(), new System.Numerics.Vector3(52.5f, 0f, 0f), 0f);
        second.GenerateOnBallCandidates(OnBallObservation(), new System.Numerics.Vector3(52.5f, 0f, 0f), 0f);

        Assert.Equal(first.CandidateCount, second.CandidateCount);
        for (int i = 0; i < first.CandidateCount; i++)
        {
            Assert.Equal(first.Candidates[i].ActionName, second.Candidates[i].ActionName);
            Assert.Equal(first.Candidates[i].Key, second.Candidates[i].Key);
            Assert.Equal(first.Candidates[i].UtilityScore, second.Candidates[i].UtilityScore);
        }
    }

    [Fact]
    public void TackleCooldown_StartsExpiredAndNeverGoesNegative()
    {
        var generator = new CandidateGenerator(new PitchDimensions());
        Assert.Equal(0f, generator.TackleCooldown);

        generator.TickTimers(SimConstants.FixedDeltaTime);

        Assert.Equal(0f, generator.TackleCooldown);
    }

    private static FootballerObservation OnBallObservation() => Observation(ballAtFeet: true);

    private static FootballerObservation OffBallObservation() => Observation(ballAtFeet: false);

    private static FootballerObservation Observation(bool ballAtFeet) => new()
    {
        SelfId = 8,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CentralMidfielder,
        SelfPosition = new System.Numerics.Vector3(0f, 0f, 0f),
        SelfVelocity = System.Numerics.Vector3.Zero,
        SelfFacingXZ = new System.Numerics.Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = ballAtFeet,
        ControlQuality01 = ballAtFeet ? 1f : 0f,
        PerceivedBallPosition = ballAtFeet
            ? new System.Numerics.Vector3(0.4f, 0f, 0f)
            : new System.Numerics.Vector3(8f, 0f, 0f),
        PerceivedBallVelocity = System.Numerics.Vector3.Zero,
        PossessingTeam = ballAtFeet ? TeamId.Home : TeamId.Away,
        BallLastTouchedByPlayerId = 8,
        BallControlState = ballAtFeet ? BallControlState.Controlled : BallControlState.Loose,
        BallControllingPlayerId = ballAtFeet ? 8 : null,
        TacticalAnchorPosition = new System.Numerics.Vector3(0f, 0f, 0f),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 0f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        VisiblePlayers = new List<PerceivedEntity>
        {
            new(9, TeamId.Home, TacticalRole.Striker, new System.Numerics.Vector3(20f, 0f, 2f),
                System.Numerics.Vector3.Zero, 0.9f),
            new(12, TeamId.Away, TacticalRole.CenterBack, new System.Numerics.Vector3(30f, 0f, -4f),
                System.Numerics.Vector3.Zero, 0.9f)
        },
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = new PlayerTendencies(),
        SelfAttributes = new PlayerAttributes()
    };
}
