using System.Text;
using System.Text.Json;

namespace FootballSim.World.Data;

/// <summary>
/// Loads and validates world data packs (<c>data/world/*.json</c>).
///
/// Validation is deliberately strict and eager. The prototype's scope is one country with six
/// divisions of eighteen clubs, so the loader refuses a pack whose shape cannot produce that world
/// instead of letting a generator fail later with an obscure missing-key error. Every rejection names
/// the offending entry; mods are expected to hit these messages, not the debugger.
/// </summary>
public static class WorldPackLoader
{
    /// <summary>Clubs a v1 pack must define (6 tiers x 18 clubs).</summary>
    public const int RequiredClubCount = 108;

    /// <summary>Tiers a v1 pack must define.</summary>
    public const int RequiredTierCount = 6;

    /// <summary>Clubs each tier must contain.</summary>
    public const int RequiredClubsPerTier = 18;

    /// <summary>Minimum distinct first names and last names a pack must supply.</summary>
    public const int MinimumNamePoolSize = 200;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict
    };

    private static readonly HashSet<string> KnownClubStatuses = new(StringComparer.Ordinal)
    {
        "FullTimePro", "LowerPro", "SemiPro", "UpperAmateur", "Amateur"
    };

    /// <summary>Loads and validates a pack file.</summary>
    /// <exception cref="WorldPackException">The pack is structurally or semantically invalid.</exception>
    public static WorldPack Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"World data pack not found: {fullPath}", fullPath);

        string json = File.ReadAllText(fullPath, Encoding.UTF8);
        return Parse(json, fullPath);
    }

    /// <summary>Parses and validates pack JSON held in memory.</summary>
    /// <param name="json">The pack document.</param>
    /// <param name="sourceName">Label used in errors and stored on the pack; a path or <c>&lt;memory&gt;</c>.</param>
    /// <exception cref="WorldPackException">The pack is structurally or semantically invalid.</exception>
    public static WorldPack Parse(string json, string sourceName = "<memory>")
    {
        if (json is null) throw new ArgumentNullException(nameof(json));

        WorldPackDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<WorldPackDto>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new WorldPackException(sourceName, new[] { $"invalid JSON: {ex.Message}" });
        }

        if (dto is null)
            throw new WorldPackException(sourceName, new[] { "the document is empty or null" });

        var errors = new List<string>();

        if (dto.SchemaVersion != WorldPack.SupportedSchemaVersion)
        {
            errors.Add(
                $"schemaVersion {dto.SchemaVersion} is not supported (this build supports " +
                $"{WorldPack.SupportedSchemaVersion})");
        }

        string packId = ValidatePackId(dto.PackId, errors);
        CountryDef? country = ValidateCountry(dto.Country, errors);
        Dictionary<string, DivisionDef> divisions = ValidateDivisions(dto.Divisions, errors);
        List<ClubDef> clubs = ValidateClubs(dto.Clubs, divisions, errors);
        NamePools? names = ValidateNames(dto.Names, errors);

        if (errors.Count > 0) throw new WorldPackException(sourceName, errors);

        // Sorted views so two consumers (or two runs) never disagree about iteration order.
        List<DivisionDef> orderedDivisions = new(divisions.Values);
        orderedDivisions.Sort(static (a, b) => a.Tier.CompareTo(b.Tier));

        clubs.Sort(static (a, b) =>
        {
            int byTier = a.Tier.CompareTo(b.Tier);
            return byTier != 0 ? byTier : string.CompareOrdinal(a.Id, b.Id);
        });

        return new WorldPack(
            dto.SchemaVersion, packId, country!, orderedDivisions, clubs, names!, sourceName);
    }

    private static string ValidatePackId(string? packId, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(packId))
        {
            errors.Add("packId is required");
            return string.Empty;
        }

        if (!IsSlug(packId))
            errors.Add($"packId '{packId}' is not a lowercase slug (a-z, 0-9, '-')");

        return packId.Trim();
    }

    private static CountryDef? ValidateCountry(CountryDto? dto, List<string> errors)
    {
        if (dto is null)
        {
            errors.Add("country is required");
            return null;
        }

        if (string.IsNullOrWhiteSpace(dto.Id) || !IsSlug(dto.Id))
            errors.Add($"country.id '{dto.Id}' is not a lowercase slug (a-z, 0-9, '-')");
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.Name.Length > 64)
            errors.Add("country.name is required and must be 1..64 characters");
        if (string.IsNullOrWhiteSpace(dto.Code) || !IsUpperCode(dto.Code, 2, 4))
            errors.Add($"country.code '{dto.Code}' is not 2..4 uppercase A-Z characters");
        if (dto.Currency is { Length: > 40 })
            errors.Add("country.currency must be at most 40 characters");
        if (dto.Motto is { Length: > 80 })
            errors.Add("country.motto must be at most 80 characters");

        return new CountryDef(
            dto.Id?.Trim() ?? string.Empty,
            dto.Name?.Trim() ?? string.Empty,
            dto.Code?.Trim() ?? string.Empty,
            dto.Currency?.Trim(),
            dto.Motto?.Trim());
    }

    private static Dictionary<string, DivisionDef> ValidateDivisions(
        List<DivisionDto>? dtos, List<string> errors)
    {
        var result = new Dictionary<string, DivisionDef>(StringComparer.OrdinalIgnoreCase);
        if (dtos is null)
        {
            errors.Add("divisions is required");
            return result;
        }

        if (dtos.Count != RequiredTierCount)
            errors.Add($"expected {RequiredTierCount} divisions, found {dtos.Count}");

        var tiers = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < dtos.Count; i++)
        {
            DivisionDto dto = dtos[i];
            string where = $"divisions[{i}]";

            string? id = dto.Id?.Trim();
            if (string.IsNullOrWhiteSpace(id) || !IsSlug(id))
            {
                errors.Add($"{where}.id '{dto.Id}' is not a lowercase slug (a-z, 0-9, '-')");
                continue;
            }

            if (!result.TryAdd(id, null!))
            {
                errors.Add($"{where}.id '{id}' is a duplicate division id");
                continue;
            }

            if (dto.Tier is < 1 or > RequiredTierCount)
                errors.Add($"{where}.tier {dto.Tier} is outside 1..{RequiredTierCount}");
            else if (!tiers.Add(dto.Tier))
                errors.Add($"{where}.tier {dto.Tier} is a duplicate tier");

            string? name = dto.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
                errors.Add($"{where}.name is required and must be 1..64 characters");
            else if (!names.Add(name))
                errors.Add($"{where}.name '{name}' is a duplicate division name");

            if (dto.ShortName is { Length: > 32 })
                errors.Add($"{where}.shortName must be at most 32 characters");

            if (dto.ClubStatus is not null && !KnownClubStatuses.Contains(dto.ClubStatus))
            {
                errors.Add(
                    $"{where}.clubStatus '{dto.ClubStatus}' is not one of " +
                    string.Join(", ", KnownClubStatuses));
            }

            if (dto.WeeklyTrainingSessions is { } sessions && sessions is < 1 or > 7)
                errors.Add($"{where}.weeklyTrainingSessions {sessions} is outside 1..7");

            result[id] = new DivisionDef(
                id,
                dto.Tier,
                name ?? string.Empty,
                dto.ShortName?.Trim(),
                dto.ClubStatus,
                dto.WeeklyTrainingSessions);
        }

        for (int tier = 1; tier <= RequiredTierCount; tier++)
        {
            if (!tiers.Contains(tier)) errors.Add($"no division is defined for tier {tier}");
        }

        return result;
    }

    private static List<ClubDef> ValidateClubs(
        List<ClubDto>? dtos, Dictionary<string, DivisionDef> divisions, List<string> errors)
    {
        var result = new List<ClubDef>();
        if (dtos is null)
        {
            errors.Add("clubs is required");
            return result;
        }

        if (dtos.Count != RequiredClubCount)
            errors.Add($"expected {RequiredClubCount} clubs, found {dtos.Count}");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var cities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stadiums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var perDivision = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < dtos.Count; i++)
        {
            ClubDto dto = dtos[i];
            string where = $"clubs[{i}]";

            string? id = dto.Id?.Trim();
            if (string.IsNullOrWhiteSpace(id) || !IsSlug(id))
            {
                errors.Add($"{where}.id '{dto.Id}' is not a lowercase slug (a-z, 0-9, '-')");
                continue;
            }

            if (!ids.Add(id))
            {
                errors.Add($"{where}.id '{id}' is a duplicate club id");
                continue;
            }

            DivisionDef? division = null;
            string? divisionId = dto.DivisionId?.Trim();
            if (string.IsNullOrWhiteSpace(divisionId) || !divisions.TryGetValue(divisionId, out division))
            {
                errors.Add($"{where}.divisionId '{dto.DivisionId}' does not match any division");
            }
            else
            {
                perDivision.TryGetValue(divisionId, out int count);
                perDivision[divisionId] = count + 1;
            }

            string? name = dto.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
                errors.Add($"{where}.name is required and must be 1..64 characters");
            else if (!names.Add(name))
                errors.Add($"{where}.name '{name}' is a duplicate club name");

            string? code = dto.ShortCode?.Trim();
            if (string.IsNullOrWhiteSpace(code) || !IsUpperCode(code, 3, 3))
                errors.Add($"{where}.shortCode '{dto.ShortCode}' is not exactly 3 uppercase A-Z0-9 characters");
            else if (!codes.Add(code))
                errors.Add($"{where}.shortCode '{code}' is a duplicate short code");

            string? city = dto.City?.Trim();
            if (string.IsNullOrWhiteSpace(city) || city.Length > 48)
                errors.Add($"{where}.city is required and must be 1..48 characters");
            else if (!cities.Add(city))
                errors.Add($"{where}.city '{city}' is a duplicate city");

            string? stadium = dto.Stadium?.Trim();
            if (string.IsNullOrWhiteSpace(stadium) || stadium.Length > 64)
                errors.Add($"{where}.stadium is required and must be 1..64 characters");
            else if (!stadiums.Add(stadium))
                errors.Add($"{where}.stadium '{stadium}' is a duplicate stadium name");

            if (dto.Capacity is < 100 or > 150_000)
                errors.Add($"{where}.capacity {dto.Capacity} is outside 100..150000");

            if (dto.Reputation is < 1 or > 100)
                errors.Add($"{where}.reputation {dto.Reputation} is outside 1..100");

            bool primaryOk = WorldColors.TryParseHex(dto.PrimaryColor, out RgbColor primary);
            if (!primaryOk)
                errors.Add($"{where}.primaryColor '{dto.PrimaryColor}' is not a #RRGGBB colour");

            bool secondaryOk = WorldColors.TryParseHex(dto.SecondaryColor, out RgbColor secondary);
            if (!secondaryOk)
                errors.Add($"{where}.secondaryColor '{dto.SecondaryColor}' is not a #RRGGBB colour");

            if (primaryOk && secondaryOk && primary == secondary)
                errors.Add($"{where}: primaryColor and secondaryColor are identical");

            if (dto.Founded is { } founded && founded is < 1850 or > 2000)
                errors.Add($"{where}.founded {founded} is outside 1850..2000");

            // A club's tier is always its division's tier; the pack never states it twice.
            int tier = division?.Tier ?? 0;
            result.Add(new ClubDef(
                id,
                divisionId ?? string.Empty,
                tier,
                name ?? string.Empty,
                code ?? string.Empty,
                city ?? string.Empty,
                stadium ?? string.Empty,
                dto.Capacity,
                primary,
                secondary,
                dto.Reputation,
                dto.Founded));
        }

        foreach (DivisionDef division in divisions.Values)
        {
            perDivision.TryGetValue(division.Id, out int count);
            if (count != RequiredClubsPerTier)
                errors.Add($"division '{division.Id}' (tier {division.Tier}) has {count} clubs, expected {RequiredClubsPerTier}");
        }

        return result;
    }

    private static NamePools? ValidateNames(NamesDto? dto, List<string> errors)
    {
        if (dto is null)
        {
            errors.Add("names is required");
            return null;
        }

        List<string> first = ValidateNamePool(dto.FirstNames, "names.firstNames", errors);
        List<string> last = ValidateNamePool(dto.LastNames, "names.lastNames", errors);
        return new NamePools(first, last);
    }

    private static List<string> ValidateNamePool(
        List<string>? pool, string where, List<string> errors)
    {
        var result = new List<string>();
        if (pool is null)
        {
            errors.Add($"{where} is required");
            return result;
        }

        if (pool.Count < MinimumNamePoolSize)
            errors.Add($"{where} has {pool.Count} entries, expected at least {MinimumNamePoolSize}");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < pool.Count; i++)
        {
            string? entry = pool[i];
            if (string.IsNullOrEmpty(entry))
            {
                errors.Add($"{where}[{i}] is empty");
                continue;
            }

            string trimmed = entry.Trim();
            if (trimmed.Length != entry.Length)
                errors.Add($"{where}[{i}] '{entry}' has leading or trailing whitespace");
            if (!IsPersonName(trimmed))
                errors.Add($"{where}[{i}] '{entry}' is not a 2..24 character letter name");
            else if (!seen.Add(trimmed))
                errors.Add($"{where}[{i}] '{trimmed}' is a duplicate name");

            result.Add(trimmed);
        }

        return result;
    }

    private static bool IsSlug(string value)
    {
        if (value.Length is < 2 or > 48) return false;
        if (value[0] == '-' || value[^1] == '-') return false;

        foreach (char c in value)
        {
            bool ok = c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-';
            if (!ok) return false;
        }
        return true;
    }

    private static bool IsUpperCode(string value, int minLength, int maxLength)
    {
        if (value.Length < minLength || value.Length > maxLength) return false;
        foreach (char c in value)
        {
            bool ok = c is >= 'A' and <= 'Z' or >= '0' and <= '9';
            if (!ok) return false;
        }
        return true;
    }

    private static bool IsPersonName(string value)
    {
        if (value.Length is < 2 or > 24) return false;
        if (value[0] is ' ' or '-' or '\'' || value[^1] is ' ' or '-' or '\'') return false;

        int letters = 0;
        foreach (char c in value)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                letters++;
                continue;
            }
            if (c is ' ' or '-' or '\'') continue;
            return false;
        }
        return letters >= 2;
    }
}
