using FootballSim.World.Core;

namespace FootballSim.World.World;

/// <summary>One tie in a cup round: the fixture that decides who advances.</summary>
public sealed class CupTie
{
    /// <summary>Creates a tie around an unplayed cup fixture.</summary>
    public CupTie(WorldFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (!fixture.IsCupTie) throw new ArgumentException("A cup tie needs a cup fixture.", nameof(fixture));
        Fixture = fixture;
    }

    /// <summary>The cup fixture.</summary>
    public WorldFixture Fixture { get; }

    /// <summary>Home club.</summary>
    public ClubId HomeClubId => Fixture.HomeClubId;

    /// <summary>Away club.</summary>
    public ClubId AwayClubId => Fixture.AwayClubId;

    /// <summary>True once the tie has a winner.</summary>
    public bool IsResolved => Fixture.IsPlayed && Fixture.CupWinner.IsValid;

    /// <summary>Winning club, or <see cref="ClubId.None"/> before the tie is played.</summary>
    public ClubId WinnerClubId => Fixture.CupWinner;
}

/// <summary>
/// One round of the domestic cup. Rounds are created as the bracket fills (tier 1 and 2 only join at the
/// round of 64), so a round owns its ties plus any clubs that received a bye through it.
/// </summary>
public sealed class CupRoundBracket
{
    private readonly List<CupTie> _ties = new();
    private readonly List<ClubId> _byes = new();

    /// <summary>Creates a round.</summary>
    public CupRoundBracket(CupRound round, int week)
    {
        if (round == CupRound.None) throw new ArgumentOutOfRangeException(nameof(round));
        if (week is < 1 or > 52) throw new ArgumentOutOfRangeException(nameof(week));
        Round = round;
        Week = week;
    }

    /// <summary>Which round this is.</summary>
    public CupRound Round { get; }

    /// <summary>Calendar week the round is played in.</summary>
    public int Week { get; }

    /// <summary>Ties in this round.</summary>
    public IReadOnlyList<CupTie> Ties => _ties;

    /// <summary>Clubs that advance without playing this round.</summary>
    public IReadOnlyList<ClubId> Byes => _byes;

    /// <summary>True when every tie has a winner.</summary>
    public bool IsResolved
    {
        get
        {
            foreach (CupTie tie in _ties)
            {
                if (!tie.IsResolved) return false;
            }
            return true;
        }
    }

    /// <summary>Adds a tie (the fixture's round must match this round).</summary>
    public void AddTie(CupTie tie)
    {
        ArgumentNullException.ThrowIfNull(tie);
        if (tie.Fixture.CupRound != Round)
            throw new ArgumentException($"{tie.Fixture.CupRound} fixture added to the {Round} round.");
        _ties.Add(tie);
    }

    /// <summary>Adds a club that advances through this round without a tie.</summary>
    public void AddBye(ClubId clubId)
    {
        if (!clubId.IsValid) throw new ArgumentOutOfRangeException(nameof(clubId));
        if (!_byes.Contains(clubId)) _byes.Add(clubId);
    }

    /// <summary>Winners of this round in stable order (tie winners first, then byes), or empty while unresolved.</summary>
    public IReadOnlyList<ClubId> QualifiedClubs()
    {
        if (!IsResolved) return Array.Empty<ClubId>();
        var result = new List<ClubId>(_ties.Count + _byes.Count);
        foreach (CupTie tie in _ties) result.Add(tie.WinnerClubId);
        result.AddRange(_byes);
        return result;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Round} (week {Week}, {_ties.Count} ties, {_byes.Count} byes)";
}

/// <summary>
/// The domestic knockout cup for one season. All clubs enter, in tiers: the preliminary round is tiers 5
/// and 6, the second round adds tiers 3 and 4, and the round of 64 adds tiers 1 and 2. The bracket logic
/// (which clubs pair, when rounds appear) lives in the cup scheduler; this type is only the state.
/// </summary>
public sealed class WorldCup
{
    private readonly List<CupRoundBracket> _rounds = new(8);

    /// <summary>Creates an empty cup for a season.</summary>
    public WorldCup(string name, int season)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Season = season;
    }

    /// <summary>Display name (country + "National Cup").</summary>
    public string Name { get; }

    /// <summary>Season the cup belongs to.</summary>
    public int Season { get; private set; }

    /// <summary>Rounds created so far, in order.</summary>
    public IReadOnlyList<CupRoundBracket> Rounds => _rounds;

    /// <summary>True once the final has been played.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Winning club of the final, or <see cref="ClubId.None"/> until the cup is complete.</summary>
    public ClubId ChampionClubId { get; private set; }

    /// <summary>The most recently created round, or null before the season starts.</summary>
    public CupRoundBracket? CurrentRound => _rounds.Count > 0 ? _rounds[^1] : null;

    /// <summary>Looks a round up by enum value.</summary>
    public CupRoundBracket? RoundOf(CupRound round)
    {
        foreach (CupRoundBracket bracket in _rounds)
        {
            if (bracket.Round == round) return bracket;
        }
        return null;
    }

    /// <summary>
    /// Creates the next round. Rounds must be added in enum order and only once the previous round is
    /// fully resolved, which is what keeps a bracket from being created against unknown participants.
    /// </summary>
    /// <exception cref="InvalidOperationException">Out of order, duplicate, or previous round unresolved.</exception>
    public CupRoundBracket AddRound(CupRound round, int week)
    {
        if (IsComplete) throw new InvalidOperationException("The cup is already complete.");
        if (_rounds.Count > 0)
        {
            CupRoundBracket previous = _rounds[^1];
            if (round <= previous.Round)
                throw new InvalidOperationException($"Round {round} cannot follow {previous.Round}.");
            if (!previous.IsResolved)
                throw new InvalidOperationException($"Round {previous.Round} is not resolved yet.");
        }

        var bracket = new CupRoundBracket(round, week);
        _rounds.Add(bracket);
        return bracket;
    }

    /// <summary>Marks the cup complete with the given champion.</summary>
    public void MarkComplete(ClubId championClubId)
    {
        if (!championClubId.IsValid) throw new ArgumentOutOfRangeException(nameof(championClubId));
        if (ChampionClubId.IsValid) throw new InvalidOperationException("The cup already has a champion.");
        ChampionClubId = championClubId;
        IsComplete = true;
    }

    /// <summary>Resets the cup for a new season.</summary>
    public void Reset(int season)
    {
        Season = season;
        _rounds.Clear();
        IsComplete = false;
        ChampionClubId = ClubId.None;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Name} S{Season} ({_rounds.Count} rounds drawn, complete: {IsComplete})";
}
