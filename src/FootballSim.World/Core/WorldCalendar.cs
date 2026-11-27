using System;

namespace FootballSim.World.Core;

/// <summary>
/// A point in the world calendar: one week of one season. Seasons are one-based and each season has
/// <see cref="WorldCalendar.WeeksPerSeason"/> weeks — 34 league matchdays plus cup, transfer and off-season
/// weeks. It carries no time of day: the world advances one week at a time.
/// </summary>
public readonly record struct WorldDate : IComparable<WorldDate>
{
    /// <summary>Creates a date, validating both components.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="season"/> is below 1, or <paramref name="week"/> is outside 1..52.
    /// </exception>
    public WorldDate(int season, int week)
    {
        if (season < 1)
            throw new ArgumentOutOfRangeException(nameof(season), season, "Season is one-based.");
        if (week < 1 || week > WorldCalendar.WeeksPerSeason)
            throw new ArgumentOutOfRangeException(nameof(week), week,
                $"Week must be in 1..{WorldCalendar.WeeksPerSeason}.");

        Season = season;
        Week = week;
    }

    /// <summary>One-based season number.</summary>
    public int Season { get; }

    /// <summary>One-based week inside the season, 1..52.</summary>
    public int Week { get; }

    /// <summary>
    /// Whether the date lies inside a real season. A default-constructed value is <c>Season 0, Week 0</c>
    /// and is not valid.
    /// </summary>
    public bool IsValid => Season >= 1 && Week >= 1 && Week <= WorldCalendar.WeeksPerSeason;

    /// <summary>Zero-based week index since the world began: <c>(Season - 1) * 52 + (Week - 1)</c>.</summary>
    public int AbsoluteWeek => (Season - 1) * WorldCalendar.WeeksPerSeason + (Week - 1);

    /// <summary>First week of the first season.</summary>
    public static WorldDate Start => new(1, 1);

    /// <summary>Orders dates by <see cref="AbsoluteWeek"/>, so season boundaries sort correctly.</summary>
    public int CompareTo(WorldDate other) => AbsoluteWeek.CompareTo(other.AbsoluteWeek);

    /// <summary>Compact form used in logs and save files, e.g. <c>S3 W07</c>.</summary>
    public override string ToString() => $"S{Season} W{Week:00}";
}

/// <summary>
/// The fixed shape of one world season. The season is a pure function of the week number: pre-season and
/// the winter transfer window are weeks 1..4 and 22..25, league play is weeks 5..21 and 26..42 (34
/// matchdays), the domestic cup play-offs occupy weeks 43..45 and the off-season closes the year.
/// </summary>
public enum SeasonPhase
{
    /// <summary>Weeks 1..4: squad building, friendlies; transfer window open.</summary>
    PreSeason = 0,

    /// <summary>Weeks 5..21: league matchdays 1..17.</summary>
    FirstHalfLeague = 1,

    /// <summary>Weeks 22..25: mid-season break; winter transfer window open.</summary>
    WinterBreak = 2,

    /// <summary>Weeks 26..42: league matchdays 18..34.</summary>
    SecondHalfLeague = 3,

    /// <summary>Weeks 43..45: domestic cup knockout rounds.</summary>
    Playoffs = 4,

    /// <summary>Weeks 46..52: awards, retirements, new-year rollover.</summary>
    OffSeason = 5
}

/// <summary>
/// The one-authority season calendar for the world simulation. Every helper is a pure function of the week
/// number (or of a <see cref="WorldDate"/>), so callers never have to re-derive week ranges themselves.
/// </summary>
public static class WorldCalendar
{
    /// <summary>Weeks in one world season.</summary>
    public const int WeeksPerSeason = 52;

    /// <summary>League matches each club plays per season (34 matchdays).</summary>
    public const int LeagueMatchdaysPerSeason = 34;

    /// <summary>First week of pre-season.</summary>
    public const int PreSeasonFirstWeek = 1;

    /// <summary>Last week of pre-season; the transfer window closes after this week.</summary>
    public const int PreSeasonLastWeek = 4;

    /// <summary>First league week of the first half (matchday 1).</summary>
    public const int LeagueFirstHalfFirstWeek = 5;

    /// <summary>Last league week of the first half (matchday 17).</summary>
    public const int LeagueFirstHalfLastWeek = 21;

    /// <summary>First week of the winter break; the winter transfer window opens.</summary>
    public const int WinterBreakFirstWeek = 22;

    /// <summary>Last week of the winter break; the winter transfer window closes after this week.</summary>
    public const int WinterBreakLastWeek = 25;

    /// <summary>First league week of the second half (matchday 18).</summary>
    public const int LeagueSecondHalfFirstWeek = 26;

    /// <summary>Last league week of the second half (matchday 34).</summary>
    public const int LeagueSecondHalfLastWeek = 42;

    /// <summary>First week of the domestic cup play-offs.</summary>
    public const int PlayoffFirstWeek = 43;

    /// <summary>Last week of the domestic cup play-offs.</summary>
    public const int PlayoffLastWeek = 45;

    /// <summary>First week of the off-season.</summary>
    public const int OffSeasonFirstWeek = 46;

    /// <summary>Last week of the season (week 52).</summary>
    public const int OffSeasonLastWeek = 52;

    /// <summary>Phase containing <paramref name="week"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="week"/> is outside 1..52.</exception>
    public static SeasonPhase PhaseForWeek(int week)
    {
        if (week < PreSeasonFirstWeek || week > OffSeasonLastWeek)
            throw new ArgumentOutOfRangeException(nameof(week), week,
                $"Week must be in {PreSeasonFirstWeek}..{OffSeasonLastWeek}.");

        if (week <= PreSeasonLastWeek) return SeasonPhase.PreSeason;
        if (week <= LeagueFirstHalfLastWeek) return SeasonPhase.FirstHalfLeague;
        if (week <= WinterBreakLastWeek) return SeasonPhase.WinterBreak;
        if (week <= LeagueSecondHalfLastWeek) return SeasonPhase.SecondHalfLeague;
        if (week <= PlayoffLastWeek) return SeasonPhase.Playoffs;
        return SeasonPhase.OffSeason;
    }

    /// <summary>
    /// Whether the transfer window is open: pre-season (weeks 1..4) and the winter break (weeks 22..25).
    /// </summary>
    public static bool IsTransferWindowOpen(int week) =>
        (week >= PreSeasonFirstWeek && week <= PreSeasonLastWeek) ||
        (week >= WinterBreakFirstWeek && week <= WinterBreakLastWeek);

    /// <summary>Whether a league matchday falls in <paramref name="week"/>: weeks 5..21 and 26..42.</summary>
    public static bool IsLeagueMatchWeek(int week) =>
        (week >= LeagueFirstHalfFirstWeek && week <= LeagueFirstHalfLastWeek) ||
        (week >= LeagueSecondHalfFirstWeek && week <= LeagueSecondHalfLastWeek);

    /// <summary>
    /// League matchday (1..34) played in <paramref name="week"/>, or 0 when the week has no league match.
    /// Week 5 is matchday 1 and week 21 matchday 17; week 26 is matchday 18 and week 42 matchday 34.
    /// </summary>
    public static int LeagueMatchdayForWeek(int week)
    {
        if (week >= LeagueFirstHalfFirstWeek && week <= LeagueFirstHalfLastWeek)
            return week - LeagueFirstHalfFirstWeek + 1;
        if (week >= LeagueSecondHalfFirstWeek && week <= LeagueSecondHalfLastWeek)
            return LeagueMatchdaysPerSeason / 2 + week - LeagueSecondHalfFirstWeek + 1;
        return 0;
    }

    /// <summary>
    /// Moves <paramref name="date"/> by <paramref name="weeks"/> (negative moves backwards) and rolls over
    /// season boundaries. It throws instead of silently clamping when the target would precede
    /// <see cref="WorldDate.Start"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The target week would precede Season 1 Week 1.</exception>
    public static WorldDate AddWeeks(WorldDate date, int weeks)
    {
        long absolute = (long)date.AbsoluteWeek + weeks;
        if (absolute < 0L)
            throw new ArgumentOutOfRangeException(nameof(weeks), weeks,
                "Cannot move to a week before Season 1 Week 1.");

        int season = (int)(absolute / WeeksPerSeason) + 1;
        int week = (int)(absolute % WeeksPerSeason) + 1;
        return new WorldDate(season, week);
    }

    /// <summary>Following week; rolls from week 52 into week 1 of the next season.</summary>
    public static WorldDate NextWeek(WorldDate date) => AddWeeks(date, 1);

    /// <summary>
    /// Signed distance in weeks: positive when <paramref name="to"/> is later than <paramref name="from"/>.
    /// </summary>
    public static int WeeksBetween(WorldDate from, WorldDate to) => to.AbsoluteWeek - from.AbsoluteWeek;

    /// <summary>Weeks remaining in the season after <paramref name="date"/>; 0 in week 52.</summary>
    public static int WeeksUntilSeasonEnd(WorldDate date) => WeeksPerSeason - date.Week;
}
