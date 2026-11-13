using System;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A4.9 — the restart taker is a passer.
///
/// A restart used to be taken with a blind ball at the receiver's current feet and a linear power ramp
/// (<c>distance / 32</c>). Measured on a thirty-minute 11v11 segment that completed 29 % of throw-ins and
/// turned over 57 %, and every long one was a raised ball that flew 10–20 m past its receiver: a 22 m
/// throw travelled 40 m and a 30 m throw travelled 50 m. These tests pin the two properties that fixed
/// it — the delivery is planned by the carrier's own planner, and the taker looks for a receiver whose
/// lane is actually open before settling for one whose is not.
/// </summary>
public sealed class SetPieceDeliveryTests
{
    private const int TakerId = 8;
    private static readonly PitchDimensions Pitch = new();

    /// <summary>
    /// A throw-in twenty-four metres up the line is a *ground* pass, not a punt. The old threshold lofted
    /// everything past 22 m, and the raised power ramp sent that ball well beyond the receiver.
    /// </summary>
    [Fact]
    public void ALongThrowIn_IsPlayedAlongTheGround()
    {
        var controller = new TacticalUtilityAiController(Pitch);
        FootballerObservation observation = RestartObservation(
            0f, Midfielder(9, new Vector2(24f, Pitch.HalfWidth - 8f)));

        FootballerCommand command = controller.Decide(observation, SimConstants.FixedDeltaTime);

        Assert.Equal(KickIntentType.GroundPass, command.KickType);
        Assert.Equal(9, command.IntendedReceiverId);
        // The planner's powers are anchored to a trapable/crisp flight, never the old distance ramp.
        Assert.InRange(command.KickPower01, 0.2f, 1.0f);
    }

    /// <summary>
    /// The taker prefers a receiver he can actually reach. An advanced team-mate with a defender standing
    /// on the pass line loses to a less advanced one standing in space, which is the difference between a
    /// contested restart and a retained ball.
    /// </summary>
    [Fact]
    public void ABlockedReceiver_LosesToAnOpenOne()
    {
        var controller = new TacticalUtilityAiController(Pitch);
        FootballerObservation observation = RestartObservation(
            0f,
            Midfielder(9, new Vector2(24f, Pitch.HalfWidth - 6f)),        // advanced, but marked
            Midfielder(10, new Vector2(14f, Pitch.HalfWidth - 18f)),      // shorter, in space
            Opponent(14, new Vector2(13f, Pitch.HalfWidth - 3.2f)));      // standing on the first line

        FootballerCommand command = controller.Decide(observation, SimConstants.FixedDeltaTime);

        Assert.Equal(10, command.IntendedReceiverId);
        Assert.Equal(KickIntentType.GroundPass, command.KickType);
    }

    /// <summary>A delivered restart still names the man it is for, so reception assistance can use it.</summary>
    [Fact]
    public void ADeliveredRestart_NamesItsReceiver()
    {
        var controller = new TacticalUtilityAiController(Pitch);
        FootballerObservation observation = RestartObservation(
            0f, Midfielder(9, new Vector2(12f, Pitch.HalfWidth - 10f), new Vector2(3f, 0f)));

        FootballerCommand command = controller.Decide(observation, SimConstants.FixedDeltaTime);

        Assert.Equal(9, command.IntendedReceiverId);
        Assert.False(controller.LastSelected.Terms.IsPresent,
            "a restart has no scored alternative, so its trace must not invent terms");
    }

    private static PerceivedEntity Midfielder(int id, Vector2 position, Vector2 velocity = default) => new(
        id, TeamId.Home, TacticalRole.CentralMidfielder,
        new Vector3(position.X, 0f, position.Y),
        new Vector3(velocity.X, 0f, velocity.Y),
        1f);

    private static PerceivedEntity Opponent(int id, Vector2 position) => new(
        id, TeamId.Away, TacticalRole.CenterBack,
        new Vector3(position.X, 0f, position.Y),
        Vector3.Zero,
        1f);

    /// <summary>
    /// A throw-in taker: standing on the touchline, with the ball placed 0.45 m up the line — exactly how
    /// <c>MatchRulesEngine.TriggerSetPiece</c> positions the taker and the ball.
    /// </summary>
    private static FootballerObservation RestartObservation(
        float takerX,
        params PerceivedEntity[] visible)
    {
        Vector2 selfXZ = new(takerX, Pitch.HalfWidth - 1f);
        Vector2 ballXZ = new(takerX + 0.45f, Pitch.HalfWidth - 1f);

        return new FootballerObservation
        {
            SelfId = TakerId,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.FullBack,
            SelfPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(ballXZ.X, 0f, ballXZ.Y),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = null,
            BallLastTouchedByTeam = TeamId.Home,
            BallControlState = BallControlState.Loose,
            TacticalAnchorPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 120f,
            CurrentMatchPhase = MatchPhase.ThrowIn,
            IsSetPieceTaker = true,
            Tick = 7200,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.FullBack),
            SelfAttributes = new PlayerAttributes()
        };
    }
}
