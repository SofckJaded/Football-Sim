using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A8.4 — the short trap recognition, at the model level. Every test asks the election one question at a time
/// from the fixture in <see cref="TrapTestWorld"/>: the carrier pinned on the negative touchline, the elected
/// presser close to him, the allocator's cover behind the press and one shape-duty midfielder near the lane the
/// carrier would use to get out.
/// </summary>
public sealed class TrapPolicyTests
{
    private const int PresserId = TrapTestWorld.HomeFullBack;
    private const int CoverId = TrapTestWorld.HomeCover;
    private const int ScreenerId = TrapTestWorld.HomeScreener;
    private const int CarrierId = TrapTestWorld.AwayCarrier;

    private static readonly PitchDimensions Pitch = TrapTestWorld.Pitch;
    private static readonly TeamTacticalProfile Profile = TeamTacticalProfile.PossessionControl;

    [Fact]
    public void ACarrierPinnedOnTheLine_WithPressCoverAndAScreener_IsRecognized()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        TeamBelief belief = TeamBelief.FromTruth(TeamId.Home, world.Players);

        bool recognized = TryEvaluate(world, belief, out TrapOpportunity opportunity);

        Assert.True(recognized, "the fixture's picture is a touchline trap");
        Assert.Equal(TrapKind.Touchline, opportunity.Kind);
        Assert.Equal(CarrierId, opportunity.CarrierId);
        Assert.Equal(PresserId, opportunity.PresserId);
        Assert.Equal(CoverId, opportunity.CoverId);
        Assert.Equal(ScreenerId, opportunity.ScreenId);
        Assert.InRange(opportunity.Confidence01, TrapPolicy.MinTrapConfidence01, 1f);
        Assert.True(opportunity.IsRecognized);

        // The presser stands just outside contact reach, on the inside of the carrier; the screen stands on
        // the carrier's forward lane, inside the pitch.
        Assert.InRange(opportunity.PressOffsetXZ.Length(),
            PressPolicy.PressStandoffMeters - 0.6f, PressPolicy.PressStandoffMeters + 0.6f);
        Assert.True(opportunity.ScreenOffsetXZ.Y > 0f,
            $"the screen must be inside the touchline carrier (offset Y {opportunity.ScreenOffsetXZ.Y:F2})");
    }

    /// <summary>
    /// The presser's point is A6.2's own angled pressure point for the shared picture — the trap reuses the one
    /// angle scoring instead of inventing a second one.
    /// </summary>
    [Fact]
    public void ThePressPoint_IsA6sAngledPressurePoint()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        TeamBelief belief = TeamBelief.FromTruth(TeamId.Home, world.Players);
        Assert.True(TryEvaluate(world, belief, out TrapOpportunity opportunity));

        FootballerState carrier = world.Player(CarrierId);
        FootballerState presser = world.Player(PresserId);
        Vector2 carrierXZ = new(carrier.Position.X, carrier.Position.Z);
        Vector2 presserXZ = new(presser.Position.X, presser.Position.Z);
        Vector2 expected = PressPolicy.AngledPressPoint(
            carrierXZ, new Vector2(carrier.Velocity.X, carrier.Velocity.Z), presserXZ,
            PressPolicy.Touchline01(carrierXZ, Pitch), attackDirX: 1f, Pitch,
            PressPolicy.PressStandoffMeters, support: false);

        float error = Vector2.Distance(carrierXZ + opportunity.PressOffsetXZ, expected);
        Assert.True(error < 1e-4f, $"the trap press point differs from A6.2's by {error:F4} m");
    }

    [Fact]
    public void ACarrierInTheMiddleOfThePitch_IsNotTrapped()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        MoveCarrier(world, new Vector3(0f, 0f, 0f));
        TeamBelief belief = TeamBelief.FromTruth(TeamId.Home, world.Players);

        Assert.False(TryEvaluate(world, belief, out _));
    }

    [Fact]
    public void APresserTooFarAway_IsNotATrap()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        world.Player(PresserId).Position = new Vector3(-18f, 0f, -28f); // ~18 m from the carrier
        TeamBelief belief = TeamBelief.FromTruth(TeamId.Home, world.Players);

        Assert.False(TryEvaluate(world, belief, out _));
    }

    /// <summary>The exit gate's structural half: without the allocator's cover there is no trap to preserve.</summary>
    [Fact]
    public void WithoutTheAllocatorsCover_NoTrapIsRaised()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        world.Player(CoverId).IsSubstitutedOrInjured = true;
        TeamBelief belief = TeamBelief.FromTruth(TeamId.Home, world.Players);

        Assert.False(TryEvaluate(world, belief, out _));
    }

    /// <summary>A man the allocator committed to a line, a gap or the cover is never re-pointed by a trap.</summary>
    [Fact]
    public void OnlyAShapeDutyPlayer_CanBeTheScreener()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        world.Player(ScreenerId).IsSubstitutedOrInjured = true;
        TeamBelief belief = TeamBelief.FromTruth(TeamId.Home, world.Players);

        Assert.False(TryEvaluate(world, belief, out _));
    }

    [Fact]
    public void TheNearestShapeDutyPlayer_IsTheScreener_EvenWhenAnotherDefenderStandsCloser()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        // A line-duty centre-back much closer to the screen point: he is not offered the trap's screen.
        world.Player(TrapTestWorld.HomeCenterBackTwo).Position = new Vector3(-2f, 0f, -26f);
        TeamBelief belief = TeamBelief.FromTruth(TeamId.Home, world.Players);

        Assert.True(TryEvaluate(world, belief, out TrapOpportunity opportunity));
        Assert.Equal(ScreenerId, opportunity.ScreenId);
    }

    [Fact]
    public void AStalePicture_IsNotATrap()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();

        Assert.False(TryEvaluate(world, TrapTestWorld.AgedBelief(world, TeamId.Home,
            TrapPolicy.TrapMaxCarrierAgeTicks + 1), out _), "a stale carrier belief must not buy a trap");

        Assert.True(TryEvaluate(world, TrapTestWorld.AgedBelief(world, TeamId.Home, 0), out _),
            "the same picture seen this tick is a trap");
    }

    [Fact]
    public void AnUnknownCarrier_IsNotATrap()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        TeamBelief blind = TrapTestWorld.AgedBelief(world, TeamId.Home, 0, includeOpponents: false);

        Assert.False(TryEvaluate(world, blind, out _));
    }

    [Fact]
    public void TheLockedHuman_IsNeverNamed()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        TeamBelief belief = TeamBelief.FromTruth(TeamId.Home, world.Players);

        Assert.False(TrapPolicy.TryEvaluate(TeamId.Home, world.Players, belief, TrapTestWorld.Possession(),
            PresserId, supportPresserId: null, world.Jobs, humanPlayerId: PresserId, attackDirX: 1f,
            Profile.Compactness01, Pitch, out _), "a human presser is not a trap the AI coordinates");

        Assert.False(TrapPolicy.TryEvaluate(TeamId.Home, world.Players, belief, TrapTestWorld.Possession(),
            PresserId, supportPresserId: null, world.Jobs, humanPlayerId: ScreenerId, attackDirX: 1f,
            Profile.Compactness01, Pitch, out _), "the screen would re-point the locked human");
    }

    /// <summary>A trap needs a real press: the same picture with the presser arriving is a weaker trap.</summary>
    [Fact]
    public void Confidence_RisesWithPressureAndWithHowPinnedTheCarrierIs()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        TeamBelief belief = TeamBelief.FromTruth(TeamId.Home, world.Players);
        Assert.True(TryEvaluate(world, belief, out TrapOpportunity close));

        world.Player(PresserId).Position = new Vector3(-8f, 0f, -30f); // still engaged, further from the carrier
        Assert.True(TryEvaluate(world, belief, out TrapOpportunity far));
        Assert.True(close.Confidence01 > far.Confidence01,
            $"tighter pressure must raise the confidence ({close.Confidence01:F3} vs {far.Confidence01:F3})");

        world.Player(PresserId).Position = new Vector3(-7f, 0f, -28.5f);
        MoveCarrier(world, new Vector3(0f, 0f, -27f)); // still inside the band, less pinned
        Assert.True(TryEvaluate(world, TeamBelief.FromTruth(TeamId.Home, world.Players), out TrapOpportunity midLine));
        Assert.True(close.Confidence01 > midLine.Confidence01,
            $"a carrier closer to the line must raise the confidence ({close.Confidence01:F3} vs " +
            $"{midLine.Confidence01:F3})");
    }

    [Fact]
    public void TheSamePicture_RecognizesTheSameTrap()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        TeamBelief belief = TeamBelief.FromTruth(TeamId.Home, world.Players);

        Assert.True(TryEvaluate(world, belief, out TrapOpportunity first));
        Assert.True(TryEvaluate(world, belief, out TrapOpportunity second));
        Assert.Equal(first, second);
    }

    [Fact]
    public void TheEscapeCondition_IsTheBoundedTrapRegion()
    {
        var contract = new TrapContract(
            TrapId: 1, Kind: TrapKind.Touchline, Team: TeamId.Home, CarrierId: CarrierId,
            PresserId: PresserId, ScreenId: ScreenerId, CoverId: CoverId,
            CarrierXZ: new Vector2(0f, -30f), PressOffsetXZ: new Vector2(-1f, 1.2f),
            ScreenOffsetXZ: new Vector2(-2f, 3f), CreatedTick: 1, ExpiresTick: 91, Confidence01: 0.5f,
            Pressure01: 0.3f, Touchline01: 0.6f, CoverFit01: 0.9f, Screen01: 0.2f);

        Assert.False(TrapPolicy.IsEscaped(contract, new Vector2(0f, -30f)));
        Assert.False(TrapPolicy.IsEscaped(contract,
            new Vector2(TrapPolicy.TrapEscapeMeters - 0.1f, -30f)));
        Assert.True(TrapPolicy.IsEscaped(contract,
            new Vector2(TrapPolicy.TrapEscapeMeters + 0.1f, -30f)));
        Assert.True(TrapPolicy.IsEscaped(contract, new Vector2(0f, -30f + TrapPolicy.TrapEscapeMeters + 0.2f)));
    }

    /// <summary>
    /// The trap's whole mechanical contribution to the screen is one bounded preference: the same observation
    /// with a confidence-1 trap and a confidence-0 trap must produce the same screen candidate at the same point,
    /// exactly <see cref="TrapPolicy.ScreenPreference"/> apart.
    /// </summary>
    [Fact]
    public void TheScreenCandidate_CarriesExactlyTheBoundedPreference()
    {
        TrapTestWorld.World world = TrapTestWorld.Build();
        TeamBelief belief = TeamBelief.FromTruth(TeamId.Home, world.Players);
        Assert.True(TryEvaluate(world, belief, out TrapOpportunity opportunity));

        FootballerState screener = world.Player(ScreenerId);
        TrapContext full = TrapContextFor(world, opportunity, confidence01: 1f);
        TrapContext none = TrapContextFor(world, opportunity, confidence01: 0f);

        PressCandidate fullCandidate = ScreenerCandidate(world, screener, full);
        PressCandidate noneCandidate = ScreenerCandidate(world, screener, none);

        Assert.Equal(PressAction.Screen, fullCandidate.Action);
        Assert.Equal(PressAction.Screen, noneCandidate.Action);
        Assert.Equal(fullCandidate.TargetXZ.X, noneCandidate.TargetXZ.X, 4);
        Assert.Equal(fullCandidate.TargetXZ.Y, noneCandidate.TargetXZ.Y, 4);
        Assert.Equal(TrapPolicy.ScreenPreference, fullCandidate.Score - noneCandidate.Score, 4);
        Assert.False(fullCandidate.IsPrimary, "the screen competes with the screener's own duty, it does not replace it");
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    private static bool TryEvaluate(
        TrapTestWorld.World world, TeamBelief belief, out TrapOpportunity opportunity) =>
        TrapPolicy.TryEvaluate(TeamId.Home, world.Players, belief, TrapTestWorld.Possession(),
            PresserId, supportPresserId: null, world.Jobs, humanPlayerId: null, attackDirX: 1f,
            Profile.Compactness01, Pitch, out opportunity);

    private static void MoveCarrier(TrapTestWorld.World world, Vector3 position)
    {
        world.Player(CarrierId).Position = position;
        world.Player(CarrierId).TacticalAnchor = position;
        world.Ball.Position = position with { Y = 0.11f };
        world.Ball.PreviousPosition = world.Ball.Position;
    }

    private static TrapContext TrapContextFor(
        TrapTestWorld.World world, in TrapOpportunity opportunity, float confidence01)
    {
        FootballerState carrier = world.Player(CarrierId);
        Vector2 carrierXZ = new(carrier.Position.X, carrier.Position.Z);
        return new TrapContext(
            TrapId: 1, Kind: TrapKind.Touchline, Role: TrapRole.Screen, CarrierId,
            PresserId, ScreenerId, CoverId,
            CarrierXZ: carrierXZ,
            PressOffsetXZ: opportunity.PressOffsetXZ,
            ScreenOffsetXZ: opportunity.ScreenOffsetXZ,
            CreatedTick: 1, ExpiresTick: 91, Confidence01: confidence01);
    }

    /// <summary>The screener's own observation of the picture, built through the one perception seam.</summary>
    private static FootballerObservation ObserveScreener(
        TrapTestWorld.World world, FootballerState screener, TrapContext trap) =>
        new PerceptionSystem().BuildObservation(
            screener, world.Players, world.Ball, confirmedPossessingTeam: TeamId.Away,
            matchPhase: MatchPhase.FirstHalf, matchTimeSeconds: 10f, attackingDirX: 1f,
            isSetPieceTaker: false, teamProfile: Profile, rng: new SeededRandom(7),
            possession: TrapTestWorld.Possession(),
            isDesignatedBallResponder: false,
            job: world.Jobs.Get(screener.Id),
            tick: 30,
            ballResponderPlayerId: PresserId,
            supportPresserPlayerId: null,
            trap: trap);

    private static PressCandidate ScreenerCandidate(
        TrapTestWorld.World world, FootballerState screener, TrapContext trap)
    {
        FootballerObservation observation = ObserveScreener(world, screener, trap);
        PressSituation situation = PressPolicy.BuildSituation(observation, Pitch);
        Span<PressCandidate> buffer = stackalloc PressCandidate[PressPolicy.MaxCandidates];
        int count = PressPolicy.Build(observation, situation, Pitch, buffer);
        Assert.Equal(1, count);
        return buffer[0];
    }
}
