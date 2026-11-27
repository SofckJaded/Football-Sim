using System;
using System.Collections.Generic;
using System.Linq;
using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Players;
using FootballSim.World.Simulation;
using FootballSim.World.World;
using Xunit;
using Xunit.Abstractions;

namespace FootballSim.World.Tests.Simulation;

/// <summary>
/// Stage W3 transfer-market contract (plan Â§5.4): the window calendar, fee valuation, permanent/free/loan
/// execution, the weekly AI window and the human career offer generator.
///
/// <para>The generated world is created once for the read-only checks; every mutating scenario generates its
/// own fresh world from the same seed, so a test can never observe another test's transfers.</para>
/// </summary>
public sealed class TransferMarketTests
{
    private const ulong DefaultSeed = 20261127UL;

    private static readonly Lazy<WorldPack> FullPack =
        new(() => WorldPackLoader.Load(WorldPackPaths.ResolveDefaultPack()));

    /// <summary>One generated, never-mutated world used by the pure/read-only checks.</summary>
    private static readonly Lazy<WorldState> Generated =
        new(() => WorldGenerator.Generate(FullPack.Value, DefaultSeed));

    private readonly ITestOutputHelper _output;

    public TransferMarketTests(ITestOutputHelper output) => _output = output;

    private static WorldPack Pack => FullPack.Value;

    // =============================================================================================
    // 1. Calendar and valuation.
    // =============================================================================================

    [Fact]
    public void IsWindowOpen_DelegatesToTheSeasonCalendar()
    {
        for (int week = 1; week <= 4; week++) Assert.True(TransferMarket.IsWindowOpen(week));
        for (int week = 22; week <= 25; week++) Assert.True(TransferMarket.IsWindowOpen(week));

        Assert.False(TransferMarket.IsWindowOpen(5));
        Assert.False(TransferMarket.IsWindowOpen(21));
        Assert.False(TransferMarket.IsWindowOpen(26));
        Assert.False(TransferMarket.IsWindowOpen(42));
        Assert.False(TransferMarket.IsWindowOpen(46));
        Assert.False(TransferMarket.IsWindowOpen(52));
    }

    [Fact]
    public void FeeFor_IsThePlayersMarketValue()
    {
        WorldState world = Generated.Value;
        long starFee = 0L;

        for (int index = 0; index < world.Players.Count; index += 97)
        {
            WorldPlayer player = world.Players.All[index];
            long expected = ContractAndFinanceSystem.MarketValue(player, world.Season);
            Assert.Equal(expected, TransferMarket.FeeFor(player, world.Season));
            starFee = Math.Max(starFee, expected);
        }

        WorldPlayer best = world.Players.All[0];
        foreach (WorldPlayer player in world.Players.All)
        {
            if (player.Attributes.MeanCurrent() > best.Attributes.MeanCurrent()) best = player;
        }

        _output.WriteLine(
            $"fees: best player {best.DisplayName} (ability {best.Attributes.MeanCurrent():0.0}) " +
            $"valued at {TransferMarket.FeeFor(best, world.Season):N0}");
        Assert.True(starFee > 1_000_000L);
    }

    // =============================================================================================
    // 2. Transfer execution.
    // =============================================================================================

    [Fact]
    public void ExecuteTransfer_MovesThePlayerMoneySquadsContractAndLog()
    {
        WorldState world = NewContractedWorld();
        WorldClub buyer = world.Clubs.InTier(1)[0];
        WorldClub seller = world.Clubs.InTier(5)[0];
        WorldPlayer player = world.Players[seller.Squad[0]];

        const long fee = 250_000L;
        const long weeklyWage = 7_500L;
        const int contractSeasons = 3;

        long buyerBalanceBefore = buyer.Finances.Balance;
        long sellerBalanceBefore = seller.Finances.Balance;
        long buyerBudgetBefore = buyer.Finances.TransferBudget;
        long sellerBudgetBefore = seller.Finances.TransferBudget;
        int logBefore = world.TransferLog.Count;

        Assert.True(TransferMarket.ExecuteTransfer(
            world, player.Id, buyer.Id, fee, weeklyWage, TransferKind.Permanent, contractSeasons));

        Assert.Equal(buyer.Id, player.ClubId);
        Assert.Contains(player.Id, buyer.Squad);
        Assert.DoesNotContain(player.Id, seller.Squad);

        Assert.Equal(buyerBalanceBefore - fee, buyer.Finances.Balance);
        Assert.Equal(sellerBalanceBefore + fee, seller.Finances.Balance);
        Assert.Equal(buyerBudgetBefore - fee, buyer.Finances.TransferBudget);
        Assert.Equal(sellerBudgetBefore + fee, seller.Finances.TransferBudget);

        Assert.Equal(logBefore + 1, world.TransferLog.Count);
        Assert.Equal(
            new TransferRecord(
                world.Season, world.Week, player.Id, seller.Id, buyer.Id, TransferKind.Permanent, fee),
            world.TransferLog[^1]);

        Assert.Equal(world.Season, player.Contract.SignedSeason);
        Assert.Equal(world.Season + contractSeasons - 1, player.Contract.EndSeason);
        Assert.Equal(weeklyWage, player.Contract.WeeklyWage);
        Assert.False(player.Contract.IsOnLoan);

        Assert.Equal(SquadWageBill(world, buyer), buyer.Finances.WeeklyWageBill);
        Assert.Equal(SquadWageBill(world, seller), seller.Finances.WeeklyWageBill);

        _output.WriteLine(
            $"transfer: {player.DisplayName} {seller.Name} -> {buyer.Name} for {fee:N0} " +
            $"on {weeklyWage:N0}/week over {contractSeasons} seasons");
    }

    [Fact]
    public void ExecuteTransfer_RefusesAmateurFeesAndInvalidMovesAndSignsFreeAgents()
    {
        WorldState world = NewContractedWorld();
        WorldClub amateurBuyer = world.Clubs.InTier(6)[0];
        WorldPlayer amateurTarget = world.Players[world.Clubs.InTier(6)[1].Squad[0]];
        int logBefore = world.TransferLog.Count;

        // Tier 6 is amateur: no fee may change hands through the transfer system.
        Assert.False(TransferMarket.ExecuteTransfer(
            world, amateurTarget.Id, amateurBuyer.Id, fee: 1_000L, weeklyWage: 0L, TransferKind.Permanent));
        Assert.Equal(world.Clubs.InTier(6)[1].Id, amateurTarget.ClubId);
        Assert.Equal(logBefore, world.TransferLog.Count);

        // A retired player cannot be signed; a move to the club he already plays for is refused too.
        WorldPlayer retired = world.Players[world.Clubs.InTier(4)[0].Squad[0]];
        retired.IsRetired = true;
        Assert.False(TransferMarket.ExecuteTransfer(
            world, retired.Id, amateurBuyer.Id, fee: 0L, weeklyWage: 0L, TransferKind.Free));

        WorldPlayer sameClub = world.Players[amateurBuyer.Squad[0]];
        Assert.False(TransferMarket.ExecuteTransfer(
            world, sameClub.Id, amateurBuyer.Id, fee: 0L, weeklyWage: 0L, TransferKind.Free));

        // Unknown ids are contract violations, not ordinary refusals.
        Assert.Throws<KeyNotFoundException>(() => TransferMarket.ExecuteTransfer(
            world, new PlayerId(999_999), amateurBuyer.Id, 0L, 0L, TransferKind.Free));
        Assert.Throws<KeyNotFoundException>(() => TransferMarket.ExecuteLoan(
            world, new PlayerId(999_999), amateurBuyer.Id, world.Season));

        // A free agent can be signed by an amateur club at zero fee.
        WorldClub formerClub = world.Clubs.InTier(3)[0];
        WorldPlayer freeAgent = world.Players[formerClub.Squad[^1]];
        formerClub.RemovePlayer(freeAgent.Id);
        freeAgent.ClubId = ClubId.None;

        Assert.True(TransferMarket.ExecuteTransfer(
            world, freeAgent.Id, amateurBuyer.Id, fee: 0L, weeklyWage: 0L, TransferKind.Free));
        Assert.Equal(amateurBuyer.Id, freeAgent.ClubId);
        Assert.Contains(freeAgent.Id, amateurBuyer.Squad);
        Assert.Equal(0L, freeAgent.Contract.WeeklyWage); // amateur football is unpaid

        TransferRecord record = world.TransferLog[^1];
        Assert.Equal(TransferKind.Free, record.Kind);
        Assert.Equal(ClubId.None, record.FromClubId);
        Assert.Equal(amateurBuyer.Id, record.ToClubId);
        Assert.Equal(0L, record.Fee);
    }

    // =============================================================================================
    // 3. Loans and the rollover.
    // =============================================================================================

    [Fact]
    public void ExecuteLoan_MovesRegistrationAndTheRolloverReturnsThePlayer()
    {
        WorldState world = NewContractedWorld();
        int season = world.Season;

        WorldClub parent = world.Clubs.InTier(1)[0];
        WorldClub loanClub = world.Clubs.InTier(5)[0];
        WorldPlayer player = world.Players[parent.Squad[0]];
        player.Contract.EndSeason = season + 2;
        int logBefore = world.TransferLog.Count;

        Assert.True(TransferMarket.ExecuteLoan(world, player.Id, loanClub.Id, season));

        Assert.Equal(loanClub.Id, player.ClubId);
        Assert.Contains(player.Id, loanClub.Squad);
        Assert.DoesNotContain(player.Id, parent.Squad);
        Assert.True(player.Contract.IsOnLoan);
        Assert.Equal(parent.Id, player.Contract.ParentClubId);
        Assert.Equal(season, player.Contract.LoanEndSeason);

        Assert.Equal(logBefore + 1, world.TransferLog.Count);
        Assert.Equal(
            new TransferRecord(season, world.Week, player.Id, parent.Id, loanClub.Id, TransferKind.Loan, 0L),
            world.TransferLog[^1]);

        // Invalid loans are ordinary refusals.
        Assert.False(TransferMarket.ExecuteLoan(world, player.Id, loanClub.Id, season)); // already on loan
        WorldPlayer unfit = world.Players[parent.Squad[^1]];
        Assert.False(TransferMarket.ExecuteLoan(world, unfit.Id, unfit.ClubId, season)); // own club

        // The off-season rollover returns him to the parent club and clears the loan flags.
        ContractRolloverResult rollover = ContractAndFinanceSystem.ProcessExpiriesAndLoans(
            world, new WorldRng(11UL));

        Assert.Equal(1, rollover.LoansReturned);
        Assert.Equal(parent.Id, player.ClubId);
        Assert.Contains(player.Id, parent.Squad);
        Assert.DoesNotContain(player.Id, loanClub.Squad);
        Assert.False(player.Contract.IsOnLoan);
        Assert.Equal(ClubId.None, player.Contract.ParentClubId);
        Assert.Equal(0, player.Contract.LoanEndSeason);
        Assert.Contains(
            world.TransferLog,
            record => record.Kind == TransferKind.LoanReturn &&
                record.PlayerId == player.Id &&
                record.FromClubId == loanClub.Id &&
                record.ToClubId == parent.Id);
    }

    // =============================================================================================
    // 4. The weekly AI window.
    // =============================================================================================

    [Fact]
    public void RunWindow_ProducesInternallyConsistentMovesInsideTheWindow()
    {
        WorldState world = NewContractedWorld();
        var transferRng = new WorldRng(424_242UL);
        var scoutingRng = new WorldRng(515_151UL);

        int permanent = 0;
        int free = 0;
        int loans = 0;
        long fees = 0L;
        int active = 0;

        for (int week = 1; week <= 4; week++)
        {
            world.Date = new WorldDate(world.Season, week);
            Assert.True(TransferMarket.IsWindowOpen(week));

            ScoutingSystem.AdvanceWeek(world, scoutingRng);
            TransferWindowResult result = TransferMarket.RunWindow(world, transferRng);

            permanent += result.PermanentTransfers;
            free += result.FreeTransfers;
            loans += result.Loans;
            fees += result.TotalFees;
            active = Math.Max(active, result.ClubsActive);
        }

        Assert.True(
            permanent + free + loans > 0,
            "four pre-season transfer weeks produced no moves at all");

        Assert.Equal(world.CountTransfers(TransferKind.Permanent), permanent);
        Assert.Equal(world.CountTransfers(TransferKind.Free), free);
        Assert.Equal(world.CountTransfers(TransferKind.Loan), loans);

        long loggedFees = world.TransferLog
            .Where(record => record.Kind == TransferKind.Permanent)
            .Sum(record => record.Fee);
        Assert.Equal(fees, loggedFees);

        // A player can move more than once in one window, so per-move invariants and "where he is now" are
        // checked separately: the log must chain, and the LAST entry for a player must describe his squad.
        var lastRecordByPlayer = new Dictionary<PlayerId, TransferRecord>();
        var previousRecordByPlayer = new Dictionary<PlayerId, TransferRecord>();

        foreach (TransferRecord record in world.TransferLog)
        {
            Assert.True(record.Fee >= 0L);
            Assert.Equal(world.Season, record.Season);
            Assert.True(record.ToClubId.IsValid);

            WorldClub toClub = world.Clubs[record.ToClubId];

            switch (record.Kind)
            {
                case TransferKind.Permanent:
                    Assert.True(record.Fee > 0L);
                    Assert.InRange(toClub.Tier, 1, 4);
                    if (toClub.Tier == 4) Assert.True(record.Fee <= 500_000L);
                    break;
                case TransferKind.Free:
                    Assert.Equal(0L, record.Fee);
                    Assert.Equal(ClubId.None, record.FromClubId);
                    break;
                case TransferKind.Loan:
                    Assert.Equal(0L, record.Fee);
                    Assert.InRange(toClub.Tier, 4, 6);
                    break;
                default:
                    Assert.Fail($"unexpected transfer-log kind {record.Kind} inside the window");
                    break;
            }

            if (previousRecordByPlayer.TryGetValue(record.PlayerId, out TransferRecord? previous))
            {
                Assert.Equal(previous.ToClubId, record.FromClubId);
            }

            previousRecordByPlayer[record.PlayerId] = record;
            lastRecordByPlayer[record.PlayerId] = record;
        }

        foreach (KeyValuePair<PlayerId, TransferRecord> pair in lastRecordByPlayer)
        {
            TransferRecord record = pair.Value;
            WorldPlayer player = world.Players[record.PlayerId];

            Assert.Equal(record.ToClubId, player.ClubId);
            Assert.Contains(record.PlayerId, world.Clubs[record.ToClubId].Squad);
            if (record.FromClubId.IsValid)
            {
                Assert.DoesNotContain(record.PlayerId, world.Clubs[record.FromClubId].Squad);
            }

            if (record.Kind == TransferKind.Loan)
            {
                Assert.True(player.Contract.IsOnLoan);
                Assert.Equal(record.FromClubId, player.Contract.ParentClubId);
            }
            else
            {
                Assert.False(player.Contract.IsOnLoan);
            }
        }

        _output.WriteLine(
            $"transfers in window: {permanent} permanent, {free} free, {loans} loans, " +
            $"fees {fees:N0}, up to {active} clubs active in one week");
    }

    [Fact]
    public void RunWindow_IsDeterministicAcrossIdenticalWorlds()
    {
        WorldState first = NewContractedWorld();
        WorldState second = NewContractedWorld();

        var firstTransfers = new WorldRng(777UL);
        var secondTransfers = new WorldRng(777UL);
        var firstScouting = new WorldRng(888UL);
        var secondScouting = new WorldRng(888UL);

        for (int week = 1; week <= 4; week++)
        {
            first.Date = new WorldDate(first.Season, week);
            second.Date = new WorldDate(second.Season, week);

            ScoutingSystem.AdvanceWeek(first, firstScouting);
            ScoutingSystem.AdvanceWeek(second, secondScouting);

            TransferWindowResult left = TransferMarket.RunWindow(first, firstTransfers);
            TransferWindowResult right = TransferMarket.RunWindow(second, secondTransfers);

            Assert.Equal(left, right);
        }

        Assert.Equal(first.TransferLog.Count, second.TransferLog.Count);
        Assert.True(first.TransferLog.Count > 0, "the deterministic window replay produced no moves");

        for (int index = 0; index < first.TransferLog.Count; index++)
        {
            Assert.Equal(first.TransferLog[index], second.TransferLog[index]);
        }

        for (int index = 0; index < first.Clubs.Count; index++)
        {
            WorldClub left = first.Clubs.All[index];
            WorldClub right = second.Clubs.All[index];
            Assert.Equal(left.Finances.Balance, right.Finances.Balance);
            Assert.Equal(left.Finances.WeeklyWageBill, right.Finances.WeeklyWageBill);
        }

        _output.WriteLine(
            $"window determinism: {first.TransferLog.Count} identical logged moves, " +
            $"{first.Clubs.All.Sum(club => club.Finances.Balance):N0} total balance");
    }

    // =============================================================================================
    // 5. Human career offers.
    // =============================================================================================

    [Fact]
    public void GenerateOffersForPlayer_IsDeterministicAndDoesNotMutateTheWorld()
    {
        WorldState world = Generated.Value;
        CaptureWorld(world, out ClubId[] playerClubs, out long[] balances, out long[] wages, out int logCount);

        IReadOnlyList<TransferOffer>? offers = null;
        WorldPlayer player = null!;
        foreach (WorldPlayer candidate in world.Players.All)
        {
            IReadOnlyList<TransferOffer> generated = TransferMarket.GenerateOffersForPlayer(
                world, candidate.Id, new WorldRng(1234UL));
            if (generated.Count < 2) continue;
            offers = generated;
            player = candidate;
            break;
        }

        Assert.NotNull(offers);
        Assert.NotNull(player);
        Assert.InRange(offers!.Count, 2, 4);

        long expectedFee = TransferMarket.FeeFor(player, world.Season);
        int expectedExpiry = Math.Min(WorldCalendar.WeeksPerSeason, world.Week + 4);

        for (int index = 0; index < offers.Count; index++)
        {
            TransferOffer offer = offers[index];
            Assert.Equal(index + 1, offer.Id);
            Assert.Equal(player.Id, offer.PlayerId);
            Assert.Equal(player.ClubId, offer.FromClubId);
            Assert.Equal(expectedFee, offer.Fee);
            Assert.Equal(TransferOfferStatus.Pending, offer.Status);
            Assert.Equal(world.Season, offer.CreatedSeason);
            Assert.Equal(world.Week, offer.CreatedWeek);
            Assert.Equal(expectedExpiry, offer.ExpiresWeek);
            Assert.Equal(TransferKind.Permanent, offer.Kind);

            WorldClub buyer = world.Clubs[offer.ToClubId];
            Assert.Equal(
                ContractAndFinanceSystem.WageDemand(player, buyer.Tier), offer.WeeklyWage);
            Assert.InRange(Math.Abs(buyer.Tier - world.Clubs[player.ClubId].Tier), 0, 1);

            if (index == 0) continue;
            Assert.True(offers[index - 1].ToClubId.Value < offer.ToClubId.Value);
            Assert.False(offer.HasExpired(world.Season, world.Week));
        }

        // A fresh generator with the same seed reproduces the identical batch.
        IReadOnlyList<TransferOffer> again = TransferMarket.GenerateOffersForPlayer(
            world, player.Id, new WorldRng(1234UL));
        Assert.Equal(offers.Count, again.Count);
        for (int index = 0; index < offers.Count; index++)
        {
            Assert.Equal(offers[index].ToClubId, again[index].ToClubId);
            Assert.Equal(offers[index].Fee, again[index].Fee);
            Assert.Equal(offers[index].WeeklyWage, again[index].WeeklyWage);
            Assert.Equal(offers[index].ExpiresWeek, again[index].ExpiresWeek);
            Assert.Equal(offers[index].Kind, again[index].Kind);
        }

        AssertWorldUnchanged(world, playerClubs, balances, wages, logCount);
        _output.WriteLine(
            $"career offers for {player.DisplayName}: {offers.Count} clubs " +
            $"({string.Join(", ", offers.Select(offer => world.Clubs[offer.ToClubId].Name))})");
    }

    [Fact]
    public void TransferOffer_HasExpiredComparesTheCalendarAndTheExpiredStatus()
    {
        var offer = new TransferOffer
        {
            Id = 1,
            PlayerId = new PlayerId(1),
            FromClubId = new ClubId(1),
            ToClubId = new ClubId(2),
            Fee = 100L,
            WeeklyWage = 10L,
            Kind = TransferKind.Permanent,
            CreatedSeason = 3,
            CreatedWeek = 2,
            ExpiresWeek = 6
        };

        Assert.False(offer.HasExpired(3, 2));
        Assert.False(offer.HasExpired(3, 6));
        Assert.True(offer.HasExpired(3, 7));
        Assert.True(offer.HasExpired(4, 1));

        offer.Status = TransferOfferStatus.Expired;
        Assert.True(offer.HasExpired(3, 2));
    }

    // =============================================================================================
    // Helpers.
    // =============================================================================================

    /// <summary>Generates a fresh world and initialises its contracts (the state a live career starts from).</summary>
    private static WorldState NewContractedWorld()
    {
        WorldState world = WorldGenerator.Generate(Pack, DefaultSeed);
        ContractAndFinanceSystem.InitializeContracts(world, world.Stream(WorldRandomStream.Contracts));
        return world;
    }

    private static long SquadWageBill(WorldState world, WorldClub club) =>
        club.Squad.Sum(id => world.Players[id].Contract.WeeklyWage);

    /// <summary>Captures everything the offer generator must not change.</summary>
    private static void CaptureWorld(
        WorldState world,
        out ClubId[] playerClubs,
        out long[] balances,
        out long[] wages,
        out int logCount)
    {
        playerClubs = world.Players.All.Select(player => player.ClubId).ToArray();
        balances = world.Clubs.All.Select(club => club.Finances.Balance).ToArray();
        wages = world.Players.All.Select(player => player.Contract.WeeklyWage).ToArray();
        logCount = world.TransferLog.Count;
    }

    private static void AssertWorldUnchanged(
        WorldState world,
        ClubId[] playerClubs,
        long[] balances,
        long[] wages,
        int logCount)
    {
        Assert.Equal(playerClubs.Length, world.Players.Count);
        for (int index = 0; index < playerClubs.Length; index++)
        {
            Assert.Equal(playerClubs[index], world.Players.All[index].ClubId);
            Assert.Equal(wages[index], world.Players.All[index].Contract.WeeklyWage);
        }

        Assert.Equal(balances.Length, world.Clubs.Count);
        for (int index = 0; index < balances.Length; index++)
        {
            Assert.Equal(balances[index], world.Clubs.All[index].Finances.Balance);
        }

        Assert.Equal(logCount, world.TransferLog.Count);
    }
}
