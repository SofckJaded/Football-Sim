using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Tactics;

namespace FootballSim.AI;

/// <summary>
/// The bounded menu a restart routine chooses from (A8.5). The plan asks for "a small library of legal
/// kick-off, throw-in, corner, free-kick and goal-kick routines"; this is that library as two options per
/// restart kind. Both are <b>laws the taker can already take</b>: a pass to a team-mate's feet either short
/// (the support) or further up the pitch (the target). A routine never invents a placement (the rules engine
/// places the ball), a contact (execution arbitrates every kick) or an offside exemption (the law model owns
/// that); it chooses <i>which</i> lawful option to take and <i>who</i> receives it.
/// </summary>
public enum RestartOption
{
    /// <summary>No routine.</summary>
    None = 0,

    /// <summary>Play the short option to the support player's feet.</summary>
    Short,

    /// <summary>Play the longer option to the target player, further up the pitch or into the area.</summary>
    Deliver
}

/// <summary>What one player's part in a live restart routine is.</summary>
public enum RestartRole
{
    /// <summary>Not a participant.</summary>
    None = 0,

    /// <summary>The player the rules engine designated to take the restart.</summary>
    Taker,

    /// <summary>The short option: the nearest lawful receiver the routine may play to feet.</summary>
    Support,

    /// <summary>The longer option: the lawful receiver the routine may deliver to up the pitch.</summary>
    Target
}

/// <summary>How a routine left the board. Diagnostics, the probe and the tests read it; no decision does.</summary>
public enum RestartEndReason
{
    /// <summary>Still live, or never created.</summary>
    None = 0,

    /// <summary>The restart was taken by its own team: the routine did its job (the exit gate's "completion").</summary>
    Taken,

    /// <summary>An opponent touched the ball before the restart was taken (a stolen restart, or a physics accident).</summary>
    Stolen,

    /// <summary>A different restart is in force: kind, team, taker or spot changed under the routine.</summary>
    Replaced,

    /// <summary>The window ran out without the restart being taken.</summary>
    Expired,

    /// <summary>The designated taker changed or is no longer on the pitch.</summary>
    TakerLost,

    /// <summary>The support or the target left the pitch: he owned a named role inside the routine.</summary>
    ParticipantRemoved,

    /// <summary>The interval, full time or an engine reset ended the passage the routine belonged to.</summary>
    Reset
}

/// <summary>
/// One bounded restart routine (A8.5): the restart it belongs to, the option chosen from the lawful menu, the
/// three named roles, the two receivers it names, its window and the confidence it was raised with.
///
/// <b>Why a routine and not a smarter taker.</b> The taker already exists (A4.9 gave him a passer's planning);
/// what is missing is the <i>team's</i> choice of option: the taker's own heuristic ("the most advanced mate
/// whose route is open") is a per-player rule, so every restart is chosen by whoever happens to be nearest the
/// ball. A routine is the team's decision — this restart is played short, or delivered to this target, and
/// these are the men who are part of it — elected once from the team's shared picture and handed to the taker
/// for exactly the window the restart is armed. It ends once, with a stated reason, and no routine outlives the
/// restart that produced it (the exit gate's "no contract survives into unrelated possession").
///
/// <b>Knowledge boundary.</b> Every field is either the restarting team's own player id or a belief the team's
/// shared picture already holds: the ball's placed spot, the receiver's believed position and the offside line
/// the law model derives from the team's own <see cref="TeamBelief"/>. It carries no opponent velocity or
/// intent, so a routine can never become a second, faster sight line.
/// </summary>
public readonly record struct RestartRoutine(
    int RoutineId,
    RestartKind Kind,
    RestartOption Option,
    TeamId Team,
    int TakerId,
    int SupportId,
    int TargetId,
    Vector2 BallXZ,
    Vector2 ReceiverXZ,
    int CreatedTick,
    int ExpiresTick,
    float Confidence01,
    float ReceiverSpace01,
    float Progression01)
{
    /// <summary>True while the routine's own window is open.</summary>
    public bool IsLiveAt(int tick) => tick <= ExpiresTick;

    /// <summary>The player the chosen option is played to, or 0 when that option named nobody.</summary>
    public int ReceiverId => Option == RestartOption.Short ? SupportId : TargetId;

    /// <summary>This player's role in the routine, or <see cref="RestartRole.None"/>.</summary>
    public RestartRole RoleOf(int playerId)
    {
        if (playerId == TakerId) return RestartRole.Taker;
        if (SupportId > 0 && playerId == SupportId) return RestartRole.Support;
        if (TargetId > 0 && playerId == TargetId) return RestartRole.Target;
        return RestartRole.None;
    }

    /// <summary>The player parts of the routine are mine if this is the taker, the support or the target.</summary>
    public bool IsParticipant(int playerId) => RoleOf(playerId) != RestartRole.None;

    /// <summary>Compact text form for a trace line, a probe or a failure message.</summary>
    public string Describe() =>
        $"{Kind} {Option} routine #{RoutineId} team {Team}: taker #{TakerId}" +
        $" | support #{SupportId} target #{TargetId} | ball ({BallXZ.X:F1},{BallXZ.Y:F1})" +
        $" -> receiver #{ReceiverId} at ({ReceiverXZ.X:F1},{ReceiverXZ.Y:F1})" +
        $" | created t{CreatedTick} until t{ExpiresTick} conf {Confidence01:F2}" +
        $" (space {ReceiverSpace01:F2}, progression {Progression01:F2})";
}

/// <summary>
/// What one player's observation carries about the live restart routine he is part of: his role, the option the
/// team chose, the receiver it names and the ball's placed spot.
///
/// The default is neutral (<see cref="Kind"/> none), so a fixture or standalone controller that knows nothing
/// about restart routines behaves exactly as it did before A8.5 — the rule every added observation field in
/// this project follows. The taker is the only participant whose decision reads it (his delivery follows the
/// routine while it is live); the support and the target carry their roles because the routine named them and a
/// test must be able to prove who they were.
/// </summary>
public readonly record struct RestartRoutineContext(
    int RoutineId,
    RestartKind Kind,
    RestartOption Option,
    RestartRole Role,
    int TakerId,
    int SupportId,
    int TargetId,
    Vector2 BallXZ,
    Vector2 ReceiverXZ,
    int CreatedTick,
    int ExpiresTick,
    float Confidence01)
{
    /// <summary>No live routine: the neutral picture of every pre-A8.5 fixture.</summary>
    public static RestartRoutineContext None => default;

    /// <summary>True when this player is part of a live restart routine.</summary>
    public bool IsActive => Kind != RestartKind.None && Role != RestartRole.None && RoutineId != 0;

    /// <summary>True when this player is the designated taker the routine is written for.</summary>
    public bool IsTaker => IsActive && Role == RestartRole.Taker;

    /// <summary>True when this player is the routine's short option.</summary>
    public bool IsSupport => IsActive && Role == RestartRole.Support;

    /// <summary>True when this player is the routine's longer option.</summary>
    public bool IsTarget => IsActive && Role == RestartRole.Target;

    /// <summary>The player the chosen option is played to, or 0 when that option named nobody.</summary>
    public int ReceiverId => Option == RestartOption.Short ? SupportId : TargetId;

    /// <summary>Compact text form for the inspector, a trace line or a failure message.</summary>
    public string Describe() =>
        IsActive
            ? $"{Kind} {Option} routine #{RoutineId} as {Role} (taker #{TakerId}, support #{SupportId}," +
              $" target #{TargetId}, receiver #{ReceiverId}) conf {Confidence01:F2} until t{ExpiresTick}"
            : "none";
}

/// <summary>
/// The match's bounded restart-routine board (A8.5): at most one live routine — a restart is a match-global
/// event, so one entry is the whole answer to "which restart is being played, how, and by whom".
///
/// <b>Ownership.</b> One instance lives on the match's <see cref="FootballSim.Input.MatchCommunicationContext"/>
/// — the same match-owned object that already carries the human's call, A5.3's run advertisements, A7.2's
/// calls, A8.1's contracts and A8.4's traps. This is the one team communication object extended, not a second
/// channel. The routine is elected from the restarting team's shared inputs (its own exact player positions, its
/// <see cref="TeamBelief"/> of the opponents and the rules engine's own restart state), the same election
/// pattern A6.1's second man and A8.4's trap use.
///
/// <b>Lifecycle.</b> A routine is raised while the rules engine has a restart armed for its team and the team's
/// shared picture still has a lawful option worth taking. It ends exactly once: <see cref="RestartEndReason.Taken"/>
/// when the restart is played, <see cref="RestartEndReason.Stolen"/> when the other side touches it first,
/// <see cref="RestartEndReason.Replaced"/> when a different restart is armed under it,
/// <see cref="RestartEndReason.Expired"/> when its window closes, <see cref="RestartEndReason.TakerLost"/> or
/// <see cref="RestartEndReason.ParticipantRemoved"/> when a named player can no longer take his part, and
/// <see cref="RestartEndReason.Reset"/> on an interval or an engine reset.
///
/// <b>Determinism.</b> Every window is measured in live ticks, every scan is list-ordered, ties keep the first
/// entry, and no RNG is touched: two matches with the same seed raise, keep and end exactly the same routines.
/// </summary>
public sealed class RestartCoordinator
{
    /// <summary>
    /// How long a routine stays live without its restart being taken, in ticks (4 s). A restart is normally
    /// played on the first live tick after the setup pause, so this only bounds a taker who cannot reach the
    /// ball (a blocked path, a scramble) — a routine is not a state that waits for a better moment.
    /// </summary>
    public const int RoutineDurationTicks = 240;

    /// <summary>
    /// How far the ball may be from the spot the routine was written for before the routine is treated as
    /// belonging to a different restart, in metres. The ball is stationary while a restart is armed; this only
    /// separates two restarts of the same kind for the same team at almost the same spot.
    /// </summary>
    public const float RestartSpotToleranceMeters = 2f;

    private struct Entry
    {
        public bool Occupied;
        public RestartRoutine Routine;
    }

    private Entry _entry;
    private readonly int[] _endedByReason = new int[Enum.GetValues(typeof(RestartEndReason)).Length];
    private int _nextRoutineId = 1;

    /// <summary>Routines raised on this board since the match started (diagnostics only).</summary>
    public int RaisedCount { get; private set; }

    /// <summary>Routines that ended for one reason since the match started.</summary>
    public int CountOfEnd(RestartEndReason reason)
    {
        int index = (int)reason;
        return index >= 0 && index < _endedByReason.Length ? _endedByReason[index] : 0;
    }

    /// <summary>
    /// Advances the match's restart routine: keeps or ends the live one, or raises a new one when the rules
    /// engine has armed a restart and the restarting team's shared picture has a lawful option to take.
    /// Returns true when a routine is live after the call.
    ///
    /// The checks run in one documented order — taken/stolen, replaced, participant availability, expiry — so
    /// the ending a routine is attributed is deterministic when two facts change on the same tick.
    /// </summary>
    /// <param name="restartTeam">The team the rules engine awarded the armed restart to.</param>
    /// <param name="activeRestart">The restart the rules engine has armed, or
    /// <see cref="RestartKind.None"/> once its first touch has been played.</param>
    /// <param name="ballXZ">Where the ball actually is: the placed spot of an armed restart, otherwise the last
    /// position the setup left it in.</param>
    /// <param name="takerId">The player the rules engine designated to take the restart, or null.</param>
    /// <param name="players">Every player in the match; the restarting team's own are read exactly.</param>
    /// <param name="opponents">The restarting team's shared picture of its opponents (A2.6): never live state.</param>
    /// <param name="profile">The restarting team's tactical profile (its stated short/long preference).</param>
    /// <param name="humanPlayerId">The locked human, who is never bound by an AI routine: the AI must not take
    /// the human's own restart over.</param>
    /// <param name="attackDirX">The restarting team's attacking direction.</param>
    /// <param name="pitch">Pitch geometry.</param>
    /// <param name="lastTouchPlayerId">The ball's last toucher, used only to attribute the routine's ending.</param>
    /// <param name="lastTouchTeam">That toucher's team, used only to attribute the routine's ending.</param>
    /// <param name="tick">The live tick.</param>
    /// <param name="enabled">False for the ablation build: nothing is raised.</param>
    public bool Update(
        TeamId restartTeam,
        RestartKind activeRestart,
        Vector2 ballXZ,
        int? takerId,
        IReadOnlyList<FootballerState> players,
        TeamBelief opponents,
        TeamTacticalProfile profile,
        int? humanPlayerId,
        float attackDirX,
        PitchDimensions pitch,
        int? lastTouchPlayerId,
        TeamId? lastTouchTeam,
        int tick,
        bool enabled)
    {
        if (_entry.Occupied)
        {
            RestartRoutine routine = _entry.Routine;

            // 1. The restart setup is over. The rules engine clears its armed restart on the first touch, so
            // this is the exit gate's "clear on completion or invalidation": the routine did its job, or the
            // other side stole the restart, or the setup was reset without any touch.
            if (activeRestart == RestartKind.None)
            {
                RestartEndReason reason = lastTouchPlayerId.HasValue
                    ? lastTouchTeam == routine.Team
                        ? RestartEndReason.Taken
                        : RestartEndReason.Stolen
                    : RestartEndReason.Reset;
                End(reason);
                return false;
            }

            // 2. A different restart is in force under it: nothing about the routine's answer applies any more.
            if (activeRestart != routine.Kind ||
                restartTeam != routine.Team ||
                takerId != routine.TakerId ||
                Vector2.Distance(ballXZ, routine.BallXZ) > RestartSpotToleranceMeters)
            {
                End(RestartEndReason.Replaced);
                return false;
            }

            // 3. The participants still exist and are still available. Any named player who is no longer on the
            // pitch ends the routine: an inactive player must not keep owning a role inside a contract.
            if (!IsActive(players, routine.TakerId))
            {
                End(RestartEndReason.TakerLost);
                return false;
            }

            if (routine.SupportId > 0 && !IsActive(players, routine.SupportId))
            {
                End(RestartEndReason.ParticipantRemoved);
                return false;
            }

            if (routine.TargetId > 0 && !IsActive(players, routine.TargetId))
            {
                End(RestartEndReason.ParticipantRemoved);
                return false;
            }

            // 4. The window is the outermost bound: a routine never outlives the setup pause it was written for.
            if (!routine.IsLiveAt(tick))
            {
                End(RestartEndReason.Expired);
                return false;
            }

            return true;
        }

        if (!enabled) return false;
        if (activeRestart == RestartKind.None) return false;

        if (!RestartPolicy.TryPlan(restartTeam, activeRestart, ballXZ, takerId, players, opponents, profile,
                humanPlayerId, attackDirX, pitch, out RestartPlan plan))
        {
            return false;
        }

        Publish(in plan, ballXZ, tick);
        return true;
    }

    /// <summary>What this player's own observation should carry: his part in the live routine he is entitled to know.</summary>
    public RestartRoutineContext ContextFor(TeamId team, int playerId, int tick)
    {
        if (playerId <= 0 || !_entry.Occupied) return RestartRoutineContext.None;

        RestartRoutine routine = _entry.Routine;
        if (routine.Team != team || !routine.IsLiveAt(tick)) return RestartRoutineContext.None;

        RestartRole role = routine.RoleOf(playerId);
        if (role == RestartRole.None) return RestartRoutineContext.None;

        return new RestartRoutineContext(
            routine.RoutineId, routine.Kind, routine.Option, role,
            routine.TakerId, routine.SupportId, routine.TargetId,
            routine.BallXZ, routine.ReceiverXZ, routine.CreatedTick, routine.ExpiresTick, routine.Confidence01);
    }

    /// <summary>The live routine, if any (diagnostics and tests).</summary>
    public bool TryGetLive(int tick, out RestartRoutine routine)
    {
        if (!_entry.Occupied || !_entry.Routine.IsLiveAt(tick))
        {
            routine = default;
            return false;
        }

        routine = _entry.Routine;
        return true;
    }

    /// <summary>True while a routine is live (diagnostics and tests).</summary>
    public bool HasLive(int tick) => TryGetLive(tick, out _);

    /// <summary>
    /// A player left the pitch: a routine he was part of ends with him — an inactive player must not keep
    /// owning a role inside a contract.
    /// </summary>
    public void ReleasePlayer(int playerId, int tick)
    {
        if (playerId <= 0 || !_entry.Occupied) return;
        if (!_entry.Routine.IsParticipant(playerId)) return;

        End(_entry.Routine.TakerId == playerId
            ? RestartEndReason.TakerLost
            : RestartEndReason.ParticipantRemoved);
    }

    /// <summary>
    /// Ends the live routine because the passage it belonged to ended (the interval, full time, an engine reset,
    /// or a restart replacing it). The reason is <see cref="RestartEndReason.Replaced"/> when another restart is
    /// already armed under it, and <see cref="RestartEndReason.Reset"/> when the world was simply reset.
    /// </summary>
    public void ClearAll(RestartKind activeRestart)
    {
        if (!_entry.Occupied) return;
        End(activeRestart == RestartKind.None ? RestartEndReason.Reset : RestartEndReason.Replaced);
    }

    private void Publish(in RestartPlan plan, Vector2 ballXZ, int tick)
    {
        var routine = new RestartRoutine(
            RoutineId: _nextRoutineId++,
            Kind: plan.Kind,
            Option: plan.Option,
            Team: plan.Team,
            TakerId: plan.TakerId,
            SupportId: plan.SupportId,
            TargetId: plan.TargetId,
            BallXZ: ballXZ,
            ReceiverXZ: plan.ReceiverXZ,
            CreatedTick: tick,
            ExpiresTick: tick + RoutineDurationTicks,
            Confidence01: Math.Clamp(plan.Confidence01, 0f, 1f),
            ReceiverSpace01: Math.Clamp(plan.ReceiverSpace01, 0f, 1f),
            Progression01: Math.Clamp(plan.Progression01, 0f, 1f));

        _entry = new Entry { Occupied = true, Routine = routine };
        RaisedCount++;
    }

    private void End(RestartEndReason reason)
    {
        if (!_entry.Occupied) return;

        _entry = default;
        _endedByReason[(int)reason]++;
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
}
