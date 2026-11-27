using System;
using System.Collections.Generic;
using Xunit;
using FootballSim.World.Core;

namespace FootballSim.World.Tests;

/// <summary>W0 core primitives: the five one-based world identity structs.</summary>
public sealed class WorldIdsTests
{
    [Fact]
    public void None_IsTheDefaultValue_AndIsNeverValid()
    {
        Assert.Equal(default(PlayerId), PlayerId.None);
        Assert.Equal(default(ClubId), ClubId.None);
        Assert.Equal(default(ManagerId), ManagerId.None);
        Assert.Equal(default(LeagueId), LeagueId.None);
        Assert.Equal(default(MatchId), MatchId.None);

        Assert.False(PlayerId.None.IsValid);
        Assert.False(ClubId.None.IsValid);
        Assert.False(ManagerId.None.IsValid);
        Assert.False(LeagueId.None.IsValid);
        Assert.False(MatchId.None.IsValid);
    }

    [Fact]
    public void IsValid_AcceptsPositiveValuesOnly()
    {
        Assert.True(new PlayerId(1).IsValid);
        Assert.True(new PlayerId(int.MaxValue).IsValid);
        Assert.False(new PlayerId(0).IsValid);
        Assert.False(new PlayerId(-1).IsValid);
        Assert.False(new PlayerId(int.MinValue).IsValid);

        Assert.True(new ClubId(1).IsValid);
        Assert.False(new ClubId(0).IsValid);
        Assert.False(new ClubId(-1).IsValid);

        Assert.True(new ManagerId(1).IsValid);
        Assert.False(new ManagerId(0).IsValid);
        Assert.False(new ManagerId(-1).IsValid);

        Assert.True(new LeagueId(1).IsValid);
        Assert.False(new LeagueId(0).IsValid);
        Assert.False(new LeagueId(-1).IsValid);

        Assert.True(new MatchId(1).IsValid);
        Assert.False(new MatchId(0).IsValid);
        Assert.False(new MatchId(-1).IsValid);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2026, true)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    public void MatchId_IsFixture_IsAnAliasOfIsValid(int value, bool expected)
    {
        MatchId id = new(value);

        Assert.Equal(expected, id.IsValid);
        Assert.Equal(expected, id.IsFixture);
    }

    [Fact]
    public void ToString_FormatsWithTheTypePrefix()
    {
        Assert.Equal("P7", new PlayerId(7).ToString());
        Assert.Equal("C42", new ClubId(42).ToString());
        Assert.Equal("M3", new ManagerId(3).ToString());
        Assert.Equal("L2", new LeagueId(2).ToString());
        Assert.Equal("X2026", new MatchId(2026).ToString());
        Assert.Equal("P0", PlayerId.None.ToString());
    }

    [Fact]
    public void CompareTo_OrdersByIdValue()
    {
        Assert.True(new PlayerId(3).CompareTo(new PlayerId(9)) < 0);
        Assert.True(new PlayerId(9).CompareTo(new PlayerId(3)) > 0);
        Assert.Equal(0, new PlayerId(9).CompareTo(new PlayerId(9)));

        Assert.True(new ClubId(3).CompareTo(new ClubId(9)) < 0);
        Assert.Equal(0, new ClubId(9).CompareTo(new ClubId(9)));

        Assert.True(new ManagerId(3).CompareTo(new ManagerId(9)) < 0);
        Assert.Equal(0, new ManagerId(9).CompareTo(new ManagerId(9)));

        Assert.True(new LeagueId(3).CompareTo(new LeagueId(9)) < 0);
        Assert.Equal(0, new LeagueId(9).CompareTo(new LeagueId(9)));

        Assert.True(new MatchId(3).CompareTo(new MatchId(9)) < 0);
        Assert.Equal(0, new MatchId(9).CompareTo(new MatchId(9)));
    }

    [Fact]
    public void Sorting_ByCompareTo_UsesIdOrder()
    {
        LeagueId[] ids = { new LeagueId(3), new LeagueId(1), new LeagueId(2) };
        Array.Sort(ids);

        Assert.Equal(new LeagueId(1), ids[0]);
        Assert.Equal(new LeagueId(2), ids[1]);
        Assert.Equal(new LeagueId(3), ids[2]);
    }

    [Fact]
    public void Records_UseValueEquality_AndUsableHashing()
    {
        Assert.Equal(new PlayerId(5), new PlayerId(5));
        Assert.NotEqual(new PlayerId(5), new PlayerId(6));
        Assert.Equal(new PlayerId(5).GetHashCode(), new PlayerId(5).GetHashCode());

        Assert.Equal(new ClubId(5), new ClubId(5));
        Assert.NotEqual(new ClubId(5), new ClubId(6));
        Assert.Equal(new ClubId(5).GetHashCode(), new ClubId(5).GetHashCode());

        Assert.Equal(new ManagerId(5), new ManagerId(5));
        Assert.NotEqual(new ManagerId(5), new ManagerId(6));
        Assert.Equal(new ManagerId(5).GetHashCode(), new ManagerId(5).GetHashCode());

        Assert.Equal(new LeagueId(5), new LeagueId(5));
        Assert.NotEqual(new LeagueId(5), new LeagueId(6));
        Assert.Equal(new LeagueId(5).GetHashCode(), new LeagueId(5).GetHashCode());

        Assert.Equal(new MatchId(5), new MatchId(5));
        Assert.NotEqual(new MatchId(5), new MatchId(6));
        Assert.Equal(new MatchId(5).GetHashCode(), new MatchId(5).GetHashCode());

        HashSet<ClubId> clubs = new HashSet<ClubId> { new ClubId(1), new ClubId(1), new ClubId(2) };
        Assert.Contains(new ClubId(1), clubs);
        Assert.Contains(new ClubId(2), clubs);
        Assert.True(clubs.Count == 2, $"expected two distinct club ids, got {clubs.Count}");
    }
}
