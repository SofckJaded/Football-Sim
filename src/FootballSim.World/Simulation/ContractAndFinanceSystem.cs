using FootballSim.World.Core;
using FootballSim.World.Players;
using FootballSim.World.World;

namespace FootballSim.World.Simulation;

/// <summary>
/// One week of club cash flow (plan stage W3): the money every club earned, the wages it paid, the net
/// change and how many clubs ended the week in debt.
/// </summary>
/// <param name="Income">Total income credited to all clubs this week, in whole currency units.</param>
/// <param name="WagesPaid">Total wages debited from all clubs this week, in whole currency units.</param>
/// <param name="NetChange">Income minus wages: the pyramid's net cash flow for the week.</param>
/// <param name="ClubsInDebt">Clubs whose balance is negative after the week (debt is allowed; see the plan).</param>
public sealed record FinanceWeekResult(long Income, long WagesPaid, long NetChange, int ClubsInDebt);

/// <summary>
/// The season's end-of-season payout run: prize money credited from the final league tables.
///
/// <para>TV money is already paid weekly by <see cref="ContractAndFinanceSystem.AdvanceWeek"/> in v1, so
/// <see cref="TotalTvMoney"/> is always 0. It stays in the result so a later stage can add a lump-sum TV
/// deal without changing the method signature.</para>
/// </summary>
/// <param name="TotalPrizeMoney">Prize money credited to all clubs, in whole currency units.</param>
/// <param name="TotalTvMoney">Lump-sum TV money credited; 0 in v1 (TV is weekly).</param>
public sealed record SeasonPayoutResult(long TotalPrizeMoney, long TotalTvMoney);

/// <summary>What the off-season contract/loan rollover changed.</summary>
/// <param name="Renewals">Expiring contracts that were extended.</param>
/// <param name="ReleasedToFreeAgency">Expiring contracts that were not renewed (players became free agents).</param>
/// <param name="LoansReturned">Loans that ended and returned the player to his parent club.</param>
public sealed record ContractRolloverResult(int Renewals, int ReleasedToFreeAgency, int LoansReturned);

/// <summary>
/// Player contracts, club money and the weekly/seasonal finance tick (plan §5.4, stage W3).
///
/// <para><b>What this system owns.</b> The valuation and wage formulae (<see cref="MarketValue"/>,
/// <see cref="WageDemand"/>), contract creation and expiry (<see cref="NewContract"/>,
/// <see cref="InitializeContracts"/>, <see cref="AssignYouthContracts"/>,
/// <see cref="ProcessExpiriesAndLoans"/>), the weekly cash tick (<see cref="AdvanceWeek"/>), the season
/// payout (<see cref="PaySeasonPayouts"/>) and the budget refresh every club reads
/// (<see cref="RefreshBudgets"/>). It never stores money or contracts itself: balances live on
/// <see cref="ClubFinances"/> and terms live on <see cref="PlayerContract"/>, so a save carries both.</para>
///
/// <para><b>Determinism.</b> Every method mutates only the world it is given. Randomness is used only where
/// an explicit <see cref="WorldRng"/> parameter exists, and every collection is walked in the world's stable
/// registry order, so replaying the same calls against two identical worlds produces identical results.</para>
///
/// <para><b>Money.</b> All amounts are whole <see cref="long"/> currency units. A negative club balance is
/// debt, which the plan permits: the weekly tick never refuses to pay wages, it only records the debt.</para>
/// </summary>
public static class ContractAndFinanceSystem
{
    /// <summary>Clubs per division in the v1 pyramid (six divisions of eighteen).</summary>
    private const int ClubsPerTier = 18;

    /// <summary>Divisions in the v1 pyramid, and the valid tier range.</summary>
    private const int TierCount = 6;

    /// <summary>
    /// Age multiplier applied to <see cref="MarketValue"/>: young players carry resale and growth value,
    /// players past 30 lose it quickly (plan §5.4 "market valuation formula (OVR, Age, Form, Tier)").
    /// </summary>
    private static readonly double[] AgeFactorByBucket = { 1.35, 1.25, 1.15, 1.00, 0.75, 0.45, 0.20 };

    /// <summary>
    /// Tier multiplier applied to <see cref="MarketValue"/>, indexed by tier: a top-flight player is worth
    /// roughly 2,600x a Division 6 amateur of the same ability (the tiers are separate markets).
    /// </summary>
    private static readonly double[] TierMarketFactor = { 0.0, 260.0, 90.0, 30.0, 8.0, 1.2, 0.1 };

    /// <summary>
    /// Tier multiplier applied to <see cref="WageDemand"/>: wages fall much faster than fees down the
    /// pyramid because the lower tiers are semi-professional/amateur (plan §5.1 transfer rules).
    /// </summary>
    private static readonly double[] TierWageFactor = { 0.0, 1.0, 0.5, 0.18, 0.045, 0.006, 0.0 };

    /// <summary>Weekly TV money per division (the plan's "TV money by tier"); 0 for an unknown tier.</summary>
    private static readonly long[] WeeklyTvMoney = { 0L, 620_000L, 340_000L, 150_000L, 40_000L, 8_000L, 1_000L };

    /// <summary>Average ticket price per division, in whole currency units.</summary>
    private static readonly long[] TicketPrice = { 0L, 26L, 18L, 12L, 8L, 5L, 3L };

    /// <summary>
    /// Typical attendance as a share of stadium capacity per division: the top flight sells out more often
    /// than an amateur ground, so matchday income has a second tier gradient on top of the ticket price.
    /// </summary>
    private static readonly double[] AttendanceFactor = { 0.0, 0.92, 0.85, 0.75, 0.62, 0.48, 0.36 };

    /// <summary>Base transfer budget per division before the 10% positive-balance share is added.</summary>
    private static readonly long[] TransferBudgetBase =
        { 0L, 25_000_000L, 8_000_000L, 2_000_000L, 350_000L, 20_000L, 0L };

    /// <summary>Base weekly wage budget per division before the reputation scaling is applied.</summary>
    private static readonly long[] WeeklyWageBudgetBase =
        { 0L, 2_500_000L, 1_200_000L, 500_000L, 180_000L, 40_000L, 8_000L };

    /// <summary>Prize money for the champion of each division (position 1).</summary>
    private static readonly long[] PrizeMoneyMaximum =
        { 0L, 20_000_000L, 6_000_000L, 1_500_000L, 300_000L, 40_000L, 5_000L };

    /// <summary>Prize money for the bottom club of each division (position 18).</summary>
    private static readonly long[] PrizeMoneyMinimum =
        { 0L, 1_000_000L, 600_000L, 200_000L, 40_000L, 5_000L, 500L };

    /// <summary>Weekly sponsorship base every club receives before reputation and tier scaling.</summary>
    private const long SponsorshipBase = 4_000L;

    /// <summary>Sponsorship per reputation point (a famous club sells a better commercial deal).</summary>
    private const long SponsorshipPerReputation = 900L;

    /// <summary>Sponsorship tier bonus: <c>40_000 / tier</c>, so the top flight earns the largest deal.</summary>
    private const long SponsorshipTierBonus = 40_000L;

    /// <summary>Lower bound of the renewal probability (a club basically never gives up a nailed-on player).</summary>
    private const float RenewalProbabilityFloor = 0.10f;

    /// <summary>Upper bound of the renewal probability (even a model professional can be released).</summary>
    private const float RenewalProbabilityCeiling = 0.95f;

    // ---------------------------------------------------------------------------------------------
    // Valuation and wages.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Estimated market value of a player, in whole currency units (plan §5.4).
    ///
    /// <para>Formula: <c>(max(1, ability - 25) ^ 3) * ageFactor * tierFactor</c>, where ability is the mean
    /// of the eighteen current attributes, the age factor is the plan's age curve and the tier factor is the
    /// division market multiplier. The cubic ability term makes a star worth orders of magnitude more than a
    /// squad filler; the result is clamped to zero.</para>
    ///
    /// <para><b>Division source.</b> The API deliberately takes no world, so the division is inferred from
    /// the player's club id: <see cref="WorldGenerator"/> allocates club ids tier by tier (eighteen per
    /// tier), so <c>(ClubId - 1) / 18 + 1</c> is the club's division. A free agent is valued at the bottom
    /// division factor. After promotion/relegation a club's id keeps its founding tier, so this is the
    /// player's "market tier" rather than his current division; that keeps valuation a pure function of the
    /// player, which is what the transfer window and career layer need.</para>
    /// </summary>
    /// <param name="player">The player to value.</param>
    /// <param name="currentSeason">The world season the valuation is for (reserved for a future
    /// contract-remaining factor; the v1 formula does not need it).</param>
    /// <returns>A non-negative value in whole currency units.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> is null.</exception>
    public static long MarketValue(WorldPlayer player, int currentSeason)
    {
        ArgumentNullException.ThrowIfNull(player);

        float ability = player.Attributes.MeanCurrent();
        double ageFactor = AgeFactorFor(player.Age);
        double tierFactor = TierMarketFactor[TierOfPlayer(player)];
        double value = Math.Pow(Math.Max(1.0, ability - 25.0), 3.0) * ageFactor * tierFactor;
        return value <= 0.0 ? 0L : (long)value;
    }

    /// <summary>
    /// Weekly wage a player demands from a club in <paramref name="tier"/>, in whole currency units
    /// (plan §5.4). Formula: <c>(max(1, ability - 30) ^ 2.2) * 8 * tierWageFactor</c>, rounded to the
    /// nearest ten and floored at zero. Ability is the mean current attribute; the tier factor is 1.0 in the
    /// top flight and falls to 0.006 in Division 5. Division 6 is amateur: the demand is always 0.
    /// </summary>
    /// <param name="player">The player whose wage is wanted.</param>
    /// <param name="tier">The bidding club's division (1..6; values outside are clamped into range).</param>
    /// <returns>A non-negative weekly wage, always 0 for tier 6.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> is null.</exception>
    public static long WageDemand(WorldPlayer player, int tier)
    {
        ArgumentNullException.ThrowIfNull(player);

        int clampedTier = Math.Clamp(tier, 1, TierCount);
        if (clampedTier == TierCount) return 0L; // amateur football is unpaid

        float ability = player.Attributes.MeanCurrent();
        double demand = Math.Pow(Math.Max(1.0, ability - 30.0), 2.2) * 8.0 * TierWageFactor[clampedTier];
        double rounded = Math.Round(demand / 10.0, MidpointRounding.AwayFromZero) * 10.0;
        return rounded <= 0.0 ? 0L : (long)rounded;
    }

    /// <summary>
    /// Creates (or renews) a player's contract at a club and returns the mutated contract.
    ///
    /// <para>Length: when <paramref name="seasons"/> is supplied it is used as-is; otherwise the player's
    /// age picks the band — 21 and under: 3–4 seasons, 22–27: 2–4, 28–31: 1–3, 32+: 1–2 — and one draw is
    /// taken from <paramref name="rng"/>. The wage is always <see cref="WageDemand"/> for the club's tier,
    /// and the loan flags are cleared because a new contract is a permanent registration.</para>
    /// </summary>
    /// <param name="world">The world supplying the current season.</param>
    /// <param name="player">The player signing.</param>
    /// <param name="club">The club he signs for.</param>
    /// <param name="rng">Contract stream used when the length is drawn.</param>
    /// <param name="seasons">Explicit contract length in seasons (1 or more), or null to draw it by age.</param>
    /// <returns>The player's mutated <see cref="PlayerContract"/>.</returns>
    /// <exception cref="ArgumentNullException">Any reference argument is null.</exception>
    public static PlayerContract NewContract(
        WorldState world, WorldPlayer player, WorldClub club, WorldRng rng, int? seasons = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(club);
        ArgumentNullException.ThrowIfNull(rng);

        int length = seasons ?? DrawContractSeasons(player.Age, rng);
        if (length < 1) length = 1;

        PlayerContract contract = player.Contract;
        contract.SignedSeason = world.Season;
        contract.EndSeason = world.Season + length - 1;
        contract.WeeklyWage = WageDemand(player, club.Tier);
        contract.IsOnLoan = false;
        contract.ParentClubId = ClubId.None;
        contract.LoanEndSeason = 0;
        return contract;
    }

    /// <summary>
    /// Gives every player who currently belongs to a club a full contract, then refreshes the club budgets
    /// and the weekly wage bills (plan stage W3 opening state).
    ///
    /// <para>This is the one-time bridge from the W2 "generated squads with default contracts" state to the
    /// living economy: after it returns, every squad player has a wage and an expiry, and every club's
    /// <see cref="ClubFinances.WeeklyWageBill"/> is the exact sum of its squad's contracts.</para>
    /// </summary>
    /// <param name="world">The world to initialise.</param>
    /// <param name="rng">Contract stream used for the per-player length draws.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static void InitializeContracts(WorldState world, WorldRng rng)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(rng);

        foreach (WorldPlayer player in world.Players.All)
        {
            if (!player.ClubId.IsValid) continue;
            if (!world.Clubs.TryGet(player.ClubId, out WorldClub? club) || club is null) continue;
            NewContract(world, player, club, rng);
        }

        RefreshBudgets(world);
        foreach (WorldClub club in world.Clubs.All)
        {
            club.Finances.WeeklyWageBill = SquadWageBill(world, club);
        }
    }

    /// <summary>
    /// Assigns short youth-team contracts (2–3 seasons) to newly created players. The wage is the
    /// <see cref="WageDemand"/> for the club's tier and the contract is signed in the current season, exactly
    /// like an academy graduate (plan §5.2 "annual youth intakes").
    /// </summary>
    /// <param name="world">The world the players belong to.</param>
    /// <param name="playerIds">The newly created players, in the order they should be processed.</param>
    /// <param name="rng">Contract stream used for the 2–3 season length draw.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="KeyNotFoundException">An id is not registered in the world.</exception>
    public static void AssignYouthContracts(WorldState world, IReadOnlyList<PlayerId> playerIds, WorldRng rng)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(playerIds);
        ArgumentNullException.ThrowIfNull(rng);

        var touchedClubs = new List<ClubId>(playerIds.Count);
        foreach (PlayerId playerId in playerIds)
        {
            WorldPlayer player = world.Players[playerId];
            if (!player.ClubId.IsValid ||
                !world.Clubs.TryGet(player.ClubId, out WorldClub? club) || club is null)
            {
                // A youth intake is always placed at a club before contracts are assigned; the guard keeps a
                // hand-built world valid instead of throwing on a player who has not been attached yet.
                ResetToAmateurContract(player, world.Season);
                continue;
            }

            int seasons = 2 + rng.NextInt(0, 2); // 2 or 3 seasons
            NewContract(world, player, club, rng, seasons);
            if (!touchedClubs.Contains(club.Id)) touchedClubs.Add(club.Id);
        }

        for (int i = 0; i < touchedClubs.Count; i++)
        {
            WorldClub club = world.Clubs[touchedClubs[i]];
            club.Finances.WeeklyWageBill = SquadWageBill(world, club);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Weekly cash tick and season payouts.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs one weekly finance tick for every club and returns the pyramid's totals.
    ///
    /// <para><b>Income</b> (credited to <see cref="ClubFinances.Balance"/>):</para>
    /// <list type="bullet">
    /// <item>Sponsorship: <c>4_000 + 900 * reputation + 40_000 / tier</c> per week — documented tiered
    /// scaling of the plan's commercial income.</item>
    /// <item>TV: a fixed weekly amount by division (tier 1: 620,000 down to tier 6: 1,000).</item>
    /// <item>Matchday: only in a week the club is at home in a league fixture or a drawn cup tie
    /// (<c>ticket price[division] * capacity * attendance factor[division]</c> per home match). The fixture
    /// does not have to be unplayed: the finance hook runs after the week's results, and the gate looks at
    /// <c>Week == world.Week && HomeClubId == club.Id</c> exactly as the plan states.</item>
    /// </list>
    ///
    /// <para><b>Wages</b>: the sum of every squad member's <see cref="PlayerContract.WeeklyWage"/>, paid
    /// unconditionally — a club may go (or stay) negative, which the plan explicitly allows. The club's
    /// <see cref="ClubFinances.WeeklyWageBill"/> is rewritten to that sum.</para>
    ///
    /// <para>The tick is deterministic and consumes no randomness.</para>
    /// </summary>
    /// <param name="world">The world to tick.</param>
    /// <returns>The week's totals and the number of clubs in debt afterwards.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    public static FinanceWeekResult AdvanceWeek(WorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);

        int week = world.Week;
        int[] homeMatches = CountHomeMatches(world, week);

        long totalIncome = 0L;
        long totalWages = 0L;
        int clubsInDebt = 0;

        foreach (WorldClub club in world.Clubs.All)
        {
            int tier = Math.Clamp(club.Tier, 1, TierCount);
            long income = WeeklyIncome(club, tier);

            int homeCount = club.Id.Value > 0 && club.Id.Value < homeMatches.Length
                ? homeMatches[club.Id.Value]
                : 0;
            if (homeCount > 0) income += MatchdayIncome(club, tier) * homeCount;

            long wages = SquadWageBill(world, club);
            club.Finances.WeeklyWageBill = wages;
            club.Finances.Balance += income - wages;

            totalIncome += income;
            totalWages += wages;
            if (club.Finances.Balance < 0L) clubsInDebt++;
        }

        return new FinanceWeekResult(totalIncome, totalWages, totalIncome - totalWages, clubsInDebt);
    }

    /// <summary>
    /// Credits end-of-season prize money from the resolved final tables (plan §5.2 awards/expiry phase).
    ///
    /// <para>Prize money falls linearly by final position inside each division, from the division's
    /// documented maximum (champion) to its minimum (18th): tier 1 20M..1M, tier 2 6M..0.6M, tier 3
    /// 1.5M..0.2M, tier 4 300k..40k, tier 5 40k..5k, tier 6 5k..500. The tier comes from
    /// <see cref="SeasonOutcome.TablesByTier"/>, i.e. the division the club actually played in.</para>
    ///
    /// <para>TV money is not paid here: v1 pays it weekly in <see cref="AdvanceWeek"/>, so
    /// <see cref="SeasonPayoutResult.TotalTvMoney"/> stays 0.</para>
    /// </summary>
    /// <param name="world">The world whose club balances are credited.</param>
    /// <param name="outcome">The resolved season (final tables, tier 1 first).</param>
    /// <returns>The total prize money credited and the (zero) lump-sum TV money.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static SeasonPayoutResult PaySeasonPayouts(WorldState world, SeasonOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(outcome);

        long totalPrizeMoney = 0L;
        for (int tierIndex = 0; tierIndex < outcome.TablesByTier.Count; tierIndex++)
        {
            IReadOnlyList<LeagueTableRow> table = outcome.TablesByTier[tierIndex];
            int tier = Math.Clamp(tierIndex + 1, 1, TierCount);

            for (int positionIndex = 0; positionIndex < table.Count; positionIndex++)
            {
                long prize = PrizeMoneyFor(tier, positionIndex, table.Count);
                if (prize <= 0L) continue;

                WorldClub club = world.Clubs[table[positionIndex].ClubId];
                club.Finances.Balance += prize;
                totalPrizeMoney += prize;
            }
        }

        return new SeasonPayoutResult(totalPrizeMoney, 0L);
    }

    // ---------------------------------------------------------------------------------------------
    // Off-season rollover.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs the off-season contract and loan rollover, after promotion/relegation has been applied.
    ///
    /// <para><b>Loans first</b>: a player whose <see cref="PlayerContract.LoanExpiresAfter"/> reports true is
    /// removed from his loan club and returned to <see cref="PlayerContract.ParentClubId"/> when that club
    /// still exists; otherwise he becomes a free agent. The loan flags are cleared and a
    /// <see cref="TransferKind.LoanReturn"/> entry is appended to the world log.</para>
    ///
    /// <para><b>Expiries</b>: a non-loan contract that expires after this season is renewed with probability
    /// <c>p = 0.75 - 0.04 * (age - 24) + 0.25 * (ability - 55) / 45</c> (clamped to 0.10..0.95) for 1–3
    /// seasons; a club that is already over its weekly wage budget always releases instead. A released
    /// player leaves the squad, becomes a free agent, drops to an amateur contract and is logged as
    /// <see cref="TransferKind.Released"/>. A player whose club no longer exists is treated as released
    /// without a log entry (he has nobody to release him).</para>
    ///
    /// <para>Afterwards every club's wage bill is recomputed and <see cref="RefreshBudgets"/> refreshes the
    /// transfer and wage budgets, so the next season starts from a consistent money state.</para>
    /// </summary>
    /// <param name="world">The world to roll over.</param>
    /// <param name="rng">Contract stream used for renewal rolls and the 1–3 season renewal length.</param>
    /// <returns>Renewals, releases and loans returned.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static ContractRolloverResult ProcessExpiriesAndLoans(WorldState world, WorldRng rng)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(rng);

        int season = world.Season;
        int renewals = 0;
        int released = 0;
        int loansReturned = 0;

        // 1. Loans are settled first: the loan club loses the registration before expiries are evaluated,
        //    so a returned player can still be renewed by his parent club in the same rollover.
        foreach (WorldPlayer player in world.Players.All)
        {
            if (!player.Contract.IsOnLoan || !player.Contract.LoanExpiresAfter(season)) continue;

            ClubId loanClubId = player.ClubId;
            if (loanClubId.IsValid && world.Clubs.TryGet(loanClubId, out WorldClub? loanClub) && loanClub is not null)
            {
                loanClub.RemovePlayer(player.Id);
            }

            ClubId parentClubId = player.Contract.ParentClubId;
            if (parentClubId.IsValid &&
                world.Clubs.TryGet(parentClubId, out WorldClub? parentClub) && parentClub is not null)
            {
                player.ClubId = parentClubId;
                parentClub.AddPlayer(player.Id);
            }
            else
            {
                player.ClubId = ClubId.None;
            }

            player.Contract.IsOnLoan = false;
            player.Contract.ParentClubId = ClubId.None;
            player.Contract.LoanEndSeason = 0;

            world.RecordTransfer(new TransferRecord(
                season, world.Week, player.Id, loanClubId, player.ClubId,
                TransferKind.LoanReturn, 0L));
            loansReturned++;
        }

        // 2. Expiring permanent contracts: renew or release.
        foreach (WorldPlayer player in world.Players.All)
        {
            if (player.Contract.IsOnLoan) continue;
            if (!player.Contract.ExpiresAfter(season)) continue;

            ClubId clubId = player.ClubId;
            if (!clubId.IsValid || !world.Clubs.TryGet(clubId, out WorldClub? club) || club is null)
            {
                ResetToAmateurContract(player, season);
                player.ClubId = ClubId.None;
                continue;
            }

            bool overWageBudget = club.Finances.WeeklyWageBill > club.Finances.WeeklyWageBudget;
            bool renew = !overWageBudget && rng.NextBool(RenewalProbability(player));

            if (renew)
            {
                int seasons = rng.NextInt(1, 4); // 1..3 seasons
                NewContract(world, player, club, rng, seasons);
                renewals++;
                continue;
            }

            club.RemovePlayer(player.Id);
            player.ClubId = ClubId.None;
            ResetToAmateurContract(player, season);
            world.RecordTransfer(new TransferRecord(
                season, world.Week, player.Id, clubId, ClubId.None, TransferKind.Released, 0L));
            released++;
        }

        foreach (WorldClub club in world.Clubs.All)
        {
            club.Finances.WeeklyWageBill = SquadWageBill(world, club);
        }

        RefreshBudgets(world);
        return new ContractRolloverResult(renewals, released, loansReturned);
    }

    /// <summary>
    /// Refreshes every club's transfer budget, weekly wage budget and weekly wage bill.
    ///
    /// <para>Transfer budget: the division base (tier 1: 25M, tier 2: 8M, tier 3: 2M, tier 4: 350k, tier 5:
    /// 20k, tier 6: 0) plus 10% of a positive balance, clamped to non-negative. Weekly wage budget: the
    /// division base scaled by reputation (<c>0.60 + 0.80 * reputation / 100</c>). The wage bill is always
    /// recomputed from the current squad so budget checks see the contracts that actually exist.</para>
    /// </summary>
    /// <param name="world">The world to refresh.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    public static void RefreshBudgets(WorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);

        foreach (WorldClub club in world.Clubs.All)
        {
            int tier = Math.Clamp(club.Tier, 1, TierCount);

            long positiveBalance = Math.Max(0L, club.Finances.Balance);
            long transferBudget = TransferBudgetBase[tier] + positiveBalance / 10L;
            club.Finances.TransferBudget = Math.Max(0L, transferBudget);

            int reputation = Math.Clamp(club.Reputation, 0, 100);
            double reputationFactor = 0.60 + 0.80 * (reputation / 100.0);
            long wageBudget = (long)Math.Round(
                WeeklyWageBudgetBase[tier] * reputationFactor, MidpointRounding.AwayFromZero);
            club.Finances.WeeklyWageBudget = Math.Max(1L, wageBudget);

            club.Finances.WeeklyWageBill = SquadWageBill(world, club);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Internals.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Age bucket multiplier of the plan's valuation curve.</summary>
    private static double AgeFactorFor(int age) => age switch
    {
        <= 18 => AgeFactorByBucket[0],
        <= 21 => AgeFactorByBucket[1],
        <= 24 => AgeFactorByBucket[2],
        <= 27 => AgeFactorByBucket[3],
        <= 30 => AgeFactorByBucket[4],
        <= 33 => AgeFactorByBucket[5],
        _ => AgeFactorByBucket[6]
    };

    /// <summary>
    /// Market tier used by <see cref="MarketValue"/>: club ids are allocated tier by tier by the generator,
    /// eighteen per division; a free agent is valued at the bottom division.
    /// </summary>
    private static int TierOfPlayer(WorldPlayer player)
    {
        if (!player.ClubId.IsValid) return TierCount;
        int tier = (player.ClubId.Value - 1) / ClubsPerTier + 1;
        return Math.Clamp(tier, 1, TierCount);
    }

    /// <summary>Draws a contract length from the plan's age bands.</summary>
    private static int DrawContractSeasons(int age, WorldRng rng) => age switch
    {
        <= 21 => 3 + rng.NextInt(0, 2),   // 3 or 4 seasons for a young player
        <= 27 => 2 + rng.NextInt(0, 3),   // 2..4
        <= 31 => 1 + rng.NextInt(0, 3),   // 1..3
        _ => 1 + rng.NextInt(0, 2)        // 1 or 2 at the end of a career
    };

    /// <summary>Renewal probability for one expiring player, clamped to the documented floor/ceiling.</summary>
    private static float RenewalProbability(WorldPlayer player)
    {
        float ability = player.Attributes.MeanCurrent();
        float probability = 0.75f - 0.04f * (player.Age - 24) + 0.25f * ((ability - 55f) / 45f);
        return Math.Clamp(probability, RenewalProbabilityFloor, RenewalProbabilityCeiling);
    }

    /// <summary>Rewrites a player's contract to the default amateur terms (no wage, one season, no loan).</summary>
    private static void ResetToAmateurContract(WorldPlayer player, int season)
    {
        PlayerContract contract = player.Contract;
        contract.SignedSeason = season;
        contract.EndSeason = season;
        contract.WeeklyWage = 0L;
        contract.IsOnLoan = false;
        contract.ParentClubId = ClubId.None;
        contract.LoanEndSeason = 0;
    }

    /// <summary>Weekly wage bill of a club: the sum of its squad's contracts (retired players excluded).</summary>
    private static long SquadWageBill(WorldState world, WorldClub club)
    {
        long total = 0L;
        IReadOnlyList<PlayerId> squad = club.Squad;
        for (int i = 0; i < squad.Count; i++)
        {
            if (!world.Players.TryGet(squad[i], out WorldPlayer? player) || player is null) continue;
            if (player.IsRetired) continue;
            total += player.Contract.WeeklyWage;
        }

        return total;
    }

    /// <summary>Non-matchday weekly income: sponsorship plus the division's TV money.</summary>
    private static long WeeklyIncome(WorldClub club, int tier)
    {
        int reputation = Math.Clamp(club.Reputation, 0, 100);
        long sponsorship = SponsorshipBase + SponsorshipPerReputation * reputation + SponsorshipTierBonus / tier;
        return sponsorship + WeeklyTvMoney[tier];
    }

    /// <summary>Matchday ticket income for one home match: price x capacity x attendance factor.</summary>
    private static long MatchdayIncome(WorldClub club, int tier)
    {
        long gate = (long)(TicketPrice[tier] * (double)club.Capacity * AttendanceFactor[tier]);
        return gate < 0L ? 0L : gate;
    }

    /// <summary>
    /// Counts, per club id, how many home matches (league fixture or drawn cup tie) fall in
    /// <paramref name="week"/> and the current season — the "is this club at home?" test of the weekly tick.
    /// The season filters are defensive: the rollover normally clears and regenerates a season's fixtures, and
    /// counting a stale previous-season fixture would pay a second gate for the same calendar week.
    /// </summary>
    private static int[] CountHomeMatches(WorldState world, int week)
    {
        var counts = new int[world.Clubs.Count + 1];
        int season = world.Season;

        foreach (WorldLeague league in world.Leagues)
        {
            IReadOnlyList<WorldFixture> fixtures = league.Fixtures;
            for (int i = 0; i < fixtures.Count; i++)
            {
                WorldFixture fixture = fixtures[i];
                if (fixture.Week != week || fixture.Season != season) continue;
                IncrementHome(counts, fixture.HomeClubId);
            }
        }

        if (world.Cup.Season == season)
        {
            foreach (CupRoundBracket round in world.Cup.Rounds)
            {
                IReadOnlyList<CupTie> ties = round.Ties;
                for (int i = 0; i < ties.Count; i++)
                {
                    WorldFixture fixture = ties[i].Fixture;
                    if (fixture.Week != week) continue;
                    IncrementHome(counts, fixture.HomeClubId);
                }
            }
        }

        return counts;
    }

    /// <summary>Increments one club's home-match count, tolerating hand-built ids outside the array.</summary>
    private static void IncrementHome(int[] counts, ClubId clubId)
    {
        if (clubId.Value > 0 && clubId.Value < counts.Length) counts[clubId.Value]++;
    }

    /// <summary>
    /// Prize money for one final position: a linear ramp from the division's champion maximum down to the
    /// bottom-club minimum. <paramref name="positionIndex"/> is zero-based; a table that is not the contract
    /// eighteen rows still produces a sensible ramp.
    /// </summary>
    private static long PrizeMoneyFor(int tier, int positionIndex, int tableCount)
    {
        long maximum = PrizeMoneyMaximum[tier];
        long minimum = PrizeMoneyMinimum[tier];
        if (tableCount <= 1) return maximum;

        long span = tableCount - 1;
        long drop = (maximum - minimum) * positionIndex / span;
        return Math.Max(minimum, maximum - drop);
    }
}
