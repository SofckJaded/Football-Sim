using System;
using System.Collections.Generic;
using System.Linq;
using FootballSim.Core;
using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Simulation;
using FootballSim.World.World;
using Xunit;
using Xunit.Abstractions;

namespace FootballSim.World.Tests.Simulation;

/// <summary>
/// Stage W3 manager-carousel tests: the end-of-season board-confidence verdict, the sack/hire rules and the
/// deterministic replacement managers.
///
/// <para>The real completed season (all 1,836 matches) is played once per test class and only its
/// <see cref="SeasonSummary"/> is shared; every carousel test runs on a freshly generated world (which is cheap
/// and deterministic) so the shared world is never mutated. The synthetic-outcome tests build a valid
/// six-table <see cref="SeasonOutcome"/> whose final positions are made deliberately extreme, which is the
/// cleanest way to force/avoid the positional sack rule without replaying a season.</para>
/// </summary>
public sealed class ManagerCarouselTests
{
    private const ulong DefaultSeed = 20261127UL;
    private const int ClubsInWorld = 108;
    private const int ClubsPerDivision = 18;

    private const int MinimumManagerAge = 35;
    private const int MaximumManagerAge = 65;
    private const float NewManagerBoardConfidence = 0.60f;

    private static readonly int[] ReputationMinimumByTier = { 0, 55, 45, 35, 25, 18, 12 };
    private static readonly int[] ReputationMaximumByTier = { 0, 85, 75, 65, 55, 45, 35 };

    private static readonly string[] TacticalPresetNames =
    {
        TeamTacticalProfile.PossessionControl.Name,
        TeamTacticalProfile.Direct.Name,
        TeamTacticalProfile.DirectCounterattack.Name,
        TeamTacticalProfile.HighPressGegenpress.Name,
        TeamTacticalProfile.LowBlock.Name,
        TeamTacticalProfile.ManOriented.Name
    };

    private static readonly Lazy<WorldPack> FullPack =
        new(() => WorldPackLoader.Load(WorldPackPaths.ResolveDefaultPack()));

    /// <summary>
    /// The one expensive shared artifact: a fully generated world played through its complete season. Only the
    /// immutable summary (final tables, positions and tiers) is used afterwards.
    /// </summary>
    private static readonly Lazy<SeasonSummary> CompletedSeason =
        new(() => new WorldSeasonRunner(WorldGenerator.Generate(FullPack.Value, DefaultSeed)).PlaySeason());

    private readonly ITestOutputHelper _output;

    public ManagerCarouselTests(ITestOutputHelper output) => _output = output;

    private static WorldPack Pack => FullPack.Value;

    private static WorldState NewWorld(ulong seed = DefaultSeed) => WorldGenerator.Generate(Pack, seed);

    private static SeasonOutcome CompletedOutcome() => new(CompletedSeason.Value.TablesByTier);

    // =============================================================================================
    // 1. Division expectation table.
    // =============================================================================================

    [Fact]
    public void ExpectedPointsPerGame_ReturnsTheDocumentedDivisionExpectation()
    {
        Assert.Equal(1.45f, ManagerCarousel.ExpectedPointsPerGame(1));
        Assert.Equal(1.35f, ManagerCarousel.ExpectedPointsPerGame(2));
        Assert.Equal(1.30f, ManagerCarousel.ExpectedPointsPerGame(3));
        Assert.Equal(1.25f, ManagerCarousel.ExpectedPointsPerGame(4));
        Assert.Equal(1.20f, ManagerCarousel.ExpectedPointsPerGame(5));
        Assert.Equal(1.15f, ManagerCarousel.ExpectedPointsPerGame(6));

        Assert.Throws<ArgumentOutOfRangeException>(() => ManagerCarousel.ExpectedPointsPerGame(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ManagerCarousel.ExpectedPointsPerGame(7));
    }

    [Fact]
    public void ManagerCarousel_GuardsNullArguments()
    {
        WorldState world = NewWorld();
        SeasonOutcome outcome = SyntheticOutcome(world);

        Assert.Throws<ArgumentNullException>(() => ManagerCarousel.UpdateBoardConfidence(null!, outcome));
        Assert.Throws<ArgumentNullException>(() => ManagerCarousel.UpdateBoardConfidence(world, null!));
        Assert.Throws<ArgumentNullException>(() => ManagerCarousel.Process(null!, outcome, new WorldRng(1UL)));
        Assert.Throws<ArgumentNullException>(() => ManagerCarousel.Process(world, null!, new WorldRng(1UL)));
        Assert.Throws<ArgumentNullException>(() => ManagerCarousel.Process(world, outcome, null!));
    }

    // =============================================================================================
    // 2. Board confidence.
    // =============================================================================================

    [Fact]
    public void UpdateBoardConfidence_ClampsEveryManagerAndRewardsTheChampionOverTheBottomClub()
    {
        WorldState world = NewWorld();
        SeasonOutcome outcome = CompletedOutcome();

        var seasonsAtClubBefore = world.Managers.All.ToDictionary(manager => manager.Id, manager => manager.SeasonsAtClub);

        ManagerCarousel.UpdateBoardConfidence(world, outcome);

        foreach (WorldManager manager in world.Managers.All)
        {
            Assert.InRange(manager.BoardConfidence01, 0.05f, 0.95f);
            Assert.Equal(seasonsAtClubBefore[manager.Id] + 1, manager.SeasonsAtClub);
        }

        ClubId champion = outcome.ChampionsByTier[0];
        ClubId bottom = outcome.TablesByTier[0][17].ClubId;
        WorldManager championManager = world.Managers[world.Clubs[champion].ManagerId];
        WorldManager bottomManager = world.Managers[world.Clubs[bottom].ManagerId];

        _output.WriteLine(
            $"tier-1 champion {world.Clubs[champion].Name}: confidence " +
            $"{championManager.BoardConfidence01:0.000}; bottom {world.Clubs[bottom].Name}: confidence " +
            $"{bottomManager.BoardConfidence01:0.000}");

        Assert.True(championManager.BoardConfidence01 >= 0.50f);
        Assert.True(bottomManager.BoardConfidence01 <= 0.30f);
        Assert.True(championManager.BoardConfidence01 > bottomManager.BoardConfidence01);

        // The exact documented formula holds for every club: 0.35 + 0.5 * points delta + 0.5 * position delta.
        WorldClub sample = world.Clubs.All[7];
        LeagueTableRow row = outcome.RowFor(sample.Id);
        int actualPosition = outcome.PositionOf(sample.Id);
        int tier = outcome.TierOf(sample.Id);
        int expectedPosition = ExpectedPositionInOutcome(world, outcome, sample.Id);
        float pointsPerGame = row.Points / (float)row.Played;
        float delta =
            0.5f * ((pointsPerGame - ManagerCarousel.ExpectedPointsPerGame(tier)) / 1.5f) +
            0.5f * ((expectedPosition - actualPosition) / 10f);
        float expectedConfidence = Math.Clamp(0.35f + delta, 0.05f, 0.95f);

        WorldManager sampleManager = world.Managers[sample.ManagerId];
        Assert.Equal(expectedConfidence, sampleManager.BoardConfidence01, 5);
        _output.WriteLine(
            $"formula check {sample.Name}: tier {tier}, points/game {pointsPerGame:0.000}, " +
            $"expected position {expectedPosition}, actual {actualPosition}, " +
            $"confidence {sampleManager.BoardConfidence01:0.0000}");
    }

    // =============================================================================================
    // 3. Carousel on a real completed season.
    // =============================================================================================

    [Fact]
    public void Process_OnACompletedSeason_ProducesConsistentCountsAndValidReplacements()
    {
        WorldState world = NewWorld();
        SeasonOutcome outcome = CompletedOutcome();

        var originalManagerByClub = world.Clubs.All.ToDictionary(club => club.Id, club => club.ManagerId);
        ManagerCarousel.UpdateBoardConfidence(world, outcome);
        WorldRng rng = WorldRng.For(DefaultSeed, WorldRandomStream.Managers);

        ManagerCarouselResult result = ManagerCarousel.Process(world, outcome, rng);

        int sackedClubs = result.Changes.Count;
        Assert.True(result.Sacked > 0, "a whole season produced no sackings");
        Assert.Equal(result.Sacked, result.Hired);
        Assert.Equal(sackedClubs, result.Changes.Count);
        Assert.Equal(ClubsInWorld + result.Sacked, world.Managers.Count);

        int unemployed = world.Managers.All.Count(manager => !manager.ClubId.IsValid);
        Assert.Equal(unemployed, result.UnemployedAfter);
        Assert.Equal(result.Sacked, result.UnemployedAfter);

        var changedClubs = new HashSet<ClubId>();
        foreach (ManagerChangeRecord change in result.Changes)
        {
            Assert.True(changedClubs.Add(change.ClubId), $"club {change.ClubId} changed manager twice");
            Assert.True(change.WasSacked);
            Assert.Equal(originalManagerByClub[change.ClubId], change.OutgoingManagerId);
            AssertValidReplacement(world, change);
        }

        // Every club ends with a live manager who agrees about which club employs him.
        foreach (WorldClub club in world.Clubs.All)
        {
            Assert.True(club.ManagerId.IsValid);
            WorldManager manager = world.Managers[club.ManagerId];
            Assert.Equal(club.Id, manager.ClubId);
        }

        Assert.Equal(
            ClubsInWorld,
            world.Clubs.All.Count(club => world.Managers[club.ManagerId].ClubId == club.Id));
        Assert.Equal(
            result.Sacked,
            world.Managers.All.Count(manager => !manager.ClubId.IsValid));

        _output.WriteLine(
            $"carousel on completed season {outcome.ChampionsByTier.Count} divisions: " +
            $"{result.Sacked} sacked, {result.Hired} hired, {result.UnemployedAfter} unemployed, " +
            $"{world.Managers.Count} managers registered");
    }

    // =============================================================================================
    // 4. Forced / avoided sackings.
    // =============================================================================================

    [Fact]
    public void Process_ForcesSackingForCatastrophicUnderperformanceAndSparesTheOverachiever()
    {
        WorldState world = NewWorld();
        SeasonOutcome outcome = SyntheticOutcome(world);

        // The synthetic table puts the strongest-reputation club last and the weakest-reputation club first.
        ClubId underperformer = outcome.TablesByTier[0][17].ClubId;
        ClubId overachiever = outcome.TablesByTier[0][0].ClubId;
        ManagerId underperformingManager = world.Clubs[underperformer].ManagerId;
        ManagerId overachievingManager = world.Clubs[overachiever].ManagerId;

        ManagerCarousel.UpdateBoardConfidence(world, outcome);
        Assert.True(world.Managers[underperformingManager].BoardConfidence01 < 0.30f);
        Assert.True(world.Managers[overachievingManager].BoardConfidence01 > 0.30f);

        ManagerCarouselResult result = ManagerCarousel.Process(world, outcome, new WorldRng(4242UL));

        ManagerChangeRecord change = Assert.Single(result.Changes, record => record.ClubId == underperformer);
        Assert.Equal(underperformingManager, change.OutgoingManagerId);
        Assert.True(change.WasSacked);
        Assert.Equal(ClubId.None, world.Managers[underperformingManager].ClubId);

        // The overachiever is over-performing, so neither the confidence nor the positional rule applies and
        // the 0.04 roll is never drawn for him.
        Assert.DoesNotContain(result.Changes, record => record.ClubId == overachiever);
        Assert.Equal(overachievingManager, world.Clubs[overachiever].ManagerId);

        _output.WriteLine(
            $"forced sacking: {world.Clubs[underperformer].Name} " +
            $"(confidence {world.Managers[underperformingManager].BoardConfidence01:0.000} before the sack); " +
            $"overachiever {world.Clubs[overachiever].Name} kept his manager");
    }

    [Fact]
    public void Process_SacksOnCatastrophicPositionGapEvenWithHighBoardConfidence()
    {
        WorldState world = NewWorld();
        SeasonOutcome outcome = SyntheticOutcome(world);

        ClubId underperformer = outcome.TablesByTier[0][17].ClubId;
        ClubId overachiever = outcome.TablesByTier[0][0].ClubId;
        ManagerId underperformingManager = world.Clubs[underperformer].ManagerId;
        ManagerId overachievingManager = world.Clubs[overachiever].ManagerId;

        // Deliberately detach the confidence rule: only the "6+ places down and below 14th" rule can fire.
        world.Managers[underperformingManager].BoardConfidence01 = 0.95f;
        world.Managers[overachievingManager].BoardConfidence01 = 0.95f;

        ManagerCarouselResult result = ManagerCarousel.Process(world, outcome, new WorldRng(7UL));

        Assert.Contains(result.Changes, record => record.ClubId == underperformer);
        Assert.DoesNotContain(result.Changes, record => record.ClubId == overachiever);
        Assert.Equal(overachievingManager, world.Clubs[overachiever].ManagerId);
    }

    [Fact]
    public void Process_SacksOnLowBoardConfidenceAlone()
    {
        WorldState world = NewWorld();
        SeasonOutcome outcome = SyntheticOutcome(world);

        // The weakest-reputation club finishing first massively over-performs, so the positional and random
        // rules cannot fire; only the confidence threshold can.
        ClubId overachiever = outcome.TablesByTier[2][0].ClubId;
        ManagerId overachievingManager = world.Clubs[overachiever].ManagerId;
        world.Managers[overachievingManager].BoardConfidence01 = 0.29f;

        ManagerCarouselResult result = ManagerCarousel.Process(world, outcome, new WorldRng(11UL));

        Assert.Contains(result.Changes, record => record.ClubId == overachiever);
        Assert.Equal(ClubId.None, world.Managers[overachievingManager].ClubId);
    }

    // =============================================================================================
    // 5. Determinism.
    // =============================================================================================

    [Fact]
    public void Process_IsDeterministicForTheSameSeed()
    {
        WorldState first = NewWorld();
        WorldState second = NewWorld();
        SeasonOutcome outcome = SyntheticOutcome(first);

        ManagerCarousel.UpdateBoardConfidence(first, outcome);
        ManagerCarousel.UpdateBoardConfidence(second, outcome);

        WorldRng firstRng = WorldRng.For(DefaultSeed, WorldRandomStream.Managers);
        WorldRng secondRng = WorldRng.For(DefaultSeed, WorldRandomStream.Managers);

        ManagerCarouselResult firstResult = ManagerCarousel.Process(first, outcome, firstRng);
        ManagerCarouselResult secondResult = ManagerCarousel.Process(second, outcome, secondRng);

        Assert.True(firstResult.Sacked > 0);
        Assert.Equal(firstResult.Sacked, secondResult.Sacked);
        Assert.Equal(firstResult.Hired, secondResult.Hired);
        Assert.Equal(firstResult.UnemployedAfter, secondResult.UnemployedAfter);
        Assert.Equal(firstResult.Changes, secondResult.Changes);
        Assert.Equal(firstRng.State, secondRng.State);

        Assert.Equal(first.Managers.Count, second.Managers.Count);
        for (int index = 0; index < first.Managers.Count; index++)
        {
            WorldManager left = first.Managers.All[index];
            WorldManager right = second.Managers.All[index];
            Assert.Equal(left.Id, right.Id);
            Assert.Equal(left.ClubId, right.ClubId);
            Assert.Equal(left.FirstName, right.FirstName);
            Assert.Equal(left.LastName, right.LastName);
            Assert.Equal(left.Age, right.Age);
            Assert.Equal(left.Reputation, right.Reputation);
            Assert.Equal(left.Formation, right.Formation);
            Assert.Equal(left.TacticalProfile.Name, right.TacticalProfile.Name);
            Assert.Equal(left.SeasonsAtClub, right.SeasonsAtClub);
            Assert.Equal(left.MatchesInCharge, right.MatchesInCharge);
            Assert.Equal(
                BitConverter.SingleToInt32Bits(left.BoardConfidence01),
                BitConverter.SingleToInt32Bits(right.BoardConfidence01));
        }

        for (int index = 0; index < first.Clubs.Count; index++)
        {
            Assert.Equal(first.Clubs.All[index].ManagerId, second.Clubs.All[index].ManagerId);
        }

        _output.WriteLine(
            $"deterministic carousel: {firstResult.Sacked} sacks, {firstResult.Hired} hires, " +
            $"{first.Managers.Count} managers; rng state {firstRng.State}");
    }

    // =============================================================================================
    // Helpers.
    // =============================================================================================

    /// <summary>
    /// Builds a valid six-division outcome whose final positions are deliberately extreme: within every tier
    /// the clubs are ordered by reputation ascending (so the weakest reputation finishes first and the strongest
    /// finishes last), with 18 down to 1 wins apiece.
    /// </summary>
    private static SeasonOutcome SyntheticOutcome(WorldState world)
    {
        var tables = new List<IReadOnlyList<LeagueTableRow>>(6);

        for (int tier = 1; tier <= 6; tier++)
        {
            IReadOnlyList<WorldClub> tierClubs = world.Clubs.InTier(tier);
            Assert.Equal(ClubsPerDivision, tierClubs.Count);

            var ordered = tierClubs
                .OrderBy(club => club.Reputation)
                .ThenBy(club => club.Id.Value)
                .ToList();

            var rows = new List<LeagueTableRow>(ClubsPerDivision);
            for (int position = 0; position < ordered.Count; position++)
            {
                var row = new LeagueTableRow(ordered[position].Id);
                int wins = ClubsPerDivision - position;
                int losses = 34 - wins;

                for (int win = 0; win < wins; win++) row.ApplyResult(1, 0);
                for (int loss = 0; loss < losses; loss++) row.ApplyResult(0, 1);
                rows.Add(row);
            }

            tables.Add(rows);
        }

        return new SeasonOutcome(tables);
    }

    /// <summary>Reputation rank (1..18) of a club inside the division it played in the given outcome.</summary>
    private static int ExpectedPositionInOutcome(WorldState world, SeasonOutcome outcome, ClubId clubId)
    {
        int tier = outcome.TierOf(clubId);
        IReadOnlyList<LeagueTableRow> table = outcome.TablesByTier[tier - 1];

        var clubs = new List<WorldClub>(table.Count);
        foreach (LeagueTableRow row in table) clubs.Add(world.Clubs[row.ClubId]);
        clubs.Sort(static (left, right) =>
        {
            int byReputation = right.Reputation.CompareTo(left.Reputation);
            return byReputation != 0 ? byReputation : left.Id.Value.CompareTo(right.Id.Value);
        });

        for (int position = 0; position < clubs.Count; position++)
        {
            if (clubs[position].Id == clubId) return position + 1;
        }

        throw new InvalidOperationException($"Club {clubId} is not in its outcome tier table.");
    }

    /// <summary>Asserts the outgoing manager is unemployed and the incoming manager is a valid fresh hire.</summary>
    private static void AssertValidReplacement(WorldState world, ManagerChangeRecord change)
    {
        WorldClub club = world.Clubs[change.ClubId];
        Assert.True(change.IncomingManagerId.IsValid);
        Assert.NotEqual(change.OutgoingManagerId, change.IncomingManagerId);
        Assert.Equal(change.IncomingManagerId, club.ManagerId);

        WorldManager outgoing = world.Managers[change.OutgoingManagerId];
        Assert.Equal(ClubId.None, outgoing.ClubId);

        WorldManager incoming = world.Managers[change.IncomingManagerId];
        Assert.Equal(change.ClubId, incoming.ClubId);
        Assert.False(string.IsNullOrWhiteSpace(incoming.FirstName));
        Assert.False(string.IsNullOrWhiteSpace(incoming.LastName));
        Assert.NotEqual(outgoing.DisplayName, incoming.DisplayName);
        Assert.InRange(incoming.Age, MinimumManagerAge, MaximumManagerAge);
        Assert.InRange(
            incoming.Reputation,
            ReputationMinimumByTier[club.Tier],
            ReputationMaximumByTier[club.Tier]);

        Assert.Contains(incoming.TacticalProfile.Name, TacticalPresetNames);
        bool possessionOrPress =
            incoming.TacticalProfile.Name == TeamTacticalProfile.PossessionControl.Name ||
            incoming.TacticalProfile.Name == TeamTacticalProfile.HighPressGegenpress.Name;
        Assert.Equal(
            possessionOrPress ? FormationKind.FourThreeThree : FormationKind.FourFourTwo,
            incoming.Formation);

        Assert.Equal(NewManagerBoardConfidence, incoming.BoardConfidence01, 5);
        Assert.Equal(0, incoming.SeasonsAtClub);
        Assert.Equal(0, incoming.MatchesInCharge);
        Assert.Equal(0, incoming.Wins);
        Assert.Equal(0, incoming.Draws);
        Assert.Equal(0, incoming.Losses);
    }
}
