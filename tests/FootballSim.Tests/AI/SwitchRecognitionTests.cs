using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A8.3 — the switch / overload-to-isolate recognition, as a pure function of the carrier's picture.
///
/// These tests build two observations that differ in exactly one fact and compare the recognized opportunity,
/// which is the direct form of the milestone's claims: the five named inputs each gate or move it, an unknown
/// input never buys a switch, and nothing about the clock or the passage forces one. The engine wiring is
/// <see cref="SwitchScenarioTests"/>.
/// </summary>
public sealed class SwitchRecognitionTests
{
    private const int CarrierId = 8;
    private const int ReceiverId = 9;
    private const int MarkerId = 205;
    private const int PresserId = 201;

    /// <summary>
    /// The canonical recognized picture: a pressed home carrier, a far-side home receiver in a one-v-one, a
    /// secure rest defence and a ball the pass model says is 80 % likely to arrive.
    /// </summary>
    private static FootballerObservation Recognized(
        IReadOnlyList<PerceivedEntity>? visible = null,
        float security01 = 0.75f,
        int tick = 500,
        float matchTimeSeconds = 20f,
        TacticalRole selfRole = TacticalRole.CentralMidfielder) =>
        Observation(
            visible ?? DefaultVisible(),
            security01,
            tick,
            matchTimeSeconds,
            selfRole);

    private static IReadOnlyList<PerceivedEntity> DefaultVisible() => new List<PerceivedEntity>
    {
        // The far-side receiver: 18 m across the pitch, a marker 5.6 m away (a one-v-one).
        Mate(ReceiverId, 6f, 18f),
        Opponent(MarkerId, 8f, 23.2f),
        // The press on the carrier: 2.5 m away.
        Opponent(PresserId, 2.5f, 0f)
    };

    // ---------------------------------------------------------------------------------------------
    // The recognized case and its five inputs.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void APressedCarrierWithAFreeFarSide_RecognizesTheSwitch()
    {
        SwitchOpportunity opportunity = Evaluate(Recognized());

        Assert.True(opportunity.IsRecognized, opportunity.Describe());
        Assert.Equal(ReceiverId, opportunity.ReceiverId);
        Assert.InRange(opportunity.Preference, 0.05f, SwitchRecognition.MaxPreference);
        Assert.InRange(opportunity.Confidence01, 0f, 1f);
        Assert.InRange(opportunity.LateralMeters, TeamCallPlanner.SwitchMinLateralMeters, 68f);
        Assert.True(opportunity.CarrierPressure01 > 0f, "the carrier is pressed in this fixture");
        Assert.Equal(0.80f, opportunity.Feasibility01, precision: 4);
    }

    [Fact]
    public void ACoveredFarSide_IsNotRecognized_HoweverPressedTheCarrierIs()
    {
        // The only difference: a second defender stands on the far-side receiver, so the far side has no room.
        var covered = new List<PerceivedEntity>
        {
            Mate(ReceiverId, 6f, 18f),
            Opponent(MarkerId, 7f, 19.5f),
            Opponent(PresserId, 2.5f, 0f)
        };

        SwitchOpportunity opportunity = Evaluate(Recognized(covered));

        Assert.False(opportunity.IsRecognized, opportunity.Describe());
        Assert.Equal(0f, opportunity.Preference);
        Assert.True(opportunity.FarSideOpenness01 < SwitchRecognition.MinFarSideOpenness01,
            $"the fixture must actually cover the far side: {opportunity.Describe()}");
    }

    [Fact]
    public void AnUnpressedCarrier_DoesNotRecognizeASwitch()
    {
        // The only difference: nobody is near the carrier, so there is no overload to escape.
        var unpressed = new List<PerceivedEntity>
        {
            Mate(ReceiverId, 6f, 18f),
            Opponent(MarkerId, 8f, 23.2f),
            Opponent(PresserId, 12f, 0f)
        };

        SwitchOpportunity opportunity = Evaluate(Recognized(unpressed));

        Assert.False(opportunity.IsRecognized, opportunity.Describe());
        Assert.True(opportunity.CarrierPressure01 < 0.9f, "the term still reports the true pressure");
    }

    [Fact]
    public void APassTheModelCannotComplete_IsNotASwitch()
    {
        // The only difference: the pass model prices the crossfield ball at 20 %, below the recognition floor.
        SwitchOpportunity opportunity = Evaluate(Recognized(), completionProbability01: 0.20f);

        Assert.False(opportunity.IsRecognized, opportunity.Describe());
        Assert.Equal(0.20f, opportunity.Feasibility01, precision: 4);
    }

    [Fact]
    public void AnUnknownRestDefence_DoesNotBuyASwitch()
    {
        // TeamSecurity's default is "no picture" (Security01 = 0). A1.5/A3.6's rule is that "cannot say" must
        // not move a mechanism; for a switch it must not read as "safe to risk the ball".
        SwitchOpportunity opportunity = Evaluate(Recognized(security01: 0f));

        Assert.False(opportunity.IsRecognized, opportunity.Describe());
        Assert.Equal(0f, opportunity.TurnoverSafety01, precision: 4);
    }

    [Fact]
    public void AShortRestDefence_RefusesTheSwitch_AndASecureOneAllowsItAndPaysMore()
    {
        // Below the allocator's forward-commitment threshold the switch is not recognized at all.
        SwitchOpportunity shortRestDefence = Evaluate(Recognized(security01: 0.40f));
        Assert.False(shortRestDefence.IsRecognized, shortRestDefence.Describe());

        // Above it, the better the rest defence the more the switch is worth.
        SwitchOpportunity marginal = Evaluate(Recognized(security01: 0.55f));
        SwitchOpportunity secure = Evaluate(Recognized(security01: 0.95f));
        Assert.True(marginal.IsRecognized && secure.IsRecognized,
            $"{marginal.Describe()} | {secure.Describe()}");
        Assert.True(secure.TurnoverSafety01 > marginal.TurnoverSafety01);
        Assert.True(secure.Preference > marginal.Preference,
            $"a secure rest defence must pay more: {secure.Describe()} vs {marginal.Describe()}");
    }

    // ---------------------------------------------------------------------------------------------
    // The receiver matchup.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AnIsolatedOneOnOne_BeatsADoubledUpReceiver()
    {
        // Same receiver position and same nearest marker distance (5.6 m); the doubled fixture adds cover.
        var isolated = new List<PerceivedEntity>
        {
            Mate(ReceiverId, 6f, 18f),
            Opponent(MarkerId, 8f, 23.2f),
            Opponent(PresserId, 2.5f, 0f)
        };
        var doubled = new List<PerceivedEntity>
        {
            Mate(ReceiverId, 6f, 18f),
            Opponent(MarkerId, 8f, 23.2f),
            Opponent(206, 8.8f, 22.9f),
            Opponent(PresserId, 2.5f, 0f)
        };

        SwitchOpportunity oneOnOne = Evaluate(Recognized(isolated));
        SwitchOpportunity covered = Evaluate(Recognized(doubled));

        Assert.True(oneOnOne.IsRecognized && covered.IsRecognized,
            $"{oneOnOne.Describe()} | {covered.Describe()}");
        Assert.True(oneOnOne.Matchup01 > covered.Matchup01,
            $"the one-v-one must be the better matchup: {oneOnOne.Describe()} vs {covered.Describe()}");
        Assert.True(oneOnOne.Preference > covered.Preference);
    }

    [Fact]
    public void AQuickReceiverAgainstASlowMarker_IsABetterSwitch_ThanTheReverse()
    {
        // Scouting knowledge only — the belief band, never the live attribute (A2.3).
        var quickReceiver = new List<PerceivedEntity>
        {
            Mate(ReceiverId, 6f, 18f, maxSprintSpeed: 10f),
            Opponent(MarkerId, 8f, 23.2f, maxSprintSpeed: 7f),
            Opponent(PresserId, 2.5f, 0f)
        };
        var quickMarker = new List<PerceivedEntity>
        {
            Mate(ReceiverId, 6f, 18f, maxSprintSpeed: 7f),
            Opponent(MarkerId, 8f, 23.2f, maxSprintSpeed: 10f),
            Opponent(PresserId, 2.5f, 0f)
        };

        SwitchOpportunity goodDuel = Evaluate(Recognized(quickReceiver));
        SwitchOpportunity badDuel = Evaluate(Recognized(quickMarker));

        Assert.True(goodDuel.IsRecognized && badDuel.IsRecognized,
            $"{goodDuel.Describe()} | {badDuel.Describe()}");
        Assert.True(goodDuel.Matchup01 > badDuel.Matchup01,
            $"{goodDuel.Describe()} vs {badDuel.Describe()}");
        Assert.True(goodDuel.Preference > badDuel.Preference);
    }

    // ---------------------------------------------------------------------------------------------
    // Monotonicity of the weighted terms and the bound.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AMorePressedCarrier_AndAMoreCompletableBall_AreBothWorthMore()
    {
        var tighter = new List<PerceivedEntity>
        {
            Mate(ReceiverId, 6f, 18f),
            Opponent(MarkerId, 8f, 23.2f),
            Opponent(PresserId, 1.0f, 0f)
        };
        var looser = new List<PerceivedEntity>
        {
            Mate(ReceiverId, 6f, 18f),
            Opponent(MarkerId, 8f, 23.2f),
            Opponent(PresserId, 4.0f, 0f)
        };

        SwitchOpportunity tight = Evaluate(Recognized(tighter));
        SwitchOpportunity loose = Evaluate(Recognized(looser));
        Assert.True(tight.Preference > loose.Preference,
            $"{tight.Describe()} vs {loose.Describe()}");

        SwitchOpportunity poorBall = Evaluate(Recognized(), completionProbability01: 0.45f);
        SwitchOpportunity goodBall = Evaluate(Recognized(), completionProbability01: 0.90f);
        Assert.True(goodBall.Preference > poorBall.Preference,
            $"{goodBall.Describe()} vs {poorBall.Describe()}");
    }

    [Fact]
    public void ThePreference_NeverExceedsItsDocumentedBound()
    {
        // A whole grid of pictures; nothing can make the nudge larger than MaxPreference.
        for (float pressureDistance = 0f; pressureDistance <= 5f; pressureDistance += 0.5f)
        {
            for (float completion = 0.35f; completion <= 1f; completion += 0.05f)
            {
                var visible = new List<PerceivedEntity>
                {
                    Mate(ReceiverId, 6f, 18f),
                    Opponent(MarkerId, 8f, 23.2f),
                    Opponent(PresserId, pressureDistance, 0f)
                };
                SwitchOpportunity opportunity = Evaluate(Recognized(visible), completion);
                Assert.InRange(opportunity.Preference, 0f, SwitchRecognition.MaxPreference);
                Assert.InRange(opportunity.Confidence01, 0f, 1f);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // No clock, no pass count, no keeper, and the cheap gate agrees with the price.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheRecognition_IsAPurePicture_NoClockOrPassageHistoryCanForceIt()
    {
        // A8.3's "no forced switch after N passes": the only difference between these two observations is the
        // tick and the match clock — there is no pass counter anywhere in the recognition to depend on.
        SwitchOpportunity early = Evaluate(Recognized(tick: 12, matchTimeSeconds: 3f));
        SwitchOpportunity late = Evaluate(Recognized(tick: 40_000, matchTimeSeconds: 2_400f));

        Assert.Equal(early, late);
        Assert.True(early.IsRecognized);
    }

    [Fact]
    public void TheRecognition_IsDeterministic()
    {
        FootballerObservation observation = Recognized();
        SwitchOpportunity first = Evaluate(observation);
        SwitchOpportunity second = Evaluate(observation);
        Assert.Equal(first, second);
    }

    [Fact]
    public void AGoalkeeper_DoesNotRecognizeSwitches()
    {
        // The keeper's distribution is the keeper model's decision (A6.5b); the recognizer does not re-price it.
        SwitchOpportunity opportunity = Evaluate(
            Recognized(selfRole: TacticalRole.Goalkeeper));

        Assert.False(opportunity.IsRecognized, opportunity.Describe());
    }

    [Fact]
    public void TheCheapGeometryGate_AgreesWithTheFullPrice_AndOnlyTheCompletionGateSeparatesThem()
    {
        Vector2 ballXZ = new(0.4f, 0f);
        FootballerObservation recognized = Recognized();
        PerceivedEntity receiver = Find(recognized, ReceiverId);

        Assert.True(SwitchRecognition.IsSwitchGeometry(
            recognized, new Vector2(receiver.EstimatedPosition.X, receiver.EstimatedPosition.Z), ballXZ));

        // The price refuses a ball the model cannot complete, which is the one input the cheap gate cannot see.
        Assert.False(Evaluate(recognized, completionProbability01: 0.10f).IsRecognized);

        // Every gate the price applies, the cheap gate applies: covered far side, unpressed carrier, no picture.
        var covered = new List<PerceivedEntity>
        {
            Mate(ReceiverId, 6f, 18f),
            Opponent(MarkerId, 7f, 19.5f),
            Opponent(PresserId, 2.5f, 0f)
        };
        FootballerObservation coveredObs = Recognized(covered);
        PerceivedEntity coveredReceiver = Find(coveredObs, ReceiverId);
        Assert.False(SwitchRecognition.IsSwitchGeometry(
            coveredObs,
            new Vector2(coveredReceiver.EstimatedPosition.X, coveredReceiver.EstimatedPosition.Z),
            new Vector2(coveredObs.PerceivedBallPosition.X, coveredObs.PerceivedBallPosition.Z)));

        FootballerObservation unknownSecurity = Recognized(security01: 0f);
        PerceivedEntity unknownReceiver = Find(unknownSecurity, ReceiverId);
        Assert.False(SwitchRecognition.IsSwitchGeometry(
            unknownSecurity,
            new Vector2(unknownReceiver.EstimatedPosition.X, unknownReceiver.EstimatedPosition.Z),
            new Vector2(unknownSecurity.PerceivedBallPosition.X, unknownSecurity.PerceivedBallPosition.Z)));

        // The near-side team-mate is not a switch however open he is.
        var nearSide = new List<PerceivedEntity>
        {
            Mate(ReceiverId, 16f, 4f),
            Opponent(MarkerId, 8f, 23.2f),
            Opponent(PresserId, 2.5f, 0f)
        };
        FootballerObservation nearSideObs = Recognized(nearSide);
        PerceivedEntity nearSideReceiver = Find(nearSideObs, ReceiverId);
        Assert.False(SwitchRecognition.IsSwitchGeometry(
            nearSideObs,
            new Vector2(nearSideReceiver.EstimatedPosition.X, nearSideReceiver.EstimatedPosition.Z),
            new Vector2(nearSideObs.PerceivedBallPosition.X, nearSideObs.PerceivedBallPosition.Z)));
    }

    [Fact]
    public void SwitchRecognition_CentralOneOnOneCarrier_DoesNotAwardWideSwitchPreference()
    {
        const int wideWingerId = 107;
        var visible = new List<PerceivedEntity>
        {
            Mate(wideWingerId, 24f, 26f),
            Opponent(PresserId, 19.5f, 1f),
            Opponent(202, 18.0f, 3f),
            new(201, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(50f, 0f, 0f), Vector3.Zero, 1f)
            {
                Capability = CapabilityEstimate.Generic
            }
        };

        static FootballerObservation CentralObs(IReadOnlyList<PerceivedEntity> entities) => new()
        {
            SelfId = CarrierId,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.Striker,
            SelfPosition = new Vector3(22f, 0f, 2f),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(22.4f, 0.11f, 2f),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = CarrierId,
            BallLastTouchedByTeam = TeamId.Home,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = CarrierId,
            TacticalAnchorPosition = new Vector3(22f, 0f, 2f),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 10f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = 100,
            VisiblePlayers = entities,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.Striker),
            SelfAttributes = new PlayerAttributes(),
            TeamSecurity = new RestDefence(5, 1.2f, 0.25f, 0.20f, 0.1f, 0.75f)
        };

        FootballerObservation oneOnOneObs = CentralObs(visible);
        PerceivedEntity winger = Find(oneOnOneObs, wideWingerId);

        SwitchOpportunity opportunity = SwitchRecognition.Evaluate(
            oneOnOneObs, winger, new Vector2(24f, 26f), completionProbability01: 0.85f);
        Assert.False(opportunity.IsRecognized, opportunity.Describe());
        Assert.Equal(0f, opportunity.Preference);

        // Verify CandidateGenerator drives toward goal rather than switching wide to the touchline.
        var pitch = new PitchDimensions();
        var generator = new CandidateGenerator(pitch) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(oneOnOneObs, new Vector3(pitch.HalfLength, 0f, 0f), 40f);
        UtilityCandidate? carry = null;
        UtilityCandidate? wideSwitch = null;
        foreach (UtilityCandidate candidate in generator.Candidates)
        {
            if (candidate.Key.Kind == CandidateKind.Carry) carry = candidate;
            if (candidate.Key.Kind == CandidateKind.Pass && candidate.Key.TargetPlayerId == wideWingerId)
                wideSwitch = candidate;
        }
        Assert.NotNull(carry);
        if (wideSwitch is not null)
        {
            Assert.True(carry.Value.UtilityScore > wideSwitch.Value.UtilityScore,
                $"1v1 carry ({carry.Value.UtilityScore:F3}) must beat wide switch ({wideSwitch.Value.UtilityScore:F3})");
        }

        // Control: placing one center-back ahead in the central corridor restores the switch recognition.
        var blockedVisible = new List<PerceivedEntity>(visible)
        {
            Opponent(205, 32f, 1f)
        };
        FootballerObservation blockedObs = CentralObs(blockedVisible);
        SwitchOpportunity blockedOpp = SwitchRecognition.Evaluate(
            blockedObs, winger, new Vector2(24f, 26f), completionProbability01: 0.85f);
        Assert.True(blockedOpp.IsRecognized, blockedOpp.Describe());
        Assert.True(blockedOpp.Preference > 0f);
    }

    // ---------------------------------------------------------------------------------------------
    // The picture builder.
    // ---------------------------------------------------------------------------------------------

    private static SwitchOpportunity Evaluate(
        FootballerObservation observation,
        float completionProbability01 = 0.80f)
    {
        PerceivedEntity receiver = Find(observation, ReceiverId);
        return SwitchRecognition.Evaluate(
            observation,
            receiver,
            new Vector2(receiver.EstimatedPosition.X, receiver.EstimatedPosition.Z),
            completionProbability01);
    }

    private static PerceivedEntity Find(FootballerObservation observation, int playerId)
    {
        for (int i = 0; i < observation.VisiblePlayers.Count; i++)
        {
            if (observation.VisiblePlayers[i].PlayerId == playerId) return observation.VisiblePlayers[i];
        }

        throw new Xunit.Sdk.XunitException($"#{playerId} is not in the picture");
    }

    /// <summary>
    /// The carrier's picture: home #8 in possession at the origin, facing upfield; every other entity is
    /// supplied by the caller. TeamSecurity is set explicitly (0.75 = secure) so the recognition's turnover-cost
    /// input is a stated fact rather than the default "no picture".
    /// </summary>
    private static FootballerObservation Observation(
        IReadOnlyList<PerceivedEntity> visible,
        float security01,
        int tick,
        float matchTimeSeconds,
        TacticalRole selfRole) => new()
    {
        SelfId = CarrierId,
        SelfTeam = TeamId.Home,
        SelfRole = selfRole,
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
        MatchTimeSeconds = matchTimeSeconds,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = tick,
        VisiblePlayers = visible,
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.CentralMidfielder),
        SelfAttributes = new PlayerAttributes(),
        TeamSecurity = new RestDefence(5, 1.2f, 0.25f, 0.20f, 0.1f, security01)
    };

    private static PerceivedEntity Mate(int id, float x, float z, float maxSprintSpeed = 8.4f) =>
        new(id, TeamId.Home, TacticalRole.Winger, new Vector3(x, 0f, z), Vector3.Zero, 1f)
        {
            Capability = new CapabilityEstimate(
                maxSprintSpeed, 6.8f, 9.5f, 0.75f, ScoutingReport.ScoutingConfidence01)
        };

    private static PerceivedEntity Opponent(int id, float x, float z, float maxSprintSpeed = 8.4f) =>
        new(id, TeamId.Away, TacticalRole.CenterBack, new Vector3(x, 0f, z), Vector3.Zero, 1f)
        {
            Capability = new CapabilityEstimate(
                maxSprintSpeed, 6.8f, 9.5f, 0.75f, ScoutingReport.ScoutingConfidence01)
        };
}
