using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Scenarios;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// Regressions for the TeamJobs/MatchEngine findings of docs/Bug_Audit_2026-11-22.md: AUD-01c (width
/// formula), AUD-02 (box-run offside clamp), AUD-03 (incomplete AI phase reset), AUD-05 (offside-line
/// fallback), AUD-07 (duty-count corruption) and AUD-12 (dead cadence configuration).
///
/// Every fixture is synthetic: players and a ball are placed directly, the real
/// <see cref="TeamJobCoordinator"/> or <see cref="MatchSimulationEngine"/> is driven, and the assertion is
/// about the state the fix changes. A test states in its comment why the pre-fix code fails it.
/// </summary>
public sealed class AudFixJobsEngineTests
{
    private static readonly PitchDimensions Pitch = new();

    private static FootballerState Player(
        int id, TeamId team, TacticalRole role, Vector3 position, float attackingDirectionX = 1f) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(role),
        Position = position,
        TacticalAnchor = position,
        FacingXZ = new Vector2(attackingDirectionX, 0f)
    };

    private static TeamFormation FormationFor(TeamId team, IEnumerable<FootballerState> players, float attackDirX)
    {
        var formation = new TeamFormation(team, Pitch);
        var list = new List<FootballerState>();
        foreach (FootballerState player in players)
            if (player.Team == team) list.Add(player);
        formation.AssignFromPositions(list, attackDirX);
        return formation;
    }

    private static BallState BallAt(Vector3 position) => new()
    {
        Position = position with { Y = 0.11f },
        PreviousPosition = position with { Y = 0.11f },
        Velocity = Vector3.Zero
    };

    private static void RunPass(
        TeamJobCoordinator coordinator,
        IReadOnlyList<FootballerState> players,
        BallState ball,
        BallPossessionSnapshot possession,
        TeamId? possessingTeam,
        TeamFormation homeFormation,
        TeamFormation awayFormation,
        int tick,
        int? homeResponderId = null,
        int? awayResponderId = null,
        TeamTacticalProfile? homeProfile = null,
        TeamTacticalProfile? awayProfile = null,
        AiRuntimeConfig? ai = null)
    {
        coordinator.Update(
            players, ball, possession, possessingTeam,
            homeResponderId, awayResponderId, homeDirX: 1f, tick,
            homeProfile ?? TeamTacticalProfile.PossessionControl,
            awayProfile ?? TeamTacticalProfile.DirectCounterattack,
            TeamBelief.FromTruth(TeamId.Home, players),
            TeamBelief.FromTruth(TeamId.Away, players),
            homeFormation, awayFormation,
            homeContext: null, awayContext: null);
    }

    // ---------------------------------------------------------------------------------------------
    // AUD-01c — the width formula must stay inside the pitch and be monotonic in the profile.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// AUD-01c: <c>wideZ</c> used to be <c>(HalfWidth - 3) * (0.55 + 0.55 * width)</c>, i.e. 34.1 m at
    /// width 1.0 on a 68 m pitch — outside the 34 m half-width, so the target saturated against
    /// <c>ClampToPitch</c> from about 0.82 upwards and stopped following the profile. The new multiplier is
    /// <c>0.55 + 0.45 * width</c> (17.05..31.0 m), so the assertion on the exact target both checks the new
    /// formula and fails on the old one (29.84 m at 0.75 and 34.1 m at 1.0).
    /// </summary>
    [Fact]
    public void AUD01c_WidthTarget_StaysInsideThePitchAndFollowsTheProfile()
    {
        float Expected(float attackingWidth01) =>
            (Pitch.HalfWidth - 3f) * (0.55f + 0.45f * attackingWidth01);

        Assert.Equal(Expected(0.75f),
            WidthTargetOf(new TeamTacticalProfile { AttackingWidth01 = 0.75f }), precision: 3);
        Assert.Equal(Expected(1.0f),
            WidthTargetOf(new TeamTacticalProfile { AttackingWidth01 = 1.0f }), precision: 3);

        // The top of the range is the clamp bound itself: 31 m, three metres inside a 34 m half-width.
        Assert.True(WidthTargetOf(new TeamTacticalProfile { AttackingWidth01 = 1.0f }) <= Pitch.HalfWidth - 3f,
            "the widest profile target is not inside the pitch");

        // Monotonic and strictly increasing while the target is inside the pitch.
        float previous = -1f;
        for (float width = 0f; width <= 1.0f; width += 0.125f)
        {
            float target = WidthTargetOf(new TeamTacticalProfile { AttackingWidth01 = width });
            Assert.True(target > previous,
                $"the width target did not increase at {width:F3}: {target:F2} m after {previous:F2} m");
            previous = target;
        }
    }

    /// <summary>The width the away pass (the pass whose counts survive an Update) wanted, in metres.</summary>
    private static float WidthTargetOf(TeamTacticalProfile awayProfile)
    {
        var homeCarrier = Player(110, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(0f, 0f, 0f));
        var awayKeeper = Player(201, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(48f, 0f, 0f), -1f);
        var awayCentreBack = Player(202, TeamId.Away, TacticalRole.CenterBack, new Vector3(30f, 0f, 5f), -1f);
        var players = new List<FootballerState> { homeCarrier, awayKeeper, awayCentreBack };
        var ball = BallAt(homeCarrier.Position);
        var possession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, homeCarrier.Id, null);
        var coordinator = new TeamJobCoordinator(Pitch, new AiRuntimeConfig());
        RunPass(coordinator, players, ball, possession, TeamId.Home,
            FormationFor(TeamId.Home, players, 1f), FormationFor(TeamId.Away, players, -1f),
            tick: 0, awayProfile: awayProfile);
        return coordinator.AttackingWidthTargetMeters;
    }

    // ---------------------------------------------------------------------------------------------
    // AUD-02 — the box arrival must be clamped to the believed offside line.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// AUD-02: with the ball in the final third the box-run anchor was 11 m from the goal line
    /// (<c>HalfLength - 11 = 41.5 m</c>) and <see cref="TeamJobCoordinator"/>'s own hard guard rejects an
    /// attacking target more than 2 m beyond the believed line. With the line at 35 m the guard needs
    /// 41.5 &lt;= 37, so EVERY candidate was rejected and no <see cref="TacticalJob.DepthRun"/> was assigned
    /// at all. Post-fix the anchor is clamped to the onside budget (35 - 1.5 = 33.5 m, still deeper than the
    /// ball at 31 m), so the striker receives it.
    /// </summary>
    [Fact]
    public void AUD02_FinalThirdBoxRun_IsClampedToTheOnsideBudget()
    {
        const float offsideLineProj = 35f;
        var carrier = Player(110, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(31f, 0f, 0f));
        var striker = Player(105, TeamId.Home, TacticalRole.Striker, new Vector3(25f, 0f, 4f));
        var homeKeeper = Player(101, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-48f, 0f, 0f));
        var homeCentreBack = Player(102, TeamId.Home, TacticalRole.CenterBack, new Vector3(-20f, 0f, 8f));
        // A second centre-back near the ball takes the DefensiveCover duty, so the striker is still free
        // when the final third offers the box arrival (the cover duty is handed out before the run duties).
        var homeCentreBackTwo = Player(103, TeamId.Home, TacticalRole.CenterBack, new Vector3(18f, 0f, -8f));
        var awayKeeper = Player(201, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(48f, 0f, 0f), -1f);
        var awayCentreBack = Player(202, TeamId.Away, TacticalRole.CenterBack, new Vector3(offsideLineProj, 0f, 0f), -1f);
        var awayCentreBackTwo = Player(203, TeamId.Away, TacticalRole.CenterBack, new Vector3(30f, 0f, -8f), -1f);
        var players = new List<FootballerState>
        {
            homeKeeper, homeCentreBack, homeCentreBackTwo, carrier, striker,
            awayKeeper, awayCentreBack, awayCentreBackTwo
        };

        var ball = BallAt(carrier.Position);
        var possession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, carrier.Id, null);

        // Rest defence off only removes a gate from the fixture: the allowance is fully secure either way.
        var coordinator = new TeamJobCoordinator(Pitch, new AiRuntimeConfig { EnableRestDefence = false });
        RunPass(coordinator, players, ball, possession, TeamId.Home,
            FormationFor(TeamId.Home, players, 1f), FormationFor(TeamId.Away, players, -1f), tick: 0);

        TeamJobAssignment? job = coordinator.Get(striker.Id);
        Assert.NotNull(job);
        Assert.Equal(TacticalJob.DepthRun, job!.Value.Job);

        float anchorProj = job.Value.Anchor.X;
        Assert.True(anchorProj <= offsideLineProj - 1.5f + 0.01f,
            $"the box-run anchor {anchorProj:F2} m is beyond the onside budget " +
            $"{offsideLineProj - 1.5f:F2} m of the believed {offsideLineProj:F1} m line");
        Assert.True(anchorProj >= 31f,
            $"the box-run anchor {anchorProj:F2} m is behind the ball at 31 m, so it is not a box arrival");
    }

    // ---------------------------------------------------------------------------------------------
    // AUD-03 — the allocator is an IAiStateResettable and drops its passage state.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The A3.5 fixture (mirrored to the home side): a centre-back stepped out and left his slot.</summary>
    private static (List<FootballerState> Players, BallState Ball, TeamFormation Home, TeamFormation Away)
        CentreBackSteppedOutFixture()
    {
        var keeper = Player(101, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-48f, 0f, 0f));
        var steppedOut = Player(104, TeamId.Home, TacticalRole.CenterBack, new Vector3(-20f, 0f, -8f));
        var otherCentreBack = Player(105, TeamId.Home, TacticalRole.CenterBack, new Vector3(-22f, 0f, 6f));
        var defensiveMidfielder = Player(106, TeamId.Home, TacticalRole.DefensiveMidfielder, new Vector3(-6f, 0f, 16f));
        var awayCarrier = Player(201, TeamId.Away, TacticalRole.CentralMidfielder, new Vector3(-5.4f, 0f, -9.8f), -1f);
        var players = new List<FootballerState>
        {
            keeper, steppedOut, otherCentreBack, defensiveMidfielder, awayCarrier
        };

        // The anchors are what "out of the line" is measured against; write them as the engine would.
        keeper.TacticalAnchor = keeper.Position;
        steppedOut.TacticalAnchor = new Vector3(-20f, 0f, -8f);
        otherCentreBack.TacticalAnchor = new Vector3(-22f, 0f, 6f);
        defensiveMidfielder.TacticalAnchor = new Vector3(-6f, 0f, 16f);
        awayCarrier.TacticalAnchor = awayCarrier.Position;

        // The formations are derived from the shape positions, so they are assigned before the press moves
        // the centre-back out of his slot (the way the engine assigns them from the kickoff layout).
        TeamFormation homeFormation = FormationFor(TeamId.Home, players, 1f);
        TeamFormation awayFormation = FormationFor(TeamId.Away, players, -1f);

        // Then the centre-back presses: he is on the ball, well ahead of the shape point he was given.
        steppedOut.Position = new Vector3(-6f, 0f, -9.5f);

        var ball = BallAt(awayCarrier.Position);
        return (players, ball, homeFormation, awayFormation);
    }

    /// <summary>
    /// AUD-03 (coordinator half): the job coordinator was not on the engine's reset list, so an open
    /// coverage request, the assignments, the line band and the turnover commitments survived a
    /// discontinuity and a quick restart kept pre-stoppage anchors for up to MinimumHoldTicks. Before the
    /// fix there was no <c>ResetForPhase</c> at all, and the coverage request below stayed live.
    /// </summary>
    [Fact]
    public void AUD03_CoordinatorReset_DropsPassageStateAndForcesTheNextPass()
    {
        var (players, ball, homeFormation, awayFormation) = CentreBackSteppedOutFixture();
        var possession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Away, 201, null);
        var coordinator = new TeamJobCoordinator(Pitch, new AiRuntimeConfig());

        RunPass(coordinator, players, ball, possession, TeamId.Away,
            homeFormation, awayFormation, tick: 0, homeResponderId: 104);

        string fixtureState = DescribeJobs(coordinator, players, TeamId.Home, homeFormation, 1f, null);
        Assert.True(coordinator.CoverageOf(TeamId.Home).IsLive,
            $"the fixture did not raise the coverage request, so the reset has nothing to drop. {fixtureState}");
        Assert.NotEmpty(coordinator.Assignments);
        Assert.True(coordinator.LineBandOf(TeamId.Home).IsValid, "the fixture has no defensive line band");
        Assert.Equal(1, coordinator.AssignmentPassCount);

        coordinator.ResetForPhase(AiResetReason.HalfTime);

        Assert.Equal(1, coordinator.PhaseResetCount);
        Assert.False(coordinator.CoverageOf(TeamId.Home).IsLive);
        Assert.Equal(CoverageRequestState.PassageEnded, coordinator.CoverageOf(TeamId.Home).State);
        Assert.Contains("phase reset", coordinator.CoverageOf(TeamId.Home).Reason);
        Assert.Empty(coordinator.Assignments);
        Assert.False(coordinator.LineBandOf(TeamId.Home).IsValid);
        Assert.Equal(TurnoverSituation.None, coordinator.TurnoverOf(TeamId.Home));

        // The next tick must recompute immediately, not wait out the cadence: the assignments were
        // cleared, so a skipped pass would leave every player with no duty.
        Assert.True(coordinator.WillRecompute(TeamId.Away, tick: 1),
            "the reset did not force a recompute on the next live tick");

        RunPass(coordinator, players, ball, possession, TeamId.Away,
            homeFormation, awayFormation, tick: 1, homeResponderId: 104);
        Assert.Equal(2, coordinator.AssignmentPassCount);
        Assert.NotEmpty(coordinator.Assignments);
    }

    /// <summary>
    /// AUD-03 (engine half): at half time the engine clears the observations and the team beliefs, because
    /// the interval is not live play and nothing refills them before the second half's first live tick.
    /// Before the fix <see cref="MatchSimulationEngine.GetLastObservation"/> still returned a first-half
    /// snapshot and <see cref="TeamBelief.HasBeenBuilt"/> was still true, so the first live tick rebuilt the
    /// beliefs from stale first-half sightings (opposite direction, pre-kickoff positions).
    /// </summary>
    [Fact]
    public void AUD03_HalfTime_DropsStaleObservationsAndBeliefs_Safely()
    {
        MatchScenario scenario = ShortHalfScenario();
        var engine = new MatchSimulationEngine(scenario);

        // Prove the first half really produced the working state the reset has to drop.
        for (int tick = 0; tick < 10; tick++) engine.StepTick();
        Assert.Equal(MatchPhase.FirstHalf, engine.Rules.Phase);
        Assert.True(engine.TeamBeliefOf(TeamId.Home).HasBeenBuilt,
            "the first half never built a belief, so the stale-rebuild defect cannot be shown");
        Assert.NotNull(engine.GetLastObservation(102));

        for (int tick = 0; tick < 400 && engine.LastAiResetReason is null; tick++)
            engine.StepTick();

        Assert.Equal(MatchPhase.HalfTime, engine.Rules.Phase);
        Assert.Equal(AiResetReason.HalfTime, engine.LastAiResetReason);
        Assert.Null(engine.GetLastObservation(102));
        Assert.False(engine.TeamBeliefOf(TeamId.Home).HasBeenBuilt);
        Assert.False(engine.TeamBeliefOf(TeamId.Away).HasBeenBuilt);
        Assert.Empty(engine.TeamJobs.Assignments);
        Assert.True(engine.TeamJobs.PhaseResetCount >= 1,
            "the half-time reset never reached the job coordinator");

        // Walk to the first tick that STARTS live in the second half. The first look there must be the safe
        // seed, not a rebuild from observations that the reset cleared.
        int guard = 0;
        while (guard++ < 2000)
        {
            bool liveNow = engine.Rules.Period == MatchPeriod.SecondHalf && !engine.Rules.IsPlaySuspended;
            engine.StepTick();
            if (liveNow) break;
        }

        Assert.Equal(MatchPeriod.SecondHalf, engine.Rules.Period);
        Assert.False(engine.Rules.IsPlaySuspended);
        TeamBelief firstLook = engine.TeamBeliefOf(TeamId.Home);
        Assert.True(firstLook.HasBeenBuilt, "the first live tick of the second half built no belief at all");
        Assert.True(firstLook.IsTruthView,
            "the first live tick rebuilt the belief from observations instead of taking the first look");

        // A later rebuild in the second half comes from second-half observations again.
        guard = 0;
        while (guard++ < 400 && engine.TeamBeliefOf(TeamId.Home).IsTruthView)
            engine.StepTick();
        Assert.False(engine.TeamBeliefOf(TeamId.Home).IsTruthView,
            "the belief never returned to the observation-driven rebuild in the second half");
    }

    /// <summary>
    /// AUD-03 (engine half, restart): every authoritative discontinuity clears the allocator and both
    /// teams' run advertisements. Before the fix a manual promise published on the board survived a
    /// restart (it was only ever withdrawn by the runner's own generator), and the pre-stoppage job
    /// assignments were kept for up to MinimumHoldTicks.
    /// </summary>
    [Fact]
    public void AUD03_MatchRestart_ResetsAllocatorAndRunAdvertisements()
    {
        // The ball rolls slowly towards the touchline, so the match is still live for a few ticks and a
        // throw-in is the first real restart.
        var scenario = new MatchScenario
        {
            Name = "AUD-03 restart probe",
            Seed = 31337UL,
            BallSetup = ScenarioBallSetup.Loose,
            BallPosition = new Vector3(0f, 0f, 32.8f),
            BallVelocity = new Vector3(0f, 0f, 3f)
        };
        scenario.Add(MatchScenario.Player(101, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-40f, 0f, 0f), 1));
        scenario.Add(MatchScenario.Player(102, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(-6f, 0f, 0f), 8));
        scenario.Add(MatchScenario.Player(201, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(40f, 0f, 0f), 1));
        scenario.Add(MatchScenario.Player(202, TeamId.Away, TacticalRole.CentralMidfielder, new Vector3(6f, 0f, 0f), 8));

        var engine = new MatchSimulationEngine(scenario);
        for (int tick = 0; tick < 3; tick++) engine.StepTick();
        Assert.NotEmpty(engine.TeamJobs.Assignments);

        // Publish a run promise for an id no controller can withdraw, with a window that outlives the
        // restart, so only the reset itself can clear it.
        int publishedAt = engine.CurrentTick;
        var path = new RunPath(
            RunKind.CheckShort, new Vector2(0f, 0f), new Vector2(12f, 4f), 3f,
            StartTick: publishedAt, MeetTick: publishedAt + 30, ExpiresTick: publishedAt + 600,
            HoldUntilTick: publishedAt, HoldXZ: new Vector2(6f, 2f), Confidence01: 0.8f);
        var intent = new RunIntent(path, TacticalJob.DepthRun, publishedAt);
        engine.Communication.Runs.Publish(TeamId.Home, 9999, intent, publishedAt);

        for (int tick = 0; tick < 120 && engine.LastAiResetReason is null; tick++) engine.StepTick();
        Assert.Equal(AiResetReason.MatchRestart, engine.LastAiResetReason);
        Assert.True(engine.TeamJobs.PhaseResetCount >= 1,
            "the restart never reached the job coordinator");
        Assert.Empty(engine.TeamJobs.Assignments);

        // Read at a tick where the promise would be visible and still unexpired without the reset.
        int queryTick = publishedAt + RunAdvertisementBoard.CommunicationDelayTicks + 1;
        Assert.Equal(0, engine.Communication.Runs.VisibleCount(TeamId.Home, queryTick));
        Assert.False(engine.Communication.Runs.TryGetAdvertisement(TeamId.Home, 9999, queryTick, out _));
    }

    private static MatchScenario ShortHalfScenario()
    {
        var scenario = new MatchScenario
        {
            Name = "AUD-03 half-time probe",
            Seed = 20261122UL,
            HalfDurationSeconds = 0.4f,
            BallSetup = ScenarioBallSetup.Loose,
            BallPosition = new Vector3(0f, 0f, 0f)
        };
        scenario.Add(MatchScenario.Player(101, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-40f, 0f, 0f), 1));
        scenario.Add(MatchScenario.Player(102, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(-6f, 0f, 0f), 8));
        scenario.Add(MatchScenario.Player(201, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(40f, 0f, 0f), 1));
        scenario.Add(MatchScenario.Player(202, TeamId.Away, TacticalRole.CentralMidfielder, new Vector3(6f, 0f, 0f), 8));
        return scenario;
    }

    // ---------------------------------------------------------------------------------------------
    // AUD-05 — fewer than two believed opponents falls back to the halfway line.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// AUD-05: with fewer than two believed opponents the old code returned <c>_pitch.HalfLength</c> — the
    /// opponent goal line — so every attacking duty was allowed up to 52.5 m. The fixture has one believed
    /// opponent while the ball is at 2 m: the old line was 52.5 m and a DepthRun was handed out at 46.5 m;
    /// the fixed line is the documented halfway fallback (raised to the ball's own projection), so no
    /// attacking anchor may be placed ahead of the ball.
    /// </summary>
    [Fact]
    public void AUD05_SparseBelief_FallsBackToTheHalfwayLine()
    {
        var carrier = Player(110, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(2f, 0f, 0f));
        var homeKeeper = Player(101, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-48f, 0f, 0f));
        var homeCentreBack = Player(102, TeamId.Home, TacticalRole.CenterBack, new Vector3(-20f, 0f, -8f));
        var homeCentreBackTwo = Player(103, TeamId.Home, TacticalRole.CenterBack, new Vector3(-20f, 0f, 8f));
        var homeStriker = Player(105, TeamId.Home, TacticalRole.Striker, new Vector3(30f, 0f, 4f));
        // A single believed opponent: TeamBelief.TryGetSecondLastProjX must report "not enough".
        var awayKeeper = Player(201, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(48f, 0f, 0f), -1f);
        var players = new List<FootballerState>
        {
            homeKeeper, homeCentreBack, homeCentreBackTwo, carrier, homeStriker, awayKeeper
        };

        var ball = BallAt(carrier.Position);
        var possession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, carrier.Id, null);
        var coordinator = new TeamJobCoordinator(Pitch, new AiRuntimeConfig { EnableRestDefence = false });
        RunPass(coordinator, players, ball, possession, TeamId.Home,
            FormationFor(TeamId.Home, players, 1f), FormationFor(TeamId.Away, players, -1f), tick: 0);

        Assert.Equal(1, TeamBelief.FromTruth(TeamId.Home, players).Count);

        // The believed line is the halfway line raised to the ball's projection (2 m), so every attacking
        // target must sit at or behind it — and the old 52.5 m fallback put the striker's depth run at
        // 46.5 m, well beyond it.
        foreach (FootballerState player in players)
        {
            if (player.Team != TeamId.Home) continue;
            TeamJobAssignment? job = coordinator.Get(player.Id);
            if (job is not TeamJobAssignment assignment) continue;
            if (!IsAttackingJob(assignment.Job)) continue;

            Assert.True(assignment.Anchor.X <= 2f + 0.01f,
                $"#{player.Id} holds {assignment.Job} at {assignment.Anchor.X:F1} m, beyond the believed " +
                "halfway line at 2 m");
        }

        Assert.NotEqual(TacticalJob.DepthRun, coordinator.Get(homeStriker.Id)?.Job);
    }

    private static bool IsAttackingJob(TacticalJob job) => job is TacticalJob.NearOutlet
        or TacticalJob.ThirdPlayerOption or TacticalJob.FarSideWidth or TacticalJob.DepthRun
        or TacticalJob.Overlap;

    // ---------------------------------------------------------------------------------------------
    // AUD-07 — the duty counts must describe the duties actually handed out.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// AUD-07 (1): a handover may take a defender held on <see cref="TacticalJob.Shape"/> as well as one on
    /// <see cref="TacticalJob.DefensiveLine"/>, but the old release always decremented the line's count.
    /// The fixture keeps one centre-back on the line and a second far enough wide to stay out of the cover
    /// duty, holds a defensive midfielder on Shape, then runs the striker into the midfielder's zone so the
    /// handover takes the Shape duty. Pre-fix the release spends a line count while the line still holds a
    /// man, so the counter disagrees with the assignments after the pass.
    /// </summary>
    [Fact]
    public void AUD07_HandoverTakingAShapeDefender_DoesNotSpendALineCount()
    {
        const int markingCentreBack = 204;
        const int coverCentreBack = 205;
        const int shapeMidfielder = 206;
        const int lineCentreBack = 207;

        var awayKeeper = Player(201, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(46f, 0f, 0f), -1f);
        var marker = Player(markingCentreBack, TeamId.Away, TacticalRole.CenterBack, new Vector3(25f, 0f, -5f), -1f);
        var cover = Player(coverCentreBack, TeamId.Away, TacticalRole.CenterBack, new Vector3(25f, 0f, 8f), -1f);
        var midfielder = Player(shapeMidfielder, TeamId.Away, TacticalRole.DefensiveMidfielder, new Vector3(28f, 0f, 25f), -1f);
        var lineDefender = Player(lineCentreBack, TeamId.Away, TacticalRole.CenterBack, new Vector3(24f, 0f, 2f), -1f);
        var homeCarrier = Player(111, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(5.4f, 0f, 9.8f));
        var threat = Player(112, TeamId.Home, TacticalRole.Striker, new Vector3(26f, 0f, -4f));
        var players = new List<FootballerState>
        {
            awayKeeper, marker, cover, midfielder, lineDefender, homeCarrier, threat
        };

        foreach (FootballerState player in players) player.TacticalAnchor = player.Position;

        var awayFormation = FormationFor(TeamId.Away, players, -1f);
        var homeFormation = FormationFor(TeamId.Home, players, 1f);
        var ball = BallAt(homeCarrier.Position);
        var possession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, homeCarrier.Id, null);
        var coordinator = new TeamJobCoordinator(Pitch, new AiRuntimeConfig());
        var awayProfile = TeamTacticalProfile.DirectCounterattack;   // one marker, so line men stay on the line

        // Pass 1: the marker picks up the striker; the cover duty and the line hold the other defenders;
        // the wide midfielder is Shape.
        RunPass(coordinator, players, ball, possession, TeamId.Home, homeFormation, awayFormation, tick: 0,
            awayProfile: awayProfile);
        Assert.Equal(TacticalJob.Marker, coordinator.Get(markingCentreBack)?.Job);
        Assert.Equal(TacticalJob.DefensiveLine, coordinator.Get(lineCentreBack)?.Job);
        Assert.Equal(TacticalJob.Shape, coordinator.Get(shapeMidfielder)?.Job);
        string afterFirstPass = DescribeJobs(coordinator, players, TeamId.Away, awayFormation, -1f, threat);

        // The striker crosses towards the wide midfielder's zone; that slot now owns the threat, while the
        // held marker's slot is far away.
        threat.Position = new Vector3(25f, 0f, 20f);
        threat.TacticalAnchor = threat.Position;

        RunPass(coordinator, players, ball, possession, TeamId.Home, homeFormation, awayFormation, tick: 6,
            awayProfile: awayProfile);

        Assert.True(coordinator.HandoverCount == 1,
            $"no handover was made. after pass 1: {afterFirstPass} | after pass 2: " +
            DescribeJobs(coordinator, players, TeamId.Away, awayFormation, -1f, threat));
        Assert.Equal(TacticalJob.Marker, coordinator.Get(shapeMidfielder)?.Job);
        Assert.Equal(threat.Id, coordinator.Get(shapeMidfielder)?.TargetPlayerId);

        AssertDutyCountsMatchAssignments(coordinator, players, TeamId.Away, TacticalJob.DefensiveLine);
    }

    /// <summary>
    /// AUD-07 (2): <c>MaintainCoverage</c> lets an incumbent kept on <see cref="TacticalJob.DefensiveLine"/>
    /// (or Shape) win the ProtectGap bid, then overwrites his duty — the old code never moved the count, so
    /// the line kept counting a defender who now covers a gap. The fixture runs the A3.5 step-out twice with
    /// the request closing in between, which is what makes the second winner an incumbent.
    /// </summary>
    [Fact]
    public void AUD07_CoverageProtector_LeavesHisOwnDutyCount()
    {
        const int stepOutCentreBack = 204;
        const int otherCentreBack = 205;
        const int defensiveMidfielder = 206;

        var keeper = Player(201, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(48f, 0f, 0f), -1f);
        var stepOut = Player(stepOutCentreBack, TeamId.Away, TacticalRole.CenterBack, new Vector3(20f, 0f, 8f), -1f);
        var other = Player(otherCentreBack, TeamId.Away, TacticalRole.CenterBack, new Vector3(22f, 0f, -6f), -1f);
        var midfielder = Player(defensiveMidfielder, TeamId.Away, TacticalRole.DefensiveMidfielder, new Vector3(6f, 0f, -16f), -1f);
        var homeCarrier = Player(111, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(5.4f, 0f, 9.8f));
        var players = new List<FootballerState> { keeper, stepOut, other, midfielder, homeCarrier };

        foreach (FootballerState player in players) player.TacticalAnchor = player.Position;
        var awayFormation = FormationFor(TeamId.Away, players, -1f);
        var homeFormation = FormationFor(TeamId.Home, players, 1f);
        var ball = BallAt(homeCarrier.Position);
        var possession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, homeCarrier.Id, null);
        var coordinator = new TeamJobCoordinator(Pitch, new AiRuntimeConfig());

        // Pass 1: the centre-back steps out, the coverage request opens and the other centre-back takes it.
        stepOut.Position = new Vector3(6.4f, 0f, 9.8f);
        RunPass(coordinator, players, ball, possession, TeamId.Home, homeFormation, awayFormation, tick: 0,
            awayResponderId: stepOutCentreBack);
        Assert.True(coordinator.CoverageOf(TeamId.Away).IsLive,
            "the step-out fixture did not open the away coverage request");
        Assert.Equal(otherCentreBack, coordinator.CoverageOf(TeamId.Away).OwnerPlayerId);

        // Pass 2: the presser returns to his line, which hands the request back and closes it. With no
        // presser duty the other centre-back re-forms as a plain line defender.
        stepOut.Position = stepOut.TacticalAnchor;
        RunPass(coordinator, players, ball, possession, TeamId.Home, homeFormation, awayFormation, tick: 6);
        Assert.False(coordinator.CoverageOf(TeamId.Away).IsLive);
        Assert.Equal(TacticalJob.DefensiveLine, coordinator.Get(otherCentreBack)?.Job);

        // Pass 3: the press starts again. The winner is an incumbent already counted on the line this pass.
        stepOut.Position = new Vector3(6.4f, 0f, 9.8f);
        RunPass(coordinator, players, ball, possession, TeamId.Home, homeFormation, awayFormation, tick: 12,
            awayResponderId: stepOutCentreBack);
        Assert.True(coordinator.CoverageOf(TeamId.Away).IsLive);
        Assert.Equal(otherCentreBack, coordinator.CoverageOf(TeamId.Away).OwnerPlayerId);
        Assert.Equal(TacticalJob.ProtectGap, coordinator.Get(otherCentreBack)?.Job);

        AssertDutyCountsMatchAssignments(coordinator, players, TeamId.Away, TacticalJob.DefensiveLine);
    }

    /// <summary>Diagnostics for a failing handover assertion: every duty plus the relevant slot distances.</summary>
    private static string DescribeJobs(
        TeamJobCoordinator coordinator,
        IReadOnlyList<FootballerState> players,
        TeamId team,
        TeamFormation formation,
        float attackDirX,
        FootballerState? reference)
    {
        var text = new System.Text.StringBuilder();
        foreach (FootballerState player in players)
        {
            if (player.Team != team) continue;
            TeamJobAssignment? duty = coordinator.Get(player.Id);
            formation.TryGetWorldAnchor(player.Id, attackDirX, out Vector3 slot);
            text.Append($"#{player.Id}:{duty?.Job}/{duty?.TargetPlayerId} " +
                        $"anchor({duty?.Anchor.X:F1},{duty?.Anchor.Z:F1}) " +
                        $"pos({player.Position.X:F0},{player.Position.Z:F0}) " +
                        $"slot({slot.X:F1},{slot.Z:F1}) ");
            if (reference is FootballerState referencePlayer)
            {
                text.Append(
                    $"d{Vector2.Distance(new Vector2(slot.X, slot.Z), PositionXZOf(referencePlayer)):F1} ");
            }
        }

        text.Append($"handovers {coordinator.Handovers.Count}");
        return text.ToString();
    }

    /// <summary>XZ position of a footballer (convenience for diagnostics).</summary>
    private static Vector2 PositionXZOf(FootballerState player) => new(player.Position.X, player.Position.Z);

    /// <summary>
    /// The invariant AUD-07 (1) breaks: after a pass, the allocator's line count equals the number of
    /// players the same pass left holding <see cref="TacticalJob.DefensiveLine"/>. The final shape loop and
    /// the keeper are deliberately not counted, which is why the line duty is the invariant checked.
    /// </summary>
    private static void AssertDutyCountsMatchAssignments(
        TeamJobCoordinator coordinator,
        IReadOnlyList<FootballerState> players,
        TeamId team,
        TacticalJob job)
    {
        int assigned = 0;
        var held = new System.Text.StringBuilder();
        foreach (FootballerState player in players)
        {
            if (player.Team != team) continue;
            TeamJobAssignment? duty = coordinator.Get(player.Id);
            if (duty?.Job != job) continue;
            assigned++;
            held.Append($"#{player.Id} ");
        }

        Assert.True(assigned == coordinator.DutyCountOf(job),
            $"{job}: the pass left {assigned} player(s) holding it ({held.ToString().Trim()}) but the " +
            $"allocator counts {coordinator.DutyCountOf(job)}");
    }

    // ---------------------------------------------------------------------------------------------
    // AUD-12 — the configured cadences are the ones the mechanisms actually run on.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// AUD-12 (allocator): the recompute interval is <see cref="AiRuntimeConfig.LocalCoordinationTicks"/>,
    /// whose default 6 is exactly the old hard-coded literal. With a configuration of 2 ticks the second
    /// pass must run at tick 2 instead of waiting for tick 6; the old code ignored the configuration, so
    /// both coordinators behaved the same.
    /// </summary>
    [Fact]
    public void AUD12_RecomputeCadence_FollowsLocalCoordinationTicks()
    {
        Assert.Equal(2, PassesByTick(new AiRuntimeConfig { LocalCoordinationTicks = 2 }, tick: 2));
        Assert.Equal(1, PassesByTick(new AiRuntimeConfig(), tick: 2));
        Assert.Equal(2, PassesByTick(new AiRuntimeConfig(), tick: 6));

        // The predicate the engine reads and the pass itself must agree. An initial pass consumes the
        // "not recomputed yet" state a fresh coordinator starts with.
        var fast = new TeamJobCoordinator(Pitch, new AiRuntimeConfig { LocalCoordinationTicks = 2 });
        var shipped = new TeamJobCoordinator(Pitch, new AiRuntimeConfig());
        var passFixtures = PassFixtures();
        RunPass(fast, passFixtures.Players, passFixtures.Ball, passFixtures.Possession, TeamId.Home,
            passFixtures.HomeFormation, passFixtures.AwayFormation, tick: 0);
        RunPass(shipped, passFixtures.Players, passFixtures.Ball, passFixtures.Possession, TeamId.Home,
            passFixtures.HomeFormation, passFixtures.AwayFormation, tick: 0);

        Assert.True(fast.WillRecompute(TeamId.Home, tick: 2));
        Assert.False(shipped.WillRecompute(TeamId.Home, tick: 2));
        Assert.True(shipped.WillRecompute(TeamId.Home, tick: 6));
    }

    private static (List<FootballerState> Players, BallState Ball, BallPossessionSnapshot Possession,
        TeamFormation HomeFormation, TeamFormation AwayFormation) PassFixtures()
    {
        var homeCarrier = Player(110, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(0f, 0f, 0f));
        var awayKeeper = Player(201, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(48f, 0f, 0f), -1f);
        var players = new List<FootballerState> { homeCarrier, awayKeeper };
        return (players, BallAt(homeCarrier.Position),
            new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, homeCarrier.Id, null),
            FormationFor(TeamId.Home, players, 1f), FormationFor(TeamId.Away, players, -1f));
    }

    private static int PassesByTick(AiRuntimeConfig ai, int tick)
    {
        var homeCarrier = Player(110, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(0f, 0f, 0f));
        var awayKeeper = Player(201, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(48f, 0f, 0f), -1f);
        var players = new List<FootballerState> { homeCarrier, awayKeeper };
        var ball = BallAt(homeCarrier.Position);
        var possession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home, homeCarrier.Id, null);
        var coordinator = new TeamJobCoordinator(Pitch, ai);
        var homeFormation = FormationFor(TeamId.Home, players, 1f);
        var awayFormation = FormationFor(TeamId.Away, players, -1f);

        for (int passTick = 0; passTick <= tick; passTick++)
        {
            RunPass(coordinator, players, ball, possession, TeamId.Home, homeFormation, awayFormation, passTick);
        }

        return coordinator.AssignmentPassCount;
    }

    /// <summary>
    /// AUD-12 (engine): <c>UpdateTeamAnchors</c> used to hard-code <c>CurrentTick % 4 == 1</c> while
    /// <see cref="AiRuntimeConfig.PerceptionRefreshTicks"/> was printed in every artifact and read by
    /// nothing. Two identical quiet fixtures differ only in the configured cadence; over 12 live ticks the
    /// default must refresh on ticks 1, 5, 9 and a 2-tick cadence on 1, 3, 5, 7, 9, 11.
    /// </summary>
    [Fact]
    public void AUD12_AnchorRefreshCadence_FollowsPerceptionRefreshTicks()
    {
        Assert.Equal(3, AnchorRefreshesOf(period: 4));
        Assert.Equal(6, AnchorRefreshesOf(period: 2));
    }

    private static int AnchorRefreshesOf(int period)
    {
        var engine = new MatchSimulationEngine(QuietScenario(
            new AiRuntimeConfig { PerceptionRefreshTicks = period }));
        for (int tick = 0; tick < 12; tick++) engine.StepTick();
        return engine.AnchorRefreshCount;
    }

    /// <summary>
    /// AUD-12 (engine): the belief rebuild is driven by the allocator's <c>WillRecompute</c>, so it follows
    /// <see cref="AiRuntimeConfig.LocalCoordinationTicks"/> as well. A 2-tick cadence must rebuild more often
    /// than the shipped 6-tick one over the same quiet passage; the old code ignored the configuration and
    /// produced identical counts.
    /// </summary>
    [Fact]
    public void AUD12_BeliefRebuildCadence_FollowsLocalCoordinationTicks()
    {
        int fast = BeliefRebuildsOf(new AiRuntimeConfig { LocalCoordinationTicks = 2 });
        int shipped = BeliefRebuildsOf(new AiRuntimeConfig());

        Assert.True(fast > shipped,
            $"a 2-tick local-coordination cadence rebuilt the beliefs {fast} times against {shipped} " +
            "for the shipped 6-tick cadence: the configuration is still dead");
        Assert.True(shipped >= 1, "no belief rebuild happened at all, so the comparison is vacuous");
    }

    private static int BeliefRebuildsOf(AiRuntimeConfig ai)
    {
        var engine = new MatchSimulationEngine(QuietScenario(ai));
        for (int tick = 0; tick < 12; tick++) engine.StepTick();
        return (int)engine.GetAiWorkStats().BeliefRebuilds;
    }

    /// <summary>
    /// A passage that cannot produce a restart in 12 ticks: four players stand well away from a stationary
    /// loose ball, so every tick is live and the cadence is the only thing deciding the counters.
    /// </summary>
    private static MatchScenario QuietScenario(AiRuntimeConfig ai)
    {
        var scenario = new MatchScenario
        {
            Name = "AUD-12 cadence probe",
            Seed = 424242UL,
            Ai = ai,
            BallSetup = ScenarioBallSetup.Loose,
            BallPosition = new Vector3(0f, 0f, 0f)
        };
        scenario.Add(MatchScenario.Player(101, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-45f, 0f, 0f), 1));
        scenario.Add(MatchScenario.Player(102, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(-25f, 0f, 0f), 8));
        scenario.Add(MatchScenario.Player(201, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(45f, 0f, 0f), 1));
        scenario.Add(MatchScenario.Player(202, TeamId.Away, TacticalRole.CentralMidfielder, new Vector3(25f, 0f, 0f), 8));
        return scenario;
    }
}
