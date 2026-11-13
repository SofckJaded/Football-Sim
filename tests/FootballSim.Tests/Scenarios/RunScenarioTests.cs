using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Scenarios;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.Scenarios;

/// <summary>
/// A5.1/A5.2 exit evidence on the fixtures the plan names.
///
/// Two questions. First, <c>high-line-run</c>: does an off-ball player really run into the space behind a
/// high line, with a meeting region that is onside before release, inside the range a team-mate can
/// actually deliver to, and timed rather than chased — and does he keep going once he has committed? That
/// is the "correctly timed playable runs" clause.
///
/// Second, <c>winger-fullback</c>: does the choice between overlap, underlap and support follow the
/// defender's response rather than a fixed preference? The check runs on the fixture's own geometry, in
/// two worlds that differ only in where the opposing defender stands, which is what makes the answer about
/// the response and nothing else.
/// </summary>
public sealed class RunScenarioTests
{
    private static readonly PitchDimensions Pitch = new();

    // ---- the high-line fixture --------------------------------------------------------------------

    [Fact]
    public void TheHighLineFixture_ProducesATimedPlayableRun()
    {
        var engine = new MatchSimulationEngine(ScenarioLibrary.HighLineRun());
        int strikerId = ScenarioLibrary.HighLineStriker;
        FootballerState striker = engine.Players.Single(p => p.Id == strikerId);
        float startX = striker.Position.X;

        RunIntent? adopted = null;
        float ballXAtAdoption = 0f;
        float ballZAtAdoption = 0f;
        float secondLastDefenderX = 0f;

        for (int tick = 1; tick <= 240 && adopted is null; tick++)
        {
            engine.StepTick();

            if (engine.GetController(strikerId) is not TacticalUtilityAiController ai) continue;
            if (ai.Runs.Intent is not RunIntent intent) continue;

            adopted = intent;
            ballXAtAdoption = engine.Ball.Position.X;
            ballZAtAdoption = engine.Ball.Position.Z;
            secondLastDefenderX = SecondLastAwayX(engine);
        }

        Assert.True(adopted.HasValue, "the striker never adopted a run against the high line");
        RunIntent run = adopted!.Value;

        Assert.Equal(RunKind.InBehind, run.Kind);
        Assert.Equal(TacticalJob.DepthRun, run.Duty);

        // It is a run, not a shuffle: the meeting region is metres ahead of where he started.
        Assert.True(run.Path.MeetXZ.X > startX + 4f,
            $"the meeting region {run.Path.MeetXZ.X:F1} must be ahead of the striker's start {startX:F1}");

        // Onside before release (A5.4 puts the margin here), and behind the line it attacks.
        Assert.True(run.Path.MeetXZ.X <= secondLastDefenderX - 0.5f,
            $"meeting at {run.Path.MeetXZ.X:F1} is level with or beyond the second-last defender at {secondLastDefenderX:F1}");

        // Playable: inside the band a team-mate can actually deliver into from where the ball was.
        float passDistance = Vector2.Distance(
            new Vector2(ballXAtAdoption, ballZAtAdoption), run.Path.MeetXZ);
        Assert.InRange(passDistance, RunScoring.MinUsefulPassMeters, RunScoring.MaxUsefulPassMeters);

        // Timed: a window opens after adoption and closes before the run is abandoned, and an in-behind
        // run has no hold point to wait at (that is what the box arrivals are for).
        Assert.True(run.Path.MeetTick > run.AdoptedTick, "the meeting tick must be in the future");
        Assert.True(run.Path.ExpiresTick > run.Path.MeetTick, "the region must outlive the arrival");
        Assert.Equal(run.Path.StartTick, run.Path.HoldUntilTick);

        // And he actually goes. Measured over the first second and a half of the fixture.
        float furthest = striker.Position.X;
        for (int tick = 0; tick < 90; tick++)
        {
            engine.StepTick();
            if (striker.Position.X > furthest) furthest = striker.Position.X;
        }

        Assert.True(furthest > startX + 4f,
            $"the striker only advanced to {furthest:F1} from {startX:F1}: the run was adopted, not run");
    }

    [Fact]
    public void ACommittedRun_KeepsClosingOnItsMeetingRegion()
    {
        var engine = new MatchSimulationEngine(ScenarioLibrary.HighLineRun());
        int strikerId = ScenarioLibrary.HighLineStriker;
        FootballerState striker = engine.Players.Single(p => p.Id == strikerId);

        RunIntent? run = null;
        float previousDistance = float.MaxValue;
        float worstBacktrack = 0f;
        int samples = 0;

        for (int tick = 1; tick <= 200; tick++)
        {
            engine.StepTick();

            if (engine.GetController(strikerId) is not TacticalUtilityAiController ai) continue;
            if (ai.Runs.Intent is not RunIntent live) continue;

            run ??= live;
            if (live.AdoptedTick != run.Value.AdoptedTick) continue;   // a new run: measure again

            // Only measure while the meeting region is where the intent froze it.
            float distance = Vector2.Distance(
                new Vector2(striker.Position.X, striker.Position.Z), live.Path.MeetXZ);
            if (samples > 0)
            {
                float backtrack = distance - previousDistance;
                if (backtrack > worstBacktrack) worstBacktrack = backtrack;
            }

            previousDistance = distance;
            samples++;
            if (samples >= 60) break;
        }

        Assert.True(run.HasValue, "no run was adopted in the fragment");
        Assert.True(samples >= 20, $"only {samples} ticks of a live run were measured");
        Assert.True(worstBacktrack < 1.0f,
            $"the runner backed away from his meeting region by {worstBacktrack:F2} m in one tick: " +
            "a committed run must not stop and restart on a small score change");
    }

    // ---- overlap, underlap or support, from the defender's response ---------------------------------

    private static PerceivedEntity Entity(int id, TeamId team, TacticalRole role, float x, float z) =>
        new(id, team, role, new Vector3(x, 0f, z), Vector3.Zero, 0.9f)
        {
            Capability = CapabilityEstimate.Generic
        };

    /// <summary>
    /// The winger/full-back world of the fixture, with the full-back deeper so a checking run is also on
    /// offer. The carrier is the fixture's winger at (10, 26); the away defender's position is the only
    /// thing the two worlds differ in.
    /// </summary>
    private static FootballerObservation FullbackWorld(float defenderX, float defenderZ, float selfX)
    {
        var visible = new List<PerceivedEntity>
        {
            Entity(ScenarioLibrary.WingerCarrier, TeamId.Home, TacticalRole.Winger, 10f, 26f),
            Entity(203, TeamId.Away, TacticalRole.FullBack, defenderX, defenderZ),
            Entity(201, TeamId.Away, TacticalRole.CenterBack, 30f, 4f)
        };

        return new FootballerObservation
        {
            SelfId = ScenarioLibrary.OverlapFullBack,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.FullBack,
            SelfPosition = new Vector3(selfX, 0f, 22f),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = false,
            ControlQuality01 = 0f,
            PerceivedBallPosition = new Vector3(9.4f, 0.11f, 26f),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = ScenarioLibrary.WingerCarrier,
            BallLastTouchedByTeam = TeamId.Home,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = ScenarioLibrary.WingerCarrier,
            TacticalAnchorPosition = new Vector3(selfX, 0f, 24f),
            TacticalJob = TacticalJob.Overlap,
            TacticalJobAnchor = new Vector3(selfX, 0f, 24f),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 0f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = 50,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.FullBack),
            SelfAttributes = new PlayerAttributes(),
            TeamSecurity = new RestDefence(3, 1f, 0.2f, 0.2f, 0.1f, 0.7f)
        };
    }

    private static float ScoreOf(FootballerObservation obs, RunKind kind, out bool offered)
    {
        var generator = new RunCandidateGenerator(Pitch);
        generator.Generate(obs, 46f, obs.Tick, 4);
        for (int i = 0; i < generator.Count; i++)
        {
            if (generator.PathAt(i).Kind != kind) continue;
            offered = true;
            return generator.TermsAt(i).Score;
        }

        offered = false;
        return float.NegativeInfinity;
    }

    [Fact]
    public void TheForwardRuns_AreOfferedForAWideCarrier()
    {
        FootballerObservation obs = FullbackWorld(defenderX: 14f, defenderZ: 28f, selfX: -8f);

        float overlap = ScoreOf(obs, RunKind.Overlap, out bool hasOverlap);
        float underlap = ScoreOf(obs, RunKind.Underlap, out bool hasUnderlap);
        float support = ScoreOf(obs, RunKind.CheckShort, out bool hasSupport);

        Assert.True(hasOverlap, "no overlap path offered");
        Assert.True(hasUnderlap, "no underlap path offered");
        Assert.True(hasSupport, "no support path offered");
        Assert.True(float.IsFinite(overlap) && float.IsFinite(underlap) && float.IsFinite(support));
    }

    [Fact]
    public void AFullBackOnTheOutside_MakesTheUnderlapTheBetterForwardRun()
    {
        // The defender stands beyond the carrier on the touchline: the overlap is played into his cover.
        FootballerObservation outside = FullbackWorld(defenderX: 14f, defenderZ: 28f, selfX: 0f);
        FootballerObservation inside = FullbackWorld(defenderX: 16f, defenderZ: 12f, selfX: 0f);

        float overlapWhenOutside = ScoreOf(outside, RunKind.Overlap, out _);
        float underlapWhenOutside = ScoreOf(outside, RunKind.Underlap, out _);
        float overlapWhenInside = ScoreOf(inside, RunKind.Overlap, out _);
        float underlapWhenInside = ScoreOf(inside, RunKind.Underlap, out _);

        Assert.True(underlapWhenOutside > overlapWhenOutside,
            $"a defender outside must make the inside run better (underlap {underlapWhenOutside:F2} " +
            $"vs overlap {overlapWhenOutside:F2})");

        Assert.True(overlapWhenInside > underlapWhenInside,
            $"a defender tucked inside must make the overlap better (overlap {overlapWhenInside:F2} " +
            $"vs underlap {underlapWhenInside:F2})");

        // The response, not a fixed preference: each run scores better in the world that leaves it open.
        Assert.True(overlapWhenInside > overlapWhenOutside);
        Assert.True(underlapWhenOutside > underlapWhenInside);
    }

    [Fact]
    public void WhenBothForwardLanesAreCovered_TheSupportRunIsTheBestOfTheThree()
    {
        // Two defenders: one outside, one in the half-space. Neither forward run has a lane to be found
        // in, so the checking run back toward the ball is the option that actually offers something.
        var visible = new List<PerceivedEntity>
        {
            Entity(ScenarioLibrary.WingerCarrier, TeamId.Home, TacticalRole.Winger, 10f, 26f),
            Entity(203, TeamId.Away, TacticalRole.FullBack, 14f, 28f),
            Entity(206, TeamId.Away, TacticalRole.DefensiveMidfielder, 15f, 12f),
            Entity(201, TeamId.Away, TacticalRole.CenterBack, 30f, 2f)
        };

        FootballerObservation obs = FullbackWorld(defenderX: 14f, defenderZ: 28f, selfX: -8f);
        FootballerObservation twoDefenders = CopyWithPlayers(obs, visible);

        float overlap = ScoreOf(twoDefenders, RunKind.Overlap, out _);
        float underlap = ScoreOf(twoDefenders, RunKind.Underlap, out _);
        float support = ScoreOf(twoDefenders, RunKind.CheckShort, out bool offered);

        Assert.True(offered, "the support run must still be on offer when the forward lanes are covered");
        Assert.True(support > overlap && support > underlap,
            $"with both forward lanes covered the support run should win (support {support:F2}, " +
            $"overlap {overlap:F2}, underlap {underlap:F2})");
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static float SecondLastAwayX(MatchSimulationEngine engine)
    {
        List<float> awayX = engine.Players
            .Where(p => p.Team == TeamId.Away && p.IsActiveOnPitch)
            .Select(p => p.Position.X)
            .OrderByDescending(x => x)
            .ToList();

        return awayX.Count >= 2 ? awayX[1] : awayX[0];
    }

    private static FootballerObservation CopyWithPlayers(
        FootballerObservation obs,
        IReadOnlyList<PerceivedEntity> visible) => new()
    {
        SelfId = obs.SelfId,
        SelfTeam = obs.SelfTeam,
        SelfRole = obs.SelfRole,
        SelfPosition = obs.SelfPosition,
        SelfVelocity = obs.SelfVelocity,
        SelfFacingXZ = obs.SelfFacingXZ,
        Stamina01 = obs.Stamina01,
        AvailableToKick = obs.AvailableToKick,
        ControlQuality01 = obs.ControlQuality01,
        PerceivedBallPosition = obs.PerceivedBallPosition,
        PerceivedBallVelocity = obs.PerceivedBallVelocity,
        PossessingTeam = obs.PossessingTeam,
        BallLastTouchedByPlayerId = obs.BallLastTouchedByPlayerId,
        BallLastTouchedByTeam = obs.BallLastTouchedByTeam,
        BallControlState = obs.BallControlState,
        BallControllingPlayerId = obs.BallControllingPlayerId,
        TacticalAnchorPosition = obs.TacticalAnchorPosition,
        TacticalJob = obs.TacticalJob,
        TacticalJobAnchor = obs.TacticalJobAnchor,
        AttackingDirectionX = obs.AttackingDirectionX,
        MatchTimeSeconds = obs.MatchTimeSeconds,
        CurrentMatchPhase = obs.CurrentMatchPhase,
        IsSetPieceTaker = obs.IsSetPieceTaker,
        Tick = obs.Tick,
        VisiblePlayers = visible,
        TeamProfile = obs.TeamProfile,
        SelfTendencies = obs.SelfTendencies,
        SelfAttributes = obs.SelfAttributes,
        TeamSecurity = obs.TeamSecurity
    };
}
