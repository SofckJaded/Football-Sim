using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;

namespace FootballSim;

/// <summary>
/// Owns the match's ball-ownership model (audit F10). It consumes the typed contact events the
/// execution layer already publishes and answers, every tick:
///
/// <list type="bullet">
/// <item>is the ball loose, controlled, in flight as a planned pass, or held by a keeper;</item>
/// <item>which player controls it;</item>
/// <item>who a pass in flight is actually meant for.</item>
/// </list>
///
/// This is deliberately separate from <see cref="BallState.LastTouchedByTeam"/>: a deflection or a
/// parry changes the last touch without anybody controlling the ball, which is what made the old
/// possession heuristic hand the ball to the team that merely brushed it. A deliberate play (kick,
/// tackle, control touch) does establish the playing team's claim, which keeps match flow after
/// clearances.
/// </summary>
public sealed class BallPossessionTracker
{
    private const float MaxControlDistance = 3.0f;
    private const float SettledPassSpeed = 2.0f;
    private const float SettledPassGraceSeconds = 0.35f;
    private const float MaxPassFlightSeconds = 8.0f;

    private BallControlState _state = BallControlState.Loose;
    private TeamId? _possessionTeam;
    private int? _controllingPlayerId;
    private int? _intendedReceiverId;
    private TeamId? _lastPlayTeam;
    private float _elapsedInState;

    public BallPossessionSnapshot Current =>
        new(_state, _possessionTeam, _controllingPlayerId, _intendedReceiverId);

    /// <summary>Forgets all ownership state, e.g. across a restart or a drill reset.</summary>
    public void Reset()
    {
        _state = BallControlState.Loose;
        _possessionTeam = null;
        _controllingPlayerId = null;
        _intendedReceiverId = null;
        _lastPlayTeam = null;
        _elapsedInState = 0f;
    }

    /// <summary>
    /// Declares the ownership state directly, for a match that starts mid-play (a scenario fragment)
    /// rather than at a kickoff. Normal play never needs this: ownership is learned from contacts.
    ///
    /// The next <see cref="Update"/> still validates the seed — a controlled ball whose carrier is
    /// more than 3 m away is dropped to loose on the first tick, so a fixture cannot create a
    /// possession the physics would not allow.
    /// </summary>
    public void Seed(BallPossessionSnapshot snapshot)
    {
        _state = snapshot.State;
        _possessionTeam = snapshot.PossessionTeam;
        _controllingPlayerId = snapshot.ControllingPlayerId;
        _intendedReceiverId = snapshot.IntendedReceiverId;

        // A seeded deliberate play is the last team to have played the ball, so a loose ball
        // following it keeps the same claim it would have had after a real kick.
        _lastPlayTeam = snapshot.PossessionTeam;

        // A seeded in-flight pass is allowed to settle immediately; a seeded control is not.
        _elapsedInState = 0f;
    }

    public void Update(
        IReadOnlyList<FootballerState> players,
        BallState ball,
        IReadOnlyList<BallContactEvent>? contacts,
        float dt)
    {
        if (float.IsFinite(dt) && dt > 0f)
            _elapsedInState += dt;

        if (contacts is { Count: > 0 })
        {
            foreach (BallContactEvent contact in contacts)
                ApplyContact(contact);
        }

        Validate(players, ball);
    }

    private void ApplyContact(in BallContactEvent contact)
    {
        switch (contact.Kind)
        {
            case BallContactKind.Kick:
                _lastPlayTeam = contact.Team;
                if (KickIntents.IsPass(contact.Intent))
                    Set(BallControlState.InFlightPass, contact.Team, null, contact.IntendedReceiverId);
                else
                    SetLoose();
                break;

            case BallContactKind.Tackle:
            case BallContactKind.Collection:
                _lastPlayTeam = contact.Team;
                Set(BallControlState.Controlled, contact.Team, contact.PlayerId, null);
                break;

            case BallContactKind.Save:
                // A catch is a held ball; a parry is only a deflection. The deflection must not
                // hand team possession to the keeper's team (audit F10), so it leaves the ball
                // loose and keeps the previous play's team in possession.
                if (contact.IsParry)
                    SetLoose();
                else
                    Set(BallControlState.KeeperHeld, contact.Team, contact.PlayerId, null);
                break;
        }
    }

    /// <summary>Drops control once the carrier is gone or has left the ball behind.</summary>
    private void Validate(IReadOnlyList<FootballerState> players, BallState ball)
    {
        if (_state is BallControlState.Controlled or BallControlState.KeeperHeld)
        {
            FootballerState? carrier = FindPlayer(players, _controllingPlayerId);
            if (carrier is null || !carrier.IsActiveOnPitch ||
                PlanarDistance(carrier.Position, ball.Position) > MaxControlDistance)
            {
                SetLoose();
            }
            return;
        }

        if (_state != BallControlState.InFlightPass)
            return;

        bool settled = _elapsedInState > SettledPassGraceSeconds &&
                       HorizontalSpeed(ball.Velocity) < SettledPassSpeed;
        bool timedOut = _elapsedInState > MaxPassFlightSeconds;
        bool invalid = !SimMath.IsFinite(ball.Velocity);
        if (settled || timedOut || invalid)
            SetLoose();
    }

    /// <summary>
    /// The ball is nobody's to control right now. Team possession stays with the team that last
    /// <i>deliberately played</i> it (a kick, a tackle or a control touch) — that is what keeps the
    /// match flowing after a clearance or a shot. A save/deflection never updates that team.
    /// </summary>
    private void SetLoose() => Set(BallControlState.Loose, _lastPlayTeam, null, null);

    private void Set(BallControlState state, TeamId? team, int? controllingPlayerId, int? intendedReceiverId)
    {
        if (_state != state || _possessionTeam != team || _controllingPlayerId != controllingPlayerId)
            _elapsedInState = 0f;

        _state = state;
        _possessionTeam = team;
        _controllingPlayerId = controllingPlayerId;
        _intendedReceiverId = intendedReceiverId;
    }

    private static FootballerState? FindPlayer(IReadOnlyList<FootballerState> players, int? playerId)
    {
        if (!playerId.HasValue) return null;
        foreach (FootballerState player in players)
            if (player.Id == playerId.Value) return player;
        return null;
    }

    private static float PlanarDistance(Vector3 a, Vector3 b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static float HorizontalSpeed(Vector3 velocity) =>
        MathF.Sqrt(velocity.X * velocity.X + velocity.Z * velocity.Z);
}

/// <summary>
/// Elects one ball responder per team (audit F10). Every AI used to decide this independently from
/// its own noisy observations, and it counted the human as a competitor, so an idle human near a
/// loose ball silently switched the whole AI team off the ball. The election is central, uses
/// time-to-reach, respects the intended receiver of a pass, and has hysteresis so the same player
/// keeps the job instead of flickering between candidates.
/// </summary>
public sealed class BallResponderElection
{
    private const float RetainMarginSeconds = 0.35f;
    private const float InterceptTimeCapSeconds = 2.5f;
    private const float MinimumApproachSpeed = 2.0f;

    private readonly PitchDimensions _pitch;
    private readonly Dictionary<TeamId, int> _responders = new();

    public BallResponderElection(PitchDimensions pitch)
    {
        _pitch = pitch;
    }

    public int? GetResponder(TeamId team) =>
        _responders.TryGetValue(team, out int playerId) ? playerId : null;

    public bool IsResponder(TeamId team, int playerId) => GetResponder(team) == playerId;

    public void Update(
        IReadOnlyList<FootballerState> players,
        BallState ball,
        BallPossessionSnapshot possession,
        int? humanPlayerId)
    {
        SetResponder(TeamId.Home, ElectTeam(TeamId.Home, players, ball, possession, humanPlayerId));
        SetResponder(TeamId.Away, ElectTeam(TeamId.Away, players, ball, possession, humanPlayerId));
    }

    private int? ElectTeam(
        TeamId team,
        IReadOnlyList<FootballerState> players,
        BallState ball,
        BallPossessionSnapshot possession,
        int? humanPlayerId)
    {
        // The team that controls the ball needs support shape, not a chaser.
        if (possession.IsControlledBy(team))
            return null;

        // A pass in flight: the planned receiver goes for it. A human receiver is left alone —
        // they were asked for the ball, so the AI must not crowd them out of the reception.
        if (possession.State == BallControlState.InFlightPass && possession.PossessionTeam == team)
        {
            if (possession.IntendedReceiverId is int receiverId)
            {
                FootballerState? receiver = FindPlayer(players, receiverId);
                if (receiver is not null && receiver.Team == team && receiver.IsActiveOnPitch &&
                    receiver.Role != TacticalRole.Goalkeeper)
                {
                    return receiverId == humanPlayerId ? null : receiverId;
                }
            }
        }

        // Loose ball, opponent control, or an interception: nearest eligible AI by time-to-reach.
        Vector2 interceptPoint = PredictInterceptPoint(players, ball, team, humanPlayerId);
        int? best = null;
        float bestTime = float.MaxValue;

        foreach (FootballerState player in players)
        {
            if (!IsCandidate(player, team, humanPlayerId)) continue;
            float time = TimeToReach(player, interceptPoint);
            if (time < bestTime || (MathF.Abs(time - bestTime) < 1e-4f && best.HasValue && player.Id < best.Value))
            {
                bestTime = time;
                best = player.Id;
            }
        }

        if (best is null) return null;

        // Hysteresis: keep the current responder unless a rival is clearly quicker.
        if (_responders.TryGetValue(team, out int previous) && previous != best.Value)
        {
            FootballerState? incumbent = FindPlayer(players, previous);
            if (incumbent is not null && IsCandidate(incumbent, team, humanPlayerId))
            {
                float incumbentTime = TimeToReach(incumbent, interceptPoint);
                if (incumbentTime <= bestTime + RetainMarginSeconds)
                    return previous;
            }
        }

        return best;
    }

    /// <summary>
    /// AI candidates only. The human is never elected: they decide for themselves, and counting
    /// them as the nearest player was exactly what made an idle human switch the AI team off the
    /// ball (audit F10).
    /// </summary>
    private static bool IsCandidate(FootballerState player, TeamId team, int? humanPlayerId) =>
        player.Team == team &&
        player.IsActiveOnPitch &&
        player.Role != TacticalRole.Goalkeeper &&
        player.Id != humanPlayerId;

    /// <summary>
    /// Where the ball will be when the quickest chaser could meet it. One point for the whole team
    /// keeps the ranking consistent between candidates (and therefore stable).
    /// </summary>
    private Vector2 PredictInterceptPoint(
        IReadOnlyList<FootballerState> players,
        BallState ball,
        TeamId team,
        int? humanPlayerId)
    {
        Vector2 ballXZ = new(ball.Position.X, ball.Position.Z);
        Vector2 ballVelocity = new(ball.Velocity.X, ball.Velocity.Z);
        float speed = ballVelocity.Length();
        if (speed < 1.0f) return ballXZ;

        // Earliest point at which any candidate could be on the ball: smallest time-to-close,
        // approximated with a constant-velocity closest approach.
        float closestTime = InterceptTimeCapSeconds;
        foreach (FootballerState player in players)
        {
            if (!IsCandidate(player, team, humanPlayerId)) continue;
            Vector2 relative = ballXZ - new Vector2(player.Position.X, player.Position.Z);
            float time = -Vector2.Dot(relative, ballVelocity) / MathF.Max(1e-4f, speed * speed);
            if (time >= 0f && time < closestTime)
                closestTime = time;
        }

        Vector2 point = ballXZ + ballVelocity * closestTime;
        return new Vector2(
            Math.Clamp(point.X, -_pitch.HalfLength + 1f, _pitch.HalfLength - 1f),
            Math.Clamp(point.Y, -_pitch.HalfWidth + 1f, _pitch.HalfWidth - 1f));
    }

    private static float TimeToReach(FootballerState player, Vector2 targetXZ)
    {
        float distance = Vector2.Distance(new Vector2(player.Position.X, player.Position.Z), targetXZ);
        float speed = MathF.Max(MinimumApproachSpeed, player.Attributes.MaxSprintSpeed * 0.85f);
        return distance / speed;
    }

    private void SetResponder(TeamId team, int? playerId)
    {
        if (playerId.HasValue) _responders[team] = playerId.Value;
        else _responders.Remove(team);
    }

    private static FootballerState? FindPlayer(IReadOnlyList<FootballerState> players, int playerId)
    {
        foreach (FootballerState player in players)
            if (player.Id == playerId) return player;
        return null;
    }
}
