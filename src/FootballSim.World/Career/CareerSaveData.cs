using FootballSim.Core;
using FootballSim.World.Core;
using FootballSim.World.Players;
using FootballSim.World.World;

namespace FootballSim.World.Career;

/// <summary>
/// The career half of a save file (plan stage W4): creation config, identity, inbox, ledger, the career
/// markers and the runner/ecosystem counters. It is plain mutable data with primitive members only, so the
/// save layer can serialize it with <c>System.Text.Json</c> without converters, and it deliberately carries
/// no world objects — the world has its own snapshot.
/// </summary>
public sealed class CareerSaveData
{
    /// <summary>A save schema version (the file-level version lives on the save manager; this one guards the
    /// career DTO's own shape as the plan's W4+ polish evolves it).</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>The creation request.</summary>
    public CareerConfigSave Config { get; set; } = new();

    /// <summary>The career player's id inside the world.</summary>
    public int HumanPlayerId { get; set; }

    /// <summary>Next inbox message id to hand out.</summary>
    public int NextMessageId { get; set; } = 1;

    /// <summary>Club tier at the start of the current season (honours/division-movement context).</summary>
    public int SeasonStartClubTier { get; set; } = 6;

    /// <summary>True when this season's contract-renewal offer has already been sent.</summary>
    public bool RenewalOfferCreated { get; set; }

    /// <summary>Season of the last generated transfer-offer window (-1 = never).</summary>
    public int LastOfferSeason { get; set; } = -1;

    /// <summary>Week of the last generated transfer-offer window.</summary>
    public int LastOfferWindowWeek { get; set; }

    /// <summary>Season of the last mid-season coach report (-1 = none yet).</summary>
    public int LastMidSeasonReportSeason { get; set; } = -1;

    /// <summary>Highest cup round the career has already announced.</summary>
    public int LastCupRoundSeen { get; set; }

    /// <summary>True when the current injury has already been announced.</summary>
    public bool InjuryNotified { get; set; }

    /// <summary>Season whose automatic training focuses the runner has stamped.</summary>
    public int RunnerFocusSeason { get; set; } = -1;

    /// <summary>Player ids the runner has already given an automatic focus this season.</summary>
    public List<int> RunnerFocusSetPlayerIds { get; set; } = new();

    /// <summary>Runner injury counter for the season summary.</summary>
    public int RunnerInjuriesThisSeason { get; set; }

    /// <summary>Runner yellow-card counter for the season summary.</summary>
    public int RunnerYellowCardsThisSeason { get; set; }

    /// <summary>Runner red-card counter for the season summary.</summary>
    public int RunnerRedCardsThisSeason { get; set; }

    /// <summary>True once the ecosystem created the world's opening contracts.</summary>
    public bool EcosystemInitialized { get; set; }

    /// <summary>Transfer-log length at the ecosystem's previous rollover.</summary>
    public int EcosystemTransferLogCursor { get; set; }

    /// <summary>Ecosystem medical recoveries since the previous rollover.</summary>
    public int EcosystemInjuriesRecoveredSinceRollover { get; set; }

    /// <summary>Ecosystem emergency-intake generation counter.</summary>
    public int EcosystemEmergencySequence { get; set; }

    /// <summary>The inbox, in creation order.</summary>
    public List<CareerInboxMessageSave> Messages { get; set; } = new();

    /// <summary>The completed season ledger, oldest first.</summary>
    public List<CareerSeasonSave> Seasons { get; set; } = new();

    /// <summary>The player's own transfer history, oldest first.</summary>
    public List<CareerTransferSave> Transfers { get; set; } = new();
}

/// <summary>Serializable form of <see cref="CareerConfig"/>.</summary>
public sealed class CareerConfigSave
{
    public int Mode { get; set; }
    public ulong WorldSeed { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;

    /// <summary>Chosen foot, or null for the generated one.</summary>
    public int? PreferredFoot { get; set; }

    public int StartingAge { get; set; }
    public int Backstory { get; set; }
    public bool PreferGoalkeeper { get; set; }

    /// <summary>True when the custom slider aptitudes (rather than a preset) were chosen.</summary>
    public bool HasCustomAptitudes { get; set; }

    public float CustomAthleticism { get; set; }
    public float CustomTechnical { get; set; }
    public float CustomMental { get; set; }
    public float CustomGoalkeeping { get; set; }

    /// <summary>Custom preset, or null when the sliders were used.</summary>
    public int? Potential { get; set; }

    public int StartingClubId { get; set; }
    public int InitialTrainingFocus { get; set; }
    public bool ShowHiddenPotential { get; set; }

    /// <summary>Captures a creation request.</summary>
    /// <param name="config">The request to capture.</param>
    public static CareerConfigSave FromConfig(CareerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new CareerConfigSave
        {
            Mode = (int)config.Mode,
            WorldSeed = config.WorldSeed,
            FirstName = config.FirstName,
            LastName = config.LastName,
            PreferredFoot = config.PreferredFoot is { } foot ? (int)foot : null,
            StartingAge = config.StartingAge,
            Backstory = (int)config.Backstory,
            PreferGoalkeeper = config.PreferGoalkeeper,
            HasCustomAptitudes = config.CustomAptitudes is not null,
            CustomAthleticism = config.CustomAptitudes?.Athleticism ?? 0f,
            CustomTechnical = config.CustomAptitudes?.Technical ?? 0f,
            CustomMental = config.CustomAptitudes?.Mental ?? 0f,
            CustomGoalkeeping = config.CustomAptitudes?.Goalkeeping ?? 0f,
            Potential = config.Potential is { } preset ? (int)preset : null,
            StartingClubId = config.StartingClubId.Value,
            InitialTrainingFocus = (int)config.InitialTrainingFocus,
            ShowHiddenPotential = config.ShowHiddenPotential
        };
    }

    /// <summary>Rebuilds the creation request.</summary>
    /// <returns>The creation request stored in the save.</returns>
    /// <exception cref="InvalidOperationException">A stored value is not a defined enum member.</exception>
    public CareerConfig ToConfig()
    {
        if (!Enum.IsDefined((CareerCreationMode)Mode))
        {
            throw new InvalidOperationException($"The save has an unknown career creation mode {Mode}.");
        }

        if (!Enum.IsDefined((BackstoryKind)Backstory))
        {
            throw new InvalidOperationException($"The save has an unknown backstory {Backstory}.");
        }

        if (PreferredFoot is { } footValue && !Enum.IsDefined((Footedness)footValue))
        {
            throw new InvalidOperationException($"The save has an unknown foot {footValue}.");
        }

        if (Potential is { } potentialValue && !Enum.IsDefined((PotentialPreset)potentialValue))
        {
            throw new InvalidOperationException($"The save has an unknown potential preset {potentialValue}.");
        }

        if (!Enum.IsDefined((TrainingFocus)InitialTrainingFocus))
        {
            throw new InvalidOperationException($"The save has an unknown training focus {InitialTrainingFocus}.");
        }

        return new CareerConfig
        {
            Mode = (CareerCreationMode)Mode,
            WorldSeed = WorldSeed,
            FirstName = FirstName ?? string.Empty,
            LastName = LastName ?? string.Empty,
            PreferredFoot = PreferredFoot is { } storedFoot ? (Footedness)storedFoot : null,
            StartingAge = StartingAge,
            Backstory = (BackstoryKind)Backstory,
            PreferGoalkeeper = PreferGoalkeeper,
            CustomAptitudes = HasCustomAptitudes
                ? new LatentAptitudes(CustomAthleticism, CustomTechnical, CustomMental, CustomGoalkeeping)
                : null,
            Potential = Potential is { } storedPotential ? (PotentialPreset)storedPotential : null,
            StartingClubId = new ClubId(StartingClubId),
            InitialTrainingFocus = (TrainingFocus)InitialTrainingFocus,
            ShowHiddenPotential = ShowHiddenPotential
        };
    }
}

/// <summary>
/// One flattened inbox message. The runtime inbox is a typed hierarchy; the save format flattens it into one
/// record with the union of the payload members plus <see cref="CareerInboxKind"/>, so a new message family
/// is an additive field rather than a polymorphism converter.
/// </summary>
public sealed class CareerInboxMessageSave
{
    public int Id { get; set; }
    public int Kind { get; set; }
    public int Season { get; set; }
    public int Week { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public bool IsResolved { get; set; }
    public int ExpiresSeason { get; set; }
    public int ExpiresWeek { get; set; }

    /// <summary>Coach-report summary, or a manager-feedback verdict.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Coach-report sentences or award names.</summary>
    public string[] Details { get; set; } = Array.Empty<string>();

    /// <summary>Transfer/loan from club.</summary>
    public int FromClubId { get; set; }

    /// <summary>Transfer/loan/trial/renewal club.</summary>
    public int ToClubId { get; set; }

    /// <summary>Fee (transfers).</summary>
    public long Fee { get; set; }

    /// <summary>Weekly wage (transfers, trials, renewals).</summary>
    public long WeeklyWage { get; set; }

    /// <summary><see cref="TransferKind"/> ordinal for transfer offers; -1 otherwise.</summary>
    public int OfferKind { get; set; } = -1;

    /// <summary>Loan end season (loans).</summary>
    public int LoanEndSeason { get; set; }

    /// <summary>Contract length in seasons (renewals).</summary>
    public int Seasons { get; set; }

    /// <summary><see cref="InjuryKind"/> ordinal for injury notices; -1 otherwise.</summary>
    public int InjuryKindValue { get; set; } = -1;

    /// <summary><see cref="InjurySeverity"/> ordinal for injury notices; -1 otherwise.</summary>
    public int InjurySeverityValue { get; set; } = -1;

    /// <summary>Weeks out (injury notices).</summary>
    public int WeeksOut { get; set; }

    /// <summary><see cref="CupRound"/> ordinal for cup draws; -1 otherwise.</summary>
    public int CupRoundValue { get; set; } = -1;

    /// <summary>True when the drawn cup tie is at home.</summary>
    public bool IsHome { get; set; }

    /// <summary>Week the drawn cup tie is played.</summary>
    public int MatchWeek { get; set; }
}

/// <summary>Serializable form of one <see cref="CareerSeasonRecord"/>.</summary>
public sealed class CareerSeasonSave
{
    public int Season { get; set; }
    public int ClubId { get; set; }
    public string ClubName { get; set; } = string.Empty;
    public int Tier { get; set; }
    public int Appearances { get; set; }
    public int Starts { get; set; }
    public int SubstituteAppearances { get; set; }
    public int MinutesPlayed { get; set; }
    public int Goals { get; set; }
    public int Assists { get; set; }
    public int YellowCards { get; set; }
    public int RedCards { get; set; }
    public float AverageRating { get; set; }
    public string[] Honours { get; set; } = Array.Empty<string>();
    public string DivisionMovement { get; set; } = string.Empty;
}

/// <summary>Serializable form of one <see cref="CareerTransferRecord"/>.</summary>
public sealed class CareerTransferSave
{
    public int Season { get; set; }
    public int Week { get; set; }
    public int FromClubId { get; set; }
    public int ToClubId { get; set; }
    public string FromClubName { get; set; } = string.Empty;
    public string ToClubName { get; set; } = string.Empty;
    public int Kind { get; set; }
    public long Fee { get; set; }
}
