using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Physics;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A5.5 — the airborne prediction, and the cross aimed at a future meet point.
///
/// The raised alternative used to be priced by a drag-free kinematic range shortened by a flat 15 %, which
/// is why the A4 audit measured every raised ball from the carrier's own action set being collected by the
/// opposition: the landing point it aimed at was fiction and the flight time it charged was not the one the
/// ball took. <see cref="RaisedPassModel"/> replaces that with a table calibrated from the execution layer's
/// own launch through the real solver — the same way <see cref="PassPlanner"/> calibrates its ground table —
/// so these tests pin the trajectory, the band, the monotonicity, and then the delivery the AI is allowed to
/// choose: a cross may only be offered into a team-mate's <b>announced</b> arrival region, never into a crowd
/// nobody is attacking.
/// </summary>
public sealed class RaisedDeliveryTests
{
    private static readonly PitchDimensions Pitch = new();
    private static readonly RaisedPassModel Flights = RaisedPassModel.Default;

    private const int CarrierId = 8;
    private const int MateId = 9;

    // ---- the prediction is the real trajectory ------------------------------------------------------

    [Theory]
    [InlineData(RaisedPassProfile.Lofted, 0.30f)]
    [InlineData(RaisedPassProfile.Lofted, 0.65f)]
    [InlineData(RaisedPassProfile.Lofted, 1.00f)]
    [InlineData(RaisedPassProfile.Cross, 0.40f)]
    [InlineData(RaisedPassProfile.Cross, 0.90f)]
    [InlineData(RaisedPassProfile.Driven, 0.55f)]
    [InlineData(RaisedPassProfile.Driven, 1.00f)]
    public void TheLanding_IsTheRealSolverFlight(RaisedPassProfile profile, float power)
    {
        Assert.True(Flights.TryLanding(profile, power, out float tableRange, out float tableFlight));
        (float range, float flight) = SimulateFirstGroundContact(profile, power);

        Assert.Equal(range, tableRange, 2);
        Assert.Equal(flight, tableFlight, 2);
    }

    /// <summary>Rolls the exact launch through a fresh solver and finds the first ground contact, the hard way.</summary>
    private static (float Range, float Flight) SimulateFirstGroundContact(RaisedPassProfile profile, float power)
    {
        KickIntentType intent = profile switch
        {
            RaisedPassProfile.Cross => KickIntentType.Cross,
            RaisedPassProfile.Driven => KickIntentType.DrivenPass,
            _ => KickIntentType.LoftedPass
        };

        KickLaunch launch = KickLaunchModel.For(intent, power, new Vector2(1f, 0f));
        var ball = new BallState
        {
            Position = new Vector3(0f, KickLaunchModel.LaunchHeight, 0f),
            Velocity = launch.Velocity,
            AngularVelocity = launch.Spin
        };

        var solver = new BallPhysicsSolver();
        for (int tick = 1; tick <= 480; tick++)
        {
            solver.Step(ball, SimConstants.FixedDeltaTime);
            for (int i = 0; i < ball.RecentCollisions.Count; i++)
            {
                if (!string.Equals(ball.RecentCollisions[i].SurfaceType, "Ground", StringComparison.Ordinal)) continue;
                return (ball.RecentCollisions[i].ContactPoint.X, tick * SimConstants.FixedDeltaTime);
            }
        }

        throw new Xunit.Sdk.XunitException($"{profile} at {power:F2} never touched the ground");
    }

    [Fact]
    public void TheTables_AreMonotoneInPower()
    {
        foreach (RaisedPassProfile profile in Enum.GetValues<RaisedPassProfile>())
        {
            float previous = 0f;
            for (int i = 0; i < RaisedPassModel.PowerSamples; i++)
            {
                float power = RaisedPassModel.MinPower + i * RaisedPassModel.PowerStep;
                Assert.True(Flights.TryLanding(profile, power, out float range, out float flight));
                Assert.True(range > previous,
                    $"{profile} at {power:F2} lands at {range:F1} m, not further than {previous:F1} m");
                Assert.True(flight > 0f);
                previous = range;
            }
        }
    }

    [Fact]
    public void ACross_IsFlatterAndFasterThanTheLoftedBallAtTheSamePower()
    {
        Assert.True(Flights.TryLanding(RaisedPassProfile.Lofted, 0.60f, out float loftedRange, out float loftedFlight));
        Assert.True(Flights.TryLanding(RaisedPassProfile.Cross, 0.60f, out float crossRange, out float crossFlight));

        Assert.True(crossRange < loftedRange, $"cross {crossRange:F1} m vs lofted {loftedRange:F1} m");
        Assert.True(crossFlight < loftedFlight, $"cross {crossFlight:F2}s vs lofted {loftedFlight:F2}s");
    }

    [Fact]
    public void ADrivenBall_FirstBouncesWithinAboutTenMetres()
    {
        Assert.True(Flights.TryLanding(RaisedPassProfile.Driven, 1.0f, out float range, out float flight));

        Assert.InRange(range, 6f, 12f);
        Assert.True(flight < 0.7f, $"a driven ball's first bounce took {flight:F2}s");
    }

    [Fact]
    public void TheModel_RefusesTargetsItCannotDropNear()
    {
        // Too far for any lofted ball (the table's longest is under 40 m) and too short for the gentlest.
        Assert.False(Flights.TryPowerForLanding(RaisedPassProfile.Lofted, 60f, out _, out _, out _));
        Assert.False(Flights.TryPowerForLanding(RaisedPassProfile.Lofted, 1.5f, out _, out _, out _));

        Assert.True(Flights.TryPowerForLanding(RaisedPassProfile.Lofted, 23f, out float power, out float landing, out float flight));
        Assert.InRange(power, RaisedPassModel.MinPower, 1f);
        Assert.True(MathF.Abs(landing - 23f) <= RaisedPassModel.MaxLandingMissMeters);
        Assert.True(flight > 0.5f);
    }

    // ---- the cross is a delivery to an announced meet point -----------------------------------------

    [Fact]
    public void ACross_IsOfferedOnlyIntoAnAnnouncedBoxArrival()
    {
        MatchCommunicationContext comms = AnnounceBoxArrival(mateId: MateId, region: BoxRegion);
        var generator = new CandidateGenerator(Pitch, comms) { TimeOnBall = 0.8f };
        FootballerObservation obs = CarrierObservation();

        generator.GenerateOnBallCandidates(obs, new Vector3(Pitch.HalfLength, 0f, 0f), offsideLineProjX: 40f);
        UtilityCandidate cross = CrossCandidate(generator);

        Assert.Equal(KickIntentType.Cross, cross.Command.KickType);
        Assert.Equal(MateId, cross.Key.TargetPlayerId);
        Assert.NotNull(cross.TargetXZ);

        Vector2 landing = new(cross.TargetXZ!.Value.X, cross.TargetXZ.Value.Z);
        Assert.True(Vector2.Distance(landing, BoxRegion) <= RaisedPassModel.MaxLandingMissMeters + 0.5f,
            $"the cross lands at ({landing.X:F1},{landing.Y:F1}), {Vector2.Distance(landing, BoxRegion):F1} m " +
            $"from the announced region ({BoxRegion.X:F1},{BoxRegion.Y:F1})");

        // And it is aimed at the region, not at where the runner is standing now.
        Vector2 aim = new(cross.Command.KickAimDirectionXZ.X, cross.Command.KickAimDirectionXZ.Z);
        Vector2 expected = SimMath.SafeNormalize(BoxRegion - BallXZ, new Vector2(1f, 0f));
        Assert.True(Vector2.Dot(aim, expected) > 0.999f);

        // A5.5 compares deliveries on one scale: the cross sits in the same candidate list, priced in the
        // same units, as carrying the ball, recycling it and playing to feet.
        Assert.Contains(generator.Candidates, c => c.Key.Kind == CandidateKind.Carry);
        Assert.Contains(generator.Candidates, c => c.Key.Kind == CandidateKind.Pass && c.Key.TargetTag == "ToFeet");
        Assert.All(generator.Candidates, c => Assert.True(float.IsFinite(c.UtilityScore)));
    }

    [Fact]
    public void WithoutAnAnnouncedArrival_ThereIsNoCross()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        FootballerObservation obs = CarrierObservation();

        generator.GenerateOnBallCandidates(obs, new Vector3(Pitch.HalfLength, 0f, 0f), offsideLineProjX: 40f);

        for (int i = 0; i < generator.CandidateCount; i++)
            Assert.NotEqual(KickIntentType.Cross, generator.Candidates[i].Command.KickType);
    }

    [Fact]
    public void ANonBoxAnnouncement_IsNotACrossTarget()
    {
        // The runner announces a checking run back towards the ball: a real promise, but not a cross.
        MatchCommunicationContext comms = AnnounceBoxArrival(
            mateId: MateId, region: new Vector2(30f, 14f), kind: RunKind.CheckShort);
        var generator = new CandidateGenerator(Pitch, comms) { TimeOnBall = 0.8f };

        generator.GenerateOnBallCandidates(CarrierObservation(), new Vector3(Pitch.HalfLength, 0f, 0f), 40f);

        for (int i = 0; i < generator.CandidateCount; i++)
            Assert.NotEqual(KickIntentType.Cross, generator.Candidates[i].Command.KickType);
    }

    [Fact]
    public void ACrossIntoADefendedBox_IsStillPricedAsAnOption()
    {
        // A defender six metres from the landing, off the line: the cross is a contest at the far post, not
        // a blocked pass, and it must survive the carrier's completion floor or no cross could ever be
        // chosen in a real box.
        var comms = AnnounceBoxArrival(mateId: MateId, region: BoxRegion);
        var generator = new CandidateGenerator(Pitch, comms) { TimeOnBall = 0.8f };
        FootballerObservation obs = CarrierObservationWithDefender(new Vector2(BoxRegion.X - 6f, BoxRegion.Y - 5f));

        generator.GenerateOnBallCandidates(obs, new Vector3(Pitch.HalfLength, 0f, 0f), offsideLineProjX: 40f);

        Assert.True(CrossCandidate(generator).UtilityScore > 0f);
    }

    [Fact]
    public void AAirborneLane_IgnoresTheDefenderContestingTheLanding()
    {
        var planner = new PassPlanner(Pitch);
        var model = new PassSuccessModel(planner);
        FootballerObservation obs = CarrierObservationWithDefender(new Vector2(BoxRegion.X - 1f, BoxRegion.Y + 1.5f));

        // The defender is at the landing point itself: an aerial contest, priced by the defender term.
        Assert.False(model.IsAirborneLaneBlocked(
            BallXZ, BoxRegion, RaisedPassProfile.Cross, 0.85f, obs.VisiblePlayers, TeamId.Home));

        // The same man under the early flight, where the ball is high over his head, is not a block either.
        FootballerObservation nearLaunch = CarrierObservationWithDefender(new Vector2(38f, 19f));
        Assert.False(model.IsAirborneLaneBlocked(
            BallXZ, BoxRegion, RaisedPassProfile.Cross, 0.85f, nearLaunch.VisiblePlayers, TeamId.Home));
    }

    // ---- fixtures ----------------------------------------------------------------------------------

    private static readonly Vector2 BallXZ = new(34.4f, 24f);

    /// <summary>A far-post region the runner has announced, inside the cross band from the wide carrier.</summary>
    private static readonly Vector2 BoxRegion = new(42f, 0f);

    /// <summary>A wide carrier in the final third, with one forward running into the box.</summary>
    private static FootballerObservation CarrierObservation(params PerceivedEntity[] extra)
    {
        var visible = new List<PerceivedEntity>
        {
            new(MateId, TeamId.Home, TacticalRole.Striker, new Vector3(38f, 0f, 4f), Vector3.Zero, 0.95f)
            {
                Capability = CapabilityEstimate.Generic
            }
        };
        visible.AddRange(extra);

        return new FootballerObservation
        {
        SelfId = CarrierId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.Winger,
        SelfPosition = new Vector3(34f, 0f, 24f),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = new Vector3(BallXZ.X, 0.11f, BallXZ.Y),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = CarrierId,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = CarrierId,
        TacticalAnchorPosition = new Vector3(34f, 0f, 24f),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 20f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = 500,
        VisiblePlayers = visible,
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.Winger),
        SelfAttributes = new PlayerAttributes()
        };
    }

    private static FootballerObservation CarrierObservationWithDefender(Vector2 defenderXZ) =>
        CarrierObservation(new PerceivedEntity(
            25, TeamId.Away, TacticalRole.CenterBack,
            new Vector3(defenderXZ.X, 0f, defenderXZ.Y), Vector3.Zero, 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        });

    private static MatchCommunicationContext AnnounceBoxArrival(
        int mateId,
        Vector2 region,
        RunKind kind = RunKind.LateBoxArrival)
    {
        var comms = new MatchCommunicationContext();
        var path = new RunPath(
            kind,
            StartXZ: region - new Vector2(6f, 0f),
            MeetXZ: region,
            MeetRadiusMeters: 5f,
            StartTick: 400,
            MeetTick: 560,
            ExpiresTick: 620,
            HoldUntilTick: 400,
            HoldXZ: region - new Vector2(6f, 0f),
            Confidence01: 0.85f);

        comms.Runs.Publish(TeamId.Home, mateId, new RunIntent(path, TacticalJob.DepthRun, 400), 400);
        return comms;
    }

    private static UtilityCandidate CrossCandidate(CandidateGenerator generator)
    {
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            if (generator.Candidates[i].Command.KickType == KickIntentType.Cross) return generator.Candidates[i];
        }

        throw new Xunit.Sdk.XunitException("no cross candidate was offered");
    }
}
