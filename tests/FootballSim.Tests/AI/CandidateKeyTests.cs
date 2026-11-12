using System;
using System.Collections.Generic;
using System.Linq;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Scenarios;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A1.1c — stable candidate identity.
///
/// Action names carry identity today by accident: "Pass(GroundPass)To#9" names a receiver in prose. The
/// contract is that identity is kind + target + variant, survives a relabelling, and is independent of
/// the utility score or a floating-point target point, so a commitment can be matched reliably.
/// </summary>
public sealed class CandidateKeyTests
{
    [Fact]
    public void Describe_ComposesKindTargetAndVariant()
    {
        Assert.Equal("Shot:goal", new CandidateKey(CandidateKind.Shot, TargetTag: "goal").Describe());
        Assert.Equal("Carry:forward", new CandidateKey(CandidateKind.Carry, TargetTag: "forward").Describe());
        Assert.Equal("Shape:support", new CandidateKey(CandidateKind.Shape, TargetTag: "support").Describe());

        // A player target replaces the tag and promotes the tag to the variant slot.
        Assert.Equal("Pass:#9:GroundPass",
            new CandidateKey(CandidateKind.Pass, 9, "GroundPass").Describe());

        // The adjusted variant is a suffix on the same job, not a different kind.
        Assert.Equal("Job:Marker", new CandidateKey(CandidateKind.Job, TargetTag: "Marker").Describe());
        Assert.Equal("Job:Marker:adj", new CandidateKey(CandidateKind.Job, TargetTag: "Marker", Adjusted: true).Describe());
        Assert.Equal("Pass:#9:ThroughPass:adj",
            new CandidateKey(CandidateKind.Pass, 9, "ThroughPass", Adjusted: true).Describe());
    }

    [Fact]
    public void Equality_IsStructural()
    {
        var a = new CandidateKey(CandidateKind.Pass, 9, "GroundPass");
        var b = new CandidateKey(CandidateKind.Pass, 9, "GroundPass");
        var c = new CandidateKey(CandidateKind.Pass, 10, "GroundPass");
        var d = new CandidateKey(CandidateKind.Pass, 9, "ThroughPass");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
    }

    [Fact]
    public void None_IsRecognisable()
    {
        Assert.True(CandidateKey.None.IsNone);
        Assert.False(new CandidateKey(CandidateKind.Shot, TargetTag: "goal").IsNone);
        Assert.False(new CandidateKey(CandidateKind.Pass, 9, "GroundPass").IsNone);
    }

    [Fact]
    public void AsAdjusted_KeepsTargetAndKind()
    {
        var key = new CandidateKey(CandidateKind.Job, TargetTag: "Width");
        CandidateKey adjusted = key.AsAdjusted(true);

        Assert.Equal(key.Kind, adjusted.Kind);
        Assert.Equal(key.TargetTag, adjusted.TargetTag);
        Assert.NotEqual(key, adjusted);
    }

    [Fact]
    public void Identity_DoesNotChangeWhenTheUtilityOrTheTargetPointChanges()
    {
        // The whole point of the key: a commitment must survive a score wobble and a target point that
        // moves by centimetres every tick.
        var first = new UtilityCandidate("DriveWithBall", 0.71f, FootballerCommand.Idle)
        {
            Key = new CandidateKey(CandidateKind.Carry, TargetTag: "forward"),
            TargetXZ = new System.Numerics.Vector3(10.0f, 0f, 2.0f)
        };
        var second = new UtilityCandidate("DriveWithBall", 0.68f, FootballerCommand.Idle)
        {
            Key = new CandidateKey(CandidateKind.Carry, TargetTag: "forward"),
            TargetXZ = new System.Numerics.Vector3(10.4f, 0f, 2.1f)
        };

        Assert.Equal(first.Identity, second.Identity);
        Assert.Equal("Carry:forward", second.Identity);
    }

    [Fact]
    public void Identity_FallsBackToTheActionNameWhenNoKeyWasSet()
    {
        var candidate = new UtilityCandidate("SomeUnkeyedAction", 0.5f, FootballerCommand.Idle);

        Assert.True(candidate.Key.IsNone);
        Assert.Equal("SomeUnkeyedAction", candidate.Identity);
    }

    [Fact]
    public void TwoDifferentPasses_TwoDifferentIdentities_EvenWithTheSameLabelShape()
    {
        var toNine = new UtilityCandidate("Pass(GroundPass)To#9", 0.6f, FootballerCommand.Idle)
        {
            Key = new CandidateKey(CandidateKind.Pass, 9, "GroundPass")
        };
        var toTen = new UtilityCandidate("Pass(GroundPass)To#10", 0.6f, FootballerCommand.Idle)
        {
            Key = new CandidateKey(CandidateKind.Pass, 10, "GroundPass")
        };

        Assert.NotEqual(toNine.Key, toTen.Key);
        Assert.NotEqual(toNine.Identity, toTen.Identity);
    }

    [Fact]
    public void EveryCandidateTheControllerGenerates_CarriesAKey()
    {
        // A candidate without a key would silently fall back to label identity, which is exactly the
        // failure mode this change removes. One fixture exercises the on-ball set, the other the
        // off-ball set.
        AssertAllKeyed(ScenarioLibrary.BuildUpThreeVersusTwo(), playerId: 103);
        AssertAllKeyed(ScenarioLibrary.CounterattackThreeVersusThree(), playerId: 109);
    }

    [Fact]
    public void TraceRecordsTheCandidateKeyInsteadOfNull()
    {
        ScenarioRunResult run = ScenarioRunner.Run(ScenarioLibrary.BuildUpThreeVersusTwo(), 60, traceDecisions: true);
        var aiEntries = run.Decisions.Where(d => d.CandidateCount > 0).ToList();

        Assert.NotEmpty(aiEntries);
        Assert.All(aiEntries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.CandidateKey),
            $"t{entry.Tick} p{entry.PlayerId} recorded no candidate key"));

        // Keys must be stable enough to appear more than once — a per-tick random label would not.
        Assert.Contains(aiEntries.GroupBy(d => d.CandidateKey!), g => g.Count() > 3);
    }

    [Fact]
    public void CommitmentFollowsTheKey_NotTheLabel()
    {
        // The controller must commit to an identity, not to a display string. The crafted observation has
        // the ball at the player's feet, so it commits to a carry; the assertion is about the key being
        // the thing that is held and about it describing itself consistently.
        var controller = new TacticalUtilityAiController(new PitchDimensions());

        Assert.True(controller.CommittedKey.IsNone);
        Assert.Equal("Idle", controller.CommittedActionName);

        controller.Decide(Observation(1), SimConstants.FixedDeltaTime);

        Assert.False(controller.CommittedKey.IsNone);
        Assert.NotEqual("Idle", controller.CommittedActionName);
        Assert.Equal(controller.CommittedKey.Describe(), controller.CommittedKey.ToString());
        Assert.True(controller.CommitmentRemainingSeconds > 0f);
    }

    [Fact]
    public void ScenarioRunsStayDeterministicWithKeyedIdentity()
    {
        ScenarioRunResult a = ScenarioRunner.Run(ScenarioLibrary.TouchlinePress(), 200);
        ScenarioRunResult b = ScenarioRunner.Run(ScenarioLibrary.TouchlinePress(), 200);

        Assert.Equal(a.BallPosition, b.BallPosition);
        Assert.Equal(a.Possession, b.Possession);
        Assert.Equal(a.ContactAttempts.Count, b.ContactAttempts.Count);
    }

    /// <summary>
    /// Steps a real match and inspects every candidate the controller generated, rather than feeding it a
    /// synthetic observation — that is the only way to cover the on-ball and off-ball sets both.
    /// </summary>
    private static void AssertAllKeyed(MatchScenario scenario, int playerId)
    {
        var engine = new MatchSimulationEngine(scenario);
        var controller = Assert.IsType<TacticalUtilityAiController>(engine.GetController(playerId));

        int inspected = 0;
        for (int tick = 0; tick < 150; tick++)
        {
            engine.StepTick();

            foreach (UtilityCandidate candidate in controller.LastCandidates)
            {
                inspected++;
                Assert.False(candidate.Key.IsNone,
                    $"t{engine.CurrentTick} p{playerId} generated '{candidate.ActionName}' without a key");
                Assert.False(string.IsNullOrWhiteSpace(candidate.Identity));
            }
        }

        Assert.True(inspected > 0, $"player #{playerId} generated no candidates to inspect");
    }

    /// <summary>Minimal valid observation for exercising a standalone controller.</summary>
    private static FootballerObservation Observation(int id) => new()
    {
        SelfId = id,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CentralMidfielder,
        SelfPosition = new System.Numerics.Vector3(0f, 0f, 0f),
        SelfVelocity = System.Numerics.Vector3.Zero,
        SelfFacingXZ = new System.Numerics.Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = new System.Numerics.Vector3(0.4f, 0f, 0f),
        PerceivedBallVelocity = System.Numerics.Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = id,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = id,
        TacticalAnchorPosition = new System.Numerics.Vector3(0f, 0f, 0f),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 0f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        VisiblePlayers = Array.Empty<PerceivedEntity>(),
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = new PlayerTendencies(),
        SelfAttributes = new PlayerAttributes()
    };
}
