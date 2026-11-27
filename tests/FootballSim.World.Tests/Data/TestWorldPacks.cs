using System.Text.Json;
using System.Text.Json.Nodes;

namespace FootballSim.World.Tests.Data;

/// <summary>
/// Builds a structurally valid data-pack document in memory so validation tests can mutate exactly one
/// field and assert that the loader names it. Keeping the generator here also means the mutation tests
/// never depend on the shipped pack's contents.
/// </summary>
internal static class TestWorldPacks
{
    internal const int Tiers = 6;
    internal const int ClubsPerTier = 18;
    internal const int NamePoolSize = 240;

    internal static readonly string[] ClubStatuses =
    {
        "FullTimePro", "FullTimePro", "LowerPro", "SemiPro", "UpperAmateur", "Amateur"
    };

    internal static string ValidJson()
    {
        var divisions = new List<object>();
        for (int tier = 1; tier <= Tiers; tier++)
        {
            divisions.Add(new
            {
                id = $"val-d{tier}",
                tier,
                name = $"Valdorian Division {tier}",
                shortName = $"Div{tier}",
                clubStatus = ClubStatuses[tier - 1],
                weeklyTrainingSessions = Math.Clamp(7 - tier, 1, 6)
            });
        }

        var clubs = new List<object>();
        for (int tier = 1; tier <= Tiers; tier++)
        {
            for (int index = 1; index <= ClubsPerTier; index++)
            {
                char codeLetter = (char)('A' + tier - 1);
                clubs.Add(new
                {
                    id = $"val-d{tier}-{index:000}",
                    divisionId = $"val-d{tier}",
                    name = $"Test Club {tier}-{index:00}",
                    shortCode = $"{codeLetter}{index:00}",
                    city = $"Test City {tier}-{index:00}",
                    stadium = $"Test Ground {tier}-{index:00}",
                    capacity = 400 + (tier * -1 + 7) * 5000 + index * 100,
                    primaryColor = $"#{(tier * ClubsPerTier + index) % 256:X2}1020",
                    secondaryColor = "#F0E0D0",
                    reputation = 20 + (Tiers - tier) * 12 + index,
                    founded = 1870 + tier * 10 + index
                });
            }
        }

        var firstNames = new List<string>();
        var lastNames = new List<string>();
        for (int i = 1; i <= NamePoolSize; i++)
        {
            firstNames.Add("Given" + Alpha(i));
            lastNames.Add("Family" + Alpha(i));
        }

        var pack = new
        {
            schemaVersion = 1,
            packId = "test-country",
            country = new
            {
                id = "valdoria",
                name = "Republic of Valdoria",
                code = "VAL",
                currency = "valdorian crown",
                motto = "One valley, one ball"
            },
            divisions,
            clubs,
            names = new { firstNames, lastNames }
        };

        return JsonSerializer.Serialize(pack);
    }

    internal static string Mutate(string json, Action<JsonObject> mutate)
    {
        JsonNode node = JsonNode.Parse(json) ?? throw new InvalidOperationException("null JSON node");
        JsonObject root = node.AsObject();
        mutate(root);
        return root.ToJsonString();
    }

    internal static JsonObject Club(JsonObject root, int index) =>
        root["clubs"]!.AsArray()[index]!.AsObject();

    internal static JsonObject Division(JsonObject root, int index) =>
        root["divisions"]!.AsArray()[index]!.AsObject();

    /// <summary>Letters-only unique suffix: 1 -> "A", 26 -> "Z", 27 -> "AA".</summary>
    private static string Alpha(int value)
    {
        Span<char> buffer = stackalloc char[8];
        int position = buffer.Length;
        int remaining = value;
        while (remaining > 0)
        {
            remaining--;
            buffer[--position] = (char)('A' + remaining % 26);
            remaining /= 26;
        }
        return new string(buffer[position..]);
    }
}
