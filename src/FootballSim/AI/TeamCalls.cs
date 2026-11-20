using System;
using FootballSim.Core;

namespace FootballSim.AI;

/// <summary>
/// The seven team calls A7.2 adds (the plan's "man-on, turn, step, drop, cover, switch, and run").
///
/// A call is a <b>delayed, expiring instruction from one player to his own team</b>. It is not a second
/// perception channel: the caller may only say what he himself observed, the call carries no opponent
/// position or velocity, and the receiver's decision changes through a preference or through the belief
/// that somebody has cover — never through a live read of the world. It is team-scoped (an opponent never
/// hears it) and bounded (a fixed board, a deterministic eviction, one shout per caller per call while it
/// is live).
/// </summary>
public enum TeamCallKind
{
    None = 0,

    /// <summary>"Man on" — the caller sees an opponent close to a team-mate who has the ball.</summary>
    ManOn,

    /// <summary>"Turn" — the caller sees the ball carrier with no opponent near him: he has time to turn.</summary>
    Turn,

    /// <summary>"Switch" — the caller is a far-side, unmarked team-mate asking for the ball to be switched.</summary>
    Switch,

    /// <summary>"Run" — the ball carrier asks a team-mate to make a forward run.</summary>
    Run,

    /// <summary>"Cover" — the elected second defender tells the presser he is behind him.</summary>
    Cover,

    /// <summary>"Step" — the line caller asks the defensive line to step up (the carrier is not advancing).</summary>
    Step,

    /// <summary>"Drop" — the line caller warns that an opponent is running in behind and asks the line back.</summary>
    Drop,
}

/// <summary>
/// One call on a team's board, after the communication delay has been applied.
///
/// <b>Why it carries no position.</b> A7.2's rule is "calls update belief or preference, not exact hidden
/// world state". The struct has no opponent position, velocity or intent to transmit, so a call can only
/// ever say <i>which</i> instruction was shouted, by whom, at whom, and how strongly — the receiver acts
/// on his own perception plus that instruction. That is what keeps the knowledge boundary (AGENTS §4.2)
/// intact: a call cannot become a second, faster sight line.
/// </summary>
public readonly record struct TeamCall(
    TeamCallKind Kind,
    int CallerId,
    int TargetPlayerId,
    int NamedPlayerId,
    float Strength01,
    int PublishedTick,
    int VisibleTick,
    int ExpiresTick)
{
    /// <summary>True once the shout has had time to reach the team-mate (A5.3's 0.3 s, same channel).</summary>
    public bool IsVisibleAt(int tick) => tick >= VisibleTick;

    /// <summary>True while the instruction is still current; an expired call is not heard again.</summary>
    public bool IsLiveAt(int tick) => tick <= ExpiresTick;

    /// <summary>True while this call may change anything: both heard and still current.</summary>
    public bool IsUsableAt(int tick) => IsVisibleAt(tick) && IsLiveAt(tick);

    /// <summary>Ticks this call still has to run at <paramref name="tick"/>, never negative.</summary>
    public int RemainingTicks(int tick) => Math.Max(0, ExpiresTick - tick);

    /// <summary>Compact text form for a trace line or a failure message.</summary>
    public string Describe() =>
        $"{Kind} #{CallerId}→#{TargetPlayerId}" +
        (NamedPlayerId > 0 ? $" (names #{NamedPlayerId})" : string.Empty) +
        $" str {Strength01:F2} t{PublishedTick} (heard t{VisibleTick}, until t{ExpiresTick})";
}

/// <summary>
/// What one player hears from his own team this tick: at most one call addressed to him and at most one
/// team-wide line instruction. Both default to <see cref="TeamCallKind.None"/>, so an observation built by
/// a fixture or a standalone controller behaves exactly as it did before A7.2 — the default is neutral.
///
/// This is the whole read side of A7.2. It is put on <see cref="FootballerObservation"/> rather than read
/// from the match object inside the scorers for two reasons: the knowledge boundary stays in one place
/// (everything a decision may use arrives through the observation), and a test can build two observations
/// that differ only in one call and prove the call is the only difference.
/// </summary>
public readonly record struct TeamCallContext(
    TeamCallKind Kind,
    int CallerId,
    int NamedPlayerId,
    TeamCallKind LineDirective)
{
    /// <summary>No call heard: the neutral picture of every pre-A7.2 fixture.</summary>
    public static TeamCallContext None => default;

    /// <summary>True when a player-addressed call (man-on, turn, switch, run, cover) was heard.</summary>
    public bool HasPlayerCall => Kind != TeamCallKind.None;

    /// <summary>True when a team-wide defensive instruction (step, drop) was heard.</summary>
    public bool HasLineDirective => LineDirective != TeamCallKind.None;

    /// <summary>True when every field is neutral, i.e. the context cannot change a decision.</summary>
    public bool IsNeutral => !HasPlayerCall && !HasLineDirective;

    /// <summary>A context carrying one player call and no team directive.</summary>
    public static TeamCallContext ForPlayer(in TeamCall call) =>
        new(call.Kind, call.CallerId, call.NamedPlayerId, TeamCallKind.None);

    /// <summary>A context carrying one team directive and no player call.</summary>
    public static TeamCallContext ForLine(TeamCallKind directive) =>
        new(TeamCallKind.None, 0, 0, directive);

    /// <summary>This context plus a team directive (the engine merges the two reads).</summary>
    public TeamCallContext WithLineDirective(TeamCallKind directive) =>
        this with { LineDirective = directive };

    /// <summary>Compact text form for the inspector and trace lines.</summary>
    public string Describe()
    {
        if (IsNeutral) return "none";

        string player = HasPlayerCall
            ? $"{Kind}{(NamedPlayerId > 0 ? $"→#{NamedPlayerId}" : string.Empty)} from #{CallerId}"
            : "no player call";
        return HasLineDirective ? $"{player}, line {LineDirective}" : player;
    }
}

/// <summary>
/// The per-team call board (A7.2): a small fixed table of the shouts a team has made, each with a
/// communication delay and an expiry.
///
/// <b>Why a table, not a message queue.</b> A decision needs one answer — "which instruction is addressed
/// to me, and is any team-wide instruction in force" — not a transcript. A fixed table has one answer per
/// (call kind, target), cannot grow, and cannot make the outcome depend on the order messages were drained
/// in. The same reasoning as A5.3's run board, deliberately reused rather than invented again.
///
/// <b>Determinism.</b> The delay is expressed in live ticks (never seconds of wall clock), every scan is
/// index-ordered, and eviction is by "expired first, then earliest expiry, then lowest index". Two matches
/// with the same seed therefore raise, hear and forget exactly the same calls.
/// </summary>
public sealed class TeamCallBoard
{
    /// <summary>
    /// Entries the board can hold per match. Every player raises at most one shout per call cycle, most
    /// shouts target the one carrier, and the board holds both teams, so 32 is a hard ceiling with slack:
    /// it is a bound that a broken producer cannot exceed, not a number the normal path approaches.
    /// </summary>
    public const int Capacity = 32;

    private struct Entry
    {
        public bool Occupied;
        public TeamId Team;
        public TeamCall Call;
    }

    private readonly Entry[] _entries = new Entry[Capacity];
    private int _lastSweepTick = int.MinValue;

    /// <summary>Calls accepted on this board since the match started (diagnostics only).</summary>
    public int PublishedCount { get; private set; }

    /// <summary>Repeated shouts suppressed while an identical one was still live (diagnostics only).</summary>
    public int SuppressedCount { get; private set; }

    /// <summary>Calls that expired and were swept off the board (diagnostics only).</summary>
    public int ExpiredCount { get; private set; }

    /// <summary>Live calls evicted to make room for a new one (diagnostics only).</summary>
    public int EvictedCount { get; private set; }

    private readonly int[] _kindCounts = new int[Enum.GetValues(typeof(TeamCallKind)).Length];

    /// <summary>Lifetime count of accepted calls of one kind, for the evidence probe.</summary>
    public int CountOf(TeamCallKind kind)
    {
        int index = (int)kind;
        return index >= 0 && index < _kindCounts.Length ? _kindCounts[index] : 0;
    }

    /// <summary>
    /// Publishes one shout. Returns false when an identical call (same team, caller, kind and target) is
    /// still live: a call is an <b>event</b>, not a state re-asserted every tick, so re-deciding a call
    /// must not restart its delay forever (the run board's renewal lesson, applied to calls).
    ///
    /// <paramref name="delayTicks"/> is the communication delay; the call is usable for
    /// <paramref name="lifetimeTicks"/> ticks after it is first heard.
    /// </summary>
    public bool Publish(
        TeamId team,
        TeamCallKind kind,
        int callerId,
        int targetPlayerId,
        int namedPlayerId,
        float strength01,
        int publishedTick,
        int delayTicks,
        int lifetimeTicks)
    {
        if (kind == TeamCallKind.None || callerId <= 0) return false;
        if (targetPlayerId < 0 || namedPlayerId < 0) return false;
        if (targetPlayerId == callerId && targetPlayerId != 0) return false;
        if (namedPlayerId == callerId && kind != TeamCallKind.Switch) return false;
        if (!float.IsFinite(strength01)) return false;

        int delay = Math.Max(0, delayTicks);
        int lifetime = Math.Max(1, lifetimeTicks);
        int visibleTick = publishedTick + delay;

        Sweep(publishedTick);

        int existing = Find(team, kind, callerId, targetPlayerId);
        if (existing >= 0 && _entries[existing].Call.IsLiveAt(publishedTick))
        {
            SuppressedCount++;
            return false;
        }

        int slot = existing >= 0 ? existing : TakeSlot(team, publishedTick);
        if (slot < 0)
        {
            EvictedCount++;
            slot = EvictOne(team, publishedTick);
            if (slot < 0) return false;
        }

        _entries[slot] = new Entry
        {
            Occupied = true,
            Team = team,
            Call = new TeamCall(
                kind, callerId, targetPlayerId, namedPlayerId,
                Clamp01(strength01), publishedTick, visibleTick, visibleTick + lifetime)
        };
        PublishedCount++;
        _kindCounts[(int)kind]++;
        return true;
    }

    /// <summary>
    /// The instruction addressed to this player at <paramref name="tick"/>: the highest-priority live call
    /// among those aimed at him, ties broken by strength, then by the earlier visible tick, then by the
    /// lower slot. Returns false when he has heard nothing.
    /// </summary>
    public bool TryGetPlayerCall(TeamId team, int playerId, int tick, out TeamCall call)
    {
        if (playerId <= 0)
        {
            call = default;
            return false;
        }

        Sweep(tick);
        int best = -1;
        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied || entry.Team != team) continue;
            if (entry.Call.TargetPlayerId != playerId) continue;
            if (!IsPlayerKind(entry.Call.Kind) || !entry.Call.IsUsableAt(tick)) continue;
            if (best < 0 || Beats(entry.Call, _entries[best].Call)) best = i;
        }

        if (best < 0)
        {
            call = default;
            return false;
        }

        call = _entries[best].Call;
        return true;
    }

    /// <summary>
    /// The team-wide defensive instruction in force at <paramref name="tick"/>, or false. Drop outranks
    /// Step: when both are live the line must not step up into a ball played in behind.
    /// </summary>
    public bool TryGetLineDirective(TeamId team, int tick, out TeamCall call)
    {
        Sweep(tick);
        int best = -1;
        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied || entry.Team != team) continue;
            if (entry.Call.TargetPlayerId != 0 || !IsLineKind(entry.Call.Kind)) continue;
            if (!entry.Call.IsUsableAt(tick)) continue;
            if (best < 0 || LinePriority(entry.Call.Kind) > LinePriority(_entries[best].Call.Kind) ||
                (LinePriority(entry.Call.Kind) == LinePriority(_entries[best].Call.Kind) &&
                 entry.Call.Strength01 > _entries[best].Call.Strength01))
            {
                best = i;
            }
        }

        if (best < 0)
        {
            call = default;
            return false;
        }

        call = _entries[best].Call;
        return true;
    }

    /// <summary>Everything this player can hear in one read: his own call plus the team directive.</summary>
    public TeamCallContext BuildContext(TeamId team, int playerId, int tick)
    {
        var context = TeamCallContext.None;
        if (TryGetPlayerCall(team, playerId, tick, out TeamCall personal))
            context = TeamCallContext.ForPlayer(personal);
        if (TryGetLineDirective(team, tick, out TeamCall line))
            context = context.WithLineDirective(line.Kind);
        return context;
    }

    /// <summary>Number of live calls this team can currently hear, for diagnostics and tests.</summary>
    public int VisibleCount(TeamId team, int tick)
    {
        Sweep(tick);
        int count = 0;
        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (entry.Occupied && entry.Team == team && entry.Call.IsUsableAt(tick)) count++;
        }

        return count;
    }

    /// <summary>Drops everything this team has shouted (a passage ended; diagnostics and tests).</summary>
    public void Clear(TeamId team)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i].Occupied && _entries[i].Team == team) _entries[i] = default;
        }
    }

    /// <summary>Drops both teams' calls (half time and full time, where direction reverses).</summary>
    public void ClearAll()
    {
        for (int i = 0; i < _entries.Length; i++) _entries[i] = default;
    }

    /// <summary>True while a call of this shape is live, without changing the read path.</summary>
    public bool HasLiveCall(TeamId team, TeamCallKind kind, int tick)
    {
        Sweep(tick);
        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (entry.Occupied && entry.Team == team && entry.Call.Kind == kind && entry.Call.IsLiveAt(tick))
                return true;
        }

        return false;
    }

    private static bool IsPlayerKind(TeamCallKind kind) => kind is TeamCallKind.ManOn or TeamCallKind.Turn
        or TeamCallKind.Switch or TeamCallKind.Run or TeamCallKind.Cover;

    private static bool IsLineKind(TeamCallKind kind) =>
        kind is TeamCallKind.Step or TeamCallKind.Drop;

    private static int PlayerPriority(TeamCallKind kind) => kind switch
    {
        TeamCallKind.ManOn => 5,
        TeamCallKind.Cover => 4,
        TeamCallKind.Switch => 3,
        TeamCallKind.Turn => 2,
        TeamCallKind.Run => 1,
        _ => 0
    };

    private static int LinePriority(TeamCallKind kind) => kind switch
    {
        TeamCallKind.Drop => 2,
        TeamCallKind.Step => 1,
        _ => 0
    };

    /// <summary>Reading order for two calls aimed at the same player: priority, then strength, then age.</summary>
    private static bool Beats(in TeamCall candidate, in TeamCall current)
    {
        int candidatePriority = PlayerPriority(candidate.Kind);
        int currentPriority = PlayerPriority(current.Kind);
        if (candidatePriority != currentPriority) return candidatePriority > currentPriority;
        if (MathF.Abs(candidate.Strength01 - current.Strength01) > 1e-4f)
            return candidate.Strength01 > current.Strength01;
        return candidate.VisibleTick < current.VisibleTick;
    }

    /// <summary>Expired entries are counted once and freed; nothing remembers a call past its expiry.</summary>
    private void Sweep(int tick)
    {
        if (tick <= _lastSweepTick) return;
        _lastSweepTick = tick;

        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied) continue;
            if (entry.Call.IsLiveAt(tick)) continue;
            _entries[i] = default;
            ExpiredCount++;
        }
    }

    private int Find(TeamId team, TeamCallKind kind, int callerId, int targetPlayerId)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied || entry.Team != team) continue;
            if (entry.Call.Kind != kind || entry.Call.CallerId != callerId) continue;
            if (entry.Call.TargetPlayerId != targetPlayerId) continue;
            return i;
        }

        return -1;
    }

    /// <summary>
    /// A free slot, or the first entry that has expired (the sweep just ran, so a live board can only
    /// reach the eviction path under a pathological producer). Deterministic: index order, no dictionary.
    /// </summary>
    private int TakeSlot(TeamId team, int tick)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (!_entries[i].Occupied) return i;
        }

        return -1;
    }

    /// <summary>
    /// Last-resort replacement: the live entry with the earliest expiry, preferring the other team's
    /// (a team's own shouts are the ones it is acting on). Tie-break is the lowest index, so the outcome
    /// never depends on iteration order beyond the fixed table order.
    /// </summary>
    private int EvictOne(TeamId team, int tick)
    {
        int best = -1;
        int bestExpiry = int.MaxValue;
        bool bestOtherTeam = false;

        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied) return i;
            bool otherTeam = entry.Team != team;
            int expiry = entry.Call.ExpiresTick;
            bool better = otherTeam && !bestOtherTeam ||
                          (otherTeam == bestOtherTeam && expiry < bestExpiry);
            if (!better) continue;
            best = i;
            bestExpiry = expiry;
            bestOtherTeam = otherTeam;
        }

        if (best >= 0) _entries[best] = default;
        return best;
    }

    private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;
}
