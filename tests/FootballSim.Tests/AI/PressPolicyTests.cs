using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A6.1 — pressure-cover-balance, and A6.2 — angled pressure and cover-shadow scoring, at the model level.
///
/// Every fixture is built from one player's own observation: the carrier is an opponent he can see, his
/// team-mates are the team-mates he observes, and the policy answers from that picture alone. The clause
/// each test exists for is named in its summary.
/// </summary>
public sealed class PressPolicyTests
{
    private const int SelfId = 4;
    private const int PresserId = 6;
    private const int CoverMateId = 5;
    private const int OtherMateId = 7;
    private const int CarrierId = 15;
    private const int RunnerId = 12;

    private static readonly PitchDimensions Pitch = new();

    // ---------------------------------------------------------------------------------------------
    // A6.1 — one second man, and only when the space behind the press is covered.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A6.1's balance rule: a second defender who is <b>not</b> goal-side of the carrier and has nobody
    /// behind the press must not dive in. He delays the carrier instead. Add a covering team-mate behind
    /// the presser and the same defender is allowed to step in — the only difference between the two
    /// worlds is cover, which is exactly what the rule says decides it.
    /// </summary>
    [Fact]
    public void AnUnsupportedSecondMan_DelaysInsteadOfDivingIn()
    {
        var visible = new List<PerceivedEntity>
        {
            Opponent(CarrierId, TacticalRole.Winger, new Vector2(-35f, 0f)),
            Mate(PresserId, TacticalRole.DefensiveMidfielder, new Vector2(-36f, -1f))
        };

        PressSituation uncovered = PressPolicy.BuildSituation(Observe(
            new Vector2(-32f, 6f), TacticalRole.CenterBack, TacticalJob.Shape, visible,
            carrierXZ: new Vector2(-35f, 0f)), Pitch);

        Assert.False(uncovered.HasCoverBehind);
        Assert.True(uncovered.IsSupport, "the fixture's centre-back is the nearest eligible second man");

        Span<PressCandidate> buffer = stackalloc PressCandidate[PressPolicy.MaxCandidates];
        int count = PressPolicy.Build(Observe(
            new Vector2(-32f, 6f), TacticalRole.CenterBack, TacticalJob.Shape, visible,
            carrierXZ: new Vector2(-35f, 0f)), uncovered, Pitch, buffer);

        Assert.Equal(1, count);
        Assert.Equal(PressAction.Delay, buffer[0].Action);
        Assert.True(buffer[0].IsPrimary, "a delay is this defender's decision for the tick, not an alternative");
        float distance = Vector2.Distance(buffer[0].TargetXZ, uncovered.CarrierXZ);
        Assert.InRange(distance, PressPolicy.SupportDelayStandoffMeters - 1.2f,
            PressPolicy.SupportDelayStandoffMeters + 1.2f);

        // The covered world: a centre-back goal-side of the presser, and far enough from the carrier that
        // he is not the second man himself.
        visible.Add(Mate(CoverMateId, TacticalRole.CenterBack, new Vector2(-42f, 4f)));
        FootballerObservation coveredObservation = Observe(
            new Vector2(-32f, 6f), TacticalRole.CenterBack, TacticalJob.Shape, visible,
            carrierXZ: new Vector2(-35f, 0f));
        PressSituation covered = PressPolicy.BuildSituation(coveredObservation, Pitch);

        Assert.True(covered.HasCoverBehind);
        Assert.True(covered.IsSupport);

        count = PressPolicy.Build(coveredObservation, covered, Pitch, buffer);
        Assert.Equal(1, count);
        Assert.Equal(PressAction.SupportPress, buffer[0].Action);
        Assert.True(buffer[0].IsPrimary);
        Assert.True(covered.Urgency01 > uncovered.Urgency01,
            $"cover must raise the urgency ({covered.Urgency01:F2} vs {uncovered.Urgency01:F2})");
    }

    /// <summary>
    /// The other half of the rule: a defender who is himself goal-side of the carrier steps <i>forward</i>
    /// to press him, so he abandons nothing — no cover is needed. This is the old body challenge's case,
    /// kept deliberately.
    /// </summary>
    [Fact]
    public void ASecondManGoalSideOfTheCarrier_MayStepInWithoutCover()
    {
        var visible = new List<PerceivedEntity>
        {
            Opponent(CarrierId, TacticalRole.Striker, new Vector2(-35f, 0f)),
            Mate(PresserId, TacticalRole.DefensiveMidfielder, new Vector2(-34f, 2f))
        };

        FootballerObservation observation = Observe(
            new Vector2(-39f, 2f), TacticalRole.CenterBack, TacticalJob.DefensiveLine, visible,
            carrierXZ: new Vector2(-35f, 0f), jobAnchor: new Vector3(-39f, 0f, 2f));

        PressSituation situation = PressPolicy.BuildSituation(observation, Pitch);
        Assert.False(situation.HasCoverBehind);
        Assert.True(situation.IsSupport);

        Span<PressCandidate> buffer = stackalloc PressCandidate[PressPolicy.MaxCandidates];
        int count = PressPolicy.Build(observation, situation, Pitch, buffer);

        Assert.Equal(1, count);
        Assert.Equal(PressAction.SupportPress, buffer[0].Action);
        Assert.False(buffer[0].WantsTackle, "8 m away is not a tackle");
    }

    /// <summary>
    /// Exactly one team-mate is ever the second man, decided by the same nearest-eligible rule for every
    /// observer. Two defenders independently deciding to step in is the failure this removes.
    /// </summary>
    [Fact]
    public void OnlyOneTeammate_IsEverTheSecondMan()
    {
        Vector2 carrierXZ = new(-35f, 0f);
        var nearVisible = new List<PerceivedEntity>
        {
            Opponent(CarrierId, TacticalRole.Winger, carrierXZ),
            Mate(PresserId, TacticalRole.DefensiveMidfielder, new Vector2(-36f, 1f)),
            Mate(OtherMateId, TacticalRole.CenterBack, new Vector2(-29f, 9f))
        };
        var farVisible = new List<PerceivedEntity>
        {
            Opponent(CarrierId, TacticalRole.Winger, carrierXZ),
            Mate(PresserId, TacticalRole.DefensiveMidfielder, new Vector2(-36f, 1f)),
            Mate(OtherMateId, TacticalRole.CenterBack, new Vector2(-32f, 4f))
        };

        PressSituation near = PressPolicy.BuildSituation(Observe(
            new Vector2(-32f, 4f), TacticalRole.CenterBack, TacticalJob.DefensiveLine, nearVisible,
            carrierXZ: carrierXZ, jobAnchor: new Vector3(-32f, 0f, 4f)), Pitch);

        PressSituation far = PressPolicy.BuildSituation(Observe(
            new Vector2(-29f, 9f), TacticalRole.CenterBack, TacticalJob.DefensiveLine, farVisible,
            carrierXZ: carrierXZ, jobAnchor: new Vector3(-29f, 0f, 9f)), Pitch);

        Assert.True(near.IsSupport);
        Assert.False(far.IsSupport);
        Assert.Equal(near.PresserId, far.PresserId);
    }

    // ---------------------------------------------------------------------------------------------
    // A6.2 — the cover stands where its shadow is worth something.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A6.2's cover shadow. The central lane is the dangerous one, so the cover drops onto it; a point four
    /// metres to the side shadows measurably less. The shadow — not a fixed seven-metre depth — is what
    /// chooses the point.
    /// </summary>
    [Fact]
    public void TheCoverPoint_BlocksTheDangerousLane()
    {
        var visible = new List<PerceivedEntity>
        {
            Opponent(CarrierId, TacticalRole.CentralMidfielder, new Vector2(-30f, 0f)),
            // The opponent's most dangerous forward option: straight down the middle, toward the goal.
            Opponent(21, TacticalRole.Striker, new Vector2(-42f, 0f)),
            // A wide runner is a real option but the central one is the one that hurts.
            Opponent(22, TacticalRole.Winger, new Vector2(-38f, 9f)),
            Mate(PresserId, TacticalRole.DefensiveMidfielder, new Vector2(-32f, 0f))
        };

        var observation = Observe(
            new Vector2(-37f, 0f), TacticalRole.CenterBack, TacticalJob.CoverDefender, visible,
            carrierXZ: new Vector2(-30f, 0f), jobAnchor: new Vector3(-38f, 0f, 0f));

        PressSituation situation = PressPolicy.BuildSituation(observation, Pitch);
        Assert.True(situation.IsCover);

        Span<PressCandidate> buffer = stackalloc PressCandidate[PressPolicy.MaxCandidates];
        int count = PressPolicy.Build(observation, situation, Pitch, buffer);

        Assert.Equal(1, count);
        Assert.Equal(PressAction.Cover, buffer[0].Action);
        Assert.True(buffer[0].Shadow01 > 0.35f,
            $"the chosen cover point shadows only {buffer[0].Shadow01:F2} of the danger");

        // Goal-side of the presser, on the central lane, and better than the same depth shifted sideways.
        Vector2 goalSide = PressPolicy.GoalSideXZ(situation.CarrierXZ, observation, Pitch);
        float depth = Vector2.Dot(buffer[0].TargetXZ - situation.PresserXZ, goalSide);
        Assert.True(depth > 4f, $"the cover is only {depth:F1} m behind the press");
        Assert.True(MathF.Abs(buffer[0].TargetXZ.Y) < 2.5f,
            $"the cover left the central lane (Z={buffer[0].TargetXZ.Y:F1})");

        Vector2 shifted = buffer[0].TargetXZ + new Vector2(0f, 4f);
        float shiftedShadow = PressPolicy.CoverShadow01(
            shifted, situation, visible, TeamId.Home, observation.AttackingDirectionX, Pitch);
        Assert.True(buffer[0].Shadow01 > shiftedShadow + 0.2f,
            $"the chosen point ({buffer[0].Shadow01:F2}) does not beat the side one ({shiftedShadow:F2})");
    }

    /// <summary>
    /// A shape-duty player with nobody to press screens the most dangerous forward pass instead of walking
    /// to his anchor: the point is on the carrier-to-receiver lane and it shadows it.
    /// </summary>
    [Fact]
    public void AScreenStandsOnTheDangerousLane()
    {
        Vector2 carrierXZ = new(-10f, 0f);
        var visible = new List<PerceivedEntity>
        {
            Opponent(CarrierId, TacticalRole.CentralMidfielder, carrierXZ),
            Opponent(21, TacticalRole.Striker, new Vector2(-25f, 6f)),
            Mate(PresserId, TacticalRole.DefensiveMidfielder, new Vector2(-11f, 1f))
        };

        var observation = Observe(
            new Vector2(0f, -5f), TacticalRole.CentralMidfielder, TacticalJob.Shape, visible,
            carrierXZ: carrierXZ, jobAnchor: new Vector3(0f, 0f, -5f));

        PressSituation situation = PressPolicy.BuildSituation(observation, Pitch);
        Assert.False(situation.IsSupport, "a central midfielder is not one of the two second-man roles");
        Assert.False(situation.IsCover);

        Span<PressCandidate> buffer = stackalloc PressCandidate[PressPolicy.MaxCandidates];
        int count = PressPolicy.Build(observation, situation, Pitch, buffer);

        Assert.Equal(1, count);
        Assert.Equal(PressAction.Screen, buffer[0].Action);
        Assert.False(buffer[0].IsPrimary, "a screen competes with the player's duty, it does not replace it");
        Assert.Equal(21, buffer[0].TargetPlayerId);
        Assert.True(buffer[0].Shadow01 > 0.45f, $"the screen shadows {buffer[0].Shadow01:F2}");
    }

    /// <summary>
    /// A marking duty tracks the man: the target moves with the runner and stays on his goal side, instead
    /// of pointing at the anchor chosen a few ticks ago.
    /// </summary>
    [Fact]
    public void AMarkerTracksTheRunner()
    {
        Vector2 runnerXZ = new(-20f, 4f);
        var visible = new List<PerceivedEntity>
        {
            Opponent(CarrierId, TacticalRole.CentralMidfielder, new Vector2(-30f, 0f)),
            Opponent(RunnerId, TacticalRole.Striker, runnerXZ, new Vector2(-3f, 0f)),
            Mate(PresserId, TacticalRole.DefensiveMidfielder, new Vector2(-31f, 0f)),
            // Closer to the carrier than this marker is, so the second man is somebody else and this
            // player's marking duty is what the policy is asked about.
            Mate(OtherMateId, TacticalRole.CenterBack, new Vector2(-28f, 2f))
        };

        var observation = Observe(
            new Vector2(-18f, 2f), TacticalRole.CenterBack, TacticalJob.Marker, visible,
            carrierXZ: new Vector2(-30f, 0f), jobTargetPlayerId: RunnerId,
            jobAnchor: new Vector3(-20f, 0f, 4f));

        PressSituation situation = PressPolicy.BuildSituation(observation, Pitch);
        Span<PressCandidate> buffer = stackalloc PressCandidate[PressPolicy.MaxCandidates];
        int count = PressPolicy.Build(observation, situation, Pitch, buffer);

        Assert.Equal(1, count);
        Assert.Equal(PressAction.Track, buffer[0].Action);
        Assert.Equal(RunnerId, buffer[0].TargetPlayerId);
        Assert.True(buffer[0].TargetXZ.X < runnerXZ.X,
            $"the tracker was sent to X={buffer[0].TargetXZ.X:F1}, not goal-side of the runner at {runnerXZ.X:F1}");
        float distance = Vector2.Distance(buffer[0].TargetXZ, runnerXZ);
        Assert.InRange(distance, 0.5f, 2.0f);
    }

    /// <summary>
    /// A line that has been beaten drops toward its own goal: a runner beyond the press turns the line
    /// duty's hold into a retreat.
    /// </summary>
    [Fact]
    public void ABeatenLineDrops()
    {
        var visible = new List<PerceivedEntity>
        {
            Opponent(CarrierId, TacticalRole.Striker, new Vector2(-36f, 0f)),
            Opponent(RunnerId, TacticalRole.Winger, new Vector2(-48f, 0f)),
            Mate(PresserId, TacticalRole.DefensiveMidfielder, new Vector2(-34f, 0f)),
            // Somebody else is the second man, so this defender's line duty is what the policy is asked about.
            Mate(OtherMateId, TacticalRole.CenterBack, new Vector2(-34f, -2f))
        };

        var observation = Observe(
            new Vector2(-28f, 4f), TacticalRole.CenterBack, TacticalJob.DefensiveLine, visible,
            carrierXZ: new Vector2(-36f, 0f), jobAnchor: new Vector3(-28f, 0f, 4f));

        PressSituation situation = PressPolicy.BuildSituation(observation, Pitch);
        Assert.False(situation.IsSupport);
        Assert.True(situation.BypassRisk01 >= PressPolicy.DropBypassRisk,
            $"the fixture's bypass risk is only {situation.BypassRisk01:F2}");

        Span<PressCandidate> buffer = stackalloc PressCandidate[PressPolicy.MaxCandidates];
        int count = PressPolicy.Build(observation, situation, Pitch, buffer);

        Assert.Equal(1, count);
        Assert.Equal(PressAction.Drop, buffer[0].Action);
        Assert.False(buffer[0].IsPrimary);
        Assert.Equal(-28f - PressPolicy.DropMeters, buffer[0].TargetXZ.X, 0.01f);
        Assert.Equal(4f, buffer[0].TargetXZ.Y, 0.01f);
    }

    // ---------------------------------------------------------------------------------------------
    // A6.2 — angled pressure.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A6.2's angle: a carrier on the touchline is pressed from the inside, so the line itself becomes the
    /// second defender and the only exit left is the one the defending team wants him to take.
    /// </summary>
    [Fact]
    public void ATouchlineCarrier_IsPressedFromTheInside()
    {
        Vector2 carrierXZ = new(0f, -30f);
        var observation = Observe(
            new Vector2(-4f, -18f), TacticalRole.CenterBack, TacticalJob.Presser,
            new List<PerceivedEntity> { Opponent(CarrierId, TacticalRole.Winger, carrierXZ) },
            carrierXZ: carrierXZ, isResponder: true);

        PressSituation situation = PressPolicy.BuildSituation(observation, Pitch);
        Assert.True(situation.IsPresser);

        Vector2 target = PressPolicy.PressTarget(
            observation, situation, Pitch, PressPolicy.PressStandoffMeters, support: false);

        Assert.True(target.Y > carrierXZ.Y + 0.4f,
            $"the touchline carrier was pressed from Z={target.Y:F1}, not from the inside of {carrierXZ.Y:F1}");
        float distance = Vector2.Distance(target, carrierXZ);
        Assert.InRange(distance, PressPolicy.PressStandoffMeters - 0.4f, PressPolicy.PressStandoffMeters + 0.9f);
    }

    /// <summary>
    /// The same rule in the middle of the pitch: the dangerous exit is the one toward the goal, so the
    /// presser stands goal-side of the carrier.
    /// </summary>
    [Fact]
    public void ACentralCarrier_IsPressedFromTheGoalSide()
    {
        Vector2 carrierXZ = new(0f, 0f);
        var observation = Observe(
            new Vector2(-6f, 2f), TacticalRole.CenterBack, TacticalJob.Presser,
            new List<PerceivedEntity> { Opponent(CarrierId, TacticalRole.CentralMidfielder, carrierXZ) },
            carrierXZ: carrierXZ, isResponder: true);

        PressSituation situation = PressPolicy.BuildSituation(observation, Pitch);
        Vector2 target = PressPolicy.PressTarget(
            observation, situation, Pitch, PressPolicy.PressStandoffMeters, support: false);

        Assert.True(target.X < carrierXZ.X - 0.4f,
            $"the central carrier was pressed from X={target.X:F1}, not from the goal side of {carrierXZ.X:F1}");
    }

    /// <summary>
    /// The second man takes the side of the carrier the presser is not on, so two bodies shade two exits
    /// instead of stacking on one.
    /// </summary>
    [Fact]
    public void TheSecondManTakesTheOppositeSideOfTheCarrier()
    {
        Vector2 carrierXZ = new(-20f, 0f);
        var visible = new List<PerceivedEntity>
        {
            Opponent(CarrierId, TacticalRole.CentralMidfielder, carrierXZ),
            Mate(PresserId, TacticalRole.DefensiveMidfielder, new Vector2(-18f, 0f)),
            // Goal-side of the press and far enough from the carrier not to be the second man himself.
            Mate(CoverMateId, TacticalRole.CenterBack, new Vector2(-28f, -3f))
        };

        var observation = Observe(
            new Vector2(-22f, 6f), TacticalRole.CenterBack, TacticalJob.Shape, visible,
            carrierXZ: carrierXZ);

        PressSituation situation = PressPolicy.BuildSituation(observation, Pitch);
        Assert.True(situation.IsSupport);

        Span<PressCandidate> buffer = stackalloc PressCandidate[PressPolicy.MaxCandidates];
        int count = PressPolicy.Build(observation, situation, Pitch, buffer);

        Assert.Equal(1, count);
        Assert.Equal(PressAction.SupportPress, buffer[0].Action);

        Vector2 presserSide = situation.PresserXZ - carrierXZ;
        Vector2 supportSide = buffer[0].TargetXZ - carrierXZ;
        Assert.True(Vector2.Dot(presserSide, supportSide) < 0.1f * presserSide.Length() * supportSide.Length(),
            $"the second man stood on the presser's side ({supportSide.X:F1}, {supportSide.Y:F1})");
    }

    /// <summary>
    /// The depth of the press is the balance: with cover the presser closes to contact range, without it
    /// he holds the carrier at a jockeying distance once the press can be bypassed.
    /// </summary>
    [Fact]
    public void TheLonePresserJockeys_WhenThePressCanBeBypassed()
    {
        var visible = new List<PerceivedEntity>
        {
            Opponent(CarrierId, TacticalRole.Striker, new Vector2(-36f, 0f)),
            // A runner already goal-side of where the press is happening: losing the tackle is exposed.
            Opponent(RunnerId, TacticalRole.Winger, new Vector2(-44f, 0f))
        };

        FootballerObservation uncoveredObservation = Observe(
            new Vector2(-34f, 2f), TacticalRole.CenterBack, TacticalJob.Presser, visible,
            carrierXZ: new Vector2(-36f, 0f), isResponder: true);
        PressSituation uncovered = PressPolicy.BuildSituation(uncoveredObservation, Pitch);

        Assert.False(uncovered.HasCoverBehind);
        Assert.True(uncovered.BypassRisk01 > PressPolicy.JockeyBypassRisk);
        Assert.Equal(PressPolicy.DelayStandoffMeters, PressPolicy.PressStandoffFor(uncovered));

        visible.Add(Mate(OtherMateId, TacticalRole.CenterBack, new Vector2(-40f, 3f)));
        PressSituation covered = PressPolicy.BuildSituation(
            Observe(new Vector2(-34f, 2f), TacticalRole.CenterBack, TacticalJob.Presser, visible,
                carrierXZ: new Vector2(-36f, 0f), isResponder: true), Pitch);

        Assert.True(covered.HasCoverBehind);
        Assert.Equal(PressPolicy.PressStandoffMeters, PressPolicy.PressStandoffFor(covered));
    }

    // ---------------------------------------------------------------------------------------------
    // A6.2 — the evidence.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A pass travelling back toward the opponent's own goal is read as evidence, and a slow one too. Both
    /// are observable facts about a ball the defender can see, which is what keeps them legal inputs.
    /// </summary>
    [Fact]
    public void ABackPassAndASlowPass_AreReadFromTheBall()
    {
        FootballerObservation observation = Observe(
            new Vector2(0f, 0f), TacticalRole.CenterBack, TacticalJob.Shape,
            new List<PerceivedEntity>(), carrierXZ: Vector2.Zero,
            control: BallControlState.InFlightPass,
            ballPosition: new Vector3(-10f, 0.11f, 0f),
            ballVelocity: new Vector3(4f, 0f, 0f),
            lastTouchTeam: TeamId.Away, lastTouchPlayer: CarrierId);

        PressEvidence evidence = PressPolicy.BuildSituation(observation, Pitch).Evidence;

        Assert.Equal(1f, evidence.BackPass01, 3);
        Assert.True(evidence.SlowPass01 > 0.5f, $"a 4 m/s pass is slow ({evidence.SlowPass01:F2})");
        Assert.True(PressPolicy.EvidenceStrength(evidence) > 0.3f,
            $"a back pass and a slow pass together score {PressPolicy.EvidenceStrength(evidence):F2}");
    }

    /// <summary>
    /// A loose ball that has just squirted away from the opponent who touched it reads as a poor touch.
    /// The ball is the observation; nothing about the opponent's controller is read.
    /// </summary>
    [Fact]
    public void ALooseBallNearItsToucher_ReadsAsAPoorTouch()
    {
        var visible = new List<PerceivedEntity>
        {
            Opponent(CarrierId, TacticalRole.CentralMidfielder, new Vector2(5f, 0f))
        };

        FootballerObservation observation = Observe(
            new Vector2(0f, 0f), TacticalRole.CenterBack, TacticalJob.Shape, visible,
            carrierXZ: new Vector2(5f, 0f),
            control: BallControlState.Loose,
            ballPosition: new Vector3(4.6f, 0.11f, 0f),
            ballVelocity: new Vector3(4f, 0f, 0f),
            lastTouchTeam: TeamId.Away, lastTouchPlayer: CarrierId);

        PressEvidence evidence = PressPolicy.BuildSituation(observation, Pitch).Evidence;

        Assert.True(evidence.PoorTouch01 > 0.5f, $"the touch reads {evidence.PoorTouch01:F2}");
        Assert.True(PressPolicy.EvidenceStrength(evidence) > 0.15f);
    }

    /// <summary>
    /// A carrier running back toward his own goal is the observable form of "rear-facing" (an opponent's
    /// body orientation is not in the perception picture yet), and it raises the evidence.
    /// </summary>
    [Fact]
    public void ACarrierRunningBackwards_RaisesTheRearFacingEvidence()
    {
        var visible = new List<PerceivedEntity>
        {
            Opponent(CarrierId, TacticalRole.CentralMidfielder, new Vector2(-20f, 0f), new Vector2(4f, 0f))
        };

        FootballerObservation observation = Observe(
            new Vector2(-18f, 2f), TacticalRole.CenterBack, TacticalJob.Shape, visible,
            carrierXZ: new Vector2(-20f, 0f));

        PressEvidence evidence = PressPolicy.BuildSituation(observation, Pitch).Evidence;
        Assert.True(evidence.RearFacing01 > 0.5f, $"running backwards reads {evidence.RearFacing01:F2}");
    }

    /// <summary>
    /// The evidence is <b>moderated by cover</b>: the same moment argues for pressing more when somebody is
    /// behind the press than when the defender would be committing alone.
    /// </summary>
    [Fact]
    public void TheEvidenceIsModeratedByCover()
    {
        var visible = new List<PerceivedEntity>
        {
            Opponent(CarrierId, TacticalRole.CentralMidfielder, new Vector2(-30f, 0f), new Vector2(3f, 0f)),
            Mate(PresserId, TacticalRole.DefensiveMidfielder, new Vector2(-31f, 0f))
        };

        PressSituation uncovered = PressPolicy.BuildSituation(Observe(
            new Vector2(-27f, 4f), TacticalRole.CenterBack, TacticalJob.DefensiveLine, visible,
            carrierXZ: new Vector2(-30f, 0f), jobAnchor: new Vector3(-27f, 0f, 4f)), Pitch);

        visible.Add(Mate(CoverMateId, TacticalRole.CenterBack, new Vector2(-38f, 2f)));
        PressSituation covered = PressPolicy.BuildSituation(Observe(
            new Vector2(-27f, 4f), TacticalRole.CenterBack, TacticalJob.DefensiveLine, visible,
            carrierXZ: new Vector2(-30f, 0f), jobAnchor: new Vector3(-27f, 0f, 4f)), Pitch);

        Assert.False(uncovered.HasCoverBehind);
        Assert.True(covered.HasCoverBehind);
        Assert.True(covered.Urgency01 > uncovered.Urgency01 + 0.02f,
            $"cover must moderate urgency ({covered.Urgency01:F2} covered vs {uncovered.Urgency01:F2} alone)");
        Assert.True(uncovered.Urgency01 < 1f && covered.Urgency01 > 0f);
    }

    // ---------------------------------------------------------------------------------------------
    // The boundary and the wiring.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The knowledge boundary, structurally: the policy reads the carrier's <b>believed</b> position, so a
    /// controlled ball whose carrier is in nobody's picture produces no press geometry at all. The control
    /// state is a ball fact every player knows; the man's position is not.
    /// </summary>
    [Fact]
    public void AnUnbelievedCarrier_ProducesNoPressGeometry()
    {
        FootballerObservation observation = Observe(
            new Vector2(-28f, 4f), TacticalRole.CenterBack, TacticalJob.Shape,
            new List<PerceivedEntity>(),
            carrierXZ: Vector2.Zero);

        PressSituation situation = PressPolicy.BuildSituation(observation, Pitch);

        Assert.True(observation.BallControlState == BallControlState.Controlled);
        Assert.Equal(CarrierId, observation.BallControllingPlayerId);
        Assert.False(situation.HasCarrier);
        Assert.False(situation.IsSupport);
        Assert.False(situation.IsCover);

        Span<PressCandidate> buffer = stackalloc PressCandidate[PressPolicy.MaxCandidates];
        Assert.Equal(0, PressPolicy.Build(observation, situation, Pitch, buffer));
    }

    /// <summary>
    /// Degenerate inputs stay finite and on the pitch: a carrier on the byline, a receiver across the
    /// diagonal, no team-mates. Nothing here may produce NaN, an infinite score or a point outside play.
    /// </summary>
    [Fact]
    public void TheGeometryStaysFiniteAndOnThePitch()
    {
        Vector2 carrierXZ = new(Pitch.HalfLength - 1f, Pitch.HalfWidth - 1f);
        var visible = new List<PerceivedEntity>
        {
            Opponent(CarrierId, TacticalRole.Winger, carrierXZ),
            Opponent(21, TacticalRole.Striker, new Vector2(-Pitch.HalfLength + 1f, -Pitch.HalfWidth + 1f))
        };

        FootballerObservation observation = Observe(
            new Vector2(-Pitch.HalfLength + 2f, -Pitch.HalfWidth + 2f),
            TacticalRole.CenterBack, TacticalJob.CoverDefender, visible, carrierXZ: carrierXZ);

        PressSituation situation = PressPolicy.BuildSituation(observation, Pitch);
        Assert.True(situation.HasCarrier);
        Assert.Equal(CarrierId, situation.CarrierId);

        Vector2 press = PressPolicy.PressTarget(
            observation, situation, Pitch, PressPolicy.PressStandoffMeters, support: false);
        Assert.True(float.IsFinite(press.X) && float.IsFinite(press.Y));
        Assert.InRange(press.X, -Pitch.HalfLength, Pitch.HalfLength);
        Assert.InRange(press.Y, -Pitch.HalfWidth, Pitch.HalfWidth);

        Vector2 cover = PressPolicy.CoverPoint(observation, situation, Pitch);
        Assert.True(float.IsFinite(cover.X) && float.IsFinite(cover.Y));
        Assert.InRange(cover.X, -Pitch.HalfLength, Pitch.HalfLength);
        Assert.InRange(cover.Y, -Pitch.HalfWidth, Pitch.HalfWidth);

        float shadow = PressPolicy.CoverShadow01(
            cover, situation, visible, TeamId.Home, observation.AttackingDirectionX, Pitch);
        Assert.InRange(shadow, 0f, 1f);
        Assert.InRange(situation.Urgency01, 0f, 1f);
        Assert.InRange(situation.BypassRisk01, 0f, 1f);
        Assert.InRange(PressPolicy.EvidenceStrength(situation.Evidence), 0f, 1f);
    }

    /// <summary>
    /// The team's presser id reaches the observation (A6.1's one shared team fact), and a player who is told
    /// he is not the presser does not build a press candidate of his own.
    /// </summary>
    [Fact]
    public void TheResponderId_ReachesTheObservation()
    {
        var self = new FootballSim.Locomotion.FootballerState
        {
            Id = SelfId, Team = TeamId.Home, ShirtNumber = 4,
            Role = TacticalRole.CenterBack,
            Attributes = new PlayerAttributes(),
            Tendencies = PlayerTendencyProfile.ForRole(TacticalRole.CenterBack)
        };
        self.Position = new Vector3(-30f, 0f, 0f);
        var mate = new FootballSim.Locomotion.FootballerState
        {
            Id = PresserId, Team = TeamId.Home, ShirtNumber = 6,
            Role = TacticalRole.DefensiveMidfielder,
            Attributes = new PlayerAttributes(),
            Tendencies = PlayerTendencyProfile.ForRole(TacticalRole.DefensiveMidfielder)
        };
        mate.Position = new Vector3(-28f, 0f, 2f);
        var carrier = new FootballSim.Locomotion.FootballerState
        {
            Id = CarrierId, Team = TeamId.Away, ShirtNumber = 9,
            Role = TacticalRole.Striker,
            Attributes = new PlayerAttributes(),
            Tendencies = PlayerTendencyProfile.ForRole(TacticalRole.Striker)
        };
        carrier.Position = new Vector3(-24f, 0f, 0f);

        var ball = new FootballSim.Physics.BallState
        {
            Position = new Vector3(-24f, 0.11f, 0f),
            LastTouchedByTeam = TeamId.Away,
            LastTouchedByPlayerId = CarrierId
        };

        FootballerObservation observation = new PerceptionSystem().BuildObservation(
            self, new[] { self, mate, carrier }, ball, TeamId.Away, MatchPhase.FirstHalf,
            matchTimeSeconds: 10f, attackingDirX: 1f, isSetPieceTaker: false,
            teamProfile: TeamTacticalProfile.PossessionControl, rng: new SeededRandom(7),
            possession: new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Away, CarrierId, null),
            isDesignatedBallResponder: false,
            ballResponderPlayerId: PresserId);

        Assert.Equal(PresserId, observation.BallResponderPlayerId);

        PressSituation situation = PressPolicy.BuildSituation(observation, Pitch);
        Assert.Equal(PresserId, situation.PresserId);
        Assert.False(situation.IsPresser);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    private static PerceivedEntity Mate(int id, TacticalRole role, Vector2 position, Vector2 velocity = default) =>
        new(id, TeamId.Home, role, new Vector3(position.X, 0f, position.Y),
            new Vector3(velocity.X, 0f, velocity.Y), 1f);

    private static PerceivedEntity Opponent(
        int id, TacticalRole role, Vector2 position, Vector2 velocity = default) =>
        new(id, TeamId.Away, role, new Vector3(position.X, 0f, position.Y),
            new Vector3(velocity.X, 0f, velocity.Y), 1f);

    private static FootballerObservation Observe(
        Vector2 selfXZ,
        TacticalRole role,
        TacticalJob job,
        IReadOnlyList<PerceivedEntity> visible,
        Vector2 carrierXZ,
        BallControlState control = BallControlState.Controlled,
        Vector3? ballPosition = null,
        Vector3? ballVelocity = null,
        TeamId? lastTouchTeam = null,
        int? lastTouchPlayer = null,
        Vector3? jobAnchor = null,
        int? jobTargetPlayerId = null,
        float attackDirX = 1f,
        bool isResponder = false)
    {
        bool controlled = control == BallControlState.Controlled;
        TeamId? touchingTeam = lastTouchTeam ?? TeamId.Away;
        int? touchingPlayer = lastTouchPlayer ?? CarrierId;

        return new FootballerObservation
        {
            SelfId = SelfId,
            SelfTeam = TeamId.Home,
            SelfRole = role,
            SelfPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(attackDirX, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 0f,
            PerceivedBallPosition = ballPosition ?? new Vector3(carrierXZ.X - 0.3f, 0f, carrierXZ.Y),
            PerceivedBallVelocity = ballVelocity ?? Vector3.Zero,
            PossessingTeam = controlled ? TeamId.Away : touchingTeam,
            BallLastTouchedByPlayerId = touchingPlayer,
            BallLastTouchedByTeam = touchingTeam,
            BallControlState = control,
            BallControllingPlayerId = controlled ? CarrierId : null,
            TacticalAnchorPosition = jobAnchor ?? new Vector3(selfXZ.X, 0f, selfXZ.Y),
            TacticalJob = job,
            TacticalJobAnchor = jobAnchor,
            JobTargetPlayerId = jobTargetPlayerId,
            AttackingDirectionX = attackDirX,
            MatchTimeSeconds = 30f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = 900,
            IsDesignatedBallResponder = isResponder ? true : false,
            BallResponderPlayerId = PresserId,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = PlayerTendencyProfile.ForRole(role),
            SelfAttributes = new PlayerAttributes()
        };
    }
}
