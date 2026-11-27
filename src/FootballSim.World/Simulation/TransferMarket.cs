using FootballSim.World.Core;
using FootballSim.World.Players;
using FootballSim.World.World;

namespace FootballSim.World.Simulation;

/// <summary>Lifecycle state of a transfer offer.</summary>
public enum TransferOfferStatus
{
    /// <summary>The offer is live and waiting for a response.</summary>
    Pending = 0,

    /// <summary>The offer was accepted (the move happened or is being applied).</summary>
    Accepted = 1,

    /// <summary>The offer was declined by the player.</summary>
    Declined = 2,

    /// <summary>The offer's response window passed without an answer.</summary>
    Expired = 3
}

/// <summary>
/// One offer for a player, delivered to the human career layer (stage W4) and generated deterministically by
/// <see cref="TransferMarket.GenerateOffersForPlayer"/>.
///
/// <para>An offer is a self-contained record: the buyer, the fee and wage, the reason (permanent or free) and
/// when it expires. It never references a mutable club or player, so it can sit in an inbox, or be serialised
/// into a save, without keeping the world alive.</para>
/// </summary>
public sealed class TransferOffer
{
    /// <summary>Stable offer id inside the offer batch that produced it (one-based, in club-id order).</summary>
    public int Id { get; init; }

    /// <summary>The player the offer is for.</summary>
    public PlayerId PlayerId { get; init; }

    /// <summary>The club the player would leave, or <see cref="ClubId.None"/> when he is a free agent.</summary>
    public ClubId FromClubId { get; init; }

    /// <summary>The club making the offer.</summary>
    public ClubId ToClubId { get; init; }

    /// <summary>The transfer fee the buyer would pay, in whole currency units (0 for a free agent).</summary>
    public long Fee { get; init; }

    /// <summary>The weekly wage the buyer would pay.</summary>
    public long WeeklyWage { get; init; }

    /// <summary>Why the move would happen (permanent transfer or free move).</summary>
    public TransferKind Kind { get; init; }

    /// <summary>Season the offer was created in.</summary>
    public int CreatedSeason { get; init; }

    /// <summary>Week the offer was created in.</summary>
    public int CreatedWeek { get; init; }

    /// <summary>Last week (inside <see cref="CreatedSeason"/>) the offer can still be answered.</summary>
    public int ExpiresWeek { get; init; }

    /// <summary>Live status; the career layer mutates this when the player answers.</summary>
    public TransferOfferStatus Status { get; set; } = TransferOfferStatus.Pending;

    /// <summary>
    /// Whether the offer has run out of time at the given calendar position. An offer expires after its
    /// <see cref="ExpiresWeek"/> in <see cref="CreatedSeason"/> and immediately once a later season starts;
    /// offers already marked <see cref="TransferOfferStatus.Expired"/> report true regardless.
    /// </summary>
    /// <param name="season">Current world season.</param>
    /// <param name="week">Current world week (1..52).</param>
    /// <returns>True when the offer can no longer be answered.</returns>
    public bool HasExpired(int season, int week)
    {
        if (Status == TransferOfferStatus.Expired) return true;
        if (season > CreatedSeason) return true;
        return season == CreatedSeason && week > ExpiresWeek;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"#{Id} {Kind}: {PlayerId} {FromClubId}->{ToClubId} fee {Fee} wage {WeeklyWage}/w " +
        $"(S{CreatedSeason}W{CreatedWeek:00}, expires W{ExpiresWeek:00})";
}

/// <summary>What one <see cref="TransferMarket.RunWindow"/> call did.</summary>
/// <param name="PermanentTransfers">Permanent moves for a fee.</param>
/// <param name="FreeTransfers">Free moves (free agents and amateur signings).</param>
/// <param name="Loans">Loan moves completed.</param>
/// <param name="TotalFees">Total fees paid by buyers in this window call.</param>
/// <param name="ClubsActive">Clubs that completed at least one action (buyer for a signing, parent for a loan).</param>
public sealed record TransferWindowResult(
    int PermanentTransfers, int FreeTransfers, int Loans, long TotalFees, int ClubsActive);

/// <summary>
/// The AI transfer and loan market for the living world (plan §5.4 "Transfers, Loans &amp; Amateur Free
/// Movement", stage W3).
///
/// <para><b>Volume control.</b> <see cref="RunWindow"/> is called once per week while a transfer window is
/// open. Each club acts at most once per call, so a week can produce at most one move per club and the world
/// never churns hundreds of players in a single tick. Clubs without a positional shortfall mostly sit the
/// window out (only a 20% roll makes them act), which keeps the market focused on genuine squad needs.</para>
///
/// <para><b>Tier rules (plan §5.1).</b> Tiers 5–6 are amateur: they only sign free agents and never pay a
/// fee. Tier 4 is semi-professional and may pay up to 500,000. Tiers 1–3 may pay full fees subject to their
/// transfer budget. Tier 1–3 clubs may send one 18–21-year-old reserve (fewer than 8 appearances this
/// season) on loan to a tier 4–6 club that needs his area; the loan ends at the end of the current season and
/// the player returns in the off-season rollover.</para>
///
/// <para><b>Determinism and money.</b> Every decision walks the world in stable id/registry order and draws
/// only from the supplied <see cref="WorldRng"/>; the market mutates the world (squads, contracts, balances,
/// the transfer log) and nothing else. Every completed move is appended to
/// <see cref="WorldState.TransferLog"/>, which the soak test audits.</para>
/// </summary>
public static class TransferMarket
{
    /// <summary>Share of a buyer's transfer budget one fee signing may consume.</summary>
    private const double MaximumFeeShareOfTransferBudget = 0.35;

    /// <summary>Semi-professional fee ceiling for a tier 4 buyer (plan §5.1).</summary>
    private const long TierFourFeeCeiling = 500_000L;

    /// <summary>Oldest free agent the market signs (a club rebuilds through youth, not pensioners).</summary>
    private const int MaximumFreeAgentAge = 30;

    /// <summary>Minimum area score a free agent must have to be worth a squad place.</summary>
    private const float MinimumFreeAgentAreaScore = 45f;

    /// <summary>Safety valve on squad growth: clubs stop adding bodies at this size.</summary>
    private const int MaximumSquadSize = 30;

    /// <summary>A club with this many players is happy to sell (squad-depth rule of the seller test).</summary>
    private const int SellerSquadDepthThreshold = 23;

    /// <summary>The seller asks for this multiple of the market value before a "normal" sale is accepted.</summary>
    private const double SellerAcceptPricePremium = 1.05;

    /// <summary>Chance that a club without a positional shortfall still works the market in a given week.</summary>
    private const float ShortfallFreeActionChance = 0.20f;

    /// <summary>Youngest player eligible for an outbound development loan.</summary>
    private const int LoanMinimumAge = 18;

    /// <summary>Oldest player eligible for an outbound development loan.</summary>
    private const int LoanMaximumAge = 21;

    /// <summary>Eight or more appearances this season means a reserve is too established to send out on loan.</summary>
    private const int LoanMaximumAppearances = 8;

    /// <summary>Weeks an offer stays open from creation (clamped to the end of the season).</summary>
    private const int OfferLifetimeWeeks = 4;

    /// <summary>Highest supported tier (the amateur division).</summary>
    private const int TierCount = 6;

    // ---------------------------------------------------------------------------------------------
    // Calendar and valuation.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Whether the transfer window is open in the given week: weeks 1–4 and 22–25 (§5.2).</summary>
    /// <param name="week">World week (1..52).</param>
    /// <returns>True inside a transfer window.</returns>
    public static bool IsWindowOpen(int week) => WorldCalendar.IsTransferWindowOpen(week);

    /// <summary>The fee the market values a player at: <see cref="ContractAndFinanceSystem.MarketValue"/>.</summary>
    /// <param name="player">The player to value.</param>
    /// <param name="currentSeason">The world season the valuation is for.</param>
    /// <returns>The non-negative market value in whole currency units.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> is null.</exception>
    public static long FeeFor(WorldPlayer player, int currentSeason) =>
        ContractAndFinanceSystem.MarketValue(player, currentSeason);

    // ---------------------------------------------------------------------------------------------
    // The weekly AI window.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Works the transfer market once for every club (called once per week inside an open window).
    ///
    /// <para>Clubs are visited in club-id order and each club performs at most one action:</para>
    /// <list type="number">
    /// <item>A club with no positional shortfall only acts on a 20% roll.</item>
    /// <item>It first tries a free agent (no club, not retired, at most 30, area score at least 45, wage
    /// inside the wage budget) — an <see cref="TransferKind.Free"/> move.</item>
    /// <item>Otherwise it works its scouting shortlist: the first target whose fee is at most 35% of its
    /// transfer budget, whose wage fits, and whom the seller accepts (a 5% premium over market value, an
    /// expiring contract at 29+, or a squad deeper than 23) is bought for a
    /// <see cref="TransferKind.Permanent"/> fee drawn from 90–110% of market value.</item>
    /// <item>Tier 1–3 clubs that have not acted yet may instead send one young reserve (18–21, fewer than 8
    /// appearances) on loan to a tier 4–6 club with a shortfall in his area; at most one loan is completed
    /// per call.</item>
    /// </list>
    ///
    /// <para>Tier 5–6 clubs never pay fees and tier 4 clubs never pay more than 500,000 (also enforced by
    /// <see cref="ExecuteTransfer"/>).</para>
    /// </summary>
    /// <param name="world">The world whose clubs, squads and money change.</param>
    /// <param name="rng">Transfer stream used for the shortfall roll, fee variation and club selection.</param>
    /// <returns>What the call moved.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static TransferWindowResult RunWindow(WorldState world, WorldRng rng)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(rng);

        ClubId[] clubOrder = OrderedClubIds(world);
        var active = new bool[world.Clubs.Count + 1];

        int permanentTransfers = 0;
        int freeTransfers = 0;
        int loans = 0;
        long totalFees = 0L;
        bool loanUsed = false;

        for (int index = 0; index < clubOrder.Length; index++)
        {
            WorldClub club = world.Clubs[clubOrder[index]];
            RosterGroup? shortfall = RosterAnalyser.FindShortfall(world, club);

            // Volume control: a squad with no hole in it mostly ignores the window.
            if (shortfall is null && !rng.NextBool(ShortfallFreeActionChance)) continue;

            if (TrySignFreeAgent(world, club, shortfall))
            {
                freeTransfers++;
                active[club.Id.Value] = true;
                continue;
            }

            if (club.Tier <= 4 && TryBuyPlayer(world, club, rng, out long feePaid))
            {
                permanentTransfers++;
                totalFees += feePaid;
                active[club.Id.Value] = true;
                continue;
            }

            if (!loanUsed && club.Tier <= 3 && TryLoanOut(world, club))
            {
                loans++;
                loanUsed = true;
                active[club.Id.Value] = true;
            }
        }

        int clubsActive = 0;
        for (int i = 0; i < active.Length; i++)
        {
            if (active[i]) clubsActive++;
        }

        return new TransferWindowResult(permanentTransfers, freeTransfers, loans, totalFees, clubsActive);
    }

    // ---------------------------------------------------------------------------------------------
    // Transfer and loan execution.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Executes one permanent or free move: removes the player from his old squad, registers him at the buyer,
    /// writes a fresh contract, moves the fee between the two balances/budgets, recomputes both wage bills and
    /// appends a <see cref="TransferRecord"/> to the world log.
    ///
    /// <para><b>Refusals return false, unknown ids throw.</b> The normal refusals are: a negative or
    /// unaffordable fee, a fee from an amateur (tier 5–6) buyer, a tier 4 fee above 500,000, a retired or
    /// on-loan player, moving a player to the club he is already at, or a kind that is not
    /// <see cref="TransferKind.Permanent"/>/<see cref="TransferKind.Free"/>.</para>
    ///
    /// <para>The new contract is produced by
    /// <see cref="ContractAndFinanceSystem.NewContract(WorldState, WorldPlayer, WorldClub, WorldRng, int?)"/>
    /// over <paramref name="contractSeasons"/> seasons; the negotiated <paramref name="weeklyWage"/> overrides
    /// the formula demand when it is positive (a zero leaves the formula value in place).</para>
    /// </summary>
    /// <param name="world">The world to mutate.</param>
    /// <param name="playerId">The player to move.</param>
    /// <param name="toClubId">The buying club.</param>
    /// <param name="fee">Fee paid by the buyer (0 for a free move).</param>
    /// <param name="weeklyWage">Negotiated weekly wage; 0 keeps the formula demand.</param>
    /// <param name="kind">The log kind: <see cref="TransferKind.Permanent"/> or <see cref="TransferKind.Free"/>.</param>
    /// <param name="contractSeasons">Contract length in seasons (at least 1).</param>
    /// <returns>True when the move happened; false for a normal refusal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    /// <exception cref="KeyNotFoundException">The player or club id is not registered.</exception>
    public static bool ExecuteTransfer(
        WorldState world,
        PlayerId playerId,
        ClubId toClubId,
        long fee,
        long weeklyWage,
        TransferKind kind,
        int contractSeasons = 2)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (!world.Players.TryGet(playerId, out WorldPlayer? player) || player is null)
        {
            throw new KeyNotFoundException($"No player with id {playerId}.");
        }

        if (!world.Clubs.TryGet(toClubId, out WorldClub? toClub) || toClub is null)
        {
            throw new KeyNotFoundException($"No club with id {toClubId}.");
        }

        if (fee < 0L) return false;
        if (kind is not (TransferKind.Permanent or TransferKind.Free)) return false;
        if (player.IsRetired || player.Contract.IsOnLoan) return false;

        int buyerTier = Math.Clamp(toClub.Tier, 1, TierCount);
        if (fee > 0L && buyerTier >= 5) return false;             // amateur clubs pay no fees
        if (buyerTier == 4 && fee > TierFourFeeCeiling) return false; // semi-pro fee ceiling

        if (fee > 0L && (fee > toClub.Finances.TransferBudget || fee > toClub.Finances.Balance))
        {
            return false;
        }

        ClubId fromClubId = player.ClubId;
        if (fromClubId == toClubId) return false;

        WorldClub? fromClub = null;
        if (fromClubId.IsValid && (!world.Clubs.TryGet(fromClubId, out fromClub) || fromClub is null))
        {
            return false;
        }

        // A fee needs a seller to receive it; a free agent signs for nothing.
        if (fromClub is null && fee > 0L) return false;

        if (contractSeasons < 1) contractSeasons = 1;

        fromClub?.RemovePlayer(playerId);
        player.ClubId = toClubId;
        toClub.AddPlayer(playerId);

        PlayerContract contract = ContractAndFinanceSystem.NewContract(
            world, player, toClub, world.Stream(WorldRandomStream.Contracts), contractSeasons);
        if (weeklyWage > 0L) contract.WeeklyWage = weeklyWage;

        toClub.Finances.Balance -= fee;
        toClub.Finances.TransferBudget = Math.Max(0L, toClub.Finances.TransferBudget - fee);
        if (fromClub is not null)
        {
            fromClub.Finances.Balance += fee;
            fromClub.Finances.TransferBudget += fee;
        }

        toClub.Finances.WeeklyWageBill = WageBillOf(world, toClub);
        if (fromClub is not null) fromClub.Finances.WeeklyWageBill = WageBillOf(world, fromClub);

        world.RecordTransfer(new TransferRecord(
            world.Season, world.Week, playerId, fromClubId, toClubId, kind, fee));
        return true;
    }

    /// <summary>
    /// Executes one loan move: registration moves to the loan club, the player's contract is flagged as a loan
    /// with the old club recorded as parent and the given end season, both wage bills are recomputed and a
    /// <see cref="TransferKind.Loan"/> entry is appended to the world log.
    ///
    /// <para>The loaning club keeps paying the player's existing contract wage in v1 (the plan models loans as
    /// development moves, not as wage transfers). The move is refused when the player is retired, already on
    /// loan, has no club, is moving to his own club, or when <paramref name="endSeason"/> is before the current
    /// season.</para>
    /// </summary>
    /// <param name="world">The world to mutate.</param>
    /// <param name="playerId">The player to loan.</param>
    /// <param name="toClubId">The club taking him on loan.</param>
    /// <param name="endSeason">Season after which the loan ends (the off-season rollover returns him).</param>
    /// <returns>True when the loan happened; false for a normal refusal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    /// <exception cref="KeyNotFoundException">The player or club id is not registered.</exception>
    public static bool ExecuteLoan(WorldState world, PlayerId playerId, ClubId toClubId, int endSeason)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (!world.Players.TryGet(playerId, out WorldPlayer? player) || player is null)
        {
            throw new KeyNotFoundException($"No player with id {playerId}.");
        }

        if (!world.Clubs.TryGet(toClubId, out WorldClub? toClub) || toClub is null)
        {
            throw new KeyNotFoundException($"No club with id {toClubId}.");
        }

        if (endSeason < world.Season) return false;
        if (player.IsRetired || player.Contract.IsOnLoan) return false;
        if (!player.ClubId.IsValid || player.ClubId == toClubId) return false;
        if (!world.Clubs.TryGet(player.ClubId, out WorldClub? fromClub) || fromClub is null) return false;
        if (fromClub.Id == toClub.Id) return false;

        fromClub.RemovePlayer(playerId);
        player.ClubId = toClubId;
        toClub.AddPlayer(playerId);

        player.Contract.IsOnLoan = true;
        player.Contract.ParentClubId = fromClub.Id;
        player.Contract.LoanEndSeason = endSeason;

        fromClub.Finances.WeeklyWageBill = WageBillOf(world, fromClub);
        toClub.Finances.WeeklyWageBill = WageBillOf(world, toClub);

        world.RecordTransfer(new TransferRecord(
            world.Season, world.Week, playerId, fromClub.Id, toClubId, TransferKind.Loan, 0L));
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Human career offers (stage W4 input).
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Generates the offers the human career layer shows for one player: 2–4 clubs whose division is within
    /// one tier of his club (any tier when he is a free agent) and whose positional shortfall matches his
    /// area, with the fee, wage and expiry a real bid would carry.
    ///
    /// <para>Deterministic for a given world position and <paramref name="rng"/> state; the list is sorted by
    /// buyer club id and the offer ids are one-based in that order. The method reads the world and consumes
    /// only <paramref name="rng"/> draws — it never mutates the world.</para>
    /// </summary>
    /// <param name="world">The world to read.</param>
    /// <param name="playerId">The player the offers are for.</param>
    /// <param name="rng">Career stream used to pick how many clubs show interest and which ones.</param>
    /// <returns>The offers; empty when no club qualifies or the player has retired.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="KeyNotFoundException">The player id is not registered.</exception>
    public static IReadOnlyList<TransferOffer> GenerateOffersForPlayer(
        WorldState world, PlayerId playerId, WorldRng rng)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(rng);

        WorldPlayer player = world.Players[playerId];
        if (player.IsRetired) return Array.Empty<TransferOffer>();

        bool isFreeAgent = !player.ClubId.IsValid;
        int playerTier = TierCount;
        if (!isFreeAgent && world.Clubs.TryGet(player.ClubId, out WorldClub? currentClub) && currentClub is not null)
        {
            playerTier = Math.Clamp(currentClub.Tier, 1, TierCount);
        }

        RosterGroup area = RosterAnalyser.GroupOf(player);
        var eligible = new List<ClubId>();

        foreach (WorldClub club in world.Clubs.All)
        {
            if (club.Id == player.ClubId) continue;
            if (!isFreeAgent && Math.Abs(club.Tier - playerTier) > 1) continue;
            RosterGroup? shortfall = RosterAnalyser.FindShortfall(world, club);
            if (shortfall is null || shortfall.Value != area) continue;
            eligible.Add(club.Id);
        }

        if (eligible.Count == 0) return Array.Empty<TransferOffer>();

        int wanted = 2 + rng.NextInt(0, 3); // 2..4 interested clubs
        int offerCount = Math.Min(wanted, eligible.Count);
        rng.Shuffle(eligible);
        eligible.RemoveRange(offerCount, eligible.Count - offerCount);
        eligible.Sort(static (left, right) => left.Value.CompareTo(right.Value));

        long fee = FeeFor(player, world.Season);
        int expiresWeek = Math.Min(WorldCalendar.WeeksPerSeason, world.Week + OfferLifetimeWeeks);

        var offers = new List<TransferOffer>(offerCount);
        for (int i = 0; i < eligible.Count; i++)
        {
            WorldClub buyer = world.Clubs[eligible[i]];
            offers.Add(new TransferOffer
            {
                Id = i + 1,
                PlayerId = playerId,
                FromClubId = player.ClubId,
                ToClubId = buyer.Id,
                Fee = fee,
                WeeklyWage = ContractAndFinanceSystem.WageDemand(player, Math.Clamp(buyer.Tier, 1, TierCount)),
                Kind = isFreeAgent ? TransferKind.Free : TransferKind.Permanent,
                CreatedSeason = world.Season,
                CreatedWeek = world.Week,
                ExpiresWeek = expiresWeek,
                Status = TransferOfferStatus.Pending
            });
        }

        return offers;
    }

    // ---------------------------------------------------------------------------------------------
    // RunWindow steps.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Step 2 of the window: signs the first eligible free agent in registry order. Eligibility is: no club,
    /// not retired, at most 30, an area score of at least 45 in the buyer's shortfall area (or in the player's
    /// own best area when the buyer has no shortfall) and a wage that fits the wage budget. The club must also
    /// be under the squad-size safety valve.
    /// </summary>
    private static bool TrySignFreeAgent(WorldState world, WorldClub club, RosterGroup? shortfall)
    {
        if (club.Squad.Count >= MaximumSquadSize) return false;

        foreach (WorldPlayer player in world.Players.All)
        {
            if (player.IsRetired || player.Contract.IsOnLoan) continue;
            if (player.ClubId.IsValid) continue;
            if (player.Age > MaximumFreeAgentAge) continue;
            // A frontend-managed career player only moves through his own inbox offers (W4).
            if (world.IsExternallyManaged(player.Id)) continue;

            RosterGroup area = shortfall ?? BestAreaOf(player);
            if (RosterAnalyser.AreaScore(player, area) < MinimumFreeAgentAreaScore) continue;

            long wage = ContractAndFinanceSystem.WageDemand(player, Math.Clamp(club.Tier, 1, TierCount));
            if (club.Finances.WeeklyWageBill + wage > club.Finances.WeeklyWageBudget) continue;

            return ExecuteTransfer(world, player.Id, club.Id, 0L, wage, TransferKind.Free);
        }

        return false;
    }

    /// <summary>
    /// Step 3 of the window: walks the buyer's scouting shortlist and buys the first target whose fee fits
    /// 35% of the transfer budget, whose wage fits, and whom the seller accepts. Sets
    /// <paramref name="feePaid"/> to the fee of the completed move.
    /// </summary>
    private static bool TryBuyPlayer(WorldState world, WorldClub club, WorldRng rng, out long feePaid)
    {
        feePaid = 0L;

        IReadOnlyList<ScoutingTarget> targets = ScoutingSystem.FindTargets(world, club);
        if (targets.Count == 0) return false;

        long feeLimit = (long)(club.Finances.TransferBudget * MaximumFeeShareOfTransferBudget);
        long wageBudget = club.Finances.WeeklyWageBudget;

        for (int i = 0; i < targets.Count; i++)
        {
            ScoutingTarget target = targets[i];
            if (!world.Players.TryGet(target.PlayerId, out WorldPlayer? player) || player is null) continue;
            if (!player.ClubId.IsValid || player.ClubId != target.ClubId) continue;
            if (player.Contract.IsOnLoan || player.IsRetired) continue;
            // A frontend-managed career player only moves through his own inbox offers (W4).
            if (world.IsExternallyManaged(player.Id)) continue;
            if (!world.Clubs.TryGet(target.ClubId, out WorldClub? seller) || seller is null) continue;
            if (seller.Id == club.Id) continue;

            long marketValue = FeeFor(player, world.Season);
            if (marketValue <= 0L) continue;

            double variation = rng.NextRange(0.9f, 1.1f);
            long fee = (long)Math.Round(
                marketValue * variation, MidpointRounding.AwayFromZero);
            if (fee < 0L || fee > feeLimit) continue;
            if (club.Tier == 4 && fee > TierFourFeeCeiling) continue;

            long wage = ContractAndFinanceSystem.WageDemand(player, Math.Clamp(club.Tier, 1, TierCount));
            if (club.Finances.WeeklyWageBill + wage > wageBudget) continue;

            bool sellerAccepts = fee >= marketValue * SellerAcceptPricePremium ||
                player.Age >= 29 ||
                seller.Squad.Count > SellerSquadDepthThreshold;
            if (!sellerAccepts) continue;

            if (ExecuteTransfer(world, player.Id, club.Id, fee, wage, TransferKind.Permanent))
            {
                feePaid = fee;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Step 5 of the window: sends one young reserve on a development loan to the first tier 4–6 club (in
    /// club-id order) whose positional shortfall is the player's area and whose wage budget can carry the
    /// player's existing contract.
    /// </summary>
    private static bool TryLoanOut(WorldState world, WorldClub club)
    {
        IReadOnlyList<PlayerId> squad = club.Squad;
        for (int i = 0; i < squad.Count; i++)
        {
            if (!world.Players.TryGet(squad[i], out WorldPlayer? player) || player is null) continue;
            if (player.IsRetired || player.Contract.IsOnLoan) continue;
            if (player.Age is < LoanMinimumAge or > LoanMaximumAge) continue;
            if (player.CurrentSeason.Appearances >= LoanMaximumAppearances) continue;
            // A frontend-managed career player only moves through his own inbox offers (W4).
            if (world.IsExternallyManaged(player.Id)) continue;

            RosterGroup playerArea = RosterAnalyser.GroupOf(player);
            ClubId receiverId = FindLoanReceiver(world, player, playerArea);
            if (!receiverId.IsValid) continue;

            if (ExecuteLoan(world, player.Id, receiverId, world.Season)) return true;
        }

        return false;
    }

    /// <summary>First tier 4–6 club (in club-id order) that needs the player's area and can pay his wage.</summary>
    private static ClubId FindLoanReceiver(WorldState world, WorldPlayer player, RosterGroup playerArea)
    {
        ClubId[] order = OrderedClubIds(world);
        for (int i = 0; i < order.Length; i++)
        {
            WorldClub receiver = world.Clubs[order[i]];
            if (receiver.Tier is < 4 or > TierCount) continue;
            if (receiver.Squad.Count >= MaximumSquadSize) continue;
            if (receiver.Finances.WeeklyWageBill + player.Contract.WeeklyWage >
                receiver.Finances.WeeklyWageBudget)
            {
                continue;
            }

            RosterGroup? shortfall = RosterAnalyser.FindShortfall(world, receiver);
            if (shortfall is null || shortfall.Value != playerArea) continue;
            return receiver.Id;
        }

        return ClubId.None;
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Club ids in ascending id order (the market's documented iteration order).</summary>
    private static ClubId[] OrderedClubIds(WorldState world)
    {
        var order = new ClubId[world.Clubs.Count];
        IReadOnlyList<WorldClub> clubs = world.Clubs.All;
        for (int i = 0; i < clubs.Count; i++) order[i] = clubs[i].Id;
        Array.Sort(order, static (left, right) => left.Value.CompareTo(right.Value));
        return order;
    }

    /// <summary>The player's strongest broad area (used when the buyer has no positional shortfall).</summary>
    private static RosterGroup BestAreaOf(WorldPlayer player)
    {
        RosterGroup best = RosterGroup.Forward;
        float bestScore = float.NegativeInfinity;

        RosterGroup[] groups =
            { RosterGroup.Goalkeeper, RosterGroup.Defender, RosterGroup.Midfielder, RosterGroup.Forward };
        for (int i = 0; i < groups.Length; i++)
        {
            float score = RosterAnalyser.AreaScore(player, groups[i]);
            if (score > bestScore)
            {
                bestScore = score;
                best = groups[i];
            }
        }

        return best;
    }

    /// <summary>Weekly wage bill of a club: the sum of its squad's contracts (retired players excluded).</summary>
    private static long WageBillOf(WorldState world, WorldClub club)
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
}
