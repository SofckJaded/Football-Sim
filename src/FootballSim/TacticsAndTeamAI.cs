using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;

namespace FootballSim.Tactics;

/// <summary>
/// Owns each team's tactical context (A3.1) and the formation anchors derived from it.
///
/// The context is a set of independent facts, each with its own change policy (A3.2):
/// <list type="bullet">
/// <item><b>possession stability</b> — read from the ownership model every live tick, no dwell;</item>
/// <item><b>claim and phase</b> — a turnover is applied on the tick it happens, never held;</item>
/// <item><b>zone</b> — a spatial deadband plus a structural dwell, so shape does not chase the ball's
/// third every few metres;</item>
/// <item><b>transition</b> — a window that starts on the turnover tick and expires on its own clock;</item>
/// <item><b>restart</b> — armed by the rules engine and cleared by the restart's first touch.</item>
/// </list>
///
/// The previous model had one <c>TeamTacticalState</c> per team and one 3.5 s lock over all of it, so
/// the lock that existed to stop shape flicker could also delay a counter-attack (and hold an
/// attacking state after the ball had been lost). The 3.5 s constant survives here as the *zone*
/// dwell only.
/// </summary>
public sealed class TeamTacticalCoordinator
{
    /// <summary>
    /// How long a new zone is held before another zone change is accepted. Structural: a shape that
    /// re-classifies itself every time the ball crosses a third boundary is not a shape.
    /// </summary>
    private const float StructuralDwellSeconds = 3.5f;

    /// <summary>
    /// How far past a third's boundary the ball has to be before the zone may change. Combined with
    /// the dwell this stops a ball rolling along a boundary from flipping the classification back and
    /// forth.
    /// </summary>
    private const float ZoneBoundaryHysteresisMeters = 3.0f;

    /// <summary>Window after winning the ball in which a direct side is counter-attacking.</summary>
    private const float CounterAttackWindowSeconds = 3.0f;

    /// <summary>Below this directness a team settles into its shape instead of counter-attacking.</summary>
    private const float CounterAttackDirectnessThreshold = 0.65f;

    /// <summary>Below this pressing intensity a team drops into its block instead of counter-pressing.</summary>
    private const float CounterPressPressingThreshold = 0.60f;

    private readonly PitchDimensions _pitch;
    private readonly TeamTacticalProfile _homeProfile;
    private readonly TeamTacticalProfile _awayProfile;
    private readonly TeamFormation _homeFormation;
    private readonly TeamFormation _awayFormation;

    private TeamId? _confirmedPossessingTeam = TeamId.Home;
    private TeamId? _claimTeam = TeamId.Home;
    private float _claimSeconds;
    private PossessionStability _stability = PossessionStability.Loose;
    private TeamZone _homeZone = TeamZone.MiddleThird;
    private TeamZone _awayZone = TeamZone.MiddleThird;
    private float _homeZoneDwell;
    private float _awayZoneDwell;
    private RestartContext _restart;

    public TeamTacticalCoordinator(
        PitchDimensions pitch,
        TeamTacticalProfile? homeProfile = null,
        TeamTacticalProfile? awayProfile = null)
    {
        _pitch = pitch;
        _homeProfile = homeProfile ?? new TeamTacticalProfile();
        _awayProfile = awayProfile ?? new TeamTacticalProfile();
        _homeFormation = new TeamFormation(TeamId.Home, pitch);
        _awayFormation = new TeamFormation(TeamId.Away, pitch);
        RebuildContexts(homeAttackDirX: 1f, ballX: 0f);
    }

    public TeamId? ConfirmedPossessingTeam => _confirmedPossessingTeam;

    /// <summary>Home team's owned nominal formation (A3.3).</summary>
    public TeamFormation HomeFormation => _homeFormation;

    /// <summary>Away team's owned nominal formation (A3.3).</summary>
    public TeamFormation AwayFormation => _awayFormation;

    /// <summary>Either team's nominal formation, by team.</summary>
    public TeamFormation FormationFor(TeamId team) => team == TeamId.Home ? _homeFormation : _awayFormation;

    /// <summary>
    /// Owns each player's place in the team's formation (A3.3). Called once, after the squad or the
    /// fixture has been created: a generated squad uses the pre-match shirt template, a scenario
    /// fragment its own layout. Nothing else assigns nominal ownership, and nothing re-derives it per
    /// tick — that is the whole point of the ownership model.
    /// </summary>
    public void AssignFormations(IReadOnlyList<FootballerState> players, int teamSize, bool fromPositions, float homeAttackDirX)
    {
        if (fromPositions)
        {
            _homeFormation.AssignFromPositions(players, homeAttackDirX);
            _awayFormation.AssignFromPositions(players, -homeAttackDirX);
            return;
        }

        _homeFormation.AssignFromShirts(players, teamSize);
        _awayFormation.AssignFromShirts(players, teamSize);
    }

    /// <summary>Home team's resolved tactical context for the current tick (A3.1).</summary>
    public TeamContext HomeContext { get; private set; }

    /// <summary>Away team's resolved tactical context for the current tick (A3.1).</summary>
    public TeamContext AwayContext { get; private set; }

    /// <summary>Either team's context, by team.</summary>
    public TeamContext ContextFor(TeamId team) => team == TeamId.Home ? HomeContext : AwayContext;

    /// <summary>
    /// How many times the ball's claim changed hands. Diagnostics and tests use it to prove that a
    /// turnover was recognised on a specific tick rather than inferred later from shape.
    /// </summary>
    public int ClaimChanges { get; private set; }

    /// <summary>
    /// Confirms the context from the authoritative control model instead of the last touch (audit F10).
    /// Control changes are applied immediately; a loose ball keeps the team that had it, so a
    /// deflection or an unreachable clearance cannot hand a claim to the team that merely brushed the
    /// ball — but its possession *stability* does drop to <see cref="PossessionStability.Loose"/>,
    /// which is the distinction A3.1 draws. Call this every live tick, before any decision.
    /// </summary>
    public void ObservePossession(
        BallPossessionSnapshot possession,
        BallState ball,
        float homeAttackDirX,
        float dt)
    {
        // A null team means "nobody can say": keep the previous claim rather than inventing one. The
        // ownership model produces that only before the first deliberate play and across a stoppage.
        TeamId? claim = possession.PossessionTeam ?? _confirmedPossessingTeam;

        bool claimChanged = SetClaim(claim);
        if (claimChanged)
        {
            // A turnover is an event, not structural drift: both zones are re-read from where the ball
            // is now instead of being held by the dwell of the previous passage.
            _homeZone = ZoneOf(ball.Position.X * homeAttackDirX);
            _awayZone = ZoneOf(ball.Position.X * -homeAttackDirX);
            _homeZoneDwell = 0f;
            _awayZoneDwell = 0f;
        }
        else
        {
            _claimSeconds += dt;
            _homeZoneDwell = MathF.Max(0f, _homeZoneDwell - dt);
            _awayZoneDwell = MathF.Max(0f, _awayZoneDwell - dt);
        }

        _stability = MapStability(possession.State);

        float homeProgress = ball.Position.X * homeAttackDirX;
        float awayProgress = -homeProgress;
        bool homeEmergency = IsEmergencyDefence(TeamId.Home, homeProgress);
        bool awayEmergency = IsEmergencyDefence(TeamId.Away, awayProgress);

        if (!claimChanged)
        {
            AdvanceZone(ref _homeZone, ref _homeZoneDwell, homeProgress, homeEmergency);
            AdvanceZone(ref _awayZone, ref _awayZoneDwell, awayProgress, awayEmergency);
        }

        HomeContext = BuildContext(TeamId.Home, _homeZone, homeEmergency);
        AwayContext = BuildContext(TeamId.Away, _awayZone, awayEmergency);
    }

    /// <summary>
    /// Mirrors the restart the rules engine has armed (A3.1). An armed restart is an exclusive, legal
    /// claim: the ball belongs to the restarting team until the first touch, even though the ownership
    /// tracker has nothing to say during the stoppage. It is also a new passage, so it resets the
    /// transition clock and both zone dwells. Call this every tick, before any decision.
    /// </summary>
    public void ObserveRestart(RestartKind kind, TeamId restartingTeam, BallState ball, float homeAttackDirX)
    {
        RestartContext restart = kind == RestartKind.None
            ? RestartContext.None
            : new RestartContext(kind, restartingTeam);

        if (restart == _restart) return;
        _restart = restart;

        if (restart.IsArmed)
        {
            SetClaim(restartingTeam);
            // A restart is a new passage even when the claim did not change hands: a quick throw-in
            // still starts the restarting side's counter-attack window.
            _claimSeconds = 0f;
            _homeZone = ZoneOf(ball.Position.X * homeAttackDirX);
            _awayZone = ZoneOf(ball.Position.X * -homeAttackDirX);
            _homeZoneDwell = 0f;
            _awayZoneDwell = 0f;
        }

        RebuildContexts(homeAttackDirX, ball.Position.X);
    }

    /// <summary>
    /// Declares the confirmed possessing team directly. Only scenario fragments need this: a real
    /// match learns possession from the ownership tracker on the first live tick, but a fragment that
    /// starts mid-play must not spend its first ticks acting as if the other team had the ball.
    /// </summary>
    public void SeedPossession(TeamId? team)
    {
        _claimTeam = team;
        _confirmedPossessingTeam = team;
        _claimSeconds = 0f;
        _stability = PossessionStability.Loose;
        _restart = RestartContext.None;
        _homeZoneDwell = 0f;
        _awayZoneDwell = 0f;
        RebuildContexts(homeAttackDirX: 1f, ballX: 0f);
    }

    /// <summary>
    /// Writes every active player's formation anchor from his team's context and the slot he owns
    /// (A3.3). The anchors are the structural fallback and the constraint the job coordinator works
    /// around, not a second world model; the context reads above own every decision that used to be
    /// folded into the old state enum.
    ///
    /// A5.6: this used to take a <c>dt</c> and keep an elapsed-time clock, because the role push it writes
    /// oscillated with it. The oscillation is gone (see the note at the role push), so the anchor is a pure
    /// function of the players, the ball, the context and the profile — which also makes it checkable.
    /// </summary>
    public void UpdateTeamAnchors(
        IReadOnlyList<FootballerState> players,
        BallState ball,
        float homeAttackDirX)
    {
        foreach (var p in players)
        {
            if (!p.IsActiveOnPitch) continue;

            TeamTacticalProfile profile = p.Team == TeamId.Home ? _homeProfile : _awayProfile;
            TeamContext context = p.Team == TeamId.Home ? HomeContext : AwayContext;
            TeamFormation formation = p.Team == TeamId.Home ? _homeFormation : _awayFormation;
            float attackDirX = p.Team == TeamId.Home ? homeAttackDirX : -homeAttackDirX;

            // The keeper is not part of the outfield formation (A3.3): his position is his own line,
            // and he holds no slot that a teammate could inherit or protect.
            if (p.Role == TacticalRole.Goalkeeper)
            {
                p.TacticalAnchor = ComputeGoalkeeperAnchor(ball, profile, context, attackDirX);
                continue;
            }

            NominalSlot slot = SlotFor(p, formation, attackDirX);
            p.TacticalAnchor = ComputePlayerAnchor(p, slot, ball, profile, context, attackDirX);
        }
    }

    /// <summary>
    /// The slot this player owns, or a first assignment derived from where he stands. A coordinator
    /// built without <see cref="AssignFormations"/> (a probe, a bare unit test) still needs a place:
    /// it is derived once and held, exactly as a fixture's layout would be.
    /// </summary>
    private NominalSlot SlotFor(FootballerState player, TeamFormation formation, float attackDirX)
    {
        if (formation.TryGet(player.Id, out NominalSlot slot)) return slot;

        float progress = player.Position.X * attackDirX;
        slot = new NominalSlot(
            player.Role,
            Math.Clamp(progress / (_pitch.HalfLength * TeamFormation.NormXToWorldScale), -1f, 1f),
            Math.Clamp(player.Position.Z / _pitch.HalfWidth, -1f, 1f));
        formation.Assign(player.Id, slot);
        return slot;
    }

    /// <summary>
    /// One zone step with the two policies that keep structural classification slow but honest: the
    /// change must clear the boundary by the hysteresis margin, and it must survive the dwell — unless
    /// the team is under emergency defence, where the opponent is imposing the shape (A3.2).
    /// </summary>
    private void AdvanceZone(ref TeamZone zone, ref float dwell, float ballProgress, bool emergency)
    {
        TeamZone desired = DesiredZone(zone, ballProgress);
        if (desired == zone) return;
        if (!emergency && dwell > 0f) return;

        zone = desired;
        dwell = StructuralDwellSeconds;
    }

    /// <summary>The band the ball is in, with the deadband measured from the band currently held.</summary>
    private TeamZone DesiredZone(TeamZone current, float ballProgress)
    {
        float boundary = _pitch.HalfLength / 3f;
        float finalEdge = boundary + ZoneBoundaryHysteresisMeters;
        float ownEdge = -finalEdge;
        float finalExit = boundary - ZoneBoundaryHysteresisMeters;
        float ownExit = -finalExit;

        return current switch
        {
            TeamZone.FinalThird => ballProgress >= finalExit
                ? TeamZone.FinalThird
                : ballProgress <= ownEdge ? TeamZone.OwnThird : TeamZone.MiddleThird,
            TeamZone.OwnThird => ballProgress <= ownExit
                ? TeamZone.OwnThird
                : ballProgress >= finalEdge ? TeamZone.FinalThird : TeamZone.MiddleThird,
            _ => ballProgress >= finalEdge
                ? TeamZone.FinalThird
                : ballProgress <= ownEdge ? TeamZone.OwnThird : TeamZone.MiddleThird
        };
    }

    private TeamZone ZoneOf(float ballProgress)
    {
        float boundary = _pitch.HalfLength / 3f;
        if (ballProgress >= boundary) return TeamZone.FinalThird;
        if (ballProgress <= -boundary) return TeamZone.OwnThird;
        return TeamZone.MiddleThird;
    }

    private TeamContext BuildContext(TeamId team, TeamZone zone, bool emergency)
    {
        bool hasClaim = _claimTeam == team;
        TeamTacticalProfile profile = team == TeamId.Home ? _homeProfile : _awayProfile;
        return new TeamContext(
            team,
            _claimTeam,
            _stability,
            hasClaim ? TeamPhase.Attacking : TeamPhase.Defending,
            zone,
            ResolveTransition(profile, hasClaim),
            _restart,
            emergency);
    }

    private void RebuildContexts(float homeAttackDirX, float ballX)
    {
        float homeProgress = ballX * homeAttackDirX;
        float awayProgress = -homeProgress;
        HomeContext = BuildContext(TeamId.Home, _homeZone, IsEmergencyDefence(TeamId.Home, homeProgress));
        AwayContext = BuildContext(TeamId.Away, _awayZone, IsEmergencyDefence(TeamId.Away, awayProgress));
    }

    /// <summary>
    /// Sets the claim, reporting whether it changed. Resetting the transition clock here is what makes
    /// "we just won it" and "we just lost it" measurable on the turnover tick itself.
    /// </summary>
    private bool SetClaim(TeamId? claim)
    {
        if (claim == _claimTeam) return false;

        _claimTeam = claim;
        _confirmedPossessingTeam = claim;
        _claimSeconds = 0f;
        ClaimChanges++;
        return true;
    }

    /// <summary>
    /// A3.2: a transition is a maximum window measured from the claim change, not a state with a dwell.
    /// A direct side counter-attacks for <see cref="CounterAttackWindowSeconds"/>; a pressing side has
    /// <see cref="TeamTacticalProfile.CounterPressDurationSeconds"/> to win the ball back.
    /// </summary>
    private TransitionMode ResolveTransition(TeamTacticalProfile profile, bool hasClaim)
    {
        if (hasClaim)
        {
            return _claimSeconds < CounterAttackWindowSeconds &&
                   profile.Directness01 > CounterAttackDirectnessThreshold
                ? TransitionMode.CounterAttack
                : TransitionMode.None;
        }

        return _claimSeconds < profile.CounterPressDurationSeconds &&
               profile.PressingIntensity01 > CounterPressPressingThreshold
            ? TransitionMode.CounterPress
            : TransitionMode.None;
    }

    /// <summary>
    /// A3.2: the opponent has the ball under control inside this team's own third. A loose ball there
    /// is not an emergency — it is exactly the case shape should be allowed to settle through.
    /// </summary>
    private bool IsEmergencyDefence(TeamId team, float ballProgress)
    {
        if (_claimTeam == team) return false;
        if (_stability == PossessionStability.Loose) return false;
        return ballProgress <= -_pitch.HalfLength / 3f;
    }

    private static PossessionStability MapStability(BallControlState state) => state switch
    {
        BallControlState.Controlled => PossessionStability.Controlled,
        BallControlState.InFlightPass => PossessionStability.Passing,
        BallControlState.KeeperHeld => PossessionStability.KeeperHeld,
        _ => PossessionStability.Loose
    };

    private Vector3 ComputePlayerAnchor(
        FootballerState player,
        in NominalSlot slot,
        BallState ball,
        TeamTacticalProfile profile,
        in TeamContext context,
        float attackDirX)
    {
        bool attacking = context.Phase == TeamPhase.Attacking;

        float baseNormX = slot.NormX;
        float baseNormZ = slot.NormZ;

        float ballNormX = Math.Clamp((ball.Position.X * attackDirX) / _pitch.HalfLength, -1f, 1f);
        float lineShift = (profile.DefensiveLineHeight01 - 0.5f) * 18.0f;
        float longitudinalCompression = attacking ? (1.0f - 0.22f * profile.Compactness01) : (1.0f - 0.45f * profile.Compactness01);

        // Role-specific dynamic forward runs when in possession.
        //
        // A5.6: these used to oscillate — `Sin(_elapsedTime * 1.4f + player.Id)` and friends — which is a
        // generator of movement rather than a reason for it. The plan asks for the sinusoidal pushes to go
        // "as intent-driven movement replaces them", and it has: a receiving duty now runs a set of timed
        // paths (A5.1/A5.2) with a meeting region and a window, and a runner's movement comes from that
        // rather than from standing in a lane that breathes. What is left here is the role's structural
        // station in possession, which is a property of the shape, not of the clock. A static anchor is also
        // the one that can be checked: `TeamShapeMetricsTests.AStillBall_DoesNotMoveTheAnchor`.
        float roleAttackPush = 0f;
        if (attacking)
        {
            roleAttackPush = slot.Role switch
            {
                TacticalRole.Striker => 10.0f,
                TacticalRole.Winger => 9.0f,
                TacticalRole.CentralMidfielder => 6.5f,
                TacticalRole.FullBack => 7.0f * profile.AttackingWidth01,
                _ => 2.5f
            };
        }

        float worldX = attackDirX * (
            baseNormX * (_pitch.HalfLength * TeamFormation.NormXToWorldScale) * longitudinalCompression +
            ballNormX * 11.5f +
            lineShift +
            (attacking ? roleAttackPush * 0.65f : -4.0f));

        float widthScale = attacking
            ? (0.72f + 0.25f * profile.AttackingWidth01)
            : (0.70f - 0.15f * profile.Compactness01);

        // Keep players in their horizontal lanes instead of collapsing onto the ball's Z coordinate
        float ballLateralShift = attacking
            ? ball.Position.Z * 0.08f
            : ball.Position.Z * 0.12f;

        float worldZ = baseNormZ * _pitch.HalfWidth * widthScale + ballLateralShift;

        worldX = Math.Clamp(worldX, -_pitch.HalfLength + 4.0f, _pitch.HalfLength - 4.0f);
        worldZ = Math.Clamp(worldZ, -_pitch.HalfWidth + 2.5f, _pitch.HalfWidth - 2.5f);

        return new Vector3(worldX, 0f, worldZ);
    }

    /// <summary>
    /// The keeper's own line. He steps up with the side he plays behind, but never during a
    /// counter-attack: the ball is about to go the other way and the goal is the thing to protect.
    /// </summary>
    private Vector3 ComputeGoalkeeperAnchor(
        BallState ball,
        TeamTacticalProfile profile,
        in TeamContext context,
        float attackDirX)
    {
        float gkBaseX = -attackDirX * (_pitch.HalfLength - 2.5f);
        float sweepOffset = context.Phase == TeamPhase.Attacking &&
                            context.Transition != TransitionMode.CounterAttack
            ? attackDirX * (4.5f * profile.DefensiveLineHeight01)
            : 0f;
        float gkZ = Math.Clamp(ball.Position.Z * 0.14f, -3.1f, 3.1f);
        return new Vector3(gkBaseX + sweepOffset, 0f, gkZ);
    }

    /// <summary>
    /// The pre-match template: which role and normalized slot a shirt number starts with, for a
    /// generated squad. This is read **once**, at setup (<see cref="TeamFormation.AssignFromShirts"/>,
    /// and kickoff placement), and never in a per-tick path: after ownership is assigned, a player's
    /// place in the formation comes from his slot, not from his shirt (A3.3).
    /// </summary>
    public static (TacticalRole Role, float NormX, float NormZ) GetFormationRoleAndSlot(int shirtNumber, int teamSize)
    {
        if (shirtNumber == 1) return (TacticalRole.Goalkeeper, -0.95f, 0.0f);

        if (teamSize <= 5)
        {
            return shirtNumber switch
            {
                2 => (TacticalRole.CenterBack, -0.45f, 0.0f),
                3 => (TacticalRole.Winger, 0.0f, -0.55f),
                4 => (TacticalRole.Winger, 0.0f, 0.55f),
                _ => (TacticalRole.Striker, 0.45f, 0.0f)
            };
        }

        return shirtNumber switch
        {
            2 => (TacticalRole.FullBack, -0.48f, 0.72f),
            3 => (TacticalRole.FullBack, -0.48f, -0.72f),
            4 => (TacticalRole.CenterBack, -0.55f, 0.26f),
            5 => (TacticalRole.CenterBack, -0.55f, -0.26f),
            6 => (TacticalRole.DefensiveMidfielder, -0.22f, 0.0f),
            7 => (TacticalRole.Winger, 0.38f, 0.75f),
            8 => (TacticalRole.CentralMidfielder, 0.02f, 0.34f),
            9 => (TacticalRole.Striker, 0.52f, 0.0f),
            10 => (TacticalRole.CentralMidfielder, 0.06f, -0.34f),
            11 => (TacticalRole.Winger, 0.38f, -0.75f),
            _ => (TacticalRole.CentralMidfielder, 0.0f, 0.0f)
        };
    }
}
