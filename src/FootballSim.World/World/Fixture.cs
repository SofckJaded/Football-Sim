using FootballSim.World.Core;

namespace FootballSim.World.World;

/// <summary>Which competition a fixture belongs to.</summary>
public enum CompetitionKind
{
    /// <summary>A league matchday.</summary>
    League = 0,

    /// <summary>A domestic cup tie.</summary>
    Cup = 1
}

/// <summary>
/// The domestic knockout cup's rounds, in the order they are played. The plan's tiered entry means the
/// bracket is not a pure power of two: tier 1 and 2 join at the round of 64, and the two preliminary
/// rounds plus two byes produce the 28 pre-qualified clubs that fill that round (plan §5.1).
/// </summary>
public enum CupRound
{
    /// <summary>Not a cup fixture.</summary>
    None = 0,

    /// <summary>Week 5: tier 5 and tier 6 clubs (36 enter, 18 qualify).</summary>
    Preliminary = 1,

    /// <summary>Week 10: tier 3 and tier 4 join the 18 preliminary winners (54 clubs, 26 ties + 2 byes).</summary>
    SecondRound = 2,

    /// <summary>Week 15: tier 1 and tier 2 join, producing a 64-club bracket.</summary>
    RoundOf64 = 3,

    /// <summary>Week 20: 64 -> 32.</summary>
    RoundOf32 = 4,

    /// <summary>Week 25: 32 -> 16.</summary>
    RoundOf16 = 5,

    /// <summary>Week 31: 16 -> 8.</summary>
    QuarterFinal = 6,

    /// <summary>Week 37: 8 -> 4.</summary>
    SemiFinal = 7,

    /// <summary>Week 42: the Final.</summary>
    Final = 8
}

/// <summary>
/// One match in the world: a league matchday fixture or a cup tie. The result lives on the fixture
/// itself — there is one mutable world, so the league table and the cup bracket are derived from played
/// fixtures rather than stored twice.
/// </summary>
public sealed class WorldFixture
{
    /// <summary>Creates an unplayed fixture.</summary>
    public WorldFixture(
        MatchId id,
        int season,
        int week,
        CompetitionKind competition,
        LeagueId leagueId,
        int leagueMatchday,
        CupRound cupRound,
        ClubId homeClubId,
        ClubId awayClubId)
    {
        if (!homeClubId.IsValid || !awayClubId.IsValid)
            throw new ArgumentOutOfRangeException(nameof(homeClubId), "Both clubs are required.");
        if (homeClubId == awayClubId)
            throw new ArgumentException("A club cannot play itself.", nameof(awayClubId));
        if (week is < 1 or > 52) throw new ArgumentOutOfRangeException(nameof(week));

        Id = id;
        Season = season;
        Week = week;
        Competition = competition;
        LeagueId = leagueId;
        LeagueMatchday = leagueMatchday;
        CupRound = cupRound;
        HomeClubId = homeClubId;
        AwayClubId = awayClubId;
    }

    /// <summary>Stable one-based match id.</summary>
    public MatchId Id { get; }

    /// <summary>Season the fixture belongs to.</summary>
    public int Season { get; }

    /// <summary>Calendar week (1..52).</summary>
    public int Week { get; }

    /// <summary>League or cup.</summary>
    public CompetitionKind Competition { get; }

    /// <summary>League id for a league fixture; <see cref="LeagueId.None"/> for a cup tie.</summary>
    public LeagueId LeagueId { get; }

    /// <summary>League matchday 1..34, or 0 for a cup tie.</summary>
    public int LeagueMatchday { get; }

    /// <summary>Cup round, or <see cref="CupRound.None"/> for a league fixture.</summary>
    public CupRound CupRound { get; }

    /// <summary>Home club.</summary>
    public ClubId HomeClubId { get; }

    /// <summary>Away club.</summary>
    public ClubId AwayClubId { get; }

    /// <summary>True once a result has been recorded.</summary>
    public bool IsPlayed { get; private set; }

    /// <summary>Home goals (including extra time for cup ties).</summary>
    public int HomeGoals { get; private set; }

    /// <summary>Away goals (including extra time for cup ties).</summary>
    public int AwayGoals { get; private set; }

    /// <summary>Home penalty-shootout goals for a cup tie that needed one; 0 otherwise.</summary>
    public int HomePenaltyGoals { get; private set; }

    /// <summary>Away penalty-shootout goals for a cup tie that needed one; 0 otherwise.</summary>
    public int AwayPenaltyGoals { get; private set; }

    /// <summary>True for a cup tie.</summary>
    public bool IsCupTie => Competition == CompetitionKind.Cup;

    /// <summary>True when a club plays at home in this fixture.</summary>
    public bool Involves(ClubId clubId) => clubId == HomeClubId || clubId == AwayClubId;

    /// <summary>The winning club of a cup tie, or <see cref="ClubId.None"/> for an unplayed tie.</summary>
    public ClubId CupWinner
    {
        get
        {
            if (!IsCupTie || !IsPlayed) return ClubId.None;
            if (HomeGoals != AwayGoals) return HomeGoals > AwayGoals ? HomeClubId : AwayClubId;
            if (HomePenaltyGoals != AwayPenaltyGoals)
                return HomePenaltyGoals > AwayPenaltyGoals ? HomeClubId : AwayClubId;
            // A cup tie cannot end level; validation in RecordCupResult prevents this state.
            return ClubId.None;
        }
    }

    /// <summary>The losing club of a cup tie, or <see cref="ClubId.None"/>.</summary>
    public ClubId CupLoser
    {
        get
        {
            ClubId winner = CupWinner;
            if (!winner.IsValid) return ClubId.None;
            return winner == HomeClubId ? AwayClubId : HomeClubId;
        }
    }

    /// <summary>Records a league result (a draw is legal).</summary>
    /// <exception cref="InvalidOperationException">The fixture already has a result.</exception>
    public void RecordLeagueResult(int homeGoals, int awayGoals)
    {
        if (Competition != CompetitionKind.League)
            throw new InvalidOperationException("A cup tie needs a winner; use RecordCupResult.");
        if (IsPlayed) throw new InvalidOperationException($"Fixture {Id} already has a result.");
        ValidateGoals(homeGoals, awayGoals);

        HomeGoals = homeGoals;
        AwayGoals = awayGoals;
        IsPlayed = true;
    }

    /// <summary>
    /// Records a cup tie result. The aggregate scoreline is always decisive: a level tie is resolved by
    /// (deterministic) penalties, so a shoot-out score must be supplied and must itself have a winner.
    /// </summary>
    /// <exception cref="ArgumentException">A level tie was recorded without a decisive shoot-out.</exception>
    public void RecordCupResult(int homeGoals, int awayGoals, int homePenaltyGoals, int awayPenaltyGoals)
    {
        if (!IsCupTie) throw new InvalidOperationException("A league fixture cannot record a shoot-out.");
        if (IsPlayed) throw new InvalidOperationException($"Fixture {Id} already has a result.");
        ValidateGoals(homeGoals, awayGoals);
        if (homeGoals == awayGoals)
        {
            if (homePenaltyGoals == awayPenaltyGoals)
                throw new ArgumentException("A cup tie cannot finish level; the shoot-out must be decisive.");
            if (homePenaltyGoals < 0 || awayPenaltyGoals < 0)
                throw new ArgumentOutOfRangeException(nameof(homePenaltyGoals));
        }

        HomeGoals = homeGoals;
        AwayGoals = awayGoals;
        HomePenaltyGoals = homeGoals == awayGoals ? homePenaltyGoals : 0;
        AwayPenaltyGoals = homeGoals == awayGoals ? awayPenaltyGoals : 0;
        IsPlayed = true;
    }

    private static void ValidateGoals(int homeGoals, int awayGoals)
    {
        if (homeGoals is < 0 or > 30 || awayGoals is < 0 or > 30)
            throw new ArgumentOutOfRangeException(nameof(homeGoals), "Goals must be between 0 and 30.");
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"#{Id.Value} S{Season}W{Week:00} {HomeClubId.Value}v{AwayClubId.Value}" +
        (IsPlayed ? $" {HomeGoals}-{AwayGoals}" : " (unplayed)");
}
