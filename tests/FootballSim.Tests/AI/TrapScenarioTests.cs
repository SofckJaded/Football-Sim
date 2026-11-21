using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Scenarios;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A8.4 â€” the short trap at the engine level, on the fixture the exit gate names: a carrier pinned on the
/// touchline, a home presser close enough to press, the allocator's cover behind him and a shape-duty
/// midfielder near the inside lane. The model-level clauses live in <see cref="TrapPolicyTests"/> and
/// <see cref="TrapCoordinatorTests"/>; these assertions check that the live controllers actually receive the
/// contract, take their roles, and that a failed trap hands every participant back to his ordinary duty.
/// </summary>
public sealed class TrapScenarioTests
{
    private const int HomeGoalkeeper = 101;
    private const int HomePresser = 104;
    private const int HomeScreener = 108;
    private const int HomeCover = 106;
    private const int AwayCarrier = 207;

    /// <summary>The touchline trap fixture: away winger holding on the negative touchline.</summary>
    private static MatchScenario TouchlineTrapFixture(bool enableTraps = true)
    {
        var scenario = new MatchScenario
        {
            Name = "touchline trap",
            Seed = 20261121UL,
            BallPosition = new Vector3(0.6f, 0.11f, -30f),
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = AwayCarrier,
            Ai = new AiRuntimeConfig { EnableTrapContracts = enableTraps }
        };

        scenario.Add(MatchScenario.Player(HomeGoalkeeper, TeamId.Home, TacticalRole.Goalkeeper,
            new Vector3(-45f, 0f, 0f), 1));
        scenario.Add(MatchScenario.Player(HomePresser, TeamId.Home, TacticalRole.FullBack,
            new Vector3(-7f, 0f, -28.5f), 2));
        scenario.Add(MatchScenario.Player(102, TeamId.Home, TacticalRole.CenterBack,
            new Vector3(-16f, 0f, -22f), 4));
        scenario.Add(MatchScenario.Player(103, TeamId.Home, TacticalRole.CenterBack,
            new Vector3(-24f, 0f, -2f), 5));
        scenario.Add(MatchScenario.Player(HomeCover, TeamId.Home, TacticalRole.DefensiveMidfielder,
            new Vector3(-14f, 0f, -26f), 6));
        scenario.Add(MatchScenario.Player(HomeScreener, TeamId.Home, TacticalRole.CentralMidfielder,
            new Vector3(-6f, 0f, -20f), 8));

        scenario.Add(MatchScenario.Player(209, TeamId.Away, TacticalRole.Goalkeeper,
            new Vector3(45f, 0f, 0f), 1));
        scenario.Add(MatchScenario.Player(AwayCarrier, TeamId.Away, TacticalRole.Winger,
            new Vector3(0f, 0f, -30f), 7).WithController(
            new FixedCommandController(ScenarioCommands.Hold(), "holding carrier")));
        scenario.Add(MatchScenario.Player(208, TeamId.Away, TacticalRole.CentralMidfielder,
            new Vector3(-4f, 0f, -24f), 8));
        scenario.Add(MatchScenario.Player(210, TeamId.Away, TacticalRole.Striker,
            new Vector3(8f, 0f, -12f), 9));
        return scenario;
    }

    /// <summary>The same trap, but the carrier is scripted to break inside after the trap has formed.</summary>
    private static MatchScenario EscapeFixture()
    {
        var script = new Dictionary<int, FootballerCommand>();
        for (int decision = 0; decision < 18; decision++) script[decision] = ScenarioCommands.Hold();

        // A pass out of the trap: the carrier plays it inside to his central midfielder. It is the same
        // escape the exit gate names ("can be escaped") â€” the ball leaves the trapped region under the
        // opponent's control, and the trap cannot survive it.
        Vector2 toMidfielder = new(-4f - 0.6f, -24f - -30f);
        Vector2 direction = toMidfielder / toMidfielder.Length();
        FootballerCommand escapePass = FootballerCommand.Idle with
        {
            DesiredFacingDirectionXZ = direction,
            KickType = KickIntentType.GroundPass,
            KickAimDirectionXZ = new Vector3(direction.X, 0f, direction.Y),
            KickPower01 = 0.35f,
            IntendedReceiverId = 208
        };

        MatchScenario scenario = TouchlineTrapFixture();
        return CopyWithCarrierController(scenario, new ScriptedCommandController(
            script, escapePass, "passing out of the trap"));
    }

    [Fact]
    public void TheLiveTrap_FramesThePresserTheScreenerAndTheCover()
    {
        var engine = new MatchSimulationEngine(TouchlineTrapFixture());

        TrapContract? found = null;
        int foundTick = 0;
        for (int tick = 1; tick <= 60 && found is null; tick++)
        {
            engine.StepTick();
            if (engine.Communication.Traps.TryGetLive(TeamId.Home, engine.CurrentTick, out TrapContract live))
            {
                found = live;
                foundTick = engine.CurrentTick;
            }
        }

        Assert.NotNull(found);
        TrapContract trap = found!.Value;
        Assert.Equal(TrapKind.Touchline, trap.Kind);
        Assert.Equal(TeamId.Home, trap.Team);
        Assert.Equal(AwayCarrier, trap.CarrierId);
        Assert.True(trap.IsLiveAt(foundTick));

        // The three roles are distinct, active, and the ones the match's own elections produced: the presser is
        // the elected ball responder and the cover is the allocator's cover. The screener is a shape-duty man.
        Assert.NotEqual(trap.PresserId, trap.ScreenId);
        Assert.NotEqual(trap.PresserId, trap.CoverId);
        Assert.NotEqual(trap.ScreenId, trap.CoverId);
        Assert.Equal(engine.GetBallResponder(TeamId.Home), trap.PresserId);
        Assert.Equal(TacticalJob.CoverDefender, engine.TeamJobs.Get(trap.CoverId)?.Job);
        Assert.Equal(TacticalJob.Shape, engine.TeamJobs.Get(trap.ScreenId)?.Job);

        foreach (int participant in new[] { trap.PresserId, trap.ScreenId, trap.CoverId })
        {
            FootballerObservation observation = engine.GetLastObservation(participant)!;
            Assert.True(observation.Trap.IsActive,
                $"#{participant} is a trap participant but his observation carries no contract");
            Assert.Equal(trap.TrapId, observation.Trap.TrapId);
        }
    }

    [Fact]
    public void TheParticipantsTakeTheirRoles()
    {
        var engine = new MatchSimulationEngine(TouchlineTrapFixture());
        int pressTicks = 0;
        int screenTicks = 0;
        int coverTicks = 0;
        int trapTicks = 0;

        for (int tick = 1; tick <= 120; tick++)
        {
            engine.StepTick();
            if (!engine.Communication.Traps.TryGetLive(TeamId.Home, engine.CurrentTick, out TrapContract trap))
                continue;

            trapTicks++;
            foreach (int participant in new[] { trap.PresserId, trap.ScreenId, trap.CoverId })
            {
                if (engine.GetController(participant) is not TacticalUtilityAiController controller) continue;
                FootballerObservation observation = engine.GetLastObservation(participant)!;
                if (!observation.Trap.IsActive) continue;

                string action = controller.LastSelected.ActionName;
                if (observation.Trap.IsPresser && action is "CloseDownCarrier" or "ExecuteTackle") pressTicks++;
                if (observation.Trap.IsScreener && action == "ScreenThePass") screenTicks++;
                if (observation.Trap.IsCover && action == "CoverTheLane") coverTicks++;
            }
        }

        Assert.True(trapTicks > 0, "the fixture never ran a trap");
        Assert.True(pressTicks > 0, "the trap's presser never pressed");
        Assert.True(screenTicks > 0, "the trap's screener never screened");
        Assert.True(coverTicks > 0, "the trap's cover never covered");
    }

    /// <summary>
    /// The trap names the press angle rather than a capability: while the contract is live the presser's
    /// movement target is exactly the carrier plus the contract's press offset, and that offset is a standing
    /// point just outside contact reach â€” not a reach, a speed or a tackle.
    /// </summary>
    [Fact]
    public void ThePresserTakesTheTrapsAngle()
    {
        var engine = new MatchSimulationEngine(TouchlineTrapFixture());
        int samples = 0;

        for (int tick = 1; tick <= 120; tick++)
        {
            engine.StepTick();
            if (!engine.Communication.Traps.TryGetLive(TeamId.Home, engine.CurrentTick, out TrapContract trap))
                continue;

            if (engine.GetController(trap.PresserId) is not TacticalUtilityAiController controller) continue;
            FootballerObservation observation = engine.GetLastObservation(trap.PresserId)!;
            if (!observation.Trap.IsPresser) continue;
            if (controller.LastSelected.TargetXZ is not Vector3 target) continue;
            if (controller.LastSelected.ActionName is not ("CloseDownCarrier" or "ExecuteTackle")) continue;

            // The presser applies the contract's offset to the carrier in his own picture.
            if (!TryFindVisible(observation, trap.CarrierId, out Vector2 carrierXZ)) continue;
            Vector2 expected = carrierXZ + observation.Trap.PressOffsetXZ;
            Assert.True(Vector2.Distance(new Vector2(target.X, target.Z), expected) < 1e-3f,
                $"tick {engine.CurrentTick}: presser target ({target.X:F2},{target.Z:F2}) is not the trap's " +
                $"angle ({expected.X:F2},{expected.Y:F2})");
            Assert.InRange(Vector2.Distance(expected, carrierXZ), 0.5f, 2f);
            samples++;
        }

        Assert.True(samples > 0, "the presser never took the trap's angle");
    }

    /// <summary>The exit gate's structural clause: while the trap runs, the allocator's cover covers.</summary>
    [Fact]
    public void TheCoverIsPreservedWhileTheTrapRuns()
    {
        var engine = new MatchSimulationEngine(TouchlineTrapFixture());
        int samples = 0;

        for (int tick = 1; tick <= 120; tick++)
        {
            engine.StepTick();
            if (!engine.Communication.Traps.TryGetLive(TeamId.Home, engine.CurrentTick, out TrapContract trap))
                continue;

            Assert.Equal(TacticalJob.CoverDefender, engine.TeamJobs.Get(trap.CoverId)?.Job);
            if (engine.GetController(trap.CoverId) is not TacticalUtilityAiController controller) continue;
            FootballerObservation observation = engine.GetLastObservation(trap.CoverId)!;
            Assert.True(observation.Trap.IsCover);
            Assert.Equal("CoverTheLane", controller.LastSelected.ActionName);
            samples++;
        }

        Assert.True(samples > 0, "the fixture never ran a trap");
        Assert.Equal(0, engine.Communication.Traps.CountOfEnd(TrapEndReason.CoverLost));
    }

    /// <summary>
    /// The exit gate's escape clause: a carrier who breaks inside the pitch leaves the trap's region, the trap
    /// ends as escaped, every participant is released back to his ordinary duty, and the side recovers for the
    /// model's window before it may re-commit.
    /// </summary>
    [Fact]
    public void AFailedTrap_ReleasesEveryParticipantAndTheTeamRecovers()
    {
        var engine = new MatchSimulationEngine(EscapeFixture());
        TrapContract? lastLive = null;
        int escapedTick = -1;

        for (int tick = 1; tick <= 300 && escapedTick < 0; tick++)
        {
            engine.StepTick();
            TrapCoordinator board = engine.Communication.Traps;
            if (board.TryGetLive(TeamId.Home, engine.CurrentTick, out TrapContract live)) lastLive = live;
            if (board.CountOfEnd(TrapEndReason.Escaped) > 0) escapedTick = engine.CurrentTick;
        }

        TrapCoordinator traps = engine.Communication.Traps;
        Assert.True(escapedTick > 0,
            "the scripted carrier never escaped a trap; endings " +
            string.Join(", ", Enum.GetValues<TrapEndReason>()
                .Where(r => traps.CountOfEnd(r) > 0)
                .Select(r => $"{r} {traps.CountOfEnd(r)}")) +
            $", raised {traps.RaisedCount}");
        Assert.NotNull(lastLive);

        // The participants are back on their ordinary duties: no trap context, no trap ownership, the same
        // allocator jobs the match handed out (a trap never changed a job, it only named a role).
        foreach (int participant in new[] { lastLive!.Value.PresserId, lastLive.Value.ScreenId, lastLive.Value.CoverId })
        {
            FootballerObservation observation = engine.GetLastObservation(participant)!;
            Assert.False(observation.Trap.IsActive,
                $"#{participant} still owns a trap role after the trap ended");
            Assert.NotNull(engine.TeamJobs.Get(participant)?.Job);
        }

        // Recovery: the failed trap is not immediately re-committed, it waits out the board's window.
        Assert.True(traps.IsRecovering(TeamId.Home, escapedTick));
        Assert.False(traps.TryGetLive(TeamId.Home, escapedTick, out _));
        for (int tick = escapedTick + 1; tick < escapedTick + TrapCoordinator.RecoveryTicks; tick++)
        {
            engine.StepTick();
            if (engine.Communication.Traps.IsRecovering(TeamId.Home, engine.CurrentTick))
                Assert.False(engine.Communication.Traps.TryGetLive(TeamId.Home, engine.CurrentTick, out _),
                    $"tick {engine.CurrentTick}: a trap was re-raised inside the recovery window");
        }
    }

    /// <summary>No trap outlives its possession: a team that has the ball is not trapping.</summary>
    [Fact]
    public void NoTrapOutlivesItsPossession()
    {
        var engine = new MatchSimulationEngine(TouchlineTrapFixture());
        TeamId? previousPossession = engine.Possession.PossessionTeam;
        for (int tick = 1; tick <= 300; tick++)
        {
            engine.StepTick();
            TeamId? possessing = engine.Possession.PossessionTeam;
            if (possessing is TeamId team)
            {
                if (engine.Communication.Traps.HasLive(team, engine.CurrentTick))
                {
                    // The board is read from the snapshot the decisions saw, one tick behind the ownership the
                    // tick just consumed. A trap may therefore survive exactly the tick the ball changed hands
                    // and must be gone by the next one â€” never longer.
                    Assert.NotEqual(team, previousPossession);
                    engine.StepTick();
                    Assert.False(engine.Communication.Traps.HasLive(team, engine.CurrentTick),
                        $"tick {engine.CurrentTick}: team {team} kept a trap through its own possession");
                }
            }

            previousPossession = engine.Possession.PossessionTeam;
        }
    }

    [Fact]
    public void AnInactiveParticipantReleasesHisTrapRole()
    {
        var engine = new MatchSimulationEngine(TouchlineTrapFixture());
        TrapContract? trap = null;
        for (int tick = 1; tick <= 120 && trap is null; tick++)
        {
            engine.StepTick();
            if (engine.Communication.Traps.TryGetLive(TeamId.Home, engine.CurrentTick, out TrapContract live))
                trap = live;
        }

        Assert.NotNull(trap);
        int removed = trap!.Value.ScreenId;
        var onlyTrapEnded = engine.Communication.Traps.CountOfEnd(TrapEndReason.ParticipantRemoved);

        engine.Players.Single(p => p.Id == removed).IsSubstitutedOrInjured = true;
        engine.StepTick();

        Assert.True(engine.Communication.Traps.CountOfEnd(TrapEndReason.ParticipantRemoved) > onlyTrapEnded);
        Assert.False(engine.Communication.Traps.HasLive(TeamId.Home, engine.CurrentTick));
        Assert.False(engine.Communication.Traps.ContextFor(TeamId.Home, removed, engine.CurrentTick).IsActive);
    }

    [Fact]
    public void TheTrapProcess_IsTickForTickDeterministic()
    {
        var first = new MatchSimulationEngine(TouchlineTrapFixture());
        var second = new MatchSimulationEngine(TouchlineTrapFixture());
        for (int tick = 1; tick <= 200; tick++)
        {
            first.StepTick();
            second.StepTick();

            bool liveA = first.Communication.Traps.TryGetLive(TeamId.Home, first.CurrentTick, out TrapContract a);
            bool liveB = second.Communication.Traps.TryGetLive(TeamId.Home, second.CurrentTick, out TrapContract b);
            Assert.Equal(liveA, liveB);
            if (liveA) Assert.Equal(a, b);
        }

        TrapCoordinator boardA = first.Communication.Traps;
        TrapCoordinator boardB = second.Communication.Traps;
        Assert.Equal(boardA.RaisedCount, boardB.RaisedCount);
        foreach (TrapEndReason reason in Enum.GetValues<TrapEndReason>())
            Assert.Equal(boardA.CountOfEnd(reason), boardB.CountOfEnd(reason));
    }

    [Fact]
    public void TheAblationBuild_RaisesNoTrapAndCarriesNoContext()
    {
        var engine = new MatchSimulationEngine(TouchlineTrapFixture(enableTraps: false));
        for (int tick = 1; tick <= 120; tick++) engine.StepTick();

        Assert.Equal(0, engine.Communication.Traps.RaisedCount);
        Assert.Equal(0, engine.Communication.Traps.LiveCount(engine.CurrentTick));
        foreach (FootballerState player in engine.Players)
        {
            if (!player.IsActiveOnPitch) continue;
            Assert.False(engine.GetLastObservation(player.Id)!.Trap.IsActive,
                $"#{player.Id} carries a trap context with the feature switched off");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    private static bool TryFindVisible(
        in FootballerObservation observation, int playerId, out Vector2 positionXZ)
    {
        IReadOnlyList<PerceivedEntity> visible = observation.VisiblePlayers;
        for (int index = 0; index < visible.Count; index++)
        {
            if (visible[index].PlayerId != playerId) continue;
            positionXZ = new Vector2(visible[index].EstimatedPosition.X, visible[index].EstimatedPosition.Z);
            return true;
        }

        positionXZ = default;
        return false;
    }

    /// <summary>Rebuilds the fixture with one player's controller replaced (the carrier's scripted escape).</summary>
    private static MatchScenario CopyWithCarrierController(
        MatchScenario source, IFootballerController controller)
    {
        ScenarioPlayer carrier = source.Players.Single(p => p.Id == AwayCarrier);
        source.WithPlayer(carrier.WithController(controller));
        return source;
    }
}
