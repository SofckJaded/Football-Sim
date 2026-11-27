using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using FootballSim.World.Bridge;
using FootballSim.World.Career;
using FootballSim.World.Core;
using FootballSim.World.Players;
using FootballSim.World.Simulation;
using FootballSim.World.World;

namespace FootballSim.GodotClient;

/// <summary>
/// The career hub (plan stage W6): one top bar (date, club, tier, squad status, unread inbox, save) and six
/// left-nav panels — Overview, My Player, Training, Competitions, Squad &amp; Club and Inbox &amp; Offers. It
/// drives the career only through <see cref="CareerSession"/>: every action is a session call, and every number
/// on screen comes from the session's own read-only API (no simulation state is duplicated here).
/// </summary>
public partial class CareerHubNode : Control
{
    private enum Tab
    {
        Overview,
        Player,
        Training,
        Competitions,
        Squad,
        Inbox
    }

    /// <summary>Simulated seconds per half when the career player's match is played in the 3D match scene.</summary>
    private const float CareerMatchHalfSeconds = 270f;

    /// <summary>Simulated seconds per half for a short live bench cameo.</summary>
    private const float CameoHalfSeconds = 60f;

    private static readonly string[] TabLabels =
    {
        "Overview",
        "My Player",
        "Training",
        "Competitions",
        "Squad & Club",
        "Inbox"
    };

    private CareerSession _session = null!;
    private VBoxContainer _pages = null!;
    private Label _header = null!;
    private Label _status = null!;
    private VBoxContainer _content = null!;
    private readonly List<Button> _tabButtons = new(6);

    private Tab _tab = Tab.Overview;
    private int _selectedMessageId;
    private int _divisionTier;
    private string _notice = string.Empty;
    private string _lastMatchSummary = string.Empty;
    private CareerAdvanceResult? _lastEvent;

    public override void _Ready()
    {
        if (GameSession.ActiveCareer is not { } career)
        {
            // Scene changes are deferred: the tree is still busy instantiating this node.
            Callable.From(() => GetTree().ChangeSceneToFile(GameSession.CareerEntryScene)).CallDeferred();
            return;
        }

        _session = career;
        _divisionTier = Math.Clamp(_session.HumanClub?.Tier ?? 6, 1, 6);
        _pages = CareerUi.BuildChrome(this, minimumWidth: 1180f);
        BuildLayout();
        Refresh();
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.Escape)
        {
            // Leaving to the main menu keeps the loaded career in GameSession, so Continue resumes it.
            Autosave();
            GameSession.ReturnToMainMenu(GetTree());
            GetViewport().SetInputAsHandled();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Layout and refresh.
    // ---------------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        CareerUi.Clear(_pages);

        _header = CareerUi.Heading(_pages, string.Empty, 15);
        _header.AddThemeColorOverride("font_color", CareerUi.TextColor);

        HBoxContainer headerRow = CareerUi.Row(_pages);
        CareerUi.Button(headerRow, "Save", () =>
        {
            try
            {
                CareerSaveFiles.Save(_session, 1);
                _notice = "Saved to slot 1.";
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.IO.IOException or
                UnauthorizedAccessException)
            {
                _notice = $"Save failed: {exception.Message}";
            }

            Refresh();
        });

        CareerUi.Button(headerRow, "Main Menu", () =>
        {
            Autosave();
            GameSession.ReturnToMainMenu(GetTree());
        });

        _pages.AddChild(new HSeparator());

        HBoxContainer body = CareerUi.Row(_pages, separation: 14);
        body.SizeFlagsVertical = SizeFlags.ExpandFill;

        VBoxContainer nav = new() { CustomMinimumSize = new Vector2(184f, 0f) };
        nav.AddThemeConstantOverride("separation", 6);
        body.AddChild(nav);

        for (int index = 0; index < TabLabels.Length; index++)
        {
            var tab = (Tab)index;
            Button button = CareerUi.Button(nav, TabLabels[index], () =>
            {
                _tab = tab;
                _notice = string.Empty;
                Refresh();
            });
            _tabButtons.Add(button);
        }

        VBoxContainer main = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        main.AddThemeConstantOverride("separation", 6);
        body.AddChild(main);

        _status = CareerUi.Paragraph(main, string.Empty, 13, CareerUi.Accent);
        _content = CareerUi.Scroll(main, minimumHeight: 470);
    }

    private void Refresh()
    {
        if (!IsInsideTree()) return;

        _header.Text = BuildHeaderText();
        _status.Text = _notice;

        for (int index = 0; index < _tabButtons.Count; index++)
        {
            CareerUi.StyleButton(_tabButtons[index], primary: _tab == (Tab)index);
        }

        CareerUi.Clear(_content);
        Callable.From(() => CareerUi.FocusFirstIfNone(this)).CallDeferred();
        switch (_tab)
        {
            case Tab.Overview:
                BuildOverview();
                break;
            case Tab.Player:
                BuildPlayer();
                break;
            case Tab.Training:
                BuildTraining();
                break;
            case Tab.Competitions:
                BuildCompetitions();
                break;
            case Tab.Squad:
                BuildSquad();
                break;
            default:
                BuildInbox();
                break;
        }
    }

    private string BuildHeaderText()
    {
        WorldClub? club = _session.HumanClub;
        string clubText = club is null
            ? "Free agent"
            : $"{club.Name} ({_session.World.LeagueForTier(club.Tier).ShortName}, tier {club.Tier})";
        int unread = _session.Inbox.UnreadCount;
        return $"Season {_session.Season}  ·  Week {_session.Date.Week}  ·  {clubText}  ·  " +
               $"{_session.SquadStatus}  ·  Inbox: {unread} unread";
    }

    // ---------------------------------------------------------------------------------------------
    // Overview.
    // ---------------------------------------------------------------------------------------------

    private void BuildOverview()
    {
        CareerUi.Heading(_content, "OVERVIEW");
        CareerUi.Paragraph(_content, DescribeLastEvent(), 13, CareerUi.MutedColor);

        if (!string.IsNullOrEmpty(GameSession.LastCareerMatchSummary))
        {
            CareerUi.Paragraph(_content, $"Last match — {GameSession.LastCareerMatchSummary}", 13, CareerUi.GoodColor);
        }
        else if (!string.IsNullOrEmpty(_lastMatchSummary))
        {
            CareerUi.Paragraph(_content, _lastMatchSummary, 13, CareerUi.GoodColor);
        }

        WorldFixture? next = _session.NextFixture();
        CareerUi.InfoRow(
            _content,
            "Next fixture",
            next is null
                ? "No fixture scheduled (season over)."
                : $"{ClubName(next.HomeClubId)} v {ClubName(next.AwayClubId)} — week {next.Week}" +
                  (next.Involves(_session.HumanPlayer.ClubId) ? " (your match)" : string.Empty));

        if (_session.HumanClub is { } club)
        {
            IReadOnlyList<LeagueTableRow> table = _session.CurrentLeagueTable();
            int position = 0;
            for (int index = 0; index < table.Count; index++)
            {
                if (table[index].ClubId == club.Id)
                {
                    position = index + 1;
                    break;
                }
            }

            LeagueTableRow? row = position == 0 ? null : table[position - 1];
            CareerUi.InfoRow(
                _content,
                "League position",
                row is null
                    ? "—"
                    : $"{position} of 18 — {row.Points} pts from {row.Played} played " +
                      $"(W{row.Won} D{row.Drawn} L{row.Lost})");
        }

        CareerUi.InfoRow(_content, "Squad status", _session.SquadStatus);
        CareerUi.InfoRow(_content, "Training focus", _session.TrainingFocus.ToString());
        CareerUi.InfoRow(
            _content,
            "Form (last 5)",
            DescribeRecentForm());

        CareerUi.Spacer(_content);

        bool hasFixture = _session.HumanFixturesThisWeek().Count > 0 &&
                          _session.HumanPlayer.IsAvailableForSelection;
        bool awaitingMatch = _lastEvent?.Kind == CareerEventKind.HumanMatchday;

        HBoxContainer actions = CareerUi.Row(_content, separation: 10);
        CareerUi.Button(actions, "Advance Week", AdvanceWeek, primary: true);
        CareerUi.Button(actions, "Sim Match", SimMatch, enabled: awaitingMatch && hasFixture);
        CareerUi.Button(actions, "Play Match", PlayMatchLive, enabled: awaitingMatch && hasFixture);

        if (_session.HumanPlayer.ClubId.IsValid == false)
        {
            CareerUi.Button(_content, "Request Amateur Trial", RequestTrial, primary: true);
        }

        CareerUi.Paragraph(
            _content,
            hasFixture && !awaitingMatch
                ? "Advance until the clock stops on your matchday, then simulate or play the match."
                : awaitingMatch
                    ? "Play Match runs the real 3D match; Sim Match resolves it with the world quick sim."
                    : string.Empty,
            12, CareerUi.MutedColor);
    }

    private string DescribeLastEvent() => _lastEvent switch
    {
        { Kind: CareerEventKind.HumanMatchday } result => $"Your matchday: {result.Fixture}.",
        { Kind: CareerEventKind.InboxAction } result => $"An inbox message needs an answer: {result.Message?.Subject}.",
        { Kind: CareerEventKind.NeedsClub } => "You are unattached: request an amateur trial.",
        { Kind: CareerEventKind.SeasonRolledOver } result =>
            $"Season {result.SeasonSummary?.Season} is complete — the world has rolled over into season " +
            $"{_session.Season}.",
        { Kind: CareerEventKind.CareerOver } => "Your career is over.",
        _ => "Ready. Advance the clock when you are."
    };

    private string DescribeRecentForm()
    {
        IReadOnlyList<MatchAppearance> appearances = _session.HumanPlayer.Form.Appearances;
        if (appearances.Count == 0) return "No competitive minutes yet.";
        int start = Math.Max(0, appearances.Count - 5);
        var parts = new List<string>(5);
        for (int index = start; index < appearances.Count; index++)
        {
            parts.Add($"{appearances[index].Rating:0.0}");
        }

        return string.Join(" · ", parts);
    }

    // ---------------------------------------------------------------------------------------------
    // My Player.
    // ---------------------------------------------------------------------------------------------

    private void BuildPlayer()
    {
        PlayerPublicView view = _session.GetPlayerPublicView();
        CareerUi.Heading(_content, view.DisplayName);
        CareerUi.InfoRow(_content, "Age / foot", $"{view.Age}  ·  {view.PreferredFoot}");
        CareerUi.InfoRow(
            _content,
            "Emergent role",
            view.PrimaryRole + (view.SecondaryRoles.Count > 0
                ? $", also {string.Join(", ", view.SecondaryRoles)}"
                : string.Empty));
        CareerUi.InfoRow(
            _content,
            "Playing style",
            view.StyleTags.Count == 0 ? "—" : string.Join(", ", view.StyleTags));
        CareerUi.InfoRow(
            _content,
            "Condition / form",
            $"{view.Condition01 * 100f:0}% fit  ·  form {view.Form01 * 100f:0}%  ·  " +
            $"visibility {view.Visibility01 * 100f:0}%");

        CareerUi.Heading(_content, "COACH REPORT", 15);
        CareerUi.Paragraph(_content, view.CoachReport.Text, 13);

        foreach (PublicAttributeGroup group in view.AttributeGroups)
        {
            CareerUi.Heading(_content, group.Label.ToUpperInvariant(), 14);
            GridContainer grid = AddGrid(4);
            grid.AddChild(LabelCell("Attribute", CareerUi.MutedColor));
            grid.AddChild(LabelCell("Value", CareerUi.MutedColor));
            grid.AddChild(LabelCell("Trend", CareerUi.MutedColor));
            grid.AddChild(LabelCell("Type", CareerUi.MutedColor));
            foreach (PublicAttributeView attribute in group.Attributes)
            {
                grid.AddChild(LabelCell(attribute.Label));
                grid.AddChild(LabelCell(attribute.Current.ToString(), CareerUi.Accent));
                Color trendColor = attribute.TrendArrow == "^"
                    ? CareerUi.GoodColor
                    : attribute.TrendArrow == "v" ? CareerUi.WarnColor : CareerUi.MutedColor;
                grid.AddChild(LabelCell(attribute.TrendArrow, trendColor));
                grid.AddChild(LabelCell(attribute.Category.ToString(), CareerUi.MutedColor));
            }
        }

        CareerUi.Heading(_content, "EMERGENT ROLE SUITABILITY", 15);
        foreach (RoleSuitability suitability in view.RoleSuitabilities)
        {
            CareerUi.InfoRow(
                _content,
                suitability.Role.ToString(),
                $"{suitability.Score:0} / 100",
                labelWidth: 150f);
        }

        CareerUi.Heading(_content, "CAREER HISTORY", 15);
        if (_session.History.SeasonsPlayed == 0)
        {
            CareerUi.Paragraph(_content, "No completed seasons yet.", 13, CareerUi.MutedColor);
            return;
        }

        GridContainer history = AddGrid(9);
        foreach (string header in new[]
                 {
                     "Season", "Club", "Tier", "Apps", "Starts", "Goals", "Assists", "Rating", "Honours"
                 })
        {
            history.AddChild(LabelCell(header, CareerUi.MutedColor));
        }

        foreach (CareerSeasonRecord season in _session.History.Seasons)
        {
            history.AddChild(LabelCell(season.Season.ToString()));
            history.AddChild(LabelCell(season.ClubName));
            history.AddChild(LabelCell(season.Tier.ToString()));
            history.AddChild(LabelCell(season.Appearances.ToString()));
            history.AddChild(LabelCell(season.Starts.ToString()));
            history.AddChild(LabelCell(season.Goals.ToString()));
            history.AddChild(LabelCell(season.Assists.ToString()));
            history.AddChild(LabelCell(season.AverageRating > 0f ? $"{season.AverageRating:0.00}" : "—"));
            history.AddChild(LabelCell(
                season.Honours.Count == 0
                    ? season.DivisionMovement
                    : string.Join(", ", season.Honours) +
                      (season.DivisionMovement.Length == 0 ? string.Empty : $" · {season.DivisionMovement}")));
        }

        CareerUi.Paragraph(
            _content,
            $"Career totals: {_session.History.TotalAppearances} apps, {_session.History.TotalGoals} goals, " +
            $"{_session.History.TotalAssists} assists, {_session.History.CareerAverageRating:0.00} average rating.",
            13, CareerUi.Accent);
    }

    // ---------------------------------------------------------------------------------------------
    // Training.
    // ---------------------------------------------------------------------------------------------

    private void BuildTraining()
    {
        CareerUi.Heading(_content, "TRAINING");
        CareerUi.Paragraph(
            _content,
            $"Current focus: {_session.TrainingFocus}. One focus applies to the whole week; match minutes " +
            "accelerate skill growth, and body-trainable work carries explicit trade-offs.",
            13, CareerUi.MutedColor);

        foreach (TrainingFocusDescription description in TrainingPlanner.DescribeAll())
        {
            TrainingFocusDescription captured = description;
            bool active = _session.TrainingFocus == description.Focus;

            CareerUi.Heading(_content, description.Label + (active ? "  (active)" : string.Empty), 14);
            CareerUi.Paragraph(_content, description.Summary, 12, CareerUi.MutedColor);
            if (description.BoostedLabels.Count > 0)
            {
                CareerUi.Paragraph(
                    _content,
                    $"Boosts: {string.Join(", ", description.BoostedLabels)}",
                    12, CareerUi.GoodColor);
            }

            if (description.TradeOffNote.Length > 0)
            {
                CareerUi.Paragraph(_content, $"Trade-off: {description.TradeOffNote}", 12, CareerUi.WarnColor);
            }

            CareerUi.Button(
                _content,
                active ? "Focus already set" : $"Train: {description.Label}",
                () =>
                {
                    _session.SetTrainingFocus(captured.Focus);
                    _notice = $"Training focus set to {captured.Label}.";
                    Refresh();
                },
                enabled: !active);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Competitions.
    // ---------------------------------------------------------------------------------------------

    private void BuildCompetitions()
    {
        CareerUi.Heading(_content, "COMPETITIONS");

        OptionButton divisions = CareerUi.Option();
        for (int tier = 1; tier <= 6; tier++)
        {
            divisions.AddItem(_session.World.LeagueForTier(tier).Name);
        }

        divisions.Selected = _divisionTier - 1;
        divisions.ItemSelected += index =>
        {
            _divisionTier = (int)index + 1;
            Refresh();
        };
        CareerUi.FieldRow(_content, "Division", divisions);

        WorldLeague league = _session.World.LeagueForTier(_divisionTier);
        IReadOnlyList<LeagueTableRow> table = StandingsCalculator.Calculate(league);
        CareerUi.Heading(_content, "LEAGUE TABLE", 15);
        GridContainer grid = AddGrid(9);
        foreach (string header in new[] { "Pos", "Club", "P", "W", "D", "L", "GF", "GA", "Pts" })
        {
            grid.AddChild(LabelCell(header, CareerUi.MutedColor));
        }

        for (int index = 0; index < table.Count; index++)
        {
            LeagueTableRow row = table[index];
            grid.AddChild(LabelCell((index + 1).ToString()));
            grid.AddChild(LabelCell(ClubName(row.ClubId)));
            grid.AddChild(LabelCell(row.Played.ToString()));
            grid.AddChild(LabelCell(row.Won.ToString()));
            grid.AddChild(LabelCell(row.Drawn.ToString()));
            grid.AddChild(LabelCell(row.Lost.ToString()));
            grid.AddChild(LabelCell(row.GoalsFor.ToString()));
            grid.AddChild(LabelCell(row.GoalsAgainst.ToString()));
            grid.AddChild(LabelCell(row.Points.ToString(), CareerUi.Accent));
        }

        CareerUi.Heading(_content, "TOP SCORERS", 15);
        foreach (string line in TopScorersOf(league, 8)) CareerUi.Paragraph(_content, line, 13);

        CareerUi.Heading(_content, "RECENT RESULTS", 15);
        int shown = 0;
        for (int index = league.Fixtures.Count - 1; index >= 0 && shown < 6; index--)
        {
            WorldFixture fixture = league.Fixtures[index];
            if (!fixture.IsPlayed) continue;
            CareerUi.Paragraph(
                _content,
                $"Week {fixture.Week}: {ClubName(fixture.HomeClubId)} {fixture.HomeGoals}-" +
                $"{fixture.AwayGoals} {ClubName(fixture.AwayClubId)}",
                12);
            shown++;
        }

        if (shown == 0) CareerUi.Paragraph(_content, "No matches played yet.", 12, CareerUi.MutedColor);

        CareerUi.Heading(_content, "DOMESTIC CUP", 15);
        CupRoundBracket? round = _session.World.Cup.CurrentRound;
        if (round is null)
        {
            CareerUi.Paragraph(_content, "No cup round drawn yet.", 13, CareerUi.MutedColor);
        }
        else
        {
            CareerUi.Paragraph(
                _content,
                $"{_session.World.Cup.Name} — {round.Round} (week {round.Week}), " +
                $"{round.Ties.Count} ties, {round.Byes.Count} byes.",
                13, CareerUi.MutedColor);
            for (int index = 0; index < round.Ties.Count; index++)
            {
                WorldFixture tie = round.Ties[index].Fixture;
                string score = tie.IsPlayed
                    ? $" — {tie.HomeGoals}-{tie.AwayGoals}" +
                      (tie.HomePenaltyGoals + tie.AwayPenaltyGoals > 0
                          ? $" (pens {tie.HomePenaltyGoals}-{tie.AwayPenaltyGoals})"
                          : string.Empty)
                    : string.Empty;
                CareerUi.Paragraph(
                    _content,
                    $"{ClubName(tie.HomeClubId)} v {ClubName(tie.AwayClubId)}{score}",
                    12);
            }
        }
    }

    private IReadOnlyList<string> TopScorersOf(WorldLeague league, int maximum)
    {
        var scorers = new List<(PlayerId PlayerId, int Goals, ClubId ClubId)>();
        foreach (ClubId clubId in league.Clubs)
        {
            WorldClub club = _session.World.Clubs[clubId];
            foreach (PlayerId playerId in club.Squad)
            {
                WorldPlayer player = _session.World.Players[playerId];
                if (player.CurrentSeason.Goals <= 0) continue;
                scorers.Add((playerId, player.CurrentSeason.Goals, clubId));
            }
        }

        scorers.Sort(static (left, right) =>
        {
            int byGoals = right.Goals.CompareTo(left.Goals);
            return byGoals != 0 ? byGoals : left.PlayerId.Value.CompareTo(right.PlayerId.Value);
        });

        var lines = new List<string>(Math.Min(maximum, scorers.Count));
        for (int index = 0; index < scorers.Count && index < maximum; index++)
        {
            WorldPlayer player = _session.World.Players[scorers[index].PlayerId];
            lines.Add(
                $"{index + 1}. {player.DisplayName} ({ClubName(scorers[index].ClubId)}) — " +
                $"{scorers[index].Goals} goals");
        }

        return lines;
    }

    // ---------------------------------------------------------------------------------------------
    // Squad & club.
    // ---------------------------------------------------------------------------------------------

    private void BuildSquad()
    {
        WorldClub? club = _session.HumanClub;
        CareerUi.Heading(_content, "SQUAD & CLUB");
        if (club is null)
        {
            CareerUi.Paragraph(_content, "You are unattached: request an amateur trial from the Inbox.", 13,
                CareerUi.WarnColor);
            return;
        }

        CareerUi.InfoRow(_content, "Club", $"{club.Name} ({club.ShortCode})");
        CareerUi.InfoRow(_content, "Division", _session.World.LeagueForTier(club.Tier).Name);
        CareerUi.InfoRow(_content, "Stadium", $"{club.Stadium}, {club.City} — {club.Capacity:N0} seats");
        CareerUi.InfoRow(
            _content,
            "Finances",
            $"balance {club.Finances.Balance:N0}  ·  wage bill {club.Finances.WeeklyWageBill:N0} of " +
            $"{club.Finances.WeeklyWageBudget:N0}  ·  transfer budget {club.Finances.TransferBudget:N0}");
        CareerUi.InfoRow(
            _content,
            "Facilities",
            $"coaching {club.Facilities.CoachingQuality01:0.00}  ·  medical " +
            $"{club.Facilities.MedicalQuality01:0.00}  ·  youth {club.Facilities.YouthQuality01:0.00}  ·  " +
            $"{club.Facilities.WeeklyTrainingSessions} sessions/week");

        if (_session.World.Managers.TryGet(club.ManagerId, out WorldManager? manager) && manager is not null)
        {
            CareerUi.InfoRow(
                _content,
                "Manager",
                $"{manager.DisplayName} — {manager.TacticalProfile.Name}, {manager.Formation}, " +
                $"board confidence {manager.BoardConfidence01 * 100f:0}%");
        }

        CareerUi.Heading(_content, "SQUAD", 15);
        SelectedLineup lineup = SquadSelector.Select(_session.World, club);
        GridContainer grid = AddGrid(7);
        foreach (string header in new[] { "Player", "Role", "Age", "Cond", "Form", "Value", "Status" })
        {
            grid.AddChild(LabelCell(header, CareerUi.MutedColor));
        }

        foreach (PlayerId playerId in club.Squad)
        {
            WorldPlayer player = _session.World.Players[playerId];
            if (player.IsRetired) continue;

            string status = lineup.StartingXI.Contains(playerId)
                ? "XI"
                : lineup.Bench.Contains(playerId) ? "Bench" : player.IsAvailableForSelection ? "Squad" : "Out";
            if (playerId == _session.HumanPlayerId) status = $"YOU ({status})";

            grid.AddChild(LabelCell(player.DisplayName));
            grid.AddChild(LabelCell(playerId == _session.HumanPlayerId
                ? IdentityEvaluator.Evaluate(player).PrimaryRole.ToString()
                : SquadSelector.SquadStatusOf(_session.World, club, playerId)));
            grid.AddChild(LabelCell(player.Age.ToString()));
            grid.AddChild(LabelCell($"{player.Condition01 * 100f:0}%"));
            grid.AddChild(LabelCell($"{player.Form.Form01() * 100f:0}%"));
            grid.AddChild(LabelCell($"{player.Attributes.MeanCurrent():0.0}"));
            grid.AddChild(LabelCell(status));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Inbox.
    // ---------------------------------------------------------------------------------------------

    private void BuildInbox()
    {
        CareerUi.Heading(_content, "INBOX & OFFERS");
        CareerUi.Paragraph(
            _content,
            $"{_session.Inbox.UnreadCount} unread of {_session.Inbox.Count} messages. Offers must be answered " +
            "before the career clock can advance.",
            13, CareerUi.MutedColor);

        if (_session.HumanPlayer.ClubId.IsValid == false)
        {
            CareerUi.Button(_content, "Request Amateur Trial", RequestTrial, primary: true);
        }

        IReadOnlyList<CareerInboxMessage> messages = _session.Inbox.Messages;
        if (messages.Count == 0)
        {
            CareerUi.Paragraph(_content, "No messages yet.", 13, CareerUi.MutedColor);
            return;
        }

        CareerUi.Heading(_content, "MESSAGES", 15);
        for (int index = messages.Count - 1; index >= 0; index--)
        {
            CareerInboxMessage message = messages[index];
            int captured = message.Id;
            string marker = message.IsResolved ? "✓ " : message.IsRead ? "· " : "● ";
            CareerUi.Button(
                _content,
                $"{marker}{message.Subject}  (week {message.Week})",
                () =>
                {
                    _selectedMessageId = captured;
                    _session.Inbox.MarkRead(captured);
                    _notice = string.Empty;
                    Refresh();
                },
                primary: message.Id == _selectedMessageId);
        }

        if (_session.Inbox.Get(_selectedMessageId) is { } selected)
        {
            CareerUi.Heading(_content, selected.Subject, 15);
            CareerUi.Paragraph(_content, selected.Body, 13);
            CareerUi.InfoRow(
                _content,
                "Status",
                selected.IsResolved
                    ? "Answered"
                    : selected.IsAwaitingResponse(_session.Season, _session.Date.Week)
                        ? $"Awaiting your answer (expires week {selected.ExpiresWeek})"
                        : "Informational");

            if (selected.IsAwaitingResponse(_session.Season, _session.Date.Week))
            {
                HBoxContainer actions = CareerUi.Row(_content, separation: 10);
                int capturedId = selected.Id;
                CareerUi.Button(actions, "Accept", () => AnswerOffer(capturedId, accept: true), primary: true);
                CareerUi.Button(actions, "Decline", () => AnswerOffer(capturedId, accept: false));
            }

            if (selected is CoachReportMessage report)
            {
                CareerUi.InfoRow(_content, "Verdict", report.Summary);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Actions.
    // ---------------------------------------------------------------------------------------------

    private void AdvanceWeek()
    {
        try
        {
            _lastEvent = _session.AdvanceToNextEventOrMatchday();
            _notice = string.Empty;
            if (_lastEvent.Kind == CareerEventKind.InboxAction && _lastEvent.Message is { } message)
            {
                _selectedMessageId = message.Id;
                _tab = Tab.Inbox;
            }
            else if (_lastEvent.Kind == CareerEventKind.SeasonRolledOver)
            {
                Autosave();
            }
            else if (_lastEvent.Kind == CareerEventKind.HumanMatchday)
            {
                _tab = Tab.Overview;
            }

            Refresh();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _notice = exception.Message;
            Refresh();
        }
    }

    private void SimMatch()
    {
        try
        {
            IReadOnlyList<CareerMatchResult> results = _session.ResolveHumanMatchViaQuickSim();
            _lastMatchSummary = results.Count == 0
                ? string.Empty
                : "Simulated: " + string.Join(" | ", results.Select(result => result.ToString()));
            _lastEvent = _session.AdvanceToNextEventOrMatchday();
            _tab = Tab.Overview;
            Autosave();
            Refresh();
        }
        catch (InvalidOperationException exception)
        {
            _notice = exception.Message;
            Refresh();
        }
    }

    private void PlayMatchLive()
    {
        if (_lastEvent?.Kind != CareerEventKind.HumanMatchday)
        {
            _notice = "Advance to your matchday first.";
            Refresh();
            return;
        }

        try
        {
            WorldMatchSetup setup = _session.PrepareHumanLiveMatchSetup(CareerMatchHalfSeconds);
            if (setup.HumanEnginePlayerId is null)
            {
                ShowBenchPopup();
                return;
            }

            GameSession.PendingCareerMatch = setup;
            GetTree().ChangeSceneToFile(GameSession.MatchScene);
        }
        catch (InvalidOperationException exception)
        {
            _notice = exception.Message;
            Refresh();
        }
    }

    /// <summary>
    /// W7 bench-cameo flow: the human starts on the bench. He can let the match be simulated from the bench
    /// (the quick sim models the substitution) or play a short live cameo with himself forced into the XI.
    /// A true mid-match substitution inside a running engine is the W9 backlog item.
    /// </summary>
    private void ShowBenchPopup()
    {
        Control overlay = CreateOverlay(out VBoxContainer box);
        CareerUi.Heading(box, "YOU'RE ON THE BENCH", 19);
        WorldFixture? fixture = _session.HumanFixturesThisWeek().Count > 0
            ? _session.HumanFixturesThisWeek()[0]
            : _session.NextFixture();
        string match = fixture is null
            ? "today's match"
            : $"{ClubName(fixture.HomeClubId)} v {ClubName(fixture.AwayClubId)}";
        CareerUi.Paragraph(
            box,
            $"The manager has left you out of the starting XI for {match}. You can watch the match be " +
            "simulated from the bench (the substitute's appearance is still modelled), or take a short live " +
            "cameo with yourself forced on.",
            13, CareerUi.MutedColor);

        CareerUi.Button(box, "Sim Match from the Bench", () =>
        {
            overlay.QueueFree();
            _session.DiscardLiveMatch();
            SimMatch();
        }, primary: true);

        CareerUi.Button(box, "Play Cameo (short live segment)", () =>
        {
            overlay.QueueFree();
            _session.DiscardLiveMatch();
            try
            {
                WorldMatchSetup cameo = _session.PrepareHumanLiveMatchSetup(
                    CameoHalfSeconds, forceHumanStarter: true);
                GameSession.PendingCareerMatch = cameo;
                GetTree().ChangeSceneToFile(GameSession.MatchScene);
            }
            catch (InvalidOperationException exception)
            {
                _notice = exception.Message;
                Refresh();
            }
        });

        CareerUi.Button(box, "Back", () =>
        {
            overlay.QueueFree();
            _session.DiscardLiveMatch();
            Refresh();
        });
    }

    /// <summary>Builds a dark centred overlay over the hub and returns the VBox content host.</summary>
    private Control CreateOverlay(out VBoxContainer box)
    {
        var overlay = new Control { MouseFilter = MouseFilterEnum.Stop };
        overlay.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(overlay);

        var background = new ColorRect { Color = new Color(0f, 0f, 0f, 0.62f) };
        background.SetAnchorsPreset(LayoutPreset.FullRect);
        overlay.AddChild(background);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        overlay.AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(620f, 0f) };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.055f, 0.105f, 0.07f, 0.98f),
            BorderColor = new Color(0.18f, 0.43f, 0.26f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1
        };
        style.SetCornerRadiusAll(16);
        panel.AddThemeStyleboxOverride("panel", style);
        center.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        panel.AddChild(margin);

        box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        margin.AddChild(box);
        return overlay;
    }

    private void AnswerOffer(int messageId, bool accept)
    {
        bool answered = _session.RespondToOffer(messageId, accept);
        _notice = answered
            ? (accept ? "Offer accepted." : "Offer declined.")
            : "That offer can no longer be answered.";
        if (answered) _lastEvent = null;
        Autosave();
        Refresh();
    }

    private void RequestTrial()
    {
        try
        {
            CareerInboxMessage invite = _session.RequestAmateurTrial();
            _selectedMessageId = invite.Id;
            _tab = Tab.Inbox;
            _notice = "A trial invite is waiting: accept it to sign.";
        }
        catch (InvalidOperationException exception)
        {
            _notice = exception.Message;
        }

        Refresh();
    }

    private void Autosave()
    {
        if (!CareerSaveFiles.TryAutosave(_session, out string error) && error.Length > 0)
        {
            _notice = error;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Dev-only boot check support.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Development-only: rebuilds every hub panel once so the W6 headless boot check exercises all six
    /// screens (and their data queries) in one run. Never called by the game itself.
    /// </summary>
    public void BootCheckExercisePanels()
    {
        Tab original = _tab;
        for (int index = 0; index < TabLabels.Length; index++)
        {
            _tab = (Tab)index;
            Refresh();
            GD.Print($"CAREER_BOOT: panel {TabLabels[index]} ok");
        }

        _tab = original;
        Refresh();
    }

    // ---------------------------------------------------------------------------------------------
    // Small UI helpers.
    // ---------------------------------------------------------------------------------------------

    private GridContainer AddGrid(int columns)
    {
        var grid = new GridContainer { Columns = columns, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 2);
        _content.AddChild(grid);
        return grid;
    }

    private static Label LabelCell(string text, Color? color = null)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", color ?? CareerUi.TextColor);
        return label;
    }

    private string ClubName(ClubId clubId) =>
        _session.World.Clubs.TryGet(clubId, out WorldClub? club) && club is not null ? club.Name : "—";
}
