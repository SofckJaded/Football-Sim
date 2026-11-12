using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A3.4 — a job target is a region, and the region is scored against the ball, the teammates who
/// already have a target, the opponents the team believes in and the space around it.
///
/// These are monotonicity tests on the pure scorer: the same duty scored twice with one input changed
/// must move in the stated direction. That is the property the allocator relies on, and it is the only
/// way to state "rewards width/depth/staggering/connections" without pinning one frame of a match.
/// </summary>
public sealed class JobRegionScoringTests
{
    private static readonly PitchDimensions Pitch = new();

    private static FootballerState Opponent(int id, float x, float z) => new()
    {
        Id = id,
        Team = TeamId.Away,
        ShirtNumber = id,
        Role = TacticalRole.CenterBack,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(TacticalRole.CenterBack),
        Position = new Vector3(x, 0f, z),
        FacingXZ = new Vector2(-1f, 0f)
    };

    /// <summary>
    /// The home team's belief about the given away players. The scorer reads only positions, so this is
    /// the "what the team has seen" side of the knowledge boundary; the opponents' own team is not what
    /// is being queried.
    /// </summary>
    private static TeamBelief BeliefWith(params FootballerState[] opponents) =>
        opponents.Length == 0 ? new TeamBelief(TeamId.Home) : TeamBelief.FromTruth(TeamId.Home, opponents);

    private static float Score(
        TacticalJob job,
        float x,
        float z,
        Vector2 reference,
        TeamBelief opponents,
        IReadOnlyList<AssignedRegion>? teammates = null,
        int candidatePlayerId = 1,
        float attackingWidth01 = 0.65f)
    {
        var region = new JobRegion(new Vector3(x, 0f, z), JobRegionScoring.RadiusOf(job));
        return JobRegionScoring.Score(
            region, job, reference, teammates ?? System.Array.Empty<AssignedRegion>(),
            candidatePlayerId, opponents, attackDirX: 1f, Pitch, attackingWidth01);
    }

    [Fact]
    public void ARegionWithRoom_ScoresHigherThanOneUnderPressure()
    {
        TeamBelief far = BeliefWith(Opponent(20, 30f, 0f));
        TeamBelief near = BeliefWith(Opponent(20, 8f, 0f));

        float free = Score(TacticalJob.NearOutlet, 0f, 0f, new Vector2(-4f, 0f), far);
        float pressed = Score(TacticalJob.NearOutlet, 0f, 0f, new Vector2(-4f, 0f), near);

        Assert.True(free > pressed + 0.15f,
            $"a free outlet ({free:F2}) must score clearly above a pressed one ({pressed:F2})");
    }

    [Fact]
    public void AWidthDuty_RewardsTheTouchline_AndACentralDutyDoesNot()
    {
        TeamBelief none = BeliefWith();
        Vector2 reference = new(0f, 0f);

        // Same distance from the carrier, so only the width term can separate them.
        float wideTouchline = Score(TacticalJob.FarSideWidth, 8f, 23f, reference, none);
        float wideCentral = Score(TacticalJob.FarSideWidth, 23f, 8f, reference, none);
        Assert.True(wideTouchline > wideCentral + 0.1f,
            $"a width duty must prefer the touchline ({wideTouchline:F2} vs {wideCentral:F2})");

        float outletTouchline = Score(TacticalJob.NearOutlet, 8f, 23f, reference, none);
        float outletCentral = Score(TacticalJob.NearOutlet, 23f, 8f, reference, none);
        Assert.True(System.MathF.Abs(outletTouchline - outletCentral) < 0.05f,
            $"an outlet has no width preference ({outletTouchline:F2} vs {outletCentral:F2})");
    }

    [Fact]
    public void ADepthDuty_RewardsProgress_AndACoverDutyDoesNot()
    {
        TeamBelief none = BeliefWith();
        Vector2 reference = new(0f, 0f);

        float deep = Score(TacticalJob.DepthRun, 28f, 0f, reference, none);
        float shallow = Score(TacticalJob.DepthRun, 6f, 0f, reference, none);
        Assert.True(deep > shallow + 0.05f,
            $"a depth run must prefer progress ({deep:F2} vs {shallow:F2})");

        float coverDeep = Score(TacticalJob.DefensiveCover, 28f, 0f, reference, none);
        float coverShallow = Score(TacticalJob.DefensiveCover, 6f, 0f, reference, none);
        Assert.True(System.MathF.Abs(coverDeep - coverShallow) < 0.05f,
            $"a cover duty has no progress preference ({coverDeep:F2} vs {coverShallow:F2})");
    }

    [Fact]
    public void ARegionOnTopOfATeammate_IsWorthLessThanAChannelOfItsOwn()
    {
        TeamBelief none = BeliefWith();
        Vector2 reference = new(0f, 0f);
        var teammates = new[]
        {
            new AssignedRegion(2, new JobRegion(new Vector3(10f, 0f, 8f), 5f))
        };

        float stacked = Score(TacticalJob.ThirdPlayerOption, 10f, 8f, reference, none, teammates);
        float separate = Score(TacticalJob.ThirdPlayerOption, 12f, -12f, reference, none, teammates);

        Assert.True(separate > stacked + 0.3f,
            $"a teammate's channel must be worth less than a free one ({stacked:F2} vs {separate:F2})");
    }

    [Fact]
    public void WithNoTeammates_ThereIsNoLaneToHold()
    {
        // "No compulsory five-lane occupancy": with nobody placed, two lateral targets are worth the
        // same. A lane model would have forced one of them into a fixed channel.
        TeamBelief none = BeliefWith();
        Vector2 reference = new(0f, 0f);

        float right = Score(TacticalJob.NearOutlet, 6f, 14f, reference, none);
        float left = Score(TacticalJob.NearOutlet, 6f, -14f, reference, none);

        Assert.True(System.MathF.Abs(right - left) < 0.02f,
            $"without teammates neither side is preferred ({right:F2} vs {left:F2})");
    }

    [Fact]
    public void APassLaneIsClosedByABelievedOpponentOnIt()
    {
        TeamBelief blocked = BeliefWith(Opponent(20, 5f, 0f));
        TeamBelief clear = BeliefWith(Opponent(20, 5f, 14f));

        float openLane = JobRegionScoring.LaneOpenness(new Vector2(0f, 0f), new Vector2(10f, 0f), clear);
        float closedLane = JobRegionScoring.LaneOpenness(new Vector2(0f, 0f), new Vector2(10f, 0f), blocked);

        Assert.True(closedLane < 0.1f, $"an opponent on the line must close it ({closedLane:F2})");
        Assert.True(openLane > 0.9f, $"an opponent 14 m wide must leave it open ({openLane:F2})");
    }

    [Fact]
    public void TheRegionRadii_FollowTheDuty()
    {
        Assert.True(JobRegionScoring.RadiusOf(TacticalJob.FarSideWidth) >
                    JobRegionScoring.RadiusOf(TacticalJob.Presser));
        Assert.True(JobRegionScoring.WantsWidth(TacticalJob.FarSideWidth));
        Assert.False(JobRegionScoring.WantsWidth(TacticalJob.NearOutlet));
        Assert.True(JobRegionScoring.WantsDepth(TacticalJob.DepthRun));
        Assert.False(JobRegionScoring.WantsDepth(TacticalJob.Marker));
        Assert.True(JobRegionScoring.IsReceiving(TacticalJob.ThirdPlayerOption));
        Assert.False(JobRegionScoring.IsReceiving(TacticalJob.DefensiveLine));
    }
}
