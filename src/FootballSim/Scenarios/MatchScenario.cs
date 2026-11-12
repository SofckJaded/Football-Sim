using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;

namespace FootballSim.Scenarios;

/// <summary>
/// How the ball starts a scenario. The distinction matters because the authoritative ownership model
/// (<see cref="BallPossessionTracker"/>) normally only learns ownership from contact events: a
/// scenario that starts mid-play has to seed the same state a real contact would have produced.
/// </summary>
public enum ScenarioBallSetup
{
    /// <summary>Nobody controls the ball; it is simply lying somewhere (a loose ball or a rebound).</summary>
    Loose,

    /// <summary>A player has the ball under control at their feet.</summary>
    Controlled,

    /// <summary>A pass is already travelling; <see cref="MatchScenario.BallOwnerPlayerId"/> is the passer.</summary>
    InFlightPass,

    /// <summary>The ball is in the goalkeeper's hands (the same state a catch produces).</summary>
    KeeperHeld
}

/// <summary>
/// One footballer in a scenario. Everything the engine would otherwise derive from a shirt number and
/// a formation table is explicit here, so a fixture describes a starting state rather than a squad.
/// </summary>
public sealed class ScenarioPlayer
{
    public required int Id { get; init; }
    public required TeamId Team { get; init; }
    public required TacticalRole Role { get; init; }

    /// <summary>
    /// Formation slot key. The tactical anchor table is keyed by shirt number, so a scenario that
    /// cares about nominal shape should set it. Zero means "assign in registration order".
    /// </summary>
    public int ShirtNumber { get; init; }

    public Vector3 Position { get; init; }
    public Vector3 Velocity { get; init; }
    public Vector2 FacingXZ { get; init; } = new(1f, 0f);
    public float Stamina01 { get; init; } = 1f;

    /// <summary>Attributes for this player; the neutral defaults when not set.</summary>
    public PlayerAttributes Attributes { get; init; } = new();

    /// <summary>Role preset when null, so a fixture only states the differences it actually cares about.</summary>
    public PlayerTendencies? Tendencies { get; init; }

    public bool IsActiveOnPitch { get; init; } = true;

    /// <summary>
    /// Controller to install before the first tick. Null keeps the engine's default for the role
    /// (goalkeeper controller for keepers, <see cref="FootballSim.AI.TacticalUtilityAiController"/>
    /// otherwise). Scripted/fixed opponents use this.
    /// </summary>
    public IFootballerController? Controller { get; init; }

    public ScenarioPlayer WithAttributes(PlayerAttributes attributes) => new()
    {
        Id = Id, Team = Team, Role = Role, ShirtNumber = ShirtNumber, Position = Position,
        Velocity = Velocity, FacingXZ = FacingXZ, Stamina01 = Stamina01, Attributes = attributes,
        Tendencies = Tendencies, IsActiveOnPitch = IsActiveOnPitch, Controller = Controller
    };

    public ScenarioPlayer WithTendencies(PlayerTendencies tendencies) => new()
    {
        Id = Id, Team = Team, Role = Role, ShirtNumber = ShirtNumber, Position = Position,
        Velocity = Velocity, FacingXZ = FacingXZ, Stamina01 = Stamina01, Attributes = Attributes,
        Tendencies = tendencies, IsActiveOnPitch = IsActiveOnPitch, Controller = Controller
    };

    public ScenarioPlayer WithShirt(int shirtNumber) => new()
    {
        Id = Id, Team = Team, Role = Role, ShirtNumber = shirtNumber, Position = Position,
        Velocity = Velocity, FacingXZ = FacingXZ, Stamina01 = Stamina01, Attributes = Attributes,
        Tendencies = Tendencies, IsActiveOnPitch = IsActiveOnPitch, Controller = Controller
    };

    public ScenarioPlayer WithController(IFootballerController controller) => new()
    {
        Id = Id, Team = Team, Role = Role, ShirtNumber = ShirtNumber, Position = Position,
        Velocity = Velocity, FacingXZ = FacingXZ, Stamina01 = Stamina01, Attributes = Attributes,
        Tendencies = Tendencies, IsActiveOnPitch = IsActiveOnPitch, Controller = controller
    };

    public ScenarioPlayer MovedTo(Vector3 position) => new()
    {
        Id = Id, Team = Team, Role = Role, ShirtNumber = ShirtNumber, Position = position,
        Velocity = Velocity, FacingXZ = FacingXZ, Stamina01 = Stamina01, Attributes = Attributes,
        Tendencies = Tendencies, IsActiveOnPitch = IsActiveOnPitch, Controller = Controller
    };

    public ScenarioPlayer MovingAt(Vector3 velocity) => new()
    {
        Id = Id, Team = Team, Role = Role, ShirtNumber = ShirtNumber, Position = Position,
        Velocity = velocity, FacingXZ = FacingXZ, Stamina01 = Stamina01, Attributes = Attributes,
        Tendencies = Tendencies, IsActiveOnPitch = IsActiveOnPitch, Controller = Controller
    };
}

/// <summary>
/// A complete, explicit starting state for one match fragment: who is where, moving which way, with
/// what ball, in which phase, for how long.
///
/// The point of a scenario is reproducibility. <see cref="MatchSimulationEngine"/> builds its squads
/// from a formation table and a kickoff rule; a scenario overrides that entirely and starts the match
/// from exactly the described world, so a tactical assertion has a fixed subject.
///
/// Not modelled yet (deliberate — they belong to later milestones): per-player phase roles
/// (A3), persistent intent (A1/A4), and a starting scoreboard finer than a score.
/// </summary>
public sealed class MatchScenario
{
    public string Name { get; init; } = "scenario";

    /// <summary>Seed for the match-local RNG. Same seed + same scenario ⇒ same run.</summary>
    public ulong Seed { get; init; } = 20260818UL;

    public PitchDimensions Pitch { get; init; } = new();
    public TeamTacticalProfile HomeProfile { get; init; } = TeamTacticalProfile.PossessionControl;
    public TeamTacticalProfile AwayProfile { get; init; } = TeamTacticalProfile.DirectCounterattack;

    /// <summary>
    /// AI runtime settings for this fragment. A scenario that ablates a behaviour sets them here rather
    /// than mutating a controller, so the difference is part of the fixture and shows up in the artifact.
    /// </summary>
    public AiRuntimeConfig Ai { get; init; } = AiRuntimeConfig.Default;

    /// <summary>
    /// Half length used for full-time detection. Scenarios are short fragments, so the default is far
    /// longer than any scenario is meant to run — the runner stops on its own tick budget instead.
    /// </summary>
    public float HalfDurationSeconds { get; init; } = 3600f;

    /// <summary>Clock value the fragment starts at. Used for phase logic and diagnostics only.</summary>
    public float MatchClockSeconds { get; init; }

    public MatchPeriod Period { get; init; } = MatchPeriod.FirstHalf;
    public int HomeScore { get; init; }
    public int AwayScore { get; init; }

    /// <summary>Player the human is locked to, if the scenario exercises the human path.</summary>
    public int? HumanPlayerId { get; init; }

    public Vector3 BallPosition { get; init; }
    public Vector3 BallVelocity { get; init; }
    public Vector3 BallAngularVelocity { get; init; }
    public ScenarioBallSetup BallSetup { get; init; } = ScenarioBallSetup.Loose;

    /// <summary>Controlling player, passer, or keeper — the meaning depends on <see cref="BallSetup"/>.</summary>
    public int? BallOwnerPlayerId { get; init; }

    /// <summary>Intended receiver, for <see cref="ScenarioBallSetup.InFlightPass"/>.</summary>
    public int? IntendedReceiverId { get; init; }

    /// <summary>Sets the ball's last-touch team without inventing a contact event.</summary>
    public TeamId? BallLastTouchedByTeam { get; init; }

    public List<ScenarioPlayer> Players { get; } = new();

    public int HomeCount => Players.Count(p => p.Team == TeamId.Home);
    public int AwayCount => Players.Count(p => p.Team == TeamId.Away);

    /// <summary>Registers a player. Shirt numbers are assigned per team in registration order when unset.</summary>
    public MatchScenario Add(ScenarioPlayer player)
    {
        if (player is null) throw new ArgumentNullException(nameof(player));
        Players.Add(player);
        return this;
    }

    /// <summary>Replaces one player's spec in place, preserving registration order and shirt assignment.</summary>
    public MatchScenario WithPlayer(ScenarioPlayer player)
    {
        if (player is null) throw new ArgumentNullException(nameof(player));
        for (int i = 0; i < Players.Count; i++)
        {
            if (Players[i].Id != player.Id) continue;
            Players[i] = player;
            return this;
        }
        throw new ArgumentOutOfRangeException(nameof(player), player.Id,
            $"Player #{player.Id} is not part of scenario '{Name}'.");
    }

    /// <summary>Registers several players at once.</summary>
    public MatchScenario AddRange(IEnumerable<ScenarioPlayer> players)
    {
        foreach (ScenarioPlayer player in players) Add(player);
        return this;
    }

    /// <summary>Creates a player spec with the neutral attributes/tendency defaults.</summary>
    public static ScenarioPlayer Player(
        int id, TeamId team, TacticalRole role, Vector3 position, int shirtNumber = 0) => new()
    {
        Id = id, Team = team, Role = role, Position = position, ShirtNumber = shirtNumber
    };

    /// <summary>
    /// The configuration the engine runs the fragment with. Only the fields that still mean something
    /// for an explicit world are carried over — the squad is supplied by the scenario.
    /// </summary>
    public PreMatchConfig BuildConfig() => new()
    {
        Seed = Seed,
        PlayersPerTeam = Math.Clamp(Math.Max(HomeCount, AwayCount), 1, 11),
        HalfDurationSeconds = HalfDurationSeconds,
        DesignatedHumanPlayerId = HumanPlayerId,
        HomeProfile = HomeProfile,
        AwayProfile = AwayProfile,
        Ai = Ai
    };

    /// <summary>Builds the authoritative ball state this scenario starts from.</summary>
    public BallState CreateBallState(float ballRadius)
    {
        float restHeight = BallGeometry.RestingCentreHeight(ballRadius);
        Vector3 position = BallPosition;
        if (position.Y <= 0f) position = position with { Y = restHeight };

        return new BallState
        {
            Position = position,
            PreviousPosition = position,
            Velocity = BallVelocity,
            AngularVelocity = BallAngularVelocity,
            LastTouchedByTeam = BallLastTouchedByTeam ?? (BallOwnerPlayerId is int ownerId ? FindPlayer(ownerId).Team : null),
            LastTouchedByPlayerId = BallOwnerPlayerId
        };
    }

    /// <summary>The ownership snapshot a scenario starts from, or a loose ball.</summary>
    public BallPossessionSnapshot CreatePossessionSnapshot()
    {
        if (BallSetup == ScenarioBallSetup.Loose)
            return BallPossessionSnapshot.Loose;

        ScenarioPlayer owner = FindPlayer(BallOwnerPlayerId
            ?? throw new InvalidOperationException($"Scenario '{Name}' needs BallOwnerPlayerId for {BallSetup}."));

        return BallSetup switch
        {
            ScenarioBallSetup.Controlled => new BallPossessionSnapshot(
                BallControlState.Controlled, owner.Team, owner.Id, null),
            ScenarioBallSetup.KeeperHeld => new BallPossessionSnapshot(
                BallControlState.KeeperHeld, owner.Team, owner.Id, null),
            ScenarioBallSetup.InFlightPass => new BallPossessionSnapshot(
                BallControlState.InFlightPass, owner.Team, null, IntendedReceiverId),
            _ => BallPossessionSnapshot.Loose
        };
    }

    /// <summary>
    /// Rejects a scenario the engine could not actually start. Fails loudly at construction rather
    /// than producing a subtly different match that a tactical assertion would then blame on the AI.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("A scenario must be named.", nameof(Name));

        if (Players.Count < 2)
            throw new ArgumentException($"Scenario '{Name}' needs at least two players.", nameof(Players));

        if (HomeCount == 0 || AwayCount == 0)
            throw new ArgumentException(
                $"Scenario '{Name}' needs at least one player per team (home {HomeCount}, away {AwayCount}).",
                nameof(Players));

        if (HomeCount > SimulationConfigValidation.MaxPlayersPerTeam ||
            AwayCount > SimulationConfigValidation.MaxPlayersPerTeam)
        {
            throw new ArgumentException(
                $"Scenario '{Name}' has {Math.Max(HomeCount, AwayCount)} players on one team; the maximum is " +
                $"{SimulationConfigValidation.MaxPlayersPerTeam}.", nameof(Players));
        }

        var ids = new HashSet<int>();
        var shirtsPerTeam = new Dictionary<TeamId, HashSet<int>>();
        foreach (ScenarioPlayer player in Players)
        {
            if (!ids.Add(player.Id))
                throw new ArgumentException($"Scenario '{Name}' has two players with id {player.Id}.", nameof(Players));
            if (player.Id <= 0)
                throw new ArgumentException($"Scenario '{Name}' has a non-positive player id {player.Id}.", nameof(Players));

            if (!SimMath.IsFinite(player.Position) || !SimMath.IsFinite(player.Velocity))
                throw new ArgumentException(
                    $"Scenario '{Name}' player #{player.Id} has a non-finite position or velocity.", nameof(Players));

            if (player.Position.X < -Pitch.HalfLength || player.Position.X > Pitch.HalfLength ||
                player.Position.Z < -Pitch.HalfWidth || player.Position.Z > Pitch.HalfWidth)
            {
                throw new ArgumentException(
                    $"Scenario '{Name}' player #{player.Id} starts off the pitch at " +
                    $"({player.Position.X:F1}, {player.Position.Z:F1}).", nameof(Players));
            }

            if (player.Stamina01 < 0f || player.Stamina01 > 1f)
                throw new ArgumentException(
                    $"Scenario '{Name}' player #{player.Id} has stamina {player.Stamina01:F2}; expected 0..1.", nameof(Players));

            SimulationConfigValidation.ValidatePlayerAttributes(player.Attributes, $"Scenario player #{player.Id}");
            if (player.Tendencies is not null)
                SimulationConfigValidation.ValidateTendencies(player.Tendencies, $"Scenario player #{player.Id}");

            if (!shirtsPerTeam.TryGetValue(player.Team, out HashSet<int>? shirts))
            {
                shirts = new HashSet<int>();
                shirtsPerTeam[player.Team] = shirts;
            }
            if (player.ShirtNumber > 0 && !shirts.Add(player.ShirtNumber))
                throw new ArgumentException(
                    $"Scenario '{Name}' has two {player.Team} players with shirt #{player.ShirtNumber}.", nameof(Players));
        }

        if (!SimMath.IsFinite(BallPosition) || !SimMath.IsFinite(BallVelocity) ||
            !SimMath.IsFinite(BallAngularVelocity))
            throw new ArgumentException($"Scenario '{Name}' has a non-finite ball state.", nameof(BallPosition));

        if (HumanPlayerId is int humanId && !ids.Contains(humanId))
            throw new ArgumentException(
                $"Scenario '{Name}' locks the human to player #{humanId}, who is not in the scenario.", nameof(HumanPlayerId));

        if (BallSetup != ScenarioBallSetup.Loose)
        {
            if (BallOwnerPlayerId is not int ownerId || !ids.Contains(ownerId))
                throw new ArgumentException(
                    $"Scenario '{Name}' uses {BallSetup} without a valid BallOwnerPlayerId.", nameof(BallOwnerPlayerId));

            ScenarioPlayer owner = FindPlayer(BallOwnerPlayerId.Value);
            if (BallSetup is ScenarioBallSetup.KeeperHeld or ScenarioBallSetup.Controlled &&
                IsFarFromBall(owner))
            {
                throw new ArgumentException(
                    $"Scenario '{Name}' gives player #{owner.Id} control of a ball {(owner.Position - BallPosition).Length():F1} m away; " +
                    "the ownership model drops control beyond 3 m on the first tick.", nameof(BallPosition));
            }
        }

        if (IntendedReceiverId is int receiverId)
        {
            if (!ids.Contains(receiverId))
                throw new ArgumentException(
                    $"Scenario '{Name}' targets intended receiver #{receiverId}, who is not in the scenario.",
                    nameof(IntendedReceiverId));
            if (BallSetup == ScenarioBallSetup.Controlled && receiverId == BallOwnerPlayerId)
                throw new ArgumentException(
                    $"Scenario '{Name}' names the carrier as their own intended receiver.", nameof(IntendedReceiverId));
        }

        return;

        bool IsFarFromBall(ScenarioPlayer player)
        {
            float dx = player.Position.X - BallPosition.X;
            float dz = player.Position.Z - BallPosition.Z;
            return MathF.Sqrt(dx * dx + dz * dz) > 2.5f;
        }
    }

    /// <summary>Scenario player by id. Throws when the fixture refers to a player it never added.</summary>
    public ScenarioPlayer FindPlayer(int id)
    {
        foreach (ScenarioPlayer player in Players)
            if (player.Id == id) return player;
        throw new ArgumentOutOfRangeException(nameof(id), id, $"Scenario '{Name}' has no player #{id}.");
    }

    /// <summary>Assigns shirt numbers to players that did not specify one, in registration order.</summary>
    internal IReadOnlyList<ScenarioPlayer> ResolveShirtNumbers()
    {
        var nextShirt = new Dictionary<TeamId, int> { [TeamId.Home] = 1, [TeamId.Away] = 1 };
        var resolved = new ScenarioPlayer[Players.Count];
        var used = new Dictionary<TeamId, HashSet<int>>();

        // Reserve explicitly chosen numbers first so an auto-assigned number can never collide.
        foreach (ScenarioPlayer player in Players)
        {
            if (!used.TryGetValue(player.Team, out HashSet<int>? set))
            {
                set = new HashSet<int>();
                used[player.Team] = set;
            }
            if (player.ShirtNumber > 0) set.Add(player.ShirtNumber);
        }

        for (int i = 0; i < Players.Count; i++)
        {
            ScenarioPlayer player = Players[i];
            if (player.ShirtNumber > 0)
            {
                resolved[i] = player;
                continue;
            }

            int candidate = nextShirt[player.Team];
            while (!used[player.Team].Add(candidate)) candidate++;
            nextShirt[player.Team] = candidate + 1;
            resolved[i] = player.WithShirt(candidate);
        }

        return resolved;
    }
}
