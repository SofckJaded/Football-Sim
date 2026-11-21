using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Tactics;

namespace FootballSim.Tests.AI;

/// <summary>
/// A small explicit world for the A8.4 trap tests: an away winger holding the ball on the negative touchline,
/// with the home side's presser close enough to press, the allocator's cover behind him and one shape-duty
/// midfielder near the inside lane the carrier would use to get out.
///
/// The world is deliberately the engine's own inputs (players, allocator, belief, possession) rather than a
/// finished <see cref="MatchSimulationEngine"/>, so a policy test can ask the election one question at a time.
/// </summary>
internal static class TrapTestWorld
{
    public const int HomeGoalkeeper = 101;
    public const int HomeFullBack = 104;
    public const int HomeCenterBack = 102;
    public const int HomeCenterBackTwo = 103;
    public const int HomeCover = 106;
    public const int HomeScreener = 108;
    public const int AwayGoalkeeper = 209;
    public const int AwayCarrier = 207;
    public const int AwayMidfielder = 208;
    public const int AwayStriker = 210;

    public static readonly PitchDimensions Pitch = new();

    public sealed class World
    {
        public required List<FootballerState> Players { get; init; }
        public required List<FootballerState> Home { get; init; }
        public required List<FootballerState> Away { get; init; }
        public required TeamJobCoordinator Jobs { get; init; }
        public required TeamFormation HomeFormation { get; init; }
        public required TeamFormation AwayFormation { get; init; }
        public required BallState Ball { get; init; }

        public FootballerState Player(int id) => Players.Find(p => p.Id == id)
            ?? throw new InvalidOperationException($"No player #{id} in the fixture.");

        /// <summary>The allocator's picture after the positions moved: re-runs the job assignment at a later tick.</summary>
        public void RefreshJobs(int tick)
        {
            Jobs.Update(
                Players, Ball, Possession(),
                possessingTeam: TeamId.Away,
                homeResponderId: HomeFullBack, awayResponderId: null, homeDirX: 1f, tick: tick,
                homeProfile: TeamTacticalProfile.PossessionControl,
                awayProfile: TeamTacticalProfile.DirectCounterattack,
                homeOpponents: TeamBelief.FromTruth(TeamId.Home, Players),
                awayOpponents: TeamBelief.FromTruth(TeamId.Away, Players),
                homeFormation: HomeFormation, awayFormation: AwayFormation);
        }
    }

    /// <summary>The authoritative possession the fixture is in: the away winger controls the ball.</summary>
    public static BallPossessionSnapshot Possession() =>
        new(BallControlState.Controlled, TeamId.Away, AwayCarrier, null);

    public static World Build()
    {
        var home = new List<FootballerState>
        {
            Player(HomeGoalkeeper, TeamId.Home, TacticalRole.Goalkeeper, -45f, 0f),
            Player(HomeFullBack, TeamId.Home, TacticalRole.FullBack, -7f, -28.5f),
            Player(HomeCenterBack, TeamId.Home, TacticalRole.CenterBack, -20f, -16f),
            Player(HomeCenterBackTwo, TeamId.Home, TacticalRole.CenterBack, -24f, -2f),
            Player(HomeCover, TeamId.Home, TacticalRole.DefensiveMidfielder, -14f, -26f),
            Player(HomeScreener, TeamId.Home, TacticalRole.CentralMidfielder, -6f, -20f)
        };
        var away = new List<FootballerState>
        {
            Player(AwayGoalkeeper, TeamId.Away, TacticalRole.Goalkeeper, 45f, 0f),
            Player(AwayCarrier, TeamId.Away, TacticalRole.Winger, 0f, -30f),
            Player(AwayMidfielder, TeamId.Away, TacticalRole.CentralMidfielder, -4f, -24f),
            Player(AwayStriker, TeamId.Away, TacticalRole.Striker, 8f, -12f)
        };

        var players = new List<FootballerState>();
        players.AddRange(home);
        players.AddRange(away);

        var homeFormation = new TeamFormation(TeamId.Home, Pitch);
        homeFormation.AssignFromPositions(home, 1f);
        var awayFormation = new TeamFormation(TeamId.Away, Pitch);
        awayFormation.AssignFromPositions(away, -1f);

        FootballerState carrier = away.Find(p => p.Id == AwayCarrier)!;
        var ball = new BallState
        {
            Position = carrier.Position with { Y = 0.11f },
            PreviousPosition = carrier.Position with { Y = 0.11f },
            Velocity = Vector3.Zero,
            LastTouchedByPlayerId = AwayCarrier,
            LastTouchedByTeam = TeamId.Away,
            LastPlay = BallLastPlay.None
        };

        var world = new World
        {
            Players = players,
            Home = home,
            Away = away,
            Jobs = new TeamJobCoordinator(Pitch),
            HomeFormation = homeFormation,
            AwayFormation = awayFormation,
            Ball = ball
        };
        world.RefreshJobs(tick: 1);

        if (world.Jobs.Get(HomeCover)?.Job != TacticalJob.CoverDefender)
        {
            throw new InvalidOperationException(
                $"the fixture no longer produces a cover: default jobs are {JobsOf(world)}");
        }

        return world;
    }

    /// <summary>The allocator's jobs as one line, for a failure message.</summary>
    public static string JobsOf(World world)
    {
        var text = new System.Text.StringBuilder();
        foreach (FootballerState player in world.Home)
            text.Append($"#{player.Id}:{world.Jobs.Get(player.Id)?.Job} ");
        return text.ToString();
    }

    /// <summary>
    /// A team belief built the way the engine builds it (from team-mates' observations), with the opponent
    /// sightings aged by <paramref name="ageTicks"/>. Used for the "the picture is too old to trap on" gate.
    /// </summary>
    public static TeamBelief AgedBelief(World world, TeamId team, int ageTicks, bool includeOpponents = true)
    {
        var visible = new List<PerceivedEntity>();
        if (includeOpponents)
        {
            foreach (FootballerState opponent in team == TeamId.Home ? world.Away : world.Home)
            {
                if (opponent.Role == TacticalRole.Goalkeeper) continue;
                visible.Add(new PerceivedEntity(opponent.Id, opponent.Team, opponent.Role,
                    opponent.Position, opponent.Velocity, 1f) { ObservedAgeTicks = ageTicks });
            }
        }

        FootballerState watcher = team == TeamId.Home ? world.Home[1] : world.Away[1];
        var observations = new Dictionary<int, FootballerObservation>
        {
            [watcher.Id] = Observation(watcher, visible)
        };

        var belief = new TeamBelief(team);
        belief.RebuildFromObservations(world.Players, observations);
        return belief;
    }

    private static FootballerObservation Observation(FootballerState self, IReadOnlyList<PerceivedEntity> visible) =>
        new()
        {
            SelfId = self.Id,
            SelfTeam = self.Team,
            SelfRole = self.Role,
            SelfPosition = self.Position,
            SelfVelocity = self.Velocity,
            SelfFacingXZ = self.FacingXZ,
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = self.Position,
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = self.Team == TeamId.Home ? TeamId.Away : TeamId.Home,
            BallLastTouchedByPlayerId = AwayCarrier,
            BallLastTouchedByTeam = TeamId.Away,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = AwayCarrier,
            TacticalAnchorPosition = self.TacticalAnchor,
            AttackingDirectionX = self.Team == TeamId.Home ? 1f : -1f,
            MatchTimeSeconds = 10f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = 100,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = self.Tendencies,
            SelfAttributes = self.Attributes
        };

    private static FootballerState Player(int id, TeamId team, TacticalRole role, float x, float z) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Position = new Vector3(x, 0f, z),
        TacticalAnchor = new Vector3(x, 0f, z),
        FacingXZ = new Vector2(team == TeamId.Home ? 1f : -1f, 0f),
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(role),
        LastCommand = FootballerCommand.Idle
    };
}
