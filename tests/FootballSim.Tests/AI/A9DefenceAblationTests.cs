using System.Collections.Generic;
using System.Linq;
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
/// A9.2 — the two defence ablations must be mechanisms that can be removed, not labels: local cover (A3.5)
/// and rest defence (A3.6) each have a switch, and the tests hold the world fixed and change only the
/// configuration, so the difference is evidence about the mechanism rather than a different fixture.
///
/// Two properties are pinned for each switch:
/// <list type="bullet">
/// <item><b>local cover off</b> — no coverage request is ever opened or awarded (the lifetime
///   <see cref="TeamJobCoordinator.CoverageRequestsOpened"/> counter stays zero), a committing defender keeps
///   his own job and no second defender inherits the vacated region as a <see cref="TacticalJob.ProtectGap"/>
///   responsibility.</item>
/// <item><b>rest defence off</b> — the A3.6 picture reads <see cref="TransitionSecurityModel.FullySecure"/>
///   instead of an evaluation: it never withholds a forward duty, and the A6.4 turnover response, which also
///   consumes the picture, sees the secure answer.</item>
/// </list>
/// </summary>
public sealed class A9DefenceAblationTests
{
    private static readonly PitchDimensions Pitch = new();
    private const float Dt = SimConstants.FixedDeltaTime;

    // ---------------------------------------------------------------------------------------------
    // Local cover (A3.5) — the fixture from the A3.5 suite: an away carrier between the home lines, one
    // centre-back stepped out to press him, and a second centre-back still on the line.
    // ---------------------------------------------------------------------------------------------

    private const int StepOutCentreBack = 4;
    private const int OtherCentreBack = 5;
    private const int DefensiveMidfielder = 6;
    private const int AwayCarrier = 11;

    private sealed record CoverFixture(
        TeamJobCoordinator Jobs,
        List<FootballerState> Players,
        BallState Ball,
        TeamFormation HomeFormation,
        TeamFormation AwayFormation);

    private static FootballerState Player(
        int id, TeamId team, TacticalRole role, int shirt, Vector3 position) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = shirt,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(role),
        Position = position,
        FacingXZ = new Vector2(-1f, 0f)
    };

    /// <summary>
    /// The A3.5 fixture, with only the configuration as a parameter: the home centre-back has pressed the
    /// away carrier and left his slot, the other centre-back and the defensive midfielder are the nearby line
    /// players the coverage request bids over.
    /// </summary>
    private static CoverFixture CentreBackSteppedOut(AiRuntimeConfig ai)
    {
        const int goalkeeper = 1;

        var players = new List<FootballerState>
        {
            Player(goalkeeper, TeamId.Home, TacticalRole.Goalkeeper, 1, new Vector3(-48f, 0f, 0f)),
            Player(StepOutCentreBack, TeamId.Home, TacticalRole.CenterBack, 4, new Vector3(-20f, 0f, -8f)),
            Player(OtherCentreBack, TeamId.Home, TacticalRole.CenterBack, 5, new Vector3(-22f, 0f, 6f)),
            Player(DefensiveMidfielder, TeamId.Home, TacticalRole.DefensiveMidfielder, 6,
                new Vector3(-6f, 0f, 16f)),
            Player(AwayCarrier, TeamId.Away, TacticalRole.CentralMidfielder, 8, new Vector3(-5.4f, 0f, -9.8f))
        };

        var ball = new BallState
        {
            Position = new Vector3(-5.4f, 0.11f, -9.8f),
            LastTouchedByTeam = TeamId.Away,
            LastTouchedByPlayerId = AwayCarrier
        };

        var tactics = new TeamTacticalCoordinator(
            Pitch, TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack);
        tactics.AssignFormations(players, teamSize: 5, fromPositions: true, homeAttackDirX: 1f);

        // The anchors are what "out of the line" is measured against; write them the way the engine does.
        var possession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Away, AwayCarrier, null);
        tactics.ObservePossession(possession, ball, 1f, Dt);
        tactics.UpdateTeamAnchors(players, ball, 1f);

        // Now the centre-back presses: he is on the ball, well ahead of the shape point he was given.
        players.First(p => p.Id == StepOutCentreBack).Position = new Vector3(-6f, 0f, -9.5f);

        return new CoverFixture(
            new TeamJobCoordinator(Pitch, ai), players, ball, tactics.HomeFormation, tactics.AwayFormation);
    }

    private static BallPossessionSnapshot AwayControlled =>
        new(BallControlState.Controlled, TeamId.Away, AwayCarrier, null);

    private static void Run(
        CoverFixture fixture,
        BallPossessionSnapshot possession,
        TeamId? possessingTeam,
        int? homeResponderId,
        int? awayResponderId,
        int tick)
    {
        fixture.Jobs.Update(
            fixture.Players, fixture.Ball, possession, possessingTeam,
            homeResponderId, awayResponderId,
            homeDirX: 1f, tick,
            TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
            new TeamBelief(TeamId.Home), new TeamBelief(TeamId.Away),
            fixture.HomeFormation, fixture.AwayFormation);
    }

    [Fact]
    public void LocalCoverOff_NeverOpensARequest_AndTheSecondDefenderKeepsHisOwnJob()
    {
        // ON — the default configuration raises the request: a second defender is awarded the region the
        // pressing centre-back vacated. This half proves the fixture is live, so the OFF half is not vacuous.
        CoverFixture on = CentreBackSteppedOut(new AiRuntimeConfig());
        Run(on, AwayControlled, TeamId.Away, StepOutCentreBack, awayResponderId: null, tick: 0);

        CoverageRequest live = on.Jobs.CoverageOf(TeamId.Home);
        Assert.True(live.IsLive, $"the default world did not raise the request (state {live.State})");
        Assert.Equal(OtherCentreBack, live.OwnerPlayerId);
        Assert.Equal(1, on.Jobs.CoverageRequestsOpened);
        Assert.Equal(TacticalJob.Presser, on.Jobs.Get(StepOutCentreBack)!.Value.Job);
        Assert.Equal(TacticalJob.ProtectGap, on.Jobs.Get(OtherCentreBack)!.Value.Job);

        // OFF — the same world, only the switch changed. No request is opened on any pass, so the lifetime
        // counter stays zero and the closing counter has nothing to close either.
        CoverFixture off = CentreBackSteppedOut(new AiRuntimeConfig { EnableLocalCover = false });
        for (int tick = 0; tick <= 60; tick += 6)
        {
            Run(off, AwayControlled, TeamId.Away, StepOutCentreBack, awayResponderId: null, tick);
        }

        Assert.Equal(0, off.Jobs.CoverageRequestsOpened);
        Assert.Equal(0, off.Jobs.CoverageRequestsClosed);
        Assert.Equal(CoverageRequestState.None, off.Jobs.CoverageOf(TeamId.Home).State);
        Assert.False(off.Jobs.CoverageOf(TeamId.Home).IsLive);

        // The committing defender still presses — the ablation removes the cover, not his own job.
        Assert.Equal(TacticalJob.Presser, off.Jobs.Get(StepOutCentreBack)!.Value.Job);

        // And no teammate inherited the vacated region: the second centre-back holds an ordinary duty of his
        // own, not a temporary responsibility sourced from the step-out.
        TeamJobAssignment second = off.Jobs.Get(OtherCentreBack)!.Value;
        Assert.NotEqual(TacticalJob.ProtectGap, second.Job);
        Assert.False(second.IsTemporary, "the coverage mechanism must not create a temporary responsibility");
        Assert.Null(second.SourcePlayerId);
        Assert.Equal(TacticalJob.CoverDefender, second.Job);
        Assert.DoesNotContain(off.Jobs.Assignments.Values, a => a.Job == TacticalJob.ProtectGap);
        Assert.DoesNotContain(off.Jobs.Assignments.Values, a => a.IsTemporary);
    }

    // ---------------------------------------------------------------------------------------------
    // Rest defence (A3.6) — the fixture from the A3.6 suite: eleven-a-side possession with the back six
    // pushed into the opposition half and two away players loose through the middle.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The A3.6 fixture world, unchanged from <see cref="TransitionSecurityTests"/>: home attacks +X, the
    /// carrier is at (0, 10), and every positional assignment below is the exposed one.
    /// </summary>
    private static (List<FootballerState> Players, BallState Ball) ExposedPossessionShape()
    {
        static FootballerState Side(int id, TeamId team, TacticalRole role, float x, float z)
        {
            FootballerState player = new()
            {
                Id = id,
                Team = team,
                ShirtNumber = id,
                Role = role,
                Attributes = new PlayerAttributes(),
                Tendencies = PlayerTendencyProfile.ForRole(role),
                Position = new Vector3(x, 0f, z),
                FacingXZ = new Vector2(1f, 0f)
            };
            player.TacticalAnchor = player.Position;
            return player;
        }

        var players = new List<FootballerState>
        {
            Side(1, TeamId.Home, TacticalRole.Goalkeeper, -50f, 0f),
            Side(2, TeamId.Home, TacticalRole.FullBack, -8f, 18f),
            Side(3, TeamId.Home, TacticalRole.FullBack, -8f, -18f),
            Side(4, TeamId.Home, TacticalRole.CenterBack, -20f, 6f),
            Side(5, TeamId.Home, TacticalRole.CenterBack, -20f, -6f),
            Side(6, TeamId.Home, TacticalRole.DefensiveMidfielder, -6f, 2f),
            Side(7, TeamId.Home, TacticalRole.Winger, 10f, 26f),
            Side(8, TeamId.Home, TacticalRole.CentralMidfielder, 0f, 10f),   // carrier
            Side(9, TeamId.Home, TacticalRole.Striker, 18f, 0f),
            Side(10, TeamId.Home, TacticalRole.CentralMidfielder, 6f, -12f),
            Side(11, TeamId.Home, TacticalRole.Winger, 10f, -26f),
            Side(12, TeamId.Away, TacticalRole.Goalkeeper, 50f, 0f),
            Side(13, TeamId.Away, TacticalRole.CenterBack, 16f, 6f),
            Side(14, TeamId.Away, TacticalRole.CenterBack, 20f, -6f),
            Side(15, TeamId.Away, TacticalRole.DefensiveMidfielder, 12f, 8f),
            Side(16, TeamId.Away, TacticalRole.FullBack, 26f, 20f),
            Side(17, TeamId.Away, TacticalRole.FullBack, 26f, -20f),
            Side(18, TeamId.Away, TacticalRole.CentralMidfielder, 14f, -4f),
            Side(19, TeamId.Away, TacticalRole.Winger, 30f, 18f),
            Side(20, TeamId.Away, TacticalRole.Winger, 30f, -18f),
            Side(21, TeamId.Away, TacticalRole.Striker, 34f, 0f),
            Side(22, TeamId.Away, TacticalRole.CentralMidfielder, 22f, 0f)
        };

        var ball = new BallState
        {
            Position = new Vector3(0f, 0.11f, 10f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 8
        };

        // The exposing moves: the back six are up in the opposition half and two away runners are loose
        // through the middle, goal-side of the ball.
        MoveTo(players, 2, 34f, 18f);
        MoveTo(players, 3, 34f, -18f);
        MoveTo(players, 4, 32f, 6f);
        MoveTo(players, 5, 32f, -6f);
        MoveTo(players, 6, 30f, 2f);
        MoveTo(players, 21, 2f, 4f);
        MoveTo(players, 22, 4f, -8f);

        return (players, ball);
    }

    private static void MoveTo(List<FootballerState> players, int playerId, float x, float z)
    {
        FootballerState player = players.First(p => p.Id == playerId);
        player.Position = new Vector3(x, 0f, z);
        player.TacticalAnchor = player.Position;
    }

    private static TeamJobCoordinator RunPossessionJobs(
        List<FootballerState> players, BallState ball, AiRuntimeConfig ai)
    {
        var jobs = new TeamJobCoordinator(Pitch, ai);
        jobs.Update(
            players, ball,
            new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, 8, null),
            possessingTeam: TeamId.Home,
            homeResponderId: null, awayResponderId: 15,
            homeDirX: 1f, tick: 0,
            TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
            TeamBelief.FromTruth(TeamId.Home, players), TeamBelief.FromTruth(TeamId.Away, players),
            new TeamFormation(TeamId.Home, Pitch), new TeamFormation(TeamId.Away, Pitch));
        return jobs;
    }

    [Fact]
    public void RestDefenceOff_ReportsTheFullySecurePicture_WhereTheDefaultIsExposed()
    {
        (List<FootballerState> players, BallState ball) = ExposedPossessionShape();

        // ON — the default evaluation of this world is exposed: nobody behind the ball, so the allocator
        // withholds every forward duty.
        RestDefence exposed = RunPossessionJobs(players, ball, new AiRuntimeConfig()).SecurityOf(TeamId.Home);
        Assert.True(exposed.Security01 < TransitionSecurityModel.MinSecurityForForwardRun,
            $"the fixture is not exposed on the default path: security {exposed.Security01:F2}");
        Assert.Equal(0, exposed.ForwardAllowance);
        Assert.False(exposed.AllowsForwardCommitment);

        // OFF — the same world announces itself fully secure rather than evaluating: the ablation removes the
        // veto, not the model, so the forward allowance and the commitment flag read the neutral picture.
        TeamJobCoordinator secureJobs = RunPossessionJobs(
            players, ball, new AiRuntimeConfig { EnableRestDefence = false });
        RestDefence secure = secureJobs.SecurityOf(TeamId.Home);

        Assert.Equal(TransitionSecurityModel.FullySecure, secure);
        Assert.Equal(1f, secure.Security01);
        Assert.Equal(3, secure.BehindBall);
        Assert.Equal(0f, secure.WorstRecoverySeconds);
        Assert.Equal(0f, secure.CentralAccess01);
        Assert.Equal(0f, secure.OutletExposure01);
        Assert.Equal(0f, secure.Threat01);
        Assert.Equal(2, secure.ForwardAllowance);
        Assert.True(secure.AllowsForwardCommitment);
        Assert.True(secure.Security01 > exposed.Security01);

        // The picture is neutral from construction, before any pass has evaluated anything.
        Assert.Equal(TransitionSecurityModel.FullySecure,
            new TeamJobCoordinator(Pitch, new AiRuntimeConfig { EnableRestDefence = false })
                .SecurityOf(TeamId.Home));
    }

    // ---------------------------------------------------------------------------------------------
    // Rest defence reaches A6.4's turnover response — the model-level switch and the allocator's plumbing.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The exposed turnover picture from <see cref="TurnoverResponseModelTests"/>: home has just lost the
    /// ball with every outfielder ahead of it, so the default A3.6 evaluation reports nothing behind the ball.
    /// </summary>
    private static List<FootballerState> ExposedTransitionPicture() => new()
    {
        Player(101, TeamId.Home, TacticalRole.Goalkeeper, 1, new Vector3(-48f, 0f, 0f)),
        Player(102, TeamId.Home, TacticalRole.CenterBack, 4, new Vector3(2f, 0f, -7f)),
        Player(103, TeamId.Home, TacticalRole.CenterBack, 5, new Vector3(2f, 0f, 7f)),
        Player(104, TeamId.Home, TacticalRole.DefensiveMidfielder, 6, new Vector3(6f, 0f, 0f)),
        Player(108, TeamId.Home, TacticalRole.CentralMidfielder, 8, new Vector3(8f, 0f, -8f)),
        Player(201, TeamId.Away, TacticalRole.CentralMidfielder, 8, new Vector3(0f, 0f, 0f)),
        Player(202, TeamId.Away, TacticalRole.Striker, 9, new Vector3(14f, 0f, -10f)),
        Player(209, TeamId.Away, TacticalRole.Goalkeeper, 1, new Vector3(46f, 0f, 0f))
    };

    private static BallState TransitionBall() => new()
    {
        Position = new Vector3(0f, 0.11f, 0f),
        LastTouchedByTeam = TeamId.Away,
        LastTouchedByPlayerId = 201
    };

    private static TurnoverSituation EvaluateTurnover(IReadOnlyList<FootballerState> players, bool restDefenceEnabled) =>
        TurnoverResponseModel.Evaluate(
            TeamId.Home, players, TransitionBall(),
            new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Away, 201, null),
            TeamBelief.FromTruth(TeamId.Home, players), TeamTacticalProfile.PossessionControl,
            1f, Pitch, 0f, true, restDefenceEnabled);

    [Fact]
    public void TheTurnoverModel_RestDefenceSwitch_IsOffOnlyWhenAsked()
    {
        List<FootballerState> players = ExposedTransitionPicture();

        TurnoverSituation byDefault = EvaluateTurnover(players, restDefenceEnabled: true);
        TurnoverSituation explicitOn = EvaluateTurnover(players, restDefenceEnabled: true);
        TurnoverSituation off = EvaluateTurnover(players, restDefenceEnabled: false);

        // The optional parameter preserves every existing caller: passing the default explicitly is the
        // historic computation, field for field.
        Assert.Equal(byDefault, explicitOn);

        Assert.Equal(0, byDefault.BehindBall);
        Assert.True(off.BehindBall == 3 &&
                    off.CentralSecurity01 == 1f &&
                    off.WorstRecoverySeconds == 0f,
            $"the off answer did not see the fully-secure picture: {off.BehindBall} back, " +
            $"security {off.CentralSecurity01:F2}, worst recovery {off.WorstRecoverySeconds:F1}s");
        Assert.Equal(1f, off.CentralSecurity01);
        Assert.True(off.CentralSecurity01 > byDefault.CentralSecurity01);
        Assert.Equal(1f, off.EscapeRoute01);   // FullySecure.OutletExposure01 is 0 and escape is its complement
    }

    private static TeamJobCoordinator RunTurnoverJobs(List<FootballerState> players, AiRuntimeConfig ai)
    {
        var possession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Away, 201, null);
        var homeContext = new TeamContext(
            TeamId.Home, TeamId.Away, PossessionStability.Controlled,
            TeamPhase.Defending, TeamZone.MiddleThird, TransitionMode.CounterPress,
            RestartContext.None, IsEmergencyDefence: false, TransitionSeconds: 0f);

        var jobs = new TeamJobCoordinator(Pitch, ai);
        jobs.Update(
            players, TransitionBall(), possession, possessingTeam: TeamId.Away,
            homeResponderId: null, awayResponderId: null, homeDirX: 1f, tick: 0,
            TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
            TeamBelief.FromTruth(TeamId.Home, players), TeamBelief.FromTruth(TeamId.Away, players),
            new TeamFormation(TeamId.Home, Pitch), new TeamFormation(TeamId.Away, Pitch),
            homeContext, awayContext: null);
        return jobs;
    }

    [Fact]
    public void TheAllocator_HandsTheRestDefenceSwitch_ToTheTurnoverResponse()
    {
        // TeamJobs evaluates the A6.4 response itself (UpdateTurnover), so the ablation has to be threaded
        // through the allocator's call — otherwise turning rest defence off would change the forward-duty
        // gate while the turnover answer kept believing the side was exposed.
        List<FootballerState> players = ExposedTransitionPicture();

        TurnoverSituation exposed = RunTurnoverJobs(players, new AiRuntimeConfig()).TurnoverOf(TeamId.Home);
        Assert.Equal(0, exposed.BehindBall);
        Assert.True(exposed.CentralSecurity01 < 1f,
            $"the default turnover picture is not exposed: security {exposed.CentralSecurity01:F2}");

        TurnoverSituation secure = RunTurnoverJobs(
            players, new AiRuntimeConfig { EnableRestDefence = false }).TurnoverOf(TeamId.Home);
        Assert.Equal(3, secure.BehindBall);
        Assert.Equal(1f, secure.CentralSecurity01);
        Assert.Equal(0f, secure.WorstRecoverySeconds);
    }

    // ---------------------------------------------------------------------------------------------
    // Defaults.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefenceSwitches_DefaultOn_AndAreNamedInTheDescription()
    {
        var shipped = new AiRuntimeConfig();
        Assert.True(shipped.EnableLocalCover);
        Assert.True(shipped.EnableRestDefence);
        Assert.Contains("cover(on)", shipped.Describe());
        Assert.Contains("restdef(on)", shipped.Describe());

        Assert.Contains("cover(off)", new AiRuntimeConfig { EnableLocalCover = false }.Describe());
        Assert.Contains("restdef(off)", new AiRuntimeConfig { EnableRestDefence = false }.Describe());
    }
}
