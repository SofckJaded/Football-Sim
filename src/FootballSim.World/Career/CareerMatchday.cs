using FootballSim.World.Core;
using FootballSim.World.Simulation;
using FootballSim.World.World;

namespace FootballSim.World.Career;

/// <summary>What <see cref="CareerSession.AdvanceToNextEventOrMatchday"/> stopped on.</summary>
public enum CareerEventKind
{
    /// <summary>Nothing stopped the clock (never returned by the public entry point).</summary>
    None = 0,

    /// <summary>The player's club has a match this week and the frontend must play or simulate it.</summary>
    HumanMatchday = 1,

    /// <summary>An inbox message needs an answer before the career can move on.</summary>
    InboxAction = 2,

    /// <summary>The player is unattached; <see cref="CareerSession.RequestAmateurTrial"/> is the way back.</summary>
    NeedsClub = 3,

    /// <summary>A season finished and rolled over into the next one (the summaries are attached).</summary>
    SeasonRolledOver = 4,

    /// <summary>The player retired: the career is over.</summary>
    CareerOver = 5
}

/// <summary>
/// The result of one <see cref="CareerSession.AdvanceToNextEventOrMatchday"/> call: what the clock stopped on,
/// the calendar position and the object that needs the frontend's attention.
/// </summary>
public sealed class CareerAdvanceResult
{
    /// <summary>Why the advance stopped.</summary>
    public CareerEventKind Kind { get; init; }

    /// <summary>Calendar position the career is now at.</summary>
    public WorldDate Date { get; init; }

    /// <summary>The human fixture when <see cref="Kind"/> is <see cref="CareerEventKind.HumanMatchday"/>.</summary>
    public WorldFixture? Fixture { get; init; }

    /// <summary>The unanswered message when <see cref="Kind"/> is <see cref="CareerEventKind.InboxAction"/>.</summary>
    public CareerInboxMessage? Message { get; init; }

    /// <summary>The finished season's tables when <see cref="Kind"/> is <see cref="CareerEventKind.SeasonRolledOver"/>.</summary>
    public SeasonSummary? SeasonSummary { get; init; }

    /// <summary>The off-season telemetry when <see cref="Kind"/> is <see cref="CareerEventKind.SeasonRolledOver"/>.</summary>
    public SeasonRolloverSummary? Rollover { get; init; }

    /// <summary>Human-readable one-liner for logs and CLI output.</summary>
    public string Description => Kind switch
    {
        CareerEventKind.HumanMatchday => $"Matchday: {Fixture}",
        CareerEventKind.InboxAction => $"Inbox: {Message?.Subject}",
        CareerEventKind.NeedsClub => "Unattached: request an amateur trial",
        CareerEventKind.SeasonRolledOver => $"Season {SeasonSummary?.Season} complete",
        CareerEventKind.CareerOver => "Career over",
        _ => $"S{Date.Season}W{Date.Week:00}"
    };

    /// <inheritdoc />
    public override string ToString() => $"{Description} (S{Date.Season}W{Date.Week:00})";
}

/// <summary>
/// Everything the live-match frontend needs to play the human's fixture (W4 prepares it; W5 fills the match
/// engine configuration from it; W6/W7 render it). It is also what
/// <see cref="CareerSession.CommitHumanLiveMatchResult"/> validates a report against.
/// </summary>
public sealed class CareerMatchContext
{
    /// <summary>The fixture to play.</summary>
    public MatchId FixtureId { get; init; }

    /// <summary>Calendar position of the match.</summary>
    public WorldDate Date { get; init; }

    /// <summary>League or cup.</summary>
    public CompetitionKind Competition { get; init; }

    /// <summary>Cup round, or <see cref="CupRound.None"/> for a league match.</summary>
    public CupRound CupRound { get; init; }

    /// <summary>Home club.</summary>
    public ClubId HomeClubId { get; init; }

    /// <summary>Away club.</summary>
    public ClubId AwayClubId { get; init; }

    /// <summary>Home club display name.</summary>
    public string HomeClubName { get; init; } = string.Empty;

    /// <summary>Away club display name.</summary>
    public string AwayClubName { get; init; } = string.Empty;

    /// <summary>Home club's selected XI and bench.</summary>
    public SelectedLineup HomeLineup { get; init; } = null!;

    /// <summary>Away club's selected XI and bench.</summary>
    public SelectedLineup AwayLineup { get; init; } = null!;

    /// <summary>True when the human's club is the home side.</summary>
    public bool HumanIsHome { get; init; }

    /// <summary>True when the human is in the starting XI (false = bench or unavailable).</summary>
    public bool HumanInStartingXI { get; init; }

    /// <summary>True when the human is on the bench.</summary>
    public bool HumanOnBench { get; init; }

    /// <summary>The player's 1..18 matchday shirt number (1..11 starting, 12..18 bench).</summary>
    public int HumanShirtNumber { get; init; }

    /// <summary>True when the human can take part at all (fit and unsuspended).</summary>
    public bool HumanAvailable { get; init; }

    /// <summary>True when the player is playing for a loan club this season.</summary>
    public bool HumanIsOnLoan { get; init; }
}

/// <summary>
/// The bridge's live-match output (plan stage W5): the scoreline plus one line per participant. W4's
/// <see cref="CareerSession.CommitHumanLiveMatchResult"/> converts it into the same result shape the quick sim
/// produces, so a live match and a simulated match apply identically to the world.
/// </summary>
public sealed class LiveMatchReport
{
    /// <summary>The fixture that was played.</summary>
    public MatchId FixtureId { get; init; }

    /// <summary>Home goals in normal time.</summary>
    public int HomeGoals { get; init; }

    /// <summary>Away goals in normal time.</summary>
    public int AwayGoals { get; init; }

    /// <summary>True when a level cup tie was decided by a shoot-out.</summary>
    public bool WentToPenalties { get; init; }

    /// <summary>Home shoot-out goals (0 unless <see cref="WentToPenalties"/>).</summary>
    public int HomePenaltyGoals { get; init; }

    /// <summary>Away shoot-out goals (0 unless <see cref="WentToPenalties"/>).</summary>
    public int AwayPenaltyGoals { get; init; }

    /// <summary>Home participants who took the field.</summary>
    public IReadOnlyList<PlayerMatchLine> HomePlayers { get; init; } = Array.Empty<PlayerMatchLine>();

    /// <summary>Away participants who took the field.</summary>
    public IReadOnlyList<PlayerMatchLine> AwayPlayers { get; init; } = Array.Empty<PlayerMatchLine>();

    /// <summary>In-match injuries to apply to the world.</summary>
    public IReadOnlyList<InjuryEvent> Injuries { get; init; } = Array.Empty<InjuryEvent>();

    /// <summary>Cards shown (observed only; the per-player lines already carry the counts).</summary>
    public IReadOnlyList<CardEvent> Cards { get; init; } = Array.Empty<CardEvent>();

    /// <summary>Substitutions made (observed only; the per-player lines already carry the minutes).</summary>
    public IReadOnlyList<SubstitutionEvent> Substitutions { get; init; } = Array.Empty<SubstitutionEvent>();

    /// <summary>Home team statistics (optional; recorded for the post-match screen).</summary>
    public QuickSimTeamStats HomeStats { get; init; } = new(50, 0, 0, 0, 0, 0);

    /// <summary>Away team statistics (optional; recorded for the post-match screen).</summary>
    public QuickSimTeamStats AwayStats { get; init; } = new(50, 0, 0, 0, 0, 0);
}

/// <summary>
/// One resolved human match: the fixture, the scoreline, the player's own line and enough context for the
/// post-match screen. Returned by <see cref="CareerSession.ResolveHumanMatchViaQuickSim"/> and
/// <see cref="CareerSession.CommitHumanLiveMatchResult"/>.
/// </summary>
public sealed class CareerMatchResult
{
    /// <summary>The played fixture.</summary>
    public MatchId FixtureId { get; init; }

    /// <summary>Calendar position.</summary>
    public WorldDate Date { get; init; }

    /// <summary>League or cup.</summary>
    public CompetitionKind Competition { get; init; }

    /// <summary>Home club.</summary>
    public ClubId HomeClubId { get; init; }

    /// <summary>Away club.</summary>
    public ClubId AwayClubId { get; init; }

    /// <summary>Home club display name.</summary>
    public string HomeClubName { get; init; } = string.Empty;

    /// <summary>Away club display name.</summary>
    public string AwayClubName { get; init; } = string.Empty;

    /// <summary>Home goals (normal time).</summary>
    public int HomeGoals { get; init; }

    /// <summary>Away goals (normal time).</summary>
    public int AwayGoals { get; init; }

    /// <summary>True when the player's club was the home side.</summary>
    public bool HumanIsHome { get; init; }

    /// <summary>True when the player started.</summary>
    public bool HumanStarted { get; init; }

    /// <summary>The player's own match line, or null when he did not take the field.</summary>
    public PlayerMatchLine? HumanLine { get; init; }

    /// <summary>True when the player's club won.</summary>
    public bool HumanWon { get; init; }

    /// <summary>True when the match was drawn.</summary>
    public bool HumanDrew { get; init; }

    /// <summary>True when the player's club lost.</summary>
    public bool HumanLost { get; init; }

    /// <summary>Minutes the player appeared.</summary>
    public int HumanMinutes => HumanLine?.Minutes ?? 0;

    /// <summary>The player's match rating (4..10), or 0 when he did not play.</summary>
    public float HumanRating => HumanLine?.Rating ?? 0f;

    /// <inheritdoc />
    public override string ToString() =>
        $"{HomeClubName} {HomeGoals}-{AwayGoals} {AwayClubName}" +
        (HumanLine is null ? " (did not play)" : $" — {HumanLine.Minutes}' rating {HumanLine.Rating:0.0}");
}
