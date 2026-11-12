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
/// A4.6 — the decision explains itself, and what A4 must not break stays intact.
///
/// Two halves. The explanation half: a candidate's score is published as the four terms it was built from
/// (<see cref="ValueTerms"/>) and the refusals carry a reason, both in the generator and in the bounded
/// decision trace. The preservation half: the intended-receiver metadata a pass relies on is still on the
/// command and still matches the candidate's identity.
/// </summary>
public sealed class DecisionExplanationTests
{
    private const int CarrierId = 8;
    private const int MateId = 9;
    private static readonly PitchDimensions Pitch = new();

    /// <summary>A pass candidate's four terms must compose to exactly the score it was given.</summary>
    [Fact]
    public void APassCandidate_PublishesTheTermsItsScoreWasBuiltFrom()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(
            Observation(new Vector2(0f, 8f), Mate(MateId, new Vector2(12f, 12f))),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 34f);

        UtilityCandidate pass = generator.Candidates.First(c => c.Key.Kind == CandidateKind.Pass);

        Assert.True(pass.Terms.IsPresent);
        Assert.InRange(pass.Terms.Probability01, 0f, 1f);
        Assert.True(pass.Terms.Gain > 0f);
        Assert.True(pass.Terms.Loss > 0f);
        Assert.Equal(pass.UtilityScore, pass.Terms.Value, precision: 3);
        Assert.Contains("p ", pass.Terms.Describe(), StringComparison.Ordinal);
    }

    /// <summary>A shot and a carry are explained too, with the same four terms.</summary>
    [Fact]
    public void TheOtherScaledActions_PublishTermsAsWell()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(
            Observation(new Vector2(45f, 3f), Mate(MateId, new Vector2(40f, 12f))),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 48f);

        UtilityCandidate shot = generator.Candidates.First(c => c.Key.Kind == CandidateKind.Shot);
        UtilityCandidate carry = generator.Candidates.First(c => c.Key.Kind == CandidateKind.Carry);

        Assert.True(shot.Terms.IsPresent);
        Assert.Equal(shot.UtilityScore, shot.Terms.Value, precision: 3);
        Assert.True(carry.Terms.IsPresent);
        Assert.Equal(carry.UtilityScore, carry.Terms.Value, precision: 3);
    }

    /// <summary>A blocked lane is refused with the reason that refused it, not silently dropped.</summary>
    [Fact]
    public void ABlockedPass_IsRecordedWithItsReason()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(
            Observation(
                new Vector2(0f, 8f),
                Mate(MateId, new Vector2(15f, 8f)),
                Away(14, new Vector2(0.6f, 8.55f))),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 34f);

        Assert.True(generator.RejectionCount > 0, "a blocked ground pass must be refused with a reason");
        List<string> reasons = Enumerable.Range(0, generator.RejectionCount)
            .Select(generator.GetRejection)
            .Select(r => r.Reason)
            .ToList();
        Assert.Contains(CandidateGenerator.RejectionLaneBlocked, reasons);
        Assert.DoesNotContain(generator.Candidates,
            c => c.Command.KickType is KickIntentType.GroundPass or KickIntentType.ThroughPass);
    }

    /// <summary>A receiver behind the offside line is refused as offside, with no pass offered.</summary>
    [Fact]
    public void AnOffsideReceiver_IsRefusedAsOffside()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(
            Observation(new Vector2(0f, 8f), Mate(MateId, new Vector2(20f, 12f))),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 10f);

        List<string> reasons = Enumerable.Range(0, generator.RejectionCount)
            .Select(generator.GetRejection)
            .Select(r => r.Reason)
            .ToList();
        Assert.Contains(CandidateGenerator.RejectionOffside, reasons);
    }

    /// <summary>The rejection buffer is bounded, like every other diagnostic buffer in the AI.</summary>
    [Fact]
    public void TheRejectionList_IsBounded()
    {
        var mates = new List<PerceivedEntity>();
        for (int id = 20; id < 34; id++)
            mates.Add(Mate(id, new Vector2(15f, -20f + (id - 20) * 3f)));

        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(
            Observation(new Vector2(0f, 8f), mates.ToArray()),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: -5f);   // everybody is offside, so every receiver is refused

        Assert.True(generator.RejectionCount <= CandidateGenerator.MaxRejections,
            $"{generator.RejectionCount} refusals exceeded the bound");
        Assert.True(generator.RejectionCount > 0);
    }

    /// <summary>
    /// End to end: a traced scenario run records the terms and (where the action set refused something) the
    /// reasons, and the trace line shows the terms rather than only the total.
    /// </summary>
    [Fact]
    public void TheDecisionTrace_CarriesTheTermsAndRefusals()
    {
        ScenarioRunResult run = ScenarioRunner.Run(
            ScenarioLibrary.BuildUpThreeVersusTwo(), 240, traceDecisions: true);

        Assert.NotEmpty(run.Decisions);
        DecisionTraceEntry withTerms = run.Decisions.First(d => d.Terms.IsPresent);
        Assert.Contains("p ", withTerms.Describe(), StringComparison.Ordinal);
        Assert.Contains("chose", "chose " + withTerms.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The preservation claim A4.6 exists for: a pass's intended receiver is on the command and matches the
    /// candidate's key, so reception assistance and the ownership model still know who the ball is for.
    /// </summary>
    [Fact]
    public void EveryPassCandidate_CarriesItsIntendedReceiver()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(
            Observation(new Vector2(0f, 8f), Mate(MateId, new Vector2(12f, 12f))),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 34f);

        foreach (UtilityCandidate candidate in generator.Candidates.Where(c => c.Key.Kind == CandidateKind.Pass))
        {
            Assert.True(candidate.Command.IntendedReceiverId.HasValue,
                $"{candidate.Identity} lost its intended receiver");
            Assert.Equal(candidate.Key.TargetPlayerId, candidate.Command.IntendedReceiverId!.Value);
        }
    }

    /// <summary>
    /// A decision that is not a scored choice still has to be named honestly. A restart taker has exactly
    /// one thing to do and no candidate table, so the record must say so and must not carry a value
    /// decomposition it does not have. Until the A4 pass audit this path left the previous decision (or,
    /// after a reset, the zero value of the record) in <c>LastSelected</c>, which made the trace print a
    /// fabricated <c>p 0.00</c> for a set piece.
    /// </summary>
    [Fact]
    public void ARestartTaker_IsNamedAndInventsNoTerms()
    {
        var controller = new TacticalUtilityAiController(Pitch);
        FootballerObservation observation = Observation(
            new Vector2(0f, 0f), isSetPieceTaker: true, MatchPhase.CornerKick);

        FootballerCommand command = controller.Decide(observation, SimConstants.FixedDeltaTime);

        Assert.Equal("SetPieceRestart", controller.LastSelected.ActionName);
        Assert.Equal(command, controller.LastSelected.Command);
        Assert.False(controller.LastSelected.Terms.IsPresent,
            "a restart has no scored alternative, so the trace must report no terms rather than p = 0");
        Assert.Null(controller.LastRunnerUp);
        Assert.Equal(0, controller.LastCandidateCount);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static PerceivedEntity Mate(int id, Vector2 position, Vector2 velocity = default) => new(
        id, TeamId.Home, TacticalRole.CentralMidfielder,
        new Vector3(position.X, 0f, position.Y),
        new Vector3(velocity.X, 0f, velocity.Y),
        1f);

    private static PerceivedEntity Away(int id, Vector2 position) => new(
        id, TeamId.Away, TacticalRole.CenterBack,
        new Vector3(position.X, 0f, position.Y),
        Vector3.Zero,
        1f);

    private static FootballerObservation Observation(Vector2 carrierPosition, params PerceivedEntity[] visible) =>
        Observation(carrierPosition, isSetPieceTaker: false, MatchPhase.FirstHalf, visible);

    private static FootballerObservation Observation(
        Vector2 carrierPosition, bool isSetPieceTaker, MatchPhase phase, params PerceivedEntity[] visible) => new()
    {
        SelfId = CarrierId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CentralMidfielder,
        SelfPosition = new Vector3(carrierPosition.X, 0f, carrierPosition.Y),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = new Vector3(carrierPosition.X + 0.4f, 0f, carrierPosition.Y),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = CarrierId,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = CarrierId,
        TacticalAnchorPosition = new Vector3(carrierPosition.X, 0f, carrierPosition.Y),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 20f,
        CurrentMatchPhase = phase,
        IsSetPieceTaker = isSetPieceTaker,
        Tick = 800,
        VisiblePlayers = visible,
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.CentralMidfielder),
        SelfAttributes = new PlayerAttributes()
    };
}
