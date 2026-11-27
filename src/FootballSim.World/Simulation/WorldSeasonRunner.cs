using System;
using System.Collections.Generic;
using FootballSim.World.Core;
using FootballSim.World.Players;
using FootballSim.World.World;

namespace FootballSim.World.Simulation;

/// <summary>What one week produced.</summary>
/// <param name="Date">The calendar week that was played (before the runner advanced the calendar).</param>
/// <param name="LeagueMatchesPlayed">League fixtures played this week (0 outside weeks 5..21 and 26..42).</param>
/// <param name="CupTiesPlayed">Domestic-cup ties played this week (0 in a week without a cup round).</param>
/// <param name="GoalsScored">
/// Normal-time goals scored in the matches played this week (league plus cup). Shoot-out kicks are not
/// goals and are excluded.
/// </param>
/// <param name="CupRoundsCompleted">
/// Cup rounds whose last tie was played this week and which the cup scheduler accepted as complete, in
/// bracket order (0 or 1 entries per week in a normal season; the final completes the cup).
/// </param>
public sealed record WeekReport(
    WorldDate Date,
    int LeagueMatchesPlayed,
    int CupTiesPlayed,
    int GoalsScored,
    IReadOnlyList<CupRound> CupRoundsCompleted);

/// <summary>
/// The resolved end-of-season report for the plan's W2 gate: final tables, champions, cup winner, goal
/// totals, the promotion/relegation ledger and the season's match-effect counters.
///
/// The tables and champions are the league standings <i>as played</i> (before the rollover moves clubs);
/// <see cref="DivisionMoves"/> is the rollover that was applied to the world immediately afterwards.
/// </summary>
public sealed class SeasonSummary
{
    /// <summary>Season number the summary belongs to.</summary>
    public int Season { get; init; }

    /// <summary>Final league tables, tier 1 first; each table has 18 rows in final position order.</summary>
    public IReadOnlyList<IReadOnlyList<LeagueTableRow>> TablesByTier { get; init; } =
        Array.Empty<IReadOnlyList<LeagueTableRow>>();

    /// <summary>Champion of each tier (index 0 = tier 1), i.e. the winner of each final table.</summary>
    public IReadOnlyList<ClubId> ChampionsByTier { get; init; } = Array.Empty<ClubId>();

    /// <summary>The domestic cup's winning club; always valid for a completed W2 season.</summary>
    public ClubId CupChampionClubId { get; init; }

    /// <summary>Total league matches played in the season (1,836 for the v1 six-division world).</summary>
    public int LeagueMatchesPlayed { get; init; }

    /// <summary>Total goals scored in league matches (cup and play-off goals excluded).</summary>
    public int LeagueGoals { get; init; }

    /// <summary>League goals per league match, i.e. <see cref="LeagueGoals"/> / <see cref="LeagueMatchesPlayed"/>.</summary>
    public float GoalsPerMatch { get; init; }

    /// <summary>Total domestic-cup ties played over the season's eight rounds (107 for the v1 bracket).</summary>
    public int CupTiesPlayed { get; init; }

    /// <summary>Every club that changed division in the rollover, automatic moves first.</summary>
    public IReadOnlyList<DivisionMove> DivisionMoves { get; init; } = Array.Empty<DivisionMove>();

    /// <summary>
    /// For each boundary tier 1..5, true when the higher-division club won its promotion/relegation
    /// play-off (both clubs stay), false when the lower-division club won (the clubs swap divisions).
    /// </summary>
    public IReadOnlyDictionary<int, bool> PlayoffWinnerWasHigherDivisionClub { get; init; } =
        new Dictionary<int, bool>();

    /// <summary>The season's top scorer, or <see cref="PlayerId.None"/> when nobody scored.</summary>
    public PlayerId TopScorerId { get; init; }

    /// <summary>League, cup and play-off goals the top scorer scored this season.</summary>
    public int TopScorerGoals { get; init; }

    /// <summary>Injuries suffered by players in matches this season.</summary>
    public int InjuriesThisSeason { get; init; }

    /// <summary>Yellow cards shown in matches this season (league, cup and play-offs).</summary>
    public int YellowCardsThisSeason { get; init; }

    /// <summary>Red cards shown in matches this season (league, cup and play-offs).</summary>
    public int RedCardsThisSeason { get; init; }
}

/// <summary>
/// One externally resolved fixture supplied to <see cref="WorldSeasonRunner"/> instead of being quick-simulated
/// by it (W4 career: the human's own match). The payload is the same <see cref="QuickSimResult"/> the quick
/// sim would have produced — a live-match bridge fills it from the real match engine — plus the lineups the
/// match was played with, because the applier needs to know who started.
/// </summary>
/// <param name="FixtureId">Fixture the result belongs to.</param>
/// <param name="Result">The full match result (score, events, per-player lines).</param>
/// <param name="HomeLineup">The home side's selected XI and bench used for the match.</param>
/// <param name="AwayLineup">The away side's selected XI and bench used for the match.</param>
public sealed record ExternalMatchResult(
    MatchId FixtureId,
    QuickSimResult Result,
    SelectedLineup HomeLineup,
    SelectedLineup AwayLineup);

/// <summary>
/// Per-season bookkeeping the save layer must persist so a reloaded runner continues exactly where the
/// in-memory one left off (W4 save/load). The weekly loads are not part of it: they are rebuilt every week.
/// </summary>
/// <param name="FocusSeason">Season whose training focuses have already been stamped.</param>
/// <param name="TrainingFocusSetThisSeason">Player ids already given an automatic focus this season.</param>
/// <param name="InjuriesThisSeason">Injury counter the season summary reports.</param>
/// <param name="YellowCardsThisSeason">Yellow-card counter the season summary reports.</param>
/// <param name="RedCardsThisSeason">Red-card counter the season summary reports.</param>
public sealed record WorldSeasonRunnerState(
    int FocusSeason,
    IReadOnlyList<PlayerId> TrainingFocusSetThisSeason,
    int InjuriesThisSeason,
    int YellowCardsThisSeason,
    int RedCardsThisSeason);

/// <summary>
/// Plays a generated world through a whole season: the weekly loop (fixtures, cup round, growth tick), the
/// end-of-season promotion/relegation play-offs and the rollover to the off-season. It is the W2 "full
/// season" gate: six divisions of 18 clubs x 34 matchdays, the 108-club domestic cup, realistic scorelines
/// and the exact promotion/relegation ledger, all deterministic from the world seed.
///
/// <para><b>One world, applier-only.</b> <see cref="WorldQuickSim"/> is a pure function and the cup
/// scheduler only draws the bracket: this runner is the system that applies results to the world. It
/// records the scoreline on the fixture, applies the per-player match lines (form, season stats, condition,
/// suspensions, injuries), nudges manager trust/board confidence, runs the weekly growth tick and advances
/// the calendar. Nothing else is allowed to mutate the world's match state.</para>
///
/// <para><b>Determinism.</b> Every random draw comes from the world's named streams
/// (<see cref="WorldRandomStream.QuickSim"/> forked per fixture, <see cref="WorldRandomStream.Growth"/>,
/// <see cref="WorldRandomStream.Injuries"/> and <see cref="WorldRandomStream.Cup"/>), every collection is
/// iterated in a stable order and no wall-clock or hash-order input exists, so the same world seed and the
/// same starting world produce the identical season.</para>
///
/// <para><b>v1 discipline.</b> A red card sets <c>SuspensionWeeksRemaining</c> to at least
/// <c>2</c>. Because the weekly tick decrements suspensions at the start of the next week, a straight red
/// ban of two weeks makes the player unavailable for exactly the next match under v1's single-ban model;
/// accumulated yellow cards are counted but do not trigger an extra ban (card accumulation is W3/W4).</para>
/// </summary>
public sealed class WorldSeasonRunner
{
    /// <summary>Last week whose league/cup fixtures belong to the competitive season (week 42).</summary>
    private const int LastCompetitiveWeek = WorldCalendar.LeagueSecondHalfLastWeek;

    /// <summary>Weeks a red card keeps a player out of selection (v1 single-ban model, see the class remarks).</summary>
    private const int RedCardSuspensionWeeks = 2;

    /// <summary>Manager-trust change for the winning/losing starting XI after one result.</summary>
    private const float TrustNudgePerResult = 0.015f;

    /// <summary>Board-confidence change for the winning/losing manager after one result.</summary>
    private const float BoardConfidenceNudgePerResult = 0.01f;

    /// <summary>Ordinal base of the play-off match streams (kept apart from the fixture-id forks).</summary>
    private const ulong PlayoffStreamOrdinalBase = 900_000UL;

    /// <summary>Clubs every v1 division must contain after the rollover.</summary>
    private const int ClubsPerLeague = 18;

    private readonly WorldState _world;
    private readonly IWorldWeekSystem? _weekSystem;
    private readonly HashSet<PlayerId> _trainingFocusSetThisSeason = new();
    private readonly Dictionary<PlayerId, WeeklyLoad> _weekLoads = new();

    /// <summary>Fixtures a frontend has already resolved and the runner must apply instead of simulating.</summary>
    private readonly Dictionary<MatchId, ExternalMatchResult> _externalResults = new();

    /// <summary>Players whose training focus belongs to a frontend (the career human), never auto-derived.</summary>
    private readonly HashSet<PlayerId> _manualTrainingFocus = new();

    private int _focusSeason = -1;
    private int _injuriesThisSeason;
    private int _yellowCardsThisSeason;
    private int _redCardsThisSeason;

    /// <summary>Creates a runner for one world.</summary>
    /// <param name="world">The world to play; it must be a fully generated season (clubs, players, fixtures and the cup).</param>
    /// <param name="weekSystem">
    /// Optional per-week ecosystem hook (W3 scouting/finance/transfers). It runs inside the season loop;
    /// a null hook leaves the pure match-and-growth runner the W2 gate uses.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    public WorldSeasonRunner(WorldState world, IWorldWeekSystem? weekSystem = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _weekSystem = weekSystem;
    }

    /// <summary>The world this runner mutates.</summary>
    public WorldState World => _world;

    /// <summary>
    /// Registers an externally resolved result (the human's own match, W4/W5) for a fixture the runner would
    /// otherwise quick-simulate. The runner records and applies it in its normal week slot, so the calendar,
    /// growth tick, condition and manager feedback all see it exactly like a quick-simulated match.
    /// </summary>
    /// <param name="external">The resolved fixture and the lineups it was played with.</param>
    /// <exception cref="ArgumentNullException"><paramref name="external"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The fixture is not part of this world, or is already played.</exception>
    /// <exception cref="ArgumentException">
    /// The result's clubs or the lineups do not belong to the fixture's two clubs.
    /// </exception>
    public void QueueExternalResult(ExternalMatchResult external)
    {
        ArgumentNullException.ThrowIfNull(external);

        WorldFixture? fixture = FindFixture(external.FixtureId);
        if (fixture is null)
        {
            throw new InvalidOperationException(
                $"Fixture {external.FixtureId} is not part of the world's current season.");
        }

        if (fixture.IsPlayed)
        {
            throw new InvalidOperationException($"Fixture {external.FixtureId} already has a result.");
        }

        if (external.Result.HomeClubId != fixture.HomeClubId || external.Result.AwayClubId != fixture.AwayClubId)
        {
            throw new ArgumentException(
                $"The external result names {external.Result.HomeClubId} v {external.Result.AwayClubId} but " +
                $"fixture {fixture.Id} is {fixture.HomeClubId} v {fixture.AwayClubId}.",
                nameof(external));
        }

        if (external.HomeLineup.ClubId != fixture.HomeClubId || external.AwayLineup.ClubId != fixture.AwayClubId)
        {
            throw new ArgumentException(
                $"The lineups do not match fixture {fixture.Id}'s home and away clubs.", nameof(external));
        }

        _externalResults[fixture.Id] = external;
    }

    /// <summary>True when a fixture is waiting for its externally resolved result to be applied.</summary>
    /// <param name="fixtureId">Fixture to test.</param>
    public bool HasExternalResult(MatchId fixtureId) => _externalResults.ContainsKey(fixtureId);

    /// <summary>Number of queued external results (a career matchday can queue a league and a cup fixture).</summary>
    public int ExternalResultCount => _externalResults.Count;

    /// <summary>Drops any queued external results (a discarded preview, or a save boundary).</summary>
    public void ClearExternalResults() => _externalResults.Clear();

    /// <summary>
    /// Marks a player as manually focused: <see cref="AdvanceWeek"/> will never re-derive his
    /// <see cref="WorldPlayer.TrainingFocus"/> from his emergent role. The career player's focus is the user's
    /// choice, and this keeps a reloaded session from overriding it.
    /// </summary>
    /// <param name="playerId">Player whose focus the frontend owns.</param>
    public void MarkTrainingFocusManual(PlayerId playerId)
    {
        if (!playerId.IsValid) throw new ArgumentOutOfRangeException(nameof(playerId));
        _manualTrainingFocus.Add(playerId);
    }

    /// <summary>True when the player's training focus is frontend-owned.</summary>
    /// <param name="playerId">Player to test.</param>
    public bool IsTrainingFocusManual(PlayerId playerId) => _manualTrainingFocus.Contains(playerId);

    /// <summary>Captures the runner's per-season bookkeeping for save/load (W4).</summary>
    public WorldSeasonRunnerState CaptureState()
    {
        var focuses = new PlayerId[_trainingFocusSetThisSeason.Count];
        _trainingFocusSetThisSeason.CopyTo(focuses);
        Array.Sort(focuses, static (left, right) => left.Value.CompareTo(right.Value));

        return new WorldSeasonRunnerState(
            _focusSeason,
            focuses,
            _injuriesThisSeason,
            _yellowCardsThisSeason,
            _redCardsThisSeason);
    }

    /// <summary>
    /// Restores the per-season bookkeeping captured by <see cref="CaptureState"/>. Call it on a runner that
    /// has just been constructed over a loaded world, before the next <see cref="AdvanceWeek"/>.
    /// </summary>
    /// <param name="state">The captured state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
    public void RestoreState(WorldSeasonRunnerState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        _focusSeason = state.FocusSeason;
        _injuriesThisSeason = state.InjuriesThisSeason;
        _yellowCardsThisSeason = state.YellowCardsThisSeason;
        _redCardsThisSeason = state.RedCardsThisSeason;

        _trainingFocusSetThisSeason.Clear();
        for (int index = 0; index < state.TrainingFocusSetThisSeason.Count; index++)
        {
            _trainingFocusSetThisSeason.Add(state.TrainingFocusSetThisSeason[index]);
        }

        _weekLoads.Clear();
        _externalResults.Clear();
    }

    /// <summary>
    /// Prepares one season: guards the calendar is at pre-season week 1, resets the per-season counters and
    /// stamps every player's live season stats with the season being played. Shared by
    /// <see cref="PlaySeason"/> and by career loops that drive <see cref="AdvanceWeek"/> themselves.
    /// </summary>
    /// <exception cref="InvalidOperationException">The world is not at pre-season week 1.</exception>
    public void BeginSeason()
    {
        if (_world.Week != WorldCalendar.PreSeasonFirstWeek)
        {
            throw new InvalidOperationException(
                $"A season must start at week {WorldCalendar.PreSeasonFirstWeek}; the world is at {_world.Date}.");
        }

        int season = _world.Season;
        _trainingFocusSetThisSeason.Clear();
        _focusSeason = season;
        _injuriesThisSeason = 0;
        _yellowCardsThisSeason = 0;
        _redCardsThisSeason = 0;

        // Live season stats belong to the season being played (a fresh world starts them at season 0).
        foreach (WorldPlayer player in _world.Players.All)
        {
            player.CurrentSeason.Season = season;
        }
    }

    /// <summary>
    /// Plays the current week: recovery/suspension ticks, this week's league fixtures and the cup round due
    /// this week (if any), applies every result to the world, runs the weekly growth tick for every player,
    /// advances the cup bracket, then moves the calendar on one week.
    ///
    /// <para><b>Order (part of the contract).</b></para>
    /// <list type="number">
    /// <item>Every player ticks: a suspension week is served (counter decremented) and an injury advances
    /// one recovery week (a recovered player is available again). No other recovery rule is invented here;
    /// the weekly lifecycle is W3's.</item>
    /// <item>League: every unplayed fixture whose <c>Week</c> is the current week is quick-simulated from
    /// the manager-selected XI, its scoreline recorded on the fixture and its match lines applied to the
    /// world immediately, so a second fixture in the same week (a cup tie) sees the updated condition.</item>
    /// <item>Cup: if the current bracket round is due this week, every unplayed tie is simulated, recorded
    /// through <see cref="WorldFixture.RecordCupResult"/> (which requires a decisive shoot-out on a level
    /// score) and applied the same way. After all fixtures, <see cref="CupScheduler.AdvanceIfReady"/> is
    /// called once (safe every week) so a fully resolved round is accepted and the next round drawn or the
    /// cup completed.</item>
    /// <item>Weekly growth tick: every player with a club receives
    /// <see cref="GrowthEngine.AdvanceWeek"/> with the minutes and mean rating he accumulated this week
    /// (0/0 when he did not appear) and his current <see cref="TrainingFocus"/>. AI focuses are auto-chosen
    /// once per player per season from the emergent primary role; a player still on
    /// <see cref="TrainingFocus.Balanced"/> is switched at any point in the season.</item>
    /// <item>A player who appeared this week ends the week with the stamina his last match left him
    /// (<see cref="PlayerMatchLine.EndingStamina01"/>); the growth tick's own condition drift never moves a
    /// player who played back up.</item>
    /// <item>The calendar advances one week (<see cref="WorldCalendar.NextWeek"/>).</item>
    /// </list>
    /// </summary>
    /// <returns>The played week's report: fixtures, goals and cup rounds completed.</returns>
    public WeekReport AdvanceWeek()
    {
        WorldDate playedDate = _world.Date;

        _weekSystem?.OnWeekStart(_world);

        AdvancePlayerTicks();
        EnsureTrainingFocuses();
        _weekLoads.Clear();

        int leagueMatches = 0;
        int cupTies = 0;
        int goals = 0;

        // 2a. League: one matchday's fixtures for every division, in league then fixture order.
        int week = _world.Week;
        foreach (WorldLeague league in _world.Leagues)
        {
            foreach (WorldFixture fixture in league.Fixtures)
            {
                if (fixture.Week != week || fixture.IsPlayed) continue;

                if (TryTakeExternalResult(fixture, out ExternalMatchResult? external))
                {
                    fixture.RecordLeagueResult(external.Result.HomeGoals, external.Result.AwayGoals);
                    ApplyResult(external.Result, external.HomeLineup, external.AwayLineup);
                    leagueMatches++;
                    goals += external.Result.HomeGoals + external.Result.AwayGoals;
                    continue;
                }

                QuickSimResult result = SimulateFixture(fixture, out SelectedLineup homeLineup, out SelectedLineup awayLineup);
                fixture.RecordLeagueResult(result.HomeGoals, result.AwayGoals);
                ApplyResult(result, homeLineup, awayLineup);
                leagueMatches++;
                goals += result.HomeGoals + result.AwayGoals;
            }
        }

        // 2b. Cup: the bracket round due this week (at most one), every tie not yet played.
        CupRoundBracket? playedRound = null;
        if (_world.Cup.CurrentRound is { } currentRound && currentRound.Week == week)
        {
            playedRound = currentRound;
            foreach (CupTie tie in currentRound.Ties)
            {
                if (tie.Fixture.IsPlayed) continue;

                if (TryTakeExternalResult(tie.Fixture, out ExternalMatchResult? external))
                {
                    tie.Fixture.RecordCupResult(
                        external.Result.HomeGoals,
                        external.Result.AwayGoals,
                        external.Result.HomePenaltyGoals,
                        external.Result.AwayPenaltyGoals);
                    ApplyResult(external.Result, external.HomeLineup, external.AwayLineup);
                    cupTies++;
                    goals += external.Result.HomeGoals + external.Result.AwayGoals;
                    continue;
                }

                QuickSimResult result = SimulateFixture(
                    tie.Fixture, out SelectedLineup homeLineup, out SelectedLineup awayLineup);
                tie.Fixture.RecordCupResult(
                    result.HomeGoals, result.AwayGoals, result.HomePenaltyGoals, result.AwayPenaltyGoals);
                ApplyResult(result, homeLineup, awayLineup);
                cupTies++;
                goals += result.HomeGoals + result.AwayGoals;
            }
        }

        // 2c. Accept a resolved round once: draw the next round, or crown the champion after the final.
        List<CupRound> roundsCompleted = new(1);
        bool cupAdvanced = CupScheduler.AdvanceIfReady(_world, _world.Stream(WorldRandomStream.Cup));
        if (cupAdvanced && playedRound is not null && cupTies > 0 && playedRound.IsResolved)
        {
            roundsCompleted.Add(playedRound.Round);
        }

        // 3. Weekly growth: after the appearances are recorded, so match minutes accelerate gain.
        RunGrowthTick();

        // 5. Match fatigue wins over the growth tick's condition drift for everyone who actually played.
        RestoreMatchCondition();

        // 5b. Ecosystem hook (scouting/finance/transfers) sees the finished week before the calendar moves.
        IReadOnlyList<CupRound> completed = roundsCompleted.Count == 0
            ? Array.Empty<CupRound>()
            : roundsCompleted;
        var report = new WeekReport(playedDate, leagueMatches, cupTies, goals, completed);
        _weekSystem?.OnWeekEnd(_world, report);

        // 6. One week on (weeks roll into the next season at week 52).
        _world.Date = WorldCalendar.NextWeek(_world.Date);

        return report;
    }

    /// <summary>
    /// Plays weeks until the end of the competitive season (through week 42: league matchday 34 and the cup
    /// final), resolves the final league tables, plays the five promotion/relegation play-offs, applies the
    /// promotion/relegation rollover, moves the calendar to week 46 of the same season and returns the
    /// resolved season summary.
    ///
    /// <para><b>Play-offs.</b> For each boundary tier 1..5 the higher-division club (16th of its table)
    /// hosts the lower-division club (3rd of the table below) in a single knockout match - a level score is
    /// settled by penalties - using a dedicated fork of the world's quick-sim stream. Their match lines are
    /// applied like any other match (stats, condition, injuries, manager record), but nothing is recorded on
    /// a fixture and no league total is touched.</para>
    ///
    /// <para><b>Preconditions.</b> The world must be positioned at week
    /// <see cref="WorldCalendar.PreSeasonFirstWeek"/> of a season, and it must be a fully generated world
    /// (a live domestic cup with its preliminary round drawn). The runner plays the season; W3 owns the
    /// following rollover (aging, regens, retirements, next season's fixtures).</para>
    /// </summary>
    /// <returns>The resolved season: tables, champions, cup winner, totals, moves and match-effect counters.</returns>
    /// <exception cref="InvalidOperationException">
    /// The world is not at week 1, the cup did not complete, or the rollover left a division without
    /// exactly 18 clubs.
    /// </exception>
    public SeasonSummary PlaySeason()
    {
        BeginSeason();

        while (_world.Week <= LastCompetitiveWeek)
        {
            AdvanceWeek();
        }

        return ResolveSeason();
    }

    /// <summary>
    /// Resolves the completed competitive season without playing it: guards that the competitive weeks are
    /// over, derives the final tables, plays the five promotion/relegation play-offs, applies the
    /// promotion/relegation moves, moves the calendar to the off-season (week 46) and returns the summary.
    ///
    /// <para>This is the split point the career loop (W3/W4) needs: it can drive the weeks itself — stopping
    /// for the human's match — and then resolve the season with exactly the same postseason code the W2
    /// gate runs.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The competitive weeks are not over, or the cup did not complete.</exception>
    public SeasonSummary ResolveSeason()
    {
        if (_world.Week <= LastCompetitiveWeek)
        {
            throw new InvalidOperationException(
                $"ResolveSeason requires the competitive weeks to be over (week > {LastCompetitiveWeek}); " +
                $"the world is at {_world.Date}.");
        }

        int season = _world.Season;
        SeasonOutcome outcome = StandingsCalculator.ResolveSeason(_world.Leagues);

        // The five boundary play-offs: higher-division club at home, confidence recorded per boundary.
        var playoffWinnerWasHigher = new Dictionary<int, bool>(outcome.PlayoffTies.Count);
        foreach (PlayoffTie tie in outcome.PlayoffTies)
        {
            WorldClub higher = _world.Clubs[tie.HigherDivisionClubId];
            WorldClub lower = _world.Clubs[tie.LowerDivisionClubId];

            SelectedLineup homeLineup = SquadSelector.Select(_world, higher);
            SelectedLineup awayLineup = SquadSelector.Select(_world, lower);
            WorldRng rng = _world.Stream(WorldRandomStream.QuickSim)
                .Fork(PlayoffStreamOrdinalBase + (ulong)tie.BoundaryTier);

            QuickSimResult result = WorldQuickSim.Simulate(
                _world, higher, lower, homeLineup, awayLineup, rng, knockout: true);
            ClubId winner = result.WinnerClubId;
            if (!winner.IsValid)
            {
                throw new InvalidOperationException(
                    $"The tier {tie.BoundaryTier} promotion/relegation play-off did not produce a winner.");
            }

            playoffWinnerWasHigher[tie.BoundaryTier] = winner == tie.HigherDivisionClubId;

            // Same application rules as a normal match, but a play-off is not a season fixture.
            ApplyResult(result, homeLineup, awayLineup);
        }

        IReadOnlyList<DivisionMove> moves = StandingsCalculator.ApplyPromotionAndRelegation(
            _world, outcome, playoffWinnerWasHigher);
        VerifyDivisionSizes();

        _world.Date = new WorldDate(_world.Season, WorldCalendar.OffSeasonFirstWeek);

        // League totals are derived from the played fixtures (the one place a result lives).
        int leagueMatches = 0;
        int leagueGoals = 0;
        foreach (WorldLeague league in _world.Leagues)
        {
            foreach (WorldFixture fixture in league.Fixtures)
            {
                if (!fixture.IsPlayed) continue;
                leagueMatches++;
                leagueGoals += fixture.HomeGoals + fixture.AwayGoals;
            }
        }

        int cupTiesPlayed = 0;
        foreach (CupRoundBracket round in _world.Cup.Rounds)
        {
            foreach (CupTie tie in round.Ties)
            {
                if (tie.Fixture.IsPlayed) cupTiesPlayed++;
            }
        }

        ClubId cupChampion = _world.Cup.ChampionClubId;
        if (!cupChampion.IsValid)
        {
            throw new InvalidOperationException(
                "The domestic cup did not complete: PlaySeason expects a generated world whose cup has " +
                "its preliminary round drawn and whose final is played in week " +
                $"{CupScheduler.WeekForCupRound(CupRound.Final)}.");
        }

        (PlayerId topScorerId, int topScorerGoals) = FindTopScorer();

        return new SeasonSummary
        {
            Season = season,
            TablesByTier = outcome.TablesByTier,
            ChampionsByTier = outcome.ChampionsByTier,
            CupChampionClubId = cupChampion,
            LeagueMatchesPlayed = leagueMatches,
            LeagueGoals = leagueGoals,
            GoalsPerMatch = leagueMatches == 0 ? 0f : leagueGoals / (float)leagueMatches,
            CupTiesPlayed = cupTiesPlayed,
            DivisionMoves = moves,
            PlayoffWinnerWasHigherDivisionClub = playoffWinnerWasHigher,
            TopScorerId = topScorerId,
            TopScorerGoals = topScorerGoals,
            InjuriesThisSeason = _injuriesThisSeason,
            YellowCardsThisSeason = _yellowCardsThisSeason,
            RedCardsThisSeason = _redCardsThisSeason
        };
    }

    /// <summary>Convenience: <c>new WorldSeasonRunner(world).PlaySeason()</c>.</summary>
    /// <param name="world">The world to play.</param>
    /// <returns>The resolved season summary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    public static SeasonSummary RunSeason(WorldState world) => new WorldSeasonRunner(world).PlaySeason();

    // ---------------------------------------------------------------------------------------------
    // Weekly phases.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Step 1: every player serves a suspension week and advances one injury-recovery week. A player whose
    /// suspension or injury reaches zero after this tick is available again in the matches that follow.
    /// </summary>
    private void AdvancePlayerTicks()
    {
        foreach (WorldPlayer player in _world.Players.All)
        {
            if (player.SuspensionWeeksRemaining > 0) player.SuspensionWeeksRemaining--;
            if (player.Injury.IsInjured) player.Injury.AdvanceWeek();
        }
    }

    /// <summary>
    /// Chooses each AI player's training focus once per season from his emergent primary role (plan §4.5:
    /// AI players train the attribute group their role depends on). A player who is still on
    /// <see cref="TrainingFocus.Balanced"/> is switched immediately; on the season's first week every player
    /// is re-derived, which is what keeps the focus aligned with the role the player has actually grown into.
    /// </summary>
    private void EnsureTrainingFocuses()
    {
        if (_focusSeason != _world.Season)
        {
            _trainingFocusSetThisSeason.Clear();
            _focusSeason = _world.Season;
        }

        bool seasonStart = _world.Week == WorldCalendar.PreSeasonFirstWeek;
        foreach (WorldPlayer player in _world.Players.All)
        {
            // The human career's focus is the user's choice (W4): never re-derive it from a role.
            if (_manualTrainingFocus.Contains(player.Id)) continue;
            if (!_trainingFocusSetThisSeason.Add(player.Id)) continue;
            if (!seasonStart && player.TrainingFocus != TrainingFocus.Balanced) continue;

            player.TrainingFocus = FocusForRole(IdentityEvaluator.Evaluate(player).PrimaryRole);
        }
    }

    /// <summary>Step 4: one growth week for every player who belongs to a club.</summary>
    private void RunGrowthTick()
    {
        WorldRng growthRng = _world.Stream(WorldRandomStream.Growth);
        foreach (WorldPlayer player in _world.Players.All)
        {
            // Free agents do not train at a club; the v1 world has none, but the guard keeps the tick total.
            if (!player.ClubId.IsValid) continue;
            if (!_world.Clubs.TryGet(player.ClubId, out WorldClub? club) || club is null) continue;

            int minutesThisWeek = 0;
            float averageRatingThisWeek = 0f;
            if (_weekLoads.TryGetValue(player.Id, out WeeklyLoad load))
            {
                minutesThisWeek = load.Minutes;
                if (load.Appearances > 0) averageRatingThisWeek = load.RatingSum / load.Appearances;
            }

            GrowthEngine.AdvanceWeek(
                player,
                club.TrainingEnvironment,
                player.TrainingFocus,
                minutesThisWeek,
                averageRatingThisWeek,
                growthRng);
        }
    }

    /// <summary>
    /// Step 5: a player who appeared this week is left at the stamina his last match used up, so the
    /// growth tick's recovery term cannot refill him after a match.
    /// </summary>
    private void RestoreMatchCondition()
    {
        if (_weekLoads.Count == 0) return;

        foreach (WorldPlayer player in _world.Players.All)
        {
            if (_weekLoads.TryGetValue(player.Id, out WeeklyLoad load) && load.Appearances > 0)
            {
                player.Condition01 = load.EndingCondition01;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Match resolution and application.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Quick-simulates one fixture with both managers' selected XI. This is exactly what
    /// <see cref="WorldQuickSim.Simulate(WorldState, WorldFixture)"/> does (select, fork the fixture's
    /// private quick-sim stream by match id, simulate); the runner selects explicitly because applying the
    /// match needs the used starting XI for the manager-trust nudge.
    /// </summary>
    private QuickSimResult SimulateFixture(
        WorldFixture fixture, out SelectedLineup homeLineup, out SelectedLineup awayLineup)
    {
        WorldClub home = _world.Clubs[fixture.HomeClubId];
        WorldClub away = _world.Clubs[fixture.AwayClubId];

        homeLineup = SquadSelector.Select(_world, home);
        awayLineup = SquadSelector.Select(_world, away);
        WorldRng rng = _world.Stream(WorldRandomStream.QuickSim).Fork((ulong)fixture.Id.Value);

        return WorldQuickSim.Simulate(
            _world, home, away, homeLineup, awayLineup, rng, knockout: fixture.IsCupTie);
    }

    /// <summary>
    /// Takes a queued externally resolved result for this fixture, if any. The entry is consumed, so a
    /// fixture is applied exactly once even if the loop is somehow revisited.
    /// </summary>
    private bool TryTakeExternalResult(WorldFixture fixture, out ExternalMatchResult external)
    {
        if (_externalResults.TryGetValue(fixture.Id, out ExternalMatchResult? queued) && queued is not null)
        {
            _externalResults.Remove(fixture.Id);
            external = queued;
            return true;
        }

        external = null!;
        return false;
    }

    /// <summary>Looks a fixture of the current season up by match id (league fixtures first, then the cup).</summary>
    private WorldFixture? FindFixture(MatchId fixtureId)
    {
        if (!fixtureId.IsValid) return null;

        foreach (WorldLeague league in _world.Leagues)
        {
            IReadOnlyList<WorldFixture> fixtures = league.Fixtures;
            for (int index = 0; index < fixtures.Count; index++)
            {
                if (fixtures[index].Id == fixtureId) return fixtures[index];
            }
        }

        foreach (CupRoundBracket round in _world.Cup.Rounds)
        {
            foreach (CupTie tie in round.Ties)
            {
                if (tie.Fixture.Id == fixtureId) return tie.Fixture;
            }
        }

        return null;
    }

    /// <summary>
    /// Applies one simulated match to the world: both sides' match lines, the in-match injuries and the
    /// two managers' result. The scoreline itself is recorded by the caller (league fixture or cup tie),
    /// and a promotion/relegation play-off records nothing at all.
    /// </summary>
    private void ApplyResult(
        QuickSimResult result, SelectedLineup homeLineup, SelectedLineup awayLineup)
    {
        ApplyTeamLines(homeLineup, result.HomePlayers);
        ApplyTeamLines(awayLineup, result.AwayPlayers);
        ApplyInjuries(result);
        ApplyManagerResult(result, homeLineup, awayLineup);
    }

    /// <summary>
    /// Records one side's match lines: rolling form, this season's counters, condition, discipline and the
    /// weekly growth accumulation. A red card additionally suspends the player (see the class remarks).
    /// </summary>
    private void ApplyTeamLines(SelectedLineup lineup, IReadOnlyList<PlayerMatchLine> lines)
    {
        for (int index = 0; index < lines.Count; index++)
        {
            PlayerMatchLine line = lines[index];
            WorldPlayer player = _world.Players[line.PlayerId];
            bool started = ContainsPlayer(lineup.StartingXI, line.PlayerId);

            player.Form.Record(new MatchAppearance(_world.Season, _world.Week, line.Minutes, line.Rating));

            PlayerSeasonStats stats = player.CurrentSeason;
            if (line.Minutes > 0) stats.Appearances++;
            if (started) stats.Starts++;
            else if (line.Minutes > 0) stats.SubstituteAppearances++;
            stats.MinutesPlayed += line.Minutes;
            stats.Goals += line.Goals;
            stats.Assists += line.Assists;
            stats.Shots += line.Shots;
            stats.PassesAttempted += line.PassesAttempted;
            stats.PassesCompleted += line.PassesCompleted;
            stats.Tackles += line.Tackles;
            stats.Saves += line.Saves;
            stats.YellowCards += line.YellowCards;
            stats.RedCards += line.RedCards;
            stats.RatingSum += line.Rating;

            if (line.YellowCards > 0) _yellowCardsThisSeason += line.YellowCards;
            if (line.RedCards > 0)
            {
                _redCardsThisSeason += line.RedCards;
                player.SuspensionWeeksRemaining = Math.Max(
                    player.SuspensionWeeksRemaining, RedCardSuspensionWeeks);
            }

            // The quick sim already priced the physical cost of these minutes; keep it for the next fixture
            // in the same week and for the end-of-week state.
            player.Condition01 = line.EndingStamina01;

            _weekLoads.TryGetValue(player.Id, out WeeklyLoad load);
            load.Minutes += line.Minutes;
            load.Appearances += 1;
            load.RatingSum += line.Rating;
            load.EndingCondition01 = line.EndingStamina01;
            _weekLoads[player.Id] = load;
        }
    }

    /// <summary>
    /// Step 4: records every in-match injury on the player. A severe injury (or worse) also applies the
    /// growth engine's permanent ceiling damage; the injury's own ledger makes that a once-per-injury
    /// effect.
    /// </summary>
    private void ApplyInjuries(QuickSimResult result)
    {
        for (int index = 0; index < result.Injuries.Count; index++)
        {
            InjuryEvent injury = result.Injuries[index];
            if (!_world.Players.TryGet(injury.PlayerId, out WorldPlayer? player) || player is null) continue;

            // The quick sim only names players who took the field, and an injured player cannot be selected
            // again in the same week; the guard keeps a hand-built world from double-starting an injury.
            if (player.Injury.IsInjured) continue;

            player.Injury.Begin(injury.Kind, injury.Severity, injury.WeeksOut);
            if (injury.Severity >= InjurySeverity.Severe)
            {
                GrowthEngine.ApplyPermanentInjuryDamage(
                    player, injury.Kind, _world.Stream(WorldRandomStream.Injuries));
            }

            _injuriesThisSeason++;
        }
    }

    /// <summary>
    /// Records the result in both managers' ledgers and applies the small trust/board-confidence feedback
    /// the selector and the W3 manager carousel read. A draw leaves the board confidence untouched; a
    /// shoot-out win counts as a win for trust/confidence while the recorded score stays the normal-time one.
    /// </summary>
    private void ApplyManagerResult(
        QuickSimResult result, SelectedLineup homeLineup, SelectedLineup awayLineup)
    {
        WorldClub home = _world.Clubs[result.HomeClubId];
        WorldClub away = _world.Clubs[result.AwayClubId];
        WorldManager homeManager = _world.Managers[home.ManagerId];
        WorldManager awayManager = _world.Managers[away.ManagerId];

        homeManager.RecordResult(result.HomeGoals, result.AwayGoals);
        awayManager.RecordResult(result.AwayGoals, result.HomeGoals);

        ClubId winner = result.WinnerClubId;
        if (!winner.IsValid) return; // a drawn league match: no winner, no trust or confidence nudge.

        bool homeWon = winner == home.Id;
        NudgeTrust(homeWon ? home : away, homeWon ? homeLineup : awayLineup, TrustNudgePerResult);
        NudgeTrust(homeWon ? away : home, homeWon ? awayLineup : homeLineup, -TrustNudgePerResult);

        if (homeWon)
        {
            homeManager.BoardConfidence01 += BoardConfidenceNudgePerResult;
            awayManager.BoardConfidence01 -= BoardConfidenceNudgePerResult;
        }
        else
        {
            awayManager.BoardConfidence01 += BoardConfidenceNudgePerResult;
            homeManager.BoardConfidence01 -= BoardConfidenceNudgePerResult;
        }
    }

    /// <summary>Nudges the manager's trust in every player of the used starting XI, clamped by the club.</summary>
    private static void NudgeTrust(WorldClub club, SelectedLineup lineup, float delta)
    {
        IReadOnlyList<PlayerId> startingXI = lineup.StartingXI;
        for (int index = 0; index < startingXI.Count; index++)
        {
            PlayerId playerId = startingXI[index];
            club.SetManagerTrust(playerId, club.ManagerTrustOf(playerId) + delta);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Season resolution helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Fails loudly if the rollover left any division without its contract 18 clubs.</summary>
    private void VerifyDivisionSizes()
    {
        foreach (WorldLeague league in _world.Leagues)
        {
            if (league.Clubs.Count != ClubsPerLeague)
            {
                throw new InvalidOperationException(
                    $"After promotion/relegation, {league.Name} has {league.Clubs.Count} clubs instead of " +
                    $"{ClubsPerLeague}.");
            }
        }
    }

    /// <summary>
    /// The season's top scorer by goals; ties resolve to the lower <see cref="PlayerId"/> (and therefore to
    /// the earlier-registered player). Returns <see cref="PlayerId.None"/> when nobody scored.
    /// </summary>
    private (PlayerId Id, int Goals) FindTopScorer()
    {
        PlayerId topScorerId = PlayerId.None;
        int topScorerGoals = 0;

        foreach (WorldPlayer player in _world.Players.All)
        {
            int goals = player.CurrentSeason.Goals;
            if (goals <= 0) continue;

            if (goals > topScorerGoals ||
                (goals == topScorerGoals && topScorerId.IsValid && player.Id.Value < topScorerId.Value))
            {
                topScorerGoals = goals;
                topScorerId = player.Id;
            }
        }

        return (topScorerId, topScorerGoals);
    }

    /// <summary>True when the ordered id list contains the player.</summary>
    private static bool ContainsPlayer(IReadOnlyList<PlayerId> playerIds, PlayerId playerId)
    {
        for (int index = 0; index < playerIds.Count; index++)
        {
            if (playerIds[index] == playerId) return true;
        }

        return false;
    }

    /// <summary>The AI training focus for an emergent primary role (plan §4.5).</summary>
    private static TrainingFocus FocusForRole(WorldRole role) => role switch
    {
        WorldRole.Goalkeeper => TrainingFocus.Goalkeeping,
        WorldRole.CenterBack or WorldRole.FullBack or WorldRole.DefensiveMidfielder =>
            TrainingFocus.DefendingAndPositioning,
        WorldRole.CentralMidfielder or WorldRole.AttackingMidfielder => TrainingFocus.PassingAndVision,
        WorldRole.Winger => TrainingFocus.BallControlAndDribbling,
        WorldRole.Striker => TrainingFocus.FinishingAndShooting,
        _ => TrainingFocus.Balanced
    };

    /// <summary>One player's accumulated competitive load for the week being played.</summary>
    private struct WeeklyLoad
    {
        /// <summary>Competitive minutes played (league plus cup; at most 180).</summary>
        public int Minutes;

        /// <summary>Matches appeared in (one per competition that week).</summary>
        public int Appearances;

        /// <summary>Sum of the match ratings, divided by <see cref="Appearances"/> for the week mean.</summary>
        public float RatingSum;

        /// <summary>Stamina the player's last match of the week left him.</summary>
        public float EndingCondition01;
    }
}
