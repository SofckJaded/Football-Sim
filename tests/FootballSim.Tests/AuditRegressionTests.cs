using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Rules;
using FootballSim.Tactics;

namespace FootballSim.Tests;

/// <summary>
/// Regressions for defects reproduced in docs/AUDIT_HANDOFF_2026-11-06.md.
/// Each test drives the same public path a real match uses (never the test-only
/// compatibility properties) so a green suite means the live path is fixed.
/// </summary>
public sealed class AuditRegressionTests
{
    private static readonly float Dt = SimConstants.FixedDeltaTime;

    private static FootballerState Player(int id, TeamId team, TacticalRole role, Vector3 pos, int? shirtNumber = null) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = shirtNumber ?? id,
        Role = role,
        Position = pos,
        Attributes = new PlayerAttributes(),
        Tendencies = new PlayerTendencies()
    };

    /// <summary>
    /// A truth view of the opponents for the job coordinator (A2.6). These tests exercise the
    /// assignment rules themselves, so they feed truth; the belief path is covered separately by
    /// <see cref="TeamBeliefTests"/>.
    /// </summary>
    private static TeamBelief TruthBelief(IReadOnlyList<FootballerState> players, TeamId team) =>
        TeamBelief.FromTruth(team, players);

    private static FootballerObservation Observe(
        FootballerState self,
        IReadOnlyList<FootballerState> players,
        BallState ball,
        BallPossessionSnapshot? possession = null,
        bool? isDesignatedBallResponder = null,
        TeamJobAssignment? job = null,
        TeamTacticalProfile? profile = null) => new PerceptionSystem().BuildObservation(
            self,
            players,
            ball,
            ball.LastTouchedByTeam,
            MatchPhase.FirstHalf,
            10f,
            self.Team == TeamId.Home ? 1f : -1f,
            false,
            profile ?? TeamTacticalProfile.PossessionControl,
            new SeededRandom(42),
            possession,
            isDesignatedBallResponder,
            job);

    /// <summary>
    /// Audit F19: call-for-pass state was static, so constructing a second engine
    /// cleared the call of a live first engine and both shared one countdown.
    /// </summary>
    [Fact]
    public void F19_CallForPassState_IsIsolatedPerMatch()
    {
        var engineA = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = 7UL,
            PlayersPerTeam = 11,
            DesignatedHumanPlayerId = 9
        });
        var humanA = engineA.Players.Single(p => p.Id == 9);
        humanA.Position = new Vector3(10f, 0f, 0f);
        engineA.Ball.Position = new Vector3(0f, 0.11f, 0f);
        engineA.Ball.LastTouchedByTeam = TeamId.Home;

        var ctrlA = engineA.LockedHumanController!;
        ctrlA.ReleaseChargedKick(KickIntentType.GroundPass, 0.5f);
        ctrlA.Decide(Observe(humanA, engineA.Players, engineA.Ball), Dt);

        Assert.Equal<int?>(9, ctrlA.Communication.HumanCallForPassPlayerId);
        Assert.True(ctrlA.Communication.HasActiveCall);

        // A second, independent match must not touch the first match's call.
        var engineB = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = 8UL,
            PlayersPerTeam = 11,
            DesignatedHumanPlayerId = 9
        });
        var ctrlB = engineB.LockedHumanController!;

        Assert.NotSame(ctrlA.Communication, ctrlB.Communication);
        Assert.Equal<int?>(9, ctrlA.Communication.HumanCallForPassPlayerId);
        Assert.Null(ctrlB.Communication.HumanCallForPassPlayerId);

        // Advancing match B must not decrement match A's call timer.
        for (int i = 0; i < 10; i++)
            engineB.StepTick();

        Assert.Equal<int?>(9, ctrlA.Communication.HumanCallForPassPlayerId);
        Assert.True(ctrlA.Communication.HumanCallForPassTimer > 2.4f,
            $"Match B consumed match A's call timer: {ctrlA.Communication.HumanCallForPassTimer:F3}s");
    }

    /// <summary>
    /// Audit F15: the live QueuedTackle path required distance &gt; 0.85 m, so a
    /// point-blank tackle press produced no request at all.
    /// </summary>
    [Theory]
    [InlineData(0.3f)]
    [InlineData(0.6f)]
    [InlineData(1.0f)]
    public void F15_QueuedTackle_FiresAnywhereInsideStandingTackleReach(float distanceMeters)
    {
        var human = new HumanInputController(9);
        var p = Player(9, TeamId.Home, TacticalRole.Striker, Vector3.Zero);
        var ball = new BallState
        {
            Position = new Vector3(distanceMeters, 0.11f, 0f),
            LastTouchedByTeam = TeamId.Away
        };

        human.QueueTackle();
        var cmd = human.Decide(Observe(p, new[] { p }, ball), Dt);

        Assert.True(cmd.RequestTackle, $"Buffered tackle did not fire at {distanceMeters} m");
    }

    /// <summary>
    /// Audit F15: an early press used to be spent at long range where contact is
    /// impossible. It must stay buffered and fire once the ball is reachable.
    /// </summary>
    [Fact]
    public void F15_QueuedTackle_IsRetainedWhileOutOfReach_ThenFires()
    {
        var human = new HumanInputController(9);
        var p = Player(9, TeamId.Home, TacticalRole.Striker, Vector3.Zero);
        var ball = new BallState { Position = new Vector3(4.0f, 0.11f, 0f), LastTouchedByTeam = TeamId.Away };

        human.QueueTackle();
        var outOfReach = human.Decide(Observe(p, new[] { p }, ball), Dt);
        Assert.False(outOfReach.RequestTackle, "Tackle fired outside physical reach");

        ball.Position = new Vector3(1.0f, 0.11f, 0f);
        var inReach = human.Decide(Observe(p, new[] { p }, ball), Dt);
        Assert.True(inReach.RequestTackle, "Retained tackle press was lost before reaching the ball");
    }

    /// <summary>
    /// The buffer must still expire: a press many frames before contact is not a
    /// licence to tackle indefinitely.
    /// </summary>
    [Fact]
    public void F15_QueuedTackle_ExpiresAfterBufferWindow()
    {
        var human = new HumanInputController(9);
        var p = Player(9, TeamId.Home, TacticalRole.Striker, Vector3.Zero);
        var ball = new BallState { Position = new Vector3(4.0f, 0.11f, 0f), LastTouchedByTeam = TeamId.Away };

        human.QueueTackle();
        // 0.25 s buffer; 20 ticks = 0.333 s.
        for (int i = 0; i < 20; i++)
            human.Decide(Observe(p, new[] { p }, ball), Dt);

        ball.Position = new Vector3(1.0f, 0.11f, 0f);
        var late = human.Decide(Observe(p, new[] { p }, ball), Dt);
        Assert.False(late.RequestTackle, "Expired tackle press still fired");
    }

    // ---------------------------------------------------------------------
    // Batch 2 — F05 (period vs phase), F07 (restart consumption), F09 (numerics)
    // ---------------------------------------------------------------------

    /// <summary>11-a-side squad with real shirt numbers so kickoff selection is exercised.</summary>
    private static List<FootballerState> Squad()
    {
        var list = new List<FootballerState>();
        int id = 1;
        foreach (var team in new[] { TeamId.Home, TeamId.Away })
        {
            for (int shirt = 1; shirt <= 11; shirt++)
            {
                var p = Player(id++, team,
                    shirt == 1 ? TacticalRole.Goalkeeper : TacticalRole.CentralMidfielder,
                    new Vector3(team == TeamId.Home ? -10f : 10f, 0f, shirt * 0.5f),
                    shirt);
                list.Add(p);
            }
        }
        return list;
    }

    /// <summary>
    /// Audit F05: attack direction was derived from Phase, so every second-half restart
    /// (throw-in, corner, goal kick, free kick, penalty, goal) reported first-half directions.
    /// </summary>
    [Fact]
    public void F05_SecondHalfRestart_KeepsSecondHalfAttackDirection()
    {
        var rules = new MatchRulesEngine(new PitchDimensions());
        var players = new[]
        {
            Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero),
            Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(10f, 0f, 0f)),
            Player(12, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(-40f, 0f, 0f)),
            Player(13, TeamId.Away, TacticalRole.CenterBack, new Vector3(-20f, 0f, 0f))
        };
        var ball = new BallState();
        rules.StartMatch(players, ball);
        Assert.Equal(1f, rules.GetHomeAttackDirectionX());

        // 1-second halves: reach half time, then wait out the 0.5 s break.
        for (int i = 0; i < 65; i++) rules.StepRules(players, ball, 1f, Dt);
        Assert.Equal(MatchPhase.HalfTime, rules.Phase);
        for (int i = 0; i < 35; i++) rules.StepRules(players, ball, 1f, Dt);

        Assert.Equal(MatchPhase.SecondHalf, rules.Phase);
        Assert.Equal(MatchPeriod.SecondHalf, rules.Period);
        Assert.Equal(-1f, rules.GetHomeAttackDirectionX());

        // Throw-in for Away while the second half is running.
        ball.Position = new Vector3(0f, 0.11f, 40f);
        ball.LastTouchedByTeam = TeamId.Home;
        rules.StepRules(players, ball, 1f, Dt);

        Assert.Equal(MatchPhase.ThrowIn, rules.Phase);
        Assert.Equal(-1f, rules.GetHomeAttackDirectionX());

        // Clear the 0.25 s restart window: play must resume in the same half.
        for (int i = 0; i < 20; i++) rules.StepRules(players, ball, 1f, Dt);
        Assert.Equal(MatchPhase.SecondHalf, rules.Phase);
    }

    /// <summary>
    /// Audit F07: the kickoff taker stayed designated for the rest of the match, so the AI
    /// re-entered set-piece logic whenever that player neared the ball in open play.
    /// </summary>
    [Fact]
    public void F07_KickoffDesignation_IsConsumedByTheTakersFirstKick()
    {
        var rules = new MatchRulesEngine(new PitchDimensions());
        var players = Squad();
        var ball = new BallState();
        rules.StartMatch(players, ball);

        Assert.Equal<int?>(8, rules.DesignatedSetPieceTakerId);
        Assert.True(rules.RestartAwaitingKick);

        // #8 kicks off: the setup leaves the ball still, so this can only be the restart.
        ball.Velocity = new Vector3(4f, 0f, 0f);
        ball.LastTouchedByPlayerId = 8;
        ball.LastTouchedByTeam = TeamId.Home;
        rules.StepRules(players, ball, 2700f, Dt);

        Assert.Null(rules.DesignatedSetPieceTakerId);
        Assert.False(rules.RestartAwaitingKick);
    }

    /// <summary>
    /// A restart can also be taken by a teammate or cut out by an opponent; either way the
    /// designation must not survive into open play.
    /// </summary>
    [Fact]
    public void F07_RestartDesignation_IsConsumedWhenAnyoneTakesIt()
    {
        var rules = new MatchRulesEngine(new PitchDimensions());
        var players = Squad();
        var ball = new BallState();
        rules.StartMatch(players, ball);
        Assert.Equal<int?>(8, rules.DesignatedSetPieceTakerId);

        // An Away player intercepts the kickoff instead of the designated Home #8.
        ball.Velocity = new Vector3(-3f, 0f, 2f);
        ball.LastTouchedByPlayerId = 14;
        ball.LastTouchedByTeam = TeamId.Away;
        rules.StepRules(players, ball, 2700f, Dt);

        Assert.False(rules.RestartAwaitingKick);
        Assert.Null(rules.DesignatedSetPieceTakerId);
    }

    /// <summary>
    /// Audit F07: setup searched for a new taker without clearing the old designation, so a
    /// sent-off preferred taker left the restart pointing at an unavailable player.
    /// </summary>
    [Fact]
    public void F07_SentOffPreferredTaker_DoesNotLeaveStaleDesignation()
    {
        var rules = new MatchRulesEngine(new PitchDimensions());
        var players = Squad();
        var ball = new BallState();
        rules.StartMatch(players, ball);

        var preferred = players.Single(p => p.Team == TeamId.Home && p.ShirtNumber == 8);
        preferred.IsSentOff = true;

        rules.SetupKickoffPositions(players, ball, TeamId.Home, rules.GetHomeAttackDirectionX());

        Assert.NotNull(rules.DesignatedSetPieceTakerId);
        Assert.NotEqual(preferred.Id, rules.DesignatedSetPieceTakerId);
        var taker = players.Single(p => p.Id == rules.DesignatedSetPieceTakerId);
        Assert.Equal(TeamId.Home, taker.Team);
        Assert.True(taker.IsActiveOnPitch, "Designated taker is not on the pitch");
    }

    /// <summary>
    /// Audit F09: a keeper standing exactly over the ball normalized a zero vector and emitted
    /// a NaN movement command.
    /// </summary>
    [Fact]
    public void F09_KeeperExactlyOverBall_ProducesFiniteCommand()
    {
        var keeper = Player(1, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-50f, 0f, 0f));
        var ball = new BallState
        {
            Position = new Vector3(-50f, 0.11f, 0f),
            LastTouchedByTeam = TeamId.Home
        };

        var cmd = new GoalkeeperController(new PitchDimensions())
            .Decide(Observe(keeper, new[] { keeper }, ball), Dt);

        Assert.True(SimMath.IsFinite(cmd.DesiredMoveDirectionXZ), "Move direction is not finite");
        Assert.True(SimMath.IsFinite(cmd.DesiredFacingDirectionXZ), "Facing direction is not finite");
        Assert.True(SimMath.IsFinite(cmd.KickAimDirectionXZ), "Kick aim is not finite");
    }

    /// <summary>
    /// Audit F09: ball safety only inspected X, so a NaN Z, a NaN spin, or an infinite
    /// component survived the solver untouched.
    /// </summary>
    [Fact]
    public void F09_SolverRecoversNonFiniteComponentsOnEveryAxis()
    {
        var badPositions = new[]
        {
            new Vector3(0f, 0.11f, float.NaN),
            new Vector3(float.NaN, 0.11f, 0f),
            new Vector3(0f, float.PositiveInfinity, 0f)
        };

        foreach (var bad in badPositions)
        {
            var solver = new BallPhysicsSolver();
            var ball = new BallState { Position = bad };
            solver.Step(ball, Dt);

            Assert.True(SimMath.IsFinite(ball.Position), $"Position not recovered from {bad}");
            Assert.True(SimMath.IsFinite(ball.Velocity), $"Velocity not recovered from {bad}");
            Assert.True(SimMath.IsFinite(ball.AngularVelocity), $"Spin not recovered from {bad}");
            Assert.True(ball.InvalidStateRecoveries > 0, "Recovery was not reported");
        }

        // A non-finite spin alone must also be rejected. A resting ball recomputes rolling spin
        // from the (finite) velocity, so assert the observable outcome: no NaN may survive.
        var spinSolver = new BallPhysicsSolver();
        var spun = new BallState
        {
            Position = new Vector3(0f, 0.11f, 0f),
            AngularVelocity = new Vector3(float.NaN, 0f, 0f)
        };
        spinSolver.Step(spun, Dt);
        Assert.True(SimMath.IsFinite(spun.AngularVelocity), "NaN spin survived the solver");
        Assert.True(SimMath.IsFinite(spun.Position), "NaN spin corrupted the position");
    }

    [Fact]
    public void F09_SafeNormalize_NeverReturnsNonFinite()
    {
        var fallback = new Vector2(1f, 0f);

        Assert.Equal(fallback, SimMath.SafeNormalize(Vector2.Zero, fallback));
        Assert.Equal(fallback, SimMath.SafeNormalize(new Vector2(float.NaN, 0f), fallback));
        Assert.Equal(fallback, SimMath.SafeNormalize(new Vector2(0f, float.PositiveInfinity), fallback));

        var normal = SimMath.SafeNormalize(new Vector2(3f, 4f), Vector2.Zero);
        Assert.InRange(normal.Length(), 0.999f, 1.001f);
        Assert.InRange(normal.X, 0.599f, 0.601f);
        Assert.InRange(normal.Y, 0.799f, 0.801f);
    }

    // ---------------------------------------------------------------------
    // Batch 3 — F06 (execution gating), F13 (atomic restart setup)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Audit F06: controllers, contacts and ball physics used to keep running while
    /// <c>PhasePauseTimer</c> was counting down, so players could kick during a whistle delay.
    /// </summary>
    [Fact]
    public void F06_PlayIsFrozenDuringRestartPause()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig { Seed = 11UL, PlayersPerTeam = 11 });
        engine.Ball.Position = new Vector3(0f, 0.11f, 40f); // out over the touchline
        engine.Ball.LastTouchedByTeam = TeamId.Away;
        engine.StepTick();

        Assert.True(engine.Rules.IsPlaySuspended, "Throw-in did not enter a paused restart");
        Assert.True(engine.Rules.PhasePauseTimer > 0f);

        var ballPosition = engine.Ball.Position;
        var ballVelocity = engine.Ball.Velocity;
        var playerPositions = engine.Players.Select(p => p.Position).ToArray();

        engine.StepTick();
        engine.StepTick();

        Assert.Equal(ballPosition, engine.Ball.Position);
        Assert.Equal(ballVelocity, engine.Ball.Velocity);
        for (int i = 0; i < engine.Players.Count; i++)
            Assert.Equal(playerPositions[i], engine.Players[i].Position);

        Assert.True(engine.Rules.IsPlaySuspended, "Pause ended earlier than expected");
    }

    /// <summary>
    /// Audit F06: <c>StepTick</c> kept integrating the ball after full time, so a still-running
    /// game loop could move the ball and change state after the match had ended.
    /// </summary>
    [Fact]
    public void F06_FullTime_StopsAuthoritativeExecution()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = 12UL,
            PlayersPerTeam = 5,
            HalfDurationSeconds = 1f
        });

        for (int i = 0; i < 1000 && engine.Rules.Phase != MatchPhase.FullTime; i++)
            engine.StepTick();

        Assert.Equal(MatchPhase.FullTime, engine.Rules.Phase);

        int tickAtFullTime = engine.CurrentTick;
        var ballPosition = engine.Ball.Position;
        engine.Ball.Velocity = new Vector3(5f, 0f, 0f);

        engine.StepTick();
        engine.StepTick();

        Assert.Equal(tickAtFullTime, engine.CurrentTick);
        Assert.Equal(ballPosition, engine.Ball.Position);
    }

    /// <summary>
    /// Audit F13: a restart kept the previous toucher, ball history and stale offside passer.
    /// </summary>
    [Fact]
    public void F13_RestartSetup_ClearsStaleToucherAndBallHistory()
    {
        var rules = new MatchRulesEngine(new PitchDimensions());
        var players = Squad();
        var ball = new BallState();
        rules.StartMatch(players, ball);

        ball.LastTouchedByPlayerId = 5;
        ball.LastTouchedByTeam = TeamId.Away;
        ball.Velocity = new Vector3(3f, 0f, 0f);
        ball.Position = new Vector3(10f, 0.11f, 40f); // out for a Home throw-in

        rules.StepRules(players, ball, 2700f, Dt);

        Assert.Equal(MatchPhase.ThrowIn, rules.Phase);
        Assert.Null(ball.LastTouchedByPlayerId);
        Assert.Equal(Vector3.Zero, ball.Velocity);
        Assert.Equal(ball.Position, ball.PreviousPosition);
    }

    /// <summary>
    /// Audit F13: the taker was always placed using the HOME attack direction, so away restarts
    /// put the taker on the wrong side of the ball and facing backwards.
    /// </summary>
    [Fact]
    public void F13_AwayRestart_TakerStandsBehindBallForOwnAttackDirection()
    {
        var rules = new MatchRulesEngine(new PitchDimensions());
        var players = Squad();
        var ball = new BallState();
        rules.StartMatch(players, ball);

        ball.LastTouchedByTeam = TeamId.Home;
        ball.Position = new Vector3(10f, 0.11f, 40f); // Away throw-in
        rules.StepRules(players, ball, 2700f, Dt);

        var taker = players.Single(p => p.Id == rules.DesignatedSetPieceTakerId);
        Assert.Equal(TeamId.Away, taker.Team);

        // Away attacks -X in the first half, so the taker must stand on the +X side of the ball.
        Assert.True(taker.Position.X > ball.Position.X,
            $"Away taker at X={taker.Position.X:F2} but ball at X={ball.Position.X:F2}");
        Assert.True(taker.FacingXZ.X < -0.9f, $"Away taker faces {taker.FacingXZ}");
    }

    /// <summary>
    /// Audit F13: the designation was dropped the moment the pause ended, so the restart was
    /// never actually taken by the designated player.
    /// </summary>
    [Fact]
    public void F13_TakerDesignation_SurvivesTheRestartPause()
    {
        var rules = new MatchRulesEngine(new PitchDimensions());
        var players = Squad();
        var ball = new BallState();
        rules.StartMatch(players, ball);

        ball.LastTouchedByTeam = TeamId.Home;
        ball.Position = new Vector3(10f, 0.11f, 40f);
        rules.StepRules(players, ball, 2700f, Dt);

        int? designated = rules.DesignatedSetPieceTakerId;
        Assert.NotNull(designated);

        for (int i = 0; i < 20; i++)
            rules.StepRules(players, ball, 2700f, Dt); // past the 0.25 s pause

        Assert.Equal(MatchPhase.FirstHalf, rules.Phase);
        Assert.Equal(designated, rules.DesignatedSetPieceTakerId);
        Assert.True(rules.RestartAwaitingKick);
    }

    /// <summary>
    /// Audit F13: a foul callback set up a restart, but the pending ball contact then overwrote
    /// the freshly reset ball.
    /// </summary>
    [Fact]
    public void F13_TackleFoul_ReportsRestartToTheExecutionLayer()
    {
        var rules = new MatchRulesEngine(new PitchDimensions());
        var players = Squad();
        var ball = new BallState();
        rules.StartMatch(players, ball);

        Assert.False(rules.EvaluateTackleFoul(14, 9, Vector3.Zero, 0.50f, players, ball),
            "Minor contact must not report a restart");
        Assert.True(rules.EvaluateTackleFoul(14, 9, Vector3.Zero, 0.80f, players, ball),
            "Foul did not report a restart");
        Assert.Equal(MatchPhase.FreeKick, rules.Phase);
    }

    /// <summary>
    /// Audit F13: an opponent standing exactly on the restart spot was skipped (distance 0), so
    /// it stayed on top of the ball.
    /// </summary>
    [Fact]
    public void F13_CoincidentOpponent_IsDisplacedFromTheRestartSpot()
    {
        var rules = new MatchRulesEngine(new PitchDimensions());
        var players = Squad();
        var ball = new BallState();
        rules.StartMatch(players, ball);

        var spot = new Vector3(10f, 0.11f, 34f - 0.3f); // throw-in spot for Z = HalfWidth - 0.2
        var opponent = players.First(p => p.Team == TeamId.Home && p.ShirtNumber == 5);
        opponent.Position = new Vector3(spot.X, 0f, spot.Z);

        ball.LastTouchedByTeam = TeamId.Home;
        ball.Position = new Vector3(10f, 0.11f, 40f);
        rules.StepRules(players, ball, 2700f, Dt);

        Assert.True(Vector3.Distance(opponent.Position, ball.Position) > 9.0f,
            $"Coincident opponent left {Vector3.Distance(opponent.Position, ball.Position):F2} m from the spot");
    }

    // ---------------------------------------------------------------------
    // Batch 4 — F08 (typed contact events, offside at the moment of the play)
    // ---------------------------------------------------------------------

    private static (MatchRulesEngine Rules, FootballerState[] Players, BallState Ball) OffsideScenario()
    {
        var rules = new MatchRulesEngine(new PitchDimensions());
        var passer = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        var receiver = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(30f, 0f, 0f));
        var keeper = Player(12, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(20f, 0f, 0f));
        var centreBack = Player(13, TeamId.Away, TacticalRole.CenterBack, new Vector3(10f, 0f, 0f));
        var players = new[] { passer, receiver, keeper, centreBack };
        var ball = new BallState();
        rules.StartMatch(players, ball);

        // Re-place after the kickoff reset: opponents at X=20 and X=10 put the offside line at 10.
        passer.Position = Vector3.Zero;
        receiver.Position = new Vector3(30f, 0f, 0f);
        keeper.Position = new Vector3(20f, 0f, 0f);
        centreBack.Position = new Vector3(10f, 0f, 0f);
        ball.Position = new Vector3(0f, 0.11f, 0f);
        ball.LastTouchedByTeam = TeamId.Home;
        ball.LastTouchedByPlayerId = 8;

        return (rules, players, ball);
    }

    /// <summary>
    /// Audit F08: the offside snapshot was refreshed only when the toucher id changed, so a second
    /// play by the same player reused the positions from his first touch.
    /// </summary>
    [Fact]
    public void F08_RepeatedPlayBySamePlayer_RefreshesOffsideBaseline()
    {
        var (rules, players, ball) = OffsideScenario();

        // Play 1: #9 is beyond the line.
        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(1, BallContactKind.Kick, 8, TeamId.Home, ball.Position) });

        // #9 drops onside before the same player plays the ball again.
        players[1].Position = new Vector3(5f, 0f, 0f);
        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(2, BallContactKind.Kick, 8, TeamId.Home, ball.Position) });

        // Reception must not be penalised: the baseline was refreshed at the second play.
        ball.Position = new Vector3(5f, 0.11f, 0f);
        ball.LastTouchedByPlayerId = 9;
        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(3, BallContactKind.Collection, 9, TeamId.Home, ball.Position) });

        Assert.Equal(MatchPhase.FirstHalf, rules.Phase);
    }

    /// <summary>
    /// The fix must not stop genuine offside from being detected.
    /// </summary>
    [Fact]
    public void F08_ReceptionWhileStillOffside_AwardsFreeKick()
    {
        var (rules, players, ball) = OffsideScenario();

        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(1, BallContactKind.Kick, 8, TeamId.Home, ball.Position) });

        ball.Position = new Vector3(30f, 0.11f, 0f);
        ball.LastTouchedByPlayerId = 9;
        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(2, BallContactKind.Collection, 9, TeamId.Home, ball.Position) });

        Assert.Equal(MatchPhase.FreeKick, rules.Phase);
    }

    /// <summary>
    /// Once an opponent controls the ball the attacking offside phase is over, so the previously
    /// flagged player cannot be penalised for the earlier pass.
    /// </summary>
    [Fact]
    public void F08_OpponentControl_EndsTheOffsidePhase()
    {
        var (rules, players, ball) = OffsideScenario();

        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(1, BallContactKind.Kick, 8, TeamId.Home, ball.Position) });

        // Away #13 intercepts.
        ball.LastTouchedByPlayerId = 13;
        ball.LastTouchedByTeam = TeamId.Away;
        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(2, BallContactKind.Collection, 13, TeamId.Away, ball.Position) });

        // The formerly offside Home #9 now touches it: no offence.
        ball.LastTouchedByPlayerId = 9;
        ball.LastTouchedByTeam = TeamId.Home;
        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(3, BallContactKind.Collection, 9, TeamId.Home, ball.Position) });

        Assert.Equal(MatchPhase.FirstHalf, rules.Phase);
    }

    /// <summary>
    /// The execution layer must publish a typed event at the exact place it mutates the ball.
    /// </summary>
    [Fact]
    public void F08_ExecutionLayer_EmitsTypedContactEvents()
    {
        var player = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        var ball = new BallState
        {
            Position = new Vector3(0.4f, 0.11f, 0f),
            LastTouchedByTeam = TeamId.Home
        };
        player.LastCommand = new FootballerCommand(
            new Vector2(1f, 0f), new Vector2(1f, 0f), false, false, false, false, false,
            Vector3.Zero, KickIntentType.GroundPass, new Vector3(1f, 0f, 0f), 0.5f, 0f);

        var execution = new FootballerExecutionSystem(new PitchDimensions());
        execution.UpdateLocomotionAndContacts(
            new[] { player }, ball, new SeededRandom(1), Dt, onTackleContactCallback: null, tick: 42);

        var contact = Assert.Single(execution.ContactEvents);
        Assert.Equal(BallContactKind.Kick, contact.Kind);
        Assert.Equal(8, contact.PlayerId);
        Assert.Equal(TeamId.Home, contact.Team);
        Assert.Equal(42, contact.Tick);
    }

    // ---------------------------------------------------------------------
    // Batch 17 — F14 (offside restart/contact semantics)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Sets up a real restart for Home through the rules engine's own out-of-play detection, lets the
    /// setup whistle expire, and re-places the players into a fixed shape: the designated taker stands
    /// behind the ball and Home #9 is beyond the offside line (the second-last Away player is at X = 10).
    /// </summary>
    private static (MatchRulesEngine Rules, FootballerState[] Players, BallState Ball, FootballerState Taker)
        RestartScenario(MatchPhase restartPhase)
    {
        var rules = new MatchRulesEngine(new PitchDimensions());
        var midfielder = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        var runner = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(30f, 0f, 0f));
        var winger = Player(11, TeamId.Home, TacticalRole.Winger, new Vector3(46f, 0f, 20f));
        var keeper = Player(12, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(20f, 0f, 0f));
        var centreBack = Player(13, TeamId.Away, TacticalRole.CenterBack, new Vector3(10f, 0f, 0f));
        var players = new[] { midfielder, runner, winger, keeper, centreBack };
        var ball = new BallState();
        rules.StartMatch(players, ball);

        // Put the ball out of play in the way that awards Home the requested restart.
        ball.LastTouchedByPlayerId = 12;
        ball.LastTouchedByTeam = TeamId.Away;
        ball.Position = restartPhase switch
        {
            MatchPhase.ThrowIn => new Vector3(10f, 0.11f, 35f),
            MatchPhase.GoalKick => new Vector3(-53f, 0.11f, 20f),
            MatchPhase.CornerKick => new Vector3(53f, 0.11f, 20f),
            _ => throw new ArgumentOutOfRangeException(nameof(restartPhase))
        };
        rules.StepRules(players, ball, 2700f, Dt);
        Assert.Equal(restartPhase, rules.Phase);
        Assert.Equal(TeamId.Home, rules.RestartTeam);
        Assert.True(rules.ActiveRestart is RestartKind.ThrowIn or RestartKind.GoalKick or RestartKind.CornerKick,
            $"restart kind was {rules.ActiveRestart}");

        // Let the 0.25 s setup delay expire so the restart is live.
        rules.StepRules(players, ball, 2700f, 0.25f);
        Assert.Equal(MatchPhase.FirstHalf, rules.Phase);
        Assert.True(rules.DesignatedSetPieceTakerId.HasValue, "restart has no designated taker");

        var taker = players.First(p => p.Id == rules.DesignatedSetPieceTakerId!.Value);
        var spot = rules.RestartSpot;
        runner.Position = new Vector3(30f, 0f, 0f);
        winger.Position = new Vector3(46f, 0f, 20f);
        keeper.Position = new Vector3(20f, 0f, 0f);
        centreBack.Position = new Vector3(10f, 0f, 0f);
        taker.Position = new Vector3(spot.X - 0.45f, 0f, spot.Z);
        ball.Position = spot;
        ball.Velocity = Vector3.Zero;
        ball.LastTouchedByPlayerId = null;
        ball.LastTouchedByTeam = TeamId.Home;
        return (rules, players, ball, taker);
    }

    /// <summary>
    /// Plays the restart's first kick the way the match engine does: the contact is stamped with
    /// <see cref="MatchRulesEngine.ActiveRestart"/> and handed to the rules engine in the same tick.
    /// </summary>
    private static void TakeStampedRestart(
        MatchRulesEngine rules,
        FootballerState taker,
        FootballerState[] players,
        BallState ball,
        int tick,
        KickIntentType intent)
    {
        ball.Velocity = new Vector3(12f, 0f, 0f);
        ball.LastTouchedByPlayerId = taker.Id;
        ball.LastTouchedByTeam = taker.Team;

        var contacts = new List<BallContactEvent>
        {
            new(tick, BallContactKind.Kick, taker.Id, taker.Team, ball.Position) { Intent = intent }
        };
        RestartKind pending = rules.ActiveRestart;
        Assert.NotEqual(RestartKind.None, pending);
        rules.StampRestartOrigin(contacts);
        Assert.Equal(pending, contacts[0].RestartOrigin);
        rules.StepRules(players, ball, 2700f, Dt, contacts);
    }

    /// <summary>
    /// Audit F14: a player could be flagged offside for receiving the ball directly from a throw-in,
    /// a goal kick or a corner kick, all of which are explicitly exempt under Law 11.
    /// </summary>
    [Theory]
    [InlineData(MatchPhase.ThrowIn)]
    [InlineData(MatchPhase.GoalKick)]
    [InlineData(MatchPhase.CornerKick)]
    public void F14_DirectReceptionFromAnExemptRestart_IsNotOffside(MatchPhase restartPhase)
    {
        var (rules, players, ball, taker) = RestartScenario(restartPhase);
        var runner = players[1];

        // A corner is taken from the goal line, so the only way to be beyond the line is to stand
        // behind the ball, right by the goal line - which the law still exempts.
        if (restartPhase == MatchPhase.CornerKick)
            runner.Position = new Vector3(rules.RestartSpot.X + 0.2f, 0f, 30f);

        TakeStampedRestart(rules, taker, players, ball, 1, KickIntentType.GroundPass);
        Assert.Equal(RestartKind.None, rules.ActiveRestart);

        // The receiver plays the ball directly from that restart.
        ball.Position = new Vector3(runner.Position.X, 0.11f, runner.Position.Z);
        ball.LastTouchedByPlayerId = runner.Id;
        ball.LastTouchedByTeam = TeamId.Home;
        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(2, BallContactKind.Collection, runner.Id, TeamId.Home, ball.Position) });

        Assert.Equal(MatchPhase.FirstHalf, rules.Phase);
        Assert.DoesNotContain(rules.Events, e => e.Details.Contains("Offside"));
    }

    /// <summary>
    /// The other half of the same rule: a kickoff is NOT exempt (nor is a free kick or a penalty, which
    /// are snapshotted as normal play). A player standing beyond the line at the kickoff is flagged.
    /// </summary>
    [Fact]
    public void F14_Kickoff_IsNotAnExemptRestart()
    {
        var (rules, players, ball, _) = RestartScenario(MatchPhase.ThrowIn);

        // Re-create the kickoff, which is the restart the engine starts a match with.
        rules.SetupKickoffPositions(players, ball, TeamId.Home, rules.GetHomeAttackDirectionX());
        players[0].Position = new Vector3(-0.45f, 0f, 0f);
        players[1].Position = new Vector3(30f, 0f, 0f);
        players[2].Position = new Vector3(46f, 0f, 20f);
        players[3].Position = new Vector3(20f, 0f, 0f);
        players[4].Position = new Vector3(10f, 0f, 0f);
        ball.Position = new Vector3(0f, 0.11f, 0f);

        var kickoffTaker = players.First(p => p.Id == rules.DesignatedSetPieceTakerId!.Value);
        TakeStampedRestart(rules, kickoffTaker, players, ball, 3, KickIntentType.GroundPass);

        ball.Position = new Vector3(30f, 0.11f, 0f);
        ball.LastTouchedByPlayerId = 9;
        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(4, BallContactKind.Collection, 9, TeamId.Home, ball.Position) });

        Assert.Equal(MatchPhase.FreeKick, rules.Phase);
        Assert.True(rules.ActiveRestart == RestartKind.IndirectFreeKick,
            "an offside offence must restart with an indirect free kick");
    }

    /// <summary>
    /// Audit F14: a save or deflection from the keeper cleared the attacking offside snapshot, so an
    /// attacker who was in an offside position when the ball was played escaped the offence by
    /// converting the rebound. A deliberate save does not reset offside under Law 11.
    /// </summary>
    [Fact]
    public void F14_KeeperSave_DoesNotEndTheOffsidePhase()
    {
        var (rules, players, ball) = OffsideScenario();

        // Home #9 is beyond the line when #8 plays the ball: a genuine offside position.
        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(1, BallContactKind.Kick, 8, TeamId.Home, ball.Position) });

        // The Away keeper parries it instead of a defender playing it.
        ball.LastTouchedByPlayerId = 12;
        ball.LastTouchedByTeam = TeamId.Away;
        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(2, BallContactKind.Save, 12, TeamId.Away, ball.Position) { IsParry = true } });

        // #9 converts the rebound: still offside.
        ball.Position = new Vector3(30f, 0.11f, 0f);
        ball.LastTouchedByPlayerId = 9;
        ball.LastTouchedByTeam = TeamId.Home;
        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(3, BallContactKind.Collection, 9, TeamId.Home, ball.Position) });

        Assert.Equal(MatchPhase.FreeKick, rules.Phase);
        Assert.Equal(TeamId.Away, rules.RestartTeam);
        Assert.Equal(RestartKind.IndirectFreeKick, rules.ActiveRestart);
        Assert.Contains(rules.Events, e => e.Details.Contains("Offside against Player #9"));
    }

    /// <summary>
    /// The counterpart of the save rule: a defender's deliberate play (a kick or a controlled touch)
    /// does remove the attacker's offside restriction, so the same run must not be penalised.
    /// </summary>
    [Fact]
    public void F14_OpponentDeliberatePlay_EndsTheOffsidePhase()
    {
        var (rules, players, ball) = OffsideScenario();

        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(1, BallContactKind.Kick, 8, TeamId.Home, ball.Position) });

        // The Away centre-back deliberately plays the ball out of defence.
        ball.LastTouchedByPlayerId = 13;
        ball.LastTouchedByTeam = TeamId.Away;
        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(2, BallContactKind.Kick, 13, TeamId.Away, ball.Position) });

        // The formerly offside Home #9 now plays it: no offence.
        ball.Position = new Vector3(30f, 0.11f, 0f);
        ball.LastTouchedByPlayerId = 9;
        ball.LastTouchedByTeam = TeamId.Home;
        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(3, BallContactKind.Collection, 9, TeamId.Home, ball.Position) });

        Assert.Equal(MatchPhase.FirstHalf, rules.Phase);
        Assert.DoesNotContain(rules.Events, e => e.Details.Contains("Offside"));
    }

    /// <summary>
    /// Audit F14: an offside offence is restarted with an INDIRECT free kick, so a ball kicked
    /// straight into the goal from it is a goal kick, not a goal.
    /// </summary>
    [Fact]
    public void F14_IndirectFreeKickStraightIntoTheGoal_IsAGoalKick()
    {
        var (rules, players, ball, taker) = OffsideForAwayScenario();

        // Away's free kick is played straight at the Home goal by the away centre-back.
        TakeStampedRestart(rules, taker, players, ball, 3, KickIntentType.DrivenPass);
        Assert.Equal(RestartKind.None, rules.ActiveRestart);

        ball.Position = new Vector3(-53f, 0.5f, 0f);
        ball.LastTouchedByPlayerId = 13;
        ball.LastTouchedByTeam = TeamId.Away;
        rules.StepRules(players, ball, 2700f, Dt);

        Assert.Equal(0, rules.HomeScore);
        Assert.Equal(0, rules.AwayScore);
        Assert.Equal(MatchPhase.GoalKick, rules.Phase);
        Assert.Equal(TeamId.Home, rules.RestartTeam);
        Assert.Contains(rules.Events, e => e.Details.Contains("indirect restart"));
    }

    /// <summary>
    /// The second-touch requirement is satisfied by any other touch, after which a goal counts.
    /// </summary>
    [Fact]
    public void F14_IndirectFreeKick_GoalAfterASecondTouch_Counts()
    {
        var (rules, players, ball, taker) = OffsideForAwayScenario();

        // The second touch must be legal: #14 stands onside when the free kick is played.
        var awayMidfielder = players.First(p => p.Id == 14);
        awayMidfielder.Position = new Vector3(25f, 0f, 5f);
        TakeStampedRestart(rules, taker, players, ball, 3, KickIntentType.DrivenPass);

        // The team-mate touches it on, which satisfies the indirect restart.
        ball.LastTouchedByPlayerId = 14;
        ball.LastTouchedByTeam = TeamId.Away;
        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(4, BallContactKind.Collection, 14, TeamId.Away, ball.Position) });
        Assert.False(rules.AwaitingSecondTouchForGoal);

        ball.Position = new Vector3(-53f, 0.5f, 0f);
        ball.LastTouchedByPlayerId = 14;
        ball.LastTouchedByTeam = TeamId.Away;
        rules.StepRules(players, ball, 2700f, Dt);

        Assert.Equal(1, rules.AwayScore);
        Assert.Equal(0, rules.HomeScore);
    }

    /// <summary>
    /// The direct/indirect distinction: a free kick awarded for a foul is direct, so it can score
    /// without another touch. Only the offside restart is indirect.
    /// </summary>
    [Fact]
    public void F14_DirectFreeKick_CanScoreWithoutASecondTouch()
    {
        var (rules, players, ball, _) = OffsideForAwayScenario();

        // A foul by Home #8 on the away midfielder at the halfway line: a direct free kick to Away.
        bool restart = rules.EvaluateTackleFoul(
            8, 14, new Vector3(0f, 0f, 0f), 0.80f, players, ball);
        Assert.True(restart);
        Assert.Equal(MatchPhase.FreeKick, rules.Phase);
        Assert.Equal(TeamId.Away, rules.RestartTeam);
        Assert.Equal(RestartKind.FreeKick, rules.ActiveRestart);

        rules.StepRules(players, ball, 2700f, 0.25f);
        Assert.Equal(MatchPhase.FirstHalf, rules.Phase);
        Assert.True(rules.DesignatedSetPieceTakerId.HasValue);

        var taker = players.First(p => p.Id == rules.DesignatedSetPieceTakerId!.Value);
        ball.Position = rules.RestartSpot;
        ball.Velocity = new Vector3(-12f, 0f, 0f);
        ball.LastTouchedByPlayerId = taker.Id;
        ball.LastTouchedByTeam = TeamId.Away;
        var contacts = new List<BallContactEvent>
        {
            new(5, BallContactKind.Kick, taker.Id, TeamId.Away, ball.Position) { Intent = KickIntentType.DrivenPass }
        };
        rules.StampRestartOrigin(contacts);
        Assert.Equal(RestartKind.FreeKick, contacts[0].RestartOrigin);
        rules.StepRules(players, ball, 2700f, Dt, contacts);
        Assert.False(rules.AwaitingSecondTouchForGoal);

        ball.Position = new Vector3(-53f, 0.5f, 0f);
        ball.LastTouchedByTeam = TeamId.Away;
        rules.StepRules(players, ball, 2700f, Dt);

        Assert.Equal(1, rules.AwayScore);
        Assert.Equal(0, rules.HomeScore);
    }

    /// <summary>
    /// A full match must stamp its restarts itself. A throw-in forced through the engine has to reach
    /// the rules engine as a <see cref="RestartKind.ThrowIn"/> contact, not as ordinary open play.
    /// </summary>
    [Fact]
    public void F14_MatchEngine_StampsTheThrowInOntoItsFirstContact()
    {
        var sim = new MatchSimulationEngine(new PreMatchConfig { Seed = 11UL });

        sim.Ball.Position = new Vector3(0f, 0.11f, 40f);
        sim.Ball.LastTouchedByTeam = TeamId.Home;
        sim.StepTick();

        Assert.Equal(MatchPhase.ThrowIn, sim.Rules.Phase);
        Assert.Equal(RestartKind.ThrowIn, sim.Rules.ActiveRestart);
        Assert.Equal(RestartKind.None, sim.Rules.LastStampedRestartOrigin);

        for (int i = 0; i < 400 && sim.Rules.LastStampedRestartOrigin == RestartKind.None; i++)
            sim.StepTick();

        Assert.Equal(RestartKind.ThrowIn, sim.Rules.LastStampedRestartOrigin);
        Assert.Equal(RestartKind.None, sim.Rules.ActiveRestart);
    }

    /// <summary>
    /// Shared setup for the free-kick scenarios: Home #9 is offside, so the offence awards Away an
    /// indirect free kick at the offside spot, which the rules engine has already set up.
    /// </summary>
    private static (MatchRulesEngine Rules, FootballerState[] Players, BallState Ball, FootballerState Taker)
        OffsideForAwayScenario()
    {
        var rules = new MatchRulesEngine(new PitchDimensions());
        var homePasser = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        var homeRunner = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(30f, 0f, 0f));
        var awayKeeper = Player(12, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(20f, 0f, 0f));
        var awayCentreBack = Player(13, TeamId.Away, TacticalRole.CenterBack, new Vector3(10f, 0f, 0f));
        var awayMidfielder = Player(14, TeamId.Away, TacticalRole.CentralMidfielder, new Vector3(-5f, 0f, 5f));
        var players = new[] { homePasser, homeRunner, awayKeeper, awayCentreBack, awayMidfielder };
        var ball = new BallState();
        rules.StartMatch(players, ball);

        homePasser.Position = Vector3.Zero;
        homeRunner.Position = new Vector3(30f, 0f, 0f);
        awayKeeper.Position = new Vector3(20f, 0f, 0f);
        awayCentreBack.Position = new Vector3(10f, 0f, 0f);
        ball.Position = new Vector3(0f, 0.11f, 0f);
        ball.LastTouchedByTeam = TeamId.Home;
        ball.LastTouchedByPlayerId = 8;

        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(1, BallContactKind.Kick, 8, TeamId.Home, ball.Position) });

        ball.Position = new Vector3(30f, 0.11f, 0f);
        ball.LastTouchedByPlayerId = 9;
        ball.LastTouchedByTeam = TeamId.Home;
        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(2, BallContactKind.Collection, 9, TeamId.Home, ball.Position) });

        Assert.Equal(MatchPhase.FreeKick, rules.Phase);
        Assert.Equal(TeamId.Away, rules.RestartTeam);
        Assert.Equal(RestartKind.IndirectFreeKick, rules.ActiveRestart);
        Assert.Equal(0, rules.HomeScore + rules.AwayScore);

        rules.StepRules(players, ball, 2700f, 0.25f);
        Assert.Equal(MatchPhase.FirstHalf, rules.Phase);

        // The restart taker is the nearest away outfield player; put it behind the ball.
        var taker = players.First(p => p.Id == rules.DesignatedSetPieceTakerId!.Value);
        taker.Position = new Vector3(rules.RestartSpot.X + 0.45f, 0f, rules.RestartSpot.Z);
        ball.Position = rules.RestartSpot;
        ball.Velocity = Vector3.Zero;
        ball.LastTouchedByPlayerId = null;
        ball.LastTouchedByTeam = TeamId.Away;
        return (rules, players, ball, taker);
    }

    // ---------------------------------------------------------------------
    // Batch 18 — F20 (authoritative boundary crossing, goal attribution)
    // ---------------------------------------------------------------------

    private static (MatchRulesEngine Rules, FootballerState[] Players, BallState Ball) BoundaryScenario(
        float ballRadius = 0.11f)
    {
        var rules = new MatchRulesEngine(new PitchDimensions(), ballRadius);
        var players = new[]
        {
            Player(8, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(20f, 0f, 0f)),
            Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(40f, 0f, 0f)),
            Player(12, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(47f, 0f, 0f)),
            Player(13, TeamId.Away, TacticalRole.CenterBack, new Vector3(30f, 0f, 0f)),
        };
        var ball = new BallState();
        rules.StartMatch(players, ball);
        ball.Position = new Vector3(0f, 0.11f, 0f);
        ball.PreviousPosition = ball.Position;
        ball.Velocity = Vector3.Zero;
        ball.LastTouchedByTeam = TeamId.Home;
        ball.LastTouchedByPlayerId = 9;
        return (rules, players, ball);
    }

    /// <summary>
    /// Audit F20: the goal was judged from the tick's end position, so a shot that crossed the goal
    /// line above the crossbar and only descended behind the goal was scored using its later height.
    /// The crossing height is authoritative.
    /// </summary>
    [Fact]
    public void F20_HighShotDescendingBehindTheGoal_IsNotAGoal()
    {
        var pitch = new PitchDimensions();
        var (rules, players, ball) = BoundaryScenario();
        // A dipping shot: it crosses the goal line well above the crossbar but falls below it within
        // the same tick, so the tick's end state would score it while the crossing says "over the bar".
        ball.Position = new Vector3(52.60f, 2.80f, 0f);
        ball.PreviousPosition = ball.Position;
        ball.Velocity = new Vector3(6f, -25f, 0f);

        var solver = new BallPhysicsSolver();
        int ticks = 0;
        for (; ticks < 40 && rules.Phase == MatchPhase.FirstHalf; ticks++)
        {
            solver.Step(ball, Dt);
            rules.StepRules(players, ball, 2700f, Dt);
        }

        Assert.True(ticks < 40, "the ball never left the field of play");
        Assert.Equal(0, rules.HomeScore);
        Assert.Equal(0, rules.AwayScore);
        Assert.Equal(MatchPhase.GoalKick, rules.Phase);
        Assert.Equal(TeamId.Away, rules.RestartTeam);
        Assert.DoesNotContain(rules.Events, e => e.EventType == "Goal");

        // The height the ball had when the whole ball crossed the goal line was above the crossbar.
        Assert.True(ball.BoundaryCrossings.Count > 0, "no boundary crossing was recorded");
        Assert.Equal(BallBoundary.GoalLinePositiveX, ball.BoundaryCrossings[0].Boundary);
        Assert.True(ball.BoundaryCrossings[0].CrossingPosition.Y > pitch.GoalHeight,
            $"crossing height was {ball.BoundaryCrossings[0].CrossingPosition.Y:F2} m");
        Assert.True(ball.Position.Y < pitch.GoalHeight,
            $"the tick must end below the crossbar, ended at {ball.Position.Y:F2} m");
    }

    /// <summary>
    /// Audit F20: the rules tested the goal line before the touchline, so a ball that crossed the
    /// touchline first near the corner was restarted as a corner/goal kick instead of a throw-in. The
    /// first crossing of the tick decides, and it is recorded in substep order.
    /// </summary>
    [Fact]
    public void F20_DiagonalExitCrossingTheTouchlineFirst_IsAThrowIn()
    {
        var pitch = new PitchDimensions();
        var (rules, players, ball) = BoundaryScenario();

        // Just inside the corner: the touchline plane is crossed a fraction of a tick before the goal
        // line, and the tick ends beyond both lines.
        ball.Position = new Vector3(pitch.HalfLength - 0.05f, 0.11f, pitch.HalfWidth - 0.01f);
        ball.PreviousPosition = ball.Position;
        ball.Velocity = new Vector3(12f, 0f, 12f);
        ball.LastTouchedByTeam = TeamId.Away;
        ball.LastTouchedByPlayerId = 12;

        new BallPhysicsSolver().Step(ball, Dt);

        Assert.True(ball.BoundaryCrossings.Count >= 2,
            $"expected both a touchline and a goal-line crossing, got {ball.BoundaryCrossings.Count}");
        Assert.Equal(BallBoundary.TouchlinePositiveZ, ball.BoundaryCrossings[0].Boundary);
        Assert.True(ball.BoundaryCrossings[0].ElapsedSeconds <= ball.BoundaryCrossings[1].ElapsedSeconds,
            "crossings must be reported in the order they happened");
        Assert.True(MathF.Abs(ball.Position.X) > pitch.HalfLength + 0.11f, "the tick must end beyond the goal line");

        rules.StepRules(players, ball, 2700f, Dt);

        Assert.Equal(MatchPhase.ThrowIn, rules.Phase);
        Assert.Equal(TeamId.Home, rules.RestartTeam);
        Assert.Equal(pitch.HalfWidth - 0.2f, ball.Position.Z, 3);
    }

    /// <summary>
    /// The whole-ball rule must not turn a shot that rebounds off the frame back into play into a goal.
    /// </summary>
    [Fact]
    public void F20_PostRebound_IsNotAGoal()
    {
        var pitch = new PitchDimensions();
        var (rules, players, ball) = BoundaryScenario();
        ball.Position = new Vector3(pitch.HalfLength - 0.6f, 0.5f, 3.3f);
        ball.PreviousPosition = ball.Position;
        ball.Velocity = new Vector3(10f, 0f, 6f);

        var solver = new BallPhysicsSolver();
        for (int i = 0; i < 30; i++)
        {
            solver.Step(ball, Dt);
            Assert.DoesNotContain(ball.BoundaryCrossings, c => c.Boundary == BallBoundary.GoalLinePositiveX);
            rules.StepRules(players, ball, 2700f, Dt);
            Assert.Equal(MatchPhase.FirstHalf, rules.Phase);
        }

        Assert.Equal(0, rules.HomeScore + rules.AwayScore);
        Assert.True(MathF.Abs(ball.Position.X) <= pitch.HalfLength,
            $"post rebound left the ball outside the field at X={ball.Position.X:F2}");
    }

    /// <summary>
    /// Audit F20: the out-of-play plane was a hard-coded 0.11 m. IFAB Law 9 requires the WHOLE ball to
    /// cross the line, so the plane must follow the configured ball radius.
    /// </summary>
    [Fact]
    public void F20_WholeBallMustPassTheLine_WithTheConfiguredRadius()
    {
        var pitch = new PitchDimensions();
        var position = new Vector3(pitch.HalfLength + 0.15f, 0.5f, 0f);

        var (bigRules, bigPlayers, bigBall) = BoundaryScenario(ballRadius: 0.20f);
        bigBall.Position = position;
        bigRules.StepRules(bigPlayers, bigBall, 2700f, Dt);
        Assert.Equal(MatchPhase.FirstHalf, bigRules.Phase);

        var (rules, players, ball) = BoundaryScenario();
        ball.Position = position;
        rules.StepRules(players, ball, 2700f, Dt);
        Assert.Equal(MatchPhase.GoalScored, rules.Phase);

        // The solver records crossings against the same configured plane.
        var solver = new BallPhysicsSolver(new BallPhysicsParameters { Radius = 0.20f }, pitch);
        var physicsBall = new BallState
        {
            Position = new Vector3(pitch.HalfLength - 0.2f, 0.11f, 0f),
            PreviousPosition = new Vector3(pitch.HalfLength - 0.2f, 0.11f, 0f),
            Velocity = new Vector3(20f, 0f, 0f)
        };
        for (int i = 0; i < 5 && physicsBall.BoundaryCrossings.Count == 0; i++)
            solver.Step(physicsBall, Dt);

        Assert.Single(physicsBall.BoundaryCrossings);
        BallBoundaryCrossing crossing = physicsBall.BoundaryCrossings[0];
        Assert.Equal(pitch.HalfLength + 0.20f, crossing.LineCoordinate, 3);
        Assert.Equal(0.20f, crossing.BallRadius, 3);
    }

    /// <summary>
    /// Audit F20: a goal scored by the attacking team carries the scorer, not just the last toucher.
    /// </summary>
    [Fact]
    public void F20_AttackerGoal_IsCreditedToTheScorer()
    {
        var pitch = new PitchDimensions();
        var (rules, players, ball) = BoundaryScenario();
        ball.Position = new Vector3(pitch.HalfLength + 0.3f, 0.5f, 0f);
        ball.LastTouchedByPlayerId = 9;
        ball.LastTouchedByTeam = TeamId.Home;

        rules.StepRules(players, ball, 2700f, Dt);

        Assert.Equal(MatchPhase.GoalScored, rules.Phase);
        Assert.Equal(1, rules.HomeScore);
        MatchRuleEvent goal = rules.Events.Last(e => e.EventType == "Goal");
        Assert.Equal(TeamId.Home, goal.Team);
        Assert.Equal(9, goal.ScorerPlayerId);
        Assert.False(goal.IsOwnGoal);
        Assert.Null(goal.OwnGoalPlayerId);
        Assert.Equal(9, goal.PlayerId);
    }

    /// <summary>
    /// Audit F20: a keeper's save is a deflection, not a deliberate play. A shot the keeper can only
    /// parry into the net is the shooter's goal, not an own goal by the keeper.
    /// </summary>
    [Fact]
    public void F20_ShotParriedIntoTheNet_StaysTheShootersGoal()
    {
        var pitch = new PitchDimensions();
        var (rules, players, ball) = BoundaryScenario();

        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(1, BallContactKind.Kick, 9, TeamId.Home, ball.Position) { Intent = KickIntentType.Shot } });

        ball.LastTouchedByPlayerId = 12;
        ball.LastTouchedByTeam = TeamId.Away;
        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(2, BallContactKind.Save, 12, TeamId.Away, ball.Position) { IsParry = true } });

        ball.Position = new Vector3(pitch.HalfLength + 0.3f, 0.5f, 0f);
        rules.StepRules(players, ball, 2700f, Dt);

        Assert.Equal(1, rules.HomeScore);
        MatchRuleEvent goal = rules.Events.Last(e => e.EventType == "Goal");
        Assert.Equal(9, goal.ScorerPlayerId);
        Assert.False(goal.IsOwnGoal);
        // The event still records the physical last touch separately from the attribution.
        Assert.Equal(12, goal.PlayerId);
    }

    /// <summary>
    /// Audit F20: a defender's deliberate play into their own net is an own goal, attributed to that
    /// defender rather than to the attacking team's last shot.
    /// </summary>
    [Fact]
    public void F20_DefenderPutsItIntoTheirOwnNet_IsAnOwnGoal()
    {
        var pitch = new PitchDimensions();
        var (rules, players, ball) = BoundaryScenario();

        // Home #9 shoots, but the away centre-back's clearance sends it into his own goal.
        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(1, BallContactKind.Kick, 9, TeamId.Home, ball.Position) { Intent = KickIntentType.Shot } });

        ball.LastTouchedByPlayerId = 13;
        ball.LastTouchedByTeam = TeamId.Away;
        rules.StepRules(players, ball, 2700f, Dt,
            new[] { new BallContactEvent(2, BallContactKind.Kick, 13, TeamId.Away, ball.Position) { Intent = KickIntentType.Clearance } });

        ball.Position = new Vector3(pitch.HalfLength + 0.3f, 0.5f, 0f);
        rules.StepRules(players, ball, 2700f, Dt);

        Assert.Equal(1, rules.HomeScore);
        Assert.Equal(0, rules.AwayScore);
        MatchRuleEvent goal = rules.Events.Last(e => e.EventType == "Goal");
        Assert.Equal(TeamId.Home, goal.Team);
        Assert.True(goal.IsOwnGoal);
        Assert.Equal(13, goal.OwnGoalPlayerId);
        Assert.Null(goal.ScorerPlayerId);
        Assert.Equal(13, goal.PlayerId);
    }

    // ---------------------------------------------------------------------
    // Batch 19 — F22 (player contact geometry)
    // ---------------------------------------------------------------------

    private static FootballerState LandedPlayer(int id, TeamId team, Vector3 position, float strength = 0.78f) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = TacticalRole.CentralMidfielder,
        Position = position,
        Attributes = new PlayerAttributes { Strength01 = strength },
        Tendencies = new PlayerTendencies(),
        LastCommand = FootballerCommand.Idle
    };

    private static void StepPlayers(FootballerState[] players)
    {
        var ball = new BallState { Position = new Vector3(20f, 0.11f, 20f), LastTouchedByTeam = TeamId.Home };
        new FootballerExecutionSystem(new PitchDimensions()).UpdateLocomotionAndContacts(
            players, ball, new SeededRandom(1), Dt);
    }

    /// <summary>
    /// Audit F22: exactly coincident centres were skipped, so two players could occupy the same spot
    /// forever. They must be separated, deterministically.
    /// </summary>
    [Fact]
    public void F22_CoincidentPlayers_AreSeparatedDeterministically()
    {
        static (Vector3 A, Vector3 B) Run()
        {
            var a = LandedPlayer(8, TeamId.Home, new Vector3(5f, 0f, -3f));
            var b = LandedPlayer(9, TeamId.Home, new Vector3(5f, 0f, -3f));
            StepPlayers(new[] { a, b });
            return (a.Position, b.Position);
        }

        var first = Run();
        float distance = Vector2.Distance(new(first.A.X, first.A.Z), new(first.B.X, first.B.Z));
        Assert.True(distance >= FootballerState.CapsuleRadius * 2.0f - 1e-3f,
            $"coincident players stayed {distance:F3} m apart");
        Assert.True(SimMath.IsFinite(first.A) && SimMath.IsFinite(first.B));

        var second = Run();
        Assert.Equal(first.A, second.A);
        Assert.Equal(first.B, second.B);
    }

    /// <summary>
    /// Audit F22: a player whose body clears the other's head is not a horizontal contact, so an
    /// airborne player must not be pushed sideways by a grounded one.
    /// </summary>
    [Fact]
    public void F22_AirbornePlayerClearingAGroundedOne_IsNotPushedHorizontally()
    {
        var jumper = LandedPlayer(8, TeamId.Home, new Vector3(5f, 2.4f, -3f));
        var grounded = LandedPlayer(9, TeamId.Home, new Vector3(5f, 0f, -3f));

        StepPlayers(new[] { jumper, grounded });

        Assert.Equal(5f, jumper.Position.X, 3);
        Assert.Equal(-3f, jumper.Position.Z, 3);
        Assert.Equal(5f, grounded.Position.X, 3);
        Assert.Equal(-3f, grounded.Position.Z, 3);
    }

    /// <summary>
    /// The vertical rule must not stop two players on the ground from separating, nor players whose
    /// bodies still overlap vertically (a normal jump).
    /// </summary>
    [Fact]
    public void F22_GroundedAndPartlyAirbornePairs_StillCollide()
    {
        var a = LandedPlayer(8, TeamId.Home, new Vector3(5f, 0f, -3f));
        var b = LandedPlayer(9, TeamId.Home, new Vector3(5.2f, 0f, -3f));
        StepPlayers(new[] { a, b });
        float groundedDistance = Vector2.Distance(new(a.Position.X, a.Position.Z), new(b.Position.X, b.Position.Z));
        Assert.True(groundedDistance >= FootballerState.CapsuleRadius * 2.0f - 1e-3f,
            $"grounded pair stayed {groundedDistance:F3} m apart");

        var lowJumper = LandedPlayer(10, TeamId.Home, new Vector3(-5f, 0.6f, 3f));
        var below = LandedPlayer(11, TeamId.Home, new Vector3(-4.8f, 0f, 3f));
        StepPlayers(new[] { lowJumper, below });
        float airborneDistance = Vector2.Distance(new(lowJumper.Position.X, lowJumper.Position.Z), new(below.Position.X, below.Position.Z));
        Assert.True(airborneDistance >= FootballerState.CapsuleRadius * 2.0f - 1e-3f,
            $"partly airborne pair stayed {airborneDistance:F3} m apart");
    }

    /// <summary>
    /// Degenerate strength attributes must not divide by zero and poison the positions with NaNs.
    /// </summary>
    [Fact]
    public void F22_ZeroStrengthPlayers_DoNotProduceNonFinitePositions()
    {
        var a = LandedPlayer(8, TeamId.Home, new Vector3(0f, 0f, 0f), strength: 0f);
        var b = LandedPlayer(9, TeamId.Home, new Vector3(0.1f, 0f, 0f), strength: 0f);

        StepPlayers(new[] { a, b });

        Assert.True(SimMath.IsFinite(a.Position) && SimMath.IsFinite(b.Position));
        Assert.True(SimMath.IsFinite(a.Velocity) && SimMath.IsFinite(b.Velocity));
        float distance = Vector2.Distance(new(a.Position.X, a.Position.Z), new(b.Position.X, b.Position.Z));
        Assert.True(distance >= FootballerState.CapsuleRadius * 2.0f - 1e-3f,
            $"zero-strength pair stayed {distance:F3} m apart");
    }

    // ---------------------------------------------------------------------
    // Batch 5 — F12 (keeper eligibility/threat), F02 (parry spin)
    // ---------------------------------------------------------------------

    private static (GoalkeeperController Controller, FootballerState Keeper, BallState Ball) KeeperScenario(
        Vector3 keeperPosition, Vector3 ballPosition, Vector3 ballVelocity, Vector3? spin = null)
    {
        var keeper = Player(1, TeamId.Home, TacticalRole.Goalkeeper, keeperPosition);
        keeper.LastCommand = FootballerCommand.Idle;
        var ball = new BallState
        {
            Position = ballPosition,
            Velocity = ballVelocity,
            AngularVelocity = spin ?? Vector3.Zero,
            LastTouchedByTeam = TeamId.Away
        };
        return (new GoalkeeperController(new PitchDimensions()), keeper, ball);
    }

    /// <summary>
    /// Audit F02: a parry kept the incoming rolling spin, which then drove the ball back toward the
    /// goal it had just been pushed away from.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(24f)]
    [InlineData(120f)]
    public void F02_Parry_NeverSendsTheBallBackTowardItsOwnGoal(float incomingSpinZ)
    {
        var pitch = new PitchDimensions();
        var (_, keeper, ball) = KeeperScenario(
            new Vector3(-51.7f, 0f, 0f),
            new Vector3(-51.0f, 0.11f, 0f),
            new Vector3(-16f, 0f, 0f),
            new Vector3(0f, 0f, incomingSpinZ));

        new FootballerExecutionSystem(pitch).UpdateLocomotionAndContacts(
            new[] { keeper }, ball, new SeededRandom(1), Dt);

        Assert.Equal("GK:ReflexParry", keeper.LastExecutionDiagnostic);
        Assert.True(ball.Velocity.X > 0f,
            $"Parry left the ball travelling toward its own goal (velX={ball.Velocity.X:F2})");

        var solver = new BallPhysicsSolver();
        float mostGoalwardVelocity = 0f;
        bool crossedOwnGoalLine = false;
        for (int i = 0; i < 180; i++)
        {
            solver.Step(ball, Dt);
            mostGoalwardVelocity = MathF.Min(mostGoalwardVelocity, ball.Velocity.X);
            if (ball.Position.X < -pitch.HalfLength - 0.11f && MathF.Abs(ball.Position.Z) < pitch.HalfGoalWidth)
                crossedOwnGoalLine = true;
        }

        Assert.False(crossedOwnGoalLine, "Parry returned the ball into its own goal");
        Assert.True(mostGoalwardVelocity > -0.5f,
            $"Ball was driven back toward its own goal at {mostGoalwardVelocity:F2} m/s");
    }

    /// <summary>
    /// Incoming spin must not decide a parry's outcome: identical shots with different spin must
    /// leave the goalkeeper in the same state.
    /// </summary>
    [Fact]
    public void F02_Parry_OutcomeIsIndependentOfIncomingSpin()
    {
        var pitch = new PitchDimensions();
        Vector3 FinalStateFor(float spinZ)
        {
            var (_, keeper, ball) = KeeperScenario(
                new Vector3(-51.7f, 0f, 0f),
                new Vector3(-51.0f, 0.11f, 0f),
                new Vector3(-16f, 0f, 0f),
                new Vector3(0f, 0f, spinZ));
            new FootballerExecutionSystem(pitch).UpdateLocomotionAndContacts(
                new[] { keeper }, ball, new SeededRandom(1), Dt);
            return ball.Velocity;
        }

        Assert.Equal(FinalStateFor(0f), FinalStateFor(120f));
    }

    /// <summary>
    /// Audit F12: the save branch fired for any fast ball with an opposing last touch, including a
    /// ball travelling away from the keeper's goal.
    /// </summary>
    [Fact]
    public void F12_Save_RequiresBallMovingTowardOwnGoal()
    {
        var (_, keeper, ball) = KeeperScenario(
            new Vector3(-51.7f, 0f, 0f),
            new Vector3(-51.0f, 0.11f, 0f),
            new Vector3(8f, 0f, 0f)); // moving away from the -X goal

        new FootballerExecutionSystem(new PitchDimensions()).UpdateLocomotionAndContacts(
            new[] { keeper }, ball, new SeededRandom(1), Dt);

        Assert.DoesNotContain("Save", keeper.LastExecutionDiagnostic);
        Assert.DoesNotContain("Catch", keeper.LastExecutionDiagnostic);
    }

    /// <summary>
    /// Audit F12: the keeper decided distribution at ~1.24 m while its standing contact reach is
    /// ~0.84 m, so it could start walking away from a ball it could not yet play.
    /// </summary>
    [Fact]
    public void F12_Keeper_DoesNotDistributeOutsideHandlingReach()
    {
        var keeper = Player(1, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-51.9f, 0f, 0f));
        keeper.LastCommand = FootballerCommand.Idle;
        var ball = new BallState
        {
            Position = new Vector3(-50.7f, 0.11f, 0f), // 1.2 m away: inside the old 1.24 m gate
            LastTouchedByTeam = TeamId.Home
        };

        var cmd = new GoalkeeperController(new PitchDimensions())
            .Decide(Observe(keeper, new[] { keeper }, ball), Dt);

        Assert.Equal(KickIntentType.None, cmd.KickType);
    }

    /// <summary>
    /// Audit F12: handling was purely planar, so a ball over the keeper's head counted as controlled.
    /// </summary>
    [Fact]
    public void F12_Keeper_DoesNotHandleBallAboveReachHeight()
    {
        var keeper = Player(1, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-51.9f, 0f, 0f));
        keeper.LastCommand = FootballerCommand.Idle;
        var ball = new BallState
        {
            Position = new Vector3(-51.5f, 3.0f, 0f), // directly overhead, above the 2.42 m reach
            LastTouchedByTeam = TeamId.Home
        };

        var cmd = new GoalkeeperController(new PitchDimensions())
            .Decide(Observe(keeper, new[] { keeper }, ball), Dt);

        Assert.Equal(KickIntentType.None, cmd.KickType);
    }

    /// <summary>
    /// Audit F12: an already-passed ball used |ballX - keeperX|, so it still looked like a future
    /// shot and triggered a dive.
    /// </summary>
    [Fact]
    public void F12_Keeper_DoesNotDiveForBallThatAlreadyPassed()
    {
        var keeper = Player(1, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-51.6f, 0f, 0f));
        keeper.LastCommand = FootballerCommand.Idle;
        var ball = new BallState
        {
            Position = new Vector3(-52.3f, 0.11f, 0f), // behind the keeper, still travelling -X
            Velocity = new Vector3(-12f, 0f, 0f),
            LastTouchedByTeam = TeamId.Away,
            LastTouchedByPlayerId = 12
        };

        var controller = new GoalkeeperController(new PitchDimensions());
        controller.Decide(Observe(keeper, new[] { keeper }, ball), Dt);

        Assert.DoesNotContain("DiveSave", controller.GetDebugSummary());
    }

    // ---------------------------------------------------------------------
    // Batch 6 — F01 (shared pass planner)
    // ---------------------------------------------------------------------

    /// <summary>Reception reach used by the execution system for a teammate collecting a pass.</summary>
    private const float ReceptionReachMeters = 0.75f * 1.65f; // ReachRadius * 1.65 ≈ 1.24 m

    /// <summary>
    /// Plans a pass, executes the kick through the real execution layer, then flies the ball with the
    /// real physics solver while the receiver keeps running. Returns the closest approach.
    /// </summary>
    private static (float ClosestApproach, float ArrivalSpeed, PassPlan Plan) MeasurePlannedPass(
        Vector2 ballOrigin,
        Vector2 receiverPosition,
        Vector2 receiverVelocity)
    {
        var pitch = new PitchDimensions();
        var planner = new PassPlanner(pitch);
        var plan = planner.PlanGroundPass(ballOrigin, 9, receiverPosition, receiverVelocity, 8.0f);
        Assert.True(plan.Feasible, "Planner rejected a pass a player could physically make");

        var passer = Player(8, TeamId.Home, TacticalRole.CentralMidfielder,
            new Vector3(ballOrigin.X - 0.4f, 0f, ballOrigin.Y));
        var ball = new BallState
        {
            Position = new Vector3(ballOrigin.X, 0.11f, ballOrigin.Y),
            LastTouchedByTeam = TeamId.Home
        };
        passer.LastCommand = new FootballerCommand(
            Vector2.Zero, plan.AimDirectionXZ, false, false, false, false, false, Vector3.Zero,
            KickIntentType.GroundPass,
            new Vector3(plan.AimDirectionXZ.X, 0f, plan.AimDirectionXZ.Y),
            plan.Power01, 0f);

        new FootballerExecutionSystem(pitch).UpdateLocomotionAndContacts(
            new[] { passer }, ball, new SeededRandom(1), Dt);

        Assert.NotEqual(KickIntentType.None, passer.LastCommand.KickType);

        var solver = new BallPhysicsSolver();
        float closest = float.MaxValue;
        float speedAtClosest = 0f;
        for (int i = 1; i <= 300; i++)
        {
            solver.Step(ball, Dt);
            Vector2 receiverAtTime = receiverPosition + receiverVelocity * (i * Dt);
            float miss = Vector2.Distance(new Vector2(ball.Position.X, ball.Position.Z), receiverAtTime);
            if (miss < closest)
            {
                closest = miss;
                speedAtClosest = new Vector2(ball.Velocity.X, ball.Velocity.Z).Length();
            }
        }

        return (closest, speedAtClosest, plan);
    }

    [Theory]
    [InlineData(5f, 0f, 0f)]      // stationary short
    [InlineData(10f, 0f, 0f)]     // stationary medium
    [InlineData(20f, 0f, 0f)]     // stationary long
    [InlineData(30f, 0f, 0f)]     // stationary very long
    [InlineData(20f, 0f, 6f)]     // lateral run at 6 m/s (the audit's measured case)
    [InlineData(10f, 4f, 0f)]     // running away from the passer
    [InlineData(20f, -6f, 0f)]    // running toward the passer
    [InlineData(20f, 2f, 5f)]     // diagonal run
    public void F01_PlannedPass_ArrivesWithinReceptionReach(float distance, float runAway, float runLateral)
    {
        var receiverPosition = new Vector2(distance, 0f);
        var receiverVelocity = new Vector2(runAway, runLateral);

        var (closest, arrivalSpeed, _) = MeasurePlannedPass(Vector2.Zero, receiverPosition, receiverVelocity);

        Assert.True(closest <= ReceptionReachMeters,
            $"Closest approach {closest:F2} m exceeds reception reach {ReceptionReachMeters:F2} m " +
            $"(distance {distance} m, run {receiverVelocity})");
        Assert.True(arrivalSpeed <= 18f, $"Arrival speed {arrivalSpeed:F1} m/s is not trapable");
    }

    /// <summary>
    /// A ground pass cannot catch a sprinter running directly away — the handoff asks the planner to
    /// reject such a pass rather than aim somewhere optimistic. Rejection must be reported, not faked.
    /// </summary>
    [Fact]
    public void F01_UnreachableRunner_IsReportedInfeasible()
    {
        var planner = new PassPlanner(new PitchDimensions());
        var plan = planner.PlanGroundPass(
            Vector2.Zero,
            9,
            new Vector2(20f, 0f),
            new Vector2(6f, 0f),   // sprinting straight away
            8.0f);

        Assert.False(plan.Feasible, "Planner claimed it could catch a sprinter running away");
    }

    /// <summary>The same geometry mirrored for a team attacking in -X.</summary>
    [Fact]
    public void F01_PlannedPass_WorksInBothAttackDirections()
    {
        var (closest, _, _) = MeasurePlannedPass(
            Vector2.Zero,
            new Vector2(-20f, 0f),
            new Vector2(0f, -6f));

        Assert.True(closest <= ReceptionReachMeters, $"Mirrored direction miss was {closest:F2} m");
    }

    /// <summary>
    /// The defect that started the audit: a 20 m pass to a runner moving laterally at 6 m/s used to
    /// miss by 8.07 m because the receiver was led by at most 0.35 s.
    /// </summary>
    [Fact]
    public void F01_LateralRunnerLead_IsNoLongerAFractionOfASecond()
    {
        var (closest, _, plan) = MeasurePlannedPass(
            Vector2.Zero,
            new Vector2(20f, 0f),
            new Vector2(0f, 6f));

        Assert.True(plan.FlightTimeSeconds > 1.0f,
            $"Planner still assumes a sub-second flight time ({plan.FlightTimeSeconds:F2} s)");
        Assert.True(closest < 1.0f, $"Closest approach was {closest:F2} m");
    }

    // ---------------------------------------------------------------------
    // F01b — the human "call for pass" path (reported from play)
    // ---------------------------------------------------------------------

    private static (bool Found, string Offered, float ClosestToPath, float ClosestToStart, float Power, Vector2 Aim, Vector2 EndBall) MeasureCallForPass(
        bool throughBall,
        Vector2 callerVelocity)
    {
        var pitch = new PitchDimensions();
        var comms = new MatchCommunicationContext();
        var passer = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        var caller = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(14f, 0f, 3f));
        caller.Velocity = new Vector3(callerVelocity.X, 0f, callerVelocity.Y);
        Vector2 callerStart = new(caller.Position.X, caller.Position.Z);

        var ball = new BallState
        {
            Position = new Vector3(0.4f, 0.11f, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 8
        };

        var ai = new TacticalUtilityAiController(pitch, comms);
        var obs = Observe(passer, new[] { passer, caller }, ball);

        comms.RaiseCallForPass(9, 2.5f, throughBall);
        for (int i = 0; i < 12; i++) ai.Decide(obs, Dt);

        var candidate = ai.LastCandidates.FirstOrDefault(c => c.ActionName.Contains("To#9"));
        if (candidate.ActionName is null)
            return (false, string.Join(" | ", ai.LastCandidates.Select(c => c.ActionName)), float.NaN, float.NaN, 0f, Vector2.Zero, Vector2.Zero);

        passer.LastCommand = candidate.Command;
        new FootballerExecutionSystem(pitch).UpdateLocomotionAndContacts(
            new[] { passer }, ball, new SeededRandom(1), Dt);

        var solver = new BallPhysicsSolver();
        float closestToPath = float.MaxValue;
        float closestToStart = float.MaxValue;
        for (int i = 1; i <= 300; i++)
        {
            solver.Step(ball, Dt);
            Vector2 ballXZ = new(ball.Position.X, ball.Position.Z);
            closestToStart = MathF.Min(closestToStart, Vector2.Distance(ballXZ, callerStart));
            closestToPath = MathF.Min(closestToPath,
                Vector2.Distance(ballXZ, callerStart + callerVelocity * (i * Dt)));
        }

        return (true, candidate.ActionName, closestToPath, closestToStart, candidate.Command.KickPower01,
            new Vector2(candidate.Command.KickAimDirectionXZ.X, candidate.Command.KickAimDirectionXZ.Z), EndBall: new Vector2(ball.Position.X, ball.Position.Z));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void F01b_CallForPass_ReachesTheCaller(bool throughBall)
    {
        var (found, offered, closestToPath, _, power, aim, _) = MeasureCallForPass(throughBall, new Vector2(2f, 3f));

        Assert.True(found, $"No pass to the caller was offered. AI offered: {offered}");

        if (!throughBall)
        {
            Assert.True(closestToPath <= 1.5f,
                $"Pass missed the caller by {closestToPath:F2} m (power={power:F2}, aim={aim})");
        }
    }

    /// <summary>
    /// A "through" call must be played into the space the caller is running into — ahead of them along
    /// their run, not toward the goal and not at their feet (reported from play with a screenshot).
    /// </summary>
    [Fact]
    public void F01b_ThroughCall_IsPlayedAheadAlongTheRunDirection()
    {
        Vector2 runDirection = Vector2.Normalize(new Vector2(2f, 3f));
        var (found, offered, _, _, power, aim, endBall) = MeasureCallForPass(true, new Vector2(2f, 3f));

        Assert.True(found, $"No through pass was offered. AI offered: {offered}");

        Vector2 callerStart = new(14f, 3f);
        Vector2 toBall = endBall - callerStart;
        float aheadAlongRun = Vector2.Dot(toBall, runDirection);
        float acrossRun = MathF.Abs(Vector2.Dot(toBall, new Vector2(-runDirection.Y, runDirection.X)));

        Assert.True(aheadAlongRun >= 3f,
            $"Through ball ended {aheadAlongRun:F1} m along the run (expected space ahead), aim={aim}, power={power:F2}");
        Assert.True(acrossRun <= 4f,
            $"Through ball was {acrossRun:F1} m off the runner's line");
    }

    /// <summary>
    /// Reported requirement: when the human *asks* for the pass, the button hold must not set the
    /// power — the AI decides how hard to play it.
    /// </summary>
    [Fact]
    public void F01b_CallForPass_PowerComesFromTheAiNotTheButtonHold()
    {
        float PowerForHold(float hold)
        {
            var comms = new MatchCommunicationContext();
            var human = new HumanInputController(9, comms);
            var caller = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(14f, 0f, 3f));
            var passer = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
            var ball = new BallState
            {
                Position = new Vector3(0.4f, 0.11f, 0f),
                LastTouchedByTeam = TeamId.Home,
                LastTouchedByPlayerId = 8
            };

            // The human presses pass while off the ball, holding for a given duration.
            human.ReleaseChargedKick(KickIntentType.GroundPass, hold);
            human.Decide(Observe(caller, new[] { passer, caller }, ball), Dt);
            Assert.True(comms.HasActiveCall, "The call for pass was not raised");

            var ai = new TacticalUtilityAiController(new PitchDimensions(), comms);
            for (int i = 0; i < 12; i++) ai.Decide(Observe(passer, new[] { passer, caller }, ball), Dt);

            var candidate = ai.LastCandidates.FirstOrDefault(c => c.ActionName.Contains("To#9"));
            Assert.True(candidate.ActionName is not null, "No pass to the caller was offered");
            return candidate.Command.KickPower01;
        }

        float lightTap = PowerForHold(0.22f);
        float longHold = PowerForHold(0.95f);

        Assert.Equal(lightTap, longHold, 3);
    }

    /// <summary>
    /// A sprinting caller must not be "led" far upfield: if they stop or turn, an unbounded lead
    /// puts the ball nowhere near them, which is exactly what was reported from play.
    /// </summary>
    [Fact]
    public void F01b_CallForPass_SprintingCaller_GetsTheBallNearWhereTheyAre()
    {
        var (found, offered, _, closestToStart, power, aim, _) = MeasureCallForPass(false, new Vector2(0f, 8f));

        Assert.True(found, $"No pass to the caller was offered. AI offered: {offered}");
        Assert.True(closestToStart <= 4.5f,
            $"Ball never came within {closestToStart:F1} m of the caller's position when they asked (power={power:F2}, aim={aim})");
    }

    /// <summary>
    /// When the planner cannot connect (e.g. the caller is sprinting away), the call must still be
    /// honoured with a simple pass to feet instead of being silently dropped.
    /// </summary>
    [Fact]
    public void F01b_CallForPass_UnreachableRunner_StillOffersAPassToTheCaller()
    {
        var (found, offered, _, _, _, _, _) = MeasureCallForPass(false, new Vector2(8f, 0f));

        Assert.True(found, $"AI ignored the call and offered: {offered}");
    }

    // ---------------------------------------------------------------------
    // Batch 9 — F10 (ownership model + central responder), F11 (scoped reception assist)
    // ---------------------------------------------------------------------

    private static (
        BallPossessionTracker Tracker,
        TeamTacticalCoordinator Coordinator,
        FootballerState[] Players,
        BallState Ball) PossessionScenario()
    {
        var players = new[]
        {
            Player(8, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(-0.4f, 0f, 0f)),
            Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(14f, 0f, 3f)),
            Player(12, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(0f, 0f, 2f)),
            Player(13, TeamId.Away, TacticalRole.CenterBack, new Vector3(4f, 0f, 0f))
        };
        var ball = new BallState
        {
            Position = new Vector3(0f, 0.11f, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 8
        };

        return (new BallPossessionTracker(), new TeamTacticalCoordinator(new PitchDimensions()), players, ball);
    }

    /// <summary>
    /// Audit F10: the model must distinguish a controlled ball, a pass in flight, a deflection and a
    /// keeper's held ball instead of treating "last touch" as all four at once.
    /// </summary>
    [Fact]
    public void F10_OwnershipModel_SeparatesControlStates()
    {
        var (tracker, _, players, ball) = PossessionScenario();

        tracker.Update(players, ball,
            new[] { new BallContactEvent(1, BallContactKind.Collection, 8, TeamId.Home, ball.Position) }, Dt);
        Assert.Equal(BallControlState.Controlled, tracker.Current.State);
        Assert.Equal(TeamId.Home, tracker.Current.PossessionTeam);
        Assert.Equal(8, tracker.Current.ControllingPlayerId);

        ball.Velocity = new Vector3(10f, 0f, 0f);
        tracker.Update(players, ball,
            new[]
            {
                new BallContactEvent(2, BallContactKind.Kick, 8, TeamId.Home, ball.Position)
                {
                    Intent = KickIntentType.GroundPass,
                    IntendedReceiverId = 9
                }
            }, Dt);
        Assert.Equal(BallControlState.InFlightPass, tracker.Current.State);
        Assert.Equal(TeamId.Home, tracker.Current.PossessionTeam);
        Assert.Equal(9, tracker.Current.IntendedReceiverId);
        Assert.Null(tracker.Current.ControllingPlayerId);

        // A parry is a deflection: the ball is loose and the keeper's team does not gain possession.
        ball.Velocity = new Vector3(-5f, 0f, 1f);
        tracker.Update(players, ball,
            new[] { new BallContactEvent(3, BallContactKind.Save, 12, TeamId.Away, ball.Position) { IsParry = true } }, Dt);
        Assert.Equal(BallControlState.Loose, tracker.Current.State);
        Assert.Equal(TeamId.Home, tracker.Current.PossessionTeam);

        // A clean catch is an authoritatively held ball.
        ball.Velocity = Vector3.Zero;
        tracker.Update(players, ball,
            new[] { new BallContactEvent(4, BallContactKind.Save, 12, TeamId.Away, ball.Position) }, Dt);
        Assert.Equal(BallControlState.KeeperHeld, tracker.Current.State);
        Assert.Equal(TeamId.Away, tracker.Current.PossessionTeam);
        Assert.Equal(12, tracker.Current.ControllingPlayerId);
    }

    /// <summary>
    /// Audit F10: a deflection used to flip team possession to the defending team after 0.45 s of
    /// unchanged last touch, even though nobody controlled the ball.
    /// </summary>
    [Fact]
    public void F10_Deflection_DoesNotHandPossessionToTheDefendingTeam()
    {
        var (tracker, coordinator, players, ball) = PossessionScenario();

        tracker.Update(players, ball,
            new[] { new BallContactEvent(1, BallContactKind.Collection, 8, TeamId.Home, ball.Position) }, Dt);
        coordinator.ObservePossession(tracker.Current, Dt);
        Assert.Equal(TeamId.Home, coordinator.ConfirmedPossessingTeam);

        // Away keeper parries the shot away; last touch is now Away, but the ball is loose.
        ball.LastTouchedByTeam = TeamId.Away;
        ball.LastTouchedByPlayerId = 12;
        ball.Velocity = new Vector3(-5f, 0f, 1f);
        tracker.Update(players, ball,
            new[] { new BallContactEvent(2, BallContactKind.Save, 12, TeamId.Away, ball.Position) { IsParry = true } }, Dt);

        for (int i = 0; i < 60; i++)
            coordinator.ObservePossession(tracker.Current, Dt);

        Assert.Equal(BallControlState.Loose, tracker.Current.State);
        Assert.Equal(TeamId.Home, coordinator.ConfirmedPossessingTeam);
    }

    /// <summary>
    /// A clearance is a deliberate play, so it is a real change of team possession — unlike a
    /// deflection. This keeps the match flowing after clearances instead of freezing shape.
    /// </summary>
    [Fact]
    public void F10_DeliberateClearance_ChangesPossessionImmediately()
    {
        var (tracker, coordinator, players, ball) = PossessionScenario();

        tracker.Update(players, ball,
            new[] { new BallContactEvent(1, BallContactKind.Collection, 8, TeamId.Home, ball.Position) }, Dt);
        coordinator.ObservePossession(tracker.Current, Dt);

        ball.LastTouchedByTeam = TeamId.Away;
        ball.LastTouchedByPlayerId = 13;
        tracker.Update(players, ball,
            new[]
            {
                new BallContactEvent(2, BallContactKind.Kick, 13, TeamId.Away, ball.Position)
                {
                    Intent = KickIntentType.Clearance
                }
            }, Dt);
        coordinator.ObservePossession(tracker.Current, Dt);

        Assert.Equal(BallControlState.Loose, tracker.Current.State);
        Assert.Equal(TeamId.Away, coordinator.ConfirmedPossessingTeam);
    }

    /// <summary>
    /// Audit F10: authoritative control must switch team possession on that very tick, not after the
    /// old 0.45 s last-touch confirmation window.
    /// </summary>
    [Fact]
    public void F10_ControlChange_IsAppliedImmediately()
    {
        var (tracker, coordinator, players, ball) = PossessionScenario();
        tracker.Update(players, ball,
            new[] { new BallContactEvent(1, BallContactKind.Collection, 8, TeamId.Home, ball.Position) }, Dt);
        coordinator.ObservePossession(tracker.Current, Dt);
        Assert.Equal(TeamId.Home, coordinator.ConfirmedPossessingTeam);

        // Away #13 takes the ball off him.
        var away = players.Single(p => p.Id == 13);
        away.Position = ball.Position + new Vector3(0.3f, 0f, 0f);
        tracker.Update(players, ball,
            new[] { new BallContactEvent(2, BallContactKind.Tackle, 13, TeamId.Away, ball.Position) }, Dt);
        coordinator.ObservePossession(tracker.Current, Dt);

        Assert.Equal(BallControlState.Controlled, tracker.Current.State);
        Assert.Equal(TeamId.Away, coordinator.ConfirmedPossessingTeam);
    }

    /// <summary>
    /// Audit F10: an idle human standing nearest a loose ball used to suppress every AI team-mate
    /// (the human counted as the "closest team-mate"). The central election must pick an AI that
    /// can actually reach the ball, and the elected player must get a responder action.
    /// </summary>
    [Fact]
    public void F10_IdleHumanNearest_DoesNotSuppressTheAiResponder()
    {
        var pitch = new PitchDimensions();
        var human = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(1.0f, 0f, 0.4f));
        var mate = Player(10, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(6f, 0f, -3f));
        var ball = new BallState { Position = new Vector3(0f, 0.11f, 0f), LastTouchedByTeam = TeamId.Home };
        var players = new[] { human, mate };

        var election = new BallResponderElection(pitch);
        election.Update(players, ball, BallPossessionSnapshot.Loose, human.Id);

        Assert.Equal(10, election.GetResponder(TeamId.Home));
        Assert.False(election.IsResponder(TeamId.Home, human.Id),
            "The human was elected as the AI responder");

        var observation = new PerceptionSystem().BuildObservation(
            mate, players, ball, TeamId.Home, MatchPhase.FirstHalf, 10f, 1f, false,
            TeamTacticalProfile.PossessionControl, new SeededRandom(42),
            BallPossessionSnapshot.Loose, isDesignatedBallResponder: true);

        var ai = new TacticalUtilityAiController(pitch);
        for (int i = 0; i < 12; i++) ai.Decide(observation, Dt);

        Assert.Contains(ai.LastCandidates, c => c.ActionName is "InterceptPassRay" or "CloseDownCarrier" or "ExecuteTackle");

        // Without the election flag the same player must not chase (this is the old suppression).
        var passive = new PerceptionSystem().BuildObservation(
            mate, players, ball, TeamId.Home, MatchPhase.FirstHalf, 10f, 1f, false,
            TeamTacticalProfile.PossessionControl, new SeededRandom(42),
            BallPossessionSnapshot.Loose, isDesignatedBallResponder: false);
        var idleAi = new TacticalUtilityAiController(pitch);
        for (int i = 0; i < 12; i++) idleAi.Decide(passive, Dt);
        Assert.DoesNotContain(idleAi.LastCandidates,
            c => c.ActionName is "InterceptPassRay" or "CloseDownCarrier" or "ExecuteTackle");
    }

    /// <summary>
    /// Audit F10: the AI must not crowd a human who was the planned receiver of a pass; an AI
    /// intended receiver, on the other hand, is elected directly.
    /// </summary>
    [Fact]
    public void F10_IntendedHumanReceiver_IsLeftAloneButAnAiReceiverIsElected()
    {
        var pitch = new PitchDimensions();
        var human = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(10f, 0f, 0f));
        var mate = Player(10, TeamId.Home, TacticalRole.Winger, new Vector3(14f, 0f, 3f));
        var ball = new BallState
        {
            Position = new Vector3(6f, 0.11f, 0f),
            Velocity = new Vector3(10f, 0f, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 8
        };
        var players = new[] { human, mate };

        var toHuman = new BallPossessionSnapshot(BallControlState.InFlightPass, TeamId.Home, null, 9);
        var election = new BallResponderElection(pitch);
        election.Update(players, ball, toHuman, human.Id);
        Assert.Null(election.GetResponder(TeamId.Home));

        var toAi = toHuman with { IntendedReceiverId = 10 };
        election.Update(players, ball, toAi, human.Id);
        Assert.Equal(10, election.GetResponder(TeamId.Home));
    }

    /// <summary>
    /// Audit F10: the election must not flicker between two team-mates when their distances are
    /// nearly equal, but must hand over once the incumbent is clearly beaten.
    /// </summary>
    [Fact]
    public void F10_ResponderElection_KeepsTheIncumbentUntilClearlyBeaten()
    {
        var pitch = new PitchDimensions();
        var first = Player(10, TeamId.Home, TacticalRole.CenterBack, new Vector3(10f, 0f, 0f));
        var rival = Player(11, TeamId.Home, TacticalRole.CenterBack, new Vector3(12f, 0f, 0f));
        var ball = new BallState { Position = Vector3.Zero };
        var players = new[] { first, rival };

        var election = new BallResponderElection(pitch);
        election.Update(players, ball, BallPossessionSnapshot.Loose, null);
        Assert.Equal(10, election.GetResponder(TeamId.Home));

        // Marginally closer: the incumbent keeps the job.
        rival.Position = new Vector3(10.5f, 0f, 0f);
        election.Update(players, ball, BallPossessionSnapshot.Loose, null);
        Assert.Equal(10, election.GetResponder(TeamId.Home));

        // Clearly closer: the job moves.
        rival.Position = new Vector3(2f, 0f, 0f);
        election.Update(players, ball, BallPossessionSnapshot.Loose, null);
        Assert.Equal(11, election.GetResponder(TeamId.Home));
    }

    /// <summary>
    /// A planned pass must publish who it was for, so reception and possession do not have to guess
    /// the receiver from proximity.
    /// </summary>
    [Fact]
    public void F10_PlannedPass_PublishesItsIntendedReceiver()
    {
        var pitch = new PitchDimensions();
        var passer = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(-0.4f, 0f, 0f));
        var mate = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(18f, 0f, 0f));
        var ball = new BallState
        {
            Position = new Vector3(0.1f, 0.11f, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 8
        };

        var ai = new TacticalUtilityAiController(pitch);
        var observation = Observe(passer, new[] { passer, mate }, ball);
        for (int i = 0; i < 40; i++) ai.Decide(observation, Dt);

        var candidate = ai.LastCandidates.FirstOrDefault(c => c.ActionName.Contains("To#9"));
        Assert.True(candidate.ActionName is not null, "No pass to #9 was offered");

        passer.LastCommand = candidate.Command;
        var execution = new FootballerExecutionSystem(pitch);
        execution.UpdateLocomotionAndContacts(new[] { passer }, ball, new SeededRandom(1), Dt);

        var contact = Assert.Single(execution.ContactEvents);
        Assert.Equal(BallContactKind.Kick, contact.Kind);
        Assert.Equal(KickIntentType.GroundPass, contact.Intent);
        Assert.Equal(9, contact.IntendedReceiverId);

        var tracker = new BallPossessionTracker();
        tracker.Update(new[] { passer, mate }, ball, execution.ContactEvents, Dt);
        Assert.Equal(BallControlState.InFlightPass, tracker.Current.State);
        Assert.Equal(9, tracker.Current.IntendedReceiverId);
    }

    // ---- F11: reception assistance is scoped to a designated receiver ----

    private static (Vector2 Intended, Vector2 Bystander) RunReceptionAssist(
        ReceptionAssistContext? context,
        Vector2? intendedMove = null)
    {
        var pitch = new PitchDimensions();

        FootballerState Receiver(int id, Vector3 position, Vector2 move)
        {
            var player = Player(id, TeamId.Home, TacticalRole.Striker, position);
            player.LastCommand = new FootballerCommand(
                move, move, false, false, false, false, false,
                Vector3.Zero, KickIntentType.None, Vector3.UnitX, 0f, 0f);
            return player;
        }

        var intended = Receiver(9, new Vector3(4f, 0f, 1.2f), intendedMove ?? new Vector2(0f, 1f));
        var bystander = Receiver(10, new Vector3(3f, 0f, 2.2f), new Vector2(0f, 1f));
        var ball = context is null
            ? new BallState { Position = new Vector3(0f, 0.11f, 0f), LastTouchedByTeam = TeamId.Home, LastTouchedByPlayerId = 8 }
            : new BallState
            {
                Position = new Vector3(0f, 0.11f, 0f),
                Velocity = new Vector3(10f, 0f, 0f),
                LastTouchedByTeam = TeamId.Home,
                LastTouchedByPlayerId = 8
            };

        var execution = new FootballerExecutionSystem(pitch);
        for (int i = 0; i < 20; i++)
        {
            execution.UpdateLocomotionAndContacts(
                new[] { intended, bystander }, ball, new SeededRandom(1), Dt,
                receptionAssist: context);
        }

        return (new Vector2(intended.Position.X, intended.Position.Z),
                new Vector2(bystander.Position.X, bystander.Position.Z));
    }

    /// <summary>
    /// Audit F11: only the pass's designated receiver may be steered; a second nearby team-mate must
    /// move exactly as they would with no pass at all.
    /// </summary>
    [Fact]
    public void F11_ReceptionAssist_OnlyMovesTheDesignatedReceiver()
    {
        var baseline = RunReceptionAssist(null);
        var assisted = RunReceptionAssist(
            new ReceptionAssistContext(BallControlState.InFlightPass, 9, HumanPlayerId: null));

        Assert.Equal(baseline.Bystander, assisted.Bystander);
        Assert.NotEqual(baseline.Intended, assisted.Intended);
        // The assist pulls the receiver toward the pass line (Z ≈ 0), so they gain less ground away.
        Assert.True(assisted.Intended.Y < baseline.Intended.Y - 0.05f,
            $"Designated receiver was not steered onto the pass line ({baseline.Intended.Y:F2} -> {assisted.Intended.Y:F2})");
    }

    /// <summary>
    /// Audit F11: assistance used to fire for any moving friendly ball, including a team-mate's
    /// controlled dribble. It must now require an actual pass in flight.
    /// </summary>
    [Fact]
    public void F11_ReceptionAssist_DoesNotActivateForAControlledDribble()
    {
        var baseline = RunReceptionAssist(null);
        var dribble = RunReceptionAssist(
            new ReceptionAssistContext(BallControlState.Controlled, 9, HumanPlayerId: null));

        Assert.Equal(baseline, dribble);
    }

    /// <summary>
    /// Audit F11: a human deliberately running away from an incoming pass must keep their input.
    /// </summary>
    [Fact]
    public void F11_ReceptionAssist_HumanRunningAway_KeepsTheirInput()
    {
        var baseline = RunReceptionAssist(null);
        var declining = RunReceptionAssist(
            new ReceptionAssistContext(BallControlState.InFlightPass, 9, HumanPlayerId: 9));

        Assert.Equal(baseline, declining);
        Assert.True(declining.Intended.Y > 1.2f, "The human did not move in the commanded direction");
    }

    /// <summary>
    /// Audit F11: the human's pass assist must also publish the receiver it selected, so a
    /// subsequent reception is scoped to that player.
    /// </summary>
    [Fact]
    public void F11_AssistedHumanPass_CarriesItsIntendedReceiver()
    {
        var human = new HumanInputController(9);
        var self = Player(9, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        var mate = Player(10, TeamId.Home, TacticalRole.Striker, new Vector3(12f, 0f, 0f));
        var ball = new BallState
        {
            Position = new Vector3(0.45f, 0.11f, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 9
        };

        var observation = Observe(self, new[] { self, mate }, ball);
        human.ReleaseChargedKick(KickIntentType.GroundPass, 0.5f);
        var command = human.Decide(observation, Dt);

        Assert.Equal(KickIntentType.GroundPass, command.KickType);
        Assert.Equal(10, command.IntendedReceiverId);
    }

    // ---------------------------------------------------------------------
    // Batch 10 — F03 (authoritative keeper hold and single release)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Sets up a keeper saving a goalward shot from 0.5 m away, which the save branch turns into a
    /// clean catch. <paramref name="facingX"/> is the keeper's pre-catch body direction.
    /// </summary>
    private static (FootballerState Keeper, BallState Ball, FootballerExecutionSystem Execution) KeeperCatch(
        TeamId keeperTeam = TeamId.Home,
        float facingX = 1f)
    {
        var pitch = new PitchDimensions();
        bool home = keeperTeam == TeamId.Home;
        float keeperX = home ? -51.9f : 51.9f;

        var keeper = Player(1, keeperTeam, TacticalRole.Goalkeeper, new Vector3(keeperX, 0f, 0f));
        keeper.FacingXZ = new Vector2(facingX, 0f);
        keeper.LastCommand = FootballerCommand.Idle with { DesiredFacingDirectionXZ = keeper.FacingXZ };

        var ball = new BallState
        {
            Position = new Vector3(keeperX + (home ? 0.5f : -0.5f), 0.11f, 0f),
            Velocity = new Vector3(home ? -12f : 12f, 0f, 0f),
            LastTouchedByTeam = home ? TeamId.Away : TeamId.Home,
            LastTouchedByPlayerId = home ? 12 : 8
        };

        var execution = new FootballerExecutionSystem(pitch);
        execution.UpdateLocomotionAndContacts(new[] { keeper }, ball, new SeededRandom(1), Dt);
        return (keeper, ball, execution);
    }

    /// <summary>
    /// Audit F03: a "clean catch" used to place a loose ball 0.42 m in front with 1 m/s. It must be
    /// authoritatively held instead, and a held ball must be invisible to ball physics.
    /// </summary>
    [Fact]
    public void F03_CleanCatch_IsHeldAndExcludedFromBallPhysics()
    {
        var (keeper, ball, _) = KeeperCatch();

        Assert.Equal("GK:CleanCatch", keeper.LastExecutionDiagnostic);
        Assert.Equal(keeper.Id, ball.HeldByPlayerId);
        Assert.True(ball.HeldReleaseDelaySeconds > 0f, "No hold delay was applied");
        Assert.Equal(Vector3.Zero, ball.Velocity);
        Assert.Equal(Vector3.Zero, ball.AngularVelocity);

        Vector3 heldPosition = ball.Position;
        new BallPhysicsSolver().Step(ball, Dt);

        Assert.Equal(heldPosition, ball.Position);
        Assert.Equal(keeper.Id, ball.HeldByPlayerId);
    }

    /// <summary>
    /// Audit F03: with the keeper at its own goal line, facing either toward or away from its goal,
    /// the old catch→rush→tackle sequence crossed the own goal line in 31 / 82 ticks.
    /// </summary>
    [Theory]
    [InlineData(-1f)]
    [InlineData(1f)]
    public void F03_CleanCatch_FacingEitherWay_DoesNotProduceAnOwnGoal(float facingX)
    {
        var pitch = new PitchDimensions();
        var (keeper, ball, execution) = KeeperCatch(facingX: facingX);
        var controller = new GoalkeeperController(pitch);
        var solver = new BallPhysicsSolver();

        bool ownGoal = false;
        for (int tick = 1; tick <= 150; tick++)
        {
            var players = new[] { keeper };
            keeper.LastCommand = controller.Decide(Observe(keeper, players, ball), Dt);
            execution.UpdateLocomotionAndContacts(players, ball, new SeededRandom((ulong)tick), Dt);
            solver.Step(ball, Dt);

            if (ball.Position.X < -pitch.HalfLength - 0.11f &&
                MathF.Abs(ball.Position.Z) < pitch.HalfGoalWidth)
            {
                ownGoal = true;
            }
        }

        Assert.False(ownGoal, "A keeper catch still ended up in its own goal");
        Assert.True(ball.Position.X > -pitch.HalfLength,
            $"Ball finished behind its own goal line at {ball.Position}");
    }

    /// <summary>
    /// Save at both goal lines: the hold must belong to the saving keeper's team, not be inferred
    /// from a hard-coded side of the pitch.
    /// </summary>
    [Theory]
    [InlineData(TeamId.Home)]
    [InlineData(TeamId.Away)]
    public void F03_CleanCatch_HoldsAtBothGoalLines(TeamId keeperTeam)
    {
        var (keeper, ball, execution) = KeeperCatch(keeperTeam);
        var tracker = new BallPossessionTracker();
        tracker.Update(new[] { keeper }, ball, execution.ContactEvents, Dt);

        Assert.Equal(keeper.Id, ball.HeldByPlayerId);
        Assert.Equal(keeperTeam, ball.LastTouchedByTeam);
        Assert.Equal(BallControlState.KeeperHeld, tracker.Current.State);
        Assert.Equal(keeperTeam, tracker.Current.PossessionTeam);
        Assert.Equal(keeper.Id, tracker.Current.ControllingPlayerId);
    }

    /// <summary>
    /// Audit F03: an opponent could tackle the keeper while it held the ball, sending it goalward.
    /// A held ball may only be released by its holder.
    /// </summary>
    [Fact]
    public void F03_HeldBall_CannotBeTackledByAnOpponent()
    {
        var (keeper, ball, execution) = KeeperCatch();
        var opponent = Player(12, TeamId.Away, TacticalRole.Striker, keeper.Position + new Vector3(0.45f, 0f, 0f));
        opponent.LastCommand = FootballerCommand.Idle with
        {
            RequestTackle = true,
            DesiredMoveDirectionXZ = new Vector2(-1f, 0f)
        };

        var players = new[] { keeper, opponent };
        for (int tick = 0; tick < 20; tick++)
        {
            keeper.LastCommand = FootballerCommand.Idle;
            execution.UpdateLocomotionAndContacts(players, ball, new SeededRandom(2), Dt);
        }

        Assert.Equal(keeper.Id, ball.HeldByPlayerId);
        Assert.Equal(keeper.Id, ball.LastTouchedByPlayerId);
    }

    /// <summary>
    /// Audit F03: the keeper must release the ball exactly once, after the hold delay, and never
    /// toward its own goal.
    /// </summary>
    [Fact]
    public void F03_HeldKeeper_ReleasesExactlyOnceAwayFromItsOwnGoal()
    {
        var pitch = new PitchDimensions();
        var (keeper, ball, execution) = KeeperCatch();
        var mate = Player(6, TeamId.Home, TacticalRole.DefensiveMidfielder, new Vector3(-33f, 0f, -7f));
        var controller = new GoalkeeperController(pitch);
        var solver = new BallPhysicsSolver();

        int keeperKicks = 0;
        int releaseTick = 0;
        bool sawHolding = false;

        for (int tick = 1; tick <= 150; tick++)
        {
            var players = new[] { keeper, mate };
            keeper.LastCommand = controller.Decide(Observe(keeper, players, ball), Dt);
            if (controller.State == GoalkeeperState.HoldingBall)
                sawHolding = true;

            execution.UpdateLocomotionAndContacts(players, ball, new SeededRandom((ulong)tick), Dt);
            foreach (var contact in execution.ContactEvents)
            {
                if (contact.Kind != BallContactKind.Kick || contact.PlayerId != keeper.Id) continue;
                keeperKicks++;
                if (releaseTick == 0) releaseTick = tick;
            }

            solver.Step(ball, Dt);
        }

        Assert.True(sawHolding, "The keeper never entered the HoldingBall state");
        Assert.Equal(1, keeperKicks);
        Assert.Null(ball.HeldByPlayerId);
        Assert.True(releaseTick > 20, $"Keeper released before the hold delay (tick {releaseTick})");
        Assert.True(ball.Velocity.X > 0f || ball.Position.X > keeper.Position.X,
            $"Distribution went toward its own goal (balX={ball.Position.X:F1}, velX={ball.Velocity.X:F1})");
    }

    /// <summary>
    /// Audit F03: a friendly back-pass must be handled with the feet, never caught as a save.
    /// </summary>
    [Fact]
    public void F03_BackPass_IsNotCaughtAndIsDistributedWithTheFeet()
    {
        var pitch = new PitchDimensions();
        var keeper = Player(1, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-51.9f, 0f, 0f));
        keeper.LastCommand = FootballerCommand.Idle;
        var mate = Player(6, TeamId.Home, TacticalRole.DefensiveMidfielder, new Vector3(-35f, 0f, -6f));
        var ball = new BallState
        {
            Position = new Vector3(-51.4f, 0.11f, 0f),
            Velocity = new Vector3(-3f, 0f, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 5
        };

        new FootballerExecutionSystem(pitch).UpdateLocomotionAndContacts(
            new[] { keeper }, ball, new SeededRandom(1), Dt);

        Assert.Null(ball.HeldByPlayerId);
        Assert.DoesNotContain("Catch", keeper.LastExecutionDiagnostic);
        Assert.DoesNotContain("Save", keeper.LastExecutionDiagnostic);

        var controller = new GoalkeeperController(pitch);
        var command = controller.Decide(Observe(keeper, new[] { keeper, mate }, ball), Dt);

        Assert.NotEqual(KickIntentType.None, command.KickType);
        Assert.Equal(GoalkeeperState.Distribution, controller.State);
    }

    /// <summary>
    /// Audit F03: the keeper must not charge down its own team's loose ball (a back-pass or its own
    /// parry) — only an opponent's ball may be swept.
    /// </summary>
    [Fact]
    public void F03_Keeper_DoesNotRushItsOwnLooseBall()
    {
        var pitch = new PitchDimensions();
        var keeper = Player(1, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-51.9f, 0f, 0f));
        keeper.LastCommand = FootballerCommand.Idle;
        var ball = new BallState
        {
            Position = new Vector3(-47f, 0.11f, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 5
        };

        var controller = new GoalkeeperController(pitch);
        controller.Decide(Observe(keeper, new[] { keeper }, ball), Dt);

        Assert.DoesNotContain("Rush", controller.GetDebugSummary());
    }

    /// <summary>
    /// Audit F03: a keeper removed from the pitch while holding must not leave the ball stuck in
    /// its hands forever.
    /// </summary>
    [Fact]
    public void F03_KeeperRemovedWhileHolding_ReleasesTheBall()
    {
        var (keeper, ball, execution) = KeeperCatch();
        keeper.IsSentOff = true;

        execution.UpdateLocomotionAndContacts(Array.Empty<FootballerState>(), ball, new SeededRandom(3), Dt);

        Assert.Null(ball.HeldByPlayerId);
        Assert.Equal(0f, ball.HeldReleaseDelaySeconds);
        Assert.Equal(0.11f, ball.Position.Y, 3);
    }

    // ---------------------------------------------------------------------
    // Batch 11 — F04 (persistent off-ball jobs, situational targets)
    // ---------------------------------------------------------------------

    /// <summary>Home (attacking +X) with a carrier at (0,10) and a back line; Away in a high block.</summary>
    private static (List<FootballerState> Players, BallState Ball) JobScenario()
    {
        var players = new List<FootballerState>();

        void AddPlayer(int id, TeamId team, TacticalRole role, float x, float z)
        {
            var player = Player(id, team, role, new Vector3(x, 0f, z), id);
            player.TacticalAnchor = new Vector3(x, 0f, z);
            players.Add(player);
        }

        AddPlayer(1, TeamId.Home, TacticalRole.Goalkeeper, -50f, 0f);
        AddPlayer(2, TeamId.Home, TacticalRole.FullBack, -8f, 18f);
        AddPlayer(3, TeamId.Home, TacticalRole.FullBack, -8f, -18f);
        AddPlayer(4, TeamId.Home, TacticalRole.CenterBack, -20f, 6f);
        AddPlayer(5, TeamId.Home, TacticalRole.CenterBack, -20f, -6f);
        AddPlayer(6, TeamId.Home, TacticalRole.DefensiveMidfielder, -6f, 2f);
        AddPlayer(7, TeamId.Home, TacticalRole.Winger, 10f, 26f);
        AddPlayer(8, TeamId.Home, TacticalRole.CentralMidfielder, 0f, 10f); // carrier
        AddPlayer(9, TeamId.Home, TacticalRole.Striker, 18f, 0f);
        AddPlayer(10, TeamId.Home, TacticalRole.CentralMidfielder, 6f, -12f);
        AddPlayer(11, TeamId.Home, TacticalRole.Winger, 10f, -26f);

        AddPlayer(12, TeamId.Away, TacticalRole.Goalkeeper, 50f, 0f);
        AddPlayer(13, TeamId.Away, TacticalRole.CenterBack, 16f, 6f);
        AddPlayer(14, TeamId.Away, TacticalRole.CenterBack, 20f, -6f);
        AddPlayer(15, TeamId.Away, TacticalRole.DefensiveMidfielder, 12f, 8f);
        AddPlayer(16, TeamId.Away, TacticalRole.FullBack, 26f, 20f);
        AddPlayer(17, TeamId.Away, TacticalRole.FullBack, 26f, -20f);
        AddPlayer(18, TeamId.Away, TacticalRole.CentralMidfielder, 14f, -4f);
        AddPlayer(19, TeamId.Away, TacticalRole.Winger, 30f, 18f);
        AddPlayer(20, TeamId.Away, TacticalRole.Winger, 30f, -18f);
        AddPlayer(21, TeamId.Away, TacticalRole.Striker, 34f, 0f);
        AddPlayer(22, TeamId.Away, TacticalRole.CentralMidfielder, 22f, 0f);

        var ball = new BallState
        {
            Position = new Vector3(0f, 0.11f, 10f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 8
        };
        return (players, ball);
    }

    private static BallPossessionSnapshot HomePossession() =>
        new(BallControlState.Controlled, TeamId.Home, 8, null);

    private static List<TeamJobAssignment> AssignedJobs(
        TeamJobCoordinator jobs, IReadOnlyList<FootballerState> players, TeamId team)
    {
        var result = new List<TeamJobAssignment>();
        foreach (var player in players)
        {
            if (player.Team != team || player.Role == TacticalRole.Goalkeeper) continue;
            TeamJobAssignment? job = jobs.Get(player.Id);
            if (job.HasValue) result.Add(job.Value);
        }
        return result;
    }

    /// <summary>
    /// Audit F04: in possession the team must assign coordinated jobs (outlet, third-player option,
    /// width, depth, overlap, cover), not one support command per player.
    /// </summary>
    [Fact]
    public void F04_InPossession_AssignsCoordinatedOffBallJobs()
    {
        var pitch = new PitchDimensions();
        var (players, ball) = JobScenario();
        var jobs = new TeamJobCoordinator(pitch);

        jobs.Update(players, ball, HomePossession(), TeamId.Home,
            homeResponderId: null, awayResponderId: 15, homeDirX: 1f, tick: 0,
            TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
            TruthBelief(players, TeamId.Home), TruthBelief(players, TeamId.Away));

        List<TeamJobAssignment> homeJobs = AssignedJobs(jobs, players, TeamId.Home);
        Assert.True(jobs.Assignments.Count > 0,
            $"No assignments at all (players={players.Count}, count={jobs.Assignments.Count}, " +
            $"raw={string.Join(",", jobs.Assignments.Select(kv => $"{kv.Key}:{kv.Value.Job}"))})");

        foreach (TacticalJob expected in new[]
                 {
                     TacticalJob.NearOutlet, TacticalJob.ThirdPlayerOption, TacticalJob.FarSideWidth,
                     TacticalJob.DepthRun, TacticalJob.Overlap, TacticalJob.DefensiveCover
                 })
        {
            Assert.True(homeJobs.Any(j => j.Job == expected),
                $"{expected} missing; assigned: {string.Join(", ", homeJobs.Select(j => j.Job))}; " +
                $"raw={string.Join(",", jobs.Assignments.Select(kv => $"{kv.Key}:{kv.Value.Job}"))}");
            Assert.Equal(1, homeJobs.Count(j => j.Job == expected));
        }

        // Every outfield Home player has an assignment, and no job anchor is offside or off-pitch.
        foreach (var player in players.Where(p => p.Team == TeamId.Home && p.Role != TacticalRole.Goalkeeper))
            Assert.NotNull(jobs.Get(player.Id));

        float offsideLineProj = 34f; // second-last Away defender/forward in the scenario
        foreach (var job in homeJobs.Where(j => j.Job is TacticalJob.NearOutlet or TacticalJob.ThirdPlayerOption
                     or TacticalJob.FarSideWidth or TacticalJob.DepthRun or TacticalJob.Overlap))
        {
            Assert.True(job.Anchor.X * 1f <= offsideLineProj - 1f,
                $"{job.Job} target is offside at X={job.Anchor.X:F1}");
            Assert.InRange(job.Anchor.X, -pitch.HalfLength + 3f, pitch.HalfLength - 3f);
            Assert.InRange(job.Anchor.Z, -pitch.HalfWidth + 2f, pitch.HalfWidth - 2f);
        }

        TeamJobAssignment third = homeJobs.Single(j => j.Job == TacticalJob.ThirdPlayerOption);
        TeamJobAssignment width = homeJobs.Single(j => j.Job == TacticalJob.FarSideWidth);
        TeamJobAssignment depth = homeJobs.Single(j => j.Job == TacticalJob.DepthRun);
        TeamJobAssignment cover = homeJobs.Single(j => j.Job == TacticalJob.DefensiveCover);

        Assert.True(third.Anchor.Z < ball.Position.Z - 4f,
            "Third-player option did not switch to the opposite side of the carrier");
        Assert.True(MathF.Abs(width.Anchor.Z) > 15f, "Far-side width target is not wide");
        Assert.True(depth.Anchor.X > 8f, "Depth run is not ahead of the carrier");
        Assert.True(cover.Anchor.X < ball.Position.X, "Defensive cover is not goal-side of the ball");

        // The "small set of reachable options" the stationary carrier can actually use.
        var carrier = players.Single(p => p.Id == 8);
        Vector2 carrierXZ = new(carrier.Position.X, carrier.Position.Z);
        FootballerObservation carrierObs = Observe(carrier, players, ball, HomePossession());

        int openOptions = 0;
        foreach (var job in homeJobs)
        {
            Vector2 targetXZ = new(job.Anchor.X, job.Anchor.Z);
            float distance = Vector2.Distance(carrierXZ, targetXZ);
            if (distance is < 5f or > 35f) continue;
            if (TacticalUtilityAiController.EvaluateLaneClearance(carrierXZ, targetXZ, carrierObs.VisiblePlayers, TeamId.Home) < 0.5f)
                continue;
            openOptions++;
        }

        Assert.True(openOptions >= 3, $"Only {openOptions} open passing options were created");
    }

    /// <summary>
    /// Audit F04: a turnover must interrupt the jobs immediately and give the defending team a
    /// presser plus a covering defender goal-side of the ball, while the new carrier's team gets
    /// attacking options.
    /// </summary>
    [Fact]
    public void F04_Turnover_AssignsPresserAndCoverImmediately()
    {
        var pitch = new PitchDimensions();
        var (players, ball) = JobScenario();
        var jobs = new TeamJobCoordinator(pitch);

        jobs.Update(players, ball, HomePossession(), TeamId.Home, null, 15, 1f, 0,
            TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
            TruthBelief(players, TeamId.Home), TruthBelief(players, TeamId.Away));

        // Away #15 wins the ball at (4,8); Home #6 is the elected presser.
        var awayCarrier = players.Single(p => p.Id == 15);
        awayCarrier.Position = new Vector3(4f, 0f, 8f);
        var turnoverBall = new BallState
        {
            Position = new Vector3(4f, 0.11f, 8f),
            LastTouchedByTeam = TeamId.Away,
            LastTouchedByPlayerId = 15
        };
        var possessionAway = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Away, 15, null);

        jobs.Update(players, turnoverBall, possessionAway, TeamId.Away, homeResponderId: 6, awayResponderId: null, 1f, 60,
            TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
            TruthBelief(players, TeamId.Home), TruthBelief(players, TeamId.Away));

        Assert.True(jobs.Get(6)?.Job == TacticalJob.Presser,
            $"#6 job = {jobs.Get(6)?.Job}; Home jobs: {string.Join(", ", AssignedJobs(jobs, players, TeamId.Home).Select(j => j.Job))}");

        List<TeamJobAssignment> homeJobs = AssignedJobs(jobs, players, TeamId.Home);
        foreach (var job in homeJobs)
        {
            Assert.Contains(job.Job, new[]
            {
                TacticalJob.Presser, TacticalJob.CoverDefender, TacticalJob.Marker,
                TacticalJob.DefensiveLine, TacticalJob.Shape
            });
        }

        TeamJobAssignment cover = homeJobs.Single(j => j.Job == TacticalJob.CoverDefender);
        Assert.True(cover.Anchor.X < jobs.Get(6)!.Value.Anchor.X,
            $"Cover defender at X={cover.Anchor.X:F1} is not goal-side of the presser");

        List<TeamJobAssignment> awayJobs = AssignedJobs(jobs, players, TeamId.Away);
        Assert.Contains(awayJobs, j => j.Job is TacticalJob.NearOutlet or TacticalJob.ThirdPlayerOption
            or TacticalJob.FarSideWidth or TacticalJob.DepthRun or TacticalJob.Overlap or TacticalJob.DefensiveCover);
    }

    /// <summary>
    /// Audit F04: jobs are commitments. They must survive small ball movement inside the hold window
    /// and only be interrupted by a real possession change.
    /// </summary>
    [Fact]
    public void F04_JobsPersistAndOnlyPossessionInterruptsThem()
    {
        var pitch = new PitchDimensions();
        var (players, ball) = JobScenario();
        var jobs = new TeamJobCoordinator(pitch);

        jobs.Update(players, ball, HomePossession(), TeamId.Home, null, 15, 1f, 0,
            TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
            TruthBelief(players, TeamId.Home), TruthBelief(players, TeamId.Away));

        var initialJobs = new Dictionary<int, TacticalJob>();
        foreach (var player in players.Where(p => p.Team == TeamId.Home && p.Role != TacticalRole.Goalkeeper))
            initialJobs[player.Id] = jobs.Get(player.Id)!.Value.Job;

        // The carrier drifts a few metres; jobs stay put for the hold window.
        ball.Position = new Vector3(4f, 0.11f, 12f);
        jobs.Update(players, ball, HomePossession(), TeamId.Home, null, 15, 1f, 10,
            TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
            TruthBelief(players, TeamId.Home), TruthBelief(players, TeamId.Away));
        jobs.Update(players, ball, HomePossession(), TeamId.Home, null, 15, 1f, 20,
            TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
            TruthBelief(players, TeamId.Home), TruthBelief(players, TeamId.Away));

        foreach (var (playerId, job) in initialJobs)
            Assert.Equal(job, jobs.Get(playerId)!.Value.Job);

        // A turnover is urgent: the attacking jobs are gone on the same update.
        var possessionAway = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Away, 15, null);
        jobs.Update(players, ball, possessionAway, TeamId.Away, 6, null, 1f, 24,
            TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
            TruthBelief(players, TeamId.Home), TruthBelief(players, TeamId.Away));

        var homeJobs = AssignedJobs(jobs, players, TeamId.Home);
        Assert.DoesNotContain(homeJobs, j => j.Job is TacticalJob.NearOutlet or TacticalJob.ThirdPlayerOption
            or TacticalJob.FarSideWidth or TacticalJob.DepthRun or TacticalJob.Overlap or TacticalJob.DefensiveCover);
    }

    // ---------------------------------------------------------------------
    // Batch 12 — F16 (shot aim from the current aim source, one charge at a time)
    // ---------------------------------------------------------------------

    private static (HumanInputController Human, FootballerState Self, BallState Ball, FootballerObservation Obs)
        ShootingScenario(FootballerState? goalkeeper = null)
    {
        var human = new HumanInputController(9);
        var self = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(40f, 0f, 0f));
        var ball = new BallState
        {
            Position = new Vector3(40.4f, 0.11f, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 9
        };

        var players = goalkeeper is null ? new[] { self } : new[] { self, goalkeeper };
        return (human, self, ball, Observe(self, players, ball));
    }

    /// <summary>
    /// The right stick is no longer an aim input: aim follows the movement direction, and a shot
    /// follows that direction into the goal.
    /// </summary>
    [Theory]
    [InlineData(-1f)]
    [InlineData(1f)]
    public void F16_ShotAim_FollowsTheMovementDirection(float aimZ)
    {
        var (human, _, _, observation) = ShootingScenario();
        human.MoveStickXZ = new Vector2(1f, aimZ);

        human.ReleaseChargedKick(KickIntentType.Shot, 0.8f);
        var command = human.Decide(observation, Dt);

        Assert.Equal(KickIntentType.Shot, command.KickType);
        Assert.True(command.KickAimDirectionXZ.X > 0f);
        Assert.Equal(MathF.Sign(aimZ), MathF.Sign(command.KickAimDirectionXZ.Z));
    }

    /// <summary>
    /// A cursor aim point is a real aim source, even when the movement stick says something else
    /// (only when no gamepad is connected, which the Godot reader enforces).
    /// </summary>
    [Theory]
    [InlineData(-3f)]
    [InlineData(3f)]
    public void F16_ShotAim_UsesTheMouseAimPoint(float aimPointZ)
    {
        var (human, _, _, observation) = ShootingScenario();
        human.AimPointXZ = new Vector2(52f, aimPointZ);
        human.MoveStickXZ = new Vector2(-1f, 0f); // deliberately the opposite hint

        human.ReleaseChargedKick(KickIntentType.Shot, 0.8f);
        var command = human.Decide(observation, Dt);

        Assert.Equal(KickIntentType.Shot, command.KickType);
        Assert.Equal(MathF.Sign(aimPointZ), MathF.Sign(command.KickAimDirectionXZ.Z));
    }

    /// <summary>
    /// An aim point wide of the goal is clamped just inside the posts instead of missing the target.
    /// </summary>
    [Fact]
    public void F16_ShotAim_IsClampedInsideThePosts()
    {
        var (human, self, _, observation) = ShootingScenario();
        human.AimPointXZ = new Vector2(52f, 30f);
        human.MoveStickXZ = Vector2.Zero;

        human.ReleaseChargedKick(KickIntentType.Shot, 0.8f);
        var command = human.Decide(observation, Dt);

        Vector2 aim = new(command.KickAimDirectionXZ.X, command.KickAimDirectionXZ.Z);
        Assert.True(aim.X > 0.1f, "Shot did not head toward the goal");
        float t = (52.15f - self.Position.X) / aim.X;
        float zAtGoalLine = self.Position.Z + aim.Y * t;
        Assert.InRange(zAtGoalLine, -3.35f, 3.35f);
    }

    /// <summary>
    /// With no directional input at all, assistance may still pick the corner away from the keeper.
    /// </summary>
    [Fact]
    public void F16_ShotAim_WithoutInput_AimsAwayFromTheKeeper()
    {
        var keeper = Player(12, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(52f, 0f, 2.5f));
        var (human, _, _, observation) = ShootingScenario(keeper);
        human.MoveStickXZ = Vector2.Zero;
        human.AimPointXZ = null;

        human.ReleaseChargedKick(KickIntentType.Shot, 0.8f);
        var command = human.Decide(observation, Dt);

        Assert.True(command.KickAimDirectionXZ.Z < 0f,
            $"Aim was {command.KickAimDirectionXZ} — expected the corner away from the keeper");
    }

    /// <summary>
    /// A cursor aim point selects the pass target even while the player runs another way (mouse
    /// users only; a gamepad player aims with the movement stick).
    /// </summary>
    [Fact]
    public void F16_AssistedPass_UsesTheCursorAimWhileMovementPointsElsewhere()
    {
        var human = new HumanInputController(9);
        var self = Player(9, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        var mateUp = Player(10, TeamId.Home, TacticalRole.Striker, new Vector3(0f, 0f, -12f));
        var mateDown = Player(11, TeamId.Home, TacticalRole.Striker, new Vector3(0f, 0f, 12f));
        var ball = new BallState
        {
            Position = new Vector3(0.4f, 0.11f, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 9
        };
        var observation = Observe(self, new[] { self, mateUp, mateDown }, ball);

        human.MoveStickXZ = new Vector2(1f, 0f);        // running forward
        human.AimPointXZ = new Vector2(0f, -14f);       // cursor on the up-field team-mate
        human.ReleaseChargedKick(KickIntentType.GroundPass, 0.5f);
        var command = human.Decide(observation, Dt);

        Assert.Equal(KickIntentType.GroundPass, command.KickType);
        Assert.Equal(10, command.IntendedReceiverId);
    }

    // ---- FIFA-style shooting model: power/height, finesse, chip, trivela, foot strike ----

    private static (
        float CrossingY,
        float CrossingZ,
        float MaxOffLine,
        float MaxY,
        string Diagnostic) ExecuteShot(
        float power,
        KickIntentType type,
        float cornerZ,
        float startX = 36f,
        Footedness foot = Footedness.Right)
    {
        var pitch = new PitchDimensions();
        var shooter = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(startX, 0f, 0f));
        var attributes = new PlayerAttributes { PreferredFoot = foot };
        var shooterWithFoot = new FootballerState
        {
            Id = shooter.Id,
            Team = shooter.Team,
            ShirtNumber = shooter.ShirtNumber,
            Role = shooter.Role,
            Attributes = attributes,
            Tendencies = shooter.Tendencies,
            Position = shooter.Position,
            FacingXZ = new Vector2(1f, 0f)
        };
        shooter = shooterWithFoot;

        var ball = new BallState
        {
            Position = new Vector3(startX + 0.4f, 0.11f, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 9
        };

        Vector2 aim = SimMath.SafeNormalize(
            new Vector2(pitch.HalfLength, cornerZ) - new Vector2(startX, 0f),
            new Vector2(1f, 0f));
        float curve = 0f;
        if (type is KickIntentType.CurledShot or KickIntentType.TrivelaShot)
        {
            float side = aim.X * aim.Y;
            curve = MathF.Abs(side) > 0.005f ? MathF.Sign(side) : 1f;
        }

        shooter.LastCommand = new FootballerCommand(
            aim, aim, false, false, false, false, false, Vector3.Zero,
            type, new Vector3(aim.X, 0f, aim.Y), power, curve);

        new FootballerExecutionSystem(pitch).UpdateLocomotionAndContacts(
            new[] { shooter }, ball, new SeededRandom(1), Dt);

        var solver = new BallPhysicsSolver();
        float crossingY = float.NaN;
        float crossingZ = float.NaN;
        float maxOffLine = 0f;
        float maxY = 0f;
        Vector2 origin = new(startX, 0f);

        for (int i = 1; i <= 240; i++)
        {
            solver.Step(ball, Dt);
            Vector2 ballXZ = new(ball.Position.X, ball.Position.Z);
            float along = Vector2.Dot(ballXZ - origin, aim);
            Vector2 onLine = origin + aim * MathF.Max(0f, along);
            maxOffLine = MathF.Max(maxOffLine, Vector2.Distance(ballXZ, onLine));
            maxY = MathF.Max(maxY, ball.Position.Y);

            if (ball.Position.X < pitch.HalfLength) continue;
            crossingY = ball.Position.Y;
            crossingZ = ball.Position.Z;
            break;
        }

        return (crossingY, crossingZ, maxOffLine, maxY, shooter.LastExecutionDiagnostic);
    }

    /// <summary>
    /// FIFA-style power curve: a light tap stays down, ~85 % is the sweet spot under the bar, and a
    /// full-power shot clears the bar instead of always scoring.
    /// </summary>
    [Fact]
    public void F16_ShotPower_HasASweetSpotAndFliesOverAtFullPower()
    {
        var low = ExecuteShot(0.30f, KickIntentType.Shot, 0f);
        var sweet = ExecuteShot(0.85f, KickIntentType.Shot, 0f);
        var full = ExecuteShot(1.00f, KickIntentType.Shot, 0f);

        Assert.True(low.CrossingY < 1.0f, $"A tap rose to {low.CrossingY:F2} m");
        Assert.InRange(sweet.CrossingY, 1.6f, 2.44f);
        Assert.True(full.CrossingY > 2.44f,
            $"A 100 % shot crossed at {full.CrossingY:F2} m — it must clear the crossbar");
    }

    /// <summary>
    /// The finesse shot must visibly bend: aiming at the far corner, the ball curves back toward the
    /// goal and still crosses inside the posts under the bar.
    /// </summary>
    [Fact]
    public void F16_FinesseShot_CurvesInwardAndStaysOnTarget()
    {
        var finesse = ExecuteShot(0.85f, KickIntentType.CurledShot, 3.2f);

        Assert.True(finesse.MaxOffLine > 0.8f,
            $"Finesse only deviated {finesse.MaxOffLine:F2} m from its initial line");
        Assert.True(finesse.CrossingZ < 3.2f, "Finesse did not bend back toward the goal");
        Assert.InRange(finesse.CrossingZ, -3.66f, 3.66f);
        Assert.InRange(finesse.CrossingY, 0.4f, 2.44f);
    }

    /// <summary>
    /// A chip is a lofted shot: it rises over the keeper and drops under the bar at the goal line.
    /// </summary>
    [Fact]
    public void F16_ChipShot_IsLoftedAndDropsUnderTheBar()
    {
        var chip = ExecuteShot(0.85f, KickIntentType.ChipShot, 0f);

        Assert.True(chip.MaxY > 2.0f, $"Chip only reached {chip.MaxY:F2} m");
        Assert.InRange(chip.CrossingY, 0.2f, 2.44f);
        Assert.InRange(chip.CrossingZ, -3.66f, 3.66f);
    }

    /// <summary>
    /// The shot modifiers resolve in the documented order: chip (RB/V) &gt; finesse (LT/Q) &gt;
    /// trivela (sprint/R2) &gt; plain shot.
    /// </summary>
    [Fact]
    public void F16_ShotModifiers_ResolveChipFinesseAndTrivela()
    {
        KickIntentType Resolve(bool chip, bool high, bool sprint)
        {
            var (human, _, _, observation) = ShootingScenario();
            human.ChipShotHeld = chip;
            human.HighModifierHeld = high;
            human.SprintHeld = sprint;
            human.ReleaseChargedKick(KickIntentType.Shot, 0.6f);
            return human.Decide(observation, Dt).KickType;
        }

        Assert.Equal(KickIntentType.ChipShot, Resolve(chip: true, high: true, sprint: true));
        Assert.Equal(KickIntentType.CurledShot, Resolve(chip: false, high: true, sprint: true));
        Assert.Equal(KickIntentType.TrivelaShot, Resolve(chip: false, high: false, sprint: true));
        Assert.Equal(KickIntentType.Shot, Resolve(chip: false, high: false, sprint: false));
    }

    /// <summary>
    /// Shooting across the body to the weak-foot side is an outside-foot/weak-foot contact and is
    /// flagged as less accurate; the strong-foot side is a clean strike.
    /// </summary>
    [Fact]
    public void F16_FootStrike_SideOfTheBodyDecidesTheContact()
    {
        var strong = ExecuteShot(0.85f, KickIntentType.Shot, 12f);
        var across = ExecuteShot(0.85f, KickIntentType.Shot, -12f);

        Assert.Contains("StrongFoot", strong.Diagnostic);
        Assert.Contains("OffFoot", across.Diagnostic);

        // The same geometry mirrored for a left-footed player.
        var leftStrong = ExecuteShot(0.85f, KickIntentType.Shot, -12f, foot: Footedness.Left);
        var leftAcross = ExecuteShot(0.85f, KickIntentType.Shot, 12f, foot: Footedness.Left);
        Assert.Contains("StrongFoot", leftStrong.Diagnostic);
        Assert.Contains("OffFoot", leftAcross.Diagnostic);
    }

    /// <summary>
    /// Audit F16: four kick buttons shared one charge. Pressing a second one reset the intent and the
    /// timer every frame; releasing either fired with the other's charge.
    /// </summary>
    [Fact]
    public void F16_SecondKickButton_DoesNotStealTheCharge()
    {
        var human = new HumanInputController(9);

        human.UpdateButtonCharge(KickIntentType.GroundPass, 0.20f);
        float charged = human.ChargedPower01;
        Assert.Equal(KickIntentType.GroundPass, human.ChargingIntent);

        human.UpdateButtonCharge(KickIntentType.Shot, 0.20f);

        Assert.Equal(KickIntentType.GroundPass, human.ChargingIntent);
        Assert.Equal(charged, human.ChargedPower01, 3);

        human.UpdateButtonCharge(KickIntentType.GroundPass, 0.20f);
        Assert.True(human.ChargedPower01 > charged, "The active charge stopped growing");

        // Releasing the other button must not fire.
        human.ReleaseActiveCharge(KickIntentType.Shot, 0.9f);
        Assert.Equal(KickIntentType.GroundPass, human.ChargingIntent);

        human.ReleaseActiveCharge(KickIntentType.GroundPass, 0.9f);
        Assert.Equal(KickIntentType.None, human.ChargingIntent);
    }

    /// <summary>
    /// Audit F04: the controller must actually play the assigned job — offering the job target and a
    /// cover-shadow alternative, not just the formation anchor.
    /// </summary>
    [Fact]
    public void F04_ControllerOffersTheAssignedJobTargetAndAnAlternative()
    {
        var pitch = new PitchDimensions();
        var (players, ball) = JobScenario();
        var self = players.Single(p => p.Id == 10);
        var job = new TeamJobAssignment(
            TacticalJob.NearOutlet, new Vector3(-5f, 0f, 3f), null, "Offer a short outlet");

        FootballerObservation observation = Observe(self, players, ball, HomePossession(),
            isDesignatedBallResponder: false, job: job);

        var ai = new TacticalUtilityAiController(pitch);
        for (int i = 0; i < 12; i++) ai.Decide(observation, Dt);

        UtilityCandidate main = ai.LastCandidates.Single(c => c.ActionName == "Job:NearOutlet");
        Assert.Contains(ai.LastCandidates, c => c.ActionName == "Job:NearOutletAdj");

        Vector2 toAnchor = new Vector2(job.Anchor.X - self.Position.X, job.Anchor.Z - self.Position.Z);
        float alignment = Vector2.Dot(
            Vector2.Normalize(main.Command.DesiredMoveDirectionXZ),
            Vector2.Normalize(toAnchor));
        Assert.True(alignment > 0.9f, $"Job movement points {alignment:F2} away from the job target");
    }

    // =============================================================================================
    // F17 — statistics and input buffers follow authoritative execution, not proposed commands
    // =============================================================================================

    /// <summary>A 5-a-side match with a locked human striker (#5), 10 minutes per half.</summary>
    private static MatchSimulationEngine HumanMatch(ulong seed = 424242UL)
    {
        return new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = seed,
            PlayersPerTeam = 5,
            HalfDurationSeconds = 600f,
            DesignatedHumanPlayerId = 5
        });
    }

    /// <summary>Parks every player except <paramref name="keep"/> well away from the contested ball.</summary>
    private static void ParkEveryoneExcept(MatchSimulationEngine sim, params int[] keep)
    {
        int slot = 0;
        foreach (var p in sim.Players)
        {
            if (keep.Contains(p.Id)) continue;
            p.Position = new Vector3(slot % 2 == 0 ? -44f : 44f, 0f, -30f + slot * 1.1f);
            p.Velocity = Vector3.Zero;
            slot++;
        }
    }

    private static IFootballerController IdleController(MatchSimulationEngine sim) =>
        new RecordedCommandController(ControllerSourceType.Scripted, new Dictionary<int, FootballerCommand>(),
            () => sim.CurrentTick);

    /// <summary>One live execution tick for a player with an explicit kick command, on a still ball.</summary>
    private static FootballerExecutionSystem ExecuteKickCommand(
        FootballerState player,
        BallState ball,
        KickIntentType intent,
        float distance = 0.3f,
        int? intendedReceiverId = null,
        int tick = 1)
    {
        player.Position = new Vector3(0f, 0f, 0f);
        player.FacingXZ = new Vector2(1f, 0f);
        // Each call is an independent attempt from a player free to play the ball.
        player.KickRecoveryTimer = 0f;
        player.TackleRecoveryTimer = 0f;
        player.DribbleTouchTimer = 0f;
        ball.Position = new Vector3(distance, 0.11f, 0f);
        ball.Velocity = Vector3.Zero;
        ball.LastTouchedByTeam = player.Team;
        ball.LastTouchedByPlayerId = player.Id;

        Vector2 aim = new(1f, 0f);
        player.LastCommand = new FootballerCommand(
            aim, aim, false, false, false, false, false, Vector3.Zero,
            intent, new Vector3(aim.X, 0f, aim.Y), 0.6f, 0f)
        {
            IntendedReceiverId = intendedReceiverId
        };

        var execution = new FootballerExecutionSystem(new PitchDimensions());
        execution.UpdateLocomotionAndContacts(new[] { player }, ball, new SeededRandom(7), Dt, tick: tick);
        return execution;
    }

    /// <summary>
    /// Audit F17: the pass count used a hand-written intent list that omitted ThroughPass and
    /// LobbedThroughPass entirely. Every pass type must be one attempt, every shot one shot, and a
    /// header/clearance neither.
    /// </summary>
    [Fact]
    public void F17_AllPassTypesAndShots_AreCountedExactlyOnce()
    {
        var analytics = new MatchAnalyticsSummary();
        var stats = new MatchStatisticsTracker(analytics);
        int tick = 0;

        foreach (KickIntentType passType in new[]
                 {
                     KickIntentType.GroundPass, KickIntentType.LoftedPass, KickIntentType.ThroughPass,
                     KickIntentType.LobbedThroughPass, KickIntentType.DrivenPass, KickIntentType.Cross
                 })
        {
            var passer = Player(9, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
            var execution = ExecuteKickCommand(passer, new BallState(), passType, tick: ++tick);

            Assert.True(execution.ContactEvents.Count == 1,
                $"{passType} produced {execution.ContactEvents.Count} contacts");
            Assert.Equal(BallContactKind.Kick, execution.ContactEvents[0].Kind);
            Assert.Equal(passType, execution.ContactEvents[0].Intent);

            stats.ApplyContacts(execution.ContactEvents, tick);
        }

        Assert.Equal(6, analytics.HomePassesAttempted);
        Assert.Equal(0, analytics.HomeShots);

        foreach (KickIntentType shotType in new[]
                 {
                     KickIntentType.Shot, KickIntentType.CurledShot, KickIntentType.ChipShot,
                     KickIntentType.TrivelaShot
                 })
        {
            var shooter = Player(10, TeamId.Home, TacticalRole.Striker, Vector3.Zero);
            var execution = ExecuteKickCommand(shooter, new BallState(), shotType, tick: ++tick);
            stats.ApplyContacts(execution.ContactEvents, tick);
        }

        Assert.Equal(4, analytics.HomeShots);
        Assert.Equal(6, analytics.HomePassesAttempted);

        foreach (KickIntentType other in new[] { KickIntentType.Header, KickIntentType.Clearance })
        {
            var player = Player(11, TeamId.Home, TacticalRole.CenterBack, Vector3.Zero);
            var execution = ExecuteKickCommand(player, new BallState(), other, tick: ++tick);
            stats.ApplyContacts(execution.ContactEvents, tick);
        }

        Assert.Equal(6, analytics.HomePassesAttempted);
        Assert.Equal(4, analytics.HomeShots);
    }

    /// <summary>
    /// Audit F17: an attempt exists only when execution played the ball. A kick from outside the
    /// physical reach is refused, publishes no contact, and must not be counted.
    /// </summary>
    [Fact]
    public void F17_RefusedContact_IsNotCountedAsAnAttempt()
    {
        var analytics = new MatchAnalyticsSummary();
        var stats = new MatchStatisticsTracker(analytics);

        var passer = Player(9, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        var execution = ExecuteKickCommand(passer, new BallState(), KickIntentType.GroundPass, distance: 2.0f);

        Assert.Empty(execution.ContactEvents);
        BallContactAttempt attempt = Assert.Single(execution.ContactAttempts);
        Assert.False(attempt.Executed);
        Assert.Equal(ContactRejectionReason.OutOfReach, attempt.Reason);
        Assert.Equal(KickIntentType.GroundPass, attempt.Intent);

        stats.ApplyContacts(execution.ContactEvents, 1);
        Assert.Equal(0, analytics.HomePassesAttempted);
        Assert.Equal(0, analytics.HomeShots);
    }

    /// <summary>
    /// Audit F17: the human press used its own 1.125 m strike range while execution accepted 0.825 m
    /// for an opponent's loose ball — so a press could be recorded as an attempt in a place where
    /// the ball could provably not be played. The controller now uses the execution reach.
    /// </summary>
    [Fact]
    public void F17_OutOfReachPress_IsNotAttempted_ThenFiresInsideTheRealReach()
    {
        var sim = HumanMatch();
        var human = sim.LockedHumanController!;
        var humanPlayer = sim.Players.Single(p => p.Id == 5);
        ParkEveryoneExcept(sim, 5);

        humanPlayer.Position = new Vector3(0f, 0f, 0f);
        sim.Ball.Position = new Vector3(1.0f, 0.11f, 0f);
        sim.Ball.PreviousPosition = sim.Ball.Position;
        sim.Ball.Velocity = Vector3.Zero;
        // An opponent played it last, so the reaching tolerance is the 1.10x base reach (0.825 m).
        sim.Ball.LastTouchedByTeam = TeamId.Away;
        sim.Ball.LastTouchedByPlayerId = 10;

        human.ReleaseChargedKick(KickIntentType.GroundPass, 0.5f);
        sim.StepTick();

        Assert.Equal(KickIntentType.None, humanPlayer.LastCommand.KickType);
        Assert.Empty(sim.LastContactAttempts);
        Assert.Equal(0, sim.Analytics.HomePassesAttempted);
        Assert.True(human.HasBufferedKickRequest, "The press should stay queued while the ball is unreachable");

        // Move the ball inside the real reach: the queued press now executes and is counted once.
        sim.Ball.Position = new Vector3(0.7f, 0.11f, 0f);
        sim.Ball.PreviousPosition = sim.Ball.Position;
        sim.Ball.Velocity = Vector3.Zero;
        sim.StepTick();

        Assert.Equal(KickIntentType.GroundPass, humanPlayer.LastCommand.KickType);
        Assert.Equal(1, sim.Analytics.HomePassesAttempted);
        BallContactAttempt executed = Assert.Single(sim.LastContactAttempts);
        Assert.True(executed.Executed);
        Assert.False(human.HasBufferedKickRequest, "The press must be consumed by the accepted kick");
    }

    /// <summary>
    /// Audit F17: a kick consumed by the controller could be lost to the tick's single-contact
    /// arbitration with no trace. It must not count as an attempt, and the queued press must be
    /// retried on the next tick.
    /// </summary>
    [Fact]
    public void F17_LostArbitration_IsNotAPassAttempt_AndThePressIsRetried()
    {
        var sim = HumanMatch();
        var human = sim.LockedHumanController!;
        var humanPlayer = sim.Players.Single(p => p.Id == 5);
        var rival = sim.Players.Single(p => p.Id == 10);
        ParkEveryoneExcept(sim, 5, 10);

        // The human is 0.9 m away and last played the ball (1.24 m tolerance); the rival is on top of
        // it (0.15 m), so it wins the contact for this tick.
        humanPlayer.Position = new Vector3(0.9f, 0f, 0f);
        humanPlayer.FacingXZ = new Vector2(-1f, 0f);
        rival.Position = new Vector3(-0.15f, 0f, 0f);
        rival.FacingXZ = new Vector2(1f, 0f);
        sim.OverrideController(10, IdleController(sim));

        sim.Ball.Position = new Vector3(0f, 0.11f, 0f);
        sim.Ball.PreviousPosition = sim.Ball.Position;
        sim.Ball.Velocity = Vector3.Zero;
        sim.Ball.LastTouchedByTeam = TeamId.Home;
        sim.Ball.LastTouchedByPlayerId = 5;

        human.ReleaseChargedKick(KickIntentType.GroundPass, 0.5f);
        sim.StepTick();

        Assert.Equal(KickIntentType.GroundPass, humanPlayer.LastCommand.KickType);
        BallContactAttempt refused = Assert.Single(sim.LastContactAttempts, a => a.PlayerId == 5);
        Assert.False(refused.Executed);
        Assert.Equal(ContactRejectionReason.LostArbitration, refused.Reason);
        Assert.Equal(0, sim.Analytics.HomePassesAttempted);
        Assert.True(human.HasBufferedKickRequest, "A press lost to arbitration must be retried");

        // The rival's own dribble cooldown blocks it next tick, so the retried press goes through.
        sim.OverrideController(10, IdleController(sim));
        sim.StepTick();

        Assert.Equal(1, sim.Analytics.HomePassesAttempted);
        Assert.Contains(sim.LastContactAttempts, a => a.PlayerId == 5 && a.Executed);
    }

    /// <summary>
    /// Audit F17: a ball above the reachable height is refused by execution (a header only reaches
    /// 2.27 m). It must not be counted as a shot or a pass, and the press must not be dropped.
    /// </summary>
    [Fact]
    public void F17_AerialBall_IsNotCountedAsAnyAttempt()
    {
        var sim = HumanMatch();
        var human = sim.LockedHumanController!;
        var humanPlayer = sim.Players.Single(p => p.Id == 5);
        ParkEveryoneExcept(sim, 5);

        humanPlayer.Position = new Vector3(0f, 0f, 0f);
        sim.Ball.Position = new Vector3(0.5f, 3.0f, 0f);
        sim.Ball.PreviousPosition = sim.Ball.Position;
        sim.Ball.Velocity = Vector3.Zero;
        sim.Ball.LastTouchedByTeam = TeamId.Home;
        sim.Ball.LastTouchedByPlayerId = 5;

        human.ReleaseChargedKick(KickIntentType.Shot, 0.8f);
        sim.StepTick();

        Assert.Equal(KickIntentType.Header, humanPlayer.LastCommand.KickType);
        BallContactAttempt refused = Assert.Single(sim.LastContactAttempts, a => a.PlayerId == 5);
        Assert.Equal(ContactRejectionReason.BallTooHigh, refused.Reason);
        Assert.Equal(0, sim.Analytics.HomeShots);
        Assert.Equal(0, sim.Analytics.HomePassesAttempted);
        Assert.True(human.HasBufferedKickRequest);
    }

    /// <summary>
    /// Audit F17: a press made while the player is still in kick recovery must not fire early and
    /// must not be lost — it fires once recovery ends.
    /// </summary>
    [Fact]
    public void F17_BufferedKickDuringRecovery_FiresWhenThePlayerCanPlayAgain()
    {
        var sim = HumanMatch();
        var human = sim.LockedHumanController!;
        var humanPlayer = sim.Players.Single(p => p.Id == 5);
        ParkEveryoneExcept(sim, 5);

        humanPlayer.Position = Vector3.Zero;
        humanPlayer.KickRecoveryTimer = 0.10f;
        sim.Ball.Position = new Vector3(0.4f, 0.11f, 0f);
        sim.Ball.PreviousPosition = sim.Ball.Position;
        sim.Ball.Velocity = Vector3.Zero;
        sim.Ball.LastTouchedByTeam = TeamId.Home;
        sim.Ball.LastTouchedByPlayerId = 5;

        human.ReleaseChargedKick(KickIntentType.GroundPass, 0.5f);
        sim.StepTick();

        Assert.Equal(KickIntentType.None, humanPlayer.LastCommand.KickType);
        Assert.Empty(sim.LastContactAttempts);
        Assert.Equal(0, sim.Analytics.HomePassesAttempted);

        int firedAt = 0;
        for (int i = 0; i < 20 && firedAt == 0; i++)
        {
            sim.StepTick();
            if (sim.Analytics.HomePassesAttempted > 0) firedAt = sim.CurrentTick;
        }

        Assert.True(firedAt > 0, "The press queued during recovery never fired");
        Assert.Equal(1, sim.Analytics.HomePassesAttempted);
    }

    /// <summary>
    /// Audit F17: attempts and completions are separate. A pass completes only when a team-mate (not
    /// the passer) controls the ball next; an opponent control or a second kick does not complete it.
    /// </summary>
    [Fact]
    public void F17_PassCompletion_RequiresATeamMateToControlTheBallNext()
    {
        var analytics = new MatchAnalyticsSummary();
        var stats = new MatchStatisticsTracker(analytics);

        var passer = Player(9, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        var ball = new BallState();
        var firstPass = ExecuteKickCommand(passer, ball, KickIntentType.GroundPass,
            intendedReceiverId: 8, tick: 1);
        stats.ApplyContacts(firstPass.ContactEvents, 1);

        Assert.Equal(1, analytics.HomePassesAttempted);
        Assert.Equal(0, analytics.HomePassesCompleted);
        Assert.True(stats.HasPassInFlight);

        // Re-touching his own pass is not a completion.
        stats.ApplyContacts(
            new[] { new BallContactEvent(2, BallContactKind.Collection, 9, TeamId.Home, ball.Position) }, 2);
        Assert.Equal(0, analytics.HomePassesCompleted);
        Assert.False(stats.HasPassInFlight);

        // A team-mate collecting it is.
        var secondPass = ExecuteKickCommand(passer, ball, KickIntentType.ThroughPass,
            intendedReceiverId: 8, tick: 3);
        stats.ApplyContacts(secondPass.ContactEvents, 3);
        stats.ApplyContacts(
            new[] { new BallContactEvent(4, BallContactKind.Collection, 8, TeamId.Home, ball.Position) }, 4);
        Assert.Equal(2, analytics.HomePassesAttempted);
        Assert.Equal(1, analytics.HomePassesCompleted);

        // An opponent intercepting it leaves the pass incomplete.
        var thirdPass = ExecuteKickCommand(passer, ball, KickIntentType.LobbedThroughPass,
            intendedReceiverId: 8, tick: 5);
        stats.ApplyContacts(thirdPass.ContactEvents, 5);
        stats.ApplyContacts(
            new[] { new BallContactEvent(6, BallContactKind.Collection, 12, TeamId.Away, ball.Position) }, 6);
        Assert.Equal(3, analytics.HomePassesAttempted);
        Assert.Equal(1, analytics.HomePassesCompleted);

        // A whistle (restart) ends the flight: an untouched ball controlled much later is not a pass.
        var fourthPass = ExecuteKickCommand(passer, ball, KickIntentType.GroundPass,
            intendedReceiverId: 8, tick: 7);
        stats.ApplyContacts(fourthPass.ContactEvents, 7);
        stats.ResetPassPhase();
        stats.ApplyContacts(
            new[] { new BallContactEvent(20, BallContactKind.Collection, 8, TeamId.Home, ball.Position) }, 20);
        Assert.Equal(1, analytics.HomePassesCompleted);

        // A team-mate playing the ball straight out of the air (a one-touch touch) also completes it.
        var fifthPass = ExecuteKickCommand(passer, ball, KickIntentType.GroundPass,
            intendedReceiverId: 8, tick: 30);
        stats.ApplyContacts(fifthPass.ContactEvents, 30);
        stats.ApplyContacts(
            new[]
            {
                new BallContactEvent(31, BallContactKind.Kick, 8, TeamId.Home, ball.Position)
                {
                    Intent = KickIntentType.Clearance
                }
            }, 31);
        Assert.Equal(5, analytics.HomePassesAttempted);
        Assert.Equal(2, analytics.HomePassesCompleted);

        // A pass that is never controlled inside the completion window expires incomplete.
        var sixthPass = ExecuteKickCommand(passer, ball, KickIntentType.GroundPass,
            intendedReceiverId: 8, tick: 40);
        stats.ApplyContacts(sixthPass.ContactEvents, 40);
        Assert.True(stats.HasPassInFlight);
        Assert.Equal(6, analytics.HomePassesAttempted);
        int expiryTick = 40 + (int)(MatchStatisticsTracker.MaxPassCompletionSeconds / Dt) + 2;
        stats.ApplyContacts(
            new[] { new BallContactEvent(expiryTick, BallContactKind.Collection, 8, TeamId.Home, ball.Position) },
            expiryTick);
        Assert.Equal(2, analytics.HomePassesCompleted);
    }

    /// <summary>
    /// Audit F17: possession analytics counted last touch, so a keeper's parry silently gave the
    /// defending team possession for the rest of the phase. Possession must follow the ownership
    /// model (a parry is a deflection) and controlled possession must be tracked separately.
    /// </summary>
    [Fact]
    public void F17_PossessionAnalytics_FollowTheOwnershipModelNotLastTouch()
    {
        var sim = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = 55UL,
            PlayersPerTeam = 5,
            HalfDurationSeconds = 600f
        });
        ParkEveryoneExcept(sim, 5, 6);

        var kicker = sim.Players.Single(p => p.Id == 5);
        var awayKeeper = sim.Players.Single(p => p.Id == 6);

        // Tick 1: Home #5 deliberately plays the ball — a pass in flight, owned by Home.
        kicker.Position = new Vector3(30f, 0f, 0f);
        kicker.FacingXZ = new Vector2(1f, 0f);
        sim.Ball.Position = new Vector3(30.3f, 0.11f, 0f);
        sim.Ball.PreviousPosition = sim.Ball.Position;
        sim.Ball.Velocity = Vector3.Zero;
        sim.Ball.LastTouchedByTeam = TeamId.Home;
        sim.Ball.LastTouchedByPlayerId = 5;

        sim.OverrideController(5, new RecordedCommandController(
            ControllerSourceType.Scripted,
            new Dictionary<int, FootballerCommand>
            {
                [1] = new FootballerCommand(
                    new Vector2(1f, 0f), new Vector2(1f, 0f), false, false, false, false, false,
                    Vector3.Zero, KickIntentType.GroundPass, new Vector3(1f, 0f, 0f), 0.6f, 0f)
            },
            () => sim.CurrentTick));
        sim.OverrideController(6, IdleController(sim));

        sim.StepTick();
        Assert.Equal(TeamId.Home, sim.Ball.LastTouchedByTeam);
        Assert.Equal(1, sim.Analytics.HomePassesAttempted);
        Assert.Equal(1, sim.Analytics.HomePossessionTicks);
        Assert.Equal(0, sim.Analytics.AwayPossessionTicks);

        // Tick 2: the ball arrives at the away keeper, who can only parry it. Last touch becomes Away,
        // but a deflection is not possession — the attacking team still owns the ball.
        awayKeeper.Position = new Vector3(50.4f, 0f, 0f);
        awayKeeper.FacingXZ = new Vector2(-1f, 0f);
        sim.Ball.Position = new Vector3(50.0f, 0.11f, 0f);
        sim.Ball.PreviousPosition = sim.Ball.Position;
        sim.Ball.Velocity = new Vector3(16f, 0f, 0f);
        sim.Ball.LastTouchedByTeam = TeamId.Home;
        sim.Ball.LastTouchedByPlayerId = 5;

        sim.StepTick();

        Assert.Equal(TeamId.Away, sim.Ball.LastTouchedByTeam);
        Assert.Equal("GK:ReflexParry", awayKeeper.LastExecutionDiagnostic);
        Assert.Equal(2, sim.Analytics.HomePossessionTicks);
        Assert.Equal(0, sim.Analytics.AwayPossessionTicks);
        Assert.Equal(0, sim.Analytics.HomeControlledPossessionTicks);

        // Tick 3: a Home player takes control. Controlled possession is now Home's, separately.
        var collector = sim.Players.Single(p => p.Id == 4);
        ParkEveryoneExcept(sim, 4);
        collector.Position = new Vector3(20f, 0f, 0f);
        collector.FacingXZ = new Vector2(1f, 0f);
        sim.Ball.Position = new Vector3(20.3f, 0.11f, 0f);
        sim.Ball.PreviousPosition = sim.Ball.Position;
        sim.Ball.Velocity = Vector3.Zero;

        sim.StepTick();

        Assert.Equal(1, sim.Analytics.HomeControlledPossessionTicks);
        Assert.Equal(3, sim.Analytics.HomePossessionTicks);
        Assert.Equal(0, sim.Analytics.AwayPossessionTicks);
    }

    // =============================================================================================
    // F18 — advertised behaviour fields must do something, and A/B one parameter at a time
    // =============================================================================================

    private static PlayerTendencies Tendencies(
        float risk = PlayerTendencyBaselines.RiskTolerance,
        float direct = PlayerTendencyBaselines.DirectPassPreference,
        float shooting = PlayerTendencyBaselines.ShootingAppetite,
        float dribble = PlayerTendencyBaselines.DribblePreference,
        float discipline = PlayerTendencyBaselines.PositionalDiscipline,
        float pressing = PlayerTendencyBaselines.PressingAggression) => new()
    {
        RiskTolerance01 = risk,
        DirectPassPreference01 = direct,
        ShootingAppetite01 = shooting,
        DribblePreference01 = dribble,
        PositionalDiscipline01 = discipline,
        PressingAggression01 = pressing
    };

    private static FootballerState WithTendencies(FootballerState player, PlayerTendencies tendencies) => new()
    {
        Id = player.Id,
        Team = player.Team,
        ShirtNumber = player.ShirtNumber,
        Role = player.Role,
        Attributes = player.Attributes,
        Tendencies = tendencies,
        Position = player.Position,
        FacingXZ = player.FacingXZ,
        TacticalAnchor = player.TacticalAnchor
    };

    private static FootballerState WithAttributes(FootballerState player, PlayerAttributes attributes) => new()
    {
        Id = player.Id,
        Team = player.Team,
        ShirtNumber = player.ShirtNumber,
        Role = player.Role,
        Attributes = attributes,
        Tendencies = player.Tendencies,
        Position = player.Position,
        FacingXZ = player.FacingXZ,
        TacticalAnchor = player.TacticalAnchor
    };

    /// <summary>The controller's on-ball candidate set after it has settled on the ball.</summary>
    private static IReadOnlyList<UtilityCandidate> OnBallCandidates(
        FootballerState self,
        IReadOnlyList<FootballerState> players,
        BallState ball,
        TeamTacticalProfile? profile = null,
        int settleTicks = 30)
    {
        var controller = new TacticalUtilityAiController(new PitchDimensions());
        FootballerObservation observation = Observe(self, players, ball, profile: profile);
        for (int i = 0; i < settleTicks; i++)
            controller.Decide(observation, Dt);
        return controller.LastCandidates;
    }

    /// <summary>
    /// Audit F18: <c>ShieldBall</c> was written by the AI (opponent within 2 m) and the human (shield
    /// button) but never read by execution, so shielding did nothing. A shielded ball must survive a
    /// contest that would otherwise be nicked away.
    /// </summary>
    [Fact]
    public void F18_ShieldingUnderPressure_KeepsTheBallAgainstAnOpponent()
    {
        int ContestWinner(bool shield)
        {
            var holder = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(0.7f, 0f, 0f));
            holder.FacingXZ = new Vector2(-1f, 0f);
            holder.LastCommand = FootballerCommand.Idle with { ShieldBall = shield };

            var opponent = Player(10, TeamId.Away, TacticalRole.CenterBack, new Vector3(-0.15f, 0f, 0f));
            opponent.FacingXZ = new Vector2(1f, 0f);
            opponent.LastCommand = FootballerCommand.Idle;

            var ball = new BallState
            {
                Position = new Vector3(0f, 0.11f, 0f),
                LastTouchedByTeam = TeamId.Home,
                LastTouchedByPlayerId = 9
            };

            var execution = new FootballerExecutionSystem(new PitchDimensions());
            execution.UpdateLocomotionAndContacts(
                new[] { holder, opponent }, ball, new SeededRandom(11), Dt, tick: 1);

            BallContactEvent contact = Assert.Single(execution.ContactEvents);
            return contact.PlayerId;
        }

        Assert.Equal(10, ContestWinner(shield: false));
        Assert.Equal(9, ContestWinner(shield: true));
    }

    /// <summary>
    /// Audit F18: shielding is a stance, not a free bonus — the player moves slower while using their
    /// body, so the field is a genuine trade-off rather than a pure upgrade.
    /// </summary>
    [Fact]
    public void F18_Shielding_SlowsThePlayerDown()
    {
        float Speed(bool shield)
        {
            var player = Player(9, TeamId.Home, TacticalRole.Striker, Vector3.Zero);
            player.LastCommand = new FootballerCommand(
                new Vector2(1f, 0f), new Vector2(1f, 0f), Sprint: false, ShieldBall: shield,
                RequestTackle: false, RequestJumpHeader: false, RequestKeeperDive: false,
                KeeperDiveDirection: Vector3.Zero, KickType: KickIntentType.None,
                KickAimDirectionXZ: Vector3.UnitX, KickPower01: 0f, RequestedCurveSpin: 0f);

            var ball = new BallState { Position = new Vector3(30f, 0.11f, 0f) };
            var execution = new FootballerExecutionSystem(new PitchDimensions());
            // Long enough to reach the target speed rather than one acceleration step.
            for (int i = 0; i < 90; i++)
                execution.UpdateLocomotionAndContacts(new[] { player }, ball, new SeededRandom(1), Dt, tick: i);

            return new Vector2(player.Velocity.X, player.Velocity.Z).Length();
        }

        Assert.True(Speed(shield: true) < Speed(shield: false) * 0.85f,
            $"Shielding speed {Speed(true):F2} m/s is not clearly below {Speed(false):F2} m/s");
    }

    /// <summary>
    /// Audit F18: shooting appetite must move the value of taking a shot (same geometry, one field).
    /// </summary>
    [Fact]
    public void F18_ShootingAppetite_MovesTheShotUtility()
    {
        float ShotUtility(float appetite)
        {
            var self = WithTendencies(
                Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(35f, 0f, 0f)),
                Tendencies(shooting: appetite));
            var ball = new BallState
            {
                Position = new Vector3(35.3f, 0.11f, 0f),
                LastTouchedByTeam = TeamId.Home,
                LastTouchedByPlayerId = 9
            };

            var candidates = OnBallCandidates(self, new[] { self }, ball, settleTicks: 1);
            return candidates.Single(c => c.ActionName == "ShootAtGoal").UtilityScore;
        }

        Assert.True(ShotUtility(0.95f) > ShotUtility(0.20f) + 0.1f,
            "Shooting appetite had no measurable effect on the shot utility");
    }

    /// <summary>
    /// Audit F18: risk tolerance (individual and team) must make a tight-lane pass more attractive,
    /// and direct-pass preference must make a forward pass more attractive. Defaults are neutral.
    /// </summary>
    [Fact]
    public void F18_RiskToleranceAndDirectPreference_MoveThePassUtility()
    {
        var riskyProfile = new TeamTacticalProfile { RiskTolerance01 = 0.90f };
        var safeProfile = new TeamTacticalProfile { RiskTolerance01 = 0.10f };

        float PassUtility(PlayerTendencies tendencies, TeamTacticalProfile? profile = null)
        {
            var passer = WithTendencies(
                Player(9, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero), tendencies);
            var mate = Player(8, TeamId.Home, TacticalRole.Striker, new Vector3(15f, 0f, 3f));
            var blocker = Player(12, TeamId.Away, TacticalRole.CenterBack, new Vector3(7f, 0f, -0.2f));
            var ball = new BallState
            {
                Position = new Vector3(0.3f, 0.11f, 0f),
                LastTouchedByTeam = TeamId.Home,
                LastTouchedByPlayerId = 9
            };

            var candidates = OnBallCandidates(passer, new[] { passer, mate, blocker }, ball, profile);
            return candidates.Single(c => c.ActionName.StartsWith("Pass(", StringComparison.Ordinal)).UtilityScore;
        }

        float risky = PassUtility(Tendencies(risk: 0.95f));
        float safe = PassUtility(Tendencies(risk: 0.15f));
        Assert.True(risky > safe, $"Player risk tolerance: {risky:F3} vs {safe:F3}");

        float direct = PassUtility(Tendencies(direct: 0.95f));
        float patient = PassUtility(Tendencies(direct: 0.15f));
        Assert.True(direct > patient, $"Direct pass preference: {direct:F3} vs {patient:F3}");

        float teamRisky = PassUtility(Tendencies(), riskyProfile);
        float teamSafe = PassUtility(Tendencies(), safeProfile);
        Assert.True(teamRisky > teamSafe, $"Team risk tolerance: {teamRisky:F3} vs {teamSafe:F3}");
    }

    /// <summary>
    /// Audit F18: dribble preference must move the value of carrying the ball.
    /// </summary>
    [Fact]
    public void F18_DribblePreference_MovesTheCarryUtility()
    {
        float CarryUtility(float preference)
        {
            var self = WithTendencies(
                Player(9, TeamId.Home, TacticalRole.Winger, new Vector3(10f, 0f, 10f)), Tendencies(dribble: preference));
            var ball = new BallState
            {
                Position = new Vector3(10.3f, 0.11f, 10f),
                LastTouchedByTeam = TeamId.Home,
                LastTouchedByPlayerId = 9
            };

            var candidates = OnBallCandidates(self, new[] { self }, ball, settleTicks: 1);
            return candidates.Single(c => c.ActionName == "DriveWithBall").UtilityScore;
        }

        Assert.True(CarryUtility(0.95f) > CarryUtility(0.10f) + 0.2f,
            "Dribble preference had no measurable effect on the carry utility");
    }

    /// <summary>
    /// Audit F18: positional discipline (player) and positional freedom (team) bound how far a player
    /// strays from their formation anchor instead of being inert profile data.
    /// </summary>
    [Fact]
    public void F18_PositionalDisciplineAndFreedom_BoundTheSupportDrift()
    {
        float Drift(float discipline, float freedom)
        {
            var self = WithTendencies(
                Player(6, TeamId.Home, TacticalRole.CenterBack, new Vector3(-10f, 0f, 0f)),
                Tendencies(discipline: discipline));
            self.TacticalAnchor = new Vector3(-10f, 0f, 0f);

            // A wall of team-mates to the east pushes the support target far from the anchor.
            var mates = new List<FootballerState> { self };
            for (int i = 0; i < 4; i++)
                mates.Add(Player(7 + i, TeamId.Home, TacticalRole.CentralMidfielder,
                    new Vector3(-10f + 0.8f + i * 1.6f, 0f, 0f)));

            var ball = new BallState
            {
                Position = new Vector3(-8.2f, 0.11f, 0f),
                LastTouchedByTeam = TeamId.Home,
                LastTouchedByPlayerId = 8
            };

            var profile = new TeamTacticalProfile { PositionalFreedom01 = freedom };
            FootballerObservation observation = Observe(
                self, mates, ball,
                possession: new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, 8, null),
                isDesignatedBallResponder: false,
                profile: profile);

            var controller = new TacticalUtilityAiController(new PitchDimensions());
            controller.Decide(observation, Dt);

            UtilityCandidate shape = controller.LastCandidates
                .Single(c => c.ActionName is "OnsideSupportLane" or "HoldDefensiveZone");
            Assert.NotNull(shape.TargetXZ);

            Vector3 anchor = self.TacticalAnchor;
            return Vector2.Distance(
                new Vector2(anchor.X, anchor.Z),
                new Vector2(shape.TargetXZ!.Value.X, shape.TargetXZ.Value.Z));
        }

        float undisciplined = Drift(discipline: 0.15f, freedom: 0.55f);
        float disciplined = Drift(discipline: 0.95f, freedom: 0.55f);
        Assert.True(disciplined < undisciplined - 1f,
            $"Discipline did not bound the drift: {disciplined:F1} m vs {undisciplined:F1} m");

        float free = Drift(discipline: 0.70f, freedom: 0.95f);
        float rigid = Drift(discipline: 0.70f, freedom: 0.15f);
        Assert.True(free > rigid + 1f,
            $"Team positional freedom did not widen the drift: {free:F1} m vs {rigid:F1} m");
    }

    /// <summary>
    /// Audit F18: tempo must change how quickly a carrier releases the ball. Same geometry, only the
    /// team tempo differs.
    /// </summary>
    [Fact]
    public void F18_Tempo_ChangesThePassReleaseTime()
    {
        int TicksUntilPass(double tempo)
        {
            var self = Player(9, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
            var mate = Player(8, TeamId.Home, TacticalRole.Striker, new Vector3(15f, 0f, 3f));
            var ball = new BallState
            {
                Position = new Vector3(0.3f, 0.11f, 0f),
                LastTouchedByTeam = TeamId.Home,
                LastTouchedByPlayerId = 9
            };

            var profile = new TeamTacticalProfile { Tempo01 = (float)tempo };
            FootballerObservation observation = Observe(self, new[] { self, mate }, ball, profile: profile);
            var controller = new TacticalUtilityAiController(new PitchDimensions());

            for (int tick = 1; tick <= 60; tick++)
            {
                controller.Decide(observation, Dt);
                if (controller.LastCandidates.Any(c => c.ActionName.StartsWith("Pass(", StringComparison.Ordinal)))
                    return tick;
            }

            return int.MaxValue;
        }

        int fast = TicksUntilPass(0.90);
        int slow = TicksUntilPass(0.20);
        Assert.True(fast < slow, $"High tempo passed at tick {fast}, low tempo at {slow}");
    }

    /// <summary>
    /// Audit F18: pressing aggression must move the tackle commitment range (same distance, one field).
    /// </summary>
    [Fact]
    public void F18_PressingAggression_MovesTheTackleCommitmentRange()
    {
        string PressAction(float aggression)
        {
            var self = WithTendencies(
                Player(4, TeamId.Home, TacticalRole.CenterBack, new Vector3(0f, 0f, 0f)),
                Tendencies(pressing: aggression));
            var carrier = Player(12, TeamId.Away, TacticalRole.Striker, new Vector3(1.30f, 0f, 0f));
            var ball = new BallState
            {
                Position = new Vector3(1.30f, 0.11f, 0f),
                LastTouchedByTeam = TeamId.Away,
                LastTouchedByPlayerId = 12
            };

            FootballerObservation observation = Observe(
                self, new[] { self, carrier }, ball,
                possession: new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Away, 12, null),
                isDesignatedBallResponder: true);

            var controller = new TacticalUtilityAiController(new PitchDimensions());
            controller.Decide(observation, Dt);
            return controller.LastCandidates.Single().ActionName;
        }

        Assert.Equal("CloseDownCarrier", PressAction(0.20f));
        Assert.Equal("ExecuteTackle", PressAction(1.00f));
    }

    /// <summary>
    /// Audit F18: <c>ReactionDelaySeconds</c> scaled perception noise instead of delaying perception.
    /// A player must track steady motion accurately but only notice a *change* after their reaction
    /// delay (and a slower reactor stays wrong for longer), while execution still plays the live ball.
    /// </summary>
    [Fact]
    public void F18_ReactionDelay_TracksSteadyMotionButReactsLateToAChange()
    {
        (float SteadyError, float AfterThreeTicks, float AfterEightTicks) Response(float reactionDelay)
        {
            var self = WithAttributes(
                Player(9, TeamId.Home, TacticalRole.Striker, Vector3.Zero),
                new PlayerAttributes { ReactionDelaySeconds = reactionDelay });

            var perception = new PerceptionSystem();
            var ball = new BallState();
            var observations = new Dictionary<int, FootballerObservation>();

            // 12 m/s straight line for 20 ticks, then a sudden reversal (a deflection).
            static (Vector3 Position, Vector3 Velocity) BallAt(int tick) => tick <= 20
                ? (new Vector3(tick * 0.2f, 0.11f, 0f), new Vector3(12f, 0f, 0f))
                : (new Vector3((40 - tick) * 0.2f, 0.11f, 0f), new Vector3(-12f, 0f, 0f));

            for (int t = 0; t <= 28; t++)
            {
                (ball.Position, ball.Velocity) = BallAt(t);
                observations[t] = perception.BuildObservation(
                    self, new[] { self }, ball, TeamId.Home, MatchPhase.FirstHalf,
                    t * Dt, 1f, false, TeamTacticalProfile.PossessionControl, new SeededRandom(1));
            }

            float ErrorAt(int tick) =>
                MathF.Abs(observations[tick].PerceivedBallPosition.X - BallAt(tick).Position.X);

            return (ErrorAt(20), ErrorAt(23), ErrorAt(28));
        }

        (float fastSteady, float fastThree, float fastEight) = Response(0f);
        (float normalSteady, float normalThree, float normalEight) = Response(0.08f);
        (float slowSteady, float slowThree, float slowEight) = Response(0.20f);

        // Steady motion is tracked through the delay.
        Assert.True(fastSteady < 0.05f && normalSteady < 0.05f && slowSteady < 0.05f,
            $"Steady tracking errors: {fastSteady:F2}/{normalSteady:F2}/{slowSteady:F2} m");

        // Three ticks after the reversal the instant reactor has it; the others are still on the old line.
        Assert.True(fastThree < 0.05f, $"Zero-delay error after the change: {fastThree:F2} m");
        Assert.True(normalThree > 1f, $"0.08 s reactor saw the change instantly ({normalThree:F2} m error)");
        Assert.True(slowThree > 1f, $"0.20 s reactor saw the change instantly ({slowThree:F2} m error)");

        // The normal reactor has caught up after one delay; the slow one is still behind.
        Assert.True(normalEight < 0.05f, $"0.08 s reactor was still wrong 8 ticks later ({normalEight:F2} m)");
        Assert.True(slowEight > 2f, $"0.20 s reactor caught up too early ({slowEight:F2} m error)");
    }

    /// <summary>
    /// Audit F18: <c>ShotPowerMax</c> was advertised on every player but the shot speed was
    /// hard-coded. A weaker shooter must produce a slower ball while the height solution keeps the
    /// shot honest; the 31 m/s default is unchanged.
    /// </summary>
    [Fact]
    public void F18_ShotPowerMax_ScalesTheShotSpeed()
    {
        (float Speed, float CrossingY) Shot(float maxPower)
        {
            var shooter = WithAttributes(
                Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(36f, 0f, 0f)),
                new PlayerAttributes { ShotPowerMax = maxPower });
            shooter.FacingXZ = new Vector2(1f, 0f);

            var ball = new BallState
            {
                Position = new Vector3(36.4f, 0.11f, 0f),
                LastTouchedByTeam = TeamId.Home,
                LastTouchedByPlayerId = 9
            };
            Vector2 aim = new(1f, 0f);
            shooter.LastCommand = new FootballerCommand(
                aim, aim, Sprint: false, ShieldBall: false, RequestTackle: false,
                RequestJumpHeader: false, RequestKeeperDive: false, KeeperDiveDirection: Vector3.Zero,
                KickType: KickIntentType.Shot, KickAimDirectionXZ: new Vector3(1f, 0f, 0f),
                KickPower01: 0.85f, RequestedCurveSpin: 0f);

            new FootballerExecutionSystem(new PitchDimensions()).UpdateLocomotionAndContacts(
                new[] { shooter }, ball, new SeededRandom(5), Dt, tick: 1);

            float speed = new Vector2(ball.Velocity.X, ball.Velocity.Z).Length();

            var solver = new BallPhysicsSolver();
            float crossingY = float.NaN;
            for (int i = 0; i < 240 && float.IsNaN(crossingY); i++)
            {
                solver.Step(ball, Dt);
                if (ball.Position.X >= new PitchDimensions().HalfLength) crossingY = ball.Position.Y;
            }

            return (speed, crossingY);
        }

        (float strongSpeed, float strongY) = Shot(31f);
        (float weakSpeed, float weakY) = Shot(24f);

        Assert.True(weakSpeed < strongSpeed * 0.85f,
            $"ShotPowerMax had no effect: {weakSpeed:F1} m/s vs {strongSpeed:F1} m/s");
        Assert.InRange(strongY, 0.4f, 2.44f);
        Assert.InRange(weakY, 0.4f, 2.44f);
    }

    /// <summary>
    /// Audit F18: a kick made with the body facing away from the aim must lose accuracy. The
    /// diagnostic exposes the alignment so the A/B case is checkable, and the launch direction
    /// actually deviates further from the aim.
    /// </summary>
    [Fact]
    public void F18_KickBehindTheBody_IsLessAccurateThanOneInFront()
    {
        (int Align, float Deviation) Kick(float facingX)
        {
            var shooter = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(40f, 0f, 0f));
            shooter.FacingXZ = new Vector2(facingX, 0f);

            var ball = new BallState
            {
                Position = new Vector3(40.4f, 0.11f, 0f),
                LastTouchedByTeam = TeamId.Home,
                LastTouchedByPlayerId = 9
            };

            Vector2 aim = new(1f, 0f);
            shooter.LastCommand = new FootballerCommand(
                aim, aim, Sprint: false, ShieldBall: false, RequestTackle: false,
                RequestJumpHeader: false, RequestKeeperDive: false, KeeperDiveDirection: Vector3.Zero,
                KickType: KickIntentType.Shot, KickAimDirectionXZ: new Vector3(1f, 0f, 0f),
                KickPower01: 0.85f, RequestedCurveSpin: 0f);

            new FootballerExecutionSystem(new PitchDimensions()).UpdateLocomotionAndContacts(
                new[] { shooter }, ball, new SeededRandom(3), Dt, tick: 1);

            string diagnostic = shooter.LastExecutionDiagnostic;
            int align = int.Parse(diagnostic.Split("Align=")[1].Split('%')[0]);

            Vector2 launch = SimMath.SafeNormalize(new Vector2(ball.Velocity.X, ball.Velocity.Z), aim);
            float deviation = MathF.Acos(Math.Clamp(Vector2.Dot(launch, aim), -1f, 1f));
            return (align, deviation);
        }

        (int inFrontAlign, float inFrontDeviation) = Kick(1f);
        (int behindAlign, float behindDeviation) = Kick(-1f);

        Assert.True(inFrontAlign >= 95, $"Facing the aim reported only {inFrontAlign}% alignment");
        Assert.True(behindAlign < 70, $"Kicking backwards reported {behindAlign}% alignment");
        Assert.True(behindDeviation > inFrontDeviation,
            $"The backwards kick deviated {behindDeviation:F4} rad vs {inFrontDeviation:F4} rad");
    }

    // =============================================================================================
    // F21 — the controller override API must not bypass the pre-match human lock
    // =============================================================================================

    private static RecordedCommandController HumanLabelledReplay(MatchSimulationEngine sim) =>
        new(ControllerSourceType.HumanPreMatchLocked, new Dictionary<int, FootballerCommand>(),
            () => sim.CurrentTick);

    /// <summary>
    /// Audit F21: mid-match human overrides were rejected only when the config already named a human,
    /// so a match created without one could be given a human after kickoff.
    /// </summary>
    [Fact]
    public void F21_NoHumanMatch_CannotGainHumanControlAfterKickoff()
    {
        var sim = new MatchSimulationEngine(new PreMatchConfig { Seed = 31UL, PlayersPerTeam = 5, HalfDurationSeconds = 600f });

        // Before kickoff, a labelled human replay is legitimate setup (determinism tests rely on it).
        sim.OverrideController(5, HumanLabelledReplay(sim));
        sim.OverrideController(5, new TacticalUtilityAiController(new PitchDimensions()));

        for (int i = 0; i < 3; i++) sim.StepTick();

        Assert.Throws<InvalidOperationException>(() => sim.OverrideController(5, new HumanInputController(5)));
        Assert.Throws<InvalidOperationException>(() => sim.OverrideController(5, HumanLabelledReplay(sim)));
        Assert.Null(sim.LockedHumanController);
        Assert.False(sim.IsSpectatorFastForwardActive);
    }

    /// <summary>
    /// Audit F21: a human controller must only ever drive the player it is locked to — the old API
    /// let the object and the authored observation disagree.
    /// </summary>
    [Fact]
    public void F21_HumanController_CannotDriveAnotherPlayer()
    {
        var sim = new MatchSimulationEngine(new PreMatchConfig { Seed = 32UL, PlayersPerTeam = 5, HalfDurationSeconds = 600f });

        Assert.Throws<InvalidOperationException>(() => sim.OverrideController(5, new HumanInputController(6)));

        var human = new HumanInputController(5);
        sim.OverrideController(5, human);
        Assert.Same(human, sim.LockedHumanController);
        Assert.Same(human, sim.GetController(5));

        // The lock also holds once the match is live.
        sim.StepTick();
        Assert.Throws<InvalidOperationException>(() => sim.OverrideController(6, new HumanInputController(6)));
    }

    /// <summary>
    /// Audit F21: replacing the designated player's controller left <see cref="MatchSimulationEngine.LockedHumanController"/>
    /// pointing at the detached object, so live input and spectator state were tied to a stale reference.
    /// </summary>
    [Fact]
    public void F21_ReplacingTheLockedHumanController_KeepsTheAuthoritativeReference()
    {
        var sim = HumanMatch();
        var original = sim.LockedHumanController!;
        Assert.Equal(5, original.LockedPlayerId);

        sim.StepTick();

        var replacement = new HumanInputController(5);
        sim.OverrideController(5, replacement);

        Assert.Same(replacement, sim.LockedHumanController);
        Assert.Same(replacement, sim.GetController(5));

        // The engine must read the new object, not the detached original.
        replacement.NotifyPlayerUnavailableForMatch();
        Assert.True(sim.IsSpectatorFastForwardActive);
        Assert.False(original.IsSpectatorMode);

        // The locked player cannot be handed to an AI controller mid-match, so the reference can never
        // be left describing a controller that is not installed.
        Assert.Throws<InvalidOperationException>(() => sim.OverrideController(5, IdleController(sim)));
        Assert.Same(replacement, sim.LockedHumanController);
    }

    /// <summary>
    /// Audit F21: the dictionary accepted ids that are not players in the match. Unknown ids and a
    /// locked player who has been sent off must not create an inconsistent controller graph.
    /// </summary>
    [Fact]
    public void F21_UnknownPlayer_IsRejectedAndSentOffHumanStaysAuthoritative()
    {
        var sim = HumanMatch();
        var ai = new TacticalUtilityAiController(new PitchDimensions());

        Assert.Throws<ArgumentOutOfRangeException>(() => sim.OverrideController(999, ai));
        sim.StepTick();
        Assert.Throws<ArgumentOutOfRangeException>(() => sim.OverrideController(999, ai));

        // Removing the locked player from the pitch (red card) still leaves a valid human reference.
        var human = sim.LockedHumanController!;
        sim.Players.Single(p => p.Id == 5).IsSentOff = true;
        sim.StepTick();

        Assert.True(sim.IsSpectatorFastForwardActive);
        Assert.Same(human, sim.GetController(5));
        Assert.Throws<InvalidOperationException>(() => sim.OverrideController(5, ai));
    }
}




