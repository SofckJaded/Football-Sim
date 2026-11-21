using System;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Scenarios;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A8.2 - the shallow search in a real match.
///
/// The unit tests prove the search's own properties; these prove it is wired into the one decision pipeline
/// and that it changes what the team actually plays: a controlled fixture in which A8.1's pressure heuristic
/// has nothing to offer (nobody is pressed) but a continuation is genuinely worth promising, the exit
/// gate's clause - <b>the build with the search executes more useful continuations than the build with it
/// off</b> - and the switch's neutrality at the carrier's decision.
/// </summary>
public sealed class ShallowSearchScenarioTests
{
    private const int CarrierId = 108;
    private const int ReceiverId = 109;
    private const int OutletId = 110;

    // ---------------------------------------------------------------------------------------------
    // The exit gate: search on executes continuations the heuristic build cannot find.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheSearch_ExecutesMoreContinuations_ThanTheHeuristicBuild()
    {
        int onContinued = CountContinuations(search: true, out int onPublished, out bool onFullTime,
            out string onStory);
        int offContinued = CountContinuations(search: false, out int offPublished, out bool offFullTime,
            out string offStory);

        Assert.True(onFullTime && offFullTime, "the fixtures did not reach full time");
        Assert.True(onPublished > onContinued,
            $"the search build's contracts are not being played at all; {onStory}");
        Assert.True(onPublished > offPublished,
            $"the search build created no extra contract in a fixture with a clear continuation; {onStory}");
        Assert.True(onContinued > offContinued,
            $"search on {onContinued} continuations ({onStory}), search off {offContinued} ({offStory})");
    }

    // ---------------------------------------------------------------------------------------------
    // The switch at the carrier's own decision: the searched promise is a preference on the pass, and
    // with the search off that preference is exactly the A8.1 one (zero here - the heuristic's pressure
    // gate refuses an unpressed carrier, which is the gap the search exists to fill).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheSearchedPromise_IsAPreferenceOnThePass_AndTheSwitchOffAddsNone()
    {
        float onPreference = PassPreferenceToMate(new AiRuntimeConfig
        {
            EnableCombinations = true,
            EnableShallowSearch = true,
            // A8.3's switch recognition is a third, independent preference on the same pass. It is off here so
            // the A/B is the search alone; A8.3 has its own gate in SwitchScenarioTests.
            EnableSwitchRecognition = false
        });
        float offPreference = PassPreferenceToMate(new AiRuntimeConfig
        {
            EnableCombinations = true,
            EnableShallowSearch = false,
            EnableSwitchRecognition = false
        });
        float noCombinations = PassPreferenceToMate(new AiRuntimeConfig
        {
            EnableCombinations = false,
            EnableSwitchRecognition = false
        });

        Assert.True(offPreference > 0f, "the pass must exist in every build, or nothing is being compared");
        Assert.Equal(offPreference, noCombinations, 4);
        Assert.True(onPreference > offPreference,
            $"the search adds no preference to the pass it chose ({onPreference} vs {offPreference})");
        Assert.True(onPreference - offPreference <= CandidateGenerator.CombinationInitiationBonus + 1e-4f,
            "the searched promise must stay inside A8.1's bounded initiation preference");
    }

    // ---------------------------------------------------------------------------------------------
    // Determinism through the engine's own observation build.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TwoIdenticalSearchMatches_ProduceTheSameContractsTickForTick()
    {
        var left = new MatchSimulationEngine(SearchScenario(search: true));
        var right = new MatchSimulationEngine(SearchScenario(search: true));

        for (int tick = 1; tick <= 360; tick++)
        {
            left.StepTick();
            right.StepTick();

            Assert.Equal(left.CurrentTick, right.CurrentTick);
            Assert.Equal(left.Communication.Combinations.PublishedCount,
                right.Communication.Combinations.PublishedCount);
            Assert.Equal(left.Communication.Combinations.ContinuedCount,
                right.Communication.Combinations.ContinuedCount);
            Assert.Equal(left.Ball.Position, right.Ball.Position);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Fixture and helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A home possession under pressure with a continuation worth promising that A8.1's heuristic cannot
    /// make: the carrier is pressed, and a defender stands on the <b>deep</b> return line (the only region
    /// A8.1 offers), so the heuristic's one-two has no lane there - while the short bounce the search examines
    /// (A8.2's second return region) is open. The away side is scripted to hold where it stands, so the
    /// fixture's pressure and its blocking defender are part of the fixture rather than of the opponent's
    /// mood, and the difference between the two builds is the search and nothing else.
    /// </summary>
    private static MatchScenario SearchScenario(bool search)
    {
        IFootballerController hold = new FixedCommandController(ScenarioCommands.Hold(), "hold");
        return new MatchScenario
        {
            Name = search ? "search-on" : "search-off",
            Seed = 4822UL,
            Ai = new AiRuntimeConfig
            {
                EnableCombinations = true,
                EnableShallowSearch = search,
                // The A8.2 gate measures the search; A8.3's switch recognition is a separate milestone with its
                // own gate and is held off so both columns differ by the search and nothing else.
                EnableSwitchRecognition = false
            },
            HalfDurationSeconds = 45f,
            BallPosition = new Vector3(-5.6f, 0.11f, 0f),
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = CarrierId,
            BallLastTouchedByTeam = TeamId.Home
        }
        .Add(MatchScenario.Player(101, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-45f, 0f, 0f), 1))
        .Add(MatchScenario.Player(CarrierId, TeamId.Home, TacticalRole.CentralMidfielder,
            new Vector3(-6f, 0f, 0f), 8))
        .Add(MatchScenario.Player(ReceiverId, TeamId.Home, TacticalRole.Striker, new Vector3(8f, 0f, 1f), 9))
        .Add(MatchScenario.Player(OutletId, TeamId.Home, TacticalRole.CenterBack,
            new Vector3(-24f, 0f, -12f), 4))
        .Add(MatchScenario.Player(201, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(45f, 0f, 0f), 1)
            .WithController(hold))
        .Add(MatchScenario.Player(205, TeamId.Away, TacticalRole.DefensiveMidfielder,
                new Vector3(-8.5f, 0f, 0.5f), 6)
            .WithController(hold))
        // Standing on the deep return line (receiver toward the carrier's forward run), not on the short one.
        .Add(MatchScenario.Player(206, TeamId.Away, TacticalRole.CenterBack, new Vector3(5f, 0f, 1.8f), 5)
            .WithController(hold))
        .Add(MatchScenario.Player(207, TeamId.Away, TacticalRole.CenterBack, new Vector3(20f, 0f, 10f), 4)
            .WithController(hold));
    }

    private static int CountContinuations(
        bool search,
        out int published,
        out bool reachedFullTime,
        out string story)
    {
        var engine = new MatchSimulationEngine(SearchScenario(search));
        int maxTicks = (int)(45f * 2.25f * 60f) + 1200;
        while (engine.Rules.Phase != MatchPhase.FullTime && engine.CurrentTick < maxTicks)
            engine.StepTick();

        CombinationCoordinator contracts = engine.Communication.Combinations;
        published = contracts.PublishedCount;
        reachedFullTime = engine.Rules.Phase == MatchPhase.FullTime;

        var parts = new System.Text.StringBuilder();
        parts.Append("endings: ");
        for (int reason = 1; reason < 7; reason++)
        {
            int count = contracts.CountOfEnd((CombinationEndReason)reason);
            if (count > 0) parts.Append((CombinationEndReason)reason).Append('=').Append(count).Append(' ');
        }

        parts.Append("| published ");
        for (int kind = 1; kind < 3; kind++)
            parts.Append((CombinationKind)kind).Append('=').Append(contracts.CountOfPublished((CombinationKind)kind))
                .Append(' ');

        story = parts.ToString();
        return contracts.ContinuedCount;
    }

    /// <summary>
    /// Runs one unpressed carrier decision and returns the preference the feet pass to the mate carried.
    /// The generator is the only thing under test here, so the observation is built directly.
    /// </summary>
    private static float PassPreferenceToMate(AiRuntimeConfig ai)
    {
        var pitch = new PitchDimensions();
        var generator = new CandidateGenerator(pitch, communication: null, ai) { TimeOnBall = 5f };
        var observation = new FootballerObservation
        {
            SelfId = CarrierId,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = Vector3.Zero,
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(0.4f, 0f, 0f),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = CarrierId,
            BallLastTouchedByTeam = TeamId.Home,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = CarrierId,
            TacticalAnchorPosition = Vector3.Zero,
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 10f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = 100,
            VisiblePlayers = new[]
            {
                new PerceivedEntity(ReceiverId, TeamId.Home, TacticalRole.Striker,
                    new Vector3(9f, 0f, 4f), Vector3.Zero, 1f)
            },
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = new PlayerTendencies(),
            SelfAttributes = new PlayerAttributes()
        };

        generator.GenerateOnBallCandidates(observation, new Vector3(52.5f, 0f, 0f), offsideLineProjX: 30f);

        for (int i = 0; i < generator.CandidateCount; i++)
        {
            UtilityCandidate candidate = generator.Candidates[i];
            if (candidate.Key.Kind != CandidateKind.Pass) continue;
            if (candidate.Key.TargetPlayerId != ReceiverId) continue;
            if (!string.Equals(candidate.Key.TargetTag, "ToFeet", StringComparison.Ordinal)) continue;
            return candidate.Terms.Preference;
        }

        throw new Xunit.Sdk.XunitException($"no feet pass to #{ReceiverId} was generated");
    }
}
