using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Tests.Data;
using FootballSim.World.World;
using Xunit;

namespace FootballSim.World.Tests.World;

/// <summary>
/// W2 league-scheduling contract: the 18-club circle-method double round-robin (34 matchdays x 9 fixtures,
/// 17 home / 17 away per club, every pair once at each venue), the week mapping owned by
/// <see cref="WorldCalendar.LeagueMatchdayForWeek"/>, the cup-round weeks and seed determinism.
///
/// <para>The tests build a lightweight world skeleton (leagues and clubs only, no players or managers) and
/// call <see cref="ScheduleGenerator.GenerateLeagueSeason"/> directly, so scheduling is tested in isolation
/// from the rest of world generation.</para>
/// </summary>
public sealed class ScheduleGeneratorTests
{
    private const int ClubsPerLeague = 18;
    private const int Matchdays = 34;
    private const int FixturesPerMatchday = 9;
    private const int FixturesPerLeague = 306;

    private static readonly Lazy<WorldPack> UnitPack =
        new(() => WorldPackLoader.Parse(TestWorldPacks.ValidJson()));

    private static readonly Lazy<WorldState> Scheduled =
        new(() => CreateScheduledWorld(20261127UL));

    private static readonly Lazy<WorldState> ScheduledAgain =
        new(() => CreateScheduledWorld(20261127UL));

    private static readonly Lazy<WorldState> OtherSeed =
        new(() => CreateScheduledWorld(987654321UL));

    private static WorldPack Pack => UnitPack.Value;

    [Fact]
    public void EachLeague_HasThirtyFourMatchdaysOfNineFixtures()
    {
        Assert.Equal(6, Scheduled.Value.Leagues.Count);

        foreach (WorldLeague league in Scheduled.Value.Leagues)
        {
            Assert.Equal(FixturesPerLeague, league.Fixtures.Count);

            int[] perMatchday = new int[Matchdays + 1];
            foreach (WorldFixture fixture in league.Fixtures)
            {
                Assert.InRange(fixture.LeagueMatchday, 1, Matchdays);
                perMatchday[fixture.LeagueMatchday]++;
            }

            for (int matchday = 1; matchday <= Matchdays; matchday++)
            {
                Assert.Equal(FixturesPerMatchday, perMatchday[matchday]);
            }
        }
    }

    [Fact]
    public void EachClub_PlaysSeventeenHomeAndSeventeenAway()
    {
        foreach (WorldLeague league in Scheduled.Value.Leagues)
        {
            Assert.Equal(ClubsPerLeague, league.Clubs.Count);

            var home = new Dictionary<ClubId, int>();
            var away = new Dictionary<ClubId, int>();
            foreach (ClubId clubId in league.Clubs)
            {
                home[clubId] = 0;
                away[clubId] = 0;
            }

            foreach (WorldFixture fixture in league.Fixtures)
            {
                home[fixture.HomeClubId]++;
                away[fixture.AwayClubId]++;
            }

            foreach (ClubId clubId in league.Clubs)
            {
                Assert.Equal(17, home[clubId]);
                Assert.Equal(17, away[clubId]);
                Assert.Equal(Matchdays, home[clubId] + away[clubId]);
            }
        }
    }

    [Fact]
    public void EveryUnorderedPair_MeetsTwiceWithOneFixtureAtEachVenue()
    {
        foreach (WorldLeague league in Scheduled.Value.Leagues)
        {
            var pairMeetings = new Dictionary<(int Low, int High), int>();
            var venues = new HashSet<(int Home, int Away)>();

            foreach (WorldFixture fixture in league.Fixtures)
            {
                int homeId = fixture.HomeClubId.Value;
                int awayId = fixture.AwayClubId.Value;
                Assert.NotEqual(homeId, awayId);
                Assert.True(venues.Add((homeId, awayId)), $"venue {homeId}v{awayId} is used twice");

                (int Low, int High) key = homeId < awayId ? (homeId, awayId) : (awayId, homeId);
                pairMeetings[key] = pairMeetings.GetValueOrDefault(key) + 1;
            }

            Assert.Equal(ClubsPerLeague * (ClubsPerLeague - 1) / 2, pairMeetings.Count);
            Assert.All(pairMeetings.Values, meetings => Assert.Equal(2, meetings));
            Assert.Equal(FixturesPerLeague, venues.Count);
        }
    }

    [Fact]
    public void FixtureWeeks_FollowTheCalendarAuthority()
    {
        foreach (WorldLeague league in Scheduled.Value.Leagues)
        {
            foreach (WorldFixture fixture in league.Fixtures)
            {
                Assert.Equal(
                    fixture.LeagueMatchday,
                    WorldCalendar.LeagueMatchdayForWeek(fixture.Week));

                if (fixture.LeagueMatchday <= 17)
                {
                    Assert.InRange(fixture.Week, WorldCalendar.LeagueFirstHalfFirstWeek, WorldCalendar.LeagueFirstHalfLastWeek);
                }
                else
                {
                    Assert.InRange(fixture.Week, WorldCalendar.LeagueSecondHalfFirstWeek, WorldCalendar.LeagueSecondHalfLastWeek);
                }
            }
        }
    }

    [Fact]
    public void FirstHalfAndSecondHalf_AreMirrored()
    {
        foreach (WorldLeague league in Scheduled.Value.Leagues)
        {
            var secondHalf = new HashSet<(int Matchday, int Home, int Away)>();
            foreach (WorldFixture fixture in league.Fixtures)
            {
                if (fixture.LeagueMatchday > 17)
                {
                    secondHalf.Add((fixture.LeagueMatchday, fixture.HomeClubId.Value, fixture.AwayClubId.Value));
                }
            }

            foreach (WorldFixture fixture in league.Fixtures)
            {
                if (fixture.LeagueMatchday > 17) continue;

                Assert.Contains(
                    (fixture.LeagueMatchday + 17, fixture.AwayClubId.Value, fixture.HomeClubId.Value),
                    secondHalf);
            }
        }
    }

    [Fact]
    public void NoClub_PlaysTwiceOnOneMatchday()
    {
        foreach (WorldLeague league in Scheduled.Value.Leagues)
        {
            for (int matchday = 1; matchday <= Matchdays; matchday++)
            {
                var clubs = new HashSet<ClubId>();
                int fixtures = 0;

                foreach (WorldFixture fixture in league.FixturesForMatchday(matchday))
                {
                    fixtures++;
                    Assert.True(clubs.Add(fixture.HomeClubId), $"club {fixture.HomeClubId} appears twice on matchday {matchday}");
                    Assert.True(clubs.Add(fixture.AwayClubId), $"club {fixture.AwayClubId} appears twice on matchday {matchday}");
                }

                Assert.Equal(FixturesPerMatchday, fixtures);
                Assert.Equal(ClubsPerLeague, clubs.Count);
            }
        }
    }

    [Fact]
    public void Fixtures_CarryLeagueFieldsAndUniqueMatchIds()
    {
        var matchIds = new HashSet<int>();

        foreach (WorldLeague league in Scheduled.Value.Leagues)
        {
            foreach (WorldFixture fixture in league.Fixtures)
            {
                Assert.Equal(CompetitionKind.League, fixture.Competition);
                Assert.Equal(league.Id, fixture.LeagueId);
                Assert.Equal(CupRound.None, fixture.CupRound);
                Assert.Equal(Scheduled.Value.Season, fixture.Season);
                Assert.False(fixture.IsPlayed);
                Assert.True(fixture.Id.IsFixture);
                Assert.True(matchIds.Add(fixture.Id.Value), $"duplicate match id {fixture.Id}");
            }
        }

        Assert.Equal(6 * FixturesPerLeague, matchIds.Count);
    }

    [Fact]
    public void MatchdayToWeekAnchors_MatchTheStagePlan()
    {
        // Matchday 1 -> week 5, 17 -> 21, 18 -> 26, 34 -> 42 (plan §5.2 and WorldCalendar).
        var anchors = new Dictionary<int, int> { [1] = 5, [17] = 21, [18] = 26, [34] = 42 };

        foreach (WorldLeague league in Scheduled.Value.Leagues)
        {
            foreach (WorldFixture fixture in league.Fixtures)
            {
                int firstWeek = fixture.LeagueMatchday <= 17 ? fixture.LeagueMatchday + 4 : fixture.LeagueMatchday + 8;
                Assert.Equal(firstWeek, fixture.Week);

                if (anchors.TryGetValue(fixture.LeagueMatchday, out int expectedWeek))
                {
                    Assert.Equal(expectedWeek, fixture.Week);
                    Assert.Equal(fixture.LeagueMatchday, WorldCalendar.LeagueMatchdayForWeek(fixture.Week));
                }
            }
        }
    }

    [Fact]
    public void CupRoundWeeks_MatchTheStagePlan()
    {
        var expected = new Dictionary<CupRound, int>
        {
            [CupRound.Preliminary] = 5,
            [CupRound.SecondRound] = 10,
            [CupRound.RoundOf64] = 15,
            [CupRound.RoundOf32] = 20,
            [CupRound.RoundOf16] = 25,
            [CupRound.QuarterFinal] = 31,
            [CupRound.SemiFinal] = 37,
            [CupRound.Final] = 42
        };

        foreach (KeyValuePair<CupRound, int> pair in expected)
        {
            Assert.Equal(pair.Value, ScheduleGenerator.WeekForCupRound(pair.Key));
            Assert.Equal(pair.Value, CupScheduler.WeekForCupRound(pair.Key));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => ScheduleGenerator.WeekForCupRound(CupRound.None));
    }

    [Fact]
    public void SameSeed_ProducesIdenticalFixtureLists()
    {
        WorldState expected = Scheduled.Value;
        WorldState actual = ScheduledAgain.Value;

        Assert.Equal(expected.Leagues.Count, actual.Leagues.Count);
        for (int leagueIndex = 0; leagueIndex < expected.Leagues.Count; leagueIndex++)
        {
            WorldLeague left = expected.Leagues[leagueIndex];
            WorldLeague right = actual.Leagues[leagueIndex];
            Assert.Equal(left.Fixtures.Count, right.Fixtures.Count);

            for (int fixtureIndex = 0; fixtureIndex < left.Fixtures.Count; fixtureIndex++)
            {
                WorldFixture leftFixture = left.Fixtures[fixtureIndex];
                WorldFixture rightFixture = right.Fixtures[fixtureIndex];

                Assert.Equal(leftFixture.Id, rightFixture.Id);
                Assert.Equal(leftFixture.HomeClubId, rightFixture.HomeClubId);
                Assert.Equal(leftFixture.AwayClubId, rightFixture.AwayClubId);
                Assert.Equal(leftFixture.LeagueMatchday, rightFixture.LeagueMatchday);
                Assert.Equal(leftFixture.Week, rightFixture.Week);
            }
        }
    }

    [Fact]
    public void DifferentSeed_ChangesFixtureOrderButKeepsTheStructure()
    {
        WorldState first = Scheduled.Value;
        WorldState second = OtherSeed.Value;

        bool anyOrderDifference = false;
        for (int leagueIndex = 0; leagueIndex < first.Leagues.Count; leagueIndex++)
        {
            WorldLeague left = first.Leagues[leagueIndex];
            WorldLeague right = second.Leagues[leagueIndex];

            Assert.Equal(FixturesPerLeague, right.Fixtures.Count);
            for (int fixtureIndex = 0; fixtureIndex < left.Fixtures.Count; fixtureIndex++)
            {
                WorldFixture leftFixture = left.Fixtures[fixtureIndex];
                WorldFixture rightFixture = right.Fixtures[fixtureIndex];
                if (leftFixture.HomeClubId != rightFixture.HomeClubId ||
                    leftFixture.AwayClubId != rightFixture.AwayClubId)
                {
                    anyOrderDifference = true;
                }
            }

            // The structural guarantees are seed-independent.
            var home = new Dictionary<ClubId, int>();
            var away = new Dictionary<ClubId, int>();
            foreach (ClubId clubId in right.Clubs)
            {
                home[clubId] = 0;
                away[clubId] = 0;
            }

            foreach (WorldFixture fixture in right.Fixtures)
            {
                home[fixture.HomeClubId]++;
                away[fixture.AwayClubId]++;
            }

            Assert.All(right.Clubs, clubId => Assert.Equal(17, home[clubId]));
            Assert.All(right.Clubs, clubId => Assert.Equal(17, away[clubId]));
        }

        Assert.True(anyOrderDifference, "a different seed produced an identical fixture order");
    }

    [Fact]
    public void GenerateLeagueSeason_RejectsAnAlreadyScheduledLeague()
    {
        WorldState world = CreateScheduledWorld(11UL);
        WorldLeague league = world.Leagues[0];

        Assert.Throws<InvalidOperationException>(
            () => ScheduleGenerator.GenerateLeagueSeason(world, league, world.Stream(WorldRandomStream.Fixtures)));
    }

    [Fact]
    public void GenerateLeagueSeason_RejectsAWrongClubCount()
    {
        var state = new WorldState(Pack, 1UL, new WorldDate(1, 1));
        var league = new WorldLeague(new LeagueId(1), 1, "Short League", "Short");
        state.AddLeague(league);

        var definitions = Pack.ClubsInTier(1);
        for (int index = 0; index < ClubsPerLeague - 1; index++)
        {
            ClubDef definition = definitions[index];
            var club = new WorldClub(
                new ClubId(index + 1), definition, league.Id, ManagerId.None,
                new ClubFacilities(), new ClubFinances());
            state.Clubs.Add(club);
            league.AddClub(club.Id);
        }

        Assert.Throws<InvalidOperationException>(
            () => ScheduleGenerator.GenerateLeagueSeason(state, league, state.Stream(WorldRandomStream.Fixtures)));
    }

    // ---------------------------------------------------------------------------------------------
    // World skeleton builder.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds leagues and clubs (no players or managers) and schedules all six divisions, so the tests can
    /// exercise the scheduler without paying for world generation.
    /// </summary>
    private static WorldState CreateScheduledWorld(ulong seed)
    {
        var state = new WorldState(Pack, seed, new WorldDate(1, 1));

        for (int tier = 1; tier <= 6; tier++)
        {
            DivisionDef division = Pack.DivisionForTier(tier);
            state.AddLeague(new WorldLeague(
                new LeagueId(tier), tier, division.Name, division.ShortName ?? division.Name));
        }

        int clubIndex = 0;
        foreach (ClubDef definition in Pack.Clubs)
        {
            WorldLeague league = state.LeagueForTier(definition.Tier);
            var club = new WorldClub(
                new ClubId(++clubIndex),
                definition,
                league.Id,
                ManagerId.None,
                new ClubFacilities(),
                new ClubFinances());
            state.Clubs.Add(club);
            league.AddClub(club.Id);
        }

        WorldRng fixturesRng = state.Stream(WorldRandomStream.Fixtures);
        foreach (WorldLeague league in state.Leagues)
        {
            ScheduleGenerator.GenerateLeagueSeason(state, league, fixturesRng);
        }

        return state;
    }
}
