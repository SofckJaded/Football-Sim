using System.Text.Json.Serialization;

namespace FootballSim.World.Data;

/// <summary>
/// Raw JSON shapes for a world data pack (<c>data/world/*.json</c>).
///
/// These types exist only to bind the file. Everything that leaves the loader is the validated,
/// immutable <see cref="WorldPack"/> model: a DTO that reaches gameplay could carry a null, a
/// duplicate id or a colour string the renderer cannot parse, and the plan requires those failures
/// to happen once at load with a message naming the offending entry.
/// </summary>
internal sealed class WorldPackDto
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("packId")] public string? PackId { get; set; }
    [JsonPropertyName("country")] public CountryDto? Country { get; set; }
    [JsonPropertyName("divisions")] public List<DivisionDto>? Divisions { get; set; }
    [JsonPropertyName("clubs")] public List<ClubDto>? Clubs { get; set; }
    [JsonPropertyName("names")] public NamesDto? Names { get; set; }
}

internal sealed class CountryDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("currency")] public string? Currency { get; set; }
    [JsonPropertyName("motto")] public string? Motto { get; set; }
}

internal sealed class DivisionDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("tier")] public int Tier { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("shortName")] public string? ShortName { get; set; }
    [JsonPropertyName("clubStatus")] public string? ClubStatus { get; set; }
    [JsonPropertyName("weeklyTrainingSessions")] public int? WeeklyTrainingSessions { get; set; }
}

internal sealed class ClubDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("divisionId")] public string? DivisionId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("shortCode")] public string? ShortCode { get; set; }
    [JsonPropertyName("city")] public string? City { get; set; }
    [JsonPropertyName("stadium")] public string? Stadium { get; set; }
    [JsonPropertyName("capacity")] public int Capacity { get; set; }
    [JsonPropertyName("primaryColor")] public string? PrimaryColor { get; set; }
    [JsonPropertyName("secondaryColor")] public string? SecondaryColor { get; set; }
    [JsonPropertyName("reputation")] public int Reputation { get; set; }
    [JsonPropertyName("founded")] public int? Founded { get; set; }
}

internal sealed class NamesDto
{
    [JsonPropertyName("firstNames")] public List<string>? FirstNames { get; set; }
    [JsonPropertyName("lastNames")] public List<string>? LastNames { get; set; }
}
