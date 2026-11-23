using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Scenarios;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A8.3 — the switch / overload-to-isolate recognition wired into the one decision pipeline.
///
/// The unit tests prove the recognition's own properties; these prove the carrier actually plays the switch and
/// that the switch is a <b>preference on a pass that already exists</b>, not a new action: with the recognition
/// off the same picture offers the same pass at its ordinary price, and with it on that one pass is worth more
/// by exactly the recognized preference. The engine A/B then shows the consequence: a pressed carrier's first
/// ball is the far-side switch with the recognition on and is not without it, while every other player stands
/// still so the difference is the recognition and nothing else.
/// </summary>
public sealed class SwitchScenarioTests
{
    private const int CarrierId = 108;
    private const int NearMateId = 107;
    private const int FarWingerId = 109;

    /// <summary>
    /// The carrier's tendency profile. The role preset, with one difference: the dribble preference is the
    /// winger-level 0.85 instead of the central midfielder's 0.52. AIB-12a (audit 2026-11-23, finding 5)
    /// removed 0.15 of early-carry value, and this fixture's discriminating margin was 0.04 (carry 1.00
    /// against the far-side pass 0.96), so the neutral carrier now passes without the recognition and the
    /// A8.3 engine A/B this scenario exists for would no longer separate. Keeping the carrier's other
    /// tendencies at the role preset means the recognition's own preference is still the only difference
    /// between the on/off builds.
    /// </summary>
    private static readonly PlayerTendencies CarrierTendencies = new()
    {
        ShootingAppetite01 = 0.55f,
        DirectPassPreference01 = 0.62f,
        RiskTolerance01 = 0.60f,
        DribblePreference01 = 0.85f,
        PositionalDiscipline01 = 0.72f,
        PressingAggression01 = 0.62f
    };

    // ---------------------------------------------------------------------------------------------
    // The preference on the existing pass (the switch is a choice, not a capability).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheRecognition_IsAPreferenceOnTheExistingPass_AndTheSwitchOffAddsNone()
    {
        FootballerObservation picture = CarrierPicture();
        (float on, float off, float recognitionPreference) = FarSidePassPreference(picture);

        Assert.True(off > 0f, "the far-side pass must exist in both builds or nothing is being compared");
        Assert.True(recognitionPreference > 0f, "the picture must be a recognized switch opportunity");
        Assert.Equal(off + recognitionPreference, on, precision: 4);
        Assert.True(on - off <= SwitchRecognition.MaxPreference + 1e-4f,
            $"the recognition must stay inside its documented bound: {on - off}");
    }

    [Fact]
    public void TheRecognition_AddsAPreferenceOnlyToTheFarSideTarget()
    {
        FootballerObservation picture = CarrierPicture();
        (float on, float off, _) = FarSidePassPreference(picture);

        // The near-side team-mate is the ordinary alternative: A8.3 must not touch his ballot.
        Assert.Equal(PreferenceTo(picture, NearMateId, recognize: true),
            PreferenceTo(picture, NearMateId, recognize: false), precision: 4);
        Assert.True(on > off);
    }

    [Fact]
    public void ACoveredFarSide_RecognizesNothing()
    {
        // The far-side receiver has a defender on him (1.1 m): there is no room to switch into, and the pass is
        // not offered at all. The recognition must not manufacture a preference for it.
        FootballerObservation picture = CarrierPicture(markerX: 10.5f, markerZ: 20.5f);

        Assert.Empty(FeetPassesTo(picture, FarWingerId, recognize: true));
        Assert.Empty(FeetPassesTo(picture, FarWingerId, recognize: false));
        Assert.Equal(0, SwitchPreferences(picture));
    }

    [Fact]
    public void AnUnpressedCarrier_RecognizesNothing()
    {
        // The only difference from the recognized picture: the presser stands twelve metres away, so there is
        // no overload to release. The far-side pass still exists at its ordinary price.
        FootballerObservation picture = CarrierPicture(presserX: 12f);

        Assert.True(FeetPassesTo(picture, FarWingerId, recognize: false).Count > 0,
            "the far-side pass must still be offered: the recognition is a preference, not the action");
        Assert.Equal(0, SwitchPreferences(picture));
        Assert.Equal(PreferenceTo(picture, FarWingerId, recognize: false),
            PreferenceTo(picture, FarWingerId, recognize: true), precision: 4);
    }

    [Fact]
    public void AShortRestDefence_TakesTheSwitchAway()
    {
        FootballerObservation picture = CarrierPicture(security01: 0.30f);
        (float on, float off, float recognitionPreference) = FarSidePassPreference(picture);

        Assert.Equal(0f, recognitionPreference, precision: 4);
        Assert.Equal(off, on, precision: 4);
    }

    [Fact]
    public void TheRecognitionRescuesTheFarSideTargetFromThePassTargetBudget()
    {
        // The near-side team-mate is the better-placed receiver on the cheap pre-rank, so a budget of one keeps
        // him and prunes the far side — the switch would never be priced. The recognition's geometry rescue is
        // what puts the far-side target inside the budget, and it is gated by the same configuration switch.
        FootballerObservation picture = CarrierPicture(nearMateX: 10f, nearMateZ: 6f);

        var without = new CandidateGenerator(new PitchDimensions(), communication: null,
            new AiRuntimeConfig { MaxPassTargets = 1, EnableSwitchRecognition = false }) { TimeOnBall = 5f };
        without.GenerateOnBallCandidates(picture, new Vector3(52.5f, 0f, 0f), offsideLineProjX: 35f);
        Assert.True(HasPassTo(without, NearMateId), Describe(without));
        Assert.False(HasPassTo(without, FarWingerId),
            "the budget of one must exclude the far side in the pre-A8.3 build: " + Describe(without));

        var with = new CandidateGenerator(new PitchDimensions(), communication: null,
            new AiRuntimeConfig { MaxPassTargets = 1 }) { TimeOnBall = 5f };
        with.GenerateOnBallCandidates(picture, new Vector3(52.5f, 0f, 0f), offsideLineProjX: 35f);
        Assert.True(HasPassTo(with, FarWingerId),
            "the recognition must rescue the far-side target from the budget: " + Describe(with));
    }

    // ---------------------------------------------------------------------------------------------
    // The engine consequence.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheCarrierTakesTheSwitchAtTheFirstDecision_WithTheRecognition()
    {
        FirstDecision on = FirstCarrierDecision(recognize: true, coveredFarSide: false);

        Assert.True(on.HasDecision, on.Story);
        Assert.True(on.ChosenKind == CandidateKind.Pass && on.ChosenTargetId == FarWingerId,
            $"the recognition build's first decision must be the switch; {on.Story}");
        Assert.Equal(FarWingerId, on.FirstExecutedPassReceiver);
    }

    [Fact]
    public void TheCarrierDoesNotTakeTheSwitchAtTheSameDecision_WithoutTheRecognition()
    {
        FirstDecision on = FirstCarrierDecision(recognize: true, coveredFarSide: false);
        FirstDecision off = FirstCarrierDecision(recognize: false, coveredFarSide: false);

        Assert.True(on.ChosenKind == CandidateKind.Pass && on.ChosenTargetId == FarWingerId, on.Story);
        Assert.False(off.ChosenKind == CandidateKind.Pass && off.ChosenTargetId == FarWingerId,
            $"the pre-A8.3 build played the switch from the same picture; {off.Story}");
        Assert.True(off.HasDecision, off.Story);
    }

    [Fact]
    public void ACoveredFarSide_IsNeverPlayed()
    {
        FirstDecision on = FirstCarrierDecision(recognize: true, coveredFarSide: true);
        FirstDecision off = FirstCarrierDecision(recognize: false, coveredFarSide: true);

        Assert.Equal(0, on.FarSidePasses);
        Assert.Equal(0, off.FarSidePasses);
        Assert.False(on.ChosenKind == CandidateKind.Pass && on.ChosenTargetId == FarWingerId, on.Story);
        Assert.False(off.ChosenKind == CandidateKind.Pass && off.ChosenTargetId == FarWingerId, off.Story);
    }

    [Fact]
    public void TwoIdenticalRecognitionMatches_AreTheSameTickForTick()
    {
        var left = new MatchSimulationEngine(SwitchScenario(recognize: true));
        var right = new MatchSimulationEngine(SwitchScenario(recognize: true));

        for (int tick = 1; tick <= 240; tick++)
        {
            left.StepTick();
            right.StepTick();

            Assert.Equal(left.CurrentTick, right.CurrentTick);
            Assert.Equal(left.Ball.Position, right.Ball.Position);
            Assert.Equal(ExecutedPassesTo(left, FarWingerId), ExecutedPassesTo(right, FarWingerId));
        }

        Assert.True(ExecutedPassesTo(left, FarWingerId) > 0, "the fixture must actually play the switch");
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The carrier's picture for the direct-generator tests: a pressed home carrier at the origin with a
    /// far-side winger in a one-v-one, a near-side team-mate behind him and a secure rest defence. One picture,
    /// so an on/off comparison differs only in the configuration.
    /// </summary>
    private static FootballerObservation CarrierPicture(
        float markerX = 13f,
        float markerZ = 26f,
        float presserX = 2.2f,
        float security01 = 0.72f,
        float nearMateX = 2f,
        float nearMateZ = -10f) => new()
    {
        SelfId = CarrierId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CentralMidfielder,
        SelfPosition = new Vector3(0f, 0f, 0f),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = new Vector3(0.4f, 0.11f, 0f),
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
        VisiblePlayers = new List<PerceivedEntity>
        {
            // The far-side winger: far across the pitch, one marker 6.7 m away (a one-v-one).
            new(FarWingerId, TeamId.Home, TacticalRole.Winger, new Vector3(10f, 0f, 20f), Vector3.Zero, 0.95f)
            {
                Capability = CapabilityEstimate.Generic
            },
            // The near-side alternative: a real pass option, but behind the carrier.
            new(NearMateId, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(nearMateX, 0f, nearMateZ),
                Vector3.Zero, 0.95f)
            {
                Capability = CapabilityEstimate.Generic
            },
            // The press on the carrier (heavy enough to need a release).
            new(205, TeamId.Away, TacticalRole.DefensiveMidfielder, new Vector3(presserX, 0f, 0f),
                Vector3.Zero, 0.95f)
            {
                Capability = CapabilityEstimate.Generic
            },
            // The far-side marker: close enough to be a duel, far enough to leave real room.
            new(206, TeamId.Away, TacticalRole.CenterBack, new Vector3(markerX, 0f, markerZ),
                Vector3.Zero, 0.95f)
            {
                Capability = CapabilityEstimate.Generic
            }
        },
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.CentralMidfielder),
        SelfAttributes = new PlayerAttributes(),
        TeamSecurity = new RestDefence(4, 1.2f, 0.20f, 0.20f, 0.10f, security01)
    };

    /// <summary>Generates the carrier's action set for one config and reads the far-side pass's preference.</summary>
    private static (float On, float Off, float RecognitionPreference) FarSidePassPreference(
        FootballerObservation picture)
    {
        float on = PreferenceTo(picture, FarWingerId, recognize: true);
        float off = PreferenceTo(picture, FarWingerId, recognize: false);
        return (on, off, on - off);
    }

    private static float PreferenceTo(FootballerObservation picture, int receiverId, bool recognize)
    {
        List<UtilityCandidate> passes = FeetPassesTo(picture, receiverId, recognize);
        return passes[0].Terms.Preference;
    }

    private static int SwitchPreferences(FootballerObservation picture)
    {
        var generator = new CandidateGenerator(
            new PitchDimensions(),
            communication: null,
            new AiRuntimeConfig { EnableSwitchRecognition = true })
        {
            TimeOnBall = 5f
        };
        generator.GenerateOnBallCandidates(picture, new Vector3(52.5f, 0f, 0f), offsideLineProjX: 35f);
        return generator.SwitchPreferencesApplied;
    }

    private static List<UtilityCandidate> FeetPassesTo(
        FootballerObservation picture,
        int receiverId,
        bool recognize)
    {
        var generator = new CandidateGenerator(
            new PitchDimensions(),
            communication: null,
            new AiRuntimeConfig { EnableSwitchRecognition = recognize })
        {
            TimeOnBall = 5f
        };
        generator.GenerateOnBallCandidates(picture, new Vector3(52.5f, 0f, 0f), offsideLineProjX: 35f);

        var matches = new List<UtilityCandidate>();
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            UtilityCandidate candidate = generator.Candidates[i];
            if (candidate.Key.Kind != CandidateKind.Pass) continue;
            if (candidate.Key.TargetPlayerId != receiverId) continue;
            if (!string.Equals(candidate.Key.TargetTag, "ToFeet", StringComparison.Ordinal)) continue;
            matches.Add(candidate);
        }

        return matches;
    }

    /// <summary>True when the generator offered a pass of any variant to this receiver.</summary>
    private static bool HasPassTo(CandidateGenerator generator, int receiverId)
    {
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            UtilityCandidate candidate = generator.Candidates[i];
            if (candidate.Key.Kind == CandidateKind.Pass && candidate.Key.TargetPlayerId == receiverId) return true;
        }

        return false;
    }

    private static string Describe(CandidateGenerator generator)
    {
        var names = new List<string>();
        for (int i = 0; i < generator.CandidateCount; i++) names.Add(generator.Candidates[i].ActionName);
        return string.Join(" | ", names);
    }

    /// <summary>
    /// A small fixture that isolates the switch: home in possession under pressure, a far-side winger alone
    /// against one marker, a near-side alternative behind the carrier, four home players behind the ball (a real
    /// rest defence), and <b>every other player scripted to stand still</b> so the picture that the carrier reads
    /// is the fixture rather than an opponent's or a team-mate's mood.
    /// </summary>
    private static MatchScenario SwitchScenario(bool recognize, bool coveredFarSide = false)
    {
        IFootballerController hold = new FixedCommandController(ScenarioCommands.Hold(), "hold");
        MatchScenario scenario = new MatchScenario
        {
            Name = recognize ? "switch-recognition-on" : "switch-recognition-off",
            Seed = 4822UL,
            Ai = new AiRuntimeConfig { EnableSwitchRecognition = recognize },
            HalfDurationSeconds = 45f,
            BallPosition = new Vector3(0.4f, 0.11f, 0f),
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = CarrierId,
            BallLastTouchedByTeam = TeamId.Home
        }
            .Add(MatchScenario.Player(101, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-45f, 0f, 0f), 1)
                .WithController(hold))
            .Add(MatchScenario.Player(102, TeamId.Home, TacticalRole.CenterBack, new Vector3(-22f, 0f, -8f), 4)
                .WithController(hold))
            .Add(MatchScenario.Player(103, TeamId.Home, TacticalRole.CenterBack, new Vector3(-22f, 0f, 8f), 5)
                .WithController(hold))
            .Add(MatchScenario.Player(104, TeamId.Home, TacticalRole.DefensiveMidfielder,
                    new Vector3(-8f, 0f, -5f), 6)
                .WithController(hold))
            .Add(MatchScenario.Player(CarrierId, TeamId.Home, TacticalRole.CentralMidfielder,
                    new Vector3(0f, 0f, 0f), 8)
                .WithTendencies(CarrierTendencies))
            // A backward near-side outlet: playable, but not the release the pressed carrier needs.
            .Add(MatchScenario.Player(NearMateId, TeamId.Home, TacticalRole.CentralMidfielder,
                    new Vector3(2f, 0f, -10f), 7)
                .WithController(hold))
            // The far-side receiver: free, across the pitch, with a clear lane from the carrier.
            .Add(MatchScenario.Player(FarWingerId, TeamId.Home, TacticalRole.Winger,
                    new Vector3(4f, 0f, 22f), 11)
                .WithController(hold))
            // Away: the presser just off the switch lane, a marker on the near-side option, and bodies a long
            // way from the far side so the switch's lane and reception stay clean. Everyone stands still.
            .Add(MatchScenario.Player(201, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(45f, 0f, 0f), 1)
                .WithController(hold))
            .Add(MatchScenario.Player(205, TeamId.Away, TacticalRole.DefensiveMidfielder,
                    new Vector3(1.6f, 0f, -1.4f), 6)
                .WithController(hold))
            .Add(MatchScenario.Player(207, TeamId.Away, TacticalRole.CenterBack, new Vector3(6f, 0f, -3f), 4)
                .WithController(hold))
            .Add(MatchScenario.Player(209, TeamId.Away, TacticalRole.CenterBack, new Vector3(7.5f, 0f, 9.5f), 5)
                .WithController(hold))
            // A deep defender, so the far-side winger is onside (the offside line is the second-deepest
            // opponent) and far enough away that the lane and the reception stay clean.
            .Add(MatchScenario.Player(208, TeamId.Away, TacticalRole.CenterBack, new Vector3(24f, 0f, -6f), 3)
                .WithController(hold))
            .Add(MatchScenario.Player(210, TeamId.Away, TacticalRole.FullBack, new Vector3(-10f, 0f, -26f), 2)
                .WithController(hold));

        if (coveredFarSide)
        {
            // The covered variant: the same world with a defender standing on the far-side receiver. No other
            // fact changes, so the difference is the far side's availability and nothing else.
            scenario = scenario.Add(MatchScenario.Player(206, TeamId.Away, TacticalRole.CenterBack,
                    new Vector3(4f, 0f, 24.5f), 7)
                .WithController(hold));
        }

        return scenario;
    }

    /// <summary>One fixture run's first carrier decision, plus what was actually played afterwards.</summary>
    private readonly record struct FirstDecision(
        bool HasDecision,
        CandidateKind ChosenKind,
        int ChosenTargetId,
        int FirstExecutedPassReceiver,
        int FarSidePasses,
        string Story);

    private static FirstDecision FirstCarrierDecision(bool recognize, bool coveredFarSide)
    {
        var engine = new MatchSimulationEngine(SwitchScenario(recognize, coveredFarSide));
        int maxTicks = 240;
        bool decisionCaptured = false;
        CandidateKind chosenKind = CandidateKey.None.Kind;
        int chosenTargetId = 0;
        string diagnostics = "no carrier decision";
        int decisionTick = 0;

        while (engine.CurrentTick < maxTicks && engine.Rules.Phase != MatchPhase.FullTime)
        {
            engine.StepTick();
            if (decisionCaptured) continue;
            if (engine.GetController(CarrierId) is not TacticalUtilityAiController controller ||
                controller.LastCandidateCount == 0)
            {
                continue;
            }

            chosenKind = controller.LastSelected.Key.Kind;
            chosenTargetId = controller.LastSelected.Key.TargetPlayerId;
            decisionTick = engine.CurrentTick;
            var table = new System.Text.StringBuilder();
            for (int i = 0; i < controller.LastCandidates.Count; i++)
            {
                UtilityCandidate candidate = controller.LastCandidates[i];
                table.Append(candidate.ActionName).Append('=').Append(candidate.UtilityScore.ToString("F2"))
                    .Append(" [").Append(candidate.Terms.Describe()).Append("] ");
            }

            diagnostics = $"tick {decisionTick} security " +
                          $"{engine.TeamJobs.SecurityOf(TeamId.Home).Security01:F2}; chosen " +
                          $"{controller.LastSelected.ActionName}; recognized " +
                          $"{controller.SwitchPreferencesApplied} [{controller.LastSwitchOpportunity.Describe()}];" +
                          $" candidates: {table}";
            decisionCaptured = true;
        }

        int firstReceiver = -1;
        var log = new System.Text.StringBuilder();
        for (int i = 0; i < engine.ContactLog.Count; i++)
        {
            BallContactEvent contact = engine.ContactLog[i];
            if (contact.Kind != BallContactKind.Kick || !KickIntents.IsPass(contact.Intent)) continue;
            log.Append("t").Append(contact.Tick).Append(":#").Append(contact.PlayerId).Append("->#")
                .Append(contact.IntendedReceiverId).Append(' ');
            if (contact.PlayerId == CarrierId && firstReceiver < 0 &&
                contact.IntendedReceiverId is int receiver)
            {
                firstReceiver = receiver;
            }
        }

        return new FirstDecision(
            decisionCaptured,
            chosenKind,
            chosenTargetId,
            firstReceiver,
            ExecutedPassesTo(engine, FarWingerId),
            $"over {engine.CurrentTick} ticks; {diagnostics}; executed passes: {log}");
    }

    /// <summary>
    /// The executed pass contacts with one intended receiver, from the engine's own contact log: a decision that
    /// never reached execution is not a ball that was played.
    /// </summary>
    private static int ExecutedPassesTo(MatchSimulationEngine engine, int receiverId)
    {
        int count = 0;
        for (int i = 0; i < engine.ContactLog.Count; i++)
        {
            BallContactEvent contact = engine.ContactLog[i];
            if (contact.Kind != BallContactKind.Kick) continue;
            if (!KickIntents.IsPass(contact.Intent)) continue;
            if (contact.IntendedReceiverId == receiverId) count++;
        }

        return count;
    }
}
