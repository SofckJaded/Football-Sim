using System;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A7.2 — the team-call board.
///
/// These tests pin the channel's contract independently of the producer and the consumers: a shout is
/// heard only after the communication delay, is forgotten at its expiry, is per team, is bounded and
/// deterministic, and two shouts aimed at the same player are resolved by a stated priority instead of by
/// insertion order. The scenarios in <see cref="TeamCallScenarioTests"/> prove the pieces together.
/// </summary>
public sealed class TeamCallBoardTests
{
    private const int CallerId = 8;
    private const int TargetId = 9;
    private const int DelayTicks = 18;
    private const int Delay = DelayTicks;

    private static TeamCallBoard NewBoard() => new();

    private static bool PublishManOn(TeamCallBoard board, int tick, int target = TargetId, float strength = 0.8f) =>
        board.Publish(TeamId.Home, TeamCallKind.ManOn, CallerId, target, 0, strength, tick, Delay, 27);

    [Fact]
    public void ACall_IsNotHeardUntilTheCommunicationDelayHasPassed()
    {
        TeamCallBoard board = NewBoard();
        Assert.True(PublishManOn(board, tick: 100));

        Assert.False(board.TryGetPlayerCall(TeamId.Home, TargetId, 100 + Delay - 1, out _));
        Assert.Equal(0, board.VisibleCount(TeamId.Home, 100 + Delay - 1));

        Assert.True(board.TryGetPlayerCall(TeamId.Home, TargetId, 100 + Delay, out TeamCall call));
        Assert.Equal(TeamCallKind.ManOn, call.Kind);
        Assert.Equal(CallerId, call.CallerId);
        Assert.Equal(100, call.PublishedTick);
        Assert.Equal(100 + Delay, call.VisibleTick);
        Assert.Equal(1, board.VisibleCount(TeamId.Home, 100 + Delay));
    }

    [Fact]
    public void ACall_ExpiresAtTheEndOfItsLifetime()
    {
        TeamCallBoard board = NewBoard();
        const int lifetime = 27;
        board.Publish(TeamId.Home, TeamCallKind.Turn, CallerId, TargetId, 0, 0.6f, 100, Delay, lifetime);

        int expires = 100 + Delay + lifetime;
        Assert.True(board.TryGetPlayerCall(TeamId.Home, TargetId, expires - 1, out _));
        Assert.False(board.TryGetPlayerCall(TeamId.Home, TargetId, expires + 1, out _));

        // The sweep counts the expiry once, when a later read observes it.
        Assert.Equal(1, board.ExpiredCount);
    }

    [Fact]
    public void TheBoard_IsPerTeam()
    {
        TeamCallBoard board = NewBoard();
        PublishManOn(board, 100);

        int visible = 100 + Delay;
        Assert.True(board.TryGetPlayerCall(TeamId.Home, TargetId, visible, out _));
        Assert.False(board.TryGetPlayerCall(TeamId.Away, TargetId, visible, out _));
        Assert.Equal(1, board.VisibleCount(TeamId.Home, visible));
        Assert.Equal(0, board.VisibleCount(TeamId.Away, visible));
    }

    [Fact]
    public void ARepeatedShout_IsSuppressedWhileTheFirstIsLive()
    {
        TeamCallBoard board = NewBoard();
        Assert.True(PublishManOn(board, tick: 100));
        Assert.False(PublishManOn(board, tick: 110));
        Assert.Equal(1, board.SuppressedCount);

        // A repeat is a new event only after the first is over — and it starts its own delay.
        int afterExpiry = 100 + Delay + 27 + 1;
        Assert.True(PublishManOn(board, tick: afterExpiry));
        Assert.False(board.TryGetPlayerCall(TeamId.Home, TargetId, afterExpiry + Delay - 1, out _));
        Assert.True(board.TryGetPlayerCall(TeamId.Home, TargetId, afterExpiry + Delay, out _));
    }

    [Fact]
    public void ADifferentCaller_MayShoutTheSameThing()
    {
        TeamCallBoard board = NewBoard();
        Assert.True(PublishManOn(board, 100, strength: 0.4f));
        Assert.True(board.Publish(TeamId.Home, TeamCallKind.ManOn, 10, TargetId, 0, 0.9f, 100, Delay, 27));

        Assert.True(board.TryGetPlayerCall(TeamId.Home, TargetId, 100 + Delay, out TeamCall call));
        // Same priority: the stronger shout is the one that is heard.
        Assert.Equal(10, call.CallerId);
    }

    [Fact]
    public void TheHighestPriorityCall_IsHeard_WhenTwoTargetTheSamePlayer()
    {
        TeamCallBoard board = NewBoard();
        // Turn is published first and stronger; man-on outranks it because pressure is more urgent than space.
        board.Publish(TeamId.Home, TeamCallKind.Turn, 10, TargetId, 0, 0.99f, 100, Delay, 30);
        board.Publish(TeamId.Home, TeamCallKind.ManOn, 11, TargetId, 0, 0.30f, 100, Delay, 27);

        Assert.True(board.TryGetPlayerCall(TeamId.Home, TargetId, 100 + Delay, out TeamCall call));
        Assert.Equal(TeamCallKind.ManOn, call.Kind);
        Assert.Equal(11, call.CallerId);
    }

    [Fact]
    public void TheStrongestSwitchRequest_NamesItsReceiver()
    {
        TeamCallBoard board = NewBoard();
        // Two far-side team-mates ask for the switch; the more open one (higher strength) is the pass to make.
        board.Publish(TeamId.Home, TeamCallKind.Switch, 7, TargetId, namedPlayerId: 7, 0.55f, 100, Delay, 36);
        board.Publish(TeamId.Home, TeamCallKind.Switch, 11, TargetId, namedPlayerId: 11, 0.85f, 100, Delay, 36);

        Assert.True(board.TryGetPlayerCall(TeamId.Home, TargetId, 100 + Delay, out TeamCall call));
        Assert.Equal(TeamCallKind.Switch, call.Kind);
        Assert.Equal(11, call.NamedPlayerId);
    }

    [Fact]
    public void DropOutranksStep_ForTheLineInstruction()
    {
        TeamCallBoard board = NewBoard();
        board.Publish(TeamId.Home, TeamCallKind.Step, CallerId, 0, 0, 0.9f, 100, Delay, 36);
        board.Publish(TeamId.Home, TeamCallKind.Drop, TargetId, 0, 0, 0.6f, 100, Delay, 36);

        Assert.True(board.TryGetLineDirective(TeamId.Home, 100 + Delay, out TeamCall directive));
        Assert.Equal(TeamCallKind.Drop, directive.Kind);

        // The line instruction is not a call addressed to a player: it does not leak into the player read.
        Assert.False(board.TryGetPlayerCall(TeamId.Home, CallerId, 100 + Delay, out _));
    }

    [Fact]
    public void BuildContext_CombinesThePlayersCallAndTheLineInstruction()
    {
        TeamCallBoard board = NewBoard();
        PublishManOn(board, 100);
        board.Publish(TeamId.Home, TeamCallKind.Drop, 11, 0, 0, 0.7f, 100, Delay, 36);

        TeamCallContext heard = board.BuildContext(TeamId.Home, TargetId, 100 + Delay);
        Assert.Equal(TeamCallKind.ManOn, heard.Kind);
        Assert.Equal(CallerId, heard.CallerId);
        Assert.Equal(TeamCallKind.Drop, heard.LineDirective);
        Assert.True(heard.HasPlayerCall);
        Assert.True(heard.HasLineDirective);

        TeamCallContext silent = board.BuildContext(TeamId.Home, 999, 100 + Delay);
        Assert.Equal(TeamCallKind.None, silent.Kind);
        Assert.Equal(TeamCallKind.Drop, silent.LineDirective);
    }

    [Fact]
    public void TheBoard_RefusesACallItCannotInterpret()
    {
        TeamCallBoard board = NewBoard();

        Assert.False(board.Publish(TeamId.Home, TeamCallKind.None, CallerId, TargetId, 0, 0.5f, 100, Delay, 27));
        Assert.False(board.Publish(TeamId.Home, TeamCallKind.ManOn, callerId: 0, TargetId, 0, 0.5f, 100, Delay, 27));
        Assert.False(board.Publish(TeamId.Home, TeamCallKind.ManOn, TargetId, TargetId, 0, 0.5f, 100, Delay, 27));
        Assert.False(board.Publish(TeamId.Home, TeamCallKind.ManOn, CallerId, TargetId, 0, float.NaN, 100, Delay, 27));
        Assert.Equal(0, board.PublishedCount);
    }

    [Fact]
    public void TheBoard_IsBounded_AndEvictsLiveCallsDeterministically()
    {
        TeamCallBoard first = NewBoard();
        TeamCallBoard second = NewBoard();

        // Fill the table with more live calls than it can hold. Publishing them all on one tick keeps the
        // comparison honest: every survivor has the same visibility window.
        for (int i = 0; i < TeamCallBoard.Capacity + 10; i++)
        {
            int target = 100 + i;
            first.Publish(TeamId.Home, TeamCallKind.Turn, CallerId, target, 0, 0.5f, 100, Delay, 600);
            second.Publish(TeamId.Home, TeamCallKind.Turn, CallerId, target, 0, 0.5f, 100, Delay, 600);
        }

        Assert.Equal(TeamCallBoard.Capacity + 10, first.PublishedCount);
        Assert.Equal(10, first.EvictedCount);

        // Never more than the table: the board cannot grow with the match.
        int visible = 100 + Delay;
        Assert.Equal(TeamCallBoard.Capacity, first.VisibleCount(TeamId.Home, visible));
        Assert.True(first.VisibleCount(TeamId.Home, visible) <= TeamCallBoard.Capacity);

        // Two identical sequences end in identical state: eviction is index-ordered, not hash-ordered.
        Assert.Equal(first.EvictedCount, second.EvictedCount);
        for (int target = 100; target < 100 + TeamCallBoard.Capacity + 10; target++)
        {
            bool inFirst = first.TryGetPlayerCall(TeamId.Home, target, visible, out TeamCall _);
            bool inSecond = second.TryGetPlayerCall(TeamId.Home, target, visible, out TeamCall _);
            Assert.Equal(inFirst, inSecond);
        }
    }

    [Fact]
    public void AFullBoard_PrefersToEvictTheOtherTeamsCalls()
    {
        TeamCallBoard board = NewBoard();
        for (int i = 0; i < TeamCallBoard.Capacity; i++)
            board.Publish(TeamId.Away, TeamCallKind.Turn, CallerId, 100 + i, 0, 0.5f, 100, Delay, 600);

        // A fresh call with the board full must make room; the other team's call is the cheapest to lose.
        Assert.True(board.Publish(TeamId.Home, TeamCallKind.ManOn, 10, 200, 0, 0.9f, 100, Delay, 27));
        Assert.Equal(1, board.EvictedCount);
        Assert.True(board.TryGetPlayerCall(TeamId.Home, 200, 100 + Delay, out _));
    }

    [Fact]
    public void Clear_DropsOnlyThatTeamsCalls()
    {
        TeamCallBoard board = NewBoard();
        PublishManOn(board, 100);
        board.Publish(TeamId.Away, TeamCallKind.ManOn, 210, 220, 0, 0.5f, 100, Delay, 27);

        board.Clear(TeamId.Home);

        Assert.Equal(0, board.VisibleCount(TeamId.Home, 100 + Delay));
        Assert.Equal(1, board.VisibleCount(TeamId.Away, 100 + Delay));
    }

    [Fact]
    public void AContextWithNoCalls_IsNeutral()
    {
        TeamCallContext none = TeamCallContext.None;

        Assert.False(none.HasPlayerCall);
        Assert.False(none.HasLineDirective);
        Assert.True(none.IsNeutral);
        Assert.Equal("none", none.Describe());
    }

    [Fact]
    public void CallDescribe_NamesTheInstructionAndItsWindow()
    {
        TeamCallBoard board = NewBoard();
        board.Publish(TeamId.Home, TeamCallKind.Switch, CallerId, TargetId, namedPlayerId: 11, 0.7f, 100, Delay, 36);

        Assert.True(board.TryGetPlayerCall(TeamId.Home, TargetId, 100 + Delay, out TeamCall call));
        string text = call.Describe();
        Assert.Contains("Switch", text);
        Assert.Contains($"#{CallerId}", text);
        Assert.Contains("#11", text);
        Assert.Contains($"heard t{100 + Delay}", text);
    }
}
