using FootballSim.World.Core;

namespace FootballSim.World.World;

/// <summary>One club's row in a league table.</summary>
public sealed class LeagueTableRow
{
    /// <summary>Creates a zeroed row for a club.</summary>
    public LeagueTableRow(ClubId clubId) => ClubId = clubId;

    /// <summary>The club.</summary>
    public ClubId ClubId { get; }

    /// <summary>Matches played.</summary>
    public int Played { get; private set; }

    /// <summary>Wins.</summary>
    public int Won { get; private set; }

    /// <summary>Draws.</summary>
    public int Drawn { get; private set; }

    /// <summary>Losses.</summary>
    public int Lost { get; private set; }

    /// <summary>Goals scored.</summary>
    public int GoalsFor { get; private set; }

    /// <summary>Goals conceded.</summary>
    public int GoalsAgainst { get; private set; }

    /// <summary>Points: three for a win, one for a draw.</summary>
    public int Points => Won * 3 + Drawn;

    /// <summary>Goal difference.</summary>
    public int GoalDifference => GoalsFor - GoalsAgainst;

    /// <summary>Applies one result to the row.</summary>
    public void ApplyResult(int goalsFor, int goalsAgainst)
    {
        Played++;
        GoalsFor += goalsFor;
        GoalsAgainst += goalsAgainst;
        if (goalsFor > goalsAgainst) Won++;
        else if (goalsFor == goalsAgainst) Drawn++;
        else Lost++;
    }
}

/// <summary>
/// A promotion/relegation play-off: the 16th-placed club of the higher division hosts the 3rd-placed
/// club of the division below; the winner plays in the higher division next season.
/// </summary>
/// <param name="BoundaryTier">The higher division's tier (1..5); the lower club is from tier + 1.</param>
/// <param name="HigherDivisionClubId">16th of the higher division at season end.</param>
/// <param name="LowerDivisionClubId">3rd of the lower division at season end.</param>
public sealed record PlayoffTie(int BoundaryTier, ClubId HigherDivisionClubId, ClubId LowerDivisionClubId);

/// <summary>One club changing division.</summary>
/// <param name="ClubId">The club.</param>
/// <param name="FromTier">Division it left.</param>
/// <param name="ToTier">Division it joined.</param>
/// <param name="ViaPlayoff">True when the move was decided by a play-off rather than automatic.</param>
public sealed record DivisionMove(ClubId ClubId, int FromTier, int ToTier, bool ViaPlayoff);

/// <summary>
/// The resolved outcome of one league season: final tables per tier, champions, automatic
/// promotions/relegations and the five promotion/relegation play-off pairings.
/// </summary>
public sealed class SeasonOutcome
{
    private readonly Dictionary<ClubId, LeagueTableRow> _rows = new();

    /// <summary>Creates an outcome from finished tables (tier 1 first).</summary>
    public SeasonOutcome(IReadOnlyList<IReadOnlyList<LeagueTableRow>> tablesByTier)
    {
        ArgumentNullException.ThrowIfNull(tablesByTier);
        if (tablesByTier.Count != 6) throw new ArgumentException("A v1 season has six division tables.", nameof(tablesByTier));
        TablesByTier = tablesByTier;

        var champions = new List<ClubId>(6);
        var promoted = new List<ClubId>();
        var relegated = new List<ClubId>();
        var playoffs = new List<PlayoffTie>(5);

        for (int index = 0; index < tablesByTier.Count; index++)
        {
            int tier = index + 1;
            IReadOnlyList<LeagueTableRow> table = tablesByTier[index];
            if (table.Count != 18) throw new ArgumentException($"Tier {tier} table must have 18 rows.", nameof(tablesByTier));

            champions.Add(table[0].ClubId);
            foreach (LeagueTableRow row in table) _rows[row.ClubId] = row;

            // Bottom two of tiers 1..5 go down automatically; 17th/18th of tier 6 stay.
            if (tier < 6)
            {
                relegated.Add(table[16].ClubId);
                relegated.Add(table[17].ClubId);
            }

            // Top two of tiers 2..6 go up automatically.
            if (tier > 1)
            {
                promoted.Add(table[0].ClubId);
                promoted.Add(table[1].ClubId);
            }
        }

        // Five boundaries: 16th of tier N against 3rd of tier N+1.
        for (int tier = 1; tier <= 5; tier++)
        {
            playoffs.Add(new PlayoffTie(tier, tablesByTier[tier - 1][15].ClubId, tablesByTier[tier][2].ClubId));
        }

        ChampionsByTier = champions;
        AutoPromoted = promoted;
        AutoRelegated = relegated;
        PlayoffTies = playoffs;
    }

    /// <summary>Final tables, tier 1 first; each row list is sorted by final position.</summary>
    public IReadOnlyList<IReadOnlyList<LeagueTableRow>> TablesByTier { get; }

    /// <summary>Champion of each tier (index 0 = tier 1).</summary>
    public IReadOnlyList<ClubId> ChampionsByTier { get; }

    /// <summary>Clubs promoted automatically (top two of tiers 2..6).</summary>
    public IReadOnlyList<ClubId> AutoPromoted { get; }

    /// <summary>Clubs relegated automatically (bottom two of tiers 1..5).</summary>
    public IReadOnlyList<ClubId> AutoRelegated { get; }

    /// <summary>The five promotion/relegation play-offs.</summary>
    public IReadOnlyList<PlayoffTie> PlayoffTies { get; }

    /// <summary>Final row for a club.</summary>
    public LeagueTableRow RowFor(ClubId clubId) =>
        _rows.TryGetValue(clubId, out LeagueTableRow? row)
            ? row
            : throw new KeyNotFoundException($"Club {clubId} has no table row in this outcome.");

    /// <summary>Final position (1-based) of a club in its division.</summary>
    public int PositionOf(ClubId clubId)
    {
        LeagueTableRow row = RowFor(clubId);
        foreach (IReadOnlyList<LeagueTableRow> table in TablesByTier)
        {
            for (int i = 0; i < table.Count; i++)
            {
                if (table[i].ClubId == clubId) return i + 1;
            }
        }
        throw new KeyNotFoundException($"Club {clubId} has no table row in this outcome.");
    }

    /// <summary>Tier the club played in (1..6), inferred from which table contains it.</summary>
    public int TierOf(ClubId clubId)
    {
        for (int index = 0; index < TablesByTier.Count; index++)
        {
            foreach (LeagueTableRow row in TablesByTier[index])
            {
                if (row.ClubId == clubId) return index + 1;
            }
        }
        throw new KeyNotFoundException($"Club {clubId} has no table row in this outcome.");
    }
}

/// <summary>
/// Derives league tables from played fixtures and resolves the season's promotion/relegation.
///
/// There is no stored table: a matchday only records its result on the fixture, and the table is a pure
/// function of the played fixtures. That is what keeps league position, top scorers and the season
/// rollover from ever disagreeing after a save/load or a replayed week.
/// </summary>
public static class StandingsCalculator
{
    /// <summary>Tiebreak order: points, goal difference, goals scored, then club id (stable and total).</summary>
    public static IReadOnlyList<LeagueTableRow> Calculate(WorldLeague league)
    {
        ArgumentNullException.ThrowIfNull(league);

        var rows = new Dictionary<ClubId, LeagueTableRow>(league.Clubs.Count);
        var ordered = new List<LeagueTableRow>(league.Clubs.Count);
        foreach (ClubId clubId in league.Clubs)
        {
            var row = new LeagueTableRow(clubId);
            rows[clubId] = row;
            ordered.Add(row);
        }

        foreach (WorldFixture fixture in league.Fixtures)
        {
            if (!fixture.IsPlayed || fixture.Competition != CompetitionKind.League) continue;

            rows[fixture.HomeClubId].ApplyResult(fixture.HomeGoals, fixture.AwayGoals);
            rows[fixture.AwayClubId].ApplyResult(fixture.AwayGoals, fixture.HomeGoals);
        }

        ordered.Sort(static (a, b) =>
        {
            if (a.Points != b.Points) return b.Points.CompareTo(a.Points);
            if (a.GoalDifference != b.GoalDifference) return b.GoalDifference.CompareTo(a.GoalDifference);
            if (a.GoalsFor != b.GoalsFor) return b.GoalsFor.CompareTo(a.GoalsFor);
            return a.ClubId.Value.CompareTo(b.ClubId.Value);
        });

        return ordered;
    }

    /// <summary>Resolves the whole season from the six leagues' played fixtures.</summary>
    public static SeasonOutcome ResolveSeason(IReadOnlyList<WorldLeague> leagues)
    {
        ArgumentNullException.ThrowIfNull(leagues);
        if (leagues.Count != 6) throw new ArgumentException("A v1 season has six leagues.", nameof(leagues));

        var tables = new List<IReadOnlyList<LeagueTableRow>>(6);
        for (int tier = 1; tier <= 6; tier++)
        {
            WorldLeague? league = null;
            foreach (WorldLeague candidate in leagues)
            {
                if (candidate.Tier == tier) { league = candidate; break; }
            }
            if (league is null) throw new ArgumentException($"No league registered for tier {tier}.", nameof(leagues));
            tables.Add(Calculate(league));
        }

        return new SeasonOutcome(tables);
    }

    /// <summary>
    /// Applies a resolved season: every club moves to its next-season tier and its tier league registers
    /// the new composition. Play-off winners are supplied by the caller (the season runner plays them).
    /// Returns the division moves, automatic ones first.
    /// </summary>
    /// <param name="world">The world to mutate.</param>
    /// <param name="outcome">The resolved season.</param>
    /// <param name="playoffWinnerIsHigherDivisionClub">
    /// For each boundary tier 1..5: true when the higher-division club won its play-off (both stay), false
    /// when the lower-division club won (the two clubs swap divisions).
    /// </param>
    public static IReadOnlyList<DivisionMove> ApplyPromotionAndRelegation(
        WorldState world,
        SeasonOutcome outcome,
        IReadOnlyDictionary<int, bool> playoffWinnerIsHigherDivisionClub)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(playoffWinnerIsHigherDivisionClub);

        var newTiers = new Dictionary<ClubId, int>(world.Clubs.Count);
        foreach (WorldClub club in world.Clubs.All) newTiers[club.Id] = club.Tier;

        var moves = new List<DivisionMove>();

        foreach (ClubId clubId in outcome.AutoPromoted)
        {
            int from = outcome.TierOf(clubId);
            int to = from - 1;
            newTiers[clubId] = to;
            moves.Add(new DivisionMove(clubId, from, to, false));
        }

        foreach (ClubId clubId in outcome.AutoRelegated)
        {
            int from = outcome.TierOf(clubId);
            int to = from + 1;
            newTiers[clubId] = to;
            moves.Add(new DivisionMove(clubId, from, to, false));
        }

        foreach (PlayoffTie tie in outcome.PlayoffTies)
        {
            bool higherWon = playoffWinnerIsHigherDivisionClub.TryGetValue(tie.BoundaryTier, out bool value) && value;
            if (higherWon) continue;

            int higherTier = tie.BoundaryTier;
            int lowerTier = higherTier + 1;
            newTiers[tie.HigherDivisionClubId] = lowerTier;
            newTiers[tie.LowerDivisionClubId] = higherTier;
            moves.Add(new DivisionMove(tie.HigherDivisionClubId, higherTier, lowerTier, true));
            moves.Add(new DivisionMove(tie.LowerDivisionClubId, lowerTier, higherTier, true));
        }

        // Apply: club tier, tier league id, then rebuild each league's club list from the new tiers.
        foreach (WorldClub club in world.Clubs.All)
        {
            int tier = newTiers[club.Id];
            WorldLeague league = world.LeagueForTier(tier);
            club.Tier = tier;
            club.LeagueId = league.Id;
        }

        foreach (WorldLeague league in world.Leagues)
        {
            league.ClearClubs();
        }

        foreach (WorldClub club in world.Clubs.All)
        {
            world.LeagueForTier(club.Tier).AddClub(club.Id);
        }

        foreach (WorldLeague league in world.Leagues)
        {
            if (league.Clubs.Count != 18)
            {
                throw new InvalidOperationException(
                    $"After promotion/relegation, {league.Name} has {league.Clubs.Count} clubs instead of 18.");
            }
        }

        return moves;
    }
}
