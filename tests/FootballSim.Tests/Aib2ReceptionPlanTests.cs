using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// AUD-P4 — a reception plan has to meet the ball, not stand beside its path.
///
/// The audit (<c>docs/AUDIT_PASSING_SPACE_2026-11-23.md</c>, AUD-P4, Capture 3) found the planner parking
/// the planned meeting point 1.1 m (<see cref="ReceptionPlanner.OpenBodyOffsetMeters"/>) / 1.3 m
/// (<see cref="ReceptionPlanner.EscapeOffsetMeters"/>) to the side of the pass trajectory. A receiver's
/// total planar reach in execution is <c>0.75 × 1.65 = 1.24 m</c>
/// (<see cref="BallContactRules.ReachRadius"/> with the receiving/dribbling multiplier), and the receiving
/// candidate's approach command <i>stops steering</i> to the meeting point inside its own deadband, so the
/// old offsets parked the body outside its own trap circle: a firm pass rolled straight past and out of
/// bounds.
///
/// The fix reduces the offsets (0.40 m / 0.50 m) and makes the invariant explicit:
/// <see cref="ReceptionPlanner.MaxTrajectoryOffsetMeters"/> bounds how far a produced body-offset plan may
/// put <see cref="ReceptionPlan.MeetingPointXZ"/> from the ball's predicted trajectory sample at its own
/// <see cref="ReceptionPlan.ArrivalSeconds"/>. Every fixture below re-derives that sample from the
/// observation alone, through the same <see cref="BallTrajectoryEstimate"/> the planner uses, so the
/// assertion is about the physics and not about a hard-coded coordinate. Each regression comment states
/// the pre-fix arithmetic that makes the assertion fail on the old code.
/// </summary>
public sealed class Aib2ReceptionPlanTests
{
    private const int ReceiverId = 106;
    private const int ForwardMateId = 107;
    private const int PresserId = 205;

    private static readonly PitchDimensions Pitch = new();

    // The fixture's ball: a 5 m/s roller 6 m in front of the receiver. The receiver meets it early — the
    // first reachable trajectory sample is ~2.85 m away at ~0.73 s — which is exactly the audit's shape:
    // a firm pass arriving at pace, not a ball dying at the receiver's feet.
    private static readonly Vector2 RollerPosition = new(6f, 0f);
    private static readonly Vector2 RollerVelocity = new(-5f, 0f);

    // A presser standing between the ball and the meeting point, so the pressure direction points back
    // toward the receiver and the escape touch is a real body offset rather than a free run.
    private static readonly Vector2 PresserPosition = new(5f, 0f);

    // =============================================================================================
    // AUD-P4 — the body-offset meeting point stays on the trajectory tube
    // =============================================================================================

    /// <summary>
    /// With a presser present (so both body-offset touches are produced and feasible), every
    /// <see cref="ReceptionTouchKind.OpenBody"/> / <see cref="ReceptionTouchKind.EscapePressure"/> plan must
    /// meet the ball within <see cref="ReceptionPlanner.MaxTrajectoryOffsetMeters"/> of the trajectory
    /// sample at its own arrival time.
    ///
    /// Pre-fix arithmetic: the planner offset the touch point by 1.1 m (OpenBody) and 1.3 m
    /// (EscapePressure) from that sample, so this test read <b>1.100 m / 1.300 m</b> against a 0.55 m bound
    /// and failed. Adding the candidate's 0.45 m approach deadband, the body could finish up to 1.55 m /
    /// 1.75 m from the path — past the 1.24 m receiving reach, which is the Capture 3 miss. With the fix the
    /// offsets are 0.40 m / 0.50 m, inside the bound and well inside the reach.
    /// </summary>
    [Fact]
    public void AIB2_Reception_BodyOffsetTouchesStayOnTheBallTrajectory()
    {
        FootballerObservation obs = Receiving(
            RollerPosition,
            RollerVelocity,
            visible: [Mate(ForwardMateId, 14f, 5f), Opponent(PresserId, PresserPosition.X, PresserPosition.Y)]);

        var planner = new ReceptionPlanner(Pitch);
        Assert.True(planner.TryPlan(obs, out _), "the fixture must produce a reception plan");

        bool sawOpenBody = false;
        bool sawEscape = false;
        for (int i = 0; i < planner.OptionCount; i++)
        {
            ReceptionOption option = planner.GetOption(i);
            if (!option.Feasible) continue;
            if (option.TouchKind is not (ReceptionTouchKind.OpenBody or ReceptionTouchKind.EscapePressure))
                continue;

            sawOpenBody |= option.TouchKind == ReceptionTouchKind.OpenBody;
            sawEscape |= option.TouchKind == ReceptionTouchKind.EscapePressure;

            ReceptionPlan plan = planner.PlanForOption(i);
            Vector2 sampleXZ = TrajectorySampleAt(obs, plan.ArrivalSeconds);
            float deviation = Vector2.Distance(plan.MeetingPointXZ, sampleXZ);

            // Pre-fix this is 1.100 m for OpenBody and 1.300 m for EscapePressure — both fail the bound.
            Assert.True(deviation <= ReceptionPlanner.MaxTrajectoryOffsetMeters + 1e-3f,
                $"{option.TouchKind} planned ({plan.MeetingPointXZ.X:F2},{plan.MeetingPointXZ.Y:F2}), " +
                $"{deviation:F3} m from the ball's trajectory sample at {plan.ArrivalSeconds:F2}s " +
                $"(bound {ReceptionPlanner.MaxTrajectoryOffsetMeters:F2} m); pre-fix the offsets were " +
                "1.1 m / 1.3 m");
        }

        Assert.True(sawOpenBody,
            "the presser fixture must still produce a feasible OpenBody plan: " + OptionsText(planner));
        Assert.True(sawEscape,
            "the presser fixture must still produce a feasible EscapePressure plan: " + OptionsText(planner));
    }

    /// <summary>
    /// The same invariant without a presser: no produced plan may escape the trajectory tube. The fixture
    /// stands the receiver just in front of a 5 m/s roller, so every body-offset touch is comfortably
    /// reachable and the test is about the bound, not about feasibility.
    ///
    /// <see cref="ReceptionTouchKind.StrideTouch"/> is deliberately excluded: its meeting point is a forward
    /// touch on the ball's own path (<see cref="ReceptionPlanner.StrideLeadMeters"/> = 0.8 m ahead of the
    /// sample), not a body offset to the side of it. It is covered by the reach assertion in
    /// <see cref="AIB2_Reception_OffsetConstantsStayInsideTheExecutionReach"/>.
    ///
    /// Pre-fix arithmetic: OpenBody was 1.1 m from the sample and EscapePressure 1.3 m, so both fail the
    /// 0.55 m bound (and the escape option was refused as unreachable before it could even be checked —
    /// the pre-fix offset was itself outside the time budget). With the fix Kill, Shield, OpenBody and
    /// EscapePressure are all produced and all inside the bound.
    /// </summary>
    [Fact]
    public void AIB2_Reception_NoBodyOffsetOptionEscapesTheTrajectoryTube()
    {
        FootballerObservation obs = Receiving(
            RollerPosition,
            RollerVelocity,
            visible: [Mate(ForwardMateId, 14f, 5f)],
            selfPosition: new Vector2(5f, 0f));

        var planner = new ReceptionPlanner(Pitch);
        Assert.True(planner.TryPlan(obs, out _), "the fixture must produce a reception plan");

        int bounded = 0;
        for (int i = 0; i < planner.OptionCount; i++)
        {
            ReceptionOption option = planner.GetOption(i);
            if (!option.Feasible) continue;
            if (option.TouchKind == ReceptionTouchKind.StrideTouch) continue;

            ReceptionPlan plan = planner.PlanForOption(i);
            Vector2 sampleXZ = TrajectorySampleAt(obs, plan.ArrivalSeconds);
            float deviation = Vector2.Distance(plan.MeetingPointXZ, sampleXZ);

            Assert.True(deviation <= ReceptionPlanner.MaxTrajectoryOffsetMeters + 1e-3f,
                $"{option.TouchKind} planned ({plan.MeetingPointXZ.X:F2},{plan.MeetingPointXZ.Y:F2}), " +
                $"{deviation:F3} m from the ball's trajectory sample at {plan.ArrivalSeconds:F2}s " +
                $"(bound {ReceptionPlanner.MaxTrajectoryOffsetMeters:F2} m)");
            bounded++;
        }

        Assert.True(bounded >= 3,
            "the fixture must produce at least kill, open-body and shield inside the tube: " +
            OptionsText(planner));
    }

    /// <summary>
    /// Kill and Shield are not body offsets — they meet the ball exactly where the predicted path passes —
    /// so the fix must leave them on the sample. This passes on the pre-fix code too: it is the guard that
    /// the new clamp did not start moving the touches that were already correct.
    /// </summary>
    [Fact]
    public void AIB2_Reception_KillAndShieldMeetTheBallExactly()
    {
        FootballerObservation obs = Receiving(
            RollerPosition,
            RollerVelocity,
            visible: [Mate(ForwardMateId, 14f, 5f), Opponent(PresserId, PresserPosition.X, PresserPosition.Y)]);

        var planner = new ReceptionPlanner(Pitch);
        Assert.True(planner.TryPlan(obs, out _), "the fixture must produce a reception plan");

        int checkedKinds = 0;
        for (int i = 0; i < planner.OptionCount; i++)
        {
            ReceptionOption option = planner.GetOption(i);
            if (!option.Feasible) continue;
            if (option.TouchKind is not (ReceptionTouchKind.Kill or ReceptionTouchKind.Shield)) continue;

            ReceptionPlan plan = planner.PlanForOption(i);
            Vector2 sampleXZ = TrajectorySampleAt(obs, plan.ArrivalSeconds);

            // Millimetre tolerance: the sample is re-interpolated from a second trajectory build and may
            // differ in the last float bit, but the planned point is the sample itself, not an offset.
            Assert.Equal(sampleXZ.X, plan.MeetingPointXZ.X, 3);
            Assert.Equal(sampleXZ.Y, plan.MeetingPointXZ.Y, 3);
            checkedKinds++;
        }

        Assert.Equal(2, checkedKinds);
    }

    /// <summary>
    /// The fix must reduce the offsets, not delete the options: a pressed reception still needs the
    /// half-body open touch and the escape touch, and both must still be feasible (the test asserts the
    /// set, not a score or a winner). This is the guard against "fixing" the miss by removing the option
    /// that caused it.
    /// </summary>
    [Fact]
    public void AIB2_Reception_TheFixKeepsTheBodyOffsetOptionsFeasible()
    {
        FootballerObservation obs = Receiving(
            RollerPosition,
            RollerVelocity,
            visible: [Mate(ForwardMateId, 14f, 5f), Opponent(PresserId, PresserPosition.X, PresserPosition.Y)]);

        var planner = new ReceptionPlanner(Pitch);
        Assert.True(planner.TryPlan(obs, out _), "the fixture must produce a reception plan");
        Assert.Equal(ReceptionPlanner.MaxOptions, planner.OptionCount);

        ReceptionOption openBody = OptionOf(planner, ReceptionTouchKind.OpenBody);
        ReceptionOption escape = OptionOf(planner, ReceptionTouchKind.EscapePressure);

        Assert.True(openBody.Feasible, OptionsText(planner));
        Assert.True(escape.Feasible, OptionsText(planner));
        Assert.True(planner.PlanForOption(IndexOf(planner, ReceptionTouchKind.OpenBody)).IsPlanned);
        Assert.True(planner.PlanForOption(IndexOf(planner, ReceptionTouchKind.EscapePressure)).IsPlanned);
    }

    // =============================================================================================
    // The constants — later tuning cannot silently re-break the physics
    // =============================================================================================

    /// <summary>
    /// The static arithmetic AUD-P4 rests on. Execution's planar receiving reach is
    /// <c>0.75 × 1.65 = 1.2375 m</c> (<see cref="BallContactRules.ReachRadius"/>); the candidate's approach
    /// command parks the body inside ~0.15 m of the planned point. Both body offsets must stay inside the
    /// trajectory bound, and the bound must stay inside the reach, so the plan can always be touched.
    ///
    /// Pre-fix: <c>OpenBodyOffsetMeters = 1.1</c> was already off the 0.55 m tube and
    /// <c>EscapeOffsetMeters = 1.3</c> was outside the 1.2375 m reach outright, so the last two assertions
    /// fail on the old constants. The stride assertion is a forward-touch guard: the lead is not a body
    /// offset, but a touch point one lead ahead of the trajectory sample must still be within reach of the
    /// ball the sample carries.
    /// </summary>
    [Fact]
    public void AIB2_Reception_OffsetConstantsStayInsideTheExecutionReach()
    {
        float baseReach = new PlayerAttributes().ReachRadius;
        float receivingReach = BallContactRules.ReachRadius(
            baseReach,
            keeperHandling: false,
            isDiving: false,
            requestingTackle: false,
            receivingOrDribbling: true);

        // The audit's arithmetic: the receiving/dribbling reach multiplier is 1.65, i.e. 1.2375 m here.
        Assert.Equal(baseReach * 1.65f, receivingReach, 4);

        Assert.True(ReceptionPlanner.OpenBodyOffsetMeters <= ReceptionPlanner.MaxTrajectoryOffsetMeters,
            $"OpenBodyOffsetMeters {ReceptionPlanner.OpenBodyOffsetMeters:F2} m is outside the trajectory " +
            $"bound {ReceptionPlanner.MaxTrajectoryOffsetMeters:F2} m (pre-fix it was 1.1 m)");
        Assert.True(ReceptionPlanner.EscapeOffsetMeters <= ReceptionPlanner.MaxTrajectoryOffsetMeters,
            $"EscapeOffsetMeters {ReceptionPlanner.EscapeOffsetMeters:F2} m is outside the trajectory " +
            $"bound {ReceptionPlanner.MaxTrajectoryOffsetMeters:F2} m (pre-fix it was 1.3 m)");
        Assert.True(ReceptionPlanner.MaxTrajectoryOffsetMeters < receivingReach,
            $"the trajectory bound {ReceptionPlanner.MaxTrajectoryOffsetMeters:F2} m must stay inside the " +
            $"execution receiving reach {receivingReach:F4} m (pre-fix the escape offset 1.3 m was already " +
            "outside it)");
        Assert.True(ReceptionPlanner.StrideLeadMeters < receivingReach,
            $"the stride lead {ReceptionPlanner.StrideLeadMeters:F2} m must stay inside the execution " +
            $"receiving reach {receivingReach:F4} m: the body at the stride point is that far from the " +
            "trajectory sample the ball passes through");
    }

    // =============================================================================================
    // Helpers: one receiver picture and the ball's predicted path, stated explicitly.
    // =============================================================================================

    /// <summary>
    /// The ball's predicted ground position when the plan says it can be met, rebuilt from the observation
    /// exactly as <see cref="ReceptionPlanner.TryPlan"/> rebuilds it (same estimate, same horizon). This is
    /// the fixture's independent statement of "the ball's trajectory sample at ArrivalSeconds".
    /// </summary>
    private static Vector2 TrajectorySampleAt(FootballerObservation obs, float seconds)
    {
        var trajectory = new BallTrajectoryEstimate(pitch: Pitch);
        Assert.True(
            trajectory.TryBuild(obs.PerceivedBallPosition, obs.PerceivedBallVelocity,
                ReceptionPlanner.PlanHorizonSeconds),
            "the fixture's flight must be predictable");

        Vector3 sample = trajectory.PositionAtTime(seconds);
        return new Vector2(sample.X, sample.Z);
    }

    private static FootballerObservation Receiving(
        Vector2 ballPosition,
        Vector2 ballVelocity,
        IReadOnlyList<PerceivedEntity>? visible = null,
        Vector2? selfPosition = null,
        int knowledgeAgeTicks = 0)
    {
        Vector2 self = selfPosition ?? Vector2.Zero;
        return new FootballerObservation
        {
            SelfId = ReceiverId,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.DefensiveMidfielder,
            SelfPosition = new Vector3(self.X, 0f, self.Y),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Scan = ScanStatus.None,
            Calls = default,
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(ballPosition.X, 0.11f, ballPosition.Y),
            PerceivedBallVelocity = new Vector3(ballVelocity.X, 0f, ballVelocity.Y),
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = 102,
            BallLastTouchedByTeam = TeamId.Home,
            BallControlState = BallControlState.InFlightPass,
            BallControllingPlayerId = null,
            IntendedReceiverId = ReceiverId,
            TacticalAnchorPosition = new Vector3(self.X, 0f, self.Y),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 15f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = 700,
            KnowledgeAgeTicks = knowledgeAgeTicks,
            VisiblePlayers = visible ?? [Mate(ForwardMateId, 14f, 5f)],
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.DefensiveMidfielder),
            SelfAttributes = new PlayerAttributes()
        };
    }

    private static PerceivedEntity Mate(int id, float x, float z) =>
        new(id, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(x, 0f, z), Vector3.Zero, 1f)
        {
            Capability = CapabilityEstimate.Generic
        };

    private static PerceivedEntity Opponent(int id, float x, float z) =>
        new(id, TeamId.Away, TacticalRole.DefensiveMidfielder, new Vector3(x, 0f, z), Vector3.Zero, 1f)
        {
            Capability = CapabilityEstimate.Generic
        };

    private static ReceptionOption OptionOf(ReceptionPlanner planner, ReceptionTouchKind kind) =>
        planner.GetOption(IndexOf(planner, kind));

    private static int IndexOf(ReceptionPlanner planner, ReceptionTouchKind kind)
    {
        for (int i = 0; i < planner.OptionCount; i++)
        {
            if (planner.GetOption(i).TouchKind == kind) return i;
        }

        throw new Xunit.Sdk.XunitException($"the planner evaluated no {kind} option");
    }

    private static string OptionsText(ReceptionPlanner planner)
    {
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < planner.OptionCount; i++)
        {
            if (i > 0) text.Append("; ");
            ReceptionOption option = planner.GetOption(i);
            text.Append(option.Describe())
                .Append(" @(").Append(option.MeetingPointXZ.X.ToString("F2")).Append(',')
                .Append(option.MeetingPointXZ.Y.ToString("F2")).Append(')');
        }

        return text.ToString();
    }
}
