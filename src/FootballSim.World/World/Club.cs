using FootballSim.Core;
using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Players;

namespace FootballSim.World.World;

/// <summary>Formations a manager may line a club up in.</summary>
public enum FormationKind
{
    /// <summary>Flat 4-4-2: two banks of four and two strikers (the balanced default).</summary>
    FourFourTwo = 0,

    /// <summary>4-3-3: one holding midfielder and two wide forwards around a striker.</summary>
    FourThreeThree = 1
}

/// <summary>
/// The one authoritative shape of a starting XI and bench per formation. The squad selector fills exactly
/// these slots, the quick-sim rates exactly these players, and the live-match bridge maps exactly these
/// roles, so the three systems cannot drift apart.
/// </summary>
public static class Formations
{
    /// <summary>Bench size (two keepers would be wasteful; one keeper plus six outfield players).</summary>
    public const int BenchSize = 7;

    private static readonly WorldRole[] FourFourTwoStarting =
    {
        WorldRole.Goalkeeper,
        WorldRole.FullBack,
        WorldRole.CenterBack,
        WorldRole.CenterBack,
        WorldRole.FullBack,
        WorldRole.Winger,
        WorldRole.CentralMidfielder,
        WorldRole.CentralMidfielder,
        WorldRole.Winger,
        WorldRole.Striker,
        WorldRole.Striker
    };

    private static readonly WorldRole[] FourThreeThreeStarting =
    {
        WorldRole.Goalkeeper,
        WorldRole.FullBack,
        WorldRole.CenterBack,
        WorldRole.CenterBack,
        WorldRole.FullBack,
        WorldRole.DefensiveMidfielder,
        WorldRole.CentralMidfielder,
        WorldRole.CentralMidfielder,
        WorldRole.Winger,
        WorldRole.Winger,
        WorldRole.Striker
    };

    private static readonly WorldRole[] BenchRolesArray =
    {
        WorldRole.Goalkeeper,
        WorldRole.CenterBack,
        WorldRole.FullBack,
        WorldRole.CentralMidfielder,
        WorldRole.Winger,
        WorldRole.Striker,
        WorldRole.CentralMidfielder
    };

    /// <summary>Eleven starting roles, in shirt-number order.</summary>
    public static IReadOnlyList<WorldRole> StartingRoles(FormationKind formation) => formation switch
    {
        FormationKind.FourFourTwo => FourFourTwoStarting,
        FormationKind.FourThreeThree => FourThreeThreeStarting,
        _ => throw new ArgumentOutOfRangeException(nameof(formation))
    };

    /// <summary>Seven bench slots, in substitution-priority order.</summary>
    public static IReadOnlyList<WorldRole> BenchRoles() => BenchRolesArray;

    /// <summary>Validates that a formation's slot list is a legal XI (one keeper).</summary>
    public static bool IsValid(FormationKind formation) => formation is
        FormationKind.FourFourTwo or FormationKind.FourThreeThree;
}

/// <summary>Club money. Values are in the country's minor unit (whole currency units for clarity).</summary>
public sealed class ClubFinances
{
    /// <summary>Cash balance.</summary>
    public long Balance { get; set; }

    /// <summary>Weekly wage budget the board allows.</summary>
    public long WeeklyWageBudget { get; set; }

    /// <summary>Transfer budget available in the current window.</summary>
    public long TransferBudget { get; set; }

    /// <summary>Current weekly wage bill (recomputed when contracts change).</summary>
    public long WeeklyWageBill { get; set; }

    /// <summary>True when the club can afford the given transfer fee and wage.</summary>
    public bool CanAfford(long transferFee, long weeklyWage) =>
        transferFee <= TransferBudget && WeeklyWageBill + weeklyWage <= WeeklyWageBudget;
}

/// <summary>
/// Club facilities. These are the concrete inputs growth, injuries and scouting read; they are club data,
/// not a separate system, so a mod can make a rich amateur club and see it matter.
/// </summary>
public sealed class ClubFacilities
{
    private float _coachingQuality01 = 1.00f;
    private float _medicalQuality01 = 0.50f;
    private float _youthQuality01 = 0.50f;
    private int _weeklyTrainingSessions = 3;

    /// <summary>Coaching quality 0.70..1.30 (plan §4.5).</summary>
    public float CoachingQuality01
    {
        get => _coachingQuality01;
        set => _coachingQuality01 = Math.Clamp(value, 0.70f, 1.30f);
    }

    /// <summary>Medical/recovery quality 0..1: shortens injuries and speeds condition recovery.</summary>
    public float MedicalQuality01
    {
        get => _medicalQuality01;
        set => _medicalQuality01 = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Youth-academy quality 0..1: raises the ceiling distribution of new intakes.</summary>
    public float YouthQuality01
    {
        get => _youthQuality01;
        set => _youthQuality01 = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Organised training sessions per week (1..6; amateur clubs train less).</summary>
    public int WeeklyTrainingSessions
    {
        get => _weeklyTrainingSessions;
        set => _weeklyTrainingSessions = Math.Clamp(value, 1, 6);
    }
}

/// <summary>
/// A club in the living world: identity, division, manager, squad and the money/facility model the
/// simulation systems read. The squad is a stable ordered id list; player objects live in the world's
/// player registry, so a club never duplicates mutable player state.
/// </summary>
public sealed class WorldClub
{
    private readonly List<PlayerId> _squad = new(30);
    private readonly Dictionary<PlayerId, float> _managerTrust = new();

    /// <summary>Creates a club from its data-pack definition plus world state (division, manager).</summary>
    public WorldClub(
        ClubId id,
        ClubDef definition,
        LeagueId leagueId,
        ManagerId managerId,
        ClubFacilities facilities,
        ClubFinances finances)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(facilities);
        ArgumentNullException.ThrowIfNull(finances);

        Id = id;
        Name = definition.Name;
        ShortCode = definition.ShortCode;
        City = definition.City;
        Stadium = definition.Stadium;
        Capacity = definition.Capacity;
        PrimaryColor = definition.PrimaryColor;
        SecondaryColor = definition.SecondaryColor;
        Founded = definition.Founded ?? 0;
        Tier = definition.Tier;
        LeagueId = leagueId;
        ManagerId = managerId;
        Reputation = definition.Reputation;
        Facilities = facilities;
        Finances = finances;
    }

    /// <summary>Stable one-based identity.</summary>
    public ClubId Id { get; }

    public string Name { get; }
    public string ShortCode { get; }
    public string City { get; }
    public string Stadium { get; }
    public int Capacity { get; }
    public RgbColor PrimaryColor { get; }
    public RgbColor SecondaryColor { get; }
    public string PrimaryColorHex => PrimaryColor.ToHex();
    public string SecondaryColorHex => SecondaryColor.ToHex();
    public int Founded { get; }

    /// <summary>Base club reputation 1..100 (data pack; W3 may nudge it).</summary>
    public int Reputation { get; set; }

    /// <summary>Current division tier, 1 (top) .. 6 (amateur).</summary>
    public int Tier { get; set; }

    /// <summary>The league the club plays in this season.</summary>
    public LeagueId LeagueId { get; set; }

    /// <summary>Current manager.</summary>
    public ManagerId ManagerId { get; set; }

    /// <summary>Money model.</summary>
    public ClubFinances Finances { get; }

    /// <summary>Facility model.</summary>
    public ClubFacilities Facilities { get; }

    /// <summary>Squad in selection order (registration order, not strength order).</summary>
    public IReadOnlyList<PlayerId> Squad => _squad;

    /// <summary>Weekly growth environment this club gives its players.</summary>
    public TrainingEnvironment TrainingEnvironment =>
        new(Tier, Facilities.CoachingQuality01, Facilities.WeeklyTrainingSessions);

    /// <summary>Adds a player to the squad (idempotent).</summary>
    public void AddPlayer(PlayerId playerId)
    {
        if (!playerId.IsValid) throw new ArgumentOutOfRangeException(nameof(playerId));
        if (!_squad.Contains(playerId)) _squad.Add(playerId);
        _managerTrust.TryAdd(playerId, 0.50f);
    }

    /// <summary>Removes a player from the squad and its trust table.</summary>
    public bool RemovePlayer(PlayerId playerId)
    {
        _managerTrust.Remove(playerId);
        return _squad.Remove(playerId);
    }

    /// <summary>Manager trust in a squad player, 0 (frozen out) .. 1 (favourite); 0.5 when unknown.</summary>
    public float ManagerTrustOf(PlayerId playerId) =>
        _managerTrust.TryGetValue(playerId, out float trust) ? trust : 0.50f;

    /// <summary>Sets manager trust for a squad player.</summary>
    public void SetManagerTrust(PlayerId playerId, float trust01) =>
        _managerTrust[playerId] = Math.Clamp(trust01, 0f, 1f);

    /// <summary>Roster trust table (selection inputs; save/load and W3 manager feedback read it).</summary>
    public IReadOnlyDictionary<PlayerId, float> ManagerTrust => _managerTrust;

    /// <inheritdoc />
    public override string ToString() => $"{Name} [{ShortCode}] (tier {Tier})";
}
