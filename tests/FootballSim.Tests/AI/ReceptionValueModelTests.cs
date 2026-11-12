using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A4.3 — the value of the state after a successful reception.
///
/// The A4 exit gate is stated as comparisons between receptions, so each test varies one input a player
/// could observe and asserts the direction the value must move: a pressured backward-facing receiver is
/// worth less than the open equivalent, a progressive reception is worth more than a deep one, and a
/// position with a shooting chance is worth more than one without.
/// </summary>
public sealed class ReceptionValueModelTests
{
    private static readonly PitchDimensions Pitch = new();
    private static readonly ReceptionValueModel Model = new(Pitch);

    /// <summary>
    /// The gate clause: the same point, the same pressure — a receiver who has to turn back toward his own
    /// goal is worth less than one arriving with his momentum up the pitch.
    /// </summary>
    [Fact]
    public void ABackwardFacingReceiver_IsWorthLessThanAForwardFacingOne()
    {
        Vector2 point = new(0f, 5f);
        ReceptionValueTerms forward = Model.Evaluate(Observation(Vector2.Zero, (9, new Vector2(-6f, 5f), new Vector2(6f, 0f))), 9, point, 0.3f);
        ReceptionValueTerms backward = Model.Evaluate(Observation(Vector2.Zero, (9, new Vector2(-6f, 5f), new Vector2(-6f, 0f))), 9, point, 0.3f);

        Assert.True(forward.Turnability01 > backward.Turnability01);
        Assert.True(forward.Value > backward.Value,
            $"a receiver facing forward ({forward.Value:F2}) must beat one facing his own goal ({backward.Value:F2})");
    }

    /// <summary>
    /// And the other half of that clause: an opponent goal-side of the reception takes away the room to
    /// turn, so the same reception is worth less.
    /// </summary>
    [Fact]
    public void AnOpponentGoalSideOfTheReception_TakesValueAway()
    {
        Vector2 point = new(0f, 5f);
        ReceptionValueTerms open = Model.Evaluate(
            Observation(Vector2.Zero, (9, new Vector2(-6f, 5f), Vector2.Zero), (14, new Vector2(20f, -20f), Vector2.Zero)),
            9, point, 0.2f);
        ReceptionValueTerms marked = Model.Evaluate(
            Observation(Vector2.Zero, (9, new Vector2(-6f, 5f), Vector2.Zero), (14, new Vector2(-2f, 5f), Vector2.Zero)),
            9, point, 0.2f);

        Assert.True(open.Turnability01 > marked.Turnability01);
        Assert.True(open.Value > marked.Value);
    }

    /// <summary>A reception that advances the ball is worth more than one played backwards.</summary>
    [Fact]
    public void AProgressiveReception_IsWorthMoreThanADeepOne()
    {
        FootballerObservation observation = Observation(
            new Vector2(-10f, 0f), (9, new Vector2(15f, 5f), Vector2.Zero));

        ReceptionValueTerms forward = Model.Evaluate(observation, 9, new Vector2(15f, 5f), 0.2f);
        ReceptionValueTerms backward = Model.Evaluate(observation, 9, new Vector2(-25f, 5f), 0.2f);

        Assert.True(forward.Progression01 > backward.Progression01);
        Assert.True(forward.Value > backward.Value + 0.1f,
            $"forward {forward.Value:F2} vs backward {backward.Value:F2}");
    }

    /// <summary>Pressure is the caller's one estimate and it lowers the value.</summary>
    [Fact]
    public void PressureAtReception_LowersTheValue()
    {
        FootballerObservation observation = Observation(Vector2.Zero, (9, new Vector2(10f, 4f), Vector2.Zero));
        Vector2 point = new Vector2(10f, 4f);

        ReceptionValueTerms free = Model.Evaluate(observation, 9, point, 0f);
        ReceptionValueTerms pressed = Model.Evaluate(observation, 9, point, 0.9f);

        Assert.Equal(1f, free.Pressure01, precision: 3);
        Assert.True(pressed.Pressure01 < 0.15f);
        Assert.True(free.Value > pressed.Value + 0.1f);
    }

    /// <summary>A reception in the opponent's box has a shooting chance a midfield one does not.</summary>
    [Fact]
    public void AReceptionNearGoal_OffersAShootingChance()
    {
        FootballerObservation observation = Observation(Vector2.Zero, (9, new Vector2(45f, 3f), Vector2.Zero));

        ReceptionValueTerms nearGoal = Model.Evaluate(observation, 9, new Vector2(45f, 3f), 0.2f);
        ReceptionValueTerms halfway = Model.Evaluate(observation, 9, new Vector2(0f, 3f), 0.2f);

        Assert.True(nearGoal.ShootingChance01 > halfway.ShootingChance01);
        Assert.True(nearGoal.Value > halfway.Value);
    }

    /// <summary>An isolated receiver has fewer options than one with a team-mate alongside.</summary>
    [Fact]
    public void ASupportedReception_IsWorthMoreThanAnIsolatedOne()
    {
        Vector2 point = new(10f, 4f);
        ReceptionValueTerms supported = Model.Evaluate(
            Observation(Vector2.Zero, (9, point, Vector2.Zero), (10, new Vector2(16f, 8f), Vector2.Zero)),
            9, point, 0.3f);
        ReceptionValueTerms isolated = Model.Evaluate(
            Observation(Vector2.Zero, (9, point, Vector2.Zero)),
            9, point, 0.3f);

        Assert.True(supported.NextOptions01 > isolated.NextOptions01);
        Assert.True(supported.Value > isolated.Value);
    }

    [Fact]
    public void TheValue_IsFiniteAndBounded()
    {
        FootballerObservation observation = Observation(
            new Vector2(-30f, -20f), (9, new Vector2(30f, 20f), Vector2.Zero), (14, new Vector2(29f, 21f), Vector2.Zero));

        ReceptionValueTerms terms = Model.Evaluate(observation, 9, new Vector2(30f, 20f), 0.5f);

        Assert.InRange(terms.Value, 0f, Model.MaxValue);
        Assert.InRange(terms.Progression01, 0f, 1f);
        Assert.InRange(terms.Turnability01, 0f, 1f);
        Assert.InRange(terms.NextOptions01, 0f, 1f);
        Assert.InRange(terms.ShootingChance01, 0f, 1f);
        Assert.InRange(terms.Structure01, 0f, 1f);
        Assert.InRange(terms.BallSafety01, 0f, 1f);
    }

    /// <summary>The model is pure: the same inputs give the same value, twice over.</summary>
    [Fact]
    public void TheValue_IsDeterministic()
    {
        FootballerObservation observation = Observation(
            new Vector2(-5f, 3f), (9, new Vector2(12f, 8f), new Vector2(4f, 0f)), (14, new Vector2(14f, 9f), Vector2.Zero));

        ReceptionValueTerms first = Model.Evaluate(observation, 9, new Vector2(12f, 8f), 0.4f);
        ReceptionValueTerms second = Model.Evaluate(observation, 9, new Vector2(12f, 8f), 0.4f);

        Assert.Equal(first, second);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static FootballerObservation Observation(
        Vector2 ballXZ,
        params (int Id, Vector2 Position, Vector2 Velocity)[] players)
    {
        var visible = new List<PerceivedEntity>();
        foreach ((int id, Vector2 position, Vector2 velocity) in players)
        {
            // Ids above 13 are the away side, so a test can name an opponent without a second list.
            TeamId team = id >= 14 ? TeamId.Away : TeamId.Home;
            visible.Add(new PerceivedEntity(
                id, team, TacticalRole.CentralMidfielder,
                new Vector3(position.X, 0f, position.Y),
                new Vector3(velocity.X, 0f, velocity.Y),
                1f));
        }

        return new FootballerObservation
        {
            SelfId = 8,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = new Vector3(ballXZ.X, 0f, ballXZ.Y),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(ballXZ.X, 0f, ballXZ.Y),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = 8,
            BallLastTouchedByTeam = TeamId.Home,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = 8,
            TacticalAnchorPosition = new Vector3(ballXZ.X, 0f, ballXZ.Y),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 20f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = 700,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.CentralMidfielder),
            SelfAttributes = new PlayerAttributes()
        };
    }
}
