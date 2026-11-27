using FootballSim.World.Data;
using Xunit;

namespace FootballSim.World.Tests.Data;

/// <summary>
/// Content gate for the shipped moddable pack: one fictional country, six tiers of eighteen clubs each,
/// tier-appropriate reputations and capacities, and name pools rich enough for ~2,592 players.
/// </summary>
public sealed class DefaultWorldPackTests
{
    private static readonly Lazy<WorldPack> LazyPack =
        new(() => WorldPackLoader.Load(WorldPackPaths.ResolveDefaultPack()));

    private static WorldPack Pack => LazyPack.Value;

    [Fact]
    public void Pack_DescribesOneFictionalCountry()
    {
        Assert.Equal("valdoria", Pack.Country.Id);
        Assert.Contains("Valdoria", Pack.Country.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("VAL", Pack.Country.Code);
        Assert.DoesNotContain(
            new[] { "england", "spain", "italy", "germany", "france", "brazil", "argentina" },
            c => Pack.Country.Name.Contains(c, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pack_HasSixDivisionsOfEighteenUniqueClubs()
    {
        Assert.Equal(6, Pack.TierCount);
        Assert.Equal(108, Pack.ClubCount);

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (ClubDef club in Pack.Clubs)
        {
            Assert.True(ids.Add(club.Id), $"duplicate club id {club.Id}");
            Assert.True(names.Add(club.Name), $"duplicate club name {club.Name}");
            Assert.True(codes.Add(club.ShortCode), $"duplicate short code {club.ShortCode}");
        }

        for (int tier = 1; tier <= 6; tier++)
        {
            Assert.Equal(tier, Pack.DivisionForTier(tier).Tier);
            Assert.Equal(18, Pack.ClubsInTier(tier).Count);
            Assert.All(Pack.ClubsInTier(tier), club => Assert.Equal(tier, club.Tier));
        }
    }

    [Fact]
    public void Pack_ReputationsSitInTierBandsAndDescend()
    {
        int[][] bands =
        {
            new[] { 72, 92 }, new[] { 64, 80 }, new[] { 56, 70 },
            new[] { 48, 62 }, new[] { 38, 54 }, new[] { 28, 44 }
        };

        double[] means = new double[6];
        for (int tier = 1; tier <= 6; tier++)
        {
            IReadOnlyList<ClubDef> clubs = Pack.ClubsInTier(tier);
            foreach (ClubDef club in clubs)
            {
                Assert.InRange(club.Reputation, bands[tier - 1][0], bands[tier - 1][1]);
            }
            means[tier - 1] = clubs.Average(c => c.Reputation);
        }

        for (int tier = 1; tier < 6; tier++)
        {
            Assert.True(
                means[tier - 1] > means[tier],
                $"tier {tier} mean reputation {means[tier - 1]} is not above tier {tier + 1} {means[tier]}");
        }

        // The pyramid must actually separate: top flight clearly stronger than amateur.
        Assert.True(means[0] - means[5] >= 30);
    }

    [Fact]
    public void Pack_CapacitiesMatchTierScale()
    {
        int[][] bands =
        {
            new[] { 20_000, 60_000 }, new[] { 12_000, 35_000 }, new[] { 6_000, 20_000 },
            new[] { 2_500, 12_000 }, new[] { 1_000, 5_000 }, new[] { 300, 2_000 }
        };

        for (int tier = 1; tier <= 6; tier++)
        {
            foreach (ClubDef club in Pack.ClubsInTier(tier))
            {
                Assert.InRange(club.Capacity, bands[tier - 1][0], bands[tier - 1][1]);
            }
        }
    }

    [Fact]
    public void Pack_KitColorsAreValidAndDistinct()
    {
        for (int tier = 1; tier <= 6; tier++)
        {
            foreach (ClubDef club in Pack.ClubsInTier(tier))
            {
                Assert.NotEqual(club.PrimaryColor, club.SecondaryColor);
                Assert.StartsWith("#", club.PrimaryColorHex, StringComparison.Ordinal);
                Assert.Equal(7, club.PrimaryColorHex.Length);
                Assert.Equal(7, club.SecondaryColorHex.Length);
            }
        }
    }

    [Fact]
    public void Pack_NamePoolsAreRichAndUnique()
    {
        Assert.True(Pack.Names.FirstNames.Count >= 200);
        Assert.True(Pack.Names.LastNames.Count >= 200);
        Assert.Equal(Pack.Names.FirstNames.Count, Pack.Names.FirstNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(Pack.Names.LastNames.Count, Pack.Names.LastNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(Pack.Names.FirstNames, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.All(Pack.Names.LastNames, n => Assert.False(string.IsNullOrWhiteSpace(n)));

        // Enough combinations for a ~2,592-player world even before any nickname logic.
        Assert.True((long)Pack.Names.FirstNames.Count * Pack.Names.LastNames.Count >= 2_592 * 20);
    }

    [Fact]
    public void Pack_ClubsResolveByIdAndDivision()
    {
        foreach (ClubDef club in Pack.Clubs)
        {
            Assert.True(Pack.TryGetClub(club.Id, out ClubDef? found));
            Assert.Same(club, found);
            Assert.True(Pack.TryGetDivisionById(club.DivisionId, out DivisionDef? division));
            Assert.Equal(club.Tier, division!.Tier);
        }

        Assert.False(Pack.TryGetClub("not-a-club", out _));
        Assert.False(Pack.TryGetDivision(7, out _));
        Assert.Throws<KeyNotFoundException>(() => Pack.DivisionForTier(7));
    }

    [Fact]
    public void Pack_DefinesEconomicStatusPerTier()
    {
        string[] expected =
        {
            "FullTimePro", "FullTimePro", "LowerPro", "SemiPro", "UpperAmateur", "Amateur"
        };

        for (int tier = 1; tier <= 6; tier++)
        {
            DivisionDef division = Pack.DivisionForTier(tier);
            Assert.Equal(expected[tier - 1], division.ClubStatus);
            Assert.False(string.IsNullOrWhiteSpace(division.Name));
            Assert.False(string.IsNullOrWhiteSpace(division.ShortName));
            Assert.InRange(division.WeeklyTrainingSessions ?? 0, 1, 7);
        }
    }
}
