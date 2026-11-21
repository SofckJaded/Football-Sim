using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;

namespace FootballSim.Tests.AI;

/// <summary>
/// A small explicit world for the A8.5 restart-routine tests: a home team taking a restart with one team-mate
/// near the ball (the support option), one further up the pitch (the target option) and the away side positioned
/// so a test can state the offside line and the space around each receiver.
///
/// The world is deliberately the election's own inputs (players, the restarting team's belief, its profile)
/// rather than a finished <see cref="MatchSimulationEngine"/>, so a policy test can ask the election one question
/// at a time.
/// </summary>
internal static class RestartTestWorld
{
    public const int HomeKeeper = 1;
    public const int HomeCenterBack = 2;
    public const int HomeCenterBackTwo = 3;
    public const int HomeMidfielder = 4;
    public const int HomeTaker = 8;
    public const int HomeSupport = 9;
    public const int HomeTarget = 10;
    public const int HomeWinger = 11;

    public const int AwayKeeper = 21;
    public const int AwayCenterBack = 22;
    public const int AwayCenterBackTwo = 23;
    public const int AwayMidfielder = 24;
    public const int AwayWinger = 25;
    public const int AwayStriker = 26;

    public static readonly PitchDimensions Pitch = new();

    /// <summary>Where the fixture's throw-in is placed: on the positive touchline, at the halfway line.</summary>
    public static readonly Vector2 ThrowInBall = new(0f, Pitch.HalfWidth - 0.5f);

    /// <summary>A world whose players a test can move before it asks the election.</summary>
    public sealed class World
    {
        public required List<FootballerState> Players { get; init; }

        public FootballerState Player(int id) => Players.Find(p => p.Id == id)
            ?? throw new InvalidOperationException($"No player #{id} in the fixture.");

        /// <summary>Moves a player and returns the world, for a fluent fixture line in a test.</summary>
        public World Move(int id, float x, float z)
        {
            FootballerState player = Player(id);
            player.Position = new Vector3(x, 0f, z);
            player.PreviousPosition = player.Position;
            return this;
        }

        /// <summary>The restarting team's own picture of its opponents, built from the world's players.</summary>
        public TeamBelief Belief(TeamId team) => TeamBelief.FromTruth(team, Players);
    }

    /// <summary>
    /// The default roster, laid out for a home throw-in at <see cref="ThrowInBall"/>: #9 stands inside the line
    /// five metres from the taker (the support option), #10 is the most advanced team-mate (the target option),
    /// the away side is deep enough that nobody except the ball is close to the throw.
    /// </summary>
    public static World Build()
    {
        var players = new List<FootballerState>
        {
            Player(HomeKeeper, TeamId.Home, TacticalRole.Goalkeeper, -45f, 0f),
            Player(HomeCenterBack, TeamId.Home, TacticalRole.CenterBack, -30f, -10f),
            Player(HomeCenterBackTwo, TeamId.Home, TacticalRole.CenterBack, -30f, 10f),
            Player(HomeMidfielder, TeamId.Home, TacticalRole.DefensiveMidfielder, -18f, 0f),
            Player(HomeTaker, TeamId.Home, TacticalRole.FullBack, -0.45f, Pitch.HalfWidth - 0.5f),
            Player(HomeSupport, TeamId.Home, TacticalRole.CentralMidfielder, 4f, 29f),
            Player(HomeTarget, TeamId.Home, TacticalRole.Striker, 25f, 12f),
            Player(HomeWinger, TeamId.Home, TacticalRole.Winger, 20f, 30f),

            Player(AwayKeeper, TeamId.Away, TacticalRole.Goalkeeper, 45f, 0f),
            Player(AwayCenterBack, TeamId.Away, TacticalRole.CenterBack, 12f, 8f),
            Player(AwayCenterBackTwo, TeamId.Away, TacticalRole.CenterBack, 8f, -12f),
            Player(AwayMidfielder, TeamId.Away, TacticalRole.CentralMidfielder, 22f, 22f),
            Player(AwayWinger, TeamId.Away, TacticalRole.Winger, -5f, 15f),
            Player(AwayStriker, TeamId.Away, TacticalRole.Striker, 35f, -5f)
        };

        return new World { Players = players };
    }

    /// <summary>
    /// Moves the away side so the believed offside line sits at x = 10 m: the keeper is the only opponent ahead of
    /// it, every other away player is behind. It is the fixture the law-gate tests need — an advanced home man
    /// (x = 25, <see cref="HomeTarget"/>) is beyond the line.
    /// </summary>
    public static void LowAwayLine(World world)
    {
        world.Move(AwayKeeper, 45f, 0f);
        world.Move(AwayCenterBack, 10f, 0f);
        world.Move(AwayCenterBackTwo, 10f, 8f);
        world.Move(AwayMidfielder, 8f, 22f);
        world.Move(AwayWinger, -5f, 15f);
        world.Move(AwayStriker, 5f, -5f);
    }

    /// <summary>Asks the election for a routine on the fixture's throw-in.</summary>
    public static bool TryPlanThrowIn(
        World world,
        out RestartPlan plan,
        TeamTacticalProfile? profile = null,
        RestartKind kind = RestartKind.ThrowIn,
        Vector2? ball = null,
        int? humanPlayerId = null) =>
        RestartPolicy.TryPlan(
            TeamId.Home, kind, ball ?? ThrowInBall, HomeTaker, world.Players, world.Belief(TeamId.Home),
            profile ?? TeamTacticalProfile.PossessionControl, humanPlayerId, 1f, Pitch, out plan);

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
