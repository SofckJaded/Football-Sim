using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A1.1d.2 — the candidate generator is a module of its own.
///
/// These tests cover the seam the extraction created (a public type with a small surface), not the
/// generation rules themselves: those are already pinned by the scenario suite and by the byte-identical
/// 11v11 reference match recorded in `artifacts/advanced-ai/`.
/// </summary>
public sealed class CandidateGeneratorTests
{
    [Fact]
    public void GenerateOnBall_ProducesKeyedCandidates()
    {
        var generator = new CandidateGenerator(new PitchDimensions());

        generator.GenerateOnBallCandidates(OnBallObservation(), new System.Numerics.Vector3(52.5f, 0f, 0f), 0f);

        Assert.NotEmpty(generator.Candidates);
        Assert.Equal(generator.Candidates.Count, generator.CandidateCount);
        Assert.All(generator.Candidates, candidate => Assert.False(candidate.Key.IsNone));
        Assert.Contains(generator.Candidates, c => c.Key.Kind == CandidateKind.Carry);
    }

    [Fact]
    public void GenerateOffBall_ProducesKeyedCandidates()
    {
        var generator = new CandidateGenerator(new PitchDimensions());

        generator.GenerateOffBallCandidates(
            OffBallObservation(),
            oppGoalCenter: new System.Numerics.Vector3(52.5f, 0f, 0f),
            ownGoalCenter: new System.Numerics.Vector3(-52.5f, 0f, 0f),
            myInterceptXZ: new System.Numerics.Vector2(0f, 0f),
            isPrimaryBallResponder: false,
            offsideLineProjX: 0f);

        Assert.NotEmpty(generator.Candidates);
        Assert.All(generator.Candidates, candidate => Assert.False(candidate.Key.IsNone));
    }

    [Fact]
    public void Clear_DropsThePreviousSet()
    {
        var generator = new CandidateGenerator(new PitchDimensions());
        generator.GenerateOnBallCandidates(OnBallObservation(), new System.Numerics.Vector3(52.5f, 0f, 0f), 0f);
        Assert.NotEmpty(generator.Candidates);

        generator.Clear();

        Assert.Empty(generator.Candidates);
        Assert.Equal(0, generator.CandidateCount);
    }

    [Fact]
    public void Generation_IsDeterministicForTheSameInput()
    {
        var first = new CandidateGenerator(new PitchDimensions());
        var second = new CandidateGenerator(new PitchDimensions());

        first.GenerateOnBallCandidates(OnBallObservation(), new System.Numerics.Vector3(52.5f, 0f, 0f), 0f);
        second.GenerateOnBallCandidates(OnBallObservation(), new System.Numerics.Vector3(52.5f, 0f, 0f), 0f);

        Assert.Equal(first.CandidateCount, second.CandidateCount);
        for (int i = 0; i < first.CandidateCount; i++)
        {
            Assert.Equal(first.Candidates[i].ActionName, second.Candidates[i].ActionName);
            Assert.Equal(first.Candidates[i].Key, second.Candidates[i].Key);
            Assert.Equal(first.Candidates[i].UtilityScore, second.Candidates[i].UtilityScore);
        }
    }

    [Fact]
    public void TackleCooldown_StartsExpiredAndNeverGoesNegative()
    {
        var generator = new CandidateGenerator(new PitchDimensions());
        Assert.Equal(0f, generator.TackleCooldown);

        generator.TickTimers(SimConstants.FixedDeltaTime);

        Assert.Equal(0f, generator.TackleCooldown);
    }

    [Fact]
    public void AnOpponentsCallForPass_DoesNotReleaseThisCarriersDelay()
    {
        var comms = new MatchCommunicationContext();
        var generator = new CandidateGenerator(new PitchDimensions(), comms);
        FootballerObservation awayCarrier = Observation(ballAtFeet: true, selfTeam: TeamId.Away);
        var awayGoal = new System.Numerics.Vector3(-52.5f, 0f, 0f);

        generator.GenerateOnBallCandidates(awayCarrier, awayGoal, 0f);
        Assert.Equal(0, PassCount(generator));

        // The home human asks for the ball: the away carrier's observations are unchanged, and the call
        // is not his team's instruction.
        comms.RaiseCallForPass(TeamId.Home, 9, 2.5f, throughBall: false);
        generator.Clear();
        generator.GenerateOnBallCandidates(awayCarrier, awayGoal, 0f);
        Assert.Equal(0, PassCount(generator));

        // Positive control: the same call from the carrier's own team does release the pass, so the
        // filter above is team identity and not a disabled call path.
        comms.RaiseCallForPass(TeamId.Away, 9, 2.5f, throughBall: false);
        generator.Clear();
        generator.GenerateOnBallCandidates(awayCarrier, awayGoal, 0f);
        Assert.True(PassCount(generator) > 0,
            "an own-team call for the pass must still release the carrier's normal delay");
    }

    private static int PassCount(CandidateGenerator generator)
    {
        int count = 0;
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            if (generator.Candidates[i].Key.Kind == CandidateKind.Pass) count++;
        }

        return count;
    }

    private static FootballerObservation OnBallObservation() => Observation(ballAtFeet: true);

    private static FootballerObservation OffBallObservation() => Observation(ballAtFeet: false);

    private static FootballerObservation Observation(bool ballAtFeet, TeamId selfTeam = TeamId.Home)
    {
        bool home = selfTeam == TeamId.Home;
        TeamId opponent = home ? TeamId.Away : TeamId.Home;
        return new()
        {
            SelfId = 8,
            SelfTeam = selfTeam,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = new System.Numerics.Vector3(0f, 0f, 0f),
            SelfVelocity = System.Numerics.Vector3.Zero,
            SelfFacingXZ = new System.Numerics.Vector2(home ? 1f : -1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = ballAtFeet,
            ControlQuality01 = ballAtFeet ? 1f : 0f,
            PerceivedBallPosition = ballAtFeet
                ? new System.Numerics.Vector3(0.4f, 0f, 0f)
                : new System.Numerics.Vector3(8f, 0f, 0f),
            PerceivedBallVelocity = System.Numerics.Vector3.Zero,
            PossessingTeam = ballAtFeet ? selfTeam : opponent,
            BallLastTouchedByPlayerId = 8,
            BallControlState = ballAtFeet ? BallControlState.Controlled : BallControlState.Loose,
            BallControllingPlayerId = ballAtFeet ? 8 : null,
            TacticalAnchorPosition = new System.Numerics.Vector3(0f, 0f, 0f),
            AttackingDirectionX = home ? 1f : -1f,
            MatchTimeSeconds = 0f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            VisiblePlayers = new List<PerceivedEntity>
            {
                new(9, selfTeam, TacticalRole.Striker,
                    new System.Numerics.Vector3(home ? 20f : -20f, 0f, 2f),
                    System.Numerics.Vector3.Zero, 0.9f),
                new(12, opponent, TacticalRole.CenterBack,
                    new System.Numerics.Vector3(home ? 30f : -30f, 0f, -4f),
                    System.Numerics.Vector3.Zero, 0.9f)
            },
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = new PlayerTendencies(),
            SelfAttributes = new PlayerAttributes()
        };
    }

    [Fact]
    public void BoxedInMidfieldTouchlineCarrier_WithNoViablePasses_OffersEscapeClearance()
    {
        // Carrier pinned on the midfield touchline (0, 31.5) by two close pressers blocking any forward
        // ground/lofted pass. Escape clearance must be emitted and outscore ShieldBall/Dribble.
        var boxedObs = new FootballerObservation
        {
            SelfId = 8,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.Winger,
            SelfPosition = new Vector3(0f, 0f, 31.5f),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(0.4f, 0f, 31.5f),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = 8,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = 8,
            TacticalAnchorPosition = new Vector3(0f, 0f, 31.5f),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 10f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            VisiblePlayers = new List<PerceivedEntity>
            {
                new(9, TeamId.Home, TacticalRole.Striker, new Vector3(16f, 0f, 29f), Vector3.Zero, 0.9f),
                new(20, TeamId.Away, TacticalRole.FullBack, new Vector3(1.5f, 0f, 31.2f), Vector3.Zero, 0.9f),
                new(21, TeamId.Away, TacticalRole.DefensiveMidfielder, new Vector3(2.8f, 0f, 29.2f), Vector3.Zero, 0.9f),
                new(22, TeamId.Away, TacticalRole.CenterBack, new Vector3(15.5f, 0f, 29.0f), Vector3.Zero, 0.9f)
            },
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = new PlayerTendencies(),
            SelfAttributes = new PlayerAttributes()
        };

        var pitch = new PitchDimensions();
        var boxedGen = new CandidateGenerator(pitch) { TimeOnBall = 0.8f };
        boxedGen.GenerateOnBallCandidates(boxedObs, new Vector3(pitch.HalfLength, 0f, 0f), 40f);

        Assert.DoesNotContain(boxedGen.Candidates, c => c.Key.Kind == CandidateKind.Pass);
        UtilityCandidate clearance = Assert.Single(boxedGen.Candidates, c => c.Key.Kind == CandidateKind.Clearance);
        Assert.True(clearance.TargetXZ.HasValue);
        Assert.True(pitch.HalfWidth - MathF.Abs(clearance.TargetXZ!.Value.Z) >= 3.0f - 1e-3f,
            $"escape clearance must stay >= 3 m inside touchline, aimed at z={clearance.TargetXZ.Value.Z:F2}");

        foreach (UtilityCandidate c in boxedGen.Candidates)
        {
            if (c.Key.Kind is CandidateKind.Shield or CandidateKind.Dribble)
            {
                Assert.True(clearance.UtilityScore > c.UtilityScore,
                    $"escape clearance ({clearance.UtilityScore:F3}) must outscore {c.ActionName} ({c.UtilityScore:F3})");
            }
        }

        // Unpressed midfield carrier must still generate NO clearance.
        FootballerObservation openObs = Observation(ballAtFeet: true);
        var openGen = new CandidateGenerator(pitch) { TimeOnBall = 0.8f };
        openGen.GenerateOnBallCandidates(openObs, new Vector3(pitch.HalfLength, 0f, 0f), 40f);
        Assert.DoesNotContain(openGen.Candidates, c => c.Key.Kind == CandidateKind.Clearance);
    }

    [Fact]
    public void BoxedInMidfieldTouchlineCarrier_WithLowUtilityPass_StillOffersEscapeClearance()
    {
        var pitch = new PitchDimensions();
        FootballerObservation obs = new()
        {
            SelfId = 8,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = new Vector3(0f, 0f, 31.5f),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(0.4f, 0f, 31.5f),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = 8,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = 8,
            TacticalAnchorPosition = new Vector3(0f, 0f, 31.5f),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 10f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            VisiblePlayers = new List<PerceivedEntity>
            {
                // Open backward recycle mate (clean lane, negative progress -> low utility in 0.05..0.45):
                // Must NOT suppress ClearBall even though Pass.UtilityScore > 0.
                new(5, TeamId.Home, TacticalRole.FullBack, new Vector3(-16f, 0f, 18f), Vector3.Zero, 0.9f),
                new(9, TeamId.Home, TacticalRole.Striker, new Vector3(16f, 0f, 29f), Vector3.Zero, 0.9f),
                new(20, TeamId.Away, TacticalRole.FullBack, new Vector3(1.5f, 0f, 31.2f), Vector3.Zero, 0.9f),
                new(21, TeamId.Away, TacticalRole.DefensiveMidfielder, new Vector3(2.8f, 0f, 29.2f), Vector3.Zero, 0.9f),
                new(22, TeamId.Away, TacticalRole.CenterBack, new Vector3(15.5f, 0f, 29.0f), Vector3.Zero, 0.9f)
            },
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = new PlayerTendencies(),
            SelfAttributes = new PlayerAttributes()
        };

        var generator = new CandidateGenerator(pitch) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(obs, new Vector3(pitch.HalfLength, 0f, 0f), 40f);

        UtilityCandidate clearance = Assert.Single(generator.Candidates,
            c => c.Key.Kind == CandidateKind.Clearance);
        bool hasWeakPass = false;
        foreach (UtilityCandidate c in generator.Candidates)
        {
            if (c.Key.Kind == CandidateKind.Pass && c.UtilityScore > 0f && c.UtilityScore < 0.55f)
            {
                hasWeakPass = true;
            }
        }
        Assert.True(hasWeakPass, "fixture must contain a low-utility (< 0.55) pass that previously suppressed ClearBall");
        Assert.True(clearance.UtilityScore >= 0.45f,
            $"escape clearance ({clearance.UtilityScore:F3}) must coexist with weak pass options");
    }

    [Fact]
    public void WingCarrier_OnFirstTouchOrInFinalThird_DoesNotHoofClearanceOutOfBounds()
    {
        var pitch = new PitchDimensions();

        static FootballerObservation WingObs(Vector3 pos, List<PerceivedEntity> visible) => new()
        {
            SelfId = 8,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.Winger,
            SelfPosition = pos,
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(pos.X + 0.4f, 0f, pos.Z),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = 8,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = 8,
            TacticalAnchorPosition = pos,
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 10f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = new PlayerTendencies(),
            SelfAttributes = new PlayerAttributes()
        };

        // 1. Touchline wing carrier on tick 1 (TimeOnBall = 0.05s < 0.35s): must NOT clear before pass candidates exist.
        FootballerObservation firstTouchObs = WingObs(
            new Vector3(10f, 0f, 32.0f),
            new List<PerceivedEntity>
            {
                new(9, TeamId.Home, TacticalRole.Striker, new Vector3(22f, 0f, 10f), Vector3.Zero, 0.9f),
                new(20, TeamId.Away, TacticalRole.FullBack, new Vector3(8.2f, 0f, 31.8f), Vector3.Zero, 0.9f)
            });
        var firstTouchGen = new CandidateGenerator(pitch) { TimeOnBall = 0.05f };
        firstTouchGen.GenerateOnBallCandidates(firstTouchObs, new Vector3(pitch.HalfLength, 0f, 0f), 40f);
        Assert.DoesNotContain(firstTouchGen.Candidates, c => c.Key.Kind == CandidateKind.Clearance);

        // 2. Final-third wing carrier (myAttackProjX = 32 > CarryFinalThirdProjX = 26): must NOT hoof clearance.
        FootballerObservation finalThirdObs = WingObs(
            new Vector3(32f, 0f, 31.5f),
            new List<PerceivedEntity>
            {
                new(9, TeamId.Home, TacticalRole.Striker, new Vector3(40f, 0f, 4f), Vector3.Zero, 0.9f),
                new(20, TeamId.Away, TacticalRole.FullBack, new Vector3(30.5f, 0f, 31.0f), Vector3.Zero, 0.9f),
                new(21, TeamId.Away, TacticalRole.CenterBack, new Vector3(33.5f, 0f, 29.0f), Vector3.Zero, 0.9f)
            });
        var finalThirdGen = new CandidateGenerator(pitch) { TimeOnBall = 0.60f };
        finalThirdGen.GenerateOnBallCandidates(finalThirdObs, new Vector3(pitch.HalfLength, 0f, 0f), 40f);
        Assert.DoesNotContain(finalThirdGen.Candidates, c => c.Key.Kind == CandidateKind.Clearance);

        // 3. Midfield wing carrier with ONLY 1 trailing chaser behind him: must NOT trigger boxed-in escape.
        FootballerObservation trailingChaserObs = WingObs(
            new Vector3(0f, 0f, 31.5f),
            new List<PerceivedEntity>
            {
                new(9, TeamId.Home, TacticalRole.Striker, new Vector3(16f, 0f, 29f), Vector3.Zero, 0.9f),
                new(20, TeamId.Away, TacticalRole.FullBack, new Vector3(-2.0f, 0f, 31.2f), Vector3.Zero, 0.9f),
                new(22, TeamId.Away, TacticalRole.CenterBack, new Vector3(15.5f, 0f, 29.0f), Vector3.Zero, 0.9f)
            });
        var trailingGen = new CandidateGenerator(pitch) { TimeOnBall = 0.60f };
        trailingGen.GenerateOnBallCandidates(trailingChaserObs, new Vector3(pitch.HalfLength, 0f, 0f), 40f);
        Assert.DoesNotContain(trailingGen.Candidates, c => c.Key.Kind == CandidateKind.Clearance);

        // 4. Genuine boxed-in midfield touchline carrier aims inward (>= 4.5 m inside touchline) at <= 0.60 power.
        FootballerObservation boxedObs = WingObs(
            new Vector3(0f, 0f, 31.5f),
            new List<PerceivedEntity>
            {
                new(9, TeamId.Home, TacticalRole.Striker, new Vector3(16f, 0f, 29f), Vector3.Zero, 0.9f),
                new(20, TeamId.Away, TacticalRole.FullBack, new Vector3(1.5f, 0f, 31.2f), Vector3.Zero, 0.9f),
                new(21, TeamId.Away, TacticalRole.DefensiveMidfielder, new Vector3(2.8f, 0f, 29.2f), Vector3.Zero, 0.9f),
                new(22, TeamId.Away, TacticalRole.CenterBack, new Vector3(15.5f, 0f, 29.0f), Vector3.Zero, 0.9f)
            });
        var boxedGen = new CandidateGenerator(pitch) { TimeOnBall = 0.80f };
        boxedGen.GenerateOnBallCandidates(boxedObs, new Vector3(pitch.HalfLength, 0f, 0f), 40f);
        UtilityCandidate clearance = Assert.Single(boxedGen.Candidates, c => c.Key.Kind == CandidateKind.Clearance);
        Assert.True(clearance.TargetXZ.HasValue);
        Assert.True(pitch.HalfWidth - MathF.Abs(clearance.TargetXZ!.Value.Z) >= 4.5f - 1e-3f,
            $"escape clearance must aim inward (>= 4.5 m inside touchline), aimed at z={clearance.TargetXZ.Value.Z:F2}");
        Assert.True(clearance.Command.KickPower01 <= 0.60f,
            $"escape clearance must use bounded power <= 0.60, got {clearance.Command.KickPower01:F2}");
    }

    [Fact]
    public void AttackingHalfCentralCarrier_SurroundedByTwoOpponents_DoesNotHoofGoalKickClearance()
    {
        var pitch = new PitchDimensions();
        // Exact t=3089 live geometry: central attacker at (21.0, -15.9) surrounded by 2 opponents within 2.5 m.
        var obs = new FootballerObservation
        {
            SelfId = 9,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.Striker,
            SelfPosition = new Vector3(21.0f, 0f, -15.9f),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(21.4f, 0f, -15.9f),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = 9,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = 9,
            TacticalAnchorPosition = new Vector3(21.0f, 0f, -15.9f),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 51f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            VisiblePlayers = new List<PerceivedEntity>
            {
                new(20, TeamId.Away, TacticalRole.CenterBack, new Vector3(22.2f, 0f, -15.2f), Vector3.Zero, 0.9f),
                new(21, TeamId.Away, TacticalRole.DefensiveMidfielder, new Vector3(20.0f, 0f, -14.3f), Vector3.Zero, 0.9f)
            },
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = new PlayerTendencies(),
            SelfAttributes = new PlayerAttributes()
        };

        var generator = new CandidateGenerator(pitch) { TimeOnBall = 0.80f };
        generator.GenerateOnBallCandidates(obs, new Vector3(pitch.HalfLength, 0f, 0f), 40f);

        Assert.DoesNotContain(generator.Candidates, c => c.Key.Kind == CandidateKind.Clearance);
    }

    [Fact]
    public void BoxedInMidfieldTouchlineCarrier_UsesLoftedChannelEscape_NotLongClearanceHoof()
    {
        var pitch = new PitchDimensions();
        var boxedObs = new FootballerObservation
        {
            SelfId = 8,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.Winger,
            SelfPosition = new Vector3(0f, 0f, 31.5f),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(0.4f, 0f, 31.5f),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = 8,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = 8,
            TacticalAnchorPosition = new Vector3(0f, 0f, 31.5f),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 10f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            VisiblePlayers = new List<PerceivedEntity>
            {
                new(9, TeamId.Home, TacticalRole.Striker, new Vector3(16f, 0f, 29f), Vector3.Zero, 0.9f),
                new(20, TeamId.Away, TacticalRole.FullBack, new Vector3(1.5f, 0f, 31.2f), Vector3.Zero, 0.9f),
                new(21, TeamId.Away, TacticalRole.DefensiveMidfielder, new Vector3(2.8f, 0f, 29.2f), Vector3.Zero, 0.9f),
                new(22, TeamId.Away, TacticalRole.CenterBack, new Vector3(15.5f, 0f, 29.0f), Vector3.Zero, 0.9f)
            },
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = new PlayerTendencies(),
            SelfAttributes = new PlayerAttributes()
        };

        var boxedGen = new CandidateGenerator(pitch) { TimeOnBall = 0.80f };
        boxedGen.GenerateOnBallCandidates(boxedObs, new Vector3(pitch.HalfLength, 0f, 0f), 40f);
        UtilityCandidate escape = Assert.Single(boxedGen.Candidates, c => c.Key.Kind == CandidateKind.Clearance);
        Assert.Equal(KickIntentType.LoftedPass, escape.Command.KickType);
        Assert.True(escape.Command.KickPower01 <= 0.50f,
            $"boxed-in escape must use <= 0.50 lofted power, got {escape.Command.KickPower01:F2}");

        // Own-third clearance at (-30, 28) still uses KickIntentType.Clearance at 0.85f.
        var ownThirdObs = new FootballerObservation
        {
            SelfId = 4,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.FullBack,
            SelfPosition = new Vector3(-30f, 0f, 28f),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(-29.6f, 0f, 28f),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = 4,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = 4,
            TacticalAnchorPosition = new Vector3(-30f, 0f, 28f),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 10f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            VisiblePlayers = new List<PerceivedEntity>
            {
                new(20, TeamId.Away, TacticalRole.Striker, new Vector3(-28f, 0f, 27.5f), Vector3.Zero, 0.9f)
            },
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = new PlayerTendencies(),
            SelfAttributes = new PlayerAttributes()
        };

        var ownThirdGen = new CandidateGenerator(pitch) { TimeOnBall = 0.80f };
        ownThirdGen.GenerateOnBallCandidates(ownThirdObs, new Vector3(pitch.HalfLength, 0f, 0f), 40f);
        UtilityCandidate ownClear = Assert.Single(ownThirdGen.Candidates, c => c.Key.Kind == CandidateKind.Clearance);
        Assert.Equal(KickIntentType.Clearance, ownClear.Command.KickType);
        Assert.Equal(0.85f, ownClear.Command.KickPower01, 2);
    }
}
