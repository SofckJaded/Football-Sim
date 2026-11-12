using System;
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

namespace FootballSim.Tests;

/// <summary>
/// Regressions for the five gameplay-feel defects reported from playtesting the match:
///
/// <list type="number">
/// <item>an AI carrier near a line carried the ball straight out of play;</item>
/// <item>the AI's first touch flung the ball away instead of controlling it;</item>
/// <item>two AI players on the same spot made the ball jump between their feet (and shook the view);</item>
/// <item>only the elected responder ever defended, so one carrier could cross the whole block;</item>
/// <item>off-ball jobs were assigned dozens of metres away, pulling players out of formation.</item>
/// </list>
///
/// Each test drives the live decision/physics path, not a test-only property.
/// </summary>
public class GameplayFeelTests
{
    private const float Dt = SimConstants.FixedDeltaTime;
    private static readonly float BallRadius = BallGeometry.DefaultRadius;

    private static FootballerState Player(
        int id,
        TeamId team,
        TacticalRole role,
        Vector3 position,
        float strength = 0.78f)
    {
        return new FootballerState
        {
            Id = id,
            Team = team,
            ShirtNumber = id,
            Role = role,
            Position = position,
            Attributes = new PlayerAttributes { Strength01 = strength },
            Tendencies = PlayerTendencyProfile.ForRole(role),
            LastCommand = FootballerCommand.Idle
        };
    }

    /// <summary>
    /// A truth view of the opponents for the job coordinator (A2.6); the belief path is covered by
    /// <see cref="FootballSim.Tests.AI.TeamBeliefTests"/>.
    /// </summary>
    private static FootballSim.AI.TeamBelief TruthBelief(IReadOnlyList<FootballerState> players, TeamId team) =>
        FootballSim.AI.TeamBelief.FromTruth(team, players);

    private static FootballerObservation Observe(
        FootballerState self,
        IReadOnlyList<FootballerState> players,
        BallState ball,
        TeamId? possessingTeam,
        BallPossessionSnapshot? possession = null,
        bool? isDesignatedBallResponder = null,
        TeamJobAssignment? job = null)
    {
        return new PerceptionSystem().BuildObservation(
            self, players, ball, possessingTeam, MatchPhase.FirstHalf, 10f,
            self.Team == TeamId.Home ? 1f : -1f, isSetPieceTaker: false,
            new TeamTacticalProfile(), new SeededRandom(42),
            possession, isDesignatedBallResponder, job);
    }

    /// <summary>
    /// A winger carrying the ball near the touchline, with opponents goal-side (so the emptiest
    /// direction is straight out of play — the exact situation that used to walk the ball out).
    /// The whole ball must never cross a line.
    /// </summary>
    [Fact]
    public void AiCarrierNearTheTouchline_NeverCarriesTheBallOutOfPlay()
    {
        var pitch = new PitchDimensions();
        var carrier = Player(11, TeamId.Home, TacticalRole.Winger, new Vector3(0f, 0f, 30f));
        carrier.FacingXZ = new Vector2(1f, 0f);
        carrier.TacticalAnchor = new Vector3(0f, 0f, 26f);
        var mate = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(18f, 0f, -10f));
        // Goal-side of the carrier: the space behind them is toward the touchline.
        var centreBack = Player(14, TeamId.Away, TacticalRole.CenterBack, new Vector3(8f, 0f, 24f));
        var fullBack = Player(15, TeamId.Away, TacticalRole.FullBack, new Vector3(14f, 0f, 21f));

        var players = new List<FootballerState> { carrier, mate, centreBack, fullBack };
        var ball = new BallState
        {
            Position = new Vector3(0.35f, BallRadius, 30f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = carrier.Id
        };

        var ai = new TacticalUtilityAiController(pitch);
        var perception = new PerceptionSystem();
        var execution = new FootballerExecutionSystem(pitch);
        var solver = new BallPhysicsSolver(null, pitch);

        bool carried = false;
        for (int tick = 0; tick < 300; tick++)
        {
            var possession = ball.LastTouchedByPlayerId == carrier.Id
                ? new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, carrier.Id, null)
                : BallPossessionSnapshot.Loose;

            FootballerObservation observation = perception.BuildObservation(
                carrier, players, ball, TeamId.Home, MatchPhase.FirstHalf, tick * Dt,
                attackingDirX: 1f, isSetPieceTaker: false, new TeamTacticalProfile(),
                new SeededRandom((ulong)tick + 1), possession,
                isDesignatedBallResponder: true, job: null);

            carrier.LastCommand = ai.Decide(observation, Dt);
            carried |= ai.GetDebugSummary().StartsWith("DriveWithBall", StringComparison.Ordinal);

            execution.UpdateLocomotionAndContacts(
                players, ball, new SeededRandom((ulong)tick + 1), Dt, tick: tick);
            solver.Step(ball, Dt);

            Assert.True(
                MathF.Abs(ball.Position.Z) + BallRadius <= pitch.HalfWidth + 1e-3f,
                $"tick {tick}: the carried ball crossed the touchline (Z={ball.Position.Z:F2}, limit {pitch.HalfWidth:F1})");
            Assert.True(
                MathF.Abs(ball.Position.X) + BallRadius <= pitch.HalfLength + 1e-3f,
                $"tick {tick}: the carried ball crossed the goal line (X={ball.Position.X:F2})");
        }

        Assert.True(carried, "the carrier never actually drove with the ball; the scenario did not exercise the carry");
    }

    /// <summary>
    /// A pass arriving at a receiver's feet must be cushioned onto them: the touch must not teleport
    /// the ball (the old 55 % lerp moved it up to ~1 m in a single tick) and must not leave it moving
    /// anything like the incoming pace. After half a second it has to still be within reach.
    /// </summary>
    [Fact]
    public void FirstTouch_CushionsTheBallInsteadOfKickingItAway()
    {
        var pitch = new PitchDimensions();
        var receiver = Player(10, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        receiver.FacingXZ = new Vector2(-1f, 0f); // facing the incoming pass
        receiver.LastCommand = FootballerCommand.Idle with
        {
            DesiredFacingDirectionXZ = new Vector2(-1f, 0f)
        };

        var ball = new BallState
        {
            Position = new Vector3(-1.2f, BallRadius, 0f),
            Velocity = new Vector3(13f, 0f, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 8 // a team-mate played it
        };

        var players = new[] { receiver };
        var execution = new FootballerExecutionSystem(pitch);
        var solver = new BallPhysicsSolver();

        Vector3 beforeTouch = ball.Position;
        execution.UpdateLocomotionAndContacts(players, ball, new SeededRandom(1), Dt, tick: 1);
        solver.Step(ball, Dt);

        Assert.StartsWith("FirstTouchCushion", receiver.LastExecutionDiagnostic, StringComparison.Ordinal);

        float displacement = Vector2.Distance(
            new Vector2(beforeTouch.X, beforeTouch.Z),
            new Vector2(ball.Position.X, ball.Position.Z));
        Assert.True(displacement <= 0.25f,
            $"the first touch teleported the ball {displacement:F2} m (the old defect was a ~1 m jump)");

        float ballSpeed = new Vector2(ball.Velocity.X, ball.Velocity.Z).Length();
        Assert.True(ballSpeed <= 3.5f,
            $"a 13 m/s pass left the foot at {ballSpeed:F1} m/s; it was not cushioned");

        for (int tick = 2; tick <= 32; tick++)
        {
            execution.UpdateLocomotionAndContacts(players, ball, new SeededRandom((ulong)tick), Dt, tick: tick);
            solver.Step(ball, Dt);
        }

        float gap = Vector2.Distance(
            new Vector2(receiver.Position.X, receiver.Position.Z),
            new Vector2(ball.Position.X, ball.Position.Z));
        Assert.True(gap <= 1.4f, $"0.5 s after the first touch the ball was {gap:F2} m away from the receiver");
    }

    /// <summary>
    /// Two players on the same loose ball used to take turns winning the arbitration: each Collection
    /// teleported the ball to that player's "sweet spot", so it flickered between their feet and the
    /// ball-following camera shook. The player who just controlled it must keep a short exclusive
    /// claim that only a requested tackle breaks.
    /// </summary>
    [Fact]
    public void TwoPlayersOnTheBall_DoNotPingPongItBetweenTheirFeet()
    {
        var pitch = new PitchDimensions();
        var owner = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(0.30f, 0f, 0f), strength: 1.0f);
        owner.FacingXZ = new Vector2(1f, 0f);
        var rival = Player(12, TeamId.Away, TacticalRole.CenterBack, new Vector3(-3f, 0f, 0f));

        var ball = new BallState
        {
            Position = new Vector3(0f, BallRadius, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = owner.Id
        };

        var execution = new FootballerExecutionSystem(pitch);

        // Tick 1: only the owner is on the ball; he takes a controlling touch and locks it.
        execution.UpdateLocomotionAndContacts(new[] { owner, rival }, ball, new SeededRandom(1), Dt, tick: 1);
        Assert.Contains(execution.ContactEvents, c => c.PlayerId == owner.Id && c.Kind == BallContactKind.Collection);

        Vector3 ballAfterOwnerTouch = ball.Position;

        // The rival arrives on top of the ball on the next tick. A plain touch must not steal it
        // back (only a tackle may), so the ball must not jump to his feet.
        rival.Position = ball.Position + new Vector3(-0.15f, 0f, 0f);
        execution.UpdateLocomotionAndContacts(new[] { owner, rival }, ball, new SeededRandom(2), Dt, tick: 2);

        Assert.DoesNotContain(execution.ContactEvents, c => c.PlayerId == rival.Id);
        Assert.True(
            Vector2.Distance(
                new Vector2(ballAfterOwnerTouch.X, ballAfterOwnerTouch.Z),
                new Vector2(ball.Position.X, ball.Position.Z)) <= 0.2f,
            "the ball jumped away from the player who had just controlled it");

        // A requested tackle is still allowed to break the claim.
        rival.LastCommand = rival.LastCommand with { RequestTackle = true };
        execution.UpdateLocomotionAndContacts(new[] { owner, rival }, ball, new SeededRandom(3), Dt, tick: 3);
        Assert.Contains(execution.ContactEvents, c => c.PlayerId == rival.Id);
    }

    /// <summary>
    /// Two AI players standing on each other used to trade the ball with a tackle every ~0.5 s: each
    /// tackle put the ball at the winner's feet, the dispossessed player recovered, poked it back, and
    /// neither ever moved. This runs the same 1v1 face-off on the live decision/execution/physics
    /// path; it must produce a handful of contacts and actually get the players moving, not a tackle
    /// exchange.
    /// </summary>
    [Fact]
    public void TwoClosePlayers_DoNotTradeTheBallWithRepeatedTackles()
    {
        var pitch = new PitchDimensions();
        var carrier = Player(9, TeamId.Home, TacticalRole.Striker, Vector3.Zero);
        var defender = Player(12, TeamId.Away, TacticalRole.CenterBack, new Vector3(0.8f, 0f, 0f));
        carrier.TacticalAnchor = carrier.Position;
        defender.TacticalAnchor = defender.Position;

        var players = new List<FootballerState> { carrier, defender };
        var ball = new BallState
        {
            Position = new Vector3(0.25f, BallRadius, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = carrier.Id
        };

        var carrierAi = new TacticalUtilityAiController(pitch);
        var defenderAi = new TacticalUtilityAiController(pitch);
        var perception = new PerceptionSystem();
        var execution = new FootballerExecutionSystem(pitch);
        var solver = new BallPhysicsSolver();
        var tracker = new BallPossessionTracker();
        var rng = new SeededRandom(7);

        int tackles = 0;
        int collections = 0;
        Vector3 carrierStart = carrier.Position;
        Vector3 defenderStart = defender.Position;

        for (int tick = 1; tick <= 360; tick++)
        {
            BallPossessionSnapshot possession = tracker.Current;
            float time = tick * Dt;

            carrier.LastCommand = carrierAi.Decide(perception.BuildObservation(
                carrier, players, ball, possession.PossessionTeam, MatchPhase.FirstHalf, time,
                attackingDirX: 1f, isSetPieceTaker: false, new TeamTacticalProfile(), rng,
                possession, isDesignatedBallResponder: true), Dt);
            defender.LastCommand = defenderAi.Decide(perception.BuildObservation(
                defender, players, ball, possession.PossessionTeam, MatchPhase.FirstHalf, time,
                attackingDirX: -1f, isSetPieceTaker: false, new TeamTacticalProfile(), rng,
                possession, isDesignatedBallResponder: true), Dt);

            execution.UpdateLocomotionAndContacts(players, ball, rng, Dt, tick: tick);
            var contacts = execution.ContactEvents.ToList();
            tracker.Update(players, ball, contacts, Dt);
            solver.Step(ball, Dt);

            tackles += contacts.Count(c => c.Kind == BallContactKind.Tackle);
            collections += contacts.Count(c => c.Kind == BallContactKind.Collection);
        }

        // The bound is the invariant this test exists for: a tackle exchange (one every ~0.5 s) is what
        // used to happen, and three in six seconds is already generous. A2.4 gave the carrier a
        // pressure-aware choice of carry direction, so a defender standing 0.8 m in front is now
        // something the carrier turns away from instead of dribbling into — measured on `ai7-rs1`, this
        // face-off resolves as an evasion with no dispossession at all. Counting dispossessions was
        // therefore pinning the old behaviour, not the invariants; carrying and movement below are.
        Assert.InRange(tackles, 0, 3);
        Assert.True(collections > 20,
            $"the ball was never actually carried ({collections} control touches in 6 s)");
        float movement = MathF.Max(
            Vector3.Distance(carrierStart, carrier.Position),
            Vector3.Distance(defenderStart, defender.Position));
        Assert.True(movement > 1.5f, $"neither player moved away ({movement:F2} m)");
    }

    /// <summary>
    /// The elected responder alone could never stop a carrier who ran at the same speed: he chased
    /// the ball from behind. The improved first defender cuts the path, and once the carrier reaches
    /// the last 20 m a central defender takes up a covering position instead of standing in formation.
    /// </summary>
    [Fact]
    public void SecondDefender_StepsInFrontOfTheCarrierInTheDefensiveThird()
    {
        var pitch = new PitchDimensions();
        // Home defends the -X goal. Self is the second-best-placed central defender; a team-mate is
        // closer to the carrier (the elected responder), so this player must be the covering one.
        // The carrier is inside the last 20 m, where the second man is armed.
        var self = Player(4, TeamId.Home, TacticalRole.CenterBack, new Vector3(-42f, 0f, 4f));
        var responder = Player(6, TeamId.Home, TacticalRole.DefensiveMidfielder, new Vector3(-38f, 0f, 2f));
        var carrier = Player(15, TeamId.Away, TacticalRole.Striker, new Vector3(-34f, 0f, 0f));
        carrier.Velocity = new Vector3(6f, 0f, 0f);

        var players = new List<FootballerState> { self, responder, carrier };
        var ball = new BallState
        {
            Position = new Vector3(-33.7f, BallRadius, 0f),
            LastTouchedByTeam = TeamId.Away,
            LastTouchedByPlayerId = carrier.Id
        };
        var possession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Away, carrier.Id, null);

        FootballerObservation observation = Observe(
            self, players, ball, TeamId.Away, possession, isDesignatedBallResponder: false);

        var ai = new TacticalUtilityAiController(pitch);
        for (int i = 0; i < 12; i++) ai.Decide(observation, Dt);

        UtilityCandidate challenge = ai.LastCandidates.Single(
            c => c.ActionName is "ChallengeCarrier" or "ChallengeCarrierTackle");
        Assert.NotNull(challenge.TargetXZ);

        // The cover point is between the carrier and the goal this defender is protecting (-X), and
        // it leads the run slightly, so it is not simply the ball's current position.
        Vector3 target = challenge.TargetXZ!.Value;
        Assert.True(target.X < self.Position.X + 8f);
        Assert.True(target.X <= carrier.Position.X + 1.0f,
            $"the covering defender was sent to X={target.X:F1}, ahead of the carrier at {carrier.Position.X:F1}");
        Assert.True(challenge.Command.Sprint, "the second defender must cover with urgency");
    }

    /// <summary>
    /// The audit F10 contract must survive the new pressing: a loose ball is still the elected
    /// responder's job, and a player of the team in possession is not dragged into a challenge.
    /// </summary>
    [Fact]
    public void CarrierChallenge_DoesNotFireForLooseBallsOrOwnPossession()
    {
        var pitch = new PitchDimensions();
        var self = Player(4, TeamId.Home, TacticalRole.CenterBack, new Vector3(-42f, 0f, 4f));
        var carrier = Player(15, TeamId.Away, TacticalRole.Striker, new Vector3(-34f, 0f, 0f));
        var players = new List<FootballerState> { self, carrier };
        var ai = new TacticalUtilityAiController(pitch);

        var ballAtFeet = new BallState
        {
            Position = new Vector3(-33.7f, BallRadius, 0f),
            LastTouchedByTeam = TeamId.Away,
            LastTouchedByPlayerId = carrier.Id
        };

        FootballerObservation loose = Observe(
            self, players, ballAtFeet, possessingTeam: TeamId.Away,
            possession: BallPossessionSnapshot.Loose, isDesignatedBallResponder: false);
        for (int i = 0; i < 12; i++) ai.Decide(loose, Dt);
        Assert.DoesNotContain(ai.LastCandidates, c => c.ActionName.StartsWith("ChallengeCarrier", StringComparison.Ordinal));

        FootballerObservation ownPossession = Observe(
            self, players, ballAtFeet, possessingTeam: TeamId.Home,
            possession: new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, carrier.Id, null),
            isDesignatedBallResponder: false);
        var secondAi = new TacticalUtilityAiController(pitch);
        for (int i = 0; i < 12; i++) secondAi.Decide(ownPossession, Dt);
        Assert.DoesNotContain(secondAi.LastCandidates, c => c.ActionName.StartsWith("ChallengeCarrier", StringComparison.Ordinal));
    }

    /// <summary>
    /// A contextual job must not be handed to a player tens of metres from its target. The old
    /// assignment could send a full-back 50 m on an overlap and leave a hole in the shape.
    /// </summary>
    [Fact]
    public void OffBallJobs_AreNotAssignedToPlayersFarFromTheirTarget()
    {
        var pitch = new PitchDimensions();
        var players = new List<FootballerState>
        {
            Player(1, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-50f, 0f, 0f)),
            // Far-side width target is around (6, -31): this winger is already there.
            Player(11, TeamId.Home, TacticalRole.Winger, new Vector3(0f, 0f, -26f)),
            // This winger is 60 m from it and must not be sent across the pitch.
            Player(7, TeamId.Home, TacticalRole.Winger, new Vector3(10f, 0f, 26f)),
            Player(8, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(0f, 0f, 10f)),
            Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(18f, 0f, 0f)),
            Player(6, TeamId.Home, TacticalRole.DefensiveMidfielder, new Vector3(-6f, 0f, 2f)),
            Player(4, TeamId.Home, TacticalRole.CenterBack, new Vector3(-20f, 0f, 6f)),
            Player(12, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(50f, 0f, 0f)),
            Player(13, TeamId.Away, TacticalRole.CenterBack, new Vector3(16f, 0f, 6f)),
            Player(14, TeamId.Away, TacticalRole.CenterBack, new Vector3(20f, 0f, -6f)),
            Player(15, TeamId.Away, TacticalRole.DefensiveMidfielder, new Vector3(12f, 0f, 8f))
        };

        var ball = new BallState
        {
            Position = new Vector3(0f, BallRadius, 10f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 8
        };
        var possession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, 8, null);

        var jobs = new TeamJobCoordinator(pitch);
        jobs.Update(players, ball, possession, TeamId.Home, homeResponderId: null, awayResponderId: null,
            homeDirX: 1f, tick: 0,
            TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
            TruthBelief(players, TeamId.Home), TruthBelief(players, TeamId.Away));

        foreach (var (playerId, assignment) in jobs.Assignments)
        {
            FootballerState player = players.Single(p => p.Id == playerId);
            float travel = Vector2.Distance(
                new Vector2(player.Position.X, player.Position.Z),
                new Vector2(assignment.Anchor.X, assignment.Anchor.Z));

            switch (assignment.Job)
            {
                case TacticalJob.Overlap:
                case TacticalJob.FarSideWidth:
                case TacticalJob.DepthRun:
                    Assert.True(travel <= 26.5f, $"{assignment.Job} assigned {travel:F1} m away from the player");
                    break;
                case TacticalJob.ThirdPlayerOption:
                case TacticalJob.NearOutlet:
                    Assert.True(travel <= 22.5f, $"{assignment.Job} assigned {travel:F1} m away from the player");
                    break;
                case TacticalJob.DefensiveCover:
                    Assert.True(travel <= 22.5f, $"{assignment.Job} assigned {travel:F1} m away from the player");
                    break;
                case TacticalJob.CoverDefender:
                case TacticalJob.Marker:
                    Assert.True(travel <= 20.5f, $"{assignment.Job} assigned {travel:F1} m away from the player");
                    break;
            }
        }

        // The far winger must not be the one sent to the far-side touchline (the near one should take it).
        int? farSideWidthPlayerId = jobs.Assignments
            .Where(kv => kv.Value.Job == TacticalJob.FarSideWidth)
            .Select(kv => (int?)kv.Key)
            .FirstOrDefault();
        if (farSideWidthPlayerId.HasValue)
            Assert.NotEqual(7, farSideWidthPlayerId.Value);
    }

    // ---------------------------------------------------------------------
    // Playtest feedback: "passing for AI is broken — let them see if the pass line is blocked and
    // abandon it" and "the AI passes with almost no power".
    // ---------------------------------------------------------------------

    /// <summary>
    /// A passer with an opponent standing right next to the ball. The old lane metric skipped every
    /// opponent whose projection was under 0.4 m, so this pass scored as a perfectly clear lane and
    /// the AI kicked the ball straight through the defender (that is the interception the playtester
    /// saw about a tenth of the time). The explicit blocked-line test must abandon the pass instead.
    /// </summary>
    [Fact]
    public void Ai_AbandonsAPassThroughAnOpponentStandingOnTheBall()
    {
        var pitch = new PitchDimensions();
        var passer = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        var mate = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(15f, 0f, 0f));
        // 0.25 m up the line from the ball, 0.55 m off it: inside the old 0.4 m skip, so the old
        // clearance metric reports the lane as completely clear.
        var blocker = Player(14, TeamId.Away, TacticalRole.CenterBack, new Vector3(0.6f, 0f, 0.55f));

        var players = new List<FootballerState> { passer, mate, blocker };
        var ball = new BallState
        {
            Position = new Vector3(0.35f, BallRadius, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = passer.Id
        };

        FootballerObservation observation = Observe(passer, players, ball, TeamId.Home);
        var ai = new TacticalUtilityAiController(pitch);
        for (int i = 0; i < 40; i++) ai.Decide(observation, Dt);

        Vector2 ballXZ = new(ball.Position.X, ball.Position.Z);
        Vector2 mateXZ = new(mate.Position.X, mate.Position.Z);
        float legacyClearance = TacticalUtilityAiController.EvaluateLaneClearance(
            ballXZ, mateXZ, observation.VisiblePlayers, TeamId.Home);
        Assert.True(legacyClearance > 0.95f,
            $"the scenario must look clear to the old metric for this regression to mean anything ({legacyClearance:F2})");

        Assert.DoesNotContain(ai.LastCandidates, c => c.ActionName.StartsWith("Pass(", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half of the same report: a defender whose current position is metres off the line
    /// but who is running across it. Their reach and the ball's arrival coincide, so the pass is
    /// blocked even though a static "is somebody on the line?" check says the lane is open.
    /// </summary>
    [Fact]
    public void Ai_AbandonsAPassIntoALaneADefenderIsClosing()
    {
        var pitch = new PitchDimensions();
        var passer = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        var mate = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(18f, 0f, 0f));
        // 3.2 m off the line and running across it at 4.5 m/s: he meets the ball at ~12 m.
        var closing = Player(14, TeamId.Away, TacticalRole.CenterBack, new Vector3(12f, 0f, -3.2f));
        closing.Velocity = new Vector3(0f, 0f, 4.5f);

        var players = new List<FootballerState> { passer, mate, closing };
        var ball = new BallState
        {
            Position = new Vector3(0.35f, BallRadius, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = passer.Id
        };

        FootballerObservation observation = Observe(passer, players, ball, TeamId.Home);
        var ai = new TacticalUtilityAiController(pitch);
        for (int i = 0; i < 40; i++) ai.Decide(observation, Dt);

        Vector2 ballXZ = new(ball.Position.X, ball.Position.Z);
        Vector2 mateXZ = new(mate.Position.X, mate.Position.Z);
        float legacyClearance = TacticalUtilityAiController.EvaluateLaneClearance(
            ballXZ, mateXZ, observation.VisiblePlayers, TeamId.Home);
        Assert.True(legacyClearance > 0.95f,
            $"the scenario must look clear to the old metric for this regression to mean anything ({legacyClearance:F2})");

        Assert.DoesNotContain(ai.LastCandidates, c => c.ActionName.StartsWith("Pass(", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other side of the contract: with the lane genuinely open the AI must still pass. A blocked
    /// test that rejects every pass would silently turn the side back into eleven dribblers.
    /// </summary>
    [Fact]
    public void Ai_StillPassesUpAGenuinelyOpenLane()
    {
        var pitch = new PitchDimensions();
        var passer = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        var mate = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(15f, 0f, 0f));
        // Well off the line and running away from it.
        var runner = Player(14, TeamId.Away, TacticalRole.CenterBack, new Vector3(8f, 0f, -8f));
        runner.Velocity = new Vector3(0f, 0f, -4f);

        var players = new List<FootballerState> { passer, mate, runner };
        var ball = new BallState
        {
            Position = new Vector3(0.35f, BallRadius, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = passer.Id
        };

        FootballerObservation observation = Observe(passer, players, ball, TeamId.Home);
        var ai = new TacticalUtilityAiController(pitch);
        for (int i = 0; i < 40; i++) ai.Decide(observation, Dt);

        Assert.Contains(ai.LastCandidates, c => c.ActionName.StartsWith("Pass(", StringComparison.Ordinal));
    }

    /// <summary>
    /// The AI used to strike almost every pass with the minimum power that merely *reached* the
    /// receiver, so a 15 m pass crawled there in about two seconds (5 m/s at arrival) and every
    /// defender beside the lane could step in. A normal pass is firm: the executed kick must be
    /// well above the old minimum power, arrive inside the reception reach, and get there quickly.
    /// </summary>
    [Fact]
    public void Ai_PassesAreStruckWithFirmPowerAndArriveQuickly()
    {
        var pitch = new PitchDimensions();
        var passer = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        var mate = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(20f, 0f, 0f));

        var players = new List<FootballerState> { passer, mate };
        var ball = new BallState
        {
            Position = new Vector3(0.35f, BallRadius, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = passer.Id
        };

        FootballerObservation observation = Observe(passer, players, ball, TeamId.Home);
        var ai = new TacticalUtilityAiController(pitch);
        for (int i = 0; i < 40; i++) ai.Decide(observation, Dt);

        UtilityCandidate pass = ai.LastCandidates.Single(
            c => c.ActionName.StartsWith("Pass(", StringComparison.Ordinal));
        Assert.True(pass.Command.KickPower01 >= 0.80f,
            $"a 20 m pass was struck at {pass.Command.KickPower01:P0} power (the old minimum-power rule struck it at 50 %)");

        passer.LastCommand = pass.Command;
        new FootballerExecutionSystem(pitch).UpdateLocomotionAndContacts(
            new[] { passer, mate }, ball, new SeededRandom(1), Dt, tick: 1);
        Assert.StartsWith("Kick:GroundPass", passer.LastExecutionDiagnostic, StringComparison.Ordinal);

        var solver = new BallPhysicsSolver();
        const float receptionReach = 0.75f * 1.65f; // ReachRadius × 1.65 for a team-mate receiving
        float closest = float.MaxValue;
        int arrivalTick = -1;
        for (int tick = 1; tick <= 300; tick++)
        {
            solver.Step(ball, Dt);
            float gap = Vector2.Distance(
                new Vector2(ball.Position.X, ball.Position.Z),
                new Vector2(mate.Position.X, mate.Position.Z));
            if (gap < closest) closest = gap;
            if (gap <= receptionReach && arrivalTick < 0) arrivalTick = tick;
        }

        Assert.True(closest <= receptionReach, $"the pass missed the receiver by {closest:F2} m");
        Assert.True(arrivalTick > 0, "the pass never reached the receiver inside the reception reach");
        float arrivalSeconds = arrivalTick * Dt;
        Assert.True(arrivalSeconds <= 1.75f,
            $"the 20 m pass took {arrivalSeconds:F2} s to arrive (the slow minimum-power pass took ~2.4 s)");
    }
}
