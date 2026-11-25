using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A8.1 — bounded one-two and third-player contracts.
///
/// The milestone is a contract, so the tests are split the way the mechanism is:
///
/// <list type="bullet">
/// <item><b>The board.</b> A contract is heard by team-mates only after the communication delay (the
/// initiator hears his own decision at once), expires after the arrival it names, ends with the possession
/// it was made in, is per team, is bounded, and every participant can abandon it. A continuation is
/// attributed from the executed kick, never from proximity.</item>
/// <item><b>The carrier.</b> A pressed carrier with a returnable partner offers a one-two as a
/// <b>preference on the existing pass candidate</b> — no candidate is added — and only the pass that wins the
/// decision publishes the contract. A team-mate's advertised run can instead make the pass a third-player
/// contract. Neither moves a reach, a sight or a limit.</item>
/// <item><b>The receiver.</b> A live contract's continuation pass is aimed at the contract's region and time
/// (the promise the initiator made), earns the bounded continuation preference, and a blocked return aborts
/// the contract cleanly.</item>
/// <item><b>The initiator.</b> A one-two's initiator runs into the frozen return region; if the region stops
/// being a lane (offside, occupied, off the pitch) he abandons it and stops running.</item>
/// </list>
/// </summary>
public sealed class CombinationCoordinatorTests
{
    private static readonly PitchDimensions Pitch = new();

    private const int InitiatorId = 108;
    private const int ReceiverId = 109;
    private const int ThirdRunnerId = 110;
    private const int PresserId = 205;
    private const int SecondDefenderId = 206;
    private const int ReceiverMateId = 111;

    private const TeamId Home = TeamId.Home;
    private const TeamId Away = TeamId.Away;

    private static readonly Vector2 ReturnRegion = new(3f, 2.5f);

    // ---------------------------------------------------------------------------------------------
    // The board: delay, expiry, team, continuation, abandonment, bounds.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AContract_IsHeardByTeamMatesOnlyAfterTheCommunicationDelay()
    {
        var board = new CombinationCoordinator();
        Assert.True(PublishOneTwo(board, tick: 100, arrivalTick: 190));

        // The initiator knows his own decision immediately.
        Assert.True(board.TryGetContext(Home, InitiatorId, 100, out CombinationContext mine));
        Assert.Equal(CombinationRole.Initiator, mine.Role);
        Assert.Equal(CombinationKind.OneTwo, mine.Kind);

        // The receiver and the third man hear it only after the delay.
        int due = 100 + CombinationCoordinator.CommunicationDelayTicks;
        Assert.False(board.TryGetContext(Home, ReceiverId, due - 1, out _));
        Assert.True(board.TryGetContext(Home, ReceiverId, due, out CombinationContext heard));
        Assert.Equal(CombinationRole.Receiver, heard.Role);
        Assert.Equal(ReturnRegion, heard.ReturnXZ);
        Assert.Equal(190, heard.ExpectedArrivalTick);

        // The other team never hears it.
        Assert.False(board.TryGetContext(Away, ReceiverId, due, out _));
        Assert.Equal(0, board.LiveCount(Away, due));
        Assert.Equal(1, board.LiveCount(Home, due));
    }

    [Fact]
    public void AContract_ExpiresAfterItsArrivalGrace()
    {
        var board = new CombinationCoordinator();
        Assert.True(PublishOneTwo(board, tick: 100, arrivalTick: 190));

        int expires = 190 + CombinationCoordinator.ArrivalGraceTicks;
        Assert.True(board.TryGetInitiatorContract(Home, InitiatorId, expires, out _));
        Assert.False(board.TryGetInitiatorContract(Home, InitiatorId, expires + 1, out _));

        // The sweep counts the expiry once, when a later read observes it.
        Assert.Equal(1, board.CountOfEnd(CombinationEndReason.Expired));
        Assert.Equal(1, board.PublishedCount);
    }

    [Fact]
    public void AContract_EndsWithThePossessionItWasMadeIn()
    {
        var board = new CombinationCoordinator();
        Assert.True(PublishOneTwo(board, tick: 100, arrivalTick: 190));

        // Still the same team's ball: the contract stands.
        board.ObservePossession(Home, 120);
        Assert.Equal(1, board.LiveCount(Home, 120));
        Assert.Equal(0, board.CountOfEnd(CombinationEndReason.PossessionEnded));

        // A dead ball (null) is not a turnover: a restart is cleared explicitly by the engine instead.
        board.ObservePossession(null, 121);
        Assert.Equal(1, board.LiveCount(Home, 121));

        // The other team has it now: the contract must not outlive its possession.
        board.ObservePossession(Away, 122);
        Assert.Equal(0, board.LiveCount(Home, 122));
        Assert.Equal(1, board.CountOfEnd(CombinationEndReason.PossessionEnded));
        Assert.False(board.TryGetContext(Home, InitiatorId, 122, out _));
    }

    [Fact]
    public void OnlyAParticipant_MayAbandonHisLane_AndOnlyForAnAbandonReason()
    {
        var board = new CombinationCoordinator();
        Assert.True(PublishOneTwo(board, tick: 100, arrivalTick: 190));
        Assert.True(board.TryGetInitiatorContract(Home, InitiatorId, 100, out CombinationContract contract));

        // Somebody else's player, and an engine-forced reason, are both refused.
        Assert.False(board.Abandon(contract.ContractId, PresserId,
            CombinationEndReason.AbandonedByReceiver, 120));
        Assert.False(board.Abandon(contract.ContractId, ReceiverId,
            CombinationEndReason.PossessionEnded, 120));
        Assert.Equal(1, board.LiveCount(Home, 120));

        // The receiver may abandon it, and the ending is recorded with the reason.
        Assert.True(board.Abandon(contract.ContractId, ReceiverId,
            CombinationEndReason.AbandonedByReceiver, 120));
        Assert.Equal(0, board.LiveCount(Home, 120));
        Assert.Equal(1, board.CountOfEnd(CombinationEndReason.AbandonedByReceiver));

        // Idempotent: a second abandonment changes nothing.
        Assert.False(board.Abandon(contract.ContractId, ReceiverId,
            CombinationEndReason.AbandonedByReceiver, 121));
        Assert.Equal(1, board.CountOfEnd(CombinationEndReason.AbandonedByReceiver));
    }

    [Fact]
    public void AContinuation_IsTheExecutedKickToTheContractsTarget()
    {
        var board = new CombinationCoordinator();
        Assert.True(PublishOneTwo(board, tick: 100, arrivalTick: 190));

        // A kick by somebody other than the contract's receiver does not continue it.
        var foreign = new[]
        {
            Kick(tick: 150, team: Home, kicker: 111, target: InitiatorId)
        };
        Assert.Equal(0, board.ApplyContacts(foreign, 150));
        Assert.Equal(0, board.ContinuedCount);

        // The receiver's kick to the initiator, while the contract is live, is the continuation.
        var continuation = new[]
        {
            Kick(tick: 160, team: Home, kicker: ReceiverId, target: InitiatorId)
        };
        Assert.Equal(1, board.ApplyContacts(continuation, 160));
        Assert.Equal(1, board.ContinuedCount);
        Assert.Equal(1, board.CountOfContinued(CombinationKind.OneTwo));

        // The receiver's side of the contract is finished; the initiator keeps his until the ball arrives,
        // because the return run is what the ball is travelling to.
        Assert.False(board.TryGetContext(Home, ReceiverId, 161, out _));
        Assert.True(board.TryGetContext(Home, InitiatorId, 161, out CombinationContext initiator));
        Assert.True(initiator.IsReturnRunner);

        // The same kick cannot continue the contract twice.
        Assert.Equal(0, board.ApplyContacts(continuation, 162));
        Assert.Equal(1, board.ContinuedCount);
    }

    [Fact]
    public void TheSameProposal_IsSuppressed_ButADifferentOneReplacesIt()
    {
        var board = new CombinationCoordinator();
        Assert.True(PublishOneTwo(board, tick: 100, arrivalTick: 190));
        Assert.False(PublishOneTwo(board, tick: 101, arrivalTick: 190));
        Assert.Equal(1, board.PublishedCount);
        Assert.Equal(1, board.SuppressedCount);

        // The carrier changes his mind about where the ball is going: the old promise is replaced.
        Assert.True(board.Publish(CombinationKind.OneTwo, Home, InitiatorId, receiverId: 112, thirdPlayerId: 0,
            returnXZ: new Vector2(6f, -4f), returnRadiusMeters: 3f, expectedArrivalTick: 210,
            confidence01: 0.7f, tick: 102));
        Assert.Equal(2, board.PublishedCount);
        Assert.Equal(1, board.CountOfEnd(CombinationEndReason.Replaced));
        Assert.Equal(1, board.LiveCount(Home, 102));
        Assert.True(board.TryGetInitiatorContract(Home, InitiatorId, 102, out CombinationContract replacement));
        Assert.Equal(112, replacement.ReceiverId);
    }

    [Fact]
    public void AContractNeverOutlivesItsParticipants_AndTheBoardIsBounded()
    {
        var board = new CombinationCoordinator();
        Assert.True(PublishOneTwo(board, tick: 100, arrivalTick: 190));
        board.ReleasePlayer(ReceiverId, 110);
        Assert.Equal(0, board.LiveCount(Home, 110));
        Assert.Equal(1, board.CountOfEnd(CombinationEndReason.ParticipantRemoved));

        // Capacity is a hard bound: no producer can grow the table, and the eviction is deterministic.
        for (int i = 0; i < CombinationCoordinator.Capacity + 6; i++)
        {
            board.Publish(CombinationKind.OneTwo, Home, initiatorId: 1000 + i, receiverId: 2000 + i,
                thirdPlayerId: 0, returnXZ: new Vector2(i, 0f), returnRadiusMeters: 3f,
                expectedArrivalTick: 300 + i, confidence01: 0.5f, tick: 100);
        }

        Assert.True(board.LiveCount(Home, 100) <= CombinationCoordinator.Capacity);
        Assert.True(board.EvictedCount > 0);
        Assert.Equal(6, board.CountOfEnd(CombinationEndReason.Evicted));
    }

    [Fact]
    public void InvalidProposals_AreRefused()
    {
        var board = new CombinationCoordinator();

        // No receiver, no third man on a third-player contract, arrival in the past, arrival too far ahead.
        Assert.False(board.Publish(CombinationKind.None, Home, InitiatorId, ReceiverId, 0,
            ReturnRegion, 3f, 190, 0.8f, 100));
        Assert.False(board.Publish(CombinationKind.OneTwo, Home, InitiatorId, InitiatorId, 0,
            ReturnRegion, 3f, 190, 0.8f, 100));
        Assert.False(board.Publish(CombinationKind.ThirdPlayer, Home, InitiatorId, ReceiverId, 0,
            ReturnRegion, 3f, 190, 0.8f, 100));
        Assert.False(board.Publish(CombinationKind.OneTwo, Home, InitiatorId, ReceiverId, 0,
            ReturnRegion, 3f, 90, 0.8f, 100));
        Assert.False(board.Publish(CombinationKind.OneTwo, Home, InitiatorId, ReceiverId, 0,
            ReturnRegion, 3f, 100 + CombinationCoordinator.MaxArrivalLeadTicks + 1, 0.8f, 100));
        Assert.False(board.Publish(CombinationKind.OneTwo, Home, InitiatorId, ReceiverId, 0,
            new Vector2(float.NaN, 0f), 3f, 190, 0.8f, 100));

        Assert.Equal(0, board.PublishedCount);
    }

    // ---------------------------------------------------------------------------------------------
    // The carrier: the proposal is a preference on an existing pass, and only a winning pass publishes.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void APressedCarrier_OffersTheOneTwoAsAPreference_WithoutAddingACandidate()
    {
        FootballerObservation observation = CarrierObservation();
        var onComms = new MatchCommunicationContext();
        var offComms = new MatchCommunicationContext();
        var on = new CandidateGenerator(Pitch, onComms, new AiRuntimeConfig());
        var off = new CandidateGenerator(Pitch, offComms, new AiRuntimeConfig { EnableCombinations = false });
        on.TimeOnBall = 1f;
        off.TimeOnBall = 1f;

        on.GenerateOnBallCandidates(observation, OppGoal(), OffsideLine);
        off.GenerateOnBallCandidates(observation, OppGoal(), OffsideLine);

        // The candidate set is identical: a combination is a preference, never a new action or target.
        Assert.Equal(KeysOf(off), KeysOf(on));

        float onPass = PassScore(on, ReceiverId, "ToFeet");
        float offPass = PassScore(off, ReceiverId, "ToFeet");
        Assert.True(onPass > offPass,
            $"the one-two pass scored {onPass:F4}, the same pass with combinations off {offPass:F4}");
        Assert.True(onPass - offPass <= CandidateGenerator.CombinationInitiationBonus + 1e-4f,
            "the initiation preference must stay inside its stated bound");

        // Only the pass that wins the decision publishes the contract.
        Assert.Equal(0, onComms.Combinations.PublishedCount);
        on.NoteDecision(observation, new CandidateKey(CandidateKind.Pass, ReceiverId, "ToFeet"));

        Assert.Equal(1, onComms.Combinations.PublishedCount);
        Assert.True(onComms.Combinations.TryGetInitiatorContract(Home, InitiatorId, observation.Tick,
            out CombinationContract contract));
        Assert.Equal(CombinationKind.OneTwo, contract.Kind);
        Assert.Equal(ReceiverId, contract.ReceiverId);
        Assert.Equal(Home, contract.Team);
        Assert.Equal(ReturnRegion.X, contract.ReturnXZ.X, 1);
        Assert.Equal(ReturnRegion.Y, contract.ReturnXZ.Y, 1);
        Assert.InRange(contract.Confidence01, 0f, 1f);
        Assert.True(contract.ExpectedArrivalTick > observation.Tick);
        Assert.Equal(observation.Tick + CombinationCoordinator.CommunicationDelayTicks, contract.VisibleTick);

        // Re-deciding the same pass is a re-assertion, not a new contract: the delay does not restart.
        on.NoteDecision(observation, new CandidateKey(CandidateKind.Pass, ReceiverId, "ToFeet"));
        Assert.Equal(1, onComms.Combinations.PublishedCount);
        Assert.Equal(1, onComms.Combinations.SuppressedCount);
    }

    [Fact]
    public void AnAdvertisedRun_CanMakeThePassAThirdPlayerContract()
    {
        FootballerObservation observation = CarrierObservation(pressured: false);
        var comms = new MatchCommunicationContext();
        PublishAdvert(comms, ThirdRunnerId, meetXZ: new Vector2(20f, 4f), meetTick: 460, publishedTick: 270);
        Assert.Equal(1, comms.Runs.VisibleCount(Home, observation.Tick));

        var generator = new CandidateGenerator(Pitch, comms, new AiRuntimeConfig());
        generator.TimeOnBall = 1f;
        generator.GenerateOnBallCandidates(observation, OppGoal(), OffsideLine);

        generator.NoteDecision(observation, new CandidateKey(CandidateKind.Pass, ReceiverId, "ToFeet"));

        Assert.Equal(1, comms.Combinations.PublishedCount);
        Assert.True(comms.Combinations.TryGetInitiatorContract(Home, InitiatorId, observation.Tick,
            out CombinationContract contract));
        Assert.Equal(CombinationKind.ThirdPlayer, contract.Kind);
        Assert.Equal(ReceiverId, contract.ReceiverId);
        Assert.Equal(ThirdRunnerId, contract.ThirdPlayerId);
        Assert.Equal(ThirdRunnerId, contract.ContinuationTargetId);
        Assert.Equal(new Vector2(20f, 4f), contract.ReturnXZ);
    }

    // ---------------------------------------------------------------------------------------------
    // The receiver: the continuation is aimed at the contract's region and earns its preference.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheReceiversContinuation_IsTimedToTheContractRegion()
    {
        // The same world, two contracts differing only in the confidence the initiator put on the return.
        // The promised pass's preference is therefore PassPreference + continuation bonus x promise, and the
        // difference between the two decisions isolates the documented bonus exactly.
        var highComms = new MatchCommunicationContext();
        var lowComms = new MatchCommunicationContext();
        Assert.True(PublishOneTwo(highComms.Combinations, tick: 300, arrivalTick: 425, confidence01: 0.8f));
        Assert.True(PublishOneTwo(lowComms.Combinations, tick: 300, arrivalTick: 425, confidence01: 0.4f));
        FootballerObservation highObservation = ReceiverObservation(tick: 390,
            combination: ContextOf(highComms, ReceiverId, 390));
        FootballerObservation lowObservation = ReceiverObservation(tick: 390,
            combination: ContextOf(lowComms, ReceiverId, 390));

        var high = new CandidateGenerator(Pitch, highComms, new AiRuntimeConfig());
        var low = new CandidateGenerator(Pitch, lowComms, new AiRuntimeConfig());
        high.TimeOnBall = 1f;
        low.TimeOnBall = 1f;
        high.GenerateOnBallCandidates(highObservation, OppGoal(), OffsideLine);
        low.GenerateOnBallCandidates(lowObservation, OppGoal(), OffsideLine);

        // The promised return exists as a pass into the region, not just a pass to the initiator's feet.
        UtilityCandidate promised = Candidate(high, InitiatorId, CandidateGenerator.RunPassTag);
        Vector2 toRegion = SimMath.SafeNormalize(ReturnRegion - new Vector2(4.6f, 8f), new Vector2(1f, 0f));
        Assert.True(Vector2.Dot(
                new Vector2(promised.Command.KickAimDirectionXZ.X, promised.Command.KickAimDirectionXZ.Z),
                toRegion) > 0.98f,
            "the continuation pass was not aimed at the contract's region");

        // The continuation preference is the documented bounded bonus, scaled by the remaining promise.
        float highPreference = promised.Terms.Preference;
        float lowPreference = Candidate(low, InitiatorId, CandidateGenerator.RunPassTag).Terms.Preference;
        Assert.Equal(CandidateGenerator.CombinationContinuationBonus * 0.4f,
            highPreference - lowPreference, 3);
    }

    [Fact]
    public void TheContinuationPreference_IsTheBoundedBonus_AndItDoesNotAddAPass()
    {
        // With the third man's run already advertised, the ball to him exists in both worlds; the contract
        // adds exactly its stated preference and nothing else.
        var controlComms = new MatchCommunicationContext();
        var treatedComms = new MatchCommunicationContext();
        PublishAdvert(controlComms, ThirdRunnerId, meetXZ: new Vector2(20f, 10f), meetTick: 460, publishedTick: 270);
        PublishAdvert(treatedComms, ThirdRunnerId, meetXZ: new Vector2(20f, 10f), meetTick: 460, publishedTick: 270);
        Assert.True(treatedComms.Combinations.Publish(CombinationKind.ThirdPlayer, Home, InitiatorId, ReceiverId,
            ThirdRunnerId, returnXZ: new Vector2(20f, 10f), returnRadiusMeters: 4f, expectedArrivalTick: 460,
            confidence01: 0.8f, tick: 300));

        FootballerObservation neutral = ReceiverObservation(tick: 390, combination: CombinationContext.None);
        FootballerObservation contracted = ReceiverObservation(tick: 390,
            combination: ContextOf(treatedComms, ReceiverId, 390));

        var control = new CandidateGenerator(Pitch, controlComms, new AiRuntimeConfig());
        var treated = new CandidateGenerator(Pitch, treatedComms, new AiRuntimeConfig());
        control.TimeOnBall = 1f;
        treated.TimeOnBall = 1f;
        control.GenerateOnBallCandidates(neutral, OppGoal(), OffsideLine);
        treated.GenerateOnBallCandidates(contracted, OppGoal(), OffsideLine);

        float controlScore = PassScore(control, ThirdRunnerId, CandidateGenerator.RunPassTag);
        float treatedScore = PassScore(treated, ThirdRunnerId, CandidateGenerator.RunPassTag);
        float expected = CandidateGenerator.CombinationContinuationBonus * 0.8f;
        Assert.Equal(controlScore + expected, treatedScore, 3);
        Assert.True(HasCandidate(treated, ThirdRunnerId, CandidateGenerator.RunPassTag));
        Assert.True(HasCandidate(control, ThirdRunnerId, CandidateGenerator.RunPassTag));
    }

    [Fact]
    public void ABlockedReturn_AbortsTheContractCleanly()
    {
        var comms = new MatchCommunicationContext();
        Assert.True(PublishOneTwo(comms.Combinations, tick: 300, arrivalTick: 425));

        // A defender standing on the receiver-to-region line makes the promised return impossible.
        FootballerObservation blocked = ReceiverObservation(tick: 390,
            combination: ContextOf(comms, ReceiverId, 390), blockReturnLane: true);

        var generator = new CandidateGenerator(Pitch, comms, new AiRuntimeConfig());
        generator.TimeOnBall = 1f;
        generator.GenerateOnBallCandidates(blocked, OppGoal(), OffsideLine);

        Assert.False(HasCandidate(generator, InitiatorId, CandidateGenerator.RunPassTag));
        Assert.Equal(1, comms.Combinations.CountOfEnd(CombinationEndReason.AbandonedByReceiver));
        Assert.False(comms.Combinations.TryGetContext(Home, ReceiverId, 321, out _));

        // The initiator is no longer asked to keep running for it.
        Assert.False(comms.Combinations.TryGetContext(Home, InitiatorId, 321, out _));
    }

    // ---------------------------------------------------------------------------------------------
    // The initiator: run into the frozen region, abandon it when it stops being a lane.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AOneTwosInitiator_RunsIntoTheFrozenReturnRegion()
    {
        var comms = new MatchCommunicationContext();
        Assert.True(PublishOneTwo(comms.Combinations, tick: 300, arrivalTick: 390));

        FootballerObservation observation = InitiatorObservation(tick: 302,
            combination: ContextOf(comms, InitiatorId, 302));
        var generator = new CandidateGenerator(Pitch, comms, new AiRuntimeConfig());
        generator.GenerateOffBallCandidates(observation, OppGoal(), OwnGoal(),
            myInterceptXZ: new Vector2(-4f, 0f), isPrimaryBallResponder: false, offsideLineProjX: OffsideLine);

        UtilityCandidate run = Assert.Single(generator.Candidates,
            candidate => candidate.Key.Kind == CandidateKind.Run &&
                         candidate.Key.TargetTag == CandidateGenerator.CombinationReturnTag);
        Assert.Equal("Run:CombinationReturn", run.ActionName);
        Assert.NotNull(run.TargetXZ);
        Assert.Equal(ReturnRegion.X, run.TargetXZ!.Value.X, 1);
        Assert.Equal(ReturnRegion.Y, run.TargetXZ.Value.Z, 1);
        Assert.True(run.Command.Sprint, "the return run must be a sprint");
    }

    [Fact]
    public void AnInvalidReturnRegion_IsAbandoned_AndNoRunIsOffered()
    {
        var comms = new MatchCommunicationContext();
        Assert.True(PublishOneTwo(comms.Combinations, tick: 300, arrivalTick: 390));

        // A defender has taken the room at the region: the lane the initiator committed to is gone.
        FootballerObservation occupied = InitiatorObservation(tick: 302,
            combination: ContextOf(comms, InitiatorId, 302), occupantAtRegion: true);
        var generator = new CandidateGenerator(Pitch, comms, new AiRuntimeConfig());
        generator.GenerateOffBallCandidates(occupied, OppGoal(), OwnGoal(),
            myInterceptXZ: new Vector2(-4f, 0f), isPrimaryBallResponder: false, offsideLineProjX: OffsideLine);

        Assert.DoesNotContain(generator.Candidates,
            candidate => candidate.Key.TargetTag == CandidateGenerator.CombinationReturnTag);
        Assert.Equal(1, comms.Combinations.CountOfEnd(CombinationEndReason.AbandonedByInitiator));
        Assert.False(comms.Combinations.TryGetContext(Home, InitiatorId, 303, out _));
    }

    [Fact]
    public void Law11_OneTwoInitiator_CanTargetBehindTheLine_AndHoldsOnsideUntilReleased()
    {
        // Initiator at (15, 0) (onside behind offsideLine=20), receiver at (19, 8) (onside: 19 <= 20-1).
        // Return region is (15+8, 2.5) = (23, 2.5), 3 m behind offsideLine=20 (within +6 m behind-line depth).
        var comms = new MatchCommunicationContext();
        Vector2 behindRegion = new(23f, 2.5f);
        FootballerObservation carrierObs = new()
        {
            SelfId = InitiatorId,
            SelfTeam = Home,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = new Vector3(15f, 0f, 0f),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(15.4f, 0f, 0f),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = Home,
            BallLastTouchedByPlayerId = InitiatorId,
            BallLastTouchedByTeam = Home,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = InitiatorId,
            TacticalAnchorPosition = new Vector3(15f, 0f, 0f),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = TimeOf(MatchTick),
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = MatchTick,
            VisiblePlayers = new List<PerceivedEntity>
            {
                Mate(ReceiverId, new Vector3(19f, 0f, 8f), TacticalRole.CentralMidfielder),
                Opponent(PresserId, new Vector3(12.5f, 0f, 0.5f)),
                Opponent(SecondDefenderId, new Vector3(20f, 0f, 11.5f))
            },
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = new PlayerTendencies(),
            SelfAttributes = new PlayerAttributes()
        };

        var carrierGen = new CandidateGenerator(Pitch, comms, new AiRuntimeConfig { EnableCombinations = true })
        {
            TimeOnBall = 0.8f
        };
        carrierGen.GenerateOnBallCandidates(carrierObs, OppGoal(), offsideLineProjX: 20f);
        UtilityCandidate pass = Candidate(carrierGen, ReceiverId, "ToFeet");
        carrierGen.NoteDecision(carrierObs, pass.Key);

        Assert.True(comms.Combinations.TryGetInitiatorContract(Home, InitiatorId, MatchTick, out CombinationContract contract));
        Assert.True(contract.ReturnXZ.X > 20f,
            $"one-two return region must be allowed behind offsideLine=20, got {contract.ReturnXZ.X:F2}");

        // Pre-release: initiator's off-ball run must stay held onside (<= 20 - 1 = 19) without abandoning.
        FootballerObservation preRelease = new()
        {
            SelfId = InitiatorId,
            SelfTeam = Home,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = new Vector3(17f, 0f, 0f),
            SelfVelocity = new Vector3(4f, 0f, 1f),
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = false,
            ControlQuality01 = 0f,
            PerceivedBallPosition = new Vector3(18f, 0f, 6f),
            PerceivedBallVelocity = new Vector3(6f, 0f, 1f),
            PossessingTeam = Home,
            BallLastTouchedByPlayerId = InitiatorId,
            BallLastTouchedByTeam = Home,
            BallControlState = BallControlState.InFlightPass,
            BallControllingPlayerId = null,
            IntendedReceiverId = ReceiverId,
            TacticalAnchorPosition = new Vector3(17f, 0f, 0f),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = TimeOf(302),
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = 302,
            VisiblePlayers = new List<PerceivedEntity>
            {
                Mate(ReceiverId, new Vector3(19f, 0f, 8f), TacticalRole.CentralMidfielder),
                Opponent(PresserId, new Vector3(12.5f, 0f, 0.5f))
            },
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = new PlayerTendencies(),
            SelfAttributes = new PlayerAttributes(),
            Combination = ContextOf(comms, InitiatorId, 302)
        };
        var offBallGen = new CandidateGenerator(Pitch, comms, new AiRuntimeConfig());
        offBallGen.GenerateOffBallCandidates(preRelease, OppGoal(), OwnGoal(),
            myInterceptXZ: new Vector2(17f, 0f), isPrimaryBallResponder: false, offsideLineProjX: 20f);

        UtilityCandidate preRun = Assert.Single(offBallGen.Candidates,
            c => c.Key.Kind == CandidateKind.Run && c.Key.TargetTag == CandidateGenerator.CombinationReturnTag);
        Assert.True(preRun.TargetXZ!.Value.X <= 20f - OffsideTactics.MarginMeters + 0.01f,
            $"pre-release initiator run target must hold onside <= 19, got {preRun.TargetXZ.Value.X:F2}");

        // Post-release: once return pass is in flight to initiator, he releases into the behind-line region.
        FootballerObservation released = new()
        {
            SelfId = InitiatorId,
            SelfTeam = Home,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = new Vector3(17f, 0f, 0f),
            SelfVelocity = new Vector3(4f, 0f, 1f),
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = false,
            ControlQuality01 = 0f,
            PerceivedBallPosition = new Vector3(19f, 0f, 7f),
            PerceivedBallVelocity = new Vector3(6f, 0f, -2f),
            PossessingTeam = Home,
            BallLastTouchedByPlayerId = ReceiverId,
            BallLastTouchedByTeam = Home,
            BallControlState = BallControlState.InFlightPass,
            BallControllingPlayerId = null,
            IntendedReceiverId = InitiatorId,
            TacticalAnchorPosition = new Vector3(17f, 0f, 0f),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = TimeOf(325),
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = 325,
            VisiblePlayers = preRelease.VisiblePlayers,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = new PlayerTendencies(),
            SelfAttributes = new PlayerAttributes(),
            Combination = ContextOf(comms, InitiatorId, 325)
        };
        var releasedGen = new CandidateGenerator(Pitch, comms, new AiRuntimeConfig());
        releasedGen.GenerateOffBallCandidates(released, OppGoal(), OwnGoal(),
            myInterceptXZ: behindRegion, isPrimaryBallResponder: false, offsideLineProjX: 20f);
        UtilityCandidate postRun = Assert.Single(releasedGen.Candidates,
            c => c.Key.Kind == CandidateKind.Run && c.Key.TargetTag == CandidateGenerator.CombinationReturnTag);
        Assert.True(postRun.TargetXZ!.Value.X > 20f,
            $"post-release initiator run must release into {contract.ReturnXZ.X:F2}, got {postRun.TargetXZ.Value.X:F2}");
    }

    [Fact]
    public void TheSwitchOff_IsThePreviousBuild()
    {
        // With combinations off no contract is published and no combination candidate exists, even when the
        // observation says a contract is live: the switch gates the producer and the consumers together.
        var comms = new MatchCommunicationContext();
        Assert.True(PublishOneTwo(comms.Combinations, tick: 300, arrivalTick: 390));
        FootballerObservation initiator = InitiatorObservation(tick: 302,
            combination: ContextOf(comms, InitiatorId, 302));
        FootballerObservation receiver = ReceiverObservation(tick: 320,
            combination: ContextOf(comms, ReceiverId, 320));

        var off = new AiRuntimeConfig { EnableCombinations = false };
        var offGenerator = new CandidateGenerator(Pitch, new MatchCommunicationContext(), off);
        offGenerator.GenerateOffBallCandidates(initiator, OppGoal(), OwnGoal(),
            myInterceptXZ: new Vector2(-4f, 0f), isPrimaryBallResponder: false, offsideLineProjX: OffsideLine);
        Assert.DoesNotContain(offGenerator.Candidates,
            candidate => candidate.Key.TargetTag == CandidateGenerator.CombinationReturnTag);

        offGenerator.TimeOnBall = 1f;
        offGenerator.GenerateOnBallCandidates(receiver, OppGoal(), OffsideLine);
        Assert.False(HasCandidate(offGenerator, InitiatorId, CandidateGenerator.RunPassTag));
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures and helpers.
    // ---------------------------------------------------------------------------------------------

    private const float OffsideLine = 20f;

    private static Vector3 OppGoal() => new(52.5f, 0f, 0f);

    private static Vector3 OwnGoal() => new(-52.5f, 0f, 0f);

    private static bool PublishOneTwo(CombinationCoordinator board, int tick, int arrivalTick,
        float confidence01 = 0.8f) =>
        board.Publish(CombinationKind.OneTwo, Home, InitiatorId, ReceiverId, thirdPlayerId: 0,
            ReturnRegion, returnRadiusMeters: 3f, expectedArrivalTick: arrivalTick, confidence01: confidence01,
            tick: tick);

    private static CombinationContext ContextOf(MatchCommunicationContext comms, int playerId, int tick)
    {
        if (comms.Combinations.TryGetContext(Home, playerId, tick, out CombinationContext context))
            return context;

        throw new Xunit.Sdk.XunitException($"no combination context for #{playerId} at tick {tick}");
    }

    private static void PublishAdvert(
        MatchCommunicationContext comms, int playerId, Vector2 meetXZ, int meetTick, int publishedTick)
    {
        var path = new RunPath(
            RunKind.InBehind, StartXZ: new Vector2(0f, 8f), MeetXZ: meetXZ, MeetRadiusMeters: 4f,
            StartTick: publishedTick, MeetTick: meetTick, ExpiresTick: meetTick + 90,
            HoldUntilTick: 0, HoldXZ: meetXZ, Confidence01: 0.9f);
        comms.Runs.Publish(Home, playerId, new RunIntent(path, TacticalJob.DepthRun, publishedTick),
            publishedTick);
    }

    private static BallContactEvent Kick(int tick, TeamId team, int kicker, int target) =>
        new(tick, BallContactKind.Kick, kicker, team, Vector3.Zero)
        {
            Intent = KickIntentType.GroundPass,
            IntendedReceiverId = target
        };

    /// <summary>
    /// A pressed home carrier (midfield, facing up the pitch) with a returnable partner: the receiver is
    /// lightly pressed too, there is room ahead of the carrier, and no opponent sits in the partner-to-region
    /// lane. With <paramref name="pressured"/> false nobody is near either end, so only an advertised run can
    /// make the pass a combination.
    /// </summary>
    private static FootballerObservation CarrierObservation(bool pressured = true)
    {
        var visible = new List<PerceivedEntity>
        {
            Mate(ReceiverId, new Vector3(5f, 0f, 8f), TacticalRole.CentralMidfielder),
            Mate(ReceiverMateId, new Vector3(-12f, 0f, -10f), TacticalRole.CenterBack),
            Opponent(207, new Vector3(32f, 0f, -20f))
        };

        if (pressured)
        {
            visible.Add(Opponent(PresserId, new Vector3(-7.5f, 0f, 0.5f)));
            visible.Add(Opponent(SecondDefenderId, new Vector3(7f, 0f, 11.5f)));
        }
        else
        {
            visible.Add(Opponent(PresserId, new Vector3(-20f, 0f, -20f)));
            visible.Add(Opponent(SecondDefenderId, new Vector3(20f, 0f, 20f)));
        }

        return new FootballerObservation
        {
            SelfId = InitiatorId,
            SelfTeam = Home,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = new Vector3(-5f, 0f, 0f),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(-4.6f, 0f, 0f),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = Home,
            BallLastTouchedByPlayerId = InitiatorId,
            BallLastTouchedByTeam = Home,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = InitiatorId,
            TacticalAnchorPosition = new Vector3(-5f, 0f, 0f),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = TimeOf(MatchTick),
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = MatchTick,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = new PlayerTendencies(),
            SelfAttributes = new PlayerAttributes()
        };
    }

    private const int MatchTick = 300;

    private static float TimeOf(int tick) => tick * SimConstants.FixedDeltaTime;

    /// <summary>
    /// The receiver of a published one-two, on the ball after the first pass. <paramref name="blockReturnLane"/>
    /// puts a defender on the return line; <paramref name="combination"/> is what the board told him.
    /// </summary>
    private static FootballerObservation ReceiverObservation(
        int tick,
        CombinationContext combination,
        bool blockReturnLane = false)
    {
        var visible = new List<PerceivedEntity>
        {
            Mate(InitiatorId, new Vector3(1f, 0f, 3f), TacticalRole.CentralMidfielder),
            Mate(ReceiverMateId, new Vector3(12f, 0f, 10f), TacticalRole.CentralMidfielder),
            // The third runner, in the region his advertisement names.
            Mate(ThirdRunnerId, new Vector3(16f, 0f, 10f), TacticalRole.CentralMidfielder),
            Opponent(PresserId, new Vector3(-10f, 0f, -6f)),
            Opponent(SecondDefenderId, new Vector3(30f, 0f, -14f))
        };

        if (blockReturnLane)
        {
            // Directly on the line from the ball (+4.6, 8) to the region (3, 2.5).
            visible.Add(Opponent(208, new Vector3(3.8f, 0f, 5.2f)));
        }

        return new FootballerObservation
        {
            SelfId = ReceiverId,
            SelfTeam = Home,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = new Vector3(5f, 0f, 8f),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(4.6f, 0f, 8f),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = Home,
            BallLastTouchedByPlayerId = ReceiverId,
            BallLastTouchedByTeam = Home,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = ReceiverId,
            TacticalAnchorPosition = new Vector3(5f, 0f, 8f),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = TimeOf(tick),
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = tick,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = new PlayerTendencies(),
            SelfAttributes = new PlayerAttributes(),
            Combination = combination
        };
    }

    /// <summary>The one-two initiator off the ball, running toward the region after making the first pass.</summary>
    private static FootballerObservation InitiatorObservation(
        int tick,
        CombinationContext combination,
        bool occupantAtRegion = false)
    {
        var visible = new List<PerceivedEntity>
        {
            Mate(ReceiverId, new Vector3(5f, 0f, 8f), TacticalRole.CentralMidfielder),
            Mate(ReceiverMateId, new Vector3(12f, 0f, -12f), TacticalRole.CentralMidfielder),
            Opponent(PresserId, new Vector3(-12f, 0f, -4f))
        };

        if (occupantAtRegion)
            visible.Add(Opponent(209, new Vector3(ReturnRegion.X, 0f, ReturnRegion.Y)));

        return new FootballerObservation
        {
            SelfId = InitiatorId,
            SelfTeam = Home,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = new Vector3(-3f, 0f, 0f),
            SelfVelocity = new Vector3(4f, 0f, 1f),
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = false,
            ControlQuality01 = 0f,
            PerceivedBallPosition = new Vector3(2f, 0f, 6f),
            PerceivedBallVelocity = new Vector3(6f, 0f, 1f),
            PossessingTeam = Home,
            BallLastTouchedByPlayerId = InitiatorId,
            BallLastTouchedByTeam = Home,
            BallControlState = BallControlState.InFlightPass,
            BallControllingPlayerId = null,
            IntendedReceiverId = ReceiverId,
            TacticalAnchorPosition = new Vector3(-3f, 0f, 0f),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = TimeOf(tick),
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = tick,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = new PlayerTendencies(),
            SelfAttributes = new PlayerAttributes(),
            Combination = combination
        };
    }

    private static PerceivedEntity Mate(int id, Vector3 position, TacticalRole role) =>
        new(id, Home, role, position, Vector3.Zero, 0.95f);

    private static PerceivedEntity Opponent(int id, Vector3 position) =>
        new(id, Away, TacticalRole.DefensiveMidfielder, position, Vector3.Zero, 0.95f);

    private static float PassScore(CandidateGenerator generator, int targetId, string tag)
    {
        foreach (UtilityCandidate candidate in generator.Candidates)
        {
            if (candidate.Key.Kind != CandidateKind.Pass) continue;
            if (candidate.Key.TargetPlayerId != targetId) continue;
            if (!string.Equals(candidate.Key.TargetTag, tag, StringComparison.Ordinal)) continue;
            return candidate.UtilityScore;
        }

        throw new Xunit.Sdk.XunitException($"no pass match to #{targetId} tag {tag}; candidates: {KeysOf(generator)}");
    }

    private static UtilityCandidate Candidate(CandidateGenerator generator, int targetId, string tag)
    {
        foreach (UtilityCandidate candidate in generator.Candidates)
        {
            if (candidate.Key.Kind != CandidateKind.Pass) continue;
            if (candidate.Key.TargetPlayerId != targetId) continue;
            if (!string.Equals(candidate.Key.TargetTag, tag, StringComparison.Ordinal)) continue;
            return candidate;
        }

        throw new Xunit.Sdk.XunitException($"no pass match to #{targetId} tag {tag}; candidates: {KeysOf(generator)}");
    }

    [Fact]
    public void OneTwoReturn_WhenInitiatorHeldOnsideBehindSchedule_PacesPassToLiveReachInsteadOfMaxPowerRocket()
    {
        var comms = new MatchCommunicationContext();
        var returnRegion = new Vector2(30f, 0f);
        const int publishTick = 300;
        const int evalTick = 390;
        // Stale ExpectedArrivalTick = evalTick + 30 (0.50 s remaining) while initiator is still at (24, 0).
        Assert.True(comms.Combinations.Publish(
            CombinationKind.OneTwo,
            Home,
            InitiatorId,
            ReceiverId,
            thirdPlayerId: 0,
            returnRegion,
            returnRadiusMeters: 3f,
            expectedArrivalTick: evalTick + 30,
            confidence01: 0.9f,
            tick: publishTick));

        var receiverPos = new Vector3(18f, 0f, 8f);
        var ballPos = new Vector3(18.4f, 0f, 8f);
        var initiatorPos = new Vector3(24f, 0f, 0f);
        var visible = new List<PerceivedEntity>
        {
            Mate(InitiatorId, initiatorPos, TacticalRole.CentralMidfielder),
            Opponent(PresserId, new Vector3(8f, 0f, -10f)),
            Opponent(SecondDefenderId, new Vector3(40f, 0f, -15f))
        };

        var obs = new FootballerObservation
        {
            SelfId = ReceiverId,
            SelfTeam = Home,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = receiverPos,
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = ballPos,
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = Home,
            BallLastTouchedByPlayerId = ReceiverId,
            BallLastTouchedByTeam = Home,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = ReceiverId,
            TacticalAnchorPosition = receiverPos,
            AttackingDirectionX = 1f,
            MatchTimeSeconds = TimeOf(evalTick),
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = evalTick,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = new PlayerTendencies(),
            SelfAttributes = new PlayerAttributes(),
            Combination = ContextOf(comms, ReceiverId, evalTick)
        };

        var generator = new CandidateGenerator(Pitch, comms, new AiRuntimeConfig()) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(obs, OppGoal(), offsideLineProjX: 25f);

        UtilityCandidate returnPass = Candidate(generator, InitiatorId, CandidateGenerator.RunPassTag);
        Assert.True(returnPass.Command.KickPower01 <= 0.75f,
            $"one-two return must be paced to live reach instead of slamming power={returnPass.Command.KickPower01:F2}");

        var planner = new PassPlanner(Pitch);
        float distance = Vector2.Distance(new Vector2(ballPos.X, ballPos.Z), returnRegion);
        ReachEstimate liveReach = TimeToReachModel.ToPoint(
            ReachKinematics.FromEntity(visible[0]), returnRegion, TimeToReachModel.DefaultHorizonSeconds);
        Assert.True(liveReach.Reachable);
        float rolledPastAtLiveReach = planner.BallDistanceAtTime(returnPass.Command.KickPower01, liveReach.Seconds) - distance;
        Assert.True(rolledPastAtLiveReach <= CandidateGenerator.EffectivePromiseCatchRadiusMeters(distance),
            $"paced pass rolled {rolledPastAtLiveReach:F2} m past return region at runner live reach {liveReach.Seconds:F2} s");
    }

    /// <summary>
    /// ai67 (Task 4): exact <c>t=11040</c> one-two continuation geometry — when the receiver holds the ball
    /// under an active <c>OneTwo</c> contract and the initiator is already near <c>ReturnXZ</c> ahead of
    /// <c>ExpectedArrivalTick</c>:
    /// (1) no <c>ToSpace</c> candidate may be generated for the initiator,
    /// (2) <c>ToFeet</c> must not receive <c>CombinationContinuationBonus</c>, and
    /// (3) <c>ToRun</c> aimed at <c>ReturnXZ</c> is generated and outscores <c>ToFeet</c>.
    /// </summary>
    [Fact]
    public void OneTwoReceiver_DoesNotGenerateToSpaceOrAwardContinuationBonusToFeet_AndTimesToLiveArrivalWhenAheadOfSchedule()
    {
        var comms = new MatchCommunicationContext();
        var retXZ = new Vector2(7.0f, 27.6f);
        const int publishTick = 11000;
        const int evalTick = 11040;
        Assert.True(comms.Combinations.Publish(
            CombinationKind.OneTwo,
            Home,
            InitiatorId,
            ReceiverId,
            thirdPlayerId: 0,
            retXZ,
            returnRadiusMeters: 3f,
            expectedArrivalTick: evalTick + 105, // 1.75 s remaining while initiator is 1.5 m from retXZ
            confidence01: 0.9f,
            tick: publishTick));

        var receiverPos = new Vector3(3.2f, 0f, 18.3f);
        var ballPos = new Vector3(3.5f, 0f, 18.6f);
        var initiatorPos = new Vector3(5.8f, 0f, 28.6f);
        var visible = new List<PerceivedEntity>
        {
            new(InitiatorId, Home, TacticalRole.FullBack, initiatorPos, new Vector3(2.5f, 0f, -1.5f), 0.95f),
            Opponent(PresserId, new Vector3(-5f, 0f, 5f)),
            Opponent(SecondDefenderId, new Vector3(30f, 0f, -10f))
        };

        FootballerObservation MakeObs(CombinationContext combination) => new()
        {
            SelfId = ReceiverId,
            SelfTeam = Home,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = receiverPos,
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = SimMath.SafeNormalize(new Vector2(initiatorPos.X - receiverPos.X, initiatorPos.Z - receiverPos.Z), Vector2.UnitX),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = ballPos,
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = Home,
            BallLastTouchedByPlayerId = ReceiverId,
            BallLastTouchedByTeam = Home,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = ReceiverId,
            TacticalAnchorPosition = receiverPos,
            AttackingDirectionX = 1f,
            MatchTimeSeconds = TimeOf(evalTick),
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = evalTick,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = new PlayerTendencies(),
            SelfAttributes = new PlayerAttributes(),
            Combination = combination
        };

        FootballerObservation obs = MakeObs(ContextOf(comms, ReceiverId, evalTick));
        var controlComms = new MatchCommunicationContext();
        FootballerObservation controlObs = MakeObs(CombinationContext.None);

        var treated = new CandidateGenerator(Pitch, comms, new AiRuntimeConfig()) { TimeOnBall = 0.8f };
        var control = new CandidateGenerator(Pitch, controlComms, new AiRuntimeConfig()) { TimeOnBall = 0.8f };
        treated.GenerateOnBallCandidates(obs, OppGoal(), offsideLineProjX: 25f);
        control.GenerateOnBallCandidates(controlObs, OppGoal(), offsideLineProjX: 25f);

        Assert.False(HasCandidate(treated, InitiatorId, CandidateGenerator.SpacePassTag),
            "ToSpace must not be generated for a live OneTwo continuation target");

        UtilityCandidate treatedFeet = Candidate(treated, InitiatorId, "ToFeet");
        UtilityCandidate controlFeet = Candidate(control, InitiatorId, "ToFeet");
        Assert.Equal(controlFeet.Terms.Preference, treatedFeet.Terms.Preference, 4);

        UtilityCandidate toRun = Candidate(treated, InitiatorId, CandidateGenerator.RunPassTag);
        Vector2 toRetXZ = SimMath.SafeNormalize(retXZ - new Vector2(ballPos.X, ballPos.Z), Vector2.UnitX);
        Assert.True(Vector2.Dot(
                new Vector2(toRun.Command.KickAimDirectionXZ.X, toRun.Command.KickAimDirectionXZ.Z),
                toRetXZ) > 0.98f,
            "OneTwo ToRun return pass must aim at ReturnXZ");
        Assert.True(toRun.UtilityScore > treatedFeet.UtilityScore,
            $"ToRun ({toRun.UtilityScore:F3}) must outscore ToFeet ({treatedFeet.UtilityScore:F3})");
    }

    private static bool HasCandidate(CandidateGenerator generator, int targetId, string tag)
    {
        foreach (UtilityCandidate candidate in generator.Candidates)
        {
            if (candidate.Key.Kind != CandidateKind.Pass) continue;
            if (candidate.Key.TargetPlayerId != targetId) continue;
            if (string.Equals(candidate.Key.TargetTag, tag, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static string KeysOf(CandidateGenerator generator)
    {
        var text = new StringBuilder();
        foreach (UtilityCandidate candidate in generator.Candidates)
            text.Append(candidate.Key.Describe()).Append(';');
        return text.ToString();
    }
}
