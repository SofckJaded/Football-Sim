using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A5.1 — the bounded path set.
///
/// Before A5 an off-ball player had one target, the anchor of his team job. These tests pin the new
/// contract at the model level: which kinds a duty may run, that the set is bounded by configuration and
/// truncates the speculative end, that each kind's geometry respects the law (onside before release) and
/// the situation (no space behind the line, nobody to pin, no box nearby), and that the set is
/// deterministic and free of the kinds the milestone deliberately defers.
///
/// The fixture-level evidence (a real run in a real match) lives in <see cref="RunScenarioTests"/>.
/// </summary>
public sealed class RunPathSetTests
{
    private static readonly PitchDimensions Pitch = new();

    private const float HalfLength = 52.5f;
    private const float HalfWidth = 34f;

    // ---- helpers -------------------------------------------------------------------------------

    private static PerceivedEntity Entity(
        int id,
        TeamId team,
        TacticalRole role,
        float x,
        float z,
        Vector3? velocity = null,
        CapabilityEstimate? capability = null) =>
        new(id, team, role, new Vector3(x, 0f, z), velocity ?? Vector3.Zero, 0.95f)
        {
            Capability = capability ?? CapabilityEstimate.Generic
        };

    private static FootballerObservation Observation(
        TacticalJob duty,
        TacticalRole selfRole,
        Vector2 selfXZ,
        Vector2 dutyAnchorXZ,
        Vector2 ballXZ,
        int? carrierId,
        IReadOnlyList<PerceivedEntity> visible,
        float attackingDirectionX = 1f,
        TeamId? possessingTeam = TeamId.Home,
        RestDefence security = default) => new()
    {
        SelfId = 7,
        SelfTeam = TeamId.Home,
        SelfRole = selfRole,
        SelfPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = false,
        ControlQuality01 = 0f,
        PerceivedBallPosition = new Vector3(ballXZ.X, 0.11f, ballXZ.Y),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = possessingTeam,
        BallLastTouchedByPlayerId = carrierId ?? 8,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = possessingTeam == TeamId.Home ? BallControlState.Controlled : BallControlState.Loose,
        BallControllingPlayerId = possessingTeam == TeamId.Home ? carrierId : null,
        TacticalAnchorPosition = new Vector3(dutyAnchorXZ.X, 0f, dutyAnchorXZ.Y),
        TacticalJob = duty,
        TacticalJobAnchor = new Vector3(dutyAnchorXZ.X, 0f, dutyAnchorXZ.Y),
        AttackingDirectionX = attackingDirectionX,
        MatchTimeSeconds = 0f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = 100,
        VisiblePlayers = visible,
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(selfRole),
        SelfAttributes = new PlayerAttributes(),
        TeamSecurity = security
    };

    /// <summary>
    /// A wide possession: the carrier is on the right touchline at (10, 24) and the player under test is
    /// behind him at (0, 20). One away centre-back marks the middle at (26, 2).
    /// </summary>
    private static List<PerceivedEntity> WideWorld() => new()
    {
        Entity(8, TeamId.Home, TacticalRole.Winger, 10f, 24f),
        Entity(25, TeamId.Away, TacticalRole.CenterBack, 26f, 2f)
    };

    private static FootballerObservation WideObservation(
        TacticalJob duty = TacticalJob.Overlap,
        TacticalRole role = TacticalRole.FullBack,
        IReadOnlyList<PerceivedEntity>? visible = null) =>
        Observation(duty, role, new Vector2(0f, 20f), new Vector2(6f, 22f), new Vector2(10f, 24f), 8,
            visible ?? WideWorld(), security: new RestDefence(3, 1f, 0.2f, 0.2f, 0.1f, 0.7f));

    private static int Generate(
        RunCandidateGenerator generator,
        in FootballerObservation obs,
        float lineProjX,
        int maxPaths = 4) =>
        generator.Generate(obs, lineProjX, obs.Tick, maxPaths);

    private static bool ContainsKind(RunCandidateGenerator generator, RunKind kind)
    {
        for (int i = 0; i < generator.Count; i++)
            if (generator.PathAt(i).Kind == kind) return true;
        return false;
    }

    private static RunPath PathOfKind(RunCandidateGenerator generator, RunKind kind)
    {
        for (int i = 0; i < generator.Count; i++)
            if (generator.PathAt(i).Kind == kind) return generator.PathAt(i);
        throw new Xunit.Sdk.XunitException($"no {kind} path in the set ({Describe(generator)})");
    }

    private static string Describe(RunCandidateGenerator generator)
    {
        var kinds = new List<string>();
        for (int i = 0; i < generator.Count; i++) kinds.Add(generator.PathAt(i).Kind.ToString());
        return string.Join(",", kinds);
    }

    // ---- the duty's vocabulary ------------------------------------------------------------------

    public static TheoryData<TacticalJob> ReceivingDuties() => new()
    {
        TacticalJob.NearOutlet,
        TacticalJob.ThirdPlayerOption,
        TacticalJob.FarSideWidth,
        TacticalJob.DepthRun,
        TacticalJob.Overlap
    };

    [Theory]
    [MemberData(nameof(ReceivingDuties))]
    public void AReceivingDuty_OffersOnlyKindsFromItsOwnList(TacticalJob duty)
    {
        var generator = new RunCandidateGenerator(Pitch);
        // A deep outlet: 25 m behind the ball, so every short-support kind has something to do.
        FootballerObservation obs = Observation(
            duty, TacticalRole.CentralMidfielder, new Vector2(-18f, 14f), new Vector2(-16f, 14f),
            new Vector2(10f, 24f), 8, WideWorld(), security: new RestDefence(3, 1f, 0.2f, 0.2f, 0.1f, 0.7f));

        int count = Generate(generator, obs, lineProjX: 34f);

        Assert.True(count > 0, $"{duty} offered no path at all");
        Assert.True(count <= RunCandidateGenerator.MaxKindsPerDuty);

        Span<RunKind> kinds = stackalloc RunKind[RunCandidateGenerator.MaxKindsPerDuty];
        int kindCount = RunCandidateGenerator.KindsFor(duty, kinds);
        for (int i = 0; i < generator.Count; i++)
        {
            RunKind kind = generator.PathAt(i).Kind;
            bool allowed = false;
            for (int k = 0; k < kindCount; k++) allowed |= kinds[k] == kind;
            Assert.True(allowed, $"{duty} offered {kind}, which is not in its kind list");
        }
    }

    [Theory]
    [InlineData(TacticalJob.Shape)]
    [InlineData(TacticalJob.DefensiveLine)]
    [InlineData(TacticalJob.Marker)]
    [InlineData(TacticalJob.DefensiveCover)]
    [InlineData(TacticalJob.BallResponder)]
    [InlineData(TacticalJob.Presser)]
    [InlineData(TacticalJob.ProtectGap)]
    public void ANonReceivingDuty_RunsNoPath(TacticalJob duty)
    {
        var generator = new RunCandidateGenerator(Pitch);
        FootballerObservation obs = WideObservation(duty);

        Assert.Equal(0, Generate(generator, obs, lineProjX: 34f));
        Assert.Empty(KindsForDuty(duty));
    }

    [Fact]
    public void WithoutPossession_ThereIsNoRunIntoSpace()
    {
        var generator = new RunCandidateGenerator(Pitch);
        FootballerObservation obs = Observation(
            TacticalJob.DepthRun, TacticalRole.Striker, new Vector2(-8f, 0f), new Vector2(-6f, 0f),
            new Vector2(10f, 24f), 8, WideWorld(), possessingTeam: TeamId.Away);

        Assert.Equal(0, Generate(generator, obs, lineProjX: 34f));
    }

    [Fact]
    public void TheGoalkeeperNeverRunsAnOutfieldPath()
    {
        var generator = new RunCandidateGenerator(Pitch);
        FootballerObservation obs = WideObservation(TacticalJob.FarSideWidth, TacticalRole.Goalkeeper);

        Assert.Equal(0, Generate(generator, obs, lineProjX: 34f));
    }

    // ---- the bound ------------------------------------------------------------------------------

    [Fact]
    public void TheSetIsBoundedByConfiguration_AndTruncatesTheSpeculativeEnd()
    {
        var generator = new RunCandidateGenerator(Pitch);
        // A depth runner in the final third with space behind a line 44 m up the pitch, a defender to pin
        // and a box ahead: all four kinds exist, so the bound is the only thing deciding how many are
        // offered.
        List<PerceivedEntity> visible = new()
        {
            Entity(8, TeamId.Home, TacticalRole.Winger, 38f, 20f),
            Entity(25, TeamId.Away, TacticalRole.CenterBack, 36f, 0f),
            Entity(26, TeamId.Away, TacticalRole.CenterBack, 40f, 8f)
        };
        FootballerObservation obs = Observation(
            TacticalJob.DepthRun, TacticalRole.Striker, new Vector2(30f, 0f), new Vector2(32f, 0f),
            new Vector2(38f, 20f), 8, visible, security: new RestDefence(3, 1f, 0.2f, 0.2f, 0.1f, 0.7f));

        var all = new RunCandidateGenerator(Pitch);
        int full = Generate(all, obs, lineProjX: 44f, maxPaths: 4);
        Assert.Equal(4, full);

        var one = new RunCandidateGenerator(Pitch);
        int bounded = Generate(one, obs, lineProjX: 44f, maxPaths: 1);
        Assert.Equal(1, bounded);
        Assert.Equal(RunKind.InBehind, one.PathAt(0).Kind);

        var two = new RunCandidateGenerator(Pitch);
        Assert.Equal(2, Generate(two, obs, lineProjX: 44f, maxPaths: 2));
        Assert.Equal(RunKind.InBehind, two.PathAt(0).Kind);
        Assert.Equal(RunKind.Pin, two.PathAt(1).Kind);
    }

    // ---- geometry and law -------------------------------------------------------------------------

    [Fact]
    public void AnInBehindPath_MeetsTheBallAheadOfTheLineButOnside()
    {
        var generator = new RunCandidateGenerator(Pitch);
        List<PerceivedEntity> visible = new()
        {
            Entity(8, TeamId.Home, TacticalRole.CentralMidfielder, 10f, 20f),
            Entity(25, TeamId.Away, TacticalRole.CenterBack, 30f, 0f)
        };
        FootballerObservation obs = Observation(
            TacticalJob.DepthRun, TacticalRole.Striker, new Vector2(12f, 0f), new Vector2(14f, 0f),
            new Vector2(10f, 20f), 8, visible, security: new RestDefence(3, 1f, 0.2f, 0.2f, 0.1f, 0.7f));

        const float lineProjX = 30f;
        Assert.True(Generate(generator, obs, lineProjX) > 0);
        RunPath run = PathOfKind(generator, RunKind.InBehind);

        Assert.True(run.MeetXZ.X > 12f, $"the run must gain ground, not lose it ({run.MeetXZ})");
        Assert.True(run.MeetXZ.X <= lineProjX - RunCandidateGenerator.OnsideMarginMeters,
            $"the meeting point {run.MeetXZ.X:F1} is beyond the offside line {lineProjX} minus the margin");
        Assert.True(run.MeetTick > run.StartTick, "a run must take time");
        Assert.True(run.ExpiresTick > run.MeetTick, "the region must stay a place to meet the ball after arrival");
    }

    [Fact]
    public void WithNoSpaceBehindTheLine_ThereIsNoInBehindPath()
    {
        var generator = new RunCandidateGenerator(Pitch);
        List<PerceivedEntity> visible = new()
        {
            Entity(8, TeamId.Home, TacticalRole.CentralMidfielder, 10f, 20f),
            Entity(25, TeamId.Away, TacticalRole.CenterBack, 30f, 0f)
        };
        FootballerObservation obs = Observation(
            TacticalJob.DepthRun, TacticalRole.Striker, new Vector2(29f, 0f), new Vector2(29f, 0f),
            new Vector2(10f, 20f), 8, visible, security: new RestDefence(3, 1f, 0.2f, 0.2f, 0.1f, 0.7f));

        Generate(generator, obs, lineProjX: 30f);

        Assert.False(ContainsKind(generator, RunKind.InBehind),
            $"the runner is level with the line and there is nothing behind it to attack ({Describe(generator)})");
    }

    [Fact]
    public void APinPath_HoldsTheNearestDefendersLane()
    {
        var generator = new RunCandidateGenerator(Pitch);
        List<PerceivedEntity> visible = new()
        {
            Entity(8, TeamId.Home, TacticalRole.CentralMidfielder, 0f, 20f),
            Entity(25, TeamId.Away, TacticalRole.CenterBack, 18f, 6f)
        };
        FootballerObservation obs = Observation(
            TacticalJob.DepthRun, TacticalRole.Striker, new Vector2(12f, 2f), new Vector2(12f, 2f),
            new Vector2(0f, 20f), 8, visible, security: new RestDefence(3, 1f, 0.2f, 0.2f, 0.1f, 0.7f));

        Assert.True(Generate(generator, obs, lineProjX: 30f) > 0);
        RunPath pin = PathOfKind(generator, RunKind.Pin);

        // The pin holds the man, not a lane: the meeting point is in the defender's channel and level with
        // the line rather than beyond it.
        Assert.Equal(6f, pin.MeetXZ.Y, 1);
        Assert.True(pin.MeetXZ.X <= 30f - RunCandidateGenerator.OnsideMarginMeters + 0.01f);
        Assert.True(pin.MeetXZ.X > 12f);
    }

    [Fact]
    public void WithNobodyNearby_ThereIsNoPinPath()
    {
        var generator = new RunCandidateGenerator(Pitch);
        List<PerceivedEntity> visible = new()
        {
            Entity(8, TeamId.Home, TacticalRole.CentralMidfielder, 0f, 20f),
            Entity(25, TeamId.Away, TacticalRole.CenterBack, 40f, -30f)   // 45 m away: nobody to pin
        };
        FootballerObservation obs = Observation(
            TacticalJob.DepthRun, TacticalRole.Striker, new Vector2(12f, 2f), new Vector2(12f, 2f),
            new Vector2(0f, 20f), 8, visible, security: new RestDefence(3, 1f, 0.2f, 0.2f, 0.1f, 0.7f));

        Generate(generator, obs, lineProjX: 30f);

        Assert.False(ContainsKind(generator, RunKind.Pin), Describe(generator));
    }

    [Fact]
    public void ALateBoxArrival_TimesItselfThroughAHoldPoint()
    {
        var generator = new RunCandidateGenerator(Pitch);
        List<PerceivedEntity> visible = new()
        {
            Entity(8, TeamId.Home, TacticalRole.Winger, 30f, 24f),
            Entity(25, TeamId.Away, TacticalRole.CenterBack, 44f, 4f)
        };
        FootballerObservation obs = Observation(
            TacticalJob.DepthRun, TacticalRole.CentralMidfielder, new Vector2(20f, 6f), new Vector2(20f, 6f),
            new Vector2(30f, 24f), 8, visible, security: new RestDefence(3, 1f, 0.2f, 0.2f, 0.1f, 0.7f));

        Assert.True(Generate(generator, obs, lineProjX: 46f) > 0);
        RunPath arrival = PathOfKind(generator, RunKind.LateBoxArrival);

        Assert.True(arrival.IsHolding(arrival.StartTick), "a late arrival must hold before it attacks the region");
        Assert.Equal(arrival.HoldXZ, arrival.TargetFor(arrival.StartXZ, arrival.StartTick));
        Assert.Equal(arrival.MeetXZ, arrival.TargetFor(arrival.StartXZ, arrival.HoldUntilTick));
        Assert.True(arrival.MeetTick >= arrival.HoldUntilTick,
            $"the meeting tick {arrival.MeetTick} must be after the hold ends at {arrival.HoldUntilTick}");
        Assert.True(arrival.MeetXZ.X > arrival.HoldXZ.X,
            "the hold point must be goal-side of the box region the run attacks");
    }

    [Fact]
    public void ABoxArrival_IsNotOfferedFromTheOwnHalf()
    {
        var generator = new RunCandidateGenerator(Pitch);
        List<PerceivedEntity> visible = new()
        {
            Entity(8, TeamId.Home, TacticalRole.CentralMidfielder, -20f, 10f),
            Entity(25, TeamId.Away, TacticalRole.CenterBack, 30f, 0f)
        };
        FootballerObservation obs = Observation(
            TacticalJob.DepthRun, TacticalRole.CentralMidfielder, new Vector2(-14f, 6f), new Vector2(-14f, 6f),
            new Vector2(-20f, 10f), 8, visible, security: new RestDefence(3, 1f, 0.2f, 0.2f, 0.1f, 0.7f));

        Generate(generator, obs, lineProjX: 30f);

        Assert.False(ContainsKind(generator, RunKind.LateBoxArrival), Describe(generator));
        Assert.False(ContainsKind(generator, RunKind.BlindSideArrival), Describe(generator));
    }

    [Fact]
    public void DecoyPaths_AreDeclaredButNotGeneratedYet()
    {
        var generator = new RunCandidateGenerator(Pitch);
        List<PerceivedEntity> visible = new()
        {
            Entity(8, TeamId.Home, TacticalRole.CentralMidfielder, 10f, 24f),
            Entity(25, TeamId.Away, TacticalRole.CenterBack, 22f, 4f)
        };
        FootballerObservation obs = Observation(
            TacticalJob.DepthRun, TacticalRole.Striker, new Vector2(14f, 4f), new Vector2(14f, 4f),
            new Vector2(10f, 24f), 8, visible, security: new RestDefence(3, 1f, 0.2f, 0.2f, 0.1f, 0.7f));

        Generate(generator, obs, lineProjX: 34f);

        // A5.1 delivers support and depth before speculative decoys; the decoy's value is the defender's
        // bounded follow-versus-hold response, which is A5.6. Declaring the kind keeps the vocabulary
        // complete and this assertion keeps the deferral honest.
        Assert.False(ContainsKind(generator, RunKind.Decoy), Describe(generator));
        Assert.Contains(RunKind.Decoy, Enum.GetValues<RunKind>());
    }

    [Fact]
    public void TheSameInput_ProducesTheSamePathSet()
    {
        FootballerObservation obs = WideObservation();

        var first = new RunCandidateGenerator(Pitch);
        var second = new RunCandidateGenerator(Pitch);
        Generate(first, obs, 34f);
        Generate(second, obs, 34f);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first.PathAt(i), second.PathAt(i));
            Assert.Equal(first.TermsAt(i), second.TermsAt(i));
        }
    }

    /// <summary>The duty's own kind list, as an array, for assertions that want to enumerate it.</summary>
    private static RunKind[] KindsForDuty(TacticalJob duty)
    {
        Span<RunKind> buffer = stackalloc RunKind[RunCandidateGenerator.MaxKindsPerDuty];
        int count = RunCandidateGenerator.KindsFor(duty, buffer);
        var kinds = new RunKind[count];
        for (int i = 0; i < count; i++) kinds[i] = buffer[i];
        return kinds;
    }
}
