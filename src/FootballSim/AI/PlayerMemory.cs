using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;

namespace FootballSim.AI;

/// <summary>
/// What one player believes about another: the last moment they actually saw them, and the state they
/// saw (A2.2).
///
/// This deliberately stores the *observation*, not a reference to the living player. The whole point of
/// the knowledge boundary is that a player who has not been seen cannot be refreshed from the
/// authoritative world; a belief that could read through to the real position would make every later
/// belief test meaningless.
/// </summary>
public sealed class PlayerBelief
{
    internal PlayerBelief(int playerId) => PlayerId = playerId;

    public int PlayerId { get; }
    public TeamId Team { get; internal set; }
    public TacticalRole Role { get; internal set; }

    /// <summary>Engine tick of the last real observation, or −1 when this player has never been seen.</summary>
    public int LastObservedTick { get; internal set; } = -1;

    /// <summary>Position as it was when last observed (noise included). Never the live position.</summary>
    public Vector3 Position { get; internal set; }

    /// <summary>Velocity as it was when last observed.</summary>
    public Vector3 Velocity { get; internal set; }

    /// <summary>Confidence of the last real observation, before any ageing decay.</summary>
    public float Confidence01 { get; internal set; }

    /// <summary>True once this player has been observed at least once.</summary>
    public bool IsKnown => LastObservedTick >= 0;

    /// <summary>Ticks since this player was last seen; 0 for an observation made this tick.</summary>
    public int AgeTicks(int nowTick) => nowTick > LastObservedTick && LastObservedTick >= 0
        ? nowTick - LastObservedTick
        : 0;

    /// <summary>Seconds since this player was last seen.</summary>
    public float AgeSeconds(int nowTick) => AgeTicks(nowTick) * SimConstants.FixedDeltaTime;
}

/// <summary>
/// One player's persistent memory of the players they have seen (A2.2).
///
/// Before this existed, <see cref="PerceptionSystem"/> put <b>every active opponent</b> into every
/// observation on every tick, including opponents behind the player, with position noise but no age.
/// A defender was therefore permanently omniscient about a striker's live run, and lowering a
/// confidence number would not have changed that — the estimate still tracked truth tick by tick.
///
/// Here an unseen entity keeps a <i>predicted</i> position: last seen position plus last seen velocity,
/// damped so a stale guess does not extrapolate across the pitch, with uncertainty that grows with the
/// age of the observation and confidence that decays. When the player sees them again, the belief is
/// refreshed from the new observation. Nothing in this type can read a live position.
///
/// The decay curves are rational rather than exponential on purpose: an exponential per entity per
/// observer per tick is real work for a curve shape nobody can perceive at these ages.
/// </summary>
public sealed class PlayerMemory
{
    /// <summary>Age at which extrapolated displacement is halved: a player out of sight may have stopped.</summary>
    public const float VelocityMemorySeconds = 2.5f;

    /// <summary>Hard cap on how far a stale belief may be extrapolated from where they were seen.</summary>
    public const float MaxExtrapolationMeters = 16f;

    /// <summary>Positional uncertainty of a perfectly fresh observation.</summary>
    public const float BaseUncertaintyMeters = 0.3f;

    /// <summary>How fast positional uncertainty grows while a player is out of sight.</summary>
    public const float UncertaintyGrowthMetersPerSecond = 1.4f;

    /// <summary>Age at which confidence has halved.</summary>
    public const float ConfidenceMemorySeconds = 2.5f;

    /// <summary>Confidence floor: an old sighting is weak evidence, not no evidence.</summary>
    public const float MinConfidence01 = 0.15f;

    private readonly Dictionary<int, PlayerBelief> _beliefs = new();

    /// <summary>
    /// False until this player has looked around once. The first look sees everyone: a match starts with
    /// every player on the pitch, and an AI that began blind to its own kickoff formation would lose
    /// seconds of play for no modelled reason.
    /// </summary>
    public bool IsInitialised { get; private set; }

    public int BeliefCount => _beliefs.Count;

    /// <summary>Records a real observation of <paramref name="playerId"/>.</summary>
    public PlayerBelief Observe(
        int playerId,
        TeamId team,
        TacticalRole role,
        Vector3 position,
        Vector3 velocity,
        float confidence01,
        int tick)
    {
        if (!_beliefs.TryGetValue(playerId, out PlayerBelief? belief))
        {
            belief = new PlayerBelief(playerId);
            _beliefs[playerId] = belief;
        }

        belief.Team = team;
        belief.Role = role;
        belief.LastObservedTick = tick;
        belief.Position = position;
        belief.Velocity = velocity;
        belief.Confidence01 = confidence01;
        return belief;
    }

    /// <summary>Marks that this player has completed their first look at the pitch.</summary>
    public void MarkInitialised() => IsInitialised = true;

    /// <summary>
    /// The predicted perception of a player who has not been observed this tick. Returns false when they
    /// have never been seen — a player nobody has looked at is genuinely absent from the picture, not a
    /// zero-confidence guess at the origin.
    /// </summary>
    public bool TryPredict(int playerId, int nowTick, out PerceivedEntity entity)
    {
        if (!_beliefs.TryGetValue(playerId, out PlayerBelief? belief) || !belief.IsKnown)
        {
            entity = default;
            return false;
        }

        float ageSeconds = belief.AgeSeconds(nowTick);
        float decay = 1f / (1f + ageSeconds / VelocityMemorySeconds);

        Vector3 extrapolation = belief.Velocity * (ageSeconds * decay);
        float length = extrapolation.Length();
        if (length > MaxExtrapolationMeters)
            extrapolation *= MaxExtrapolationMeters / length;

        float confidence = MathF.Max(MinConfidence01, belief.Confidence01 / (1f + ageSeconds / ConfidenceMemorySeconds));
        entity = new PerceivedEntity(
            belief.PlayerId,
            belief.Team,
            belief.Role,
            belief.Position + extrapolation,
            belief.Velocity * decay,
            confidence)
        {
            ObservedAgeTicks = belief.AgeTicks(nowTick),
            PositionUncertaintyMeters = BaseUncertaintyMeters + UncertaintyGrowthMetersPerSecond * ageSeconds
        };
        return true;
    }

    /// <summary>Oldest age among the players this memory holds, in ticks; 0 when empty.</summary>
    public int OldestAgeTicks(int nowTick)
    {
        int oldest = 0;
        foreach (PlayerBelief belief in _beliefs.Values)
        {
            int age = belief.AgeTicks(nowTick);
            if (age > oldest) oldest = age;
        }
        return oldest;
    }

    /// <summary>What this player remembers about one other player, or null when they never saw them.</summary>
    public PlayerBelief? Find(int playerId) =>
        _beliefs.TryGetValue(playerId, out PlayerBelief? belief) ? belief : null;

    /// <summary>Drops what this player knows about one other player (they left the pitch).</summary>
    public void Forget(int playerId) => _beliefs.Remove(playerId);

    /// <summary>Drops everything, e.g. at half time when every player is re-placed.</summary>
    public void Clear()
    {
        _beliefs.Clear();
        IsInitialised = false;
    }
}
