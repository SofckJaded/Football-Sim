using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// Regressions for the 2026-11-22 bug audit's intent-layer findings (AUD-08, AUD-10, AUD-12, AUD-13b).
///
/// Each test is written so it fails on the pre-fix code for the reason named in its comment: the fixture is
/// synthetic (a standalone controller plus a hand-built observation), so the only thing that differs between
/// the old and new behaviour is the audited defect rather than a match's emergent play.
/// </summary>
public sealed class AudFixIntentTests
{
    private static readonly PitchDimensions Pitch = new();
    private static readonly float Dt = SimConstants.FixedDeltaTime;

    private static readonly AiRuntimeConfig Scheduled = new()
    {
        EnableScheduledDecisions = true,
        CarrierDecisionTicks = 6,
        OutfieldDecisionTicks = 10,
        JobCommitmentSeconds = 1.0f,
        ReactionCommitmentSeconds = 0.5f
    };

    private static readonly AiRuntimeConfig CommitmentOff = new() { EnableCommitment = false };

    // =============================================================================================
    // AUD-08 — cached intents: possession changes invalidate, discrete actions are emitted once
    // =============================================================================================

    /// <summary>
    /// AUD-08 part 1 (off-ball -> on-ball): after an off-ball decision the player receives the ball. The
    /// scheduler, now asked with <c>hasBall: true</c>, may still say "do not recompute"; the pre-fix
    /// executor re-emitted the off-ball movement command regardless, so the player kept running the old
    /// plan for up to <see cref="AiRuntimeConfig.CarrierDecisionTicks"/>-1 ticks. The fix must force a
    /// fresh decision, proven by the decision counter and the stored intent's possession side.
    /// </summary>
    [Fact]
    public void ScheduledDecisions_PossessionGainForcesAFreshDecision()
    {
        var controller = new TacticalUtilityAiController(Pitch, null, Scheduled);
        var scheduler = new AiScheduler(Scheduled);
        const int id = 8;

        int decisionTick = FindTick(scheduler, id, hasBall: false);
        Assert.False(scheduler.ShouldRecompute(decisionTick + 1, id, hasBall: true),
            "the fixture needs the next tick outside the carrier cadence, or the old bug cannot show");

        controller.Decide(Observation(decisionTick, onBall: false), Dt);
        Assert.Equal(1, controller.DecisionsComputed);
        Assert.False(controller.CurrentIntent!.Value.OnBallAtDecision);

        controller.Decide(Observation(decisionTick + 1, onBall: true), Dt);

        // Pre-fix: DecisionsComputed stayed 1 and the intent still said "decided off the ball".
        Assert.Equal(2, controller.DecisionsComputed);
        Assert.True(controller.CurrentIntent!.Value.OnBallAtDecision);
    }

    /// <summary>
    /// AUD-08 part 1 (on-ball -> off-ball): after an on-ball decision the player kicks the ball away. The
    /// pre-fix executor re-emitted the on-ball command (carry/shot/pass intent) while the next outfield
    /// cadence window had not landed, i.e. an action chosen for a ball the player no longer had.
    /// </summary>
    [Fact]
    public void ScheduledDecisions_PossessionLossForcesAFreshDecision()
    {
        var controller = new TacticalUtilityAiController(Pitch, null, Scheduled);
        var scheduler = new AiScheduler(Scheduled);
        const int id = 8;

        int decisionTick = FindTick(scheduler, id, hasBall: true);
        Assert.False(scheduler.ShouldRecompute(decisionTick + 1, id, hasBall: false),
            "the fixture needs the next tick outside the outfield cadence, or the old bug cannot show");

        controller.Decide(Observation(decisionTick, onBall: true), Dt);
        Assert.Equal(1, controller.DecisionsComputed);
        Assert.True(controller.CurrentIntent!.Value.OnBallAtDecision);

        controller.Decide(Observation(decisionTick + 1, onBall: false), Dt);

        // Pre-fix: DecisionsComputed stayed 1 and the cached intent still claimed the on-ball side.
        Assert.Equal(2, controller.DecisionsComputed);
        Assert.False(controller.CurrentIntent!.Value.OnBallAtDecision);
    }

    /// <summary>
    /// AUD-08 part 2 (Architecture Rule 4): the cached command's discrete actions are one-shot. The pre-fix
    /// executor returned the stored <see cref="FootballerCommand"/> verbatim on every skipped tick, so a kick
    /// or tackle that execution refused on the tick it was chosen could execute ticks later after the ball or
    /// target had moved. A re-emitted command must keep the movement/facing that keeps the player playing, and
    /// must carry none of the discrete actions.
    /// </summary>
    [Fact]
    public void CachedIntent_ReEmitsMovement_ButNeverTheDiscreteAction()
    {
        var executor = new IntentExecutor();
        var kick = new FootballerCommand(
            new Vector2(1f, 0f), new Vector2(0f, 1f), false, false,
            RequestTackle: true, RequestJumpHeader: false, RequestKeeperDive: false, Vector3.Zero,
            KickIntentType.GroundPass, new Vector3(0f, 0f, 1f), 0.6f, 0.25f)
        {
            IntendedReceiverId = 9
        };
        executor.Set(new PlayerIntent(CandidateKey.None, "Pass", kick, StartTick: 10, ExpiryTick: 40)
        {
            OnBallAtDecision = true
        });

        // The decision's own tick is the one emission of the action; it is not a re-emission.
        Assert.True(executor.TryGetLiveCommand(10, out FootballerCommand chosen));
        Assert.Equal(kick, chosen);

        // Pre-fix this returned a command equal to `kick`, so every assertion below failed.
        Assert.True(executor.TryGetLiveCommand(11, out FootballerCommand reEmitted));
        Assert.Equal(KickIntentType.None, reEmitted.KickType);
        Assert.False(reEmitted.RequestTackle);
        Assert.False(reEmitted.RequestJumpHeader);
        Assert.False(reEmitted.RequestKeeperDive);
        Assert.Equal(Vector3.Zero, reEmitted.KeeperDiveDirection);
        Assert.Equal(0f, reEmitted.KickPower01);
        Assert.Equal(0f, reEmitted.RequestedCurveSpin);
        Assert.Null(reEmitted.IntendedReceiverId);
        Assert.Equal(kick.DesiredMoveDirectionXZ, reEmitted.DesiredMoveDirectionXZ);
        Assert.Equal(kick.DesiredFacingDirectionXZ, reEmitted.DesiredFacingDirectionXZ);
    }

    /// <summary>AUD-08: the possession side is part of the executor's "is the intent still valid?" question.</summary>
    [Fact]
    public void Executor_RefusesToReEmitAcrossAPossessionChange()
    {
        var executor = new IntentExecutor();
        executor.Set(new PlayerIntent(CandidateKey.None, "Support", FootballerCommand.Idle, 10, 40)
        {
            OnBallAtDecision = false
        });

        Assert.True(executor.TryGetLiveCommand(12, onBallAtDecision: false, out _));
        // Pre-fix this overload did not exist; the controller re-emitted the off-ball command on the ball.
        Assert.False(executor.TryGetLiveCommand(12, onBallAtDecision: true, out _));
    }

    /// <summary>
    /// AUD-08 must not change the shipped path: <see cref="AiRuntimeConfig.EnableScheduledDecisions"/> is off by
    /// default, so every tick is a fresh decision regardless of possession.
    /// </summary>
    [Fact]
    public void DefaultConfig_StillDecidesEveryTickAcrossAPossessionChange()
    {
        var controller = new TacticalUtilityAiController(Pitch);
        Assert.False(AiRuntimeConfig.Default.EnableScheduledDecisions);

        controller.Decide(Observation(tick: 100, onBall: false), Dt);
        controller.Decide(Observation(tick: 101, onBall: true), Dt);

        Assert.Equal(2, controller.DecisionsComputed);
    }

    /// <summary>AUD-08 guard: without a possession change the cadence still re-emits instead of re-scoring.</summary>
    [Fact]
    public void ScheduledDecisions_StillReuseTheIntent_WhenPossessionDoesNotChange()
    {
        var controller = new TacticalUtilityAiController(Pitch, null, Scheduled);
        var scheduler = new AiScheduler(Scheduled);
        const int id = 8;

        int decisionTick = FindTick(scheduler, id, hasBall: true);
        Assert.False(scheduler.ShouldRecompute(decisionTick + 1, id, hasBall: true));

        controller.Decide(Observation(decisionTick, onBall: true), Dt);
        controller.Decide(Observation(decisionTick + 1, onBall: true), Dt);

        Assert.Equal(1, controller.DecisionsComputed);
    }

    // =============================================================================================
    // AUD-10 — EnableCommitment = false must not keep the switch-margin hysteresis
    // =============================================================================================

    /// <summary>
    /// AUD-10: with <see cref="AiRuntimeConfig.EnableCommitment"/> off the ablation must decide on this tick's
    /// raw scores, without the A4.5 switch hysteresis. The fixture commits a carry, then offers a pass that
    /// is the raw winner but only marginally (the A4.5 margin is not cleared). Pre-fix the off build kept the
    /// carry because <c>ShouldSwitch</c> still ran; the fix switches to the raw winner immediately, while the
    /// shipped default (<c>true</c>) still holds the carry.
    ///
    /// AIB-12a fixture recalibration (2026-11-23, finding 5): the first observation has no pass available,
    /// because the lowered early-carry bonus makes the clean 8 m pass the better action and the committed
    /// action must differ from the competing pass. The competing observation is the nearest pass that is
    /// better by less than the A4.5 margin (measured 0.9924 against the carry's 0.9862; the margin is
    /// 0.02 + 0.04 x 0.9862 = 0.059), which is the precondition the test states.
    /// </summary>
    [Fact]
    public void CommitmentOff_SwitchesOnAMarginalImprovement_WhileCommitmentOnHolds()
    {
        var on = new TacticalUtilityAiController(Pitch);
        var off = new TacticalUtilityAiController(Pitch, ai: CommitmentOff);

        on.Decide(PressureObservationWithoutMate(), Dt);
        off.Decide(PressureObservationWithoutMate(), Dt);
        Assert.Equal(CandidateKind.Carry, on.LastSelected.Key.Kind);
        Assert.Equal(CandidateKind.Carry, off.LastSelected.Key.Kind);

        on.Decide(PressureObservation(4.5f), Dt);
        off.Decide(PressureObservation(4.5f), Dt);

        // Fixture precondition: the pass is the raw winner, but inside the A4.5 switch margin.
        float passRaw = RawUtility(off.LastCandidates, CandidateKind.Pass);
        float carryRaw = RawUtility(off.LastCandidates, CandidateKind.Carry);
        Assert.True(passRaw > carryRaw,
            $"the fixture needs the pass to be the raw winner: {passRaw:F4} against {carryRaw:F4}");
        Assert.False(TacticalUtilityAiController.ShouldSwitch(
                passRaw, carryRaw, off.RuntimeConfig.SwitchAbsoluteMargin,
                off.RuntimeConfig.SwitchRelativeMargin),
            "the fixture needs the improvement to be inside the A4.5 switch margin");

        // Pre-fix: the off build held the carry (ShouldSwitch returned false and it was still applied).
        Assert.Equal(CandidateKind.Pass, off.LastSelected.Key.Kind);
        Assert.Equal(passRaw, off.LastSelected.UtilityScore);

        // The shipped default is unchanged: the bonus keeps the committed carry.
        Assert.Equal(CandidateKind.Carry, on.LastSelected.Key.Kind);
    }

    /// <summary>
    /// AUD-10: with commitment off nothing is held, so the diagnostic committed key must stay empty. Pre-fix
    /// the key tracked every chosen action even though the ablation never defended it — an artifact would
    /// have claimed a commitment the run did not have.
    /// </summary>
    [Fact]
    public void CommitmentOff_TracksNoCommittedAction()
    {
        var on = new TacticalUtilityAiController(Pitch);
        var off = new TacticalUtilityAiController(Pitch, ai: CommitmentOff);

        on.Decide(PressureObservation(8f), Dt);
        off.Decide(PressureObservation(8f), Dt);

        Assert.False(on.CommittedKey.IsNone);
        // Pre-fix: off.CommittedKey named the selected carry.
        Assert.True(off.CommittedKey.IsNone);
        Assert.Equal(0f, off.CommitmentRemainingSeconds);
    }

    // =============================================================================================
    // AUD-12 — the description must not imply the reserved cadence fields govern the run
    // =============================================================================================

    /// <summary>
    /// AUD-12: four cadence fields are validated and printed but read by no mechanism. The pre-fix
    /// <see cref="AiRuntimeConfig.Describe"/> put them in the same <c>cadence(...)</c> group as the wired
    /// fields, which is the misleading artifact label the audit found; the fix separates them into a
    /// <c>reserved(...)</c> group. Every field, name, default and validation is kept.
    /// </summary>
    [Fact]
    public void Describe_SeparatesWiredCadenceFromTheReservedCadenceGroup()
    {
        string text = new AiRuntimeConfig().Describe();

        Assert.Contains("cadence(p4 c6 o10 l6)", text);
        // Pre-fix: the four unwired numbers sat inside cadence(...) and no reserved(...) group existed.
        Assert.Contains("reserved(s6 b12 t20 r60)", text);
        Assert.True(text.IndexOf("cadence(", StringComparison.Ordinal) <
                    text.IndexOf("reserved(", StringComparison.Ordinal));
    }

    /// <summary>AUD-12: "honest description" must not turn into "silently unvalidated".</summary>
    [Fact]
    public void ReservedCadenceFields_AreStillValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AiRuntimeConfigValidation.Validate(new AiRuntimeConfig { LocalSpatialCacheTicks = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AiRuntimeConfigValidation.Validate(new AiRuntimeConfig { TeamBeliefMapTicks = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AiRuntimeConfigValidation.Validate(new AiRuntimeConfig { TacticalStructureTicks = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AiRuntimeConfigValidation.Validate(new AiRuntimeConfig { StructuralReassessmentTicks = 0 }));
    }

    // =============================================================================================
    // AUD-13b — a set-piece taker outside kicking reach must close on the ball
    // =============================================================================================

    /// <summary>
    /// AUD-13b: the restart trigger fires from up to 4.5 m away, but the pre-fix method always returned the
    /// pass aim vector as the movement direction, so a taker standing 1.5-4.4 m from the ball walked away
    /// from it. Outside the execution layer's reach he must move toward the ball and emit no kick (a kick
    /// there would be refused as OutOfReach); the aim vector is for the kick, not for the walk.
    /// </summary>
    [Fact]
    public void SetPieceTakerOutsideKickReach_ClosesOnTheBall()
    {
        var controller = new TacticalUtilityAiController(Pitch);
        FootballerObservation obs = SetPieceObservation(self: Vector3.Zero, ball: new Vector3(3f, 0f, 0f));

        // The fixture's mate is 12 m up the pitch (+Z), so the pre-fix aim vector pointed at him.
        FootballerCommand command = controller.Decide(obs, Dt);

        Assert.Equal(KickIntentType.None, command.KickType);
        // Pre-fix: the direction was the aim vector (roughly +Z), and this dot was ~0.
        Assert.True(Vector2.Dot(command.DesiredMoveDirectionXZ, Vector2.UnitX) > 0.99f,
            $"the taker set off at {command.DesiredMoveDirectionXZ} instead of closing on the ball at +X");
        Assert.True(command.DesiredMoveDirectionXZ.Length() > 0.99f);
    }

    /// <summary>
    /// AUD-13b: within kicking reach the taker still takes the restart, and the kick keeps its aim/pass
    /// vector rather than turning into the walk-toward-the-ball direction.
    /// </summary>
    [Fact]
    public void SetPieceTakerWithinKickReach_PlaysTheRestartWithTheAimVector()
    {
        var controller = new TacticalUtilityAiController(Pitch);
        FootballerObservation obs = SetPieceObservation(self: Vector3.Zero, ball: new Vector3(0.4f, 0f, 0f));

        FootballerCommand command = controller.Decide(obs, Dt);

        Assert.NotEqual(KickIntentType.None, command.KickType);
        Vector2 aimXZ = new(command.KickAimDirectionXZ.X, command.KickAimDirectionXZ.Z);
        // The mate is +Z, the ball +X: aiming at the ball would mean the pass vector was lost.
        Assert.True(Vector2.Dot(aimXZ, Vector2.UnitX) < 0.9f,
            $"the restart was aimed at the ball direction {aimXZ} instead of the receiver");
    }

    // =============================================================================================
    // Fixtures
    // =============================================================================================

    /// <summary>
    /// A standalone player either on the ball (0.4 m, controlled) or far off it (9 m, loose) — the same shape
    /// the scheduler's own tests use, so a cadence test isolates possession from everything else.
    /// </summary>
    private static FootballerObservation Observation(int tick, bool onBall) => new()
    {
        SelfId = 8,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CentralMidfielder,
        SelfPosition = Vector3.Zero,
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = onBall,
        ControlQuality01 = onBall ? 1f : 0f,
        PerceivedBallPosition = onBall ? new Vector3(0.4f, 0f, 0f) : new Vector3(9f, 0f, 3f),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = onBall ? TeamId.Home : TeamId.Away,
        BallLastTouchedByPlayerId = onBall ? 8 : 12,
        BallLastTouchedByTeam = onBall ? TeamId.Home : TeamId.Away,
        BallControlState = onBall ? BallControlState.Controlled : BallControlState.Loose,
        BallControllingPlayerId = onBall ? 8 : null,
        TacticalAnchorPosition = Vector3.Zero,
        AttackingDirectionX = 1f,
        MatchTimeSeconds = tick / 60f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = tick,
        VisiblePlayers = new List<PerceivedEntity>
        {
            new(9, TeamId.Home, TacticalRole.Striker, new Vector3(20f, 0f, 2f), Vector3.Zero, 0.9f)
        },
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = new PlayerTendencies(),
        SelfAttributes = new PlayerAttributes()
    };

    /// <summary>The commitment fixture: a pressured carrier with one striker and one close opponent.</summary>
    private static FootballerObservation PressureObservation(float mateDistance) => new()
    {
        SelfId = 8,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CentralMidfielder,
        SelfPosition = Vector3.Zero,
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = new Vector3(0.4f, 0f, 0f),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = 8,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = 8,
        TacticalAnchorPosition = Vector3.Zero,
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 0f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        VisiblePlayers = new List<PerceivedEntity>
        {
            new(9, TeamId.Home, TacticalRole.Striker, new Vector3(mateDistance, 0f, 3f), Vector3.Zero, 0.9f),
            new(12, TeamId.Away, TacticalRole.CenterBack, new Vector3(0f, 0f, 2.4f), Vector3.Zero, 0.9f)
        },
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = new PlayerTendencies(),
        SelfAttributes = new PlayerAttributes()
    };

    /// <summary>
    /// The commitment fixture with no team-mate visible: the carrier's action set is the carry, a take-on and a
    /// shield. AIB-12a lowered the early-carry bonus below the clean pass's value, so a committed carry needs a
    /// picture without the pass for the AUD-10 ablation to have a switch to test.
    /// </summary>
    private static FootballerObservation PressureObservationWithoutMate() => new()
    {
        SelfId = 8,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CentralMidfielder,
        SelfPosition = Vector3.Zero,
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = new Vector3(0.4f, 0f, 0f),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = 8,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = 8,
        TacticalAnchorPosition = Vector3.Zero,
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 0f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        VisiblePlayers = new List<PerceivedEntity>
        {
            new(12, TeamId.Away, TacticalRole.CenterBack, new Vector3(0f, 0f, 2.4f), Vector3.Zero, 0.9f)
        },
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = new PlayerTendencies(),
        SelfAttributes = new PlayerAttributes()
    };

    /// <summary>A set-piece taker whose ball is <paramref name="ball"/> away; the only mate is 12 m up (+Z).</summary>
    private static FootballerObservation SetPieceObservation(Vector3 self, Vector3 ball) => new()
    {
        SelfId = 8,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CentralMidfielder,
        SelfPosition = self,
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = ball,
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = 8,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = 8,
        TacticalAnchorPosition = self,
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 10f,
        // A throw-in is armed for this team: the player knows he is the taker.
        CurrentMatchPhase = MatchPhase.ThrowIn,
        IsSetPieceTaker = true,
        Tick = 600,
        VisiblePlayers = new List<PerceivedEntity>
        {
            new(9, TeamId.Home, TacticalRole.Striker, new Vector3(0f, 0f, 12f), Vector3.Zero, 0.9f)
        },
        TacticalContext = new TeamContext(
            TeamId.Home,
            TeamId.Home,
            PossessionStability.Controlled,
            TeamPhase.Attacking,
            TeamZone.MiddleThird,
            TransitionMode.None,
            new RestartContext(RestartKind.ThrowIn, TeamId.Home),
            IsEmergencyDefence: false),
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = new PlayerTendencies(),
        SelfAttributes = new PlayerAttributes()
    };

    // ---- helpers ---------------------------------------------------------------------------------

    private static int FindTick(AiScheduler scheduler, int playerId, bool hasBall)
    {
        for (int tick = 1; tick < 120; tick++)
            if (scheduler.ShouldRecompute(tick, playerId, hasBall)) return tick;
        throw new InvalidOperationException("no cadence tick found inside the fixture's window");
    }

    private static float RawUtility(IReadOnlyList<UtilityCandidate> candidates, CandidateKind kind) =>
        Best(candidates, kind).UtilityScore;

    private static UtilityCandidate Best(IReadOnlyList<UtilityCandidate> candidates, CandidateKind kind)
    {
        bool found = false;
        float best = float.NegativeInfinity;
        UtilityCandidate result = default;
        foreach (UtilityCandidate candidate in candidates)
        {
            if (candidate.Key.Kind != kind || candidate.UtilityScore <= best) continue;
            best = candidate.UtilityScore;
            result = candidate;
            found = true;
        }

        return found ? result : throw new InvalidOperationException($"the fixture generated no {kind} candidate");
    }
}
