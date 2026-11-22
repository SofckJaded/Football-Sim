using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// AIB-3 (owner report 2026-11-22, section 3): the AI was afraid of the enemy box. The `ai54-11v11`
/// capture executed six crosses in ninety minutes because four structural limits kept it out:
///
/// <list type="number">
/// <item>every attacking duty was capped at one player, so a 4-5 man attack was one box runner plus shape;</item>
/// <item>the formation anchor moved the side forward by at most 11.5 m, so with the ball on the byline a
///   central midfielder still stood 34 m from goal and could not even be offered a box run inside
///   <c>MaxAssignmentTravel</c>;</item>
/// <item>only <see cref="TacticalJob.DepthRun"/> could generate a box run -- the <see cref="TacticalJob.ThirdPlayerOption"/>
///   never advertised a late arrival, and a <see cref="TacticalJob.FarSideWidth"/> blind-side arrival always lost
///   to holding width on travel cost;</item>
/// <item>a player already in the box failed <c>RunCandidateGenerator.MinRunGainMeters</c>, so a striker on the
///   penalty spot could never advertise the arrival a cross or cutback is aimed at.</item>
/// </list>
///
/// Every fixture here is synthetic and every assertion is about the value the fix changes. Each test comment
/// states the pre-fix arithmetic it falsifies.
/// </summary>
public sealed class AibBoxSupportTests
{
    private static readonly PitchDimensions Pitch = new();

    /// <summary>Rest defence of a side that can afford two forward commitments (A3.6: Security01 = 1).</summary>
    private static readonly RestDefence Secure = new(3, 1f, 0.2f, 0.2f, 0.1f, 0.8f);

    // =============================================================================================
    // AIB-3.2 -- the final-third formation anchor
    // =============================================================================================

    /// <summary>
    /// Pre-fix the anchor was <c>baseNormX * 40.95 * compression + ballNormX * 11.5 + lineShift +
    /// rolePush * 0.65</c>. With the ball on the byline (ballNormX = 0.962, PossessionControl) that put a
    /// central midfielder at <b>19.6 m</b> (34 m from goal), a far-side winger at <b>33.6 m</b> and the striker
    /// at <b>39.0 m</b> -- the striker merely level with the box edge and the midfield 30 m behind the ball.
    /// The fix adds a final-third progression (13 m to a CM, 8 m to a winger, 5 m to a striker at full
    /// progress), so the same picture now stands at <b>31.1 / 36.4 / 40.9 m</b>; those are the assertions below.
    /// </summary>
    [Fact]
    public void AIB3_AHighBall_PushesTheAttackingBlockIntoAndAroundTheBox()
    {
        List<FootballerState> players = AnchorPlayers();
        var ball = new BallState
        {
            Position = new Vector3(Pitch.HalfLength - 2f, 0.11f, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 8
        };

        var tactics = new TeamTacticalCoordinator(
            Pitch, TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack);
        tactics.AssignFormations(players, teamSize: 11, fromPositions: true, homeAttackDirX: 1f);
        tactics.SeedPossession(TeamId.Home, homeAttackDirX: 1f, ballX: ball.Position.X);
        tactics.UpdateTeamAnchors(players, ball, homeAttackDirX: 1f);

        FootballerState centralMidfielder = players.Find(p => p.Id == 7)!;
        FootballerState farWinger = players.Find(p => p.Id == 10)!;
        FootballerState striker = players.Find(p => p.Id == 11)!;
        float boxEdgeProjX = Pitch.HalfLength - Pitch.PenaltyAreaLength;   // 36 m: the penalty-area line

        Assert.InRange(centralMidfielder.TacticalAnchor.X, 28f, 42f);
        Assert.True(farWinger.TacticalAnchor.X >= 33f,
            $"the far-side winger is at {farWinger.TacticalAnchor.X:F1} m, not at the box edge");
        Assert.True(farWinger.TacticalAnchor.X <= 44f,
            $"the winger is past the box at {farWinger.TacticalAnchor.X:F1} m");
        Assert.True(striker.TacticalAnchor.X > boxEdgeProjX,
            $"the striker is at {striker.TacticalAnchor.X:F1} m, still level with the box edge rather than in it");

        // An attacking width convention (AUD-01) is untouched: the winger is still wide.
        Assert.True(MathF.Abs(farWinger.TacticalAnchor.Z) > 15f,
            $"the winger collapsed inside ({farWinger.TacticalAnchor.Z:F1} m)");

        // And the defensive blocks are the pre-fix anchors: no final-third push without the ball.
        float attackingCm = centralMidfielder.TacticalAnchor.X;
        tactics.SeedPossession(TeamId.Away, homeAttackDirX: 1f, ballX: ball.Position.X);
        tactics.UpdateTeamAnchors(players, ball, homeAttackDirX: 1f);

        Assert.True(centralMidfielder.TacticalAnchor.X < attackingCm - 12f,
            $"with the ball lost the midfield station must drop back: {centralMidfielder.TacticalAnchor.X:F1} m " +
            $"vs {attackingCm:F1} m in possession");
        Assert.True(centralMidfielder.TacticalAnchor.X < 15f,
            $"the defending central midfielder is at {centralMidfielder.TacticalAnchor.X:F1} m");
    }

    /// <summary>
    /// AIB-3: the progression changes where the Shape players stand, so the structural invariant the
    /// off-ball-target suite measures has to survive it -- no two outfield formation anchors closer than 5 m.
    /// On the `box-cross` layout this failed with the ball in the home half: the central midfielder and the
    /// striker slots sit <b>4.88 m</b> apart once the defending width compression applies (7.4 m with the
    /// attacking width, so the progression's duty reshuffle exposed the compacted pair at
    /// `box-cross t=198`). The bounded separation pass now holds every pair at 5.4 m or more.
    /// </summary>
    [Fact]
    public void AIB3_ACompactShape_StillKeepsOutfieldAnchorsApart()
    {
        List<FootballerState> players = BoxCrossHomeShape();
        var tactics = new TeamTacticalCoordinator(
            Pitch, TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack);
        tactics.AssignFormations(players, teamSize: 6, fromPositions: true, homeAttackDirX: 1f);

        // The picture the suite reported: home defending, the away ball at x = 43, z = 14.
        var defendingBall = new BallState
        {
            Position = new Vector3(43f, 0.11f, 14f),
            LastTouchedByTeam = TeamId.Away,
            LastTouchedByPlayerId = 201
        };
        tactics.SeedPossession(TeamId.Away, homeAttackDirX: 1f, ballX: defendingBall.Position.X);
        tactics.UpdateTeamAnchors(players, defendingBall, homeAttackDirX: 1f);
        AssertAnchorsApart(players);
        AssertPairApart(players, 108, 109);

        // And with the ball the final-third progression must not compress the shape either.
        var attackingBall = new BallState
        {
            Position = new Vector3(30.6f, 0.11f, 26f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 107
        };
        tactics.SeedPossession(TeamId.Home, homeAttackDirX: 1f, ballX: attackingBall.Position.X);
        tactics.UpdateTeamAnchors(players, attackingBall, homeAttackDirX: 1f);
        AssertAnchorsApart(players);
    }

    // =============================================================================================
    // AIB-3.1 -- context-aware duty caps: two box runners in the final third only
    // =============================================================================================

    /// <summary>
    /// Pre-fix <c>MaxPerJob(DepthRun)</c> was a flat 1, so a final-third attack had exactly one box arrival and
    /// the far post was never attacked. The fixture is secure (Security01 = 1, so the rest-defence allowance is
    /// 2), the carrier is at 30 m and the believed line at 42 m, which leaves the box target inside the onside
    /// budget: post-fix two DepthRun duties are handed out, one to the striker (central, so the far post by the
    /// single-runner rule) and one to the free central midfielder (the opposite post). Both targets sit in the
    /// box, at least <see cref="TeamJobCoordinator.BoxRunPostLaneMeters"/> / 2 apart laterally, and the cover
    /// duty is still handed out -- the extra runner spends the second forward allowance, it does not strip the
    /// rest defence.
    /// </summary>
    [Fact]
    public void AIB3_AHighBall_CommitsTwoBoxRunnersToOppositePosts()
    {
        (List<FootballerState> players, BallState ball) = FinalThirdFixture(carrierX: 30f);
        TeamJobCoordinator jobs = RunJobs(players, ball);

        RestDefence security = jobs.SecurityOf(TeamId.Home);
        Assert.Equal(2, security.ForwardAllowance);

        Assert.Equal(2, HomeDutyCount(jobs, TacticalJob.DepthRun));

        var runners = new List<TeamJobAssignment>();
        foreach (KeyValuePair<int, TeamJobAssignment> pair in jobs.Assignments)
        {
            if (pair.Value.Job == TacticalJob.DepthRun) runners.Add(pair.Value);
        }

        Assert.Equal(2, runners.Count);
        foreach (TeamJobAssignment runner in runners)
        {
            Assert.True(runner.Anchor.X >= 34f, $"a box runner is at {runner.Anchor.X:F1} m, not in the box");
            Assert.True(runner.Anchor.X <= 42f + 0.01f,
                $"a box runner is beyond the believed line at {runner.Anchor.X:F1} m");
        }

        float lateralSeparation = MathF.Abs(runners[0].Anchor.Z - runners[1].Anchor.Z);
        Assert.True(lateralSeparation >= 6f,
            $"the two runners attack the same region ({lateralSeparation:F1} m apart)");

        // Cover integrity: the final-third pair is paid for by the rest-defence allowance, not by removing
        // the cover behind the ball, and the defensive line/markers are not in the possession phase at all.
        Assert.Equal(1, HomeDutyCount(jobs, TacticalJob.DefensiveCover));
        Assert.Equal(0, HomeDutyCount(jobs, TacticalJob.Marker));
        Assert.Equal(0, HomeDutyCount(jobs, TacticalJob.DefensiveLine));
    }

    /// <summary>
    /// The cap is context, not a global raise: the same players with the ball in the middle third still get one
    /// DepthRun. Pre-fix this was also one, so the assertion pins that the fix did not leak the extra runner
    /// into the build-up, where a second commitment would cost a rest-defence place for no box presence.
    /// </summary>
    [Fact]
    public void AIB3_AMiddleThirdBall_StillAllowsOnlyOneDepthRunner()
    {
        (List<FootballerState> players, BallState ball) = FinalThirdFixture(carrierX: 10f);
        TeamJobCoordinator jobs = RunJobs(players, ball);

        // The side can afford two commitments here, so the single runner really is the context-aware cap
        // and not an artefact of the rest-defence gate.
        Assert.Equal(2, jobs.SecurityOf(TeamId.Home).ForwardAllowance);
        Assert.Equal(1, HomeDutyCount(jobs, TacticalJob.DepthRun));
    }

    /// <summary>
    /// The rest-defence veto still wins: the same final-third fixture with the back line pushed up and two
    /// central opponents loose behind the ball scores Security01 well below
    /// <see cref="TransitionSecurityModel.MinSecurityForForwardRun"/>, so the allowance is 0 and neither box
    /// runner goes. Pre-fix a single runner was also withdrawn here, which is the behaviour this test keeps.
    /// </summary>
    [Fact]
    public void AIB3_AnExposedRestDefence_StillWithdrawsTheBoxRunners()
    {
        List<FootballerState> players = ExposedFinalThirdShape();
        var ball = new BallState
        {
            Position = new Vector3(30f, 0.11f, 12f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 8
        };

        var jobs = new TeamJobCoordinator(Pitch);
        jobs.Update(
            players, ball,
            new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, 8, null),
            possessingTeam: TeamId.Home,
            homeResponderId: null, awayResponderId: null,
            homeDirX: 1f, tick: 0,
            TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
            TeamBelief.FromTruth(TeamId.Home, players), TeamBelief.FromTruth(TeamId.Away, players),
            new TeamFormation(TeamId.Home, Pitch), new TeamFormation(TeamId.Away, Pitch));

        Assert.Equal(0, jobs.SecurityOf(TeamId.Home).ForwardAllowance);
        Assert.Equal(0, HomeDutyCount(jobs, TacticalJob.DepthRun));
        foreach (KeyValuePair<int, TeamJobAssignment> pair in jobs.Assignments)
        {
            Assert.NotEqual(TacticalJob.DepthRun, pair.Value.Job);
            Assert.NotEqual(TacticalJob.Overlap, pair.Value.Job);
        }
    }

    /// <summary>The pure split rule behind the second runner: opposite sign, fixed post lane.</summary>
    [Fact]
    public void AIB3_TheSecondBoxRunnerAttacksTheOppositePost()
    {
        Assert.Equal(TeamJobCoordinator.BoxRunPostLaneMeters,
            TeamJobCoordinator.OppositeBoxPostLaneMeters(-TeamJobCoordinator.BoxRunPostLaneMeters));
        Assert.Equal(-TeamJobCoordinator.BoxRunPostLaneMeters,
            TeamJobCoordinator.OppositeBoxPostLaneMeters(TeamJobCoordinator.BoxRunPostLaneMeters));
        // A lane exactly on the centre line counts as the positive side, so the pair goes to the negative post.
        Assert.Equal(-TeamJobCoordinator.BoxRunPostLaneMeters, TeamJobCoordinator.OppositeBoxPostLaneMeters(0f));
        Assert.Equal(-TeamJobCoordinator.BoxRunPostLaneMeters,
            TeamJobCoordinator.OppositeBoxPostLaneMeters(22f));
    }

    // =============================================================================================
    // AIB-3.4 -- an in-box player can advertise a live arrival
    // =============================================================================================

    /// <summary>
    /// Pre-fix <c>TryBuild</c> rejected both box kinds with <c>runProj &lt;= selfProj + MinRunGainMeters</c>:
    /// for the striker at 39.5 m and the box region at 41.5 m that is <c>41.5 &lt;= 42.0</c> a*' <b>false</b>, so
    /// no LateBoxArrival or BlindSideArrival was offered and the cross/cutback consumers -- which only target an
    /// announced box arrival -- could not pick an open man in the box at all. Post-fix he offers a live presence
    /// at his own depth (2 m of forward reach, then the onside clamp), and the advertised region satisfies the
    /// existing consumer gates: projection &gt;= 32 m for a cross and 3 m behind a byline carrier for a cutback.
    /// </summary>
    [Fact]
    public void AIB3_AStrikerAlreadyInTheBox_AdvertisesACrossableArrival()
    {
        const int tick = 300;
        Vector2 carrierXZ = new(50f, 16f);      // byline carrier, wide enough for a cross and a cutback
        var striker = new Vector2(39.5f, 0f);   // on the penalty spot, inside the 16.5 m box (edge at 36 m)

        List<PerceivedEntity> visible = new()
        {
            Entity(8, TeamId.Home, TacticalRole.Winger, carrierXZ.X, carrierXZ.Y),
            Entity(25, TeamId.Away, TacticalRole.CenterBack, 44f, 8f)
        };

        FootballerObservation obs = RunnerObservation(
            TacticalRole.Striker, striker, striker, carrierXZ, visible, TacticalJob.DepthRun, tick);
        var generator = new RunCandidateGenerator(Pitch);

        int count = generator.Generate(obs, offsideLineProjX: 46f, tick, maxPaths: 4);
        Assert.True(count > 0, "the in-box striker was offered no path at all");

        // The pre-fix guard rejected the ordinary box region: 41.5 m (11 m from the goal line) is not
        // 2.5 m beyond the striker at 39.5 m, so LateBoxArrival/BlindSideArrival both returned false. The
        // next two assertions pin that arithmetic, then the paths that now exist under it.
        float ordinaryBoxProj = Pitch.HalfLength - RunCandidateGenerator.BoxMeetFromGoalMeters;
        Assert.True(ordinaryBoxProj <= striker.X + RunCandidateGenerator.MinRunGainMeters,
            "the fixture does not reproduce the pre-fix rejection, so it proves nothing");

        RunPath lateBox = PathOfKind(generator, RunKind.LateBoxArrival);
        RunPath blindSide = PathOfKind(generator, RunKind.BlindSideArrival);

        // The consumer floors: CandidateGenerator.CrossBoxProjX = 32 m and CutbackMinRegionProjX = 24 m,
        // and a cutback region must be at least CutbackMinBehindMeters = 3 m behind the carrier. The
        // delivered region also has to be legal (inside the offside budget).
        float carrierProj = carrierXZ.X * obs.AttackingDirectionX;
        Assert.True(lateBox.MeetXZ.X >= 32f, $"the announced arrival is at {lateBox.MeetXZ.X:F1} m, behind the cross floor");
        Assert.True(lateBox.MeetXZ.X <= carrierProj - 3f,
            $"the announced arrival is not behind the byline carrier: {lateBox.MeetXZ.X:F1} m vs {carrierProj:F1} m");
        Assert.True(lateBox.MeetXZ.X <= 46f - RunCandidateGenerator.OnsideMarginMeters + 0.01f,
            $"the announced arrival {lateBox.MeetXZ.X:F1} m is beyond the offside budget");
        Assert.True(MathF.Abs(lateBox.MeetXZ.Y) >= 1f, "a box presence must name a post, not the player's feet");

        // It is a *future* meeting, long enough to survive the communication delay and to be planned to.
        Assert.True(lateBox.MeetTick > tick + RunAdvertisementBoard.CommunicationDelayTicks,
            $"the presence meets at tick {lateBox.MeetTick}, inside the communication delay from {tick}");

        // The blind-side variant is offered from the same presence, and the two name different posts.
        Assert.True(blindSide.MeetXZ.X >= 32f);
        Assert.True(blindSide.MeetTick > tick + RunAdvertisementBoard.CommunicationDelayTicks);
        Assert.NotEqual(MathF.Sign(lateBox.MeetXZ.Y), MathF.Sign(blindSide.MeetXZ.Y));
    }

    /// <summary>
    /// The same presence against a deep block: the believed line is at the player's own depth, so the forward
    /// box region would be offside. Pre-fix this also returned no path. Post-fix the player peels back to the
    /// onside budget and advertises that -- a legal box target the carrier can still cross to -- which is the
    /// cutback case the delivery model documents: no forward run can get inside the box, but the man already
    /// there can hold a receivable region.
    /// </summary>
    [Fact]
    public void AIB3_ADeepBlock_PeelsThePresenceBackToTheOnsideBudget()
    {
        const int tick = 300;
        Vector2 carrierXZ = new(50f, 16f);
        var striker = new Vector2(40f, 4f);

        List<PerceivedEntity> visible = new()
        {
            Entity(8, TeamId.Home, TacticalRole.Winger, carrierXZ.X, carrierXZ.Y),
            Entity(25, TeamId.Away, TacticalRole.CenterBack, 40f, -6f)
        };

        FootballerObservation obs = RunnerObservation(
            TacticalRole.Striker, striker, striker, carrierXZ, visible, TacticalJob.DepthRun, tick);
        var generator = new RunCandidateGenerator(Pitch);

        Assert.True(generator.Generate(obs, offsideLineProjX: 40f, tick, maxPaths: 4) > 0);
        RunPath lateBox = PathOfKind(generator, RunKind.LateBoxArrival);

        Assert.InRange(lateBox.MeetXZ.X,
            Pitch.HalfLength - RunCandidateGenerator.BoxMeetFromGoalMeters - 4f,   // still in the box
            40f - RunCandidateGenerator.OnsideMarginMeters + 0.01f);
        Assert.True(lateBox.MeetXZ.X >= 32f, "a peeled presence must stay inside the cross band");
    }

    // =============================================================================================
    // AIB-3.3 -- the third-player option and the far-side blind-side run
    // =============================================================================================

    /// <summary>
    /// Pre-fix <c>KindsFor(ThirdPlayerOption)</c> was check-short, underlap, in-behind, decoy: the one duty that
    /// means "the #8 arriving late" could not offer the arrival it is named for. Post-fix the late box arrival
    /// is in the list and is actually offered from a final-third picture; a build-up picture still offers none
    /// (the <c>TryBuild</c> final-third gate), so this is a context, not a new always-on run.
    /// </summary>
    [Fact]
    public void AIB3_TheThirdPlayerOption_OffersTheLateBoxArrival()
    {
        Span<RunKind> kinds = stackalloc RunKind[RunCandidateGenerator.MaxKindsPerDuty];
        int kindCount = RunCandidateGenerator.KindsFor(TacticalJob.ThirdPlayerOption, kinds);
        var listed = new List<RunKind>(kindCount);
        for (int i = 0; i < kindCount; i++) listed.Add(kinds[i]);
        Assert.Contains(RunKind.LateBoxArrival, listed);
        Assert.Contains(RunKind.Decoy, listed);

        // Final third: carrier at 32 m, the #8 at 22 m, line at 46 m.
        Vector2 self = new(22f, 6f);
        Vector2 carrier = new(32f, 20f);
        List<PerceivedEntity> visible = new()
        {
            Entity(8, TeamId.Home, TacticalRole.Winger, carrier.X, carrier.Y),
            Entity(25, TeamId.Away, TacticalRole.CenterBack, 46f, 0f)
        };
        FootballerObservation finalThird = RunnerObservation(
            TacticalRole.CentralMidfielder, self, new Vector2(20f, 4f), carrier, visible,
            TacticalJob.ThirdPlayerOption);
        var generator = new RunCandidateGenerator(Pitch);
        Assert.True(generator.Generate(finalThird, 46f, finalThird.Tick, maxPaths: 4) > 0);
        _ = PathOfKind(generator, RunKind.LateBoxArrival);

        // Own half: the same duty offers no box arrival at all.
        Vector2 deepSelf = new(-10f, 6f);
        Vector2 deepCarrier = new(-20f, 10f);
        List<PerceivedEntity> deepVisible = new()
        {
            Entity(8, TeamId.Home, TacticalRole.Winger, deepCarrier.X, deepCarrier.Y),
            Entity(25, TeamId.Away, TacticalRole.CenterBack, 30f, 0f)
        };
        FootballerObservation ownHalf = RunnerObservation(
            TacticalRole.CentralMidfielder, deepSelf, new Vector2(-12f, 6f), deepCarrier, deepVisible,
            TacticalJob.ThirdPlayerOption);
        var deepGenerator = new RunCandidateGenerator(Pitch);
        deepGenerator.Generate(ownHalf, 30f, ownHalf.Tick, maxPaths: 4);
        for (int i = 0; i < deepGenerator.Count; i++)
            Assert.NotEqual(RunKind.LateBoxArrival, deepGenerator.PathAt(i).Kind);
    }

    /// <summary>
    /// Pre-fix the far-side winger's BlindSideArrival paid the full <c>RunScoring.TravelWeight</c> on its 26.5 m
    /// diagonal into the box: <c>-0.35 * 26.5 / 40 = -0.232</c>, while HoldWidth pays <c>-0.064</c> for a 7.3 m
    /// shuffle. Measured in this fixture the blind-side path scored <b>0.248</b> against hold-width's
    /// <b>0.358</b>, so the duty re-chose "hold width" every tick and the far post was never announced. The
    /// AIB-3 final-third weight is 0.05, a rebate of <c>+0.199</c>: <b>0.447</b> against 0.358, so the run can
    /// win -- and the test also pins the rebate to the formula, not to a hand-tuned score.
    /// </summary>
    [Fact]
    public void AIB3_TheFarSideBlindSideRun_CanOutbidHoldingWidth()
    {
        var ballXZ = new Vector2(40f, -16f);
        var winger = new Vector2(22f, -24f);
        List<PerceivedEntity> visible = new()
        {
            Entity(8, TeamId.Home, TacticalRole.Winger, ballXZ.X, ballXZ.Y),
            SlowEntity(25, 44f, 20f)
        };
        FootballerObservation obs = RunnerObservation(
            TacticalRole.Winger, winger, winger, ballXZ, visible, TacticalJob.FarSideWidth);
        var generator = new RunCandidateGenerator(Pitch);

        Assert.True(generator.Generate(obs, offsideLineProjX: 46f, obs.Tick, maxPaths: 4) > 0);
        RunScoreTerms holdWidth = TermsOfKind(generator, RunKind.HoldWidth);
        RunPath blindSidePath = PathOfKind(generator, RunKind.BlindSideArrival);
        RunScoreTerms blindSide = TermsOfKind(generator, RunKind.BlindSideArrival);

        Assert.True(blindSide.Score > holdWidth.Score,
            $"the far-post run ({blindSide.Score:F3}) has to be able to beat holding width ({holdWidth.Score:F3})");

        // The rebate is exactly the difference between the shared travel weight and the final-third weight.
        float sharedScore = RunScoring.Score(blindSidePath, obs, ballXZ, obs.TeamSecurity).Score;
        float expectedRebate = (RunScoring.TravelWeight - RunCandidateGenerator.BlindSideFinalThirdTravelWeight)
                               * MathF.Min(blindSide.TravelMeters, 40f) / 40f;
        Assert.Equal(sharedScore + expectedRebate, blindSide.Score, precision: 4);

        // And the fixture really is one the old pricing rejected: at the shared travel weight the far-post run
        // falls below holding width, which is why the duty never advertised it.
        Assert.True(blindSide.Score - expectedRebate < holdWidth.Score,
            $"the pre-fix pricing ({sharedScore:F3}) has to lose to holding width ({holdWidth.Score:F3}), " +
            "or the fixture does not prove the fix");
    }

    /// <summary>
    /// The rebate is a final-third rule only. With the ball short of <see cref="RunCandidateGenerator.FinalThirdProjX"/>
    /// the generator's score for a blind-side arrival is exactly the shared <see cref="RunScoring"/> score, so a
    /// wide build-up picture still prices holding width as the preferred duty.
    /// </summary>
    [Fact]
    public void AIB3_TheBlindSideTravelRebate_AppliesOnlyInTheFinalThird()
    {
        var ballXZ = new Vector2(RunCandidateGenerator.FinalThirdProjX - 2f, 5f);
        var winger = new Vector2(22f, -24f);
        List<PerceivedEntity> visible = new()
        {
            Entity(8, TeamId.Home, TacticalRole.Winger, ballXZ.X, ballXZ.Y)
        };
        FootballerObservation obs = RunnerObservation(
            TacticalRole.Winger, winger, winger, ballXZ, visible, TacticalJob.FarSideWidth);
        var generator = new RunCandidateGenerator(Pitch);

        Assert.True(generator.Generate(obs, offsideLineProjX: 46f, obs.Tick, maxPaths: 4) > 0);
        RunPath blindSidePath = PathOfKind(generator, RunKind.BlindSideArrival);
        RunScoreTerms blindSide = TermsOfKind(generator, RunKind.BlindSideArrival);

        float sharedScore = RunScoring.Score(blindSidePath, obs, ballXZ, obs.TeamSecurity).Score;
        Assert.Equal(sharedScore, blindSide.Score, precision: 4);
    }

    // =============================================================================================
    // Fixtures
    // =============================================================================================

    private static FootballerState Player(
        int id, TeamId team, TacticalRole role, float x, float z, float attackDirX = 1f) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(role),
        Position = new Vector3(x, 0f, z),
        TacticalAnchor = new Vector3(x, 0f, z),
        FacingXZ = new Vector2(attackDirX, 0f)
    };

    /// <summary>An 11v11 home shape with one player per nominal slot, all of them behind the halfway line.</summary>
    private static List<FootballerState> AnchorPlayers() => new()
    {
        Player(1, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f),
        Player(2, TeamId.Home, TacticalRole.CenterBack, -22f, 6f),
        Player(3, TeamId.Home, TacticalRole.CenterBack, -22f, -6f),
        Player(4, TeamId.Home, TacticalRole.FullBack, -14f, 20f),
        Player(5, TeamId.Home, TacticalRole.FullBack, -14f, -20f),
        Player(6, TeamId.Home, TacticalRole.DefensiveMidfielder, -6f, 0f),
        Player(7, TeamId.Home, TacticalRole.CentralMidfielder, 0f, 8f),
        Player(8, TeamId.Home, TacticalRole.CentralMidfielder, 0f, -8f),
        Player(9, TeamId.Home, TacticalRole.Winger, 10f, 22f),
        Player(10, TeamId.Home, TacticalRole.Winger, 10f, -22f),
        Player(11, TeamId.Home, TacticalRole.Striker, 18f, 0f)
    };

    /// <summary>The `box-cross` home shape: five outfielders, two of them central slots 4.9 m apart when compact.</summary>
    private static List<FootballerState> BoxCrossHomeShape() => new()
    {
        Player(101, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f),
        Player(102, TeamId.Home, TacticalRole.CenterBack, -20f, 0f),
        Player(104, TeamId.Home, TacticalRole.FullBack, 34f, -12f),
        Player(108, TeamId.Home, TacticalRole.CentralMidfielder, 38f, 10f),
        Player(109, TeamId.Home, TacticalRole.Striker, 40f, 2f),
        Player(107, TeamId.Home, TacticalRole.Winger, 30f, 26f)
    };

    /// <summary>No two outfield formation anchors on the same team may hold the same place.</summary>
    private static void AssertAnchorsApart(IReadOnlyList<FootballerState> players)
    {
        for (int i = 0; i < players.Count; i++)
        {
            FootballerState a = players[i];
            if (a.Role == TacticalRole.Goalkeeper) continue;

            for (int j = i + 1; j < players.Count; j++)
            {
                FootballerState b = players[j];
                if (b.Team != a.Team || b.Role == TacticalRole.Goalkeeper) continue;

                float distance = Vector2.Distance(
                    new Vector2(a.TacticalAnchor.X, a.TacticalAnchor.Z),
                    new Vector2(b.TacticalAnchor.X, b.TacticalAnchor.Z));
                Assert.True(distance >= 5f,
                    $"#{a.Id} and #{b.Id} formation anchors are {distance:F2} m apart");
            }
        }
    }

    private static void AssertPairApart(IReadOnlyList<FootballerState> players, int firstId, int secondId)
    {
        FootballerState? first = null;
        FootballerState? second = null;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].Id == firstId) first = players[i];
            if (players[i].Id == secondId) second = players[i];
        }

        Assert.NotNull(first);
        Assert.NotNull(second);
        float distance = Vector2.Distance(
            new Vector2(first!.TacticalAnchor.X, first.TacticalAnchor.Z),
            new Vector2(second!.TacticalAnchor.X, second.TacticalAnchor.Z));
        Assert.True(distance >= 5f, $"#{firstId} and #{secondId} are {distance:F2} m apart");
    }

    /// <summary>
    /// The final-third possession fixture: the carrier (home #8) is at <paramref name="carrierX"/> on the ball,
    /// nine team-mates are behind it, and the believed away line is at 42 m (two centre-backs plus a keeper).
    /// The shape is fully secure behind the ball, so the A3.6 allowance is the full two forward commitments.
    /// </summary>
    private static (List<FootballerState> Players, BallState Ball) FinalThirdFixture(float carrierX)
    {
        var players = new List<FootballerState>
        {
            Player(1, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f),
            Player(2, TeamId.Home, TacticalRole.CenterBack, -22f, 6f),
            Player(3, TeamId.Home, TacticalRole.CenterBack, -22f, -6f),
            Player(4, TeamId.Home, TacticalRole.FullBack, -12f, 20f),
            Player(5, TeamId.Home, TacticalRole.FullBack, -12f, -20f),
            Player(6, TeamId.Home, TacticalRole.DefensiveMidfielder, 14f, 12f),
            Player(7, TeamId.Home, TacticalRole.Winger, 12f, 26f),
            Player(8, TeamId.Home, TacticalRole.CentralMidfielder, carrierX, 12f),   // carrier
            Player(9, TeamId.Home, TacticalRole.Striker, 16f, 0f),
            Player(10, TeamId.Home, TacticalRole.Winger, 12f, -26f),
            Player(11, TeamId.Home, TacticalRole.CentralMidfielder, 20f, 4f),
            Player(201, TeamId.Away, TacticalRole.Goalkeeper, 50f, 0f, -1f),
            Player(202, TeamId.Away, TacticalRole.CenterBack, 42f, 6f, -1f),
            Player(203, TeamId.Away, TacticalRole.CenterBack, 40f, -6f, -1f)
        };

        var ball = new BallState
        {
            Position = new Vector3(carrierX, 0.11f, 12f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 8
        };
        return (players, ball);
    }

    /// <summary>
    /// The same final-third attack with nothing behind the ball: every home outfielder is ahead of it and two
    /// central opponents are loose goal-side, so the A3.6 picture must refuse both forward commitments.
    /// </summary>
    private static List<FootballerState> ExposedFinalThirdShape() => new()
    {
        Player(1, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f),
        Player(2, TeamId.Home, TacticalRole.CenterBack, 38f, 6f),
        Player(3, TeamId.Home, TacticalRole.CenterBack, 38f, -6f),
        Player(4, TeamId.Home, TacticalRole.FullBack, 40f, 20f),
        Player(5, TeamId.Home, TacticalRole.FullBack, 40f, -20f),
        Player(6, TeamId.Home, TacticalRole.DefensiveMidfielder, 40f, 2f),
        Player(7, TeamId.Home, TacticalRole.Winger, 42f, 26f),
        Player(8, TeamId.Home, TacticalRole.CentralMidfielder, 30f, 12f),   // carrier
        Player(9, TeamId.Home, TacticalRole.Striker, 44f, 0f),
        Player(10, TeamId.Home, TacticalRole.Winger, 42f, -26f),
        Player(11, TeamId.Home, TacticalRole.CentralMidfielder, 41f, 4f),
        // The opponents the home picture believes in: two central men loose goal-side of the ball.
        Player(201, TeamId.Away, TacticalRole.CenterBack, -10f, 2f, -1f),
        Player(202, TeamId.Away, TacticalRole.CenterBack, -14f, -4f, -1f),
        Player(203, TeamId.Away, TacticalRole.Striker, 20f, 0f, -1f)
    };

    /// <summary>
    /// The number of <paramref name="job"/> duties the allocator handed to the home side in its last pass.
    /// <see cref="TeamJobCoordinator.DutyCountOf"/> deliberately reports the second (away) team's tally -- the
    /// counts are shared by both passes and cleared between them -- so a fixture that is about the home side
    /// reads the assignments themselves. Home ids in these fixtures are 1..11 and away ids 200+.
    /// </summary>
    private static int HomeDutyCount(TeamJobCoordinator jobs, TacticalJob job)
    {
        int count = 0;
        foreach (KeyValuePair<int, TeamJobAssignment> pair in jobs.Assignments)
        {
            if (pair.Key <= 100 && pair.Value.Job == job) count++;
        }

        return count;
    }

    private static TeamJobCoordinator RunJobs(IReadOnlyList<FootballerState> players, BallState ball)
    {
        var jobs = new TeamJobCoordinator(Pitch);
        jobs.Update(
            players, ball,
            new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, 8, null),
            possessingTeam: TeamId.Home,
            homeResponderId: null, awayResponderId: null,
            homeDirX: 1f, tick: 0,
            TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
            TeamBelief.FromTruth(TeamId.Home, players), TeamBelief.FromTruth(TeamId.Away, players),
            new TeamFormation(TeamId.Home, Pitch), new TeamFormation(TeamId.Away, Pitch));
        return jobs;
    }

    // ---- run-generator fixture ------------------------------------------------------------------

    private static PerceivedEntity Entity(
        int id, TeamId team, TacticalRole role, float x, float z) =>
        new(id, team, role, new Vector3(x, 0f, z), Vector3.Zero, 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };

    /// <summary>A defender who cannot close the diagonal: the far-post race is the runner's to lose.</summary>
    private static PerceivedEntity SlowEntity(int id, float x, float z) => new(
        id, TeamId.Away, TacticalRole.CenterBack, new Vector3(x, 0f, z), Vector3.Zero, 0.95f)
    {
        Capability = new CapabilityEstimate(MaxSprintSpeed: 6.0f, Acceleration: 4.5f,
            AgilityTurnRate: 7f, ReachRadius: 0.75f, Confidence01: 0.8f)
    };

    private static FootballerObservation RunnerObservation(
        TacticalRole role,
        Vector2 selfXZ,
        Vector2 dutyAnchorXZ,
        Vector2 carrierXZ,
        IReadOnlyList<PerceivedEntity> visible,
        TacticalJob duty,
        int tick = 100,
        RestDefence? security = null) => new()
    {
        SelfId = 9,
        SelfTeam = TeamId.Home,
        SelfRole = role,
        SelfPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = false,
        ControlQuality01 = 0f,
        PerceivedBallPosition = new Vector3(carrierXZ.X, 0.11f, carrierXZ.Y),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = 8,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = 8,
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
        TeamSecurity = security ?? Secure
    };

    private static RunPath PathOfKind(RunCandidateGenerator generator, RunKind kind)
    {
        for (int i = 0; i < generator.Count; i++)
        {
            if (generator.PathAt(i).Kind == kind) return generator.PathAt(i);
        }

        throw new Xunit.Sdk.XunitException($"the path set offers no {kind}");
    }

    private static RunScoreTerms TermsOfKind(RunCandidateGenerator generator, RunKind kind)
    {
        for (int i = 0; i < generator.Count; i++)
        {
            if (generator.PathAt(i).Kind == kind) return generator.TermsAt(i);
        }

        throw new Xunit.Sdk.XunitException($"the path set offers no {kind}");
    }
}
