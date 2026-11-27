using System;
using System.Collections.Generic;
using System.Linq;
using FootballSim.Core;
using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Players;
using FootballSim.World.Simulation;
using FootballSim.World.World;
using Xunit;
using Xunit.Abstractions;

namespace FootballSim.World.Tests.Simulation;

/// <summary>
/// Stage W3 contract and finance contract (plan §5.4): the market-value and wage formulae, contract creation
/// and expiry, the weekly cash tick and the season payout.
///
/// <para>The generated world is created once and used read-only where possible; every mutating test builds
/// its own fresh world from the same seed, so no test can see another test's mutations. Synthetic players
/// (generated then pinned to an exact ability) keep the formula tests independent of generator noise.</para>
/// </summary>
public sealed class ContractAndFinanceSystemTests
{
    private const ulong DefaultSeed = 20261127UL;

    /// <summary>Prize maximum/minimum per tier (index 0 unused), matching the documented payout ramp.</summary>
    private static readonly long[] PrizeMaximum =
        { 0L, 20_000_000L, 6_000_000L, 1_500_000L, 300_000L, 40_000L, 5_000L };

    private static readonly long[] PrizeMinimum =
        { 0L, 1_000_000L, 600_000L, 200_000L, 40_000L, 5_000L, 500L };

    private static readonly Lazy<WorldPack> FullPack =
        new(() => WorldPackLoader.Load(WorldPackPaths.ResolveDefaultPack()));

    /// <summary>One generated world with contracts initialised; read-only shared by the formula tests.</summary>
    private static readonly Lazy<WorldState> Contracted =
        new(() => NewContractedWorld(DefaultSeed));

    private readonly ITestOutputHelper _output;

    public ContractAndFinanceSystemTests(ITestOutputHelper output) => _output = output;

    private static WorldPack Pack => FullPack.Value;

    // =============================================================================================
    // 1. Valuation and wages.
    // =============================================================================================

    [Fact]
    public void MarketValue_FollowsTheAgeAndTierCurve()
    {
        WorldState world = Contracted.Value;
        WorldClub tierOneClub = world.Clubs.InTier(1)[0];
        WorldClub tierSixClub = world.Clubs.InTier(6)[0];

        WorldPlayer prime = CreateSyntheticPlayer(11UL, age: 27, ability: 75f);
        prime.ClubId = tierOneClub.Id;

        WorldPlayer young = CreateSyntheticPlayer(12UL, age: 18, ability: 75f);
        young.ClubId = tierOneClub.Id;

        WorldPlayer veteran = CreateSyntheticPlayer(13UL, age: 34, ability: 75f);
        veteran.ClubId = tierOneClub.Id;

        WorldPlayer lowerDivisionCopy = CreateSyntheticPlayer(14UL, age: 27, ability: 75f);
        lowerDivisionCopy.ClubId = tierSixClub.Id;

        WorldPlayer freeAgent = CreateSyntheticPlayer(15UL, age: 27, ability: 75f);

        long primeValue = ContractAndFinanceSystem.MarketValue(prime, world.Season);
        long youngValue = ContractAndFinanceSystem.MarketValue(young, world.Season);
        long veteranValue = ContractAndFinanceSystem.MarketValue(veteran, world.Season);
        long lowerDivisionValue = ContractAndFinanceSystem.MarketValue(lowerDivisionCopy, world.Season);
        long freeAgentValue = ContractAndFinanceSystem.MarketValue(freeAgent, world.Season);

        // A top-flight 27-year-old at ability 75 is worth roughly thirty million: (75 - 25)^3 * 260.
        _output.WriteLine(
            $"star market value: tier 1, ability 75.0, age 27 -> {primeValue:N0} " +
            $"(young 18: {youngValue:N0}, veteran 34: {veteranValue:N0}, " +
            $"tier 6 copy: {lowerDivisionValue:N0}, free agent: {freeAgentValue:N0})");
        Assert.InRange(primeValue, 30_000_000L, 35_000_000L);

        // Younger is worth more at the same ability; old legs are worth much less.
        Assert.True(youngValue > primeValue, "an 18-year-old must be worth more than the same player at 27");
        Assert.True(primeValue > veteranValue * 4L, "a 34-year-old must be worth far less than his 27-year-old self");

        // A lower division sells in a completely different market.
        Assert.True(primeValue > lowerDivisionValue * 1000L);
        Assert.True(lowerDivisionValue > 0L);

        // A free agent is still valued (at the bottom-division factor) and never negative.
        Assert.True(freeAgentValue >= 0L);

        // The generated world's best player is a real, plausible star valuation.
        WorldPlayer star = BestPlayerOfTier(world, 1);
        long starValue = ContractAndFinanceSystem.MarketValue(star, world.Season);
        _output.WriteLine(
            $"generated tier-1 star: {star.DisplayName}, ability {star.Attributes.MeanCurrent():0.0}, " +
            $"age {star.Age} -> {starValue:N0}");
        Assert.True(starValue > 5_000_000L, $"the best tier-1 player is worth only {starValue:N0}");
    }

    [Fact]
    public void WageDemand_IsMonotoneInAbilityAndTierAndZeroForAmateurs()
    {
        WorldPlayer weak = CreateSyntheticPlayer(21UL, age: 26, ability: 45f);
        WorldPlayer strong = CreateSyntheticPlayer(22UL, age: 26, ability: 80f);

        long strongWage = ContractAndFinanceSystem.WageDemand(strong, 1);
        long weakWage = ContractAndFinanceSystem.WageDemand(weak, 1);
        Assert.True(strongWage > weakWage);
        Assert.True(strongWage > 0L);

        long previous = long.MaxValue;
        for (int tier = 1; tier <= 5; tier++)
        {
            long wage = ContractAndFinanceSystem.WageDemand(strong, tier);
            Assert.True(wage >= 0L);
            Assert.Equal(0L, wage % 10L); // rounded to the nearest ten
            Assert.True(wage < previous, $"wage demand must fall from tier {tier - 1} to {tier}");
            previous = wage;

            Assert.True(
                ContractAndFinanceSystem.WageDemand(strong, tier) >
                ContractAndFinanceSystem.WageDemand(weak, tier),
                $"a stronger player must demand more in tier {tier}");
        }

        // Amateur football is unpaid, regardless of ability.
        Assert.Equal(0L, ContractAndFinanceSystem.WageDemand(strong, 6));
        Assert.Equal(0L, ContractAndFinanceSystem.WageDemand(weak, 6));
    }

    // =============================================================================================
    // 2. Contract creation.
    // =============================================================================================

    [Fact]
    public void InitializeContracts_GivesEverySquadPlayerSaneTermsAndConsistentWageBills()
    {
        WorldState world = NewContractedWorld(DefaultSeed);

        foreach (WorldClub club in world.Clubs.All)
        {
            long expectedWageBill = 0L;

            foreach (PlayerId playerId in club.Squad)
            {
                WorldPlayer player = world.Players[playerId];
                PlayerContract contract = player.Contract;

                Assert.Equal(world.Season, contract.SignedSeason);
                Assert.False(contract.IsOnLoan);
                Assert.Equal(ClubId.None, contract.ParentClubId);
                Assert.Equal(0, contract.LoanEndSeason);
                Assert.True(contract.EndSeason >= world.Season);

                int seasons = contract.EndSeason - contract.SignedSeason + 1;
                if (player.Age <= 21) Assert.InRange(seasons, 3, 4);
                else if (player.Age <= 27) Assert.InRange(seasons, 2, 4);
                else if (player.Age <= 31) Assert.InRange(seasons, 1, 3);
                else Assert.InRange(seasons, 1, 2);

                Assert.Equal(
                    ContractAndFinanceSystem.WageDemand(player, club.Tier),
                    contract.WeeklyWage);
                expectedWageBill += contract.WeeklyWage;
            }

            Assert.Equal(expectedWageBill, club.Finances.WeeklyWageBill);
            Assert.True(club.Finances.WeeklyWageBudget > 0L);

            // Budgets were refreshed from tier plus positive balance: the top flight keeps its base budget.
            if (club.Tier == 1) Assert.True(club.Finances.TransferBudget >= 25_000_000L);
            if (club.Tier == 6) Assert.InRange(club.Finances.TransferBudget, 0L, 100_000L);
        }

        // Every registered player attached to a club now carries a live contract.
        foreach (WorldPlayer player in world.Players.All)
        {
            if (!player.ClubId.IsValid) continue;
            WorldClub club = world.Clubs[player.ClubId];
            Assert.True(player.Contract.EndSeason >= world.Season);
            Assert.Equal(
                ContractAndFinanceSystem.WageDemand(player, club.Tier), player.Contract.WeeklyWage);
        }

        // Youth intake contracts: 2–3 seasons, tier wage, signed this season.
        WorldClub youthClub = world.Clubs.InTier(2)[0];
        var youthIds = youthClub.Squad.Take(5).ToList();
        ContractAndFinanceSystem.AssignYouthContracts(
            world, youthIds, world.Stream(WorldRandomStream.Contracts));

        foreach (PlayerId playerId in youthIds)
        {
            WorldPlayer player = world.Players[playerId];
            int seasons = player.Contract.EndSeason - player.Contract.SignedSeason + 1;
            Assert.InRange(seasons, 2, 3);
            Assert.Equal(world.Season, player.Contract.SignedSeason);
            Assert.Equal(
                ContractAndFinanceSystem.WageDemand(player, youthClub.Tier), player.Contract.WeeklyWage);
        }

        Assert.Equal(
            youthClub.Squad.Sum(id => world.Players[id].Contract.WeeklyWage),
            youthClub.Finances.WeeklyWageBill);
    }

    // =============================================================================================
    // 3. Weekly cash tick.
    // =============================================================================================

    [Fact]
    public void AdvanceWeek_IsDeterministicPaysWagesAndCreditsIncome()
    {
        WorldState first = NewContractedWorld(DefaultSeed);
        WorldState second = NewContractedWorld(DefaultSeed);

        long expectedWages = first.Clubs.All.Sum(club =>
            club.Squad.Sum(id => first.Players[id].Contract.WeeklyWage));

        FinanceWeekResult firstResult = ContractAndFinanceSystem.AdvanceWeek(first);
        FinanceWeekResult secondResult = ContractAndFinanceSystem.AdvanceWeek(second);

        Assert.Equal(firstResult, secondResult);
        Assert.Equal(expectedWages, firstResult.WagesPaid);
        Assert.True(firstResult.Income > 0L, "the weekly tick must credit sponsorship and TV money");
        Assert.Equal(firstResult.Income - firstResult.WagesPaid, firstResult.NetChange);

        int expectedDebt = first.Clubs.All.Count(club => club.Finances.Balance < 0L);
        Assert.Equal(expectedDebt, firstResult.ClubsInDebt);

        for (int index = 0; index < first.Clubs.Count; index++)
        {
            WorldClub left = first.Clubs.All[index];
            WorldClub right = second.Clubs.All[index];

            Assert.Equal(left.Id, right.Id);
            Assert.Equal(left.Finances.Balance, right.Finances.Balance);
            Assert.Equal(left.Finances.WeeklyWageBill, right.Finances.WeeklyWageBill);
        }

        _output.WriteLine(
            $"weekly tick: income {firstResult.Income:N0}, wages {firstResult.WagesPaid:N0}, " +
            $"net {firstResult.NetChange:N0}, clubs in debt {firstResult.ClubsInDebt}");
    }

    [Fact]
    public void AdvanceWeek_AHomeClubEarnsMatchdayIncomeAnAwayClubDoesNot()
    {
        WorldState world = NewContractedWorld(DefaultSeed);

        // Isolate the income channel: with no wages the balance change is exactly this week's income.
        foreach (WorldPlayer player in world.Players.All) player.Contract.WeeklyWage = 0L;
        ContractAndFinanceSystem.RefreshBudgets(world);

        world.Date = new WorldDate(world.Season, 5); // league matchday 1

        WorldFixture? matchdayOne = null;
        foreach (WorldFixture fixture in world.LeagueForTier(1).Fixtures)
        {
            if (fixture.Week == 5) { matchdayOne = fixture; break; }
        }

        Assert.NotNull(matchdayOne);
        WorldClub home = world.Clubs[matchdayOne!.HomeClubId];
        WorldClub away = world.Clubs[matchdayOne.AwayClubId];

        long[] balanceBefore = world.Clubs.All.Select(club => club.Finances.Balance).ToArray();
        FinanceWeekResult result = ContractAndFinanceSystem.AdvanceWeek(world);
        long[] balanceAfter = world.Clubs.All.Select(club => club.Finances.Balance).ToArray();

        long homeDelta = balanceAfter[home.Id.Value - 1] - balanceBefore[home.Id.Value - 1];
        long awayDelta = balanceAfter[away.Id.Value - 1] - balanceBefore[away.Id.Value - 1];

        long totalDelta = balanceAfter.Sum(balance => balance) - balanceBefore.Sum(balance => balance);
        Assert.Equal(result.Income, totalDelta);
        Assert.Equal(0L, result.WagesPaid);
        Assert.True(homeDelta > awayDelta, $"home club earned {homeDelta:N0}, away {awayDelta:N0}");
        Assert.True(homeDelta - awayDelta > 100_000L, "the home fixture must bring gate receipts");

        // Both clubs are in the same division, so TV and tier scaling are identical; the gate is the gap.
        _output.WriteLine(
            $"matchday income: {home.Name} (home) +{homeDelta:N0} vs {away.Name} (away) +{awayDelta:N0} " +
            $"gate {homeDelta - awayDelta:N0} on capacity {home.Capacity:N0}");
    }

    // =============================================================================================
    // 4. Season payouts.
    // =============================================================================================

    [Fact]
    public void PaySeasonPayouts_CreditsTheChampionMoreThanTheBottomClub()
    {
        WorldState world = NewContractedWorld(DefaultSeed);
        SeasonOutcome outcome = StandingsCalculator.ResolveSeason(world.Leagues);
        long[] before = world.Clubs.All.Select(club => club.Finances.Balance).ToArray();

        SeasonPayoutResult result = ContractAndFinanceSystem.PaySeasonPayouts(world, outcome);

        Assert.True(result.TotalPrizeMoney > 0L);
        Assert.Equal(0L, result.TotalTvMoney); // v1 pays TV weekly

        long credited = 0L;
        for (int tier = 1; tier <= 6; tier++)
        {
            ClubId championId = outcome.TablesByTier[tier - 1][0].ClubId;
            ClubId bottomId = outcome.TablesByTier[tier - 1][17].ClubId;

            long championGain = world.Clubs[championId].Finances.Balance - before[championId.Value - 1];
            long bottomGain = world.Clubs[bottomId].Finances.Balance - before[bottomId.Value - 1];

            Assert.Equal(PrizeMaximum[tier], championGain);
            Assert.Equal(PrizeMinimum[tier], bottomGain);
            Assert.True(championGain > bottomGain);

            credited += outcome.TablesByTier[tier - 1]
                .Select(row => world.Clubs[row.ClubId].Finances.Balance - before[row.ClubId.Value - 1])
                .Sum();
        }

        Assert.Equal(result.TotalPrizeMoney, credited);
        _output.WriteLine(
            $"season payouts: tier 1 champion {PrizeMaximum[1]:N0} .. 18th {PrizeMinimum[1]:N0}; " +
            $"total prize money {result.TotalPrizeMoney:N0}");
    }

    // =============================================================================================
    // 5. Off-season rollover.
    // =============================================================================================

    [Fact]
    public void ProcessExpiriesAndLoans_RenewsOrReleasesEveryExpiringPlayerAndReturnsLoans()
    {
        WorldState world = NewContractedWorld(DefaultSeed);
        int season = world.Season;

        // A tier-1 prospect is out on loan at a tier-5 club; his parent contract is not expiring.
        WorldClub parent = world.Clubs.InTier(1)[0];
        WorldClub loanClub = world.Clubs.InTier(5)[0];
        WorldPlayer loaned = world.Players[parent.Squad[0]];
        loaned.Contract.EndSeason = season + 2;
        Assert.True(TransferMarket.ExecuteLoan(world, loaned.Id, loanClub.Id, season));
        Assert.Equal(loanClub.Id, loaned.ClubId);

        // Expiring sample: young tier-1 players (very likely renewed) and old tier-6 players (often released).
        var expiring = new List<WorldPlayer>();
        CollectExpiring(world, 1, 10, expiring, loaned, maximumAge: 22);
        int oldTarget = expiring.Count + 15;
        CollectExpiring(world, 6, oldTarget, expiring, loaned, minimumAge: 31);
        Assert.True(expiring.Count >= 20, $"only {expiring.Count} expiring players could be constructed");

        var expiringIds = new HashSet<PlayerId>(expiring.Select(player => player.Id));
        foreach (WorldPlayer player in world.Players.All)
        {
            if (player.Id == loaned.Id || expiringIds.Contains(player.Id)) continue;
            player.Contract.EndSeason = season + 2; // everything else is safely under contract
        }

        ContractRolloverResult result = ContractAndFinanceSystem.ProcessExpiriesAndLoans(
            world, new WorldRng(2026_1127UL));

        // 1. The loan came home (and the parent squad owns the registration again).
        Assert.Equal(1, result.LoansReturned);
        Assert.Equal(parent.Id, loaned.ClubId);
        Assert.Contains(loaned.Id, parent.Squad);
        Assert.DoesNotContain(loaned.Id, loanClub.Squad);
        Assert.False(loaned.Contract.IsOnLoan);
        Assert.Equal(ClubId.None, loaned.Contract.ParentClubId);

        // 2. Every expiring player was renewed or released, never left dangling.
        Assert.Equal(expiring.Count, result.Renewals + result.ReleasedToFreeAgency);
        Assert.True(result.Renewals > 0, "no expiring player was renewed");
        Assert.True(result.ReleasedToFreeAgency > 0, "no expiring player was released");

        int releasedLogEntries = 0;
        int loanReturnLogEntries = 0;
        foreach (TransferRecord record in world.TransferLog)
        {
            if (record.Kind == TransferKind.Released)
            {
                releasedLogEntries++;
                Assert.Equal(ClubId.None, record.ToClubId);
                Assert.False(world.Players[record.PlayerId].ClubId.IsValid);
            }
            else if (record.Kind == TransferKind.LoanReturn)
            {
                loanReturnLogEntries++;
                Assert.Equal(parent.Id, record.ToClubId);
                Assert.Equal(loanClub.Id, record.FromClubId);
            }
        }

        Assert.Equal(result.ReleasedToFreeAgency, releasedLogEntries);
        Assert.Equal(1, loanReturnLogEntries);

        int renewed = 0;
        int released = 0;
        foreach (WorldPlayer player in expiring)
        {
            if (player.ClubId.IsValid)
            {
                renewed++;
                Assert.Contains(player.Id, world.Clubs[player.ClubId].Squad);
                Assert.True(player.Contract.EndSeason >= season);
            }
            else
            {
                released++;
                Assert.Equal(0L, player.Contract.WeeklyWage);
                Assert.Equal(season, player.Contract.EndSeason);
            }
        }

        Assert.Equal(result.Renewals, renewed);
        Assert.Equal(result.ReleasedToFreeAgency, released);

        // 3. Every active player has a valid contract and every club's wage bill agrees with its squad.
        foreach (WorldPlayer player in world.Players.All)
        {
            if (!player.ClubId.IsValid) continue;
            Assert.Contains(player.Id, world.Clubs[player.ClubId].Squad);
            Assert.True(player.Contract.EndSeason >= season);
            Assert.True(player.Contract.SignedSeason <= season);
            Assert.False(player.Contract.IsOnLoan);
        }

        foreach (WorldClub club in world.Clubs.All)
        {
            long expectedWageBill = club.Squad.Sum(id => world.Players[id].Contract.WeeklyWage);
            Assert.Equal(expectedWageBill, club.Finances.WeeklyWageBill);
            Assert.True(club.Finances.WeeklyWageBudget > 0L);
        }

        _output.WriteLine(
            $"rollover: {result.Renewals} renewals, {result.ReleasedToFreeAgency} releases, " +
            $"{result.LoansReturned} loan returned; {world.CountTransfers(TransferKind.Released)} " +
            $"release log entries");
    }

    // =============================================================================================
    // 6. Budgets.
    // =============================================================================================

    [Fact]
    public void RefreshBudgets_RebuildsFromBalanceAndIsIdempotent()
    {
        WorldState world = NewContractedWorld(DefaultSeed);

        WorldClub rich = world.Clubs.InTier(1)[0];
        rich.Finances.Balance = 60_000_000L;
        rich.Finances.TransferBudget = 1L;

        WorldClub poor = world.Clubs.InTier(6)[0];
        poor.Finances.Balance = -5_000_000L;
        poor.Finances.TransferBudget = 999_999L;

        ContractAndFinanceSystem.RefreshBudgets(world);

        // Tier-1 base 25M plus 10% of the 60M positive balance.
        Assert.Equal(25_000_000L + 6_000_000L, rich.Finances.TransferBudget);
        Assert.True(rich.Finances.WeeklyWageBudget > 0L);

        // The amateur club has no base budget and no positive balance to invest.
        Assert.Equal(0L, poor.Finances.TransferBudget);
        Assert.True(poor.Finances.WeeklyWageBudget > 0L);

        foreach (WorldClub club in world.Clubs.All)
        {
            long expectedWageBill = club.Squad.Sum(id => world.Players[id].Contract.WeeklyWage);
            Assert.Equal(expectedWageBill, club.Finances.WeeklyWageBill);
            Assert.True(club.Finances.TransferBudget >= 0L);
        }

        // The refresh is a pure recomputation: a second call changes nothing.
        long[] transferBudgets = world.Clubs.All.Select(club => club.Finances.TransferBudget).ToArray();
        long[] wageBudgets = world.Clubs.All.Select(club => club.Finances.WeeklyWageBudget).ToArray();
        ContractAndFinanceSystem.RefreshBudgets(world);
        for (int index = 0; index < world.Clubs.Count; index++)
        {
            Assert.Equal(transferBudgets[index], world.Clubs.All[index].Finances.TransferBudget);
            Assert.Equal(wageBudgets[index], world.Clubs.All[index].Finances.WeeklyWageBudget);
        }
    }

    // =============================================================================================
    // Helpers.
    // =============================================================================================

    /// <summary>Generates a fresh world and initialises its contracts.</summary>
    private static WorldState NewContractedWorld(ulong seed)
    {
        WorldState world = WorldGenerator.Generate(Pack, seed);
        ContractAndFinanceSystem.InitializeContracts(world, world.Stream(WorldRandomStream.Contracts));
        return world;
    }

    /// <summary>
    /// Generates a player and then pins every current attribute and ceiling so the derived ability is exactly
    /// <paramref name="ability"/> (the value formulae are pure functions of the mean current attribute).
    /// </summary>
    private static WorldPlayer CreateSyntheticPlayer(ulong seed, int age, float ability)
    {
        WorldPlayer player = PlayerGenerator.GenerateNpcForTier(
            seed, tier: 3, age, TacticalRole.CentralMidfielder);
        foreach (AttributeId id in AttributeMetadata.All)
        {
            player.Attributes.SetCeiling(id, 99f);
            player.Attributes.SetCurrent(id, ability);
        }

        return player;
    }

    /// <summary>The highest-ability player in one division of a generated world.</summary>
    private static WorldPlayer BestPlayerOfTier(WorldState world, int tier)
    {
        WorldPlayer best = world.Players[world.Clubs.InTier(tier)[0].Squad[0]];
        foreach (WorldClub club in world.Clubs.InTier(tier))
        {
            foreach (PlayerId id in club.Squad)
            {
                WorldPlayer candidate = world.Players[id];
                if (candidate.Attributes.MeanCurrent() > best.Attributes.MeanCurrent()) best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Adds one expiring player per club in a division to <paramref name="expiring"/>, marking his contract as
    /// ending this season. Age filters keep the two test groups (renewals vs releases) disjoint.
    /// </summary>
    private static void CollectExpiring(
        WorldState world,
        int tier,
        int targetCount,
        List<WorldPlayer> expiring,
        WorldPlayer excluded,
        int? maximumAge = null,
        int? minimumAge = null)
    {
        foreach (WorldClub club in world.Clubs.InTier(tier))
        {
            if (expiring.Count >= targetCount) return;

            foreach (PlayerId id in club.Squad)
            {
                WorldPlayer player = world.Players[id];
                if (player.Id == excluded.Id) continue;
                if (player.Contract.IsOnLoan) continue;
                if (maximumAge.HasValue && player.Age > maximumAge.Value) continue;
                if (minimumAge.HasValue && player.Age < minimumAge.Value) continue;

                player.Contract.EndSeason = world.Season;
                expiring.Add(player);
                break;
            }
        }
    }
}
