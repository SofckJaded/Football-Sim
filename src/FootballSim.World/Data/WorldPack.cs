namespace FootballSim.World.Data;

/// <summary>An 8-bit-per-channel sRGB colour, as used by the data pack for kit colours.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    /// <summary>Uppercase <c>#RRGGBB</c> form, which is what the pack stores.</summary>
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>Hex colour helpers shared by the pack loader and the presentation layer.</summary>
public static class WorldColors
{
    /// <summary>
    /// Parses exactly <c>#RRGGBB</c> (case-insensitive). Deliberately strict: a colour string that
    /// does not parse is a broken data pack, not something the renderer should silently grey out.
    /// </summary>
    public static bool TryParseHex(string? value, out RgbColor color)
    {
        color = default;
        if (value is null || value.Length != 7 || value[0] != '#') return false;

        int r = ParseHexPair(value, 1);
        int g = ParseHexPair(value, 3);
        int b = ParseHexPair(value, 5);
        if (r < 0 || g < 0 || b < 0) return false;

        color = new RgbColor((byte)r, (byte)g, (byte)b);
        return true;
    }

    /// <summary>Parses a validated pack colour; throws if the string is malformed.</summary>
    public static RgbColor ParseHex(string value) =>
        TryParseHex(value, out RgbColor color)
            ? color
            : throw new FormatException($"'{value}' is not a #RRGGBB colour.");

    private static int ParseHexPair(string value, int offset)
    {
        int high = HexDigit(value[offset]);
        int low = HexDigit(value[offset + 1]);
        return high < 0 || low < 0 ? -1 : (high << 4) | low;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };
}

/// <summary>The country a pack describes. One pack is one country in v1; the model is country-keyed
/// so multi-country worlds are additive later.</summary>
public sealed record CountryDef(string Id, string Name, string Code, string? Currency, string? Motto);

/// <summary>One division (tier) of the pyramid.</summary>
/// <param name="Id">Stable data-pack id, e.g. <c>val-d1</c>.</param>
/// <param name="Tier">1 (top) .. 6 (amateur).</param>
/// <param name="Name">Display name, e.g. <c>Valdorian Premier Division</c>.</param>
/// <param name="ShortName">Compact display name for tables and HUD, e.g. <c>Premier</c>.</param>
/// <param name="ClubStatus">Economics preset for clubs in this tier.</param>
/// <param name="WeeklyTrainingSessions">Typical organised sessions per week (1..7).</param>
public sealed record DivisionDef(
    string Id,
    int Tier,
    string Name,
    string? ShortName,
    string? ClubStatus,
    int? WeeklyTrainingSessions);

/// <summary>One club definition from the data pack. Tier is resolved from its division.</summary>
/// <param name="Id">Stable data-pack id, e.g. <c>val-d1-001</c>.</param>
/// <param name="DivisionId">The division this club starts in.</param>
/// <param name="Tier">Resolved tier (1..6) of the club's starting division.</param>
/// <param name="Name">Unique club name.</param>
/// <param name="ShortCode">Unique three-character code for compact tables.</param>
/// <param name="City">The club's home city.</param>
/// <param name="Stadium">Unique stadium name.</param>
/// <param name="Capacity">Stadium capacity, seats.</param>
/// <param name="PrimaryColor">Primary kit colour.</param>
/// <param name="SecondaryColor">Secondary/trim kit colour; never equal to the primary.</param>
/// <param name="Reputation">Base reputation 1..100, the world's initial strength hint.</param>
/// <param name="Founded">Year founded, when the pack states it.</param>
public sealed record ClubDef(
    string Id,
    string DivisionId,
    int Tier,
    string Name,
    string ShortCode,
    string City,
    string Stadium,
    int Capacity,
    RgbColor PrimaryColor,
    RgbColor SecondaryColor,
    int Reputation,
    int? Founded)
{
    /// <summary>Primary kit colour as <c>#RRGGBB</c>.</summary>
    public string PrimaryColorHex => PrimaryColor.ToHex();

    /// <summary>Secondary kit colour as <c>#RRGGBB</c>.</summary>
    public string SecondaryColorHex => SecondaryColor.ToHex();
}

/// <summary>Procedural name pools for players and managers, from the data pack.</summary>
public sealed class NamePools
{
    public NamePools(IReadOnlyList<string> firstNames, IReadOnlyList<string> lastNames)
    {
        FirstNames = firstNames;
        LastNames = lastNames;
    }

    public IReadOnlyList<string> FirstNames { get; }
    public IReadOnlyList<string> LastNames { get; }

    /// <summary>Deterministic full-name helper used by generators (no RNG of its own).</summary>
    public string ComposeName(string firstName, string lastName) => $"{firstName} {lastName}";
}

/// <summary>
/// A validated, immutable world data pack: one country, six divisions, 108 clubs and the name pools.
///
/// Loading is separated from validation on purpose: the game, the tests and future mods all consume
/// this type, and no consumer should ever have to re-check a null, a duplicate or a malformed colour.
/// </summary>
public sealed class WorldPack
{
    /// <summary>The only schema version this build understands.</summary>
    public const int SupportedSchemaVersion = 1;

    private readonly Dictionary<string, DivisionDef> _divisionsById;
    private readonly Dictionary<int, DivisionDef> _divisionsByTier;
    private readonly Dictionary<string, ClubDef> _clubsById;
    private readonly Dictionary<int, IReadOnlyList<ClubDef>> _clubsByTier;

    public WorldPack(
        int schemaVersion,
        string packId,
        CountryDef country,
        IReadOnlyList<DivisionDef> divisions,
        IReadOnlyList<ClubDef> clubs,
        NamePools names,
        string sourceName)
    {
        SchemaVersion = schemaVersion;
        PackId = packId;
        Country = country;
        Divisions = divisions;
        Clubs = clubs;
        Names = names;
        SourceName = sourceName;

        _divisionsById = new Dictionary<string, DivisionDef>(StringComparer.OrdinalIgnoreCase);
        _divisionsByTier = new Dictionary<int, DivisionDef>();
        foreach (DivisionDef division in divisions)
        {
            _divisionsById[division.Id] = division;
            _divisionsByTier[division.Tier] = division;
        }

        _clubsById = new Dictionary<string, ClubDef>(StringComparer.OrdinalIgnoreCase);
        foreach (ClubDef club in clubs) _clubsById[club.Id] = club;

        var byTier = new Dictionary<int, List<ClubDef>>();
        foreach (ClubDef club in clubs)
        {
            if (!byTier.TryGetValue(club.Tier, out List<ClubDef>? list))
            {
                list = new List<ClubDef>();
                byTier[club.Tier] = list;
            }
            list.Add(club);
        }

        _clubsByTier = new Dictionary<int, IReadOnlyList<ClubDef>>(byTier.Count);
        foreach (KeyValuePair<int, List<ClubDef>> pair in byTier) _clubsByTier[pair.Key] = pair.Value;
    }

    /// <summary>Schema version the pack was written against.</summary>
    public int SchemaVersion { get; }

    /// <summary>Stable pack identifier, e.g. <c>default-country</c>.</summary>
    public string PackId { get; }

    public CountryDef Country { get; }

    /// <summary>Divisions ordered by tier (top flight first).</summary>
    public IReadOnlyList<DivisionDef> Divisions { get; }

    /// <summary>Clubs ordered by tier and then by data-pack id, so every consumer sees one order.</summary>
    public IReadOnlyList<ClubDef> Clubs { get; }

    public NamePools Names { get; }

    /// <summary>File the pack was loaded from, or a caller-supplied label for in-memory packs.</summary>
    public string SourceName { get; }

    /// <summary>Number of clubs in the pack (108 in the default pack).</summary>
    public int ClubCount => Clubs.Count;

    /// <summary>Number of tiers in the pyramid (6 in the default pack).</summary>
    public int TierCount => Divisions.Count;

    public DivisionDef DivisionForTier(int tier) =>
        _divisionsByTier.TryGetValue(tier, out DivisionDef? division)
            ? division
            : throw new KeyNotFoundException($"The pack '{PackId}' has no division for tier {tier}.");

    public bool TryGetDivision(int tier, out DivisionDef? division) =>
        _divisionsByTier.TryGetValue(tier, out division);

    public bool TryGetDivisionById(string id, out DivisionDef? division) =>
        _divisionsById.TryGetValue(id, out division);

    /// <summary>Clubs in <paramref name="tier"/>, in data-pack id order.</summary>
    public IReadOnlyList<ClubDef> ClubsInTier(int tier) =>
        _clubsByTier.TryGetValue(tier, out IReadOnlyList<ClubDef>? clubs)
            ? clubs
            : Array.Empty<ClubDef>();

    public bool TryGetClub(string id, out ClubDef? club) => _clubsById.TryGetValue(id, out club);
}
