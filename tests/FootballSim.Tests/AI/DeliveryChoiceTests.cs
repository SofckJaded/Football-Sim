using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Physics;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A5.5b — the deliveries that had a model but no way to be chosen.
///
/// A5.5 shipped an airborne prediction and a cross that was offered but never selected: a full reference
/// match executed zero crosses while the cross sat in the carrier's action set. The measured reasons were
/// three defects on the same theme, and each has a test here:
///
/// <list type="bullet">
/// <item><b>A raised delivery paid no preference.</b> The cross, the lofted ball and the raised alternative
///   scored with <c>preference = 0</c> next to ground passes collecting up to
///   <see cref="ActionValueScale.ProgressBonus"/> — a stated handicap larger than the margin they lost by.
///   One <c>PassPreference</c> now prices every pass.</item>
/// <item><b>A reception in the box was only "a good place".</b> It is now worth the chance it creates
///   (<see cref="ReceptionValueModel.ChanceShare"/>), which is what makes a met cross worth more than a
///   clean midfield possession.</item>
/// <item><b>The driven ball had no candidate and the cutback was pruned before it was planned.</b> Both are
///   explicit deliveries aimed at an announced arrival, and both are priced like every other ball.</item>
/// </list>
///
/// The fixtures are the situations the deliveries exist for: a carrier boxed in on the wing with a runner
/// arriving at the far post (the cross), a carrier at the byline with a runner arriving behind the ball (the
/// cutback), and a long announced run the carrier can reach with a flat ball (the driven pass).
/// </summary>
public sealed class DeliveryChoiceTests
{
    private static readonly PitchDimensions Pitch = new();

    private const int CarrierId = 8;
    private const int RunnerId = 9;
    private const int KeeperId = 25;

    // ---- the driven ball is timed to the arrival, not dropped ---------------------------------------

    /// <summary>
    /// The driven profile's arrival semantics (A5.5b). A driven ball first touches the ground inside ten
    /// metres, so "where does it land" cannot describe a delivery to a man twenty metres away — the table is
    /// asked how far along the aim the ball is when he gets there, and the power that gets it closest wins.
    /// </summary>
    [Fact]
    public void ADrivenBall_IsTimedToTheRequestedArrival()
    {
        RaisedPassModel flights = RaisedPassModel.Default;

        Assert.True(flights.TryPowerForArrival(
            RaisedPassProfile.Driven, 20f, 1.1f, out float power, out float distance, out float seconds));

        Assert.InRange(power, RaisedPassModel.MinPower, 1f);
        Assert.Equal(1.1f, seconds, 2);
        Assert.True(MathF.Abs(distance - 20f) <= RaisedPassModel.MaxArrivalMissMeters,
            $"the driven ball is {distance:F1} m along at {seconds:F2}s, not 20 m");

        // And the ball really is past its first bounce by then: this is a skipping ball, not a flyer.
        Assert.True(flights.TryLanding(RaisedPassProfile.Driven, power, out float landing, out float flight));
        Assert.True(landing < 12f && flight < seconds,
            $"first bounce at {landing:F1} m after {flight:F2}s, arrival asked at {seconds:F2}s");
    }

    [Fact]
    public void ADrivenBall_IsRefusedWhenNoPowerReachesThePointOnTime()
    {
        RaisedPassModel flights = RaisedPassModel.Default;

        // A ball asked to be 45 m along after a quarter of a second is not a pass in this model.
        Assert.False(flights.TryPowerForArrival(RaisedPassProfile.Driven, 45f, 0.25f, out _, out _, out _));
        Assert.False(flights.TryPowerForArrival(RaisedPassProfile.Driven, 20f, -1f, out _, out _, out _));
    }

    // ---- the cross wins when the carrier has run out of pitch ----------------------------------------

    /// <summary>
    /// The box-cross situation: a winger pinned on the touchline with a defender on him and a runner arriving
    /// at the far post. The cross is offered, it is priced on the chance the reception creates, and it beats
    /// carrying the ball into a corner that has nothing in it.
    /// </summary>
    [Fact]
    public void ACross_IsChosen_WhenTheCarrierIsBoxedIn()
    {
        var comms = Announce(RunnerId, new Vector2(41.5f, -6f), meetTick: 560, radius: 5f);
        var generator = new CandidateGenerator(Pitch, comms) { TimeOnBall = 0.8f };
        FootballerObservation obs = BoxedInOnTheWing();

        generator.GenerateOnBallCandidates(obs, new Vector3(Pitch.HalfLength, 0f, 0f), offsideLineProjX: 44f);

        UtilityCandidate cross = Candidate(generator, "Cross");
        UtilityCandidate carry = Candidate(generator, CandidateKind.Carry);

        Assert.Equal(KickIntentType.Cross, cross.Command.KickType);
        Assert.True(cross.Terms.IsPresent);
        Assert.True(cross.Terms.Preference > 0.10f,
            $"a forward cross earns the same forward preference a ground pass does, got {cross.Terms.Preference:F2}");
        Assert.True(cross.UtilityScore > carry.UtilityScore,
            $"the cross ({cross.UtilityScore:F2}) has to beat the carry ({carry.UtilityScore:F2}) " +
            "when the wide space is gone");
    }

    // ---- the cutback exists, and it is not the generic run pass -------------------------------------

    /// <summary>
    /// The cutback's situation: the carrier has the ball beyond the box arrival, so the ball has to be pulled
    /// back. The generic run pass is pre-ranked on forward progress and would prune a region behind the
    /// carrier before planning it, which is why the delivery needs its own candidate.
    /// </summary>
    [Fact]
    public void ACutback_IsOfferedAndChosen_FromBeyondTheArrival()
    {
        var comms = Announce(RunnerId, new Vector2(41.5f, 6f), meetTick: 555, radius: 4.5f);
        var generator = new CandidateGenerator(Pitch, comms) { TimeOnBall = 0.8f };
        FootballerObservation obs = AtTheByline();

        generator.GenerateOnBallCandidates(obs, new Vector3(Pitch.HalfLength, 0f, 0f), offsideLineProjX: 44f);

        UtilityCandidate cutback = Candidate(generator, "Cutback");

        Assert.Equal(KickIntentType.GroundPass, cutback.Command.KickType);
        Assert.Equal(RunnerId, cutback.Key.TargetPlayerId);

        // Aimed backwards, at the announced region: the ball goes behind the carrier.
        Assert.True(cutback.TargetXZ is not null);
        Assert.True(cutback.TargetXZ!.Value.X < obs.SelfPosition.X,
            $"the cutback is played to x={cutback.TargetXZ.Value.X:F1}, not behind the carrier at " +
            $"{obs.SelfPosition.X:F1}");

        UtilityCandidate carry = Candidate(generator, CandidateKind.Carry);
        Assert.True(cutback.UtilityScore > carry.UtilityScore,
            $"the cutback ({cutback.UtilityScore:F2}) has to beat carrying into the line ({carry.UtilityScore:F2})");
    }

    [Fact]
    public void WithoutAnAnnouncedArrival_ThereIsNoCutbackAndNoDrivenBall()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };

        generator.GenerateOnBallCandidates(AtTheByline(), new Vector3(Pitch.HalfLength, 0f, 0f), 44f);

        for (int i = 0; i < generator.CandidateCount; i++)
        {
            Assert.NotEqual("Cutback", generator.Candidates[i].Key.TargetTag);
            Assert.NotEqual("Driven", generator.Candidates[i].Key.TargetTag);
        }
    }

    // ---- the driven ball is a candidate, and it is the ball the arrival asks for ---------------------

    /// <summary>
    /// A long announced run is offered a driven ball. Its power is solved from the promise, so the flight it
    /// is priced on is the moment the runner said he would be there — not the moment the ball first bounces.
    /// </summary>
    [Fact]
    public void ADrivenBall_IsOfferedToALongAnnouncedRun_AndTimedToIt()
    {
        var comms = Announce(RunnerId, new Vector2(30f, 6f), meetTick: 580, radius: 5f);
        var generator = new CandidateGenerator(Pitch, comms) { TimeOnBall = 0.8f };
        FootballerObservation obs = Carrier(
            new Vector2(14f, 16f),
            new Vector2(14f, 16f),
            new PerceivedEntity(RunnerId, TeamId.Home, TacticalRole.Striker,
                new Vector3(30f, 0f, 6f), new Vector3(1f, 0f, 0f), 0.95f) { Capability = CapabilityEstimate.Generic },
            Opponent(KeeperId, TacticalRole.Goalkeeper, 50f, 0f),
            Opponent(26, TacticalRole.CenterBack, 44f, -4f));

        generator.GenerateOnBallCandidates(obs, new Vector3(Pitch.HalfLength, 0f, 0f), offsideLineProjX: 46f);

        UtilityCandidate driven = Candidate(generator, "Driven");

        Assert.Equal(KickIntentType.DrivenPass, driven.Command.KickType);
        Assert.Equal(RunnerId, driven.Key.TargetPlayerId);
        Assert.True(driven.Terms.IsPresent);
        Assert.InRange(driven.Command.KickPower01, RaisedPassModel.MinPower, 1f);
    }

    // ---- fixtures ----------------------------------------------------------------------------------

    /// <summary>
    /// A winger at (32, 20) with a full-back breathing on him and nothing to carry into: the far-post runner
    /// is the only forward option. The away line sits at 44, so the box is onside.
    /// </summary>
    private static FootballerObservation BoxedInOnTheWing() => Carrier(
        new Vector2(32.4f, 20f),
        new Vector2(32f, 20f),
        new PerceivedEntity(RunnerId, TeamId.Home, TacticalRole.Striker,
            new Vector3(40f, 0f, -2f), new Vector3(0f, 0f, -1f), 0.95f) { Capability = CapabilityEstimate.Generic },
        Opponent(KeeperId, TacticalRole.Goalkeeper, 50f, 0f),
        Opponent(26, TacticalRole.CenterBack, 44f, 4f),
        Opponent(27, TacticalRole.CenterBack, 45f, 12f),
        // The man on the carrier: the wide space is gone, which is what makes the cross the better ball.
        Opponent(28, TacticalRole.FullBack, 34f, 24f));

    /// <summary>
    /// A carrier on the byline with a runner arriving at the penalty spot behind the ball, a defender goal-side
    /// of him blocking the carry toward the goal line, and the region itself free. The cutback is the ball the
    /// situation asks for: there is no room to carry on, and the runner is arriving where a shot is on.
    /// </summary>
    private static FootballerObservation AtTheByline() => Carrier(
        new Vector2(46.4f, 16f),
        new Vector2(46f, 16f),
        new PerceivedEntity(RunnerId, TeamId.Home, TacticalRole.Striker,
            new Vector3(38f, 0f, 4f), new Vector3(1f, 0f, 1f), 0.95f) { Capability = CapabilityEstimate.Generic },
        Opponent(KeeperId, TacticalRole.Goalkeeper, 50f, 0f),
        Opponent(26, TacticalRole.CenterBack, 44f, -2f),
        // Goal-side of the carrier, two metres away: the byline is not a place to keep going from.
        Opponent(28, TacticalRole.FullBack, 48f, 16f),
        Opponent(29, TacticalRole.FullBack, 47f, 24f));

    private static PerceivedEntity Opponent(int id, TacticalRole role, float x, float z) =>
        new(id, TeamId.Away, role, new Vector3(x, 0f, z), Vector3.Zero, 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };

    private static FootballerObservation Carrier(Vector2 ballXZ, Vector2 selfXZ, params PerceivedEntity[] visible)
    {
        var players = new List<PerceivedEntity>(visible);
        return new FootballerObservation
        {
            SelfId = CarrierId,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.Winger,
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
            SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.Winger),
            SelfAttributes = new PlayerAttributes()
        };
    }

    private static MatchCommunicationContext Announce(int runnerId, Vector2 region, int meetTick, float radius)
    {
        var comms = new MatchCommunicationContext();
        var path = new RunPath(
            RunKind.LateBoxArrival,
            StartXZ: region - new Vector2(6f, 0f),
            MeetXZ: region,
            MeetRadiusMeters: radius,
            StartTick: 400,
            MeetTick: meetTick,
            ExpiresTick: meetTick + 60,
            HoldUntilTick: 400,
            HoldXZ: region - new Vector2(6f, 0f),
            Confidence01: 0.85f);

        comms.Runs.Publish(TeamId.Home, runnerId, new RunIntent(path, TacticalJob.DepthRun, 400), 400);
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
}
