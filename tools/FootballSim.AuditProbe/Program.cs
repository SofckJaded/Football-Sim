using System.Numerics;
using System.Text.Json;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Rules;
using FootballSim.Tactics;
using FootballSim.Training;

// Read-only baseline diagnostics. These report actual behavior, not passing
// acceptance tests. Keep separate from the production test suite.
var output = new Dictionary<string, object>();
const float dt = SimConstants.FixedDeltaTime;
var pitch = new PitchDimensions();
FootballerState Player(int id, TeamId team, TacticalRole role, Vector3 pos) => new()
{
    Id = id, Team = team, ShirtNumber = id, Role = role, Position = pos,
    Attributes = new PlayerAttributes(), Tendencies = new PlayerTendencies()
};
FootballerObservation Observe(FootballerState p, IReadOnlyList<FootballerState> players,
    BallState ball, bool taker = false, BallPossessionSnapshot? possession = null,
    bool? isDesignatedBallResponder = null, TeamTacticalProfile? profile = null) => new PerceptionSystem().BuildObservation(
        p, players, ball, ball.LastTouchedByTeam, MatchPhase.FirstHalf, 10f,
        p.Team == TeamId.Home ? 1f : -1f, taker,
        profile ?? TeamTacticalProfile.PossessionControl, new SeededRandom(42),
        possession, isDesignatedBallResponder);
object Vec(Vector3 v) => new { x = v.X, y = v.Y, z = v.Z };

// A 20 m cross-field pass to a runner moving sideways at 6 m/s.
{
    var passer = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
    var runner = Player(9, TeamId.Home, TacticalRole.Striker, new(20, 0, 0));
    runner.Velocity = new(0, 0, 6);
    var ball = new BallState { Position = new(0.4f, .11f, 0), LastTouchedByTeam = TeamId.Home, LastTouchedByPlayerId = 8 };
    var ai = new TacticalUtilityAiController(pitch);
    var obs = Observe(passer, new[] { passer, runner }, ball);
    for (int i = 0; i < 50; i++) ai.Decide(obs, dt);

    var planner = new PassPlanner(pitch);
    var plan = planner.PlanGroundPass(
        new Vector2(ball.Position.X, ball.Position.Z), 9,
        new Vector2(runner.Position.X, runner.Position.Z), new Vector2(0f, 6f), 8f);

    var candidate = ai.LastCandidates.FirstOrDefault(c => c.ActionName.Contains("To#9"));
    if (candidate.ActionName is null)
    {
        output["moving_receiver"] = new
        {
            plannerMaxDistance = planner.MaxPassDistance,
            plan,
            candidate = "none",
            candidates = ai.LastCandidates.Select(c => c.ActionName).ToArray(),
            calibration = new[] { 0.2f, 0.4f, 0.6f, 0.8f, 1.0f }.Select(p => new
            {
                power = p,
                range = planner.RangeFor(p),
                t5 = planner.TryGetFlightTime(p, 5f, out var a) ? a : -1f,
                t10 = planner.TryGetFlightTime(p, 10f, out var b) ? b : -1f,
                t20 = planner.TryGetFlightTime(p, 20f, out var c) ? c : -1f,
                t30 = planner.TryGetFlightTime(p, 30f, out var d) ? d : -1f
            }).ToArray()
        };
    }
    else
    {
        var cmd = candidate.Command;
        passer.LastCommand = cmd;
        new FootballerExecutionSystem(pitch).UpdateLocomotionAndContacts(new[] { passer }, ball, new SeededRandom(1), dt);
        var solver = new BallPhysicsSolver();
        float minMiss = float.MaxValue;
        float closestAt = 0;
        for (int i = 1; i <= 300; i++)
        {
            solver.Step(ball, dt);
            Vector3 receiver = runner.Position + runner.Velocity * (i * dt);
            float miss = Vector2.Distance(new(ball.Position.X, ball.Position.Z), new(receiver.X, receiver.Z));
            if (miss < minMiss) { minMiss = miss; closestAt = i * dt; }
        }
        output["moving_receiver"] = new
        {
            plannerMaxDistance = planner.MaxPassDistance,
            plan,
            commandedAim = Vec(cmd.KickAimDirectionXZ),
            cmd.KickPower01,
            closestDistanceMeters = minMiss,
            closestTimeSeconds = closestAt
        };
    }
}

// Save with the keeper facing toward their own goal. A 'catch' has no held state.
foreach (float facingX in new[] { -1f, 1f })
{
    var keeper = Player(1, TeamId.Home, TacticalRole.Goalkeeper, new(-51.9f, 0, 0));
    keeper.FacingXZ = new(facingX, 0);
    keeper.LastCommand = FootballerCommand.Idle with { DesiredFacingDirectionXZ = keeper.FacingXZ };
    var ball = new BallState { Position = new(-51.5f, .11f, 0), Velocity = new(-10, 0, 0), LastTouchedByTeam = TeamId.Away, LastTouchedByPlayerId = 12 };
    var exec = new FootballerExecutionSystem(pitch);
    exec.UpdateLocomotionAndContacts(new[] { keeper }, ball, new SeededRandom(1), dt);
    string firstDiagnostic = keeper.LastExecutionDiagnostic;
    object catchPos = Vec(ball.Position);
    int? heldByAfterCatch = ball.HeldByPlayerId;
    var solver = new BallPhysicsSolver();
    var controller = new GoalkeeperController(pitch);
    var timeline = new List<object>();
    bool ownGoal = false;
    int goalTick = 0;
    for (int i = 1; i <= 90; i++)
    {
        keeper.LastCommand = controller.Decide(Observe(keeper, new[] { keeper }, ball), dt);
        float recoveryBefore = keeper.KickRecoveryTimer;
        exec.UpdateLocomotionAndContacts(new[] { keeper }, ball, new SeededRandom((ulong)i), dt);
        solver.Step(ball, dt);
        if (i % 10 == 0 || keeper.KickRecoveryTimer > recoveryBefore)
            timeline.Add(new { tick = i, keeper = Vec(keeper.Position), ball = Vec(ball.Position), velocity = Vec(ball.Velocity), heldBy = ball.HeldByPlayerId, holdRemaining = Math.Round(ball.HeldReleaseDelaySeconds, 2), keeper.KickRecoveryTimer, state = controller.State.ToString(), command = controller.GetDebugSummary(), execution = keeper.LastExecutionDiagnostic });
        if (ball.Position.X < -pitch.HalfLength - .11f) { ownGoal = true; goalTick = i; break; }
    }
    output[$"keeper_catch_facing_{facingX}"] = new { firstDiagnostic, catchPos, heldByAfterCatch, ownGoal, goalTick, finalBall = Vec(ball.Position), finalDiagnostic = keeper.LastExecutionDiagnostic, timeline };
}

// Held-ball release (audit F03): a catch is held, then released exactly once toward a team-mate.
{
    var keeper = Player(1, TeamId.Home, TacticalRole.Goalkeeper, new(-51.9f, 0, 0));
    keeper.LastCommand = FootballerCommand.Idle;
    var mate = Player(6, TeamId.Home, TacticalRole.DefensiveMidfielder, new(-33, 0, -7));
    var ball = new BallState { Position = new(-51.4f, .11f, 0), Velocity = new(-12, 0, 0), LastTouchedByTeam = TeamId.Away, LastTouchedByPlayerId = 12 };
    var exec = new FootballerExecutionSystem(pitch);
    exec.UpdateLocomotionAndContacts(new[] { keeper }, ball, new SeededRandom(1), dt);
    var controller = new GoalkeeperController(pitch);
    var solver = new BallPhysicsSolver();
    int releaseTick = 0;
    string releaseCommand = "none";
    int? releasedTo = null;
    int kicks = 0;
    for (int i = 1; i <= 120; i++)
    {
        keeper.LastCommand = controller.Decide(Observe(keeper, new[] { keeper, mate }, ball), dt);
        if (keeper.LastCommand.KickType != KickIntentType.None)
        {
            if (releaseTick == 0)
            {
                releaseTick = i;
                releaseCommand = controller.GetDebugSummary();
                releasedTo = keeper.LastCommand.IntendedReceiverId;
            }
        }

        exec.UpdateLocomotionAndContacts(new[] { keeper, mate }, ball, new SeededRandom((ulong)i), dt);
        kicks += exec.ContactEvents.Count(c => c.Kind == BallContactKind.Kick && c.PlayerId == keeper.Id);
        solver.Step(ball, dt);
    }

    output["keeper_hold_release"] = new
    {
        releaseTick,
        releaseCommand,
        releasedTo,
        keeperKicks = kicks,
        heldAtEnd = ball.HeldByPlayerId,
        finalBall = Vec(ball.Position),
        finalVelocity = Vec(ball.Velocity)
    };
}

// A held ball cannot be tackled away from the keeper (audit F03).
{
    var keeper = Player(1, TeamId.Home, TacticalRole.Goalkeeper, new(-51.9f, 0, 0));
    keeper.LastCommand = FootballerCommand.Idle;
    var ball = new BallState { Position = new(-51.4f, .11f, 0), Velocity = new(-12, 0, 0), LastTouchedByTeam = TeamId.Away, LastTouchedByPlayerId = 12 };
    var exec = new FootballerExecutionSystem(pitch);
    exec.UpdateLocomotionAndContacts(new[] { keeper }, ball, new SeededRandom(1), dt);
    int? heldAfterCatch = ball.HeldByPlayerId;

    var opponent = Player(12, TeamId.Away, TacticalRole.Striker, new(-51.45f, 0, 0));
    opponent.LastCommand = FootballerCommand.Idle with { RequestTackle = true, DesiredMoveDirectionXZ = new Vector2(-1, 0) };
    for (int i = 1; i <= 30; i++)
    {
        keeper.LastCommand = FootballerCommand.Idle;
        exec.UpdateLocomotionAndContacts(new[] { keeper, opponent }, ball, new SeededRandom((ulong)i), dt);
    }

    output["keeper_hold_tackle"] = new
    {
        heldAfterCatch,
        heldAtEnd = ball.HeldByPlayerId,
        lastTouchedBy = ball.LastTouchedByPlayerId,
        keeperDiagnostic = keeper.LastExecutionDiagnostic,
        opponentDiagnostic = opponent.LastExecutionDiagnostic
    };
}

// Parry spin carry-over: compare the exact same save with/without shot spin.
foreach (float spinZ in new[] { 0f, 24f, 120f })
{
    var keeper = Player(1, TeamId.Home, TacticalRole.Goalkeeper, new(-51.7f, 0, 0));
    keeper.LastCommand = FootballerCommand.Idle;
    var ball = new BallState { Position = new(-51.0f, .11f, 0), Velocity = new(-16, 0, 0), AngularVelocity = new(0, 0, spinZ), LastTouchedByTeam = TeamId.Away };
    new FootballerExecutionSystem(pitch).UpdateLocomotionAndContacts(new[] { keeper }, ball, new SeededRandom(1), dt);
    object parryVelocity = Vec(ball.Velocity);
    var solver = new BallPhysicsSolver();
    bool crossedOwnGoal = false;
    float mostGoalwardVelocity = 0;
    for (int i = 0; i < 180; i++)
    {
        solver.Step(ball, dt);
        mostGoalwardVelocity = MathF.Min(mostGoalwardVelocity, ball.Velocity.X);
        if (ball.Position.X < -pitch.HalfLength - .11f && MathF.Abs(ball.Position.Z) < pitch.HalfGoalWidth)
            crossedOwnGoal = true;
    }
    output[$"parry_spin_{spinZ}"] = new { keeper.LastExecutionDiagnostic, parryVelocity, mostGoalwardVelocity, crossedOwnGoal, finalBall = Vec(ball.Position) };
}

// Collision impulse sanity check: this suspected issue is NOT a bug.
{
    var a = Player(1, TeamId.Home, TacticalRole.Striker, new(-.3f, 0, 0));
    var b = Player(2, TeamId.Away, TacticalRole.Striker, new(.3f, 0, 0));
    a.Velocity = new(1, 0, 0); b.Velocity = new(-1, 0, 0);
    var collide = typeof(FootballerExecutionSystem).GetMethod("ResolvePlayerCollisions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    collide.Invoke(null, new object[] { new[] { a, b } });
    output["collision"] = new { initialClosingSpeed = 2f, finalClosingSpeed = a.Velocity.X - b.Velocity.X };
}

// Restart clock and direction, including configurable short halves.
foreach (float halfLength in new[] { 1f, 2700f })
{
    var rules = new MatchRulesEngine(pitch);
    var ball = new BallState();
    rules.StartMatch(Array.Empty<FootballerState>(), ball);
    rules.StepRules(Array.Empty<FootballerState>(), ball, halfLength, halfLength);
    rules.StepRules(Array.Empty<FootballerState>(), ball, halfLength, 1f);
    var secondHalf = rules.Phase.ToString();
    ball.Position = new(0, .11f, 35);
    ball.LastTouchedByTeam = TeamId.Home;
    rules.StepRules(Array.Empty<FootballerState>(), ball, halfLength, dt);
    var duringRestartDirection = rules.GetHomeAttackDirectionX();
    rules.StepRules(Array.Empty<FootballerState>(), ball, halfLength, 1f);
    output[$"restart_half_{halfLength}"] = new { secondHalf, duringRestartDirection, resumedPhase = rules.Phase.ToString(), resumedDirection = rules.GetHomeAttackDirectionX() };
}

// Human call-for-pass: does pressing pass off the ball actually raise a call during a live match?
{
    var sim = new MatchSimulationEngine(new PreMatchConfig { Seed = 4242UL, DesignatedHumanPlayerId = 9 });
    var human = sim.LockedHumanController!;
    int presses = 0;
    int ticksWithCall = 0;
    const int ticks = 3600;

    for (int i = 0; i < ticks; i++)
    {
        if (i % 30 == 0)
        {
            human.ReleaseChargedKick(KickIntentType.GroundPass, 0.5f);
            presses++;
        }
        sim.StepTick();
        if (human.Communication.HasActiveCall) ticksWithCall++;
    }

    output["human_call_for_pass"] = new
    {
        presses,
        ticksWithCall,
        share = Math.Round(ticksWithCall / (double)ticks, 3),
        throughCalls = human.Communication.HumanCalledThroughBall
    };
}

// Call-for-pass accuracy: a sprinting caller must get the ball near where they asked for it.
{
    var pitch2 = new PitchDimensions();
    var comms = new MatchCommunicationContext();
    var passer = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
    var caller = Player(9, TeamId.Home, TacticalRole.Striker, new(14, 0, 3));
    caller.Velocity = new(2, 0, 3);
    var ball = new BallState { Position = new(0.4f, .11f, 0), LastTouchedByTeam = TeamId.Home, LastTouchedByPlayerId = 8 };
    var ai = new TacticalUtilityAiController(pitch2, comms);
    var obs = Observe(passer, new[] { passer, caller }, ball);
    comms.RaiseCallForPass(TeamId.Home, 9, 2.5f, false);
    for (int i = 0; i < 12; i++) ai.Decide(obs, dt);

    var candidate = ai.LastCandidates.FirstOrDefault(c => c.ActionName.Contains("To#9"));
    if (candidate.ActionName is null)
    {
        output["call_for_pass_accuracy"] = new { candidate = "none", offered = ai.LastCandidates.Select(c => c.ActionName).ToArray() };
    }
    else
    {
        passer.LastCommand = candidate.Command;
        new FootballerExecutionSystem(pitch2).UpdateLocomotionAndContacts(new[] { passer }, ball, new SeededRandom(1), dt);
        var solver = new BallPhysicsSolver();
        float minMiss = float.MaxValue;
        for (int i = 1; i <= 240; i++)
        {
            solver.Step(ball, dt);
            Vector3 at = caller.Position + caller.Velocity * (i * dt);
            minMiss = MathF.Min(minMiss, Vector2.Distance(new(ball.Position.X, ball.Position.Z), new(at.X, at.Z)));
        }
        output["call_for_pass_accuracy"] = new { closestMeters = minMiss, power = candidate.Command.KickPower01 };
    }
}

// FullTime does not stop the simulation engine.
{
    var sim = new MatchSimulationEngine(new PreMatchConfig { PlayersPerTeam = 5, HalfDurationSeconds = 1 });
    for (int i = 0; i < 1000 && sim.Rules.Phase != MatchPhase.FullTime; i++) sim.StepTick();
    var before = sim.Ball.Position;
    sim.Ball.Velocity = new(5, 0, 0);
    sim.StepTick();
    output["after_fulltime"] = new { phase = sim.Rules.Phase.ToString(), moved = sim.Ball.Position != before };
}

// Restarts execute while the rules engine still claims to be paused.
{
    var sim = new MatchSimulationEngine();
    sim.Ball.Position = new(0, .11f, 35);
    sim.Ball.LastTouchedByTeam = TeamId.Away;
    sim.StepTick();
    string phase = sim.Rules.Phase.ToString();
    sim.StepTick();
    output["restart_pause"] = new { phase, sim.Rules.PhasePauseTimer, ballVelocity = Vec(sim.Ball.Velocity), ballMovedDuringPause = sim.Ball.Velocity.Length() > 0.1f };
}

// Kickoff taker is not cleared by executing the kickoff.
{
    var sim = new MatchSimulationEngine();
    for (int i = 0; i < 600; i++) sim.StepTick();
    output["sticky_kickoff_taker"] = new { sim.Rules.DesignatedSetPieceTakerId, ballLastTouchedBy = sim.Ball.LastTouchedByPlayerId, phase = sim.Rules.Phase.ToString() };
}

// Offside baseline must refresh when the SAME player plays the ball again. Before the F08 fix the
// snapshot was keyed to toucher identity, so this later pass reused the stale 30 m position.
{
    var rules = new MatchRulesEngine(pitch);
    var passer = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
    var receiver = Player(9, TeamId.Home, TacticalRole.Striker, new(30, 0, 0));
    var def1 = Player(12, TeamId.Away, TacticalRole.Goalkeeper, new(20, 0, 0));
    var def2 = Player(13, TeamId.Away, TacticalRole.CenterBack, new(10, 0, 0));
    var players = new[] { passer, receiver, def1, def2 };
    var ball = new BallState();
    rules.StartMatch(players, ball);
    passer.Position = Vector3.Zero; receiver.Position = new(30, 0, 0);
    def1.Position = new(20, 0, 0); def2.Position = new(10, 0, 0);
    ball.Position = new(0, .11f, 0); ball.LastTouchedByPlayerId = 8; ball.LastTouchedByTeam = TeamId.Home;

    // Play 1: #9 is beyond the line and must be flagged.
    rules.StepRules(players, ball, 2700, dt,
        new[] { new BallContactEvent(1, BallContactKind.Kick, 8, TeamId.Home, ball.Position) });

    receiver.Position = new(5, 0, 0);

    // Play 2: the same passer plays again, now with #9 onside.
    rules.StepRules(players, ball, 2700, dt,
        new[] { new BallContactEvent(2, BallContactKind.Kick, 8, TeamId.Home, ball.Position) });

    ball.Position = new(5, .11f, 0); ball.LastTouchedByPlayerId = 9;
    rules.StepRules(players, ball, 2700, dt,
        new[] { new BallContactEvent(3, BallContactKind.Collection, 9, TeamId.Home, ball.Position) });

    output["stale_offside"] = new { receiverOnsideAtPass = true, actualPhase = rules.Phase.ToString(), lastEvent = rules.Events[^1].Details };
}

// Live input path (QueueTackle), unlike immediate test compatibility property.
{
    var human = new HumanInputController(9);
    var p = Player(9, TeamId.Home, TacticalRole.Striker, Vector3.Zero);
    var ball = new BallState { Position = new(.6f, .11f, 0), LastTouchedByTeam = TeamId.Away };
    human.QueueTackle();
    var cmd = human.Decide(Observe(p, new[] { p }, ball), dt);
    output["close_human_tackle"] = new { distanceMeters = .6f, cmd.RequestTackle };
}

// Call-for-pass state leaks across independent engine instances.
{
    var sim = new MatchSimulationEngine(new PreMatchConfig { DesignatedHumanPlayerId = 9 });
    var human = sim.Players.Single(p => p.Id == 9);
    human.Position = new(10, 0, 0);
    sim.Ball.Position = new(0, .11f, 0);
    sim.Ball.LastTouchedByTeam = TeamId.Home;
    sim.LockedHumanController!.ReleaseChargedKick(KickIntentType.GroundPass, .5f);
    sim.LockedHumanController.Decide(Observe(human, sim.Players, sim.Ball), dt);
    int? before = sim.LockedHumanController!.Communication.HumanCallForPassPlayerId;
    _ = new MatchSimulationEngine();
    // After the F19 fix this stays 9 (match-owned state); before the fix it was null.
    output["cross_match_call_reset"] = new { callerBeforeCreatingOtherMatch = before, callerAfterCreatingOtherMatch = sim.LockedHumanController!.Communication.HumanCallForPassPlayerId };
}

// Controller override lock (audit F21): the pre-match human lock must not depend on the optional
// initial config, a human controller may only drive its own player, unknown ids are rejected, and the
// authoritative human reference must follow a replacement.
{
    string Try(Action action)
    {
        try
        {
            action();
            return "ok";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    var noHuman = new MatchSimulationEngine(new PreMatchConfig
    {
        Seed = 71UL, PlayersPerTeam = 5, HalfDurationSeconds = 600f
    });
    noHuman.OverrideController(5, new TacticalUtilityAiController(pitch));
    for (int i = 0; i < 3; i++) noHuman.StepTick();

    var withHuman = new MatchSimulationEngine(new PreMatchConfig
    {
        Seed = 72UL, PlayersPerTeam = 5, HalfDurationSeconds = 600f, DesignatedHumanPlayerId = 5
    });
    withHuman.StepTick();
    var replacement = new HumanInputController(5);
    string replaceResult = Try(() => withHuman.OverrideController(5, replacement));

    output["override_lock"] = new
    {
        noHumanConfigLateHumanOverride = Try(() => noHuman.OverrideController(5, new HumanInputController(5))),
        noHumanConfigLateReplayOverride = Try(() => noHuman.OverrideController(5,
            new RecordedCommandController(ControllerSourceType.HumanPreMatchLocked,
                new Dictionary<int, FootballerCommand>(), () => noHuman.CurrentTick))),
        wrongPlayerHumanController = Try(() => noHuman.OverrideController(5, new HumanInputController(6))),
        unknownPlayerId = Try(() => noHuman.OverrideController(999, new TacticalUtilityAiController(pitch))),
        replaceLockedHumanController = replaceResult,
        lockedReferenceUpdated = ReferenceEquals(withHuman.LockedHumanController, replacement),
        lockedPlayerToAiAfterKickoff = Try(() => withHuman.OverrideController(5, new TacticalUtilityAiController(pitch)))
    };
}

// During keeper kick recovery, coincident ground positions normalize a zero
// direction. NaN is on Z only; ball safety currently checks only X.
{
    var keeper = Player(1, TeamId.Home, TacticalRole.Goalkeeper, new(-50, 0, 0));
    keeper.KickRecoveryTimer = .5f;
    var ball = new BallState { Position = new(-50, .11f, 0), LastTouchedByTeam = TeamId.Home };
    var cmd = new GoalkeeperController(pitch).Decide(Observe(keeper, new[] { keeper }, ball), dt);
    bool nonFiniteCommand = !float.IsFinite(cmd.DesiredMoveDirectionXZ.X) || !float.IsFinite(cmd.DesiredMoveDirectionXZ.Y);
    ball.Position = new(0, .11f, float.NaN);
    new BallPhysicsSolver().Step(ball, dt);
    output["nonfinite_guards"] = new { keeperGeneratedNonFiniteMove = nonFiniteCommand, ballStillNonFiniteZ = !float.IsFinite(ball.Position.Z) };
}

// Possession ownership (audit F10): a deflection changes the last touch but nobody controls the ball.
{
    var coord = new TeamTacticalCoordinator(pitch);
    var tracker = new BallPossessionTracker();
    var players = new[]
    {
        Player(8, TeamId.Home, TacticalRole.CentralMidfielder, new(-0.4f, 0, 0)),
        Player(9, TeamId.Home, TacticalRole.Striker, new(6, 0, 2)),
        Player(13, TeamId.Away, TacticalRole.CenterBack, new(5, 0, 3)),
        Player(12, TeamId.Away, TacticalRole.Goalkeeper, new(-45, 0, 0))
    };
    var ball = new BallState { Position = new(0, .11f, 0), LastTouchedByTeam = TeamId.Home, LastTouchedByPlayerId = 8 };
    tracker.Update(players, ball,
        new[] { new BallContactEvent(1, BallContactKind.Collection, 8, TeamId.Home, ball.Position) }, dt);
    for (int i = 0; i < 60; i++) coord.ObservePossession(tracker.Current, ball, 1f, dt);
    string afterHomeControl = coord.ConfirmedPossessingTeam?.ToString() ?? "none";
    string controlState = tracker.Current.State.ToString();

    // Away keeper only deflects (parries) it: the last touch flips, but a deflection must not hand
    // possession to the defending team.
    ball.LastTouchedByTeam = TeamId.Away;
    ball.LastTouchedByPlayerId = 12;
    ball.Velocity = new(-6, 0, 1);
    tracker.Update(players, ball,
        new[] { new BallContactEvent(2, BallContactKind.Save, 12, TeamId.Away, ball.Position) { IsParry = true } }, dt);
    for (int i = 0; i < 60; i++) coord.ObservePossession(tracker.Current, ball, 1f, dt);
    string afterAwayParry = coord.ConfirmedPossessingTeam?.ToString() ?? "none";
    string stateAfterParry = tracker.Current.State.ToString();

    // A clearance is a deliberate play, so it does hand possession to the clearing team.
    ball.LastTouchedByPlayerId = 13;
    tracker.Update(players, ball,
        new[] { new BallContactEvent(3, BallContactKind.Kick, 13, TeamId.Away, ball.Position) { Intent = KickIntentType.Clearance } }, dt);
    for (int i = 0; i < 60; i++) coord.ObservePossession(tracker.Current, ball, 1f, dt);

    output["possession_deflection"] = new
    {
        afterHomeControl,
        controlStateAfterHomeControl = controlState,
        afterAwayParry,
        stateAfterParry,
        afterAwayClearance = coord.ConfirmedPossessingTeam?.ToString() ?? "none",
        ballSpeed = ball.Velocity.Length()
    };
}

// A nearest idle human must not suppress the AI teammate that can actually reach a loose ball (F10).
{
    var human = Player(9, TeamId.Home, TacticalRole.Striker, new(1.0f, 0, 0.4f));
    var mate = Player(10, TeamId.Home, TacticalRole.CentralMidfielder, new(6, 0, -3));
    var ball = new BallState { Position = new(0, .11f, 0), LastTouchedByTeam = TeamId.Home };
    var players = new[] { human, mate };
    var election = new BallResponderElection(pitch);
    var snapshot = BallPossessionSnapshot.Loose;
    election.Update(players, ball, snapshot, humanPlayerId: human.Id);

    var obs = Observe(mate, players, ball, possession: snapshot,
        isDesignatedBallResponder: election.IsResponder(TeamId.Home, mate.Id));
    var ai = new TacticalUtilityAiController(pitch);
    for (int i = 0; i < 12; i++) ai.Decide(obs, dt);
    output["idle_human_responder"] = new
    {
        electedResponder = election.GetResponder(TeamId.Home),
        humanIsNearest = true,
        offered = ai.LastCandidates.Select(c => c.ActionName).ToArray()
    };
}

// Reception assistance (F11): an incoming friendly pass should only steer its designated receiver.
{
    object RunAssist(bool incomingPass, int? humanReceiverId = null)
    {
        FootballerState Receiver(int id, Vector3 pos)
        {
            var p = Player(id, TeamId.Home, TacticalRole.Striker, pos);
            p.LastCommand = new FootballerCommand(
                new Vector2(0, 1), new Vector2(0, 1), false, false, false, false, false,
                Vector3.Zero, KickIntentType.None, Vector3.UnitX, 0, 0);
            return p;
        }

        var intended = Receiver(9, new(4, 0, 1.2f));
        var bystander = Receiver(10, new(3, 0, 2.2f));
        var ball = incomingPass
            ? new BallState { Position = new(0, .11f, 0), Velocity = new(10, 0, 0), LastTouchedByTeam = TeamId.Home, LastTouchedByPlayerId = 8 }
            : new BallState { Position = new(0, .11f, 0), LastTouchedByTeam = TeamId.Home, LastTouchedByPlayerId = 8 };

        ReceptionAssistContext? assist = incomingPass
            ? new ReceptionAssistContext(BallControlState.InFlightPass, intended.Id, humanReceiverId)
            : null;

        var exec = new FootballerExecutionSystem(pitch);
        for (int i = 0; i < 20; i++)
            exec.UpdateLocomotionAndContacts(new[] { intended, bystander }, ball, new SeededRandom(1), dt,
                receptionAssist: assist);

        return new
        {
            intendedZ = Math.Round(intended.Position.Z, 3),
            bystanderZ = Math.Round(bystander.Position.Z, 3)
        };
    }

    output["reception_assist_scope"] = new
    {
        noPass = RunAssist(false),
        incomingPass = RunAssist(true),
        humanReceiverRunningAway = RunAssist(true, humanReceiverId: 9)
    };
}

// TEMP health check: same model, several seeds, short samples.
{
    var health = new List<object>();
    foreach (ulong seed in new ulong[] { 20260818UL, 31337UL, 777UL, 4242UL })
    {
        var sim = new MatchSimulationEngine(new PreMatchConfig { Seed = seed });
        for (int t = 0; t < 6000; t++) sim.StepTick();
        health.Add(new
        {
            seed,
            goals = sim.Analytics.HomeGoals + "-" + sim.Analytics.AwayGoals,
            passes = sim.Analytics.HomePassesAttempted + sim.Analytics.AwayPassesAttempted,
            passCompletions = sim.Analytics.HomePassesCompleted + sim.Analytics.AwayPassesCompleted,
            shots = sim.Analytics.HomeShots + sim.Analytics.AwayShots,
            possessionHome = Math.Round(sim.Analytics.HomePossessionShare, 3),
            controlledPossessionHome = Math.Round(sim.Analytics.HomeControlledPossessionShare, 3),
            homeTerritoryX = Math.Round(sim.Analytics.HomeAverageBallTerritoryX, 1),
            awayTerritoryX = Math.Round(sim.Analytics.AwayAverageBallTerritoryX, 1)
        });
    }
    output["match_health_multi_seed"] = health;
}

// Off-ball jobs (F04): coordinated possession jobs, the stationary carrier's open options, and the
// defensive response to a turnover.
{
    var players = new List<FootballerState>();
    void AddP(int id, TeamId team, TacticalRole role, float x, float z)
    {
        var p = Player(id, team, role, new(x, 0, z));
        p.TacticalAnchor = new Vector3(x, 0, z);
        players.Add(p);
    }

    AddP(1, TeamId.Home, TacticalRole.Goalkeeper, -50, 0);
    AddP(2, TeamId.Home, TacticalRole.FullBack, -8, 18);
    AddP(3, TeamId.Home, TacticalRole.FullBack, -8, -18);
    AddP(4, TeamId.Home, TacticalRole.CenterBack, -20, 6);
    AddP(5, TeamId.Home, TacticalRole.CenterBack, -20, -6);
    AddP(6, TeamId.Home, TacticalRole.DefensiveMidfielder, -6, 2);
    AddP(7, TeamId.Home, TacticalRole.Winger, 10, 26);
    AddP(8, TeamId.Home, TacticalRole.CentralMidfielder, 0, 10);
    AddP(9, TeamId.Home, TacticalRole.Striker, 18, 0);
    AddP(10, TeamId.Home, TacticalRole.CentralMidfielder, 6, -12);
    AddP(11, TeamId.Home, TacticalRole.Winger, 10, -26);

    AddP(12, TeamId.Away, TacticalRole.Goalkeeper, 50, 0);
    AddP(13, TeamId.Away, TacticalRole.CenterBack, 16, 6);
    AddP(14, TeamId.Away, TacticalRole.CenterBack, 20, -6);
    AddP(15, TeamId.Away, TacticalRole.DefensiveMidfielder, 12, 8);
    AddP(16, TeamId.Away, TacticalRole.FullBack, 26, 20);
    AddP(17, TeamId.Away, TacticalRole.FullBack, 26, -20);
    AddP(18, TeamId.Away, TacticalRole.CentralMidfielder, 14, -4);
    AddP(19, TeamId.Away, TacticalRole.Winger, 30, 18);
    AddP(20, TeamId.Away, TacticalRole.Winger, 30, -18);
    AddP(21, TeamId.Away, TacticalRole.Striker, 34, 0);
    AddP(22, TeamId.Away, TacticalRole.CentralMidfielder, 22, 0);

    var ball = new BallState { Position = new(0, .11f, 10), LastTouchedByTeam = TeamId.Home, LastTouchedByPlayerId = 8 };
    var homePossession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, 8, null);
    var jobs = new TeamJobCoordinator(pitch);
    // The probe is a labelled diagnostic, so its job assignments are allowed to read the truth view
    // (A2.6); the match itself passes a team belief here.
    var homeFormation = new TeamFormation(TeamId.Home, pitch);
    homeFormation.AssignFromPositions(players, 1f);
    var awayFormation = new TeamFormation(TeamId.Away, pitch);
    awayFormation.AssignFromPositions(players, -1f);
    jobs.Update(players, ball, homePossession, TeamId.Home, null, 15, 1f, 0,
        TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
        TeamBelief.FromTruth(TeamId.Home, players), TeamBelief.FromTruth(TeamId.Away, players),
        homeFormation, awayFormation);

    var carrier = players.Single(p => p.Id == 8);
    Vector2 carrierXZ = new(carrier.Position.X, carrier.Position.Z);
    var carrierObs = Observe(carrier, players, ball, possession: homePossession);
    int openOptions = 0;
    var homeJobs = new List<object>();
    foreach (var p in players.Where(p => p.Team == TeamId.Home && p.Role != TacticalRole.Goalkeeper))
    {
        var assignment = jobs.Get(p.Id);
        if (assignment is not { } job) continue;
        homeJobs.Add(new { p.Id, job = job.Job.ToString(), anchor = new { x = Math.Round(job.Anchor.X, 1), z = Math.Round(job.Anchor.Z, 1) } });

        Vector2 targetXZ = new(job.Anchor.X, job.Anchor.Z);
        float distance = Vector2.Distance(carrierXZ, targetXZ);
        if (distance is < 5f or > 35f) continue;
        if (TacticalUtilityAiController.EvaluateLaneClearance(carrierXZ, targetXZ, carrierObs.VisiblePlayers, TeamId.Home) < 0.5f) continue;
        openOptions++;
    }

    output["jobs_in_possession"] = new { jobs = homeJobs, openOptions };

    // Turnover: Away #15 wins the ball at (4,8); Home responds with press + cover.
    var awayCarrier = players.Single(p => p.Id == 15);
    awayCarrier.Position = new Vector3(4, 0, 8);
    var turnoverBall = new BallState { Position = new(4, .11f, 8), LastTouchedByTeam = TeamId.Away, LastTouchedByPlayerId = 15 };
    var awayPossession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Away, 15, null);
    jobs.Update(players, turnoverBall, awayPossession, TeamId.Away, 6, null, 1f, 60,
        TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
        TeamBelief.FromTruth(TeamId.Home, players), TeamBelief.FromTruth(TeamId.Away, players),
        homeFormation, awayFormation);

    object TeamJobsDiag(TeamId team) => players
        .Where(p => p.Team == team && p.Role != TacticalRole.Goalkeeper)
        .Select(p => new { p.Id, job = jobs.Get(p.Id)?.Job.ToString(), anchorX = Math.Round(jobs.Get(p.Id)?.Anchor.X ?? 0f, 1) })
        .ToArray();

    output["jobs_turnover"] = new { home = TeamJobsDiag(TeamId.Home), away = TeamJobsDiag(TeamId.Away) };
}

// Human shot aiming (audit F16): aim a shot at each corner with the right stick and with a cursor
// point, then measure where the ball actually crosses the goal line.
{
    object ShotResult(float? stickZ, float? cursorZ)
    {
        var shooter = Player(9, TeamId.Home, TacticalRole.Striker, new(40, 0, 0));
        var ball = new BallState
        {
            Position = new(40.4f, .11f, 0),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 9
        };
        var human = new HumanInputController(9);
        if (stickZ.HasValue) human.MoveStickXZ = new Vector2(1f, stickZ.Value);
        if (cursorZ.HasValue) human.AimPointXZ = new Vector2(52f, cursorZ.Value);

        human.ReleaseChargedKick(KickIntentType.Shot, 0.8f);
        var obs = Observe(shooter, new[] { shooter }, ball);
        var command = human.Decide(obs, dt);
        shooter.LastCommand = command;

        new FootballerExecutionSystem(pitch).UpdateLocomotionAndContacts(
            new[] { shooter }, ball, new SeededRandom(1), dt);

        var solver = new BallPhysicsSolver();
        float crossingZ = float.NaN;
        float crossingY = float.NaN;
        for (int i = 1; i <= 180; i++)
        {
            solver.Step(ball, dt);
            if (float.IsNaN(crossingZ) && ball.Position.X >= pitch.HalfLength)
            {
                crossingZ = ball.Position.Z;
                crossingY = ball.Position.Y;
            }
        }

        return new
        {
            aimX = Math.Round(command.KickAimDirectionXZ.X, 3),
            aimZ = Math.Round(command.KickAimDirectionXZ.Z, 3),
            crossingZ = double.IsNaN(crossingZ) ? (double?)null : Math.Round(crossingZ, 2),
            crossingY = double.IsNaN(crossingY) ? (double?)null : Math.Round(crossingY, 2),
            onTarget = !double.IsNaN(crossingZ) && MathF.Abs(crossingZ) <= pitch.HalfGoalWidth
        };
    }

    output["human_shot_aim"] = new
    {
        moveLeft = ShotResult(-1f, null),
        moveRight = ShotResult(1f, null),
        cursorLeft = ShotResult(null, -3f),
        cursorRight = ShotResult(null, 3f)
    };
}

// Shooting model (audit follow-up): power vs height, finesse/chip/trivela and the foot strike.
{
    object ShotProbe(float power, KickIntentType type, float cornerZ, float facingZ = 0f, float startX = 36f)
    {
        var shooter = Player(9, TeamId.Home, TacticalRole.Striker, new(startX, 0, 0));
        shooter.FacingXZ = SimMath.SafeNormalize(new Vector2(1f, facingZ), new Vector2(1f, 0f));
        var ball = new BallState
        {
            Position = new(startX + 0.4f, .11f, 0),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 9
        };

        Vector2 aim = SimMath.SafeNormalize(
            new Vector2(pitch.HalfLength, cornerZ) - new Vector2(shooter.Position.X, shooter.Position.Z),
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

        var exec = new FootballerExecutionSystem(pitch);
        exec.UpdateLocomotionAndContacts(new[] { shooter }, ball, new SeededRandom(1), dt);
        object initialVelocity = Vec(ball.Velocity);
        object initialSpin = Vec(ball.AngularVelocity);

        var solver = new BallPhysicsSolver();
        double crossingY = double.NaN;
        double crossingZ = double.NaN;
        float maxOffLine = 0f;
        float maxY = 0f;
        Vector2 origin = new(shooter.Position.X, shooter.Position.Z);
        bool crossed = false;
        for (int i = 1; i <= 240 && !crossed; i++)
        {
            solver.Step(ball, dt);
            Vector2 ballXZ = new(ball.Position.X, ball.Position.Z);
            float along = Vector2.Dot(ballXZ - origin, aim);
            Vector2 onLine = origin + aim * MathF.Max(0f, along);
            maxOffLine = MathF.Max(maxOffLine, Vector2.Distance(ballXZ, onLine));
            maxY = MathF.Max(maxY, ball.Position.Y);

            if (ball.Position.X >= pitch.HalfLength)
            {
                crossingY = ball.Position.Y;
                crossingZ = ball.Position.Z;
                crossed = true;
            }
        }

        return new
        {
            type = type.ToString(),
            power = Math.Round(power, 2),
            crossingY = double.IsNaN(crossingY) ? (double?)null : Math.Round(crossingY, 2),
            crossingZ = double.IsNaN(crossingZ) ? (double?)null : Math.Round(crossingZ, 2),
            maxOffLine = Math.Round(maxOffLine, 2),
            maxY = Math.Round(maxY, 2),
            initialVelocity,
            initialSpin,
            finalBall = Vec(ball.Position),
            onTarget = !double.IsNaN(crossingY) && crossingY <= pitch.GoalHeight
                       && Math.Abs(crossingZ) <= pitch.HalfGoalWidth,
            execution = shooter.LastExecutionDiagnostic
        };
    }

    output["shot_power_height"] = new[] { 0.30f, 0.60f, 0.75f, 0.85f, 0.90f, 0.96f, 1.00f }
        .Select(power => ShotProbe(power, KickIntentType.Shot, 0f)).ToArray();

    output["shot_types"] = new
    {
        finesse = ShotProbe(0.85f, KickIntentType.CurledShot, 3.2f),
        chip = ShotProbe(0.85f, KickIntentType.ChipShot, 0f),
        trivela = ShotProbe(0.85f, KickIntentType.TrivelaShot, 2.0f),
        trivelaCentre = ShotProbe(0.85f, KickIntentType.TrivelaShot, 0f)
    };

    // A pronounced body angle: aiming to the near post is the strong-foot side, the far post is
    // across the body (outside foot / weak foot), which is less accurate.
    output["shot_foot"] = new
    {
        strongSide = ShotProbe(0.85f, KickIntentType.Shot, 12.0f),
        acrossBody = ShotProbe(0.85f, KickIntentType.Shot, -12.0f)
    };
}

// Authoritative execution metrics (audit F17): an attempt exists only when execution played the
// ball, every kick intent is classified, and each refusal says why.
{
    object Metric(KickIntentType intent, float distance, float ballY = .11f, float recovery = 0f,
        bool opponentLastTouch = false)
    {
        var player = Player(9, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        player.FacingXZ = new(1f, 0f);
        player.KickRecoveryTimer = recovery;
        var ball = new BallState
        {
            Position = new(distance, ballY, 0),
            LastTouchedByTeam = opponentLastTouch ? TeamId.Away : TeamId.Home,
            LastTouchedByPlayerId = opponentLastTouch ? 12 : 9
        };
        Vector2 aim = new(1f, 0f);
        player.LastCommand = new FootballerCommand(aim, aim, false, false, false, false, false, Vector3.Zero,
            intent, new Vector3(1f, 0f, 0f), 0.6f, 0f);
        var analytics = new MatchAnalyticsSummary();
        var stats = new MatchStatisticsTracker(analytics);
        var exec = new FootballerExecutionSystem(pitch);
        exec.UpdateLocomotionAndContacts(new[] { player }, ball, new SeededRandom(7), dt, tick: 1);
        stats.ApplyContacts(exec.ContactEvents, 1);

        return new
        {
            type = intent.ToString(),
            contactEvents = exec.ContactEvents.Count,
            rejection = exec.ContactAttempts.Count > 0 ? exec.ContactAttempts[0].Reason.ToString() : null,
            passAttempts = analytics.HomePassesAttempted,
            passCompletions = analytics.HomePassesCompleted,
            shots = analytics.HomeShots
        };
    }

    object LostArbitration()
    {
        var winner = Player(8, TeamId.Home, TacticalRole.Striker, new(-.15f, 0, 0));
        winner.FacingXZ = new(1f, 0f);
        winner.LastCommand = FootballerCommand.Idle;
        var loser = Player(9, TeamId.Home, TacticalRole.CentralMidfielder, new(.9f, 0, 0));
        loser.FacingXZ = new(-1f, 0f);
        loser.LastCommand = new FootballerCommand(new(1f, 0f), new(1f, 0f), false, false, false, false, false,
            Vector3.Zero, KickIntentType.GroundPass, new Vector3(1f, 0f, 0f), 0.6f, 0f);
        var ball = new BallState
        {
            Position = new(0, .11f, 0),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 9
        };
        var exec = new FootballerExecutionSystem(pitch);
        exec.UpdateLocomotionAndContacts(new[] { winner, loser }, ball, new SeededRandom(7), dt, tick: 1);
        return new
        {
            loser = exec.ContactAttempts
                .Where(a => a.PlayerId == 9)
                .Select(a => new { executed = a.Executed, rejection = a.Reason.ToString() })
                .ToArray(),
            contact = exec.ContactEvents.Select(c => new { kind = c.Kind.ToString(), c.PlayerId }).ToArray()
        };
    }

    output["execution_metrics"] = new
    {
        // Every kick intent, played once from 0.3 m: exactly the pass types become pass attempts.
        kickTypes = new[]
        {
            KickIntentType.GroundPass, KickIntentType.LoftedPass, KickIntentType.ThroughPass,
            KickIntentType.LobbedThroughPass, KickIntentType.DrivenPass, KickIntentType.Cross,
            KickIntentType.Shot, KickIntentType.CurledShot, KickIntentType.ChipShot,
            KickIntentType.TrivelaShot, KickIntentType.Header, KickIntentType.Clearance
        }.Select(t => Metric(t, .3f)).ToArray(),
        outOfReach = Metric(KickIntentType.GroundPass, 2.0f),
        facingAway = Metric(KickIntentType.GroundPass, -.3f, opponentLastTouch: true),
        aerialBall = Metric(KickIntentType.Shot, .5f, ballY: 3.0f),
        inRecovery = Metric(KickIntentType.GroundPass, .3f, recovery: 0.2f),
        lostArbitration = LostArbitration()
    };
}

// Advertised behaviour fields (audit F18): one-parameter A/B measurements for every field the audit
// called inert, plus the pre-contact alignment gate on explicit kicks.
{
    PlayerTendencies Tend(float risk = 0.55f, float direct = 0.55f, float shooting = 0.60f,
        float dribble = 0.50f, float discipline = 0.70f, float pressing = 0.60f) => new()
    {
        RiskTolerance01 = risk,
        DirectPassPreference01 = direct,
        ShootingAppetite01 = shooting,
        DribblePreference01 = dribble,
        PositionalDiscipline01 = discipline,
        PressingAggression01 = pressing
    };

    FootballerState P(int id, TeamId team, TacticalRole role, float x, float z,
        PlayerTendencies? tendencies = null, float reactionDelay = 0.08f) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Position = new(x, 0, z),
        TacticalAnchor = new(x, 0, z),
        Attributes = new PlayerAttributes { ReactionDelaySeconds = reactionDelay },
        Tendencies = tendencies ?? new PlayerTendencies()
    };

    string BestAction(FootballerState self, IReadOnlyList<FootballerState> players, BallState ball,
        BallPossessionSnapshot? possession = null)
    {
        var ai = new TacticalUtilityAiController(pitch);
        ai.Decide(Observe(self, players, ball, possession: possession, isDesignatedBallResponder: true), dt);
        return ai.LastCandidates.Count == 0 ? "none" : ai.LastCandidates[0].ActionName;
    }

    float PassUtility(PlayerTendencies tendencies, TeamTacticalProfile? profile = null)
    {
        var passer = P(9, TeamId.Home, TacticalRole.CentralMidfielder, 0, 0, tendencies);
        var mate = P(8, TeamId.Home, TacticalRole.Striker, 15, 3);
        var blocker = P(12, TeamId.Away, TacticalRole.CenterBack, 7, -0.2f);
        var ball = new BallState
        {
            Position = new(0.3f, .11f, 0),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 9
        };

        var ai = new TacticalUtilityAiController(pitch);
        FootballerObservation obs = Observe(passer, new[] { passer, mate, blocker }, ball, profile: profile);
        for (int i = 0; i < 30; i++) ai.Decide(obs, dt);
        var pass = ai.LastCandidates.FirstOrDefault(c => c.ActionName.StartsWith("Pass(", StringComparison.Ordinal));
        return pass.ActionName is null ? float.NaN : pass.UtilityScore;
    }

    float ShotUtility(float shootingAppetite)
    {
        var self = P(9, TeamId.Home, TacticalRole.Striker, 35, 0, Tend(shooting: shootingAppetite));
        var ball = new BallState
        {
            Position = new(35.3f, .11f, 0),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 9
        };
        var ai = new TacticalUtilityAiController(pitch);
        ai.Decide(Observe(self, new[] { self }, ball), dt);
        return ai.LastCandidates.Single(c => c.ActionName == "ShootAtGoal").UtilityScore;
    }

    float CarryUtility(float dribblePreference)
    {
        var self = P(9, TeamId.Home, TacticalRole.Winger, 10, 10, Tend(dribble: dribblePreference));
        var ball = new BallState
        {
            Position = new(10.3f, .11f, 10),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 9
        };
        var ai = new TacticalUtilityAiController(pitch);
        ai.Decide(Observe(self, new[] { self }, ball), dt);
        return ai.LastCandidates.Single(c => c.ActionName == "DriveWithBall").UtilityScore;
    }

    int TempoReleaseTick(float tempo)
    {
        var self = P(9, TeamId.Home, TacticalRole.CentralMidfielder, 0, 0);
        var mate = P(8, TeamId.Home, TacticalRole.Striker, 15, 3);
        var ball = new BallState
        {
            Position = new(0.3f, .11f, 0),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 9
        };
        var profile = new TeamTacticalProfile { Tempo01 = tempo };
        var ai = new TacticalUtilityAiController(pitch);
        FootballerObservation obs = Observe(self, new[] { self, mate }, ball, profile: profile);
        for (int tick = 1; tick <= 60; tick++)
        {
            ai.Decide(obs, dt);
            if (ai.LastCandidates.Any(c => c.ActionName.StartsWith("Pass(", StringComparison.Ordinal)))
                return tick;
        }
        return -1;
    }

    float SupportDrift(float discipline, float freedom)
    {
        var self = P(6, TeamId.Home, TacticalRole.CenterBack, -10, 0, Tend(discipline: discipline));
        var mates = new List<FootballerState> { self };
        for (int i = 0; i < 4; i++)
            mates.Add(P(7 + i, TeamId.Home, TacticalRole.CentralMidfielder, -10 + 0.8f + i * 1.6f, 0));

        var ball = new BallState
        {
            Position = new(-8.2f, .11f, 0),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 8
        };
        var profile = new TeamTacticalProfile { PositionalFreedom01 = freedom };
        var ai = new TacticalUtilityAiController(pitch);
        ai.Decide(Observe(
            self, mates, ball,
            possession: new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, 8, null),
            isDesignatedBallResponder: false,
            profile: profile), dt);

        UtilityCandidate shape = ai.LastCandidates.First(c =>
            c.ActionName is "OnsideSupportLane" or "HoldDefensiveZone");
        Vector3 target = shape.TargetXZ ?? self.Position;
        return Vector2.Distance(
            new Vector2(self.TacticalAnchor.X, self.TacticalAnchor.Z),
            new Vector2(target.X, target.Z));
    }

    int ShieldWinner(bool shield)
    {
        var holder = P(9, TeamId.Home, TacticalRole.Striker, 0.7f, 0);
        holder.FacingXZ = new(-1f, 0);
        holder.LastCommand = FootballerCommand.Idle with { ShieldBall = shield };
        var opponent = P(10, TeamId.Away, TacticalRole.CenterBack, -0.15f, 0);
        opponent.FacingXZ = new(1f, 0);
        opponent.LastCommand = FootballerCommand.Idle;
        var ball = new BallState
        {
            Position = new(0, .11f, 0),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 9
        };
        var exec = new FootballerExecutionSystem(pitch);
        exec.UpdateLocomotionAndContacts(new[] { holder, opponent }, ball, new SeededRandom(11), dt, tick: 1);
        return exec.ContactEvents.Count == 0 ? -1 : exec.ContactEvents[0].PlayerId;
    }

    object ReactionDelay(float reactionDelay)
    {
        var self = P(9, TeamId.Home, TacticalRole.Striker, 0, 0, reactionDelay: reactionDelay);
        var perception = new PerceptionSystem();
        var ball = new BallState();
        var observations = new Dictionary<int, FootballerObservation>();

        // 12 m/s straight for 20 ticks, then a sudden reversal (a deflection).
        (Vector3 Position, Vector3 Velocity) BallAt(int tick) => tick <= 20
            ? (new Vector3(tick * 0.2f, .11f, 0), new Vector3(12f, 0f, 0f))
            : (new Vector3((40 - tick) * 0.2f, .11f, 0), new Vector3(-12f, 0f, 0f));

        for (int t = 0; t <= 28; t++)
        {
            (ball.Position, ball.Velocity) = BallAt(t);
            observations[t] = perception.BuildObservation(self, new[] { self }, ball, TeamId.Home,
                MatchPhase.FirstHalf, t * dt, 1f, false,
                TeamTacticalProfile.PossessionControl, new SeededRandom(1));
        }

        float ErrorAt(int tick) => MathF.Abs(observations[tick].PerceivedBallPosition.X - BallAt(tick).Position.X);
        return new
        {
            reactionDelay,
            steadyErrorMeters = Math.Round(ErrorAt(20), 3),
            errorThreeTicksAfterReversal = Math.Round(ErrorAt(23), 3),
            errorEightTicksAfterReversal = Math.Round(ErrorAt(28), 3)
        };
    }

    object Alignment(float facingX)
    {
        var shooter = P(9, TeamId.Home, TacticalRole.Striker, 40, 0);
        shooter.FacingXZ = new(facingX, 0);
        var ball = new BallState
        {
            Position = new(40.4f, .11f, 0),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 9
        };
        Vector2 aim = new(1f, 0);
        shooter.LastCommand = new FootballerCommand(
            aim, aim, false, false, false, false, false, Vector3.Zero,
            KickIntentType.Shot, new Vector3(1f, 0f, 0f), 0.85f, 0f);
        new FootballerExecutionSystem(pitch).UpdateLocomotionAndContacts(
            new[] { shooter }, ball, new SeededRandom(3), dt, tick: 1);
        Vector2 launch = SimMath.SafeNormalize(new Vector2(ball.Velocity.X, ball.Velocity.Z), aim);
        return new
        {
            facingX,
            diagnostic = shooter.LastExecutionDiagnostic,
            deviationRad = Math.Round(MathF.Acos(Math.Clamp(Vector2.Dot(launch, aim), -1f, 1f)), 4)
        };
    }

    object ShotPower(float maxPower)
    {
        var shooter = P(9, TeamId.Home, TacticalRole.Striker, 36, 0);
        shooter = new FootballerState
        {
            Id = shooter.Id,
            Team = shooter.Team,
            ShirtNumber = shooter.ShirtNumber,
            Role = shooter.Role,
            Position = shooter.Position,
            FacingXZ = new(1f, 0f),
            Attributes = new PlayerAttributes { ShotPowerMax = maxPower },
            Tendencies = shooter.Tendencies
        };
        var ball = new BallState
        {
            Position = new(36.4f, .11f, 0),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 9
        };
        Vector2 aim = new(1f, 0);
        shooter.LastCommand = new FootballerCommand(
            aim, aim, false, false, false, false, false, Vector3.Zero,
            KickIntentType.Shot, new Vector3(1f, 0f, 0f), 0.85f, 0f);
        new FootballerExecutionSystem(pitch).UpdateLocomotionAndContacts(
            new[] { shooter }, ball, new SeededRandom(5), dt, tick: 1);

        float speed = new Vector2(ball.Velocity.X, ball.Velocity.Z).Length();
        var solver = new BallPhysicsSolver();
        float crossingY = float.NaN;
        for (int i = 0; i < 240 && float.IsNaN(crossingY); i++)
        {
            solver.Step(ball, dt);
            if (ball.Position.X >= pitch.HalfLength) crossingY = ball.Position.Y;
        }

        return new
        {
            maxPower,
            horizontalSpeed = Math.Round(speed, 2),
            crossingY = double.IsNaN(crossingY) ? (double?)null : Math.Round(crossingY, 2)
        };
    }

    var blockerFree = P(12, TeamId.Away, TacticalRole.Striker, 1.30f, 0);
    output["behaviour_fields"] = new
    {
        shielding = new
        {
            withoutShieldContact = ShieldWinner(false),
            withShieldContact = ShieldWinner(true)
        },
        shootingAppetite = new
        {
            low = Math.Round(ShotUtility(0.20f), 3),
            high = Math.Round(ShotUtility(0.95f), 3)
        },
        riskTolerance = new
        {
            playerLow = Math.Round(PassUtility(Tend(risk: 0.15f)), 3),
            playerHigh = Math.Round(PassUtility(Tend(risk: 0.95f)), 3),
            teamLow = Math.Round(PassUtility(Tend(), new TeamTacticalProfile { RiskTolerance01 = 0.10f }), 3),
            teamHigh = Math.Round(PassUtility(Tend(), new TeamTacticalProfile { RiskTolerance01 = 0.90f }), 3)
        },
        directPassPreference = new
        {
            low = Math.Round(PassUtility(Tend(direct: 0.15f)), 3),
            high = Math.Round(PassUtility(Tend(direct: 0.95f)), 3)
        },
        dribblePreference = new
        {
            low = Math.Round(CarryUtility(0.10f), 3),
            high = Math.Round(CarryUtility(0.95f), 3)
        },
        pressingAggression = new
        {
            low = BestAction(
                P(4, TeamId.Home, TacticalRole.CenterBack, 0, 0, Tend(pressing: 0.20f)),
                new[] { P(4, TeamId.Home, TacticalRole.CenterBack, 0, 0, Tend(pressing: 0.20f)), blockerFree },
                new BallState
                {
                    Position = new(1.30f, .11f, 0),
                    LastTouchedByTeam = TeamId.Away,
                    LastTouchedByPlayerId = 12
                },
                new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Away, 12, null)),
            high = BestAction(
                P(4, TeamId.Home, TacticalRole.CenterBack, 0, 0, Tend(pressing: 1.00f)),
                new[] { P(4, TeamId.Home, TacticalRole.CenterBack, 0, 0, Tend(pressing: 1.00f)), blockerFree },
                new BallState
                {
                    Position = new(1.30f, .11f, 0),
                    LastTouchedByTeam = TeamId.Away,
                    LastTouchedByPlayerId = 12
                },
                new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Away, 12, null))
        },
        positionalDriftMeters = new
        {
            undisciplined = Math.Round(SupportDrift(0.15f, 0.55f), 2),
            disciplined = Math.Round(SupportDrift(0.95f, 0.55f), 2),
            freeRole = Math.Round(SupportDrift(0.70f, 0.95f), 2),
            rigidShape = Math.Round(SupportDrift(0.70f, 0.15f), 2)
        },
        tempoReleaseTick = new
        {
            lowTempo = TempoReleaseTick(0.20f),
            highTempo = TempoReleaseTick(0.90f)
        },
        reactionDelay = new
        {
            instant = ReactionDelay(0f),
            normal = ReactionDelay(0.08f),
            slow = ReactionDelay(0.20f)
        },
        kickAlignment = new
        {
            facingTheAim = Alignment(1f),
            facingAway = Alignment(-1f)
        },
        shotPowerMax = new
        {
            strong = ShotPower(31f),
            weak = ShotPower(24f)
        }
    };
}

// Offside restart/contact semantics (audit F14). Law 11: a player cannot be offside when the ball
// comes directly from a throw-in, goal kick or corner kick; a deliberate save does not remove an
// existing offside restriction; and an offside free kick is indirect (it needs a second touch).
{
    object ExemptRestart(MatchPhase restartPhase)
    {
        var rules = new MatchRulesEngine(pitch);
        var midfielder = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        var runner = Player(9, TeamId.Home, TacticalRole.Striker, new(30f, 0f, 0f));
        var winger = Player(11, TeamId.Home, TacticalRole.Winger, new(46f, 0f, 20f));
        var keeper = Player(12, TeamId.Away, TacticalRole.Goalkeeper, new(20f, 0f, 0f));
        var centreBack = Player(13, TeamId.Away, TacticalRole.CenterBack, new(10f, 0f, 0f));
        var players = new[] { midfielder, runner, winger, keeper, centreBack };
        var ball = new BallState();
        rules.StartMatch(players, ball);

        ball.LastTouchedByPlayerId = 12;
        ball.LastTouchedByTeam = TeamId.Away;
        ball.Position = restartPhase switch
        {
            MatchPhase.ThrowIn => new Vector3(10f, 0.11f, 35f),
            MatchPhase.GoalKick => new Vector3(-53f, 0.11f, 20f),
            _ => new Vector3(53f, 0.11f, 20f)
        };
        rules.StepRules(players, ball, 2700f, dt);
        RestartKind restartKind = rules.ActiveRestart;
        rules.StepRules(players, ball, 2700f, 0.25f);

        var taker = players.First(p => p.Id == rules.DesignatedSetPieceTakerId!.Value);
        runner.Position = restartPhase == MatchPhase.CornerKick
            ? new Vector3(rules.RestartSpot.X + 0.2f, 0f, 30f)
            : new Vector3(30f, 0f, 0f);
        winger.Position = new Vector3(46f, 0f, 20f);
        keeper.Position = new Vector3(20f, 0f, 0f);
        centreBack.Position = new Vector3(10f, 0f, 0f);
        taker.Position = new Vector3(rules.RestartSpot.X - 0.45f, 0f, rules.RestartSpot.Z);
        ball.Position = rules.RestartSpot;
        ball.Velocity = new Vector3(12f, 0f, 0f);
        ball.LastTouchedByPlayerId = taker.Id;
        ball.LastTouchedByTeam = taker.Team;

        var contacts = new List<BallContactEvent>
        {
            new(1, BallContactKind.Kick, taker.Id, taker.Team, ball.Position) { Intent = KickIntentType.GroundPass }
        };
        rules.StampRestartOrigin(contacts);
        rules.StepRules(players, ball, 2700f, dt, contacts);

        // The intended receiver plays the ball on directly from the restart.
        ball.Position = new Vector3(runner.Position.X, 0.11f, runner.Position.Z);
        ball.LastTouchedByPlayerId = runner.Id;
        ball.LastTouchedByTeam = TeamId.Home;
        rules.StepRules(players, ball, 2700f, dt,
            new[] { new BallContactEvent(2, BallContactKind.Collection, runner.Id, TeamId.Home, ball.Position) });

        return new
        {
            restart = restartKind.ToString(),
            stampedContact = contacts[0].RestartOrigin.ToString(),
            receiverX = Math.Round(runner.Position.X, 2),
            ballLineX = Math.Round(rules.RestartSpot.X, 2),
            secondLastDefenderX = centreBack.Position.X,
            phaseAfterReception = rules.Phase.ToString(),
            offsideAwarded = rules.Events.Any(e => e.Details.Contains("Offside")),
            lastEvent = rules.Events[^1].Details
        };
    }

    output["offside_exempt_restarts"] = new
    {
        throwIn = ExemptRestart(MatchPhase.ThrowIn),
        goalKick = ExemptRestart(MatchPhase.GoalKick),
        cornerKick = ExemptRestart(MatchPhase.CornerKick)
    };

    // A deliberate defensive play removes the offside restriction; a save/parry does not.
    object AfterOpponentTouch(BallContactKind kind)
    {
        var rules = new MatchRulesEngine(pitch);
        var passer = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        var striker = Player(9, TeamId.Home, TacticalRole.Striker, new(30f, 0f, 0f));
        var keeper = Player(12, TeamId.Away, TacticalRole.Goalkeeper, new(20f, 0f, 0f));
        var centreBack = Player(13, TeamId.Away, TacticalRole.CenterBack, new(10f, 0f, 0f));
        var players = new[] { passer, striker, keeper, centreBack };
        var ball = new BallState();
        rules.StartMatch(players, ball);
        striker.Position = new Vector3(30f, 0f, 0f);
        keeper.Position = new Vector3(20f, 0f, 0f);
        centreBack.Position = new Vector3(10f, 0f, 0f);
        ball.Position = new Vector3(0f, 0.11f, 0f);
        ball.LastTouchedByTeam = TeamId.Home;

        rules.StepRules(players, ball, 2700f, dt,
            new[] { new BallContactEvent(1, BallContactKind.Kick, 8, TeamId.Home, ball.Position) });

        int opponentId = kind == BallContactKind.Save ? 12 : 13;
        ball.LastTouchedByPlayerId = opponentId;
        ball.LastTouchedByTeam = TeamId.Away;
        rules.StepRules(players, ball, 2700f, dt,
            new[] { new BallContactEvent(2, kind, opponentId, TeamId.Away, ball.Position) { IsParry = kind == BallContactKind.Save } });

        ball.Position = new Vector3(30f, 0.11f, 0f);
        ball.LastTouchedByPlayerId = 9;
        ball.LastTouchedByTeam = TeamId.Home;
        rules.StepRules(players, ball, 2700f, dt,
            new[] { new BallContactEvent(3, BallContactKind.Collection, 9, TeamId.Home, ball.Position) });

        return new
        {
            opponentTouch = kind.ToString(),
            phaseAfterReception = rules.Phase.ToString(),
            offsideAwarded = rules.Events.Any(e => e.Details.Contains("Offside")),
            lastEvent = rules.Events[^1].Details
        };
    }

    output["offside_deliberate_play_vs_save"] = new
    {
        deliberatePlay = AfterOpponentTouch(BallContactKind.Kick),
        save = AfterOpponentTouch(BallContactKind.Save)
    };

    // An indirect free kick cannot score without a second touch; a direct free kick for a foul can.
    object OffsideFreeKickGoal(bool secondTouch)
    {
        var rules = new MatchRulesEngine(pitch);
        var passer = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        var runner = Player(9, TeamId.Home, TacticalRole.Striker, new(30f, 0f, 0f));
        var keeper = Player(12, TeamId.Away, TacticalRole.Goalkeeper, new(20f, 0f, 0f));
        var centreBack = Player(13, TeamId.Away, TacticalRole.CenterBack, new(10f, 0f, 0f));
        var midfielder = Player(14, TeamId.Away, TacticalRole.CentralMidfielder, new(25f, 0f, 5f));
        var players = new[] { passer, runner, keeper, centreBack, midfielder };
        var ball = new BallState();
        rules.StartMatch(players, ball);
        runner.Position = new Vector3(30f, 0f, 0f);
        keeper.Position = new Vector3(20f, 0f, 0f);
        centreBack.Position = new Vector3(10f, 0f, 0f);
        ball.Position = new Vector3(0f, 0.11f, 0f);
        ball.LastTouchedByTeam = TeamId.Home;

        rules.StepRules(players, ball, 2700f, dt,
            new[] { new BallContactEvent(1, BallContactKind.Kick, 8, TeamId.Home, ball.Position) });
        ball.Position = new Vector3(30f, 0.11f, 0f);
        ball.LastTouchedByPlayerId = 9;
        rules.StepRules(players, ball, 2700f, dt,
            new[] { new BallContactEvent(2, BallContactKind.Collection, 9, TeamId.Home, ball.Position) });
        string restart = rules.ActiveRestart.ToString();
        rules.StepRules(players, ball, 2700f, 0.25f);

        var taker = players.First(p => p.Id == rules.DesignatedSetPieceTakerId!.Value);
        taker.Position = new Vector3(rules.RestartSpot.X + 0.45f, 0f, rules.RestartSpot.Z);
        ball.Position = rules.RestartSpot;
        ball.Velocity = new Vector3(-12f, 0f, 0f);
        ball.LastTouchedByPlayerId = taker.Id;
        ball.LastTouchedByTeam = taker.Team;
        var contacts = new List<BallContactEvent>
        {
            new(3, BallContactKind.Kick, taker.Id, taker.Team, ball.Position) { Intent = KickIntentType.DrivenPass }
        };
        rules.StampRestartOrigin(contacts);
        rules.StepRules(players, ball, 2700f, dt, contacts);
        bool awaitingSecondTouch = rules.AwaitingSecondTouchForGoal;

        if (secondTouch)
        {
            ball.LastTouchedByPlayerId = 14;
            rules.StepRules(players, ball, 2700f, dt,
                new[] { new BallContactEvent(4, BallContactKind.Collection, 14, TeamId.Away, ball.Position) });
        }

        ball.Position = new Vector3(-53f, 0.5f, 0f);
        rules.StepRules(players, ball, 2700f, dt);

        return new
        {
            restart,
            stampedContact = contacts[0].RestartOrigin.ToString(),
            awaitingSecondTouchAfterKick = awaitingSecondTouch,
            secondTouch,
            score = $"{rules.HomeScore}-{rules.AwayScore}",
            phaseAfterBallCrossedTheLine = rules.Phase.ToString(),
            restartTeamAfterBallCrossedTheLine = rules.RestartTeam.ToString(),
            goalAwarded = rules.Events.Any(e => e.EventType == "Goal"),
            lastEvent = rules.Events[^1].Details
        };
    }

    output["offside_indirect_restart_goal"] = new
    {
        straightIn = OffsideFreeKickGoal(secondTouch: false),
        afterSecondTouch = OffsideFreeKickGoal(secondTouch: true)
    };

    // Direct free kick for a foul: it may score without another touch.
    {
        var rules = new MatchRulesEngine(pitch);
        var passer = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        var runner = Player(9, TeamId.Home, TacticalRole.Striker, new(30f, 0f, 0f));
        var keeper = Player(12, TeamId.Away, TacticalRole.Goalkeeper, new(20f, 0f, 0f));
        var centreBack = Player(13, TeamId.Away, TacticalRole.CenterBack, new(10f, 0f, 0f));
        var midfielder = Player(14, TeamId.Away, TacticalRole.CentralMidfielder, new(-5f, 0f, 5f));
        var players = new[] { passer, runner, keeper, centreBack, midfielder };
        var ball = new BallState();
        rules.StartMatch(players, ball);
        bool restarted = rules.EvaluateTackleFoul(8, 14, new Vector3(0f, 0f, 0f), 0.80f, players, ball);
        string restart = rules.ActiveRestart.ToString();
        rules.StepRules(players, ball, 2700f, 0.25f);
        var taker = players.First(p => p.Id == rules.DesignatedSetPieceTakerId!.Value);
        ball.Position = rules.RestartSpot;
        ball.Velocity = new Vector3(-12f, 0f, 0f);
        ball.LastTouchedByPlayerId = taker.Id;
        ball.LastTouchedByTeam = taker.Team;
        var contacts = new List<BallContactEvent>
        {
            new(5, BallContactKind.Kick, taker.Id, taker.Team, ball.Position) { Intent = KickIntentType.DrivenPass }
        };
        rules.StampRestartOrigin(contacts);
        rules.StepRules(players, ball, 2700f, dt, contacts);
        bool awaitingSecondTouch = rules.AwaitingSecondTouchForGoal;
        ball.Position = new Vector3(-53f, 0.5f, 0f);
        rules.StepRules(players, ball, 2700f, dt);
        output["offside_direct_free_kick_goal"] = new
        {
            restarted,
            restart,
            stampedContact = contacts[0].RestartOrigin.ToString(),
            awaitingSecondTouchAfterKick = awaitingSecondTouch,
            score = $"{rules.HomeScore}-{rules.AwayScore}",
            goalAwarded = rules.Events.Any(e => e.EventType == "Goal")
        };
    }

    // The match engine must stamp its restarts itself (live path, not the rules engine alone).
    {
        var sim = new MatchSimulationEngine(new PreMatchConfig { Seed = 11UL });
        sim.Ball.Position = new Vector3(0f, 0.11f, 40f);
        sim.Ball.LastTouchedByTeam = TeamId.Home;
        sim.StepTick();
        string phase = sim.Rules.Phase.ToString();
        string activeRestart = sim.Rules.ActiveRestart.ToString();
        int ticksToStamp = 0;
        for (; ticksToStamp < 400 && sim.Rules.LastStampedRestartOrigin == RestartKind.None; ticksToStamp++)
            sim.StepTick();
        output["offside_engine_restart_stamp"] = new
        {
            forcedPhase = phase,
            activeRestart,
            ticksUntilFirstContact = ticksToStamp,
            stampedOnFirstContact = sim.Rules.LastStampedRestartOrigin.ToString(),
            activeRestartAfterTheKick = sim.Rules.ActiveRestart.ToString(),
            phaseAfterTheKick = sim.Rules.Phase.ToString()
        };
    }
}

// Boundary crossings and goal attribution (audit F20): the whole ball has to cross the line, the
// first crossing of the tick decides, the geometry is read at the crossing, and the scorer is not
// simply the last toucher.
{
    FootballerState[] BoundaryPlayers() => new[]
    {
        Player(8, TeamId.Home, TacticalRole.CentralMidfielder, new(20f, 0f, 0f)),
        Player(9, TeamId.Home, TacticalRole.Striker, new(40f, 0f, 0f)),
        Player(12, TeamId.Away, TacticalRole.Goalkeeper, new(47f, 0f, 0f)),
        Player(13, TeamId.Away, TacticalRole.CenterBack, new(30f, 0f, 0f))
    };

    object BoundaryCase(Vector3 start, Vector3 velocity, TeamId lastTouchTeam, int lastTouchId, int maxTicks)
    {
        var rules = new MatchRulesEngine(pitch);
        var players = BoundaryPlayers();
        var ball = new BallState();
        rules.StartMatch(players, ball);
        ball.Position = start;
        ball.PreviousPosition = start;
        ball.Velocity = velocity;
        ball.LastTouchedByTeam = lastTouchTeam;
        ball.LastTouchedByPlayerId = lastTouchId;

        var solver = new BallPhysicsSolver(null, pitch);
        var crossings = new List<object>();
        int ticks = 0;
        for (; ticks < maxTicks && rules.Phase == MatchPhase.FirstHalf; ticks++)
        {
            solver.Step(ball, dt);
            foreach (var c in ball.BoundaryCrossings)
            {
                crossings.Add(new
                {
                    boundary = c.Boundary.ToString(),
                    at = Vec(c.CrossingPosition),
                    elapsedSeconds = Math.Round(c.ElapsedSeconds, 4),
                    lineCoordinate = Math.Round(c.LineCoordinate, 3),
                    radius = c.BallRadius
                });
            }
            rules.StepRules(players, ball, 2700f, dt);
        }

        return new
        {
            ticks,
            phase = rules.Phase.ToString(),
            restartTeam = rules.RestartTeam.ToString(),
            restartSpot = Vec(rules.RestartSpot),
            score = $"{rules.HomeScore}-{rules.AwayScore}",
            crossings,
            lastEvent = rules.Events[^1].Details
        };
    }

    output["boundary_classification"] = new
    {
        // A dipping shot: above the crossbar when the whole ball crossed, below it by the tick's end.
        highShotDescending = BoundaryCase(new Vector3(52.60f, 2.80f, 0f), new Vector3(6f, -25f, 0f), TeamId.Home, 9, 5),
        // Corner exit: the touchline is crossed before the goal line inside one tick.
        diagonalCornerExit = BoundaryCase(new Vector3(52.45f, 0.11f, 33.99f), new Vector3(12f, 0f, 12f), TeamId.Away, 12, 5),
        // A shot that rebounds off the post back into play must not register any crossing.
        postRebound = BoundaryCase(new Vector3(51.9f, 0.5f, 3.3f), new Vector3(10f, 0f, 6f), TeamId.Home, 9, 10)
    };

    // Law 9: the WHOLE ball has to cross the line, so the out-of-play plane follows the ball radius.
    string WholeBall(float radius)
    {
        var rules = new MatchRulesEngine(pitch, radius);
        var players = BoundaryPlayers();
        var ball = new BallState();
        rules.StartMatch(players, ball);
        ball.Position = new Vector3(pitch.HalfLength + 0.15f, 0.5f, 0f);
        ball.LastTouchedByTeam = TeamId.Home;
        ball.LastTouchedByPlayerId = 9;
        rules.StepRules(players, ball, 2700f, dt);
        return rules.Phase.ToString();
    }

    output["boundary_whole_ball"] = new
    {
        ballCentreX = Math.Round(pitch.HalfLength + 0.15f, 2),
        defaultRadius = new { radius = 0.11f, phase = WholeBall(0.11f) },
        largeRadius = new { radius = 0.20f, phase = WholeBall(0.20f) }
    };

    // Scorer and own goal are attributed separately from the ball's physical last touch.
    object GoalAttribution(
        string label,
        (int PlayerId, TeamId Team, BallContactKind Kind, KickIntentType Intent, bool IsParry)[] touches,
        TeamId lastTouchTeam,
        int lastTouchId)
    {
        var rules = new MatchRulesEngine(pitch);
        var players = BoundaryPlayers();
        var ball = new BallState();
        rules.StartMatch(players, ball);

        int tick = 1;
        foreach (var touch in touches)
        {
            rules.StepRules(players, ball, 2700f, dt, new[]
            {
                new BallContactEvent(tick++, touch.Kind, touch.PlayerId, touch.Team, ball.Position)
                {
                    Intent = touch.Intent,
                    IsParry = touch.IsParry
                }
            });
        }

        ball.Position = new Vector3(pitch.HalfLength + 0.3f, 0.5f, 0f);
        ball.LastTouchedByTeam = lastTouchTeam;
        ball.LastTouchedByPlayerId = lastTouchId;
        rules.StepRules(players, ball, 2700f, dt);

        MatchRuleEvent? goal = rules.Events.LastOrDefault(e => e.EventType == "Goal") is { EventType: "Goal" } g ? g : null;
        return new
        {
            label,
            phase = rules.Phase.ToString(),
            score = $"{rules.HomeScore}-{rules.AwayScore}",
            lastToucher = goal?.PlayerId,
            scorer = goal?.ScorerPlayerId,
            ownGoal = goal?.OwnGoalPlayerId,
            isOwnGoal = goal?.IsOwnGoal,
            details = goal?.Details
        };
    }

    output["goal_attribution"] = new
    {
        attacker = GoalAttribution("attacker", Array.Empty<(int, TeamId, BallContactKind, KickIntentType, bool)>(), TeamId.Home, 9),
        afterSave = GoalAttribution("afterSave", new[]
        {
            (9, TeamId.Home, BallContactKind.Kick, KickIntentType.Shot, false),
            (12, TeamId.Away, BallContactKind.Save, KickIntentType.None, true)
        }, TeamId.Away, 12),
        ownGoal = GoalAttribution("ownGoal", new[]
        {
            (9, TeamId.Home, BallContactKind.Kick, KickIntentType.Shot, false),
            (13, TeamId.Away, BallContactKind.Kick, KickIntentType.Clearance, false)
        }, TeamId.Away, 13)
    };
}

// Player contact geometry (audit F22): coincident centres and vertical separation.
{
    var contactPitch = new PitchDimensions();
    object ContactCase(Vector3 aPosition, Vector3 bPosition, Vector3 aVelocity, Vector3 bVelocity)
    {
        var a = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, aPosition);
        var b = Player(9, TeamId.Home, TacticalRole.CentralMidfielder, bPosition);
        a.LastCommand = FootballerCommand.Idle;
        b.LastCommand = FootballerCommand.Idle;
        a.Velocity = aVelocity;
        b.Velocity = bVelocity;
        var ball = new BallState { Position = new Vector3(20f, 0.11f, 20f), LastTouchedByTeam = TeamId.Home };
        new FootballerExecutionSystem(contactPitch).UpdateLocomotionAndContacts(
            new[] { a, b }, ball, new SeededRandom(1), dt);

        float distance = Vector2.Distance(new(a.Position.X, a.Position.Z), new(b.Position.X, b.Position.Z));
        float horizontalClosing = -Vector2.Dot(
            new(b.Velocity.X - a.Velocity.X, b.Velocity.Z - a.Velocity.Z),
            Vector2.Normalize(new Vector2(b.Position.X - a.Position.X, b.Position.Z - a.Position.Z) + new Vector2(1e-6f, 0f)));
        return new
        {
            distanceAfter = Math.Round(distance, 3),
            horizontalClosingSpeed = Math.Round(horizontalClosing, 2),
            a = Vec(a.Position),
            b = Vec(b.Position)
        };
    }

    output["player_contacts"] = new
    {
        capsuleDiameter = Math.Round(FootballerState.CapsuleRadius * 2f, 2),
        playerHeight = FootballerState.PlayerHeight,
        coincident = ContactCase(new Vector3(5f, 0f, -3f), new Vector3(5f, 0f, -3f), Vector3.Zero, Vector3.Zero),
        airborneAboveGrounded = ContactCase(new Vector3(5f, 2.4f, -3f), new Vector3(5f, 0f, -3f), Vector3.Zero, Vector3.Zero),
        groundedOverlap = ContactCase(new Vector3(5f, 0f, -3f), new Vector3(5.2f, 0f, -3f), Vector3.Zero, Vector3.Zero),
        headOnContact = ContactCase(new Vector3(5f, 0f, -3f), new Vector3(5.6f, 0f, -3f), new(2f, 0f, 0f), new(-2f, 0f, 0f))
    };
}

// Presentation geometry (audit F22): the rendered ball must be the physical ball.
{
    var geometryEngine = new MatchSimulationEngine(new PreMatchConfig { Seed = 11UL, PlayersPerTeam = 5 });
    var geometrySession = new TrainingSession(TrainingDrillRegistry.Default);
    output["ball_geometry"] = new
    {
        previousRenderRadius = 0.16f,
        enginePhysicsRadius = geometryEngine.BallRadius,
        renderSphereRadius = BallGeometry.SphereRadius(geometryEngine.BallRadius),
        renderSphereHeight = BallGeometry.Diameter(geometryEngine.BallRadius),
        renderShadowRadius = Math.Round(BallGeometry.ShadowRadius(geometryEngine.BallRadius), 4),
        restingCentreHeight = BallGeometry.RestingCentreHeight(geometryEngine.BallRadius),
        matchBallRestY = geometryEngine.Ball.Position.Y,
        practiceRadius = geometrySession.BallRadius,
        freeTrainingSpawnRadius = FreeTrainingDrill.BallRadius,
        customRadius = new { physics = 0.20f, render = BallGeometry.SphereRadius(0.20f) }
    };
}

// Frame timing (audit F22): one wall-clock second is the same simulation at every frame rate, a long
// stall is bounded and reported, and the camera smooths by elapsed time instead of by frame count.
{
    object ClockRun(float fps)
    {
        var clock = new FixedStepClock();
        int frames = (int)MathF.Round(fps);
        double frameDelta = 1.0 / fps;
        int steps = 0;
        for (int i = 0; i < frames; i++) steps += clock.Advance(frameDelta);
        return new
        {
            fps,
            frames,
            steps,
            simulatedSeconds = Math.Round(steps * FixedStepClock.StepSeconds, 4),
            leftoverSeconds = Math.Round(clock.AccumulatorSeconds, 6),
            alpha = Math.Round(clock.Alpha, 4)
        };
    }

    Vector3 SettleCamera(float fps, bool useHalfLife, float seconds)
    {
        var position = Vector3.Zero;
        Vector3 target = new(10f, 20f, 27f);
        int frames = Math.Max(1, (int)MathF.Round(fps * seconds));
        for (int i = 0; i < frames; i++)
        {
            position = useHalfLife
                ? CameraSmoothing.Step(position, target, CameraSmoothing.MatchHalfLifeSeconds, 1f / fps)
                : Vector3.Lerp(position, target, 0.14f);
        }
        return position;
    }

    var stallClock = new FixedStepClock();
    int stalledSteps = stallClock.Advance(3.0);

    output["frame_timing"] = new
    {
        at30Fps = ClockRun(30f),
        at60Fps = ClockRun(60f),
        at144Fps = ClockRun(144f),
        stalledFrame = new
        {
            frameSeconds = 3.0,
            steps = stalledSteps,
            maxStepsPerFrame = FixedStepClock.MaxStepsPerFrame,
            droppedSeconds = Math.Round(stallClock.DroppedSeconds, 3),
            cappedFrames = stallClock.CappedFrames,
            nextFrameSteps = stallClock.Advance(SimConstants.FixedDeltaTime)
        },
        camera = new
        {
            legacyWeightPerFrame = CameraSmoothing.LegacyPerFrameWeight,
            halfLifeSeconds = Math.Round(CameraSmoothing.MatchHalfLifeSeconds, 4),
            weightAt60Hz = Math.Round(CameraSmoothing.Weight(CameraSmoothing.MatchHalfLifeSeconds, 1f / 60f), 4),
            weightAt30Hz = Math.Round(CameraSmoothing.Weight(CameraSmoothing.MatchHalfLifeSeconds, 1f / 30f), 4),
            weightAt144Hz = Math.Round(CameraSmoothing.Weight(CameraSmoothing.MatchHalfLifeSeconds, 1f / 144f), 4),
            afterOneSecondX = new
            {
                halfLife30 = Math.Round(SettleCamera(30f, true, 1f).X, 3),
                halfLife60 = Math.Round(SettleCamera(60f, true, 1f).X, 3),
                halfLife144 = Math.Round(SettleCamera(144f, true, 1f).X, 3),
                legacy30 = Math.Round(SettleCamera(30f, false, 1f).X, 3),
                legacy144 = Math.Round(SettleCamera(144f, false, 1f).X, 3)
            },
            // One 0.1 s window: the legacy rule is visibly frame-rate dependent, the new one is not.
            afterOneTenthSecondX = new
            {
                halfLife30 = Math.Round(SettleCamera(30f, true, 0.1f).X, 3),
                halfLife144 = Math.Round(SettleCamera(144f, true, 0.1f).X, 3),
                legacy30 = Math.Round(SettleCamera(30f, false, 0.1f).X, 3),
                legacy144 = Math.Round(SettleCamera(144f, false, 0.1f).X, 3)
            }
        }
    };
}

// Configuration validation (audit F22): degenerate match/pitch/attribute configuration is rejected
// up front with the offending field named instead of failing later inside the simulation.
{
    object Reject(Func<object> build)
    {
        try
        {
            build();
            return new { rejected = false, type = (string?)null, field = (string?)null, message = (string?)null };
        }
        catch (Exception ex)
        {
            return new
            {
                rejected = true,
                type = ex.GetType().Name,
                field = (ex as ArgumentOutOfRangeException)?.ParamName,
                message = ex.Message
            };
        }
    }

    object ValidateCase(Action validate)
    {
        try
        {
            validate();
            return new { valid = true, message = (string?)null };
        }
        catch (Exception ex)
        {
            return new { valid = false, message = ex.Message };
        }
    }

    output["config_validation"] = new
    {
        playersPerTeamZero = Reject(() => new MatchSimulationEngine(new PreMatchConfig { PlayersPerTeam = 0 })),
        playersPerTeamTwelve = Reject(() => new MatchSimulationEngine(new PreMatchConfig { PlayersPerTeam = 12 })),
        halfDurationZero = Reject(() => new MatchSimulationEngine(new PreMatchConfig { HalfDurationSeconds = 0f })),
        halfDurationNegative = Reject(() => new MatchSimulationEngine(new PreMatchConfig { HalfDurationSeconds = -30f })),
        halfDurationNaN = Reject(() => new MatchSimulationEngine(new PreMatchConfig { HalfDurationSeconds = float.NaN })),
        humanIdNotInMatch = Reject(() => new MatchSimulationEngine(new PreMatchConfig { DesignatedHumanPlayerId = 23 })),
        humanIdZero = Reject(() => new MatchSimulationEngine(new PreMatchConfig { DesignatedHumanPlayerId = 0 })),
        goalWiderThanPitch = Reject(() => new MatchSimulationEngine(new PreMatchConfig(), new PitchDimensions { GoalWidth = 80f })),
        pitchWidthZero = Reject(() => new MatchSimulationEngine(new PreMatchConfig(), new PitchDimensions { Width = 0f })),
        penaltyAreaTooLong = Reject(() => new MatchSimulationEngine(new PreMatchConfig(), new PitchDimensions { PenaltyAreaLength = 60f })),
        ballRadiusZero = Reject(() => new BallPhysicsSolver(new BallPhysicsParameters { Radius = 0f })),
        invalidAttributes = Reject(() =>
        {
            SimulationConfigValidation.ValidatePlayerAttributes(new PlayerAttributes { ReachRadius = -1f });
            return new object();
        }),
        invalidTeamProfile = Reject(() => new MatchSimulationEngine(new PreMatchConfig
        {
            HomeProfile = new TeamTacticalProfile { PositionalFreedom01 = 3f }
        })),
        accepted = new
        {
            defaultMatchPlayers = new MatchSimulationEngine().Players.Count,
            fiveASideWithHuman10 = new MatchSimulationEngine(
                new PreMatchConfig { PlayersPerTeam = 5, DesignatedHumanPlayerId = 10 }).LockedHumanController?.LockedPlayerId,
            defaultAttributesValid = ValidateCase(() =>
                SimulationConfigValidation.ValidatePlayerAttributes(new PlayerAttributes())),
            defaultProfileValid = ValidateCase(() =>
                SimulationConfigValidation.ValidateTacticalProfile(TeamTacticalProfile.PossessionControl))
        }
    };
}

// Inspector resolution (audit F22): the HUD must never index the squad by player id.
{
    output["inspector_index"] = new
    {
        spectator22 = MatchPositions.ResolveInspectedPlayerId(null, 22),
        human9Of22 = MatchPositions.ResolveInspectedPlayerId(9, 22),
        human9Of4 = MatchPositions.ResolveInspectedPlayerId(9, 4),
        human5Of10 = MatchPositions.ResolveInspectedPlayerId(5, 10),
        outOfRange99Of22 = MatchPositions.ResolveInspectedPlayerId(99, 22),
        previousAssumption = "Players[DesignatedHumanPlayerId - 1]"
    };
}

// Recorded-command replay (audit F23): record every decided command from a live AI match, replay it
// into a fresh engine and compare the state tick by tick.
{
    const int replayTicks = 600;
    var replayConfig = new PreMatchConfig { Seed = 24680UL, PlayersPerTeam = 5, HalfDurationSeconds = 600f };
    var recordingEngine = new MatchSimulationEngine(replayConfig);
    var recordedCommands = recordingEngine.Players.ToDictionary(
        p => p.Id, _ => new Dictionary<int, FootballerCommand>());
    var recordedStates = new List<(Vector3 Ball, Vector3[] Players, int Home, int Away)>();

    for (int t = 0; t < replayTicks; t++)
    {
        recordingEngine.StepTick();
        foreach (var p in recordingEngine.Players)
            recordedCommands[p.Id][recordingEngine.CurrentTick] = p.LastCommand;
        recordedStates.Add((
            recordingEngine.Ball.Position,
            recordingEngine.Players.Select(p => p.Position).ToArray(),
            recordingEngine.Rules.HomeScore,
            recordingEngine.Rules.AwayScore));
    }

    var replayEngine = new MatchSimulationEngine(replayConfig);
    foreach (var p in replayEngine.Players)
    {
        replayEngine.OverrideController(
            p.Id,
            new RecordedCommandController(ControllerSourceType.Scripted, recordedCommands[p.Id], () => replayEngine.CurrentTick));
    }

    int mismatchedTicks = 0;
    for (int t = 0; t < recordedStates.Count; t++)
    {
        replayEngine.StepTick();
        var state = recordedStates[t];
        bool identical = replayEngine.Ball.Position == state.Ball &&
                         replayEngine.Rules.HomeScore == state.Home &&
                         replayEngine.Rules.AwayScore == state.Away &&
                         replayEngine.Players.Select(p => p.Position).SequenceEqual(state.Players);
        if (!identical) mismatchedTicks++;
    }

    output["recorded_replay"] = new
    {
        ticks = replayTicks,
        engines = 2,
        mismatchedTicks,
        bitIdentical = mismatchedTicks == 0,
        recordedPasses = new { home = recordingEngine.Analytics.HomePassesAttempted, away = recordingEngine.Analytics.AwayPassesAttempted },
        replayedPasses = new { home = replayEngine.Analytics.HomePassesAttempted, away = replayEngine.Analytics.AwayPassesAttempted },
        recordedScore = new { home = recordingEngine.Rules.HomeScore, away = recordingEngine.Rules.AwayScore },
        replayedScore = new { home = replayEngine.Rules.HomeScore, away = replayEngine.Rules.AwayScore },
        replayedBall = Vec(replayEngine.Ball.Position)
    };
}

// Both halves and full time (audit F23): a match must reach HalfTime and FullTime, not a tick cap, and
// full time must be terminal.
{
    var completionEngine = new MatchSimulationEngine(new PreMatchConfig
    {
        Seed = 808UL,
        PlayersPerTeam = 5,
        HalfDurationSeconds = 12f
    });
    var phases = new List<string>();
    int guard = 0;
    while (completionEngine.Rules.Phase != MatchPhase.FullTime && guard++ < 20000)
    {
        completionEngine.StepTick();
        string phase = completionEngine.Rules.Phase.ToString();
        if (phases.Count == 0 || phases[^1] != phase) phases.Add(phase);
    }

    int ticksAtFullTime = completionEngine.CurrentTick;
    int eventsAtFullTime = completionEngine.Rules.Events.Count;
    completionEngine.StepTick();

    output["match_completion"] = new
    {
        phases,
        reachedFullTime = completionEngine.Rules.Phase == MatchPhase.FullTime,
        clockSeconds = Math.Round(completionEngine.Rules.MatchClockSeconds, 2),
        expectedClockSeconds = 24,
        fullTimeTick = ticksAtFullTime,
        tickAfterFullTime = completionEngine.CurrentTick,
        clockAfterFullTime = Math.Round(completionEngine.Rules.MatchClockSeconds, 2),
        halfTimeEvents = completionEngine.Rules.Events.Count(e => e.EventType == "HalfTime"),
        fullTimeEvents = completionEngine.Rules.Events.Count(e => e.EventType == "FullTime"),
        eventsAtFullTime,
        secondHalfHomeAttackDirectionX = completionEngine.Rules.GetHomeAttackDirectionX(),
        finalScore = new { home = completionEngine.Rules.HomeScore, away = completionEngine.Rules.AwayScore }
    };
}

// Multi-seed tactical report (audit F23): complete matches on four seeds, width separation across the
// set, plus execution-aware kick counts.
{
    ulong[] tacticalSeeds = { 2026UL, 4242UL, 91UL, 777UL };
    var perSeed = new List<object>();
    var differences = new List<float>();

    foreach (ulong tacticalSeed in tacticalSeeds)
    {
        var tacticalEngine = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = tacticalSeed,
            PlayersPerTeam = 11,
            HalfDurationSeconds = 60f,
            HomeProfile = TeamTacticalProfile.PossessionControl,
            AwayProfile = TeamTacticalProfile.DirectCounterattack
        });

        int requestedKicks = 0;
        int executedKicks = 0;
        int lastAttemptTick = -1;
        int tacticalGuard = 0;
        while (tacticalEngine.Rules.Phase != MatchPhase.FullTime && tacticalGuard++ < 30000)
        {
            tacticalEngine.StepTick();
            var attempts = tacticalEngine.LastContactAttempts;
            if (attempts.Count == 0 || attempts[0].Tick == lastAttemptTick) continue;
            lastAttemptTick = attempts[0].Tick;
            foreach (var attempt in attempts)
            {
                if (!attempt.WantedKick) continue;
                requestedKicks++;
                if (attempt.Executed) executedKicks++;
            }
        }

        var a = tacticalEngine.Analytics;
        float difference = a.HomeAverageTeamWidthMeters - a.AwayAverageTeamWidthMeters;
        differences.Add(difference);
        perSeed.Add(new
        {
            seed = tacticalSeed,
            completed = tacticalEngine.Rules.Phase == MatchPhase.FullTime,
            clockSeconds = Math.Round(tacticalEngine.Rules.MatchClockSeconds, 1),
            score = $"{a.HomeGoals}-{a.AwayGoals}",
            widthHome = Math.Round(a.HomeAverageTeamWidthMeters, 1),
            widthAway = Math.Round(a.AwayAverageTeamWidthMeters, 1),
            widthDifference = Math.Round(difference, 2),
            passes = new { home = a.HomePassesAttempted, away = a.AwayPassesAttempted },
            completions = new { home = a.HomePassesCompleted, away = a.AwayPassesCompleted },
            shots = new { home = a.HomeShots, away = a.AwayShots },
            possessionShareHome = Math.Round(a.HomePossessionShare, 3),
            kicks = new { executed = executedKicks, requested = requestedKicks }
        });
    }

    output["multi_seed_tactical"] = new
    {
        seeds = tacticalSeeds,
        perSeed,
        meanWidthDifference = Math.Round(differences.Average(), 2),
        widerSeeds = differences.Count(d => d > 0f),
        gatePassed = differences.Count(d => d > 0f) >= tacticalSeeds.Length - 1 &&
                     differences.Average() > 0.5f
    };
}

// Match-level traces: event count, goalkeeper own goals, and responder activity.
foreach (bool humanEnabled in new[] { false, true })
{
    var sim = new MatchSimulationEngine(new PreMatchConfig { Seed = 20260818, DesignatedHumanPlayerId = humanEnabled ? 9 : null });
    var goalSamples = new List<object>();
    var lastKeeperContact = new Dictionary<int, object>();
    var recent = new Queue<object>();
    var actionCounts = new Dictionary<string, int>();
    var rejectedKicks = new Dictionary<string, int>();
    int kickCommands = 0, executedKicks = 0, lastAttemptTick = -1;
    int eventsSeen = sim.Rules.Events.Count;
    int defendingLastTouchGoals = 0, keeperLastTouchGoals = 0, attributedOwnGoals = 0, attributedScorerGoals = 0;
    for (int t = 0; t < 18000; t++)
    {
        var recoveries = sim.Players.Where(p => p.Role == TacticalRole.Goalkeeper).ToDictionary(p => p.Id, p => p.KickRecoveryTimer);
        var incomingBall = sim.Ball.Clone();
        sim.StepTick();

        // Audit F17: every requested kick is either an executed contact or an explicit refusal.
        // The old analytics counted proposed commands with their own distance filter instead.
        var attempts = sim.LastContactAttempts;
        if (attempts.Count > 0 && attempts[0].Tick != lastAttemptTick)
        {
            lastAttemptTick = attempts[0].Tick;
            foreach (var a in attempts)
            {
                if (!a.WantedKick) continue;
                kickCommands++;
                if (a.Executed) executedKicks++;
                else rejectedKicks[a.Reason.ToString()] = rejectedKicks.GetValueOrDefault(a.Reason.ToString()) + 1;
            }
        }

        foreach (var p in sim.Players.Where(p => p.Role != TacticalRole.Goalkeeper && (p.Id != 9 || !humanEnabled)))
        {
            string action = sim.GetController(p.Id).GetDebugSummary().Split(" (U=")[0];
            actionCounts[action] = actionCounts.GetValueOrDefault(action) + 1;
        }
        var gks = sim.Players.Where(p => p.Role == TacticalRole.Goalkeeper).ToArray();
        foreach (var g in gks)
            if (g.KickRecoveryTimer > recoveries[g.Id])
            {
                lastKeeperContact[g.Id] = new { tick = sim.CurrentTick, keeper = Vec(g.Position), facingX = g.FacingXZ.X, ball = Vec(sim.Ball.Position), incomingVelocity = Vec(incomingBall.Velocity), incomingSpin = Vec(incomingBall.AngularVelocity), velocity = Vec(sim.Ball.Velocity), spin = Vec(sim.Ball.AngularVelocity), command = sim.GetController(g.Id).GetDebugSummary(), execution = g.LastExecutionDiagnostic };
                if (humanEnabled && g.LastExecutionDiagnostic == "GK:ReflexParry" && !output.ContainsKey("natural_parry_replay"))
                {
                    var replayResults = new List<object>();
                    foreach (bool retainSpin in new[] { true, false })
                    {
                        var replay = sim.Ball.Clone();
                        if (!retainSpin) replay.AngularVelocity = Vector3.Zero;
                        var solver = new BallPhysicsSolver();
                        bool goal = false;
                        for (int i = 0; i < 180; i++)
                        {
                            solver.Step(replay, dt);
                            if (replay.Position.X > pitch.HalfLength + .11f && MathF.Abs(replay.Position.Z) < pitch.HalfGoalWidth && replay.Position.Y < pitch.GoalHeight)
                            { goal = true; break; }
                        }
                        replayResults.Add(new { retainSpin, goal, finalBall = Vec(replay.Position) });
                    }
                    output["natural_parry_replay"] = new { sourceTick = sim.CurrentTick, initialBall = Vec(sim.Ball.Position), initialVelocity = Vec(sim.Ball.Velocity), initialSpin = Vec(sim.Ball.AngularVelocity), replayResults };
                }
            }
        if (MathF.Abs(sim.Ball.Position.X) > 45)
        {
            recent.Enqueue(new { tick = sim.CurrentTick, phase = sim.Rules.Phase.ToString(), ball = Vec(sim.Ball.Position), vel = Vec(sim.Ball.Velocity), touch = sim.Ball.LastTouchedByPlayerId,
                keepers = gks.Select(g => new { g.Id, pos = Vec(g.Position), facing = g.FacingXZ.X, g.KickRecoveryTimer, cmd = sim.GetController(g.Id).GetDebugSummary(), exec = g.LastExecutionDiagnostic }).ToArray() });
            while (recent.Count > 15) recent.Dequeue();
        }
        foreach (var ev in sim.Rules.Events.Skip(eventsSeen))
        {
            if (ev.EventType != "Goal") continue;
            var toucher = sim.Players.FirstOrDefault(p => p.Id == ev.PlayerId);
            bool own = toucher != null && toucher.Team != ev.Team;
            if (own) defendingLastTouchGoals++;
            if (own && toucher!.Role == TacticalRole.Goalkeeper) keeperLastTouchGoals++;
            // Audit F20: the authoritative attribution is separate from the physical last touch.
            if (ev.IsOwnGoal) attributedOwnGoals++;
            else attributedScorerGoals++;
            if (goalSamples.Count < 3) goalSamples.Add(new { tick = t + 1, scoringTeam = ev.Team.ToString(), ev.PlayerId, defendingLastTouch = own, scorer = ev.ScorerPlayerId, ownGoal = ev.OwnGoalPlayerId, details = ev.Details, lastKeeperContact = ev.PlayerId.HasValue ? lastKeeperContact.GetValueOrDefault(ev.PlayerId.Value) : null, trace = recent.ToArray() });
        }
        eventsSeen = sim.Rules.Events.Count;
    }
    output[$"match_human_{humanEnabled}"] = new
    {
        sim.CurrentTick,
        clock = sim.Rules.MatchClockSeconds,
        sim.Analytics.HomeGoals,
        sim.Analytics.AwayGoals,
        possessionShareHome = Math.Round(sim.Analytics.HomePossessionShare, 3),
        homeTerritoryX = Math.Round(sim.Analytics.HomeAverageBallTerritoryX, 2),
        awayTerritoryX = Math.Round(sim.Analytics.AwayAverageBallTerritoryX, 2),
        homeWidth = Math.Round(sim.Analytics.HomeAverageTeamWidthMeters, 1),
        awayWidth = Math.Round(sim.Analytics.AwayAverageTeamWidthMeters, 1),
        passes = new { home = sim.Analytics.HomePassesAttempted, away = sim.Analytics.AwayPassesAttempted },
        passCompletions = new { home = sim.Analytics.HomePassesCompleted, away = sim.Analytics.AwayPassesCompleted },
        passCompletionShare = new
        {
            home = Math.Round(sim.Analytics.HomePassCompletionShare, 3),
            away = Math.Round(sim.Analytics.AwayPassCompletionShare, 3)
        },
        controlledPossessionShareHome = Math.Round(sim.Analytics.HomeControlledPossessionShare, 3),
        shots = new { home = sim.Analytics.HomeShots, away = sim.Analytics.AwayShots },
        kickCommands,
        executedKicks,
        rejectedKicks,
        defendingLastTouchGoals,
        keeperLastTouchGoals,
        attributedScorerGoals,
        attributedOwnGoals,
        actionCounts,
        goalSamples
    };
}
Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
