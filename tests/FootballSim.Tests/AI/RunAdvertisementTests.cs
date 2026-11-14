using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Scenarios;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A5.3 — advertise the run, match the pass to it.
///
/// A5.1/A5.2 gave a runner a frozen meeting region; the carrier still priced the same ball blind, leading a
/// velocity plus a fixed push and paying <see cref="PassSuccessModel.LeadRisk01"/> for a run nothing had
/// promised. These tests pin the three halves of the fix:
///
/// <list type="bullet">
/// <item><b>The board.</b> A promise is one per runner, per team, heard only after the communication delay,
/// withdrawn when the runner stops, and usable only inside its own window. A claim names a ball rather than
/// a mood: it lives until that ball should have arrived and no longer.</item>
/// <item><b>The carrier.</b> With a promise the pass is aimed at the announced region, planned to arrive
/// when the runner does, and priced by the promise (confidence × timing) instead of by the lead — one risk
/// owner, not two. Without a promise the pre-A5.3 option is unchanged, which is what makes the A/B about
/// the advertisement and nothing else.</item>
/// <item><b>The runner.</b> An adopted run is announced; a claim keeps it — and the region it names — alive
/// until the ball has arrived, including releasing a timed hold so a late box arrival is received rather
/// than timed.</item>
/// </list>
///
/// The fixture-level evidence (a team-mate hears a real run in the high-line scenario) is the last test.
/// </summary>
public sealed class RunAdvertisementTests
{
    private static readonly PitchDimensions Pitch = new();

    private const int CarrierId = 8;
    private const int RunnerId = 9;

    // ---------------------------------------------------------------------------------------------
    // The board: delay, team, renewal, withdrawal, window
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void APromise_IsNotHeardUntilTheCommunicationDelayHasPassed()
    {
        var board = new RunAdvertisementBoard();
        RunIntent intent = Intent(RunKind.InBehind, meet: new Vector2(20f, 4f), publishedTick: 100,
            meetTick: 160, expiresTick: 220);

        board.Publish(TeamId.Home, RunnerId, intent, 100);

        int due = 100 + RunAdvertisementBoard.CommunicationDelayTicks;
        Assert.False(board.TryGetAdvertisement(TeamId.Home, RunnerId, due - 1, out _));
        Assert.Equal(0, board.VisibleCount(TeamId.Home, due - 1));

        Assert.True(board.TryGetAdvertisement(TeamId.Home, RunnerId, due, out RunAdvertisement advert));
        Assert.Equal(due, advert.VisibleTick);
        Assert.Equal((int)RunKind.InBehind, (int)advert.Kind);
        Assert.Equal(intent.Path.MeetXZ, advert.MeetXZ);
        Assert.Equal(1, board.VisibleCount(TeamId.Home, due));
    }

    [Fact]
    public void TheBoard_IsPerTeam_AndAClaimIsToo()
    {
        var board = new RunAdvertisementBoard();
        board.Publish(TeamId.Home, RunnerId,
            Intent(RunKind.InBehind, new Vector2(20f, 4f), 100, 160, 220), 100);
        board.Claim(TeamId.Home, RunnerId, CarrierId, arriveTick: 160, tick: 101);

        int visible = 100 + RunAdvertisementBoard.CommunicationDelayTicks;
        Assert.Equal(1, board.VisibleCount(TeamId.Home, visible));
        Assert.Equal(0, board.VisibleCount(TeamId.Away, visible));
        Assert.False(board.TryGetAdvertisement(TeamId.Away, RunnerId, visible, out _));

        Assert.True(board.TryGetClaim(TeamId.Home, RunnerId, visible, out _));
        Assert.False(board.TryGetClaim(TeamId.Away, RunnerId, visible, out _));
    }

    [Fact]
    public void ReAnnouncingTheSameRun_KeepsItsPublicationTick()
    {
        var board = new RunAdvertisementBoard();
        RunIntent intent = Intent(RunKind.InBehind, new Vector2(20f, 4f), publishedTick: 100,
            meetTick: 160, expiresTick: 220);
        board.Publish(TeamId.Home, RunnerId, intent, 100);
        board.Publish(TeamId.Home, RunnerId, intent, 130);

        int due = 100 + RunAdvertisementBoard.CommunicationDelayTicks;
        Assert.True(board.TryGetAdvertisement(TeamId.Home, RunnerId, due, out RunAdvertisement advert));
        Assert.Equal(100, advert.PublishedTick);
        Assert.Equal(due, advert.VisibleTick);
    }

    [Fact]
    public void AnnouncingADifferentRegion_StartsTheDelayAgain()
    {
        var board = new RunAdvertisementBoard();
        board.Publish(TeamId.Home, RunnerId,
            Intent(RunKind.InBehind, new Vector2(20f, 4f), 100, 160, 220), 100);

        RunIntent moved = Intent(RunKind.InBehind, new Vector2(24f, -6f), publishedTick: 130,
            meetTick: 190, expiresTick: 250);
        board.Publish(TeamId.Home, RunnerId, moved, 130);

        int oldDue = 100 + RunAdvertisementBoard.CommunicationDelayTicks;
        int newDue = 130 + RunAdvertisementBoard.CommunicationDelayTicks;
        Assert.False(board.TryGetAdvertisement(TeamId.Home, RunnerId, oldDue, out _));
        Assert.True(board.TryGetAdvertisement(TeamId.Home, RunnerId, newDue, out RunAdvertisement advert));
        Assert.Equal(new Vector2(24f, -6f), advert.MeetXZ);
    }

    [Fact]
    public void AWithdrawnRun_DisappearsAtOnce()
    {
        var board = new RunAdvertisementBoard();
        board.Publish(TeamId.Home, RunnerId,
            Intent(RunKind.InBehind, new Vector2(20f, 4f), 100, 160, 220), 100);

        int visible = 100 + RunAdvertisementBoard.CommunicationDelayTicks;
        Assert.Equal(1, board.VisibleCount(TeamId.Home, visible));

        board.Withdraw(TeamId.Home, RunnerId);

        Assert.Equal(0, board.VisibleCount(TeamId.Home, visible));
        Assert.False(board.TryGetAdvertisement(TeamId.Home, RunnerId, visible, out _));
        Assert.Equal(1, board.WithdrawnCount);
    }

    [Fact]
    public void APromise_StopsBeingUsableWhenItsWindowCloses_AndDecaysThroughTheGrace()
    {
        var board = new RunAdvertisementBoard();
        board.Publish(TeamId.Home, RunnerId,
            Intent(RunKind.InBehind, new Vector2(20f, 4f), 100, meetTick: 160, expiresTick: 220,
                confidence: 0.8f), 100);

        int visible = 100 + RunAdvertisementBoard.CommunicationDelayTicks;
        Assert.True(board.TryGetAdvertisement(TeamId.Home, RunnerId, visible, out RunAdvertisement advert));

        // Before the meeting the carrier may believe the runner's own confidence.
        Assert.Equal(0.8f, advert.Promise01(160), 3);

        // After it, the belief decays as the region stops being where he *will* be.
        float atMeeting = advert.Promise01(160);
        float later = advert.Promise01(190);
        float atExpiry = advert.Promise01(220);
        Assert.True(later < atMeeting);
        Assert.True(atExpiry <= later);
        Assert.Equal(0f, advert.Promise01(221), 3);
        Assert.False(advert.IsUsableAt(221));
    }

    [Fact]
    public void TheBoard_IsBoundedAndEvictsTheLeastLivePromiseFirst()
    {
        var board = new RunAdvertisementBoard();
        int issued = 0;

        // One promise per team per player id, alternating sides until the board is exactly full.
        for (int i = 0; i < RunAdvertisementBoard.Capacity; i++)
        {
            TeamId team = i % 2 == 0 ? TeamId.Home : TeamId.Away;
            int playerId = 100 + i;
            board.Publish(team, playerId,
                Intent(RunKind.InBehind, new Vector2(10f + i, 0f), 100, 200, 260 + i), 100);
            issued++;
        }

        Assert.Equal(issued, board.PublishedCount);
        Assert.Equal(RunAdvertisementBoard.Capacity / 2, board.VisibleCount(TeamId.Home, 130));

        // A full board still hears a new promise: the least nearly-expired entry is reused rather than the
        // shout being dropped, and the bound itself is what keeps the table small.
        board.Publish(TeamId.Home, 999,
            Intent(RunKind.InBehind, new Vector2(30f, 0f), 100, 260, 320), 100);

        Assert.True(board.TryGetAdvertisement(TeamId.Home, 999, 130, out _));
        Assert.Equal(RunAdvertisementBoard.Capacity / 2, board.VisibleCount(TeamId.Home, 130));
    }

    [Fact]
    public void AClaim_LivesUntilTheBallShouldHaveArrived()
    {
        var board = new RunAdvertisementBoard();
        board.Publish(TeamId.Home, RunnerId,
            Intent(RunKind.InBehind, new Vector2(20f, 4f), 100, meetTick: 160, expiresTick: 220), 100);

        board.Claim(TeamId.Home, RunnerId, CarrierId, arriveTick: 160, tick: 101);

        Assert.True(board.TryGetClaim(TeamId.Home, RunnerId, 160, out RunClaim claim));
        Assert.Equal(CarrierId, claim.CarrierId);
        Assert.Equal(160 + RunAdvertisementBoard.ClaimGraceTicks, claim.ExpiresTick);

        int afterGrace = claim.ExpiresTick + 1;
        Assert.False(board.TryGetClaim(TeamId.Home, RunnerId, afterGrace, out _));
    }

    [Fact]
    public void ReIssuingAClaim_DoesNotPushItsDeadlineOutEveryTick()
    {
        var board = new RunAdvertisementBoard();
        board.Publish(TeamId.Home, RunnerId,
            Intent(RunKind.InBehind, new Vector2(20f, 4f), 100, meetTick: 160, expiresTick: 220), 100);

        // The carrier holds the pass as its chosen action for several ticks; the claim names the same ball,
        // so it keeps the deadline it was first raised with instead of sliding with the present.
        for (int tick = 101; tick <= 120; tick++)
            board.Claim(TeamId.Home, RunnerId, CarrierId, arriveTick: 160, tick: tick);

        Assert.True(board.TryGetClaim(TeamId.Home, RunnerId, 160, out RunClaim claim));
        Assert.Equal(101, claim.ClaimedTick);
    }

    // ---------------------------------------------------------------------------------------------
    // The runner: announcement, withdrawal, and the claim that keeps it
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AnAdoptedRun_IsAnnouncedToTheTeam_AfterTheDelay()
    {
        var board = new RunAdvertisementBoard();
        var generator = new RunCandidateGenerator(Pitch, board);
        FootballerObservation obs = DepthObservation(tick: 100);

        RunPath adopted = AdoptInBehind(generator, obs);

        Assert.Equal(1, board.PublishedCount);
        int due = obs.Tick + RunAdvertisementBoard.CommunicationDelayTicks;
        Assert.Equal(0, board.VisibleCount(TeamId.Home, due - 1));
        Assert.True(board.TryGetAdvertisement(TeamId.Home, obs.SelfId, due, out RunAdvertisement advert));
        Assert.Equal(adopted.MeetXZ, advert.MeetXZ);
        Assert.Equal(adopted.MeetTick, advert.MeetTick);
        Assert.Equal(adopted.Confidence01, advert.Confidence01);
    }

    [Fact]
    public void ChoosingSomethingOtherThanTheRun_WithdrawsTheAnnouncement()
    {
        var board = new RunAdvertisementBoard();
        var generator = new RunCandidateGenerator(Pitch, board);
        FootballerObservation obs = DepthObservation(tick: 100);
        AdoptInBehind(generator, obs);

        generator.NoteSelected(obs, new CandidateKey(CandidateKind.Shape, TargetTag: "support"));

        Assert.Null(generator.Intent);
        Assert.Equal(1, board.WithdrawnCount);
        Assert.Equal(0, board.VisibleCount(TeamId.Home, obs.Tick + RunAdvertisementBoard.CommunicationDelayTicks));
    }

    [Fact]
    public void AClaim_KeepsTheRunAlivePastItsOwnWindow_AndLetsItGoAfterwards()
    {
        var board = new RunAdvertisementBoard();
        var generator = new RunCandidateGenerator(Pitch, board);
        FootballerObservation obs = DepthObservation(tick: 100);
        RunPath adopted = AdoptInBehind(generator, obs);

        // The carrier plays the ball to arrive well after the run's own window closes.
        int arriveTick = adopted.ExpiresTick + 40;
        board.Claim(TeamId.Home, obs.SelfId, CarrierId, arriveTick, tick: 101);

        FootballerObservation afterWindow = DepthObservation(tick: adopted.ExpiresTick + 1);
        generator.Generate(afterWindow, 30f, afterWindow.Tick, 4);

        Assert.NotNull(generator.Intent);
        Assert.True(generator.Intent!.Value.Path.ExpiresTick >= arriveTick,
            $"the window must run to the ball's arrival {arriveTick}, not end at {generator.Intent.Value.Path.ExpiresTick}");

        // And once the ball should have arrived the claim lets go: the run cannot be frozen for ever.
        int afterClaim = arriveTick + RunAdvertisementBoard.ClaimGraceTicks + 1;
        FootballerObservation released = DepthObservation(tick: afterClaim);
        generator.Generate(released, 30f, released.Tick, 4);

        Assert.True(generator.Intent is null || generator.Intent.Value.AdoptedTick > obs.Tick);
    }

    [Fact]
    public void AClaim_ReleasesATimedBoxArrival()
    {
        var board = new RunAdvertisementBoard();
        var generator = new RunCandidateGenerator(Pitch, board);
        FootballerObservation obs = BoxObservation(tick: 100);

        Assert.True(generator.Generate(obs, 46f, obs.Tick, 4) > 0);
        RunPath arrival = PathOfKind(generator, RunKind.LateBoxArrival);
        Assert.True(arrival.IsHolding(arrival.StartTick));

        generator.NoteSelected(obs, RunTags.KeyOf(RunKind.LateBoxArrival));
        board.Claim(TeamId.Home, obs.SelfId, CarrierId, arriveTick: arrival.MeetTick, tick: 101);

        FootballerObservation next = BoxObservation(tick: 102);
        generator.Generate(next, 46f, next.Tick, 4);

        Assert.NotNull(generator.Intent);
        RunIntent live = generator.Intent!.Value;
        Assert.True(live.Path.HoldUntilTick <= next.Tick,
            $"a received run must not wait at its hold point (hold until {live.Path.HoldUntilTick}, now {next.Tick})");
        Assert.Equal(live.Path.MeetXZ, live.Path.TargetFor(new Vector2(20f, 6f), next.Tick));
    }

    [Fact]
    public void AClaimedRun_IsTheOnlyRunOnOffer()
    {
        var board = new RunAdvertisementBoard();
        var generator = new RunCandidateGenerator(Pitch, board);
        FootballerObservation obs = DepthObservation(tick: 100);
        AdoptInBehind(generator, obs);

        // Unclaimed, the duty's other kinds are still offered (the set is not a single option).
        generator.Generate(obs, 30f, obs.Tick, 4);
        Assert.True(generator.Count >= 2, $"the depth duty should offer more than one path ({generator.Count})");

        board.Claim(TeamId.Home, obs.SelfId, CarrierId, arriveTick: obs.Tick + 60, tick: obs.Tick + 1);
        FootballerObservation claimed = DepthObservation(tick: obs.Tick + 2);
        int count = generator.Generate(claimed, 30f, claimed.Tick, 4);

        Assert.Equal(1, count);
        Assert.Equal(RunKind.InBehind, generator.PathAt(0).Kind);
        Assert.Equal(obs.Tick, generator.Intent!.Value.AdoptedTick);
    }

    [Fact]
    public void TheAdvertisedConfidence_FollowsTheLiveRun()
    {
        var board = new RunAdvertisementBoard();
        var generator = new RunCandidateGenerator(Pitch, board);
        FootballerObservation clear = DepthObservation(tick: 100);
        AdoptInBehind(generator, clear);
        float atAdoption = generator.Intent!.Value.Confidence01;

        // The world changes under the frozen region: a centre-back arrives in it and a midfielder shuts the
        // lane. The geometry, the window and the adoption tick are untouched — only the belief is re-measured.
        var contested = new List<PerceivedEntity>(DepthWorld())
        {
            Entity(26, TeamId.Away, TacticalRole.CenterBack, 22f, 2f),
            Entity(27, TeamId.Away, TacticalRole.DefensiveMidfielder, 12f, 12f)
        };
        FootballerObservation crowded = RunnerObservation(
            TacticalRole.Striker, new Vector2(12f, 2f), new Vector2(14f, 2f), contested,
            new Vector2(0f, 20f), TacticalJob.DepthRun, tick: 101);

        generator.Generate(crowded, 30f, crowded.Tick, 4);

        RunIntent live = generator.Intent ?? throw new Xunit.Sdk.XunitException("the run was dropped, not re-priced");
        Assert.Equal(RunKind.InBehind, live.Kind);
        Assert.Equal(clear.Tick, live.AdoptedTick);
        Assert.True(live.Confidence01 < atAdoption,
            $"a contested run must be worth less than the clear one ({live.Confidence01:F2} vs {atAdoption:F2})");

        // And the team hears the new number: the renewal updates the promise in place.
        generator.NoteSelected(crowded, RunTags.KeyOf(RunKind.InBehind));
        Assert.True(board.TryGetAdvertisement(
            TeamId.Home, crowded.SelfId, clear.Tick + RunAdvertisementBoard.CommunicationDelayTicks,
            out RunAdvertisement advert));
        Assert.Equal(live.Confidence01, advert.Confidence01, 3);
        Assert.Equal(clear.Tick, advert.PublishedTick);
    }

    // ---------------------------------------------------------------------------------------------
    // The carrier: aim at the region, and price the promise rather than the lead
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AnAnnouncedRun_SurvivesThePassTargetBudget()
    {
        // Three eligible receivers and a budget of two. On progression and standing space the runner is
        // third, so without an advertisement he is pruned before any trajectory is planned and the promise
        // is never read at all.
        var cut = new AiRuntimeConfig { MaxPassTargets = 2 };

        var withBoard = new CandidateGenerator(Pitch, AnnounceRunFor(MateId: RunnerId), cut) { TimeOnBall = 0.8f };
        withBoard.GenerateOnBallCandidates(ThreeMateObservation(), new Vector3(Pitch.HalfLength, 0f, 0f), 34f);

        Assert.True(HasPassTo(withBoard, RunnerId),
            "the announced runner must reach the planner even when the budget is two");

        // And it is the promised form that is planned, not the fallback: the region the runner named is
        // what the carrier aims at.
        RunPassCandidate(withBoard, RunnerId);

        var withoutBoard = new CandidateGenerator(Pitch, communication: null, cut) { TimeOnBall = 0.8f };
        withoutBoard.GenerateOnBallCandidates(ThreeMateObservation(), new Vector3(Pitch.HalfLength, 0f, 0f), 34f);

        Assert.False(HasPassTo(withoutBoard, RunnerId),
            "without a promise the same runner is third on progression and is pruned: that is the A/B");
    }

    /// <summary>
    /// The A5 exit gate: "runs do not … force a return pass from a carrier". A team-mate announcing a region
    /// <b>behind</b> the ball is information about one ball the carrier may play, not a debt he owes: every
    /// forward option scores exactly the same as it does in a silent world. The A/B is the same world with
    /// and without the shout, and the forward candidates are compared key by key.
    /// </summary>
    [Fact]
    public void AnAnnouncedRunBehindTheCarrier_DoesNotChangeTheForwardOptions()
    {
        var comms = new MatchCommunicationContext();
        comms.Runs.Publish(
            TeamId.Home,
            RunnerId,
            Intent(RunKind.CheckShort, new Vector2(-8f, 6f), Regions.PublishedTick, Regions.MeetTick,
                Regions.ExpiresTick),
            Regions.PublishedTick);

        var withShout = new CandidateGenerator(Pitch, comms) { TimeOnBall = 0.8f };
        withShout.GenerateOnBallCandidates(CarrierObservation(), new Vector3(Pitch.HalfLength, 0f, 0f), 34f);

        var silent = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        silent.GenerateOnBallCandidates(CarrierObservation(), new Vector3(Pitch.HalfLength, 0f, 0f), 34f);

        List<string> shouted = ForwardOptions(withShout);
        List<string> quiet = ForwardOptions(silent);

        Assert.NotEmpty(quiet);
        Assert.Equal(quiet, shouted);
    }

    /// <summary>Every candidate that plays the ball forward, as "key → score", in a stable order.</summary>
    private static List<string> ForwardOptions(CandidateGenerator generator)
    {
        var options = new List<string>();
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            UtilityCandidate candidate = generator.Candidates[i];
            if (candidate.TargetXZ is not Vector3 target) continue;
            if (target.X <= 2f) continue;
            options.Add($"{candidate.Key.Describe()} {candidate.UtilityScore:F4}");
        }

        options.Sort(StringComparer.Ordinal);
        return options;
    }

    [Fact]
    public void ACarrier_PlaysToTheAnnouncedRegion()
    {
        MatchCommunicationContext comms = AnnounceRunFor(MateId: RunnerId);
        var generator = new CandidateGenerator(Pitch, comms) { TimeOnBall = 0.8f };
        FootballerObservation obs = CarrierObservation();

        generator.GenerateOnBallCandidates(obs, new Vector3(Pitch.HalfLength, 0f, 0f), offsideLineProjX: 34f);

        UtilityCandidate run = RunPassCandidate(generator, RunnerId);
        Vector2 expected = SimMath.SafeNormalize(Regions.MeetRegion - BallXZ, new Vector2(1f, 0f));
        Vector2 actual = new(run.Command.KickAimDirectionXZ.X, run.Command.KickAimDirectionXZ.Z);

        Assert.True(Vector2.Dot(actual, expected) > 0.999f,
            $"the pass must be played at the announced region: aimed {actual} vs region {expected}");
    }

    [Fact]
    public void WithoutAnAnnouncement_TheCarrierStillLeadsTheRunnersVelocity()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        FootballerObservation obs = CarrierObservation();

        generator.GenerateOnBallCandidates(obs, new Vector3(Pitch.HalfLength, 0f, 0f), offsideLineProjX: 34f);

        UtilityCandidate run = RunPassCandidate(generator, RunnerId);
        Vector2 actual = new(run.Command.KickAimDirectionXZ.X, run.Command.KickAimDirectionXZ.Z);
        Vector2 region = SimMath.SafeNormalize(Regions.MeetRegion - BallXZ, new Vector2(1f, 0f));

        // The A/B: the same world, the same receiver, no promise — the ball goes where his velocity plus
        // the through lead points, which is a different line from the region.
        Assert.True(Vector2.Dot(actual, region) < 0.99f,
            $"an unpromised ToRun must follow the velocity lead, not the region (aimed {actual})");
    }

    [Fact]
    public void APromisedArrival_PaysNoLeadCharge()
    {
        var planner = new PassPlanner(Pitch);
        PassPlan plan = planner.PlanGroundPassToRegion(BallXZ, Regions.MeetRegion, arriveInSeconds: 1.0f);
        Assert.True(plan.Feasible);

        FootballerObservation obs = CarrierObservation();
        PerceivedEntity mate = Runner();
        var model = new PassSuccessModel(planner);
        var promise = new PassArrivalPromise(Regions.MeetRegion, plan.FlightTimeSeconds, 5f, 0.8f);

        PassSuccessTerms promised = model.Estimate(obs, mate, plan, 1f, promise);
        PassSuccessTerms unpromised = model.Estimate(obs, mate, plan, 1f);

        Assert.True(promised.PromisedArrival);
        Assert.Equal(0f, promised.LeadCharge01, 3);
        Assert.True(unpromised.LeadCharge01 > 0f);

        // One risk owner: the promise is priced through confidence × timing, not through the lead as well.
        Assert.True(promised.CompletionProbability01 > unpromised.CompletionProbability01,
            $"promised {promised.CompletionProbability01:F2} should beat unpromised " +
            $"{unpromised.CompletionProbability01:F2}");
    }

    [Fact]
    public void AMistimedPromise_IsChargedForTheMistiming()
    {
        var planner = new PassPlanner(Pitch);
        PassPlan plan = planner.PlanGroundPassToRegion(BallXZ, Regions.MeetRegion, arriveInSeconds: 1.0f);
        Assert.True(plan.Feasible);

        FootballerObservation obs = CarrierObservation();
        PerceivedEntity mate = Runner();
        var model = new PassSuccessModel(planner);

        var onTime = new PassArrivalPromise(Regions.MeetRegion, plan.FlightTimeSeconds, 5f, 0.8f);
        var oneSecondLate = new PassArrivalPromise(Regions.MeetRegion, plan.FlightTimeSeconds + 1f, 5f, 0.8f);

        float onTimeCompletion = model.Estimate(obs, mate, plan, 1f, onTime).CompletionProbability01;
        float lateCompletion = model.Estimate(obs, mate, plan, 1f, oneSecondLate).CompletionProbability01;

        Assert.True(lateCompletion < onTimeCompletion,
            $"a ball a second off the promised meeting is worth less ({lateCompletion:F2} vs {onTimeCompletion:F2})");
    }

    [Fact]
    public void ALowerConfidencePromise_IsWorthLess()
    {
        var planner = new PassPlanner(Pitch);
        PassPlan plan = planner.PlanGroundPassToRegion(BallXZ, Regions.MeetRegion, arriveInSeconds: 1.0f);
        Assert.True(plan.Feasible);

        FootballerObservation obs = CarrierObservation();
        PerceivedEntity mate = Runner();
        var model = new PassSuccessModel(planner);

        float sure = model.Estimate(obs, mate, plan, 1f,
            new PassArrivalPromise(Regions.MeetRegion, plan.FlightTimeSeconds, 5f, 0.9f)).CompletionProbability01;
        float unsure = model.Estimate(obs, mate, plan, 1f,
            new PassArrivalPromise(Regions.MeetRegion, plan.FlightTimeSeconds, 5f, 0.3f)).CompletionProbability01;

        Assert.True(unsure < sure);
    }

    [Fact]
    public void ChoosingTheRunPass_ClaimsTheRunnersRegion()
    {
        MatchCommunicationContext comms = AnnounceRunFor(MateId: RunnerId);
        var generator = new CandidateGenerator(Pitch, comms) { TimeOnBall = 0.8f };
        FootballerObservation obs = CarrierObservation();

        generator.GenerateOnBallCandidates(obs, new Vector3(Pitch.HalfLength, 0f, 0f), offsideLineProjX: 34f);
        UtilityCandidate run = RunPassCandidate(generator, RunnerId);
        generator.NoteDecision(obs, run.Key);

        Assert.True(comms.Runs.TryGetClaim(TeamId.Home, RunnerId, obs.Tick, out RunClaim claim));
        Assert.Equal(CarrierId, claim.CarrierId);
        Assert.Equal(Regions.MeetTick, claim.ArriveTick);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixture-level: the high-line run is really heard by the team
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheHighLineRun_IsAnnouncedToTheTeam()
    {
        var engine = new MatchSimulationEngine(ScenarioLibrary.HighLineRun());
        int strikerId = ScenarioLibrary.HighLineStriker;
        FootballerState striker = engine.Players.First(p => p.Id == strikerId);

        RunAdvertisement? heard = null;
        int heardAt = 0;

        for (int step = 1; step <= 240 && heard is null; step++)
        {
            engine.StepTick();
            int tick = engine.CurrentTick;
            RunAdvertisementBoard board = engine.Communication.Runs;
            int visible = board.VisibleCount(TeamId.Home, tick);
            for (int i = 0; i < visible; i++)
            {
                if (!board.TryGetVisibleAt(TeamId.Home, i, tick, out RunAdvertisement advert)) continue;
                if (advert.PlayerId != strikerId) continue;
                heard = advert;
                heardAt = tick;
                break;
            }
        }

        Assert.True(heard.HasValue, "the striker's run never reached his team-mates");
        Assert.True(engine.Communication.Runs.PublishedCount > 0);
        RunAdvertisement shouted = heard!.Value;

        Assert.Equal(RunKind.InBehind, shouted.Kind);
        Assert.True(shouted.VisibleTick <= heardAt);
        Assert.True(shouted.MeetTick > shouted.PublishedTick, "a run is announced before it is met");
        Assert.True(shouted.MeetXZ.X > striker.Position.X + 1f,
            $"the announced region {shouted.MeetXZ.X:F1} must be ahead of the runner {striker.Position.X:F1}");
        Assert.InRange(shouted.Confidence01, 0f, 1f);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static readonly Vector2 BallXZ = new(0.4f, 8f);

    /// <summary>The announced region the carrier tests play to, and when the runner plans to be there.</summary>
    private static class Regions
    {
        public static readonly Vector2 MeetRegion = new(20f, 20f);
        public const int MeetTick = 560;
        public const int ExpiresTick = 620;
        public const int PublishedTick = 500 - RunAdvertisementBoard.CommunicationDelayTicks;
        public const float Confidence01 = 0.8f;
        public const float Radius = 5f;
    }

    private static PerceivedEntity Entity(
        int id,
        TeamId team,
        TacticalRole role,
        float x,
        float z,
        Vector3? velocity = null) =>
        new(id, team, role, new Vector3(x, 0f, z), velocity ?? Vector3.Zero, 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };

    private static PerceivedEntity Runner() => Entity(
        RunnerId, TeamId.Home, TacticalRole.CentralMidfielder, 12f, 12f, new Vector3(4f, 0f, 0f));

    private static RunIntent Intent(
        RunKind kind,
        Vector2 meet,
        int publishedTick,
        int meetTick,
        int expiresTick,
        float confidence = Regions.Confidence01) =>
        new(
            new RunPath(
                kind,
                meet - new Vector2(4f, 0f),
                meet,
                Regions.Radius,
                publishedTick,
                meetTick,
                expiresTick,
                publishedTick,
                meet - new Vector2(4f, 0f),
                confidence),
            TacticalJob.DepthRun,
            publishedTick);

    /// <summary>A carrier in possession at the halfway line with one team-mate running behind him.</summary>
    private static FootballerObservation CarrierObservation(params PerceivedEntity[] extra)
    {
        var visible = new List<PerceivedEntity> { Runner() };
        visible.AddRange(extra);

        return new FootballerObservation
        {
            SelfId = CarrierId,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = new Vector3(0f, 0f, 8f),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(BallXZ.X, 0.11f, BallXZ.Y),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = CarrierId,
            BallLastTouchedByTeam = TeamId.Home,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = CarrierId,
            TacticalAnchorPosition = new Vector3(0f, 0f, 8f),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 20f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = 500,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.CentralMidfielder),
            SelfAttributes = new PlayerAttributes()
        };
    }

    /// <summary>A team communication context in which player 9 has already announced the region.</summary>
    private static MatchCommunicationContext AnnounceRunFor(int MateId)
    {
        var comms = new MatchCommunicationContext();
        comms.Runs.Publish(TeamId.Home, MateId,
            Intent(RunKind.CheckShort, Regions.MeetRegion, Regions.PublishedTick, Regions.MeetTick,
                Regions.ExpiresTick),
            Regions.PublishedTick);
        return comms;
    }

    /// <summary>
    /// The carrier with three eligible receivers. On progression alone #10 (18, 4) and #11 (16, -6) outrank
    /// the runner at (12, 12), so a pass-target budget of two prunes him unless he is announced.
    /// </summary>
    private static FootballerObservation ThreeMateObservation() => CarrierObservation(
        Entity(10, TeamId.Home, TacticalRole.Striker, 18f, 4f),
        Entity(11, TeamId.Home, TacticalRole.Winger, 16f, -6f));

    private static bool HasPassTo(CandidateGenerator generator, int receiverId)
    {
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            if (generator.Candidates[i].Key.Kind == CandidateKind.Pass &&
                generator.Candidates[i].Key.TargetPlayerId == receiverId)
            {
                return true;
            }
        }

        return false;
    }

    private static UtilityCandidate RunPassCandidate(CandidateGenerator generator, int receiverId)
    {
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            UtilityCandidate candidate = generator.Candidates[i];
            if (candidate.Key.Kind == CandidateKind.Pass &&
                candidate.Key.TargetPlayerId == receiverId &&
                candidate.Key.TargetTag == CandidateGenerator.RunPassTag)
            {
                return candidate;
            }
        }

        throw new Xunit.Sdk.XunitException($"no ToRun pass to #{receiverId} was offered");
    }

    // ---- runner fixtures ------------------------------------------------------------------------

    private static List<PerceivedEntity> DepthWorld() => new()
    {
        Entity(CarrierId, TeamId.Home, TacticalRole.CentralMidfielder, 0f, 20f),
        Entity(25, TeamId.Away, TacticalRole.CenterBack, 24f, 2f)
    };

    private static FootballerObservation DepthObservation(int tick = 100) =>
        RunnerObservation(
            TacticalRole.Striker, new Vector2(12f, 2f), new Vector2(14f, 2f), DepthWorld(),
            new Vector2(0f, 20f), TacticalJob.DepthRun, tick);

    /// <summary>The final-third fragment from <c>ALateBoxArrival</c>: a depth duty with a box to arrive in.</summary>
    private static FootballerObservation BoxObservation(int tick = 100)
    {
        var visible = new List<PerceivedEntity>
        {
            Entity(CarrierId, TeamId.Home, TacticalRole.Winger, 30f, 24f),
            Entity(25, TeamId.Away, TacticalRole.CenterBack, 44f, 4f)
        };

        return RunnerObservation(
            TacticalRole.CentralMidfielder, new Vector2(20f, 6f), new Vector2(20f, 6f), visible,
            new Vector2(30f, 24f), TacticalJob.DepthRun, tick);
    }

    private static FootballerObservation RunnerObservation(
        TacticalRole role,
        Vector2 selfXZ,
        Vector2 dutyAnchorXZ,
        IReadOnlyList<PerceivedEntity> visible,
        Vector2 ballXZ,
        TacticalJob duty,
        int tick) => new()
    {
        SelfId = RunnerId,
        SelfTeam = TeamId.Home,
        SelfRole = role,
        SelfPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = false,
        ControlQuality01 = 0f,
        PerceivedBallPosition = new Vector3(ballXZ.X, 0.11f, ballXZ.Y),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = CarrierId,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = CarrierId,
        TacticalAnchorPosition = new Vector3(dutyAnchorXZ.X, 0f, dutyAnchorXZ.Y),
        TacticalJob = duty,
        TacticalJobAnchor = new Vector3(dutyAnchorXZ.X, 0f, dutyAnchorXZ.Y),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 0f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = tick,
        VisiblePlayers = visible,
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(role),
        SelfAttributes = new PlayerAttributes(),
        TeamSecurity = new RestDefence(3, 1f, 0.2f, 0.2f, 0.1f, 0.7f)
    };

    private static RunPath AdoptInBehind(RunCandidateGenerator generator, in FootballerObservation obs)
    {
        Assert.True(generator.Generate(obs, 30f, obs.Tick, 4) > 0);
        RunPath chosen = PathOfKind(generator, RunKind.InBehind);
        generator.NoteSelected(obs, RunTags.KeyOf(RunKind.InBehind));
        Assert.NotNull(generator.Intent);
        return chosen;
    }

    private static RunPath PathOfKind(RunCandidateGenerator generator, RunKind kind)
    {
        for (int i = 0; i < generator.Count; i++)
        {
            if (generator.PathAt(i).Kind == kind) return generator.PathAt(i);
        }

        throw new Xunit.Sdk.XunitException($"no {kind} path in the set");
    }
}
