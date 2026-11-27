using System;
using Xunit;
using FootballSim.World.Core;

namespace FootballSim.World.Tests;

/// <summary>W0 core primitives: the fixed 52-week season calendar.</summary>
public sealed class WorldCalendarTests
{
    [Fact]
    public void Constants_MatchTheDocumentedSeasonPlan()
    {
        Assert.Equal(52, WorldCalendar.WeeksPerSeason);
        Assert.Equal(34, WorldCalendar.LeagueMatchdaysPerSeason);

        Assert.Equal(4, WorldCalendar.PreSeasonLastWeek - WorldCalendar.PreSeasonFirstWeek + 1);
        Assert.Equal(17, WorldCalendar.LeagueFirstHalfLastWeek - WorldCalendar.LeagueFirstHalfFirstWeek + 1);
        Assert.Equal(4, WorldCalendar.WinterBreakLastWeek - WorldCalendar.WinterBreakFirstWeek + 1);
        Assert.Equal(17, WorldCalendar.LeagueSecondHalfLastWeek - WorldCalendar.LeagueSecondHalfFirstWeek + 1);
        Assert.Equal(3, WorldCalendar.PlayoffLastWeek - WorldCalendar.PlayoffFirstWeek + 1);
        Assert.Equal(7, WorldCalendar.OffSeasonLastWeek - WorldCalendar.OffSeasonFirstWeek + 1);

        Assert.Equal(WorldCalendar.PreSeasonFirstWeek, 1);
        Assert.Equal(WorldCalendar.LeagueFirstHalfFirstWeek, WorldCalendar.PreSeasonLastWeek + 1);
        Assert.Equal(WorldCalendar.WinterBreakFirstWeek, WorldCalendar.LeagueFirstHalfLastWeek + 1);
        Assert.Equal(WorldCalendar.LeagueSecondHalfFirstWeek, WorldCalendar.WinterBreakLastWeek + 1);
        Assert.Equal(WorldCalendar.PlayoffFirstWeek, WorldCalendar.LeagueSecondHalfLastWeek + 1);
        Assert.Equal(WorldCalendar.OffSeasonFirstWeek, WorldCalendar.PlayoffLastWeek + 1);
        Assert.Equal(WorldCalendar.WeeksPerSeason, WorldCalendar.OffSeasonLastWeek);
    }

    [Theory]
    [InlineData(1, SeasonPhase.PreSeason)]
    [InlineData(4, SeasonPhase.PreSeason)]
    [InlineData(5, SeasonPhase.FirstHalfLeague)]
    [InlineData(21, SeasonPhase.FirstHalfLeague)]
    [InlineData(22, SeasonPhase.WinterBreak)]
    [InlineData(25, SeasonPhase.WinterBreak)]
    [InlineData(26, SeasonPhase.SecondHalfLeague)]
    [InlineData(42, SeasonPhase.SecondHalfLeague)]
    [InlineData(43, SeasonPhase.Playoffs)]
    [InlineData(45, SeasonPhase.Playoffs)]
    [InlineData(46, SeasonPhase.OffSeason)]
    [InlineData(52, SeasonPhase.OffSeason)]
    public void PhaseForWeek_BoundariesMatchTheSeasonPlan(int week, SeasonPhase expected)
    {
        Assert.Equal(expected, WorldCalendar.PhaseForWeek(week));
    }

    [Fact]
    public void PhaseForWeek_AssignsExactlyOnePhasePerWeek()
    {
        int[] phaseCounts = new int[6];
        for (int week = 1; week <= WorldCalendar.WeeksPerSeason; week++)
        {
            SeasonPhase phase = WorldCalendar.PhaseForWeek(week);
            Assert.True(Enum.IsDefined(typeof(SeasonPhase), phase), $"undefined phase {phase} in week {week}");
            phaseCounts[(int)phase]++;
        }

        Assert.Equal(4, phaseCounts[(int)SeasonPhase.PreSeason]);
        Assert.Equal(17, phaseCounts[(int)SeasonPhase.FirstHalfLeague]);
        Assert.Equal(4, phaseCounts[(int)SeasonPhase.WinterBreak]);
        Assert.Equal(17, phaseCounts[(int)SeasonPhase.SecondHalfLeague]);
        Assert.Equal(3, phaseCounts[(int)SeasonPhase.Playoffs]);
        Assert.Equal(7, phaseCounts[(int)SeasonPhase.OffSeason]);

        int total = 0;
        for (int i = 0; i < phaseCounts.Length; i++)
            total += phaseCounts[i];
        Assert.Equal(WorldCalendar.WeeksPerSeason, total);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(53)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void PhaseForWeek_ThrowsOutsideTheSeason(int week)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldCalendar.PhaseForWeek(week));
    }

    [Fact]
    public void LeagueMatchdays_AreNumberedOneToThirtyFour_ExactlyOnce()
    {
        int[] seen = new int[WorldCalendar.LeagueMatchdaysPerSeason + 1];
        int leagueWeeks = 0;

        for (int week = 1; week <= WorldCalendar.WeeksPerSeason; week++)
        {
            int matchday = WorldCalendar.LeagueMatchdayForWeek(week);
            if (WorldCalendar.IsLeagueMatchWeek(week))
            {
                Assert.InRange(matchday, 1, WorldCalendar.LeagueMatchdaysPerSeason);
                seen[matchday]++;
                leagueWeeks++;
            }
            else
            {
                Assert.Equal(0, matchday);
            }
        }

        Assert.Equal(WorldCalendar.LeagueMatchdaysPerSeason, leagueWeeks);
        for (int matchday = 1; matchday <= WorldCalendar.LeagueMatchdaysPerSeason; matchday++)
            Assert.Equal(1, seen[matchday]);
    }

    [Theory]
    [InlineData(5, 1)]
    [InlineData(21, 17)]
    [InlineData(26, 18)]
    [InlineData(42, 34)]
    public void LeagueMatchdayForWeek_MapsBoundaryWeeks(int week, int expectedMatchday)
    {
        Assert.Equal(expectedMatchday, WorldCalendar.LeagueMatchdayForWeek(week));
    }

    [Fact]
    public void TransferWindow_IsOpenOnlyDuringPreSeasonAndWinterBreak()
    {
        for (int week = 1; week <= WorldCalendar.WeeksPerSeason; week++)
        {
            bool expected = week <= WorldCalendar.PreSeasonLastWeek ||
                            (week >= WorldCalendar.WinterBreakFirstWeek && week <= WorldCalendar.WinterBreakLastWeek);
            Assert.Equal(expected, WorldCalendar.IsTransferWindowOpen(week));
        }

        Assert.True(WorldCalendar.IsTransferWindowOpen(2));
        Assert.True(WorldCalendar.IsTransferWindowOpen(23));
        Assert.False(WorldCalendar.IsTransferWindowOpen(5));
        Assert.False(WorldCalendar.IsTransferWindowOpen(21));
        Assert.False(WorldCalendar.IsTransferWindowOpen(26));
        Assert.False(WorldCalendar.IsTransferWindowOpen(52));
    }

    [Fact]
    public void WorldDate_ValidatesSeasonAndWeek()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldDate(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldDate(-1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldDate(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldDate(1, 53));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldDate(1, -1));

        Assert.True(new WorldDate(1, 1).IsValid);
        Assert.True(new WorldDate(1, 52).IsValid);
        Assert.True(new WorldDate(42, 27).IsValid);
    }

    [Fact]
    public void DefaultWorldDate_IsNotValid()
    {
        WorldDate unset = default;

        Assert.Equal(0, unset.Season);
        Assert.Equal(0, unset.Week);
        Assert.False(unset.IsValid);
    }

    [Fact]
    public void AbsoluteWeek_IsContiguousAcrossSeasons()
    {
        Assert.Equal(0, WorldDate.Start.AbsoluteWeek);
        Assert.Equal(51, new WorldDate(1, 52).AbsoluteWeek);
        Assert.Equal(52, new WorldDate(2, 1).AbsoluteWeek);
        Assert.Equal((7 - 1) * 52 + (33 - 1), new WorldDate(7, 33).AbsoluteWeek);
    }

    [Fact]
    public void Ordering_UsesSeasonThenWeek()
    {
        Assert.True(new WorldDate(1, 52).CompareTo(new WorldDate(2, 1)) < 0);
        Assert.True(new WorldDate(2, 1).CompareTo(new WorldDate(1, 52)) > 0);
        Assert.True(new WorldDate(1, 5).CompareTo(new WorldDate(1, 6)) < 0);
        Assert.Equal(0, new WorldDate(4, 8).CompareTo(new WorldDate(4, 8)));

        Assert.Equal(new WorldDate(4, 8), new WorldDate(4, 8));
        Assert.NotEqual(new WorldDate(4, 8), new WorldDate(4, 9));
        Assert.Equal(new WorldDate(4, 8).GetHashCode(), new WorldDate(4, 8).GetHashCode());

        WorldDate[] dates = { new WorldDate(3, 1), new WorldDate(1, 52), new WorldDate(2, 1) };
        Array.Sort(dates);
        Assert.Equal(new WorldDate(1, 52), dates[0]);
        Assert.Equal(new WorldDate(2, 1), dates[1]);
        Assert.Equal(new WorldDate(3, 1), dates[2]);
    }

    [Fact]
    public void NextWeek_RollsWeek52IntoTheNextSeason()
    {
        Assert.Equal(new WorldDate(1, 2), WorldCalendar.NextWeek(WorldDate.Start));
        Assert.Equal(new WorldDate(1, 52), WorldCalendar.NextWeek(new WorldDate(1, 51)));
        Assert.Equal(new WorldDate(2, 1), WorldCalendar.NextWeek(new WorldDate(1, 52)));
        Assert.Equal(new WorldDate(9, 1), WorldCalendar.NextWeek(new WorldDate(8, 52)));
    }

    [Fact]
    public void AddWeeks_HandlesRolloverNegativeAndMultiSeasonJumps()
    {
        Assert.Equal(new WorldDate(2, 3), WorldCalendar.AddWeeks(new WorldDate(2, 3), 0));
        Assert.Equal(new WorldDate(1, 52), WorldCalendar.AddWeeks(WorldDate.Start, 51));
        Assert.Equal(new WorldDate(2, 1), WorldCalendar.AddWeeks(WorldDate.Start, 52));
        Assert.Equal(new WorldDate(4, 10), WorldCalendar.AddWeeks(new WorldDate(2, 10), 104));
        Assert.Equal(new WorldDate(1, 1), WorldCalendar.AddWeeks(new WorldDate(2, 10), -61));
        Assert.Equal(new WorldDate(1, 5), WorldCalendar.AddWeeks(new WorldDate(3, 5), -104));
        Assert.Equal(new WorldDate(3, 45), WorldCalendar.AddWeeks(new WorldDate(3, 50), -5));
    }

    [Fact]
    public void AddWeeks_ThrowsWhenMovingBeforeTheFirstSeason()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldCalendar.AddWeeks(WorldDate.Start, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldCalendar.AddWeeks(new WorldDate(2, 1), -53));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WorldCalendar.AddWeeks(new WorldDate(10, 30), -(9 * 52 + 30)));
    }

    [Fact]
    public void WeeksUntilSeasonEnd_CountsDownToZero()
    {
        Assert.Equal(51, WorldCalendar.WeeksUntilSeasonEnd(WorldDate.Start));
        Assert.Equal(1, WorldCalendar.WeeksUntilSeasonEnd(new WorldDate(5, 51)));
        Assert.Equal(0, WorldCalendar.WeeksUntilSeasonEnd(new WorldDate(5, 52)));

        for (int week = 1; week <= WorldCalendar.WeeksPerSeason; week++)
            Assert.Equal(52 - week, WorldCalendar.WeeksUntilSeasonEnd(new WorldDate(3, week)));
    }

    [Fact]
    public void WeeksBetween_IsSigned()
    {
        Assert.Equal(52, WorldCalendar.WeeksBetween(WorldDate.Start, new WorldDate(2, 1)));
        Assert.Equal(-52, WorldCalendar.WeeksBetween(new WorldDate(2, 1), WorldDate.Start));
        Assert.Equal(1, WorldCalendar.WeeksBetween(new WorldDate(1, 52), new WorldDate(2, 1)));
        Assert.Equal(-1, WorldCalendar.WeeksBetween(new WorldDate(2, 1), new WorldDate(1, 52)));
        Assert.Equal(51, WorldCalendar.WeeksBetween(WorldDate.Start, new WorldDate(1, 52)));
        Assert.Equal(0, WorldCalendar.WeeksBetween(new WorldDate(3, 7), new WorldDate(3, 7)));
    }

    [Fact]
    public void ToString_UsesSeasonAndTwoDigitWeek()
    {
        Assert.Equal("S1 W01", WorldDate.Start.ToString());
        Assert.Equal("S1 W07", new WorldDate(1, 7).ToString());
        Assert.Equal("S12 W34", new WorldDate(12, 34).ToString());
        Assert.Equal("S2 W52", new WorldDate(2, 52).ToString());
    }
}
