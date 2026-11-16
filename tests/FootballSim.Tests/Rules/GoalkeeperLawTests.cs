using System.Numerics;
using FootballSim;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Rules;
using Xunit;

namespace FootballSim.Tests.Rules;

/// <summary>
/// A6.6: goalkeeper handling is gated on the law (Law 12), and the gate is one model that the execution
/// layer and the keeper's own decision both read.
///
/// The three restrictions are proven at two levels. The model tests move one input at a time
/// (position, play kind, play intent, player) and assert the reason that came back. The execution tests
/// then show the thing that matters: the same ball, the same keeper, one input changed, and a different
/// contact — a ball his hands may not touch is played with his feet, one they may is caught, and one he
/// cannot reach stays untouched with a stated refusal.
/// </summary>
public sealed class GoalkeeperLawTests
{
    private static readonly PitchDimensions Pitch = new();
    private static readonly float Dt = SimConstants.FixedDeltaTime;

    private const int KeeperId = 1;
    private const int MateId = 6;
    private const int OpponentId = 12;

    /// <summary>The home keeper's goal line (he defends -X) and the away keeper's (+X).</summary>
    private static readonly float HomeGoalX = -Pitch.HalfLength;
    private static readonly float AwayGoalX = Pitch.HalfLength;

    /// <summary>12 m out from the home goal: comfortably inside the 16.5 m penalty area.</summary>
    private static readonly Vector3 InsideHomeArea = new(HomeGoalX + 12f, 0.11f, 0f);

    // ---------------------------------------------------------------------------------------------
    // The model: one restriction at a time.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Law 12's first restriction: the hands exist only inside the keeper's own penalty area. Just past
    /// the area line, and just wide of it, he is an outfield player — and the other end is symmetric.
    /// </summary>
    [Fact]
    public void HandsAreOnlyAllowedInsideTheKeepersOwnPenaltyArea()
    {
        GoalkeeperHandlingVerdict inside = EvaluateHandling(TeamId.Home, HomeGoalX, InsideHomeArea);
        Assert.True(inside.MayUseHands, inside.Describe());
        Assert.Equal(GoalkeeperHandlingReason.Allowed, inside.Reason);

        // Half a metre beyond the area's own line: still his half, no longer his area.
        GoalkeeperHandlingVerdict beyondLine = EvaluateHandling(
            TeamId.Home, HomeGoalX, new Vector3(HomeGoalX + Pitch.PenaltyAreaLength + 0.5f, 0.11f, 0f));
        Assert.False(beyondLine.MayUseHands);
        Assert.Equal(GoalkeeperHandlingReason.OutsideOwnPenaltyArea, beyondLine.Reason);

        // Wide of the area: the area is 40.32 m wide, so 21 m off centre is outside it.
        GoalkeeperHandlingVerdict wide = EvaluateHandling(
            TeamId.Home, HomeGoalX,
            new Vector3(HomeGoalX + 6f, 0.11f, Pitch.PenaltyAreaWidth * 0.5f + 0.5f));
        Assert.Equal(GoalkeeperHandlingReason.OutsideOwnPenaltyArea, wide.Reason);

        // Symmetric at the other end: the away keeper's area is the +X one and his own half is +X.
        Assert.Equal(GoalkeeperHandlingReason.Allowed,
            EvaluateHandling(TeamId.Away, AwayGoalX, new Vector3(AwayGoalX - 12f, 0.11f, 0f)).Reason);
        Assert.Equal(GoalkeeperHandlingReason.OutsideOwnPenaltyArea,
            EvaluateHandling(TeamId.Away, AwayGoalX, new Vector3(AwayGoalX - 20f, 0.11f, 0f)).Reason);
    }

    /// <summary>
    /// A team-mate's deliberate kick to the keeper may not be handled, whatever kind of ball it was. The
    /// engine models every one of these as a <see cref="BallContactKind.Kick"/>, which is the fact the law
    /// names — the old "any own-team last touch" test could not say this.
    /// </summary>
    [Theory]
    [InlineData(KickIntentType.GroundPass)]
    [InlineData(KickIntentType.LoftedPass)]
    [InlineData(KickIntentType.ThroughPass)]
    [InlineData(KickIntentType.DrivenPass)]
    [InlineData(KickIntentType.Cross)]
    [InlineData(KickIntentType.Clearance)]
    public void ATeamMatesDeliberateKick_MayNotBeHandled(KickIntentType intent)
    {
        GoalkeeperHandlingVerdict verdict = EvaluateHandling(
            TeamId.Home, HomeGoalX, InsideHomeArea,
            new BallLastPlay(BallContactKind.Kick, intent, MateId, TeamId.Home));

        Assert.False(verdict.MayUseHands);
        Assert.Equal(GoalkeeperHandlingReason.TeamMateDeliberateKick, verdict.Reason);
    }

    /// <summary>
    /// A header back to the keeper is not a kick, so it stays handleable: "deliberately kicked" is the
    /// clause, and the engine's kick contact carries the intent that tells the two apart.
    /// </summary>
    [Fact]
    public void ATeamMatesHeader_IsNotAKick_SoItMayBeHandled()
    {
        GoalkeeperHandlingVerdict verdict = EvaluateHandling(
            TeamId.Home, HomeGoalX, InsideHomeArea,
            new BallLastPlay(BallContactKind.Kick, KickIntentType.Header, MateId, TeamId.Home));

        Assert.True(verdict.MayUseHands);
        Assert.Equal(GoalkeeperHandlingReason.Allowed, verdict.Reason);
    }

    /// <summary>
    /// A deflection, a control touch or a tackle by a team-mate is not a deliberate kick, so none of them
    /// bars the keeper's hands. This is the fidelity the last-touch-team check could not express.
    /// </summary>
    [Theory]
    [InlineData(BallContactKind.Collection)]
    [InlineData(BallContactKind.Tackle)]
    [InlineData(BallContactKind.Save)]
    public void ATeamMatesDeflection_IsNotABackPass_SoItMayBeHandled(BallContactKind kind)
    {
        GoalkeeperHandlingVerdict verdict = EvaluateHandling(
            TeamId.Home, HomeGoalX, InsideHomeArea,
            new BallLastPlay(kind, KickIntentType.None, MateId, TeamId.Home));

        Assert.True(verdict.MayUseHands, verdict.Describe());
        Assert.Equal(GoalkeeperHandlingReason.Allowed, verdict.Reason);
    }

    /// <summary>
    /// Nobody has touched the ball since the keeper released it: handling it again is a second handling,
    /// whether he sliced a clearance back at himself or punched a parry straight up. Once a team-mate has
    /// touched it the release is spent and the hands are available again.
    /// </summary>
    [Fact]
    public void AKeepersOwnRelease_MayNotBeHandledAgain()
    {
        GoalkeeperHandlingVerdict ownKick = EvaluateHandling(
            TeamId.Home, HomeGoalX, InsideHomeArea,
            new BallLastPlay(BallContactKind.Kick, KickIntentType.Clearance, KeeperId, TeamId.Home));
        Assert.False(ownKick.MayUseHands);
        Assert.Equal(GoalkeeperHandlingReason.SecondHandling, ownKick.Reason);

        GoalkeeperHandlingVerdict afterAMate = EvaluateHandling(
            TeamId.Home, HomeGoalX, InsideHomeArea,
            new BallLastPlay(BallContactKind.Collection, KickIntentType.None, MateId, TeamId.Home));
        Assert.True(afterAMate.MayUseHands);
    }

    /// <summary>An opponent's play never restricts the keeper: the back-pass clause is about team-mates.</summary>
    [Fact]
    public void AnOpponentsPlay_NeverRestrictsTheKeeper()
    {
        GoalkeeperHandlingVerdict verdict = EvaluateHandling(
            TeamId.Home, HomeGoalX, InsideHomeArea,
            new BallLastPlay(BallContactKind.Kick, KickIntentType.Shot, OpponentId, TeamId.Away));

        Assert.True(verdict.MayUseHands);
    }

    /// <summary>
    /// An unknown play (a restart re-placed the ball, a fixture seeded one) constrains nothing: a law that
    /// invented a play would bar the keeper from his own area on the first tick of every passage.
    /// </summary>
    [Fact]
    public void AnUnknownPlay_DoesNotRestrict()
    {
        Assert.False(BallLastPlay.None.IsKnown);
        Assert.True(EvaluateHandling(TeamId.Home, HomeGoalX, InsideHomeArea, BallLastPlay.None).MayUseHands);
    }

    // ---------------------------------------------------------------------------------------------
    // The execution layer: the verdict decides the contact, not a role.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The same 2 m ball, the same command, the same keeper: only his position differs. Inside his area his
    /// hands reach 2.42 m and he plays it; half a metre past the area line his reach is an outfielder's
    /// 1.45 m, so execution refuses it as a ball too high. A keeper does not get keeper privileges where
    /// the law does not grant them.
    ///
    /// A6.5b restated the inside half: the keeper <b>gathers</b> the ball with his hands now, because the claim
    /// exists. What this fixture proves is the reach and the height (the ball is played inside the area and
    /// refused outside it), not the contact type — a keeper who can reach a 2 m ball catches it.
    /// </summary>
    [Fact]
    public void AHighBallInsideTheArea_IsPlayable_ButOutsideItIsABallTooHigh()
    {
        static BallState HighBall() => new()
        {
            Position = new Vector3(-40.15f, 2.0f, 0f), // 0.35 m in front of the keeper, 2 m up
            PreviousPosition = new Vector3(-40.15f, 2.0f, 0f),
            Velocity = Vector3.Zero,
            LastTouchedByTeam = TeamId.Away,
            LastTouchedByPlayerId = OpponentId,
            LastPlay = new BallLastPlay(BallContactKind.Kick, KickIntentType.Shot, OpponentId, TeamId.Away)
        };

        FootballerCommand wantsIt = FootballerCommand.Idle with { KickType = KickIntentType.LoftedPass };

        FootballerState insideKeeper = Keeper(new Vector3(-40.5f, 0f, 0f));
        insideKeeper.LastCommand = wantsIt;
        BallState insideBall = HighBall();
        var insideExecution = new FootballerExecutionSystem(Pitch);
        insideExecution.UpdateLocomotionAndContacts(
            new[] { insideKeeper }, insideBall, new SeededRandom(1), Dt);

        BallContactEvent played = Assert.Single(insideExecution.ContactEvents);
        Assert.Equal(BallContactKind.Save, played.Kind);
        Assert.Equal(insideKeeper.Id, insideBall.HeldByPlayerId);

        FootballerState outsideKeeper = Keeper(new Vector3(HomeGoalX + Pitch.PenaltyAreaLength + 0.5f, 0f, 0f));
        outsideKeeper.LastCommand = wantsIt;
        // The same ball, moved with the keeper: it is 0.35 m in front of him in both worlds.
        BallState outsideBall = HighBall();
        outsideBall.Position = new Vector3(outsideKeeper.Position.X + 0.35f, 2.0f, 0f);
        outsideBall.PreviousPosition = outsideBall.Position;
        var outsideExecution = new FootballerExecutionSystem(Pitch);
        outsideExecution.UpdateLocomotionAndContacts(
            new[] { outsideKeeper }, outsideBall, new SeededRandom(1), Dt);

        Assert.Empty(outsideExecution.ContactEvents);
        BallContactAttempt refused = Assert.Single(outsideExecution.ContactAttempts);
        Assert.False(refused.Executed);
        Assert.Equal(ContactRejectionReason.BallTooHigh, refused.Reason);
    }

    /// <summary>
    /// Outside his area the keeper is still a player: the same fixture on the ground is played with the
    /// feet. Losing the hands must not mean losing the ability to defend the ball.
    /// </summary>
    [Fact]
    public void AKeeperOutsideHisArea_StillPlaysAGroundBall()
    {
        FootballerState keeper = Keeper(new Vector3(HomeGoalX + Pitch.PenaltyAreaLength + 3f, 0f, 0f));
        var ball = new BallState
        {
            Position = new Vector3(keeper.Position.X + 0.4f, 0.11f, 0f),
            PreviousPosition = new Vector3(keeper.Position.X + 0.4f, 0.11f, 0f),
            Velocity = new Vector3(-2f, 0f, 0f),
            LastTouchedByTeam = TeamId.Away,
            LastTouchedByPlayerId = OpponentId,
            LastPlay = new BallLastPlay(BallContactKind.Kick, KickIntentType.Shot, OpponentId, TeamId.Away)
        };

        var execution = new FootballerExecutionSystem(Pitch);
        execution.UpdateLocomotionAndContacts(new[] { keeper }, ball, new SeededRandom(1), Dt);

        Assert.Single(execution.ContactEvents);
        Assert.False(ball.HeldByPlayerId.HasValue, "a keeper outside his area caught the ball");
    }

    /// <summary>
    /// The gate clause: the <b>same</b> ball, at save speed, moving at his own goal, inside his area. A
    /// team-mate's deliberate kick may not be caught — it is played with the feet — while the same ball
    /// that merely deflected off a team-mate may be. Only the play kind differs, and the last-touch team is
    /// identical, so this is the law being modelled rather than the old team test standing in for it.
    /// </summary>
    [Fact]
    public void ABackPassIsPlayedWithTheFeet_WhileATeamMatesDeflectionIsCaught()
    {
        (FootballerState Keeper, BallState Ball, FootballerExecutionSystem Execution) Run(BallLastPlay lastPlay)
        {
            FootballerState keeper = Keeper(new Vector3(-51.9f, 0f, 0f));
            var ball = new BallState
            {
                Position = new Vector3(-51.4f, 0.11f, 0f),
                PreviousPosition = new Vector3(-51.4f, 0.11f, 0f),
                Velocity = new Vector3(-12f, 0f, 0f), // toward the keeper's own goal at save speed
                LastTouchedByTeam = lastPlay.Team,
                LastTouchedByPlayerId = lastPlay.PlayerId,
                LastPlay = lastPlay
            };
            var execution = new FootballerExecutionSystem(Pitch);
            execution.UpdateLocomotionAndContacts(new[] { keeper }, ball, new SeededRandom(1), Dt);
            return (keeper, ball, execution);
        }

        var backPass = Run(new BallLastPlay(BallContactKind.Kick, KickIntentType.GroundPass, MateId, TeamId.Home));
        Assert.Null(backPass.Ball.HeldByPlayerId);
        Assert.DoesNotContain(backPass.Execution.ContactEvents, c => c.Kind == BallContactKind.Save);
        Assert.Contains(backPass.Execution.ContactEvents, c => c.Kind == BallContactKind.Collection);
        Assert.DoesNotContain("Catch", backPass.Keeper.LastExecutionDiagnostic);
        Assert.DoesNotContain("Parry", backPass.Keeper.LastExecutionDiagnostic);

        var deflection = Run(new BallLastPlay(BallContactKind.Collection, KickIntentType.None, MateId, TeamId.Home));
        Assert.Equal(deflection.Keeper.Id, deflection.Ball.HeldByPlayerId);
        Assert.Contains(deflection.Execution.ContactEvents,
            c => c.Kind == BallContactKind.Save && !c.IsParry);
        Assert.Equal("GK:CleanCatch", deflection.Keeper.LastExecutionDiagnostic);

        // And an opponent's shot, the case F03 already covered, is still caught.
        var shot = Run(new BallLastPlay(BallContactKind.Kick, KickIntentType.Shot, OpponentId, TeamId.Away));
        Assert.Equal(shot.Keeper.Id, shot.Ball.HeldByPlayerId);
    }

    /// <summary>
    /// A keeper may not handle again before anybody has touched the ball he released — so a sliced
    /// clearance coming back at his own goal is played with the feet, not gathered.
    /// </summary>
    [Fact]
    public void AKeepersOwnRelease_IsPlayedWithTheFeet()
    {
        FootballerState keeper = Keeper(new Vector3(-51.9f, 0f, 0f));
        var ball = new BallState
        {
            Position = new Vector3(-51.4f, 0.11f, 0f),
            PreviousPosition = new Vector3(-51.4f, 0.11f, 0f),
            Velocity = new Vector3(-12f, 0f, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = KeeperId,
            LastPlay = new BallLastPlay(BallContactKind.Kick, KickIntentType.Clearance, KeeperId, TeamId.Home)
        };

        var execution = new FootballerExecutionSystem(Pitch);
        execution.UpdateLocomotionAndContacts(new[] { keeper }, ball, new SeededRandom(1), Dt);

        Assert.Null(ball.HeldByPlayerId);
        Assert.DoesNotContain(execution.ContactEvents, c => c.Kind == BallContactKind.Save);
        Assert.Contains(execution.ContactEvents, c => c.Kind == BallContactKind.Collection);
    }

    /// <summary>
    /// A6.5b: the missing physical action behind the claim. The same ball at chest-and-a-half height, inside his
    /// area, with two different play histories: an opponent's cross is gathered — the hands are the only contact
    /// that reaches it — while the identical ball a team-mate deliberately kicked back to him cannot be played at
    /// all, because his hands are barred and his feet do not reach 1.9 m. Before this the keeper "controlled" a
    /// dropping cross with a foot, which is to say no keeper could claim one.
    /// </summary>
    [Fact]
    public void ADroppingCross_IsGathered_ButADeliberateBackPassIsNotPlayable()
    {
        static BallState AirBall() => new()
        {
            Position = new Vector3(-51.5f, 1.9f, 0f),
            PreviousPosition = new Vector3(-51.5f, 1.9f, 0f),
            Velocity = Vector3.Zero
        };

        FootballerState keeper = Keeper(new Vector3(-51.9f, 0f, 0f));
        BallState cross = AirBall();
        cross.LastTouchedByTeam = TeamId.Away;
        cross.LastTouchedByPlayerId = OpponentId;
        cross.LastPlay = new BallLastPlay(BallContactKind.Kick, KickIntentType.Cross, OpponentId, TeamId.Away);

        var crossExecution = new FootballerExecutionSystem(Pitch);
        crossExecution.UpdateLocomotionAndContacts(new[] { keeper }, cross, new SeededRandom(1), Dt);

        Assert.Equal(keeper.Id, cross.HeldByPlayerId);
        Assert.True(cross.HeldReleaseDelaySeconds > 0f, "a claim must be held, not tapped");
        Assert.Equal("GK:CrossClaim", keeper.LastExecutionDiagnostic);
        Assert.Contains(crossExecution.ContactEvents, c => c.Kind == BallContactKind.Save && !c.IsParry);

        FootballerState backPassKeeper = Keeper(new Vector3(-51.9f, 0f, 0f));
        BallState backPass = AirBall();
        backPass.LastTouchedByTeam = TeamId.Home;
        backPass.LastTouchedByPlayerId = MateId;
        backPass.LastPlay = new BallLastPlay(BallContactKind.Kick, KickIntentType.GroundPass, MateId, TeamId.Home);

        var backPassExecution = new FootballerExecutionSystem(Pitch);
        backPassExecution.UpdateLocomotionAndContacts(new[] { backPassKeeper }, backPass, new SeededRandom(1), Dt);

        Assert.Null(backPass.HeldByPlayerId);
        Assert.Empty(backPassExecution.ContactEvents);
        Assert.DoesNotContain("CrossClaim", backPassKeeper.LastExecutionDiagnostic);
    }

    /// <summary>
    /// The other half of the claim: a ball arriving faster than the keeper can hold is punched clear, and the
    /// punch goes <b>away from his own goal</b> — a cross that never came at the goal cannot be parried "back
    /// where it came from" without crossing his own face (F02's rule, applied to a delivery).
    /// </summary>
    [Fact]
    public void AFastBallAtHandHeight_IsPunchedClear_NotCaught()
    {
        FootballerState keeper = Keeper(new Vector3(-51.9f, 0f, 0f));
        var ball = new BallState
        {
            Position = new Vector3(-51.5f, 1.8f, 0f),
            PreviousPosition = new Vector3(-51.5f, 1.8f, 0f),
            Velocity = new Vector3(0f, 0f, 20f), // across his own face, at twice the pace he can hold
            LastTouchedByTeam = TeamId.Away,
            LastTouchedByPlayerId = OpponentId,
            LastPlay = new BallLastPlay(BallContactKind.Kick, KickIntentType.Cross, OpponentId, TeamId.Away)
        };

        var execution = new FootballerExecutionSystem(Pitch);
        execution.UpdateLocomotionAndContacts(new[] { keeper }, ball, new SeededRandom(1), Dt);

        Assert.Null(ball.HeldByPlayerId);
        Assert.Equal("GK:PunchClear", keeper.LastExecutionDiagnostic);
        Assert.Contains(execution.ContactEvents, c => c.Kind == BallContactKind.Save && c.IsParry);
        Assert.True(ball.Velocity.X > 0f,
            $"the punch sent the ball toward the keeper's own goal (velX={ball.Velocity.X:F2})");
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures.
    // ---------------------------------------------------------------------------------------------

    private static FootballerState Keeper(Vector3 position)
    {
        var keeper = new FootballerState
        {
            Id = KeeperId,
            Team = TeamId.Home,
            ShirtNumber = 1,
            Role = TacticalRole.Goalkeeper,
            Attributes = new PlayerAttributes(),
            Tendencies = new PlayerTendencies(),
            Position = position,
            Velocity = Vector3.Zero,
            FacingXZ = new Vector2(1f, 0f)
        };
        keeper.LastCommand = FootballerCommand.Idle;
        return keeper;
    }

    private static GoalkeeperHandlingVerdict EvaluateHandling(
        TeamId keeperTeam, float ownGoalX, Vector3 ballPosition, BallLastPlay lastPlay = default) =>
        GoalkeeperLawModel.EvaluateHandling(
            Pitch, ownGoalX, KeeperId, keeperTeam, ballPosition,
            lastPlay.IsKnown ? lastPlay : BallLastPlay.None);
}
