using FootballSim.World.Core;

namespace FootballSim.World.World;

/// <summary>
/// A player's contract terms: the money and the expiry. The current employer is still
/// <see cref="Players.WorldPlayer.ClubId"/> (one authority for "who does he play for"); this type adds
/// what that relationship costs and when it ends, plus the loan link.
/// </summary>
public sealed class PlayerContract
{
    /// <summary>Season the current contract was signed in.</summary>
    public int SignedSeason { get; set; }

    /// <summary>Last season covered by the contract; it expires in the off-season after this one.</summary>
    public int EndSeason { get; set; }

    /// <summary>Weekly wage in whole currency units.</summary>
    public long WeeklyWage { get; set; }

    /// <summary>True while the player is on loan at his current club.</summary>
    public bool IsOnLoan { get; set; }

    /// <summary>The club that owns the player's registration while he is on loan, else <see cref="ClubId.None"/>.</summary>
    public ClubId ParentClubId { get; set; }

    /// <summary>Last season covered by the loan.</summary>
    public int LoanEndSeason { get; set; }

    /// <summary>Seasons of the contract still to run from <paramref name="currentSeason"/> (0 = expiring).</summary>
    public int SeasonsRemaining(int currentSeason) => Math.Max(0, EndSeason - currentSeason + 1);

    /// <summary>True when the contract expires in the off-season after <paramref name="season"/>.</summary>
    public bool ExpiresAfter(int season) => EndSeason <= season;

    /// <summary>True when the loan expires in the off-season after <paramref name="season"/>.</summary>
    public bool LoanExpiresAfter(int season) => IsOnLoan && LoanEndSeason <= season;

    /// <summary>A default amateur contract: no wage, one season, not a loan.</summary>
    public static PlayerContract Amateur(int season) => new()
    {
        SignedSeason = season,
        EndSeason = season,
        WeeklyWage = 0,
        IsOnLoan = false,
        ParentClubId = ClubId.None,
        LoanEndSeason = 0
    };
}

/// <summary>Why a player changed club (or left the game).</summary>
public enum TransferKind
{
    /// <summary>A permanent transfer for a fee.</summary>
    Permanent = 0,

    /// <summary>A permanent move with no fee (amateur/free movement or expired contract).</summary>
    Free = 1,

    /// <summary>A loan move; registration stays with the parent club.</summary>
    Loan = 2,

    /// <summary>A youth intake signing (new player to the world).</summary>
    YouthIntake = 3,

    /// <summary>A player released by his club and becoming a free agent.</summary>
    Released = 4,

    /// <summary>A player retired from football.</summary>
    Retirement = 5,

    /// <summary>A loan ending and the player returning to his parent club.</summary>
    LoanReturn = 6
}

/// <summary>
/// One entry in the world's transfer log. The log is the audit trail the soak test asserts on (volume,
/// determinism) and the career inbox/history reads from; it is append-only.
/// </summary>
/// <param name="Season">Season the move happened in.</param>
/// <param name="Week">Week the move happened in.</param>
/// <param name="PlayerId">The player who moved.</param>
/// <param name="FromClubId">Club he left, or <see cref="ClubId.None"/> for a youth intake/new player.</param>
/// <param name="ToClubId">Club he joined, or <see cref="ClubId.None"/> for retirement/release.</param>
/// <param name="Kind">Why the move happened.</param>
/// <param name="Fee">Transfer fee paid, in whole currency units (0 for loans and frees).</param>
public sealed record TransferRecord(
    int Season,
    int Week,
    PlayerId PlayerId,
    ClubId FromClubId,
    ClubId ToClubId,
    TransferKind Kind,
    long Fee)
{
    /// <summary>True for a move that changes the player's employer club.</summary>
    public bool ChangesClub => Kind is TransferKind.Permanent or TransferKind.Free;

    /// <summary>True for an incoming player (fee or free).</summary>
    public bool IsIncomingMove => ChangesClub;

    /// <inheritdoc />
    public override string ToString() =>
        $"S{Season}W{Week:00} {Kind}: {PlayerId} {FromClubId}->{ToClubId} fee {Fee}";
}
