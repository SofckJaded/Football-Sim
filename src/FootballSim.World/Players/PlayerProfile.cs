using FootballSim.Core;
using FootballSim.World.Core;

namespace FootballSim.World.Players;

/// <summary>
/// One attribute's three numbers (plan §4.2): what the player can do now, the hidden cap, and how fast
/// training can close the gap.
///
/// All mutations clamp: a sim system that adds a training gain can never push a current value past its
/// ceiling or outside <see cref="AttributeMetadata.MinimumValue"/>..99, so "over-trained into an invalid
/// player" is not a representable state.
/// </summary>
public struct PlayerAttributeState
{
    private float _current;
    private float _ceiling;
    private float _plasticity;

    /// <summary>Creates a state, enforcing the value/ceiling/plasticity invariants.</summary>
    public PlayerAttributeState(float current, float ceiling, float plasticity)
    {
        _current = 0f;
        _ceiling = 0f;
        _plasticity = 0f;
        Ceiling = ceiling;
        Plasticity = plasticity;
        Current = current;
    }

    /// <summary>What the player can do right now (1..99, never above <see cref="Ceiling"/>).</summary>
    public float Current
    {
        get => _current;
        set => _current = Math.Clamp(
            value, AttributeMetadata.MinimumValue, MathF.Max(AttributeMetadata.MinimumValue, _ceiling));
    }

    /// <summary>Hidden cap (1..99); training gain stops at it.</summary>
    public float Ceiling
    {
        get => _ceiling;
        set
        {
            _ceiling = Math.Clamp(value, AttributeMetadata.MinimumValue, AttributeMetadata.MaximumCeiling);
            if (_current > _ceiling) _current = _ceiling;
        }
    }

    /// <summary>How strongly training moves <see cref="Current"/> toward <see cref="Ceiling"/> (0.05..1).</summary>
    public float Plasticity
    {
        get => _plasticity;
        set => _plasticity = Math.Clamp(
            value, AttributeMetadata.MinimumPlasticity, AttributeMetadata.MaximumPlasticity);
    }

    /// <summary>Remaining room between current and ceiling (may be negative after an aging/injury cap cut).</summary>
    public float Gap => _ceiling - _current;

    /// <summary>True when the three numbers are inside their hard ranges.</summary>
    public bool IsValid =>
        _current is >= AttributeMetadata.MinimumValue and <= 100f &&
        _ceiling is >= AttributeMetadata.MinimumValue and <= AttributeMetadata.MaximumCeiling &&
        _plasticity is >= AttributeMetadata.MinimumPlasticity and <= AttributeMetadata.MaximumPlasticity;

    public override string ToString() => $"{_current:0.0}/{_ceiling:0.0} (p{_plasticity:0.00})";
}

/// <summary>
/// All eighteen attribute states of one player. Slot order is <see cref="AttributeId"/> order, so the
/// table doubles as the training/age-curve working set.
/// </summary>
public sealed class PlayerAttributeSet
{
    private readonly PlayerAttributeState[] _states;

    /// <summary>Creates a set at the neutral floor (every attribute 1/1, minimum plasticity).</summary>
    public PlayerAttributeSet()
    {
        _states = new PlayerAttributeState[AttributeMetadata.Count];
        for (int i = 0; i < _states.Length; i++)
        {
            _states[i] = new PlayerAttributeState(
                AttributeMetadata.MinimumValue,
                AttributeMetadata.MinimumValue,
                AttributeMetadata.MinimumPlasticity);
        }
    }

    /// <summary>Creates a set from eighteen states in roster order.</summary>
    public PlayerAttributeSet(PlayerAttributeState[] states)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (states.Length != AttributeMetadata.Count)
        {
            throw new ArgumentException(
                $"An attribute set needs {AttributeMetadata.Count} states, got {states.Length}.",
                nameof(states));
        }
        _states = (PlayerAttributeState[])states.Clone();
    }

    /// <summary>Gets or sets one attribute's three numbers.</summary>
    public PlayerAttributeState this[AttributeId id]
    {
        get => _states[(int)id];
        set => _states[(int)id] = value;
    }

    /// <summary>Current value of an attribute.</summary>
    public float Current(AttributeId id) => _states[(int)id].Current;

    /// <summary>Hidden ceiling of an attribute.</summary>
    public float Ceiling(AttributeId id) => _states[(int)id].Ceiling;

    /// <summary>Plasticity of an attribute.</summary>
    public float Plasticity(AttributeId id) => _states[(int)id].Plasticity;

    /// <summary>Remaining gap between current and ceiling of an attribute.</summary>
    public float Gap(AttributeId id) => _states[(int)id].Gap;

    /// <summary>Sets the current value (clamped to the ceiling and hard range).</summary>
    public void SetCurrent(AttributeId id, float value) => _states[(int)id].Current = value;

    /// <summary>Adds a delta to the current value (clamped). Returns the actual change applied.</summary>
    public float AdjustCurrent(AttributeId id, float delta)
    {
        ref PlayerAttributeState state = ref _states[(int)id];
        float before = state.Current;
        state.Current = before + delta;
        return state.Current - before;
    }

    /// <summary>Sets the ceiling (clamped); current follows it down if the cap drops below the current value.</summary>
    public void SetCeiling(AttributeId id, float value) => _states[(int)id].Ceiling = value;

    /// <summary>Adds a delta to the ceiling (clamped) and returns the actual change applied.</summary>
    public float AdjustCeiling(AttributeId id, float delta)
    {
        ref PlayerAttributeState state = ref _states[(int)id];
        float before = state.Ceiling;
        state.Ceiling = before + delta;
        return state.Ceiling - before;
    }

    /// <summary>Mean current value across every attribute.</summary>
    public float MeanCurrent()
    {
        float sum = 0f;
        for (int i = 0; i < _states.Length; i++) sum += _states[i].Current;
        return sum / _states.Length;
    }

    /// <summary>Mean current value of one category.</summary>
    public float MeanCurrent(AttributeCategory category)
    {
        float sum = 0f;
        int count = 0;
        IReadOnlyList<AttributeId> ids = AttributeMetadata.OfCategory(category);
        for (int i = 0; i < ids.Count; i++)
        {
            sum += _states[(int)ids[i]].Current;
            count++;
        }
        return count == 0 ? 0f : sum / count;
    }

    /// <summary>Mean remaining gap of one category.</summary>
    public float MeanGap(AttributeCategory category)
    {
        float sum = 0f;
        int count = 0;
        IReadOnlyList<AttributeId> ids = AttributeMetadata.OfCategory(category);
        for (int i = 0; i < ids.Count; i++)
        {
            sum += _states[(int)ids[i]].Gap;
            count++;
        }
        return count == 0 ? 0f : sum / count;
    }

    /// <summary>Copies every state from another set (used by save/load and injury roll-back paths).</summary>
    public void CopyFrom(PlayerAttributeSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        for (int i = 0; i < _states.Length; i++) _states[i] = other._states[i];
    }

    /// <summary>Deep copy.</summary>
    public PlayerAttributeSet Clone() => new(_states);

    /// <summary>True when every attribute state is inside its hard range.</summary>
    public bool IsValid()
    {
        for (int i = 0; i < _states.Length; i++)
        {
            if (!_states[i].IsValid) return false;
        }
        return true;
    }

    /// <summary>A snapshot of the current values, indexed by <see cref="AttributeId"/>.</summary>
    public float[] CurrentValues()
    {
        var result = new float[_states.Length];
        for (int i = 0; i < _states.Length; i++) result[i] = _states[i].Current;
        return result;
    }
}

/// <summary>
/// The four hidden aptitudes every ceiling is derived from (plan §4.3). They are rolled before any
/// backstory is read, which is what makes the ceiling independent of the chosen academy/backstory.
/// </summary>
public readonly record struct LatentAptitudes(
    float Athleticism,
    float Technical,
    float Mental,
    float Goalkeeping)
{
    /// <summary>Neutral, mid-pyramid aptitudes.</summary>
    public static LatentAptitudes Neutral => new(50f, 50f, 50f, 50f);

    /// <summary>Clamps every aptitude into 0..100.</summary>
    public LatentAptitudes Clamped() => new(
        Math.Clamp(Athleticism, 0f, 100f),
        Math.Clamp(Technical, 0f, 100f),
        Math.Clamp(Mental, 0f, 100f),
        Math.Clamp(Goalkeeping, 0f, 100f));

    /// <summary>Mean of the three outfield aptitudes (ignores goalkeeping).</summary>
    public float OutfieldMean => (Athleticism + Technical + Mental) / 3f;
}

/// <summary>
/// Personality and resilience traits, 0..100 (plan §4.4). They shape training consistency, morale
/// recovery, flair and discipline — never ceilings.
/// </summary>
public readonly record struct PersonalityTraits(
    float Resilience,
    float WorkEthic,
    float Creativity,
    float Discipline)
{
    /// <summary>Neutral personality (50 in every trait).</summary>
    public static PersonalityTraits Neutral => new(50f, 50f, 50f, 50f);

    /// <summary>Clamps every trait into 0..100.</summary>
    public PersonalityTraits Clamped() => new(
        Math.Clamp(Resilience, 0f, 100f),
        Math.Clamp(WorkEthic, 0f, 100f),
        Math.Clamp(Creativity, 0f, 100f),
        Math.Clamp(Discipline, 0f, 100f));
}

/// <summary>
/// Where a player came from. The backstory sets starting realization, domain tilts, coaching quality,
/// scouting visibility and personality trade-offs — never the ceiling (hard invariant, unit-tested).
/// </summary>
public enum BackstoryKind
{
    /// <summary>High realization, technical/positioning tilt, excellent early coaching, low resilience.</summary>
    EliteAcademy = 0,

    /// <summary>Medium-high realization, balanced tilt, good coaching.</summary>
    RegionalAcademy = 1,

    /// <summary>Baseline realization, modest stamina/work-ethic tilt, low visibility.</summary>
    LocalClubYouth = 2,

    /// <summary>Low-medium realization, dribbling/touch tilt, poor positioning, high resilience/creativity.</summary>
    StreetFootball = 3,

    /// <summary>Low technical realization, body tilt, high work ethic and resilience.</summary>
    MultisportLateStarter = 4
}

/// <summary>What kind of injury a player has; drives flavor and which ceilings permanent damage shaves.</summary>
public enum InjuryKind
{
    None = 0,
    HamstringTear = 1,
    KneeLigament = 2,
    AnkleLigament = 3,
    GroinStrain = 4,
    CalfStrain = 5,
    Concussion = 6,
    FootFracture = 7,
    BackInjury = 8,
    Illness = 9
}

/// <summary>How serious an injury is (drives weeks out and whether permanent damage is applied).</summary>
public enum InjurySeverity
{
    None = 0,
    Knock = 1,
    Minor = 2,
    Moderate = 3,
    Severe = 4,
    SeasonEnding = 5,
    CareerThreatening = 6
}

/// <summary>
/// A player's current injury. Recovery is a weekly countdown (W3 lifecycle); this type only carries the
/// state so selection, growth and the UI agree on availability.
/// </summary>
public sealed class InjuryState
{
    /// <summary>Injury kind, or <see cref="InjuryKind.None"/>.</summary>
    public InjuryKind Kind { get; private set; }

    /// <summary>Injury severity, or <see cref="InjurySeverity.None"/>.</summary>
    public InjurySeverity Severity { get; private set; }

    /// <summary>Weeks still to miss; zero when fit.</summary>
    public int WeeksOutRemaining { get; private set; }

    /// <summary>Total weeks the injury was expected to last when it happened.</summary>
    public int TotalWeeksOut { get; private set; }

    /// <summary>How many permanent-damage events this injury has already applied (never applied twice).</summary>
    public int PermanentDamageApplied { get; private set; }

    /// <summary>True while the player is unavailable.</summary>
    public bool IsInjured => WeeksOutRemaining > 0;

    /// <summary>Starts an injury; throws if weeks are not positive or the player is already injured.</summary>
    public void Begin(InjuryKind kind, InjurySeverity severity, int weeksOut)
    {
        if (kind == InjuryKind.None) throw new ArgumentOutOfRangeException(nameof(kind));
        if (severity == InjurySeverity.None) throw new ArgumentOutOfRangeException(nameof(severity));
        if (weeksOut <= 0) throw new ArgumentOutOfRangeException(nameof(weeksOut));
        if (IsInjured) throw new InvalidOperationException("The player is already injured.");

        Kind = kind;
        Severity = severity;
        WeeksOutRemaining = weeksOut;
        TotalWeeksOut = weeksOut;
        PermanentDamageApplied = 0;
    }

    /// <summary>One week of recovery; returns true when the player is fit again.</summary>
    public bool AdvanceWeek()
    {
        if (WeeksOutRemaining > 0) WeeksOutRemaining--;
        if (WeeksOutRemaining == 0) Clear();
        return !IsInjured;
    }

    /// <summary>Marks that permanent ceiling damage has been applied for this injury.</summary>
    public void MarkPermanentDamageApplied() => PermanentDamageApplied++;

    /// <summary>Clears the injury state (recovered or reset).</summary>
    public void Clear()
    {
        Kind = InjuryKind.None;
        Severity = InjurySeverity.None;
        WeeksOutRemaining = 0;
        TotalWeeksOut = 0;
        PermanentDamageApplied = 0;
    }
}

/// <summary>
/// The ten weekly training presets (plan §4.5). <see cref="Balanced"/> is the neutral control; every
/// other focus multiplies gain on a small attribute set and can drag a competing attribute.
/// </summary>
public enum TrainingFocus
{
    /// <summary>Even development across every category.</summary>
    Balanced = 0,

    /// <summary>Strength and power: strength/balance up, with a mild agility drag once strong.</summary>
    StrengthAndPower = 1,

    /// <summary>Endurance and engine: stamina up, competing slightly with explosive strength.</summary>
    EnduranceAndEngine = 2,

    /// <summary>Agility and quickness: agility/balance up, with a mild strength drag once strong.</summary>
    AgilityAndQuickness = 3,

    /// <summary>Finishing and shooting.</summary>
    FinishingAndShooting = 4,

    /// <summary>Passing and vision.</summary>
    PassingAndVision = 5,

    /// <summary>Ball control and dribbling.</summary>
    BallControlAndDribbling = 6,

    /// <summary>Defending and positioning.</summary>
    DefendingAndPositioning = 7,

    /// <summary>Goalkeeping-specific work.</summary>
    Goalkeeping = 8,

    /// <summary>Recovery and light work: no growth push, better condition/sharpness recovery.</summary>
    RecoveryAndLight = 9
}

/// <summary>One competitive appearance, kept for rolling form and rolling-average ceiling nudges.</summary>
/// <param name="Season">Season the match was played in.</param>
/// <param name="Week">Week the match was played in.</param>
/// <param name="Minutes">Minutes played.</param>
/// <param name="Rating">Match rating 4.0..10.0.</param>
public readonly record struct MatchAppearance(int Season, int Week, int Minutes, float Rating);

/// <summary>
/// A bounded window of recent appearances. Form-driven systems (growth, selection, ceiling nudges) read
/// this instead of keeping their own copies of the match log.
/// </summary>
public sealed class RecentForm
{
    /// <summary>How many appearances are retained.</summary>
    public const int MaxAppearances = 20;

    private readonly List<MatchAppearance> _appearances = new(MaxAppearances);

    /// <summary>Recorded appearances, oldest first.</summary>
    public IReadOnlyList<MatchAppearance> Appearances => _appearances;

    /// <summary>Appends an appearance, dropping the oldest beyond <see cref="MaxAppearances"/>.</summary>
    public void Record(in MatchAppearance appearance)
    {
        _appearances.Add(appearance);
        if (_appearances.Count > MaxAppearances) _appearances.RemoveAt(0);
    }

    /// <summary>Average rating over the last <paramref name="lastN"/> appearances, or 0 with none.</summary>
    public float AverageRating(int lastN = 8)
    {
        if (lastN <= 0) throw new ArgumentOutOfRangeException(nameof(lastN));
        int count = Math.Min(lastN, _appearances.Count);
        if (count == 0) return 0f;

        float sum = 0f;
        for (int i = _appearances.Count - count; i < _appearances.Count; i++) sum += _appearances[i].Rating;
        return sum / count;
    }

    /// <summary>Total minutes over the last <paramref name="lastN"/> appearances.</summary>
    public int TotalMinutes(int lastN = 8)
    {
        if (lastN <= 0) throw new ArgumentOutOfRangeException(nameof(lastN));
        int count = Math.Min(lastN, _appearances.Count);
        int sum = 0;
        for (int i = _appearances.Count - count; i < _appearances.Count; i++) sum += _appearances[i].Minutes;
        return sum;
    }

    /// <summary>True when the last <paramref name="lastN"/> appearances are all at least 60 minutes.</summary>
    public bool HasSustainedMinutes(int lastN = 8, int minimumMinutes = 60)
    {
        if (lastN <= 0) throw new ArgumentOutOfRangeException(nameof(lastN));
        if (_appearances.Count < lastN) return false;
        for (int i = _appearances.Count - lastN; i < _appearances.Count; i++)
        {
            if (_appearances[i].Minutes < minimumMinutes) return false;
        }
        return true;
    }

    /// <summary>Normalized form in 0..1 from the rolling rating (4.0 -> 0, 10.0 -> 1).</summary>
    public float Form01(int lastN = 8)
    {
        float rating = AverageRating(lastN);
        return rating <= 0f ? 0.5f : Math.Clamp((rating - 4f) / 6f, 0f, 1f);
    }

    /// <summary>Drops the whole window (retirement, new club, save reset).</summary>
    public void Clear() => _appearances.Clear();
}

/// <summary>
/// A rolling 12-week history of each attribute's current value, used for the trend arrows the player
/// screen shows. Growth records one snapshot per week; the delta over twelve weeks is what the arrow
/// represents.
/// </summary>
public sealed class PlayerTrendHistory
{
    /// <summary>Length of the trend window in weeks.</summary>
    public const int WindowWeeks = 12;

    private readonly List<float[]> _snapshots = new(WindowWeeks + 1);

    /// <summary>Number of weekly snapshots retained.</summary>
    public int Count => _snapshots.Count;

    /// <summary>Raw weekly snapshots, oldest first; each is eighteen current values in roster order.</summary>
    public IReadOnlyList<float[]> Snapshots => _snapshots;

    /// <summary>Records this week's current values, keeping at most <see cref="WindowWeeks"/> + 1 snapshots.</summary>
    public void Record(PlayerAttributeSet attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        _snapshots.Add(attributes.CurrentValues());
        while (_snapshots.Count > WindowWeeks + 1) _snapshots.RemoveAt(0);
    }

    /// <summary>
    /// Change in one attribute over the last <paramref name="weeks"/> recorded weeks. Zero when the
    /// history is too short, so a brand-new player shows a flat arrow rather than a fake trend.
    /// </summary>
    public float Delta(AttributeId id, int weeks = WindowWeeks, PlayerAttributeSet? current = null)
    {
        if (weeks <= 0) throw new ArgumentOutOfRangeException(nameof(weeks));
        if (_snapshots.Count == 0) return 0f;

        int index = Math.Max(0, _snapshots.Count - 1 - weeks);
        float oldest = _snapshots[index][(int)id];
        float newest = current is not null && _snapshots.Count < weeks + 1
            ? current.Current(id)
            : _snapshots[^1][(int)id];
        return newest - oldest;
    }

    /// <summary>Clears the history (new season reset, save rebuild).</summary>
    public void Clear() => _snapshots.Clear();
}

/// <summary>
/// Per-season counting stats for one player. The world keeps the current season live and archives the
/// finished ones; the career ledger (W4) reads the archive.
/// </summary>
public sealed class PlayerSeasonStats
{
    /// <summary>Season these stats belong to.</summary>
    public int Season { get; set; }

    public int Appearances { get; set; }
    public int Starts { get; set; }
    public int SubstituteAppearances { get; set; }
    public int MinutesPlayed { get; set; }
    public int Goals { get; set; }
    public int Assists { get; set; }
    public int Shots { get; set; }
    public int PassesAttempted { get; set; }
    public int PassesCompleted { get; set; }
    public int Tackles { get; set; }
    public int Saves { get; set; }
    public int YellowCards { get; set; }
    public int RedCards { get; set; }

    /// <summary>Sum of match ratings over the appearances.</summary>
    public float RatingSum { get; set; }

    /// <summary>Mean match rating, or 0 with no appearances.</summary>
    public float AverageRating => Appearances > 0 ? RatingSum / Appearances : 0f;

    /// <summary>Resets every counter (new season).</summary>
    public void Reset(int season)
    {
        Season = season;
        Appearances = 0;
        Starts = 0;
        SubstituteAppearances = 0;
        MinutesPlayed = 0;
        Goals = 0;
        Assists = 0;
        Shots = 0;
        PassesAttempted = 0;
        PassesCompleted = 0;
        Tackles = 0;
        Saves = 0;
        YellowCards = 0;
        RedCards = 0;
        RatingSum = 0f;
    }
}

/// <summary>
/// One footballer in the living world. This is the single mutable player entity shared by the human
/// career player and every AI player: attributes, latents, personality, injury, form, training and the
/// hidden ceiling-nudge ledger all live here (plan: "Two Creation Modes, One Engine").
///
/// Simulation state (attributes, condition, form, club) is mutable; identity (id, name, backstory) is
/// fixed at creation. Nothing in the model mentions Godot or the match engine.
/// </summary>
public sealed class WorldPlayer
{
    /// <summary>Smallest supported age (youth intake is 16; a little floor tolerance is deliberate).</summary>
    public const int MinimumAge = 14;

    /// <summary>Largest supported age (retirements are forced by 40; graying NPCs may linger).</summary>
    public const int MaximumAge = 45;

    /// <summary>Maximum lifetime ceiling nudge one attribute may accumulate (plan §4.5: max +8).</summary>
    public const float MaxCareerCeilingNudge = 8f;

    private readonly float[] _ceilingNudges = new float[AttributeMetadata.Count];
    private int _age;

    /// <summary>Creates a player. Attributes must be a valid eighteen-attribute set.</summary>
    public WorldPlayer(
        PlayerId id,
        string firstName,
        string lastName,
        int age,
        int birthWeek,
        Footedness preferredFoot,
        float weakFoot01,
        PlayerAttributeSet attributes,
        LatentAptitudes latent,
        PersonalityTraits personality,
        BackstoryKind backstory)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (!attributes.IsValid()) throw new ArgumentException("Attributes are outside their ranges.", nameof(attributes));
        if (birthWeek is < 1 or > 52) throw new ArgumentOutOfRangeException(nameof(birthWeek));

        Id = id;
        FirstName = firstName ?? string.Empty;
        LastName = lastName ?? string.Empty;
        Age = age;
        BirthWeek = birthWeek;
        PreferredFoot = preferredFoot;
        WeakFoot01 = Math.Clamp(weakFoot01, 0f, 1f);
        Attributes = attributes;
        Latent = latent.Clamped();
        Personality = personality.Clamped();
        Backstory = backstory;
        Injury = new InjuryState();
        Form = new RecentForm();
        Trends = new PlayerTrendHistory();
        CurrentSeason = new PlayerSeasonStats();
    }

    /// <summary>Stable one-based identity, unique within a world.</summary>
    public PlayerId Id { get; }

    /// <summary>Given name.</summary>
    public string FirstName { get; }

    /// <summary>Family name.</summary>
    public string LastName { get; }

    /// <summary>Full display name.</summary>
    public string DisplayName =>
        LastName.Length == 0 ? FirstName : FirstName.Length == 0 ? LastName : $"{FirstName} {LastName}";

    /// <summary>Whole years; advances once per season in the off-season rollover (W3).</summary>
    public int Age
    {
        get => _age;
        set => _age = Math.Clamp(value, MinimumAge, MaximumAge);
    }

    /// <summary>Week of the year the player was born in (1..52); flavour and birthday ticks.</summary>
    public int BirthWeek { get; }

    /// <summary>Strong foot.</summary>
    public Footedness PreferredFoot { get; }

    /// <summary>Weak-foot ability, 0..1.</summary>
    public float WeakFoot01 { get; }

    /// <summary>The eighteen attribute states.</summary>
    public PlayerAttributeSet Attributes { get; }

    /// <summary>Hidden aptitudes the ceilings were derived from (never shown in Authentic mode).</summary>
    public LatentAptitudes Latent { get; set; }

    /// <summary>Personality and resilience traits.</summary>
    public PersonalityTraits Personality { get; set; }

    /// <summary>Where the player came from; fixed at creation and never a ceiling input.</summary>
    public BackstoryKind Backstory { get; }

    /// <summary>Current injury state.</summary>
    public InjuryState Injury { get; }

    /// <summary>Current club, or <see cref="ClubId.None"/> for a free agent.</summary>
    public ClubId ClubId { get; set; }

    /// <summary>Weeks of suspension still to serve (cards, discipline).</summary>
    public int SuspensionWeeksRemaining { get; set; }

    /// <summary>Condition/freshness, 0 (spent) .. 1 (fully fresh).</summary>
    public float Condition01
    {
        get => _condition01;
        set => _condition01 = Math.Clamp(value, 0f, 1f);
    }

    private float _condition01 = 1f;

    /// <summary>Match sharpness, 0 (rusty) .. 1 (sharp); decays without minutes.</summary>
    public float MatchSharpness01
    {
        get => _matchSharpness01;
        set => _matchSharpness01 = Math.Clamp(value, 0f, 1f);
    }

    private float _matchSharpness01 = 0.6f;

    /// <summary>Rolling form, 0 (poor) .. 1 (excellent).</summary>
    public float Form01
    {
        get => _form01;
        set => _form01 = Math.Clamp(value, 0f, 1f);
    }

    private float _form01 = 0.5f;

    /// <summary>Scouting visibility, 0 (unknown) .. 1 (household name).</summary>
    public float Visibility01
    {
        get => _visibility01;
        set => _visibility01 = Math.Clamp(value, 0f, 1f);
    }

    private float _visibility01 = 0.3f;

    /// <summary>The player's currently selected weekly training focus.</summary>
    public TrainingFocus TrainingFocus { get; set; } = TrainingFocus.Balanced;

    /// <summary>Rolling appearance window (form, minutes, ceiling nudges).</summary>
    public RecentForm Form { get; }

    /// <summary>Twelve-week attribute trend history.</summary>
    public PlayerTrendHistory Trends { get; }

    /// <summary>Live stats for the current season.</summary>
    public PlayerSeasonStats CurrentSeason { get; }

    /// <summary>Archived per-season stats, oldest first.</summary>
    public List<PlayerSeasonStats> SeasonHistory { get; } = new();

    /// <summary>Set when the player retires; retired players are skipped by every selection system.</summary>
    public bool IsRetired { get; set; }

    /// <summary>True when the player has no club.</summary>
    public bool IsFreeAgent => !ClubId.IsValid;

    /// <summary>True when the player can be selected this week.</summary>
    public bool IsAvailableForSelection =>
        !IsRetired && !Injury.IsInjured && SuspensionWeeksRemaining == 0;

    /// <summary>Total ceiling nudge already applied to one attribute (career cap: +8).</summary>
    public float CeilingNudgeApplied(AttributeId id) => _ceilingNudges[(int)id];

    /// <summary>
    /// Records a ceiling nudge against the career cap. Returns the amount actually available
    /// (0 when the attribute has already received its lifetime +8).
    /// </summary>
    public float AvailableCeilingNudge(AttributeId id) =>
        MathF.Max(0f, MaxCareerCeilingNudge - _ceilingNudges[(int)id]);

    /// <summary>Bookkeeping for an applied nudge: adds to the attribute's lifetime total.</summary>
    public void RecordCeilingNudge(AttributeId id, float amount)
    {
        if (amount <= 0f) return;
        _ceilingNudges[(int)id] += MathF.Min(amount, AvailableCeilingNudge(id));
    }

    /// <summary>True when the player has played at least <paramref name="minimumMinutes"/> this week (aggregated matches).</summary>
    public bool HasPlayedThisWeek(int minimumMinutes = 1) => Form.Appearances.Count > 0 &&
        Form.Appearances[^1].Minutes >= minimumMinutes;

    /// <inheritdoc />
    public override string ToString() => $"{DisplayName} (#{Id.Value}, {Age})";
}
