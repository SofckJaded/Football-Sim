using FootballSim.World.Core;
using FootballSim.World.World;

namespace FootballSim.World.Career;

/// <summary>
/// One completed season of the career: where the player played, what he did and how the club fared
/// (plan stage W4 "season-by-season ledger"). The record is a value snapshot: the live season stats reset
/// every rollover, so the history owns what the player has actually done.
/// </summary>
public sealed class CareerSeasonRecord
{
    /// <summary>The finished season number.</summary>
    public int Season { get; init; }

    /// <summary>Club the player represented that season.</summary>
    public ClubId ClubId { get; init; }

    /// <summary>Club display name at the time (kept for a readable ledger if the club moves).</summary>
    public string ClubName { get; init; } = string.Empty;

    /// <summary>Division tier the club played in that season.</summary>
    public int Tier { get; init; }

    /// <summary>Appearances (starts plus substitute appearances).</summary>
    public int Appearances { get; init; }

    /// <summary>Appearances in the starting XI.</summary>
    public int Starts { get; init; }

    /// <summary>Appearances off the bench.</summary>
    public int SubstituteAppearances { get; init; }

    /// <summary>Competitive minutes played.</summary>
    public int MinutesPlayed { get; init; }

    /// <summary>Goals scored.</summary>
    public int Goals { get; init; }

    /// <summary>Assists made.</summary>
    public int Assists { get; init; }

    /// <summary>Yellow cards.</summary>
    public int YellowCards { get; init; }

    /// <summary>Red cards.</summary>
    public int RedCards { get; init; }

    /// <summary>Mean match rating (0 with no appearances).</summary>
    public float AverageRating { get; init; }

    /// <summary>Trophies and individual awards won that season, in display order.</summary>
    public IReadOnlyList<string> Honours { get; init; } = Array.Empty<string>();

    /// <summary>"Promoted", "Relegated" or an empty string when the club stayed in its division.</summary>
    public string DivisionMovement { get; init; } = string.Empty;

    /// <inheritdoc />
    public override string ToString() =>
        $"S{Season} {ClubName} (tier {Tier}): {Appearances} apps, {Goals} goals, rating {AverageRating:0.00}";
}

/// <summary>One permanent/loan move in the career ledger, derived from the world's transfer log.</summary>
public sealed class CareerTransferRecord
{
    /// <summary>Season the move happened in.</summary>
    public int Season { get; init; }

    /// <summary>Week the move happened in.</summary>
    public int Week { get; init; }

    /// <summary>Club left, or <see cref="ClubId.None"/> for a youth intake/new player.</summary>
    public ClubId FromClubId { get; init; }

    /// <summary>Club joined, or <see cref="ClubId.None"/> for a retirement/release.</summary>
    public ClubId ToClubId { get; init; }

    /// <summary>Display name of the club left ("Free agent" when there was none).</summary>
    public string FromClubName { get; init; } = string.Empty;

    /// <summary>Display name of the club joined ("Free agent" when there is none).</summary>
    public string ToClubName { get; init; } = string.Empty;

    /// <summary>Why the move happened.</summary>
    public TransferKind Kind { get; init; }

    /// <summary>Fee paid in whole currency units (0 for loans and frees).</summary>
    public long Fee { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"S{Season}W{Week:00} {Kind}: {FromClubName} -> {ToClubName}";
}

/// <summary>
/// The career ledger (plan stage W4): one record per completed season plus the player's own transfer
/// history. It is append-only and saved with the career, so the player screen can show the whole career
/// even though the live world keeps only the current season's counters.
/// </summary>
public sealed class CareerHistory
{
    private readonly List<CareerSeasonRecord> _seasons = new();
    private readonly List<CareerTransferRecord> _transfers = new();

    /// <summary>Completed seasons, oldest first.</summary>
    public IReadOnlyList<CareerSeasonRecord> Seasons => _seasons;

    /// <summary>The player's own moves, oldest first.</summary>
    public IReadOnlyList<CareerTransferRecord> Transfers => _transfers;

    /// <summary>Number of completed seasons.</summary>
    public int SeasonsPlayed => _seasons.Count;

    /// <summary>Total appearances across the career.</summary>
    public int TotalAppearances
    {
        get
        {
            int total = 0;
            for (int index = 0; index < _seasons.Count; index++) total += _seasons[index].Appearances;
            return total;
        }
    }

    /// <summary>Total goals across the career.</summary>
    public int TotalGoals
    {
        get
        {
            int total = 0;
            for (int index = 0; index < _seasons.Count; index++) total += _seasons[index].Goals;
            return total;
        }
    }

    /// <summary>Total assists across the career.</summary>
    public int TotalAssists
    {
        get
        {
            int total = 0;
            for (int index = 0; index < _seasons.Count; index++) total += _seasons[index].Assists;
            return total;
        }
    }

    /// <summary>Appearance-weighted career mean rating (0 with no appearances).</summary>
    public float CareerAverageRating
    {
        get
        {
            int appearances = 0;
            float ratingSum = 0f;
            for (int index = 0; index < _seasons.Count; index++)
            {
                CareerSeasonRecord season = _seasons[index];
                appearances += season.Appearances;
                ratingSum += season.AverageRating * season.Appearances;
            }

            return appearances == 0 ? 0f : ratingSum / appearances;
        }
    }

    /// <summary>The highest division tier the player has appeared in (7 when nothing was played yet).</summary>
    public int BestTier
    {
        get
        {
            int best = 7;
            for (int index = 0; index < _seasons.Count; index++)
            {
                if (_seasons[index].Appearances > 0 && _seasons[index].Tier < best) best = _seasons[index].Tier;
            }
            return best;
        }
    }

    /// <summary>
    /// Appends a completed season. Seasons are recorded once, in order: re-adding the same season is a bug
    /// in the career loop, so it throws instead of silently duplicating the ledger.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is null.</exception>
    /// <exception cref="InvalidOperationException">That season is already recorded.</exception>
    public void AddSeason(CareerSeasonRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Season < 1) throw new ArgumentOutOfRangeException(nameof(record), "A season is one-based.");
        if (FindSeason(record.Season) is not null)
        {
            throw new InvalidOperationException($"Season {record.Season} is already in the career history.");
        }

        _seasons.Add(record);
    }

    /// <summary>Appends one transfer record (append-only; duplicates are the world's problem, not the ledger's).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is null.</exception>
    public void AddTransfer(CareerTransferRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _transfers.Add(record);
    }

    /// <summary>The recorded season, or null.</summary>
    /// <param name="season">Season number.</param>
    public CareerSeasonRecord? FindSeason(int season)
    {
        for (int index = 0; index < _seasons.Count; index++)
        {
            if (_seasons[index].Season == season) return _seasons[index];
        }
        return null;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{SeasonsPlayed} seasons, {TotalAppearances} apps, {TotalGoals} goals, {_transfers.Count} moves";
}
