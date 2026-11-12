using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Rules;
using Xunit;

namespace FootballSim.Tests.Rules;

/// <summary>
/// A1.4 — a laws decision must be made from the geometry that existed when the ball was played.
///
/// The rules engine runs after the physics step, so by the time a contact is judged the live
/// <see cref="BallState"/> has already moved. Judging offside against it means the offside line is
/// drawn where the ball is *going*. <see cref="ContactGeometrySnapshot"/> freezes the play instead.
/// </summary>
public sealed class ContactTimeOffsideTests
{
    private const float Dt = SimConstants.FixedDeltaTime;

    /// <summary>
    /// Passer at X=0, an attacker at X=30, and two opponents at X=10 and X=20: the second-last
    /// opponent puts the offside line at X=10, so the attacker at X=30 is offside when the pass is
    /// played.
    /// </summary>
    private static (MatchRulesEngine Rules, FootballerState[] Players, BallState Ball) Scenario()
    {
        var rules = new MatchRulesEngine(new PitchDimensions());
        FootballerState passer = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(0f, 0f, 0f));
        FootballerState attacker = Player(9, TeamId.Home, TacticalRole.Striker, new Vector3(30f, 0f, 0f));
        FootballerState keeper = Player(12, TeamId.Away, TacticalRole.Goalkeeper, new Vector3(20f, 0f, 0f));
        FootballerState centreBack = Player(13, TeamId.Away, TacticalRole.CenterBack, new Vector3(10f, 0f, 0f));
        var players = new[] { passer, attacker, keeper, centreBack };

        var ball = new BallState { Position = new Vector3(0f, 0.11f, 0f) };
        rules.StartMatch(players, ball);

        // StartMatch re-places everyone for the kickoff; set the fragment up after it.
        passer.Position = new Vector3(0f, 0f, 0f);
        attacker.Position = new Vector3(30f, 0f, 0f);
        keeper.Position = new Vector3(20f, 0f, 0f);
        centreBack.Position = new Vector3(10f, 0f, 0f);
        ball.Position = new Vector3(0f, 0.11f, 0f);
        ball.LastTouchedByTeam = TeamId.Home;
        ball.LastTouchedByPlayerId = 8;

        return (rules, players, ball);
    }

    private static FootballerState Player(int id, TeamId team, TacticalRole role, Vector3 position) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Position = position,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfiles.Neutral
    };

    /// <summary>Calls the rules pass the way the engine does: contacts + the geometry captured with them.</summary>
    private static void StepWithGeometry(
        MatchRulesEngine rules,
        FootballerState[] players,
        BallState ball,
        ContactGeometrySnapshot geometry,
        BallContactEvent contact) =>
        rules.StepRules(players, ball, 2700f, Dt, new[] { contact }, geometry);

    [Fact]
    public void OffsideVerdict_DoesNotChangeWhenTheBallMovesAfterTheContact()
    {
        // Play 1: #8 plays the ball while #9 is beyond the line.
        var (rulesAtRest, playersAtRest, ballAtRest) = Scenario();
        var geometryAtRest = new ContactGeometrySnapshot();
        geometryAtRest.Capture(1, playersAtRest, ballAtRest.Position);
        var pass = new BallContactEvent(1, BallContactKind.Kick, 8, TeamId.Home, ballAtRest.Position);
        StepWithGeometry(rulesAtRest, playersAtRest, ballAtRest, geometryAtRest, pass);

        // Play 1 again, except the ball has been integrated 35 m downfield before the rules pass —
        // past the attacker. Read live, that would put the attacker onside.
        var (rulesMoved, playersMoved, ballMoved) = Scenario();
        var geometryMoved = new ContactGeometrySnapshot();
        geometryMoved.Capture(1, playersMoved, ballMoved.Position);
        var passMoved = new BallContactEvent(1, BallContactKind.Kick, 8, TeamId.Home, ballMoved.Position);
        ballMoved.Position = new Vector3(35f, 0.11f, 0f);
        StepWithGeometry(rulesMoved, playersMoved, ballMoved, geometryMoved, passMoved);

        // The reception decides whether the earlier offside position becomes an offence.
        var collection = new BallContactEvent(2, BallContactKind.Collection, 9, TeamId.Home,
            new Vector3(35f, 0.11f, 0f));

        ballAtRest.Position = new Vector3(35f, 0.11f, 0f);
        ballAtRest.LastTouchedByPlayerId = 9;
        var geometryRest2 = new ContactGeometrySnapshot();
        geometryRest2.Capture(2, playersAtRest, ballAtRest.Position);
        StepWithGeometry(rulesAtRest, playersAtRest, ballAtRest, geometryRest2, collection);

        ballMoved.LastTouchedByPlayerId = 9;
        var geometryMoved2 = new ContactGeometrySnapshot();
        geometryMoved2.Capture(2, playersMoved, ballMoved.Position);
        StepWithGeometry(rulesMoved, playersMoved, ballMoved, geometryMoved2, collection);

        Assert.Equal(MatchPhase.FreeKick, rulesAtRest.Phase);
        Assert.Equal(MatchPhase.FreeKick, rulesMoved.Phase);
        Assert.Contains(rulesAtRest.Events, e => e.EventType == "FreeKick");
        Assert.Contains(rulesMoved.Events, e => e.EventType == "FreeKick");
    }

    [Fact]
    public void OffsideVerdict_WithoutGeometry_StillReadsTheLiveBall()
    {
        // This pins why the snapshot exists: the legacy call reads state after integration, so moving
        // the ball changes the verdict. If this ever starts failing, the fallback path changed and the
        // A1.4 guarantee is being delivered some other way — check before deleting the snapshot.
        var (rulesLive, playersLive, ballLive) = Scenario();
        ballLive.Position = new Vector3(35f, 0.11f, 0f);
        rulesLive.StepRules(playersLive, ballLive, 2700f, Dt,
            new[] { new BallContactEvent(1, BallContactKind.Kick, 8, TeamId.Home, ballLive.Position) });

        ballLive.LastTouchedByPlayerId = 9;
        rulesLive.StepRules(playersLive, ballLive, 2700f, Dt,
            new[] { new BallContactEvent(2, BallContactKind.Collection, 9, TeamId.Home, ballLive.Position) });

        // The live ball at 35 m lifted the line past the attacker, so no offside was recorded.
        Assert.Equal(MatchPhase.FirstHalf, rulesLive.Phase);
    }

    [Fact]
    public void OffsideFreeKick_IsPlacedWhereTheOffenceHappened()
    {
        var (rules, players, ball) = Scenario();
        var geometry = new ContactGeometrySnapshot();
        geometry.Capture(1, players, ball.Position);

        StepWithGeometry(rules, players, ball, geometry,
            new BallContactEvent(1, BallContactKind.Kick, 8, TeamId.Home, new Vector3(0f, 0.11f, 0f)));

        // The ball is integrated 35 m downfield before the reception is judged.
        ball.Position = new Vector3(35f, 0.11f, 0f);
        ball.LastTouchedByPlayerId = 9;
        var geometryAtReception = new ContactGeometrySnapshot();
        geometryAtReception.Capture(2, players, ball.Position);
        StepWithGeometry(rules, players, ball, geometryAtReception,
            new BallContactEvent(2, BallContactKind.Collection, 9, TeamId.Home, new Vector3(35f, 0.11f, 0f)));

        Assert.Equal(MatchPhase.FreeKick, rules.Phase);
        Assert.InRange(rules.RestartSpot.X, 34.5f, 35.5f);
    }

    [Fact]
    public void GeometrySnapshot_OverwritesItsBufferOnEveryCapture()
    {
        var geometry = new ContactGeometrySnapshot(capacity: 4);
        FootballerState[] first =
        {
            Player(1, TeamId.Home, TacticalRole.Striker, new Vector3(1f, 0f, 0f)),
            Player(2, TeamId.Away, TacticalRole.CenterBack, new Vector3(2f, 0f, 0f))
        };
        FootballerState[] second =
        {
            Player(3, TeamId.Away, TacticalRole.Winger, new Vector3(3f, 0f, 0f))
        };

        Assert.False(geometry.IsCaptured);

        geometry.Capture(10, first, new Vector3(5f, 0f, 0f));
        Assert.True(geometry.IsCaptured);
        Assert.Equal(2, geometry.Count);
        Assert.Equal(10, geometry.Tick);
        Assert.Equal(new Vector3(5f, 0f, 0f), geometry.BallPosition);

        geometry.Capture(20, second, new Vector3(6f, 0f, 0f));
        Assert.Equal(1, geometry.Count);
        Assert.Equal(3, geometry.PlayerIdAt(0));
        Assert.Equal(20, geometry.Tick);

        geometry.Reset();
        Assert.False(geometry.IsCaptured);
        Assert.Equal(0, geometry.Count);
    }

    [Fact]
    public void GeometrySnapshot_RejectsANonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContactGeometrySnapshot(0));
    }
}

/// <summary>Neutral tendencies for tests that do not care about individual differences.</summary>
internal static class PlayerTendencyProfiles
{
    public static PlayerTendencies Neutral => new();
}
