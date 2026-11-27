using System.Text.Json.Nodes;
using FootballSim.World.Data;
using Xunit;

namespace FootballSim.World.Tests.Data;

/// <summary>
/// Loader contract: a valid pack becomes a validated <see cref="WorldPack"/>, and every broken pack is
/// rejected with a message naming the offending entry (plan gate W0).
/// </summary>
public sealed class WorldPackLoaderTests
{
    [Fact]
    public void ValidPack_ParsesWithAllClubsDivisionsAndNames()
    {
        WorldPack pack = WorldPackLoader.Parse(TestWorldPacks.ValidJson(), "test-pack");

        Assert.Equal(WorldPack.SupportedSchemaVersion, pack.SchemaVersion);
        Assert.Equal("test-country", pack.PackId);
        Assert.Equal("valdoria", pack.Country.Id);
        Assert.Equal("VAL", pack.Country.Code);
        Assert.Equal(6, pack.TierCount);
        Assert.Equal(108, pack.ClubCount);
        Assert.Equal(TestWorldPacks.NamePoolSize, pack.Names.FirstNames.Count);
        Assert.Equal(TestWorldPacks.NamePoolSize, pack.Names.LastNames.Count);

        for (int tier = 1; tier <= 6; tier++)
        {
            Assert.Equal(tier, pack.DivisionForTier(tier).Tier);
            Assert.Equal(18, pack.ClubsInTier(tier).Count);
        }

        Assert.True(pack.TryGetClub("val-d3-007", out ClubDef? club));
        Assert.NotNull(club);
        Assert.Equal(3, club!.Tier);
        Assert.Equal("val-d3", club.DivisionId);
    }

    [Fact]
    public void ValidPack_OrdersDivisionsAndClubsDeterministically()
    {
        WorldPack first = WorldPackLoader.Parse(TestWorldPacks.ValidJson(), "one");
        WorldPack second = WorldPackLoader.Parse(TestWorldPacks.ValidJson(), "two");

        Assert.Equal(first.Divisions.Select(d => d.Tier), second.Divisions.Select(d => d.Tier));
        Assert.Equal(first.Clubs.Select(c => c.Id), second.Clubs.Select(c => c.Id));

        // Clubs are ordered by tier then id, whatever order the JSON used.
        for (int i = 1; i < first.Clubs.Count; i++)
        {
            ClubDef previous = first.Clubs[i - 1];
            ClubDef current = first.Clubs[i];
            Assert.True(
                previous.Tier < current.Tier ||
                (previous.Tier == current.Tier && string.CompareOrdinal(previous.Id, current.Id) < 0),
                $"clubs[{i}] '{current.Id}' is out of order after '{previous.Id}'");
        }
    }

    [Fact]
    public void Load_FromFile_ReadsThePack()
    {
        string path = Path.Combine(Path.GetTempPath(), $"world-pack-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, TestWorldPacks.ValidJson());
            WorldPack pack = WorldPackLoader.Load(path);
            Assert.Equal(108, pack.ClubCount);
            Assert.Equal(Path.GetFullPath(path), pack.SourceName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFound()
    {
        string path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");
        Assert.Throws<FileNotFoundException>(() => WorldPackLoader.Load(path));
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        WorldPackException error = Assert.Throws<WorldPackException>(
            () => WorldPackLoader.Parse("{ not json", "broken"));
        Assert.Contains("invalid JSON", error.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_UnsupportedSchemaVersion_Throws()
    {
        string json = TestWorldPacks.Mutate(
            TestWorldPacks.ValidJson(), root => root["schemaVersion"] = 99);
        WorldPackException error = Assert.Throws<WorldPackException>(
            () => WorldPackLoader.Parse(json));
        Assert.Contains(error.Errors, e => e.Contains("schemaVersion", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_DuplicateClubId_Throws()
    {
        string json = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root =>
            TestWorldPacks.Club(root, 1)["id"] = TestWorldPacks.Club(root, 0)["id"]!.GetValue<string>());

        WorldPackException error = Assert.Throws<WorldPackException>(() => WorldPackLoader.Parse(json));
        Assert.Contains(error.Errors, e => e.Contains("duplicate club id", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_DuplicateClubName_Throws()
    {
        string json = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root =>
            TestWorldPacks.Club(root, 1)["name"] =
                TestWorldPacks.Club(root, 0)["name"]!.GetValue<string>());

        WorldPackException error = Assert.Throws<WorldPackException>(() => WorldPackLoader.Parse(json));
        Assert.Contains(error.Errors, e => e.Contains("duplicate club name", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_DuplicateShortCode_Throws()
    {
        string json = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root =>
            TestWorldPacks.Club(root, 1)["shortCode"] =
                TestWorldPacks.Club(root, 0)["shortCode"]!.GetValue<string>());

        WorldPackException error = Assert.Throws<WorldPackException>(() => WorldPackLoader.Parse(json));
        Assert.Contains(error.Errors, e => e.Contains("duplicate short code", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_DuplicateCityAndStadium_Throws()
    {
        string json = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root =>
        {
            TestWorldPacks.Club(root, 1)["city"] = TestWorldPacks.Club(root, 0)["city"]!.GetValue<string>();
            TestWorldPacks.Club(root, 2)["stadium"] =
                TestWorldPacks.Club(root, 0)["stadium"]!.GetValue<string>();
        });

        WorldPackException error = Assert.Throws<WorldPackException>(() => WorldPackLoader.Parse(json));
        Assert.Contains(error.Errors, e => e.Contains("duplicate city", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("duplicate stadium", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("1234567")]
    [InlineData("#1234567")]
    public void Parse_InvalidPrimaryColor_Throws(string color)
    {
        string json = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root =>
            TestWorldPacks.Club(root, 5)["primaryColor"] = color);

        WorldPackException error = Assert.Throws<WorldPackException>(() => WorldPackLoader.Parse(json));
        Assert.Contains(error.Errors, e => e.Contains("primaryColor", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_IdenticalKitColors_Throws()
    {
        string json = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root =>
        {
            string primary = TestWorldPacks.Club(root, 3)["primaryColor"]!.GetValue<string>();
            TestWorldPacks.Club(root, 3)["secondaryColor"] = primary;
        });

        WorldPackException error = Assert.Throws<WorldPackException>(() => WorldPackLoader.Parse(json));
        Assert.Contains(error.Errors, e => e.Contains("identical", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_WrongClubCount_Throws()
    {
        string json = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root =>
        {
            JsonArray clubs = root["clubs"]!.AsArray();
            clubs.RemoveAt(clubs.Count - 1);
        });

        WorldPackException error = Assert.Throws<WorldPackException>(() => WorldPackLoader.Parse(json));
        Assert.Contains(error.Errors, e => e.Contains("expected 108 clubs", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("has 17 clubs, expected 18", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_WrongDivisionCount_Throws()
    {
        string json = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root =>
        {
            JsonArray divisions = root["divisions"]!.AsArray();
            divisions.RemoveAt(divisions.Count - 1);
        });

        WorldPackException error = Assert.Throws<WorldPackException>(() => WorldPackLoader.Parse(json));
        Assert.Contains(error.Errors, e => e.Contains("expected 6 divisions", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_DuplicateDivisionTier_Throws()
    {
        string json = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root =>
            TestWorldPacks.Division(root, 1)["tier"] = 1);

        WorldPackException error = Assert.Throws<WorldPackException>(() => WorldPackLoader.Parse(json));
        Assert.Contains(error.Errors, e => e.Contains("duplicate tier", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("no division is defined for tier", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_UnknownDivisionReference_Throws()
    {
        string json = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root =>
            TestWorldPacks.Club(root, 0)["divisionId"] = "val-d9");

        WorldPackException error = Assert.Throws<WorldPackException>(() => WorldPackLoader.Parse(json));
        Assert.Contains(error.Errors, e => e.Contains("does not match any division", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_InvalidCapacityOrReputation_Throws()
    {
        string json = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root =>
        {
            TestWorldPacks.Club(root, 0)["capacity"] = 12;
            TestWorldPacks.Club(root, 1)["reputation"] = 0;
        });

        WorldPackException error = Assert.Throws<WorldPackException>(() => WorldPackLoader.Parse(json));
        Assert.Contains(error.Errors, e => e.Contains("capacity 12", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("reputation 0", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_InvalidShortCodeFormat_Throws()
    {
        string json = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root =>
            TestWorldPacks.Club(root, 0)["shortCode"] = "ab");

        WorldPackException error = Assert.Throws<WorldPackException>(() => WorldPackLoader.Parse(json));
        Assert.Contains(error.Errors, e => e.Contains("3 uppercase", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_TooSmallNamePool_Throws()
    {
        string json = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root =>
        {
            JsonArray names = root["names"]!["firstNames"]!.AsArray();
            while (names.Count > 10) names.RemoveAt(names.Count - 1);
        });

        WorldPackException error = Assert.Throws<WorldPackException>(() => WorldPackLoader.Parse(json));
        Assert.Contains(error.Errors, e =>
            e.Contains("names.firstNames has 10 entries", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_DuplicateAndInvalidNames_Throws()
    {
        string json = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root =>
        {
            JsonArray names = root["names"]!["firstNames"]!.AsArray();
            names[1] = names[0]!.GetValue<string>();
            names[2] = "R2-D2";
        });

        WorldPackException error = Assert.Throws<WorldPackException>(() => WorldPackLoader.Parse(json));
        Assert.Contains(error.Errors, e => e.Contains("duplicate name", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("letter name", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_MissingSections_ThrowsOnEach()
    {
        string noCountry = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root => root.Remove("country"));
        string noNames = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root => root.Remove("names"));
        string noDivisions = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root => root.Remove("divisions"));

        Assert.Contains(
            Assert.Throws<WorldPackException>(() => WorldPackLoader.Parse(noCountry)).Errors,
            e => e.Contains("country is required", StringComparison.Ordinal));
        Assert.Contains(
            Assert.Throws<WorldPackException>(() => WorldPackLoader.Parse(noNames)).Errors,
            e => e.Contains("names is required", StringComparison.Ordinal));
        Assert.Contains(
            Assert.Throws<WorldPackException>(() => WorldPackLoader.Parse(noDivisions)).Errors,
            e => e.Contains("divisions is required", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_CollectsEveryError()
    {
        string json = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root =>
        {
            TestWorldPacks.Club(root, 0)["primaryColor"] = "nope";
            TestWorldPacks.Club(root, 1)["capacity"] = -5;
            TestWorldPacks.Club(root, 2)["reputation"] = 500;
            TestWorldPacks.Division(root, 0)["clubStatus"] = "Professional";
        });

        WorldPackException error = Assert.Throws<WorldPackException>(() => WorldPackLoader.Parse(json));
        Assert.True(error.Errors.Count >= 4, $"expected >= 4 errors, got {error.Errors.Count}");
    }

    [Fact]
    public void Parse_UnknownFields_AreIgnoredForForwardCompatibility()
    {
        string json = TestWorldPacks.Mutate(TestWorldPacks.ValidJson(), root =>
            root["futureField"] = new JsonObject { ["anything"] = 1 });

        WorldPack pack = WorldPackLoader.Parse(json);
        Assert.Equal(108, pack.ClubCount);
    }

    [Theory]
    [InlineData("#000000", 0, 0, 0)]
    [InlineData("#FFFFFF", 255, 255, 255)]
    [InlineData("#1b3a6b", 27, 58, 107)]
    public void Colors_ParseStrictHex(string hex, byte r, byte g, byte b)
    {
        Assert.True(WorldColors.TryParseHex(hex, out RgbColor color));
        Assert.Equal(new RgbColor(r, g, b), color);
        Assert.Equal(hex.ToUpperInvariant(), color.ToHex());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#12345678")]
    [InlineData("1B3A6B")]
    public void Colors_RejectMalformedStrings(string? hex)
    {
        Assert.False(WorldColors.TryParseHex(hex, out _));
    }
}
