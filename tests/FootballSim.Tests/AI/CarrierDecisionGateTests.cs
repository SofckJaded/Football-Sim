using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Scenarios;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// The A4 exit gate, clause by clause, on live decisions and a live fixture.
///
/// The model-level halves of clauses 1 and 2 live in <see cref="PassSuccessModelTests"/> and
/// <see cref="ReceptionValueModelTests"/>; this file asserts the same claims where they matter — in what
/// the carrier's action set offers and what a team does with the ball over time.
/// </summary>
public sealed class CarrierDecisionGateTests
{
    private const int CarrierId = 8;
    private const int FreeMateId = 9;
    private const int MarkedMateId = 10;
    private static readonly PitchDimensions Pitch = new();

    /// <summary>
    /// Gate clause 1: <b>a free useful receiver beats a blocked lane</b>. Two team-mates are available; a
    /// defender stands on the ground line to one of them. The action set must offer the free receiver's
    /// ground pass and refuse the blocked one with a stated reason. (A raised alternative over the blocked
    /// defender may still be offered — A4.2 prices it separately — so the assertion is about the ground
    /// ball, which is the one that would run into him.)
    /// </summary>
    [Fact]
    public void AFreeReceiver_IsOffered_AndTheBlockedLaneIsRefused()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(
            Observation(
                new Vector2(0f, 8f),
                Mate(FreeMateId, new Vector2(6f, 22f)),
                Mate(MarkedMateId, new Vector2(18f, 8f)),
                // Standing on the ground line to the marked team-mate, at the far end of it.
                Away(14, new Vector2(18f, 8.55f))),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 40f);

        Assert.Contains(generator.Candidates,
            c => c.Key.TargetPlayerId == FreeMateId &&
                 c.Command.KickType is KickIntentType.GroundPass or KickIntentType.ThroughPass);
        Assert.DoesNotContain(generator.Candidates,
            c => c.Key.TargetPlayerId == MarkedMateId &&
                 c.Command.KickType is KickIntentType.GroundPass or KickIntentType.ThroughPass);

        List<string> reasons = Enumerable.Range(0, generator.RejectionCount)
            .Select(generator.GetRejection).Select(r => r.Reason).ToList();
        Assert.Contains(CandidateGenerator.RejectionLaneBlocked, reasons);
    }

    /// <summary>
    /// Gate clause 3: <b>an unpressured team progresses rather than circulating indefinitely</b>. The
    /// build-up fixture starts with the ball deep in the home side's own half and no pressure on the
    /// carrier; over ten seconds the ball must move up the pitch and the home side must keep it.
    /// </summary>
    [Fact]
    public void AnUnpressuredTeam_ProgressesUpThePitch()
    {
        ScenarioRunResult run = ScenarioRunner.Run(ScenarioLibrary.BuildUpThreeVersusTwo(), 600);

        float startX = -4.4f;   // the fixture's ball position (see ScenarioLibrary.BuildUpThreeVersusTwo)
        float furthest = float.MinValue;
        float finalX = startX;
        int homeTicks = 0;
        int liveTicks = 0;

        foreach (BallContactEvent contact in run.Contacts)
        {
            furthest = MathF.Max(furthest, contact.BallPosition.X);
        }

        finalX = run.BallPosition.X;
        homeTicks = run.Analytics.HomePossessionTicks;
        liveTicks = run.Analytics.HomePossessionTicks + run.Analytics.AwayPossessionTicks;

        Assert.True(furthest > startX + 10f,
            $"the ball never advanced more than 10 m from its start ({startX:F1} -> {furthest:F1})");
        Assert.True(finalX > startX + 5f,
            $"after ten seconds the ball was back at {finalX:F1} m from a start of {startX:F1} m");
        Assert.True(liveTicks > 0 && homeTicks > liveTicks * 0.6f,
            $"the home side held the ball for {homeTicks}/{liveTicks} live ticks");
        Assert.False(run.Contacts.Any(c => c.Intent is KickIntentType.Shot && c.Team != TeamId.Home), "no own shot");
    }

    /// <summary>
    /// Gate clause 2, at the decision level: a receiver who would have to turn back toward his own goal,
    /// with an opponent behind him, must be valued below the same receiver with room to turn. Measured as
    /// the value the carrier's own action set assigns to the two lanes.
    /// </summary>
    [Fact]
    public void APressuredBackwardFacingReceiver_ScoresBelowAnOpenOne()
    {
        // Same receiver position; in one case he is running back toward his own goal with an opponent
        // goal-side of him, in the other he is standing free with room behind.
        float openValue = PassValueFor(Y: 20f, receiverVelocity: Vector2.Zero, markerZ: 30f);
        float pressuredValue = PassValueFor(Y: 20f, receiverVelocity: new Vector2(-5f, 0f), markerZ: 20.6f);

        Assert.True(openValue > pressuredValue + 0.05f,
            $"the open receiver scored {openValue:F2} against {pressuredValue:F2} for the pressured one");
    }

    /// <summary>Scores the best pass candidate to one receiver in a fixed configuration.</summary>
    private static float PassValueFor(float Y, Vector2 receiverVelocity, float markerZ)
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        var entities = new List<PerceivedEntity>
        {
            new(FreeMateId, TeamId.Home, TacticalRole.Striker,
                new Vector3(12f, 0f, Y), new Vector3(receiverVelocity.X, 0f, receiverVelocity.Y), 1f),
            new(14, TeamId.Away, TacticalRole.CenterBack,
                new Vector3(14f, 0f, markerZ), System.Numerics.Vector3.Zero, 1f)
        };

        generator.GenerateOnBallCandidates(
            ObservationWith(new Vector2(0f, 8f), entities),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 40f);

        UtilityCandidate? pass = generator.Candidates
            .FirstOrDefault(c => c.Key.Kind == CandidateKind.Pass && c.Key.TargetPlayerId == FreeMateId);
        Assert.NotNull(pass);
        return pass!.Value.UtilityScore;
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static PerceivedEntity Mate(int id, Vector2 position) => new(
        id, TeamId.Home, TacticalRole.CentralMidfielder,
        new Vector3(position.X, 0f, position.Y), System.Numerics.Vector3.Zero, 1f);

    private static PerceivedEntity Away(int id, Vector2 position) => new(
        id, TeamId.Away, TacticalRole.CenterBack,
        new Vector3(position.X, 0f, position.Y), System.Numerics.Vector3.Zero, 1f);

    private static FootballerObservation Observation(Vector2 carrierPosition, params PerceivedEntity[] visible) =>
        ObservationWith(carrierPosition, visible.ToList());

    private static FootballerObservation ObservationWith(Vector2 carrierPosition, List<PerceivedEntity> visible) => new()
    {
        SelfId = CarrierId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CentralMidfielder,
        SelfPosition = new Vector3(carrierPosition.X, 0f, carrierPosition.Y),
        SelfVelocity = System.Numerics.Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = new Vector3(carrierPosition.X + 0.4f, 0f, carrierPosition.Y),
        PerceivedBallVelocity = System.Numerics.Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = CarrierId,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = CarrierId,
        TacticalAnchorPosition = new Vector3(carrierPosition.X, 0f, carrierPosition.Y),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 20f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = 900,
        VisiblePlayers = visible,
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.CentralMidfielder),
        SelfAttributes = new PlayerAttributes()
    };
}
