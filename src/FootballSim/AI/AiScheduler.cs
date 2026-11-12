using System;
using FootballSim.Core;

namespace FootballSim.AI;

/// <summary>
/// Decides <em>when</em> a player re-scores its candidates, independently of when it re-emits a command.
///
/// Physical execution, steering and intent execution stay at 60 Hz. Only the expensive part — generating
/// and scoring candidates — is spread out, which is what makes a larger behaviour library affordable later.
///
/// Two invariants are load-bearing:
/// <list type="bullet">
/// <item><b>Deterministic.</b> The schedule is a function of the tick and the player id only. No wall clock,
/// no randomness, so a replay reproduces it exactly.</item>
/// <item><b>Staggered.</b> Each player's phase is derived from its id, so a whole team does not re-score on
/// the same tick and the cost is flat instead of spiky.</item>
/// </list>
///
/// It is <b>off by default</b>. Turning it on changes behaviour substantially (a player acts on a slightly
/// older evaluation), so it stays a switch until the behaviour it gates has been tuned with it on.
/// </summary>
public sealed class AiScheduler
{
    /// <summary>Golden-ratio odd multiplier used to spread player phases without clustering.</summary>
    private const uint PhaseStride = 2654435761u;

    private readonly AiRuntimeConfig _config;

    public AiScheduler(AiRuntimeConfig? config = null) => _config = config ?? AiRuntimeConfig.Default;

    /// <summary>True when scheduled (slower-than-every-tick) re-scoring is enabled.</summary>
    public bool IsEnabled => _config.EnableScheduledDecisions;

    /// <summary>
    /// True when this player should re-generate and re-score its candidates on <paramref name="tick"/>.
    ///
    /// Returns true in every case that must not wait: scheduling disabled, an unknown tick (a standalone
    /// controller that was not given one), or the first tick.
    /// </summary>
    public bool ShouldRecompute(int tick, int playerId, bool hasBall)
    {
        if (!IsEnabled || tick <= 0) return true;

        int interval = hasBall ? _config.CarrierDecisionTicks : _config.OutfieldDecisionTicks;
        if (interval <= 1) return true;

        return (tick + PhaseOffset(playerId, interval)) % interval == 0;
    }

    /// <summary>
    /// This player's phase within its cadence window. Deterministic for a given id, and spread across the
    /// window rather than clustered, because ids are dense small integers.
    /// </summary>
    public static int PhaseOffset(int playerId, int interval)
    {
        if (interval <= 1) return 0;
        uint mixed = unchecked((uint)playerId * PhaseStride);
        return (int)(mixed % (uint)interval);
    }
}
