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
/// A2.6 — the team-level knowledge boundary, and the labelled maps built on it.
///
/// Until this batch the boundary was player-local: a player's own decisions read his observation, but the
/// persistent job coordinator read the authoritative squad. That meant a team's *structure* still reacted
/// instantly to an opponent nobody on that team had seen. The first test here is the gate statement: move
/// a hidden opponent, and the team's assignments must not move with him.
/// </summary>
public sealed class TeamBeliefTests
{
    private const float Dt = SimConstants.FixedDeltaTime;

    private static FootballerState Player(int id, TeamId team, TacticalRole role, Vector3 position, Vector2 facing) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(role),
        Position = position,
        FacingXZ = facing
    };

    private static BallState BallAt(Vector3 position) => new() { Position = position, LastTouchedByTeam = TeamId.Home };

    /// <summary>Builds every player's observation for one tick, exactly as the engine does.</summary>
    private static Dictionary<int, FootballerObservation> Observe(
        PerceptionSystem perception,
        IReadOnlyList<FootballerState> players,
        BallState ball,
        int tick)
    {
        var observations = new Dictionary<int, FootballerObservation>(players.Count);
        var rng = new SeededRandom(4242);
        foreach (FootballerState player in players)
        {
            observations[player.Id] = perception.BuildObservation(
                player, players, ball, TeamId.Home, MatchPhase.FirstHalf, tick * Dt, attackingDirX: 1f,
                isSetPieceTaker: false, TeamTacticalProfile.PossessionControl, rng, tick: tick);
        }
        return observations;
    }

    /// <summary>
    /// Two identical matches that differ only in what a hidden opponent does after the first look. Both
    /// teams are built the same way, so any difference in the returned jobs would be the leak.
    /// </summary>
    private static (Dictionary<int, TeamJobAssignment> First, Dictionary<int, TeamJobAssignment> Second, TeamBelief TruthFirst, TeamBelief TruthSecond)
        HiddenOpponentJobs(Vector3 hiddenPositionA, Vector3 hiddenVelocityA, Vector3 hiddenPositionB, Vector3 hiddenVelocityB)
    {
        var home = new List<FootballerState>
        {
            Player(1, TeamId.Home, TacticalRole.CenterBack, new Vector3(-20f, 0f, 0f), new Vector2(1f, 0f)),
            Player(2, TeamId.Home, TacticalRole.CenterBack, new Vector3(-22f, 0f, 8f), new Vector2(1f, 0f)),
            Player(3, TeamId.Home, TacticalRole.DefensiveMidfielder, new Vector3(-18f, 0f, -6f), new Vector2(1f, 0f)),
            Player(4, TeamId.Home, TacticalRole.FullBack, new Vector3(-24f, 0f, -12f), new Vector2(1f, 0f))
        };

        // #12 is in front of them and stays put: the world they are allowed to see.
        var visible = Player(12, TeamId.Away, TacticalRole.Striker, new Vector3(6f, 0f, 0f), new Vector2(-1f, 0f));

        var ball = BallAt(new Vector3(-16f, 0f, 0f));

        (Dictionary<int, TeamJobAssignment> Jobs, TeamBelief Truth) Run(Vector3 hiddenPosition, Vector3 hiddenVelocity)
        {
            var hidden = Player(11, TeamId.Away, TacticalRole.Winger, hiddenPosition, new Vector2(1f, 0f));
            hidden.Velocity = hiddenVelocity;

            var players = new List<FootballerState>(home) { visible, hidden };
            var perception = new PerceptionSystem();

            // First look sees everyone (A2.2), then the hidden player falls behind the home players' backs.
            Observe(perception, players, ball, tick: 1);
            hidden.Position = hiddenPosition;
            hidden.Velocity = hiddenVelocity;
            Dictionary<int, FootballerObservation> second = Observe(perception, players, ball, tick: 2);

            var belief = new TeamBelief(TeamId.Home);
            belief.RebuildFromObservations(players, second);

            var jobs = new TeamJobCoordinator(new PitchDimensions());
            jobs.Update(
                players, ball, BallPossessionSnapshot.Loose, possessingTeam: TeamId.Home,
                homeResponderId: null, awayResponderId: null, homeDirX: 1f, tick: 2,
                TeamTacticalProfile.PossessionControl, TeamTacticalProfile.DirectCounterattack,
                belief, new TeamBelief(TeamId.Away));

            return (new Dictionary<int, TeamJobAssignment>(jobs.Assignments), TeamBelief.FromTruth(TeamId.Home, players));
        }

        (Dictionary<int, TeamJobAssignment> firstJobs, TeamBelief firstTruth) = Run(hiddenPositionA, hiddenVelocityA);
        (Dictionary<int, TeamJobAssignment> secondJobs, TeamBelief secondTruth) = Run(hiddenPositionB, hiddenVelocityB);
        return (firstJobs, secondJobs, firstTruth, secondTruth);
    }

    [Fact]
    public void AHiddenOpponent_MovingDifferently_CannotChangeTheTeamsJobs()
    {
        (Dictionary<int, TeamJobAssignment> first, Dictionary<int, TeamJobAssignment> second, _, _) = HiddenOpponentJobs(
            new Vector3(-34f, 0f, 6f), new Vector3(0f, 0f, 3f),
            new Vector3(-30f, 0f, -14f), new Vector3(0f, 0f, -5f));

        Assert.Equal(first.Count, second.Count);
        foreach (KeyValuePair<int, TeamJobAssignment> entry in first)
        {
            // Only the home team's jobs: the away team's own anchors legitimately follow its own
            // players, which is not an opponent leak.
            if (entry.Key is not (1 or 2 or 3 or 4)) continue;

            Assert.True(second.TryGetValue(entry.Key, out TeamJobAssignment other), $"player #{entry.Key} lost his job");
            Assert.Equal(entry.Value.Job, other.Job);
            Assert.Equal(entry.Value.Anchor, other.Anchor);
            Assert.Equal(entry.Value.TargetPlayerId, other.TargetPlayerId);
        }
    }

    [Fact]
    public void ATruthView_SeesTheDifferenceTheBeliefDoesNot_MarkingTheBoundaryReal()
    {
        // The same two worlds through a truth view: if the belief test above passed because *nothing*
        // was different anywhere, this would fail. It does not, so the two worlds genuinely differ and
        // only the belief is blind to it.
        (_, _, TeamBelief truthFirst, TeamBelief truthSecond) = HiddenOpponentJobs(
            new Vector3(-34f, 0f, 6f), new Vector3(0f, 0f, 3f),
            new Vector3(-30f, 0f, -14f), new Vector3(0f, 0f, -5f));

        TeamBelief.Opponent hiddenFirst = Find(truthFirst, 11);
        TeamBelief.Opponent hiddenSecond = Find(truthSecond, 11);

        Assert.NotEqual(hiddenFirst.PositionXZ, hiddenSecond.PositionXZ);
        Assert.NotEqual(hiddenFirst.VelocityXZ, hiddenSecond.VelocityXZ);
    }

    [Fact]
    public void TheBelief_KeepsTheYoungestSighting()
    {
        var near = Player(1, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero, new Vector2(1f, 0f));
        var far = Player(2, TeamId.Home, TacticalRole.CenterBack, new Vector3(-30f, 0f, 0f), new Vector2(1f, 0f));
        var opponent = Player(11, TeamId.Away, TacticalRole.Striker, new Vector3(9f, 0f, 0f), new Vector2(-1f, 0f));
        var players = new[] { near, far, opponent };
        var ball = BallAt(new Vector3(20f, 0f, 20f));

        var perception = new PerceptionSystem();
        Dictionary<int, FootballerObservation> observations = Observe(perception, players, ball, tick: 1);

        // Only the near player looks again on the next tick; the far one's sighting is a tick older.
        var fresh = new Dictionary<int, FootballerObservation>
        {
            [1] = perception.BuildObservation(
                near, players, ball, TeamId.Home, MatchPhase.FirstHalf, 2f, 1f, false,
                TeamTacticalProfile.PossessionControl, new SeededRandom(4243), tick: 2),
            [2] = observations[2]
        };

        var belief = new TeamBelief(TeamId.Home);
        belief.RebuildFromObservations(players, fresh);

        TeamBelief.Opponent seen = Find(belief, 11);
        Assert.True(seen.IsFresh, $"age {seen.AgeTicks}; the fresh sighting was not the one kept");
        Assert.Equal(0, belief.OldestAgeTicks);
    }

    [Fact]
    public void FewerThanTwoKnownOpponents_DoesNotInventAnOffsideLine()
    {
        var belief = new TeamBelief(TeamId.Home);
        Assert.False(belief.TryGetSecondLastProjX(1f, out float projection));
        Assert.Equal(0f, projection);

        belief.RebuildFromObservations(
            new[]
            {
                Player(1, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero, new Vector2(1f, 0f)),
                Player(11, TeamId.Away, TacticalRole.Striker, new Vector3(9f, 0f, 0f), new Vector2(-1f, 0f))
            },
            Observe(new PerceptionSystem(), new[]
            {
                Player(1, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero, new Vector2(1f, 0f)),
                Player(11, TeamId.Away, TacticalRole.Striker, new Vector3(9f, 0f, 0f), new Vector2(-1f, 0f))
            }, BallAt(new Vector3(20f, 0f, 20f)), tick: 1));

        Assert.Equal(1, belief.Count);
        Assert.False(belief.TryGetSecondLastProjX(1f, out _));
    }

    [Fact]
    public void TheOverlayMap_IsFiniteBoundedAndNormalised()
    {
        var pitch = new PitchDimensions();
        var home = new List<FootballerState>
        {
            Player(1, TeamId.Home, TacticalRole.CenterBack, new Vector3(-10f, 0f, 0f), new Vector2(1f, 0f)),
            Player(2, TeamId.Home, TacticalRole.Striker, new Vector3(10f, 0f, 5f), new Vector2(1f, 0f)),
            Player(3, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-48f, 0f, 0f), new Vector2(1f, 0f))
        };
        var away = new List<FootballerState>
        {
            Player(11, TeamId.Away, TacticalRole.Striker, new Vector3(20f, 0f, -5f), new Vector2(-1f, 0f))
        };
        var players = new List<FootballerState>(home);
        players.AddRange(away);

        var map = new SpatialOverlayMap(pitch);
        map.Build(players, TeamId.Home, TeamBelief.FromTruth(TeamId.Home, players));

        Assert.Equal(map.CellCount, map.Pressure01.Count);
        for (int i = 0; i < map.CellCount; i++)
        {
            Assert.InRange(map.InfluenceShare[i], 0f, 1f);
            Assert.InRange(map.OpponentInfluenceShare[i], 0f, 1f);
            Assert.InRange(map.UnclaimedShare[i], 0f, 1f);
            Assert.InRange(map.Pressure01[i], 0f, 1f);
            Assert.InRange(map.PerceptionAgeSeconds[i], 0f, 600f);
            Assert.Equal(
                1f,
                map.InfluenceShare[i] + map.OpponentInfluenceShare[i] + map.UnclaimedShare[i],
                precision: 3);
            Assert.True(map.OpponentArrivalSeconds[i] is -1f or >= 0f);
        }

        // Away from both teams the ground is genuinely unclaimed: that is the component the old 50/50
        // share could not express.
        Assert.Contains(map.UnclaimedShare, share => share > 0.99f);
    }

    private static TeamBelief.Opponent Find(TeamBelief belief, int playerId)
    {
        for (int i = 0; i < belief.Opponents.Count; i++)
            if (belief.Opponents[i].PlayerId == playerId) return belief.Opponents[i];
        throw new InvalidOperationException($"The belief does not contain player #{playerId}.");
    }
}
