using FootballSim.Core;
using FootballSim.World.Core;
using FootballSim.World.Players;

namespace FootballSim.World.Career;

/// <summary>Which creation flow produced the career player (plan §4.1 "Two Creation Modes, One Engine").</summary>
public enum CareerCreationMode
{
    /// <summary>Default: the talent is rolled from the population pyramid and the ceiling stays hidden.</summary>
    Authentic = 0,

    /// <summary>The player configures potential tier/aptitudes and the academy; the engine is the same.</summary>
    Custom = 1
}

/// <summary>
/// The Custom-mode potential presets (plan §4.1). Each maps onto the same three latent aptitudes the
/// Authentic generator rolls, so a preset never bypasses the shared ceiling/realization pipeline.
/// </summary>
public enum PotentialPreset
{
    /// <summary>Amateur/semi-pro ceiling; most of the football pyramid.</summary>
    Journeyman = 0,

    /// <summary>Solid lower-pro ceiling.</summary>
    RegionalStandout = 1,

    /// <summary>Top-flight prospect ceiling.</summary>
    ProProspect = 2,

    /// <summary>Top-flight star ceiling.</summary>
    TopFlightStar = 3,

    /// <summary>Generational ceiling (clamped by the same 99 attribute cap as everyone else).</summary>
    Generational = 4
}

/// <summary>
/// Everything the frontend (CLI or Godot) supplies to start a career (plan stage W4). The config is a pure
/// creation request: it is validated once, then stored on the session and saved with it so a reloaded career
/// still knows which mode produced the player and whether potential is visible.
///
/// <para><b>Backstory never changes ceilings.</b> The config carries a backstory, but the generator applies
/// it only after the talent/ceiling draws (plan §4.4 hard invariant), exactly as it does for AI players.</para>
/// </summary>
public sealed record CareerConfig
{
    /// <summary>Youngest supported career starting age (plan §4.4).</summary>
    public const int MinimumStartingAge = 16;

    /// <summary>Oldest supported career starting age (plan §4.4).</summary>
    public const int MaximumStartingAge = 22;

    /// <summary>
    /// Latent aptitudes selected by each Custom-mode preset: (Athleticism, Technical, Mental). The
    /// goalkeeper aptitude is derived in <see cref="ResolveAptitudes"/> so a keeper preset keeps a keeper's
    /// hands rather than a low outfielder roll.
    /// </summary>
    private static readonly Dictionary<PotentialPreset, (float Athleticism, float Technical, float Mental)>
        PresetAptitudes = new()
        {
            [PotentialPreset.Journeyman] = (42f, 40f, 43f),
            [PotentialPreset.RegionalStandout] = (54f, 52f, 55f),
            [PotentialPreset.ProProspect] = (65f, 64f, 66f),
            [PotentialPreset.TopFlightStar] = (77f, 75f, 77f),
            [PotentialPreset.Generational] = (90f, 88f, 89f)
        };

    /// <summary>Root world seed; the whole world and the player's career streams derive from it.</summary>
    public ulong WorldSeed { get; init; } = 20261127UL;

    /// <summary>Authentic (hidden, rolled) or Custom (chosen potential).</summary>
    public CareerCreationMode Mode { get; init; } = CareerCreationMode.Authentic;

    /// <summary>Career player's given name (required).</summary>
    public string FirstName { get; init; } = string.Empty;

    /// <summary>Career player's family name (required).</summary>
    public string LastName { get; init; } = string.Empty;

    /// <summary>Chosen strong foot, or null to keep the generated foot.</summary>
    public Footedness? PreferredFoot { get; init; }

    /// <summary>Starting age 16..22; it sets the development runway (plan §4.4).</summary>
    public int StartingAge { get; init; } = 17;

    /// <summary>Academy/backstory preset; it changes realization, tilts and personality, never a ceiling.</summary>
    public BackstoryKind Backstory { get; init; } = BackstoryKind.LocalClubYouth;

    /// <summary>True when the career player is created as a goalkeeper.</summary>
    public bool PreferGoalkeeper { get; init; }

    /// <summary>Custom-mode aptitudes picked with the sliders (overrides <see cref="Potential"/> when set).</summary>
    public LatentAptitudes? CustomAptitudes { get; init; }

    /// <summary>Custom-mode preset; defaults to <see cref="PotentialPreset.RegionalStandout"/> when neither
    /// this nor <see cref="CustomAptitudes"/> is set.</summary>
    public PotentialPreset? Potential { get; init; }

    /// <summary>Preferred starting club, or <see cref="ClubId.None"/> to let the career pick an amateur club.</summary>
    public ClubId StartingClubId { get; init; } = ClubId.None;

    /// <summary>Training focus the career starts on (the user may change it any week).</summary>
    public TrainingFocus InitialTrainingFocus { get; init; } = TrainingFocus.Balanced;

    /// <summary>Authentic mode never renders hidden numbers; a Custom career may opt to reveal them.</summary>
    public bool ShowHiddenPotential { get; init; }

    /// <summary>Display name of the career player.</summary>
    public string DisplayName
    {
        get
        {
            string first = FirstName?.Trim() ?? string.Empty;
            string last = LastName?.Trim() ?? string.Empty;
            if (first.Length == 0) return last;
            return last.Length == 0 ? first : $"{first} {last}";
        }
    }

    /// <summary>
    /// Validates the creation request. The session calls this before generating anything, so a bad request
    /// fails fast instead of producing a half-built career.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The name is missing, the age is outside 16..22, an enum is undefined, or a custom aptitude is outside
    /// 0..100 / not finite.
    /// </exception>
    public void Validate()
    {
        if (DisplayName.Length == 0)
        {
            throw new ArgumentException("A career player needs a name.", nameof(CareerConfig));
        }

        if (StartingAge is < MinimumStartingAge or > MaximumStartingAge)
        {
            throw new ArgumentOutOfRangeException(
                nameof(StartingAge),
                StartingAge,
                $"A career starts at age {MinimumStartingAge}..{MaximumStartingAge}.");
        }

        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode), Mode, "Unknown creation mode.");
        }

        if (!Enum.IsDefined(Backstory))
        {
            throw new ArgumentOutOfRangeException(nameof(Backstory), Backstory, "Unknown backstory.");
        }

        if (!Enum.IsDefined(InitialTrainingFocus))
        {
            throw new ArgumentOutOfRangeException(
                nameof(InitialTrainingFocus), InitialTrainingFocus, "Unknown training focus.");
        }

        if (Potential is { } preset && !Enum.IsDefined(preset))
        {
            throw new ArgumentOutOfRangeException(nameof(Potential), preset, "Unknown potential preset.");
        }

        if (CustomAptitudes is { } custom)
        {
            ValidateAptitude(custom.Athleticism, nameof(CustomAptitudes), "Athleticism");
            ValidateAptitude(custom.Technical, nameof(CustomAptitudes), "Technical");
            ValidateAptitude(custom.Mental, nameof(CustomAptitudes), "Mental");
            ValidateAptitude(custom.Goalkeeping, nameof(CustomAptitudes), "Goalkeeping");
        }
    }

    /// <summary>
    /// The four latent aptitudes the human runs through the shared ceiling pipeline: the slider values when
    /// supplied, otherwise the chosen (or default) preset, with a keeper's hands boosted to keeper level.
    /// Authentic mode ignores this — the population-pyramid roll supplies the aptitudes there.
    /// </summary>
    /// <returns>The clamped latent aptitudes for <see cref="PlayerGenerator.GenerateCustom"/>.</returns>
    public LatentAptitudes ResolveAptitudes()
    {
        if (CustomAptitudes is { } custom) return custom.Clamped();

        PotentialPreset preset = Potential ?? PotentialPreset.RegionalStandout;
        (float athleticism, float technical, float mental) = PresetAptitudes[preset];
        float goalkeeping = PreferGoalkeeper
            ? Math.Clamp((athleticism + technical + mental) / 3f + 8f, 5f, 98f)
            : Math.Clamp(20f, 5f, 60f);

        return new LatentAptitudes(athleticism, technical, mental, goalkeeping);
    }

    private static void ValidateAptitude(float value, string parameterName, string aptitude)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value is < 0f or > 100f)
        {
            throw new ArgumentOutOfRangeException(
                parameterName, value, $"The {aptitude} aptitude must be inside 0..100.");
        }
    }
}
