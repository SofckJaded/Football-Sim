using System;
using System.Collections.Generic;
using FootballSim.Core;
using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Players;
using FootballSim.World.World;

namespace FootballSim.World.Simulation;

/// <summary>
/// One off-season rollover's results (plan W3 world-health telemetry).
///
/// <para>The summary is a snapshot, not a second source of truth: every number is measured from the mutated
/// <see cref="WorldState"/> after <see cref="WorldEcosystem.Rollover"/> has run, so the soak test and a UI
/// read exactly the world a career continues from.</para>
/// </summary>
public sealed class SeasonRolloverSummary
{
    /// <summary>The finished season number the rollover closed (the world date is already season + 1).</summary>
    public int Season { get; init; }

    /// <summary>Players that retired in this off-season (plan §5.2).</summary>
    public int Retirements { get; init; }

    /// <summary>Players created by this off-season's youth intakes (plan §5.2).</summary>
    public int YouthIntakes { get; init; }

    /// <summary>Active (not retired) population after the rollover.</summary>
    public int Population { get; init; }

    /// <summary>Mean age of the active population after the rollover.</summary>
    public double MeanAge { get; init; }

    /// <summary>Tier-1 mean squad overall after the rollover (see <see cref="WorldEcosystem.TierMeanOverall"/>).</summary>
    public float Tier1MeanOverall { get; init; }

    /// <summary>Tier-2 mean squad overall after the rollover.</summary>
    public float Tier2MeanOverall { get; init; }

    /// <summary>Tier-3 mean squad overall after the rollover.</summary>
    public float Tier3MeanOverall { get; init; }

    /// <summary>Tier-4 mean squad overall after the rollover.</summary>
    public float Tier4MeanOverall { get; init; }

    /// <summary>Tier-5 mean squad overall after the rollover.</summary>
    public float Tier5MeanOverall { get; init; }

    /// <summary>Tier-6 mean squad overall after the rollover.</summary>
    public float Tier6MeanOverall { get; init; }

    /// <summary>
    /// Transfer-market moves (log entries of kind <see cref="TransferKind.Permanent"/>,
    /// <see cref="TransferKind.Free"/> or <see cref="TransferKind.Loan"/>) added since the previous rollover,
    /// i.e. the finished season's window activity. Youth intakes, retirements and releases are excluded.
    /// </summary>
    public int Transfers { get; init; }

    /// <summary>Manager dismissals plus hirings this carousel (equal in v1, where every sacking hires).</summary>
    public int ManagerChanges { get; init; }

    /// <summary>Injuries the weekly club-medical extra-recovery tick healed over the finished season.</summary>
    public int InjuriesRecovered { get; init; }

    /// <summary>
    /// The finished season's division movements: automatic promotions/relegations first, then the
    /// promotion/relegation play-off swaps, in the order the season runner applied them.
    /// </summary>
    public IReadOnlyList<DivisionMove> DivisionMoves { get; init; } = Array.Empty<DivisionMove>();
}

/// <summary>
/// The ecosystem's save-state: the private counters a reloaded session must continue from. Everything else
/// the ecosystem owns is derived from the world (transfer log, squads, contracts) or rebuilt on demand (the
/// cached name-uniqueness set).
/// </summary>
/// <param name="Initialized">True once the opening contracts were created for the world.</param>
/// <param name="TransferLogCursor">Transfer-log length at the previous rollover (season-move counting).</param>
/// <param name="InjuriesRecoveredSinceRollover">Weekly medical recoveries accumulated since the last rollover.</param>
/// <param name="EmergencySequence">Monotonic emergency-intake counter used for deterministic generation forks.</param>
public readonly record struct WorldEcosystemState(
    bool Initialized,
    int TransferLogCursor,
    int InjuriesRecoveredSinceRollover,
    int EmergencySequence);

/// <summary>
/// The living world's season loop (plan stage W3): the weekly ecosystem hook the season runner calls inside
/// its week, plus the full off-season rollover that closes a season and opens the next one.
///
/// <para><b>What it owns.</b> It is the composition root for the five W3 systems, not a sixth system: it
/// orders <see cref="ContractAndFinanceSystem"/>, <see cref="LifecycleSystem"/>, <see cref="ScoutingSystem"/>,
/// <see cref="TransferMarket"/> and <see cref="ManagerCarousel"/> around the match/growth spine
/// (<see cref="WorldSeasonRunner"/>) and records the resulting world-health telemetry. Every mutation still
/// lives in those systems and on the one mutable <see cref="WorldState"/>.</para>
///
/// <para><b>Determinism.</b> Every random draw comes from the world's named streams
/// (<see cref="WorldRandomStream.Contracts"/>, <see cref="WorldRandomStream.Scouting"/>,
/// <see cref="WorldRandomStream.Transfers"/>, <see cref="WorldRandomStream.Injuries"/>,
/// <see cref="WorldRandomStream.Regens"/>, <see cref="WorldRandomStream.Managers"/>,
/// <see cref="WorldRandomStream.Fixtures"/> and <see cref="WorldRandomStream.Cup"/>), every collection is
/// walked in registry order and the weekly work never touches the match streams
/// (<see cref="WorldRandomStream.QuickSim"/>, <see cref="WorldRandomStream.Growth"/>) the runner owns. Two
/// worlds with the same seed therefore replay identical seasons and identical rollovers.</para>
///
/// <para><b>Calendar ordering inside the rollover.</b> The world date is advanced to the new season's
/// pre-season week 1 <i>before</i> the next season's fixtures and cup draw are created, because
/// <see cref="ScheduleGenerator.GenerateLeagueSeason"/> and <see cref="CupScheduler.StartSeason"/> stamp
/// <see cref="WorldState.Season"/> on every fixture they create. Stamping the finished season would hide the
/// new season's home matches from the finance system's matchday-income filter. The observable postcondition
/// is exactly the documented one: the rollover returns with the calendar at <c>season + 1, week 1</c>.</para>
///
/// <para><b>Contract-length reconciliation.</b> The contract system stamps a new deal's
/// <see cref="PlayerContract.SignedSeason"/> with <see cref="WorldState.Season"/>; because the rollover runs
/// before the calendar advances, every deal it signs during the rollover would otherwise cover one season
/// fewer than the length it drew. The rollover re-stamps and extends exactly the youth contracts and the
/// renewals it just signed, so a 1-3 season renewal and a 2-3 season youth deal last their drawn length.</para>
///
/// <para><b>v1 safety valve.</b> Real long-run worlds can leave a club short of playable bodies (releases,
/// retirements and injuries stack up). <see cref="Rollover"/> therefore tops up any club below eighteen
/// registered / sixteen available players with emergency 16-17-year-old academy players generated through
/// the same <see cref="PlayerGenerator.GenerateNpcForTier"/> path the annual youth intake uses, and
/// <see cref="OnWeekStart"/> keeps every club at the twenty available players a matchday squad and its
/// injury buffer need. This is a documented v1 robustness valve: without it a season could become
/// unplayable, and it can be removed once squad-building and availability are modelled more deeply.</para>
/// </summary>
public sealed class WorldEcosystem : IWorldWeekSystem
{
    /// <summary>Divisions in the v1 pyramid; also the valid tier range for <see cref="TierMeanOverall"/>.</summary>
    private const int TierCount = 6;

    /// <summary>Clubs per division in the v1 pyramid (a published season table has this many rows).</summary>
    private const int ClubsPerLeague = 18;

    /// <summary>Registered squad size below which the rollover tops a club up (v1 safety valve).</summary>
    private const int MinimumRegisteredSquad = 18;

    /// <summary>Available (fit, unsuspended, active) players below which the rollover tops a club up.</summary>
    private const int MinimumAvailablePlayers = 16;

    /// <summary>
    /// Available players a club must have before a matchday. <see cref="SquadSelector.Select"/> needs a
    /// starting XI plus a seven-player bench (18 bodies), so the weekly valve keeps two more available as
    /// an in-week injury buffer (a cup week plays a league match and a tie back to back).
    /// </summary>
    private const int MinimumMatchdayAvailablePlayers = 20;

    /// <summary>Youngest emergency-intake age (same band as the annual youth intake).</summary>
    private const int EmergencyIntakeMinimumAge = 16;

    /// <summary>Oldest emergency-intake age.</summary>
    private const int EmergencyIntakeMaximumAge = 17;

    /// <summary>Ordinal base of the emergency-intake player-generation forks (keeps them clear of the lifecycle's).</summary>
    private const ulong EmergencySeedOrdinalBase = 500_000UL;

    /// <summary>Multiplier that folds the club id into the emergency-intake fork ordinal.</summary>
    private const ulong EmergencySeedClubMultiplier = 31UL;

    /// <summary>Name-pair draws tried before the numbered-suffix uniqueness fallback.</summary>
    private const int MaximumNameAttempts = 25;

    /// <summary>Vocabulary size of <see cref="RosterGroup"/>; used by the deterministic area cycle.</summary>
    private const int RosterGroupCount = 4;

    /// <summary>Defensive guard against an endless top-up loop on a broken world.</summary>
    private const int MaximumEmergencyIntakesPerClub = 32;

    private readonly WorldState _world;

    /// <summary>True once <see cref="EnsureInitialized"/> has given a generated world its opening contracts.</summary>
    private bool _initialized;

    /// <summary>Transfer-log length at the previous rollover (the cursor for the season's market-move count).</summary>
    private int _transferLogCursor;

    /// <summary>Club-medical injury recoveries since the previous rollover (summed over the weekly ticks).</summary>
    private int _injuriesRecoveredSinceRollover;

    /// <summary>Cached world-unique player names for emergency intakes; null when it must be rebuilt.</summary>
    private HashSet<string>? _usedNames;

    /// <summary>Monotonic emergency-intake counter: keeps every emergency player's generation fork distinct.</summary>
    private int _emergencySequence;

    /// <summary>Creates the season loop for one world. Construction mutates nothing.</summary>
    /// <param name="world">The world the ecosystem operates on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    public WorldEcosystem(WorldState world) =>
        _world = world ?? throw new ArgumentNullException(nameof(world));

    /// <summary>The world this ecosystem operates on.</summary>
    public WorldState World => _world;

    // ---------------------------------------------------------------------------------------------
    // Opening state.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Gives a freshly generated world its opening contracts (the W2 generator deliberately leaves every
    /// contract at its default lifetime 0). Idempotent: a world whose squad players already carry a contract
    /// is left exactly as it is, and repeated calls are safe.
    ///
    /// <para>A generated world has no wages, so this is also the one-time bridge that fills every club's
    /// <see cref="ClubFinances.WeeklyWageBill"/> and refreshes its budgets before the first weekly tick.</para>
    /// </summary>
    public void EnsureInitialized()
    {
        if (_initialized) return;

        bool needsContracts = false;
        foreach (WorldPlayer player in _world.Players.All)
        {
            if (!player.ClubId.IsValid) continue;
            if (player.Contract.EndSeason != 0) continue;
            needsContracts = true;
            break;
        }

        if (needsContracts)
        {
            ContractAndFinanceSystem.InitializeContracts(_world, _world.Stream(WorldRandomStream.Contracts));
        }

        _initialized = true;
    }

    /// <summary>
    /// Captures the ecosystem counters for save/load (W4). A reloaded session constructs a fresh ecosystem and
    /// restores this state before its next week, so emergency-intake generation and rollover telemetry
    /// continue identically.
    /// </summary>
    public WorldEcosystemState CaptureState() =>
        new(_initialized, _transferLogCursor, _injuriesRecoveredSinceRollover, _emergencySequence);

    /// <summary>
    /// Restores the state captured by <see cref="CaptureState"/>. Call it before the next weekly tick.
    /// </summary>
    /// <param name="state">The captured state.</param>
    public void RestoreState(WorldEcosystemState state)
    {
        _initialized = state.Initialized;
        _transferLogCursor = state.TransferLogCursor;
        _injuriesRecoveredSinceRollover = state.InjuriesRecoveredSinceRollover;
        _emergencySequence = state.EmergencySequence;
        _usedNames = null;
    }

    // ---------------------------------------------------------------------------------------------
    // IWorldWeekSystem: the weekly ecosystem hook.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs the weekly ecosystem work before the week's player ticks and fixtures: the club cash flow, the
    /// club-medical injury recovery on top of the runner's guaranteed base countdown, the weekly visibility
    /// update, one pass of the AI transfer market inside a transfer window, and the matchday-availability
    /// safety valve.
    ///
    /// <para>The order is part of the contract: finance books the week's income/wages from the scheduled
    /// fixtures, medical recovery happens before selection, scouting visibility reflects the latest form, the
    /// market sees the freshest visibility before it bids, and the valve repairs any squad the market (or an
    /// injury wave) left short before the matches are selected.</para>
    /// </summary>
    /// <param name="world">The world being played (the runner's world).</param>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    public void OnWeekStart(WorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);

        ContractAndFinanceSystem.AdvanceWeek(world);

        InjuryTickResult injuries = LifecycleSystem.AdvanceInjuries(world);
        _injuriesRecoveredSinceRollover += injuries.Recovered;

        ScoutingSystem.AdvanceWeek(world, world.Stream(WorldRandomStream.Scouting));

        if (TransferMarket.IsWindowOpen(world.Week))
        {
            TransferMarket.RunWindow(world, world.Stream(WorldRandomStream.Transfers));
        }

        // The market can sell a club down to an unplayable squad (and a transfer window opens right before
        // the week's matches), so the matchday-availability valve runs after it and before selection.
        EnsureMatchdayAvailability(world);
    }

    /// <summary>
    /// End-of-week extension point; the W3 pipeline deliberately has no after-the-results work yet (the next
    /// week-start hook sees the finished week's state), so this is a no-op.
    /// </summary>
    /// <param name="world">The world that just finished its week.</param>
    /// <param name="report">The played week's report.</param>
    public void OnWeekEnd(WorldState world, WeekReport report)
    {
        // Extension point only: the W3 systems run at week start, before selection.
    }

    // ---------------------------------------------------------------------------------------------
    // Season loop.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Plays one whole season through the ecosystem: ensures the opening contracts exist, then runs
    /// <see cref="WorldSeasonRunner.PlaySeason"/> with this instance as its week hook, so the weekly ecosystem
    /// work happens inside the season loop rather than being bolted on afterwards.
    /// </summary>
    /// <returns>The resolved season (final tables, champions, cup winner, totals and division moves).</returns>
    public SeasonSummary PlaySeason()
    {
        EnsureInitialized();
        return new WorldSeasonRunner(_world, this).PlaySeason();
    }

    /// <summary>
    /// Closes the finished season and opens the next one (plan §5.2 off-season rollover). See
    /// <see cref="Rollover"/> for the ordered steps.
    /// </summary>
    /// <param name="rollover">The rollover telemetry for the season that just finished.</param>
    /// <returns>The finished season's resolved summary.</returns>
    public SeasonSummary PlaySeasonWithRollover(out SeasonRolloverSummary rollover)
    {
        SeasonSummary summary = PlaySeason();
        rollover = Rollover(summary);
        return summary;
    }

    /// <summary>
    /// The full off-season rollover, in the plan's documented order: season payouts, cup giant-killing
    /// visibility spikes, aging/retirements/youth intakes, youth contracts, contract expiries and loan
    /// returns, the manager carousel, the squad-playability safety valve, the season-stat archive, next
    /// season's fixtures and cup draw, budgets and the calendar.
    ///
    /// <para><b>Preconditions.</b> The competitive weeks must be over and the season must already have been
    /// resolved with promotion/relegation applied (which is what
    /// <see cref="WorldSeasonRunner.PlaySeason"/> leaves behind at week 46). The finished-tables summary is
    /// used as the resolved <see cref="SeasonOutcome"/>: re-deriving it from
    /// <see cref="WorldState.Leagues"/> is not possible at this point, because the promotion/relegation
    /// rollover has already replaced each league's club list with the next season's composition while the
    /// played fixtures still reference the old one.</para>
    ///
    /// <para><b>Telemetry.</b> The returned summary is measured after every step, so its population, age and
    /// tier-overall values describe the world the next season starts from.</para>
    /// </summary>
    /// <param name="finishedSeason">The summary returned by the season runner for the season being closed.</param>
    /// <returns>The off-season rollover telemetry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="finishedSeason"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The competitive season is not over yet.</exception>
    public SeasonRolloverSummary Rollover(SeasonSummary finishedSeason)
    {
        ArgumentNullException.ThrowIfNull(finishedSeason);

        if (_world.Week <= WorldCalendar.LeagueSecondHalfLastWeek)
        {
            throw new InvalidOperationException(
                "Rollover requires the competitive weeks to be over " +
                $"(week > {WorldCalendar.LeagueSecondHalfLastWeek}); the world is at {_world.Date}.");
        }

        EnsureInitialized();

        int finishedSeasonNumber = finishedSeason.Season > 0 ? finishedSeason.Season : _world.Season;
        int nextSeason = finishedSeasonNumber + 1;

        // Snapshot for the division-move fallback (used when the caller did not supply the applied moves).
        var tiersBefore = new int[_world.Clubs.Count];
        for (int index = 0; index < tiersBefore.Length; index++)
        {
            tiersBefore[index] = _world.Clubs.All[index].Tier;
        }

        // 1. Resolve the finished season from the published final tables (see the preconditions remark).
        SeasonOutcome outcome = RebuildOutcome(finishedSeason);

        // 2. End-of-season prize money from the final positions.
        ContractAndFinanceSystem.PaySeasonPayouts(_world, outcome);

        // 3. The cup's giant-killing spotlight: cup ties still hold this season's results.
        ScoutingSystem.ApplyCupGiantKillingSpikes(_world);

        // 4. Aging, retirements and the annual youth intake.
        LifecycleRolloverResult life =
            LifecycleSystem.ProcessOffSeason(_world, _world.Stream(WorldRandomStream.Regens));
        _usedNames = null; // the lifecycle just added names; the cached uniqueness set is stale

        // 5. Proper youth contracts for the intake (2-3 seasons at the club's wage demand).
        var intakeIds = new List<PlayerId>(life.Intakes.Count);
        for (int index = 0; index < life.Intakes.Count; index++)
        {
            intakeIds.Add(life.Intakes[index].PlayerId);
        }

        ContractAndFinanceSystem.AssignYouthContracts(
            _world, intakeIds, _world.Stream(WorldRandomStream.Contracts));
        ReconcileYouthContractLengths(intakeIds, nextSeason);

        // 6. Contract expiries (renew/release) and loan returns.
        int[] endSeasonBefore = SnapshotContractEndSeasons();
        ContractAndFinanceSystem.ProcessExpiriesAndLoans(
            _world, _world.Stream(WorldRandomStream.Contracts));
        ReconcileRenewalLengths(finishedSeasonNumber, nextSeason, endSeasonBefore);

        // 7. Board review and the manager carousel.
        ManagerCarousel.UpdateBoardConfidence(_world, outcome);
        ManagerCarouselResult carousel =
            ManagerCarousel.Process(_world, outcome, _world.Stream(WorldRandomStream.Managers));

        // 8. v1 safety valve: no club starts the new season unable to field a matchday squad.
        EnsurePlayableSquads(finishedSeasonNumber, nextSeason);

        // 9. Archive the finished season's stats and reset the live counters for the new season.
        ArchiveSeasonStats(_world, finishedSeasonNumber, nextSeason);

        // 10. Fresh manager match records (the carousel already advanced SeasonsAtClub).
        ResetManagerSeasonRecords();

        // 11. Next season's fixtures and cup: the calendar moves first so every new fixture is stamped with
        //     the season it belongs to (see the class remarks).
        _world.Date = new WorldDate(nextSeason, WorldCalendar.PreSeasonFirstWeek);
        GenerateNextSeasonFixtures(nextSeason);

        // 12. Budgets from the new-season balances and contracts.
        ContractAndFinanceSystem.RefreshBudgets(_world);

        // 13. Telemetry measured on the world the new season starts from.
        IReadOnlyList<DivisionMove> divisionMoves = finishedSeason.DivisionMoves.Count > 0
            ? finishedSeason.DivisionMoves
            : BuildDivisionMoves(tiersBefore);

        int transfers = CountTransferMarketMovesSinceRollover();

        var summary = new SeasonRolloverSummary
        {
            Season = finishedSeasonNumber,
            Retirements = life.Retirements,
            YouthIntakes = life.YouthIntakes,
            Population = ActivePopulation(),
            MeanAge = MeanActiveAge(),
            Tier1MeanOverall = TierMeanOverall(1),
            Tier2MeanOverall = TierMeanOverall(2),
            Tier3MeanOverall = TierMeanOverall(3),
            Tier4MeanOverall = TierMeanOverall(4),
            Tier5MeanOverall = TierMeanOverall(5),
            Tier6MeanOverall = TierMeanOverall(6),
            Transfers = transfers,
            ManagerChanges = carousel.Sacked + carousel.Hired,
            InjuriesRecovered = _injuriesRecoveredSinceRollover,
            DivisionMoves = divisionMoves
        };

        _injuriesRecoveredSinceRollover = 0;
        _transferLogCursor = _world.TransferLog.Count;
        return summary;
    }

    // ---------------------------------------------------------------------------------------------
    // World-health metrics (plan W3 telemetry).
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Mean overall of one division: the mean of the squad <see cref="PlayerAttributeSet.MeanCurrent"/> over
    /// every club currently in the tier (each club weighs equally, so the value does not depend on squad
    /// size). Clubs are read in registry order, so the result is deterministic.
    /// </summary>
    /// <param name="tier">Division tier 1..6.</param>
    /// <returns>The division's mean overall; 0 when the tier is empty.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier"/> is outside 1..6.</exception>
    public float TierMeanOverall(int tier)
    {
        if (tier is < 1 or > TierCount)
        {
            throw new ArgumentOutOfRangeException(nameof(tier), tier, "Tier must be in 1..6.");
        }

        float clubSum = 0f;
        int clubCount = 0;

        foreach (WorldClub club in _world.Clubs.All)
        {
            if (club.Tier != tier) continue;

            float playerSum = 0f;
            int playerCount = 0;
            IReadOnlyList<PlayerId> squad = club.Squad;
            for (int index = 0; index < squad.Count; index++)
            {
                if (!_world.Players.TryGet(squad[index], out WorldPlayer? player) || player is null) continue;
                if (player.IsRetired) continue;
                playerSum += player.Attributes.MeanCurrent();
                playerCount++;
            }

            if (playerCount == 0) continue;
            clubSum += playerSum / playerCount;
            clubCount++;
        }

        return clubCount == 0 ? 0f : clubSum / clubCount;
    }

    /// <summary>Mean age of the active (not retired) population, including free agents.</summary>
    public double MeanActiveAge()
    {
        long total = 0L;
        int count = 0;

        foreach (WorldPlayer player in _world.Players.All)
        {
            if (player.IsRetired) continue;
            total += player.Age;
            count++;
        }

        return count == 0 ? 0.0 : total / (double)count;
    }

    /// <summary>Active population: every registered player who has not retired (free agents included).</summary>
    public int ActivePopulation() => LifecycleSystem.CountActivePlayers(_world);

    // ---------------------------------------------------------------------------------------------
    // Rollover helpers: outcome, stats archive, managers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds the finished season's <see cref="SeasonOutcome"/> from the published final tables. The
    /// tables are the season's authoritative positions and tiers whether or not the promotion/relegation
    /// rollover has already moved the clubs (the live leagues cannot be re-resolved after it, because the
    /// league club lists are the next season's composition while the fixtures still name the old clubs).
    /// </summary>
    private SeasonOutcome RebuildOutcome(SeasonSummary finishedSeason)
    {
        IReadOnlyList<IReadOnlyList<LeagueTableRow>> tables = finishedSeason.TablesByTier;
        if (tables.Count == TierCount)
        {
            bool complete = true;
            for (int tier = 0; tier < tables.Count; tier++)
            {
                if (tables[tier].Count != ClubsPerLeague)
                {
                    complete = false;
                    break;
                }
            }

            if (complete)
            {
                return new SeasonOutcome(tables);
            }
        }

        // A caller that passes a summary without published tables still gets the live-fixture fallback.
        return StandingsCalculator.ResolveSeason(_world.Leagues);
    }

    /// <summary>
    /// Archives every played season and resets the live counters. A player whose live stats belong to the
    /// finished season keeps a copy of them in <see cref="WorldPlayer.SeasonHistory"/> when he appeared at
    /// least once; in every case the live stats are reset and stamped with the new season.
    /// </summary>
    private static void ArchiveSeasonStats(WorldState world, int finishedSeason, int nextSeason)
    {
        foreach (WorldPlayer player in world.Players.All)
        {
            PlayerSeasonStats stats = player.CurrentSeason;
            if (stats.Season == finishedSeason && stats.Appearances > 0)
            {
                player.SeasonHistory.Add(CopyStats(stats));
            }

            stats.Reset(nextSeason);
        }
    }

    /// <summary>Deep copy of one season's counting stats (the history keeps values, not a live reference).</summary>
    private static PlayerSeasonStats CopyStats(PlayerSeasonStats source) => new()
    {
        Season = source.Season,
        Appearances = source.Appearances,
        Starts = source.Starts,
        SubstituteAppearances = source.SubstituteAppearances,
        MinutesPlayed = source.MinutesPlayed,
        Goals = source.Goals,
        Assists = source.Assists,
        Shots = source.Shots,
        PassesAttempted = source.PassesAttempted,
        PassesCompleted = source.PassesCompleted,
        Tackles = source.Tackles,
        Saves = source.Saves,
        YellowCards = source.YellowCards,
        RedCards = source.RedCards,
        RatingSum = source.RatingSum
    };

    /// <summary>Resets every manager's match record for the new season (hired managers already start at zero).</summary>
    private void ResetManagerSeasonRecords()
    {
        foreach (WorldManager manager in _world.Managers.All)
        {
            manager.MatchesInCharge = 0;
            manager.Wins = 0;
            manager.Draws = 0;
            manager.Losses = 0;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Rollover helpers: contract-length reconciliation.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Extends the youth contracts just assigned in the rollover by one season. The contract system stamps
    /// <see cref="PlayerContract.SignedSeason"/> with <see cref="WorldState.Season"/>, which is still the
    /// finished season while the rollover runs, so a drawn 2-3 season deal would otherwise cover only 1-2
    /// seasons. The rollover re-stamps the deal and adds the missing season, so a youth contract lasts the
    /// 2-3 seasons the contract system drew for. See <see cref="ReconcileRenewalLengths"/> for the same
    /// reconciliation on the renewal pass.
    /// </summary>
    private void ReconcileYouthContractLengths(List<PlayerId> intakeIds, int nextSeason)
    {
        for (int index = 0; index < intakeIds.Count; index++)
        {
            if (!_world.Players.TryGet(intakeIds[index], out WorldPlayer? player) || player is null) continue;
            PlayerContract contract = player.Contract;
            contract.SignedSeason = nextSeason;
            contract.EndSeason += 1;
        }
    }

    /// <summary>Snapshots every player's contract expiry before the renewal pass rewrites it.</summary>
    private int[] SnapshotContractEndSeasons()
    {
        IReadOnlyList<WorldPlayer> players = _world.Players.All;
        var endSeasons = new int[players.Count];
        for (int index = 0; index < players.Count; index++)
        {
            endSeasons[index] = players[index].Contract.EndSeason;
        }

        return endSeasons;
    }

    /// <summary>
    /// Extends the contracts the renewal pass just signed by one season, for the same season-stamp reason as
    /// <see cref="ReconcileYouthContractLengths"/>: the rollover runs before the calendar advances, so a
    /// renewal drawn as N seasons would only cover N-1. A contract that was expiring before the pass and is
    /// still held by a club afterwards was re-signed by that pass (a released player has no club left), so
    /// this only ever touches the deals the pass just wrote — mid-season signings keep their expiry.
    /// </summary>
    private void ReconcileRenewalLengths(int finishedSeason, int nextSeason, int[] endSeasonBefore)
    {
        IReadOnlyList<WorldPlayer> players = _world.Players.All;
        for (int index = 0; index < players.Count; index++)
        {
            if (endSeasonBefore[index] > finishedSeason) continue; // was not expiring this rollover

            WorldPlayer player = players[index];
            if (player.IsRetired) continue;

            PlayerContract contract = player.Contract;
            if (!player.ClubId.IsValid || contract.IsOnLoan) continue;
            if (contract.SignedSeason != finishedSeason) continue; // not (re)signed by the renewal pass

            contract.SignedSeason = nextSeason;
            contract.EndSeason += 1;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Rollover helpers: fixtures, cup and the playability valve.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Clears and regenerates every league's fixture list and starts the new season's cup bracket.</summary>
    private void GenerateNextSeasonFixtures(int nextSeason)
    {
        WorldRng fixturesRng = _world.Stream(WorldRandomStream.Fixtures);
        foreach (WorldLeague league in _world.Leagues)
        {
            league.ClearFixtures();
            ScheduleGenerator.GenerateLeagueSeason(_world, league, fixturesRng);
        }

        _world.Cup.Reset(nextSeason);
        CupScheduler.StartSeason(_world, _world.Stream(WorldRandomStream.Cup));
    }

    /// <summary>
    /// v1 safety valve: gives every club below <see cref="MinimumRegisteredSquad"/> registered players or
    /// <see cref="MinimumAvailablePlayers"/> available players enough emergency academy players (16-17,
    /// generated through the normal NPC path) to clear both floors, then signs them through
    /// <see cref="ContractAndFinanceSystem.AssignYouthContracts"/>.
    ///
    /// <para><b>Why it is needed.</b> Releases, retirements and long injuries stack up over decades; without
    /// the valve a club can enter a season unable to field a matchday squad, which would make the season
    /// unplayable. It can be removed once squad building and availability are modelled more deeply.</para>
    /// </summary>
    private void EnsurePlayableSquads(int finishedSeason, int nextSeason)
    {
        bool anyShort = false;
        foreach (WorldClub club in _world.Clubs.All)
        {
            CountSquad(_world, club, out int registered, out int available);
            if (registered < MinimumRegisteredSquad || available < MinimumAvailablePlayers)
            {
                anyShort = true;
                break;
            }
        }

        if (!anyShort) return;

        // The lifecycle just created the intake players, so cached names from the finished season are stale.
        _usedNames = null;
        HashSet<string> usedNames = GetUsedNames();
        WorldRng rng = _world.Stream(WorldRandomStream.Regens);
        var emergency = new List<PlayerId>(8);

        foreach (WorldClub club in _world.Clubs.All)
        {
            CountSquad(_world, club, out int registered, out int available);
            int added = 0;
            while ((registered < MinimumRegisteredSquad || available < MinimumAvailablePlayers) &&
                   added < MaximumEmergencyIntakesPerClub)
            {
                GenerateEmergencyIntake(
                    _world, club, finishedSeason, _world.Week, rng, usedNames, emergency);
                registered++;
                available++;
                added++;
            }
        }

        if (emergency.Count > 0)
        {
            ContractAndFinanceSystem.AssignYouthContracts(
                _world, emergency, _world.Stream(WorldRandomStream.Contracts));
            ReconcileYouthContractLengths(emergency, nextSeason);
        }
    }

    /// <summary>
    /// Weekly playability valve: after the week's finance/medical/scouting/market tick, tops up any club
    /// below <see cref="MinimumMatchdayAvailablePlayers"/> available players with emergency academy players,
    /// so the selector can always field an XI plus a bench. It sits at the end of
    /// <see cref="OnWeekStart"/> because the transfer window can sell a club down to an unplayable squad in
    /// the very week it plays.
    /// </summary>
    private void EnsureMatchdayAvailability(WorldState world)
    {
        bool anyShort = false;
        foreach (WorldClub club in world.Clubs.All)
        {
            CountSquad(world, club, out _, out int available);
            if (available < MinimumMatchdayAvailablePlayers)
            {
                anyShort = true;
                break;
            }
        }

        if (!anyShort) return;

        HashSet<string> usedNames = GetUsedNames();
        WorldRng rng = world.Stream(WorldRandomStream.Regens);
        var emergency = new List<PlayerId>(8);

        foreach (WorldClub club in world.Clubs.All)
        {
            CountSquad(world, club, out _, out int available);
            int added = 0;
            while (available < MinimumMatchdayAvailablePlayers &&
                   added < MaximumEmergencyIntakesPerClub)
            {
                GenerateEmergencyIntake(world, club, world.Season, world.Week, rng, usedNames, emergency);
                available++;
                added++;
            }
        }

        if (emergency.Count > 0)
        {
            ContractAndFinanceSystem.AssignYouthContracts(
                world, emergency, world.Stream(WorldRandomStream.Contracts));
        }
    }

    /// <summary>
    /// Creates one emergency academy player for a club: a 16/17-year-old generated for the club's biggest
    /// roster shortfall through the same <see cref="PlayerGenerator.GenerateNpcForTier"/> path the annual
    /// youth intake uses, registered in the squad and appended to the transfer log as a youth intake.
    /// </summary>
    private void GenerateEmergencyIntake(
        WorldState world,
        WorldClub club,
        int logSeason,
        int logWeek,
        WorldRng rng,
        HashSet<string> usedNames,
        List<PlayerId> added)
    {
        RosterGroup group = RosterAnalyser.FindShortfall(world, club)
            ?? (RosterGroup)((club.Id.Value + _emergencySequence) % RosterGroupCount);
        TacticalRole role = IntakeRole(group, _emergencySequence);
        int age = EmergencyIntakeMinimumAge +
            rng.NextInt(0, EmergencyIntakeMaximumAge - EmergencyIntakeMinimumAge + 1);

        // Fork of the SEED (not the advancing state), so name retries can never move attributes.
        ulong playerSeed = rng
            .Fork(EmergencySeedOrdinalBase +
                (ulong)club.Id.Value * EmergencySeedClubMultiplier + (ulong)_emergencySequence)
            .Seed;

        PickUniqueName(rng, world.Pack.Names, usedNames, out string firstName, out string lastName);
        PlayerId playerId = world.AllocatePlayerId();
        WorldPlayer player = PlayerGenerator.GenerateNpcForTier(
            playerSeed, club.Tier, age, role, playerId, firstName, lastName);
        player.ClubId = club.Id;
        player.TrainingFocus = FocusForRole(role);
        player.CurrentSeason.Season = world.Season; // a mid-season call-up's stats belong to this season

        club.AddPlayer(playerId);
        club.SetManagerTrust(playerId, 0.5f);
        world.Players.Add(player);
        world.RecordTransfer(new TransferRecord(
            logSeason, logWeek, playerId, ClubId.None, club.Id, TransferKind.YouthIntake, 0L));

        added.Add(playerId);
        _emergencySequence++;
    }

    /// <summary>The cached world-wide unique-name set, rebuilt on demand (a rollover invalidates it).</summary>
    private HashSet<string> GetUsedNames() => _usedNames ??= BuildUsedNameSet(_world);

    /// <summary>Counts a club's registered squad and its currently selectable players.</summary>
    private static void CountSquad(WorldState world, WorldClub club, out int registered, out int available)
    {
        registered = 0;
        available = 0;
        IReadOnlyList<PlayerId> squad = club.Squad;
        for (int index = 0; index < squad.Count; index++)
        {
            if (!world.Players.TryGet(squad[index], out WorldPlayer? player) || player is null) continue;
            registered++;
            if (player.IsAvailableForSelection) available++;
        }
    }

    /// <summary>Maps a broad roster group onto the generator's tactical-role vocabulary.</summary>
    private static TacticalRole IntakeRole(RosterGroup group, int intakeIndex) => group switch
    {
        RosterGroup.Goalkeeper => TacticalRole.Goalkeeper,
        RosterGroup.Defender => (intakeIndex % 3) switch
        {
            0 => TacticalRole.CenterBack,
            1 => TacticalRole.FullBack,
            _ => TacticalRole.DefensiveMidfielder
        },
        RosterGroup.Midfielder => TacticalRole.CentralMidfielder,
        _ => (intakeIndex % 2) == 0 ? TacticalRole.Winger : TacticalRole.Striker
    };

    /// <summary>The weekly training focus an emergency intake starts on, from the role he was generated for.</summary>
    private static TrainingFocus FocusForRole(TacticalRole role) => role switch
    {
        TacticalRole.Goalkeeper => TrainingFocus.Goalkeeping,
        TacticalRole.CenterBack or TacticalRole.FullBack or TacticalRole.DefensiveMidfielder =>
            TrainingFocus.DefendingAndPositioning,
        TacticalRole.CentralMidfielder => TrainingFocus.PassingAndVision,
        TacticalRole.Winger => TrainingFocus.BallControlAndDribbling,
        TacticalRole.Striker => TrainingFocus.FinishingAndShooting,
        _ => TrainingFocus.Balanced
    };

    /// <summary>Case-insensitive set of every player's full name, so emergency intakes cannot duplicate one.</summary>
    private static HashSet<string> BuildUsedNameSet(WorldState world)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (WorldPlayer player in world.Players.All)
        {
            used.Add(player.DisplayName);
        }

        return used;
    }

    /// <summary>
    /// Picks a full name unique across the world: up to <see cref="MaximumNameAttempts"/> name-pair draws,
    /// then the last drawn pair with a numbered suffix. Deterministic and total.
    /// </summary>
    private static void PickUniqueName(
        WorldRng rng, NamePools names, HashSet<string> usedNames, out string firstName, out string lastName)
    {
        firstName = string.Empty;
        lastName = string.Empty;

        for (int attempt = 0; attempt < MaximumNameAttempts; attempt++)
        {
            firstName = names.FirstNames[rng.NextIndex(names.FirstNames.Count)];
            lastName = names.LastNames[rng.NextIndex(names.LastNames.Count)];
            if (usedNames.Add(names.ComposeName(firstName, lastName))) return;
        }

        for (int suffix = 2; suffix < 4000; suffix++)
        {
            if (usedNames.Add($"{names.ComposeName(firstName, lastName)} {suffix}")) return;
        }

        throw new InvalidOperationException("Could not find a unique emergency-intake name in the data pack.");
    }

    // ---------------------------------------------------------------------------------------------
    // Rollover helpers: telemetry.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Transfer-market moves (Permanent/Free/Loan) appended to the log since the previous rollover.</summary>
    private int CountTransferMarketMovesSinceRollover()
    {
        IReadOnlyList<TransferRecord> log = _world.TransferLog;
        int start = Math.Min(_transferLogCursor, log.Count);
        int count = 0;

        for (int index = start; index < log.Count; index++)
        {
            TransferKind kind = log[index].Kind;
            if (kind is TransferKind.Permanent or TransferKind.Free or TransferKind.Loan) count++;
        }

        return count;
    }

    /// <summary>
    /// The division moves implied by the current club tiers against an earlier tier snapshot. Only used when
    /// the caller's season summary did not publish the moves the season runner applied.
    /// </summary>
    private IReadOnlyList<DivisionMove> BuildDivisionMoves(int[] tiersBefore)
    {
        var moves = new List<DivisionMove>();
        IReadOnlyList<WorldClub> clubs = _world.Clubs.All;

        for (int index = 0; index < clubs.Count && index < tiersBefore.Length; index++)
        {
            WorldClub club = clubs[index];
            int fromTier = tiersBefore[index];
            if (club.Tier == fromTier) continue;
            moves.Add(new DivisionMove(club.Id, fromTier, club.Tier, ViaPlayoff: false));
        }

        return moves.Count == 0 ? Array.Empty<DivisionMove>() : moves.AsReadOnly();
    }
}
