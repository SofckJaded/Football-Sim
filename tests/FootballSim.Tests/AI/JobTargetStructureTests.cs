using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Scenarios;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A3.4 exit evidence on the fixtures it names: off-ball targets are complementary.
///
/// The old allocator scored a single point per duty and could hand two teammates targets a few
/// centimetres apart — measured at 0.4 m between DefensiveCover and NearOutlet in `wide-overload-4v3`,
/// because a kept incumbent's region was not visible to the duties assigned after it. With regions
/// scored against the teammates already placed, the fixtures hold their closest pair above 8 m.
/// </summary>
public sealed class JobTargetStructureTests
{
    /// <summary>
    /// Closest two home <b>off-ball</b> targets must stay apart. Measured minimum across the four fixtures is
    /// 6.1 m, so 6 m is the assertion with margin rather than a pin on one frame.
    ///
    /// The <see cref="TacticalJob.BallResponder"/> is not an off-ball duty: he is the man on the ball (or the
    /// receiver a pass is travelling to), and his assignment carries his shape slot rather than a place the
    /// team is trying to occupy. Comparing that with a team-mate's target measured the carrier's drift from
    /// his own slot — a 5.98 m "closest pair" that A6.1's changed defensive movement surfaced in `box-cross`.
    /// `build-up-3v2` has only two outfield players, so it has a single off-ball duty and cannot express this
    /// property at all; `counterattack-3v3` is the fourth fixture instead, and `build-up-3v2` keeps its own
    /// "always offers a free receiver" test.
    /// </summary>
    [Theory]
    [InlineData("wide-overload-4v3")]
    [InlineData("counterattack-3v3")]
    [InlineData("winger-fullback")]
    [InlineData("box-cross")]
    public void TheOffBallTargets_AreNotStackedOnEachOther(string key)
    {
        ScenarioLibrary.Definition definition = ScenarioLibrary.All.Single(d => d.Key == key);
        var engine = new MatchSimulationEngine(definition.Build());

        float closest = float.MaxValue;
        int comparisons = 0;
        for (int tick = 1; tick <= 240; tick++)
        {
            engine.StepTick();
            if (tick % 6 != 0) continue;

            // The possession structure only: a defensive line and the cover behind it are *meant* to be
            // metres apart, and that is not the congestion this milestone is about.
            List<(int Id, TacticalJob Job, Vector3 Anchor)> targets =
                HomeTargets(engine)
                    .Where(t => IsPossessionDuty(t.Job) && t.Job != TacticalJob.BallResponder)
                    .ToList();
            for (int i = 0; i < targets.Count; i++)
            for (int j = i + 1; j < targets.Count; j++)
            {
                comparisons++;
                float distance = Vector2.Distance(
                    new Vector2(targets[i].Anchor.X, targets[i].Anchor.Z),
                    new Vector2(targets[j].Anchor.X, targets[j].Anchor.Z));
                if (distance < closest)
                {
                    closest = distance;
                    Assert.True(distance >= 6f,
                        $"{key} t={tick}: #{targets[i].Id}({targets[i].Job}) and #{targets[j].Id}({targets[j].Job}) " +
                        $"are {distance:F1} m apart");
                }
            }
        }

        Assert.True(comparisons > 0, $"{key} never had two possession duties to compare");
    }

    /// <summary>
    /// The build-up fixture exists to answer "is there somebody to pass to". Every sample must contain a
    /// non-carrier target that is at least 8 m from every opponent and inside a plausible pass from the
    /// ball — the property the region's space, connection and lane terms are for.
    /// </summary>
    [Fact]
    public void TheBuildUpFixture_AlwaysOffersAFreeReceiver()
    {
        ScenarioLibrary.Definition definition =
            ScenarioLibrary.All.Single(d => d.Key == "build-up-3v2");
        var engine = new MatchSimulationEngine(definition.Build());

        int samples = 0;
        int freeSamples = 0;

        for (int tick = 1; tick <= 240; tick++)
        {
            engine.StepTick();
            if (tick % 6 != 0) continue;
            samples++;

            int? carrier = engine.Possession.ControllingPlayerId;
            foreach ((int id, _, Vector3 anchor) in HomeTargets(engine))
            {
                if (id == carrier) continue;

                float nearestOpponent = float.MaxValue;
                foreach (FootballerState opponent in engine.Players)
                {
                    if (opponent.Team != TeamId.Away || !opponent.IsActiveOnPitch) continue;
                    nearestOpponent = MathF.Min(nearestOpponent, Vector2.Distance(
                        new Vector2(anchor.X, anchor.Z),
                        new Vector2(opponent.Position.X, opponent.Position.Z)));
                }

                float toBall = Vector2.Distance(
                    new Vector2(anchor.X, anchor.Z),
                    new Vector2(engine.Ball.Position.X, engine.Ball.Position.Z));

                if (nearestOpponent >= 8f && toBall <= 35f)
                {
                    freeSamples++;
                    break;
                }
            }
        }

        Assert.True(samples > 0);
        Assert.Equal(samples, freeSamples);
    }

    private static bool IsPossessionDuty(TacticalJob job) => job is
        TacticalJob.BallResponder or TacticalJob.NearOutlet or TacticalJob.ThirdPlayerOption
        or TacticalJob.FarSideWidth or TacticalJob.DepthRun or TacticalJob.Overlap
        or TacticalJob.DefensiveCover or TacticalJob.Shape;

    private static List<(int Id, TacticalJob Job, Vector3 Anchor)> HomeTargets(MatchSimulationEngine engine)
    {
        var targets = new List<(int, TacticalJob, Vector3)>();
        foreach (FootballerState player in engine.Players)
        {
            if (player.Team != TeamId.Home || !player.IsActiveOnPitch) continue;
            if (player.Role == TacticalRole.Goalkeeper) continue;

            TeamJobAssignment? job = engine.TeamJobs.Get(player.Id);
            if (job is null) continue;
            targets.Add((player.Id, job.Value.Job, job.Value.Anchor));
        }

        return targets;
    }
}
