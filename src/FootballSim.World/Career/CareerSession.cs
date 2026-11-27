using System;
using System.Collections.Generic;
using FootballSim;
using FootballSim.Core;
using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Players;
using FootballSim.World.Simulation;
using FootballSim.World.World;

namespace FootballSim.World.Career;

/// <summary>
/// The single-player career facade (plan stage W4): the one API a frontend drives, without touching the
/// simulation internals. It owns exactly one career — one human player in the one mutable world — and it is
/// the applier for everything career-shaped: the inbox, the season ledger, the training focus, human match
/// resolution and the save boundary.
///
/// <para><b>How the clock works.</b> <see cref="AdvanceToNextEventOrMatchday"/> runs whole world weeks
/// (<see cref="WorldSeasonRunner.AdvanceWeek"/>, with the W3 ecosystem as the week hook) and stops only when
/// the frontend is needed: the human's matchday, an actionable inbox message, a season rollover, or the
/// player being unattached. The human's own matches are never simulated behind the frontend's back: the
/// session stops before the week, the frontend either quick-sims (<see cref="ResolveHumanMatchViaQuickSim"/>)
/// or plays it live (<see cref="PrepareHumanLiveMatch"/> + <see cref="CommitHumanLiveMatchResult"/>), and the
/// resolved result is queued on the runner so it is applied in the normal week slot (identical stats, growth
/// minutes, condition, injuries and manager feedback).</para>
///
/// <para><b>Determinism.</b> Every random draw uses the world's named streams and every collection is walked
/// in registry order. The frontend's own choices (which offer to accept, which focus to train) are the only
/// degree of freedom, exactly as the plan's "same seed + same commands" rule requires. Read-only UI queries
/// (<see cref="GetPlayerPublicView"/>, fixtures, squads) never consume simulation randomness.</para>
///
/// <para><b>Persistence.</b> The session can be captured into a <see cref="CareerSaveData"/> at any point
/// between <see cref="AdvanceToNextEventOrMatchday"/> calls (the save layer combines it with the world
/// snapshot). A live match that was prepared but not committed is deliberately not savable — the UI must
/// commit or discard it first — because a half-played match is frontend state, not career state.</para>
/// </summary>
public sealed class CareerSession
{
    /// <summary>Seasons a career's opening contract covers (the first chance to earn a renewal).</summary>
    private const int OpeningContractSeasons = 2;

    /// <summary>Seasons a renewal or a trial signing covers.</summary>
    private const int RenewalContractSeasons = 2;

    /// <summary>Weeks an inbox offer stays answerable (clamped to the end of the season).</summary>
    private const int OfferLifetimeWeeks = 4;

    /// <summary>Weeks a trial invite stays answerable.</summary>
    private const int TrialOfferLifetimeWeeks = 2;

    /// <summary>Fork ordinal of the career player's own generation seed (keeps it clear of every other use).</summary>
    private const ulong HumanGenerationFork = 700_000UL;

    /// <summary>Fork ordinal base of the coach reports; keyed by calendar position so order never matters.</summary>
    private const ulong CoachReportForkBase = 800_000UL;

    /// <summary>First division a released/free-agent career player is offered a trial in (tiers 5-6 only).</summary>
    private const int TrialMinimumTier = 5;

    /// <summary>Largest division the trial search falls back to when no tier-5/6 club has room.</summary>
    private const int TrialMaximumTier = 6;

    /// <summary>Clubs stop adding bodies at this squad size (mirrors the transfer market's valve).</summary>
    private const int MaximumSquadSize = 30;

    private readonly WorldState _world;
    private readonly WorldEcosystem _ecosystem;
    private readonly WorldSeasonRunner _runner;
    private readonly CareerConfig _config;
    private readonly PlayerId _humanPlayerId;
    private readonly CareerInbox _inbox = new();
    private readonly CareerHistory _history = new();

    private int _nextMessageId = 1;
    private int _seasonStartClubTier = 6;

    // Inbox generation markers: all saved, so a reloaded career never re-sends a message.
    private bool _renewalOfferCreated;
    private int _lastOfferSeason = -1;
    private int _lastOfferWindowWeek;
    private int _lastMidSeasonReportSeason = -1;
    private int _lastCupRoundSeen;
    private bool _injuryNotified;

    /// <summary>The prepared live match, waiting for the bridge to play it.</summary>
    private CareerMatchContext? _liveContext;

    /// <summary>The engine-ready bridge setup for the prepared live match (W5).</summary>
    private Bridge.WorldMatchSetup? _liveSetup;

    /// <summary>Creates the session around an existing (already generated) world.</summary>
    private CareerSession(
        WorldState world,
        WorldEcosystem ecosystem,
        WorldSeasonRunner runner,
        CareerConfig config,
        PlayerId humanPlayerId)
    {
        _world = world;
        _ecosystem = ecosystem;
        _runner = runner;
        _config = config;
        _humanPlayerId = humanPlayerId;
    }

    // ---------------------------------------------------------------------------------------------
    // Creation.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts a new career on the repository's default world data pack.
    /// </summary>
    /// <param name="config">The creation request (mode, identity, age, backstory, potential, seed).</param>
    /// <returns>A career positioned at pre-season week 1 of season 1.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <exception cref="ArgumentException">The creation request is invalid.</exception>
    /// <exception cref="FileNotFoundException">The default world pack cannot be found.</exception>
    public static CareerSession StartNewCareer(CareerConfig config) =>
        StartNewCareer(config, WorldPackLoader.Load(WorldPackPaths.ResolveDefaultPack()));

    /// <summary>
    /// Starts a new career from a specific world data pack (the modding/test entry point).
    /// </summary>
    /// <param name="config">The creation request.</param>
    /// <param name="pack">The validated data pack the world is built from.</param>
    /// <returns>A career positioned at pre-season week 1 of season 1.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException">The creation request is invalid, or its club does not exist.</exception>
    public static CareerSession StartNewCareer(CareerConfig config, WorldPack pack)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(pack);
        config.Validate();

        WorldState world = WorldGenerator.Generate(pack, new WorldGenerationOptions { Seed = config.WorldSeed });
        var ecosystem = new WorldEcosystem(world);
        ecosystem.EnsureInitialized();

        var runner = new WorldSeasonRunner(world, ecosystem);
        var humanId = world.AllocatePlayerId();
        WorldClub club = PlaceHumanPlayer(world, config, humanId);

        var session = new CareerSession(world, ecosystem, runner, config, humanId);
        InitializeCareerPlayer(world, runner, config, club, humanId);
        world.ExternallyManagedPlayers.Add(humanId);
        runner.MarkTrainingFocusManual(humanId);

        session._seasonStartClubTier = club.Tier;
        runner.BeginSeason();

        // First season's opening inbox: a pre-season report and the manager's welcome/status note. The
        // season-1 summer window marker is set without generating offers: clubs have not seen him play yet.
        session._lastOfferSeason = world.Season;
        session._lastOfferWindowWeek = WorldCalendar.PreSeasonFirstWeek;
        session.AddCoachReport("Pre-season report", "The staff have had a first look at you.");
        session.AddManagerWelcome(world, club, humanId);
        return session;
    }

    /// <summary>Attaches the freshly generated career player to his club: squad, contract, focus.</summary>
    private static void InitializeCareerPlayer(
        WorldState world,
        WorldSeasonRunner runner,
        CareerConfig config,
        WorldClub club,
        PlayerId humanId)
    {
        WorldPlayer player = world.Players[humanId];
        player.ClubId = club.Id;
        club.AddPlayer(humanId);
        club.SetManagerTrust(humanId, 0.5f);
        ContractAndFinanceSystem.NewContract(
            world, player, club, world.Stream(WorldRandomStream.Contracts), OpeningContractSeasons);
        ContractAndFinanceSystem.RefreshBudgets(world);

        player.TrainingFocus = config.InitialTrainingFocus;
        player.CurrentSeason.Season = world.Season;
        runner.MarkTrainingFocusManual(humanId);
    }

    /// <summary>
    /// Builds the career player and places him at his starting club: the configured club when one is chosen,
    /// otherwise a division-appropriate amateur club (academy backgrounds start a tier higher — a released
    /// academy player rebuilding in the amateur game, exactly as the plan describes).
    /// </summary>
    private static WorldClub PlaceHumanPlayer(WorldState world, CareerConfig config, PlayerId humanId)
    {
        WorldClub club = ResolveStartingClub(world, config);

        WorldRng talentSeed = world.Stream(WorldRandomStream.Career).Fork(HumanGenerationFork);
        WorldPlayer player = config.Mode == CareerCreationMode.Custom
            ? PlayerGenerator.GenerateCustom(
                talentSeed.Seed,
                config.StartingAge,
                config.ResolveAptitudes(),
                config.Backstory,
                config.PreferGoalkeeper,
                humanId,
                config.FirstName.Trim(),
                config.LastName.Trim(),
                config.PreferredFoot)
            : PlayerGenerator.GenerateAuthentic(
                talentSeed.Seed,
                config.StartingAge,
                config.Backstory,
                config.PreferGoalkeeper,
                humanId,
                config.FirstName.Trim(),
                config.LastName.Trim(),
                config.PreferredFoot);

        world.Players.Add(player);
        return club;
    }

    /// <summary>Chooses the club the career starts at (configured, or the plan's amateur/academy rule).</summary>
    private static WorldClub ResolveStartingClub(WorldState world, CareerConfig config)
    {
        if (config.StartingClubId.IsValid)
        {
            if (!world.Clubs.TryGet(config.StartingClubId, out WorldClub? chosen) || chosen is null)
            {
                throw new ArgumentException(
                    $"The requested starting club {config.StartingClubId} is not part of the world.",
                    nameof(config));
            }

            return chosen;
        }

        // An academy product starts with a tier-5 trial; local/street/multisport players start at the bottom.
        int tier = config.Backstory is BackstoryKind.EliteAcademy or BackstoryKind.RegionalAcademy ? 5 : 6;
        IReadOnlyList<WorldClub> clubs = world.Clubs.InTier(tier);
        if (clubs.Count == 0)
        {
            throw new InvalidOperationException($"The world has no clubs in tier {tier}.");
        }

        if (config.PreferGoalkeeper)
        {
            // A keeper prospect goes where the club actually needs hands (deterministic: fewest keepers, then id).
            WorldClub? best = null;
            int bestCount = int.MaxValue;
            for (int index = 0; index < clubs.Count; index++)
            {
                WorldClub candidate = clubs[index];
                int keepers = RosterAnalyser.Count(world, candidate, RosterGroup.Goalkeeper);
                if (keeperCountSooner(keepers, candidate, bestCount, best)) continue;
                best = candidate;
                bestCount = keepers;
            }

            if (best is not null) return best;

            static bool keeperCountSooner(int keepers, WorldClub candidate, int bestCount, WorldClub? best) =>
                keepers < bestCount || (keepers == bestCount && (best is null || candidate.Id.Value < best.Id.Value));
        }

        int pick = world.Stream(WorldRandomStream.Career).NextIndex(clubs.Count);
        return clubs[pick];
    }

    // ---------------------------------------------------------------------------------------------
    // Public state.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The one mutable world this career plays in.</summary>
    public WorldState World => _world;

    /// <summary>The creation request the career was started with.</summary>
    public CareerConfig Config => _config;

    /// <summary>The career player's stable id.</summary>
    public PlayerId HumanPlayerId => _humanPlayerId;

    /// <summary>The career player.</summary>
    public WorldPlayer HumanPlayer => _world.Players[_humanPlayerId];

    /// <summary>The career player's current club, or null when he is unattached.</summary>
    public WorldClub? HumanClub =>
        HumanPlayer.ClubId.IsValid && _world.Clubs.TryGet(HumanPlayer.ClubId, out WorldClub? club) ? club : null;

    /// <summary>The career inbox.</summary>
    public CareerInbox Inbox => _inbox;

    /// <summary>The career ledger.</summary>
    public CareerHistory History => _history;

    /// <summary>The world's season loop (exposed so the W5 match bridge can reuse the same appliers).</summary>
    public WorldSeasonRunner Runner => _runner;

    /// <summary>The world's W3 ecosystem (scouting, transfers, finance, lifecycle, managers).</summary>
    public WorldEcosystem Ecosystem => _ecosystem;

    /// <summary>Current season number.</summary>
    public int Season => _world.Season;

    /// <summary>Current calendar position.</summary>
    public WorldDate Date => _world.Date;

    /// <summary>True once the career player has retired.</summary>
    public bool IsCareerOver => HumanPlayer.IsRetired;

    /// <summary>The weekly training focus currently in force.</summary>
    public TrainingFocus TrainingFocus => HumanPlayer.TrainingFocus;

    /// <summary>True when a live match has been prepared and is waiting for its report.</summary>
    public bool HasPreparedLiveMatch => _liveContext is not null;

    /// <summary>True when a resolved match result is queued and the next advance will apply it.</summary>
    public bool HasQueuedMatchResult => _runner.ExternalResultCount > 0;

    /// <summary>
    /// True when the career can be captured by the save layer right now. A prepared-but-uncommitted live match
    /// is frontend state, so it must be committed (or discarded) before saving.
    /// </summary>
    public bool CanSave => _liveContext is null && _liveSetup is null && _runner.ExternalResultCount == 0;

    // ---------------------------------------------------------------------------------------------
    // Weekly training.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Sets the career player's weekly training focus (the user's main development lever). The runner never
    /// re-derives a manual focus, so this stays in force until changed again.
    /// </summary>
    /// <param name="focus">One of the ten presets.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="focus"/> is undefined.</exception>
    public void SetTrainingFocus(TrainingFocus focus)
    {
        if (!Enum.IsDefined(focus))
        {
            throw new ArgumentOutOfRangeException(nameof(focus), focus, "Unknown training focus.");
        }

        HumanPlayer.TrainingFocus = focus;
        _runner.MarkTrainingFocusManual(_humanPlayerId);
    }

    // ---------------------------------------------------------------------------------------------
    // The clock.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Advances the career until the frontend is needed, then returns what it stopped on.
    ///
    /// <para>Stop conditions, in evaluation order: a queued human match result is applied and the clock moves
    /// on; the player has retired (<see cref="CareerEventKind.CareerOver"/>); the player is unattached
    /// (<see cref="CareerEventKind.NeedsClub"/>); an inbox message needs an answer
    /// (<see cref="CareerEventKind.InboxAction"/>); the human's club plays this week
    /// (<see cref="CareerEventKind.HumanMatchday"/>); a season finishes and rolls over
    /// (<see cref="CareerEventKind.SeasonRolledOver"/>). Otherwise a whole week is played and the loop
    /// repeats.</para>
    /// </summary>
    /// <returns>What the career stopped on, plus the objects the frontend needs.</returns>
    /// <exception cref="InvalidOperationException">
    /// A prepared live match is still open (commit or discard it first), or the world is in an unexpected state.
    /// </exception>
    public CareerAdvanceResult AdvanceToNextEventOrMatchday()
    {
        if (_liveContext is not null)
        {
            throw new InvalidOperationException(
                "A live match is prepared but not committed; call CommitHumanLiveMatchResult or DiscardLiveMatch first.");
        }

        for (int guard = 0; guard < 4_000; guard++)
        {
            if (_runner.ExternalResultCount > 0)
            {
                AdvanceOneWeek();
                continue;
            }

            if (HumanPlayer.IsRetired)
            {
                return new CareerAdvanceResult { Kind = CareerEventKind.CareerOver, Date = _world.Date };
            }

            if (!HumanPlayer.ClubId.IsValid)
            {
                return new CareerAdvanceResult { Kind = CareerEventKind.NeedsClub, Date = _world.Date };
            }

            CareerInboxMessage? urgent = _inbox.FirstAwaitingResponse(_world.Season, _world.Week);
            if (urgent is not null)
            {
                return new CareerAdvanceResult
                {
                    Kind = CareerEventKind.InboxAction,
                    Date = _world.Date,
                    Message = urgent
                };
            }

            if (_world.Week > WorldCalendar.LeagueSecondHalfLastWeek)
            {
                CareerAdvanceResult? closed = CloseSeason();
                if (closed is not null) return closed;
                continue;
            }

            IReadOnlyList<WorldFixture> humanFixtures = HumanFixturesThisWeek();
            if (humanFixtures.Count > 0 && HumanPlayer.IsAvailableForSelection)
            {
                return new CareerAdvanceResult
                {
                    Kind = CareerEventKind.HumanMatchday,
                    Date = _world.Date,
                    Fixture = humanFixtures[0]
                };
            }

            AdvanceOneWeek();
        }

        throw new InvalidOperationException("The career clock did not reach an event; the world state is inconsistent.");
    }

    /// <summary>
    /// The human club's unplayed fixture(s) in the current week, in runner order (league fixtures first, then
    /// the cup tie). A club can have two matches in one week (league matchday plus a cup round), so this is a
    /// list rather than a single fixture.
    /// </summary>
    public IReadOnlyList<WorldFixture> HumanFixturesThisWeek()
    {
        if (!HumanPlayer.ClubId.IsValid) return Array.Empty<WorldFixture>();

        ClubId clubId = HumanPlayer.ClubId;
        int week = _world.Week;
        List<WorldFixture>? fixtures = null;

        foreach (WorldLeague league in _world.Leagues)
        {
            IReadOnlyList<WorldFixture> all = league.Fixtures;
            for (int index = 0; index < all.Count; index++)
            {
                WorldFixture fixture = all[index];
                if (fixture.Week != week || fixture.IsPlayed || !fixture.Involves(clubId)) continue;
                (fixtures ??= new List<WorldFixture>(2)).Add(fixture);
            }
        }

        if (_world.Cup.CurrentRound is { } round && round.Week == week)
        {
            for (int index = 0; index < round.Ties.Count; index++)
            {
                CupTie tie = round.Ties[index];
                if (tie.Fixture.IsPlayed || !tie.Fixture.Involves(clubId)) continue;
                (fixtures ??= new List<WorldFixture>(2)).Add(tie.Fixture);
            }
        }

        return fixtures is null ? Array.Empty<WorldFixture>() : fixtures.AsReadOnly();
    }

    /// <summary>The human club's next unplayed fixture on the calendar, or null when the season has none left.</summary>
    public WorldFixture? NextFixture()
    {
        if (!HumanPlayer.ClubId.IsValid) return null;

        ClubId clubId = HumanPlayer.ClubId;
        int week = _world.Week;
        WorldFixture? best = null;

        foreach (WorldLeague league in _world.Leagues)
        {
            IReadOnlyList<WorldFixture> all = league.Fixtures;
            for (int index = 0; index < all.Count; index++)
            {
                WorldFixture fixture = all[index];
                if (fixture.IsPlayed || !fixture.Involves(clubId) || fixture.Week < week) continue;
                if (best is null || fixture.Week < best.Week) best = fixture;
            }
        }

        foreach (CupRoundBracket round in _world.Cup.Rounds)
        {
            if (round.Week < week) continue;
            for (int index = 0; index < round.Ties.Count; index++)
            {
                CupTie tie = round.Ties[index];
                if (tie.Fixture.IsPlayed || !tie.Fixture.Involves(clubId)) continue;
                if (best is null || tie.Fixture.Week < best.Week) best = tie.Fixture;
            }
        }

        return best;
    }

    /// <summary>Plays one world week and runs the career's weekly inbox bookkeeping.</summary>
    private void AdvanceOneWeek()
    {
        GenerateWeekStartInbox();
        _runner.AdvanceWeek();
        _runner.ClearExternalResults(); // a queued result for a fixture that never arrived must not linger
        GenerateWeekEndInbox();
        _inbox.ExpireDue(_world.Season, _world.Week);
    }

    // ---------------------------------------------------------------------------------------------
    // Human matchday: quick sim and live handoff.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Resolves every unplayed human fixture in the current week with the deterministic quick sim and queues
    /// the results on the runner. This is the "Sim Match" path; the results apply in the normal week slot when
    /// the career next advances.
    /// </summary>
    /// <returns>One result per resolved fixture, in runner order (league first).</returns>
    /// <exception cref="InvalidOperationException">
    /// The player has no fixture this week, is unattached, or a fixture was already resolved.
    /// </exception>
    public IReadOnlyList<CareerMatchResult> ResolveHumanMatchViaQuickSim()
    {
        IReadOnlyList<WorldFixture> fixtures = HumanFixturesThisWeek();
        if (fixtures.Count == 0)
        {
            throw new InvalidOperationException("The human club has no unplayed fixture this week.");
        }

        var results = new List<CareerMatchResult>(fixtures.Count);
        for (int index = 0; index < fixtures.Count; index++)
        {
            WorldFixture fixture = fixtures[index];
            if (_runner.HasExternalResult(fixture.Id)) continue;

            WorldClub home = _world.Clubs[fixture.HomeClubId];
            WorldClub away = _world.Clubs[fixture.AwayClubId];
            SelectedLineup homeLineup = SquadSelector.Select(_world, home);
            SelectedLineup awayLineup = SquadSelector.Select(_world, away);

            WorldRng rng = _world.Stream(WorldRandomStream.QuickSim).Fork((ulong)fixture.Id.Value);
            QuickSimResult result = WorldQuickSim.Simulate(
                _world, home, away, homeLineup, awayLineup, rng, knockout: fixture.IsCupTie);

            _runner.QueueExternalResult(new ExternalMatchResult(fixture.Id, result, homeLineup, awayLineup));
            results.Add(BuildMatchResult(fixture, result, homeLineup, awayLineup));
        }

        if (results.Count == 0)
        {
            throw new InvalidOperationException("Every human fixture this week already has a result.");
        }

        return results.AsReadOnly();
    }

    /// <summary>
    /// Prepares the human's live match (the first human fixture of the week): selected lineups, the player's
    /// slot, club names and the fixture metadata. W5 fills the match-engine configuration from this context;
    /// W6/W7 render it. Nothing is mutated.
    /// </summary>
    /// <returns>The live match context to play.</returns>
    /// <exception cref="InvalidOperationException">There is no human fixture this week, or one is already prepared.</exception>
    public CareerMatchContext PrepareHumanLiveMatch()
    {
        if (_liveContext is not null)
        {
            throw new InvalidOperationException("A live match is already prepared.");
        }

        IReadOnlyList<WorldFixture> fixtures = HumanFixturesThisWeek();
        if (fixtures.Count == 0)
        {
            throw new InvalidOperationException("The human club has no unplayed fixture this week.");
        }

        WorldFixture fixture = fixtures[0];
        WorldClub home = _world.Clubs[fixture.HomeClubId];
        WorldClub away = _world.Clubs[fixture.AwayClubId];
        SelectedLineup homeLineup = SquadSelector.Select(_world, home);
        SelectedLineup awayLineup = SquadSelector.Select(_world, away);

        bool humanIsHome = fixture.HomeClubId == HumanPlayer.ClubId;
        SelectedLineup humanSide = humanIsHome ? homeLineup : awayLineup;
        int shirtNumber = ShirtNumberIn(humanSide, _humanPlayerId, out bool starts, out bool onBench);

        _liveContext = new CareerMatchContext
        {
            FixtureId = fixture.Id,
            Date = _world.Date,
            Competition = fixture.Competition,
            CupRound = fixture.CupRound,
            HomeClubId = home.Id,
            AwayClubId = away.Id,
            HomeClubName = home.Name,
            AwayClubName = away.Name,
            HomeLineup = homeLineup,
            AwayLineup = awayLineup,
            HumanIsHome = humanIsHome,
            HumanInStartingXI = starts,
            HumanOnBench = onBench,
            HumanShirtNumber = shirtNumber,
            HumanAvailable = HumanPlayer.IsAvailableForSelection,
            HumanIsOnLoan = HumanPlayer.Contract.IsOnLoan
        };

        return _liveContext;
    }

    /// <summary>Drops a prepared live match without recording a result (the frontend closed it).</summary>
    public void DiscardLiveMatch()
    {
        _liveContext = null;
        _liveSetup = null;
    }

    /// <summary>
    /// Prepares the human's live match as an engine-ready bridge setup (W5): the real
    /// <c>FootballSim.MatchSimulationEngine</c> configuration built from both world lineups, the engine-shirt to
    /// world-player mapping and the human lock. Call it instead of <see cref="PrepareHumanLiveMatch"/> when the
    /// frontend is going to run the real match engine; the W4 context DTO stays available for lightweight
    /// frontends and tests.
    /// </summary>
    /// <param name="halfDurationSeconds">Simulated seconds per half (2700 = a real 45-minute half).</param>
    /// <returns>The prepared engine match.</returns>
    /// <exception cref="InvalidOperationException">There is no human fixture this week, or one is already prepared.</exception>
    public Bridge.WorldMatchSetup PrepareHumanLiveMatchSetup(float halfDurationSeconds = 2700f)
    {
        Bridge.WorldMatchSetup? existing = _liveSetup;
        if (existing is not null) return existing;

        CareerMatchContext context = PrepareHumanLiveMatch();
        WorldFixture fixture = FindFixture(context.FixtureId)
            ?? throw new InvalidOperationException($"Prepared fixture {context.FixtureId} vanished from the world.");
        _liveSetup = Bridge.WorldMatchBridge.PrepareMatch(
            _world,
            fixture,
            context.HomeLineup,
            context.AwayLineup,
            _humanPlayerId,
            halfDurationSeconds);
        return _liveSetup;
    }

    /// <summary>
    /// Commits a live match played in the real engine: the bridge converts the finished engine output into the
    /// living world's report, which is then applied through <see cref="CommitHumanLiveMatchResult"/> exactly like
    /// a quick-simulated match (stats, condition, growth minutes, career ledger).
    /// </summary>
    /// <param name="engine">The finished match engine.</param>
    /// <param name="setup">The setup the engine was created from.</param>
    /// <returns>The career-level result.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="InvalidOperationException">No live match is prepared, or the setup is for another fixture.</exception>
    public CareerMatchResult CommitHumanLiveMatchEngine(
        MatchSimulationEngine engine, Bridge.WorldMatchSetup setup)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(setup);

        if (_liveContext is not { } context || context.FixtureId != setup.FixtureId)
        {
            throw new InvalidOperationException(
                $"No live match is prepared for fixture {setup.FixtureId}; call PrepareHumanLiveMatchSetup first.");
        }

        if (engine.Rules.Phase != MatchPhase.FullTime)
        {
            throw new InvalidOperationException("The live match has not reached full time.");
        }

        LiveMatchReport report = Bridge.WorldMatchBridge.BuildLiveMatchReport(_world, setup, engine);
        return CommitHumanLiveMatchResult(report);
    }

    /// <summary>
    /// Runs the human's live match headlessly in the real engine and commits it (the CLI/test path for a "played"
    /// live match). The same code path the Godot matchday uses, without the three-dimensional frontend.
    /// </summary>
    /// <param name="halfDurationSeconds">Simulated seconds per half (2700 = a real 45-minute half).</param>
    /// <returns>The career-level result.</returns>
    public CareerMatchResult SimulateHumanLiveMatch(float halfDurationSeconds = 2700f)
    {
        Bridge.WorldMatchSetup setup = PrepareHumanLiveMatchSetup(halfDurationSeconds);
        MatchSimulationEngine engine = Bridge.WorldMatchBridge.CreateEngine(setup);
        Bridge.WorldMatchBridge.RunToFullTime(engine);
        return CommitHumanLiveMatchEngine(engine, setup);
    }

    /// <summary>
    /// Commits a played live match: converts the bridge's report into the quick-sim result shape and queues it
    /// on the runner, so the next advance applies it exactly like a simulated match (stats, growth minutes,
    /// condition, injuries, manager feedback).
    /// </summary>
    /// <param name="report">The live match's scoreline, participant lines and injuries.</param>
    /// <returns>The career-level result (scoreline, the player's own line).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is null.</exception>
    /// <exception cref="InvalidOperationException">No live match is prepared, or the report is for another fixture.</exception>
    /// <exception cref="ArgumentException">The report is internally inconsistent with the prepared lineups.</exception>
    public CareerMatchResult CommitHumanLiveMatchResult(LiveMatchReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (_liveContext is not { } context)
        {
            throw new InvalidOperationException("No live match is prepared; call PrepareHumanLiveMatch first.");
        }

        if (report.FixtureId != context.FixtureId)
        {
            throw new InvalidOperationException(
                $"The report is for fixture {report.FixtureId} but the prepared match is {context.FixtureId}.");
        }

        if (report.HomeGoals < 0 || report.AwayGoals < 0)
        {
            throw new ArgumentException("A live report cannot carry negative goals.", nameof(report));
        }

        bool levelCupTie = context.Competition == CompetitionKind.Cup && report.HomeGoals == report.AwayGoals;
        if (levelCupTie)
        {
            if (!report.WentToPenalties || report.HomePenaltyGoals == report.AwayPenaltyGoals)
            {
                throw new ArgumentException("A level cup tie must be settled by a decisive shoot-out.", nameof(report));
            }
        }

        ValidateParticipantLines(report.HomePlayers, context.HomeLineup, "home");
        ValidateParticipantLines(report.AwayPlayers, context.AwayLineup, "away");

        var result = new QuickSimResult
        {
            HomeClubId = context.HomeClubId,
            AwayClubId = context.AwayClubId,
            HomeGoals = report.HomeGoals,
            AwayGoals = report.AwayGoals,
            WentToPenalties = levelCupTie,
            HomePenaltyGoals = levelCupTie ? report.HomePenaltyGoals : 0,
            AwayPenaltyGoals = levelCupTie ? report.AwayPenaltyGoals : 0,
            HomePlayers = report.HomePlayers,
            AwayPlayers = report.AwayPlayers,
            Injuries = report.Injuries,
            Cards = report.Cards,
            Substitutions = report.Substitutions,
            HomeStats = report.HomeStats,
            AwayStats = report.AwayStats
        };

        WorldFixture fixture = FindFixture(context.FixtureId)
            ?? throw new InvalidOperationException($"Prepared fixture {context.FixtureId} vanished from the world.");
        _runner.QueueExternalResult(
            new ExternalMatchResult(context.FixtureId, result, context.HomeLineup, context.AwayLineup));
        _liveContext = null;
        _liveSetup = null;

        return BuildMatchResult(fixture, result, context.HomeLineup, context.AwayLineup);
    }

    /// <summary>Builds the career result from a fixture and an applied match result.</summary>
    private CareerMatchResult BuildMatchResult(
        WorldFixture fixture,
        QuickSimResult result,
        SelectedLineup homeLineup,
        SelectedLineup awayLineup)
    {
        WorldClub home = _world.Clubs[fixture.HomeClubId];
        WorldClub away = _world.Clubs[fixture.AwayClubId];
        bool humanIsHome = fixture.HomeClubId == HumanPlayer.ClubId;
        SelectedLineup humanSide = humanIsHome ? homeLineup : awayLineup;
        bool started = ContainsPlayer(humanSide.StartingXI, _humanPlayerId);
        PlayerMatchLine? line = result.LineOf(_humanPlayerId);

        ClubId winner = result.WinnerClubId;
        bool humanWon = winner.IsValid && winner == HumanPlayer.ClubId;
        bool humanDrew = !winner.IsValid;

        return new CareerMatchResult
        {
            FixtureId = fixture.Id,
            Date = _world.Date,
            Competition = fixture.Competition,
            HomeClubId = home.Id,
            AwayClubId = away.Id,
            HomeClubName = home.Name,
            AwayClubName = away.Name,
            HomeGoals = result.HomeGoals,
            AwayGoals = result.AwayGoals,
            HumanIsHome = humanIsHome,
            HumanStarted = started,
            HumanLine = line,
            HumanWon = humanWon,
            HumanDrew = humanDrew,
            HumanLost = winner.IsValid && !humanWon
        };
    }

    /// <summary>Rejects participant lines for players who were not in that side's matchday squad.</summary>
    private static void ValidateParticipantLines(
        IReadOnlyList<PlayerMatchLine> lines, SelectedLineup lineup, string side)
    {
        ArgumentNullException.ThrowIfNull(lines);

        for (int index = 0; index < lines.Count; index++)
        {
            PlayerId playerId = lines[index].PlayerId;
            if (ContainsPlayer(lineup.StartingXI, playerId) || ContainsPlayer(lineup.Bench, playerId)) continue;
            throw new ArgumentException(
                $"The {side} report includes {playerId}, who is not in that side's matchday squad.",
                nameof(lines));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Inbox actions.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Answers an actionable inbox message. Transfers/loans/trials are executed on accept; a contract renewal
    /// signs the new deal on accept and leaves the expiring contract alone on decline.
    /// </summary>
    /// <param name="messageId">The message to answer.</param>
    /// <param name="accept">True to accept, false to decline.</param>
    /// <returns>
    /// True when the answer was applied; false when the message is unknown, no longer answerable, or the
    /// accepted move is refused by the world (the message then stays open, so the frontend can explain and
    /// retry or decline — nothing is silently half-executed).
    /// </returns>
    public bool RespondToOffer(int messageId, bool accept)
    {
        CareerInboxMessage? message = _inbox.Get(messageId);
        if (message is null) return false;
        message.MarkRead();
        if (!message.IsAwaitingResponse(_world.Season, _world.Week)) return false;

        switch (message)
        {
            case TransferOfferMessage transfer:
            {
                if (!accept)
                {
                    message.MarkResolved();
                    _inbox.ResolveAll(CareerInboxKind.TransferOffer, message.Id);
                    _inbox.ResolveAll(CareerInboxKind.LoanOffer);
                    AddManagerNote("Offer declined", $"You turned down {ClubName(transfer.ToClubId)}.");
                    return true;
                }

                bool moved = TransferMarket.ExecuteTransfer(
                    _world,
                    _humanPlayerId,
                    transfer.ToClubId,
                    transfer.Fee,
                    transfer.WeeklyWage,
                    transfer.OfferKind,
                    RenewalContractSeasons);
                if (!moved) return false;

                message.MarkResolved();
                _inbox.ResolveAll(CareerInboxKind.TransferOffer, message.Id);
                _inbox.ResolveAll(CareerInboxKind.LoanOffer);
                AddManagerNote(
                    "Transfer completed",
                    $"You joined {ClubName(transfer.ToClubId)} from {ClubName(transfer.FromClubId)}. " +
                    "The move is recorded in your career history.");
                return true;
            }

            case LoanOfferMessage loan:
            {
                if (!accept)
                {
                    message.MarkResolved();
                    _inbox.ResolveAll(CareerInboxKind.LoanOffer, message.Id);
                    AddManagerNote("Loan declined", $"You turned down the loan to {ClubName(loan.ToClubId)}.");
                    return true;
                }

                bool loaned = TransferMarket.ExecuteLoan(_world, _humanPlayerId, loan.ToClubId, loan.LoanEndSeason);
                if (!loaned) return false;

                message.MarkResolved();
                _inbox.ResolveAll(CareerInboxKind.LoanOffer, message.Id);
                AddManagerNote(
                    "Loan completed",
                    $"You joined {ClubName(loan.ToClubId)} on loan until the end of season {loan.LoanEndSeason}.");
                return true;
            }

            case TrialInviteMessage trial:
            {
                if (!accept)
                {
                    message.MarkResolved();
                    AddManagerNote("Trial declined", $"You turned down the trial at {ClubName(trial.ClubId)}.");
                    return true;
                }

                bool signed = TransferMarket.ExecuteTransfer(
                    _world,
                    _humanPlayerId,
                    trial.ClubId,
                    0L,
                    trial.WeeklyWage,
                    TransferKind.Free,
                    RenewalContractSeasons);
                if (!signed) return false;

                message.MarkResolved();
                AddManagerNote(
                    "Trial completed",
                    $"You signed for {ClubName(trial.ClubId)}. The contract is recorded in your career history.");
                return true;
            }

            case ContractRenewalMessage renewal:
            {
                if (!accept)
                {
                    message.MarkResolved();
                    AddManagerNote(
                        "Renewal declined",
                        $"You declined the renewal offer from {ClubName(renewal.ClubId)}.");
                    return true;
                }

                WorldClub? club = HumanClub;
                if (club is null || club.Id != renewal.ClubId)
                {
                    throw new InvalidOperationException("The renewal club is no longer the player's club.");
                }

                PlayerContract contract = ContractAndFinanceSystem.NewContract(
                    _world,
                    HumanPlayer,
                    club,
                    _world.Stream(WorldRandomStream.Contracts),
                    renewal.Seasons);
                if (renewal.WeeklyWage > 0L) contract.WeeklyWage = renewal.WeeklyWage;
                club.Finances.WeeklyWageBill = SquadWageBill(club);
                message.MarkResolved();
                AddManagerNote(
                    "Contract renewed",
                    $"You signed a new {renewal.Seasons}-season deal at {ClubName(renewal.ClubId)}.");
                return true;
            }

            default:
                // Informational messages are settled by being read/answered.
                message.MarkResolved();
                return true;
        }
    }

    /// <summary>
    /// The anti-soft-lock safeguard: an unattached career player asks for a trial. Returns a
    /// <see cref="TrialInviteMessage"/> the frontend can accept, or signs him directly at an amateur club when
    /// no club formally invites him — a career can never be stranded without a club.
    /// </summary>
    /// <returns>The trial invite (or the direct-signing feedback message) added to the inbox.</returns>
    /// <exception cref="InvalidOperationException">The player already has a club, is retired, or asked twice.</exception>
    public CareerInboxMessage RequestAmateurTrial()
    {
        if (HumanPlayer.ClubId.IsValid)
        {
            throw new InvalidOperationException("The player already has a club; a trial is only for free agents.");
        }

        if (HumanPlayer.IsRetired)
        {
            throw new InvalidOperationException("A retired player cannot request a trial.");
        }

        if (HasOpenTrialInvite(out TrialInviteMessage? existing)) return existing!;

        RosterGroup area = RosterAnalyser.GroupOf(HumanPlayer);
        for (int tier = TrialMinimumTier; tier <= TrialMaximumTier; tier++)
        {
            IReadOnlyList<WorldClub> clubs = _world.Clubs.InTier(tier);
            for (int index = 0; index < clubs.Count; index++)
            {
                WorldClub club = clubs[index];
                if (club.Squad.Count >= MaximumSquadSize) continue;
                RosterGroup? shortfall = RosterAnalyser.FindShortfall(_world, club);
                if (shortfall is not null && shortfall.Value != area) continue;

                long wage = ContractAndFinanceSystem.WageDemand(HumanPlayer, tier);
                if (club.Finances.WeeklyWageBill + wage > club.Finances.WeeklyWageBudget) continue;
                return AddTrialInvite(club, wage);
            }
        }

        // Last resort: an amateur club takes him on amateur terms, so the career can always continue.
        IReadOnlyList<WorldClub> amateurs = _world.Clubs.InTier(TrialMaximumTier);
        if (amateurs.Count == 0) throw new InvalidOperationException("The world has no amateur clubs to join.");
        WorldClub fallback = amateurs[0];
        return AddTrialInvite(fallback, 0L);
    }

    /// <summary>True when a trial invite is already waiting for an answer.</summary>
    private bool HasOpenTrialInvite(out TrialInviteMessage? invite)
    {
        for (int index = 0; index < _inbox.Messages.Count; index++)
        {
            CareerInboxMessage message = _inbox.Messages[index];
            if (message.Kind != CareerInboxKind.TrialInvite) continue;
            if (!message.IsAwaitingResponse(_world.Season, _world.Week)) continue;
            invite = (TrialInviteMessage)message;
            return true;
        }

        invite = null;
        return false;
    }

    private TrialInviteMessage AddTrialInvite(WorldClub club, long weeklyWage)
    {
        int expiresWeek = Math.Min(WorldCalendar.WeeksPerSeason, _world.Week + TrialOfferLifetimeWeeks);
        var message = new TrialInviteMessage(
            _nextMessageId++,
            _world.Season,
            _world.Week,
            $"Trial invite: {club.Name}",
            $"{club.Name} ({club.City}, tier {club.Tier}) are offering you a trial. " +
            "Accept to sign amateur terms and get back on a pitch.",
            club.Id,
            weeklyWage,
            _world.Season,
            expiresWeek);
        _inbox.Add(message);
        return message;
    }

    // ---------------------------------------------------------------------------------------------
    // Player-facing views.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The Authentic-mode public view of the career player: grouped current attributes, trend arrows, emergent
    /// identity and a noisy coach report. It structurally carries no ceiling, plasticity or latent value, and
    /// it consumes no simulation randomness (the report is derived from a diagnostics fork).
    /// </summary>
    public PlayerPublicView GetPlayerPublicView()
    {
        // A stateless derived stream: the view is stable for the same player and it never creates or advances
        // a world stream, so a UI query cannot change the simulation's random state at all.
        WorldRng rng = WorldRng.For(_world.Seed, WorldRandomStream.Diagnostics)
            .Fork((ulong)_humanPlayerId.Value);
        return PlayerPublicViewBuilder.Build(HumanPlayer, JudgingAbilityOf(HumanClub), rng);
    }

    /// <summary>
    /// The coach judging ability that scales report noise for a club: elite tier-1 staff are sharp, tier-6
    /// amateur staff are fuzzy (plan §4.6). A free agent is judged by the worst band.
    /// </summary>
    /// <param name="club">The club whose staff are judging, or null for no club.</param>
    public static float JudgingAbilityOf(WorldClub? club)
    {
        int tier = club?.Tier ?? 6;
        float ability = 0.95f - 0.13f * (tier - 1);
        return Math.Clamp(ability, 0.20f, 0.95f);
    }

    /// <summary>Squad status of the career player at his club (or "Free agent" / "Retired").</summary>
    public string SquadStatus
    {
        get
        {
            if (HumanPlayer.IsRetired) return "Retired";
            WorldClub? club = HumanClub;
            if (club is null) return "Free agent";
            return SquadSelector.SquadStatusOf(_world, club, _humanPlayerId);
        }
    }

    /// <summary>The league table of the player's current division, or an empty list for a free agent.</summary>
    public IReadOnlyList<LeagueTableRow> CurrentLeagueTable()
    {
        WorldClub? club = HumanClub;
        if (club is null) return Array.Empty<LeagueTableRow>();

        WorldLeague league = _world.LeagueForTier(club.Tier);
        return StandingsCalculator.Calculate(league);
    }

    // ---------------------------------------------------------------------------------------------
    // Weekly inbox generation.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs before the week is played: transfer/loan offers at the start of each window, the mid-season coach
    /// report and nothing else. Every generator is guarded by a saved marker, so a reload cannot duplicate a
    /// message.
    /// </summary>
    private void GenerateWeekStartInbox()
    {
        int week = _world.Week;
        int season = _world.Season;

        if ((week == WorldCalendar.PreSeasonFirstWeek || week == WorldCalendar.WinterBreakFirstWeek) &&
            (season != _lastOfferSeason || week != _lastOfferWindowWeek))
        {
            _lastOfferSeason = season;
            _lastOfferWindowWeek = week;
            GenerateWindowOffers();
        }

        if (week == WorldCalendar.WinterBreakFirstWeek && season != _lastMidSeasonReportSeason)
        {
            _lastMidSeasonReportSeason = season;
            AddCoachReport("Mid-season report", "A half-season has passed; the staff have reassessed you.");
        }
    }

    /// <summary>Generates the human's own transfer and loan offers for the current window, once.</summary>
    private void GenerateWindowOffers()
    {
        if (!HumanPlayer.ClubId.IsValid || HumanPlayer.IsRetired) return;
        if (HumanPlayer.Contract.IsOnLoan) return;

        // Clubs have to have seen him play: a debutant is not chased in his first pre-season.
        if (HumanPlayer.CurrentSeason.Appearances == 0 && _history.SeasonsPlayed == 0) return;

        WorldRng rng = _world.Stream(WorldRandomStream.Career);
        IReadOnlyList<TransferOffer> offers = TransferMarket.GenerateOffersForPlayer(_world, _humanPlayerId, rng);
        for (int index = 0; index < offers.Count; index++)
        {
            TransferOffer offer = offers[index];

            // Normalise the W3 market's raw bid into an executable amateur/pro transfer: amateur buyers pay no
            // fee at all, semi-pro buyers are capped, and an unaffordable fee is dropped rather than shown as
            // an offer the career could never accept.
            WorldClub? buyer = HumanClubOf(offer.ToClubId);
            if (buyer is null) continue;
            long fee = offer.Fee;
            TransferKind kind = offer.Kind;
            if (buyer.Tier >= 5)
            {
                fee = 0L;
                kind = TransferKind.Free;
            }
            else if (buyer.Tier == 4 && fee > SemiProfessionalFeeCeiling)
            {
                continue;
            }

            if (fee > buyer.Finances.TransferBudget || fee > buyer.Finances.Balance) continue;

            int expiresWeek = Math.Min(WorldCalendar.WeeksPerSeason, _world.Week + OfferLifetimeWeeks);
            var message = new TransferOfferMessage(
                _nextMessageId++,
                _world.Season,
                _world.Week,
                $"Transfer offer: {buyer.Name}",
                $"{buyer.Name} (tier {buyer.Tier}) want to sign you" +
                (fee > 0L ? $" for {fee:N0}" : " on a free transfer") +
                $", offering {offer.WeeklyWage:N0} a week. The offer expires in week {expiresWeek}.",
                offer.FromClubId,
                offer.ToClubId,
                fee,
                offer.WeeklyWage,
                kind,
                offer.CreatedSeason,
                expiresWeek);
            _inbox.Add(message);
        }

        AddLoanOfferIfEligible();
    }

    /// <summary>Semi-professional transfer-fee ceiling (mirrors the W3 market's tier-4 rule).</summary>
    private const long SemiProfessionalFeeCeiling = 500_000L;

    private WorldClub? HumanClubOf(ClubId clubId) =>
        _world.Clubs.TryGet(clubId, out WorldClub? club) ? club : null;

    /// <summary>
    /// Adds one development-loan offer for a young, fringe career player when a lower-division club needs his
    /// area (plan §5.4 loans). The check walks clubs in id order, so it is deterministic without any RNG.
    /// </summary>
    private void AddLoanOfferIfEligible()
    {
        WorldClub? club = HumanClub;
        if (club is null || club.Tier > 3) return;
        if (HumanPlayer.Age is < 18 or > 21) return;
        if (HumanPlayer.CurrentSeason.Appearances >= 8) return;

        RosterGroup area = RosterAnalyser.GroupOf(HumanPlayer);
        for (int tier = 4; tier <= TrialMaximumTier; tier++)
        {
            IReadOnlyList<WorldClub> receivers = _world.Clubs.InTier(tier);
            for (int index = 0; index < receivers.Count; index++)
            {
                WorldClub receiver = receivers[index];
                if (receiver.Squad.Count >= MaximumSquadSize) continue;
                RosterGroup? shortfall = RosterAnalyser.FindShortfall(_world, receiver);
                if (shortfall is null || shortfall.Value != area) continue;
                if (receiver.Finances.WeeklyWageBill + HumanPlayer.Contract.WeeklyWage >
                    receiver.Finances.WeeklyWageBudget)
                {
                    continue;
                }

                int expiresWeek = Math.Min(WorldCalendar.WeeksPerSeason, _world.Week + OfferLifetimeWeeks);
                var message = new LoanOfferMessage(
                    _nextMessageId++,
                    _world.Season,
                    _world.Week,
                    $"Loan offer: {receiver.Name}",
                    $"{receiver.Name} (tier {receiver.Tier}) want you on loan until the end of the season.",
                    club.Id,
                    receiver.Id,
                    _world.Season,
                    _world.Season,
                    expiresWeek);
                _inbox.Add(message);
                return;
            }
        }
    }

    /// <summary>
    /// Runs after the week is played: injury notices and newly drawn cup rounds. Both are observations of the
    /// finished week, so they never change the simulation.
    /// </summary>
    private void GenerateWeekEndInbox()
    {
        GenerateInjuryNotice();
        GenerateCupDrawNotice();
    }

    private void GenerateInjuryNotice()
    {
        InjuryState injury = HumanPlayer.Injury;
        if (!injury.IsInjured)
        {
            _injuryNotified = false;
            return;
        }

        if (_injuryNotified) return;
        _injuryNotified = true;

        string kind = InjuryName(injury.Kind);
        var message = new InjuryNoticeMessage(
            _nextMessageId++,
            _world.Season,
            _world.Week,
            $"Injury: {kind}",
            $"You have suffered {kind.ToLowerInvariant()} and are expected to miss about " +
            $"{injury.WeeksOutRemaining} week(s). The medical staff will keep working on it.",
            injury.Kind,
            injury.Severity,
            Math.Max(1, injury.WeeksOutRemaining));
        _inbox.Add(message);
    }

    private void GenerateCupDrawNotice()
    {
        CupRoundBracket? round = _world.Cup.CurrentRound;
        if (round is null) return;

        int roundValue = (int)round.Round;
        if (roundValue <= _lastCupRoundSeen) return;
        _lastCupRoundSeen = roundValue;
        _lastCupRoundSeen = Math.Min(_lastCupRoundSeen, (int)CupRound.Final);

        ClubId clubId = HumanPlayer.ClubId;
        if (!clubId.IsValid) return;

        for (int index = 0; index < round.Ties.Count; index++)
        {
            CupTie tie = round.Ties[index];
            if (!tie.Fixture.Involves(clubId)) continue;
            bool isHome = tie.Fixture.HomeClubId == clubId;
            ClubId opponent = isHome ? tie.Fixture.AwayClubId : tie.Fixture.HomeClubId;
            var message = new CupDrawMessage(
                _nextMessageId++,
                _world.Season,
                _world.Week,
                $"Cup draw: {CupRoundName(round.Round)}",
                $"The {CupRoundName(round.Round)} has been drawn: you travel to {ClubName(opponent)}." +
                (isHome ? " You are at home." : " You are away."),
                round.Round,
                opponent,
                isHome,
                round.Week);
            _inbox.Add(message);
            return;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Season close and rollover.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Closes a finished season: possibly stops for a contract renewal first, then resolves the tables and
    /// play-offs, records the career ledger, rolls the world over and starts the next season.
    /// </summary>
    /// <returns>An inbox stop for the renewal, or the season-rolled-over event; null to keep advancing.</returns>
    private CareerAdvanceResult? CloseSeason()
    {
        bool expiring = HumanContractExpiresThisSeason();
        if (expiring && !_renewalOfferCreated)
        {
            _renewalOfferCreated = true;
            CareerInboxMessage renewal = AddContractRenewalOffer();
            return new CareerAdvanceResult
            {
                Kind = CareerEventKind.InboxAction,
                Date = _world.Date,
                Message = renewal
            };
        }

        int finishedSeason = _world.Season;
        WorldClub? seasonClub = HumanClub;
        int seasonEndTier = seasonClub?.Tier ?? _seasonStartClubTier;

        SeasonSummary summary = _runner.ResolveSeason();
        RecordCareerSeason(summary, finishedSeason, seasonEndTier);

        ClubId clubBeforeRollover = HumanPlayer.ClubId;
        SeasonRolloverSummary rollover = _ecosystem.Rollover(summary);
        _runner.BeginSeason();

        _seasonStartClubTier = HumanClub?.Tier ?? 6;
        ResetSeasonMarkers();
        AnnounceNextSeason(summary, rollover, clubBeforeRollover);

        return new CareerAdvanceResult
        {
            Kind = CareerEventKind.SeasonRolledOver,
            Date = _world.Date,
            SeasonSummary = summary,
            Rollover = rollover
        };
    }

    /// <summary>True when the player's deal ends with the season being closed.</summary>
    private bool HumanContractExpiresThisSeason()
    {
        WorldClub? club = HumanClub;
        if (club is null || HumanPlayer.IsRetired) return false;
        return HumanPlayer.Contract.EndSeason <= _world.Season;
    }

    private CareerInboxMessage AddContractRenewalOffer()
    {
        WorldClub club = HumanClub!;
        int seasons = RenewalContractSeasons;
        long wage = ContractAndFinanceSystem.WageDemand(HumanPlayer, club.Tier);
        var message = new ContractRenewalMessage(
            _nextMessageId++,
            _world.Season,
            _world.Week,
            $"Contract renewal: {club.Name}",
            $"{club.Name} want to keep you. Accept to sign a {seasons}-season deal on {wage:N0} a week; " +
            "decline and the club will decide your future at the end of the season.",
            club.Id,
            seasons,
            wage,
            _world.Season,
            WorldCalendar.WeeksPerSeason);
        _inbox.Add(message);
        return message;
    }

    /// <summary>Records the finished season in the ledger, with honours and division movement.</summary>
    private void RecordCareerSeason(SeasonSummary summary, int finishedSeason, int seasonEndTier)
    {
        WorldClub? club = HumanClub;
        PlayerSeasonStats stats = HumanPlayer.CurrentSeason;
        var honours = new List<string>(3);

        if (club is not null)
        {
            if (seasonEndTier is >= 1 and <= 6 &&
                summary.ChampionsByTier.Count >= seasonEndTier &&
                summary.ChampionsByTier[seasonEndTier - 1] == club.Id)
            {
                honours.Add($"League champion ({_world.LeagueForTier(seasonEndTier).Name})");
            }

            if (summary.CupChampionClubId.IsValid && summary.CupChampionClubId == club.Id)
            {
                honours.Add($"{_world.Cup.Name} winner");
            }

            PlayerId divisionTopScorer = FindDivisionTopScorer(seasonEndTier);
            if (divisionTopScorer.IsValid && divisionTopScorer == _humanPlayerId && stats.Goals > 0)
            {
                honours.Add($"Golden Boot ({_world.LeagueForTier(seasonEndTier).Name})");
            }
        }

        string movement = string.Empty;
        if (club is not null)
        {
            for (int index = 0; index < summary.DivisionMoves.Count; index++)
            {
                DivisionMove move = summary.DivisionMoves[index];
                if (move.ClubId != club.Id) continue;
                movement = move.ToTier < move.FromTier ? "Promoted" : "Relegated";
                break;
            }
        }

        _history.AddSeason(new CareerSeasonRecord
        {
            Season = finishedSeason,
            ClubId = club?.Id ?? ClubId.None,
            ClubName = club?.Name ?? "Free agent",
            Tier = seasonEndTier,
            Appearances = stats.Appearances,
            Starts = stats.Starts,
            SubstituteAppearances = stats.SubstituteAppearances,
            MinutesPlayed = stats.MinutesPlayed,
            Goals = stats.Goals,
            Assists = stats.Assists,
            YellowCards = stats.YellowCards,
            RedCards = stats.RedCards,
            AverageRating = stats.AverageRating,
            Honours = honours.AsReadOnly(),
            DivisionMovement = movement
        });

        // The player's own moves this season, from the world's append-only transfer log.
        IReadOnlyList<TransferRecord> log = _world.TransferLog;
        for (int index = 0; index < log.Count; index++)
        {
            TransferRecord record = log[index];
            if (record.PlayerId != _humanPlayerId || record.Season != finishedSeason) continue;
            if (record.Kind is TransferKind.YouthIntake) continue;
            _history.AddTransfer(new CareerTransferRecord
            {
                Season = record.Season,
                Week = record.Week,
                FromClubId = record.FromClubId,
                ToClubId = record.ToClubId,
                FromClubName = record.FromClubId.IsValid ? ClubName(record.FromClubId) : "Free agent",
                ToClubName = record.ToClubId.IsValid ? ClubName(record.ToClubId) : "Free agent",
                Kind = record.Kind,
                Fee = record.Fee
            });
        }

        AddCoachReport(
            $"Season {finishedSeason} review",
            $"The staff reviewed your {finishedSeason} season at {club?.Name ?? "your club"}.");

        if (honours.Count > 0)
        {
            var award = new AwardNoticeMessage(
                _nextMessageId++,
                _world.Season,
                _world.Week,
                "Honours",
                $"Your season brought silverware: {string.Join(", ", honours)}.",
                honours.AsReadOnly());
            _inbox.Add(award);
        }
    }

    /// <summary>Announces the new season: a pre-season report and the manager's view of the player's status.</summary>
    private void AnnounceNextSeason(
        SeasonSummary summary, SeasonRolloverSummary rollover, ClubId clubBeforeRollover)
    {
        WorldClub? club = HumanClub;
        if (club is null)
        {
            AddManagerNote(
                "Released",
                $"Your contract at {ClubName(clubBeforeRollover)} expired and was not renewed. " +
                "You are a free agent: request an amateur trial to find a new club.");
        }
        else if (club.Id != clubBeforeRollover)
        {
            AddManagerNote(
                "Loan ended",
                $"Your loan finished and you are back at {club.Name}.");
        }

        AddCoachReport(
            $"Pre-season {_world.Season} report",
            $"The staff have reviewed the squad ahead of the {_world.Season} season.");

        if (club is not null)
        {
            AddManagerWelcome(_world, club, _humanPlayerId);
        }

        _ = summary;
        _ = rollover;
    }

    private void ResetSeasonMarkers()
    {
        _renewalOfferCreated = false;
        _lastMidSeasonReportSeason = -1;
        _lastCupRoundSeen = 0;
        _injuryNotified = false;
    }

    // ---------------------------------------------------------------------------------------------
    // Inbox message helpers.
    // ---------------------------------------------------------------------------------------------

    private CoachReportMessage AddCoachReport(string subject, string intro)
    {
        CoachReport report = CoachReportGenerator.Generate(
            HumanPlayer, JudgingAbilityOf(HumanClub), CoachReportRng(_world.Season, _world.Week));

        var message = new CoachReportMessage(
            _nextMessageId++,
            _world.Season,
            _world.Week,
            subject,
            string.IsNullOrEmpty(intro) ? report.Text : $"{intro} {report.Text}",
            report.Summary,
            report.Sentences);
        _inbox.Add(message);
        return message;
    }

    /// <summary>
    /// A calendar-keyed report stream: the same season/week always yields the same report, and reports can
    /// never shift each other's draws (each is an independent fork of the career stream's seed).
    /// </summary>
    private WorldRng CoachReportRng(int season, int week) =>
        WorldRng.For(_world.Seed, WorldRandomStream.Career)
            .Fork(CoachReportForkBase + (ulong)(season * 60 + week));

    private void AddManagerWelcome(WorldState world, WorldClub club, PlayerId humanId)
    {
        string status = SquadSelector.SquadStatusOf(world, club, humanId);
        string verdict = status switch
        {
            "Key Starter" => "You are in the manager's first-choice XI.",
            "First Team" => "You are in the senior matchday squad and pushing for a start.",
            "Rotation / Bench" => "You are squad cover for now: train well and take your chances.",
            _ => "You are outside the first-team picture for now."
        };

        var message = new ManagerFeedbackMessage(
            _nextMessageId++,
            world.Season,
            world.Week,
            $"Manager's note: {club.Name}",
            $"{club.Name} manager {world.Managers[club.ManagerId].DisplayName} has assessed you: {verdict}",
            status);
        _inbox.Add(message);
    }

    private void AddManagerNote(string subject, string body)
    {
        var message = new ManagerFeedbackMessage(
            _nextMessageId++,
            _world.Season,
            _world.Week,
            subject,
            body,
            subject);
        _inbox.Add(message);
    }

    // ---------------------------------------------------------------------------------------------
    // Persistence: the career half of a save.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Captures the career state for the save layer. The world itself is captured separately by the save
    /// manager; this object only holds what the career owns.
    /// </summary>
    /// <returns>The career save data.</returns>
    /// <exception cref="InvalidOperationException">
    /// A live match is prepared or a match result is queued: save between matchdays.
    /// </exception>
    internal CareerSaveData ExportSaveData()
    {
        if (!CanSave)
        {
            throw new InvalidOperationException(
                "The career cannot be saved while a match is prepared or its result is queued; " +
                "finish the matchday first.");
        }

        var data = new CareerSaveData
        {
            Config = CareerConfigSave.FromConfig(_config),
            HumanPlayerId = _humanPlayerId.Value,
            NextMessageId = _nextMessageId,
            SeasonStartClubTier = _seasonStartClubTier,
            RenewalOfferCreated = _renewalOfferCreated,
            LastOfferSeason = _lastOfferSeason,
            LastOfferWindowWeek = _lastOfferWindowWeek,
            LastMidSeasonReportSeason = _lastMidSeasonReportSeason,
            LastCupRoundSeen = _lastCupRoundSeen,
            InjuryNotified = _injuryNotified
        };

        for (int index = 0; index < _inbox.Messages.Count; index++)
        {
            data.Messages.Add(ToSave(_inbox.Messages[index]));
        }

        for (int index = 0; index < _history.Seasons.Count; index++)
        {
            CareerSeasonRecord season = _history.Seasons[index];
            data.Seasons.Add(new CareerSeasonSave
            {
                Season = season.Season,
                ClubId = season.ClubId.Value,
                ClubName = season.ClubName,
                Tier = season.Tier,
                Appearances = season.Appearances,
                Starts = season.Starts,
                SubstituteAppearances = season.SubstituteAppearances,
                MinutesPlayed = season.MinutesPlayed,
                Goals = season.Goals,
                Assists = season.Assists,
                YellowCards = season.YellowCards,
                RedCards = season.RedCards,
                AverageRating = season.AverageRating,
                Honours = ToArray(season.Honours),
                DivisionMovement = season.DivisionMovement
            });
        }

        for (int index = 0; index < _history.Transfers.Count; index++)
        {
            CareerTransferRecord transfer = _history.Transfers[index];
            data.Transfers.Add(new CareerTransferSave
            {
                Season = transfer.Season,
                Week = transfer.Week,
                FromClubId = transfer.FromClubId.Value,
                ToClubId = transfer.ToClubId.Value,
                FromClubName = transfer.FromClubName,
                ToClubName = transfer.ToClubName,
                Kind = (int)transfer.Kind,
                Fee = transfer.Fee
            });
        }

        WorldSeasonRunnerState runnerState = _runner.CaptureState();
        data.RunnerFocusSeason = runnerState.FocusSeason;
        for (int index = 0; index < runnerState.TrainingFocusSetThisSeason.Count; index++)
        {
            data.RunnerFocusSetPlayerIds.Add(runnerState.TrainingFocusSetThisSeason[index].Value);
        }
        data.RunnerInjuriesThisSeason = runnerState.InjuriesThisSeason;
        data.RunnerYellowCardsThisSeason = runnerState.YellowCardsThisSeason;
        data.RunnerRedCardsThisSeason = runnerState.RedCardsThisSeason;

        WorldEcosystemState ecosystemState = _ecosystem.CaptureState();
        data.EcosystemInitialized = ecosystemState.Initialized;
        data.EcosystemTransferLogCursor = ecosystemState.TransferLogCursor;
        data.EcosystemInjuriesRecoveredSinceRollover = ecosystemState.InjuriesRecoveredSinceRollover;
        data.EcosystemEmergencySequence = ecosystemState.EmergencySequence;

        return data;
    }

    /// <summary>
    /// Rebuilds a career around a world restored from a save. Every mutable source (world, ecosystem counters,
    /// runner counters, inbox, ledger) is restored before the session can advance again; the career's derived
    /// protection state (the human is frontend-managed and manually focused) is re-registered.
    /// </summary>
    /// <param name="world">The world restored from the world snapshot.</param>
    /// <param name="data">The career save data.</param>
    /// <returns>A career that continues the saved one.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="InvalidOperationException">The save is internally inconsistent.</exception>
    internal static CareerSession RestoreFromSave(WorldState world, CareerSaveData data)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(data);
        if (data.SchemaVersion != 1)
        {
            throw new InvalidOperationException($"Unsupported career save schema version {data.SchemaVersion}.");
        }

        CareerConfig config = data.Config.ToConfig();
        config.Validate();

        var humanId = new PlayerId(data.HumanPlayerId);
        if (!humanId.IsValid || !world.Players.TryGet(humanId, out WorldPlayer? human) || human is null)
        {
            throw new InvalidOperationException($"The save's career player {humanId} is not in the restored world.");
        }

        var ecosystem = new WorldEcosystem(world);
        ecosystem.RestoreState(new WorldEcosystemState(
            data.EcosystemInitialized,
            data.EcosystemTransferLogCursor,
            data.EcosystemInjuriesRecoveredSinceRollover,
            data.EcosystemEmergencySequence));

        var runner = new WorldSeasonRunner(world, ecosystem);
        var focusSet = new PlayerId[data.RunnerFocusSetPlayerIds.Count];
        for (int index = 0; index < focusSet.Length; index++)
        {
            focusSet[index] = new PlayerId(data.RunnerFocusSetPlayerIds[index]);
        }
        runner.RestoreState(new WorldSeasonRunnerState(
            data.RunnerFocusSeason,
            focusSet,
            data.RunnerInjuriesThisSeason,
            data.RunnerYellowCardsThisSeason,
            data.RunnerRedCardsThisSeason));

        var session = new CareerSession(world, ecosystem, runner, config, humanId)
        {
            _nextMessageId = Math.Max(1, data.NextMessageId),
            _seasonStartClubTier = data.SeasonStartClubTier,
            _renewalOfferCreated = data.RenewalOfferCreated,
            _lastOfferSeason = data.LastOfferSeason,
            _lastOfferWindowWeek = data.LastOfferWindowWeek,
            _lastMidSeasonReportSeason = data.LastMidSeasonReportSeason,
            _lastCupRoundSeen = data.LastCupRoundSeen,
            _injuryNotified = data.InjuryNotified
        };

        for (int index = 0; index < data.Messages.Count; index++)
        {
            CareerInboxMessage message = FromSave(data.Messages[index]);
            session._inbox.Add(message);
            if (message.Id >= session._nextMessageId) session._nextMessageId = message.Id + 1;
        }

        for (int index = 0; index < data.Seasons.Count; index++)
        {
            CareerSeasonSave saved = data.Seasons[index];
            session._history.AddSeason(new CareerSeasonRecord
            {
                Season = saved.Season,
                ClubId = new ClubId(saved.ClubId),
                ClubName = saved.ClubName ?? string.Empty,
                Tier = saved.Tier,
                Appearances = saved.Appearances,
                Starts = saved.Starts,
                SubstituteAppearances = saved.SubstituteAppearances,
                MinutesPlayed = saved.MinutesPlayed,
                Goals = saved.Goals,
                Assists = saved.Assists,
                YellowCards = saved.YellowCards,
                RedCards = saved.RedCards,
                AverageRating = saved.AverageRating,
                Honours = saved.Honours ?? Array.Empty<string>(),
                DivisionMovement = saved.DivisionMovement ?? string.Empty
            });
        }

        for (int index = 0; index < data.Transfers.Count; index++)
        {
            CareerTransferSave saved = data.Transfers[index];
            session._history.AddTransfer(new CareerTransferRecord
            {
                Season = saved.Season,
                Week = saved.Week,
                FromClubId = new ClubId(saved.FromClubId),
                ToClubId = new ClubId(saved.ToClubId),
                FromClubName = saved.FromClubName ?? string.Empty,
                ToClubName = saved.ToClubName ?? string.Empty,
                Kind = (TransferKind)saved.Kind,
                Fee = saved.Fee
            });
        }

        // Derived career protection state, deliberately not in the save file.
        world.ExternallyManagedPlayers.Add(humanId);
        runner.MarkTrainingFocusManual(humanId);

        return session;
    }

    private static CareerInboxMessageSave ToSave(CareerInboxMessage message)
    {
        var save = new CareerInboxMessageSave
        {
            Id = message.Id,
            Kind = (int)message.Kind,
            Season = message.Season,
            Week = message.Week,
            Subject = message.Subject,
            Body = message.Body,
            IsRead = message.IsRead,
            IsResolved = message.IsResolved,
            ExpiresSeason = message.ExpiresSeason,
            ExpiresWeek = message.ExpiresWeek
        };

        switch (message)
        {
            case CoachReportMessage coach:
                save.Summary = coach.Summary;
                save.Details = ToArray(coach.Sentences);
                break;

            case TransferOfferMessage transfer:
                save.FromClubId = transfer.FromClubId.Value;
                save.ToClubId = transfer.ToClubId.Value;
                save.Fee = transfer.Fee;
                save.WeeklyWage = transfer.WeeklyWage;
                save.OfferKind = (int)transfer.OfferKind;
                break;

            case LoanOfferMessage loan:
                save.FromClubId = loan.FromClubId.Value;
                save.ToClubId = loan.ToClubId.Value;
                save.LoanEndSeason = loan.LoanEndSeason;
                break;

            case TrialInviteMessage trial:
                save.ToClubId = trial.ClubId.Value;
                save.WeeklyWage = trial.WeeklyWage;
                break;

            case ContractRenewalMessage renewal:
                save.ToClubId = renewal.ClubId.Value;
                save.Seasons = renewal.Seasons;
                save.WeeklyWage = renewal.WeeklyWage;
                break;

            case ManagerFeedbackMessage feedback:
                save.Summary = feedback.Verdict;
                break;

            case InjuryNoticeMessage injury:
                save.InjuryKindValue = (int)injury.Injury;
                save.InjurySeverityValue = (int)injury.Severity;
                save.WeeksOut = injury.WeeksOut;
                break;

            case AwardNoticeMessage award:
                save.Details = ToArray(award.Awards);
                break;

            case CupDrawMessage draw:
                save.CupRoundValue = (int)draw.Round;
                save.ToClubId = draw.OpponentClubId.Value;
                save.IsHome = draw.IsHome;
                save.MatchWeek = draw.MatchWeek;
                break;

            default:
                throw new InvalidOperationException($"Unsupported inbox message type {message.GetType().Name}.");
        }

        return save;
    }

    private static CareerInboxMessage FromSave(CareerInboxMessageSave save)
    {
        if (!Enum.IsDefined((CareerInboxKind)save.Kind))
        {
            throw new InvalidOperationException($"The save has an unknown inbox message kind {save.Kind}.");
        }

        CareerInboxMessage message = (CareerInboxKind)save.Kind switch
        {
            CareerInboxKind.CoachReport => new CoachReportMessage(
                save.Id, save.Season, save.Week, save.Subject ?? string.Empty, save.Body ?? string.Empty,
                save.Summary ?? string.Empty, save.Details ?? Array.Empty<string>()),

            CareerInboxKind.TransferOffer => new TransferOfferMessage(
                save.Id, save.Season, save.Week, save.Subject ?? string.Empty, save.Body ?? string.Empty,
                new ClubId(save.FromClubId), new ClubId(save.ToClubId), save.Fee, save.WeeklyWage,
                (TransferKind)save.OfferKind, save.ExpiresSeason, save.ExpiresWeek),

            CareerInboxKind.LoanOffer => new LoanOfferMessage(
                save.Id, save.Season, save.Week, save.Subject ?? string.Empty, save.Body ?? string.Empty,
                new ClubId(save.FromClubId), new ClubId(save.ToClubId), save.LoanEndSeason,
                save.ExpiresSeason, save.ExpiresWeek),

            CareerInboxKind.TrialInvite => new TrialInviteMessage(
                save.Id, save.Season, save.Week, save.Subject ?? string.Empty, save.Body ?? string.Empty,
                new ClubId(save.ToClubId), save.WeeklyWage, save.ExpiresSeason, save.ExpiresWeek),

            CareerInboxKind.ContractRenewal => new ContractRenewalMessage(
                save.Id, save.Season, save.Week, save.Subject ?? string.Empty, save.Body ?? string.Empty,
                new ClubId(save.ToClubId), save.Seasons, save.WeeklyWage, save.ExpiresSeason, save.ExpiresWeek),

            CareerInboxKind.ManagerFeedback => new ManagerFeedbackMessage(
                save.Id, save.Season, save.Week, save.Subject ?? string.Empty, save.Body ?? string.Empty,
                save.Summary ?? string.Empty),

            CareerInboxKind.InjuryNotice => new InjuryNoticeMessage(
                save.Id, save.Season, save.Week, save.Subject ?? string.Empty, save.Body ?? string.Empty,
                (InjuryKind)save.InjuryKindValue, (InjurySeverity)save.InjurySeverityValue, save.WeeksOut),

            CareerInboxKind.AwardNotice => new AwardNoticeMessage(
                save.Id, save.Season, save.Week, save.Subject ?? string.Empty, save.Body ?? string.Empty,
                save.Details ?? Array.Empty<string>()),

            CareerInboxKind.CupDraw => new CupDrawMessage(
                save.Id, save.Season, save.Week, save.Subject ?? string.Empty, save.Body ?? string.Empty,
                (CupRound)save.CupRoundValue, new ClubId(save.ToClubId), save.IsHome, save.MatchWeek),

            _ => throw new InvalidOperationException($"Unsupported inbox message kind {save.Kind}.")
        };

        if (save.IsRead) message.MarkRead();
        if (save.IsResolved) message.MarkResolved();
        return message;
    }

    private static string[] ToArray(IReadOnlyList<string> values)
    {
        if (values.Count == 0) return Array.Empty<string>();
        var result = new string[values.Count];
        for (int index = 0; index < result.Length; index++) result[index] = values[index];
        return result;
    }

    // ---------------------------------------------------------------------------------------------
    // Small helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The top scorer of one division by current-season goals, or <see cref="PlayerId.None"/>.</summary>
    private PlayerId FindDivisionTopScorer(int tier)
    {
        if (tier is < 1 or > 6) return PlayerId.None;

        WorldLeague league = _world.LeagueForTier(tier);
        PlayerId best = PlayerId.None;
        int bestGoals = 0;

        for (int clubIndex = 0; clubIndex < league.Clubs.Count; clubIndex++)
        {
            WorldClub club = _world.Clubs[league.Clubs[clubIndex]];
            IReadOnlyList<PlayerId> squad = club.Squad;
            for (int index = 0; index < squad.Count; index++)
            {
                WorldPlayer player = _world.Players[squad[index]];
                int goals = player.CurrentSeason.Goals;
                if (goals <= 0) continue;
                if (goals > bestGoals || (goals == bestGoals && best.IsValid && player.Id.Value < best.Value))
                {
                    bestGoals = goals;
                    best = player.Id;
                }
            }
        }

        return best;
    }

    private WorldFixture? FindFixture(MatchId fixtureId)
    {
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
            for (int index = 0; index < round.Ties.Count; index++)
            {
                if (round.Ties[index].Fixture.Id == fixtureId) return round.Ties[index].Fixture;
            }
        }

        return null;
    }

    /// <summary>Shirt number of a player in a selected lineup: 1..11 starting, 12..18 bench, 0 not in the squad.</summary>
    private static int ShirtNumberIn(SelectedLineup lineup, PlayerId playerId, out bool starts, out bool onBench)
    {
        starts = false;
        onBench = false;

        for (int index = 0; index < lineup.StartingXI.Count; index++)
        {
            if (lineup.StartingXI[index] != playerId) continue;
            starts = true;
            return index + 1;
        }

        for (int index = 0; index < lineup.Bench.Count; index++)
        {
            if (lineup.Bench[index] != playerId) continue;
            onBench = true;
            return lineup.StartingXI.Count + index + 1;
        }

        return 0;
    }

    private static bool ContainsPlayer(IReadOnlyList<PlayerId> playerIds, PlayerId playerId)
    {
        for (int index = 0; index < playerIds.Count; index++)
        {
            if (playerIds[index] == playerId) return true;
        }
        return false;
    }

    private long SquadWageBill(WorldClub club)
    {
        long total = 0L;
        IReadOnlyList<PlayerId> squad = club.Squad;
        for (int index = 0; index < squad.Count; index++)
        {
            if (!_world.Players.TryGet(squad[index], out WorldPlayer? player) || player is null) continue;
            if (player.IsRetired) continue;
            total += player.Contract.WeeklyWage;
        }
        return total;
    }

    /// <summary>Club display name by id, or "Free agent"/"unknown club" for the sentinel/missing ids.</summary>
    internal string ClubName(ClubId clubId)
    {
        if (!clubId.IsValid) return "Free agent";
        return _world.Clubs.TryGet(clubId, out WorldClub? club) && club is not null ? club.Name : "unknown club";
    }

    private static string CupRoundName(CupRound round) => round switch
    {
        CupRound.Preliminary => "preliminary round",
        CupRound.SecondRound => "second round",
        CupRound.RoundOf64 => "round of 64",
        CupRound.RoundOf32 => "round of 32",
        CupRound.RoundOf16 => "round of 16",
        CupRound.QuarterFinal => "quarter-final",
        CupRound.SemiFinal => "semi-final",
        CupRound.Final => "final",
        _ => "cup round"
    };

    private static string InjuryName(InjuryKind kind) => kind switch
    {
        InjuryKind.HamstringTear => "Hamstring tear",
        InjuryKind.KneeLigament => "Knee ligament injury",
        InjuryKind.AnkleLigament => "Ankle ligament injury",
        InjuryKind.GroinStrain => "Groin strain",
        InjuryKind.CalfStrain => "Calf strain",
        InjuryKind.Concussion => "Concussion",
        InjuryKind.FootFracture => "Foot fracture",
        InjuryKind.BackInjury => "Back injury",
        InjuryKind.Illness => "Illness",
        _ => "Injury"
    };
}
