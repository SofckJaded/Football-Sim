using FootballSim.Core;
using FootballSim.World.Core;

namespace FootballSim.World.World;

/// <summary>
/// A club manager: the tactical style his team plays, the formation he picks, and the board/performance
/// state that the manager carousel (W3) will use to keep or sack him.
/// </summary>
public sealed class WorldManager
{
    /// <summary>Creates a manager.</summary>
    public WorldManager(
        ManagerId id,
        string firstName,
        string lastName,
        int age,
        ClubId clubId,
        TeamTacticalProfile tacticalProfile,
        FormationKind formation,
        int reputation)
    {
        ArgumentNullException.ThrowIfNull(tacticalProfile);
        if (!Formations.IsValid(formation)) throw new ArgumentOutOfRangeException(nameof(formation));

        Id = id;
        FirstName = firstName ?? string.Empty;
        LastName = lastName ?? string.Empty;
        Age = age;
        ClubId = clubId;
        TacticalProfile = tacticalProfile;
        Formation = formation;
        Reputation = reputation;
    }

    /// <summary>Stable one-based identity.</summary>
    public ManagerId Id { get; }

    public string FirstName { get; }
    public string LastName { get; }

    /// <summary>Full display name.</summary>
    public string DisplayName =>
        LastName.Length == 0 ? FirstName : FirstName.Length == 0 ? LastName : $"{FirstName} {LastName}";

    /// <summary>Manager age in years (30..75); managers retire/are replaced by the carousel.</summary>
    public int Age
    {
        get => _age;
        set => _age = Math.Clamp(value, 30, 75);
    }

    private int _age;

    /// <summary>Club he currently manages.</summary>
    public ClubId ClubId { get; set; }

    /// <summary>The style his team plays (the match engine's one tactical-profile vocabulary).</summary>
    public TeamTacticalProfile TacticalProfile { get; set; }

    /// <summary>The formation he selects.</summary>
    public FormationKind Formation { get; set; }

    /// <summary>Reputation 1..100: the calibre of club that will consider him.</summary>
    public int Reputation
    {
        get => _reputation;
        set => _reputation = Math.Clamp(value, 1, 100);
    }

    private int _reputation;

    /// <summary>Board confidence 0 (about to be sacked) .. 1 (untouchable).</summary>
    public float BoardConfidence01
    {
        get => _boardConfidence01;
        set => _boardConfidence01 = Math.Clamp(value, 0f, 1f);
    }

    private float _boardConfidence01 = 0.60f;

    /// <summary>Seasons at the current club (W3 carousel input).</summary>
    public int SeasonsAtClub { get; set; }

    /// <summary>League matches in charge (W3 carousel input).</summary>
    public int MatchesInCharge { get; set; }

    /// <summary>Wins in charge.</summary>
    public int Wins { get; set; }

    /// <summary>Draws in charge.</summary>
    public int Draws { get; set; }

    /// <summary>Losses in charge.</summary>
    public int Losses { get; set; }

    /// <summary>Records one match result for the manager carousel.</summary>
    public void RecordResult(int goalsFor, int goalsAgainst)
    {
        MatchesInCharge++;
        if (goalsFor > goalsAgainst) Wins++;
        else if (goalsFor == goalsAgainst) Draws++;
        else Losses++;
    }

    /// <inheritdoc />
    public override string ToString() => $"{DisplayName} ({TacticalProfile.Name})";
}
