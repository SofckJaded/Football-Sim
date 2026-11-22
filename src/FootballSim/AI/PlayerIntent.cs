using System;
using System.Numerics;
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
    /// <summary>
    /// A7.3: the reception plan behind the command, when this intent is a first touch. The default is
    /// <see cref="ReceptionPlan.None"/>, so every pre-A7.3 caller and every non-reception action keeps
    /// the neutral value. It lives on the intent (not only on the observation) because the planned
    /// touch survives until the ball actually arrives and A7.4 publishes it on the command boundary.
    /// </summary>
    public ReceptionPlan Reception { get; init; } = ReceptionPlan.None;

    /// <summary>
    /// AUD-08: whether this player was on the ball when the intent was chosen. A possession change ends
    /// the intent's authority: an off-ball movement plan is the wrong thing to keep executing once the
    /// player controls the ball, and an on-ball action chosen before a kick must not be re-emitted into
    /// the passage of play that follows it. Defaults to false so an intent built by an older fixture keeps
    /// its pre-audit meaning.
    /// </summary>
    public bool OnBallAtDecision { get; init; }

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
///
/// AUD-08: the recovery timers alone are not enough. Re-emitting the stored command verbatim on a cadence
/// skip re-issued the same <see cref="FootballerCommand.KickType"/> / <see cref="FootballerCommand.RequestTackle"/>
/// every skipped tick, so a kick that execution refused on the tick it was chosen could still execute ticks
/// later, after the ball or its target had moved — exactly the cached discrete action Architecture Rule 4
/// forbids. The executor therefore strips the one-shot actions from every re-emitted command (the tick the
/// intent was issued on is not a re-emission) and refuses to re-emit at all when the player's on-ball state
/// has changed since the decision that stored the intent.
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
    ///
    /// AUD-08: a re-emitted command is the stored movement/facing/hold command with its discrete actions
    /// cleared by <see cref="ClearDiscreteActions"/>. The intent's own tick is returned verbatim — that is
    /// the decision's emission, not a re-emission.
    /// </summary>
    public bool TryGetLiveCommand(int tick, out FootballerCommand command)
    {
        if (_intent is { } intent && intent.IsLiveAt(tick))
        {
            command = ReEmitted(intent, tick);
            return true;
        }

        command = default;
        return false;
    }

    /// <summary>
    /// As <see cref="TryGetLiveCommand(int, out FootballerCommand)"/>, and false when
    /// <paramref name="onBallAtDecision"/> differs from the state the stored intent was chosen in — the
    /// controller must then decide again rather than re-emit a command chosen for the other side of the
    /// possession change (AUD-08).
    /// </summary>
    public bool TryGetLiveCommand(int tick, bool onBallAtDecision, out FootballerCommand command)
    {
        if (_intent is { } intent &&
            intent.IsLiveAt(tick) &&
            intent.OnBallAtDecision == onBallAtDecision)
        {
            command = ReEmitted(intent, tick);
            return true;
        }

        command = default;
        return false;
    }

    /// <summary>
    /// The command to emit for <paramref name="tick"/>: verbatim on the intent's own tick, otherwise with
    /// its one-shot discrete actions cleared (AUD-08).
    /// </summary>
    private static FootballerCommand ReEmitted(in PlayerIntent intent, int tick) =>
        tick == intent.StartTick ? intent.Command : ClearDiscreteActions(intent.Command);

    /// <summary>
    /// Strips every one-shot discrete action from <paramref name="command"/> and keeps the fields that
    /// describe continuing movement, facing and ball shielding (AUD-08, Architecture Rule 4).
    ///
    /// The aim vector is kept with the movement fields: it is not an action by itself (execution only reads
    /// it when <see cref="FootballerCommand.KickType"/> is set) and it is what a steer/aim consumer expects to
    /// keep seeing. The reception plan is kept for the same reason: it is not an action to execute, it is the
    /// touch planned for a ball already on its way, and it must survive until that ball arrives.
    /// </summary>
    public static FootballerCommand ClearDiscreteActions(FootballerCommand command) => command with
    {
        RequestTackle = false,
        RequestJumpHeader = false,
        RequestKeeperDive = false,
        KeeperDiveDirection = Vector3.Zero,
        KickType = KickIntentType.None,
        KickPower01 = 0f,
        RequestedCurveSpin = 0f,
        IntendedReceiverId = null
    };
}
