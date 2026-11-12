using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A3.1/A3.2 — the team context and its hysteresis.
///
/// A3.1 separates four facts that one <c>TeamTacticalState</c> used to conflate: how firmly the ball is
/// held, who has the right to it, which third of the pitch it is in, and whether a transition or a
/// restart is in force. The gate statement for the first part is the deflection: a loose ball must not
/// erase the claiming team's phase, and it must not be read as stable control either.
///
/// A3.2 replaces the single 3.5 s state lock with a policy per fact: a turnover changes the phase and
/// the transition on the tick it happens, while a positional perturbation has to beat a deadband and a
/// structural dwell before it changes the zone.
/// </summary>
public sealed class TeamContextTests
{
    private const float Dt = SimConstants.FixedDeltaTime;

    private static TeamTacticalCoordinator Coordinator() => new(
        new PitchDimensions(),
        TeamTacticalProfile.PossessionControl,
        TeamTacticalProfile.DirectCounterattack);

    private static FootballerState Player(int id, TeamId team, TacticalRole role, Vector3 position) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(role),
        Position = position,
        FacingXZ = new Vector2(1f, 0f)
    };

    private static BallState BallAt(Vector3 position) => new() { Position = position, LastTouchedByTeam = TeamId.Home };

    private static BallPossessionSnapshot Controlled(TeamId team, int playerId) =>
        new(BallControlState.Controlled, team, playerId, null);

    private static BallPossessionSnapshot LooseOf(TeamId claimHolder) =>
        new(BallControlState.Loose, claimHolder, null, null);

    [Fact]
    public void ALooseBall_KeepsTheClaimsPhase_WhileItsStabilityFalls()
    {
        // The ownership model really produces the case: a parry flips the last touch and leaves the
        // ball loose, but the team that last deliberately played it keeps the claim (audit F10).
        var players = new[]
        {
            Player(8, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(-0.4f, 0f, 0f)),
            Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(14f, 0f, 3f)),
            Player(12, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(0f, 0f, 2f)),
            Player(13, TeamId.Away, TacticalRole.CenterBack, new Vector3(4f, 0f, 0f))
        };
        var ball = BallAt(new Vector3(0f, 0f, 0f));
        var tracker = new BallPossessionTracker();
        var coordinator = Coordinator();

        tracker.Update(players, ball,
            new[] { new BallContactEvent(1, BallContactKind.Collection, 8, TeamId.Home, ball.Position) }, Dt);
        coordinator.ObservePossession(tracker.Current, ball, 1f, Dt);

        Assert.Equal(PossessionStability.Controlled, coordinator.HomeContext.Stability);
        Assert.Equal(TeamPhase.Attacking, coordinator.HomeContext.Phase);
        Assert.True(coordinator.HomeContext.HasStableControl);
        Assert.Equal(TeamPhase.Defending, coordinator.AwayContext.Phase);

        // The away keeper parries it away: last touch is now Away, but nobody controls the ball.
        ball.LastTouchedByTeam = TeamId.Away;
        ball.LastTouchedByPlayerId = 12;
        ball.Velocity = new Vector3(-5f, 0f, 1f);
        tracker.Update(players, ball,
            new[] { new BallContactEvent(2, BallContactKind.Save, 12, TeamId.Away, ball.Position) { IsParry = true } }, Dt);
        coordinator.ObservePossession(tracker.Current, ball, 1f, Dt);

        Assert.Equal(BallControlState.Loose, tracker.Current.State);

        // The two facts now disagree, which is the whole point of splitting them.
        Assert.Equal(TeamId.Home, coordinator.HomeContext.ClaimedTeam);
        Assert.Equal(TeamPhase.Attacking, coordinator.HomeContext.Phase);
        Assert.Equal(PossessionStability.Loose, coordinator.HomeContext.Stability);
        Assert.False(coordinator.HomeContext.HasStableControl);
        Assert.Equal(TeamId.Home, coordinator.ConfirmedPossessingTeam);

        // A deflection is not a turnover.
        Assert.Equal(0, coordinator.ClaimChanges);
    }

    [Fact]
    public void AnArmedRestart_ClaimsTheBallForTheRestartingTeam_AndClearsOnItsFirstTouch()
    {
        var coordinator = Coordinator();
        var ball = BallAt(new Vector3(0f, 0f, 0f));
        coordinator.ObservePossession(Controlled(TeamId.Home, 8), ball, 1f, Dt);
        Assert.Equal(TeamId.Home, coordinator.ConfirmedPossessingTeam);

        // A throw-in to the away team, 20 m from the home goal: not the home team's passage any more.
        ball.Position = new Vector3(-20f, 0f, 8f);
        coordinator.ObserveRestart(RestartKind.ThrowIn, TeamId.Away, ball, 1f);

        Assert.True(coordinator.AwayContext.Restart.IsArmed);
        Assert.Equal(RestartKind.ThrowIn, coordinator.AwayContext.Restart.Kind);
        Assert.True(coordinator.AwayContext.Restart.IsFor(TeamId.Away));
        Assert.False(coordinator.AwayContext.Restart.IsFor(TeamId.Home));
        Assert.Equal(TeamId.Away, coordinator.ConfirmedPossessingTeam);
        Assert.Equal(TeamId.Away, coordinator.AwayContext.ClaimedTeam);
        Assert.Equal(TeamPhase.Attacking, coordinator.AwayContext.Phase);
        Assert.Equal(TeamPhase.Defending, coordinator.HomeContext.Phase);
        Assert.Equal(TeamZone.FinalThird, coordinator.AwayContext.Zone);

        // The taker's first touch stamps the restart, so the rules engine has no restart armed. In
        // open play the context keeps the claim the live ownership model last confirmed.
        coordinator.ObserveRestart(RestartKind.None, TeamId.Away, ball, 1f);

        Assert.False(coordinator.AwayContext.Restart.IsArmed);
        Assert.Equal(TeamId.Away, coordinator.AwayContext.ClaimedTeam);
    }

    [Fact]
    public void TheTeamContext_ReachesEveryPlayersObservation()
    {
        var context = new TeamContext(
            TeamId.Home, TeamId.Home, PossessionStability.Loose, TeamPhase.Attacking, TeamZone.FinalThird,
            TransitionMode.CounterAttack, new RestartContext(RestartKind.CornerKick, TeamId.Home), false);

        var player = Player(7, TeamId.Home, TacticalRole.Winger, new Vector3(10f, 0f, 4f));
        FootballerObservation observation = new PerceptionSystem().BuildObservation(
            player, new[] { player }, BallAt(new Vector3(12f, 0f, 4f)), TeamId.Home,
            MatchPhase.FirstHalf, 30f, 1f, isSetPieceTaker: true,
            TeamTacticalProfile.PossessionControl, new SeededRandom(11), teamContext: context);

        Assert.Equal(context, observation.TacticalContext);
        Assert.True(observation.TacticalContext.Restart.IsFor(TeamId.Home));
        Assert.Equal(TransitionMode.CounterAttack, observation.TacticalContext.Transition);

        // A controller built without a match gets the neutral context, never an invented attack.
        FootballerObservation standalone = new PerceptionSystem().BuildObservation(
            player, new[] { player }, BallAt(new Vector3(12f, 0f, 4f)), TeamId.Home,
            MatchPhase.FirstHalf, 30f, 1f, isSetPieceTaker: false,
            TeamTacticalProfile.PossessionControl, new SeededRandom(11));

        Assert.Equal(TeamPhase.Defending, standalone.TacticalContext.Phase);
        Assert.Null(standalone.TacticalContext.ClaimedTeam);
    }

    [Fact]
    public void ATurnover_ChangesTheTransitionOnThatTick_AndTheWindowExpiresOnItsOwnClock()
    {
        var coordinator = Coordinator();
        var ball = BallAt(new Vector3(0f, 0f, 0f));

        coordinator.ObservePossession(Controlled(TeamId.Home, 8), ball, 1f, Dt);
        for (int i = 0; i < 300; i++)
            coordinator.ObservePossession(Controlled(TeamId.Home, 8), ball, 1f, Dt);

        // Home is a possession side (not direct enough to counter); Away is direct but does not press.
        Assert.Equal(TransitionMode.None, coordinator.HomeContext.Transition);
        Assert.Equal(TransitionMode.None, coordinator.AwayContext.Transition);

        // Away wins the ball with a tackle. Both windows open on this very call: the winner can
        // counter, the loser presses — no dwell timer can hold either of them.
        ball.Position = new Vector3(-6f, 0f, 0f);
        coordinator.ObservePossession(Controlled(TeamId.Away, 13), ball, 1f, Dt);

        Assert.Equal(1, coordinator.ClaimChanges);
        Assert.Equal(TeamPhase.Defending, coordinator.HomeContext.Phase);
        Assert.Equal(TeamPhase.Attacking, coordinator.AwayContext.Phase);
        Assert.Equal(TransitionMode.CounterAttack, coordinator.AwayContext.Transition);
        Assert.Equal(TransitionMode.CounterPress, coordinator.HomeContext.Transition);

        // The counter-attack window is 3 s; the away profile's press window is 2 s and the home
        // profile's is 4.5 s, so 5.5 s clears both without any dwell being involved.
        for (int i = 0; i < 330; i++)
            coordinator.ObservePossession(Controlled(TeamId.Away, 13), ball, 1f, Dt);

        Assert.Equal(TransitionMode.None, coordinator.AwayContext.Transition);
        Assert.Equal(TransitionMode.None, coordinator.HomeContext.Transition);
        Assert.Equal(1, coordinator.ClaimChanges);
    }

    [Fact]
    public void APositionalPerturbation_InsideTheDwell_DoesNotChangeTheZone()
    {
        var coordinator = Coordinator();
        var ball = BallAt(new Vector3(0f, 0f, 0f));

        coordinator.ObservePossession(Controlled(TeamId.Home, 8), ball, 1f, Dt);
        Assert.Equal(TeamZone.MiddleThird, coordinator.HomeContext.Zone);

        // Well into the final third: accepted, and it starts the structural dwell.
        ball.Position = new Vector3(25f, 0f, 0f);
        coordinator.ObservePossession(Controlled(TeamId.Home, 8), ball, 1f, Dt);
        Assert.Equal(TeamZone.FinalThird, coordinator.HomeContext.Zone);
        Assert.Equal(TeamZone.OwnThird, coordinator.AwayContext.Zone);

        // A nudge over the boundary is inside the deadband: not even a candidate change.
        ball.Position = new Vector3(16f, 0f, 0f);
        coordinator.ObservePossession(Controlled(TeamId.Home, 8), ball, 1f, Dt);
        Assert.Equal(TeamZone.FinalThird, coordinator.HomeContext.Zone);

        // Clear of the deadband, but inside the 3.5 s dwell: still no change.
        ball.Position = new Vector3(12f, 0f, 0f);
        coordinator.ObservePossession(Controlled(TeamId.Home, 8), ball, 1f, Dt);
        Assert.Equal(TeamZone.FinalThird, coordinator.HomeContext.Zone);

        // Once the dwell expires the zone follows the ball.
        for (int i = 0; i < 240; i++)
            coordinator.ObservePossession(Controlled(TeamId.Home, 8), ball, 1f, Dt);
        Assert.Equal(TeamZone.MiddleThird, coordinator.HomeContext.Zone);
    }

    [Fact]
    public void ATurnover_InsideTheDwell_ResolvesTheNewPhaseImmediately()
    {
        var coordinator = Coordinator();
        var ball = BallAt(new Vector3(25f, 0f, 0f));
        coordinator.ObservePossession(Controlled(TeamId.Home, 8), ball, 1f, Dt);
        Assert.Equal(TeamZone.FinalThird, coordinator.HomeContext.Zone);

        ball.Position = new Vector3(12f, 0f, 0f);
        coordinator.ObservePossession(Controlled(TeamId.Home, 8), ball, 1f, Dt);
        Assert.Equal(TeamZone.FinalThird, coordinator.HomeContext.Zone);

        // The ball is won back here. A turnover is an event, not structural drift: the zone is
        // re-read from the ball and the phase flips, both on this tick.
        coordinator.ObservePossession(Controlled(TeamId.Away, 13), ball, 1f, Dt);

        Assert.Equal(1, coordinator.ClaimChanges);
        Assert.Equal(TeamZone.MiddleThird, coordinator.HomeContext.Zone);
        Assert.Equal(TeamPhase.Defending, coordinator.HomeContext.Phase);
        Assert.Equal(TeamPhase.Attacking, coordinator.AwayContext.Phase);
    }

    [Fact]
    public void EmergencyDefence_InterruptsTheStructuralDwell()
    {
        var coordinator = Coordinator();
        var ball = BallAt(new Vector3(-25f, 0f, 0f));

        // The ball is deep in the home half, which is the away team's *attacking* third.
        coordinator.ObservePossession(Controlled(TeamId.Home, 8), ball, 1f, Dt);
        Assert.Equal(TeamZone.FinalThird, coordinator.AwayContext.Zone);
        Assert.False(coordinator.AwayContext.IsUnderEmergencyDefence);

        // Now the ball is deep in the away team's own third and Home is controlling it. The dwell
        // would hold the old band, but the opponent is imposing the shape: resolve it now.
        ball.Position = new Vector3(25f, 0f, 0f);
        coordinator.ObservePossession(Controlled(TeamId.Home, 8), ball, 1f, Dt);

        Assert.True(coordinator.AwayContext.IsUnderEmergencyDefence);
        Assert.Equal(TeamZone.OwnThird, coordinator.AwayContext.Zone);

        // Contrast: the same jump with the ball loose. Home still claims it, so Away is still
        // defending — but a loose ball in its own third is not an emergency, and the dwell holds.
        var loose = Coordinator();
        var looseBall = BallAt(new Vector3(-25f, 0f, 0f));
        loose.ObservePossession(Controlled(TeamId.Home, 8), looseBall, 1f, Dt);
        Assert.Equal(TeamZone.FinalThird, loose.AwayContext.Zone);

        looseBall.Position = new Vector3(25f, 0f, 0f);
        for (int i = 0; i < 10; i++)
            loose.ObservePossession(LooseOf(TeamId.Home), looseBall, 1f, Dt);

        Assert.False(loose.AwayContext.IsUnderEmergencyDefence);
        Assert.Equal(TeamZone.FinalThird, loose.AwayContext.Zone);
    }

    [Fact]
    public void AnArmedRestart_StartsANewPassageEvenWhenTheClaimDoesNotChangeHands()
    {
        var coordinator = Coordinator();
        var ball = BallAt(new Vector3(0f, 0f, 0f));
        coordinator.ObservePossession(Controlled(TeamId.Away, 13), ball, 1f, Dt);
        for (int i = 0; i < 300; i++)
            coordinator.ObservePossession(Controlled(TeamId.Away, 13), ball, 1f, Dt);

        Assert.Equal(1, coordinator.ClaimChanges);
        Assert.Equal(TransitionMode.None, coordinator.AwayContext.Transition);

        // A throw-in to the team that already had the ball is a new passage: the direct side's
        // counter-attack window reopens, but no turnover is recorded because nothing changed hands.
        coordinator.ObserveRestart(RestartKind.ThrowIn, TeamId.Away, ball, 1f);

        Assert.Equal(TransitionMode.CounterAttack, coordinator.AwayContext.Transition);
        Assert.Equal(1, coordinator.ClaimChanges);
    }

    [Fact]
    public void TheEngine_PublishesItsResolvedContextToItsPlayers()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = 911UL,
            PlayersPerTeam = 5,
            HalfDurationSeconds = 60f
        });

        engine.StepTick();

        TeamContext home = engine.Tactics.HomeContext;
        FootballerObservation? recorded = engine.GetLastObservation(engine.Players[0].Id);
        Assert.NotNull(recorded);
        FootballerObservation observation = recorded!;

        // Everything except the restart, which the rules engine can consume inside the tick (a kickoff
        // taken on tick 1) after the decisions were made.
        Assert.Equal(home.Team, observation.TacticalContext.Team);
        Assert.Equal(home.ClaimedTeam, observation.TacticalContext.ClaimedTeam);
        Assert.Equal(home.Stability, observation.TacticalContext.Stability);
        Assert.Equal(home.Phase, observation.TacticalContext.Phase);
        Assert.Equal(home.Zone, observation.TacticalContext.Zone);
        Assert.Equal(home.Transition, observation.TacticalContext.Transition);

        // The kickoff is the restart in force at the first decision.
        Assert.True(home.Restart.IsArmed);
        Assert.Equal(RestartKind.Kickoff, home.Restart.Kind);
        Assert.Equal(TeamId.Home, home.Restart.Team);
    }
}
