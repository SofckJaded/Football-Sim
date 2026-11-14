using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Scenarios;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A6.3 at the team level: the band the model computes has to actually reach the line, and marking
/// responsibility has to move explicitly when the threat crosses into another defender's zone. The
/// model-level clauses live in <see cref="DefensiveLineModelTests"/>.
/// </summary>
public sealed class LineDepthTests
{
    // ---------------------------------------------------------------------------------------------
    // One shared depth.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The whole line is told one depth: every defender holding a <see cref="TacticalJob.DefensiveLine"/>
    /// duty carries the same band in his observation as the allocator placed him from, and his own job
    /// target is the band's depth — allowing only the documented ball-side and far-side local exceptions.
    ///
    /// Eleven a side deliberately: a five-a-side field has one centre-back, so it rarely fields a line at
    /// all, and "one shared depth" is a claim about a back line.
    /// </summary>
    [Fact]
    public void TheDefensiveLine_HoldsOneSharedBand()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = 20260818UL,
            PlayersPerTeam = 11,
            HalfDurationSeconds = 90f
        });

        int samples = 0;
        for (int tick = 0; tick < 240; tick++)
        {
            engine.StepTick();
            if (tick % 7 != 0) continue;

            foreach (TeamId team in new[] { TeamId.Home, TeamId.Away })
            {
                DefensiveLineBand band = engine.TeamJobs.LineBandOf(team);
                if (!band.IsValid) continue;

                float attackDirX = team == TeamId.Home ? 1f : -1f;
                int linePlayers = 0;

                foreach (FootballerState player in engine.Players)
                {
                    if (player.Team != team || !player.IsActiveOnPitch) continue;
                    TeamJobAssignment? duty = engine.TeamJobs.Get(player.Id);
                    if (duty?.Job != TacticalJob.DefensiveLine) continue;

                    linePlayers++;
                    samples++;

                    // The band the player was told about is the band the allocator used.
                    DefensiveLineBand seen = engine.GetLastObservation(player.Id)?.TeamLineBand ?? default;
                    Assert.True(seen.IsValid, $"{team} #{player.Id} held a line duty with no band");
                    Assert.Equal(band.ProjX, seen.ProjX, precision: 3);

                    // And his own target is that depth, inside the documented local exceptions.
                    float target = duty.Value.Anchor.X * attackDirX;
                    Assert.InRange(target, band.MinProjX - 0.01f, band.MaxProjX + 0.01f);
                    Assert.True(MathF.Abs(target - band.ProjX) <=
                                MathF.Max(DefensiveLineModel.BallSideAdvanceMeters, DefensiveLineModel.FarSideTuckMeters) + 0.01f,
                        $"{team} #{player.Id} targeted {target:F1} m against a band at {band.ProjX:F1} m");
                }

                if (linePlayers < 2) continue;

                // A band with two or more men on it is a line, not a coincidence: they agree to within the
                // local exception, never more.
                float min = float.MaxValue, max = float.MinValue;
                foreach (FootballerState player in engine.Players)
                {
                    if (player.Team != team || !player.IsActiveOnPitch) continue;
                    if (engine.TeamJobs.Get(player.Id)?.Job != TacticalJob.DefensiveLine) continue;
                    float target = engine.TeamJobs.Get(player.Id)!.Value.Anchor.X * attackDirX;
                    min = MathF.Min(min, target);
                    max = MathF.Max(max, target);
                }

                Assert.True(max - min <=
                            DefensiveLineModel.BallSideAdvanceMeters + DefensiveLineModel.FarSideTuckMeters + 0.01f,
                    $"{team}'s line spans {max - min:F1} m from the ball-side man to the far-side one");
            }
        }

        Assert.True(samples > 0, "no defending sample produced a line duty");
    }

    /// <summary>
    /// The band is more than a copy of the shape: over a passage of play it moves the line away from its
    /// own formation anchors, and it always reports itself inside its own bounds.
    /// </summary>
    [Fact]
    public void TheBand_MovesTheLineOffItsFormationAnchors()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = 4321UL,
            PlayersPerTeam = 11,
            HalfDurationSeconds = 120f
        });

        float largest = 0f;
        int samples = 0;

        for (int tick = 0; tick < 400; tick++)
        {
            engine.StepTick();
            if (tick % 5 != 0) continue;

            foreach (TeamId team in new[] { TeamId.Home, TeamId.Away })
            {
                DefensiveLineBand band = engine.TeamJobs.LineBandOf(team);
                if (!band.IsValid) continue;

                samples++;
                Assert.True(float.IsFinite(band.ProjX));
                Assert.InRange(band.ProjX, band.MinProjX - 0.01f, band.MaxProjX + 0.01f);
                largest = MathF.Max(largest, MathF.Abs(band.ProjX - band.AnchorMeanProjX));
            }
        }

        Assert.True(samples > 0);
        Assert.True(largest > 1.5f,
            $"the band never moved more than {largest:F2} m off the anchors: it is a copy, not a decision");
    }

    // ---------------------------------------------------------------------------------------------
    // Explicit marking handovers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The fixture's ids, so a failure message names the player it is about.</summary>
    private const int HomeKeeper = 101;
    private const int HomeCbA = 102;
    private const int HomeCbB = 103;
    private const int HomePressingStriker = 109;
    private const int AwayKeeper = 209;
    private const int AwayStriker = 201;

    /// <summary>
    /// A threat that runs from one centre-back's zone into the other's is <b>handed over</b>: the event
    /// records who lost him, who took him and both slot distances, the new owner holds the marking job, and
    /// the old marker is back in the line on the same pass instead of being left running alongside.
    ///
    /// The two worlds differ only in the striker's movement — the same start, the same slots, the same ball.
    /// </summary>
    [Fact]
    public void AThreatCrossingZones_IsHandedOverExplicitly()
    {
        MatchSimulationEngine running = EngineFor(strikerRuns: true);
        for (int tick = 0; tick < 220; tick++) running.StepTick();

        Assert.True(running.TeamJobs.HandoverCount >= 1,
            "the striker crossed both centre-backs' zones and no handover was made");
        Assert.NotEmpty(running.TeamJobs.Handovers);

        MarkingHandover handover = running.TeamJobs.Handovers[0];
        Assert.Equal(AwayStriker, handover.ThreatPlayerId);
        Assert.Equal(HomeCbA, handover.FromPlayerId);
        Assert.Equal(HomeCbB, handover.ToPlayerId);
        Assert.True(handover.ToSlotDistance + 3f <= handover.FromSlotDistance,
            $"the new owner's slot ({handover.ToSlotDistance:F1} m) did not beat the old one's " +
            $"({handover.FromSlotDistance:F1} m) by the handover margin");

        // The job moved, and the man who lost it went back into the line rather than following.
        TeamJobAssignment? newOwner = running.TeamJobs.Get(HomeCbB);
        TeamJobAssignment? released = running.TeamJobs.Get(HomeCbA);
        Assert.NotNull(newOwner);
        Assert.Equal(TacticalJob.Marker, newOwner!.Value.Job);
        Assert.Equal(AwayStriker, newOwner.Value.TargetPlayerId);
        Assert.NotNull(released);
        Assert.NotEqual(TacticalJob.Marker, released!.Value.Job);
    }

    /// <summary>
    /// The control: the same threat standing in the same place is never handed over. A held marker keeps
    /// his man, which is what makes the handover above a response to the crossing rather than a churn.
    /// </summary>
    [Fact]
    public void AThreatWhoDoesNotMove_IsNotHandedOver()
    {
        MatchSimulationEngine standing = EngineFor(strikerRuns: false);
        for (int tick = 0; tick < 220; tick++) standing.StepTick();

        Assert.True(standing.TeamJobs.HandoverCount == 0,
            "a striker standing in one centre-back's zone was handed over: " + Describe(standing));

        TeamJobAssignment? marker = standing.TeamJobs.Get(HomeCbA);
        Assert.NotNull(marker);
        Assert.Equal(TacticalJob.Marker, marker!.Value.Job);
        Assert.Equal(AwayStriker, marker.Value.TargetPlayerId);
    }

    /// <summary>The handover log as a failure message, so a churn failure names its reason.</summary>
    private static string Describe(MatchSimulationEngine engine)
    {
        if (engine.TeamJobs.Handovers.Count == 0) return "no handovers";

        var text = new System.Text.StringBuilder();
        foreach (MarkingHandover handover in engine.TeamJobs.Handovers)
            text.Append(handover.Describe()).Append("; ");
        return text.ToString();
    }

    /// <summary>
    /// Two centre-backs, one away striker standing between them, and an away keeper holding the ball at the
    /// other end so the home side is defending without a centre-back being dragged into the press.
    /// </summary>
    private static MatchSimulationEngine EngineFor(bool strikerRuns)
    {
        var scenario = new MatchScenario
        {
            Name = strikerRuns ? "A6.3 marking handover (run)" : "A6.3 marking handover (standing)",
            Seed = 20261114UL,
            BallPosition = new Vector3(45.4f, 0f, 0f),
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = AwayKeeper
        };

        scenario.Add(MatchScenario.Player(HomeKeeper, TeamId.Home, TacticalRole.Goalkeeper,
            new Vector3(-48f, 0f, 0f), 1));
        scenario.Add(MatchScenario.Player(HomeCbA, TeamId.Home, TacticalRole.CenterBack,
            new Vector3(-25f, 0f, -8f), 4));
        scenario.Add(MatchScenario.Player(HomeCbB, TeamId.Home, TacticalRole.CenterBack,
            new Vector3(-25f, 0f, 8f), 5));
        scenario.Add(MatchScenario.Player(HomePressingStriker, TeamId.Home, TacticalRole.Striker,
            new Vector3(20f, 0f, 0f), 9).WithController(
            new FixedCommandController(ScenarioCommands.Hold(), "holding striker")));

        scenario.Add(MatchScenario.Player(AwayKeeper, TeamId.Away, TacticalRole.Goalkeeper,
            new Vector3(46f, 0f, 0f), 1).WithController(
            new FixedCommandController(ScenarioCommands.Hold(), "keeper holding the ball")));

        ScenarioPlayer striker = MatchScenario.Player(AwayStriker, TeamId.Away, TacticalRole.Striker,
            new Vector3(-18f, 0f, -8f), 9);
        scenario.Add(strikerRuns
            ? striker.WithController(new FixedCommandController(ScenarioCommands.Move(new Vector2(0f, 1f)), "running striker"))
            : striker.WithController(new FixedCommandController(ScenarioCommands.Hold(), "standing striker")));

        return new MatchSimulationEngine(scenario);
    }
}
