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
            Observation(Vector2.Zero, (9, new Vector2(-6f, 5f), Vector2.Zero), (14, new Vector2(2f, 5f), Vector2.Zero)),
            9, point, 0.2f);

        Assert.True(open.Turnability01 > marked.Turnability01);
        Assert.True(open.Value > marked.Value);
    }

    /// <summary>
    /// The side matters, not just the distance. The same opponent two metres from the reception is only a
    /// body in the way when he is <b>between the receiver and the goal</b> (goal-side, +X here). Standing
    /// behind the reception he is a pressure from behind — charged by the caller's pressure term, not by
    /// the turn — so the room to turn has to stay intact. (The projection test was inverted until the A4
    /// pass audit, which made a marked forward look like a free one.)
    /// </summary>
    [Fact]
    public void AnOpponentBehindTheReception_DoesNotTakeTheRoomToTurn()
    {
        Vector2 point = new(0f, 5f);
        ReceptionValueTerms goalSide = Model.Evaluate(
            Observation(Vector2.Zero, (9, new Vector2(-6f, 5f), Vector2.Zero), (14, new Vector2(2f, 5f), Vector2.Zero)),
            9, point, 0.2f);
        ReceptionValueTerms behind = Model.Evaluate(
            Observation(Vector2.Zero, (9, new Vector2(-6f, 5f), Vector2.Zero), (14, new Vector2(-2f, 5f), Vector2.Zero)),
            9, point, 0.2f);

        Assert.True(behind.Turnability01 > goalSide.Turnability01,
            $"an opponent behind the reception ({behind.Turnability01:F2}) must leave more room to turn " +
            $"than one goal-side of it ({goalSide.Turnability01:F2})");
        Assert.True(behind.Value > goalSide.Value);
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

    // ---- A5.5b: the reception in the box is worth the chance it creates ----------------------------

    /// <summary>
    /// The chance term is <b>the shot model's own number</b>, not a second curve (A5.5b). This is the
    /// "do not add a second estimate" rule: a reception eight metres out with a clear lane quotes exactly the
    /// conversion probability <see cref="ActionValueScale.ShotConversion"/> gives the shot from there.
    /// </summary>
    [Fact]
    public void AReceptionsShootingChance_IsTheShotModelsOwnConversion()
    {
        FootballerObservation observation = Observation(new Vector2(30f, -10f), (9, new Vector2(44.5f, 0f), Vector2.Zero));
        Vector2 point = new(44.5f, 0f);
        float distance = Vector2.Distance(point, new Vector2(Pitch.HalfLength, 0f));

        ReceptionValueTerms terms = Model.Evaluate(observation, 9, point, 0.2f);

        Assert.Equal(ActionValueScale.ShotConversion(distance, 1f), terms.ShootingChance01, precision: 3);
    }

    /// <summary>
    /// A body in the shooting lane takes the chance away — the term answers "is a shot on from here", not
    /// "how close to the goal is here". Without this, every reception in a crowded box would be priced like
    /// a clean one and the delivery would be aimed at the most crowded spot on the pitch.
    /// </summary>
    [Fact]
    public void ABlockedShootingLane_TakesTheChanceAway()
    {
        Vector2 point = new(44.5f, 0f);
        ReceptionValueTerms clear = Model.Evaluate(
            Observation(new Vector2(30f, -10f), (9, point, Vector2.Zero)), 9, point, 0.2f);
        ReceptionValueTerms blocked = Model.Evaluate(
            Observation(new Vector2(30f, -10f), (9, point, Vector2.Zero), (16, new Vector2(48f, 0f), Vector2.Zero)),
            9, point, 0.2f);

        Assert.True(clear.ShootingChance01 > 0.35f, $"a clear lane at 8 m quoted {clear.ShootingChance01:F2}");
        // The shot's own lane weighting keeps a floor under a blocked shot (a defender in the way reduces
        // the conversion, it does not forbid the attempt), so the blocked number is the floor, not zero.
        Assert.True(blocked.ShootingChance01 <= clear.ShootingChance01 * 0.65f,
            $"a defender in the lane left {blocked.ShootingChance01:F2} of {clear.ShootingChance01:F2}");
        Assert.True(clear.Value > blocked.Value + 0.5f, $"clear {clear.Value:F2} vs blocked {blocked.Value:F2}");
    }

    /// <summary>
    /// A45 — the A4.4 invariant, restated against the term A5.5b added: a pass to a man in the box is worth
    /// its completion probability times the <b>share</b> of the chance it creates, and a shot from the same
    /// place pays all of it. So even a near-certain pass into the box must not outbid the shot, or the
    /// carrier stops shooting (the measured A4.4 defect).
    /// </summary>
    [Theory]
    [InlineData(6f)]
    [InlineData(8f)]
    public void AClearShotInTheBox_StillBeatsAPassToTheSamePlace(float metersFromGoal)
    {
        Vector2 point = new(Pitch.HalfLength - metersFromGoal, 0f);
        FootballerObservation observation = Observation(
            new Vector2(point.X - 6.5f, 0f), (9, point, Vector2.Zero));

        ReceptionValueTerms reception = Model.Evaluate(observation, 9, point, 0.2f);
        float pass = PassSuccessModel.MaxCompletion01 * reception.Value;
        float shot = ActionValueScale.ShotValue(metersFromGoal, 1f);

        Assert.True(pass < shot,
            $"a {PassSuccessModel.MaxCompletion01:F2}-complete pass into {metersFromGoal:F0} m from goal is " +
            $"worth {pass:F2} and the shot from there {shot:F2}; the shot has to stay in front");
    }

    /// <summary>An isolated receiver has fewer options than one with a team-mate alongside.</summary>
    [Fact]
    public void ASupportedReception_IsWorthMoreThanAnIsolatedOne()    {
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
