using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A4.1 — the carrier's action set is explicit, named and bounded.
///
/// Before this milestone the carrier's options were whatever the per-branch code happened to append:
/// a shot inside 23 m, passes when a timer expired, and one carry in the best direction. Each test here
/// asks about an action the plan names — shield, clearance, recycle/keeper support, a pass into a run
/// region, the raised alternative over a blocked lane — and about the bounds the configuration now
/// enforces. Keys, not action labels, are what the decision path compares, so the assertions read keys.
/// </summary>
public sealed class OnBallActionSetTests
{
    private const int CarrierId = 8;
    private const int KeeperId = 1;
    private const int MateId = 9;
    private const int SecondMateId = 10;
    private static readonly PitchDimensions Pitch = new();

    // ---------------------------------------------------------------------------------------------
    // The set exists and is named
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void EveryCarrierCandidate_CarriesAStableKey()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };

        generator.GenerateOnBallCandidates(
            Observation(new Vector2(-5f, 8f), Mate(MateId, new Vector2(6f, 8f))),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 30f);

        Assert.NotEmpty(generator.Candidates);
        Assert.All(generator.Candidates, candidate => Assert.False(candidate.Key.IsNone));
        Assert.Contains(generator.Candidates, c => c.Key.Kind == CandidateKind.Pass);
        Assert.Contains(generator.Candidates, c => c.Key.Kind == CandidateKind.Carry);
    }

    // ---------------------------------------------------------------------------------------------
    // Shield and clearance
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A carrier pinned in his own third with an opponent on him has two options a passer does not need:
    /// protect the ball with his body, and get it out of danger. The same player in midfield, with nobody
    /// near him, must be offered neither — a shield with no opponent to shield from is not an action.
    /// </summary>
    [Fact]
    public void ShieldAndClearance_AreOfferedOnlyUnderPressureInTheOwnThird()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.6f };

        generator.GenerateOnBallCandidates(
            Observation(
                new Vector2(-40f, 5f),
                Home(KeeperId, TacticalRole.Goalkeeper, new Vector2(-48f, 0f)),
                Mate(MateId, new Vector2(-32f, 12f)),
                Away(14, TacticalRole.Striker, new Vector2(-38.6f, 5.2f))),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 0f);

        Assert.Contains(generator.Candidates, c => c.Key.Kind == CandidateKind.Shield);
        Assert.Contains(generator.Candidates, c => c.Key.Kind == CandidateKind.Clearance);

        var openGenerator = new CandidateGenerator(Pitch) { TimeOnBall = 0.6f };
        openGenerator.GenerateOnBallCandidates(
            Observation(
                new Vector2(-5f, 8f),
                Home(KeeperId, TacticalRole.Goalkeeper, new Vector2(-48f, 0f)),
                Mate(MateId, new Vector2(6f, 8f))),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 30f);

        Assert.DoesNotContain(openGenerator.Candidates, c => c.Key.Kind == CandidateKind.Shield);
        Assert.DoesNotContain(openGenerator.Candidates, c => c.Key.Kind == CandidateKind.Clearance);
    }

    /// <summary>
    /// Shielding is bounded in time: a carrier who has held the ball past the limit must release it, or
    /// the action would beat every risky pass forever and the side would stand still.
    /// </summary>
    [Fact]
    public void Shielding_IsBoundedInTime()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = CandidateGenerator.ShieldMaxTimeOnBall + 0.2f };

        generator.GenerateOnBallCandidates(
            Observation(
                new Vector2(-40f, 5f),
                Mate(MateId, new Vector2(-32f, 12f)),
                Away(14, TacticalRole.Striker, new Vector2(-38.6f, 5.2f))),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 0f);

        Assert.DoesNotContain(generator.Candidates, c => c.Key.Kind == CandidateKind.Shield);
    }

    // ---------------------------------------------------------------------------------------------
    // Recycle and keeper support
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Keeper support is an own-third option — and an off-his-line one (A4.4). A keeper standing on his
    /// goal line is not a pass target: the reference match produced own goals from balls played at him that
    /// he could not stop, so the option requires him to be at least twelve metres off his line. From the
    /// halfway line it is not offered at all.
    /// </summary>
    [Fact]
    public void KeeperSupport_IsOfferedFromTheOwnThirdOnly()
    {
        var deep = new CandidateGenerator(Pitch) { TimeOnBall = 1.0f };
        deep.GenerateOnBallCandidates(
            Observation(
                new Vector2(-20f, 5f),
                Home(KeeperId, TacticalRole.Goalkeeper, new Vector2(-38f, 0f)),
                Mate(MateId, new Vector2(-8f, 14f))),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 0f);

        Assert.Contains(deep.Candidates, c => c.Key.Kind == CandidateKind.Pass && c.Key.TargetPlayerId == KeeperId);

        // The same own-third ball, but the keeper is standing on his line: no pass.
        var onHisLine = new CandidateGenerator(Pitch) { TimeOnBall = 1.0f };
        onHisLine.GenerateOnBallCandidates(
            Observation(
                new Vector2(-20f, 5f),
                Home(KeeperId, TacticalRole.Goalkeeper, new Vector2(-48.5f, 0f)),
                Mate(MateId, new Vector2(-8f, 14f))),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 0f);

        Assert.DoesNotContain(onHisLine.Candidates, c => c.Key.TargetPlayerId == KeeperId);

        var high = new CandidateGenerator(Pitch) { TimeOnBall = 1.0f };
        high.GenerateOnBallCandidates(
            Observation(
                new Vector2(10f, 5f),
                Home(KeeperId, TacticalRole.Goalkeeper, new Vector2(-38f, 0f)),
                Mate(MateId, new Vector2(22f, 10f))),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 30f);

        Assert.DoesNotContain(high.Candidates, c => c.Key.TargetPlayerId == KeeperId);
    }

    // ---------------------------------------------------------------------------------------------
    // Passes into a run region
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A pass to feet and a pass into the space ahead are different actions against the same receiver, so
    /// both must be offered when he is <b>running</b> and there is room to play into — and only the feet
    /// option when a defender is standing in that space. A receiver standing still gets no run option at
    /// all: the through lead would put the ball five metres past a man who was never going anywhere
    /// (A4.10, measured — a stationary mate was rated p = 0.92 for a ball he had no reason to chase).
    /// </summary>
    [Fact]
    public void APassIntoARunRegion_IsASeparateOption()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(
            Observation(
                new Vector2(0f, 8f),
                Mate(MateId, new Vector2(12f, 12f), new Vector2(4f, 0f))),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 34f);

        Assert.Contains(generator.Candidates, c => c.Key.Kind == CandidateKind.Pass && c.Key.TargetTag == "ToRun");
        Assert.Contains(generator.Candidates, c => c.Key.Kind == CandidateKind.Pass && c.Key.TargetTag == "ToFeet");

        var stationary = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        stationary.GenerateOnBallCandidates(
            Observation(
                new Vector2(0f, 8f),
                Mate(MateId, new Vector2(12f, 12f))),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 34f);

        Assert.DoesNotContain(stationary.Candidates, c => c.Key.TargetTag == "ToRun");
        Assert.Contains(stationary.Candidates, c => c.Key.TargetTag == "ToFeet");

        var blockedSpace = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        blockedSpace.GenerateOnBallCandidates(
            Observation(
                new Vector2(0f, 8f),
                Mate(MateId, new Vector2(12f, 12f), new Vector2(4f, 0f)),
                Away(14, TacticalRole.CenterBack, new Vector2(17.5f, 12f))),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 34f);

        Assert.DoesNotContain(blockedSpace.Candidates, c => c.Key.TargetTag == "ToRun");
    }

    // ---------------------------------------------------------------------------------------------
    // The raised alternative
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A ground lane a defender is physically standing in is hard-rejected, and the raised alternative is
    /// what the carrier is left with to that receiver (A4.2). It must be a *lofted* action — scoring an
    /// airborne ball with a ground flight time is the mistake A5.5 warns about.
    /// </summary>
    [Fact]
    public void ABlockedGroundLane_LeavesTheRaisedAlternativeToTheSameReceiver()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(
            Observation(
                new Vector2(0f, 8f),
                Mate(MateId, new Vector2(15f, 8f)),
                Away(14, TacticalRole.CenterBack, new Vector2(0.6f, 8.55f))),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 34f);

        Assert.DoesNotContain(generator.Candidates,
            c => c.Command.KickType is KickIntentType.GroundPass or KickIntentType.ThroughPass);
        Assert.Contains(generator.Candidates,
            c => c.Command.KickType == KickIntentType.LoftedPass && c.Key.TargetPlayerId == MateId);
    }

    /// <summary>
    /// A raised ball still has to clear the completion floor the ground options use. The A4 pass audit
    /// measured the raised alternative without one: thirteen raised balls in a twenty-minute reference
    /// match and all thirteen were collected by the opposition, because a drag-free landing estimate made
    /// every one of them look playable. Here the lane is blocked <i>and</i> a defender is standing on the
    /// point the ball would come down at, so the estimate itself says the ball is lost — and the option is
    /// refused with a reason instead of offered.
    /// </summary>
    [Fact]
    public void ARaisedPassTheModelCallsHopeless_IsRefusedWithAReason()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(
            Observation(
                new Vector2(0f, 8f),
                Mate(MateId, new Vector2(15f, 8f)),
                // A body in the ground lane, so only the raised alternative is considered at all.
                Away(14, TacticalRole.CenterBack, new Vector2(0.6f, 8.55f)),
                // A marker on the landing point, which is what makes the raised estimate hopeless.
                Away(15, TacticalRole.CenterBack, new Vector2(15.6f, 8.6f))),
            new Vector3(Pitch.HalfLength, 0f, 0f),
            offsideLineProjX: 34f);

        Assert.DoesNotContain(generator.Candidates, c => c.Command.KickType == KickIntentType.LoftedPass);

        List<string> reasons = Enumerable.Range(0, generator.RejectionCount)
            .Select(generator.GetRejection)
            .Select(r => r.Reason)
            .ToList();
        Assert.Contains(CandidateGenerator.RejectionLowCompletion, reasons);
    }

    // ---------------------------------------------------------------------------------------------
    // Bounds
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Both caps are live configuration, not documentation: the pass budget bounds how many receivers get
    /// a trajectory planned, and the carrier cap bounds the finished set (best score first).
    /// </summary>
    [Fact]
    public void TheConfiguredCaps_BoundTheCarrierSet()
    {
        var broad = new CandidateGenerator(Pitch, ai: new AiRuntimeConfig()) { TimeOnBall = 0.8f };
        broad.GenerateOnBallCandidates(ManyMates(), new Vector3(Pitch.HalfLength, 0f, 0f), 34f);
        int plannedTargets = broad.Candidates.Count(c => c.Key.TargetTag == "ToFeet");
        Assert.True(plannedTargets > 1);
        Assert.True(plannedTargets <= AiRuntimeConfig.Default.MaxPassTargets);
        Assert.True(broad.CandidateCount <= AiRuntimeConfig.Default.MaxCarrierCandidates,
            $"{broad.CandidateCount} candidates exceeded the carrier cap");

        var narrowTargets = new CandidateGenerator(Pitch, ai: new AiRuntimeConfig { MaxPassTargets = 1 })
            { TimeOnBall = 0.8f };
        narrowTargets.GenerateOnBallCandidates(ManyMates(), new Vector3(Pitch.HalfLength, 0f, 0f), 34f);
        Assert.Equal(1, narrowTargets.Candidates.Count(c => c.Key.TargetTag == "ToFeet"));

        var narrowCarrier = new CandidateGenerator(Pitch, ai: new AiRuntimeConfig { MaxCarrierCandidates = 2 })
            { TimeOnBall = 0.8f };
        narrowCarrier.GenerateOnBallCandidates(ManyMates(), new Vector3(Pitch.HalfLength, 0f, 0f), 34f);
        Assert.Equal(2, narrowCarrier.CandidateCount);
    }

    /// <summary>Generation with the same inputs produces the same keyed set, in the same order.</summary>
    [Fact]
    public void TheActionSet_IsDeterministicForTheSameInput()
    {
        var first = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        var second = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };

        first.GenerateOnBallCandidates(ManyMates(), new Vector3(Pitch.HalfLength, 0f, 0f), 34f);
        second.GenerateOnBallCandidates(ManyMates(), new Vector3(Pitch.HalfLength, 0f, 0f), 34f);

        Assert.Equal(first.CandidateCount, second.CandidateCount);
        for (int i = 0; i < first.CandidateCount; i++)
        {
            Assert.Equal(first.Candidates[i].Key, second.Candidates[i].Key);
            Assert.Equal(first.Candidates[i].UtilityScore, second.Candidates[i].UtilityScore);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static PerceivedEntity Home(int id, TacticalRole role, Vector2 position, Vector2 velocity = default) => new(
        id, TeamId.Home, role,
        new Vector3(position.X, 0f, position.Y),
        new Vector3(velocity.X, 0f, velocity.Y),
        1f);

    private static PerceivedEntity Away(int id, TacticalRole role, Vector2 position) => new(
        id, TeamId.Away, role,
        new Vector3(position.X, 0f, position.Y),
        Vector3.Zero,
        1f);

    private static PerceivedEntity Mate(int id, Vector2 position, Vector2 velocity = default) =>
        Home(id, TacticalRole.CentralMidfielder, position, velocity);

    private static FootballerObservation ManyMates() => Observation(
        new Vector2(0f, 8f),
        Mate(MateId, new Vector2(6f, 14f)),
        Mate(SecondMateId, new Vector2(10f, 2f)),
        Mate(11, new Vector2(16f, -10f), new Vector2(3f, 0f)),
        Mate(12, new Vector2(22f, 18f)),
        Mate(13, new Vector2(-4f, -12f)));

    private static FootballerObservation Observation(Vector2 carrierPosition, params PerceivedEntity[] visible) => new()
    {
        SelfId = CarrierId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CentralMidfielder,
        SelfPosition = new Vector3(carrierPosition.X, 0f, carrierPosition.Y),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = new Vector3(carrierPosition.X + 0.4f, 0f, carrierPosition.Y),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = CarrierId,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = CarrierId,
        TacticalAnchorPosition = new Vector3(carrierPosition.X, 0f, carrierPosition.Y),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 20f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = 500,
        VisiblePlayers = visible,
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.CentralMidfielder),
        SelfAttributes = new PlayerAttributes()
    };
}
