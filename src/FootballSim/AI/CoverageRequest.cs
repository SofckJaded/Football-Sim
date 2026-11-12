using System;
using System.Numerics;
using FootballSim.Core;

namespace FootballSim.AI;

/// <summary>
/// A3.5: one candidate owner's bid for a coverage region. Bids are kept so a request can be inspected
/// after the fact — which candidate was closest, which had the better role, and by how much the winner
/// won — instead of the allocator silently returning one name.
/// </summary>
public readonly record struct CoverageBid(
    int PlayerId,
    float Score,
    float TravelMeters,
    float RoleFit,
    float OwnRegionDistanceMeters);

/// <summary>
/// A3.5: the lifecycle of a coverage request. A region that belongs to one player is temporarily held by
/// another; the states are the documented end reasons, so a closed request says <i>why</i> it ended
/// rather than only that it did.
/// </summary>
public enum CoverageRequestState
{
    /// <summary>No request has ever been raised for this team, or the last one is not live.</summary>
    None = 0,

    /// <summary>Bids are being collected. Only lasts for the pass that opens the request.</summary>
    Open = 1,

    /// <summary>An owner accepted and the duty is live.</summary>
    Accepted = 2,

    /// <summary>The source is back in the region: handback (A3.5).</summary>
    HandedBack = 3,

    /// <summary>The hold reached its maximum duration.</summary>
    Expired = 4,

    /// <summary>The owner left the pitch; the request is re-owned or closed.</summary>
    OwnerRemoved = 5,

    /// <summary>The passage ended — a turnover, or the ball left the region's area.</summary>
    PassageEnded = 6,

    /// <summary>Nobody was eligible to bid (no line player close enough, or nobody left).</summary>
    NoBid = 7
}

/// <summary>
/// A3.5: a coverage request — a region one player's duty has vacated, offered to nearby teammates, won by
/// a deterministic bid, accepted, held, and ended with a stated reason (handback, expiry, owner removal
/// or the passage ending).
///
/// The instance is reused per team: exactly one request can be live per team, which is the cascade bound
/// the plan asks for (A6.4 will want more than one and will have to raise the bound deliberately). Bids
/// are stored in a fixed buffer, so maintaining a request allocates nothing.
///
/// <para>
/// "Recovery" in the plan's list is the handback path plus re-opening: when the region is vacated again
/// after a handback, a *new* request is raised (its <see cref="CreatedTick"/> and bid table show it) rather
/// than the closed one being resurrected, so the history of who held what stays readable.
/// </para>
/// </summary>
public sealed class CoverageRequest
{
    /// <summary>Largest bid table a request keeps. Eight is more line players than a team can spare.</summary>
    public const int MaxBids = 8;

    private readonly CoverageBid[] _bids = new CoverageBid[MaxBids];

    public CoverageRequest(TeamId team) => Team = team;

    public TeamId Team { get; }

    /// <summary>True while an owner is holding the region.</summary>
    public bool IsLive => State == CoverageRequestState.Accepted;

    public CoverageRequestState State { get; private set; } = CoverageRequestState.None;

    /// <summary>The player whose region is (or was) uncovered.</summary>
    public int SourcePlayerId { get; private set; }

    /// <summary>The player holding the region, while live and after it closes.</summary>
    public int OwnerPlayerId { get; private set; }

    public Vector3 Region { get; private set; }
    public int CreatedTick { get; private set; }
    public int AcceptedTick { get; private set; }
    public int ClosedTick { get; private set; }

    /// <summary>Why the request is in its current state. Never empty once it has been opened.</summary>
    public string Reason { get; private set; } = string.Empty;

    /// <summary>
    /// How the *previous* request on this team ended, kept when the instance is reopened (A3.5). A team
    /// has one request at a time, so without this a re-open would hide the handback or expiry that
    /// preceded it.
    /// </summary>
    public CoverageRequestState PreviousState { get; private set; } = CoverageRequestState.None;

    public string PreviousReason { get; private set; } = string.Empty;
    public int PreviousClosedTick { get; private set; }

    public int BidCount { get; private set; }

    public CoverageBid Bid(int index) =>
        index >= 0 && index < BidCount ? _bids[index] : throw new ArgumentOutOfRangeException(nameof(index), index, "Bid index out of range.");

    /// <summary>The winning bid, when a winner was accepted.</summary>
    public CoverageBid? WinningBid
    {
        get
        {
            for (int i = 0; i < BidCount; i++)
                if (_bids[i].PlayerId == OwnerPlayerId) return _bids[i];
            return null;
        }
    }

    /// <summary>Opens a fresh request, discarding the previous one's bids and state.</summary>
    internal void Open(int sourcePlayerId, Vector3 region, int tick, string reason)
    {
        // Keep the closure that this re-open is replacing, so a handback or an expiry is not lost the
        // moment the next request is raised.
        PreviousState = State;
        PreviousReason = Reason;
        PreviousClosedTick = ClosedTick;

        State = CoverageRequestState.Open;
        SourcePlayerId = sourcePlayerId;
        OwnerPlayerId = 0;
        Region = region;
        CreatedTick = tick;
        AcceptedTick = 0;
        ClosedTick = 0;
        BidCount = 0;
        Reason = reason;
    }

    /// <summary>Records one candidate's bid, in ascending player id order; extras beyond the table are dropped.</summary>
    internal void AddBid(in CoverageBid bid)
    {
        if (BidCount >= MaxBids) return;
        _bids[BidCount++] = bid;
    }

    /// <summary>Accepts the winning bid. The caller has already chosen deterministically from the table.</summary>
    internal void Accept(int ownerPlayerId, int tick, string reason)
    {
        if (State != CoverageRequestState.Open)
            throw new InvalidOperationException($"Cannot accept a coverage request in state {State}.");
        State = CoverageRequestState.Accepted;
        OwnerPlayerId = ownerPlayerId;
        AcceptedTick = tick;
        Reason = reason;
    }

    /// <summary>
    /// Closes the request with a stated reason. A request that never opened, or that is already closed,
    /// is left alone — the counters and the reason text must describe one closure, not every pass.
    /// </summary>
    internal bool Close(CoverageRequestState state, int tick, string reason)
    {
        if (State != CoverageRequestState.Open && State != CoverageRequestState.Accepted) return false;

        State = state;
        ClosedTick = tick;
        Reason = reason;
        return true;
    }

    /// <summary>Picks the winner: best score, ties by lower player id (deterministic by construction).</summary>
    internal int SelectWinner()
    {
        int winner = 0;
        float bestScore = float.MinValue;
        for (int i = 0; i < BidCount; i++)
        {
            CoverageBid bid = _bids[i];
            if (bid.Score > bestScore || (MathF.Abs(bid.Score - bestScore) < 1e-4f && bid.PlayerId < winner))
            {
                bestScore = bid.Score;
                winner = bid.PlayerId;
            }
        }

        return winner;
    }
}
