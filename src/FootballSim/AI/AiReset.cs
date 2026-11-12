namespace FootballSim.AI;

/// <summary>
/// Why AI working state is being dropped.
///
/// The reason is part of the contract, not decoration: a restart and a half-time are different
/// discontinuities, and a component that clears the same things for both is hiding which one it actually
/// understands. Tests assert the mapping rather than a bare boolean.
/// </summary>
public enum AiResetReason
{
    /// <summary>A restart (throw-in, goal kick, corner, free kick, penalty, goal) interrupted play.</summary>
    MatchRestart,

    /// <summary>The half ended: direction reverses and positions are reset.</summary>
    HalfTime,

    /// <summary>The match is over. Nothing may carry into a replay of the same engine.</summary>
    FullTime,

    /// <summary>A player left the pitch (sent off or substituted).</summary>
    PlayerRemoved,

    /// <summary>A training drill reset back to its spawn state.</summary>
    TrainingReset,

    /// <summary>A different controller was installed for a player.</summary>
    ControllerReplaced
}

/// <summary>
/// An AI component holding state that belongs to one continuous passage of play.
///
/// What must be dropped: commitment, a chosen intent, a tackle cooldown, and any cached observation
/// history whose staleness would be indistinguishable from a real memory. What must NOT be dropped:
/// anything that legitimately spans play — stamina, cards, sent-off status, the match clock, or a
/// player's current authoritative control of the ball.
///
/// Implementations must be safe to call at any time, including mid-tick, and must not depend on the
/// engine being in a particular phase.
/// </summary>
public interface IAiStateResettable
{
    /// <summary>Drops passage-of-play state. Called from the authoritative discontinuity, not by the AI.</summary>
    void ResetForPhase(AiResetReason reason);
}
