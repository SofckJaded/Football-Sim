using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Rules;
using FootballSim.Scenarios;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A6.5b: the goalkeeper's remaining decisions.
///
/// Three things were missing and each is proved here on its own inputs:
/// <list type="number">
/// <item><b>The delivery.</b> The keeper had no answer for a cross: the sweep's gates were written for a ball
/// already inside the box, and the handling branch had no way to gather a dropping ball. A claim is now a race
/// over the point the ball is going to, gated on the law at that point (A6.6), and the mode reports whether
/// the contact will hold the ball or punch it.</item>
/// <item><b>The engagement point.</b> The same gates now judge the sweep and the 1v1 block — a race to the
/// point he would meet the ball at, not to wherever the ball happens to be.</item>
/// <item><b>The two phase duties.</b> Distribution is priced on the pass scale instead of a lane heuristic,
/// and his position is a possession-phase duty: an outlet behind the ball when his team has it.</item>
/// </list>
/// </summary>
public sealed class GoalkeeperClaimTests
{
    private static readonly PitchDimensions Pitch = new();
    private const float HomeGoalX = -52.5f;

    private const int KeeperId = 1;
    private const int AttackerId = 15;
    private const int MateId = 6;

    // ---------------------------------------------------------------------------------------------
    // The claim: a delivery is judged where it is going.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A ball dropping into the six-yard box with nobody near it: he goes and gathers it, at a meeting point
    /// inside his own area, and the reported arrival pace is one he can hold. The mode is the execution's own
    /// catch limit, so "Gather" is a prediction of the contact rather than a second rule.
    /// </summary>
    [Fact]
    public void ADeliveryNobodyContests_IsGathered()
    {
        FootballerState keeperState = Keeper(new Vector3(-46f, 0f, 0f));
        var keeper = new GoalkeeperController(Pitch);
        FootballerCommand command = keeper.Decide(
            Observe(keeperState, DroppingAt(new Vector3(-46f, 3.4f, 1f)), TeamId.Away),
            1f / 60f);

        GoalkeeperClaim claim = keeper.LastClaim;
        Assert.True(claim.ShouldAttack, claim.Describe());
        Assert.Equal(GoalkeeperClaimReason.KeeperFirst, claim.Reason);
        Assert.Equal(GoalkeeperClaimMode.Gather, claim.Mode);
        Assert.True(claim.WillHold);
        Assert.True(claim.ArrivalSpeedMps < BallContactRules.KeeperCatchSpeed(
                keeperState.Attributes.GoalkeepingReflex01),
            $"the fixture is meant to be a holdable ball: {claim.ArrivalSpeedMps:F1} m/s");
        Assert.True(GoalkeeperLawModel.IsInsideOwnPenaltyArea(Pitch, HomeGoalX, claim.MeetPoint),
            $"the meeting point {claim.MeetPoint} is not inside his area");
        Assert.Equal(GoalkeeperState.Claiming, keeper.State);
        Assert.StartsWith("GK:ClaimCross", keeper.GetDebugSummary());

        // And the body is sent at that point, not at the ball's current position.
        Vector2 towardMeet = new(claim.MeetPoint.X - keeperState.Position.X,
            claim.MeetPoint.Z - keeperState.Position.Z);
        Assert.True(Vector2.Dot(Vector2.Normalize(towardMeet), command.DesiredMoveDirectionXZ) > 0.5f,
            "the keeper did not move toward the meeting point");
    }

    /// <summary>
    /// The same delivery, with an attacker arriving level with him: the touch is a contest, and a keeper
    /// contesting a ball does not plan to hold it — it is a punch. He still goes, because denying the ball is
    /// worth more than leaving it.
    /// </summary>
    [Fact]
    public void AContestedDelivery_IsPunchedRatherThanCaught()
    {
        var keeper = new GoalkeeperController(Pitch);
        FootballerCommand command = keeper.Decide(
            Observe(Keeper(new Vector3(-48f, 0f, 0f)), DroppingAt(new Vector3(-46f, 3.4f, 1f)), TeamId.Away,
                attackerXZ: new Vector2(-46f, 0.6f)),
            1f / 60f);

        GoalkeeperClaim claim = keeper.LastClaim;
        Assert.True(claim.ShouldAttack, claim.Describe());
        Assert.True(claim.Reason == GoalkeeperClaimReason.Contested, claim.Describe());
        Assert.Equal(GoalkeeperClaimMode.Punch, claim.Mode);
        Assert.False(claim.WillHold);
        Assert.StartsWith("GK:PunchClear", keeper.GetDebugSummary());
        Assert.True(command.DesiredMoveDirectionXZ.Length() > 0f);
    }

    /// <summary>
    /// An attacker who is clearly there first: the keeper stays on his line. The delivery is his to win or
    /// lose, and charging a ball somebody else reaches half a second earlier is how a keeper concedes an
    /// empty net.
    ///
    /// The ball is on its way up rather than down: an attacker can head it out of the air at the top of its
    /// flight while the keeper's hands can only meet it in the band, later. That is the case this branch is
    /// for — the keeper's own race is a race he has already lost by the time he could play the ball at all.
    /// </summary>
    [Fact]
    public void AnAttackerFirstToTheDelivery_LeavesItToHim()
    {
        var keeper = new GoalkeeperController(Pitch);
        keeper.Decide(
            Observe(Keeper(new Vector3(-48f, 0f, 0f)), RisingAt(new Vector3(-46f, 0.9f, 1f)), TeamId.Away,
                attackerXZ: new Vector2(-46.3f, 0.6f)),
            1f / 60f);

        GoalkeeperClaim claim = keeper.LastClaim;
        Assert.False(claim.ShouldAttack, claim.Describe());
        Assert.Equal(GoalkeeperClaimReason.OpponentFirst, claim.Reason);
        Assert.Equal(GoalkeeperClaimMode.Stay, claim.Mode);
        Assert.Equal(GoalkeeperState.Positioning, keeper.State);
        Assert.DoesNotContain("ClaimCross", keeper.GetDebugSummary());
        Assert.DoesNotContain("PunchClear", keeper.GetDebugSummary());
    }

    /// <summary>
    /// The law decides whether he may claim at all, at the point he would meet the ball. The same kind of ball
    /// outside his area is not his to catch, and one a team-mate deliberately kicked back to him is not either
    /// — the two restrictions A6.6 implemented, now wired into the decision instead of only into the contact.
    /// </summary>
    [Fact]
    public void TheLawDecidesWhetherADeliveryMayBeClaimed()
    {
        var outsideKeeper = new GoalkeeperController(Pitch);
        outsideKeeper.Decide(
            Observe(Keeper(new Vector3(-33f, 0f, 0f)), DroppingAt(new Vector3(-34f, 3.4f, 1f)), TeamId.Away),
            1f / 60f);

        Assert.False(outsideKeeper.LastClaim.ShouldAttack, outsideKeeper.LastClaim.Describe());
        Assert.Equal(GoalkeeperClaimReason.OutsideArea, outsideKeeper.LastClaim.Reason);

        var backPassKeeper = new GoalkeeperController(Pitch);
        var backPass = new BallLastPlay(BallContactKind.Kick, KickIntentType.GroundPass, MateId, TeamId.Home);
        backPassKeeper.Decide(
            Observe(Keeper(new Vector3(-46f, 0f, 0f)), DroppingAt(new Vector3(-46f, 3.4f, 1f)), TeamId.Home,
                lastPlay: backPass),
            1f / 60f);

        Assert.False(backPassKeeper.LastClaim.ShouldAttack, backPassKeeper.LastClaim.Describe());
        Assert.Equal(GoalkeeperClaimReason.HandsBarred, backPassKeeper.LastClaim.Reason);
    }

    /// <summary>
    /// The hands are what the claim is for, so the meeting point has to be in the band only hands can reach.
    /// A ball on the deck is the sweep's business, and the claim declines it as no delivery at all rather than
    /// racing it a second time.
    /// </summary>
    [Fact]
    public void ABallOnTheDeck_IsNotADelivery()
    {
        var keeper = new GoalkeeperController(Pitch);
        var ball = new BallState
        {
            Position = new Vector3(-40f, 0.11f, 0f),
            PreviousPosition = new Vector3(-40f, 0.11f, 0f),
            Velocity = new Vector3(-4f, 0f, 0f)
        };

        keeper.Decide(Observe(Keeper(new Vector3(-46f, 0f, 0f)), ball, TeamId.Away), 1f / 60f);

        Assert.False(keeper.ClaimEvaluated);
        Assert.Equal(GoalkeeperClaimReason.NoDelivery, keeper.LastClaim.Reason);
    }

    // ---------------------------------------------------------------------------------------------
    // The 1v1 block: the same race, named.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// An opponent with the ball under control inside the area is a 1v1, not a loose ball. The model says so
    /// in its mode, and the controller's answer is to close him down on his feet — a block, with no dive and
    /// with the hands only where the law allows them (A6.6 gates that separately).
    /// </summary>
    [Fact]
    public void AnOpponentInPossessionInsideTheArea_IsBlockedOnHisFeet()
    {
        var keeper = new GoalkeeperController(Pitch);
        var ball = new BallState
        {
            Position = new Vector3(-44f, 0.11f, 0f),
            PreviousPosition = new Vector3(-44f, 0.11f, 0f),
            Velocity = new Vector3(-3f, 0f, 0.4f)
        };

        FootballerCommand command = keeper.Decide(
            Observe(Keeper(new Vector3(-48f, 0f, 0f)), ball, TeamId.Away,
                controlState: BallControlState.Controlled, controllingPlayerId: AttackerId),
            1f / 60f);

        Assert.True(keeper.SweepEvaluated, "the 1v1 was never evaluated");
        Assert.Equal(GoalkeeperRaceMode.Block, keeper.LastSweep.Mode);
        Assert.True(keeper.LastSweep.ShouldGo, keeper.LastSweep.Describe());
        Assert.StartsWith("GK:Block1v1", keeper.GetDebugSummary());
        Assert.False(command.RequestKeeperDive, "the keeper went to ground against a dribbler");
    }

    // ---------------------------------------------------------------------------------------------
    // The possession-phase duties.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The two standoffs, one rule: the shot-blocking angle while defending, and a real outlet while his team
    /// has the ball. A4.1's keeper-support pass only exists once he is 12 m off his line, so the support depth
    /// has to be able to reach that — the defensive one deliberately never does.
    /// </summary>
    [Fact]
    public void TheSupportDepth_IsAPhaseOfPlay()
    {
        // Ball on the halfway line: 10.5 m off his line in possession, 4.2 m (the cap) defending.
        Assert.Equal(10.5f, GoalkeeperSupportModel.StandoffMeters(52.5f, teamHasBall: true), precision: 2);
        Assert.Equal(4.2f, GoalkeeperSupportModel.StandoffMeters(52.5f, teamHasBall: false), precision: 2);

        // In the final third he is on the edge of his box, and never outside it.
        Assert.Equal(16f, GoalkeeperSupportModel.StandoffMeters(90f, teamHasBall: true), precision: 2);

        // The A4.1 support gate can only be met in possession.
        Assert.True(GoalkeeperSupportModel.IsSupportOption(70f, teamHasBall: true, requiredDepthMeters: 12f));
        Assert.False(GoalkeeperSupportModel.IsSupportOption(70f, teamHasBall: false, requiredDepthMeters: 12f));
        Assert.False(GoalkeeperSupportModel.IsSupportOption(40f, teamHasBall: true, requiredDepthMeters: 12f));
    }

    /// <summary>
    /// The duty has to reach his feet, not only the model: with his team on the ball at the halfway line the
    /// keeper walks out past the old 4.2 m ceiling; with the opponent on the ball he stays inside it.
    /// </summary>
    [Fact]
    public void TheKeeperStandsFurtherOffHisLine_WhenHisTeamHasTheBall()
    {
        float inPossession = KeeperStandoffAfter(ownerIsHome: true);
        float defending = KeeperStandoffAfter(ownerIsHome: false);

        Assert.True(inPossession > 4.5f,
            $"the support duty did not move him: {inPossession:F1} m off his line");
        Assert.True(defending <= 5.0f,
            $"the keeper left his line while defending: {defending:F1} m off it");
        Assert.True(inPossession > defending + 3f,
            $"the two phases look the same ({inPossession:F1} m against {defending:F1} m)");
    }

    // ---------------------------------------------------------------------------------------------
    // Distribution: priced, on the same scale as every other pass.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Two receivers at the same distance, one with a defender in the lane: the keeper plays the open one, and
    /// the ball carries the completion it was chosen on. The old rule scored lane clearance × 2 and a hand-set
    /// profile bias; this is the pass-success model, the reception value and the action scale.
    /// </summary>
    [Fact]
    public void TheDistribution_IsPriced_AndPlaysTheOpenReceiver()
    {
        var keeper = new GoalkeeperController(Pitch);
        var open = new PerceivedEntity(11, TeamId.Home, TacticalRole.CenterBack,
            new Vector3(-35f, 0f, -10f), Vector3.Zero, 1f);
        var covered = new PerceivedEntity(12, TeamId.Home, TacticalRole.CenterBack,
            new Vector3(-35f, 0f, 10f), Vector3.Zero, 1f);
        // Standing on the line from the keeper to the covered receiver, a fifth of the way along it.
        var blocker = new PerceivedEntity(AttackerId, TeamId.Away, TacticalRole.Striker,
            new Vector3(-44.9f, 0f, 4f), Vector3.Zero, 1f);

        keeper.Decide(
            ObserveHolding(Keeper(new Vector3(-51.9f, 0f, 0f)), new PerceivedEntity[] { open, covered, blocker }),
            1f / 60f);

        GoalkeeperDistribution distribution = keeper.LastDistribution;
        Assert.Equal(GoalkeeperDistributionKind.Ground, distribution.Kind);
        Assert.Equal(11, distribution.ReceiverId);
        Assert.InRange(distribution.Completion01, 0.05f, 0.97f);
        Assert.StartsWith("GK:PassTo#11", keeper.GetDebugSummary());
    }

    /// <summary>
    /// Nobody worth playing the ball to: the keeper clears it. The clearance is on the same scale as any other
    /// pass, so "no outlet" is a priced answer rather than a failed search — and the release still happens,
    /// because a keeper may not simply keep the ball.
    /// </summary>
    [Fact]
    public void WithNoOutlet_TheKeeperClearsIt()
    {
        var keeper = new GoalkeeperController(Pitch);
        keeper.Decide(
            ObserveHolding(Keeper(new Vector3(-51.9f, 0f, 0f)), Array.Empty<PerceivedEntity>()),
            1f / 60f);

        Assert.Equal(GoalkeeperDistributionKind.SafeBall, keeper.LastDistribution.Kind);
        Assert.Equal("GK:SafeDistribution", keeper.GetDebugSummary());
    }

    /// <summary>
    /// Audit F03/F20: no distribution may run through the keeper's own goal mouth, however well it is priced.
    /// The only team-mate here stands near his own goal line, so the ball to him would cross the mouth.
    /// </summary>
    [Fact]
    public void ADistributionThatWouldCrossTheOwnGoal_IsRefused()
    {
        var keeper = new GoalkeeperController(Pitch);
        var onlyMate = new PerceivedEntity(11, TeamId.Home, TacticalRole.FullBack,
            new Vector3(-52.4f, 0f, 2.5f), Vector3.Zero, 1f);

        keeper.Decide(
            ObserveHolding(Keeper(new Vector3(-43f, 0f, 6f)), new PerceivedEntity[] { onlyMate }),
            1f / 60f);

        Assert.Equal(GoalkeeperDistributionKind.SafeBall, keeper.LastDistribution.Kind);
        Assert.Equal("GK:SafeDistribution", keeper.GetDebugSummary());
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A ball hanging in the air over his area, falling from above the handling height: slow enough that a
    /// keeper standing a metre away can be under it, high enough that only the hands can play it.
    /// </summary>
    private static BallState DroppingAt(Vector3 position) => new()
    {
        Position = position,
        PreviousPosition = position,
        Velocity = Vector3.Zero
    };

    /// <summary>The same ball, hoofed upward: it crosses the hands' band twice, and the attacker can head it on
    /// the way up while the keeper can only reach it later.</summary>
    private static BallState RisingAt(Vector3 position) => new()
    {
        Position = position,
        PreviousPosition = position,
        Velocity = new Vector3(0f, 6f, 0f)
    };

    private static FootballerState Keeper(Vector3 position) => new()
    {
        Id = KeeperId,
        Team = TeamId.Home,
        ShirtNumber = 1,
        Role = TacticalRole.Goalkeeper,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(TacticalRole.Goalkeeper),
        Position = position,
        Velocity = Vector3.Zero,
        FacingXZ = new Vector2(1f, 0f)
    };

    /// <summary>One keeper's belief: the ball, and the one opponent he can see.</summary>
    private static FootballerObservation Observe(
        FootballerState keeper,
        BallState ball,
        TeamId possession,
        Vector2? attackerXZ = null,
        BallLastPlay? lastPlay = null,
        BallControlState controlState = BallControlState.Loose,
        int? controllingPlayerId = null)
    {
        Vector2 attacker = attackerXZ ?? new Vector2(-20f, 3f);
        var visible = new List<PerceivedEntity>
        {
            new(AttackerId, TeamId.Away, TacticalRole.Striker,
                new Vector3(attacker.X, 0f, attacker.Y), Vector3.Zero, 1f)
        };

        return Build(keeper, ball, possession, visible, lastPlay, controlState, controllingPlayerId);
    }

    /// <summary>A keeper holding the ball, looking at the team-mates he could release it to.</summary>
    private static FootballerObservation ObserveHolding(
        FootballerState keeper,
        IReadOnlyList<PerceivedEntity> mates)
    {
        var ball = new BallState
        {
            Position = new Vector3(keeper.Position.X + 0.45f, 1.2f, keeper.Position.Z),
            PreviousPosition = new Vector3(keeper.Position.X + 0.45f, 1.2f, keeper.Position.Z),
            Velocity = Vector3.Zero,
            HeldByPlayerId = keeper.Id
        };

        return Build(
            keeper, ball, TeamId.Home, mates,
            new BallLastPlay(BallContactKind.Save, KickIntentType.None, keeper.Id, TeamId.Home),
            BallControlState.KeeperHeld, keeper.Id);
    }

    /// <summary>
    /// The observation every fixture starts from. One factory rather than a <c>with</c> expression, because
    /// <see cref="FootballerObservation"/> is a plain class with <c>init</c> members.
    /// </summary>
    private static FootballerObservation Build(
        FootballerState keeper,
        BallState ball,
        TeamId possession,
        IReadOnlyList<PerceivedEntity> visible,
        BallLastPlay? lastPlay,
        BallControlState controlState,
        int? controllingPlayerId) =>
        new()
        {
            SelfId = keeper.Id,
            SelfTeam = keeper.Team,
            SelfRole = keeper.Role,
            SelfPosition = keeper.Position,
            SelfVelocity = keeper.Velocity,
            SelfFacingXZ = keeper.FacingXZ,
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 0f,
            IsSetPieceTaker = false,
            PerceivedBallPosition = ball.Position,
            PerceivedBallVelocity = ball.Velocity,
            PossessingTeam = possession,
            BallLastTouchedByTeam = lastPlay?.Team ?? possession,
            BallLastTouchedByPlayerId = lastPlay?.PlayerId ?? (possession == TeamId.Home ? keeper.Id : AttackerId),
            BallLastPlay = lastPlay ?? BallLastPlay.None,
            BallControlState = controlState,
            BallControllingPlayerId = controllingPlayerId,
            BallHoldRemainingSeconds = 0f,
            TacticalAnchorPosition = keeper.Position,
            TacticalJob = Tactics.TacticalJob.Shape,
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 30f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            Tick = 900,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = keeper.Tendencies,
            SelfAttributes = keeper.Attributes
        };

    /// <summary>
    /// Runs a fragment in which one player owns the ball at the halfway line and stands there, and returns how
    /// far off his line the home keeper has walked after four seconds. Nobody else is within thirty metres of
    /// the ball, so possession cannot change hands and the only thing moving the keeper is his own duty.
    /// </summary>
    private static float KeeperStandoffAfter(bool ownerIsHome)
    {
        var scenario = new MatchScenario
        {
            Name = ownerIsHome ? "keeper-support" : "keeper-defending",
            HalfDurationSeconds = 120f,
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = ownerIsHome ? MateId : 16,
            BallPosition = new Vector3(0f, 0.11f, 2f)
        };

        scenario.Add(MatchScenario.Player(KeeperId, TeamId.Home, TacticalRole.Goalkeeper,
            new Vector3(-52f, 0f, 0f), 1));
        scenario.Add(MatchScenario.Player(MateId, TeamId.Home, TacticalRole.CentralMidfielder,
            ownerIsHome ? new Vector3(0f, 0f, 2f) : new Vector3(30f, 0f, 20f), 8)
            .WithController(new FixedCommandController(FootballerCommand.Idle)));
        scenario.Add(MatchScenario.Player(7, TeamId.Home, TacticalRole.Winger,
            new Vector3(25f, 0f, 12f), 11).WithController(new FixedCommandController(FootballerCommand.Idle)));
        scenario.Add(MatchScenario.Player(16, TeamId.Away, TacticalRole.CentralMidfielder,
            ownerIsHome ? new Vector3(-30f, 0f, -20f) : new Vector3(0f, 0f, 2f), 8)
            .WithController(new FixedCommandController(FootballerCommand.Idle)));
        scenario.Add(MatchScenario.Player(17, TeamId.Away, TacticalRole.Winger,
            new Vector3(-20f, 0f, -24f), 11).WithController(new FixedCommandController(FootballerCommand.Idle)));

        var engine = new MatchSimulationEngine(scenario);
        FootballerState? keeper = null;
        for (int tick = 0; tick < 240; tick++)
        {
            engine.StepTick();
            foreach (FootballerState player in engine.Players)
            {
                if (player.Role == TacticalRole.Goalkeeper && player.Team == TeamId.Home) keeper = player;
            }
        }

        Assert.NotNull(keeper);
        return keeper!.Position.X - HomeGoalX;
    }
}
