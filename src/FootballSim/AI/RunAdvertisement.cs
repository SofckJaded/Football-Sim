using System;
using System.Numerics;
using FootballSim.Core;

namespace FootballSim.AI;

/// <summary>
/// One team-mate's announced run, as the rest of the team can hear it (A5.3).
///
/// Before this, a run was private to the runner: he had a frozen meeting region, and the carrier priced
/// the same ball blind — a lead computed from the receiver's current velocity plus a fixed push into
/// space, charged by <see cref="PassSuccessModel.LeadRisk01"/> because nothing had promised the run would
/// continue. The advertisement is that promise, in the form the carrier actually needs it: <b>where</b>
/// (<see cref="MeetXZ"/> and <see cref="MeetRadiusMeters"/>), <b>when</b> (<see cref="MeetTick"/>), and
/// <b>how sure</b> the runner is (<see cref="Confidence01"/>).
///
/// It is information, not truth. It carries no opponent position, no ball trajectory and no live
/// attribute — only the runner's own intention, which he is entitled to tell his team. The board applies
/// the communication delay, so a carrier acts on a shout that has had time to reach him.
/// </summary>
public readonly record struct RunAdvertisement(
    int PlayerId,
    RunKind Kind,
    Vector2 MeetXZ,
    float MeetRadiusMeters,
    int PublishedTick,
    int VisibleTick,
    int MeetTick,
    int ExpiresTick,
    float Confidence01)
{
    /// <summary>True once the shout has had time to reach a team-mate.</summary>
    public bool IsVisibleAt(int tick) => tick >= VisibleTick;

    /// <summary>True while the run's own window is open (the promise has not expired).</summary>
    public bool IsLiveAt(int tick) => tick <= ExpiresTick;

    /// <summary>True while this promise may be played to: both visible and unexpired.</summary>
    public bool IsUsableAt(int tick) => IsVisibleAt(tick) && IsLiveAt(tick);

    /// <summary>Ticks from now until the runner plans to be in the region; never negative.</summary>
    public int TicksUntilMeeting(int tick) => Math.Max(0, MeetTick - tick);

    /// <summary>
    /// How much of the promise a carrier may still believe at <paramref name="tick"/>: the runner's own
    /// confidence before the meeting, decaying through the grace window afterwards. A ball delivered after
    /// the planned arrival is a ball to where the runner *was*, so the belief falls away with the window
    /// instead of ending abruptly.
    /// </summary>
    public float Promise01(int tick)
    {
        if (!IsUsableAt(tick)) return 0f;

        float confidence = float.IsFinite(Confidence01) ? Math.Clamp(Confidence01, 0f, 1f) : 0f;
        if (confidence <= 0f) return 0f;
        if (tick <= MeetTick) return confidence;

        float grace = Math.Max(1, ExpiresTick - MeetTick);
        float remaining = 1f - (tick - MeetTick) / grace;
        return confidence * Math.Clamp(remaining, 0f, 1f);
    }

    /// <summary>Compact text form for a trace line or a failure message.</summary>
    public string Describe() =>
        $"{Kind} #{PlayerId} →({MeetXZ.X:F1},{MeetXZ.Y:F1}) r{MeetRadiusMeters:F1} " +
        $"meet t{MeetTick} from t{PublishedTick} (visible t{VisibleTick}) conf {Confidence01:F2}";
}

/// <summary>
/// A team-mate has played the ball into an advertised region (A5.3). The claim is what stops a runner
/// from dropping a run two ticks before the ball lands: while it is live he holds the region, and his
/// window is extended to the ball's own arrival instead of expiring underneath it.
///
/// The claim is a decision, not a contact — the execution layer may still refuse the kick — so it expires
/// on its own (<see cref="RunAdvertisementBoard.ClaimGraceTicks"/> after the arrival it names). A runner
/// who is never actually passed to is free again within a second and a half.
/// </summary>
public readonly record struct RunClaim(
    int RunnerId,
    int CarrierId,
    int ClaimedTick,
    int ArriveTick,
    int ExpiresTick)
{
    /// <summary>True while the ball this claim announced may still be on its way.</summary>
    public bool IsLiveAt(int tick) => tick <= ExpiresTick;

    /// <summary>Compact text form for a trace line or a failure message.</summary>
    public string Describe() =>
        $"claim #{RunnerId} from #{CarrierId} arrive t{ArriveTick} (t{ClaimedTick}..t{ExpiresTick})";
}

/// <summary>
/// The per-team advertisement board (A5.3): who is running where, arriving when, with what confidence —
/// visible to team-mates after a communication delay.
///
/// <b>Why a board and not a message per pair.</b> The carrier needs an answer for every possible receiver
/// on every decision; a queue of messages would have to be drained, ordered and interpreted. A small
/// fixed table indexed by player id has one answer per runner, is read in the same tick it is needed, and
/// cannot grow.
///
/// <b>Why the delay is ticks.</b> The plan asks for communication delay, and a delay expressed in engine
/// ticks is the only kind that survives a pause or a replay. It also makes the board safe for a
/// deterministic decision loop: a player who decides later in the same tick cannot see a shout raised
/// earlier in it, so the order the controllers happen to be iterated in cannot change the outcome.
///
/// <b>Ownership.</b> One board lives on the match's <c>MatchCommunicationContext</c> — the same object
/// that already carries the human's call for the pass — so there is one communication channel, not two.
/// The human controller does not publish runs (a locked human decides his own movement); the board's read
/// side is AI-only today, which is recorded in <c>docs/KNOWN_ISSUES.md</c>.
/// </summary>
public sealed class RunAdvertisementBoard
{
    /// <summary>
    /// Entries the board can hold. Two teams of eleven outfield players plus slack: nobody else can
    /// advertise a run, because a run is adopted only by a player on a receiving duty.
    /// </summary>
    public const int Capacity = 24;

    /// <summary>
    /// How long a shout takes to reach a team-mate, in live ticks (0.30 s at 60 Hz). Long enough to be a
    /// real communication delay — a carrier reacts to where the runner said he would be, not to where he
    /// was at the instant of the decision — and short enough that a run's own window (about two seconds)
    /// still mostly lies ahead when the carrier hears it.
    /// </summary>
    public const int CommunicationDelayTicks = 18;

    /// <summary>
    /// How long a runner stays committed after the ball he was promised should have arrived, in ticks
    /// (1.5 s). This is the claim's own bound: if the kick was refused or the ball was cut out, the runner
    /// is free again rather than standing in a region waiting for a pass that no longer exists.
    /// </summary>
    public const int ClaimGraceTicks = 90;

    private struct Entry
    {
        public bool Occupied;
        public TeamId Team;
        public RunAdvertisement Advert;
        public bool HasClaim;
        public RunClaim Claim;
    }

    private readonly Entry[] _entries = new Entry[Capacity];

    /// <summary>Advertisements published on this board since the match started (diagnostics).</summary>
    public int PublishedCount { get; private set; }

    /// <summary>Advertisements withdrawn because the runner stopped running (diagnostics).</summary>
    public int WithdrawnCount { get; private set; }

    /// <summary>Claims raised by a carrier playing into an announced region (diagnostics).</summary>
    public int ClaimedCount { get; private set; }

    /// <summary>
    /// Publishes (or renews) one runner's promise. Renewing the *same* promise keeps its original publish
    /// tick, so re-deciding the run every tick cannot restart the communication delay and hide the run for
    /// ever. A different meeting region is a different promise and starts the delay again.
    /// </summary>
    public void Publish(TeamId team, int playerId, in RunIntent intent, int tick)
    {
        if (playerId <= 0) return;
        if (!SimMath.IsFinite(intent.Path.MeetXZ)) return;

        int slot = Find(team, playerId);
        if (slot >= 0 && IsSamePromise(_entries[slot].Advert, intent))
        {
            // Same region, same run: renew in place. The window and the confidence are current — the
            // runner re-measures how sure he is of the space every tick (A5.3) — but the publication tick
            // stays, so re-deciding the run cannot restart the communication delay.
            Entry renewed = _entries[slot];
            renewed.Advert = renewed.Advert with
            {
                ExpiresTick = intent.Path.ExpiresTick,
                MeetTick = intent.Path.MeetTick,
                MeetRadiusMeters = intent.Path.MeetRadiusMeters,
                Confidence01 = intent.Confidence01
            };
            _entries[slot] = renewed;
            return;
        }

        if (slot < 0) slot = TakeSlot(team, tick);
        if (slot < 0) return;

        _entries[slot] = new Entry
        {
            Occupied = true,
            Team = team,
            Advert = new RunAdvertisement(
                PlayerId: playerId,
                Kind: intent.Kind,
                MeetXZ: intent.Path.MeetXZ,
                MeetRadiusMeters: intent.Path.MeetRadiusMeters,
                PublishedTick: tick,
                VisibleTick: tick + CommunicationDelayTicks,
                MeetTick: intent.Path.MeetTick,
                ExpiresTick: intent.Path.ExpiresTick,
                Confidence01: intent.Confidence01),
            // A new promise drops the claim: the ball was announced for the previous region.
            HasClaim = false,
            Claim = default
        };
        PublishedCount++;
    }

    /// <summary>Withdraws one runner's promise: he is no longer running there.</summary>
    public void Withdraw(TeamId team, int playerId)
    {
        int slot = Find(team, playerId);
        if (slot < 0) return;

        _entries[slot] = default;
        WithdrawnCount++;
    }

    /// <summary>
    /// The promise a carrier can act on for this receiver: visible, live, and published by the same team.
    /// Returns false when there is nothing to hear.
    /// </summary>
    public bool TryGetAdvertisement(TeamId team, int playerId, int tick, out RunAdvertisement advert)
    {
        int slot = Find(team, playerId);
        if (slot < 0)
        {
            advert = default;
            return false;
        }

        advert = _entries[slot].Advert;
        return advert.IsUsableAt(tick);
    }

    /// <summary>Number of promises this team can currently hear and still play to.</summary>
    public int VisibleCount(TeamId team, int tick)
    {
        int count = 0;
        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied || entry.Team != team) continue;
            if (entry.Advert.IsUsableAt(tick)) count++;
        }

        return count;
    }

    /// <summary>
    /// The <paramref name="index"/>-th promise this team can hear, in publication order. Together with
    /// <see cref="VisibleCount"/> this is how a diagnostic or a test reads the board without an allocation.
    /// </summary>
    public bool TryGetVisibleAt(TeamId team, int index, int tick, out RunAdvertisement advert)
    {
        int seen = 0;
        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied || entry.Team != team) continue;
            if (!entry.Advert.IsUsableAt(tick)) continue;
            if (seen++ != index) continue;

            advert = entry.Advert;
            return true;
        }

        advert = default;
        return false;
    }

    /// <summary>
    /// Records that a team-mate has played the ball into this runner's advertised region. Only an existing
    /// promise can be claimed: there is nothing to be committed to if the board never heard the run.
    /// </summary>
    public void Claim(TeamId team, int runnerId, int carrierId, int arriveTick, int tick)
    {
        int slot = Find(team, runnerId);
        if (slot < 0) return;

        Entry entry = _entries[slot];
        int arrival = Math.Max(tick, arriveTick);
        int expires = arrival + ClaimGraceTicks;

        // Re-issuing the same claim must not extend it every tick the carrier holds the pass as his chosen
        // action; the claim names a ball, and it keeps the deadline of the first time that ball was played.
        if (entry.HasClaim && entry.Claim.CarrierId == carrierId && entry.Claim.IsLiveAt(tick))
        {
            if (expires > entry.Claim.ExpiresTick)
            {
                entry.Claim = entry.Claim with { ExpiresTick = expires };
                _entries[slot] = entry;
            }

            return;
        }

        entry.HasClaim = true;
        entry.Claim = new RunClaim(runnerId, carrierId, tick, arrival, expires);
        _entries[slot] = entry;
        ClaimedCount++;
    }

    /// <summary>The live claim on this runner's run, if a team-mate has announced a ball to him.</summary>
    public bool TryGetClaim(TeamId team, int runnerId, int tick, out RunClaim claim)
    {
        int slot = Find(team, runnerId);
        if (slot < 0 || !_entries[slot].HasClaim)
        {
            claim = default;
            return false;
        }

        claim = _entries[slot].Claim;
        return claim.IsLiveAt(tick);
    }

    /// <summary>Drops everything this team has advertised (diagnostics, phase fixtures, tests).</summary>
    public void Clear(TeamId team)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i].Occupied && _entries[i].Team == team) _entries[i] = default;
        }
    }

    /// <summary>A promise is the same while it means the same place and the same kind of run.</summary>
    private static bool IsSamePromise(in RunAdvertisement advert, in RunIntent intent) =>
        advert.Kind == intent.Kind &&
        Vector2.DistanceSquared(advert.MeetXZ, intent.Path.MeetXZ) < 1e-4f;

    private int Find(TeamId team, int playerId)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (!_entries[i].Occupied) continue;
            if (_entries[i].Team == team && _entries[i].Advert.PlayerId == playerId) return i;
        }

        return -1;
    }

    /// <summary>
    /// A free slot, or the slot holding this team's most nearly expired promise. Eviction is by the earliest
    /// <see cref="RunAdvertisement.ExpiresTick"/> with the lowest index as the tie-break, so a full board
    /// degrades deterministically instead of depending on the order a dictionary happened to iterate in.
    /// </summary>
    private int TakeSlot(TeamId team, int tick)
    {
        int fallback = -1;
        int fallbackExpiry = int.MaxValue;

        for (int i = 0; i < _entries.Length; i++)
        {
            if (!_entries[i].Occupied) return i;

            Entry entry = _entries[i];
            if (entry.Team == team) continue;
            if (entry.Advert.IsLiveAt(tick)) continue;          // another team's live run: keep it
            if (entry.Advert.ExpiresTick >= fallbackExpiry) continue;

            fallback = i;
            fallbackExpiry = entry.Advert.ExpiresTick;
        }

        if (fallback >= 0) return fallback;

        // Still nothing free: overwrite this team's own most nearly expired promise rather than drop the
        // shout entirely. With two elevens and Capacity 24 this is a safety net, not the normal path.
        for (int i = 0; i < _entries.Length; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Occupied || entry.Team != team) continue;
            if (entry.Advert.ExpiresTick >= fallbackExpiry) continue;

            fallback = i;
            fallbackExpiry = entry.Advert.ExpiresTick;
        }

        return fallback;
    }
}
