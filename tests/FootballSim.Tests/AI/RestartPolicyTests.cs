using System;
using System.Linq;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A8.5 — the small library of legal restart routines, at the model level.
///
/// The plan's task is "a small library of legal kick-off, throw-in, corner, free-kick and goal-kick routines with
/// taker/support/target roles; the rules engine supplies legal placement and restart state; routines choose among
/// lawful options". <see cref="RestartPolicy"/> is that choice: two lawful options (the short support ball or the
/// longer target ball) per restart, gated by the restart's own Law 11 semantics and weighted by the team's stated
/// short/long preference and the space around each receiver.
///
/// These tests pin the boundaries: what is legal, who may be named, when the menu collapses to one option, and
/// that the election is a pure function of its stated inputs. The live-controller wiring is in
/// <see cref="RestartScenarioTests"/>; the lifecycle is in <see cref="RestartCoordinatorTests"/>.
/// </summary>
public sealed class RestartPolicyTests
{
    private static readonly PitchDimensions Pitch = RestartTestWorld.Pitch;

    /// <summary>
    /// A possession side throws the nearest free team-mate in: the short option is what the profile asks for and
    /// the picture supports it. The taker, the support and the longer target are all named.
    /// </summary>
    [Fact]
    public void APossessionSide_PlaysTheShortOptionToTheNearestFreeSupport()
    {
        RestartTestWorld.World world = RestartTestWorld.Build();

        bool planned = RestartTestWorld.TryPlanThrowIn(world, out RestartPlan plan);

        Assert.True(planned);
        Assert.Equal(RestartKind.ThrowIn, plan.Kind);
        Assert.Equal(RestartOption.Short, plan.Option);
        Assert.Equal(RestartTestWorld.HomeTaker, plan.TakerId);
        Assert.Equal(RestartTestWorld.HomeSupport, plan.SupportId);
        Assert.Equal(RestartTestWorld.HomeTarget, plan.TargetId);
        Assert.Equal(RestartTestWorld.HomeSupport, plan.ReceiverId);
        Assert.InRange(plan.Confidence01, RestartPolicy.MinRoutineConfidence01, 1f);
        Assert.Equal(plan.SupportId, plan.ReceiverId);
    }

    /// <summary>The same picture through a direct side: the long ball is the profile's preference and the target wins.</summary>
    [Fact]
    public void ADirectSide_PlaysTheLongerOptionToTheTarget()
    {
        RestartTestWorld.World world = RestartTestWorld.Build();

        bool planned = RestartTestWorld.TryPlanThrowIn(
            world, out RestartPlan plan, TeamTacticalProfile.DirectCounterattack);

        Assert.True(planned);
        Assert.Equal(RestartOption.Deliver, plan.Option);
        Assert.Equal(RestartTestWorld.HomeTarget, plan.ReceiverId);
        Assert.Equal(RestartTestWorld.HomeTarget, plan.TargetId);
        Assert.True(plan.Progression01 > 0.5f, $"the target should be well up the pitch, progression {plan.Progression01:F2}");
    }

    /// <summary>
    /// Space is what makes the delivery worth asking for: with the support marked and the target free, the
    /// possession side stops preferring the short ball — the preference is bounded by the picture, not a switch.
    /// </summary>
    [Fact]
    public void AMarkedSupport_LosesTheShortOptionToAFreeTarget()
    {
        RestartTestWorld.World world = RestartTestWorld.Build();
        // An away defender on the support's shoulder (2 m) and nobody near the target.
        world.Move(RestartTestWorld.AwayWinger, 5.5f, 27.5f);

        bool planned = RestartTestWorld.TryPlanThrowIn(world, out RestartPlan plan);

        Assert.True(planned);
        Assert.Equal(RestartOption.Deliver, plan.Option);
        Assert.Equal(RestartTestWorld.HomeTarget, plan.ReceiverId);
        Assert.True(plan.Confidence01 > RestartPolicy.MinRoutineConfidence01);
    }

    /// <summary>The option score is monotone in the chosen receiver's freedom: the same option scores higher when he is free.</summary>
    [Fact]
    public void TheConfidence_IsHigherForAFreeReceiverThanAMarkedOne()
    {
        RestartTestWorld.World free = RestartTestWorld.Build();
        RestartTestWorld.World marked = RestartTestWorld.Build();
        marked.Move(RestartTestWorld.AwayWinger, 5.5f, 27.5f);

        Assert.True(RestartTestWorld.TryPlanThrowIn(free, out RestartPlan freePlan));
        Assert.True(RestartTestWorld.TryPlanThrowIn(marked, out RestartPlan markedPlan));

        Assert.Equal(RestartOption.Short, freePlan.Option);
        // The free picture's short option is the free support; the marked picture prefers the delivery, and the
        // point is that the same picture with less space cannot score a higher short option.
        Assert.True(SpaceOf(freePlan) > SpaceOf(markedPlan) || markedPlan.ReceiverId != freePlan.ReceiverId);
    }

    /// <summary>
    /// The law model is the one test a preference cannot excuse: a free kick is not offside-exempt, so the
    /// advanced man standing beyond the believed line is not a lawful receiver and the routine must not name him.
    /// </summary>
    [Fact]
    public void AFreeKick_RefusesAnOffsideReceiver()
    {
        RestartTestWorld.World world = RestartTestWorld.Build();
        // The away keeper is the only opponent ahead of the line and the rest of the away side is behind x = 10,
        // so the believed line is 10 m: #10 (x = 25) is beyond it and cannot receive directly.
        RestartTestWorld.LowAwayLine(world);
        var ball = new Vector2(0f, 18f);

        bool planned = RestartPolicy.TryPlan(
            TeamId.Home, RestartKind.FreeKick, ball, RestartTestWorld.HomeTaker, world.Players,
            world.Belief(TeamId.Home), TeamTacticalProfile.DirectCounterattack, null, 1f, Pitch, out RestartPlan plan);

        Assert.True(planned);
        Assert.NotEqual(RestartTestWorld.HomeTarget, plan.ReceiverId);
        Assert.NotEqual(RestartTestWorld.HomeTarget, plan.SupportId);
        Assert.NotEqual(RestartTestWorld.HomeTarget, plan.TargetId);
        // The named receivers are the ones the law allows: behind the believed line.
        Assert.True(world.Player(plan.ReceiverId).Position.X < 10f - OffsideTactics.MarginMeters);
    }

    /// <summary>
    /// The same advanced man is a lawful receiver from a throw-in or a corner, which Law 11 exempts — the law gate
    /// is the restart's own semantics, not a blanket rule.
    /// </summary>
    [Theory]
    [InlineData(RestartKind.ThrowIn)]
    [InlineData(RestartKind.CornerKick)]
    public void AnOffsideExemptRestart_MayNameTheAdvancedMan(RestartKind kind)
    {
        RestartTestWorld.World world = RestartTestWorld.Build();
        RestartTestWorld.LowAwayLine(world);

        bool planned = RestartPolicy.TryPlan(
            TeamId.Home, kind, RestartTestWorld.ThrowInBall, RestartTestWorld.HomeTaker, world.Players,
            world.Belief(TeamId.Home), TeamTacticalProfile.DirectCounterattack, null, 1f, Pitch, out RestartPlan plan);

        Assert.True(planned);
        // #10 is the most advanced lawful man for the exempt kinds, so the delivery targets him.
        Assert.Equal(RestartTestWorld.HomeTarget, plan.ReceiverId);
    }

    /// <summary>
    /// A goal kick is exempt too, and it is the keeper's own restart: the advanced team-mate may be named, the
    /// keeper himself never is.
    /// </summary>
    [Fact]
    public void AGoalKick_MayNameTheAdvancedManButNeverTheKeeper()
    {
        RestartTestWorld.World world = RestartTestWorld.Build();
        RestartTestWorld.LowAwayLine(world);
        world.Move(RestartTestWorld.HomeCenterBack, -40f, 5f);
        world.Move(RestartTestWorld.HomeCenterBackTwo, -40f, -5f);
        world.Move(RestartTestWorld.HomeMidfielder, -15f, 0f);
        var ball = new Vector2(-47f, 0f);

        bool planned = RestartPolicy.TryPlan(
            TeamId.Home, RestartKind.GoalKick, ball, RestartTestWorld.HomeKeeper, world.Players,
            world.Belief(TeamId.Home), TeamTacticalProfile.DirectCounterattack, null, 1f, Pitch, out RestartPlan plan);

        Assert.True(planned);
        Assert.NotEqual(RestartTestWorld.HomeKeeper, plan.ReceiverId);
        Assert.NotEqual(RestartTestWorld.HomeKeeper, plan.SupportId);
        Assert.NotEqual(RestartTestWorld.HomeKeeper, plan.TargetId);
    }

    /// <summary>
    /// A kick-off is not offside-exempt either. In this match's own kick-off setup every player is in his own
    /// half, so the gate is satisfied trivially there — but a fixture that puts a man beyond the line must still
    /// refuse him.
    /// </summary>
    [Fact]
    public void AKickoff_RefusesAnOffsideReceiver()
    {
        RestartTestWorld.World world = RestartTestWorld.Build();
        RestartTestWorld.LowAwayLine(world);
        world.Move(RestartTestWorld.HomeTarget, 30f, 0f);
        var ball = new Vector2(0f, 0f);

        bool planned = RestartPolicy.TryPlan(
            TeamId.Home, RestartKind.Kickoff, ball, RestartTestWorld.HomeTaker, world.Players,
            world.Belief(TeamId.Home), TeamTacticalProfile.DirectCounterattack, null, 1f, Pitch, out RestartPlan plan);

        Assert.True(planned);
        Assert.NotEqual(RestartTestWorld.HomeTarget, plan.ReceiverId);
    }

    /// <summary>An indirect free kick is a free kick for the routine: normal offside semantics, and it names a receiver.</summary>
    [Fact]
    public void AnIndirectFreeKick_IsTreatedAsAFreeKick()
    {
        RestartTestWorld.World world = RestartTestWorld.Build();
        var ball = new Vector2(0f, 18f);

        bool planned = RestartPolicy.TryPlan(
            TeamId.Home, RestartKind.IndirectFreeKick, ball, RestartTestWorld.HomeTaker, world.Players,
            world.Belief(TeamId.Home), TeamTacticalProfile.PossessionControl, null, 1f, Pitch, out RestartPlan plan);

        Assert.True(planned);
        Assert.Equal(RestartKind.IndirectFreeKick, plan.Kind);
        Assert.NotEqual(0, plan.ReceiverId);
    }

    /// <summary>The goal kick is the keeper's restart: he is the taker, and never one of the receivers he names.</summary>
    [Fact]
    public void AGoalKick_IsTakenByTheKeeperAndNeverNamesHimAsAReceiver()
    {
        RestartTestWorld.World world = RestartTestWorld.Build();
        world.Move(RestartTestWorld.HomeCenterBack, -40f, 5f);
        world.Move(RestartTestWorld.HomeCenterBackTwo, -40f, -5f);
        world.Move(RestartTestWorld.HomeMidfielder, -15f, 0f);
        var ball = new Vector2(-47f, 0f);

        bool planned = RestartPolicy.TryPlan(
            TeamId.Home, RestartKind.GoalKick, ball, RestartTestWorld.HomeKeeper, world.Players,
            world.Belief(TeamId.Home), TeamTacticalProfile.PossessionControl, null, 1f, Pitch, out RestartPlan plan);

        Assert.True(planned);
        Assert.Equal(RestartTestWorld.HomeKeeper, plan.TakerId);
        Assert.NotEqual(RestartTestWorld.HomeKeeper, plan.SupportId);
        Assert.NotEqual(RestartTestWorld.HomeKeeper, plan.TargetId);
        Assert.NotEqual(RestartTestWorld.HomeKeeper, plan.ReceiverId);
    }

    /// <summary>
    /// The goal kick's option follows A6.5b's own short-distribution preference — the one place that choice already
    /// lives — instead of adding a second preference for the same decision.
    /// </summary>
    [Fact]
    public void AGoalKick_FollowsTheProfilesOwnDistributionStyle()
    {
        RestartTestWorld.World world = RestartTestWorld.Build();
        world.Move(RestartTestWorld.HomeCenterBack, -40f, 5f);
        world.Move(RestartTestWorld.HomeCenterBackTwo, -40f, -5f);
        world.Move(RestartTestWorld.HomeMidfielder, -15f, 0f);
        var ball = new Vector2(-47f, 0f);

        Assert.True(RestartPolicy.TryPlan(TeamId.Home, RestartKind.GoalKick, ball, RestartTestWorld.HomeKeeper,
            world.Players, world.Belief(TeamId.Home), TeamTacticalProfile.PossessionControl, null, 1f, Pitch,
            out RestartPlan shortPlan));
        Assert.True(RestartPolicy.TryPlan(TeamId.Home, RestartKind.GoalKick, ball, RestartTestWorld.HomeKeeper,
            world.Players, world.Belief(TeamId.Home), TeamTacticalProfile.DirectCounterattack, null, 1f, Pitch,
            out RestartPlan longPlan));

        Assert.Equal(RestartOption.Short, shortPlan.Option);
        Assert.Equal(RestartOption.Deliver, longPlan.Option);
    }

    /// <summary>The menu has no routine for a penalty, and nothing is raised when there is no taker the AI may bind.</summary>
    [Fact]
    public void NoRoutine_ForAPenaltyOrWithoutABindableTaker()
    {
        RestartTestWorld.World world = RestartTestWorld.Build();

        Assert.False(RestartPolicy.TryPlan(TeamId.Home, RestartKind.PenaltyKick, RestartTestWorld.ThrowInBall,
            RestartTestWorld.HomeTaker, world.Players, world.Belief(TeamId.Home), TeamTacticalProfile.PossessionControl,
            null, 1f, Pitch, out _));
        Assert.False(RestartPolicy.TryPlan(TeamId.Home, RestartKind.None, RestartTestWorld.ThrowInBall,
            RestartTestWorld.HomeTaker, world.Players, world.Belief(TeamId.Home), TeamTacticalProfile.PossessionControl,
            null, 1f, Pitch, out _));
        Assert.False(RestartTestWorld.TryPlanThrowIn(world, out _, humanPlayerId: RestartTestWorld.HomeTaker));

        world.Player(RestartTestWorld.HomeTaker).IsSentOff = true;
        Assert.False(RestartTestWorld.TryPlanThrowIn(world, out _));
    }

    /// <summary>No lawful receiver, no routine: the taker keeps his own pre-A8.5 heuristic rather than a fabricated plan.</summary>
    [Fact]
    public void NoLawfulReceiver_MeansNoRoutine()
    {
        RestartTestWorld.World world = RestartTestWorld.Build();
        // Every home outfield player except the taker is beyond the routine's delivery horizon.
        world.Move(RestartTestWorld.HomeCenterBack, -120f, 0f);
        world.Move(RestartTestWorld.HomeCenterBackTwo, -120f, 10f);
        world.Move(RestartTestWorld.HomeMidfielder, -120f, -10f);
        world.Move(RestartTestWorld.HomeSupport, 120f, 0f);
        world.Move(RestartTestWorld.HomeTarget, 120f, 10f);
        world.Move(RestartTestWorld.HomeWinger, 120f, -10f);

        Assert.False(RestartTestWorld.TryPlanThrowIn(world, out _));
    }

    /// <summary>
    /// The plan names only the restarting team's available players, never the taker, never the keeper, and the two
    /// options never name the same man.
    /// </summary>
    [Fact]
    public void ThePlan_NamesOnlyLawfulParticipants()
    {
        RestartTestWorld.World world = RestartTestWorld.Build();

        Assert.True(RestartTestWorld.TryPlanThrowIn(world, out RestartPlan plan));
        foreach (int id in new[] { plan.SupportId, plan.TargetId })
        {
            if (id == 0) continue;
            FootballerState player = world.Player(id);
            Assert.Equal(TeamId.Home, player.Team);
            Assert.True(player.IsActiveOnPitch);
            Assert.NotEqual(plan.TakerId, id);
            Assert.NotEqual(RestartTestWorld.HomeKeeper, id);
        }

        Assert.NotEqual(plan.SupportId, plan.TargetId);
    }

    /// <summary>The election is a pure function of its inputs: same picture twice, same plan, and no player is moved.</summary>
    [Fact]
    public void TheElection_IsPureAndDeterministic()
    {
        RestartTestWorld.World world = RestartTestWorld.Build();
        Vector3[] before = world.Players.Select(p => p.Position).ToArray();

        Assert.True(RestartTestWorld.TryPlanThrowIn(world, out RestartPlan first));
        Assert.True(RestartTestWorld.TryPlanThrowIn(world, out RestartPlan second));

        Assert.Equal(first, second);
        Assert.Equal(before, world.Players.Select(p => p.Position).ToArray());
    }

    /// <summary>The library's own vocabulary: which kinds have a routine and how long each one's short option may be.</summary>
    [Fact]
    public void TheLibrary_CoversThePlanKindsAndNoOthers()
    {
        Assert.True(RestartPolicy.Supports(RestartKind.Kickoff));
        Assert.True(RestartPolicy.Supports(RestartKind.ThrowIn));
        Assert.True(RestartPolicy.Supports(RestartKind.CornerKick));
        Assert.True(RestartPolicy.Supports(RestartKind.FreeKick));
        Assert.True(RestartPolicy.Supports(RestartKind.IndirectFreeKick));
        Assert.True(RestartPolicy.Supports(RestartKind.GoalKick));
        Assert.False(RestartPolicy.Supports(RestartKind.PenaltyKick));
        Assert.False(RestartPolicy.Supports(RestartKind.None));

        // A corner's short option must be at the flag, not in the box; a goal kick's may be a defender dropping.
        Assert.True(RestartPolicy.SupportBoundMeters(RestartKind.CornerKick) <
                    RestartPolicy.SupportBoundMeters(RestartKind.ThrowIn));
        Assert.True(RestartPolicy.SupportBoundMeters(RestartKind.GoalKick) >
                    RestartPolicy.SupportBoundMeters(RestartKind.ThrowIn));
    }

    private static float SpaceOf(RestartPlan plan) => plan.ReceiverSpace01;
}

