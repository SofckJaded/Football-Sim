using System;
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
/// A7.5 — the four error sources reaching their consumers, and the ablation that switches them off.
///
/// <see cref="PlayerCognitiveStateTests"/> pins the numbers; these prove the wiring: pressure really does
/// reduce a first touch's control over a paired sample (the A7 exit gate's clause), fatigue really does
/// blur the picture of the pitch and the view of the ball, the evaluation error really does change which
/// candidate a player prefers — and with <see cref="AiRuntimeConfig.EnableCognitiveError"/> off all four are
/// exactly the pre-A7.5 build.
/// </summary>
public sealed class CognitiveErrorTests
{
    private const float Dt = SimConstants.FixedDeltaTime;
    private static readonly float BallRadius = BallGeometry.DefaultRadius;

    // ---------------------------------------------------------------------------------------------
    // Execution error: pressure reduces control quality (the A7 exit-gate clause).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void PressureReducesControlQuality_OverAPairedSample()
    {
        FirstTouch free = RunFirstTouch(presserX: null);
        FirstTouch pressed = RunFirstTouch(presserX: -1.0f);

        Assert.Equal("FirstTouchCushion", free.Receiver.LastExecutionDiagnostic);
        Assert.Equal("FirstTouchCushion", pressed.Receiver.LastExecutionDiagnostic);

        // Both took a legal first touch on the identical ball. The pressed one keeps more of the pass's
        // pace, because a body under pressure controls the ball less well. (The presser stands behind the
        // receiver and is out of reach of the ball, so nothing else about the two worlds differs.)
        Vector2 freeBall = new(free.Ball.Velocity.X, free.Ball.Velocity.Z);
        Vector2 pressedBall = new(pressed.Ball.Velocity.X, pressed.Ball.Velocity.Z);
        Vector2 incomingDirection = new(-1f, 0f);

        float extraPace = Vector2.Dot(pressedBall - freeBall, incomingDirection);
        Assert.True(extraPace > 0.02f,
            $"pressure only changed the touch by {extraPace:F3} m/s; control quality did not move");

        // And it is the cognitive term that did it: the module's own number for this fixture.
        float expected = PlayerCognitiveState.ExecutionError01(
            pressed.Receiver.Attributes, pressed.Receiver.Stamina01,
            BallContactRules.PressureAtContact(pressed.Receiver.Position, pressed.Receiver.Team,
                new[] { pressed.Receiver, pressed.Presser! }));
        Assert.True(expected > 0f);
        Assert.True(pressed.Receiver.ControlQuality01 >= 0f);
    }

    [Fact]
    public void WithTheFeatureOff_ThePairedSampleIsIdentical()
    {
        FirstTouch free = RunFirstTouch(presserX: null, cognitiveError: false);
        FirstTouch pressed = RunFirstTouch(presserX: -1.0f, cognitiveError: false);

        // Ablation: the pre-A7.5 build has no path from contact pressure to a first touch, so the two
        // worlds' balls must be bit-for-bit the same.
        Assert.Equal(free.Ball.Velocity, pressed.Ball.Velocity);
        Assert.Equal(free.Ball.Position, pressed.Ball.Position);
    }

    [Fact]
    public void WithTheFeatureOff_AWrongAnticipationIsNotPenalised()
    {
        // A7.5's second half is ablated by the same switch: the plan still carries a meeting point, but the
        // previous build had no way to act on it, so an accurate and a badly wrong plan touch identically.
        ReceptionIntent accurate = new(ReceptionTouchKind.StrideTouch) { MeetingPointXZ = new Vector2(0.6f, 0f) };
        ReceptionIntent wrong = new(ReceptionTouchKind.StrideTouch) { MeetingPointXZ = new Vector2(3.0f, 0f) };

        FirstTouch right = RunFirstTouch(presserX: null, cognitiveError: false, intent: accurate);
        FirstTouch blind = RunFirstTouch(presserX: null, cognitiveError: false, intent: wrong);

        Assert.Equal(right.Ball.Velocity, blind.Ball.Velocity);
        Assert.Equal(right.Ball.Position, blind.Ball.Position);
    }

    // ---------------------------------------------------------------------------------------------
    // Observation error: a tired player's picture is blurrier, from the same draws.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ATiredObserversPicture_IsNoisier()
    {
        (Vector3 freshEstimate, Vector3 truth) = ObserveOpponent(stamina: 1f);
        (Vector3 tiredEstimate, Vector3 tiredTruth) = ObserveOpponent(stamina: 0.2f);

        Assert.Equal(truth, tiredTruth);

        float freshError = Vector2.Distance(
            new Vector2(freshEstimate.X, freshEstimate.Z), new Vector2(truth.X, truth.Z));
        float tiredError = Vector2.Distance(
            new Vector2(tiredEstimate.X, tiredEstimate.Z), new Vector2(truth.X, truth.Z));

        Assert.True(tiredError > freshError * 1.1f,
            $"the tired observer's positional error ({tiredError:F3} m) did not exceed the fresh one's " +
            $"({freshError:F3} m); fatigue did not reach the observation");
    }

    [Fact]
    public void WithTheFeatureOff_ATiredObserverSeesExactlyWhatAFreshOneSees()
    {
        (Vector3 freshEstimate, _) = ObserveOpponent(stamina: 1f, cognitiveError: false);
        (Vector3 tiredEstimate, _) = ObserveOpponent(stamina: 0.2f, cognitiveError: false);

        Assert.Equal(freshEstimate, tiredEstimate);
    }

    // ---------------------------------------------------------------------------------------------
    // Reaction latency: the ball view's noise follows the delayed view's own age.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ATiredObserversBallView_IsNoisier()
    {
        // A stationary ball: the perceived *velocity* is pure reaction noise, so its magnitude measures the
        // reaction-error scale directly. Same seed, so the same Gaussian draws, scaled differently.
        float freshNoise = ObservedBallVelocityNoise(stamina: 1f);
        float tiredNoise = ObservedBallVelocityNoise(stamina: 0.2f);

        Assert.True(freshNoise > 0f);
        Assert.True(tiredNoise > freshNoise * 1.1f,
            $"the tired ball view's noise ({tiredNoise:F4} m/s) did not exceed the fresh one's " +
            $"({freshNoise:F4} m/s); fatigue did not reach the reaction latency");
    }

    // ---------------------------------------------------------------------------------------------
    // Evaluation error: the misjudgement reaches the ranking, and only when a cause moved.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheEvaluationError_IsInertForAFreshComposedPlayer()
    {
        var on = new TacticalUtilityAiController(new PitchDimensions(), null, CognitiveOn);
        var off = new TacticalUtilityAiController(new PitchDimensions(), null, CognitiveOff);

        for (int tick = 300; tick < 400; tick++)
        {
            FootballerObservation observation = CarrierObservation(tick, stamina01: 1f, composure01: 0.8f);
            on.Decide(observation, Dt);
            off.Decide(observation, Dt);

            Assert.False(on.LastEvaluationChangedChoice);
            Assert.Equal(off.LastSelected.UtilityScore, on.LastSelected.UtilityScore);
            Assert.Equal(off.LastSelected.ActionName, on.LastSelected.ActionName);
        }
    }

    [Fact]
    public void TheEvaluationError_ChangesTheChoice_WhenThePlayerIsSpent()
    {
        var controller = new TacticalUtilityAiController(new PitchDimensions(), null, CognitiveOn);

        int changed = 0;
        for (int tick = 300; tick < 600; tick++)
        {
            // The same picture every tick; only the player's own state (and therefore his judgement of it)
            // differs from the fresh case above.
            FootballerObservation observation = CarrierObservation(tick, stamina01: 0f, composure01: 0f);
            controller.Decide(observation, Dt);
            if (controller.LastEvaluationChangedChoice) changed++;
        }

        Assert.True(changed > 0,
            $"600 decisions by a spent, rattled player never misjudged a choice; the evaluation error is inert. " +
            $"Candidates: {Describe(controller)}");
        Assert.False(string.IsNullOrEmpty(controller.LastRawBestActionName));
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures.
    // ---------------------------------------------------------------------------------------------

    private static readonly AiRuntimeConfig CognitiveOn = new()
    {
        EnableScanning = false,
        EnableTeamCalls = false
    };

    private static readonly AiRuntimeConfig CognitiveOff = new()
    {
        EnableScanning = false,
        EnableTeamCalls = false,
        EnableCognitiveError = false
    };

    private readonly record struct FirstTouch(
        BallState Ball,
        FootballerState Receiver,
        FootballerState? Presser);

    /// <summary>
    /// One receiving body running at +X, one pass arriving from +X at his feet, one tick of the real
    /// execution system, and — in the pressed variant — one opponent standing behind him, out of reach of
    /// the ball so he cannot contest it.
    /// </summary>
    private static FirstTouch RunFirstTouch(
        float? presserX,
        bool cognitiveError = true,
        ReceptionIntent intent = default)
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

        FootballerState? presser = null;
        var players = new List<FootballerState> { receiver };
        if (presserX is float x)
        {
            presser = Player(205, TeamId.Away, TacticalRole.DefensiveMidfielder, new Vector3(x, 0f, 0.2f));
            presser.FacingXZ = new Vector2(1f, 0f);
            players.Add(presser);
        }

        var ball = new BallState
        {
            Position = new Vector3(0.6f, BallRadius, 0f),
            Velocity = new Vector3(-6f, 0f, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 8
        };

        var execution = new FootballerExecutionSystem(pitch, cognitiveError);
        execution.UpdateLocomotionAndContacts(players, ball, new SeededRandom(1), Dt, tick: 1);
        return new FirstTouch(ball, receiver, presser);
    }

    /// <summary>One observation of a far opponent, at a given stamina, from a fixed seed.</summary>
    private static (Vector3 Estimated, Vector3 Truth) ObserveOpponent(float stamina, bool cognitiveError = true)
    {
        var observer = Player(10, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        observer.FacingXZ = new Vector2(1f, 0f);
        observer.Stamina01 = stamina;

        var opponent = Player(205, TeamId.Away, TacticalRole.CenterBack, new Vector3(30f, 0f, 0f));
        var ball = new BallState { Position = new Vector3(20f, BallRadius, 0f), Velocity = Vector3.Zero };

        var ai = new AiRuntimeConfig { EnableScanning = false, EnableCognitiveError = cognitiveError };
        var perception = new PerceptionSystem(null, ai);
        FootballerObservation observation = perception.BuildObservation(
            observer, new[] { observer, opponent }, ball, TeamId.Home, MatchPhase.FirstHalf, 10f,
            attackingDirX: 1f, isSetPieceTaker: false, new TeamTacticalProfile(),
            new SeededRandom(7), possession: null, isDesignatedBallResponder: null, tick: 0);

        foreach (PerceivedEntity entity in observation.VisiblePlayers)
        {
            if (entity.PlayerId == opponent.Id) return (entity.EstimatedPosition, opponent.Position);
        }

        throw new Xunit.Sdk.XunitException("the far opponent was not in the picture at all");
    }

    /// <summary>
    /// The perceived ball velocity's magnitude for a stationary ball: pure reaction noise, so it measures
    /// the delay the ball view is drawn from.
    /// </summary>
    private static float ObservedBallVelocityNoise(float stamina)
    {
        var observer = Player(10, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        observer.FacingXZ = new Vector2(1f, 0f);
        observer.Stamina01 = stamina;

        var ball = new BallState { Position = new Vector3(12f, BallRadius, 0f), Velocity = Vector3.Zero };
        var ai = new AiRuntimeConfig { EnableScanning = false };
        var perception = new PerceptionSystem(null, ai);
        FootballerObservation observation = perception.BuildObservation(
            observer, new[] { observer }, ball, TeamId.Home, MatchPhase.FirstHalf, 10f,
            attackingDirX: 1f, isSetPieceTaker: false, new TeamTacticalProfile(),
            new SeededRandom(11), possession: null, isDesignatedBallResponder: null, tick: 0);

        Vector3 velocity = observation.PerceivedBallVelocity;
        return new Vector3(velocity.X, velocity.Y, velocity.Z).Length();
    }

    /// <summary>
    /// A carrier in the final third with two mirrored team-mates ahead of him: the two pass candidates are
    /// worth the same to the millimetre, which is exactly the situation a misjudged value can change. Any
    /// asymmetry in the fixture (a runner, an off-centre opponent) would make one of them obviously better
    /// and the test would then be measuring the fixture instead of the error.
    /// </summary>
    private static FootballerObservation CarrierObservation(int tick, float stamina01, float composure01)
    {
        var leftMate = new Vector3(10f, 0f, 7f);
        var rightMate = new Vector3(10f, 0f, -7f);

        return new FootballerObservation
        {
            SelfId = 110,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = Vector3.Zero,
            SelfVelocity = new Vector3(4f, 0f, 0f),
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = stamina01,
            AvailableToKick = true,
            ControlQuality01 = 0.9f,
            PerceivedBallPosition = new Vector3(0.35f, BallRadius, 0f),
            PerceivedBallVelocity = new Vector3(4f, 0f, 0f),
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = 110,
            BallLastTouchedByTeam = TeamId.Home,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = 110,
            IsDesignatedBallResponder = true,
            TacticalAnchorPosition = new Vector3(5f, 0f, 0f),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = tick * Dt,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = tick,
            VisiblePlayers = new List<PerceivedEntity>
            {
                new(111, TeamId.Home, TacticalRole.CentralMidfielder, leftMate, new Vector3(3f, 0f, 0f), 0.9f),
                new(112, TeamId.Home, TacticalRole.CentralMidfielder, rightMate, new Vector3(3f, 0f, 0f), 0.9f),
                // The defenders stand deep, so the mirrored team-mates are onside and the two passes are
                // actually offered (an opponent behind the ball would put the offside line on top of them).
                new(205, TeamId.Away, TacticalRole.CenterBack, new Vector3(25f, 0f, 0f),
                    new Vector3(-1f, 0f, 0f), 0.9f),
                new(206, TeamId.Away, TacticalRole.FullBack, new Vector3(30f, 0f, 4f),
                    new Vector3(1f, 0f, 0f), 0.9f)
            },
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.CentralMidfielder),
            SelfAttributes = new PlayerAttributes { Composure01 = composure01 }
        };
    }

    private static FootballerState Player(int id, TeamId team, TacticalRole role, Vector3 position) => new()
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

    /// <summary>Candidate table for a failure message.</summary>
    private static string Describe(TacticalUtilityAiController controller)
    {
        var text = new System.Text.StringBuilder();
        text.Append($"raw best {controller.LastRawBestActionName}; ");
        foreach (UtilityCandidate candidate in controller.LastCandidates)
            text.Append(candidate.ActionName).Append('=').Append(candidate.UtilityScore.ToString("F3")).Append("; ");
        return text.ToString();
    }
}
