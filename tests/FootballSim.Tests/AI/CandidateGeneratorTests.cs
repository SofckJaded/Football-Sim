using System;
using System.Collections.Generic;
using System.Linq;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
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

    [Fact]
    public void AnOpponentsCallForPass_DoesNotReleaseThisCarriersDelay()
    {
        var comms = new MatchCommunicationContext();
        var generator = new CandidateGenerator(new PitchDimensions(), comms);
        FootballerObservation awayCarrier = Observation(ballAtFeet: true, selfTeam: TeamId.Away);
        var awayGoal = new System.Numerics.Vector3(-52.5f, 0f, 0f);

        generator.GenerateOnBallCandidates(awayCarrier, awayGoal, 0f);
        Assert.Equal(0, PassCount(generator));

        // The home human asks for the ball: the away carrier's observations are unchanged, and the call
        // is not his team's instruction.
        comms.RaiseCallForPass(TeamId.Home, 9, 2.5f, throughBall: false);
        generator.Clear();
        generator.GenerateOnBallCandidates(awayCarrier, awayGoal, 0f);
        Assert.Equal(0, PassCount(generator));

        // Positive control: the same call from the carrier's own team does release the pass, so the
        // filter above is team identity and not a disabled call path.
        comms.RaiseCallForPass(TeamId.Away, 9, 2.5f, throughBall: false);
        generator.Clear();
        generator.GenerateOnBallCandidates(awayCarrier, awayGoal, 0f);
        Assert.True(PassCount(generator) > 0,
            "an own-team call for the pass must still release the carrier's normal delay");
    }

    private static int PassCount(CandidateGenerator generator)
    {
        int count = 0;
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            if (generator.Candidates[i].Key.Kind == CandidateKind.Pass) count++;
        }

        return count;
    }

    private static FootballerObservation OnBallObservation() => Observation(ballAtFeet: true);

    private static FootballerObservation OffBallObservation() => Observation(ballAtFeet: false);

    private static FootballerObservation Observation(bool ballAtFeet, TeamId selfTeam = TeamId.Home)
    {
        bool home = selfTeam == TeamId.Home;
        TeamId opponent = home ? TeamId.Away : TeamId.Home;
        return new()
        {
            SelfId = 8,
            SelfTeam = selfTeam,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = new System.Numerics.Vector3(0f, 0f, 0f),
            SelfVelocity = System.Numerics.Vector3.Zero,
            SelfFacingXZ = new System.Numerics.Vector2(home ? 1f : -1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = ballAtFeet,
            ControlQuality01 = ballAtFeet ? 1f : 0f,
            PerceivedBallPosition = ballAtFeet
                ? new System.Numerics.Vector3(0.4f, 0f, 0f)
                : new System.Numerics.Vector3(8f, 0f, 0f),
            PerceivedBallVelocity = System.Numerics.Vector3.Zero,
            PossessingTeam = ballAtFeet ? selfTeam : opponent,
            BallLastTouchedByPlayerId = 8,
            BallControlState = ballAtFeet ? BallControlState.Controlled : BallControlState.Loose,
            BallControllingPlayerId = ballAtFeet ? 8 : null,
            TacticalAnchorPosition = new System.Numerics.Vector3(0f, 0f, 0f),
            AttackingDirectionX = home ? 1f : -1f,
            MatchTimeSeconds = 0f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            VisiblePlayers = new List<PerceivedEntity>
            {
                new(9, selfTeam, TacticalRole.Striker,
                    new System.Numerics.Vector3(home ? 20f : -20f, 0f, 2f),
                    System.Numerics.Vector3.Zero, 0.9f),
                new(12, opponent, TacticalRole.CenterBack,
                    new System.Numerics.Vector3(home ? 30f : -30f, 0f, -4f),
                    System.Numerics.Vector3.Zero, 0.9f)
            },
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = new PlayerTendencies(),
            SelfAttributes = new PlayerAttributes()
        };
    }
}
