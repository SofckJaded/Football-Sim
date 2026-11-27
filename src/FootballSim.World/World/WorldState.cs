using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Players;

namespace FootballSim.World.World;

/// <summary>
/// Ordered registry of world players. The interface is a list plus a lookup: iteration order is stable
/// (registration order) so simulation logic never depends on hash order, while lookups stay O(1).
/// </summary>
public sealed class PlayerRegistry
{
    private readonly List<WorldPlayer> _players = new(2600);
    private readonly Dictionary<PlayerId, WorldPlayer> _byId = new(2600);

    /// <summary>Every player, in stable registration order.</summary>
    public IReadOnlyList<WorldPlayer> All => _players;

    /// <summary>Number of players (including retired players retained for history).</summary>
    public int Count => _players.Count;

    /// <summary>Looks a player up by id; throws when absent.</summary>
    public WorldPlayer this[PlayerId id] =>
        _byId.TryGetValue(id, out WorldPlayer? player)
            ? player
            : throw new KeyNotFoundException($"No player with id {id}.");

    /// <summary>Adds a player; ids must be unique.</summary>
    public void Add(WorldPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!player.Id.IsValid) throw new ArgumentException("Player id must be assigned before adding.", nameof(player));
        if (_byId.ContainsKey(player.Id))
            throw new InvalidOperationException($"Player id {player.Id} is already registered.");
        _byId.Add(player.Id, player);
        _players.Add(player);
    }

    /// <summary>Looks a player up without throwing.</summary>
    public bool TryGet(PlayerId id, out WorldPlayer? player) => _byId.TryGetValue(id, out player);

    /// <summary>True when the id is registered.</summary>
    public bool Contains(PlayerId id) => _byId.ContainsKey(id);

    /// <summary>Players of a club, in the club's squad order.</summary>
    public IEnumerable<WorldPlayer> SquadOf(WorldClub club)
    {
        ArgumentNullException.ThrowIfNull(club);
        foreach (PlayerId id in club.Squad)
        {
            if (_byId.TryGetValue(id, out WorldPlayer? player)) yield return player;
        }
    }
}

/// <summary>Ordered registry of world clubs.</summary>
public sealed class ClubRegistry
{
    private readonly List<WorldClub> _clubs = new(108);
    private readonly Dictionary<ClubId, WorldClub> _byId = new(108);

    /// <summary>Every club, in stable registration order (tier order from the pack).</summary>
    public IReadOnlyList<WorldClub> All => _clubs;

    /// <summary>Number of clubs.</summary>
    public int Count => _clubs.Count;

    /// <summary>Looks a club up by id; throws when absent.</summary>
    public WorldClub this[ClubId id] =>
        _byId.TryGetValue(id, out WorldClub? club)
            ? club
            : throw new KeyNotFoundException($"No club with id {id}.");

    /// <summary>Adds a club; ids must be unique.</summary>
    public void Add(WorldClub club)
    {
        ArgumentNullException.ThrowIfNull(club);
        if (_byId.ContainsKey(club.Id)) throw new InvalidOperationException($"Club id {club.Id} is already registered.");
        _byId.Add(club.Id, club);
        _clubs.Add(club);
    }

    /// <summary>Looks a club up without throwing.</summary>
    public bool TryGet(ClubId id, out WorldClub? club) => _byId.TryGetValue(id, out club);

    /// <summary>Clubs currently in one division tier, in registration order.</summary>
    public IReadOnlyList<WorldClub> InTier(int tier)
    {
        var result = new List<WorldClub>(18);
        foreach (WorldClub club in _clubs)
        {
            if (club.Tier == tier) result.Add(club);
        }
        return result;
    }
}

/// <summary>Ordered registry of world managers.</summary>
public sealed class ManagerRegistry
{
    private readonly List<WorldManager> _managers = new(108);
    private readonly Dictionary<ManagerId, WorldManager> _byId = new(108);

    /// <summary>Every manager, in stable registration order.</summary>
    public IReadOnlyList<WorldManager> All => _managers;

    /// <summary>Number of managers.</summary>
    public int Count => _managers.Count;

    /// <summary>Looks a manager up by id; throws when absent.</summary>
    public WorldManager this[ManagerId id] =>
        _byId.TryGetValue(id, out WorldManager? manager)
            ? manager
            : throw new KeyNotFoundException($"No manager with id {id}.");

    /// <summary>Adds a manager; ids must be unique.</summary>
    public void Add(WorldManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (_byId.ContainsKey(manager.Id)) throw new InvalidOperationException($"Manager id {manager.Id} is already registered.");
        _byId.Add(manager.Id, manager);
        _managers.Add(manager);
    }

    /// <summary>Looks a manager up without throwing.</summary>
    public bool TryGet(ManagerId id, out WorldManager? manager) => _byId.TryGetValue(id, out manager);
}

/// <summary>
/// The whole living world for one save: country pack, clubs, managers, players, the six leagues, the
/// domestic cup, the calendar position and the named random streams.
///
/// There is exactly one mutable world (the project's "one world" rule): results live on fixtures, squads
/// live on players, and every system reads/writes this object rather than a parallel model. The random
/// streams live here too, so a save is (state, stream states) and reloading resumes the identical future.
/// </summary>
public sealed class WorldState
{
    private readonly Dictionary<WorldRandomStream, WorldRng> _streams = new();
    private readonly List<WorldLeague> _leagues = new(6);
    private readonly List<TransferRecord> _transferLog = new(1024);
    private int _nextPlayerId;
    private int _nextMatchId;
    private int _nextManagerId;

    /// <summary>Creates an empty world positioned at a date.</summary>
    public WorldState(WorldPack pack, ulong seed, WorldDate startDate)
    {
        Pack = pack ?? throw new ArgumentNullException(nameof(pack));
        Seed = seed;
        Date = startDate;
        Players = new PlayerRegistry();
        Clubs = new ClubRegistry();
        Managers = new ManagerRegistry();
        Cup = new WorldCup($"{pack.Country.Name} National Cup", startDate.Season);
    }

    /// <summary>The data pack this world was generated from (country, names, definitions).</summary>
    public WorldPack Pack { get; }

    /// <summary>Country definition of the pack.</summary>
    public CountryDef Country => Pack.Country;

    /// <summary>Root world seed: every stream and every generation seed derives from it.</summary>
    public ulong Seed { get; }

    /// <summary>Current calendar position.</summary>
    public WorldDate Date { get; set; }

    /// <summary>Current season number.</summary>
    public int Season => Date.Season;

    /// <summary>Current week 1..52.</summary>
    public int Week => Date.Week;

    /// <summary>All players in the world.</summary>
    public PlayerRegistry Players { get; }

    /// <summary>All clubs in the world.</summary>
    public ClubRegistry Clubs { get; }

    /// <summary>All managers in the world.</summary>
    public ManagerRegistry Managers { get; }

    /// <summary>The six divisions, ordered by tier.</summary>
    public IReadOnlyList<WorldLeague> Leagues => _leagues;

    /// <summary>The domestic knockout cup for the current season.</summary>
    public WorldCup Cup { get; }

    /// <summary>
    /// Append-only transfer/retirement/youth-intake log for the whole world. It is the audit trail the
    /// soak test asserts on and the career layer turns into news; it is kept on the world, not in a
    /// system, so a save carries it.
    /// </summary>
    public IReadOnlyList<TransferRecord> TransferLog => _transferLog;

    /// <summary>Appends one transfer-log entry (W3 transfer market / lifecycle).</summary>
    public void RecordTransfer(TransferRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _transferLog.Add(record);
    }

    /// <summary>Number of transfer-log entries matching a kind (soak-test volume checks).</summary>
    public int CountTransfers(TransferKind kind)
    {
        int count = 0;
        for (int i = 0; i < _transferLog.Count; i++)
        {
            if (_transferLog[i].Kind == kind) count++;
        }
        return count;
    }

    /// <summary>Registers a league (called once, in tier order, by the world generator).</summary>
    public void AddLeague(WorldLeague league)
    {
        ArgumentNullException.ThrowIfNull(league);
        if (_leagues.Count > 0 && league.Tier <= _leagues[^1].Tier)
            throw new InvalidOperationException("Leagues must be registered in ascending tier order.");
        _leagues.Add(league);
    }

    /// <summary>League for a division tier; throws when the tier is not registered.</summary>
    public WorldLeague LeagueForTier(int tier)
    {
        foreach (WorldLeague league in _leagues)
        {
            if (league.Tier == tier) return league;
        }
        throw new KeyNotFoundException($"No league registered for tier {tier}.");
    }

    /// <summary>The world's named random stream, created on first use from the root seed.</summary>
    public WorldRng Stream(WorldRandomStream stream)
    {
        if (_streams.TryGetValue(stream, out WorldRng? existing)) return existing;
        WorldRng created = WorldRng.For(Seed, stream);
        _streams[stream] = created;
        return created;
    }

    /// <summary>Captures the current state of every stream that has been used (save data).</summary>
    public IReadOnlyDictionary<WorldRandomStream, ulong> CaptureRngStates()
    {
        var result = new Dictionary<WorldRandomStream, ulong>(_streams.Count);
        foreach (KeyValuePair<WorldRandomStream, WorldRng> pair in _streams)
        {
            result[pair.Key] = pair.Value.State;
        }
        return result;
    }

    /// <summary>Restores stream states from a save (streams not present keep their derived seed position).</summary>
    public void RestoreRngStates(IReadOnlyDictionary<WorldRandomStream, ulong> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        foreach (KeyValuePair<WorldRandomStream, ulong> pair in states)
        {
            _streams[pair.Key] = new WorldRng(pair.Value);
        }
    }

    /// <summary>Allocates the next player id (one-based, never reused).</summary>
    public PlayerId AllocatePlayerId() => new(++_nextPlayerId);

    /// <summary>Allocates the next match id (one-based, never reused).</summary>
    public MatchId AllocateMatchId() => new(++_nextMatchId);

    /// <summary>Allocates the next manager id (one-based, never reused).</summary>
    public ManagerId AllocateManagerId() => new(++_nextManagerId);

    /// <summary>Sets the id counters when loading a save (ids continue above the saved maximum).</summary>
    public void RestoreIdCounters(int lastPlayerId, int lastMatchId, int lastManagerId)
    {
        _nextPlayerId = Math.Max(_nextPlayerId, lastPlayerId);
        _nextMatchId = Math.Max(_nextMatchId, lastMatchId);
        _nextManagerId = Math.Max(_nextManagerId, lastManagerId);
    }

    /// <summary>Every fixture of the current season (all six leagues plus every resolved cup round).</summary>
    public IEnumerable<WorldFixture> CurrentSeasonFixtures()
    {
        foreach (WorldLeague league in _leagues)
        {
            foreach (WorldFixture fixture in league.Fixtures) yield return fixture;
        }
        foreach (CupRoundBracket round in Cup.Rounds)
        {
            foreach (CupTie tie in round.Ties) yield return tie.Fixture;
        }
    }

    /// <summary>Convenience: club by id.</summary>
    public WorldClub Club(ClubId id) => Clubs[id];

    /// <summary>Convenience: player by id.</summary>
    public WorldPlayer Player(PlayerId id) => Players[id];

    /// <inheritdoc />
    public override string ToString() =>
        $"World '{Pack.Country.Name}' S{Date.Season}W{Date.Week:00}: {Clubs.Count} clubs, {Players.Count} players";
}
