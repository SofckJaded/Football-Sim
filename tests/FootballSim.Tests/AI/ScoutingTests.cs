using System;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A2.3 — the capability estimate a scorer is allowed to use.
///
/// Two properties matter. First, an opponent nobody has scouted must look the same whatever their real
/// attributes are: the door through which a live attribute (and one day an intention) would leak has to
/// be shut, not merely unused. Second, when there *is* a report the report is the source — a banded,
/// confident estimate, never the exact authoritative number.
/// </summary>
public sealed class ScoutingTests
{
    private const float Dt = SimConstants.FixedDeltaTime;

    private static FootballerState Player(
        int id,
        TeamId team,
        TacticalRole role,
        Vector3 position,
        float maxSprintSpeed = 8.4f)
    {
        var attributes = new PlayerAttributes { MaxSprintSpeed = maxSprintSpeed };
        return new FootballerState
        {
            Id = id,
            Team = team,
            ShirtNumber = id,
            Role = role,
            Attributes = attributes,
            Tendencies = new PlayerTendencies(),
            Position = position
        };
    }

    private static FootballerObservation Observe(
        PerceptionSystem perception,
        FootballerState self,
        FootballerState[] players,
        int tick)
    {
        var ball = new BallState { Position = new Vector3(45f, 0.11f, 20f) };
        return perception.BuildObservation(
            self, players, ball, null, MatchPhase.FirstHalf, tick * Dt, attackingDirX: 1f,
            isSetPieceTaker: false, TeamTacticalProfile.PossessionControl, new SeededRandom(5),
            tick: tick);
    }

    private static PerceivedEntity Find(in FootballerObservation observation, int playerId)
    {
        foreach (PerceivedEntity entity in observation.VisiblePlayers)
            if (entity.PlayerId == playerId) return entity;
        throw new InvalidOperationException($"Player #{playerId} is not in the observation.");
    }

    [Fact]
    public void AnUnscoutedOpponent_LooksIdentical_WhateverTheirRealAttributes()
    {
        // Same world except the opponent's real sprint speed: 6.0 m/s versus 10.5 m/s. Without a report
        // the observer must not be able to tell them apart.
        FootballerState self = Player(1, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        FootballerState slow = Player(2, TeamId.Away, TacticalRole.Striker, new Vector3(9f, 0f, 0f), maxSprintSpeed: 6.0f);
        FootballerState fast = Player(2, TeamId.Away, TacticalRole.Striker, new Vector3(9f, 0f, 0f), maxSprintSpeed: 10.5f);

        CapabilityEstimate slowSeen = Find(
            Observe(new PerceptionSystem(), self, new[] { self, slow }, tick: 1), 2).Capability;
        CapabilityEstimate fastSeen = Find(
            Observe(new PerceptionSystem(), self, new[] { self, fast }, tick: 1), 2).Capability;

        Assert.Equal(CapabilityEstimate.Generic, slowSeen);
        Assert.Equal(slowSeen, fastSeen);
        Assert.False(slowSeen.IsScouted);

        // And the estimate it feeds is identical too, which is the property that actually matters.
        Vector2 target = new(20f, 5f);
        Assert.Equal(
            SpatialAnalysisArrival(slowSeen, target),
            SpatialAnalysisArrival(fastSeen, target));
    }

    [Fact]
    public void AScoutingReport_IsBanded_SoItIsNotTheLiveAttribute()
    {
        // 8.43 m/s is not on the 0.25 m/s band, so a reported value proves it came from the report.
        FootballerState player = Player(7, TeamId.Away, TacticalRole.Winger, Vector3.Zero, maxSprintSpeed: 8.43f);
        ScoutingReport report = ScoutingReport.FromSquad(new[] { player });

        CapabilityEstimate estimate = report.Get(7);

        Assert.True(estimate.IsScouted);
        Assert.NotEqual(player.Attributes.MaxSprintSpeed, estimate.MaxSprintSpeed);
        Assert.Equal(8.5f, estimate.MaxSprintSpeed, precision: 3);
        Assert.Equal(ScoutingReport.ScoutingConfidence01, estimate.Confidence01);
        Assert.Equal(0f, estimate.MaxSprintSpeed % ScoutingReport.SpeedBand, precision: 3);
    }

    [Fact]
    public void TheReport_CoversEveryoneOnce_AndDefaultsForAnyoneElse()
    {
        var squad = new[]
        {
            Player(1, TeamId.Home, TacticalRole.Goalkeeper, Vector3.Zero),
            Player(2, TeamId.Home, TacticalRole.CenterBack, new Vector3(-10f, 0f, 0f)),
            Player(3, TeamId.Away, TacticalRole.Striker, new Vector3(10f, 0f, 0f), maxSprintSpeed: 9.9f)
        };

        ScoutingReport report = ScoutingReport.FromSquad(squad);

        Assert.Equal(3, report.Count);
        Assert.True(report.Get(3).IsScouted);
        Assert.Equal(10.0f, report.Get(3).MaxSprintSpeed, precision: 3);
        Assert.Equal(CapabilityEstimate.Generic, report.Get(999));
    }

    [Fact]
    public void AScoutedOpponent_IsPublishedThroughTheObservation()
    {
        FootballerState self = Player(1, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        FootballerState opponent = Player(2, TeamId.Away, TacticalRole.Striker, new Vector3(9f, 0f, 0f), maxSprintSpeed: 9.9f);
        var players = new[] { self, opponent };
        var scouting = ScoutingReport.FromSquad(players);

        PerceivedEntity seen = Find(
            Observe(new PerceptionSystem(scouting), self, players, tick: 1), 2);

        Assert.True(seen.Capability.IsScouted);
        Assert.Equal(scouting.Get(2).MaxSprintSpeed, seen.Capability.MaxSprintSpeed);
        Assert.True(seen.Capability.MaxSprintSpeed > CapabilityEstimate.Generic.MaxSprintSpeed);
    }

    [Fact]
    public void AFasterScoutedBand_ArrivesEarlier()
    {
        // The mechanism pass safety will be scored from: a quicker (scouted) defender is never given a
        // later arrival at the same point.
        Vector2 position = new(0f, -8f);
        Vector2 target = new(6f, 6f);

        float slowArrival = SpatialAnalysisArrival(
            new CapabilityEstimate(6.0f, 5.5f, 8.0f, 0.75f, 0.8f), target, position);
        float fastArrival = SpatialAnalysisArrival(
            new CapabilityEstimate(10.0f, 8.0f, 11.0f, 0.75f, 0.8f), target, position);

        Assert.True(fastArrival < slowArrival, $"fast {fastArrival:F3}s vs slow {slowArrival:F3}s");
    }

    private static float SpatialAnalysisArrival(
        CapabilityEstimate capability,
        Vector2 target,
        Vector2? position = null)
    {
        Vector2 start = position ?? Vector2.Zero;
        ReachKinematics kinematics = capability.ToKinematics(start, Vector2.Zero);
        return LocalPressureField.EstimateArrivalSeconds(kinematics, target, horizon: 10f);
    }
}
