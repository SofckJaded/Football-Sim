using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A9.2 — the run-coordination ablation (<see cref="AiRuntimeConfig.EnableRunCoordination"/>).
///
/// The switch removes A5.3's team channel and nothing else. An adopted run is a promise the runner makes
/// to his team: where he means to meet the ball, when, and how sure he is, heard after the communication
/// delay. Off, the promise is never made — nothing is published, no team-mate sees, claims or releases it,
/// and the diagnostic counters stay at zero — while the runner's own run (A5.1 path set, A5.2 frozen window,
/// release by that window) is untouched.
///
/// These tests pin the two sides of the A/B with exact assertions: the channel-level behaviour of the board,
/// the configuration wiring into the match's one communication context, and the consumer-level behaviour of
/// <see cref="RunCandidateGenerator"/>, which takes the board as a constructor parameter.
/// </summary>
public sealed class A9RunAblationTests
{
    private static readonly PitchDimensions Pitch = new();

    private const int CarrierId = 8;
    private const int RunnerId = 9;

    // ---------------------------------------------------------------------------------------------
    // The board: the enabled channel announces after the delay; the ablated channel announces nothing.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultBoard_AnnouncesTheRun_AfterTheCommunicationDelay()
    {
        var board = new RunAdvertisementBoard();
        Assert.True(board.Enabled, "the shipped board is the live channel");

        RunIntent intent = Intent(RunKind.InBehind, new Vector2(20f, 4f), publishedTick: 100,
            meetTick: 160, expiresTick: 220, confidence: 0.8f);
        board.Publish(TeamId.Home, RunnerId, intent, 100);

        Assert.Equal(1, board.PublishedCount);

        // Before the delay the team-mate has not heard it: the delay is the channel, not an implementation
        // detail a direct read could skip.
        int due = 100 + RunAdvertisementBoard.CommunicationDelayTicks;
        Assert.False(board.TryGetAdvertisement(TeamId.Home, RunnerId, due - 1, out _));
        Assert.Equal(0, board.VisibleCount(TeamId.Home, due - 1));

        // After it, the promise is readable through both read paths.
        Assert.True(board.TryGetAdvertisement(TeamId.Home, RunnerId, due, out RunAdvertisement heard));
        Assert.Equal(due, heard.VisibleTick);
        Assert.Equal(intent.Path.MeetXZ, heard.MeetXZ);
        Assert.Equal(intent.Path.MeetTick, heard.MeetTick);
        Assert.Equal(intent.Confidence01, heard.Confidence01, 3);
        Assert.Equal(1, board.VisibleCount(TeamId.Home, due));
        Assert.True(board.TryGetVisibleAt(TeamId.Home, 0, due, out RunAdvertisement visible));
        Assert.Equal(RunnerId, visible.PlayerId);
    }

    [Fact]
    public void AnAblatedBoard_PublishesNothing_AndReportsNothing()
    {
        var board = new RunAdvertisementBoard(enabled: false);
        Assert.False(board.Enabled, "an ablated board must never publish or deliver");

        RunIntent intent = Intent(RunKind.InBehind, new Vector2(20f, 4f), publishedTick: 100,
            meetTick: 160, expiresTick: 220);
        board.Publish(TeamId.Home, RunnerId, intent, 100);
        board.Publish(TeamId.Home, RunnerId, intent, 101);   // would be an in-place renewal on a live board

        Assert.Equal(0, board.PublishedCount);

        // False before the delay, false after it, false once the run's own window has closed too.
        int due = 100 + RunAdvertisementBoard.CommunicationDelayTicks;
        Assert.False(board.TryGetAdvertisement(TeamId.Home, RunnerId, due - 1, out _));
        Assert.False(board.TryGetAdvertisement(TeamId.Home, RunnerId, due, out _));
        Assert.False(board.TryGetAdvertisement(TeamId.Home, RunnerId, due + 60, out _));
        Assert.Equal(0, board.VisibleCount(TeamId.Home, due));

        // No entry exists at any slot, so nothing can be enumerated either.
        for (int i = 0; i < RunAdvertisementBoard.Capacity; i++)
            Assert.False(board.TryGetVisibleAt(TeamId.Home, i, due, out _),
                $"slot {i} must be empty on an ablated board");

        // Claim, withdrawal and sweep paths behave exactly as they do on an empty board, and the diagnostics
        // stay at zero: an artifact must not report a team channel that never existed.
        board.Claim(TeamId.Home, RunnerId, CarrierId, arriveTick: 160, tick: 101);
        Assert.Equal(0, board.ClaimedCount);
        Assert.False(board.TryGetClaim(TeamId.Home, RunnerId, 160, out _));

        board.Withdraw(TeamId.Home, RunnerId);
        Assert.Equal(0, board.WithdrawnCount);

        board.Clear(TeamId.Home);
        Assert.Equal(0, board.VisibleCount(TeamId.Home, due));
        Assert.Equal(0, board.PublishedCount);
    }

    // ---------------------------------------------------------------------------------------------
    // Configuration: the switch reaches the match's one communication context, and defaults are on.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheConfiguredCommunicationContext_FollowsTheRunCoordinationSwitch()
    {
        // Unconfigured fixtures and drills keep today's channel.
        Assert.True(new MatchCommunicationContext().Runs.Enabled);
        Assert.True(new MatchCommunicationContext(AiRuntimeConfig.Default).Runs.Enabled);
        Assert.True(new MatchCommunicationContext(new AiRuntimeConfig()).Runs.Enabled);

        // The ablation reaches the one match-owned channel the AI's runs are published to.
        Assert.False(new MatchCommunicationContext(new AiRuntimeConfig { EnableRunCoordination = false })
            .Runs.Enabled);
    }

    [Fact]
    public void RunCoordinationDefault_IsOn_AndDescribeStatesTheAblation()
    {
        AiRuntimeConfig defaults = AiRuntimeConfig.Default;
        Assert.True(defaults.EnableRunCoordination,
            "A5.3 ships coordinated runs; the switch exists to ablate them, not to disable them quietly");

        Assert.Contains("runs(on)", defaults.Describe());
        Assert.Contains("runs(off)", new AiRuntimeConfig { EnableRunCoordination = false }.Describe());

        // Off is a supported ablation, not a validation error.
        AiRuntimeConfigValidation.Validate(new AiRuntimeConfig { EnableRunCoordination = false });
    }

    // ---------------------------------------------------------------------------------------------
    // The consumer: the same adopted run is announced on a live board and silent on an ablated one.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AnAdoptedRun_IsAnnouncedToTheTeam_OnALiveBoard_ButNotOnAnAblatedOne()
    {
        FootballerObservation obs = DepthObservation(tick: 100);

        // Live board: the generator publishes the adopted run and a team-mate hears it after the delay.
        var liveBoard = new RunAdvertisementBoard();
        var liveGenerator = new RunCandidateGenerator(Pitch, liveBoard);
        RunPath liveAdopted = AdoptInBehind(liveGenerator, obs);

        Assert.True(liveBoard.Enabled);
        Assert.Equal(1, liveBoard.PublishedCount);
        int due = obs.Tick + RunAdvertisementBoard.CommunicationDelayTicks;
        Assert.False(liveBoard.TryGetAdvertisement(TeamId.Home, obs.SelfId, due - 1, out _));
        Assert.True(liveBoard.TryGetAdvertisement(TeamId.Home, obs.SelfId, due, out RunAdvertisement heard));
        Assert.Equal(liveAdopted.MeetXZ, heard.MeetXZ);
        Assert.Equal(liveAdopted.MeetTick, heard.MeetTick);

        // Ablated board: the identical world and the identical generator call. The run is still adopted —
        // that is the runner's own A5.1/A5.2 state — but the team never hears about it.
        var offBoard = new RunAdvertisementBoard(enabled: false);
        var offGenerator = new RunCandidateGenerator(Pitch, offBoard);
        RunPath offAdopted = AdoptInBehind(offGenerator, obs);

        Assert.NotNull(offGenerator.Intent);
        Assert.Equal(offAdopted.MeetXZ, offGenerator.Intent!.Value.Path.MeetXZ);
        Assert.Equal(offAdopted.MeetTick, offGenerator.Intent.Value.Path.MeetTick);
        Assert.Equal(0, offBoard.PublishedCount);
        Assert.False(offBoard.TryGetAdvertisement(TeamId.Home, obs.SelfId, due - 1, out _));
        Assert.False(offBoard.TryGetAdvertisement(TeamId.Home, obs.SelfId, due, out _));
        Assert.Equal(0, offBoard.VisibleCount(TeamId.Home, due));
    }

    [Fact]
    public void WithTheChannelAblated_TheRunnersOwnRun_StillFreezesAndReleasesOnItsOwnWindow()
    {
        var board = new RunAdvertisementBoard(enabled: false);
        var generator = new RunCandidateGenerator(Pitch, board);
        FootballerObservation adoptedObs = DepthObservation(tick: 100);
        RunPath adopted = AdoptInBehind(generator, adoptedObs);
        RunIntent frozen = generator.Intent!.Value;

        // A claim is what extends an announced run (A5.3). On an ablated board no claim can exist, so even a
        // carrier naming a very late arrival must not hold the runner past his own window.
        board.Claim(TeamId.Home, adoptedObs.SelfId, CarrierId, arriveTick: adopted.ExpiresTick + 40, tick: 101);
        Assert.False(board.TryGetClaim(TeamId.Home, adoptedObs.SelfId, 101, out _));
        Assert.Equal(0, board.ClaimedCount);

        // Inside the window the run survives with its frozen geometry (A5.2), and still nothing is announced.
        FootballerObservation inside = DepthObservation(tick: 101);
        Assert.True(generator.Generate(inside, 30f, inside.Tick, 4) > 0);
        Assert.NotNull(generator.Intent);
        RunIntent kept = generator.Intent!.Value;
        Assert.Equal(frozen.AdoptedTick, kept.AdoptedTick);
        Assert.Equal(frozen.Path.MeetXZ, kept.Path.MeetXZ);
        Assert.Equal(frozen.Path.MeetTick, kept.Path.MeetTick);
        Assert.Equal(0, board.PublishedCount);

        // Past its own window, with no claim able to extend it, A5.1/A5.2's own release rule ends the run.
        FootballerObservation afterWindow = DepthObservation(tick: adopted.ExpiresTick + 1);
        generator.Generate(afterWindow, 30f, afterWindow.Tick, 4);
        Assert.Null(generator.Intent);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures — the high-line depth-runner fragment, kept locally so the test states its own world.
    // ---------------------------------------------------------------------------------------------

    private static PerceivedEntity Entity(
        int id,
        TeamId team,
        TacticalRole role,
        float x,
        float z,
        Vector3? velocity = null) =>
        new(id, team, role, new Vector3(x, 0f, z), velocity ?? Vector3.Zero, 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };

    private static RunIntent Intent(
        RunKind kind,
        Vector2 meet,
        int publishedTick,
        int meetTick,
        int expiresTick,
        float confidence = 0.8f) =>
        new(
            new RunPath(
                kind,
                meet - new Vector2(4f, 0f),
                meet,
                5f,
                publishedTick,
                meetTick,
                expiresTick,
                publishedTick,
                meet - new Vector2(4f, 0f),
                confidence),
            TacticalJob.DepthRun,
            publishedTick);

    /// <summary>An away centre-back in the space behind the line and the home carrier on the ball.</summary>
    private static List<PerceivedEntity> DepthWorld() => new()
    {
        Entity(CarrierId, TeamId.Home, TacticalRole.CentralMidfielder, 0f, 20f),
        Entity(25, TeamId.Away, TacticalRole.CenterBack, 24f, 2f)
    };

    private static FootballerObservation DepthObservation(int tick = 100) =>
        RunnerObservation(
            TacticalRole.Striker, new Vector2(12f, 2f), new Vector2(14f, 2f), DepthWorld(),
            new Vector2(0f, 20f), TacticalJob.DepthRun, tick);

    private static FootballerObservation RunnerObservation(
        TacticalRole role,
        Vector2 selfXZ,
        Vector2 dutyAnchorXZ,
        IReadOnlyList<PerceivedEntity> visible,
        Vector2 ballXZ,
        TacticalJob duty,
        int tick) => new()
    {
        SelfId = RunnerId,
        SelfTeam = TeamId.Home,
        SelfRole = role,
        SelfPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = false,
        ControlQuality01 = 0f,
        PerceivedBallPosition = new Vector3(ballXZ.X, 0.11f, ballXZ.Y),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = CarrierId,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = CarrierId,
        TacticalAnchorPosition = new Vector3(dutyAnchorXZ.X, 0f, dutyAnchorXZ.Y),
        TacticalJob = duty,
        TacticalJobAnchor = new Vector3(dutyAnchorXZ.X, 0f, dutyAnchorXZ.Y),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 0f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = tick,
        VisiblePlayers = visible,
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(role),
        SelfAttributes = new PlayerAttributes(),
        TeamSecurity = new RestDefence(3, 1f, 0.2f, 0.2f, 0.1f, 0.7f)
    };

    private static RunPath AdoptInBehind(RunCandidateGenerator generator, in FootballerObservation obs)
    {
        Assert.True(generator.Generate(obs, 30f, obs.Tick, 4) > 0);
        RunPath chosen = PathOfKind(generator, RunKind.InBehind);
        generator.NoteSelected(obs, RunTags.KeyOf(RunKind.InBehind));
        Assert.NotNull(generator.Intent);
        return chosen;
    }

    private static RunPath PathOfKind(RunCandidateGenerator generator, RunKind kind)
    {
        for (int i = 0; i < generator.Count; i++)
        {
            if (generator.PathAt(i).Kind == kind) return generator.PathAt(i);
        }

        throw new Xunit.Sdk.XunitException($"no {kind} path in the set");
    }
}
