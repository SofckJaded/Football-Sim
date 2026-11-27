using System.Text;
using FootballSim.Core;
using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Players;

namespace FootballSim.World.World;

/// <summary>
/// Options for one deterministic world generation (plan §5.1, stage W2).
///
/// <para>The defaults reproduce the v1 contract world: season 1 week 1, 24 players per club, seed
/// 20261127 (the plan's date). Only the seed and the starting calendar position normally vary; the squad
/// size is exposed so tests and later sandbox modes can build a smaller world from the same pack.</para>
/// </summary>
public sealed record WorldGenerationOptions
{
    /// <summary>Root seed every random stream (and every player generation seed) derives from.</summary>
    public ulong Seed { get; init; } = 20261127UL;

    /// <summary>One-based season the world starts in.</summary>
    public int StartingSeason { get; init; } = 1;

    /// <summary>One-based week (1..52) the world starts in.</summary>
    public int StartingWeek { get; init; } = 1;

    /// <summary>
    /// Players generated per club. The v1 contract is <c>24</c> (3 GK, 4 CB, 4 FB, 2 DM, 4 CM, 3 W,
    /// 4 ST); other sizes are scaled proportionally with at least one player in every generation group.
    /// </summary>
    public int PlayersPerClub { get; init; } = 24;
}

/// <summary>
/// Deterministic generator for the whole living world (plan §5.1, stage W2): six divisions of eighteen
/// clubs, one manager and one squad per club, the tier facilities/finances, the double round-robin fixture
/// list for every division and the preliminary round of the domestic cup.
///
/// <para><b>Determinism.</b> Every random value comes from a named <see cref="WorldRng"/> stream rooted in
/// <see cref="WorldState.Seed"/>; there is no <c>Random.Shared</c>, clock or unordered iteration anywhere.
/// Two calls with the same pack, seed and options produce bit-identical clubs, managers, players,
/// fixtures and cup ties.</para>
///
/// <para><b>Draw order.</b> The fixed order is part of the generator's contract because it decides every
/// generated value:
/// <list type="number">
/// <item>Leagues are created from the pack in tier order (no randomness).</item>
/// <item>Manager tactical presets are pre-planned per tier: the six presets cycled and then shuffled with
/// a per-tier fork of <see cref="WorldRandomStream.WorldGeneration"/>, which guarantees every preset is
/// represented in every division.</item>
/// <item>Clubs are iterated in the pack's stable (tier, then id) order and given ids 1..N. Each club forks
/// its own stream from <see cref="WorldRandomStream.WorldGeneration"/> by club id, then draws: coaching
/// quality, medical quality, youth quality, cash balance, weekly wage budget, transfer budget, the
/// manager's first/last name, age, reputation, board confidence, seasons at club, then per player (slot
/// order): age bucket, first/last name (possibly retried for world-wide name uniqueness), manager trust.
/// The player's own generation seed is a fork of the slot ordinal, so it does not depend on how many
/// draws the club stream happened to consume.</item>
/// <item>League fixtures use forks of <see cref="WorldRandomStream.Fixtures"/> per tier and cup ties use a
/// fork of <see cref="WorldRandomStream.Cup"/>.</item>
/// </list></para>
/// </summary>
public static class WorldGenerator
{
    /// <summary>Number of divisions in a v1 world.</summary>
    private const int TierCount = 6;

    /// <summary>Base squad size of the v1 contract; the role composition below is defined for it.</summary>
    private const int DefaultPlayersPerClub = 24;

    /// <summary>One player per generation group is the smallest squad the generator can make.</summary>
    private const int MinimumPlayersPerClub = 7;

    private const int ManagerMinimumAge = 35;
    private const int ManagerMaximumAgeExclusive = 66;
    private const int ManagerMaximumSeasonsAtClub = 4;
    private const float ManagerBoardConfidenceMinimum = 0.55f;
    private const float ManagerBoardConfidenceMaximum = 0.75f;

    /// <summary>How many random full-name draws are tried before a roman-numeral suffix is added.</summary>
    private const int MaximumNameAttempts = 25;

    private const int TacticalPresetCount = 6;
    private const int PresetPossessionControl = 0;
    private const int PresetDirect = 1;
    private const int PresetDirectCounterattack = 2;
    private const int PresetHighPress = 3;
    private const int PresetLowBlock = 4;
    private const int PresetManOriented = 5;

    /// <summary>The seven squad-composition groups, in the plan's order (no attacking-midfielder group).</summary>
    private static readonly WorldRole[] GenerationGroups =
    {
        WorldRole.Goalkeeper,
        WorldRole.CenterBack,
        WorldRole.FullBack,
        WorldRole.DefensiveMidfielder,
        WorldRole.CentralMidfielder,
        WorldRole.Winger,
        WorldRole.Striker
    };

    /// <summary>The 24-player contract composition, parallel to <see cref="GenerationGroups"/>.</summary>
    private static readonly int[] DefaultGroupCounts = { 3, 4, 4, 2, 4, 3, 4 };

    /// <summary>Weighted age buckets: 16-18, 19-21, 22-24, 25-27, 28-31, 32-36 (plan §5.1).</summary>
    private static readonly (int Minimum, int Maximum, float Weight)[] AgeBuckets =
    {
        (16, 18, 0.18f),
        (19, 21, 0.26f),
        (22, 24, 0.22f),
        (25, 27, 0.18f),
        (28, 31, 0.11f),
        (32, 36, 0.05f)
    };

    /// <summary>Facility quality bands per tier, indexed by tier (index 0 unused).</summary>
    private static readonly float[] CoachingQualityMinimum = { 0f, 1.15f, 1.05f, 0.95f, 0.85f, 0.75f, 0.70f };
    private static readonly float[] CoachingQualityMaximum = { 0f, 1.30f, 1.20f, 1.10f, 1.00f, 0.88f, 0.80f };
    private static readonly float[] MedicalQualityMinimum = { 0f, 0.80f, 0.70f, 0.60f, 0.45f, 0.35f, 0.25f };
    private static readonly float[] MedicalQualityMaximum = { 0f, 0.95f, 0.85f, 0.75f, 0.60f, 0.50f, 0.40f };
    private static readonly float[] YouthQualityMinimum = { 0f, 0.70f, 0.60f, 0.50f, 0.40f, 0.30f, 0.18f };
    private static readonly float[] YouthQualityMaximum = { 0f, 0.90f, 0.80f, 0.70f, 0.55f, 0.45f, 0.32f };

    /// <summary>Fallback organised sessions per week when the division does not state a value.</summary>
    private static readonly int[] FallbackTrainingSessions = { 0, 5, 5, 4, 3, 2, 1 };

    /// <summary>Money bases per tier (plan §5.1); the generator scales each by its documented band.</summary>
    private static readonly long[] BalanceBase = { 0L, 40_000_000L, 12_000_000L, 3_000_000L, 600_000L, 80_000L, 15_000L };
    private static readonly long[] WeeklyWageBudgetBase = { 0L, 2_500_000L, 1_200_000L, 500_000L, 180_000L, 40_000L, 8_000L };
    private static readonly long[] TransferBudgetBase = { 0L, 25_000_000L, 8_000_000L, 2_000_000L, 400_000L, 30_000L, 0L };

    /// <summary>Manager reputation bands per tier, inclusive.</summary>
    private static readonly int[] ManagerReputationMinimum = { 0, 55, 45, 35, 25, 18, 12 };
    private static readonly int[] ManagerReputationMaximum = { 0, 85, 75, 65, 55, 45, 35 };

    /// <summary>
    /// Generates the complete world with the default options for the given root seed.
    /// </summary>
    /// <param name="pack">The validated data pack the world is built from.</param>
    /// <param name="seed">Root world seed.</param>
    /// <returns>A fully populated, ready-to-simulate <see cref="WorldState"/> positioned at season 1 week 1.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pack"/> is null.</exception>
    public static WorldState Generate(WorldPack pack, ulong seed) =>
        Generate(pack, new WorldGenerationOptions { Seed = seed });

    /// <summary>
    /// Generates the complete world: leagues, clubs, managers, squads, fixtures and the cup's preliminary
    /// round.
    /// </summary>
    /// <param name="pack">The validated data pack the world is built from.</param>
    /// <param name="options">Seed, starting calendar position and squad size.</param>
    /// <returns>A fully populated, ready-to-simulate <see cref="WorldState"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pack"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The starting season, the starting week or the squad size is outside its valid range.
    /// </exception>
    public static WorldState Generate(WorldPack pack, WorldGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(options);

        if (options.PlayersPerClub < MinimumPlayersPerClub)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.PlayersPerClub,
                $"A generated squad needs at least {MinimumPlayersPerClub} players (one per generation group).");
        }

        // WorldDate validates both the starting season and the week (1..52).
        var state = new WorldState(
            pack, options.Seed, new WorldDate(options.StartingSeason, options.StartingWeek));

        CreateLeagues(state, pack);
        int[] managerPresets = PlanManagerTacticalPresets(state, pack);

        var usedPlayerNames = new HashSet<string>(StringComparer.Ordinal);
        WorldRng generationStream = state.Stream(WorldRandomStream.WorldGeneration);

        int clubIndex = 0;
        foreach (ClubDef definition in pack.Clubs)
        {
            WorldLeague league = state.LeagueForTier(definition.Tier);
            var clubId = new ClubId(clubIndex + 1);
            WorldRng clubRng = generationStream.Fork((ulong)clubId.Value);

            ClubFacilities facilities = CreateFacilities(pack, definition, clubRng);
            ClubFinances finances = CreateFinances(definition.Tier, clubRng);

            var club = new WorldClub(
                clubId, definition, league.Id, ManagerId.None, facilities, finances);
            state.Clubs.Add(club);
            league.AddClub(club.Id);

            CreateManager(state, pack, club, managerPresets[clubIndex], clubRng);
            GenerateSquad(state, club, options.PlayersPerClub, clubRng, usedPlayerNames);

            clubIndex++;
        }

        // Fixtures: one named stream with a per-tier fork, so the six draws are independent.
        WorldRng fixturesRng = state.Stream(WorldRandomStream.Fixtures);
        foreach (WorldLeague league in state.Leagues)
        {
            ScheduleGenerator.GenerateLeagueSeason(state, league, fixturesRng);
        }

        // Cup: one named stream for the season's draws (the preliminary round is the first draw).
        CupScheduler.StartSeason(state, state.Stream(WorldRandomStream.Cup).Fork(0UL));
        return state;
    }

    // ---------------------------------------------------------------------------------------------
    // Leagues and managers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Registers the six divisions in tier order.</summary>
    private static void CreateLeagues(WorldState state, WorldPack pack)
    {
        for (int tier = 1; tier <= TierCount; tier++)
        {
            DivisionDef division = pack.DivisionForTier(tier);
            state.AddLeague(new WorldLeague(
                new LeagueId(tier),
                tier,
                division.Name,
                division.ShortName ?? division.Name));
        }
    }

    /// <summary>
    /// Pre-plans one tactical preset index per club. Every tier cycles the six presets and then shuffles
    /// the tier's list with a per-tier fork, so each division ends up with exactly three clubs per preset
    /// — the plan's "varied tactical styles, each style represented" requirement.
    /// </summary>
    private static int[] PlanManagerTacticalPresets(WorldState state, WorldPack pack)
    {
        int[] assignments = new int[pack.Clubs.Count];
        WorldRng generationStream = state.Stream(WorldRandomStream.WorldGeneration);

        int offset = 0;
        for (int tier = 1; tier <= TierCount; tier++)
        {
            int clubCount = pack.ClubsInTier(tier).Count;
            var tierPresets = new int[clubCount];
            for (int i = 0; i < clubCount; i++) tierPresets[i] = i % TacticalPresetCount;

            WorldRng tierRng = generationStream.Fork(WorldRandomStream.Managers, (ulong)tier);
            tierRng.Shuffle(tierPresets);

            Array.Copy(tierPresets, 0, assignments, offset, clubCount);
            offset += clubCount;
        }

        return assignments;
    }

    /// <summary>Creates the club's manager and wires the club to him.</summary>
    private static void CreateManager(
        WorldState state, WorldPack pack, WorldClub club, int tacticalPreset, WorldRng clubRng)
    {
        ManagerId id = state.AllocateManagerId();
        string firstName = pack.Names.FirstNames[clubRng.NextIndex(pack.Names.FirstNames.Count)];
        string lastName = pack.Names.LastNames[clubRng.NextIndex(pack.Names.LastNames.Count)];
        int age = ManagerMinimumAge +
            clubRng.NextInt(0, ManagerMaximumAgeExclusive - ManagerMinimumAge);
        int reputation = clubRng.NextInt(
            ManagerReputationMinimum[club.Tier], ManagerReputationMaximum[club.Tier] + 1);

        TeamTacticalProfile tacticalProfile = CreateTacticalProfile(tacticalPreset);
        FormationKind formation = FormationForPreset(tacticalPreset);

        var manager = new WorldManager(
            id, firstName, lastName, age, club.Id, tacticalProfile, formation, reputation)
        {
            BoardConfidence01 = clubRng.NextRange(
                ManagerBoardConfidenceMinimum, ManagerBoardConfidenceMaximum),
            SeasonsAtClub = clubRng.NextInt(0, ManagerMaximumSeasonsAtClub)
        };

        state.Managers.Add(manager);
        club.ManagerId = manager.Id;
    }

    /// <summary>Maps a preset index onto the match engine's tactical-profile vocabulary.</summary>
    private static TeamTacticalProfile CreateTacticalProfile(int preset) => preset switch
    {
        PresetPossessionControl => TeamTacticalProfile.PossessionControl,
        PresetDirect => TeamTacticalProfile.Direct,
        PresetDirectCounterattack => TeamTacticalProfile.DirectCounterattack,
        PresetHighPress => TeamTacticalProfile.HighPressGegenpress,
        PresetLowBlock => TeamTacticalProfile.LowBlock,
        PresetManOriented => TeamTacticalProfile.ManOriented,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown tactical preset.")
    };

    /// <summary>Plan §5.1: possession and pressing sides line up 4-3-3, everything else 4-4-2.</summary>
    private static FormationKind FormationForPreset(int preset) =>
        preset is PresetPossessionControl or PresetHighPress
            ? FormationKind.FourThreeThree
            : FormationKind.FourFourTwo;

    // ---------------------------------------------------------------------------------------------
    // Club facilities and finances.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Rolls the three facility qualities and reads the division's weekly session count.</summary>
    private static ClubFacilities CreateFacilities(WorldPack pack, ClubDef definition, WorldRng clubRng)
    {
        DivisionDef division = pack.DivisionForTier(definition.Tier);
        int tier = definition.Tier;

        var facilities = new ClubFacilities
        {
            CoachingQuality01 = clubRng.NextRange(
                CoachingQualityMinimum[tier], CoachingQualityMaximum[tier]),
            MedicalQuality01 = clubRng.NextRange(
                MedicalQualityMinimum[tier], MedicalQualityMaximum[tier]),
            YouthQuality01 = clubRng.NextRange(
                YouthQualityMinimum[tier], YouthQualityMaximum[tier])
        };

        facilities.WeeklyTrainingSessions =
            division.WeeklyTrainingSessions ?? FallbackTrainingSessions[tier];
        return facilities;
    }

    /// <summary>Rolls the tier-scaled cash balance, wage budget and transfer budget.</summary>
    private static ClubFinances CreateFinances(int tier, WorldRng clubRng)
    {
        var finances = new ClubFinances
        {
            Balance = ScaleAmount(BalanceBase[tier], clubRng, 0.7f, 1.3f),
            WeeklyWageBudget = ScaleAmount(WeeklyWageBudgetBase[tier], clubRng, 0.8f, 1.2f),
            TransferBudget = ScaleAmount(TransferBudgetBase[tier], clubRng, 0.7f, 1.3f),
            // W3's contract logic fills the wage bill once contracts exist; a fresh world has none.
            WeeklyWageBill = 0L
        };
        return finances;
    }

    /// <summary>
    /// Applies a multiplier band to a tier base amount and rounds to whole currency units. The draw is
    /// always consumed, even for the tier-6 transfer budget whose base is zero, so the documented per-club
    /// draw order is identical for every tier.
    /// </summary>
    private static long ScaleAmount(long baseAmount, WorldRng rng, float minimumMultiplier, float maximumMultiplier)
    {
        double scaled = baseAmount * (double)rng.NextRange(minimumMultiplier, maximumMultiplier);
        return (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
    }

    // ---------------------------------------------------------------------------------------------
    // Squads.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Generates one club's squad: fixed role composition, tier-tuned NPC attributes, world-unique names,
    /// an auto-selected training focus and manager trust.
    /// </summary>
    private static void GenerateSquad(
        WorldState state,
        WorldClub club,
        int playersPerClub,
        WorldRng clubRng,
        HashSet<string> usedNames)
    {
        int[] composition = BuildRoleComposition(playersPerClub);
        int slot = 0;

        for (int group = 0; group < GenerationGroups.Length; group++)
        {
            WorldRole role = GenerationGroups[group];
            TacticalRole tacticalRole = IdentityEvaluator.ToTacticalRole(role);
            TrainingFocus trainingFocus = TrainingFocusFor(role);

            for (int inGroup = 0; inGroup < composition[group]; inGroup++, slot++)
            {
                int age = RollAge(clubRng);
                PickUniqueName(clubRng, state.Pack.Names, usedNames, out string firstName, out string lastName);

                PlayerId playerId = state.AllocatePlayerId();
                ulong playerSeed = clubRng.Fork((ulong)slot).Seed;
                WorldPlayer player = PlayerGenerator.GenerateNpcForTier(
                    playerSeed, club.Tier, age, tacticalRole, playerId, firstName, lastName);

                player.ClubId = club.Id;
                player.TrainingFocus = trainingFocus;

                float trust = clubRng.NextRange(0.35f, 0.75f);
                club.AddPlayer(player.Id);
                club.SetManagerTrust(player.Id, trust);
                state.Players.Add(player);
            }
        }
    }

    /// <summary>
    /// Scales the 24-player contract composition to any supported squad size. Every generation group
    /// receives one guaranteed slot and the remainder is allocated in proportion to the contract counts
    /// by largest remainder (ties resolve in generation-group order). For 24 the result is exactly
    /// 3 GK / 4 CB / 4 FB / 2 DM / 4 CM / 3 W / 4 ST.
    /// </summary>
    private static int[] BuildRoleComposition(int playersPerClub)
    {
        int groupCount = GenerationGroups.Length;
        var counts = new int[groupCount];
        var remainders = new int[groupCount];

        int remaining = playersPerClub - groupCount;
        int allocated = 0;
        for (int group = 0; group < groupCount; group++)
        {
            counts[group] = 1;
            int numerator = remaining * DefaultGroupCounts[group];
            counts[group] += numerator / DefaultPlayersPerClub;
            remainders[group] = numerator % DefaultPlayersPerClub;
            allocated += counts[group];
        }

        // Leftover seats are fewer than the group count (the fractional parts sum to an integer), so each
        // group wins at most one. Awarding them in a fixed loop keeps the tie-break deterministic.
        int leftover = playersPerClub - allocated;
        var awarded = new bool[groupCount];
        for (int seat = 0; seat < leftover; seat++)
        {
            int best = -1;
            for (int group = 0; group < groupCount; group++)
            {
                if (awarded[group]) continue;
                if (best < 0 || remainders[group] > remainders[best]) best = group;
            }

            counts[best]++;
            awarded[best] = true;
        }

        return counts;
    }

    /// <summary>Rolls one age from the plan's weighted bands (16-36 inclusive).</summary>
    private static int RollAge(WorldRng rng)
    {
        float roll = rng.NextFloat();
        float cumulative = 0f;

        for (int bucket = 0; bucket < AgeBuckets.Length; bucket++)
        {
            (int minimum, int maximum, float weight) = AgeBuckets[bucket];
            cumulative += weight;
            if (roll < cumulative)
            {
                return minimum + rng.NextInt(0, maximum - minimum + 1);
            }
        }

        // Only reachable through float rounding of the final bucket; the last bucket owns the remainder.
        (int lastMinimum, int lastMaximum, _) = AgeBuckets[^1];
        return lastMinimum + rng.NextInt(0, lastMaximum - lastMinimum + 1);
    }

    /// <summary>
    /// Picks a player name that is unique across the whole world. Full-name collisions are re-rolled up to
    /// <see cref="MaximumNameAttempts"/> times; after that the last drawn pair gets a roman-numeral suffix
    /// ("II", "III", ...), which keeps the result deterministic without ever failing generation.
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

        for (int suffix = 2; suffix <= 3999; suffix++)
        {
            string candidate = $"{names.ComposeName(firstName, lastName)} {ToRomanNumeral(suffix)}";
            if (usedNames.Add(candidate)) return;
        }

        throw new InvalidOperationException("Could not find a unique player name in the data pack.");
    }

    /// <summary>Formats a value 1..3999 as a roman numeral (only small suffixes are used in practice).</summary>
    private static string ToRomanNumeral(int value)
    {
        if (value is < 1 or > 3999)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Roman numerals cover 1..3999.");
        }

        int[] values = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
        string[] symbols = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };

        var builder = new StringBuilder(8);
        for (int index = 0; index < values.Length; index++)
        {
            while (value >= values[index])
            {
                builder.Append(symbols[index]);
                value -= values[index];
            }
        }

        return builder.ToString();
    }

    /// <summary>Plan §4.5: AI players train the attribute group their role depends on.</summary>
    private static TrainingFocus TrainingFocusFor(WorldRole role) => role switch
    {
        WorldRole.Goalkeeper => TrainingFocus.Goalkeeping,
        WorldRole.CenterBack or WorldRole.FullBack or WorldRole.DefensiveMidfielder =>
            TrainingFocus.DefendingAndPositioning,
        WorldRole.CentralMidfielder => TrainingFocus.PassingAndVision,
        WorldRole.Winger => TrainingFocus.BallControlAndDribbling,
        WorldRole.Striker => TrainingFocus.FinishingAndShooting,
        _ => TrainingFocus.Balanced
    };
}
