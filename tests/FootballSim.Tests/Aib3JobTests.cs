using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// AIB-09 (<c>docs/AUDIT_AI57_2026-11-23.md</c> §2, "Image 3 — Amateur Defending: <c>COVER_GAP</c> Runs
/// Past <c>THIRD_OPTION</c> on the Last Line"): the two scored fixes.
///
/// <list type="number">
/// <item><b>CandidateGenerator</b>: defensive jobs used to be scored with the attacking support package —
///   <c>StepOutOfCoverShadow</c> pushed the target up to six metres away from the nearest opponent and
///   <c>+0.30 lane + 0.30 space</c> paid the defender for the empty grass he stepped into. Classified by
///   <see cref="CandidateGenerator.IsDefensiveJob"/>, those duties now emit no stepped-out variant, are
///   clamped at the live line's ceiling, and are scored on goal-side position and lane blocking.</item>
/// <item><b>TeamJobs.FindVacancy</b>: the protected region was the static kickoff-relative slot, so it
///   could sit forward of the line the team was actually defending. Its depth is now clamped to
///   <see cref="DefensiveLineBand.MaxProjX"/>; with no valid band the old result stands exactly.</item>
/// </list>
///
/// Every fixture is synthetic and drives the real public API — <see cref="CandidateGenerator"/>'s off-ball
/// entry point, which is the route the decision path takes, and the real <see cref="TeamJobCoordinator"/>.
/// Each test's comment states the pre-fix arithmetic that fails it.
/// </summary>
public sealed class Aib3JobTests
{
    private static readonly PitchDimensions Pitch = new();

    private const int SelfId = 104;
    private const int CarrierId = 110;
    private const int AttackerId = 202;

    // ---------------------------------------------------------------------------------------------
    // 1. The classification itself.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// AIB-09: one table for the whole decision path. The audit named the four out-of-possession duties
    /// (<c>ProtectGap</c>, <c>CoverDefender</c>, <c>Marker</c>, <c>DefensiveCover</c>) and the line was
    /// already special-cased; the risk this pins is the next consumer re-deriving its own list, which is
    /// exactly how the line kept the case while the other four were scored as attackers.
    /// </summary>
    [Fact]
    public void AIB09_DefensiveJobClassification_CoversTheCoverDutiesAndTheLine()
    {
        Assert.True(CandidateGenerator.IsDefensiveJob(TacticalJob.ProtectGap));
        Assert.True(CandidateGenerator.IsDefensiveJob(TacticalJob.CoverDefender));
        Assert.True(CandidateGenerator.IsDefensiveJob(TacticalJob.Marker));
        Assert.True(CandidateGenerator.IsDefensiveJob(TacticalJob.DefensiveCover));
        Assert.True(CandidateGenerator.IsDefensiveJob(TacticalJob.DefensiveLine));

        // Nothing that supports an attack may be scored as a defender.
        Assert.False(CandidateGenerator.IsDefensiveJob(TacticalJob.Shape));
        Assert.False(CandidateGenerator.IsDefensiveJob(TacticalJob.BallResponder));
        Assert.False(CandidateGenerator.IsDefensiveJob(TacticalJob.NearOutlet));
        Assert.False(CandidateGenerator.IsDefensiveJob(TacticalJob.ThirdPlayerOption));
        Assert.False(CandidateGenerator.IsDefensiveJob(TacticalJob.FarSideWidth));
        Assert.False(CandidateGenerator.IsDefensiveJob(TacticalJob.DepthRun));
        Assert.False(CandidateGenerator.IsDefensiveJob(TacticalJob.Overlap));
        // A presser is produced by PressPolicy as a primary act, never by this scoring path.
        Assert.False(CandidateGenerator.IsDefensiveJob(TacticalJob.Presser));
    }

    // ---------------------------------------------------------------------------------------------
    // 2. No stepped-out variant, goal-side of the covered man, behind the live line ceiling.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The audit's Image 3 case. A <c>COVER_GAP</c> target stood two metres <b>ball-side</b> of the
    /// attacker it protects (a slot the static formation owns, ahead of the line the side is defending).
    /// Pre-fix the preventive arithmetic is: nearest opponent 2 m from the target, so
    /// <c>StepOutOfCoverShadow</c> returns <c>target + away x (6 - 2)</c> = (16, 0) — six metres further
    /// from the attacker, four of them past his back — and the source candidate stays at (12, 0). Both are
    /// ahead of the attacker at 10 m, so the defender chases the man rather than covering him.
    ///
    /// Post-fix there is exactly one candidate: the allocator's anchor clamped to the band's ceiling of
    /// 8 m, i.e. goal-side of the attacker at 10 m, and no adjusted variant exists at all.
    /// </summary>
    [Fact]
    public void AIB09_ProtectGapCandidate_IsGoalSideOfTheCoveredAttacker_AndIsNotSteppedOut()
    {
        const float maxProjX = 8f;
        Vector2 selfXZ = new(12f, -6f);
        Vector2 anchorXZ = new(12f, 0f);         // two metres ball-side of the attacker at (10, 0)
        Vector2 attackerXZ = new(10f, 0f);
        var generator = new CandidateGenerator(Pitch, communication: null,
            new AiRuntimeConfig { EnableCombinations = false });

        generator.GenerateOffBallCandidates(
            DefenderObservation(
                selfXZ, anchorXZ, ballXZ: new Vector2(-20f, 15f), TacticalJob.ProtectGap, TeamId.Away,
                [Opponent(AttackerId, TacticalRole.Striker, attackerXZ.X, attackerXZ.Y)],
                Band(proj: maxProjX, min: -Pitch.HalfLength + 5f, max: maxProjX)),
            OpponentGoal, OwnGoal, selfXZ, isPrimaryBallResponder: false, offsideLineProjX: 0f);

        (int count, UtilityCandidate only) = SingleJobCandidate(generator, TacticalJob.ProtectGap);

        Assert.Equal(1, count);
        Assert.False(only.Key.Adjusted, "a defensive duty must not offer a stepped-out variant");
        Assert.True(only.TargetXZ.HasValue);
        float targetProj = only.TargetXZ!.Value.X;
        Assert.Equal(maxProjX, targetProj, precision: 3);
        Assert.True(targetProj < attackerXZ.X,
            $"the COVER_GAP target {targetProj:F2} m is not goal-side of the attacker at {attackerXZ.X:F1} m " +
            "(pre-fix: candidates at 12 m and 16 m, both in front of him)");
    }

    // ---------------------------------------------------------------------------------------------
    // 3. A defensive target respects the live band's ceiling exactly.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The depth half of the same defect for a <see cref="TacticalJob.Marker"/>. The allocator's anchor
    /// here is 14 m; the live band's ceiling is 6 m (the ball is deep in this team's half and the line has
    /// dropped with it), so the unclamped anchor is 8 m in front of the last line — a marker standing in
    /// the space the through ball goes through rather than goal-side of the man at 12 m.
    ///
    /// Pre-fix the base candidate is emitted at 14 m and an adjusted one even further out, so both the
    /// "inside the band" and the "one candidate" assertions fail.
    /// </summary>
    [Fact]
    public void AIB09_DefensiveJobTarget_RespectsTheLiveLineBandCeiling()
    {
        float minProjX = -Pitch.HalfLength + 5f;
        const float maxProjX = 6f;
        Vector2 selfXZ = new(16f, 8f);
        Vector2 anchorXZ = new(14f, 4f);
        var generator = new CandidateGenerator(Pitch, communication: null,
            new AiRuntimeConfig { EnableCombinations = false });

        generator.GenerateOffBallCandidates(
            DefenderObservation(
                selfXZ, anchorXZ, ballXZ: new Vector2(-14f, 0f), TacticalJob.Marker, TeamId.Away,
                [Opponent(AttackerId, TacticalRole.Striker, 12f, 4f)],
                Band(proj: maxProjX, min: minProjX, max: maxProjX)),
            OpponentGoal, OwnGoal, selfXZ, isPrimaryBallResponder: false, offsideLineProjX: 0f);

        (int count, UtilityCandidate only) = SingleJobCandidate(generator, TacticalJob.Marker);

        Assert.Equal(1, count);
        Assert.True(only.TargetXZ.HasValue);
        float targetProj = only.TargetXZ!.Value.X;
        Assert.True(targetProj <= maxProjX + 1e-3f,
            $"the marker target {targetProj:F2} m sits forward of the live line ceiling {maxProjX:F1} m");
        Assert.True(targetProj >= minProjX - 1e-3f, "the marker target left the band's floor");
        Assert.Equal(maxProjX, targetProj, precision: 3);
    }

    /// <summary>
    /// AIB-09 boundary: the replacement terms are bounded, so the total stays in the unit
    /// <see cref="CandidateGenerator.IsDefensiveJob"/>'s base utility already uses. The job base is in
    /// [0.90, 1.00] and each of the two defensive terms is weighted by
    /// <see cref="CandidateGenerator.DefensivePositionWeight"/>, so a defensive candidate's utility can
    /// never exceed <c>1.00 + 2 x weight</c> nor fall below <c>0.90 - travel</c> (travel is
    /// <c>min(distance, 40) / 30</c>, unchanged). A new unbounded scale would fail this.
    /// </summary>
    [Fact]
    public void AIB09_DefensiveJobUtility_StaysInsideTheJobBaseBand()
    {
        Vector2 selfXZ = new(14f, -6f);
        Vector2 anchorXZ = new(9f, 0f);
        var generator = new CandidateGenerator(Pitch, communication: null,
            new AiRuntimeConfig { EnableCombinations = false });

        generator.GenerateOffBallCandidates(
            DefenderObservation(
                selfXZ, anchorXZ, ballXZ: new Vector2(-30f, 20f), TacticalJob.ProtectGap, TeamId.Away,
                [Opponent(AttackerId, TacticalRole.Striker, 10f, 0f)],
                DefensiveLineBand.None),
            OpponentGoal, OwnGoal, selfXZ, isPrimaryBallResponder: false, offsideLineProjX: 0f);

        (_, UtilityCandidate only) = SingleJobCandidate(generator, TacticalJob.ProtectGap);
        float travel = MathF.Min(Vector2.Distance(selfXZ, anchorXZ), 40f) / 30f;
        float upper = 1.00f + 2f * CandidateGenerator.DefensivePositionWeight;

        Assert.True(only.UtilityScore <= upper + 1e-3f,
            $"defensive job utility {only.UtilityScore:F3} exceeds the bounded band top {upper:F3}");
        Assert.True(only.UtilityScore >= 0.90f - travel - 1e-3f,
            $"defensive job utility {only.UtilityScore:F3} falls below the job base less travel");
    }

    // ---------------------------------------------------------------------------------------------
    // 4. The attacking path is unchanged: still two candidates, still stepped out.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// No regression of the attacking case the same method serves. A <see cref="TacticalJob.DepthRun"/>
    /// whose run paths are all rejected by their geometry falls back to the job candidates (the documented
    /// A5.1/A5.2 fallback), and the pre-AIB-09 behaviour is that an attacking duty offers the anchor plus
    /// the point stepped out of the nearest opponent's cover shadow.
    ///
    /// Fixture: self and anchor at (32, 0), a defender at (34, 2), the offside line at 34 m and no space
    /// behind it — every run kind is refused, so the fallback is deterministic. Pre-fix and post-fix both
    /// emit <c>Job:DepthRun</c> and <c>Job:DepthRunAdj</c>; this test fails if the defensive branch ever
    /// swallows the attacking one.
    /// </summary>
    [Fact]
    public void AIB09_AttackingJob_StillGetsItsSteppedOutAlternative()
    {
        Vector2 selfXZ = new(32f, 0f);
        Vector2 anchorXZ = new(32f, 0f);
        var generator = new CandidateGenerator(Pitch, communication: null,
            new AiRuntimeConfig { EnableCombinations = false });

        generator.GenerateOffBallCandidates(
            DefenderObservation(
                selfXZ, anchorXZ, ballXZ: new Vector2(0f, 0f), TacticalJob.DepthRun, TeamId.Home,
                [Opponent(AttackerId, TacticalRole.CenterBack, 34f, 2f),
                 TeamMate(CarrierId, new Vector2(0f, 0f))],
                DefensiveLineBand.None),
            OpponentGoal, OwnGoal, selfXZ, isPrimaryBallResponder: false, offsideLineProjX: 34f);

        Assert.False(CandidateGenerator.IsDefensiveJob(TacticalJob.DepthRun));

        (int count, UtilityCandidate source) = SingleJobCandidate(generator, TacticalJob.DepthRun);
        Assert.Equal(2, count);
        Assert.False(source.Key.Adjusted);

        UtilityCandidate? adjusted = AdjustedJobCandidate(generator, TacticalJob.DepthRun);
        Assert.True(adjusted.HasValue, "an attacking duty lost its stepped-out alternative");
        Assert.True(source.TargetXZ.HasValue && adjusted!.Value.TargetXZ.HasValue);
        Vector2 sourceTarget = XZ(source.TargetXZ!.Value);
        Vector2 adjustedTarget = XZ(adjusted.Value.TargetXZ!.Value);
        Assert.True(Vector2.Distance(sourceTarget, adjustedTarget) > 1.5f,
            $"the adjusted attacking target only moved {Vector2.Distance(sourceTarget, adjustedTarget):F2} m");
        Assert.Equal(32f, sourceTarget.X, precision: 3);
    }

    // ---------------------------------------------------------------------------------------------
    // 5. The scoring rule: goal-side beats ball-side, in identical pictures.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The defect in one comparison. Two observations are identical except for where the duty anchor is:
    /// (9, 0) is one metre <b>goal-side</b> of the attacker at (10, 0), (19, 0) is nine metres
    /// <b>ball-side</b> of him. Self is the same distance from both (7.81 m), so the travel cost and the
    /// job base cancel exactly and only the position terms decide.
    ///
    /// Pre-fix arithmetic (ball at (−30, 20), so both lanes are clear and the attacker is behind the
    /// goal-side target): goal-side = 0.98 + 0.30 x 1.00 (lane) + 0.30 x 0.111 (space at 1 m) = 1.313;
    /// ball-side = 0.98 + 0.30 x 1.00 (lane) + 0.30 x 1.00 (space at 9 m) = 1.580 — the target standing
    /// nine metres ahead of the man it covers outscores the one covering him by 0.267. Post-fix the
    /// goal-side target scores 0.98 + 0.30 x (0.625 goal-side + 1.00 on the runner's lane) = 1.468 and the
    /// ball-side one scores 0.98 + 0.30 x (0 + 0) = 0.980.
    /// </summary>
    [Fact]
    public void AIB09_AGoalSideDefensiveTarget_OutscoresOneThatStoodAheadOfTheAttacker()
    {
        Vector2 selfXZ = new(14f, -6f);
        Vector2 ballXZ = new(-30f, 20f);
        Vector2 attackerXZ = new(10f, 0f);

        float goalSideUtility = DefensiveJobUtility(TacticalJob.ProtectGap, selfXZ, ballXZ, attackerXZ,
            anchorXZ: new Vector2(9f, 0f));
        float aheadUtility = DefensiveJobUtility(TacticalJob.ProtectGap, selfXZ, ballXZ, attackerXZ,
            anchorXZ: new Vector2(19f, 0f));

        Assert.Equal(
            Vector2.Distance(selfXZ, new Vector2(9f, 0f)),
            Vector2.Distance(selfXZ, new Vector2(19f, 0f)),
            precision: 3);

        Assert.True(goalSideUtility > aheadUtility + 0.20f,
            $"a goal-side defensive target ({goalSideUtility:F3}) must outscore one that stepped in front " +
            $"of the covered attacker ({aheadUtility:F3}); pre-fix the ball-side target won by ~0.267");
    }

    // ---------------------------------------------------------------------------------------------
    // 6. FindVacancy clamps the protected gap to the live line band.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The <see cref="TacticalJob.ProtectGap"/> region half of the finding. A centre-back steps out of his
    /// slot to press; the slot is then protected by a teammate. The slot is the static formation anchor at
    /// 15 m (away side, so attack-relative projection −15 m), but the live band's ceiling is well behind
    /// it: the ball is at 5 m with no runner and the keeper is deliberately slow, so the keeper cap puts
    /// <see cref="DefensiveLineBand.MaxProjX"/> at about −18 m.
    ///
    /// Pre-fix the request's region is the unclamped static slot (proj −15 m): the protector is sent three
    /// metres forward of the last line — the audit's <c>COVER_GAP</c> running past the man he should be
    /// covering. Post-fix the region's projection is exactly the band's ceiling and the owner's
    /// ProtectGap anchor inherits it, while the slot's lateral position is untouched.
    /// </summary>
    [Fact]
    public void AIB09_FindVacancy_ClampsTheProtectedGapToTheLiveLineBand()
    {
        const int stepOutCentreBack = 202;
        const int otherCentreBack = 203;
        var awayKeeper = Player(201, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(48f, 0f, 0f), -1f,
            SlowKeeperAttributes);
        var stepOut = Player(stepOutCentreBack, TeamId.Away, TacticalRole.CenterBack,
            new Vector3(15f, 0f, -6f), -1f);
        var other = Player(otherCentreBack, TeamId.Away, TacticalRole.CenterBack,
            new Vector3(16f, 0f, 6f), -1f);
        var homeKeeper = Player(101, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-45f, 0f, 0f));
        var homeCarrier = Player(CarrierId, TeamId.Home, TacticalRole.CentralMidfielder,
            new Vector3(5f, 0f, 0f));
        var players = new List<FootballerState> { homeKeeper, homeCarrier, awayKeeper, stepOut, other };

        foreach (FootballerState player in players) player.TacticalAnchor = player.Position;

        // The formations are derived from the shape positions, so they are assigned before the press moves
        // the centre-back out of his slot (the way the engine assigns them from the kickoff layout).
        TeamFormation homeFormation = FormationFor(TeamId.Home, players, 1f);
        TeamFormation awayFormation = FormationFor(TeamId.Away, players, -1f);

        // Then the centre-back presses: eight metres out of his slot and goal-side of it.
        stepOut.Position = new Vector3(7f, 0f, -6f);

        var ball = BallAt(homeCarrier.Position);
        var possession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home,
            homeCarrier.Id, null);
        var coordinator = new TeamJobCoordinator(Pitch, new AiRuntimeConfig());
        RunPass(coordinator, players, ball, possession, TeamId.Home, homeFormation, awayFormation,
            tick: 0, awayResponderId: stepOutCentreBack);

        DefensiveLineBand band = coordinator.LineBandOf(TeamId.Away);
        Assert.True(band.IsValid, "the fixture produced no away line band, so the clamp has nothing to read");

        Assert.True(awayFormation.TryGetWorldAnchor(stepOutCentreBack, -1f, out Vector3 staticSlot));
        float staticSlotProj = staticSlot.X * -1f;
        Assert.True(staticSlotProj > band.MaxProjX + 0.01f,
            $"the fixture is not meaningful: static slot proj {staticSlotProj:F2} is not forward of the " +
            $"band ceiling {band.MaxProjX:F2}");

        CoverageRequest request = coordinator.CoverageOf(TeamId.Away);
        Assert.True(request.IsLive,
            $"the step-out fixture opened no live request (state {request.State}, reason '{request.Reason}')");
        Assert.Equal(stepOutCentreBack, request.SourcePlayerId);
        Assert.Equal(otherCentreBack, request.OwnerPlayerId);

        float regionProj = request.Region.X * -1f;
        Assert.Equal(band.MaxProjX, regionProj, precision: 3);
        Assert.True(regionProj < staticSlotProj - 0.01f,
            $"the protected region {regionProj:F2} m is not goal-side of the static slot {staticSlotProj:F2} m");
        Assert.Equal(staticSlot.Z, request.Region.Z, precision: 3);

        TeamJobAssignment duty = coordinator.Get(otherCentreBack)!.Value;
        Assert.Equal(TacticalJob.ProtectGap, duty.Job);
        Assert.Equal(band.MaxProjX, duty.Anchor.X * -1f, precision: 3);
    }

    /// <summary>
    /// AIB-09 fallback half: with no valid band the vacancy is the static slot exactly, as before the fix.
    /// A side with no line player at all (keeper and one defensive midfielder) evaluates
    /// <see cref="DefensiveLineBand.None"/>, and the allocator must not invent a depth it was never given.
    /// The fixture's only eligible covering candidate is the keeper, who cannot bid, so the request closes
    /// with <see cref="CoverageRequestState.NoBid"/> — but the region it was raised for is observable and is
    /// the unclamped slot.
    /// </summary>
    [Fact]
    public void AIB09_FindVacancy_WithoutAValidBand_KeepsTheStaticSlot()
    {
        const int defensiveMidfielder = 202;
        var awayKeeper = Player(201, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(48f, 0f, 0f), -1f);
        var midfielder = Player(defensiveMidfielder, TeamId.Away, TacticalRole.DefensiveMidfielder,
            new Vector3(20f, 0f, -10f), -1f);
        var homeKeeper = Player(101, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-45f, 0f, 0f));
        var homeCarrier = Player(CarrierId, TeamId.Home, TacticalRole.CentralMidfielder,
            new Vector3(0f, 0f, 0f));
        var players = new List<FootballerState> { homeKeeper, homeCarrier, awayKeeper, midfielder };

        foreach (FootballerState player in players) player.TacticalAnchor = player.Position;
        TeamFormation homeFormation = FormationFor(TeamId.Home, players, 1f);
        TeamFormation awayFormation = FormationFor(TeamId.Away, players, -1f);

        // The midfielder presses from his slot, leaving it vacant (11.7 m out, goal-side of the ball).
        midfielder.Position = new Vector3(10f, 0f, -4f);

        var ball = BallAt(homeCarrier.Position);
        var possession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Home,
            homeCarrier.Id, null);
        var coordinator = new TeamJobCoordinator(Pitch, new AiRuntimeConfig());
        RunPass(coordinator, players, ball, possession, TeamId.Home, homeFormation, awayFormation,
            tick: 0, awayResponderId: defensiveMidfielder);

        Assert.False(coordinator.LineBandOf(TeamId.Away).IsValid,
            "the fixture claims a band, so it cannot prove the no-band fallback");

        Assert.True(awayFormation.TryGetWorldAnchor(defensiveMidfielder, -1f, out Vector3 staticSlot));
        CoverageRequest request = coordinator.CoverageOf(TeamId.Away);
        Assert.Equal(CoverageRequestState.NoBid, request.State);
        Assert.Equal(defensiveMidfielder, request.SourcePlayerId);
        Assert.Equal(staticSlot.X, request.Region.X, precision: 3);
        Assert.Equal(staticSlot.Z, request.Region.Z, precision: 3);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures and helpers.
    // ---------------------------------------------------------------------------------------------

    private static readonly Vector3 OpponentGoal = new(Pitch.HalfLength, 0f, 0f);
    private static readonly Vector3 OwnGoal = new(-Pitch.HalfLength, 0f, 0f);

    /// <summary>
    /// A keeper whose 3 s sweep reach is one probe (6 m), so the band's keeper cap is deterministic and
    /// deep enough for the vacancy-clamp fixture to have a static slot forward of it. Attributes, not a
    /// live read: the reach model only takes the keeper's own body.
    /// </summary>
    private static readonly PlayerAttributes SlowKeeperAttributes = new()
    {
        MaxSprintSpeed = 4f,
        Acceleration = 3f,
        AgilityTurnRate = 5f
    };

    private static Vector2 XZ(Vector3 point) => new(point.X, point.Z);

    private static FootballerState Player(
        int id,
        TeamId team,
        TacticalRole role,
        Vector3 position,
        float attackingDirectionX = 1f,
        PlayerAttributes? attributes = null) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Attributes = attributes ?? new PlayerAttributes(),
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
        TeamId possessingTeam,
        TeamFormation homeFormation,
        TeamFormation awayFormation,
        int tick,
        int? homeResponderId = null,
        int? awayResponderId = null)
    {
        coordinator.Update(
            players, ball, possession, possessingTeam,
            homeResponderId, awayResponderId, homeDirX: 1f, tick,
            TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
            TeamBelief.FromTruth(TeamId.Home, players), TeamBelief.FromTruth(TeamId.Away, players),
            homeFormation, awayFormation, homeContext: null, awayContext: null);
    }

    /// <summary>
    /// The off-ball observation a defensive duty decides from: the team is defending, the ball is loose in
    /// this picture (so <see cref="PressPolicy"/> offers no primary act and the decision reaches the job
    /// candidates), and the duty anchor and line band are exactly what the fixture sets.
    /// </summary>
    private static FootballerObservation DefenderObservation(
        Vector2 selfXZ,
        Vector2 dutyAnchorXZ,
        Vector2 ballXZ,
        TacticalJob job,
        TeamId possessingTeam,
        IReadOnlyList<PerceivedEntity> visible,
        DefensiveLineBand lineBand) => new()
    {
        SelfId = SelfId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CenterBack,
        SelfPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = false,
        ControlQuality01 = 0f,
        PerceivedBallPosition = new Vector3(ballXZ.X, 0.11f, ballXZ.Y),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = possessingTeam,
        BallLastTouchedByPlayerId = possessingTeam == TeamId.Home ? CarrierId : AttackerId,
        BallLastTouchedByTeam = possessingTeam,
        BallControlState = BallControlState.Loose,
        TacticalAnchorPosition = new Vector3(dutyAnchorXZ.X, 0f, dutyAnchorXZ.Y),
        TacticalJob = job,
        TacticalJobAnchor = new Vector3(dutyAnchorXZ.X, 0f, dutyAnchorXZ.Y),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 10f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = 100,
        VisiblePlayers = visible,
        TeamLineBand = lineBand,
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.CenterBack),
        SelfAttributes = new PlayerAttributes()
    };

    private static PerceivedEntity Opponent(int id, TacticalRole role, float x, float z) =>
        new(id, TeamId.Away, role, new Vector3(x, 0f, z), Vector3.Zero, 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };

    private static PerceivedEntity TeamMate(int id, Vector2 positionXZ) =>
        new(id, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(positionXZ.X, 0f, positionXZ.Y),
            Vector3.Zero, 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };

    /// <summary>The band the allocator publishes on <see cref="FootballerObservation.TeamLineBand"/>.</summary>
    private static DefensiveLineBand Band(float proj, float min, float max) => new(
        IsValid: true,
        ProjX: proj,
        MinProjX: min,
        MaxProjX: max,
        AnchorMeanProjX: proj,
        FieldPosition01: 0.5f,
        PasserPressure01: 0.5f,
        RunThreat01: 0f,
        KeeperCoverage01: 0.5f,
        FieldShiftMeters: 0f,
        PressureShiftMeters: 0f,
        BallCapShiftMeters: 0f,
        RunShiftMeters: 0f,
        KeeperShiftMeters: 0f,
        DeepestRunnerId: 0,
        DeepestRunnerProjX: 0f,
        KeeperId: 0,
        KeeperReachMeters: 0f);

    /// <summary>Finds the single non-adjusted job candidate for <paramref name="job"/> and counts the set.</summary>
    private static (int Count, UtilityCandidate Source) SingleJobCandidate(
        CandidateGenerator generator,
        TacticalJob job)
    {
        int count = 0;
        UtilityCandidate? source = null;
        string tag = job.ToString();
        for (int index = 0; index < generator.CandidateCount; index++)
        {
            UtilityCandidate candidate = generator.Candidates[index];
            if (candidate.Key.Kind != CandidateKind.Job) continue;
            if (!string.Equals(candidate.Key.TargetTag, tag, StringComparison.Ordinal)) continue;
            count++;
            if (!candidate.Key.Adjusted) source = candidate;
        }

        Assert.True(source.HasValue, $"no '{tag}' job candidate was generated: {Describe(generator)}");
        return (count, source!.Value);
    }

    private static UtilityCandidate? AdjustedJobCandidate(CandidateGenerator generator, TacticalJob job)
    {
        string tag = job.ToString();
        for (int index = 0; index < generator.CandidateCount; index++)
        {
            UtilityCandidate candidate = generator.Candidates[index];
            if (candidate.Key.Kind != CandidateKind.Job) continue;
            if (!string.Equals(candidate.Key.TargetTag, tag, StringComparison.Ordinal)) continue;
            if (candidate.Key.Adjusted) return candidate;
        }

        return null;
    }

    private static string Describe(CandidateGenerator generator)
    {
        var names = new List<string>();
        for (int index = 0; index < generator.CandidateCount; index++)
            names.Add(generator.Candidates[index].ActionName);
        return names.Count == 0 ? "(empty candidate set)" : string.Join(" | ", names);
    }

    /// <summary>
    /// The utility of one defensive duty anchor in one picture. The band is <see cref="DefensiveLineBand.None"/>
    /// so the depth clamp cannot be what the comparison measures.
    /// </summary>
    private static float DefensiveJobUtility(
        TacticalJob job,
        Vector2 selfXZ,
        Vector2 ballXZ,
        Vector2 attackerXZ,
        Vector2 anchorXZ)
    {
        var generator = new CandidateGenerator(Pitch, communication: null,
            new AiRuntimeConfig { EnableCombinations = false });
        generator.GenerateOffBallCandidates(
            DefenderObservation(
                selfXZ, anchorXZ, ballXZ, job, TeamId.Away,
                [Opponent(AttackerId, TacticalRole.Striker, attackerXZ.X, attackerXZ.Y)],
                DefensiveLineBand.None),
            OpponentGoal, OwnGoal, selfXZ, isPrimaryBallResponder: false, offsideLineProjX: 0f);

        (_, UtilityCandidate source) = SingleJobCandidate(generator, job);
        Assert.True(source.TargetXZ.HasValue);
        return source.UtilityScore;
    }
}
