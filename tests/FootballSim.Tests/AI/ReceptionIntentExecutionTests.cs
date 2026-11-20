using System;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Physics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A7.4 — the reception intent on the command/execution boundary.
///
/// These drive the live execution path, not the model: the same body, the same ball and the same tick
/// produce a different first touch when the command carries a different planned touch; a plan on a ball
/// the body cannot reach changes nothing at all (an intent cannot teleport a contact); and a command
/// that carries no intent — the locked human, training, replay, every fixture — is still the pre-A7.4
/// cushion, verified against the old formula written out as an oracle.
/// </summary>
public sealed class ReceptionIntentExecutionTests
{
    private const float Dt = SimConstants.FixedDeltaTime;
    private static readonly float BallRadius = BallGeometry.DefaultRadius;

    // ---------------------------------------------------------------------------------------------
    // (a) The same legal contact produces a different ball because of the intent.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheSameLegalContact_ProducesADifferentTouch_ForKillAndStride()
    {
        FirstTouch kill = RunFirstTouch(new ReceptionIntent(ReceptionTouchKind.Kill));
        FirstTouch stride = RunFirstTouch(new ReceptionIntent(ReceptionTouchKind.StrideTouch));

        // Both contacts were legal and both happened: the intent did not buy one and does not refuse one.
        Assert.StartsWith("FirstTouchCushion:Kill", kill.Receiver.LastExecutionDiagnostic, StringComparison.Ordinal);
        Assert.StartsWith("FirstTouchCushion:StrideTouch", stride.Receiver.LastExecutionDiagnostic, StringComparison.Ordinal);
        Assert.Contains(kill.Execution.ContactEvents, c => c.PlayerId == kill.Receiver.Id && c.Kind == BallContactKind.Collection);
        Assert.Contains(stride.Execution.ContactEvents, c => c.PlayerId == stride.Receiver.Id && c.Kind == BallContactKind.Collection);

        // The ball left the contact measurably differently, in the direction the receiver planned.
        Vector2 killBall = new(kill.Ball.Velocity.X, kill.Ball.Velocity.Z);
        Vector2 strideBall = new(stride.Ball.Velocity.X, stride.Ball.Velocity.Z);
        Vector2 playerVel = new(kill.Receiver.Velocity.X, kill.Receiver.Velocity.Z);
        float killRun = Vector2.Dot(killBall - playerVel, new Vector2(1f, 0f));
        float strideRun = Vector2.Dot(strideBall - playerVel, new Vector2(1f, 0f));

        Assert.True(strideRun > killRun + 0.3f,
            $"the stride touch put the ball {strideRun:F2} m/s ahead and the kill {killRun:F2}; " +
            "the plan did not reach execution");

        // The cushion still may not teleport the ball: the contact moved it by the same few centimetres.
        Assert.True(Vector2.Distance(
                new Vector2(kill.Ball.Position.X, kill.Ball.Position.Z),
                new Vector2(stride.Ball.Position.X, stride.Ball.Position.Z)) <= 0.16f);
    }

    // ---------------------------------------------------------------------------------------------
    // (b) An intent cannot buy a contact the body cannot reach.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AnUnreachableBall_WithAPlan_IsStillRefused()
    {
        // Same receiver, same planned touch, but the ball is 2.5 m away: reach, not intent, decides.
        FirstTouch touch = RunFirstTouch(new ReceptionIntent(ReceptionTouchKind.StrideTouch),
            ballPosition: new Vector3(2.5f, BallRadius, 0f));

        Assert.False(touch.Receiver.LastExecutionDiagnostic.StartsWith("FirstTouchCushion", StringComparison.Ordinal),
            $"an out-of-reach receiver still took a first touch: '{touch.Receiver.LastExecutionDiagnostic}'");
        Assert.DoesNotContain(touch.Execution.ContactEvents, c => c.PlayerId == touch.Receiver.Id);

        // Nothing moved the ball: the intent is a plan, not a force.
        Assert.Equal(2.5f, touch.Ball.Position.X, 1e-4f);
        Assert.Equal(-6f, touch.Ball.Velocity.X, 1e-4f);
        Assert.Equal(0f, touch.Ball.Velocity.Z, 1e-4f);
    }

    // ---------------------------------------------------------------------------------------------
    // (d) A7.5: an early guess can be wrong, and execution reacts to it.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AWrongAnticipation_RealisesLessOfTheRehearsedTouch()
    {
        // The same legal contact and the same planned stride touch; the only difference is where the
        // receiver's flight estimate said he would meet the ball. The accurate plan meets it at (0.6, 0),
        // the wrong one expects it 2.4 m further on.
        ReceptionIntent accurate = new(ReceptionTouchKind.StrideTouch) { MeetingPointXZ = new Vector2(0.6f, 0f) };
        ReceptionIntent wrong = new(ReceptionTouchKind.StrideTouch) { MeetingPointXZ = new Vector2(3.0f, 0f) };

        FirstTouch right = RunFirstTouch(accurate);
        FirstTouch blind = RunFirstTouch(wrong);

        Assert.StartsWith("FirstTouchCushion:StrideTouch", right.Receiver.LastExecutionDiagnostic, StringComparison.Ordinal);
        Assert.StartsWith("FirstTouchCushion:StrideTouch", blind.Receiver.LastExecutionDiagnostic, StringComparison.Ordinal);

        // Both contacts happened; the ball went measurably further ahead for the receiver who met it where
        // he expected to. A wrong anticipation cannot be free.
        Vector2 playerVel = new(right.Receiver.Velocity.X, right.Receiver.Velocity.Z);
        float rightRun = Vector2.Dot(new Vector2(right.Ball.Velocity.X, right.Ball.Velocity.Z) - playerVel,
            new Vector2(1f, 0f));
        float blindRun = Vector2.Dot(new Vector2(blind.Ball.Velocity.X, blind.Ball.Velocity.Z) - playerVel,
            new Vector2(1f, 0f));

        Assert.True(rightRun > blindRun + 0.1f,
            $"a wrong meeting point cost the touch only {rightRun - blindRun:F3} m/s; the plan's accuracy " +
            "is not reaching execution");
    }

    [Fact]
    public void AnAccurateAnticipation_ChangesNothing()
    {
        // A correct plan must be exactly the ai44 touch: the anticipation term is zero inside the contact
        // tolerance, so this is the same arithmetic the previous build ran.
        ReceptionIntent accurate = new(ReceptionTouchKind.StrideTouch) { MeetingPointXZ = new Vector2(0.6f, 0f) };
        FirstTouch touch = RunFirstTouch(accurate);

        Vector2 neutralCushion = NeutralCushionedFor(touch.Receiver);
        float cap = NeutralCapFor(touch.Receiver);
        if (neutralCushion.Length() > cap) neutralCushion *= cap / neutralCushion.Length();

        Vector2 expected = ReceptionTouchModel.Resolve(
            accurate, neutralCushion,
            new Vector2(touch.Receiver.Velocity.X, touch.Receiver.Velocity.Z),
            new Vector2(-6f, 0f), FirstTouchQuality(touch.Receiver), new Vector2(1f, 0f), cap);

        Assert.True(MathF.Abs(expected.X - touch.Ball.Velocity.X) < 1e-3f &&
                    MathF.Abs(expected.Y - touch.Ball.Velocity.Z) < 1e-3f,
            $"expected ({expected.X:F3},{expected.Y:F3}) got ({touch.Ball.Velocity.X:F3},{touch.Ball.Velocity.Z:F3})" +
            $" | quality {FirstTouchQuality(touch.Receiver):F4} control {touch.Receiver.ControlQuality01:F4}" +
            $" balance {touch.Receiver.Balance01:F4} stamina {touch.Receiver.Stamina01:F4}" +
            $" pos ({touch.Receiver.Position.X:F3},{touch.Receiver.Position.Z:F3})" +
            $" vel ({touch.Receiver.Velocity.X:F3},{touch.Receiver.Velocity.Z:F3}) cap {cap:F3}");
    }

    [Fact]
    public void AnUnplannedIntent_IgnoresTheMeetingPoint()
    {
        // No plan, no anticipation: the neutral cushion is untouched by whatever the field holds.
        ReceptionIntent stray = new(ReceptionTouchKind.None) { MeetingPointXZ = new Vector2(40f, 20f) };
        FirstTouch plain = RunFirstTouch(ReceptionIntent.None);
        FirstTouch withStray = RunFirstTouch(stray);

        Assert.Equal("FirstTouchCushion", plain.Receiver.LastExecutionDiagnostic);
        Assert.Equal(plain.Ball.Velocity, withStray.Ball.Velocity);
    }

    // ---------------------------------------------------------------------------------------------
    // (c) Neutral callers: the pre-A7.4 cushion, exactly.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ANeutralCommand_ReproducesThePreA74Cushion()
    {
        FirstTouch touch = RunFirstTouch(ReceptionIntent.None);

        Assert.Equal("FirstTouchCushion", touch.Receiver.LastExecutionDiagnostic);

        // The pre-A7.4 formula, written out: a neutral command must still produce exactly this.
        FootballerState receiver = touch.Receiver;
        Vector2 playerVel = new(receiver.Velocity.X, receiver.Velocity.Z);
        Vector2 expected = NeutralCushionedFor(receiver);
        float cap = NeutralCapFor(receiver);
        if (expected.Length() > cap) expected *= cap / expected.Length();

        Assert.Equal(expected.X, touch.Ball.Velocity.X, 3);
        Assert.Equal(expected.Y, touch.Ball.Velocity.Z, 3);
    }

    /// <summary>
    /// The pre-A7.4 first-touch cushion, before the speed clamp: the oracle for a neutral command.
    /// </summary>
    private static Vector2 NeutralCushionedFor(FootballerState receiver)
    {
        Vector2 playerVel = new(receiver.Velocity.X, receiver.Velocity.Z);
        Vector2 ballXZ = new(0.6f, 0f);
        Vector2 playerXZ = new(receiver.Position.X, receiver.Position.Z);
        Vector2 sweetSpot = playerXZ + new Vector2(1f, 0f) * 0.42f; // moving, not sprinting
        Vector2 toSweetSpot = sweetSpot - ballXZ;

        Vector2 incoming = new(-6f, 0f);
        float quality = FirstTouchQuality(receiver);
        Vector2 retained = incoming * ((1f - quality) * 0.30f);

        float distance = toSweetSpot.Length();
        Vector2 settleDir = distance > 1e-4f ? toSweetSpot / distance : Vector2.Zero;
        float settleSpeed = MathF.Min(0.9f, 0.35f + distance * 1.2f);

        return playerVel * 0.95f + settleDir * settleSpeed + retained;
    }

    /// <summary>The neutral first-touch speed cap the execution layer applies.</summary>
    private static float NeutralCapFor(FootballerState receiver)
    {
        Vector2 playerVel = new(receiver.Velocity.X, receiver.Velocity.Z);
        float quality = FirstTouchQuality(receiver);
        return playerVel.Length() + 1.2f + (1f - quality) * 6f * 0.15f;
    }

    private static float FirstTouchQuality(FootballerState receiver) => Math.Clamp(
        0.50f * receiver.Attributes.FirstTouch01 +
        0.30f * receiver.Balance01 +
        0.20f * receiver.ControlQuality01, 0.15f, 1f);

    [Fact]
    public void TheLockedHumanCommand_CarriesNoReceptionIntent()
    {
        var pitch = new PitchDimensions();
        var human = new HumanInputController(9);
        var player = Player(9, TeamId.Home, TacticalRole.Striker, Vector3.Zero);
        var ball = new BallState
        {
            Position = new Vector3(0.4f, BallRadius, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 9
        };

        FootballerCommand command = human.Decide(Observe(player, new[] { player }, ball), Dt);

        Assert.False(command.ReceptionIntent.IsPlanned,
            "the locked human's command carried an AI reception intent");
        Assert.True(FootballerCommand.Idle.ReceptionIntent.IsPlanned == false);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixture.
    // ---------------------------------------------------------------------------------------------

    private readonly record struct FirstTouch(
        BallState Ball,
        FootballerState Receiver,
        FootballerExecutionSystem Execution);

    /// <summary>
    /// One receiving body running at +X, one pass arriving from +X at his feet, one tick of the real
    /// execution system. Only the command's reception intent varies between fixtures.
    /// </summary>
    private static FirstTouch RunFirstTouch(
        ReceptionIntent intent,
        Vector3? ballPosition = null)
    {
        var pitch = new PitchDimensions();
        var receiver = Player(10, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        receiver.FacingXZ = new Vector2(1f, 0f);
        receiver.Velocity = new Vector3(3f, 0f, 0f);
        receiver.LastCommand = FootballerCommand.Idle with
        {
            DesiredMoveDirectionXZ = new Vector2(1f, 0f),
            DesiredFacingDirectionXZ = new Vector2(1f, 0f),
            ReceptionIntent = intent
        };

        var ball = new BallState
        {
            Position = ballPosition ?? new Vector3(0.6f, BallRadius, 0f),
            Velocity = new Vector3(-6f, 0f, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 8 // a team-mate played the pass
        };

        var players = new[] { receiver };
        var execution = new FootballerExecutionSystem(pitch);
        execution.UpdateLocomotionAndContacts(players, ball, new SeededRandom(1), Dt, tick: 1);
        return new FirstTouch(ball, receiver, execution);
    }

    private static FootballerState Player(
        int id,
        TeamId team,
        TacticalRole role,
        Vector3 position) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Position = position,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(role),
        LastCommand = FootballerCommand.Idle
    };

    private static FootballerObservation Observe(
        FootballerState self,
        FootballerState[] players,
        BallState ball) => new PerceptionSystem().BuildObservation(
        self, players, ball, TeamId.Home, MatchPhase.FirstHalf, 10f,
        attackingDirX: 1f, isSetPieceTaker: false, new TeamTacticalProfile(),
        new SeededRandom(42), possession: null, isDesignatedBallResponder: null, job: null);
}
