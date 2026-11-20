using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Rules;
using FootballSim.Scenarios;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A7.6 — archetypes as candidate/weight/perception preferences on <b>one</b> decision pipeline.
///
/// The rule that decides every test here: an archetype is a preference, never a capability and never a
/// second pipeline. So the evidence is in three shapes:
/// <list type="bullet">
/// <item><b>documented preference difference</b> — two identical observations differing only in the
///   archetype produce a weight difference in the expected direction and on the expected scale;</item>
/// <item><b>no capability difference</b> — those same two observations generate the same candidate set
///   (same identities, same count, same order), so nothing new became possible;</item>
/// <item><b>neutrality</b> — <see cref="PlayerArchetype.None"/> is the pre-A7.6 player exactly, which is what
///   makes <see cref="AiRuntimeConfig.EnableArchetypes"/> off the previous build rather than a near-miss.</item>
/// </list>
///
/// The four live consumers are covered one each: the role tendencies the scorers already read, the cross's
/// own preference, the off-ball run-kind preference, the scan attention policy, and the keeper's support
/// depth and sweep/claim contest window. The match-level A/B is the <c>--archetypes</c> probe.
/// </summary>
public sealed class PlayerArchetypeTests
{
    private static readonly PitchDimensions Pitch = new();

    private const float Dt = SimConstants.FixedDeltaTime;

    // =============================================================================================
    // Neutrality: no archetype is the pre-A7.6 player.
    // =============================================================================================

    [Fact]
    public void NoArchetype_IsThePreA76Player()
    {
        foreach (TacticalRole role in Enum.GetValues(typeof(TacticalRole)))
        {
            AssertTendenciesEqual(
                PlayerTendencyProfile.ForRole(role),
                PlayerTendencyProfile.For(role, PlayerArchetype.None));
        }

        Assert.Equal(ArchetypePreferences.None, PlayerArchetypeProfile.Preferences(PlayerArchetype.None));

        foreach (RunKind kind in Enum.GetValues(typeof(RunKind)))
            Assert.Equal(0f, PlayerArchetypeProfile.RunPreference(kind, PlayerArchetype.None));

        // The keeper preference helpers are the identity at no archetype, so a keeper who never mentions an
        // archetype stands and commits exactly where he did before A7.6.
        Assert.Equal(1f, PlayerArchetypeProfile.SupportDepthScale(PlayerTendencyBaselines.SweepAggression));
        Assert.Equal(1f, PlayerArchetypeProfile.SweepRaceMarginScale(PlayerTendencyBaselines.SweepAggression));
    }

    [Fact]
    public void ADefaultFootballer_HasNoArchetype_AndAllConsumersAreNeutral()
    {
        // The training drills, the locked human and every fixture that predates A7.6 build a FootballerState
        // without stating an archetype. That must be the neutral player, not a random default.
        var player = new FootballerState
        {
            Id = 7,
            Team = TeamId.Home,
            ShirtNumber = 7,
            Role = TacticalRole.Winger,
            Attributes = new PlayerAttributes(),
            Tendencies = new PlayerTendencies()
        };

        Assert.Equal(PlayerArchetype.None, player.Archetype);
        Assert.Equal(0f, PlayerArchetypeProfile.RunPreference(RunKind.HoldWidth, player.Archetype));
        AssertTendenciesEqual(new PlayerTendencies(), player.Tendencies);

        // And an observation that never states one reads as neutral, so a fixtures-built observation cannot
        // inherit a preference from somewhere else.
        FootballerObservation observation = Observation(player, PlayerArchetype.None);
        Assert.Equal(PlayerArchetype.None, observation.SelfArchetype);
    }

    // =============================================================================================
    // The seven archetypes lean their documented preferences, and stay on the 0..1 scale.
    // =============================================================================================

    [Fact]
    public void EachArchetype_LeansItsDocumentedPreferences()
    {
        PlayerTendencies cm = PlayerTendencyProfile.ForRole(TacticalRole.CentralMidfielder);
        PlayerTendencies dm = PlayerTendencyProfile.ForRole(TacticalRole.DefensiveMidfielder);
        PlayerTendencies fb = PlayerTendencyProfile.ForRole(TacticalRole.FullBack);
        PlayerTendencies winger = PlayerTendencyProfile.ForRole(TacticalRole.Winger);
        PlayerTendencies striker = PlayerTendencyProfile.ForRole(TacticalRole.Striker);
        PlayerTendencies keeper = PlayerTendencyProfile.ForRole(TacticalRole.Goalkeeper);

        // Playmaker: progressive and risk-tolerant, scans for the picture, shoots less.
        PlayerTendencies playmaker = PlayerTendencyProfile.For(TacticalRole.CentralMidfielder, PlayerArchetype.Playmaker);
        Assert.True(playmaker.DirectPassPreference01 > cm.DirectPassPreference01);
        Assert.True(playmaker.RiskTolerance01 > cm.RiskTolerance01);
        Assert.True(playmaker.ShootingAppetite01 < cm.ShootingAppetite01);
        Assert.True(playmaker.ScanAppetite01 > cm.ScanAppetite01);

        // Ball winner: the side's most aggressive presser.
        PlayerTendencies ballWinner = PlayerTendencyProfile.For(TacticalRole.DefensiveMidfielder, PlayerArchetype.BallWinner);
        Assert.True(ballWinner.PressingAggression01 > dm.PressingAggression01);
        Assert.True(ballWinner.ScanAppetite01 > dm.ScanAppetite01);

        // Inverted fullback: comfortable on the ball and licensed to leave the line.
        PlayerTendencies inverted = PlayerTendencyProfile.For(TacticalRole.FullBack, PlayerArchetype.InvertedFullBack);
        Assert.True(inverted.DribblePreference01 > fb.DribblePreference01);
        Assert.True(inverted.PositionalDiscipline01 < fb.PositionalDiscipline01);

        // Touchline winger: the delivery appetite and the take-on.
        PlayerTendencies touchline = PlayerTendencyProfile.For(TacticalRole.Winger, PlayerArchetype.TouchlineWinger);
        Assert.True(touchline.CrossPreference01 > PlayerTendencyBaselines.CrossPreference);
        Assert.True(touchline.DribblePreference01 > winger.DribblePreference01);

        // Inside forward: shoots and turns inside, and is the wide player who does not deliver.
        PlayerTendencies inside = PlayerTendencyProfile.For(TacticalRole.Winger, PlayerArchetype.InsideForward);
        Assert.True(inside.ShootingAppetite01 > winger.ShootingAppetite01);
        Assert.True(inside.CrossPreference01 < PlayerTendencyBaselines.CrossPreference);

        // Target forward: a target, not a dribbler, who shoots more than the plain striker.
        PlayerTendencies target = PlayerTendencyProfile.For(TacticalRole.Striker, PlayerArchetype.TargetForward);
        Assert.True(target.DribblePreference01 < striker.DribblePreference01);
        Assert.True(target.ShootingAppetite01 > striker.ShootingAppetite01);

        // Sweeper keeper: the sweep/claim preference and the scan appetite, with the shot-blocking angle
        // untouched (the depth preference is tested where it is used).
        PlayerTendencies sweeper = PlayerTendencyProfile.For(TacticalRole.Goalkeeper, PlayerArchetype.SweeperKeeper);
        Assert.True(sweeper.SweepAggression01 > keeper.SweepAggression01);
        Assert.True(sweeper.ScanAppetite01 > keeper.ScanAppetite01);
    }

    [Fact]
    public void TheComposition_StaysInsideTheScale_AndValidates()
    {
        foreach (TacticalRole role in Enum.GetValues(typeof(TacticalRole)))
        foreach (PlayerArchetype archetype in Enum.GetValues(typeof(PlayerArchetype)))
        {
            PlayerTendencies composed = PlayerTendencyProfile.For(role, archetype);

            AssertTendenciesInRange(composed);
            SimulationConfigValidation.ValidateTendencies(composed, $"{role}/{archetype}");
        }
    }

    [Fact]
    public void TheElevenASideDefault_FieldsEachOfTheSevenArchetypesOnce()
    {
        var perTeam = new Dictionary<PlayerArchetype, int>();
        for (int shirt = 1; shirt <= 11; shirt++)
        {
            var (role, _, _) = TeamTacticalCoordinator.GetFormationRoleAndSlot(shirt, 11);
            PlayerArchetype archetype = PlayerArchetypeProfile.ForShirt(role, shirt, 11);
            if (archetype == PlayerArchetype.None) continue;

            perTeam[archetype] = perTeam.GetValueOrDefault(archetype) + 1;
        }

        Assert.Equal(7, perTeam.Count);
        Assert.All(perTeam.Values, count => Assert.Equal(1, count));

        // The 5v5 squad maps its three attacking shirts onto the wide and central archetypes it fields.
        var smallSided = new List<PlayerArchetype>();
        for (int shirt = 1; shirt <= 5; shirt++)
        {
            var (role, _, _) = TeamTacticalCoordinator.GetFormationRoleAndSlot(shirt, 5);
            smallSided.Add(PlayerArchetypeProfile.ForShirt(role, shirt, 5));
        }

        Assert.Contains(PlayerArchetype.SweeperKeeper, smallSided);
        Assert.Contains(PlayerArchetype.TouchlineWinger, smallSided);
        Assert.Contains(PlayerArchetype.InsideForward, smallSided);
        Assert.Contains(PlayerArchetype.TargetForward, smallSided);
    }

    [Fact]
    public void TheRoleFallback_IsTheDocumentedDefault()
    {
        Assert.Equal(PlayerArchetype.SweeperKeeper, PlayerArchetypeProfile.DefaultFor(TacticalRole.Goalkeeper));
        Assert.Equal(PlayerArchetype.InvertedFullBack, PlayerArchetypeProfile.DefaultFor(TacticalRole.FullBack));
        Assert.Equal(PlayerArchetype.BallWinner, PlayerArchetypeProfile.DefaultFor(TacticalRole.DefensiveMidfielder));
        Assert.Equal(PlayerArchetype.Playmaker, PlayerArchetypeProfile.DefaultFor(TacticalRole.CentralMidfielder));
        Assert.Equal(PlayerArchetype.TouchlineWinger, PlayerArchetypeProfile.DefaultFor(TacticalRole.Winger));
        Assert.Equal(PlayerArchetype.TargetForward, PlayerArchetypeProfile.DefaultFor(TacticalRole.Striker));

        // None of the plan's seven archetypes is a centre-back: inventing one would be a role change.
        Assert.Equal(PlayerArchetype.None, PlayerArchetypeProfile.DefaultFor(TacticalRole.CenterBack));
    }

    // =============================================================================================
    // The match fields them, and the switch removes them (the A/B's two builds).
    // =============================================================================================

    [Fact]
    public void TheEngine_FieldsTheArchetypes_AndTheSwitchMakesTheSquadUniform()
    {
        var on = new MatchSimulationEngine(new PreMatchConfig());
        var home = on.Players.Where(p => p.Team == TeamId.Home).ToList();

        Assert.Equal(7, home.Select(p => p.Archetype).Where(a => a != PlayerArchetype.None).Distinct().Count());
        foreach (FootballerState player in on.Players)
            AssertTendenciesEqual(PlayerTendencyProfile.For(player.Role, player.Archetype), player.Tendencies);

        var off = new MatchSimulationEngine(new PreMatchConfig
        {
            Ai = new AiRuntimeConfig { EnableArchetypes = false }
        });
        foreach (FootballerState player in off.Players)
        {
            Assert.Equal(PlayerArchetype.None, player.Archetype);
            AssertTendenciesEqual(PlayerTendencyProfile.ForRole(player.Role), player.Tendencies);
        }

        // Deterministic assignment: the same configuration must field the same identities.
        var again = new MatchSimulationEngine(new PreMatchConfig());
        foreach (FootballerState player in on.Players)
        {
            FootballerState repeat = again.Players.Single(p => p.Id == player.Id);
            Assert.Equal(player.Archetype, repeat.Archetype);
        }
    }

    [Fact]
    public void AScenarioCanStateAnArchetype_AndTheDefaultComesFromTheRole()
    {
        // Default configuration: a scenario player that mentions no archetype gets the role's default.
        var defaultScenario = WideScenario(AiRuntimeConfig.Default);
        var defaultEngine = new MatchSimulationEngine(defaultScenario);
        Assert.Equal(PlayerArchetype.TouchlineWinger, PlayerById(defaultEngine, WideWingerId).Archetype);
        Assert.Equal(PlayerArchetype.None, PlayerById(defaultEngine, AwayCenterBackId).Archetype);

        // Switch off: the same scenario is the uniform squad...
        var offScenario = WideScenario(new AiRuntimeConfig { EnableArchetypes = false });
        var offEngine = new MatchSimulationEngine(offScenario);
        Assert.Equal(PlayerArchetype.None, PlayerById(offEngine, WideWingerId).Archetype);
        AssertTendenciesEqual(
            PlayerTendencyProfile.ForRole(TacticalRole.Winger),
            PlayerById(offEngine, WideWingerId).Tendencies);

        // ...unless the fixture states one, which is a property of the fixture's world, not of the default.
        var stated = WideScenario(new AiRuntimeConfig { EnableArchetypes = false });
        stated.WithPlayer(stated.FindPlayer(WideWingerId).WithArchetype(PlayerArchetype.InsideForward));
        var statedEngine = new MatchSimulationEngine(stated);
        FootballerState winger = PlayerById(statedEngine, WideWingerId);
        Assert.Equal(PlayerArchetype.InsideForward, winger.Archetype);
        AssertTendenciesEqual(
            PlayerTendencyProfile.For(TacticalRole.Winger, PlayerArchetype.InsideForward),
            winger.Tendencies);
    }

    // =============================================================================================
    // Evidence (a): two identical observations differing only in the archetype.
    // =============================================================================================

    [Fact]
    public void TwoIdenticalObservations_DifferingOnlyInArchetype_ProduceADocumentedPreferenceDifference()
    {
        // The same winger in the same world (pinned on the touchline, a far-post runner advertised): the only
        // difference is which archetype he is. The cross is offered to both — it is the same situation — and
        // the touchline winger prices it exactly the documented 0.14 higher. The pair shares the risk and
        // direct-pass deltas, so the whole difference is the cross affinity itself:
        // (0.75 - 0.40) x CrossPreferenceScale (= 0.40) = 0.14 scale units.
        FootballerObservation eager = CarrierObservation(PlayerArchetype.TouchlineWinger);
        FootballerObservation reluctant = CarrierObservation(PlayerArchetype.InsideForward);

        var comms = AnnounceBoxArrival();
        var eagerGenerator = new CandidateGenerator(Pitch, comms) { TimeOnBall = 0.8f };
        var reluctantGenerator = new CandidateGenerator(Pitch, comms) { TimeOnBall = 0.8f };
        eagerGenerator.GenerateOnBallCandidates(eager, OpponentGoal(), offsideLineProjX: 44f);
        reluctantGenerator.GenerateOnBallCandidates(reluctant, OpponentGoal(), offsideLineProjX: 44f);

        UtilityCandidate eagerCross = Candidate(eagerGenerator, "Cross");
        UtilityCandidate reluctantCross = Candidate(reluctantGenerator, "Cross");

        const float crossPreferenceScale = 0.40f;
        float expected = (0.75f - 0.40f) * crossPreferenceScale;
        Assert.Equal(expected, eagerCross.Terms.Preference - reluctantCross.Terms.Preference, 3);
        Assert.Equal(expected, eagerCross.UtilityScore - reluctantCross.UtilityScore, 3);

        // And the preference is the only thing that moved: the same candidates exist in the same order.
        AssertSameCandidateSet(eagerGenerator, reluctantGenerator);
    }

    [Fact]
    public void TheCandidateSet_IsIdenticalForEveryArchetype()
    {
        // The capability clause: no archetype may generate a candidate another player could not have. Every
        // archetype in the same world produces the identical candidate table; only scores differ.
        var comms = AnnounceBoxArrival();
        var baseline = new CandidateGenerator(Pitch, comms) { TimeOnBall = 0.8f };
        baseline.GenerateOnBallCandidates(CarrierObservation(PlayerArchetype.None), OpponentGoal(), 44f);

        foreach (PlayerArchetype archetype in Enum.GetValues(typeof(PlayerArchetype)))
        {
            if (archetype == PlayerArchetype.None) continue;

            var generator = new CandidateGenerator(Pitch, comms) { TimeOnBall = 0.8f };
            generator.GenerateOnBallCandidates(CarrierObservation(archetype), OpponentGoal(), 44f);
            AssertSameCandidateSet(baseline, generator);
        }
    }

    [Fact]
    public void TheShotPreference_LeansWithTheArchetypesAppetite()
    {
        // The same carrier 17 m from goal: an inside forward's shooting appetite outbids a touchline winger's.
        var insideGenerator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        var touchlineGenerator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        insideGenerator.GenerateOnBallCandidates(BylineObservation(PlayerArchetype.InsideForward), OpponentGoal(), 44f);
        touchlineGenerator.GenerateOnBallCandidates(
            BylineObservation(PlayerArchetype.TouchlineWinger), OpponentGoal(), 44f);

        UtilityCandidate insideShot = Candidate(insideGenerator, CandidateKind.Shot);
        UtilityCandidate touchlineShot = Candidate(touchlineGenerator, CandidateKind.Shot);

        // shooting: (0.68 + 0.12) vs (0.68 - 0.05) -> 0.17 x 0.40 = 0.068. The risk deltas are identical
        // (+0.05 both), so the range clause adds nothing to the difference.
        Assert.Equal(0.068f, insideShot.Terms.Preference - touchlineShot.Terms.Preference, 3);
    }

    // =============================================================================================
    // The off-ball run kind preference.
    // =============================================================================================

    [Fact]
    public void RunScoring_AppliesTheArchetypePreference_WithoutTouchingTheOtherTerms()
    {
        var holdWidth = RunPath(RunKind.HoldWidth, new Vector2(8f, 20f), new Vector2(12f, 24f));
        Vector2 carrierXZ = new(4f, 18f);

        RunScoreTerms neutral = RunScoring.Score(
            holdWidth, CarrierObservation(PlayerArchetype.None), carrierXZ, default, TeamBoxArrivals.None);
        RunScoreTerms winger = RunScoring.Score(
            holdWidth, CarrierObservation(PlayerArchetype.TouchlineWinger), carrierXZ, default, TeamBoxArrivals.None);

        // The touchline winger prefers holding width: 0.9 of a unit x the documented weight.
        Assert.Equal(0f, neutral.ArchetypePreference01);
        Assert.Equal(0.9f, winger.ArchetypePreference01, 3);
        Assert.Equal(
            PlayerArchetypeProfile.RunPreferenceWeight * 0.9f,
            winger.Score - neutral.Score, 4);

        // Nothing else about the path moved: the archetype is a preference, not a second geometry.
        Assert.Equal(neutral.ArrivalAdvantage01, winger.ArrivalAdvantage01);
        Assert.Equal(neutral.PassAvailability01, winger.PassAvailability01);
        Assert.Equal(neutral.ReceivingOrientation01, winger.ReceivingOrientation01);
        Assert.Equal(neutral.LineBreaks01, winger.LineBreaks01);
        Assert.Equal(neutral.Congestion01, winger.Congestion01);
        Assert.Equal(neutral.RoleUsefulness01, winger.RoleUsefulness01);
        Assert.Equal(neutral.TransitionExposure01, winger.TransitionExposure01);
        Assert.Equal(neutral.TravelMeters, winger.TravelMeters);

        // The same path is worth less to the archetype that wants it the least.
        RunScoreTerms inside = RunScoring.Score(
            holdWidth, CarrierObservation(PlayerArchetype.InsideForward), carrierXZ, default, TeamBoxArrivals.None);
        Assert.True(inside.Score < neutral.Score);
        Assert.Equal(-0.5f, inside.ArchetypePreference01, 3);
    }

    // =============================================================================================
    // Perception preference: the scan attention policy.
    // =============================================================================================

    [Fact]
    public void TheScanAppetite_RefreshesASightingSooner()
    {
        var planner = new ScanPlanner(AiRuntimeConfig.Default);
        int neutral = planner.RefreshAgeTicks;

        Assert.Equal(neutral, planner.RefreshAgeTicksFor(PlayerTendencyBaselines.ScanAppetite));
        Assert.True(planner.RefreshAgeTicksFor(0.85f) < neutral,
            "an eager player waits longer than the neutral policy before refreshing a sighting");
        Assert.True(planner.RefreshAgeTicksFor(0.15f) > neutral);
        Assert.InRange(planner.RefreshAgeTicksFor(1f), 1, neutral);
        Assert.InRange(planner.RefreshAgeTicksFor(0f), neutral, 600);
    }

    [Fact]
    public void AHighScanAppetite_TurnsTheHeadToTheBelievedThreatSooner()
    {
        // The same world (a loose ball, one opponent behind the observer), differing only in the observer's
        // scan appetite. With nothing else asking for a scan, the attention policy turns the head to the
        // believed threat as soon as it considers the sighting stale: the eager player does it earlier,
        // through the existing attention policy rather than a second sight model.
        (int eagerFirstThreatTick, int eagerThreatTicks) = RunScanAppetiteWorld(appetite01: 0.85f);
        (int neutralFirstThreatTick, int neutralThreatTicks) = RunScanAppetiteWorld(
            appetite01: PlayerTendencyBaselines.ScanAppetite);

        Assert.True(eagerFirstThreatTick > 0, "the eager player never turned to the believed threat");
        Assert.True(eagerThreatTicks > 0);
        Assert.True(
            neutralFirstThreatTick < 0 || eagerFirstThreatTick < neutralFirstThreatTick,
            $"eager turned at tick {eagerFirstThreatTick}, neutral at {neutralFirstThreatTick}");
        Assert.True(neutralThreatTicks <= eagerThreatTicks,
            $"eager spent {eagerThreatTicks} ticks on the threat, neutral {neutralThreatTicks}");
    }

    // =============================================================================================
    // Sweeper keeper: support depth and the contest window.
    // =============================================================================================

    [Fact]
    public void TheKeeperSupportDepth_FollowsTheArchetype_AndTheAreaCapHolds()
    {
        float neutral = GoalkeeperSupportModel.StandoffMeters(52f, teamHasBall: true);
        PlayerTendencies sweeper = PlayerTendencyProfile.For(
            TacticalRole.Goalkeeper, PlayerArchetype.SweeperKeeper);
        float sweeperDepth = GoalkeeperSupportModel.StandoffMeters(52f, teamHasBall: true, sweeper.SweepAggression01);

        Assert.Equal(
            GoalkeeperSupportModel.StandoffMeters(52f, teamHasBall: true, PlayerTendencyBaselines.SweepAggression),
            neutral, 3);
        Assert.True(sweeperDepth > neutral + 0.5f,
            $"the sweeper keeper stands {sweeperDepth:F1} m off his line, the neutral keeper {neutral:F1} m");

        // A keeper's duty does not override the law: the area cap still holds, and the defensive angle (a
        // shot-blocking duty, not a sweeping one) is not the archetype's to move.
        Assert.True(GoalkeeperSupportModel.StandoffMeters(120f, teamHasBall: true, sweeper.SweepAggression01) <=
                    GoalkeeperSupportModel.MaxSupportDepthMeters);
        Assert.Equal(
            GoalkeeperSupportModel.StandoffMeters(30f, teamHasBall: false),
            GoalkeeperSupportModel.StandoffMeters(30f, teamHasBall: false, sweeper.SweepAggression01));
    }

    [Fact]
    public void TheSweepRaceWindow_WidensWithTheArchetype_ButTheReachDoesNot()
    {
        // A race the keeper loses by 0.39 s: outside the neutral 0.35 s contest margin, inside the sweeper
        // keeper's 1.24 x 0.35 = 0.434 s. The ball, the keeper's position and the trajectory are identical.
        (FootballerObservation neutralObs, FootballerObservation sweeperObs, BallTrajectoryEstimate trajectory,
            float gapSeconds) = BuildContestedSweepRace();

        Assert.InRange(gapSeconds, 0.36f, 0.42f);

        GoalkeeperRace neutral = GoalkeeperDecisionModel.EvaluateSweep(neutralObs, trajectory, Pitch);
        GoalkeeperRace sweeper = GoalkeeperDecisionModel.EvaluateSweep(sweeperObs, trajectory, Pitch);

        Assert.False(neutral.ShouldGo, neutral.Describe());
        Assert.Equal(GoalkeeperRaceReason.OpponentFirst, neutral.Reason);

        Assert.True(sweeper.ShouldGo, sweeper.Describe());
        Assert.Equal(GoalkeeperRaceReason.Contested, sweeper.Reason);

        // Same race, same numbers: only the accepted window moved, so both report the same times and point.
        Assert.Equal(neutral.KeeperSeconds, sweeper.KeeperSeconds);
        Assert.Equal(neutral.OpponentSeconds, sweeper.OpponentSeconds);
        Assert.Equal(neutral.MeetingPoint, sweeper.MeetingPoint);
    }

    // =============================================================================================
    // Fixtures: the candidate-table world (a winger pinned on the touchline).
    // =============================================================================================

    private const int CarrierId = 8;
    private const int RunnerId = 9;
    private const int KeeperId = 25;

    private static FootballerObservation CarrierObservation(PlayerArchetype archetype) => Carrier(
        new Vector2(32.4f, 20f),
        new Vector2(32f, 20f),
        archetype,
        new PerceivedEntity(RunnerId, TeamId.Home, TacticalRole.Striker,
            new Vector3(40f, 0f, -2f), new Vector3(0f, 0f, -1f), 0.95f) { Capability = CapabilityEstimate.Generic },
        Opponent(KeeperId, TacticalRole.Goalkeeper, 50f, 0f),
        Opponent(26, TacticalRole.CenterBack, 44f, 4f),
        Opponent(27, TacticalRole.CenterBack, 45f, 12f),
        Opponent(28, TacticalRole.FullBack, 34f, 24f));

    private static FootballerObservation BylineObservation(PlayerArchetype archetype) => Carrier(
        new Vector2(46.4f, 16f),
        new Vector2(46f, 16f),
        archetype,
        new PerceivedEntity(RunnerId, TeamId.Home, TacticalRole.Striker,
            new Vector3(38f, 0f, 4f), new Vector3(1f, 0f, 1f), 0.95f) { Capability = CapabilityEstimate.Generic },
        Opponent(KeeperId, TacticalRole.Goalkeeper, 50f, 0f),
        Opponent(26, TacticalRole.CenterBack, 44f, -2f),
        Opponent(28, TacticalRole.FullBack, 48f, 16f),
        Opponent(29, TacticalRole.FullBack, 47f, 24f));

    private static FootballerObservation Carrier(
        Vector2 ballXZ,
        Vector2 selfXZ,
        PlayerArchetype archetype,
        params PerceivedEntity[] visible)
    {
        var players = new List<PerceivedEntity>(visible);
        return new FootballerObservation
        {
            SelfId = CarrierId,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.Winger,
            SelfArchetype = archetype,
            SelfPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(ballXZ.X, 0.11f, ballXZ.Y),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = CarrierId,
            BallLastTouchedByTeam = TeamId.Home,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = CarrierId,
            TacticalAnchorPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 20f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = 500,
            VisiblePlayers = players,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = PlayerTendencyProfile.For(TacticalRole.Winger, archetype),
            SelfAttributes = new PlayerAttributes()
        };
    }

    private static PerceivedEntity Opponent(int id, TacticalRole role, float x, float z) =>
        new(id, TeamId.Away, role, new Vector3(x, 0f, z), Vector3.Zero, 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };

    private static Vector3 OpponentGoal() => new(Pitch.HalfLength, 0f, 0f);

    private static MatchCommunicationContext AnnounceBoxArrival()
    {
        Vector2 region = new(41.5f, -6f);
        var comms = new MatchCommunicationContext();
        var path = new RunPath(
            RunKind.LateBoxArrival,
            StartXZ: region - new Vector2(6f, 0f),
            MeetXZ: region,
            MeetRadiusMeters: 5f,
            StartTick: 400,
            MeetTick: 560,
            ExpiresTick: 620,
            HoldUntilTick: 400,
            HoldXZ: region - new Vector2(6f, 0f),
            Confidence01: 0.85f);
        comms.Runs.Publish(TeamId.Home, RunnerId, new RunIntent(path, TacticalJob.DepthRun, 400), 400);
        return comms;
    }

    private static UtilityCandidate Candidate(CandidateGenerator generator, string tag)
    {
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            if (string.Equals(generator.Candidates[i].Key.TargetTag, tag, StringComparison.Ordinal))
                return generator.Candidates[i];
        }

        throw new Xunit.Sdk.XunitException($"no '{tag}' candidate was offered");
    }

    private static UtilityCandidate Candidate(CandidateGenerator generator, CandidateKind kind)
    {
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            if (generator.Candidates[i].Key.Kind == kind) return generator.Candidates[i];
        }

        throw new Xunit.Sdk.XunitException($"no {kind} candidate was offered");
    }

    /// <summary>Same candidate identities in the same order — the "no capability difference" assertion.</summary>
    private static void AssertSameCandidateSet(CandidateGenerator expected, CandidateGenerator actual)
    {
        Assert.Equal(expected.CandidateCount, actual.CandidateCount);
        for (int i = 0; i < expected.CandidateCount; i++)
        {
            Assert.Equal(expected.Candidates[i].Identity, actual.Candidates[i].Identity);
            Assert.Equal(expected.Candidates[i].Key, actual.Candidates[i].Key);
        }
    }

    private static RunPath RunPath(RunKind kind, Vector2 start, Vector2 meet) => new(
        kind,
        StartXZ: start,
        MeetXZ: meet,
        MeetRadiusMeters: 4f,
        StartTick: 500,
        MeetTick: 560,
        ExpiresTick: 600,
        HoldUntilTick: 500,
        HoldXZ: start,
        Confidence01: 0.85f);

    // =============================================================================================
    // Fixtures: scanning and the keeper race.
    // =============================================================================================

    private const int ScanSelfId = 2;
    private const int ScanRearId = 21;

    private static (int FirstThreatTick, int ThreatAttentionTicks) RunScanAppetiteWorld(
        float appetite01,
        int ticks = 150)
    {
        var perception = new PerceptionSystem(null, new AiRuntimeConfig());
        var rng = new SeededRandom(9001);
        FootballerState self = PlayerState(
            ScanSelfId, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero, new Vector2(1f, 0f),
            appetite01);
        FootballerState rear = PlayerState(
            ScanRearId, TeamId.Away, TacticalRole.CenterBack, new Vector3(-8f, 0f, 0f),
            new Vector2(-1f, 0f), PlayerTendencyBaselines.ScanAppetite);
        FootballerState[] players = { self, rear };

        // A loose ball, nobody receiving: the only reason to look is that the believed opponent has gone
        // stale, so the appetite's threshold is the one thing that decides when the head turns.
        var ball = new BallState
        {
            Position = new Vector3(8f, 0.11f, 0f),
            Velocity = new Vector3(-3f, 0f, 0f)
        };
        var possession = BallPossessionSnapshot.Loose;

        int firstThreatTick = -1;
        int threatTicks = 0;
        for (int tick = 1; tick <= ticks; tick++)
        {
            FootballerObservation observation = perception.BuildObservation(
                self, players, ball, possession.PossessionTeam, MatchPhase.FirstHalf, tick * Dt,
                attackingDirX: 1f, isSetPieceTaker: false, TeamTacticalProfile.PossessionControl, rng,
                possession, tick: tick);

            if (observation.Scan.Attention != ScanAttentionKind.Threat) continue;

            threatTicks++;
            if (firstThreatTick < 0) firstThreatTick = tick;
        }

        return (firstThreatTick, threatTicks);
    }

    /// <summary>
    /// A keeper race the keeper loses by about 0.39 s: the neutral margin (0.35 s) declines it, the sweeper
    /// keeper's wider window (0.434 s) accepts it. Both times come from the real trajectory model; the striker
    /// is placed by asking the same model which position makes the race a 0.39 s one.
    /// </summary>
    private static (FootballerObservation Neutral, FootballerObservation Sweeper, BallTrajectoryEstimate Trajectory,
        float GapSeconds) BuildContestedSweepRace()
    {
        const float TargetGapSeconds = 0.39f;
        var keeperXZ = new Vector2(-40f, 10f);
        var ball = new BallState
        {
            Position = new Vector3(-33f, 0.11f, 0f),
            PreviousPosition = new Vector3(-33f, 0.11f, 0f),
            Velocity = new Vector3(-4f, 0f, 0f)
        };

        var neutralKeeper = KeeperState(keeperXZ, PlayerTendencyBaselines.SweepAggression);
        FootballerState sweeperKeeper = KeeperState(
            keeperXZ, PlayerTendencyProfile.For(TacticalRole.Goalkeeper, PlayerArchetype.SweeperKeeper)
                .SweepAggression01);

        var trajectory = new BallTrajectoryEstimate(pitch: Pitch);
        Assert.True(trajectory.TryBuild(ball, GoalkeeperDecisionModel.TrajectoryHorizonSeconds));

        // The keeper-only race first: the meeting point does not depend on who else is visible.
        FootballerObservation keeperOnly = ObserveKeeper(neutralKeeper, ball, strikerXZ: null);
        Assert.True(trajectory.TryMeet(
            ReachKinematics.FromObservation(keeperOnly),
            GoalkeeperDecisionModel.TrajectoryHorizonSeconds,
            GoalkeeperDecisionModel.HandlingHeightMeters,
            out float keeperSeconds,
            out Vector3 meetPoint));

        // Place the striker at the point that makes his own race model meet the ball TargetGap earlier.
        Vector2 meetXZ = new(meetPoint.X, meetPoint.Z);
        Vector2 strikerXZ = FindStrikerXZ(
            meetXZ, trajectory, keeperSeconds - TargetGapSeconds);

        FootballerObservation neutralObs = ObserveKeeper(neutralKeeper, ball, strikerXZ);
        FootballerObservation sweeperObs = ObserveKeeper(sweeperKeeper, ball, strikerXZ);

        Assert.True(trajectory.TryMeet(
            ReachKinematics.FromEntity(StrikerEntity(strikerXZ)),
            GoalkeeperDecisionModel.TrajectoryHorizonSeconds,
            float.MaxValue,
            out float opponentSeconds,
            out _));

        return (neutralObs, sweeperObs, trajectory, keeperSeconds - opponentSeconds);
    }

    /// <summary>
    /// The striker position (on the ball's own line) at which the race model meets the ball
    /// <paramref name="targetSeconds"/> before the keeper's own meeting. A bisection on the offset along the
    /// ball path: the closer the striker stands to where the ball is coming from, the earlier he can meet it.
    /// </summary>
    private static Vector2 FindStrikerXZ(Vector2 meetXZ, BallTrajectoryEstimate trajectory, float targetSeconds)
    {
        float low = -8f;
        float high = 8f;
        for (int iteration = 0; iteration < 40; iteration++)
        {
            float mid = (low + high) * 0.5f;
            float sample = OpponentSampleSeconds(meetXZ + new Vector2(mid, 0f), trajectory);
            // A later sample than asked for means the striker is too deep; a false means he is far too deep.
            if (sample > targetSeconds) low = mid;
            else high = mid;
        }

        return meetXZ + new Vector2(high, 0f);
    }

    private static float OpponentSampleSeconds(Vector2 position, BallTrajectoryEstimate trajectory)
    {
        bool met = trajectory.TryMeet(
            ReachKinematics.FromEntity(StrikerEntity(position)),
            GoalkeeperDecisionModel.TrajectoryHorizonSeconds,
            float.MaxValue,
            out float seconds,
            out _);
        return met ? seconds : GoalkeeperDecisionModel.TrajectoryHorizonSeconds + 1f;
    }

    private static PerceivedEntity StrikerEntity(Vector2 position) =>
        new(31, TeamId.Away, TacticalRole.Striker, new Vector3(position.X, 0f, position.Y), Vector3.Zero, 1f)
        {
            Capability = CapabilityEstimate.Generic
        };

    private static FootballerState KeeperState(Vector2 positionXZ, float sweepAggression01) => new()
    {
        Id = KeeperId,
        Team = TeamId.Home,
        ShirtNumber = 1,
        Role = TacticalRole.Goalkeeper,
        Attributes = new PlayerAttributes(),
        Tendencies = new PlayerTendencies { SweepAggression01 = sweepAggression01 },
        Position = new Vector3(positionXZ.X, 0f, positionXZ.Y),
        Velocity = Vector3.Zero,
        FacingXZ = new Vector2(1f, 0f)
    };

    private static FootballerObservation ObserveKeeper(FootballerState keeper, BallState ball, Vector2? strikerXZ)
    {
        var visible = new List<PerceivedEntity>();
        if (strikerXZ is Vector2 striker)
            visible.Add(StrikerEntity(striker));

        return new FootballerObservation
        {
            SelfId = keeper.Id,
            SelfTeam = keeper.Team,
            SelfRole = keeper.Role,
            SelfPosition = keeper.Position,
            SelfVelocity = keeper.Velocity,
            SelfFacingXZ = keeper.FacingXZ,
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 0f,
            IsSetPieceTaker = false,
            PerceivedBallPosition = ball.Position,
            PerceivedBallVelocity = ball.Velocity,
            PossessingTeam = TeamId.Away,
            BallLastTouchedByPlayerId = 31,
            BallLastTouchedByTeam = TeamId.Away,
            BallLastPlay = BallLastPlay.None,
            BallControlState = BallControlState.Loose,
            TacticalAnchorPosition = keeper.Position,
            TacticalJob = TacticalJob.Shape,
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 30f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            Tick = 900,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = keeper.Tendencies,
            SelfAttributes = keeper.Attributes
        };
    }

    // =============================================================================================
    // Fixtures: a scenario and small helpers.
    // =============================================================================================

    private const int WideWingerId = 102;
    private const int AwayCenterBackId = 201;

    private static MatchScenario WideScenario(AiRuntimeConfig ai)
    {
        var scenario = new MatchScenario { Name = "archetype-fixture", Seed = 4242UL, Ai = ai };
        scenario.Add(MatchScenario.Player(101, TeamId.Home, TacticalRole.Goalkeeper,
            new Vector3(-Pitch.HalfLength + 3f, 0f, 0f), shirtNumber: 1));
        scenario.Add(MatchScenario.Player(WideWingerId, TeamId.Home, TacticalRole.Winger,
            new Vector3(-8f, 0f, 18f), shirtNumber: 7));
        scenario.Add(MatchScenario.Player(AwayCenterBackId, TeamId.Away, TacticalRole.CenterBack,
            new Vector3(10f, 0f, -6f), shirtNumber: 4));
        scenario.Add(MatchScenario.Player(202, TeamId.Away, TacticalRole.Striker,
            new Vector3(12f, 0f, 6f), shirtNumber: 9));
        return scenario;
    }

    private static FootballerState PlayerById(MatchSimulationEngine engine, int id) =>
        engine.Players.Single(player => player.Id == id);

    private static FootballerState PlayerState(
        int id,
        TeamId team,
        TacticalRole role,
        Vector3 position,
        Vector2 facing,
        float scanAppetite01) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = new PlayerTendencies { ScanAppetite01 = scanAppetite01 },
        Position = position,
        Velocity = Vector3.Zero,
        FacingXZ = facing
    };

    private static FootballerObservation Observation(FootballerState player, PlayerArchetype archetype) => new()
    {
        SelfId = player.Id,
        SelfTeam = player.Team,
        SelfRole = player.Role,
        SelfArchetype = archetype,
        SelfPosition = player.Position,
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = player.FacingXZ,
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 0f,
        PerceivedBallPosition = new Vector3(0f, 0.11f, 0f),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = player.Team,
        BallLastTouchedByPlayerId = player.Id,
        BallLastTouchedByTeam = player.Team,
        TacticalAnchorPosition = player.Position,
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 10f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        VisiblePlayers = Array.Empty<PerceivedEntity>(),
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = player.Tendencies,
        SelfAttributes = player.Attributes
    };

    private static void AssertTendenciesEqual(PlayerTendencies expected, PlayerTendencies actual)
    {
        Assert.Equal(expected.RiskTolerance01, actual.RiskTolerance01);
        Assert.Equal(expected.DirectPassPreference01, actual.DirectPassPreference01);
        Assert.Equal(expected.ShootingAppetite01, actual.ShootingAppetite01);
        Assert.Equal(expected.DribblePreference01, actual.DribblePreference01);
        Assert.Equal(expected.PositionalDiscipline01, actual.PositionalDiscipline01);
        Assert.Equal(expected.PressingAggression01, actual.PressingAggression01);
        Assert.Equal(expected.CrossPreference01, actual.CrossPreference01);
        Assert.Equal(expected.ScanAppetite01, actual.ScanAppetite01);
        Assert.Equal(expected.SweepAggression01, actual.SweepAggression01);
    }

    private static void AssertTendenciesInRange(PlayerTendencies tendencies)
    {
        Assert.InRange(tendencies.RiskTolerance01, 0f, 1f);
        Assert.InRange(tendencies.DirectPassPreference01, 0f, 1f);
        Assert.InRange(tendencies.ShootingAppetite01, 0f, 1f);
        Assert.InRange(tendencies.DribblePreference01, 0f, 1f);
        Assert.InRange(tendencies.PositionalDiscipline01, 0f, 1f);
        Assert.InRange(tendencies.PressingAggression01, 0f, 1f);
        Assert.InRange(tendencies.CrossPreference01, 0f, 1f);
        Assert.InRange(tendencies.ScanAppetite01, 0f, 1f);
        Assert.InRange(tendencies.SweepAggression01, 0f, 1f);
    }
}
