using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Tactics;

namespace FootballSim.AI;

/// <summary>
/// The bounded vocabulary of short traps (A8.4). A trap is a <b>contract between defenders</b>: one man
/// presses, one screens the carrier's way out, one covers behind the press. The plan asks for exactly that
/// shape ("pressure, screening, cover") on the defending side, where A8.1's combinations are the attacking
/// mirror.
/// </summary>
public enum TrapKind
{
    /// <summary>No trap.</summary>
    None = 0,

    /// <summary>
    /// A carrier pinned to a touchline: the line is the second defender, the presser closes him from the
    /// inside and funnels him along the line, and the screener takes away the one pass that gets him out.
    /// </summary>
    Touchline
}

/// <summary>What one player's part in a live trap is.</summary>
public enum TrapRole
{
    /// <summary>Not a participant.</summary>
    None = 0,

    /// <summary>Closes the carrier down from the angle the trap names (A6.2's angled pressure).</summary>
    Presser,

    /// <summary>Stands on the carrier's best believed forward pass so the way out is screened.</summary>
    Screen,

    /// <summary>The allocator's cover behind the press: stays behind it rather than joining the chase.</summary>
    Cover
}

/// <summary>How a trap left the board. Diagnostics, the probe and the tests read it; no decision does.</summary>
public enum TrapEndReason
{
    /// <summary>Still live, or never created.</summary>
    None = 0,

    /// <summary>The defending team won the ball while the trap was live (the successful trap).</summary>
    Won,

    /// <summary>The carrier got out: he carried the ball out of the trap region, released it to a team-mate, or
    /// another opponent took control. The trap failed and the side recovers before re-committing.</summary>
    Escaped,

    /// <summary>The ball came loose (a tackle, a deflection, a keeper claim) without the defending team in
    /// control: the trap ended without a clean win or a clean escape.</summary>
    Loose,

    /// <summary>The window ran out with the carrier still trapped but no ball won.</summary>
    Expired,

    /// <summary>The elected presser changed: the trap was built around a man who is no longer pressing.</summary>
    PresserLost,

    /// <summary>The allocator's cover moved to another duty: the trap no longer preserves cover.</summary>
    CoverLost,

    /// <summary>A participant left the pitch.</summary>
    ParticipantRemoved,

    /// <summary>A restart, the interval or full time ended the passage the trap belonged to.</summary>
    Reset
}

/// <summary>
/// One bounded short trap (A8.4): participants, the trap point, the angles and points it names, its window and
/// the confidence it was raised with, all on absolute engine ticks.
///
/// <b>Why a contract and not a press setting.</b> A6.1/A6.2 already produce pressure, a screen and a cover, but
/// each defender decides his own act every tick from his own observation, so nothing makes the three acts the
/// <i>same</i> plan: the screen may be offered on one tick and gone the next, and the cover has no idea a trap
/// is on. A contract is the joint commitment — "these three, this point, for this short window" — that the
/// participants act on. It ends exactly once, with a stated reason, and once it is gone every participant is
/// back on his ordinary duty (the exit gate's "failure to trap must return players to valid duties").
///
/// <b>Knowledge boundary.</b> Every field is either the defending team's own player id or a belief the team's
/// shared picture already holds: the carrier's believed position, the angles derived from it, and a confidence.
/// It carries no opponent velocity, intent or live attribute, so a trap can never become a second, faster sight
/// line.
/// </summary>
public readonly record struct TrapContract(
    int TrapId,
    TrapKind Kind,
    TeamId Team,
    int CarrierId,
    int PresserId,
    int ScreenId,
    int CoverId,
    Vector2 CarrierXZ,
    Vector2 PressOffsetXZ,
    Vector2 ScreenOffsetXZ,
    int CreatedTick,
    int ExpiresTick,
    float Confidence01,
    float Pressure01,
    float Touchline01,
    float CoverFit01,
    float Screen01)
{
    /// <summary>True while the contract's own window is open.</summary>
    public bool IsLiveAt(int tick) => tick <= ExpiresTick;

    /// <summary>This player's role in the trap, or <see cref="TrapRole.None"/>.</summary>
    public TrapRole RoleOf(int playerId)
    {
        if (playerId == PresserId) return TrapRole.Presser;
        if (playerId == ScreenId) return TrapRole.Screen;
        if (playerId == CoverId) return TrapRole.Cover;
        return TrapRole.None;
    }

    /// <summary>The player parts of the contract are mine if this is one of the three ids.</summary>
    public bool IsParticipant(int playerId) => RoleOf(playerId) != TrapRole.None;

    /// <summary>Compact text form for a trace line, a probe or a failure message.</summary>
    public string Describe() =>
        $"{Kind} trap #{TrapId} team {Team}: carrier #{CarrierId} at ({CarrierXZ.X:F1},{CarrierXZ.Y:F1})" +
        $" | press #{PresserId} screen #{ScreenId} cover #{CoverId}" +
        $" | created t{CreatedTick} until t{ExpiresTick} conf {Confidence01:F2}" +
        $" (pressure {Pressure01:F2}, line {Touchline01:F2}, cover {CoverFit01:F2}, screen {Screen01:F2})";
}

/// <summary>
/// What one defender's observation carries about the live trap he is part of: his role, the trap point and the
/// offsets the trap names, and how much confidence the team raised it with.
///
/// The offsets are relative to the carrier, not absolute points: a participant applies them to the carrier
/// position in <b>his own</b> picture, so the trap names an angle for a short window while each player still
/// acts on what he currently sees. The default is neutral (<see cref="Kind"/> none), so a fixture or standalone
/// controller that knows nothing about traps behaves exactly as it did before A8.4 — the rule every added
/// observation field in this project follows.
/// </summary>
public readonly record struct TrapContext(
    int TrapId,
    TrapKind Kind,
    TrapRole Role,
    int CarrierId,
    int PresserId,
    int ScreenId,
    int CoverId,
    Vector2 CarrierXZ,
    Vector2 PressOffsetXZ,
    Vector2 ScreenOffsetXZ,
    int CreatedTick,
    int ExpiresTick,
    float Confidence01)
{
    /// <summary>No live trap: the neutral picture of every pre-A8.4 fixture.</summary>
    public static TrapContext None => default;

    /// <summary>True when this player is part of a live trap.</summary>
    public bool IsActive => Kind != TrapKind.None && Role != TrapRole.None && TrapId != 0;

    /// <summary>True when this player is the trap's presser.</summary>
    public bool IsPresser => IsActive && Role == TrapRole.Presser;

    /// <summary>True when this player is the trap's screener.</summary>
    public bool IsScreener => IsActive && Role == TrapRole.Screen;

    /// <summary>True when this player is the trap's cover.</summary>
    public bool IsCover => IsActive && Role == TrapRole.Cover;

    /// <summary>The press point, from a carrier position the caller believes in.</summary>
    public Vector2 PressPoint(Vector2 carrierXZ) => carrierXZ + PressOffsetXZ;

    /// <summary>The screen point, from a carrier position the caller believes in.</summary>
    public Vector2 ScreenPoint(Vector2 carrierXZ) => carrierXZ + ScreenOffsetXZ;

    /// <summary>Compact text form for the inspector, a trace line or a failure message.</summary>
    public string Describe() =>
        IsActive
            ? $"{Kind} trap #{TrapId} as {Role} (carrier #{CarrierId}, press #{PresserId}, screen #{ScreenId}," +
              $" cover #{CoverId}) conf {Confidence01:F2} until t{ExpiresTick}"
            : "none";
}

/// <summary>
/// The match's bounded short-trap board (A8.4): at most one live trap per team, each with a fixed window and a
/// single stated ending.
///
/// <b>Ownership.</b> One instance lives on the match's <see cref="FootballSim.Input.MatchCommunicationContext"/> —
/// the same match-owned object that already carries the human's call, A5.3's run advertisements, A7.2's calls
/// and A8.1's contracts. This is the one team communication object extended, not a second channel. The trap is
/// elected from the team's shared inputs (the same pattern as A6.1's second man), so it reaches its
/// participants at once: the players are not being told a secret, they are being told a team decision they are
/// all entitled to compute — a per-player message delay here would be a second communication convention where
/// one already exists.
///
/// <b>Why a tiny table, not a pile of flags.</b> A decision needs one answer — "am I in a trap, and what does it
/// ask of me" — and the engine needs one answer — "is this team trapping, and why did it stop". One entry per
/// team gives both, cannot grow with the match, and cannot make the outcome depend on iteration order.
///
/// <b>Lifecycle.</b> A trap is raised when its picture is recognized (<see cref="TrapPolicy.TryEvaluate"/>), and
/// ends exactly once: with a won ball, an escape, a loose ball, an expiry, a lost presser or cover, a departed
/// participant or a passage reset. A failed (escaped) trap puts the team into a short recovery window before it
/// may be raised again — the side re-forms instead of re-committing to the same carrier on the next tick.
///
/// <b>Determinism.</b> Every window and recovery is measured in live ticks, every scan is list-ordered, ties
/// keep the first entry, and no RNG is touched: two matches with the same seed raise, keep and end exactly the
/// same traps.
/// </summary>
public sealed class TrapCoordinator
{
    /// <summary>Entries the board can hold: one live trap per team, plus slack for the transition window.</summary>
    public const int Capacity = 4;

    /// <summary>
    /// How long a trap stays live, in ticks (1.5 s). Short by design: the plan asks for <i>short</i> trap
    /// contracts, and a trap whose window outlives the picture it was built from is a state, not a commitment.
    /// </summary>
    public const int TrapDurationTicks = 90;

    /// <summary>
    /// Recovery window after an escaped trap, in ticks (0.5 s), before the team may raise another. This is the
    /// plan's "recovery behavior": the failed trap releases its players and the side re-forms before it
    /// re-commits, rather than re-raising the same trap on the very next tick.
    /// </summary>
    public const int RecoveryTicks = 30;

    private struct Entry
    {
        public bool Occupied;
        public TrapContract Contract;
    }

    private readonly Entry[] _entries = new Entry[Capacity];
    private readonly int[] _endedByReason = new int[Enum.GetValues(typeof(TrapEndReason)).Length];
    private readonly int[] _recoverUntilTick = new int[2];

    private int _nextTrapId = 1;
    private int _lastSweepTick = int.MinValue;

    /// <summary>Traps raised on this board since the match started (diagnostics only).</summary>
    public int RaisedCount { get; private set; }

    /// <summary>Raises skipped because the team was still recovering from an escaped trap (diagnostics only).</summary>
    public int RecoveryBlockedCount { get; private set; }

    /// <summary>Traps that ended for one reason since the match started.</summary>
    public int CountOfEnd(TrapEndReason reason)
    {
        int index = (int)reason;
        return index >= 0 && index < _endedByReason.Length ? _endedByReason[index] : 0;
    }

    /// <summary>
    /// Advances one team's trap: keeps or ends its live contract, or raises a new one when the shared picture
    /// recognizes a trap. Returns true when the team has a live trap after the call.
    ///
    /// The checks run in one documented order — possession, participant availability, escape geometry, role
    /// validity — so the ending a trap is attributed is deterministic when two facts change on the same tick.
    /// </summary>
    /// <param name="players">Every player in the match, read exactly for the team's own men.</param>
    /// <param name="opponents">The team's shared belief picture; the only opponent information used.</param>
    /// <param name="possession">Authoritative ball ownership, used to establish whose ball it is.</param>
    /// <param name="presserId">The match's elected ball responder, or null.</param>
    /// <param name="supportPresserId">The match's elected second man (A6.1), or null.</param>
    /// <param name="jobs">The allocator's assignments: the trap reads its cover and screen roles from them.</param>
    /// <param name="humanPlayerId">The locked human, never named in a trap.</param>
    /// <param name="attackDirX">The team's attacking direction.</param>
    /// <param name="compactness01">The team profile's compactness (cover depth).</param>
    /// <param name="pitch">Pitch geometry.</param>
    /// <param name="tick">The live tick.</param>
    /// <param name="enabled">False for the ablation build: nothing is raised and nothing is kept.</param>
    public bool Update(
        TeamId team,
        IReadOnlyList<FootballerState> players,
        TeamBelief opponents,
        BallPossessionSnapshot possession,
        int? presserId,
        int? supportPresserId,
        TeamJobCoordinator jobs,
        int? humanPlayerId,
        float attackDirX,
        float compactness01,
        PitchDimensions pitch,
        int tick,
        bool enabled)
    {
        Sweep(tick);

        int slot = FindLive(team, tick);
        if (slot >= 0)
        {
            TrapContract contract = _entries[slot].Contract;

            // 1. Possession. A trap never outlives the opponent possession it was built in: our own control is
            // the trap's success, a dead/loose ball is the trap ending without a winner, and a pass (in flight
            // while still theirs) means the carrier got the ball out.
            if (possession.PossessionTeam == team)
            {
                EndSlot(slot, TrapEndReason.Won);
                return false;
            }

            if (possession.PossessionTeam is not TeamId holder || holder == team ||
                possession.State is BallControlState.Loose or BallControlState.KeeperHeld)
            {
                EndSlot(slot, TrapEndReason.Loose);
                return false;
            }

            if (possession.State != BallControlState.Controlled)
            {
                // A planned pass while the opponent still owns the ball: the trapped carrier escaped by
                // releasing it, whoever ends up receiving it.
                EndSlot(slot, TrapEndReason.Escaped);
                Recover(team, tick);
                return false;
            }

            if (possession.ControllingPlayerId != contract.CarrierId)
            {
                // A different opponent has the ball: the carrier got it out to him.
                EndSlot(slot, TrapEndReason.Escaped);
                Recover(team, tick);
                return false;
            }

            // 2. The participants still exist and are still available.
            if (!IsActive(players, contract.PresserId) ||
                !IsActive(players, contract.ScreenId) ||
                !IsActive(players, contract.CoverId))
            {
                EndSlot(slot, TrapEndReason.ParticipantRemoved);
                return false;
            }

            // 3. The escape condition: the carrier is out of the trap region. Measured on the team's own belief,
            // because that is the picture the trap coordinates on; losing the picture entirely also ends it.
            if (!TryGetBelieved(opponents, contract.CarrierId, out TeamBelief.Opponent believedCarrier))
            {
                EndSlot(slot, TrapEndReason.Loose);
                return false;
            }

            if (TrapPolicy.IsEscaped(contract, believedCarrier.PositionXZ))
            {
                EndSlot(slot, TrapEndReason.Escaped);
                Recover(team, tick);
                return false;
            }

            // 4. The roles the trap was written around are still the roles the match allocates. A trap whose
            // presser or cover changed is no longer the contract the players agreed to.
            if (presserId is not int currentPresser || currentPresser != contract.PresserId)
            {
                EndSlot(slot, TrapEndReason.PresserLost);
                return false;
            }

            if (!IsCover(jobs, contract.CoverId))
            {
                EndSlot(slot, TrapEndReason.CoverLost);
                return false;
            }

            return true;
        }

        if (!enabled) return false;

        // Recovery: a failed trap is a team event. The side re-forms before it re-commits.
        if (tick < _recoverUntilTick[TeamIndex(team)])
        {
            RecoveryBlockedCount++;
            return false;
        }

        if (!TrapPolicy.TryEvaluate(team, players, opponents, possession, presserId, supportPresserId, jobs,
                humanPlayerId, attackDirX, compactness01, pitch, out TrapOpportunity opportunity))
        {
            return false;
        }

        Publish(team, opportunity, tick);
        return true;
    }

    /// <summary>What this player's own observation should carry: his part in the live trap he is entitled to know.</summary>
    public TrapContext ContextFor(TeamId team, int playerId, int tick)
    {
        if (playerId <= 0) return TrapContext.None;

        Sweep(tick);
        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied || entry.Contract.Team != team) continue;
            if (!entry.Contract.IsLiveAt(tick)) continue;

            TrapRole role = entry.Contract.RoleOf(playerId);
            if (role == TrapRole.None) continue;

            TrapContract contract = entry.Contract;
            return new TrapContext(
                contract.TrapId, contract.Kind, role, contract.CarrierId,
                contract.PresserId, contract.ScreenId, contract.CoverId,
                contract.CarrierXZ, contract.PressOffsetXZ, contract.ScreenOffsetXZ,
                contract.CreatedTick, contract.ExpiresTick, contract.Confidence01);
        }

        return TrapContext.None;
    }

    /// <summary>The live trap this team is running, if any (diagnostics and tests).</summary>
    public bool TryGetLive(TeamId team, int tick, out TrapContract contract)
    {
        Sweep(tick);
        int slot = FindLive(team, tick);
        if (slot < 0)
        {
            contract = default;
            return false;
        }

        contract = _entries[slot].Contract;
        return true;
    }

    /// <summary>True while this team is running a trap (diagnostics and tests).</summary>
    public bool HasLive(TeamId team, int tick) => TryGetLive(team, tick, out _);

    /// <summary>Number of live traps on the board.</summary>
    public int LiveCount(int tick)
    {
        Sweep(tick);
        int count = 0;
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i].Occupied && _entries[i].Contract.IsLiveAt(tick)) count++;
        }

        return count;
    }

    /// <summary>True while this team is inside the recovery window after an escaped trap (diagnostics and tests).</summary>
    public bool IsRecovering(TeamId team, int tick) => tick < _recoverUntilTick[TeamIndex(team)];

    /// <summary>
    /// A player left the pitch: every trap he was part of ends with him — an inactive player must not keep
    /// owning a role inside a contract.
    /// </summary>
    public void ReleasePlayer(int playerId, int tick)
    {
        if (playerId <= 0) return;
        Sweep(tick);

        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied || !entry.Contract.IsParticipant(playerId)) continue;
            EndSlot(i, TrapEndReason.ParticipantRemoved);
        }
    }

    /// <summary>Ends every live trap: a restart, the interval or full time ended the passage they belong to.</summary>
    public void ClearAll(int tick)
    {
        Sweep(tick);
        for (int i = 0; i < _entries.Length; i++)
        {
            if (!_entries[i].Occupied) continue;
            EndSlot(i, TrapEndReason.Reset);
        }

        _recoverUntilTick[0] = 0;
        _recoverUntilTick[1] = 0;
    }

    private void Publish(TeamId team, in TrapOpportunity opportunity, int tick)
    {
        int slot = FindFreeSlot();
        if (slot < 0) return;

        var contract = new TrapContract(
            TrapId: _nextTrapId++,
            Kind: opportunity.Kind,
            Team: team,
            CarrierId: opportunity.CarrierId,
            PresserId: opportunity.PresserId,
            ScreenId: opportunity.ScreenId,
            CoverId: opportunity.CoverId,
            CarrierXZ: opportunity.CarrierXZ,
            PressOffsetXZ: opportunity.PressOffsetXZ,
            ScreenOffsetXZ: opportunity.ScreenOffsetXZ,
            CreatedTick: tick,
            ExpiresTick: tick + TrapDurationTicks,
            Confidence01: Math.Clamp(opportunity.Confidence01, 0f, 1f),
            Pressure01: Math.Clamp(opportunity.Pressure01, 0f, 1f),
            Touchline01: Math.Clamp(opportunity.Touchline01, 0f, 1f),
            CoverFit01: Math.Clamp(opportunity.CoverFit01, 0f, 1f),
            Screen01: Math.Clamp(opportunity.Screen01, 0f, 1f));

        _entries[slot] = new Entry { Occupied = true, Contract = contract };
        RaisedCount++;
    }

    private void Recover(TeamId team, int tick)
    {
        int index = TeamIndex(team);
        int until = tick + RecoveryTicks;
        _recoverUntilTick[index] = until > _recoverUntilTick[index] ? until : _recoverUntilTick[index];
    }

    private void EndSlot(int slot, TrapEndReason reason)
    {
        if (slot < 0 || !_entries[slot].Occupied) return;

        _entries[slot] = default;
        _endedByReason[(int)reason]++;
    }

    /// <summary>Expired entries are counted once and freed; nothing remembers a trap past its expiry.</summary>
    private void Sweep(int tick)
    {
        if (tick <= _lastSweepTick) return;
        _lastSweepTick = tick;

        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied) continue;
            if (entry.Contract.IsLiveAt(tick)) continue;
            EndSlot(i, TrapEndReason.Expired);
        }
    }

    private int FindLive(TeamId team, int tick)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied || entry.Contract.Team != team) continue;
            if (!entry.Contract.IsLiveAt(tick)) continue;
            return i;
        }

        return -1;
    }

    private int FindFreeSlot()
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (!_entries[i].Occupied) return i;
        }

        return -1;
    }

    private static bool IsActive(IReadOnlyList<FootballerState> players, int playerId)
    {
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].Id != playerId) continue;
            return players[i].IsActiveOnPitch;
        }

        return false;
    }

    /// <summary>True when the allocator still has this player as its cover behind the press (not a temporary duty).</summary>
    private static bool IsCover(TeamJobCoordinator jobs, int playerId) =>
        jobs.Get(playerId) is { Job: TacticalJob.CoverDefender, IsTemporary: false };

    private static bool TryGetBelieved(
        TeamBelief opponents, int playerId, out TeamBelief.Opponent believed)
    {
        IReadOnlyList<TeamBelief.Opponent> list = opponents.Opponents;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].PlayerId != playerId) continue;
            believed = list[i];
            return true;
        }

        believed = default;
        return false;
    }

    private static int TeamIndex(TeamId team) => team == TeamId.Home ? 0 : 1;
}
