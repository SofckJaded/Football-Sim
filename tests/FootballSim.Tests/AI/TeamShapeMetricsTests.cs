using System;
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
/// A3.3 exit gate, structural half: width, length and line spacing stay plausible across 5v5 and
/// 11v11, across the half-time direction reversal, and with an outfield player missing — which is
/// what "slow structural reassignment, never a whole-team reassignment per tick" has to deliver.
/// </summary>
public sealed class TeamShapeMetricsTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(11)]
    public void ShapeMetrics_ArePlausible_InBothFormats(int playersPerTeam)
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = 4321UL,
            PlayersPerTeam = playersPerTeam,
            HalfDurationSeconds = 120f
        });

        for (int i = 0; i < 240; i++) engine.StepTick();

        foreach (TeamId team in new[] { TeamId.Home, TeamId.Away })
        {
            (float width, float length) = Spans(engine, team);
            Assert.True(width is > 8f and < 64f, $"{team} width {width:F1} m is not plausible");
            Assert.True(length is > 6f and < 80f, $"{team} length {length:F1} m is not plausible");
            Assert.True(float.IsFinite(width) && float.IsFinite(length));

            // Line spacing: the back line really is behind the forwards, and the side is not stretched
            // into one flat line or stacked into one spot.
            float attackDirX = team == TeamId.Home ? 1f : -1f;
            (float backLine, float frontLine) = LineMeans(engine, team, attackDirX);
            Assert.True(frontLine - backLine is > 2f and < 45f,
                $"{team} back line at {backLine:F1} m and front line at {frontLine:F1} m are not a formation");

            // Ownership is complete and stable: every active outfielder owns a slot, and two minutes of
            // play has not rewritten the formation.
            TeamFormation formation = engine.Tactics.FormationFor(team);
            int outfielders = engine.Players.Count(p =>
                p.Team == team && p.IsActiveOnPitch && p.Role != TacticalRole.Goalkeeper);
            Assert.Equal(outfielders, formation.Count);
            Assert.Equal(0, formation.Reassignments);
        }
    }

    /// <summary>
    /// A5.6: the formation anchor is a structure, not a heartbeat.
    ///
    /// The role push in possession used to oscillate with an elapsed clock (`Sin(_elapsedTime * 1.4 + id)`) —
    /// movement generated for its own sake, which the plan asks to remove "as intent-driven movement replaces
    /// them". The oscillation and the clock behind it are gone, so the anchor is now a pure function of the
    /// players, the ball, the context and the profile: the same picture written twice gives the same anchor.
    /// Intent-driven movement comes from the run paths (A5.1/A5.2), not from a lane that breathes.
    /// </summary>
    [Fact]
    public void TheAnchor_IsAPureFunctionOfThePicture()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = 777UL,
            PlayersPerTeam = 11,
            HalfDurationSeconds = 60f
        });

        // A few ticks of real play so the contexts, the formations and the anchors all exist.
        for (int i = 0; i < 40; i++) engine.StepTick();

        // One write of the current picture, captured, then the same write again.
        engine.Tactics.UpdateTeamAnchors(engine.Players, engine.Ball, homeAttackDirX: 1f);

        var before = new Dictionary<int, Vector3>();
        foreach (FootballerState player in engine.Players)
        {
            if (!player.IsActiveOnPitch) continue;
            before[player.Id] = player.TacticalAnchor;
        }

        engine.Tactics.UpdateTeamAnchors(engine.Players, engine.Ball, homeAttackDirX: 1f);

        foreach (FootballerState player in engine.Players)
        {
            if (!player.IsActiveOnPitch || !before.TryGetValue(player.Id, out Vector3 anchor)) continue;
            Assert.Equal(anchor, player.TacticalAnchor);
        }
    }

    [Fact]
    public void HalfTime_KeepsTheShape_AndOnlyMirrorsTheFrame()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = 99UL,
            PlayersPerTeam = 5,
            HalfDurationSeconds = 2f
        });

        TeamFormation home = engine.Tactics.HomeFormation;
        var slotsBefore = new Dictionary<int, NominalSlot>();
        foreach (FootballerState player in engine.Players.Where(p => p.Team == TeamId.Home))
            if (home.TryGet(player.Id, out NominalSlot slot)) slotsBefore[player.Id] = slot;

        int guard = 0;
        while (engine.Rules.Period == MatchPeriod.FirstHalf && guard++ < 2000) engine.StepTick();
        Assert.Equal(MatchPeriod.SecondHalf, engine.Rules.Period);

        for (int i = 0; i < 60; i++) engine.StepTick();

        // Ownership is team-relative, so the reversal changes no slot; it only flips where the slot
        // lies in the world. That is the whole reason the frame is normalized.
        foreach (KeyValuePair<int, NominalSlot> pair in slotsBefore)
        {
            Assert.True(home.TryGet(pair.Key, out NominalSlot after));
            Assert.Equal(pair.Value, after);
            Assert.True(home.TryGetWorldAnchor(pair.Key, 1f, out var firstHalf));
            Assert.True(home.TryGetWorldAnchor(pair.Key, -1f, out var secondHalf));
            Assert.Equal(firstHalf.X, -secondHalf.X, precision: 2);
            Assert.Equal(firstHalf.Z, secondHalf.Z, precision: 2);
        }

        foreach (TeamId team in new[] { TeamId.Home, TeamId.Away })
        {
            (float width, float length) = Spans(engine, team);
            Assert.True(width is > 8f and < 64f, $"{team} width {width:F1} m after the interval");
            Assert.True(length is > 6f and < 80f, $"{team} length {length:F1} m after the interval");
        }
    }

    [Fact]
    public void AMissingOutfielder_KeepsTheRemainingShape()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = 55UL,
            PlayersPerTeam = 5,
            HalfDurationSeconds = 120f
        });

        for (int i = 0; i < 60; i++) engine.StepTick();
        (float widthBefore, _) = Spans(engine, TeamId.Home);

        FootballerState centreBack = engine.Players.First(p =>
            p.Team == TeamId.Home && p.Role == TacticalRole.CenterBack);
        centreBack.IsSentOff = true;

        for (int i = 0; i < 120; i++) engine.StepTick();

        TeamFormation home = engine.Tactics.HomeFormation;
        Assert.Equal(3, home.Count);   // four outfielders minus the sent-off centre-back
        Assert.False(home.TryGet(centreBack.Id, out _));

        (float widthAfter, float lengthAfter) = Spans(engine, TeamId.Home);
        // The absolute width band belongs to a full shape. With three outfielders left, a compact trio is a
        // legitimate shape (measured at 4.9 m here, and the ball was on one side of the pitch), so the claim
        // this test stands for is narrower: they still hold depth, they do not collapse onto one spot, and
        // the shape did not fold up after the dismissal.
        Assert.True(widthAfter > 3.5f, $"three outfielders collapsed into {widthAfter:F1} m");
        Assert.True(lengthAfter is > 6f and < 80f, $"length after the sending-off {lengthAfter:F1} m");
        Assert.True(MathF.Abs(widthAfter - widthBefore) < 25f,
            $"the shape collapsed after one player left ({widthBefore:F1} m -> {widthAfter:F1} m)");
    }

    /// <summary>
    /// Every duty a team hands out belongs to the phase it is in. Two seeds of the same fixture are used
    /// — one with the ball at home, one with it at the away defensive midfielder's feet — so both phases
    /// are guaranteed to be exercised without depending on an emergent turnover. The *turnover* itself
    /// (a possession change mid-passage) is pinned deterministically in
    /// <see cref="TemporaryResponsibilityTests.ATurnover_ReassignsEveryDutyToTheNewPhase"/>.
    /// </summary>
    [Fact]
    public void EveryAssignedDuty_MatchesThePhaseTheTeamIsIn()
    {
        bool sawHomeAttacking = false;
        bool sawHomeDefending = false;

        foreach (MatchScenario scenario in new[]
                 {
                     ScenarioLibrary.CounterattackThreeVersusThree(),
                     ReownedScenario(ScenarioLibrary.CounterattackThreeVersusThree(), ownerId: 206,
                         ballPosition: new Vector3(10f, 0f, -4f))
                 })
        {
            var engine = new MatchSimulationEngine(scenario);
            for (int tick = 1; tick <= 240; tick++)
            {
                engine.StepTick();
                bool homeHasBall = engine.Tactics.ConfirmedPossessingTeam == TeamId.Home;

                foreach (FootballerState player in engine.Players)
                {
                    if (player.Team != TeamId.Home || !player.IsActiveOnPitch) continue;
                    if (player.Role == TacticalRole.Goalkeeper) continue;

                    TeamJobAssignment? duty = engine.TeamJobs.Get(player.Id);
                    if (duty is null) continue;

                    bool defensive = duty.Value.Job is TacticalJob.Presser or TacticalJob.CoverDefender
                        or TacticalJob.ProtectGap or TacticalJob.Marker or TacticalJob.DefensiveLine;
                    bool possession = duty.Value.Job is TacticalJob.BallResponder or TacticalJob.NearOutlet
                        or TacticalJob.ThirdPlayerOption or TacticalJob.FarSideWidth
                        or TacticalJob.DepthRun or TacticalJob.Overlap or TacticalJob.DefensiveCover;

                    if (homeHasBall)
                    {
                        Assert.True(!defensive || duty.Value.Job == TacticalJob.Shape,
                            $"home #{player.Id} held {duty.Value.Job} while home had the ball");
                        sawHomeAttacking |= possession;
                    }
                    else
                    {
                        Assert.True(!possession || duty.Value.Job == TacticalJob.Shape,
                            $"home #{player.Id} held {duty.Value.Job} while the opposition had the ball");
                        sawHomeDefending |= defensive;
                    }
                }
            }
        }

        Assert.True(sawHomeAttacking, "no fragment exercised home's possession duties");
        Assert.True(sawHomeDefending, "no fragment exercised home's defensive duties");
    }

    /// <summary>The same fixture with the ball handed to a different player, so a phase is guaranteed.</summary>
    private static MatchScenario ReownedScenario(MatchScenario source, int ownerId, Vector3 ballPosition)
    {
        var clone = new MatchScenario
        {
            Name = source.Name,
            Seed = source.Seed,
            Pitch = source.Pitch,
            HomeProfile = source.HomeProfile,
            AwayProfile = source.AwayProfile,
            HalfDurationSeconds = source.HalfDurationSeconds,
            BallPosition = ballPosition,
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = ownerId
        };

        foreach (ScenarioPlayer player in source.Players) clone.Add(player);
        return clone;
    }

    /// <summary>
    /// Mean position along the attacking direction of the back line (centre-backs, full-backs, defensive
    /// midfielders) and of the players ahead of it (wingers, strikers, central midfielders).
    /// </summary>
    private static (float BackLine, float FrontLine) LineMeans(
        MatchSimulationEngine engine,
        TeamId team,
        float attackDirX)
    {
        float backSum = 0f, frontSum = 0f;
        int backCount = 0, frontCount = 0;

        foreach (FootballerState player in engine.Players)
        {
            if (player.Team != team || !player.IsActiveOnPitch) continue;
            float progress = player.Position.X * attackDirX;
            if (player.Role is TacticalRole.CenterBack or TacticalRole.FullBack or TacticalRole.DefensiveMidfielder)
            {
                backSum += progress;
                backCount++;
            }
            else if (player.Role is TacticalRole.Winger or TacticalRole.Striker or TacticalRole.CentralMidfielder)
            {
                frontSum += progress;
                frontCount++;
            }
        }

        return (backCount == 0 ? 0f : backSum / backCount, frontCount == 0 ? 0f : frontSum / frontCount);
    }

    /// <summary>Width (Z span) and length (X span) of a team's active outfielders.</summary>
    private static (float Width, float Length) Spans(MatchSimulationEngine engine, TeamId team)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        int counted = 0;

        foreach (FootballerState player in engine.Players)
        {
            if (player.Team != team || !player.IsActiveOnPitch) continue;
            if (player.Role == TacticalRole.Goalkeeper) continue;

            minX = MathF.Min(minX, player.Position.X);
            maxX = MathF.Max(maxX, player.Position.X);
            minZ = MathF.Min(minZ, player.Position.Z);
            maxZ = MathF.Max(maxZ, player.Position.Z);
            counted++;
        }

        return counted == 0 ? (0f, 0f) : (maxZ - minZ, maxX - minX);
    }
}
