using System;
using FootballSim.Core;

namespace FootballSim.AI;

/// <summary>
/// A decision that outlives the tick that made it: what the player is doing, the command that executes it,
/// and when it stops being valid.
///
/// The identity is a <see cref="CandidateKey"/> rather than a label, and the expiry is an absolute tick
/// rather than a countdown, so a paused or suspended tick can never silently extend an intent.
/// </summary>
public readonly record struct PlayerIntent(
    CandidateKey Key,
    string ActionName,
    FootballerCommand Command,
    int StartTick,
    int ExpiryTick)
{
    /// <summary>True while <paramref name="tick"/> is inside the intent's half-open window.</summary>
    public bool IsLiveAt(int tick) => tick >= StartTick && tick < ExpiryTick;

    /// <summary>Age in ticks, or 0 before it starts.</summary>
    public int AgeTicks(int tick) => Math.Max(0, tick - StartTick);
}

/// <summary>
/// Holds the controller's current intent and answers "is there a command to re-emit this tick?".
///
/// The executor deliberately knows nothing about scoring: it is the boundary between choosing and
/// producing a command. Re-emitting a live intent is not "deciding again" — it is the same decision being
/// executed, which is why it must not re-run candidates and must not touch the random streams.
///
/// A kick, tackle or call is still emitted exactly once by the execution layer: the intent carries the
/// command, and the execution layer's own recovery timers and contact feedback prevent a repeated action
/// from being executed twice.
/// </summary>
public sealed class IntentExecutor
{
    private PlayerIntent? _intent;

    /// <summary>The current intent, if any. Expired intents remain readable until cleared.</summary>
    public PlayerIntent? Current => _intent;

    public bool HasIntent => _intent.HasValue;

    /// <summary>True when there is an intent and it is still valid at <paramref name="tick"/>.</summary>
    public bool IsLiveAt(int tick) => _intent is { } intent && intent.IsLiveAt(tick);

    public void Set(in PlayerIntent intent) => _intent = intent;

    public void Clear() => _intent = null;

    /// <summary>
    /// Provides the command to re-emit for this tick. Returns false when there is nothing live, which means
    /// the controller must decide.
    /// </summary>
    public bool TryGetLiveCommand(int tick, out FootballerCommand command)
    {
        if (_intent is { } intent && intent.IsLiveAt(tick))
        {
            command = intent.Command;
            return true;
        }

        command = default;
        return false;
    }
}
