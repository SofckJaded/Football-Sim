using System;
using System.Collections.Generic;
using FootballSim.Core;
using FootballSim.World.Career;
using FootballSim.World.Core;
using FootballSim.World.Players;
using FootballSim.World.Simulation;
using FootballSim.World.World;

namespace FootballSim.World.Bridge;

/// <summary>
/// One prepared live match (stage W5): the engine configuration, the two world lineups, the engine-shirt to
/// world-player mapping and the human lock. It is the handoff object between
/// <see cref="Career.CareerSession.PrepareHumanLiveMatch"/> (which decides the fixture and lineups) and the
/// live-match frontend (W6/W7), which runs a <see cref="MatchSimulationEngine"/> from <see cref="Config"/> and
/// returns <see cref="WorldMatchBridge.BuildLiveMatchReport"/> to the career.
/// </summary>
public sealed class WorldMatchSetup
{
    /// <summary>The fixture being played.</summary>
    public MatchId FixtureId { get; init; }

    /// <summary>The match engine configuration built from the two world squads.</summary>
    public PreMatchConfig Config { get; init; } = null!;

    /// <summary>Home club.</summary>
    public ClubId HomeClubId { get; init; }

    /// <summary>Away club.</summary>
    public ClubId AwayClubId { get; init; }

    /// <summary>The home manager's engine tactical profile (the one put on <see cref="Config"/>).</summary>
    public TeamTacticalProfile HomeProfile { get; init; } = null!;

    /// <summary>The away manager's engine tactical profile (the one put on <see cref="Config"/>).</summary>
    public TeamTacticalProfile AwayProfile { get; init; } = null!;

    /// <summary>Home club display name.</summary>
    public string HomeClubName { get; init; } = string.Empty;

    /// <summary>Away club display name.</summary>
    public string AwayClubName { get; init; } = string.Empty;

    /// <summary>
    /// World players assigned to the home engine shirts, index 0 = shirt 1 (the goalkeeper). Length is
    /// <see cref="PreMatchConfig.PlayersPerTeam"/>.
    /// </summary>
    public IReadOnlyList<PlayerId> HomeShirtPlayers { get; init; } = Array.Empty<PlayerId>();

    /// <summary>World players assigned to the away engine shirts, index 0 = shirt 1.</summary>
    public IReadOnlyList<PlayerId> AwayShirtPlayers { get; init; } = Array.Empty<PlayerId>();

    /// <summary>The career player's world id, when he is in the starting XI.</summary>
    public PlayerId? HumanPlayerId { get; init; }

    /// <summary>The career player's engine id (home 1..11, away 12..22), or null when he is not starting.</summary>
    public int? HumanEnginePlayerId { get; init; }

    /// <summary>True when the career player's club is the home side.</summary>
    public bool HumanIsHome { get; init; }

    /// <summary>The match seed the engine's random streams derive from.</summary>
    public ulong MatchSeed { get; init; }

    /// <summary>True when the match is a domestic cup tie.</summary>
    public bool IsCupTie { get; init; }
}

/// <summary>
/// The match bridge (stage W5): converts two world squads into a real <see cref="MatchSimulationEngine"/> match
/// and reads the finished match back into the living world's <see cref="LiveMatchReport"/>.
///
/// <para><b>Mapping.</b> The engine always lines up in its own eleven-a-side shirt formation, so the bridge
/// assigns the world manager's chosen XI to engine shirts by role suitability (keeper first, then shirts in
/// order of the engine formation), with ties broken by the lower <see cref="PlayerId"/>. Attributes and
/// tendencies come from <see cref="AttributeMapper"/>; the club manager's <see cref="TeamTacticalProfile"/> is
/// passed straight through, because the world and the engine share that vocabulary.</para>
///
/// <para><b>Determinism.</b> The match seed is a pure function of the world seed and the fixture id, so the
/// same fixture always plays the same live match when the same lineups are fielded. The bridge itself consumes
/// no world random stream and mutates no world state: the career layer commits the returned report through the
/// same applier a quick-simulated fixture uses.</para>
///
/// <para><b>v1 limitations (documented).</b> The engine has no substitutions or in-match injuries, so only the
/// starting XI takes the field and <see cref="LiveMatchReport.Injuries"/> is always empty; a level cup tie is
/// settled by a deterministic shoot-out simulated here (the engine itself has no shoot-out phase). Team
/// statistics the engine does not count (corners, fouls, offsides) are reported as zero.</para>
/// </summary>
public static class WorldMatchBridge
{
    /// <summary>Engine players per team the bridge builds (the world's formations are eleven-a-side).</summary>
    public const int PlayersPerTeam = 11;

    /// <summary>Fork ordinal of the deterministic shoot-out stream (kept clear of engine streams).</summary>
    private const ulong ShootoutForkOrdinal = 6_100_000UL;

    /// <summary>Shoot-out kicks per side in the first five-round series.</summary>
    private const int ShootoutRegulationKicks = 5;

    /// <summary>Hard cap on shoot-out rounds so a pathological tie can never loop.</summary>
    private const int ShootoutMaximumRounds = 40;

    /// <summary>Builds the setup for the human's match from the two selected lineups.</summary>
    /// <param name="world">The world the fixture belongs to.</param>
    /// <param name="fixture">The fixture to play.</param>
    /// <param name="homeLineup">The home club's selected XI and bench.</param>
    /// <param name="awayLineup">The away club's selected XI and bench.</param>
    /// <param name="humanPlayerId">The career player to lock, or null for a fully AI match.</param>
    /// <param name="halfDurationSeconds">Simulated seconds per half (2700 = a real 45-minute half).</param>
    /// <param name="matchSeed">Optional explicit engine seed (default: derived from the world seed and fixture).</param>
    /// <returns>The prepared match.</returns>
    /// <exception cref="ArgumentNullException">Any reference argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The lineups do not belong to the fixture's clubs, or a lineup has the wrong size.
    /// </exception>
    /// <exception cref="InvalidOperationException">A club has no manager or a squad is too small.</exception>
    public static WorldMatchSetup PrepareMatch(
        WorldState world,
        WorldFixture fixture,
        SelectedLineup homeLineup,
        SelectedLineup awayLineup,
        PlayerId? humanPlayerId = null,
        float halfDurationSeconds = 2700f,
        ulong? matchSeed = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(homeLineup);
        ArgumentNullException.ThrowIfNull(awayLineup);

        if (homeLineup.ClubId != fixture.HomeClubId || awayLineup.ClubId != fixture.AwayClubId)
        {
            throw new ArgumentException(
                $"The lineups ({homeLineup.ClubId} v {awayLineup.ClubId}) do not match fixture {fixture.Id} " +
                $"({fixture.HomeClubId} v {fixture.AwayClubId}).");
        }

        if (homeLineup.StartingXI.Count != PlayersPerTeam || awayLineup.StartingXI.Count != PlayersPerTeam)
        {
            throw new ArgumentException("Both lineups must field exactly eleven starting players.");
        }

        WorldClub home = world.Clubs[fixture.HomeClubId];
        WorldClub away = world.Clubs[fixture.AwayClubId];
        TeamTacticalProfile homeProfile = ManagerProfileOf(world, home);
        TeamTacticalProfile awayProfile = ManagerProfileOf(world, away);

        PlayerId[] homeShirts = AssignShirts(world, homeLineup.StartingXI);
        PlayerId[] awayShirts = AssignShirts(world, awayLineup.StartingXI);

        var homeSpecs = new MatchPlayerSpec[PlayersPerTeam];
        var awaySpecs = new MatchPlayerSpec[PlayersPerTeam];
        for (int shirt = 1; shirt <= PlayersPerTeam; shirt++)
        {
            TacticalRole role = FormationRoleOf(shirt);
            homeSpecs[shirt - 1] = BuildSpec(world, homeShirts[shirt - 1], role);
            awaySpecs[shirt - 1] = BuildSpec(world, awayShirts[shirt - 1], role);
        }

        ulong seed = matchSeed ?? DeriveMatchSeed(world, fixture);
        int? humanEngineId = null;
        bool humanIsHome = false;
        if (humanPlayerId is { IsValid: true } human)
        {
            for (int index = 0; index < homeShirts.Length; index++)
            {
                if (homeShirts[index] != human) continue;
                humanEngineId = index + 1;
                humanIsHome = true;
                break;
            }

            if (humanEngineId is null)
            {
                for (int index = 0; index < awayShirts.Length; index++)
                {
                    if (awayShirts[index] != human) continue;
                    humanEngineId = PlayersPerTeam + index + 1;
                    break;
                }
            }
        }

        var config = new PreMatchConfig
        {
            Seed = seed,
            PlayersPerTeam = PlayersPerTeam,
            HalfDurationSeconds = halfDurationSeconds,
            DesignatedHumanPlayerId = humanEngineId,
            HomeProfile = homeProfile,
            AwayProfile = awayProfile,
            HomeLineup = homeSpecs,
            AwayLineup = awaySpecs
        };

        return new WorldMatchSetup
        {
            FixtureId = fixture.Id,
            Config = config,
            HomeClubId = home.Id,
            AwayClubId = away.Id,
            HomeProfile = homeProfile,
            AwayProfile = awayProfile,
            HomeClubName = home.Name,
            AwayClubName = away.Name,
            HomeShirtPlayers = homeShirts,
            AwayShirtPlayers = awayShirts,
            HumanPlayerId = humanEngineId is null ? null : humanPlayerId,
            HumanEnginePlayerId = humanEngineId,
            HumanIsHome = humanIsHome,
            MatchSeed = seed,
            IsCupTie = fixture.IsCupTie
        };
    }

    /// <summary>Builds a prepared match for a fixture with both managers selecting their own strongest XI.</summary>
    /// <param name="world">The world the fixture belongs to.</param>
    /// <param name="fixture">The fixture to play.</param>
    /// <param name="humanPlayerId">The career player to lock, or null.</param>
    /// <param name="halfDurationSeconds">Simulated seconds per half.</param>
    /// <returns>The prepared match.</returns>
    public static WorldMatchSetup PrepareMatch(
        WorldState world,
        WorldFixture fixture,
        PlayerId? humanPlayerId = null,
        float halfDurationSeconds = 2700f)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(fixture);

        SelectedLineup home = SquadSelector.Select(world, world.Clubs[fixture.HomeClubId]);
        SelectedLineup away = SquadSelector.Select(world, world.Clubs[fixture.AwayClubId]);
        return PrepareMatch(world, fixture, home, away, humanPlayerId, halfDurationSeconds);
    }

    /// <summary>Creates the engine for a prepared match.</summary>
    /// <param name="setup">The prepared match.</param>
    /// <returns>A match engine positioned at kick-off.</returns>
    public static MatchSimulationEngine CreateEngine(WorldMatchSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        return new MatchSimulationEngine(setup.Config);
    }

    /// <summary>
    /// Steps the engine until full time. The loop is bounded by the configured half lengths plus a generous
    /// stoppage allowance, so a broken fixture throws instead of hanging.
    /// </summary>
    /// <param name="engine">The engine to advance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The match did not reach full time within the bound.</exception>
    public static void RunToFullTime(MatchSimulationEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        int maximumTicks = (int)(2f * engine.Config.HalfDurationSeconds / SimConstants.FixedDeltaTime) + 20_000;
        int guard = 0;
        while (engine.Rules.Phase != MatchPhase.FullTime)
        {
            engine.StepTick();
            if (++guard > maximumTicks)
            {
                throw new InvalidOperationException(
                    $"The match did not reach full time within {maximumTicks} ticks (phase {engine.Rules.Phase}).");
            }
        }
    }

    /// <summary>Simulates a prepared match headlessly and returns the career-facing report.</summary>
    /// <param name="world">The world the fixture belongs to (players are resolved from it).</param>
    /// <param name="setup">The prepared match.</param>
    /// <returns>The finished match report.</returns>
    public static LiveMatchReport SimulateHeadless(WorldState world, WorldMatchSetup setup)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(setup);

        MatchSimulationEngine engine = CreateEngine(setup);
        RunToFullTime(engine);
        return BuildLiveMatchReport(world, setup, engine);
    }

    /// <summary>
    /// Reads a finished engine match back into the living world's report: the scoreline, one
    /// <see cref="PlayerMatchLine"/> per player who took the field, cards, team statistics and — for a level
    /// cup tie — a deterministic shoot-out.
    /// </summary>
    /// <param name="world">The world the fixture belongs to (players are resolved from it).</param>
    /// <param name="setup">The prepared match the engine was created from.</param>
    /// <param name="engine">The finished match engine.</param>
    /// <returns>The report to hand to the career layer.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="InvalidOperationException">The engine has not reached full time.</exception>
    public static LiveMatchReport BuildLiveMatchReport(
        WorldState world, WorldMatchSetup setup, MatchSimulationEngine engine)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(engine);

        if (engine.Rules.Phase != MatchPhase.FullTime)
        {
            throw new InvalidOperationException(
                $"The match is at phase {engine.Rules.Phase}; the bridge only reports a finished match.");
        }

        int homeGoals = engine.Rules.HomeScore;
        int awayGoals = engine.Rules.AwayScore;
        float homePossession01 = engine.Analytics.HomePossessionShare;

        List<PlayerMatchLine> homeLines = BuildLines(
            setup, engine, TeamId.Home, homeGoals, awayGoals, homePossession01);
        List<PlayerMatchLine> awayLines = BuildLines(
            setup, engine, TeamId.Away, awayGoals, homeGoals, 1f - homePossession01);

        bool wentToPenalties = false;
        int homePenaltyGoals = 0;
        int awayPenaltyGoals = 0;
        if (setup.IsCupTie && homeGoals == awayGoals)
        {
            wentToPenalties = true;
            ResolveShootout(world, setup, out homePenaltyGoals, out awayPenaltyGoals);
        }

        return new LiveMatchReport
        {
            FixtureId = setup.FixtureId,
            HomeGoals = homeGoals,
            AwayGoals = awayGoals,
            WentToPenalties = wentToPenalties,
            HomePenaltyGoals = homePenaltyGoals,
            AwayPenaltyGoals = awayPenaltyGoals,
            HomePlayers = homeLines.AsReadOnly(),
            AwayPlayers = awayLines.AsReadOnly(),
            Injuries = Array.Empty<InjuryEvent>(),
            Cards = Array.Empty<CardEvent>(),
            Substitutions = Array.Empty<SubstitutionEvent>(),
            HomeStats = BuildTeamStats(homeLines, homeGoals, homePossession01),
            AwayStats = BuildTeamStats(awayLines, awayGoals, 1f - homePossession01)
        };
    }

    /// <summary>
    /// The engine's formation role for a shirt number, used to validate that a bridge lineup is shirt-ordered
    /// the way the engine expects.
    /// </summary>
    /// <param name="shirtNumber">Shirt 1..11.</param>
    public static TacticalRole FormationRoleOf(int shirtNumber) =>
        FootballSim.Tactics.TeamTacticalCoordinator.GetFormationRoleAndSlot(shirtNumber, PlayersPerTeam).Role;

    // ---------------------------------------------------------------------------------------------
    // Lineup assignment.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Assigns a world starting XI to the engine's shirts by role suitability: shirt 1 is the goalkeeper, and
    /// each later shirt takes the best remaining player for that engine role (ties by lower player id).
    /// Deterministic and total, because a squad always has eleven players and each is used once.
    /// </summary>
    private static PlayerId[] AssignShirts(WorldState world, IReadOnlyList<PlayerId> startingXI)
    {
        int count = startingXI.Count;
        var assigned = new PlayerId[count];
        var used = new bool[count];

        // The keeper first so an outfielder who happens to score highly for the keeper role can never take
        // the gloves while the natural keeper is still available.
        int keeperSlot = FindBest(world, startingXI, used, FormationRoleOf(1));
        assigned[0] = startingXI[keeperSlot];
        used[keeperSlot] = true;

        for (int shirt = 2; shirt <= count; shirt++)
        {
            int slot = FindBest(world, startingXI, used, FormationRoleOf(shirt));
            assigned[shirt - 1] = startingXI[slot];
            used[slot] = true;
        }

        return assigned;
    }

    /// <summary>Best unused player for an engine role; ties break on the lower player id.</summary>
    private static int FindBest(
        WorldState world, IReadOnlyList<PlayerId> candidates, bool[] used, TacticalRole engineRole)
    {
        WorldRole worldRole = WorldRoleFor(engineRole);
        int best = -1;
        float bestScore = float.NegativeInfinity;
        PlayerId bestId = PlayerId.None;

        for (int index = 0; index < candidates.Count; index++)
        {
            if (used[index]) continue;

            WorldPlayer player = world.Players[candidates[index]];
            float score = IdentityEvaluator.Suitability(player, worldRole);
            if (IdentityEvaluator.Evaluate(player).PrimaryRole == worldRole) score += 12f;

            if (best < 0 || score > bestScore || (score == bestScore && player.Id.Value < bestId.Value))
            {
                best = index;
                bestScore = score;
                bestId = player.Id;
            }
        }

        if (best < 0)
        {
            throw new InvalidOperationException("The lineup ran out of players while assigning engine shirts.");
        }

        return best;
    }

    /// <summary>The world role vocabulary value that corresponds to an engine role.</summary>
    private static WorldRole WorldRoleFor(TacticalRole role) => role switch
    {
        TacticalRole.Goalkeeper => WorldRole.Goalkeeper,
        TacticalRole.CenterBack => WorldRole.CenterBack,
        TacticalRole.FullBack => WorldRole.FullBack,
        TacticalRole.DefensiveMidfielder => WorldRole.DefensiveMidfielder,
        TacticalRole.CentralMidfielder => WorldRole.CentralMidfielder,
        TacticalRole.Winger => WorldRole.Winger,
        TacticalRole.Striker => WorldRole.Striker,
        _ => WorldRole.CentralMidfielder
    };

    /// <summary>Builds one engine player spec from a world player.</summary>
    private static MatchPlayerSpec BuildSpec(WorldState world, PlayerId playerId, TacticalRole role)
    {
        WorldPlayer player = world.Players[playerId];
        EmergentIdentity identity = IdentityEvaluator.Evaluate(player);
        float stamina = Math.Clamp(player.Condition01, 0f, 1f);

        return AttributeMapper.ToMatchPlayerSpec(
            player, role, identity.Archetype, stamina);
    }

    /// <summary>The manager's engine tactical profile, or the neutral profile when the club has no manager.</summary>
    private static TeamTacticalProfile ManagerProfileOf(WorldState world, WorldClub club)
    {
        if (club.ManagerId.IsValid &&
            world.Managers.TryGet(club.ManagerId, out WorldManager? manager) &&
            manager is not null)
        {
            return manager.TacticalProfile;
        }

        return new TeamTacticalProfile();
    }

    /// <summary>Match seed derived from the world seed and the fixture id (a pure function of both).</summary>
    private static ulong DeriveMatchSeed(WorldState world, WorldFixture fixture) =>
        WorldRng.For(world.Seed, WorldRandomStream.QuickSim).Fork((ulong)fixture.Id.Value).Seed;

    // ---------------------------------------------------------------------------------------------
    // Report building.
    // ---------------------------------------------------------------------------------------------

    private static List<PlayerMatchLine> BuildLines(
        WorldMatchSetup setup,
        MatchSimulationEngine engine,
        TeamId team,
        int teamGoals,
        int opponentGoals,
        float possessionShare01)
    {
        IReadOnlyList<PlayerId> shirts = team == TeamId.Home ? setup.HomeShirtPlayers : setup.AwayShirtPlayers;
        bool isHome = team == TeamId.Home;
        var lines = new List<PlayerMatchLine>(PlayersPerTeam);

        IReadOnlyList<MatchPlayerPerformance> performances = engine.PlayerStats.Players;
        for (int index = 0; index < performances.Count; index++)
        {
            MatchPlayerPerformance performance = performances[index];
            if (performance.Team != team) continue;

            int shirt = performance.ShirtNumber;
            if (shirt < 1 || shirt > shirts.Count) continue;

            float rating = MatchRatingCalculator.Rate(
                performance, isHome, teamGoals, opponentGoals, performance.Role, possessionShare01);

            lines.Add(new PlayerMatchLine(
                shirts[shirt - 1],
                Minutes: (int)MathF.Round(performance.MinutesPlayed, MidpointRounding.AwayFromZero),
                Goals: performance.Goals,
                Assists: performance.Assists,
                Shots: performance.Shots,
                ShotsOnTarget: performance.ShotsOnTarget,
                PassesAttempted: performance.PassesAttempted,
                PassesCompleted: performance.PassesCompleted,
                Tackles: performance.Tackles,
                Saves: performance.Saves,
                YellowCards: performance.YellowCards,
                RedCards: performance.RedCards,
                Rating: rating,
                EndingStamina01: Math.Clamp(performance.EndingStamina01, 0f, 1f),
                ConditionCost01: Math.Clamp(performance.StartingStamina01 - performance.EndingStamina01, 0f, 1f)));
        }

        return lines;
    }

    private static QuickSimTeamStats BuildTeamStats(
        IReadOnlyList<PlayerMatchLine> lines, int goals, float possessionShare01)
    {
        int shots = 0;
        int onTarget = 0;
        for (int index = 0; index < lines.Count; index++)
        {
            shots += lines[index].Shots;
            onTarget += lines[index].ShotsOnTarget;
        }

        int possession = (int)MathF.Round(Math.Clamp(possessionShare01, 0f, 1f) * 100f);
        return new QuickSimTeamStats(
            PossessionPercent: possession,
            Shots: shots,
            ShotsOnTarget: Math.Max(onTarget, goals),
            Corners: 0,
            Fouls: 0,
            Offsides: 0);
    }

    // ---------------------------------------------------------------------------------------------
    // Cup-tie shoot-out.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Settles a level cup tie with a deterministic shoot-out. The engine has no shoot-out phase, so the
    /// bridge simulates one from the real XI (kickers by finishing, keeper by goalkeeping) on a stream derived
    /// purely from the match seed — the same tie always produces the same shoot-out, and no world stream is
    /// consumed.
    /// </summary>
    private static void ResolveShootout(
        WorldState world, WorldMatchSetup setup, out int homeGoals, out int awayGoals)
    {
        WorldRng rng = WorldRng.For(setup.MatchSeed, WorldRandomStream.QuickSim).Fork(ShootoutForkOrdinal);

        List<PlayerId> homeKickers = ShootoutKickersOf(world, setup, TeamId.Home);
        List<PlayerId> awayKickers = ShootoutKickersOf(world, setup, TeamId.Away);
        WorldPlayer homeKeeper = ShootoutKeeperOf(world, setup, TeamId.Home);
        WorldPlayer awayKeeper = ShootoutKeeperOf(world, setup, TeamId.Away);

        homeGoals = 0;
        awayGoals = 0;

        for (int round = 0; round < ShootoutRegulationKicks; round++)
        {
            homeGoals += TakeKick(world, homeKickers, round, awayKeeper, rng);
            awayGoals += TakeKick(world, awayKickers, round, homeKeeper, rng);
        }

        int extraRound = 0;
        while (homeGoals == awayGoals && extraRound < ShootoutMaximumRounds)
        {
            homeGoals += TakeKick(world, homeKickers, ShootoutRegulationKicks + extraRound, awayKeeper, rng);
            awayGoals += TakeKick(world, awayKickers, ShootoutRegulationKicks + extraRound, homeKeeper, rng);
            extraRound++;
        }

        if (homeGoals == awayGoals)
        {
            // Pathological cap: a coin flip on the same deterministic stream still settles the tie.
            if (rng.NextBool(0.5f)) homeGoals++;
            else awayGoals++;
        }
    }

    /// <summary>
    /// One penalty: a finishing-weighted conversion against a goalkeeping-weighted keeper. The calculation is
    /// deliberately attribute-driven rather than a flat probability, so a stronger squad wins more shoot-outs.
    /// </summary>
    private static int TakeKick(
        WorldState world,
        IReadOnlyList<PlayerId> kickers,
        int kickIndex,
        WorldPlayer keeper,
        WorldRng rng)
    {
        PlayerId kickerId = kickers[kickIndex % kickers.Count];
        WorldPlayer kicker = world.Players[kickerId];
        float finishing = kicker.Attributes.Current(AttributeId.Finishing);
        float composure = kicker.Attributes.Current(AttributeId.Composure);
        float goalkeeping = keeper.Attributes.Current(AttributeId.Goalkeeping);

        float chance = 0.62f
            + (finishing - 60f) * 0.004f
            + (composure - 60f) * 0.002f
            - (goalkeeping - 60f) * 0.004f;
        chance = Math.Clamp(chance, 0.45f, 0.94f);
        return rng.NextFloat() < chance ? 1 : 0;
    }

    /// <summary>The shoot-out order: best finishers first, ties by lower player id.</summary>
    private static List<PlayerId> ShootoutKickersOf(WorldState world, WorldMatchSetup setup, TeamId team)
    {
        IReadOnlyList<PlayerId> shirts = team == TeamId.Home ? setup.HomeShirtPlayers : setup.AwayShirtPlayers;
        var kickers = new List<PlayerId>(PlayersPerTeam);
        for (int index = 0; index < shirts.Count; index++)
        {
            kickers.Add(shirts[index]);
        }

        kickers.Sort((left, right) =>
        {
            WorldPlayer firstPlayer = world.Players[left];
            WorldPlayer secondPlayer = world.Players[right];
            int byFinishing = secondPlayer.Attributes.Current(AttributeId.Finishing)
                .CompareTo(firstPlayer.Attributes.Current(AttributeId.Finishing));
            return byFinishing != 0 ? byFinishing : left.Value.CompareTo(right.Value);
        });
        return kickers;
    }

    /// <summary>The keeper (engine shirt 1 of that side).</summary>
    private static WorldPlayer ShootoutKeeperOf(WorldState world, WorldMatchSetup setup, TeamId team)
    {
        IReadOnlyList<PlayerId> shirts = team == TeamId.Home ? setup.HomeShirtPlayers : setup.AwayShirtPlayers;
        return world.Players[shirts[0]];
    }
}
