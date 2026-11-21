using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Rules;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A8.5 — the restart routine at the engine level: the live controllers receive the routine the restarting team
/// elected, the designated taker plays the receiver it names, and the routine is gone once the restart has been
/// taken. The model-level clauses live in <see cref="RestartPolicyTests"/> and the lifecycle in
/// <see cref="RestartCoordinatorTests"/>.
/// </summary>
public sealed class RestartScenarioTests
{
    private static readonly PitchDimensions Pitch = RestartTestWorld.Pitch;

    /// <summary>
    /// A match opens with a kick-off, and A8.5's library has a routine for it: the designated taker is its taker,
    /// the taker's own observation carries it, and he plays the receiver it named.
    /// </summary>
    [Fact]
    public void TheKickoff_RaisesARoutineAndTheTakerPlaysItsReceiver()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig { Seed = 20260818UL });
        // The rules engine designates the kick-off taker when it sets the restart up; the designation is
        // consumed by the restart's first touch, so it is captured before the tick that plays it.
        int? designated = engine.Rules.DesignatedSetPieceTakerId;
        Assert.NotNull(designated);

        engine.StepTick();

        Assert.True(engine.Communication.Restarts.TryGetLive(engine.CurrentTick, out RestartRoutine routine));
        Assert.Equal(RestartKind.Kickoff, routine.Kind);
        Assert.Equal(designated!.Value, routine.TakerId);
        Assert.Equal(RestartRole.Taker, routine.RoleOf(designated.Value));
        Assert.True(routine.ReceiverId > 0, "the kick-off routine names the man it plays to");

        FootballerObservation takerObservation = engine.GetLastObservation(routine.TakerId)
            ?? throw new InvalidOperationException("the taker has no observation");
        Assert.True(takerObservation.IsSetPieceTaker);
        Assert.True(takerObservation.Routine.IsTaker);
        Assert.Equal(routine.ReceiverId, takerObservation.Routine.ReceiverId);
        Assert.Equal(routine.Kind, takerObservation.Routine.Kind);

        FootballerState taker = engine.Players.Single(p => p.Id == routine.TakerId);
        Assert.Equal(routine.ReceiverId, taker.LastCommand.IntendedReceiverId);
    }

    /// <summary>The exit gate's completion clause: once the restart is taken, no player carries the routine.</summary>
    [Fact]
    public void ATakenRestart_ClearsTheRoutineFromEveryObservation()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig { Seed = 20260818UL });

        engine.StepTick();
        Assert.True(engine.Communication.Restarts.HasLive(engine.CurrentTick));

        engine.StepTick();

        Assert.False(engine.Communication.Restarts.HasLive(engine.CurrentTick));
        Assert.True(engine.Communication.Restarts.CountOfEnd(RestartEndReason.Taken) >= 1,
            "the kick-off's first touch is the routine's completion");
        foreach (FootballerState player in engine.Players)
        {
            FootballerObservation? observation = engine.GetLastObservation(player.Id);
            if (observation is null) continue;
            Assert.False(observation.Routine.IsActive, $"player #{player.Id} still carries a restart routine");
        }
    }

    /// <summary>Only the restarting team's players hold a role: the routine is a team decision, not a broadcast.</summary>
    [Fact]
    public void OnlyTheRestartingTeamsPlayers_HoldRoles()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig { Seed = 20260818UL });

        engine.StepTick();

        Assert.True(engine.Communication.Restarts.TryGetLive(engine.CurrentTick, out RestartRoutine routine));
        Assert.Equal(TeamId.Home, routine.Team);
        int support = routine.SupportId;
        int target = routine.TargetId;

        foreach (FootballerState player in engine.Players)
        {
            FootballerObservation? observation = engine.GetLastObservation(player.Id);
            if (observation is null) continue;
            if (player.Team != TeamId.Home)
                Assert.False(observation.Routine.IsActive, $"away player #{player.Id} carries a home routine");
        }

        if (support > 0)
            Assert.True(engine.GetLastObservation(support)!.Routine.IsSupport);
        if (target > 0)
            Assert.True(engine.GetLastObservation(target)!.Routine.IsTarget);
    }

    /// <summary>
    /// A throw-in forced through the engine's own out-of-play detection raises a throw-in routine, and the
    /// designated taker is the player it names. This is the "restart state comes from the rules engine" clause:
    /// the routine never chooses the spot, the kind or the taker.
    /// </summary>
    [Fact]
    public void AThrowInThroughTheEngine_RaisesAThrowInRoutineForTheDesignatedTaker()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig { Seed = 11UL });
        engine.Ball.Position = new Vector3(0f, 0.11f, 40f);
        engine.Ball.PreviousPosition = engine.Ball.Position;
        engine.Ball.LastTouchedByTeam = TeamId.Away;
        engine.Ball.LastTouchedByPlayerId = 22;

        Assert.Equal(MatchPhase.FirstHalf, engine.Rules.Phase);
        engine.StepTick();
        Assert.Equal(MatchPhase.ThrowIn, engine.Rules.Phase);

        RestartRoutine? found = null;
        int? takerWhenRaised = null;
        for (int tick = 0; tick < 60 && found is null; tick++)
        {
            // The designation is consumed by the restart's first touch, which is the same tick the routine is
            // elected on, so what the rules engine had armed is read before the step.
            int? armed = engine.Rules.DesignatedSetPieceTakerId;
            engine.StepTick();
            if (engine.Communication.Restarts.TryGetLive(engine.CurrentTick, out RestartRoutine live) &&
                live.Kind == RestartKind.ThrowIn)
            {
                found = live;
                takerWhenRaised = armed;
            }
        }

        Assert.NotNull(found);
        Assert.NotNull(takerWhenRaised);
        Assert.Equal(takerWhenRaised!.Value, found!.Value.TakerId);
        Assert.True(found.Value.ReceiverId > 0);
        Assert.True(found.Value.Confidence01 >= RestartPolicy.MinRoutineConfidence01);
    }

    /// <summary>
    /// The ablation build is the version-50 build: nothing is elected, every observation is neutral, and every
    /// restart is taken by the taker's own pre-A8.5 heuristic.
    /// </summary>
    [Fact]
    public void TheAblationBuild_RaisesNothingAndKeepsEveryObservationNeutral()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = 20260818UL,
            Ai = new AiRuntimeConfig { EnableRestartRoutines = false }
        });

        for (int tick = 0; tick < 90; tick++)
        {
            engine.StepTick();
            Assert.False(engine.Communication.Restarts.HasLive(engine.CurrentTick));
            foreach (FootballerState player in engine.Players)
            {
                FootballerObservation? observation = engine.GetLastObservation(player.Id);
                if (observation is null) continue;
                Assert.False(observation.Routine.IsActive);
            }
        }

        Assert.Equal(0, engine.Communication.Restarts.RaisedCount);
    }

    /// <summary>Same seed, same restarts, same routines: the election consumes no simulation randomness.</summary>
    [Fact]
    public void TheRoutines_AreDeterministicTickForTick()
    {
        var left = new MatchSimulationEngine(new PreMatchConfig { Seed = 20260777UL });
        var right = new MatchSimulationEngine(new PreMatchConfig { Seed = 20260777UL });

        var leftSeen = new List<string>();
        var rightSeen = new List<string>();
        for (int tick = 0; tick < 240; tick++)
        {
            left.StepTick();
            right.StepTick();
            leftSeen.Add(DescribeLive(left));
            rightSeen.Add(DescribeLive(right));
        }

        Assert.Equal(leftSeen, rightSeen);
        Assert.Equal(left.Communication.Restarts.RaisedCount, right.Communication.Restarts.RaisedCount);
        Assert.True(left.Communication.Restarts.RaisedCount > 0,
            "a match must have awarded at least the kick-off's routine");
    }

    /// <summary>
    /// The goal kick is the keeper's restart, so the routine has to reach his decision: a keeper's observation
    /// with a goal-kick routine gets the bounded preference for the receiver it names, and beats the identical
    /// picture without the routine.
    /// </summary>
    [Fact]
    public void AGoalKickRoutine_GivesTheKeeperABoundedPreferenceForItsReceiver()
    {
        // Two symmetric outlets ten metres either side of the ball: the keeper's own valuation cannot separate
        // them, so the routine's preference is what decides which one he plays.
        static FootballerObservation Observation(RestartRole role, int routineReceiver)
        {
            var visible = new List<PerceivedEntity>
            {
                new(2, TeamId.Home, TacticalRole.CenterBack, new Vector3(-47f, 0f, 10f), Vector3.Zero, 1f),
                new(3, TeamId.Home, TacticalRole.CenterBack, new Vector3(-47f, 0f, -10f), Vector3.Zero, 1f)
            };

            var teamContext = new TeamContext(
                TeamId.Home, TeamId.Home, PossessionStability.Loose, TeamPhase.Attacking, TeamZone.OwnThird,
                TransitionMode.None, new RestartContext(RestartKind.GoalKick, TeamId.Home), false);

            RestartRoutineContext routine = role == RestartRole.None
                ? RestartRoutineContext.None
                : new RestartRoutineContext(
                    RoutineId: 7, Kind: RestartKind.GoalKick, Option: RestartOption.Deliver, Role: role,
                    TakerId: 1, SupportId: 2, TargetId: 3, BallXZ: new Vector2(-47f, 0f),
                    ReceiverXZ: new Vector2(-47f, -10f), CreatedTick: 1, ExpiresTick: 200, Confidence01: 0.8f);

            if (routineReceiver == 2 && routine.IsActive)
            {
                routine = routine with { SupportId = 2, TargetId = 3, Option = RestartOption.Short };
            }

            return new FootballerObservation
            {
                SelfId = 1,
                SelfTeam = TeamId.Home,
                SelfRole = TacticalRole.Goalkeeper,
                SelfPosition = new Vector3(-47.45f, 0f, 0f),
                SelfVelocity = Vector3.Zero,
                SelfFacingXZ = new Vector2(1f, 0f),
                Stamina01 = 1f,
                AvailableToKick = true,
                ControlQuality01 = 0f,
                PerceivedBallPosition = new Vector3(-47f, 0.11f, 0f),
                PerceivedBallVelocity = Vector3.Zero,
                PossessingTeam = null,
                BallLastTouchedByPlayerId = null,
                BallLastTouchedByTeam = null,
                BallLastPlay = BallLastPlay.None,
                BallControlState = BallControlState.Loose,
                TacticalAnchorPosition = new Vector3(-47.45f, 0f, 0f),
                AttackingDirectionX = 1f,
                MatchTimeSeconds = 600f,
                CurrentMatchPhase = MatchPhase.FirstHalf,
                IsSetPieceTaker = true,
                Tick = 100,
                VisiblePlayers = visible,
                TeamProfile = TeamTacticalProfile.PossessionControl,
                SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.Goalkeeper),
                SelfAttributes = new PlayerAttributes(),
                TacticalContext = teamContext,
                Routine = routine
            };
        }

        static int KeeperPicks(FootballerObservation observation)
        {
            var keeper = new GoalkeeperController(Pitch);
            keeper.Decide(observation, SimConstants.FixedDeltaTime);
            return keeper.LastDistribution.Kind == GoalkeeperDistributionKind.None
                ? 0
                : keeper.LastDistribution.ReceiverId;
        }

        int withoutRoutine = KeeperPicks(Observation(RestartRole.None, 0));
        int withTargetRoutine = KeeperPicks(Observation(RestartRole.Taker, 3));
        int withSupportRoutine = KeeperPicks(Observation(RestartRole.Taker, 2));

        Assert.NotEqual(0, withoutRoutine);
        Assert.NotEqual(0, withTargetRoutine);
        Assert.NotEqual(0, withSupportRoutine);

        // The keeper's own valuation is symmetric here, so the routine's named receiver is the one he plays.
        Assert.Equal(3, withTargetRoutine);
        Assert.Equal(2, withSupportRoutine);
        Assert.NotEqual(withTargetRoutine, withSupportRoutine);
    }

    private static string DescribeLive(MatchSimulationEngine engine) =>
        engine.Communication.Restarts.TryGetLive(engine.CurrentTick, out RestartRoutine routine)
            ? routine.Describe()
            : "none";
}
