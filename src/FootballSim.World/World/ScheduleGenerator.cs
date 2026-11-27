using FootballSim.World.Core;

namespace FootballSim.World.World;

/// <summary>
/// Builds a division's league season: an 18-club double round-robin (34 matchdays, 306 fixtures) using the
/// circle method, with the venue balanced so every club finishes with 17 home and 17 away matches.
///
/// <para><b>Calendar authority.</b> Matchdays 1..17 are played in weeks 5..21 and matchdays 18..34 in weeks
/// 26..42; the mapping is defined by <see cref="WorldCalendar.LeagueMatchdayForWeek"/>, which is the one
/// authority every other system reads. Matchday 1 is week 5, matchday 34 week 42.</para>
///
/// <para><b>Determinism.</b> The generator draws only from the supplied <see cref="WorldRng"/>: it forks a
/// per-tier child stream, shuffles the club order once, and then pairs by the circle method. The same
/// stream state and club list always produce the same fixture list.</para>
/// </summary>
public static class ScheduleGenerator
{
    /// <summary>Clubs in one v1 division.</summary>
    private const int ClubsPerLeague = 18;

    /// <summary>Fixtures on one matchday.</summary>
    private const int FixturesPerMatchday = 9;

    /// <summary>League matchdays in one season (each club plays 34 matches).</summary>
    private const int MatchdaysPerSeason = 34;

    /// <summary>Rounds in the single round-robin that the double round-robin mirrors.</summary>
    private const int SingleRoundRobinRounds = 17;

    /// <summary>
    /// Generates one league's complete season: every club plays every other club home and away.
    /// Fixtures are added to <paramref name="league"/> in matchday order (matchday 1 first), and each one
    /// receives a fresh <see cref="MatchId"/> from the world.
    /// </summary>
    /// <param name="world">The world that owns the match-id counter and the season.</param>
    /// <param name="league">The division to schedule; it must contain exactly 18 clubs and no fixtures.</param>
    /// <param name="rng">The fixture stream; the method forks it per tier so all six divisions are independent.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The league does not have 18 clubs, or it already has fixtures.
    /// </exception>
    public static void GenerateLeagueSeason(WorldState world, WorldLeague league, WorldRng rng)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(league);
        ArgumentNullException.ThrowIfNull(rng);

        if (league.Clubs.Count != ClubsPerLeague)
        {
            throw new InvalidOperationException(
                $"League '{league.Name}' has {league.Clubs.Count} clubs; a v1 division needs {ClubsPerLeague}.");
        }

        if (league.Fixtures.Count != 0)
        {
            throw new InvalidOperationException(
                $"League '{league.Name}' already has fixtures; clear them before generating a new season.");
        }

        // One independent draw per division: moving a draw in one league cannot shift another's fixtures.
        WorldRng leagueRng = rng.Fork((ulong)league.Tier);

        var clubs = new List<ClubId>(league.Clubs);
        leagueRng.Shuffle(clubs);

        (ClubId Home, ClubId Away)[][] firstLeg = BuildSingleRoundRobin(clubs);

        for (int matchday = 1; matchday <= MatchdaysPerSeason; matchday++)
        {
            int week = WeekForMatchday(matchday);
            bool mirror = matchday > SingleRoundRobinRounds;
            (ClubId Home, ClubId Away)[] pairs = mirror
                ? firstLeg[matchday - SingleRoundRobinRounds - 1]
                : firstLeg[matchday - 1];

            for (int index = 0; index < pairs.Length; index++)
            {
                // The mirror leg swaps venues, so each unordered pair meets exactly twice, once at each home.
                ClubId home = mirror ? pairs[index].Away : pairs[index].Home;
                ClubId away = mirror ? pairs[index].Home : pairs[index].Away;

                var fixture = new WorldFixture(
                    world.AllocateMatchId(),
                    world.Season,
                    week,
                    CompetitionKind.League,
                    league.Id,
                    matchday,
                    CupRound.None,
                    home,
                    away);
                league.AddFixture(fixture);
            }
        }
    }

    /// <summary>
    /// Calendar week a league matchday (1..34) is played in: weeks 5..21 for matchdays 1..17, then weeks
    /// 26..42 for matchdays 18..34. Mirrors <see cref="WorldCalendar.LeagueMatchdayForWeek"/>.
    /// </summary>
    private static int WeekForMatchday(int matchday) => matchday <= SingleRoundRobinRounds
        ? WorldCalendar.LeagueFirstHalfFirstWeek + matchday - 1
        : WorldCalendar.LeagueSecondHalfFirstWeek + (matchday - SingleRoundRobinRounds - 1);

    /// <summary>Week a cup round is played on (see <see cref="CupScheduler.WeekForCupRound"/>).</summary>
    /// <param name="round">The cup round.</param>
    /// <returns>The calendar week (5, 10, 15, 20, 25, 31, 37 or 42).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="round"/> is not a playable cup round.</exception>
    public static int WeekForCupRound(CupRound round) => CupScheduler.WeekForCupRound(round);

    /// <summary>
    /// Builds the 17 rounds of a single round-robin with the circle method: club 0 is fixed and the other
    /// 17 rotate one place per round, so every pair is produced exactly once. Home/away alternates by
    /// round and pair position, which keeps the first leg roughly balanced per club; the mirrored second
    /// leg then makes every club exactly 17 home and 17 away over the season.
    /// </summary>
    private static (ClubId Home, ClubId Away)[][] BuildSingleRoundRobin(List<ClubId> clubs)
    {
        ClubId[] rotation = new ClubId[ClubsPerLeague];
        for (int i = 0; i < ClubsPerLeague; i++) rotation[i] = clubs[i];

        var rounds = new (ClubId Home, ClubId Away)[SingleRoundRobinRounds][];

        for (int round = 0; round < SingleRoundRobinRounds; round++)
        {
            var pairs = new (ClubId Home, ClubId Away)[FixturesPerMatchday];
            for (int pair = 0; pair < FixturesPerMatchday; pair++)
            {
                ClubId first = rotation[pair];
                ClubId second = rotation[ClubsPerLeague - 1 - pair];
                bool firstAtHome = ((round + pair) & 1) == 0;
                pairs[pair] = firstAtHome ? (first, second) : (second, first);
            }

            rounds[round] = pairs;

            // Circle method: the first club stays fixed and the other 17 rotate one place, so the next
            // round pairs a different set of clubs. Without this step every round would repeat round 0.
            ClubId rotating = rotation[ClubsPerLeague - 1];
            for (int index = ClubsPerLeague - 1; index > 1; index--)
            {
                rotation[index] = rotation[index - 1];
            }

            rotation[1] = rotating;
        }

        return rounds;
    }
}

/// <summary>
/// Builds and advances the domestic knockout cup: the tiered-entry bracket of plan §5.1.
///
/// <para>The cup is not a pure power-of-two bracket when it starts, because lower divisions enter earlier:
/// the preliminary round is the 36 tier 5-6 clubs (18 ties, 18 winners); the second round adds the 36
/// tier 3-4 clubs, pairs 52 of the 54 entrants and gives the two clubs left over a bye (26 ties + 2 byes,
/// 28 qualified); the round of 64 then adds the 36 tier 1-2 clubs to reach exactly 64. From the round of
/// 64 on the bracket halves: 32, 16, 8, 4, 2, then the final.</para>
///
/// <para><b>Home advantage is a draw, v1.</b> Cup ties are single matches. The preliminary round gives the
/// first club of each shuffled pair the home venue; later rounds draw the venue with the cup stream
/// (50/50). There is no seeding, no replay and no two-legged tie in v1.</para>
///
/// <para><b>Determinism.</b> Every draw comes from the supplied <see cref="WorldRng"/>; clubs are gathered
/// in the world's stable registration order and sorted by id before joining rounds are drawn. The same
/// world seed and the same resolved results always produce the same bracket.</para>
/// </summary>
public static class CupScheduler
{
    /// <summary>Clubs a v1 country has, and therefore the cup's total entry.</summary>
    public const int RequiredTotalClubs = 108;

    /// <summary>Ties in the preliminary round (36 tier 5-6 clubs / 2).</summary>
    private const int PreliminaryTies = 18;

    /// <summary>Ties created when tiers 3-4 join the 18 preliminary winners (54 entrants).</summary>
    private const int SecondRoundTies = 26;

    /// <summary>Byes created in the second round to reach 28 qualified clubs.</summary>
    private const int SecondRoundByes = 2;

    private const int FirstRoundWeek = 5;
    private const int SecondRoundWeek = 10;
    private const int RoundOf64Week = 15;
    private const int RoundOf32Week = 20;
    private const int RoundOf16Week = 25;
    private const int QuarterFinalWeek = 31;
    private const int SemiFinalWeek = 37;
    private const int FinalWeek = 42;

    /// <summary>Entrants in the second round: 18 preliminary winners + 18 tier 3 + 18 tier 4.</summary>
    private const int SecondRoundEntrants = 54;

    /// <summary>Clubs in tiers 1-2 that join at the round of 64.</summary>
    private const int TopTierEntrants = 36;

    /// <summary>Clubs in tiers 3-4 that join at the second round.</summary>
    private const int MiddleTierEntrants = 36;

    /// <summary>Clubs in tiers 5-6 that play the preliminary round.</summary>
    private const int LowerTierEntrants = 36;

    // Tie counts by round: 18, 26, 32, 16, 8, 4, 2, 1.
    private const int RoundOf64Ties = 32;
    private const int RoundOf32Ties = 16;
    private const int RoundOf16Ties = 8;
    private const int QuarterFinalTies = 4;
    private const int SemiFinalTies = 2;
    private const int FinalTies = 1;

    /// <summary>
    /// Creates the season's preliminary round and registers the cup state: the 36 tier 5-6 clubs are
    /// shuffled and paired into 18 unplayed ties in week 5.
    /// </summary>
    /// <param name="world">The world whose cup is started; it must not already have cup rounds.</param>
    /// <param name="rng">The cup stream (one per season; the generator passes a fork of the world's stream).</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The cup already has rounds, or the two bottom tiers do not contain exactly 36 clubs.
    /// </exception>
    public static void StartSeason(WorldState world, WorldRng rng)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(rng);

        if (world.Cup.Rounds.Count != 0)
        {
            throw new InvalidOperationException(
                "The cup already has rounds; reset it before starting a new season.");
        }

        List<ClubId> entrants = CollectEntrants(world, minimumTier: 5, maximumTier: 6);
        if (entrants.Count != LowerTierEntrants)
        {
            throw new InvalidOperationException(
                $"The preliminary round needs {LowerTierEntrants} tier 5-6 clubs, found {entrants.Count}.");
        }

        rng.Shuffle(entrants);

        CupRoundBracket round = world.Cup.AddRound(
            CupRound.Preliminary, WeekForCupRound(CupRound.Preliminary));

        for (int tie = 0; tie < PreliminaryTies; tie++)
        {
            int index = tie * 2;
            // Home is the first club of each shuffled pair: the shuffle itself is the venue draw (v1).
            round.AddTie(CreateTie(world, round, entrants[index], entrants[index + 1]));
        }
    }

    /// <summary>
    /// Advances the cup when the current round is fully resolved: creates the next round, or completes the
    /// cup from the final. Safe to call every week — it does nothing while the round is still open, after
    /// completion, or before the season has started.
    /// </summary>
    /// <param name="world">The world whose cup is advanced.</param>
    /// <param name="rng">The cup stream used for the draw (and the venue coin-flip) of the new round.</param>
    /// <returns>True when a round was created or the cup was completed; false when nothing changed.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="InvalidOperationException">A round's qualified count does not match the bracket.</exception>
    public static bool AdvanceIfReady(WorldState world, WorldRng rng)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(rng);

        if (world.Cup.IsComplete) return false;

        CupRoundBracket? current = world.Cup.CurrentRound;
        if (current is null || !current.IsResolved) return false;

        IReadOnlyList<ClubId> qualified = current.QualifiedClubs();

        switch (current.Round)
        {
            case CupRound.Preliminary:
                CreateSecondRound(world, rng, qualified);
                return true;

            case CupRound.SecondRound:
                CreateJoiningRound(world, rng, qualified, CupRound.RoundOf64, RoundOf64Ties);
                return true;

            case CupRound.RoundOf64:
                CreateKnockoutRound(world, rng, qualified, CupRound.RoundOf32, RoundOf32Ties);
                return true;

            case CupRound.RoundOf32:
                CreateKnockoutRound(world, rng, qualified, CupRound.RoundOf16, RoundOf16Ties);
                return true;

            case CupRound.RoundOf16:
                CreateKnockoutRound(world, rng, qualified, CupRound.QuarterFinal, QuarterFinalTies);
                return true;

            case CupRound.QuarterFinal:
                CreateKnockoutRound(world, rng, qualified, CupRound.SemiFinal, SemiFinalTies);
                return true;

            case CupRound.SemiFinal:
                CreateKnockoutRound(world, rng, qualified, CupRound.Final, FinalTies);
                return true;

            case CupRound.Final:
                world.Cup.MarkComplete(qualified[0]);
                return true;

            default:
                throw new InvalidOperationException($"Unsupported cup round {current.Round}.");
        }
    }

    /// <summary>Calendar week a cup round is played in (plan §5.1 and stage W2).</summary>
    /// <param name="round">The cup round.</param>
    /// <returns>
    /// Preliminary 5, SecondRound 10, RoundOf64 15, RoundOf32 20, RoundOf16 25, QuarterFinal 31,
    /// SemiFinal 37, Final 42.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="round"/> is not a playable cup round.</exception>
    public static int WeekForCupRound(CupRound round) => round switch
    {
        CupRound.Preliminary => FirstRoundWeek,
        CupRound.SecondRound => SecondRoundWeek,
        CupRound.RoundOf64 => RoundOf64Week,
        CupRound.RoundOf32 => RoundOf32Week,
        CupRound.RoundOf16 => RoundOf16Week,
        CupRound.QuarterFinal => QuarterFinalWeek,
        CupRound.SemiFinal => SemiFinalWeek,
        CupRound.Final => FinalWeek,
        _ => throw new ArgumentOutOfRangeException(nameof(round), round, "Unknown cup round.")
    };

    // ---------------------------------------------------------------------------------------------
    // Round construction.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Creates the second round: the 18 preliminary winners plus the 36 tier 3-4 clubs are sorted by id,
    /// shuffled, and 52 of them are paired into 26 ties. The two clubs left over receive byes, which is
    /// what turns 54 entrants into the 28 pre-qualified clubs the round of 64 needs. The byes are the last
    /// two clubs of the shuffled order, so the rule is unambiguous and deterministic.
    /// </summary>
    private static void CreateSecondRound(WorldState world, WorldRng rng, IReadOnlyList<ClubId> qualified)
    {
        List<ClubId> middleTiers = CollectEntrants(world, minimumTier: 3, maximumTier: 4);
        if (middleTiers.Count != MiddleTierEntrants)
        {
            throw new InvalidOperationException(
                $"The second round needs {MiddleTierEntrants} tier 3-4 clubs, found {middleTiers.Count}.");
        }

        var entrants = new List<ClubId>(SecondRoundEntrants);
        entrants.AddRange(qualified);
        entrants.AddRange(middleTiers);
        PrepareDrawOrder(entrants, SecondRoundEntrants, rng);

        CupRoundBracket round = world.Cup.AddRound(
            CupRound.SecondRound, WeekForCupRound(CupRound.SecondRound));

        for (int tie = 0; tie < SecondRoundTies; tie++)
        {
            int index = tie * 2;
            round.AddTie(CreateTieWithVenueDraw(world, rng, round, entrants[index], entrants[index + 1]));
        }

        // The two clubs left over after pairing are the round's byes (28 qualify into the round of 64).
        for (int index = SecondRoundEntrants - SecondRoundByes; index < entrants.Count; index++)
        {
            round.AddBye(entrants[index]);
        }
    }

    /// <summary>
    /// Creates the round of 64: the 28 second-round qualifiers plus all 36 tier 1-2 clubs, i.e. exactly the
    /// 64 entrants a pure knockout bracket needs. Top-tier clubs can first appear in this round only.
    /// </summary>
    private static void CreateJoiningRound(
        WorldState world,
        WorldRng rng,
        IReadOnlyList<ClubId> qualified,
        CupRound roundType,
        int expectedTies)
    {
        var entrants = new List<ClubId>(expectedTies * 2);
        entrants.AddRange(qualified);

        List<ClubId> topTiers = CollectEntrants(world, minimumTier: 1, maximumTier: 2);
        if (topTiers.Count != TopTierEntrants)
        {
            throw new InvalidOperationException(
                $"The round of 64 needs {TopTierEntrants} tier 1-2 clubs, found {topTiers.Count}.");
        }

        entrants.AddRange(topTiers);
        PrepareDrawOrder(entrants, expectedTies * 2, rng);

        CupRoundBracket round = world.Cup.AddRound(roundType, WeekForCupRound(roundType));
        AddTiesAndAssertNoByes(world, rng, round, entrants, expectedTies);
    }

    /// <summary>Creates a halving knockout round from the previous round's winners (all powers of two).</summary>
    private static void CreateKnockoutRound(
        WorldState world,
        WorldRng rng,
        IReadOnlyList<ClubId> qualified,
        CupRound roundType,
        int expectedTies)
    {
        var entrants = new List<ClubId>(expectedTies * 2);
        entrants.AddRange(qualified);
        PrepareDrawOrder(entrants, expectedTies * 2, rng);

        CupRoundBracket round = world.Cup.AddRound(roundType, WeekForCupRound(roundType));
        AddTiesAndAssertNoByes(world, rng, round, entrants, expectedTies);
    }

    /// <summary>
    /// Canonicalises the entrant list (stable club-id sort, then a draw shuffle) and asserts it has the
    /// count the bracket expects and no duplicates, so a broken previous round fails loudly instead of
    /// producing a malformed fixture list.
    /// </summary>
    private static void PrepareDrawOrder(List<ClubId> entrants, int expectedCount, WorldRng rng)
    {
        if (entrants.Count != expectedCount)
        {
            throw new InvalidOperationException(
                $"Expected {expectedCount} entrants for the next cup round, found {entrants.Count}.");
        }

        EnsureDistinct(entrants);
        entrants.Sort(static (left, right) => left.Value.CompareTo(right.Value));
        rng.Shuffle(entrants);
    }

    /// <summary>Pairs every entrant and adds the ties to the round, asserting there are no byes.</summary>
    private static void AddTiesAndAssertNoByes(
        WorldState world, WorldRng rng, CupRoundBracket round, List<ClubId> entrants, int expectedTies)
    {
        if (entrants.Count != expectedTies * 2)
        {
            throw new InvalidOperationException(
                $"Round {round.Round} needs {expectedTies} ties but has {entrants.Count} entrants.");
        }

        for (int index = 0; index < entrants.Count; index += 2)
        {
            round.AddTie(CreateTieWithVenueDraw(world, rng, round, entrants[index], entrants[index + 1]));
        }
    }

    /// <summary>Creates one tie with the first-listed club at home (preliminary round rule).</summary>
    private static CupTie CreateTie(
        WorldState world, CupRoundBracket round, ClubId homeClubId, ClubId awayClubId) =>
        new(new WorldFixture(
            world.AllocateMatchId(),
            world.Season,
            round.Week,
            CompetitionKind.Cup,
            LeagueId.None,
            0,
            round.Round,
            homeClubId,
            awayClubId));

    /// <summary>
    /// Creates one tie with the venue drawn 50/50 from the cup stream. The draw is part of the bracket
    /// state: replaying the same world seed and results reproduces the same fixtures.
    /// </summary>
    private static CupTie CreateTieWithVenueDraw(
        WorldState world, WorldRng rng, CupRoundBracket round, ClubId firstClubId, ClubId secondClubId)
    {
        bool firstAtHome = rng.NextBool();
        ClubId homeClubId = firstAtHome ? firstClubId : secondClubId;
        ClubId awayClubId = firstAtHome ? secondClubId : firstClubId;
        return CreateTie(world, round, homeClubId, awayClubId);
    }

    // ---------------------------------------------------------------------------------------------
    // Entrant collection and validation.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Gathers every club in the given inclusive tier range, in the world's stable registration order
    /// (clubs are registered in tier then data-pack order by the generator).
    /// </summary>
    private static List<ClubId> CollectEntrants(WorldState world, int minimumTier, int maximumTier)
    {
        var entrants = new List<ClubId>();
        foreach (WorldClub club in world.Clubs.All)
        {
            if (club.Tier >= minimumTier && club.Tier <= maximumTier) entrants.Add(club.Id);
        }

        return entrants;
    }

    /// <summary>Throws when an entrant list contains the same club twice.</summary>
    private static void EnsureDistinct(IReadOnlyList<ClubId> entrants)
    {
        var seen = new HashSet<ClubId>();
        for (int index = 0; index < entrants.Count; index++)
        {
            if (!seen.Add(entrants[index]))
            {
                throw new InvalidOperationException($"Club {entrants[index]} entered the cup round twice.");
            }
        }
    }
}
