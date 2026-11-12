using FootballSim.Core;

namespace FootballSim.AI;

/// <summary>
/// How firmly the ball is being held, independently of who has the right to it (A3.1).
///
/// The old model had one <c>TeamTacticalState</c> per team that mixed the ownership claim with the
/// phase, the part of the pitch and the transition. A deflection could therefore not be expressed at
/// all: the ball was loose but the claim was still the attacking team's, and whichever of the two the
/// single enum chose, one of the facts was lost. Exactly one value of this enum describes the *ball*;
/// the claim, the phase, the zone, the transition and the restart are separate members of
/// <see cref="TeamContext"/>.
/// </summary>
public enum PossessionStability
{
    /// <summary>
    /// Nobody controls the ball: it is rolling, bouncing, or travelling without a planned receiver.
    /// The claim may still belong to a team, from that team's last deliberate play (a clearance, a
    /// shot, a parried save) — that is the case this enum exists to keep distinct.
    /// </summary>
    Loose = 0,

    /// <summary>A planned pass is travelling toward a teammate of the claiming team.</summary>
    Passing = 1,

    /// <summary>A player has the ball at their feet (dribble touch or first touch).</summary>
    Controlled = 2,

    /// <summary>A goalkeeper has caught the ball and is holding it.</summary>
    KeeperHeld = 3
}

/// <summary>
/// Which side of the ball a team is on (A3.1). Derived from the ownership claim, and deliberately
/// immediate: a turnover must not wait for a structural dwell timer (A3.2). A team can be
/// <see cref="Attacking"/> while the ball is loose — the claim is a right to the ball, not control.
/// </summary>
public enum TeamPhase
{
    /// <summary>The other team (or nobody) has the claim: this team defends.</summary>
    Defending = 0,

    /// <summary>This team has the claim: whoever controls the ball next is playing for this team.</summary>
    Attacking = 1
}

/// <summary>
/// The third of the pitch the ball is in <i>from one team's point of view</i> (A3.1): the third
/// nearest that team's own goal is <see cref="OwnThird"/>, the third nearest the opponent's goal is
/// <see cref="FinalThird"/>. The same ball position is therefore a different zone for each team.
/// </summary>
public enum TeamZone
{
    /// <summary>The third containing this team's own goal.</summary>
    OwnThird = 0,
    MiddleThird = 1,
    /// <summary>The third containing the opponent's goal.</summary>
    FinalThird = 2
}

/// <summary>
/// A bounded window after the claim changes (A3.1/A3.2). A transition is a *clock*, not a dwell: it
/// starts on the turnover tick and expires on its own, and it never delays another context.
/// </summary>
public enum TransitionMode
{
    None = 0,

    /// <summary>
    /// This team has just won the ball and its profile is direct enough to go forward immediately.
    /// The old model expressed this as an attacking state; it is a window of the attacking phase.
    /// </summary>
    CounterAttack = 1,

    /// <summary>
    /// This team has just lost the ball and its profile presses hard enough to try to win it back.
    /// The window is <see cref="TeamTacticalProfile.CounterPressDurationSeconds"/>, a maximum, not an
    /// instruction: A6.4 decides whether counter-pressing actually happens.
    /// </summary>
    CounterPress = 2
}

/// <summary>
/// A restart the rules engine has armed and whose first touch has not happened yet (A3.1). While it
/// is armed the ball legally belongs to <see cref="Team"/>: the opposition cannot take it.
/// </summary>
public readonly record struct RestartContext(RestartKind Kind, TeamId? Team)
{
    /// <summary>True while a restart is waiting for its first touch.</summary>
    public bool IsArmed => Kind != RestartKind.None;

    /// <summary>True when this restart is <paramref name="team"/>'s to take.</summary>
    public bool IsFor(TeamId team) => IsArmed && Team == team;

    /// <summary>No restart is armed: open play.</summary>
    public static RestartContext None => default;
}

/// <summary>
/// One team's tactical context (A3.1), resolved every live tick by
/// <see cref="FootballSim.Tactics.TeamTacticalCoordinator"/>.
///
/// It is a set of independent facts rather than one state, because they change at different rates and
/// for different reasons (A3.2): possession stability follows the ball, the phase follows the claim
/// immediately, the zone has a deadband plus a structural dwell, a transition is a bounded clock, and
/// the restart is owned by the rules engine. Consumers must read the member they need instead of
/// mapping the whole context back to one enum.
/// </summary>
public readonly record struct TeamContext(
    TeamId Team,
    TeamId? ClaimedTeam,
    PossessionStability Stability,
    TeamPhase Phase,
    TeamZone Zone,
    TransitionMode Transition,
    RestartContext Restart,
    bool IsEmergencyDefence)
{
    /// <summary>True when this team has the right to the ball, whether or not it is being held.</summary>
    public bool HasClaim => ClaimedTeam == Team;

    /// <summary>
    /// True when this team has the claim *and* the ball is actually with it. A loose ball after a
    /// deflection has <see cref="HasClaim"/> true and this false, which is the distinction A3.1 exists
    /// for: "last deliberate play" is not "stable control".
    /// </summary>
    public bool HasStableControl => HasClaim && Stability != PossessionStability.Loose;

    /// <summary>True inside a counter-attack or counter-press window.</summary>
    public bool IsTransitioning => Transition != TransitionMode.None;

    /// <summary>
    /// A3.2: the ball is in this team's own third and the opponent has it under control (or a planned
    /// pass of theirs is travelling). This is the one case where the structural dwell must not hold a
    /// stale band: shape is being imposed on this team, not chosen by it.
    /// </summary>
    public bool IsUnderEmergencyDefence => IsEmergencyDefence;
}
