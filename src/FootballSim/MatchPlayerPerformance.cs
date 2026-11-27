using System;
using System.Collections.Generic;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Rules;

namespace FootballSim;

/// <summary>
/// One player's additive match performance line (W5), the observer view the living-world match bridge reads
/// after full time. It is diagnostics only: no decision path in the engine reads it, and the tracker that
/// fills it consumes no simulation randomness.
/// </summary>
public sealed class MatchPlayerPerformance
{
    internal MatchPlayerPerformance(
        int playerId, TeamId team, int shirtNumber, TacticalRole role, string name, float startingStamina01)
    {
        PlayerId = playerId;
        Team = team;
        ShirtNumber = shirtNumber;
        Role = role;
        Name = name ?? string.Empty;
        StartingStamina01 = startingStamina01;
        EndingStamina01 = startingStamina01;
    }

    /// <summary>Engine player id (home 1..PlayersPerTeam, away PlayersPerTeam+1..2*PlayersPerTeam).</summary>
    public int PlayerId { get; }

    /// <summary>Team the player belongs to.</summary>
    public TeamId Team { get; }

    /// <summary>Shirt number 1..PlayersPerTeam.</summary>
    public int ShirtNumber { get; }

    /// <summary>The role the player was fielded in.</summary>
    public TacticalRole Role { get; }

    /// <summary>Bridge-supplied display name, or empty for a generated squad.</summary>
    public string Name { get; }

    /// <summary>Goals credited to the player (own goals are not goals for the defender).</summary>
    public int Goals { get; internal set; }

    /// <summary>Assists: the passer of the pass the scorer controlled before the goal (bounded window).</summary>
    public int Assists { get; internal set; }

    /// <summary>Shots attempted (executed shot contacts only).</summary>
    public int Shots { get; internal set; }

    /// <summary>Shots on target, approximated from a goal scored directly from the player's last shot.</summary>
    public int ShotsOnTarget { get; internal set; }

    /// <summary>Passes attempted (executed pass contacts only).</summary>
    public int PassesAttempted { get; internal set; }

    /// <summary>Passes completed (a team-mate controlled the ball next).</summary>
    public int PassesCompleted { get; internal set; }

    /// <summary>Standing tackles that dispossessed an opponent.</summary>
    public int Tackles { get; internal set; }

    /// <summary>Goalkeeper catches/parries.</summary>
    public int Saves { get; internal set; }

    /// <summary>Yellow cards.</summary>
    public int YellowCards { get; internal set; }

    /// <summary>Red cards.</summary>
    public int RedCards { get; internal set; }

    /// <summary>Stamina/freshness the player started the match with (0..1).</summary>
    public float StartingStamina01 { get; }

    /// <summary>Stamina/freshness at full time (0..1).</summary>
    public float EndingStamina01 { get; internal set; }

    /// <summary>Match minutes the player was on the pitch for (from the match clock).</summary>
    public float MinutesPlayed { get; internal set; }

    /// <inheritdoc />
    public override string ToString() =>
        $"#{PlayerId} {Team} shirt {ShirtNumber}: {Goals}g {Assists}a {Shots}s {PassesCompleted}/{PassesAttempted}p " +
        $"{Tackles}t {Saves}sv stamina {EndingStamina01:0.00}";
}

/// <summary>
/// Additive per-player observer for the match engine (W5): it derives a performance line for every player
/// from the authoritative contact events the execution layer already emits and from the rules engine's event
/// list, exactly like <see cref="MatchStatisticsTracker"/> derives the team totals.
///
/// <para><b>Zero behaviour change.</b> The tracker never mutates players, ball or rules state, never allocates
/// per contact beyond the one-time rows, and never touches a random stream. A match whose caller does not read
/// it produces bit-for-bit the same result as before it existed; the only cost is a handful of integer
/// increments per contact.</para>
///
/// <para><b>Approximations (documented).</b> The engine has no substitutions, so every player plays the whole
/// match and minutes come from the match clock. Assists are the last completed pass into the scorer within
/// <see cref="AssistWindowTicks"/>; the engine does not model assists as a first-class output. Shots on target
/// are inferred from a goal scored straight off the player's most recent shot.</para>
/// </summary>
public sealed class MatchPlayerPerformanceTracker
{
    /// <summary>Ticks within which a completed pass into the scorer still counts as an assist.</summary>
    public const int AssistWindowTicks = 600;

    private readonly MatchPlayerPerformance?[] _byId;
    private readonly List<MatchPlayerPerformance> _players;
    private int _appliedEventCount;
    private bool _captured;

    private int? _pendingPassTeamOrdinal;
    private int _pendingPasserId = -1;
    private int _pendingPassTick = -1;

    private int? _lastShotPlayerId;
    private int _lastShotTick = -1;

    private int _lastCompletedPasserId = -1;
    private int _lastCompletedReceiverId = -1;
    private int _lastCompletionTick = -1;

    /// <summary>Creates the tracker for a built squad (all players of both teams).</summary>
    /// <param name="players">The engine's authoritative player list (after squad initialisation).</param>
    /// <exception cref="ArgumentNullException"><paramref name="players"/> is null.</exception>
    public MatchPlayerPerformanceTracker(IReadOnlyList<FootballerState> players)
    {
        ArgumentNullException.ThrowIfNull(players);

        int highestId = 0;
        for (int index = 0; index < players.Count; index++)
        {
            if (players[index].Id > highestId) highestId = players[index].Id;
        }

        _byId = new MatchPlayerPerformance?[highestId + 1];
        _players = new List<MatchPlayerPerformance>(players.Count);
        for (int index = 0; index < players.Count; index++)
        {
            FootballerState player = players[index];
            var row = new MatchPlayerPerformance(
                player.Id, player.Team, player.ShirtNumber, player.Role, string.Empty, player.Stamina01);
            _byId[player.Id] = row;
            _players.Add(row);
        }
    }

    /// <summary>Every tracked player, in squad order (home first, then away).</summary>
    public IReadOnlyList<MatchPlayerPerformance> Players => _players;

    /// <summary>Looks one player's line up by engine id, or null for an id outside the match.</summary>
    /// <param name="playerId">Engine player id.</param>
    public MatchPlayerPerformance? Get(int playerId) =>
        playerId >= 0 && playerId < _byId.Length ? _byId[playerId] : null;

    /// <summary>
    /// Applies one tick's executed contacts to the per-player counters. Call it once per tick with the same
    /// contact list the other observers consume, before the rules engine runs, exactly like the team statistics.
    /// </summary>
    /// <param name="contacts">Executed contacts of the tick, or null when the tick was not live play.</param>
    /// <param name="currentTick">Authoritative tick number.</param>
    public void ApplyContacts(IReadOnlyList<BallContactEvent>? contacts, int currentTick)
    {
        if (contacts is null) return;

        for (int i = 0; i < contacts.Count; i++)
        {
            ApplyContact(contacts[i], currentTick);
        }
    }

    /// <summary>
    /// Applies the rules engine's new events (goals, cards, half/full time) to the per-player lines. The
    /// engine's event list is cumulative, so the tracker keeps a cursor and only consumes new entries.
    /// </summary>
    /// <param name="events">The rules engine's event list (may be empty).</param>
    /// <param name="currentTick">Authoritative tick number.</param>
    public void ApplyRuleEvents(IReadOnlyList<MatchRuleEvent>? events, int currentTick)
    {
        if (events is null || events.Count <= _appliedEventCount) return;

        for (int index = _appliedEventCount; index < events.Count; index++)
        {
            MatchRuleEvent ruleEvent = events[index];
            switch (ruleEvent.EventType)
            {
                case "Goal":
                {
                    if (ruleEvent.IsOwnGoal) break;
                    int? scorerId = ruleEvent.ScorerPlayerId ?? ruleEvent.PlayerId;
                    if (scorerId is not int scorer) break;

                    MatchPlayerPerformance? scorerRow = Get(scorer);
                    if (scorerRow is null) break;
                    scorerRow.Goals++;

                    if (_lastShotPlayerId == scorer &&
                        _lastShotTick >= 0 &&
                        currentTick - _lastShotTick <= AssistWindowTicks)
                    {
                        scorerRow.ShotsOnTarget++;
                    }

                    if (_lastCompletedReceiverId == scorer &&
                        _lastCompletedPasserId >= 0 &&
                        _lastCompletedPasserId != scorer &&
                        _lastCompletionTick >= 0 &&
                        currentTick - _lastCompletionTick <= AssistWindowTicks)
                    {
                        MatchPlayerPerformance? passerRow = Get(_lastCompletedPasserId);
                        if (passerRow is not null && passerRow.Team == scorerRow.Team) passerRow.Assists++;
                    }

                    ResetPassPhase(currentTick);
                    break;
                }

                case "YellowCard":
                    if (ruleEvent.PlayerId is int booked) Booked(booked, isRed: false);
                    break;

                case "RedCard":
                    if (ruleEvent.PlayerId is int sentOff) Booked(sentOff, isRed: true);
                    break;

                case "HalfTime":
                case "FullTime":
                    ResetPassPhase(currentTick);
                    break;
            }
        }

        _appliedEventCount = events.Count;
    }

    /// <summary>
    /// Ends a pass flight without crediting anyone (a whistle, a restart, a goal). Mirrors the team statistics
    /// tracker's own reset so a stale pass can never award a completion or an assist across a stoppage.
    /// </summary>
    /// <param name="currentTick">Authoritative tick number.</param>
    public void ResetPassPhase(int currentTick)
    {
        _pendingPassTeamOrdinal = null;
        _pendingPasserId = -1;
        _pendingPassTick = -1;
        _ = currentTick;
    }

    /// <summary>
    /// Captures the closing state at full time: ending stamina and match minutes for every player. Idempotent;
    /// the first call wins so a caller that steps a finished engine again cannot move the report.
    /// </summary>
    /// <param name="players">The engine's players (authoritative stamina at full time).</param>
    /// <param name="matchClockSeconds">Match clock at full time.</param>
    /// <exception cref="ArgumentNullException"><paramref name="players"/> is null.</exception>
    public void CaptureEnd(IReadOnlyList<FootballerState> players, float matchClockSeconds)
    {
        ArgumentNullException.ThrowIfNull(players);
        if (_captured) return;
        _captured = true;

        float minutes = float.IsFinite(matchClockSeconds) ? MathF.Max(0f, matchClockSeconds) / 60f : 0f;
        for (int index = 0; index < players.Count; index++)
        {
            FootballerState player = players[index];
            MatchPlayerPerformance? row = Get(player.Id);
            if (row is null) continue;
            row.EndingStamina01 = player.Stamina01;

            // Every engine squad member starts the match (the engine models no substitutions), so match
            // minutes are the clock for everyone. A sent-off player's real minutes are shorter, but the
            // engine does not record the dismissal time, and treating a red card as "did not play" would be
            // wrong in the other direction: he did take the field.
            row.MinutesPlayed = minutes;
        }
    }

    /// <summary>True once <see cref="CaptureEnd"/> has run.</summary>
    public bool HasCapturedEnd => _captured;

    private void ApplyContact(in BallContactEvent contact, int currentTick)
    {
        switch (contact.Kind)
        {
            case BallContactKind.Kick:
            {
                // A team-mate playing the ball straight on (a one-touch pass onwards) completes the previous
                // pass, exactly as the team statistics tracker decides it.
                if (_pendingPassTeamOrdinal is int passingTeamOrdinal &&
                    contact.Team == (TeamId)passingTeamOrdinal &&
                    contact.PlayerId != _pendingPasserId)
                {
                    CompletePendingPass(currentTick, contact.PlayerId);
                }

                ResetPassPhase(currentTick);

                if (KickIntents.IsPass(contact.Intent))
                {
                    MatchPlayerPerformance? passer = Get(contact.PlayerId);
                    if (passer is not null)
                    {
                        passer.PassesAttempted++;
                        _pendingPassTeamOrdinal = (int)contact.Team;
                        _pendingPasserId = contact.PlayerId;
                        _pendingPassTick = currentTick;
                    }
                }
                else if (KickIntents.IsShot(contact.Intent))
                {
                    MatchPlayerPerformance? shooter = Get(contact.PlayerId);
                    if (shooter is not null) shooter.Shots++;
                    _lastShotPlayerId = contact.PlayerId;
                    _lastShotTick = currentTick;
                }

                break;
            }

            case BallContactKind.Collection:
            case BallContactKind.Tackle:
            case BallContactKind.Save:
            {
                if (contact.Kind == BallContactKind.Tackle)
                {
                    MatchPlayerPerformance? tackler = Get(contact.PlayerId);
                    if (tackler is not null) tackler.Tackles++;
                }
                else if (contact.Kind == BallContactKind.Save)
                {
                    MatchPlayerPerformance? keeper = Get(contact.PlayerId);
                    if (keeper is not null) keeper.Saves++;
                }

                if (_pendingPassTeamOrdinal is int controlledBy &&
                    contact.Team == (TeamId)controlledBy &&
                    contact.PlayerId != _pendingPasserId)
                {
                    CompletePendingPass(currentTick, contact.PlayerId);
                }

                ResetPassPhase(currentTick);
                break;
            }
        }
    }

    /// <summary>Credits the pending passer with a completion and remembers the link for a possible assist.</summary>
    private void CompletePendingPass(int currentTick, int receiverId)
    {
        MatchPlayerPerformance? passer = Get(_pendingPasserId);
        if (passer is null) return;

        passer.PassesCompleted++;
        _lastCompletedPasserId = _pendingPasserId;
        _lastCompletedReceiverId = receiverId;
        _lastCompletionTick = currentTick;
    }

    private void Booked(int playerId, bool isRed)
    {
        MatchPlayerPerformance? row = Get(playerId);
        if (row is null) return;
        if (isRed) row.RedCards++;
        else row.YellowCards++;
    }
}
