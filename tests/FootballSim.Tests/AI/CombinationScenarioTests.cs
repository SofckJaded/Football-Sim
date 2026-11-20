using System;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Scenarios;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A8.1 — combinations in a real match.
///
/// The unit tests prove the board, the proposal and the two consumers; these prove the pieces are wired
/// through the engine's own observation build and lifecycle: a contract reaches a team-mate's observation
/// only after the communication delay, ends with the possession it was made in, is deterministic, and the
/// exit-gate clause — a one-two in a live fixture produces a continuation with the mechanism on and none with
/// it off.
/// </summary>
public sealed class CombinationScenarioTests
{
    private const int CarrierId = 108;
    private const int ReceiverId = 109;
    private const int ThirdRunnerId = 110;

    // ---------------------------------------------------------------------------------------------
    // The exit gate: a one-two produces a useful continuation, search-disabled produces none.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AOneTwoScenario_ProducesAContinuation_WhileCombinationsOffDoesNot()
    {
        int onContinuations = CountContinuations(combinations: true, out int onPublished, out bool onFt,
            out string onStory);
        int offContinuations = CountContinuations(combinations: false, out int offPublished, out bool offFt,
            out _);

        Assert.True(onFt && offFt, "the fixtures did not reach full time");
        Assert.True(onPublished > 0, "the fixture never created a one-two contract");
        Assert.True(onContinuations > 0,
            $"the one-two produced no continuation (published {onPublished}); {onStory}");
        Assert.Equal(0, offPublished);
        Assert.Equal(0, offContinuations);
    }

    // ---------------------------------------------------------------------------------------------
    // The engine's observation wiring and the possession lifecycle.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AContract_ReachesTheOtherParticipantsObservation_OnlyAfterTheDelay()
    {
        MatchScenario scenario = OneTwoScenario(new AiRuntimeConfig { EnableCombinations = true });
        var engine = new MatchSimulationEngine(scenario);
        engine.StepTick();

        int tick = engine.CurrentTick;
        Assert.True(engine.Communication.Combinations.Publish(
            CombinationKind.OneTwo, TeamId.Home, CarrierId, ReceiverId, thirdPlayerId: 0,
            returnXZ: new Vector2(0f, 4f), returnRadiusMeters: 3f,
            expectedArrivalTick: tick + 120, confidence01: 0.8f, tick: tick));

        // The initiator's own commitment is visible at once; the receiver's is not, until the delay.
        while (engine.CurrentTick < tick + CombinationCoordinator.CommunicationDelayTicks - 1)
            engine.StepTick();
        FootballerObservation lateInitiator = Observation(engine, CarrierId);
        FootballerObservation lateReceiver = Observation(engine, ReceiverId);
        Assert.True(lateInitiator.Combination.IsActive);
        Assert.Equal(CombinationRole.Initiator, lateInitiator.Combination.Role);
        Assert.False(lateReceiver.Combination.IsActive);

        engine.StepTick();
        FootballerObservation receiver = Observation(engine, ReceiverId);
        Assert.True(receiver.Combination.IsActive);
        Assert.Equal(CombinationRole.Receiver, receiver.Combination.Role);
        Assert.Equal(CarrierId, receiver.Combination.InitiatorId);
        Assert.Equal(new Vector2(0f, 4f), receiver.Combination.ReturnXZ);
    }

    [Fact]
    public void AContract_EndsWhenTheOtherTeamHasTheBall()
    {
        // The fixture's away side has the ball, so a home contract cannot survive even one live tick.
        MatchScenario scenario = OneTwoScenario(new AiRuntimeConfig { EnableCombinations = true }, awayHasBall: true);
        var engine = new MatchSimulationEngine(scenario);
        engine.StepTick();

        int tick = engine.CurrentTick;
        Assert.True(engine.Communication.Combinations.Publish(
            CombinationKind.OneTwo, TeamId.Home, CarrierId, ReceiverId, thirdPlayerId: 0,
            returnXZ: new Vector2(0f, 4f), returnRadiusMeters: 3f,
            expectedArrivalTick: tick + 120, confidence01: 0.8f, tick: tick));

        engine.StepTick();
        Assert.Equal(1, engine.Communication.Combinations.CountOfEnd(CombinationEndReason.PossessionEnded));
        Assert.Equal(0, engine.Communication.Combinations.LiveCount(TeamId.Home, engine.CurrentTick));
    }

    [Fact]
    public void TwoIdenticalMatches_ProduceTheSameContractsTickForTick()
    {
        var left = new MatchSimulationEngine(OneTwoScenario(new AiRuntimeConfig { EnableCombinations = true }));
        var right = new MatchSimulationEngine(OneTwoScenario(new AiRuntimeConfig { EnableCombinations = true }));

        for (int tick = 1; tick <= 240; tick++)
        {
            left.StepTick();
            right.StepTick();

            Assert.Equal(left.CurrentTick, right.CurrentTick);
            Assert.Equal(left.Communication.Combinations.PublishedCount,
                right.Communication.Combinations.PublishedCount);
            Assert.Equal(left.Communication.Combinations.ContinuedCount,
                right.Communication.Combinations.ContinuedCount);
            Assert.Equal(DescribeLive(left), DescribeLive(right));
            Assert.Equal(left.Ball.Position, right.Ball.Position);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Fixture and helpers.
    // ---------------------------------------------------------------------------------------------


    /// <summary>
    /// A home carrier under pressure with a returnable partner: the carrier is pressed, the partner is
    /// pressed too, the return region ahead of the carrier is in space and the partner-to-region lane is open.
    /// The away side never has the ball, so the fixture stays a live home possession.
    /// </summary>
    private static MatchScenario OneTwoScenario(AiRuntimeConfig ai, bool awayHasBall = false) => new MatchScenario
    {
        Name = "one-two",
        Seed = 4811UL,
        Ai = ai,
        HalfDurationSeconds = 60f,
        BallPosition = awayHasBall ? new Vector3(-11.8f, 0.11f, 0.5f) : new Vector3(-9.6f, 0.11f, 0f),
        BallSetup = ScenarioBallSetup.Controlled,
        BallOwnerPlayerId = awayHasBall ? 205 : CarrierId,
        BallLastTouchedByTeam = awayHasBall ? TeamId.Away : TeamId.Home
    }
    .Add(MatchScenario.Player(101, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-45f, 0f, 0f), 1))
    .Add(MatchScenario.Player(CarrierId, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(-10f, 0f, 0f), 8))
    .Add(MatchScenario.Player(ReceiverId, TeamId.Home, TacticalRole.Striker, new Vector3(2f, 0f, 6f), 9))
    .Add(MatchScenario.Player(ThirdRunnerId, TeamId.Home, TacticalRole.CenterBack,
        new Vector3(-20f, 0f, -12f), 4))
    .Add(MatchScenario.Player(201, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(45f, 0f, 0f), 1))
    .Add(MatchScenario.Player(205, TeamId.Away, TacticalRole.DefensiveMidfielder,
        new Vector3(-12f, 0f, 0.5f), 6))
    .Add(MatchScenario.Player(206, TeamId.Away, TacticalRole.CenterBack, new Vector3(3.5f, 0f, 8.5f), 5))
    .Add(MatchScenario.Player(207, TeamId.Away, TacticalRole.CenterBack, new Vector3(18f, 0f, -8f), 4));

    private static int CountContinuations(
        bool combinations,
        out int published,
        out bool reachedFullTime,
        out string story)
    {
        var engine = new MatchSimulationEngine(OneTwoScenario(new AiRuntimeConfig
        {
            EnableCombinations = combinations
        }));
        int maxTicks = (int)(60f * 2.25f * 60f) + 1200;
        while (engine.Rules.Phase != MatchPhase.FullTime && engine.CurrentTick < maxTicks)
            engine.StepTick();

        CombinationCoordinator contracts = engine.Communication.Combinations;
        published = contracts.PublishedCount;
        reachedFullTime = engine.Rules.Phase == MatchPhase.FullTime;
        var parts = new System.Text.StringBuilder();
        for (int reason = 1; reason < 7; reason++)
        {
            int count = contracts.CountOfEnd((CombinationEndReason)reason);
            if (count > 0) parts.Append((CombinationEndReason)reason).Append('=').Append(count).Append(' ');
        }

        story = $"endings: {parts}";
        return contracts.ContinuedCount;
    }

    private static FootballerObservation Observation(MatchSimulationEngine engine, int playerId) =>
        engine.GetLastObservation(playerId) ??
        throw new Xunit.Sdk.XunitException($"no observation for #{playerId}");

    private static string DescribeLive(MatchSimulationEngine engine)
    {
        var text = new System.Text.StringBuilder();
        for (int teamIndex = 0; teamIndex < 2; teamIndex++)
        {
            TeamId team = teamIndex == 0 ? TeamId.Home : TeamId.Away;
            int count = engine.Communication.Combinations.LiveCount(team, engine.CurrentTick);
            text.Append(team).Append(':').Append(count).Append('|');
            for (int i = 0; i < count; i++)
            {
                if (engine.Communication.Combinations.TryGetLiveAt(team, i, engine.CurrentTick,
                        out CombinationContract contract))
                {
                    text.Append(contract.Describe()).Append(';');
                }
            }
        }

        return text.ToString();
    }
}
