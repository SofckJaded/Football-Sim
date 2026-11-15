using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A6.4 at the model level: the turnover response is a comparison, and every input has to be able to move
/// the answer on its own.
///
/// The four inputs are one clause each — local arrival numbers, central security, the carrier's constraint
/// and his escape routes — plus the two rules the plan is explicit about: the profile's counter-press window
/// is a <b>maximum</b> (open is necessary, never sufficient), and a chosen answer is a <b>commitment</b> that
/// is held rather than re-voted every allocator pass. The engine-level consequence (a secure turnover and an
/// exposed one choose differently, and the answer reaches the line, the markers and the press) lives in
/// <see cref="TurnoverResponseTests"/>.
/// </summary>
public sealed class TurnoverResponseModelTests
{
    private static readonly PitchDimensions Pitch = new();
    private const int AwayCarrier = 201;

    // ---------------------------------------------------------------------------------------------
    // Fixtures.
    // ---------------------------------------------------------------------------------------------

    private static FootballerState Player(
        int id, TeamId team, TacticalRole role, float x, float z, Vector3? velocity = null) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(role),
        Position = new Vector3(x, 0f, z),
        Velocity = velocity ?? Vector3.Zero,
        FacingXZ = new Vector2(-1f, 0f)
    };

    private static BallState BallAt(float x, float z) => new()
    {
        Position = new Vector3(x, 0f, z),
        LastTouchedByTeam = TeamId.Away,
        LastTouchedByPlayerId = AwayCarrier
    };

    /// <summary>
    /// Home defends an away carrier standing at (0, 0). Home attacks +X, so "behind the ball" means X &lt; 0.
    /// The default picture is the secure one: two centre-backs and a defensive midfielder goal-side, two
    /// team-mates ahead of the ball within reach, and an away side with only its own keeper behind the ball.
    /// </summary>
    private static List<FootballerState> SecurePicture() => new()
    {
        Player(101, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f),
        Player(102, TeamId.Home, TacticalRole.CenterBack, -9f, -7f),
        Player(103, TeamId.Home, TacticalRole.CenterBack, -9f, 7f),
        Player(104, TeamId.Home, TacticalRole.DefensiveMidfielder, -4f, 0f),
        Player(108, TeamId.Home, TacticalRole.CentralMidfielder, 4f, -8f),
        Player(AwayCarrier, TeamId.Away, TacticalRole.CentralMidfielder, 0f, 0f),
        Player(202, TeamId.Away, TacticalRole.Striker, 14f, -10f),
        Player(209, TeamId.Away, TacticalRole.Goalkeeper, 46f, 0f)
    };

    /// <summary>
    /// The same six home players as <see cref="SecurePicture"/>, all of them ahead of the ball: a side that
    /// has just lost the ball with nobody goal-side of it.
    /// </summary>
    private static List<FootballerState> ExposedPicture() => new()
    {
        Player(101, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f),
        Player(102, TeamId.Home, TacticalRole.CenterBack, 2f, -7f),
        Player(103, TeamId.Home, TacticalRole.CenterBack, 2f, 7f),
        Player(104, TeamId.Home, TacticalRole.DefensiveMidfielder, 6f, 0f),
        Player(108, TeamId.Home, TacticalRole.CentralMidfielder, 8f, -8f),
        Player(AwayCarrier, TeamId.Away, TacticalRole.CentralMidfielder, 0f, 0f),
        Player(202, TeamId.Away, TacticalRole.Striker, 14f, -10f),
        Player(209, TeamId.Away, TacticalRole.Goalkeeper, 46f, 0f)
    };

    private static TurnoverSituation Evaluate(
        IReadOnlyList<FootballerState> players,
        BallState ball,
        bool windowOpen = true,
        float transitionSeconds = 0f,
        TeamTacticalProfile? profile = null,
        TeamId possessing = TeamId.Away,
        BallControlState state = BallControlState.Controlled)
    {
        var possession = new BallPossessionSnapshot(
            state, possessing, state == BallControlState.Controlled ? AwayCarrier : null, null);

        return TurnoverResponseModel.Evaluate(
            TeamId.Home, players, ball, possession, TeamBelief.FromTruth(TeamId.Home, players),
            profile ?? TeamTacticalProfile.PossessionControl, 1f, Pitch, transitionSeconds, windowOpen);
    }

    // ---------------------------------------------------------------------------------------------
    // The four inputs.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Local numbers are arrivals, not a head-count: the count rises when one of ours can actually reach
    /// the contest inside the window, and the chosen answer can follow it across the counter-press threshold.
    /// </summary>
    [Fact]
    public void MoreOfOursReachingTheBall_RaisesTheLocalNumbers()
    {
        BallState ball = BallAt(0f, 0f);

        List<FootballerState> lone = SecurePicture();
        // Take away the two home players who can reach the carrier, leaving the three behind the ball.
        lone.RemoveAll(p => p.Id is 108 or 104);

        List<FootballerState> crowded = SecurePicture();

        TurnoverSituation thin = Evaluate(lone, ball);
        TurnoverSituation thick = Evaluate(crowded, ball);

        Assert.True(thin.OursInWindow < thick.OursInWindow,
            $"the crowded picture ({thick.OursInWindow}) must count more arrivals than the lone one ({thin.OursInWindow})");
        Assert.True(thick.LocalNumbers01 > thin.LocalNumbers01,
            $"local numbers {thick.LocalNumbers01:F2} must beat {thin.LocalNumbers01:F2}");
        Assert.True(thick.CounterPressScore > thin.CounterPressScore,
            $"the counter-press score must follow the arrivals: {thick.CounterPressScore:F2} vs {thin.CounterPressScore:F2}");
        Assert.True(thin.RetreatScore > thick.RetreatScore,
            $"the retreat score must rise when we are outnumbered: {thin.RetreatScore:F2} vs {thick.RetreatScore:F2}");
    }

    /// <summary>
    /// Central security is the A3.6 model read for the side that just lost the ball: the same picture with
    /// the defenders moved from behind the ball to in front of it must be less secure, and must switch the
    /// answer away from counter-pressing.
    /// </summary>
    [Fact]
    public void NobodyBehindTheBall_LowersSecurity_AndForbidsCounterPressing()
    {
        BallState ball = BallAt(0f, 0f);

        TurnoverSituation secure = Evaluate(SecurePicture(), ball);
        TurnoverSituation stranded = Evaluate(ExposedPicture(), ball);

        Assert.True(secure.BehindBall >= TurnoverResponseModel.MinBehindBallForCounterPress);
        Assert.True(stranded.BehindBall < TurnoverResponseModel.MinBehindBallForCounterPress,
            $"the exposed picture still had {stranded.BehindBall} players behind the ball");
        Assert.True(secure.CentralSecurity01 > stranded.CentralSecurity01,
            $"security {secure.CentralSecurity01:F2} must beat {stranded.CentralSecurity01:F2}");
        Assert.True(stranded.RetreatScore > secure.RetreatScore,
            $"the exposed side must score retreat higher ({stranded.RetreatScore:F2} vs {secure.RetreatScore:F2})");
    }

    /// <summary>
    /// The carrier's constraint is the exits one of ours would reach first. Four team-mates standing on his
    /// exit points constrain him; the same four 16 m away do not, and the counter-press score follows.
    /// </summary>
    [Fact]
    public void AConstrainedCarrier_RaisesTheConstraint_AndTheCounterPressScore()
    {
        BallState ball = BallAt(0f, 0f);

        var tight = new List<FootballerState>
        {
            Player(101, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f),
            // On the four primary exits from (0, 0), six metres out.
            Player(102, TeamId.Home, TacticalRole.CenterBack, -6f, 0f),
            Player(103, TeamId.Home, TacticalRole.CenterBack, 6f, 0f),
            Player(104, TeamId.Home, TacticalRole.DefensiveMidfielder, 0f, -6f),
            Player(108, TeamId.Home, TacticalRole.CentralMidfielder, 0f, 6f),
            Player(AwayCarrier, TeamId.Away, TacticalRole.CentralMidfielder, 0f, 0f),
            Player(209, TeamId.Away, TacticalRole.Goalkeeper, 46f, 0f)
        };

        var loose = new List<FootballerState>
        {
            Player(101, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f),
            Player(102, TeamId.Home, TacticalRole.CenterBack, -16f, 0f),
            Player(103, TeamId.Home, TacticalRole.CenterBack, 16f, 0f),
            Player(104, TeamId.Home, TacticalRole.DefensiveMidfielder, 0f, -16f),
            Player(108, TeamId.Home, TacticalRole.CentralMidfielder, 0f, 16f),
            Player(AwayCarrier, TeamId.Away, TacticalRole.CentralMidfielder, 0f, 0f),
            Player(209, TeamId.Away, TacticalRole.Goalkeeper, 46f, 0f)
        };

        TurnoverSituation pressed = Evaluate(tight, ball);
        TurnoverSituation free = Evaluate(loose, ball);

        Assert.True(pressed.HasCarrier);
        Assert.True(pressed.CarrierConstraint01 > free.CarrierConstraint01 + 0.2f,
            $"a carrier surrounded on his exits ({pressed.CarrierConstraint01:F2}) must be more constrained " +
            $"than one with 16 m of room ({free.CarrierConstraint01:F2})");
        Assert.True(pressed.CounterPressScore > free.CounterPressScore,
            $"the counter-press score must rise with the constraint: {pressed.CounterPressScore:F2} vs " +
            $"{free.CounterPressScore:F2}");
    }

    /// <summary>
    /// The escape route is the A3.6 outlet lane read for the new carrier. A team-mate of his standing in that
    /// lane blocks the pass out, which raises the constraint case for pressing and lowers the reason to
    /// retreat.
    /// </summary>
    [Fact]
    public void AnOpenOutlet_IsAReasonNotToCounterPress()
    {
        BallState ball = BallAt(0f, 0f);

        // The away keeper is the outlet the A3.6 model measures: the believed opponent furthest along home's
        // attacking direction, i.e. the safest pass out of the pressure.
        var blocked = new List<FootballerState>
        {
            Player(101, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f),
            Player(102, TeamId.Home, TacticalRole.CenterBack, -6f, 0f),
            Player(103, TeamId.Home, TacticalRole.CenterBack, 20f, 0f),      // standing on the outlet lane
            Player(104, TeamId.Home, TacticalRole.DefensiveMidfielder, -4f, 6f),
            Player(AwayCarrier, TeamId.Away, TacticalRole.CentralMidfielder, 0f, 0f),
            Player(209, TeamId.Away, TacticalRole.Goalkeeper, 40f, 0f)
        };

        var open = new List<FootballerState>
        {
            Player(101, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f),
            Player(102, TeamId.Home, TacticalRole.CenterBack, -6f, 0f),
            Player(103, TeamId.Home, TacticalRole.CenterBack, -20f, 25f),    // off the lane
            Player(104, TeamId.Home, TacticalRole.DefensiveMidfielder, -4f, 6f),
            Player(AwayCarrier, TeamId.Away, TacticalRole.CentralMidfielder, 0f, 0f),
            Player(209, TeamId.Away, TacticalRole.Goalkeeper, 40f, 0f)
        };

        TurnoverSituation shut = Evaluate(blocked, ball);
        TurnoverSituation through = Evaluate(open, ball);

        Assert.True(through.EscapeRoute01 > shut.EscapeRoute01,
            $"an open outlet ({through.EscapeRoute01:F2}) must read more exposed than a blocked one " +
            $"({shut.EscapeRoute01:F2})");
        Assert.True(shut.CounterPressScore > through.CounterPressScore,
            $"a blocked escape must make pressing more attractive: {shut.CounterPressScore:F2} vs " +
            $"{through.CounterPressScore:F2}");
    }

    // ---------------------------------------------------------------------------------------------
    // The two rules: the window is a maximum, and the answer is a commitment.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The plan's clause verbatim: the counter-press duration is a maximum, not an automatic instruction.
    /// The identical picture chooses counter-press inside the profile's window and something else outside it —
    /// so the window is a gate on the decision, not a score that decides it.
    /// </summary>
    [Fact]
    public void TheCounterPressWindow_IsAMaximum_NotAnInstruction()
    {
        BallState ball = BallAt(0f, 0f);
        List<FootballerState> picture = SecurePicture();

        TurnoverSituation inside = Evaluate(picture, ball, windowOpen: true);
        TurnoverSituation outside = Evaluate(picture, ball, windowOpen: false);

        Assert.Equal(TurnoverResponse.CounterPress, inside.Response);
        Assert.NotEqual(TurnoverResponse.CounterPress, outside.Response);
        Assert.Equal(inside.CounterPressScore, outside.CounterPressScore, 4);
        Assert.True(inside.CounterPressWindowOpen);
        Assert.False(outside.CounterPressWindowOpen);
    }

    /// <summary>The response is the first seconds after the loss; past the window the block is the block.</summary>
    [Fact]
    public void TheResponseWindow_Ends()
    {
        BallState ball = BallAt(0f, 0f);
        List<FootballerState> picture = SecurePicture();

        Assert.True(Evaluate(picture, ball, transitionSeconds: 0f).Responds);
        Assert.True(Evaluate(picture, ball, transitionSeconds: TurnoverResponseModel.ResponseWindowSeconds)
            .Responds);
        Assert.Equal(TurnoverResponse.None,
            Evaluate(picture, ball, transitionSeconds: TurnoverResponseModel.ResponseWindowSeconds + 0.1f)
                .Response);
        Assert.Equal(TurnoverResponse.None, Evaluate(picture, ball, transitionSeconds: float.NaN).Response);
    }

    /// <summary>Having the ball is not a turnover to answer, and a keeper holding it is not a contest.</summary>
    [Fact]
    public void ATeamInPossession_HasNoTurnoverToAnswer()
    {
        BallState ball = BallAt(0f, 0f);
        List<FootballerState> picture = SecurePicture();

        Assert.Equal(TurnoverResponse.None, Evaluate(picture, ball, possessing: TeamId.Home).Response);
        Assert.Equal(TurnoverResponse.None,
            Evaluate(picture, ball, state: BallControlState.KeeperHeld).Response);
    }

    /// <summary>
    /// The hold: a chosen answer is not replaced inside <see cref="TurnoverResponseModel.MinimumHoldTicks"/>
    /// unless the episode changes — a different carrier, the collision of the window that allowed it, or the
    /// end of the transition.
    /// </summary>
    [Fact]
    public void AChosenAnswer_IsHeldUntilItExpiresOrTheEpisodeChanges()
    {
        BallState ball = BallAt(0f, 0f);
        List<FootballerState> picture = SecurePicture();

        TurnoverSituation pressed = Evaluate(picture, ball);
        TurnoverSituation weaker = Evaluate(ExposedPicture(), ball);   // the window is open; the numbers are not
        Assert.Equal(TurnoverResponse.CounterPress, pressed.Response);
        Assert.True(weaker.CounterPressWindowOpen);
        Assert.NotEqual(TurnoverResponse.CounterPress, weaker.Response);

        // Inside the hold, the counter-press survives a fresh evaluation that disagrees ...
        TurnoverSituation held = TurnoverResponseModel.Hold(pressed, weaker, ticksSinceChosen: 6, carrierChanged: false);
        Assert.Equal(TurnoverResponse.CounterPress, held.Response);
        // ... and still reports the fresh inputs, so a trace can explain what changed.
        Assert.Equal(weaker.CounterPressScore, held.CounterPressScore, 4);

        // The counter-press window is a maximum: an answer chosen inside it cannot outlive it (proven
        // separately in AHeldCounterPress_IsReleased_WhenTheWindowShuts).

        // The commitment expires.
        Assert.Equal(weaker.Response,
            TurnoverResponseModel.Hold(pressed, weaker,
                ticksSinceChosen: TurnoverResponseModel.MinimumHoldTicks, carrierChanged: false).Response);

        // A different carrier is a different question.
        Assert.Equal(weaker.Response,
            TurnoverResponseModel.Hold(pressed, weaker, ticksSinceChosen: 1, carrierChanged: true).Response);

        // The transition ending releases the answer immediately.
        Assert.Equal(TurnoverResponse.None,
            TurnoverResponseModel.Hold(pressed, TurnoverSituation.None, ticksSinceChosen: 1,
                carrierChanged: false).Response);

        // Nothing is held when nothing was chosen.
        Assert.Equal(weaker.Response,
            TurnoverResponseModel.Hold(TurnoverSituation.None, weaker, 1, carrierChanged: false).Response);
    }

    /// <summary>
    /// The window-closing case on its own: a held counter-press is released the moment the profile's window
    /// shuts, even inside the commitment, because the window is a hard maximum.
    /// </summary>
    [Fact]
    public void AHeldCounterPress_IsReleased_WhenTheWindowShuts()
    {
        BallState ball = BallAt(0f, 0f);
        List<FootballerState> picture = SecurePicture();

        TurnoverSituation pressed = Evaluate(picture, ball, windowOpen: true);
        TurnoverSituation shut = Evaluate(picture, ball, windowOpen: false);
        Assert.Equal(TurnoverResponse.CounterPress, pressed.Response);

        TurnoverSituation released = TurnoverResponseModel.Hold(pressed, shut, ticksSinceChosen: 1, carrierChanged: false);
        Assert.Equal(shut.Response, released.Response);
        Assert.NotEqual(TurnoverResponse.CounterPress, released.Response);
    }

    // ---------------------------------------------------------------------------------------------
    // Degenerate input.
    // ---------------------------------------------------------------------------------------------

    /// <summary>An empty picture is a finite, bounded answer, and every published score is a real number.</summary>
    [Fact]
    public void AnEmptyPicture_ProducesAFiniteBoundedAnswer()
    {
        var players = new List<FootballerState>();
        TurnoverSituation situation = Evaluate(players, BallAt(0f, 0f));

        Assert.False(situation.HasCarrier);
        Assert.Equal(TurnoverResponseModel.NeutralConstraint01, situation.CarrierConstraint01, 4);
        Assert.InRange(situation.LocalNumbers01, 0f, 1f);
        Assert.InRange(situation.CentralSecurity01, 0f, 1f);
        Assert.InRange(situation.CarrierConstraint01, 0f, 1f);
        Assert.InRange(situation.EscapeRoute01, 0f, 1f);
        Assert.InRange(situation.CounterPressScore, 0f, 1f);
        Assert.InRange(situation.RetreatScore, 0f, 1f);
        Assert.NotEqual(TurnoverResponse.None, situation.Response);
        Assert.False(string.IsNullOrWhiteSpace(situation.Describe()));
    }

    /// <summary>Same picture, same answer: the comparison consumes no randomness and no clock.</summary>
    [Fact]
    public void TheComparison_IsDeterministic()
    {
        BallState ball = BallAt(0f, 0f);
        TurnoverSituation first = Evaluate(SecurePicture(), ball);
        TurnoverSituation second = Evaluate(SecurePicture(), ball);

        Assert.Equal(first, second);
        Assert.NotEqual(TurnoverResponse.None, first.Response);
    }
}
