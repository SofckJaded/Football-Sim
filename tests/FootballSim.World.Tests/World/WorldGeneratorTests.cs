using System.Diagnostics;
using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Players;
using FootballSim.World.Tests.Data;
using FootballSim.World.World;
using Xunit;

namespace FootballSim.World.Tests.World;

/// <summary>
/// W2 world-generation contract: the full 108-club / 2,592-player / 108-manager world from the shipped
/// pack, the tier-appropriate facilities and finances, the 1,836-fixture league season, the preliminary cup
/// round, determinism and generation speed.
///
/// <para><b>Role-composition note.</b> The plan's suggested assertion ("exactly three goalkeeper-primary
/// players per club, every outfield role present") does not hold for the W1 identity evaluator: at amateur
/// tiers the keeper's <see cref="AttributeId.Goalkeeping"/> current is close enough to an outfielder's
/// general attribute level that the primary emergent role of a generated keeper is often a winger or
/// centre-back. The generation contract (three keepers, ten defenders, four centre-mids, three wingers,
/// four strikers) is therefore asserted through the documented auto-selected <see cref="TrainingFocus"/>
/// — which is derived from the generation group — and the emergent-role coverage is asserted through
/// <see cref="IdentityEvaluator.SuitedTo"/> plus a world-wide primary-role census.</para>
/// </summary>
public sealed class WorldGeneratorTests
{
    private const ulong DefaultSeed = 20261127UL;

    /// <summary>One-based tier, then squad size and the plan's generation composition.</summary>
    private static readonly Lazy<WorldPack> FullPack =
        new(() => WorldPackLoader.Load(WorldPackPaths.ResolveDefaultPack()));

    private static readonly Lazy<WorldPack> UnitPack =
        new(() => WorldPackLoader.Parse(TestWorldPacks.ValidJson()));

    private static readonly Lazy<WorldState> Generated =
        new(() => WorldGenerator.Generate(FullPack.Value, DefaultSeed));

    private static readonly Lazy<WorldState> GeneratedAgain =
        new(() => WorldGenerator.Generate(FullPack.Value, DefaultSeed));

    private static readonly Lazy<WorldState> OtherSeed =
        new(() => WorldGenerator.Generate(FullPack.Value, DefaultSeed + 7UL));

    private static WorldPack Pack => FullPack.Value;
    private static WorldState World => Generated.Value;
    private static WorldState Again => GeneratedAgain.Value;
    private static WorldState Other => OtherSeed.Value;

    // Tier contract values, indexed by tier (index 0 unused). These are the W2 generation spec bands.
    private static readonly float[] CoachingMinimum = { 0f, 1.15f, 1.05f, 0.95f, 0.85f, 0.75f, 0.70f };
    private static readonly float[] CoachingMaximum = { 0f, 1.30f, 1.20f, 1.10f, 1.00f, 0.88f, 0.80f };
    private static readonly float[] MedicalMinimum = { 0f, 0.80f, 0.70f, 0.60f, 0.45f, 0.35f, 0.25f };
    private static readonly float[] MedicalMaximum = { 0f, 0.95f, 0.85f, 0.75f, 0.60f, 0.50f, 0.40f };
    private static readonly float[] YouthMinimum = { 0f, 0.70f, 0.60f, 0.50f, 0.40f, 0.30f, 0.18f };
    private static readonly float[] YouthMaximum = { 0f, 0.90f, 0.80f, 0.70f, 0.55f, 0.45f, 0.32f };

    private static readonly long[] BalanceBase = { 0L, 40_000_000L, 12_000_000L, 3_000_000L, 600_000L, 80_000L, 15_000L };
    private static readonly long[] WageBase = { 0L, 2_500_000L, 1_200_000L, 500_000L, 180_000L, 40_000L, 8_000L };
    private static readonly long[] TransferBase = { 0L, 25_000_000L, 8_000_000L, 2_000_000L, 400_000L, 30_000L, 0L };
    private static readonly float[] BalanceMinimum = { 0f, 0.70f, 0.70f, 0.70f, 0.70f, 0.70f, 0.70f };
    private static readonly float[] BalanceMaximum = { 0f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f };
    private static readonly float[] WageMinimum = { 0f, 0.80f, 0.80f, 0.80f, 0.80f, 0.80f, 0.80f };
    private static readonly float[] WageMaximum = { 0f, 1.20f, 1.20f, 1.20f, 1.20f, 1.20f, 1.20f };
    private static readonly float[] TransferMinimum = { 0f, 0.70f, 0.70f, 0.70f, 0.70f, 0.70f, 0.70f };
    private static readonly float[] TransferMaximum = { 0f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f };
    private static readonly int[] ManagerReputationMinimum = { 0, 55, 45, 35, 25, 18, 12 };
    private static readonly int[] ManagerReputationMaximum = { 0, 85, 75, 65, 55, 45, 35 };
    private static readonly int[] FallbackSessions = { 0, 5, 5, 4, 3, 2, 1 };

    private static readonly string[] TacticalPresetNames =
    {
        "Possession / Positional Play",
        "Direct",
        "Direct Counterattack",
        "High-Press Gegenpressing",
        "Low Block",
        "Man-Oriented Press"
    };

    // =============================================================================================
    // Options and overloads.
    // =============================================================================================

    [Fact]
    public void DefaultOptions_MatchTheDocumentedContract()
    {
        var options = new WorldGenerationOptions();

        Assert.Equal(20261127UL, options.Seed);
        Assert.Equal(1, options.StartingSeason);
        Assert.Equal(1, options.StartingWeek);
        Assert.Equal(24, options.PlayersPerClub);
    }

    [Fact]
    public void ConvenienceOverload_UsesTheDefaultOptions()
    {
        WorldState bySeed = WorldGenerator.Generate(UnitPack.Value, 12345UL);
        WorldState byOptions = WorldGenerator.Generate(UnitPack.Value, new WorldGenerationOptions { Seed = 12345UL });

        Assert.Equal(byOptions.Clubs.Count, bySeed.Clubs.Count);
        Assert.Equal(byOptions.Players.Count, bySeed.Players.Count);
        Assert.Equal(byOptions.Date, bySeed.Date);
        Assert.Equal(byOptions.LeagueForTier(1).Fixtures[0].Id, bySeed.LeagueForTier(1).Fixtures[0].Id);
        Assert.Equal(
            byOptions.Players.All[0].Attributes.MeanCurrent(),
            bySeed.Players.All[0].Attributes.MeanCurrent());
    }

    // =============================================================================================
    // Structural gates: leagues, clubs, managers, players.
    // =============================================================================================

    [Fact]
    public void Generate_CreatesSixLeaguesOfOneHundredAndEightClubs()
    {
        Assert.Equal(6, World.Leagues.Count);
        Assert.Equal(108, World.Clubs.Count);

        for (int tier = 1; tier <= 6; tier++)
        {
            WorldLeague league = World.LeagueForTier(tier);
            Assert.Equal(new LeagueId(tier), league.Id);
            Assert.Equal(tier, league.Tier);
            Assert.Equal(Pack.DivisionForTier(tier).Name, league.Name);
            Assert.Equal(
                Pack.DivisionForTier(tier).ShortName ?? Pack.DivisionForTier(tier).Name,
                league.ShortName);
            Assert.Equal(18, league.Clubs.Count);

            foreach (ClubId clubId in league.Clubs)
            {
                WorldClub club = World.Clubs[clubId];
                Assert.Equal(tier, club.Tier);
                Assert.Equal(league.Id, club.LeagueId);
                Assert.True(Pack.TryGetClub(FindPackClubId(club.Name), out ClubDef? definition));
                Assert.Equal(tier, definition!.Tier);
            }
        }
    }

    [Fact]
    public void Generate_CreatesOneManagerPerClub()
    {
        Assert.Equal(108, World.Managers.Count);
        Assert.Equal(108, World.Managers.All.Select(manager => manager.Id).Distinct().Count());

        foreach (WorldClub club in World.Clubs.All)
        {
            Assert.NotEqual(ManagerId.None, club.ManagerId);
            WorldManager manager = World.Managers[club.ManagerId];

            Assert.Equal(club.Id, manager.ClubId);
            Assert.Equal(club.Tier, World.Clubs[manager.ClubId].Tier);
            Assert.True(Formations.IsValid(manager.Formation));
            Assert.NotNull(manager.TacticalProfile);
            Assert.False(string.IsNullOrWhiteSpace(manager.TacticalProfile.Name));
            Assert.False(string.IsNullOrWhiteSpace(manager.DisplayName));
            Assert.InRange(manager.Age, 35, 65);
            Assert.InRange(
                manager.Reputation,
                ManagerReputationMinimum[club.Tier],
                ManagerReputationMaximum[club.Tier]);
            Assert.InRange(manager.BoardConfidence01, 0.55f, 0.75f);
            Assert.InRange(manager.SeasonsAtClub, 0, 3);
            Assert.Equal(0, manager.MatchesInCharge);

            // Plan §5.1: possession and pressing sides line up 4-3-3; everything else 4-4-2.
            FormationKind expected =
                manager.TacticalProfile.Name is "Possession / Positional Play" or "High-Press Gegenpressing"
                    ? FormationKind.FourThreeThree
                    : FormationKind.FourFourTwo;
            Assert.Equal(expected, manager.Formation);
        }

        // Every tactical preset is guaranteed representation, and the cycle gives three clubs per tier.
        for (int tier = 1; tier <= 6; tier++)
        {
            IReadOnlyList<WorldClub> clubs = World.Clubs.InTier(tier);
            foreach (string preset in TacticalPresetNames)
            {
                int count = 0;
                foreach (WorldClub club in clubs)
                {
                    if (World.Managers[club.ManagerId].TacticalProfile.Name == preset) count++;
                }

                Assert.Equal(3, count);
            }
        }
    }

    [Fact]
    public void Generate_CreatesTwentyFourPlayersPerClub()
    {
        Assert.Equal(2592, World.Players.Count);

        var registeredIds = new HashSet<PlayerId>();
        int squadTotal = 0;

        foreach (WorldClub club in World.Clubs.All)
        {
            Assert.Equal(24, club.Squad.Count);
            squadTotal += club.Squad.Count;

            foreach (PlayerId playerId in club.Squad)
            {
                Assert.True(World.Players.TryGet(playerId, out WorldPlayer? player));
                Assert.NotNull(player);
                Assert.Equal(playerId, player!.Id);
                Assert.Equal(club.Id, player.ClubId);
                Assert.True(registeredIds.Add(playerId), $"player {playerId} appears in two squads");
            }
        }

        Assert.Equal(World.Players.Count, squadTotal);
        Assert.Equal(World.Players.Count, registeredIds.Count);

        foreach (WorldPlayer player in World.Players.All)
        {
            Assert.True(player.Id.IsValid);
            Assert.True(player.ClubId.IsValid);
            Assert.True(World.Clubs.TryGet(player.ClubId, out WorldClub? club));
            Assert.Contains(player.Id, club!.Squad);
        }
    }

    [Fact]
    public void Generate_AllPlayerNamesAreUnique()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (WorldPlayer player in World.Players.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(player.DisplayName));
            Assert.True(names.Add(player.DisplayName), $"duplicate player name '{player.DisplayName}'");
        }

        Assert.Equal(2592, names.Count);
    }

    [Fact]
    public void Generate_SquadsUseTheDocumentedComposition()
    {
        // The auto-selected training focus is derived from the generation group, so it is the observable
        // form of the 3 GK / 4 CB / 4 FB / 2 DM / 4 CM / 3 W / 4 ST contract.
        foreach (WorldClub club in World.Clubs.All)
        {
            int goalkeepers = 0;
            int defenders = 0;
            int centreMids = 0;
            int wingers = 0;
            int strikers = 0;

            foreach (PlayerId playerId in club.Squad)
            {
                switch (World.Players[playerId].TrainingFocus)
                {
                    case TrainingFocus.Goalkeeping: goalkeepers++; break;
                    case TrainingFocus.DefendingAndPositioning: defenders++; break;
                    case TrainingFocus.PassingAndVision: centreMids++; break;
                    case TrainingFocus.BallControlAndDribbling: wingers++; break;
                    case TrainingFocus.FinishingAndShooting: strikers++; break;
                    default: Assert.Fail("a generated outfield player had no auto-selected focus"); break;
                }
            }

            Assert.Equal(3, goalkeepers);
            Assert.Equal(10, defenders); // 4 centre-backs + 4 full-backs + 2 defensive mids
            Assert.Equal(4, centreMids);
            Assert.Equal(3, wingers);
            Assert.Equal(4, strikers);
        }
    }

    [Fact]
    public void Generate_SquadsCoverEveryEmergentRole()
    {
        // Every club must be fieldable: someone is suited to every functional role (including goalkeeper).
        var primaryCensus = new Dictionary<WorldRole, int>();
        foreach (WorldRole role in Enum.GetValues<WorldRole>()) primaryCensus[role] = 0;

        foreach (WorldClub club in World.Clubs.All)
        {
            var suited = new Dictionary<WorldRole, bool>();
            foreach (WorldRole role in Enum.GetValues<WorldRole>()) suited[role] = false;

            foreach (PlayerId playerId in club.Squad)
            {
                EmergentIdentity identity = IdentityEvaluator.Evaluate(World.Players[playerId]);
                primaryCensus[identity.PrimaryRole]++;
                foreach (WorldRole role in Enum.GetValues<WorldRole>())
                {
                    if (identity.SuitedTo(role)) suited[role] = true;
                }
            }

            foreach (WorldRole role in Enum.GetValues<WorldRole>())
            {
                Assert.True(suited[role], $"{club.Name} has no player suited to {role}");
            }
        }

        // Across the whole world every emergent role actually occurs as a primary role.
        foreach (WorldRole role in Enum.GetValues<WorldRole>())
        {
            Assert.True(primaryCensus[role] > 0, $"no player in the world has {role} as primary role");
        }
    }

    [Fact]
    public void Generate_PlayerAgesFollowTheWeightedBands()
    {
        int minimum = int.MaxValue;
        int maximum = int.MinValue;
        double sum = 0;
        int[] bucketCounts = new int[6];

        foreach (WorldPlayer player in World.Players.All)
        {
            minimum = Math.Min(minimum, player.Age);
            maximum = Math.Max(maximum, player.Age);
            sum += player.Age;

            int bucket = player.Age switch
            {
                <= 18 => 0,
                <= 21 => 1,
                <= 24 => 2,
                <= 27 => 3,
                <= 31 => 4,
                _ => 5
            };
            bucketCounts[bucket]++;
        }

        Assert.Equal(16, minimum);
        Assert.Equal(36, maximum);

        double mean = sum / World.Players.Count;
        Assert.InRange(mean, 22.0, 27.0);

        float[] expectedWeights = { 0.18f, 0.26f, 0.22f, 0.18f, 0.11f, 0.05f };
        for (int bucket = 0; bucket < bucketCounts.Length; bucket++)
        {
            double expected = expectedWeights[bucket] * World.Players.Count;
            double tolerance = World.Players.Count * 0.03;
            Assert.InRange((double)bucketCounts[bucket], expected - tolerance, expected + tolerance);
        }
    }

    [Fact]
    public void Generate_TierStrengthIsStrictlyIncreasing()
    {
        double[] means = new double[7];
        for (int tier = 1; tier <= 6; tier++)
        {
            double sum = 0;
            int count = 0;
            foreach (WorldClub club in World.Clubs.InTier(tier))
            {
                foreach (PlayerId playerId in club.Squad)
                {
                    sum += World.Players[playerId].Attributes.MeanCurrent();
                    count++;
                }
            }

            Assert.Equal(432, count);
            means[tier] = sum / count;
        }

        for (int tier = 1; tier < 6; tier++)
        {
            Assert.True(
                means[tier] > means[tier + 1],
                $"tier {tier} mean current {means[tier]:F2} is not above tier {tier + 1} {means[tier + 1]:F2}");
        }

        // The pyramid must actually separate, not just be ordered by noise.
        Assert.True(means[1] - means[6] > 20.0, $"tier 1 ({means[1]:F2}) is not clearly above tier 6 ({means[6]:F2})");
    }

    // =============================================================================================
    // Facilities and finances.
    // =============================================================================================

    [Fact]
    public void Generate_FacilitiesSitInTierBandsAndImproveWithTier()
    {
        float[] coachingMean = new float[7];
        float[] medicalMean = new float[7];
        float[] youthMean = new float[7];

        for (int tier = 1; tier <= 6; tier++)
        {
            IReadOnlyList<WorldClub> clubs = World.Clubs.InTier(tier);
            float coachingSum = 0f;
            float medicalSum = 0f;
            float youthSum = 0f;

            foreach (WorldClub club in clubs)
            {
                Assert.InRange(club.Facilities.CoachingQuality01, CoachingMinimum[tier], CoachingMaximum[tier]);
                Assert.InRange(club.Facilities.MedicalQuality01, MedicalMinimum[tier], MedicalMaximum[tier]);
                Assert.InRange(club.Facilities.YouthQuality01, YouthMinimum[tier], YouthMaximum[tier]);

                int expectedSessions = Pack.DivisionForTier(tier).WeeklyTrainingSessions ?? FallbackSessions[tier];
                Assert.Equal(Math.Clamp(expectedSessions, 1, 6), club.Facilities.WeeklyTrainingSessions);

                coachingSum += club.Facilities.CoachingQuality01;
                medicalSum += club.Facilities.MedicalQuality01;
                youthSum += club.Facilities.YouthQuality01;
            }

            coachingMean[tier] = coachingSum / clubs.Count;
            medicalMean[tier] = medicalSum / clubs.Count;
            youthMean[tier] = youthSum / clubs.Count;
        }

        for (int tier = 1; tier < 6; tier++)
        {
            Assert.True(coachingMean[tier] > coachingMean[tier + 1], $"coaching mean does not fall from tier {tier}");
            Assert.True(medicalMean[tier] > medicalMean[tier + 1], $"medical mean does not fall from tier {tier}");
            Assert.True(youthMean[tier] > youthMean[tier + 1], $"youth mean does not fall from tier {tier}");
        }
    }

    [Fact]
    public void Generate_FinancesSitInTierBandsAndImproveWithTier()
    {
        for (int tier = 1; tier <= 6; tier++)
        {
            long balanceFloor = (long)Math.Floor(BalanceBase[tier] * (double)BalanceMinimum[tier]);
            long balanceCeiling = (long)Math.Ceiling(BalanceBase[tier] * (double)BalanceMaximum[tier]);
            long wageFloor = (long)Math.Floor(WageBase[tier] * (double)WageMinimum[tier]);
            long wageCeiling = (long)Math.Ceiling(WageBase[tier] * (double)WageMaximum[tier]);
            long transferFloor = (long)Math.Floor(TransferBase[tier] * (double)TransferMinimum[tier]);
            long transferCeiling = (long)Math.Ceiling(TransferBase[tier] * (double)TransferMaximum[tier]);

            foreach (WorldClub club in World.Clubs.InTier(tier))
            {
                Assert.InRange(club.Finances.Balance, balanceFloor, balanceCeiling);
                Assert.InRange(club.Finances.WeeklyWageBudget, wageFloor, wageCeiling);
                Assert.InRange(club.Finances.TransferBudget, transferFloor, transferCeiling);
                Assert.Equal(0L, club.Finances.WeeklyWageBill); // W3's contract logic fills the wage bill.
            }
        }

        // The tier bands do not overlap, so every club in a higher division is richer than every club below.
        for (int tier = 1; tier < 6; tier++)
        {
            long lowerBalanceMax = World.Clubs.InTier(tier + 1).Max(club => club.Finances.Balance);
            long lowerWageMax = World.Clubs.InTier(tier + 1).Max(club => club.Finances.WeeklyWageBudget);
            long lowerTransferMax = World.Clubs.InTier(tier + 1).Max(club => club.Finances.TransferBudget);

            Assert.True(World.Clubs.InTier(tier).All(club => club.Finances.Balance > lowerBalanceMax));
            Assert.True(World.Clubs.InTier(tier).All(club => club.Finances.WeeklyWageBudget > lowerWageMax));
            Assert.True(World.Clubs.InTier(tier).All(club => club.Finances.TransferBudget > lowerTransferMax));
        }
    }

    // =============================================================================================
    // Fixtures and cup wiring.
    // =============================================================================================

    [Fact]
    public void Generate_CreatesOneThousandEightHundredThirtySixLeagueFixtures()
    {
        int total = 0;
        var matchIds = new HashSet<int>();

        foreach (WorldLeague league in World.Leagues)
        {
            Assert.Equal(306, league.Fixtures.Count);
            total += league.Fixtures.Count;

            foreach (WorldFixture fixture in league.Fixtures)
            {
                Assert.Equal(CompetitionKind.League, fixture.Competition);
                Assert.Equal(league.Id, fixture.LeagueId);
                Assert.Equal(CupRound.None, fixture.CupRound);
                Assert.Equal(World.Season, fixture.Season);
                Assert.InRange(fixture.LeagueMatchday, 1, 34);
                Assert.True(matchIds.Add(fixture.Id.Value), $"duplicate match id {fixture.Id}");
                Assert.False(fixture.IsPlayed);
            }
        }

        Assert.Equal(1836, total);
        Assert.Equal(1854, World.CurrentSeasonFixtures().Count());
    }

    [Fact]
    public void Generate_CreatesThePreliminaryCupRound()
    {
        Assert.Single(World.Cup.Rounds);

        CupRoundBracket round = World.Cup.RoundOf(CupRound.Preliminary)!;
        Assert.Equal(CupRound.Preliminary, round.Round);
        Assert.Equal(5, round.Week);
        Assert.Equal(18, round.Ties.Count);
        Assert.Empty(round.Byes);
        Assert.False(World.Cup.IsComplete);

        var participants = new HashSet<ClubId>();
        foreach (CupTie tie in round.Ties)
        {
            Assert.True(tie.Fixture.IsCupTie);
            Assert.Equal(CompetitionKind.Cup, tie.Fixture.Competition);
            Assert.Equal(LeagueId.None, tie.Fixture.LeagueId);
            Assert.Equal(0, tie.Fixture.LeagueMatchday);
            Assert.Equal(CupRound.Preliminary, tie.Fixture.CupRound);
            Assert.Equal(5, tie.Fixture.Week);
            Assert.False(tie.Fixture.IsPlayed);

            Assert.True(participants.Add(tie.HomeClubId));
            Assert.True(participants.Add(tie.AwayClubId));
        }

        Assert.Equal(36, participants.Count);
        Assert.All(participants, clubId => Assert.InRange(World.Clubs[clubId].Tier, 5, 6));
        Assert.Equal(18, participants.Count(clubId => World.Clubs[clubId].Tier == 5));
        Assert.Equal(18, participants.Count(clubId => World.Clubs[clubId].Tier == 6));
    }

    // =============================================================================================
    // Determinism and speed.
    // =============================================================================================

    [Fact]
    public void Generate_IsDeterministicForTheSameSeed()
    {
        Assert.Equal(Again.Clubs.Count, World.Clubs.Count);
        for (int index = 0; index < World.Clubs.Count; index++)
        {
            WorldClub expected = World.Clubs.All[index];
            WorldClub actual = Again.Clubs.All[index];

            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Tier, actual.Tier);
            Assert.Equal(expected.LeagueId, actual.LeagueId);
            Assert.Equal(expected.ManagerId, actual.ManagerId);
            Assert.Equal(expected.Facilities.CoachingQuality01, actual.Facilities.CoachingQuality01);
            Assert.Equal(expected.Facilities.MedicalQuality01, actual.Facilities.MedicalQuality01);
            Assert.Equal(expected.Facilities.YouthQuality01, actual.Facilities.YouthQuality01);
            Assert.Equal(expected.Finances.Balance, actual.Finances.Balance);
            Assert.Equal(expected.Finances.WeeklyWageBudget, actual.Finances.WeeklyWageBudget);
            Assert.Equal(expected.Finances.TransferBudget, actual.Finances.TransferBudget);
        }

        for (int index = 0; index < World.Managers.Count; index++)
        {
            WorldManager expected = World.Managers.All[index];
            WorldManager actual = Again.Managers.All[index];

            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.DisplayName, actual.DisplayName);
            Assert.Equal(expected.Age, actual.Age);
            Assert.Equal(expected.Reputation, actual.Reputation);
            Assert.Equal(expected.TacticalProfile.Name, actual.TacticalProfile.Name);
            Assert.Equal(expected.Formation, actual.Formation);
            Assert.Equal(expected.BoardConfidence01, actual.BoardConfidence01);
            Assert.Equal(expected.SeasonsAtClub, actual.SeasonsAtClub);
        }

        // Player attributes are sampled (every 89th player) but compared bit-for-bit on current and ceiling.
        for (int index = 0; index < World.Players.Count; index += 89)
        {
            AssertSamePlayer(World.Players.All[index], Again.Players.All[index]);
        }

        AssertSameFixtureLists(World, Again);
        AssertSameCup(World, Again);
    }

    [Fact]
    public void Generate_DifferentSeedProducesADifferentWorld()
    {
        Assert.Equal(World.Clubs.Count, Other.Clubs.Count);
        Assert.Equal(World.Players.Count, Other.Players.Count);

        bool anyDifference = false;

        for (int index = 0; index < World.Clubs.Count && !anyDifference; index++)
        {
            WorldClub left = World.Clubs.All[index];
            WorldClub right = Other.Clubs.All[index];
            anyDifference = left.Finances.Balance != right.Finances.Balance ||
                left.Finances.TransferBudget != right.Finances.TransferBudget ||
                left.Facilities.CoachingQuality01 != right.Facilities.CoachingQuality01;
        }

        for (int index = 0; index < World.Leagues.Count && !anyDifference; index++)
        {
            WorldFixture left = World.Leagues[index].Fixtures[0];
            WorldFixture right = Other.Leagues[index].Fixtures[0];
            anyDifference = left.HomeClubId != right.HomeClubId || left.AwayClubId != right.AwayClubId;
        }

        for (int index = 0; index < World.Players.Count && !anyDifference; index += 61)
        {
            WorldPlayer left = World.Players.All[index];
            WorldPlayer right = Other.Players.All[index];
            anyDifference = left.Age != right.Age || left.DisplayName != right.DisplayName ||
                left.Attributes.MeanCurrent() != right.Attributes.MeanCurrent();
        }

        Assert.True(anyDifference, "a different seed produced an identical world");
    }

    [Fact]
    public void Generate_CompletesInWellUnderTheBudget()
    {
        var stopwatch = Stopwatch.StartNew();
        WorldState world = WorldGenerator.Generate(Pack, DefaultSeed);
        stopwatch.Stop();

        Assert.Equal(2592, world.Players.Count);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 3000,
            $"generation took {stopwatch.ElapsedMilliseconds} ms, expected well under 3000 ms");
    }

    // =============================================================================================
    // Options: start date and squad scaling (unit pack keeps these fast).
    // =============================================================================================

    [Fact]
    public void Generate_RespectsTheStartingDate()
    {
        WorldState world = WorldGenerator.Generate(
            UnitPack.Value,
            new WorldGenerationOptions { Seed = 5UL, StartingSeason = 3, StartingWeek = 10 });

        Assert.Equal(3, world.Date.Season);
        Assert.Equal(10, world.Date.Week);
        Assert.Equal(3, world.Cup.Season);
        Assert.All(world.CurrentSeasonFixtures(), fixture => Assert.Equal(3, fixture.Season));
    }

    [Fact]
    public void Generate_ScalesTheSquadSizeSensibly()
    {
        const int playersPerClub = 12;
        WorldState world = WorldGenerator.Generate(
            UnitPack.Value,
            new WorldGenerationOptions { Seed = 9UL, PlayersPerClub = playersPerClub });

        Assert.Equal(108 * playersPerClub, world.Players.Count);

        foreach (WorldClub club in world.Clubs.All)
        {
            Assert.Equal(playersPerClub, club.Squad.Count);

            int goalkeepers = club.Squad.Count(playerId =>
                world.Players[playerId].TrainingFocus == TrainingFocus.Goalkeeping);
            Assert.InRange(goalkeepers, 1, playersPerClub);

            foreach (PlayerId playerId in club.Squad)
            {
                Assert.True(Enum.IsDefined(world.Players[playerId].TrainingFocus));
            }
        }
    }

    [Fact]
    public void Generate_RejectsATooSmallSquad()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldGenerator.Generate(
            UnitPack.Value,
            new WorldGenerationOptions { PlayersPerClub = 6 }));
    }

    [Fact]
    public void Generate_RejectsAnEmptyPack()
    {
        Assert.Throws<ArgumentNullException>(() => WorldGenerator.Generate((WorldPack)null!, DefaultSeed));
    }

    // =============================================================================================
    // Helpers.
    // =============================================================================================

    /// <summary>Finds the data-pack id behind a generated club name (the world does not store pack ids).</summary>
    private static string FindPackClubId(string clubName)
    {
        foreach (ClubDef definition in Pack.Clubs)
        {
            if (string.Equals(definition.Name, clubName, StringComparison.Ordinal)) return definition.Id;
        }

        throw new InvalidOperationException($"Club '{clubName}' does not come from the pack.");
    }

    private static void AssertSamePlayer(WorldPlayer expected, WorldPlayer actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.Age, actual.Age);
        Assert.Equal(expected.ClubId, actual.ClubId);
        Assert.Equal(expected.TrainingFocus, actual.TrainingFocus);
        Assert.Equal(expected.Backstory, actual.Backstory);

        foreach (AttributeId id in AttributeMetadata.All)
        {
            Assert.Equal(
                BitConverter.SingleToInt32Bits(expected.Attributes.Current(id)),
                BitConverter.SingleToInt32Bits(actual.Attributes.Current(id)));
            Assert.Equal(
                BitConverter.SingleToInt32Bits(expected.Attributes.Ceiling(id)),
                BitConverter.SingleToInt32Bits(actual.Attributes.Ceiling(id)));
        }
    }

    private static void AssertSameFixtureLists(WorldState expected, WorldState actual)
    {
        for (int leagueIndex = 0; leagueIndex < expected.Leagues.Count; leagueIndex++)
        {
            WorldLeague left = expected.Leagues[leagueIndex];
            WorldLeague right = actual.Leagues[leagueIndex];
            Assert.Equal(left.Fixtures.Count, right.Fixtures.Count);

            for (int fixtureIndex = 0; fixtureIndex < left.Fixtures.Count; fixtureIndex++)
            {
                WorldFixture leftFixture = left.Fixtures[fixtureIndex];
                WorldFixture rightFixture = right.Fixtures[fixtureIndex];

                Assert.Equal(leftFixture.Id, rightFixture.Id);
                Assert.Equal(leftFixture.HomeClubId, rightFixture.HomeClubId);
                Assert.Equal(leftFixture.AwayClubId, rightFixture.AwayClubId);
                Assert.Equal(leftFixture.LeagueMatchday, rightFixture.LeagueMatchday);
                Assert.Equal(leftFixture.Week, rightFixture.Week);
                Assert.Equal(leftFixture.Season, rightFixture.Season);
            }
        }
    }

    private static void AssertSameCup(WorldState expected, WorldState actual)
    {
        Assert.Equal(expected.Cup.Rounds.Count, actual.Cup.Rounds.Count);

        for (int roundIndex = 0; roundIndex < expected.Cup.Rounds.Count; roundIndex++)
        {
            CupRoundBracket left = expected.Cup.Rounds[roundIndex];
            CupRoundBracket right = actual.Cup.Rounds[roundIndex];

            Assert.Equal(left.Round, right.Round);
            Assert.Equal(left.Week, right.Week);
            Assert.Equal(left.Ties.Count, right.Ties.Count);
            Assert.Equal(left.Byes.Count, right.Byes.Count);

            for (int tieIndex = 0; tieIndex < left.Ties.Count; tieIndex++)
            {
                Assert.Equal(left.Ties[tieIndex].Fixture.Id, right.Ties[tieIndex].Fixture.Id);
                Assert.Equal(left.Ties[tieIndex].HomeClubId, right.Ties[tieIndex].HomeClubId);
                Assert.Equal(left.Ties[tieIndex].AwayClubId, right.Ties[tieIndex].AwayClubId);
            }
        }
    }
}
