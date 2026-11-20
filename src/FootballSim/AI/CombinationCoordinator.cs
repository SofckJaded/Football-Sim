using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;

namespace FootballSim.AI;

/// <summary>
/// The bounded vocabulary of combinations (A8.1): a one-two and a third-player move.
///
/// A combination is a <b>contract between team-mates</b>, not an action one player takes: the initiator
/// commits to making the first pass and (for a one-two) to running into a return region, the receiver
/// commits to looking for the continuation, and a third player (for a third-player move) is the runner the
/// continuation is aimed at. Every part of the record is a belief the players are entitled to share — a
/// region, a meeting tick, a confidence — and none of it is a read of the authoritative world.
/// </summary>
public enum CombinationKind
{
    /// <summary>No combination.</summary>
    None = 0,

    /// <summary>
    /// Pass-and-move: the initiator passes to the receiver and runs into <see cref="CombinationContract.ReturnXZ"/>,
    /// the receiver's continuation is the ball back into that region (a give-and-go).
    /// </summary>
    OneTwo,

    /// <summary>
    /// Third-player: the initiator passes to the receiver so the receiver can lay the ball off to a third
    /// team-mate's already-advertised run (the run itself is A5.3's, not a second run system).
    /// </summary>
    ThirdPlayer
}

/// <summary>What one player's part in a live combination is.</summary>
public enum CombinationRole
{
    /// <summary>Not a participant.</summary>
    None = 0,

    /// <summary>The player who made the first pass and owns the return region (the return runner in a one-two).</summary>
    Initiator,

    /// <summary>The first receiver: the player who is expected to play the continuation.</summary>
    Receiver,

    /// <summary>The third player whose advertised run the continuation is aimed at.</summary>
    ThirdRunner
}

/// <summary>How a contract left the board. Diagnostics, the probe and the tests read it; no decision does.</summary>
public enum CombinationEndReason
{
    /// <summary>Still live, or never created.</summary>
    None = 0,

    /// <summary>The continuation ball was actually played while the contract was live (the useful outcome).</summary>
    Continued,

    /// <summary>The window ran out without a continuation (the return never came).</summary>
    Expired,

    /// <summary>The team lost the ball: a contract never outlives the possession it was made in.</summary>
    PossessionEnded,

    /// <summary>A new contract replaced it (the initiator passed to somebody else).</summary>
    Replaced,

    /// <summary>The board was full and this contract was the oldest-expiring one: a bounded table's honest loss.</summary>
    Evicted,

    /// <summary>The initiator abandoned it: the return region stopped being a lane worth running.</summary>
    AbandonedByInitiator,

    /// <summary>The receiver abandoned it: the continuation lane was blocked or its target was lost.</summary>
    AbandonedByReceiver,

    /// <summary>The third player's advertised run ended, so there was nothing left to lay the ball off to.</summary>
    AbandonedByThirdRunner,

    /// <summary>A participant left the pitch.</summary>
    ParticipantRemoved,

    /// <summary>A restart, the interval or full time ended the passage.</summary>
    Reset
}

/// <summary>
/// One bounded team-mate contract (A8.1): participants, return region, expected arrival, confidence,
/// expiry and cancellation, all on absolute engine ticks.
///
/// <b>Why a contract and not a call.</b> A call (A7.2) is a delayed, expiring instruction; a contract is a
/// joint commitment with a <i>region and a time</i> both sides act on. That is what makes a return pass a
/// ball to where the initiator will be rather than a ball to where he is, without either player reading the
/// other's live position: the region and the meeting tick travel on the contract.
///
/// <b>Knowledge boundary.</b> The record carries only what the initiator is entitled to tell his team: own
/// intention (a region and a time) plus a confidence. It carries no opponent position, velocity or intent,
/// so a contract can never become a second, faster sight line.
/// </summary>
public readonly record struct CombinationContract(
    int ContractId,
    CombinationKind Kind,
    TeamId Team,
    int InitiatorId,
    int ReceiverId,
    int ThirdPlayerId,
    Vector2 ReturnXZ,
    float ReturnRadiusMeters,
    int ExpectedArrivalTick,
    int CreatedTick,
    int VisibleTick,
    int ExpiresTick,
    float Confidence01)
{
    /// <summary>True once this player is allowed to act on the contract: the initiator knows his own decision at once.</summary>
    public bool IsVisibleTo(int playerId, int tick) =>
        playerId == InitiatorId ? tick >= CreatedTick : tick >= VisibleTick;

    /// <summary>True while the contract's own window is open.</summary>
    public bool IsLiveAt(int tick) => tick <= ExpiresTick;

    /// <summary>True while this participant may act on it: both heard (or owns it) and still in its window.</summary>
    public bool IsUsableBy(int playerId, int tick) => IsLiveAt(tick) && IsVisibleTo(playerId, tick);

    /// <summary>The player the continuation pass is aimed at: the initiator for a one-two, the third man otherwise.</summary>
    public int ContinuationTargetId =>
        Kind == CombinationKind.OneTwo ? InitiatorId : ThirdPlayerId;

    /// <summary>
    /// The player's role in this contract, or <see cref="CombinationRole.None"/>. One record answers
    /// "what am I in this?" for all three participants, which is what lets a single observation field carry
    /// the whole picture.
    /// </summary>
    public CombinationRole RoleOf(int playerId)
    {
        if (playerId == InitiatorId) return CombinationRole.Initiator;
        if (playerId == ReceiverId) return CombinationRole.Receiver;
        if (ThirdPlayerId > 0 && playerId == ThirdPlayerId) return CombinationRole.ThirdRunner;
        return CombinationRole.None;
    }

    /// <summary>
    /// How much of the promise a participant may still believe at <paramref name="tick"/>: the initiator's
    /// confidence before the meeting, decaying through the grace window afterwards. A continuation played
    /// after the planned arrival is a ball to where the initiator *was*, so the belief falls away with the
    /// window instead of ending abruptly (the same shape A5.3's advertisement uses).
    /// </summary>
    public float Promise01(int tick)
    {
        if (!IsLiveAt(tick)) return 0f;

        float confidence = float.IsFinite(Confidence01) ? Math.Clamp(Confidence01, 0f, 1f) : 0f;
        if (confidence <= 0f) return 0f;
        if (tick <= ExpectedArrivalTick) return confidence;

        float grace = Math.Max(1, ExpiresTick - ExpectedArrivalTick);
        float remaining = 1f - (tick - ExpectedArrivalTick) / grace;
        return confidence * Math.Clamp(remaining, 0f, 1f);
    }

    /// <summary>Compact text form for a trace line or a failure message.</summary>
    public string Describe() =>
        $"{Kind} #{ContractId} team {Team}: #{InitiatorId}→#{ReceiverId}" +
        (ThirdPlayerId > 0 ? $"→#{ThirdPlayerId}" : string.Empty) +
        $" region ({ReturnXZ.X:F1},{ReturnXZ.Y:F1}) r{ReturnRadiusMeters:F1} meet t{ExpectedArrivalTick}" +
        $" t{CreatedTick} (heard t{VisibleTick}, until t{ExpiresTick}) conf {Confidence01:F2}";
}

/// <summary>
/// What one player's observation carries about the live combination he is part of: his role, the return
/// region and its meeting tick, and how much of the promise is still believable.
///
/// The default is neutral (<see cref="Kind"/> none), so a fixture or standalone controller that knows
/// nothing about combinations behaves exactly as it did before A8.1 — the same rule every other
/// added-to-observation field in this project follows.
/// </summary>
public readonly record struct CombinationContext(
    int ContractId,
    CombinationKind Kind,
    CombinationRole Role,
    int InitiatorId,
    int ReceiverId,
    int ThirdPlayerId,
    Vector2 ReturnXZ,
    float ReturnRadiusMeters,
    int ExpectedArrivalTick,
    int ExpiresTick,
    float Confidence01)
{
    /// <summary>No live combination: the neutral picture of every pre-A8.1 fixture.</summary>
    public static CombinationContext None => default;

    /// <summary>True when this player is part of a live contract.</summary>
    public bool IsActive => Kind != CombinationKind.None && Role != CombinationRole.None && ContractId != 0;

    /// <summary>True when this player is the first receiver, i.e. the one expected to play the continuation.</summary>
    public bool IsReceiver => IsActive && Role == CombinationRole.Receiver;

    /// <summary>
    /// True when this player is the initiator of a one-two: the player who must run into the return region.
    /// A third-player initiator does not run — the third man's own advertised run is the movement.
    /// </summary>
    public bool IsReturnRunner => IsActive && Kind == CombinationKind.OneTwo && Role == CombinationRole.Initiator;

    /// <summary>The player the continuation pass is aimed at: the initiator for a one-two, the third man otherwise.</summary>
    public int ContinuationTargetId => Kind == CombinationKind.OneTwo ? InitiatorId : ThirdPlayerId;

    /// <summary>How much of the promise is still believable at <paramref name="tick"/> (0 when ended).</summary>
    public float Promise01(int tick)
    {
        if (!IsActive || tick > ExpiresTick) return 0f;

        float confidence = float.IsFinite(Confidence01) ? Math.Clamp(Confidence01, 0f, 1f) : 0f;
        if (confidence <= 0f) return 0f;
        if (tick <= ExpectedArrivalTick) return confidence;

        float grace = Math.Max(1, ExpiresTick - ExpectedArrivalTick);
        float remaining = 1f - (tick - ExpectedArrivalTick) / grace;
        return confidence * Math.Clamp(remaining, 0f, 1f);
    }

    /// <summary>Compact text form for the inspector, a trace line or a failure message.</summary>
    public string Describe() =>
        IsActive
            ? $"{Kind} #{ContractId} as {Role} with #{InitiatorId}/#{ReceiverId}" +
              (ThirdPlayerId > 0 ? $"/#{ThirdPlayerId}" : string.Empty) +
              $" region ({ReturnXZ.X:F1},{ReturnXZ.Y:F1}) meet t{ExpectedArrivalTick} conf {Confidence01:F2}"
            : "none";
}

/// <summary>
/// The match's bounded combination board (A8.1): one small fixed table of live team-mate contracts, each with
/// a communication delay, an expected arrival, a confidence and an expiry.
///
/// <b>Ownership.</b> One instance lives on the match's <c>MatchCommunicationContext</c> — the same object that
/// already carries the human's call for the pass, A5.3's run advertisements and A7.2's calls. This is the one
/// team communication channel extended, not a second one.
///
/// <b>Why a table, not a queue.</b> A decision needs one answer — "which contract am I part of, and what does
/// it ask of me" — not a transcript; a fixed table has one answer per participant, cannot grow with the match
/// and cannot make the outcome depend on the order messages were drained in. The same reasoning as A5.3's run
/// board and A7.2's call board, deliberately reused rather than invented a third time.
///
/// <b>Lifecycle.</b> A contract is published by the initiator's decision (<see cref="Publish"/>), heard by the
/// other participants after <see cref="CommunicationDelayTicks"/>, and ends exactly once: with a continuation
/// (<see cref="ApplyContacts"/>), an expiry, a lost possession (<see cref="ObservePossession"/>), an explicit
/// participant abandonment (<see cref="Abandon"/>), a replaced promise, a departure
/// (<see cref="ReleasePlayer"/>) or a passage reset (<see cref="ClearAll"/>). Every ending is counted with its
/// reason, so the probe can say why a contract died instead of only that it did.
///
/// <b>Determinism.</b> The delay and the expiry are live ticks, every scan is index-ordered, and eviction is
/// "expired first, then earliest expiry, then lowest index" — two matches with the same seed therefore create,
/// hear and end exactly the same contracts.
/// </summary>
public sealed class CombinationCoordinator
{
    /// <summary>
    /// Entries the board can hold. One live contract per initiator and at most one initiator per player, so
    /// two squads of eleven plus slack is a hard ceiling no correct producer can exceed.
    /// </summary>
    public const int Capacity = 24;

    /// <summary>
    /// How long a contract takes to reach a team-mate, in live ticks. Deliberately A5.3's figure (18 ticks =
    /// 0.30 s) rather than a new convention: a contract is the same kind of team communication as a run
    /// advertisement, and two delays for two channels would be a second convention where one exists.
    /// The initiator himself knows his own decision on the tick he makes it.
    /// </summary>
    public const int CommunicationDelayTicks = RunAdvertisementBoard.CommunicationDelayTicks;

    /// <summary>
    /// How long a contract stays live after the arrival it names, in ticks (1.5 s). A return played after
    /// this is a ball to where the initiator was, and the receiver is free again rather than waiting for a
    /// continuation that no longer exists.
    /// </summary>
    public const int ArrivalGraceTicks = RunAdvertisementBoard.ClaimGraceTicks;

    /// <summary>
    /// The longest meeting a contract may name, in ticks ahead (5 s). A combination is a short move, not a
    /// reservation: a promise about where somebody will be in ten seconds is not a coordination record.
    /// </summary>
    public const int MaxArrivalLeadTicks = 300;

    /// <summary>Smallest return region worth naming, in metres.</summary>
    public const float MinReturnRadiusMeters = 0.5f;

    /// <summary>Largest return region that is still a region rather than "somewhere over there", in metres.</summary>
    public const float MaxReturnRadiusMeters = 10f;

    private struct Entry
    {
        public bool Occupied;
        public CombinationContract Contract;

        /// <summary>The continuation ball was actually played; still live for the initiator's run until expiry.</summary>
        public bool Continued;
    }

    private readonly Entry[] _entries = new Entry[Capacity];
    private readonly int[] _publishedByKind = new int[3];
    private readonly int[] _continuedByKind = new int[3];
    private readonly int[] _endedByReason = new int[Enum.GetValues(typeof(CombinationEndReason)).Length];

    private int _nextContractId = 1;
    private int _lastSweepTick = int.MinValue;

    /// <summary>Contracts accepted on this board since the match started (diagnostics only).</summary>
    public int PublishedCount { get; private set; }

    /// <summary>Continuation balls actually played while their contract was live (diagnostics only).</summary>
    public int ContinuedCount { get; private set; }

    /// <summary>Repeated identical proposals suppressed while the same contract was still live.</summary>
    public int SuppressedCount { get; private set; }

    /// <summary>Live contracts evicted to make room for a new one.</summary>
    public int EvictedCount { get; private set; }

    /// <summary>Contracts of one kind accepted since the match started.</summary>
    public int CountOfPublished(CombinationKind kind)
    {
        int index = (int)kind;
        return index >= 0 && index < _publishedByKind.Length ? _publishedByKind[index] : 0;
    }

    /// <summary>Continuations of one kind actually played since the match started.</summary>
    public int CountOfContinued(CombinationKind kind)
    {
        int index = (int)kind;
        return index >= 0 && index < _continuedByKind.Length ? _continuedByKind[index] : 0;
    }

    /// <summary>Contracts that ended for one reason since the match started.</summary>
    public int CountOfEnd(CombinationEndReason reason)
    {
        int index = (int)reason;
        return index >= 0 && index < _endedByReason.Length ? _endedByReason[index] : 0;
    }

    /// <summary>
    /// Publishes one contract. Returns false when the proposal is malformed or when the same contract (same
    /// initiator, receiver, third man and kind) is already live: a contract is a commitment, not a per-tick
    /// state, so re-deciding the same pass must not restart the communication delay for ever.
    ///
    /// A different live contract from the same initiator is <b>replaced</b> — the carrier changed his mind
    /// about where the ball is going, and the team must not keep acting on the old promise.
    /// </summary>
    public bool Publish(
        CombinationKind kind,
        TeamId team,
        int initiatorId,
        int receiverId,
        int thirdPlayerId,
        Vector2 returnXZ,
        float returnRadiusMeters,
        int expectedArrivalTick,
        float confidence01,
        int tick)
    {
        if (kind == CombinationKind.None) return false;
        if (initiatorId <= 0 || receiverId <= 0) return false;
        if (initiatorId == receiverId) return false;
        if (thirdPlayerId < 0) return false;
        if (thirdPlayerId > 0 && (thirdPlayerId == initiatorId || thirdPlayerId == receiverId)) return false;
        if (kind == CombinationKind.ThirdPlayer && thirdPlayerId == 0) return false;
        if (!SimMath.IsFinite(returnXZ)) return false;
        if (!float.IsFinite(returnRadiusMeters) || !float.IsFinite(confidence01)) return false;
        if (expectedArrivalTick <= tick) return false;
        if (expectedArrivalTick - tick > MaxArrivalLeadTicks) return false;

        Sweep(tick);

        int slot = FindLiveByInitiator(team, initiatorId, tick);
        if (slot >= 0)
        {
            CombinationContract existing = _entries[slot].Contract;
            if (existing.Kind == kind && existing.ReceiverId == receiverId &&
                existing.ThirdPlayerId == thirdPlayerId)
            {
                SuppressedCount++;
                return false;
            }

            // The replaced entry's slot is free again and is reused: one live contract per initiator.
            EndSlot(slot, CombinationEndReason.Replaced);
        }
        else
        {
            slot = FindFreeSlot();
            if (slot < 0)
            {
                // Bounded table: the earliest-expiring live contract makes room. Deterministic (lowest
                // index on a tie), so two identical matches lose exactly the same contract.
                slot = FindEarliestExpirySlot(tick);
                if (slot >= 0)
                {
                    EndSlot(slot, CombinationEndReason.Evicted);
                    EvictedCount++;
                }
            }
        }

        if (slot < 0) return false;

        float radius = Math.Clamp(returnRadiusMeters, MinReturnRadiusMeters, MaxReturnRadiusMeters);
        int visibleTick = tick + CommunicationDelayTicks;
        var contract = new CombinationContract(
            ContractId: _nextContractId++,
            Kind: kind,
            Team: team,
            InitiatorId: initiatorId,
            ReceiverId: receiverId,
            ThirdPlayerId: thirdPlayerId,
            ReturnXZ: returnXZ,
            ReturnRadiusMeters: radius,
            ExpectedArrivalTick: expectedArrivalTick,
            CreatedTick: tick,
            VisibleTick: visibleTick,
            ExpiresTick: expectedArrivalTick + ArrivalGraceTicks,
            Confidence01: Math.Clamp(confidence01, 0f, 1f));

        _entries[slot] = new Entry { Occupied = true, Contract = contract, Continued = false };
        PublishedCount++;
        _publishedByKind[(int)kind]++;
        return true;
    }

    /// <summary>
    /// What this player's own observation should carry: his part in the live combination he is entitled to
    /// know about. The initiator hears his own contract immediately; the other participants after the
    /// communication delay. A continued contract no longer reaches the receiver (there is nothing left to
    /// continue) but stays for the initiator, whose return run is still in flight.
    /// </summary>
    public bool TryGetContext(TeamId team, int playerId, int tick, out CombinationContext context)
    {
        context = CombinationContext.None;
        if (playerId <= 0) return false;

        Sweep(tick);

        int best = -1;
        int bestPriority = 0;
        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied || entry.Contract.Team != team) continue;
            if (!entry.Contract.IsUsableBy(playerId, tick)) continue;

            CombinationRole role = entry.Contract.RoleOf(playerId);
            if (role == CombinationRole.None) continue;

            // The receiver and the third man lose the contract once the continuation has been played; the
            // initiator keeps his, because his run is what the ball is travelling to.
            if (entry.Continued && role != CombinationRole.Initiator) continue;

            int priority = role switch
            {
                CombinationRole.Initiator => 3,
                CombinationRole.Receiver => 2,
                _ => 1
            };

            if (priority <= bestPriority) continue;
            best = i;
            bestPriority = priority;
        }

        if (best < 0) return false;

        Entry chosen = _entries[best];
        CombinationContract contract = chosen.Contract;
        context = new CombinationContext(
            contract.ContractId,
            contract.Kind,
            contract.RoleOf(playerId),
            contract.InitiatorId,
            contract.ReceiverId,
            contract.ThirdPlayerId,
            contract.ReturnXZ,
            contract.ReturnRadiusMeters,
            contract.ExpectedArrivalTick,
            contract.ExpiresTick,
            contract.Confidence01);
        return true;
    }

    /// <summary>The live contract this player is the first receiver of, if any (diagnostics and tests).</summary>
    public bool TryGetReceiverContract(TeamId team, int receiverId, int tick, out CombinationContract contract)
    {
        Sweep(tick);
        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied || entry.Continued || entry.Contract.Team != team) continue;
            if (entry.Contract.ReceiverId != receiverId) continue;
            if (!entry.Contract.IsUsableBy(receiverId, tick)) continue;
            contract = entry.Contract;
            return true;
        }

        contract = default;
        return false;
    }

    /// <summary>The live contract this player is the initiator of, if any (diagnostics and tests).</summary>
    public bool TryGetInitiatorContract(TeamId team, int initiatorId, int tick, out CombinationContract contract)
    {
        Sweep(tick);
        int slot = FindLiveByInitiator(team, initiatorId, tick);
        if (slot < 0)
        {
            contract = default;
            return false;
        }

        contract = _entries[slot].Contract;
        return true;
    }

    /// <summary>Number of contracts this team can currently act on.</summary>
    public int LiveCount(TeamId team, int tick)
    {
        Sweep(tick);
        int count = 0;
        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (entry.Occupied && entry.Contract.Team == team && entry.Contract.IsLiveAt(tick)) count++;
        }

        return count;
    }

    /// <summary>
    /// The <paramref name="index"/>-th live contract of this team, in slot order. Together with
    /// <see cref="LiveCount"/> this is how a diagnostic or a probe reads the board without an allocation.
    /// </summary>
    public bool TryGetLiveAt(TeamId team, int index, int tick, out CombinationContract contract)
    {
        Sweep(tick);
        int seen = 0;
        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied || entry.Contract.Team != team || !entry.Contract.IsLiveAt(tick)) continue;
            if (seen++ != index) continue;
            contract = entry.Contract;
            return true;
        }

        contract = default;
        return false;
    }

    /// <summary>
    /// A participant abandons the contract because his part of it stopped being valid (the return lane is
    /// blocked, the region is no longer there, the move has gone elsewhere). Only a participant may abandon
    /// his own contract; the engine's own endings go through the other methods.
    /// </summary>
    public bool Abandon(int contractId, int playerId, CombinationEndReason reason, int tick)
    {
        if (contractId <= 0 || playerId <= 0) return false;
        if (reason is not (CombinationEndReason.AbandonedByInitiator or CombinationEndReason.AbandonedByReceiver
            or CombinationEndReason.AbandonedByThirdRunner))
        {
            return false;
        }

        int slot = FindById(contractId);
        if (slot < 0) return false;
        if (_entries[slot].Contract.RoleOf(playerId) == CombinationRole.None) return false;
        if (!_entries[slot].Contract.IsLiveAt(tick)) return false;

        EndSlot(slot, reason);
        return true;
    }

    /// <summary>
    /// Consumes this tick's executed contacts: a kick from a contract's receiver to its continuation target,
    /// while the contract is live, <b>is</b> the continuation. This is the contact event A8's exit gate asks
    /// for — the combination is attributed from the play, never from proximity.
    /// </summary>
    public int ApplyContacts(IReadOnlyList<BallContactEvent>? contacts, int tick)
    {
        if (contacts is null || contacts.Count == 0) return 0;

        int continued = 0;
        for (int i = 0; i < contacts.Count; i++)
        {
            BallContactEvent contact = contacts[i];
            if (contact.Kind != BallContactKind.Kick) continue;
            if (contact.IntendedReceiverId is not int target || target <= 0) continue;

            int slot = FindLiveForReceiver(contact.Team, contact.PlayerId, tick);
            if (slot < 0 || _entries[slot].Continued) continue;
            if (_entries[slot].Contract.ContinuationTargetId != target) continue;

            _entries[slot].Continued = true;
            ContinuedCount++;
            _continuedByKind[(int)_entries[slot].Contract.Kind]++;
            continued++;
        }

        return continued;
    }

    /// <summary>
    /// Ends every contract whose passage has gone: when the ball belongs to the other team, the move it was
    /// built for is over. A null <paramref name="possessingTeam"/> (a dead ball, a restart being set up) is
    /// not a turnover and is left to <see cref="ClearAll"/>; treating it as one would kill a contract in the
    /// ticks between a kick and the model settling on the playing team.
    /// </summary>
    public void ObservePossession(TeamId? possessingTeam, int tick)
    {
        Sweep(tick);
        if (possessingTeam is not TeamId team) return;

        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied || entry.Contract.Team == team) continue;
            if (!entry.Contract.IsLiveAt(tick)) continue;
            EndSlot(i, CombinationEndReason.PossessionEnded);
        }
    }

    /// <summary>A player left the pitch: every contract he was part of ends with him.</summary>
    public void ReleasePlayer(int playerId, int tick)
    {
        if (playerId <= 0) return;
        Sweep(tick);

        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied) continue;
            if (entry.Contract.RoleOf(playerId) == CombinationRole.None) continue;
            EndSlot(i, CombinationEndReason.ParticipantRemoved);
        }
    }

    /// <summary>Ends every live contract: a restart, the interval or full time ended the passage they belong to.</summary>
    public void ClearAll(int tick)
    {
        Sweep(tick);
        for (int i = 0; i < _entries.Length; i++)
        {
            if (!_entries[i].Occupied) continue;
            EndSlot(i, CombinationEndReason.Reset);
        }
    }

    /// <summary>True while this team has any live contract (diagnostics and tests).</summary>
    public bool HasLiveContract(TeamId team, int tick) => LiveCount(team, tick) > 0;

    private void EndSlot(int slot, CombinationEndReason reason)
    {
        if (slot < 0 || !_entries[slot].Occupied) return;

        _entries[slot] = default;
        _endedByReason[(int)reason]++;
    }

    /// <summary>Expired entries are counted once and freed; nothing remembers a contract past its expiry.</summary>
    private void Sweep(int tick)
    {
        if (tick <= _lastSweepTick) return;
        _lastSweepTick = tick;

        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied) continue;
            if (entry.Contract.IsLiveAt(tick)) continue;

            // A contract whose continuation was played is remembered as Continued rather than Expired: the
            // ending table then distinguishes "the return never came" from "the move was played and the run
            // to meet the ball simply outlived its window".
            EndSlot(i, entry.Continued ? CombinationEndReason.Continued : CombinationEndReason.Expired);
        }
    }

    private int FindById(int contractId)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i].Occupied && _entries[i].Contract.ContractId == contractId) return i;
        }

        return -1;
    }

    private int FindLiveByInitiator(TeamId team, int initiatorId, int tick)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied || entry.Contract.Team != team) continue;
            if (entry.Contract.InitiatorId != initiatorId) continue;
            if (!entry.Contract.IsLiveAt(tick)) continue;
            return i;
        }

        return -1;
    }

    private int FindLiveForReceiver(TeamId team, int receiverId, int tick)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied || entry.Contract.Team != team) continue;
            if (entry.Contract.ReceiverId != receiverId) continue;
            if (!entry.Contract.IsLiveAt(tick)) continue;
            return i;
        }

        return -1;
    }

    /// <summary>The first unoccupied slot, or -1 when the table is full.</summary>
    private int FindFreeSlot()
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (!_entries[i].Occupied) return i;
        }

        return -1;
    }

    /// <summary>
    /// The live entry with the earliest expiry, lowest index as the tie-break. Deterministic: the eviction
    /// never depends on iteration order beyond the fixed table order.
    /// </summary>
    private int FindEarliestExpirySlot(int tick)
    {
        int best = -1;
        int bestExpiry = int.MaxValue;

        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied || !entry.Contract.IsLiveAt(tick)) continue;
            if (entry.Contract.ExpiresTick >= bestExpiry) continue;
            best = i;
            bestExpiry = entry.Contract.ExpiresTick;
        }

        return best;
    }
}
