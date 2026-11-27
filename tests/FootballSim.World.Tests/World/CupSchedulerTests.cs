using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Tests.Data;
using FootballSim.World.World;
using Xunit;

namespace FootballSim.World.Tests.World;

/// <summary>
/// W2 domestic-cup contract: the tiered-entry bracket of plan §5.1 (preliminary tiers 5-6, second round
/// joins tiers 3-4 with two byes, round of 64 joins tiers 1-2), all eight rounds, the champion, the
/// home-venue draws and seed determinism.
///
/// <para>The tests build a lightweight world skeleton (leagues and clubs only) and drive
/// <see cref="CupScheduler"/> directly. Every tie is resolved deterministically (the lower club id wins),
/// so the bracket path is a pure function of the seed and the results.</para>
/// </summary>
public sealed class CupSchedulerTests
{
    private const ulong DefaultSeed = 20261127UL;

    private static readonly Lazy<WorldPack> UnitPack =
        new(() => WorldPackLoader.Parse(TestWorldPacks.ValidJson()));

    private static WorldPack Pack => UnitPack.Value;

    [Fact]
    public void Constants_MatchTheV1World()
    {
        Assert.Equal(108, CupScheduler.RequiredTotalClubs);

        WorldState world = CreateCupWorld(DefaultSeed);
        Assert.Equal(CupScheduler.RequiredTotalClubs, world.Clubs.Count);
        Assert.Equal(18, world.Clubs.InTier(5).Count);
        Assert.Equal(18, world.Clubs.InTier(6).Count);
    }

    [Fact]
    public void WeekForCupRound_MapsTheStagePlan()
    {
        Assert.Equal(5, CupScheduler.WeekForCupRound(CupRound.Preliminary));
        Assert.Equal(10, CupScheduler.WeekForCupRound(CupRound.SecondRound));
        Assert.Equal(15, CupScheduler.WeekForCupRound(CupRound.RoundOf64));
        Assert.Equal(20, CupScheduler.WeekForCupRound(CupRound.RoundOf32));
        Assert.Equal(25, CupScheduler.WeekForCupRound(CupRound.RoundOf16));
        Assert.Equal(31, CupScheduler.WeekForCupRound(CupRound.QuarterFinal));
        Assert.Equal(37, CupScheduler.WeekForCupRound(CupRound.SemiFinal));
        Assert.Equal(42, CupScheduler.WeekForCupRound(CupRound.Final));

        Assert.Throws<ArgumentOutOfRangeException>(() => CupScheduler.WeekForCupRound(CupRound.None));
    }

    [Fact]
    public void StartSeason_CreatesThePreliminaryRound()
    {
        WorldState world = CreateCupWorld(DefaultSeed);
        StartCup(world);

        Assert.Single(world.Cup.Rounds);
        Assert.False(world.Cup.IsComplete);
        Assert.False(world.Cup.ChampionClubId.IsValid);

        CupRoundBracket round = world.Cup.RoundOf(CupRound.Preliminary)!;
        Assert.Equal(CupRound.Preliminary, round.Round);
        Assert.Equal(5, round.Week);
        Assert.Equal(18, round.Ties.Count);
        Assert.Empty(round.Byes);

        var participants = new List<ClubId>();
        var matchIds = new HashSet<int>();
        foreach (CupTie tie in round.Ties)
        {
            Assert.True(tie.Fixture.IsCupTie);
            Assert.Equal(CompetitionKind.Cup, tie.Fixture.Competition);
            Assert.Equal(LeagueId.None, tie.Fixture.LeagueId);
            Assert.Equal(CupRound.Preliminary, tie.Fixture.CupRound);
            Assert.Equal(0, tie.Fixture.LeagueMatchday);
            Assert.Equal(5, tie.Fixture.Week);
            Assert.Equal(world.Season, tie.Fixture.Season);
            Assert.False(tie.Fixture.IsPlayed);
            Assert.NotEqual(tie.HomeClubId, tie.AwayClubId);
            Assert.True(matchIds.Add(tie.Fixture.Id.Value), $"duplicate match id {tie.Fixture.Id}");
            Assert.InRange(world.Clubs[tie.HomeClubId].Tier, 5, 6);
            Assert.InRange(world.Clubs[tie.AwayClubId].Tier, 5, 6);

            participants.Add(tie.HomeClubId);
            participants.Add(tie.AwayClubId);
        }

        Assert.Equal(36, participants.Count);
        Assert.Equal(36, participants.Distinct().Count());
        Assert.Equal(18, participants.Count(clubId => world.Clubs[clubId].Tier == 5));
        Assert.Equal(18, participants.Count(clubId => world.Clubs[clubId].Tier == 6));
    }

    [Fact]
    public void StartSeason_RejectsASecondStart()
    {
        WorldState world = CreateCupWorld(DefaultSeed);
        WorldRng rng = StartCup(world);

        Assert.Throws<InvalidOperationException>(() => CupScheduler.StartSeason(world, rng));
    }

    [Fact]
    public void StartSeason_RejectsAWrongBottomTierEntrantCount()
    {
        WorldState world = CreateCupWorld(DefaultSeed, includedTiers: new[] { 5 });

        Assert.Throws<InvalidOperationException>(
            () => CupScheduler.StartSeason(world, world.Stream(WorldRandomStream.Cup).Fork(0UL)));
    }

    [Fact]
    public void AdvanceIfReady_ReturnsFalseBeforeTheSeasonStarts()
    {
        WorldState world = CreateCupWorld(DefaultSeed);
        WorldRng rng = world.Stream(WorldRandomStream.Cup).Fork(0UL);

        Assert.False(CupScheduler.AdvanceIfReady(world, rng));
        Assert.Empty(world.Cup.Rounds);
    }

    [Fact]
    public void AdvanceIfReady_ReturnsFalseWhileTheRoundIsUnresolved()
    {
        WorldState world = CreateCupWorld(DefaultSeed);
        WorldRng rng = StartCup(world);
        CupRoundBracket preliminary = world.Cup.RoundOf(CupRound.Preliminary)!;

        Assert.False(CupScheduler.AdvanceIfReady(world, rng));
        Assert.Single(world.Cup.Rounds);

        // Resolve all but one tie: the round must still be open.
        for (int index = 0; index < preliminary.Ties.Count - 1; index++)
        {
            RecordLowerClubIdWinner(preliminary.Ties[index].Fixture);
        }

        Assert.False(preliminary.IsResolved);
        Assert.False(CupScheduler.AdvanceIfReady(world, rng));
        Assert.Single(world.Cup.Rounds);

        RecordLowerClubIdWinner(preliminary.Ties[^1].Fixture);
        Assert.True(preliminary.IsResolved);
        Assert.True(CupScheduler.AdvanceIfReady(world, rng));
        Assert.Equal(2, world.Cup.Rounds.Count);
        Assert.False(world.Cup.IsComplete);
    }

    [Fact]
    public void SecondRound_AddsTiersThreeAndFourAndGivesTwoByes()
    {
        WorldState world = CreateCupWorld(DefaultSeed);
        WorldRng rng = StartCup(world);
        CupRoundBracket preliminary = world.Cup.RoundOf(CupRound.Preliminary)!;

        ResolveRound(preliminary);
        IReadOnlyList<ClubId> preliminaryWinners = preliminary.QualifiedClubs();
        Assert.Equal(18, preliminaryWinners.Count);

        Assert.True(CupScheduler.AdvanceIfReady(world, rng));

        CupRoundBracket second = world.Cup.RoundOf(CupRound.SecondRound)!;
        Assert.Equal(CupRound.SecondRound, second.Round);
        Assert.Equal(10, second.Week);
        Assert.Equal(26, second.Ties.Count);
        Assert.Equal(2, second.Byes.Count);

        var participants = Participants(second);
        Assert.Equal(54, participants.Count);
        Assert.Equal(54, participants.Distinct().Count());

        var expected = new HashSet<ClubId>(preliminaryWinners);
        foreach (WorldClub club in world.Clubs.All)
        {
            if (club.Tier is 3 or 4) expected.Add(club.Id);
        }

        AssertSameClubSet(expected, participants);

        // Tiers 1-2 have not entered yet, and every tie is still unplayed.
        Assert.All(expected, clubId => Assert.InRange(world.Clubs[clubId].Tier, 3, 6));
        Assert.DoesNotContain(expected, clubId => world.Clubs[clubId].Tier <= 2);
        Assert.All(second.Ties, tie => Assert.False(tie.Fixture.IsPlayed));
        Assert.All(second.Byes, clubId => Assert.Contains(clubId, expected));
    }

    [Fact]
    public void RoundOf64_AddsTiersOneAndTwo()
    {
        WorldState world = CreateCupWorld(DefaultSeed);
        WorldRng rng = StartCup(world);

        ResolveRound(world.Cup.RoundOf(CupRound.Preliminary)!);
        Assert.True(CupScheduler.AdvanceIfReady(world, rng));
        ResolveRound(world.Cup.RoundOf(CupRound.SecondRound)!);
        Assert.True(CupScheduler.AdvanceIfReady(world, rng));

        CupRoundBracket roundOf64 = world.Cup.RoundOf(CupRound.RoundOf64)!;
        Assert.Equal(15, roundOf64.Week);
        Assert.Equal(32, roundOf64.Ties.Count);
        Assert.Empty(roundOf64.Byes);

        var participants = Participants(roundOf64);
        Assert.Equal(64, participants.Count);
        Assert.Equal(64, participants.Distinct().Count());

        // Every top-two-tier club enters here; no club can appear twice.
        foreach (WorldClub club in world.Clubs.All)
        {
            if (club.Tier is 1 or 2) Assert.Contains(club.Id, participants);
        }

        // Nothing above tier 2 appeared before this round.
        foreach (CupRound earlier in new[] { CupRound.Preliminary, CupRound.SecondRound })
        {
            CupRoundBracket bracket = world.Cup.RoundOf(earlier)!;
            foreach (ClubId clubId in Participants(bracket))
            {
                Assert.True(world.Clubs[clubId].Tier >= 3, $"tier {world.Clubs[clubId].Tier} club entered in {earlier}");
            }
        }
    }

    [Fact]
    public void Tournament_PlaysAllEightRoundsAndCrownsAChampion()
    {
        WorldState world = CreateCupWorld(777UL);
        WorldRng rng = StartCup(world);

        (CupRound Round, int Week, int Ties, int Entrants)[] expected =
        {
            (CupRound.Preliminary, 5, 18, 36),
            (CupRound.SecondRound, 10, 26, 54),
            (CupRound.RoundOf64, 15, 32, 64),
            (CupRound.RoundOf32, 20, 16, 32),
            (CupRound.RoundOf16, 25, 8, 16),
            (CupRound.QuarterFinal, 31, 4, 8),
            (CupRound.SemiFinal, 37, 2, 4),
            (CupRound.Final, 42, 1, 2)
        };

        for (int index = 0; index < expected.Length; index++)
        {
            (CupRound roundType, int week, int ties, int entrants) = expected[index];

            CupRoundBracket round = world.Cup.RoundOf(roundType)
                ?? throw new InvalidOperationException($"Round {roundType} was not created.");
            Assert.Equal(week, round.Week);
            Assert.Equal(ties, round.Ties.Count);
            Assert.Equal(roundType == CupRound.SecondRound ? 2 : 0, round.Byes.Count);
            Assert.Equal(entrants, Participants(round).Count);
            Assert.Equal(entrants, Participants(round).Distinct().Count());
            Assert.False(round.IsResolved);

            ResolveRound(round);
            Assert.True(round.IsResolved);
            Assert.Equal(ties + round.Byes.Count, round.QualifiedClubs().Count);

            Assert.True(CupScheduler.AdvanceIfReady(world, rng));
            // Every round except the final creates the next one; the final only completes the cup.
            Assert.Equal(Math.Min(index + 2, expected.Length), world.Cup.Rounds.Count);

            if (roundType == CupRound.Final)
            {
                Assert.True(world.Cup.IsComplete);
            }
            else
            {
                Assert.False(world.Cup.IsComplete);
            }
        }

        Assert.Equal(8, world.Cup.Rounds.Count);
        Assert.True(world.Cup.IsComplete);
        Assert.True(world.Cup.ChampionClubId.IsValid);

        CupRoundBracket final = world.Cup.RoundOf(CupRound.Final)!;
        Assert.True(final.Ties[0].Fixture.IsPlayed);
        Assert.Equal(final.Ties[0].WinnerClubId, world.Cup.ChampionClubId);
        Assert.Equal(world.Clubs[final.Ties[0].WinnerClubId].Id, world.Cup.ChampionClubId);

        // Completed cups never change again.
        Assert.False(CupScheduler.AdvanceIfReady(world, rng));
        Assert.Equal(8, world.Cup.Rounds.Count);
    }

    [Fact]
    public void AdvanceIfReady_IsSafeToCallEveryWeek()
    {
        WorldState world = CreateCupWorld(DefaultSeed);
        WorldRng rng = StartCup(world);

        // Before the first round resolves, repeated weekly calls do nothing at all.
        for (int week = 5; week <= 9; week++)
        {
            Assert.False(CupScheduler.AdvanceIfReady(world, rng));
        }

        Assert.Single(world.Cup.Rounds);

        CupRoundBracket preliminary = world.Cup.RoundOf(CupRound.Preliminary)!;
        ResolveRound(preliminary);

        // A resolved round advances exactly once, then the new round blocks further calls.
        Assert.True(CupScheduler.AdvanceIfReady(world, rng));
        Assert.Equal(2, world.Cup.Rounds.Count);
        Assert.False(CupScheduler.AdvanceIfReady(world, rng));
        Assert.False(CupScheduler.AdvanceIfReady(world, rng));
        Assert.Equal(2, world.Cup.Rounds.Count);
    }

    [Fact]
    public void Bracket_IsDeterministicForTheSameSeed()
    {
        WorldState first = CreateCupWorld(DefaultSeed);
        WorldState second = CreateCupWorld(DefaultSeed);

        RunThreeRounds(first);
        RunThreeRounds(second);

        AssertSameBracket(first, second);
    }

    [Fact]
    public void Bracket_DiffersAcrossSeeds()
    {
        WorldState first = CreateCupWorld(DefaultSeed);
        WorldState second = CreateCupWorld(DefaultSeed + 1UL);

        StartCup(first);
        StartCup(second);

        CupRoundBracket left = first.Cup.RoundOf(CupRound.Preliminary)!;
        CupRoundBracket right = second.Cup.RoundOf(CupRound.Preliminary)!;

        bool differs = false;
        for (int index = 0; index < left.Ties.Count && !differs; index++)
        {
            differs = left.Ties[index].HomeClubId != right.Ties[index].HomeClubId ||
                left.Ties[index].AwayClubId != right.Ties[index].AwayClubId;
        }

        Assert.True(differs, "two different seeds produced the same preliminary draw");
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Starts the cup with the season's cup stream and returns that stream for later draws.</summary>
    private static WorldRng StartCup(WorldState world)
    {
        WorldRng rng = world.Stream(WorldRandomStream.Cup).Fork(0UL);
        CupScheduler.StartSeason(world, rng);
        return rng;
    }

    /// <summary>Resolves every tie of a round with the lower club id as the winner.</summary>
    private static void ResolveRound(CupRoundBracket round)
    {
        foreach (CupTie tie in round.Ties)
        {
            RecordLowerClubIdWinner(tie.Fixture);
        }
    }

    /// <summary>Records a decisive 1-0 / 0-1 result for the lower club id.</summary>
    private static void RecordLowerClubIdWinner(WorldFixture fixture)
    {
        if (fixture.HomeClubId.Value < fixture.AwayClubId.Value)
        {
            fixture.RecordCupResult(1, 0, 0, 0);
        }
        else
        {
            fixture.RecordCupResult(0, 1, 0, 0);
        }
    }

    /// <summary>Every club playing in a round: tie participants plus byes.</summary>
    private static List<ClubId> Participants(CupRoundBracket round)
    {
        var participants = new List<ClubId>(round.Ties.Count * 2 + round.Byes.Count);
        foreach (CupTie tie in round.Ties)
        {
            participants.Add(tie.HomeClubId);
            participants.Add(tie.AwayClubId);
        }

        participants.AddRange(round.Byes);
        return participants;
    }

    private static void AssertSameClubSet(IReadOnlyCollection<ClubId> expected, IReadOnlyCollection<ClubId> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (ClubId clubId in expected) Assert.Contains(clubId, actual);
        foreach (ClubId clubId in actual) Assert.Contains(clubId, expected);
    }

    /// <summary>Runs the season start plus prelim -> second round -> round of 64 draws.</summary>
    private static void RunThreeRounds(WorldState world)
    {
        WorldRng rng = StartCup(world);
        ResolveRound(world.Cup.RoundOf(CupRound.Preliminary)!);
        Assert.True(CupScheduler.AdvanceIfReady(world, rng));
        ResolveRound(world.Cup.RoundOf(CupRound.SecondRound)!);
        Assert.True(CupScheduler.AdvanceIfReady(world, rng));
    }

    private static void AssertSameBracket(WorldState expected, WorldState actual)
    {
        Assert.Equal(expected.Cup.Rounds.Count, actual.Cup.Rounds.Count);

        for (int roundIndex = 0; roundIndex < expected.Cup.Rounds.Count; roundIndex++)
        {
            CupRoundBracket left = expected.Cup.Rounds[roundIndex];
            CupRoundBracket right = actual.Cup.Rounds[roundIndex];

            Assert.Equal(left.Round, right.Round);
            Assert.Equal(left.Week, right.Week);
            Assert.Equal(left.Ties.Count, right.Ties.Count);
            Assert.Equal(left.Byes.Count, right.Byes.Count);

            for (int tieIndex = 0; tieIndex < left.Ties.Count; tieIndex++)
            {
                CupTie leftTie = left.Ties[tieIndex];
                CupTie rightTie = right.Ties[tieIndex];
                Assert.Equal(leftTie.Fixture.Id, rightTie.Fixture.Id);
                Assert.Equal(leftTie.HomeClubId, rightTie.HomeClubId);
                Assert.Equal(leftTie.AwayClubId, rightTie.AwayClubId);
                Assert.Equal(leftTie.IsResolved, rightTie.IsResolved);
            }

            for (int byeIndex = 0; byeIndex < left.Byes.Count; byeIndex++)
            {
                Assert.Equal(left.Byes[byeIndex], right.Byes[byeIndex]);
            }
        }
    }

    /// <summary>
    /// Builds a world skeleton with the six leagues and (optionally filtered) clubs; no players or managers,
    /// because the cup scheduler only needs club identity and tiers.
    /// </summary>
    private static WorldState CreateCupWorld(ulong seed, int[]? includedTiers = null)
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
            if (includedTiers is not null && !includedTiers.Contains(definition.Tier)) continue;

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

        return state;
    }
}
