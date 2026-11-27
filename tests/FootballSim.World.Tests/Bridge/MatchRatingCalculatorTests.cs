using System;
using System.Collections.Generic;
using FootballSim;
using FootballSim.Core;
using FootballSim.World.Bridge;
using Xunit;

namespace FootballSim.World.Tests.Bridge;

/// <summary>
/// W5 match-rating contract: ratings are deterministic, stay inside the plan's 4.0..10.0 band rounded to one
/// decimal, rate a non-playing row as <see cref="MatchRatingCalculator.NotPlayedRating"/>, and move the way a
/// realistic rating has to (a winning striker above the same line in a heavy defeat, a keeper who concedes
/// one above one who concedes five, a striker with shots above a striker without). The last test rates the
/// rows of a real, short <see cref="MatchSimulationEngine"/> run so the observer overload is exercised
/// exactly as the bridge will use it at full time.
/// </summary>
public sealed class MatchRatingCalculatorTests
{
    private static readonly TacticalRole[] Roles =
    {
        TacticalRole.Goalkeeper,
        TacticalRole.CenterBack,
        TacticalRole.FullBack,
        TacticalRole.DefensiveMidfielder,
        TacticalRole.CentralMidfielder,
        TacticalRole.Winger,
        TacticalRole.Striker
    };

    [Fact]
    public void SyntheticSweep_StaysInsideTheContractRange()
    {
        (int TeamGoals, int OpponentGoals)[] results = { (2, 1), (0, 0), (0, 3) };

        foreach (TacticalRole role in Roles)
        {
            for (int goals = 0; goals <= 4; goals++)
            for (int assists = 0; assists <= 3; assists++)
            for (int saves = 0; saves <= 8; saves++)
            for (int tackles = 0; tackles <= 10; tackles++)
            for (int ratio = 0; ratio <= 1; ratio++)
            for (int onPitch = 0; onPitch <= 1; onPitch++)
            foreach ((int teamGoals, int opponentGoals) in results)
            {
                float minutes = onPitch * 90f;
                var input = new MatchRatingInput(
                    goals, assists, 3, 2, saves, tackles,
                    20, ratio * 20, minutes, role, true,
                    teamGoals, opponentGoals, ratio);

                float rating = MatchRatingCalculator.Rate(input);
                if (minutes <= 0f)
                {
                    Assert.Equal(MatchRatingCalculator.NotPlayedRating, rating);
                    continue;
                }

                Assert.True(float.IsFinite(rating));
                Assert.InRange(rating, MatchRatingCalculator.MinimumRating, MatchRatingCalculator.MaximumRating);
                Assert.Equal(MathF.Round(rating * 10f) / 10f, rating, 5);
            }
        }
    }

    [Theory]
    [InlineData(-5f)]
    [InlineData(0f)]
    [InlineData(float.NaN)]
    public void DidNotPlay_RatesZero(float minutesPlayed)
    {
        Assert.Equal(
            MatchRatingCalculator.NotPlayedRating,
            MatchRatingCalculator.Rate(Input(minutesPlayed: minutesPlayed)));
        Assert.Equal(
            MatchRatingCalculator.NotPlayedRating,
            MatchRatingCalculator.Rate(Input(role: TacticalRole.Goalkeeper, minutesPlayed: minutesPlayed)));
    }

    [Fact]
    public void TwoGoalStriker_RatesHigherInAWinThanTheSameLineInAHeavyLoss()
    {
        MatchRatingInput win = Input(
            goals: 2, shots: 4, shotsOnTarget: 3, tackles: 0, role: TacticalRole.Striker,
            teamGoals: 3, opponentGoals: 1);
        MatchRatingInput loss = win with { TeamGoals = 0, OpponentGoals = 3 };

        float winRating = MatchRatingCalculator.Rate(win);
        float lossRating = MatchRatingCalculator.Rate(loss);

        Assert.True(winRating > lossRating, $"win {winRating} should beat loss {lossRating}");
        Assert.True(winRating - lossRating >= 0.3f, $"result swing too small: {winRating - lossRating}");
    }

    [Fact]
    public void KeeperConcedingOne_RatesHigherThanKeeperConcedingFive()
    {
        // Both are defeats, so only the margin and the conceded penalty can separate them.
        MatchRatingInput oneConceded = Input(
            saves: 4, role: TacticalRole.Goalkeeper, teamGoals: 0, opponentGoals: 1);
        MatchRatingInput fiveConceded = oneConceded with { OpponentGoals = 5 };

        Assert.True(MatchRatingCalculator.Rate(oneConceded) > MatchRatingCalculator.Rate(fiveConceded));
    }

    [Fact]
    public void IdenticalInputs_RateIdentically()
    {
        MatchRatingInput input = Input(
            goals: 1, assists: 1, shots: 3, shotsOnTarget: 2, tackles: 4,
            passesAttempted: 40, passesCompleted: 32, role: TacticalRole.Winger,
            teamGoals: 2, opponentGoals: 0, teamPossessionShare01: 0.61f);

        float first = MatchRatingCalculator.Rate(input);
        float same = MatchRatingCalculator.Rate(input with { });

        Assert.Equal(first, same);
        Assert.Equal(BitConverter.SingleToInt32Bits(first), BitConverter.SingleToInt32Bits(same));
    }

    [Fact]
    public void RoleExpectation_MovesTheStrikerWithoutShotsBelowTheOneWithThree()
    {
        MatchRatingInput noShots = Input(
            goals: 0, shots: 0, shotsOnTarget: 0, tackles: 0, role: TacticalRole.Striker,
            teamGoals: 1, opponentGoals: 1);
        MatchRatingInput threeShots = noShots with { Shots = 3, ShotsOnTarget = 2 };

        Assert.True(MatchRatingCalculator.Rate(threeShots) > MatchRatingCalculator.Rate(noShots));
    }

    [Fact]
    public void PossessionShare_MovesTheRatingOnlySlightly()
    {
        float low = MatchRatingCalculator.Rate(Input(teamPossessionShare01: 0f));
        float high = MatchRatingCalculator.Rate(Input(teamPossessionShare01: 1f));

        Assert.True(high > low);
        // The term is ±0.1 before rounding, so the spread is ~0.2; allow the 0.1-step rounding slack.
        Assert.True(high - low <= 0.21f, $"possession swing too large: {high - low}");
    }

    [Fact]
    public void RealShortMatch_EveryPlayerWhoPlayedRatesInsideTheContractRange()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig { HalfDurationSeconds = 2f });
        int guard = 0;
        while (engine.Rules.Phase != MatchPhase.FullTime && guard++ < 10_000)
        {
            engine.StepTick();
        }

        Assert.Equal(MatchPhase.FullTime, engine.Rules.Phase);
        Assert.True(engine.PlayerStats.HasCapturedEnd);

        IReadOnlyList<MatchPlayerPerformance> rows = engine.PlayerStats.Players;
        Assert.Equal(engine.Config.PlayersPerTeam * 2, rows.Count);

        var homeRows = new List<MatchPlayerPerformance>(engine.Config.PlayersPerTeam);
        for (int index = 0; index < rows.Count; index++)
        {
            MatchPlayerPerformance row = rows[index];
            Assert.True(row.MinutesPlayed > 0f, $"{row} should have played");

            float rating = MatchRatingCalculator.Rate(
                row,
                row.Team == TeamId.Home,
                engine.Rules.HomeScore,
                engine.Rules.AwayScore,
                row.Role,
                teamPossessionShare01: 0.5f);

            Assert.True(float.IsFinite(rating));
            Assert.InRange(rating, MatchRatingCalculator.MinimumRating, MatchRatingCalculator.MaximumRating);
            Assert.Equal(MathF.Round(rating * 10f) / 10f, rating, 5);

            if (row.Team == TeamId.Home) homeRows.Add(row);
        }

        float teamRating = MatchRatingCalculator.RateTeam(
            homeRows, isHome: true, engine.Rules.HomeScore, engine.Rules.AwayScore, teamPossessionShare01: 0.5f);
        Assert.InRange(teamRating, MatchRatingCalculator.MinimumRating, MatchRatingCalculator.MaximumRating);
    }

    // =============================================================================================
    // Helpers.
    // =============================================================================================

    private static MatchRatingInput Input(
        int goals = 0,
        int assists = 0,
        int shots = 2,
        int shotsOnTarget = 1,
        int saves = 0,
        int tackles = 2,
        int passesAttempted = 30,
        int passesCompleted = 22,
        float minutesPlayed = 90f,
        TacticalRole role = TacticalRole.CentralMidfielder,
        bool isHome = true,
        int teamGoals = 1,
        int opponentGoals = 1,
        float teamPossessionShare01 = 0.5f) => new(
            goals,
            assists,
            shots,
            shotsOnTarget,
            saves,
            tackles,
            passesAttempted,
            passesCompleted,
            minutesPlayed,
            role,
            isHome,
            teamGoals,
            opponentGoals,
            teamPossessionShare01);
}
