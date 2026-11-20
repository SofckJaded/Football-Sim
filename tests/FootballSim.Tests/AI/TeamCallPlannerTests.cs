using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A7.2 — the call producer.
///
/// Each rule is a pure function of the caller's own <see cref="FootballerObservation"/>, so each test here
/// states one picture and asks what that player would shout. The pair that matters most for the milestone's
/// knowledge boundary is the man-on one: the shout fires only from an opponent the caller can actually see,
/// and an identical picture without that sighting produces nothing — the caller cannot reveal what he did
/// not observe. <see cref="TeamCallPreferenceTests"/> and <see cref="TeamCallScenarioTests"/> prove the
/// consumer and the delay.
/// </summary>
public sealed class TeamCallPlannerTests
{
    private const int SelfId = 8;
    private const int CarrierId = 9;
    private const int PresserId = 201;
    private const int RunnerId = 10;

    private const float AttackDirX = 1f;

    // ---------------------------------------------------------------------------------------------
    // Man-on.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ManOn_IsShouted_WhenTheCallerSeesAnOpponentCloseToTheCarrier()
    {
        FootballerObservation obs = Observation(
            selfId: SelfId, role: TacticalRole.CentralMidfielder, selfXZ: new Vector2(8f, 3f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f), Opponent(PresserId, 1.2f, 0.4f)]);

        Assert.True(TeamCallPlanner.TryBuildManOn(obs, out TeamCallDraft draft));
        Assert.Equal(TeamCallKind.ManOn, draft.Kind);
        Assert.Equal(CarrierId, draft.TargetPlayerId);
        Assert.InRange(draft.Strength01, 0f, 1f);
    }

    [Fact]
    public void ManOn_IsNotShouted_WhenTheOpponentIsFarFromTheCarrier()
    {
        FootballerObservation obs = Observation(
            selfId: SelfId, role: TacticalRole.CentralMidfielder, selfXZ: new Vector2(8f, 3f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f), Opponent(PresserId, 6f, 0f)]);

        Assert.False(TeamCallPlanner.TryBuildManOn(obs, out _));
    }

    [Fact]
    public void ManOn_IsNotShouted_FromAStaleOrBarelySeenSighting()
    {
        FootballerObservation stale = Observation(
            selfId: SelfId, role: TacticalRole.CentralMidfielder, selfXZ: new Vector2(8f, 3f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f), Opponent(PresserId, 1.2f, 0.4f, ageTicks: 120)]);
        Assert.False(TeamCallPlanner.TryBuildManOn(stale, out _));

        FootballerObservation faint = Observation(
            selfId: SelfId, role: TacticalRole.CentralMidfielder, selfXZ: new Vector2(8f, 3f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f), Opponent(PresserId, 1.2f, 0.4f, confidence: 0.15f)]);
        Assert.False(TeamCallPlanner.TryBuildManOn(faint, out _));
    }

    [Fact]
    public void ManOn_RequiresTheOpponentInTheCallersOwnPicture()
    {
        // Two worlds that differ only in whether the caller saw the opponent: the call cannot invent the
        // sighting the perception system did not deliver. This is the milestone's knowledge boundary.
        FootballerObservation saw = Observation(
            selfId: SelfId, role: TacticalRole.CentralMidfielder, selfXZ: new Vector2(8f, 3f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f), Opponent(PresserId, 1.2f, 0.4f)]);

        FootballerObservation didNotSee = Observation(
            selfId: SelfId, role: TacticalRole.CentralMidfielder, selfXZ: new Vector2(8f, 3f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f)]);

        Assert.True(TeamCallPlanner.TryBuildManOn(saw, out _));
        Assert.False(TeamCallPlanner.TryBuildManOn(didNotSee, out _));
    }

    [Fact]
    public void ManOn_IsNotShouted_ByAPlayerTooFarFromTheCarrierToJudge()
    {
        FootballerObservation obs = Observation(
            selfId: SelfId, role: TacticalRole.CentralMidfielder, selfXZ: new Vector2(45f, 20f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f), Opponent(PresserId, 1.2f, 0.4f)]);

        Assert.False(TeamCallPlanner.TryBuildManOn(obs, out _));
    }

    [Fact]
    public void ManOn_IsNotShouted_WhenTheShouterIsTheCarrier()
    {
        // The carrier's own picture is not a shout to himself: he is the one being told.
        FootballerObservation obs = Observation(
            selfId: CarrierId, role: TacticalRole.CentralMidfielder, selfXZ: new Vector2(0f, 0f),
            controllingId: CarrierId,
            visible: [Opponent(PresserId, 1.2f, 0.4f)]);

        Assert.False(TeamCallPlanner.TryBuildManOn(obs, out _));
    }

    // ---------------------------------------------------------------------------------------------
    // Turn.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Turn_IsShouted_WhenNoBelievedOpponentIsNearTheCarrier()
    {
        FootballerObservation obs = Observation(
            selfId: SelfId, role: TacticalRole.CentralMidfielder, selfXZ: new Vector2(6f, 2f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f), Opponent(PresserId, 9f, 0f)]);

        Assert.True(TeamCallPlanner.TryBuildTurn(obs, out TeamCallDraft draft));
        Assert.Equal(TeamCallKind.Turn, draft.Kind);
        Assert.Equal(CarrierId, draft.TargetPlayerId);
    }

    [Fact]
    public void Turn_IsNotShouted_WhenTheCallerBelievesSomebodyIsOnTheCarrier()
    {
        // A stale memory still counts: the caller does not get to claim space on a sighting he distrusts.
        FootballerObservation obs = Observation(
            selfId: SelfId, role: TacticalRole.CentralMidfielder, selfXZ: new Vector2(6f, 2f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f), Opponent(PresserId, 4.5f, 0f, ageTicks: 200)]);

        Assert.False(TeamCallPlanner.TryBuildTurn(obs, out _));
    }

    // ---------------------------------------------------------------------------------------------
    // Switch.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Switch_IsRequestedByAFarSideUnmarkedTeamMate_WhenTheCarrierIsPressed()
    {
        // Caller far side (24 m lateral), open; an opponent is on the carrier, so a release is wanted.
        FootballerObservation obs = Observation(
            selfId: 11, role: TacticalRole.Winger, selfXZ: new Vector2(0f, 24f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f), Opponent(PresserId, 2.5f, 1f)]);

        Assert.True(TeamCallPlanner.TryBuildSwitch(obs, out TeamCallDraft draft));
        Assert.Equal(TeamCallKind.Switch, draft.Kind);
        Assert.Equal(CarrierId, draft.TargetPlayerId);
        Assert.Equal(11, draft.NamedPlayerId);
    }

    [Fact]
    public void Switch_IsNotRequested_WhenTheCallerIsMarkedOrNearTheBall()
    {
        FootballerObservation marked = Observation(
            selfId: 11, role: TacticalRole.Winger, selfXZ: new Vector2(0f, 24f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f), Opponent(PresserId, 2.5f, 1f), Opponent(205, 1f, 23f)]);
        Assert.False(TeamCallPlanner.TryBuildSwitch(marked, out _));

        FootballerObservation nearBall = Observation(
            selfId: 11, role: TacticalRole.Winger, selfXZ: new Vector2(0f, 9f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f), Opponent(PresserId, 2.5f, 1f)]);
        Assert.False(TeamCallPlanner.TryBuildSwitch(nearBall, out _));
    }

    [Fact]
    public void Switch_IsNotRequested_WhenTheCarrierHasTime()
    {
        FootballerObservation obs = Observation(
            selfId: 11, role: TacticalRole.Winger, selfXZ: new Vector2(0f, 24f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f), Opponent(PresserId, 9f, 0f)]);

        Assert.False(TeamCallPlanner.TryBuildSwitch(obs, out _));
    }

    // ---------------------------------------------------------------------------------------------
    // Run.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Run_IsRequestedByTheCarrier_ForTheMostAdvancedTeamMateInSpace()
    {
        FootballerObservation obs = Observation(
            selfId: CarrierId, role: TacticalRole.CentralMidfielder, selfXZ: new Vector2(0f, 0f),
            controllingId: CarrierId,
            visible:
            [
                Mate(RunnerId, 18f, 4f),
                Mate(12, 8f, -10f),
                Opponent(PresserId, 26f, 0f)
            ]);

        Assert.True(TeamCallPlanner.TryBuildRun(obs, out TeamCallDraft draft));
        Assert.Equal(TeamCallKind.Run, draft.Kind);
        Assert.Equal(RunnerId, draft.TargetPlayerId);
    }

    [Fact]
    public void Run_IsNotRequested_ForACrowdedOrTrailingTeamMate()
    {
        FootballerObservation crowded = Observation(
            selfId: CarrierId, role: TacticalRole.CentralMidfielder, selfXZ: new Vector2(0f, 0f),
            controllingId: CarrierId,
            visible: [Mate(RunnerId, 18f, 4f), Opponent(PresserId, 16f, 3f)]);
        Assert.False(TeamCallPlanner.TryBuildRun(crowded, out _));

        FootballerObservation trailing = Observation(
            selfId: CarrierId, role: TacticalRole.CentralMidfielder, selfXZ: new Vector2(0f, 0f),
            controllingId: CarrierId,
            visible: [Mate(RunnerId, -12f, 4f), Opponent(PresserId, 26f, 0f)]);
        Assert.False(TeamCallPlanner.TryBuildRun(trailing, out _));
    }

    [Fact]
    public void Run_IsOnlyRequestedByThePlayerOnTheBall()
    {
        FootballerObservation obs = Observation(
            selfId: SelfId, role: TacticalRole.CentralMidfielder, selfXZ: new Vector2(6f, 0f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f), Mate(RunnerId, 18f, 4f), Opponent(PresserId, 26f, 0f)]);

        Assert.False(TeamCallPlanner.TryBuildRun(obs, out _));
    }

    // ---------------------------------------------------------------------------------------------
    // Cover.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Cover_IsShoutedByTheSecondMan_WhenHeIsGoalSideAndBehindThePresser()
    {
        // Home attacks +X, so their own goal is at -X: the second man at -8 is goal-side of the carrier at 2.
        FootballerObservation obs = Observation(
            selfId: 103, role: TacticalRole.CenterBack, selfXZ: new Vector2(-8f, -2f),
            controllingId: PresserId, possessing: TeamId.Away,
            visible: [Opponent(PresserId, 2f, 5f), Opponent(209, 8f, -4f)],
            supportPresserId: 103, responderId: 104);

        Assert.True(TeamCallPlanner.TryBuildCover(obs, out TeamCallDraft draft));
        Assert.Equal(TeamCallKind.Cover, draft.Kind);
        Assert.Equal(104, draft.TargetPlayerId);
    }

    [Fact]
    public void Cover_IsNotShouted_WhenTheCallerIsNotTheElectedSecondMan_OrIsNotGoalSide()
    {
        FootballerObservation notElected = Observation(
            selfId: 103, role: TacticalRole.CenterBack, selfXZ: new Vector2(-8f, -2f),
            controllingId: PresserId, possessing: TeamId.Away,
            visible: [Opponent(PresserId, 2f, 5f)],
            supportPresserId: 105, responderId: 104);
        Assert.False(TeamCallPlanner.TryBuildCover(notElected, out _));

        FootballerObservation inFront = Observation(
            selfId: 103, role: TacticalRole.CenterBack, selfXZ: new Vector2(9f, -2f),
            controllingId: PresserId, possessing: TeamId.Away,
            visible: [Opponent(PresserId, 2f, 5f)],
            supportPresserId: 103, responderId: 104);
        Assert.False(TeamCallPlanner.TryBuildCover(inFront, out _));
    }

    // ---------------------------------------------------------------------------------------------
    // Step / drop.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Step_IsShoutedByTheDesignatedLineCaller_WhenTheCarrierIsNotAdvancing()
    {
        FootballerObservation obs = DefendingObservation(
            selfXZ: new Vector2(-20f, 3f),
            carrierXZ: new Vector2(-6f, 0f), carrierVelocity: Vector3.Zero,
            extra: [Mate(102, -34f, -12f)]);

        Assert.True(TeamCallPlanner.TryBuildStep(obs, out TeamCallDraft draft));
        Assert.Equal(TeamCallKind.Step, draft.Kind);
        Assert.Equal(0, draft.TargetPlayerId);
    }

    [Fact]
    public void Step_IsNotShouted_WhenTheCarrierIsDrivingAtThisGoal()
    {
        FootballerObservation obs = DefendingObservation(
            selfXZ: new Vector2(-20f, 3f),
            carrierXZ: new Vector2(-6f, 0f), carrierVelocity: new Vector3(-3.5f, 0f, 0f),
            extra: [Mate(102, -34f, -12f)]);

        Assert.False(TeamCallPlanner.TryBuildStep(obs, out _));
    }

    [Fact]
    public void Step_IsNotShouted_WhenAnotherLinePlayerIsCloserToTheBall()
    {
        // The nearest line player is the designated voice; this one is not it.
        FootballerObservation obs = DefendingObservation(
            selfXZ: new Vector2(-20f, 3f),
            carrierXZ: new Vector2(-6f, 0f), carrierVelocity: Vector3.Zero,
            extra: [LineMate(103, -10f, -2f)]);

        Assert.False(TeamCallPlanner.TryBuildStep(obs, out _));
    }

    [Fact]
    public void Drop_IsShouted_WhenAnOpponentRunsInBehindTheLine()
    {
        FootballerObservation obs = DefendingObservation(
            selfXZ: new Vector2(-25f, 0f),
            carrierXZ: new Vector2(-10f, 0f), carrierVelocity: Vector3.Zero,
            extra: [Opponent(209, -33f, 2f, velocity: new Vector3(-3f, 0f, 0f))]);

        Assert.True(TeamCallPlanner.TryBuildDrop(obs, out TeamCallDraft draft));
        Assert.Equal(TeamCallKind.Drop, draft.Kind);
        Assert.Equal(0, draft.TargetPlayerId);
    }

    [Fact]
    public void Drop_IsNotShouted_ForAnOpponentRunningAwayFromGoal()
    {
        FootballerObservation obs = DefendingObservation(
            selfXZ: new Vector2(-25f, 0f),
            carrierXZ: new Vector2(-10f, 0f), carrierVelocity: Vector3.Zero,
            extra: [Opponent(209, -33f, 2f, velocity: new Vector3(3f, 0f, 0f))]);

        Assert.False(TeamCallPlanner.TryBuildDrop(obs, out _));
    }

    // ---------------------------------------------------------------------------------------------
    // The producer's bounds and lifecycle.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void APlayerSaysOneThingPerTick_AndManOnOutranksSwitch()
    {
        // The caller is far-side and open (switch) while the opponent is on the carrier (man-on too).
        FootballerObservation obs = Observation(
            selfId: 11, role: TacticalRole.Winger, selfXZ: new Vector2(0f, 24f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f), Opponent(PresserId, 2.5f, 1f)]);

        Assert.True(TeamCallPlanner.TryBuildSwitch(obs, out _));
        Assert.True(TeamCallPlanner.TryBuildManOn(obs, out _));
        Assert.True(TeamCallPlanner.TryBuild(obs, out TeamCallDraft chosen));
        Assert.Equal(TeamCallKind.ManOn, chosen.Kind);
    }

    [Fact]
    public void Publish_UsesTheConfiguredDelayAndTheKindsLifetime()
    {
        FootballerObservation obs = Observation(
            selfId: SelfId, role: TacticalRole.CentralMidfielder, selfXZ: new Vector2(8f, 3f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f), Opponent(PresserId, 1.2f, 0.4f)]);

        var board = new TeamCallBoard();
        var ai = new AiRuntimeConfig { TeamCallCommunicationDelayTicks = 24 };
        TeamCallPlanner.Publish(obs, board, ai);

        Assert.Equal(1, board.PublishedCount);
        int due = obs.Tick + 24;
        Assert.False(board.TryGetPlayerCall(TeamId.Home, CarrierId, due - 1, out _));
        Assert.True(board.TryGetPlayerCall(TeamId.Home, CarrierId, due, out TeamCall call));
        Assert.Equal(TeamCallKind.ManOn, call.Kind);
        Assert.Equal(due + TeamCallPlanner.ManOnLifetimeTicks, call.ExpiresTick);
    }

    [Fact]
    public void CallsOff_PublishesNothing()
    {
        FootballerObservation obs = Observation(
            selfId: SelfId, role: TacticalRole.CentralMidfielder, selfXZ: new Vector2(8f, 3f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f), Opponent(PresserId, 1.2f, 0.4f)]);

        var board = new TeamCallBoard();
        TeamCallPlanner.Publish(obs, board, new AiRuntimeConfig { EnableTeamCalls = false });

        Assert.Equal(0, board.PublishedCount);
    }

    [Fact]
    public void AControllerWithoutAMatchClock_DoesNotShout()
    {
        // A standalone controller has no tick to delay against, so it must not publish calls that cannot
        // be expired by the match clock.
        FootballerObservation obs = Observation(
            selfId: SelfId, role: TacticalRole.CentralMidfielder, selfXZ: new Vector2(8f, 3f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f), Opponent(PresserId, 1.2f, 0.4f)],
            tick: 0);

        var board = new TeamCallBoard();
        TeamCallPlanner.Publish(obs, board, AiRuntimeConfig.Default);

        Assert.Equal(0, board.PublishedCount);
    }

    [Fact]
    public void TheSamePicture_ProducesTheSameShout()
    {
        FootballerObservation obs = Observation(
            selfId: SelfId, role: TacticalRole.CentralMidfielder, selfXZ: new Vector2(8f, 3f),
            controllingId: CarrierId,
            visible: [Mate(CarrierId, 0f, 0f), Opponent(PresserId, 1.2f, 0.4f)]);

        Assert.True(TeamCallPlanner.TryBuild(obs, out TeamCallDraft first));
        Assert.True(TeamCallPlanner.TryBuild(obs, out TeamCallDraft second));
        Assert.Equal(first, second);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers: one picture, stated explicitly.
    // ---------------------------------------------------------------------------------------------

    private static FootballerObservation Observation(
        int selfId,
        TacticalRole role,
        Vector2 selfXZ,
        int? controllingId,
        IReadOnlyList<PerceivedEntity> visible,
        TeamId possessing = TeamId.Home,
        int? supportPresserId = null,
        int? responderId = null,
        int tick = 500) => new()
    {
        SelfId = selfId,
        SelfTeam = TeamId.Home,
        SelfRole = role,
        SelfPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = controllingId is int carrier
            ? new Vector3(PositionOf(visible, carrier).X, 0.11f, PositionOf(visible, carrier).Z)
            : new Vector3(0f, 0.11f, 0f),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = controllingId is null ? null : possessing,
        BallLastTouchedByPlayerId = controllingId,
        BallLastTouchedByTeam = controllingId is null ? null : possessing,
        BallControlState = controllingId is null ? BallControlState.Loose : BallControlState.Controlled,
        BallControllingPlayerId = controllingId,
        SupportPresserPlayerId = supportPresserId,
        BallResponderPlayerId = responderId,
        TacticalAnchorPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
        AttackingDirectionX = AttackDirX,
        MatchTimeSeconds = 10f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = tick,
        VisiblePlayers = visible,
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(role),
        SelfAttributes = new PlayerAttributes()
    };

    /// <summary>A defending line player's picture: the opponent carrier plus the stated extra entities.</summary>
    private static FootballerObservation DefendingObservation(
        Vector2 selfXZ,
        Vector2 carrierXZ,
        Vector3 carrierVelocity,
        IReadOnlyList<PerceivedEntity> extra)
    {
        var visible = new List<PerceivedEntity>
        {
            new(213, TeamId.Away, TacticalRole.Striker, new Vector3(carrierXZ.X, 0f, carrierXZ.Y),
                carrierVelocity, 0.95f)
            {
                Capability = CapabilityEstimate.Generic
            }
        };
        visible.AddRange(extra);

        return new FootballerObservation
        {
            SelfId = 102,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.CenterBack,
            SelfPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(-1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = false,
            ControlQuality01 = 0f,
            PerceivedBallPosition = new Vector3(carrierXZ.X, 0.11f, carrierXZ.Y),
            PerceivedBallVelocity = carrierVelocity,
            PossessingTeam = TeamId.Away,
            BallLastTouchedByPlayerId = 213,
            BallLastTouchedByTeam = TeamId.Away,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = 213,
            TacticalAnchorPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
            AttackingDirectionX = AttackDirX,
            MatchTimeSeconds = 10f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = 500,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.CenterBack),
            SelfAttributes = new PlayerAttributes()
        };
    }

    private static PerceivedEntity Mate(int id, float x, float z, Vector3? velocity = null) =>
        new(id, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(x, 0f, z),
            velocity ?? Vector3.Zero, 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };

    /// <summary>A team-mate who holds a line slot, for the designated-line-caller rule.</summary>
    private static PerceivedEntity LineMate(int id, float x, float z) =>
        new(id, TeamId.Home, TacticalRole.CenterBack, new Vector3(x, 0f, z), Vector3.Zero, 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };

    private static PerceivedEntity Opponent(
        int id,
        float x,
        float z,
        Vector3? velocity = null,
        int ageTicks = 0,
        float confidence = 0.95f) =>
        new(id, TeamId.Away, TacticalRole.CenterBack, new Vector3(x, 0f, z),
            velocity ?? Vector3.Zero, confidence)
        {
            Capability = CapabilityEstimate.Generic,
            ObservedAgeTicks = ageTicks
        };

    private static Vector3 PositionOf(IReadOnlyList<PerceivedEntity> visible, int playerId)
    {
        for (int i = 0; i < visible.Count; i++)
        {
            if (visible[i].PlayerId == playerId) return visible[i].EstimatedPosition;
        }

        return Vector3.Zero;
    }
}
