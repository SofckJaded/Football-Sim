using FootballSim.World.Core;

namespace FootballSim.World.World;

/// <summary>
/// One division of the pyramid for one season: its clubs and this season's league fixtures. The table is
/// not stored — <see cref="StandingsCalculator"/> derives it from played fixtures, so there is only one
/// place a result can live.
/// </summary>
public sealed class WorldLeague
{
    private readonly List<ClubId> _clubs = new(18);
    private readonly List<WorldFixture> _fixtures = new(306);

    /// <summary>Creates a league.</summary>
    public WorldLeague(LeagueId id, int tier, string name, string shortName)
    {
        if (tier is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(tier));
        Id = id;
        Tier = tier;
        Name = name;
        ShortName = shortName;
    }

    /// <summary>Stable identity.</summary>
    public LeagueId Id { get; }

    /// <summary>Division tier 1..6.</summary>
    public int Tier { get; }

    /// <summary>Display name, e.g. "Valdorian Premier Division".</summary>
    public string Name { get; }

    /// <summary>Compact display name.</summary>
    public string ShortName { get; }

    /// <summary>Clubs in this division, in stable registration order.</summary>
    public IReadOnlyList<ClubId> Clubs => _clubs;

    /// <summary>This season's fixtures (double round-robin, 306 matches).</summary>
    public IReadOnlyList<WorldFixture> Fixtures => _fixtures;

    /// <summary>Registers a club (idempotent; at most 18 clubs per v1 division).</summary>
    public void AddClub(ClubId clubId)
    {
        if (Clubs.Count >= 18)
            throw new InvalidOperationException($"Division '{Name}' already has 18 clubs.");
        if (!_clubs.Contains(clubId)) _clubs.Add(clubId);
    }

    /// <summary>Adds a fixture to this season's list.</summary>
    public void AddFixture(WorldFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (fixture.LeagueId != Id)
            throw new ArgumentException("Fixture belongs to another league.", nameof(fixture));
        _fixtures.Add(fixture);
    }

    /// <summary>Fixtures scheduled for one calendar week.</summary>
    public IEnumerable<WorldFixture> FixturesForWeek(int week)
    {
        foreach (WorldFixture fixture in _fixtures)
        {
            if (fixture.Week == week) yield return fixture;
        }
    }

    /// <summary>Fixtures for one league matchday (1..34).</summary>
    public IEnumerable<WorldFixture> FixturesForMatchday(int matchday)
    {
        foreach (WorldFixture fixture in _fixtures)
        {
            if (fixture.LeagueMatchday == matchday && fixture.Competition == CompetitionKind.League)
                yield return fixture;
        }
    }

    /// <summary>Clears the season's fixtures (off-season rollover before the next schedule is built).</summary>
    public void ClearFixtures() => _fixtures.Clear();

    /// <summary>Clears the club list (promotion/relegation then re-registers the new composition).</summary>
    public void ClearClubs() => _clubs.Clear();

    /// <inheritdoc />
    public override string ToString() => $"{Name} (tier {Tier}, {Clubs.Count} clubs)";
}
