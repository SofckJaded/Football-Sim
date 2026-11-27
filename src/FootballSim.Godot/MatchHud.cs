using System;
using Godot;
using FootballSim;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Rules;

namespace FootballSim.GodotClient;

/// <summary>
/// The in-match HUD: one compact scoreboard in the top-left corner and two equal player cards at the
/// bottom corners, drawn flat and aligned to a single 18 px margin grid.
///
/// The old wall of debug/control/context text is gone. The left card is always the controlled human
/// player while human control is active (number ring, position, stamina, charge); the right card shows
/// the ball carrier or nearest opponent. Both cards use exactly the same geometry, so the corners read
/// as a pair; the only asymmetric element is the charge ring around the active player's number, which is
/// where the old power bar lives (a ring does not need a second row and keeps the cards the same size).
/// </summary>
public partial class MatchHud : Control
{
    // --- palette ---------------------------------------------------------------------------------
    private static readonly Color PanelBackground = new(0.030f, 0.052f, 0.075f, 0.80f);
    private static readonly Color PanelBorder = new(1f, 1f, 1f, 0.070f);
    private static readonly Color TextPrimary = new(0.94f, 0.96f, 0.99f);
    private static readonly Color TextSecondary = new(0.70f, 0.78f, 0.86f);
    private static readonly Color TextMuted = new(0.45f, 0.53f, 0.61f);
    private static readonly Color Track = new(1f, 1f, 1f, 0.14f);
    private static readonly Color HomeAccent = new(0.34f, 0.63f, 1.00f);
    private static readonly Color AwayAccent = new(1.00f, 0.42f, 0.42f);
    private static readonly Color StaminaGood = new(0.40f, 0.86f, 0.58f);
    private static readonly Color StaminaMid = new(0.96f, 0.76f, 0.30f);
    private static readonly Color StaminaLow = new(0.95f, 0.36f, 0.36f);
    private static readonly Color PowerFill = new(0.55f, 0.85f, 1.00f);

    // --- geometry (all on the same 18 px margin) -------------------------------------------------
    private const float EdgeMargin = 18f;
    private const float ScoreboardWidth = 232f;
    private const float ScoreboardHeight = 42f;
    private const float CardWidth = 252f;
    private const float CardHeight = 58f;

    private Font _font = null!;
    private StyleBoxFlat _panelStyle = null!;
    private StyleBoxFlat _fillStyle = null!;
    private StyleBoxFlat _trackStyle = null!;

    private MatchPhase _phase = MatchPhase.PreKickoff;
    private float _clockSeconds;
    private int _homeScore;
    private int _awayScore;
    private CardView _activeCard;
    private CardView _focusCard;
    private bool _hasActiveCard;
    private bool _hasFocusCard;
    private bool _humanControlActive;
    private float _chargedPower01;
    private bool _charging;
    private bool _highModifier;

    /// <summary>Home side's scoreboard label (the real club name in a career match, "HOME" otherwise).</summary>
    private string _homeLabel = "HOME";

    /// <summary>Away side's scoreboard label (the real club name in a career match, "AWAY" otherwise).</summary>
    private string _awayLabel = "AWAY";

    private string HomeLabel => _homeLabel.ToUpperInvariant();

    private string AwayLabel => _awayLabel.ToUpperInvariant();

    /// <summary>
    /// W7: replaces the generic HOME/AWAY scoreboard labels with the real club names (a career match). The
    /// quick-match path never calls it, so its HUD is byte-identical to before.
    /// </summary>
    /// <param name="homeName">Home club display name.</param>
    /// <param name="awayName">Away club display name.</param>
    public void SetTeamNames(string homeName, string awayName)
    {
        _homeLabel = string.IsNullOrWhiteSpace(homeName) ? "HOME" : homeName;
        _awayLabel = string.IsNullOrWhiteSpace(awayName) ? "AWAY" : awayName;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _font = GetThemeDefaultFont();

        _panelStyle = new StyleBoxFlat
        {
            BorderColor = PanelBorder,
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1
        };
        _panelStyle.SetCornerRadiusAll(7);

        _fillStyle = new StyleBoxFlat();
        _fillStyle.SetCornerRadiusAll(2);

        _trackStyle = new StyleBoxFlat { BgColor = Track };
        _trackStyle.SetCornerRadiusAll(2);
    }

    public void Refresh(
        MatchRulesEngine rules,
        FootballerState? active,
        FootballerState? focus,
        HumanInputController? human,
        bool humanControlActive,
        bool activeIsBallCarrier,
        bool focusIsBallCarrier)
    {
        _phase = rules.Phase;
        _clockSeconds = rules.MatchClockSeconds;
        _homeScore = rules.HomeScore;
        _awayScore = rules.AwayScore;

        _humanControlActive = humanControlActive && human is { IsSpectatorMode: false };
        _charging = _humanControlActive && human is { ChargingIntent: not KickIntentType.None };
        _chargedPower01 = _charging ? Mathf.Clamp(human!.ChargedPower01, 0f, 1f) : 0f;
        _highModifier = _humanControlActive && human is { HighModifierHeld: true };

        _hasActiveCard = active is not null;
        if (active is not null)
        {
            string activeTag = _humanControlActive
                ? "YOU"
                : activeIsBallCarrier ? "BALL CARRIER" : "ACTIVE";
            _activeCard = CardView.From(active, activeTag);
        }

        _hasFocusCard = focus is not null;
        if (focus is not null)
        {
            string focusTag = focusIsBallCarrier
                ? "BALL CARRIER"
                : focus.Team != active?.Team ? "OPPONENT" : "TEAMMATE";
            _focusCard = CardView.From(focus, focusTag);
        }

        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_font is null) return;

        Vector2 viewport = Size;
        if (viewport.X < 320f || viewport.Y < 200f) return;

        DrawScoreboard(new Vector2(EdgeMargin, EdgeMargin));

        float cardY = viewport.Y - EdgeMargin - CardHeight;
        if (_hasActiveCard)
        {
            DrawPlayerCard(new Rect2(EdgeMargin, cardY, CardWidth, CardHeight), _activeCard,
                mirror: false, showCharge: _humanControlActive);
        }

        if (_hasFocusCard)
        {
            DrawPlayerCard(new Rect2(viewport.X - EdgeMargin - CardWidth, cardY, CardWidth, CardHeight),
                _focusCard, mirror: true, showCharge: false);
        }

        DrawMatchBanner(viewport);
    }

    // ---------------------------------------------------------------------------------------------
    // Scoreboard
    // ---------------------------------------------------------------------------------------------

    private void DrawScoreboard(Vector2 topLeft)
    {
        var board = new Rect2(topLeft, new Vector2(ScoreboardWidth, ScoreboardHeight));
        DrawPanel(board);

        float left = board.Position.X;
        float top = board.Position.Y;
        float right = board.End.X;
        const float dividerOffset = 82f;
        float dividerInset = 7f;

        DrawAccentBar(new Rect2(left + 11f, top + 6f, 2f, 30f), HomeAccent);
        DrawAccentBar(new Rect2(right - 13f, top + 6f, 2f, 30f), AwayAccent);

        Color divider = new(1f, 1f, 1f, 0.075f);
        DrawRect(new Rect2(left + dividerOffset, top + dividerInset, 1f, ScoreboardHeight - dividerInset * 2f), divider);
        DrawRect(new Rect2(right - dividerOffset, top + dividerInset, 1f, ScoreboardHeight - dividerInset * 2f), divider);

        DrawText(HomeLabel, new Vector2(left + 20f, top + 6f), 8, TextMuted);
        DrawText(_homeScore.ToString(), new Vector2(left + 20f, top + 15f), 20, TextPrimary);

        DrawTextRight(AwayLabel, new Vector2(right - 20f, top + 6f), 8, TextMuted);
        DrawTextRight(_awayScore.ToString(), new Vector2(right - 20f, top + 15f), 20, TextPrimary);

        float centreWidth = right - dividerOffset - (left + dividerOffset);
        string clock = FormatClock(_clockSeconds);
        DrawTextCentered(clock, new Vector2(left + dividerOffset, top + 10f), centreWidth, 14,
            _phase == MatchPhase.FullTime ? TextSecondary : TextPrimary);
        DrawTextCentered(PhaseLabel(_phase), new Vector2(left + dividerOffset, top + 27f), centreWidth, 7, TextMuted);
    }

    // ---------------------------------------------------------------------------------------------
    // Player cards
    // ---------------------------------------------------------------------------------------------

    private void DrawPlayerCard(Rect2 card, in CardView view, bool mirror, bool showCharge)
    {
        DrawPanel(card);

        float accentX = mirror ? card.End.X - 10f : card.Position.X + 8f;
        DrawAccentBar(new Rect2(accentX, card.Position.Y + 9f, 2f, card.Size.Y - 18f), view.TeamAccent);

        // Number ring: faint track on both cards so the corners stay identical; the active human's
        // charge fills it. The old power bar was the only asymmetric row in the HUD.
        float ringRadius = 17.5f;
        var ringCentre = new Vector2(
            mirror ? card.End.X - 34f : card.Position.X + 34f,
            card.Position.Y + 29f);
        DrawArc(ringCentre, ringRadius, -Mathf.Pi * 0.5f, Mathf.Pi * 1.5f, 42,
            new Color(1f, 1f, 1f, 0.10f), 2f, true);
        if (showCharge && _charging && _chargedPower01 > 0.001f)
        {
            DrawArc(ringCentre, ringRadius, -Mathf.Pi * 0.5f,
                -Mathf.Pi * 0.5f + Mathf.Pi * 2f * _chargedPower01, 42,
                PowerFill, 2f, true);
        }

        DrawTextCenteredAt($"#{view.ShirtNumber}", ringCentre, 17, TextPrimary);

        float roleX = mirror ? card.End.X - 56f : card.Position.X + 58f;
        if (mirror)
        {
            DrawTextRight(view.RoleLabel, new Vector2(roleX, card.Position.Y + 10f), 9, TextSecondary);
            DrawTextRight(view.TeamLabel, new Vector2(roleX, card.Position.Y + 23f), 7, TextMuted);
            DrawText(view.Tag, new Vector2(card.Position.X + 14f, card.Position.Y + 9f), 7,
                view.IsHuman ? HomeAccent : TextMuted);
            if (showCharge && _highModifier)
                DrawText("HIGH", new Vector2(card.Position.X + 14f, card.Position.Y + 18f), 7, PowerFill);
        }
        else
        {
            DrawText(view.RoleLabel, new Vector2(roleX, card.Position.Y + 10f), 9, TextSecondary);
            DrawText(view.TeamLabel, new Vector2(roleX, card.Position.Y + 23f), 7, TextMuted);
            DrawTextRight(view.Tag, new Vector2(card.End.X - 14f, card.Position.Y + 9f), 7,
                view.IsHuman ? HomeAccent : TextMuted);
            if (showCharge && _highModifier)
                DrawTextRight("HIGH", new Vector2(card.End.X - 14f, card.Position.Y + 18f), 7, PowerFill);
        }

        float stamina = Mathf.Clamp(view.Stamina01, 0f, 1f);
        Color staminaColor = stamina > 0.6f ? StaminaGood : stamina > 0.3f ? StaminaMid : StaminaLow;
        DrawStaminaBar(card, mirror, stamina, staminaColor);
    }

    private void DrawStaminaBar(Rect2 card, bool mirror, float stamina, Color fill)
    {
        float labelY = card.Position.Y + 35f;
        float barY = card.Position.Y + 45f;
        float barLeft = card.Position.X + 58f;
        float barWidth = card.Size.X - 58f - 14f;
        string percent = $"{Mathf.RoundToInt(stamina * 100f)}%";

        if (mirror)
        {
            DrawTextRight("STAMINA", new Vector2(card.End.X - 56f, labelY), 7, TextMuted);
            DrawText(percent, new Vector2(card.Position.X + 14f, labelY), 7, TextMuted);
        }
        else
        {
            DrawText("STAMINA", new Vector2(card.Position.X + 58f, labelY), 7, TextMuted);
            DrawTextRight(percent, new Vector2(card.End.X - 14f, labelY), 7, TextMuted);
        }

        var bar = new Rect2(barLeft, barY, barWidth, 3f);
        DrawStyleBox(_trackStyle, bar);

        float fillWidth = Mathf.Round(barWidth * stamina);
        if (fillWidth < 1f) return;

        _fillStyle.BgColor = fill;
        var fillRect = mirror
            ? new Rect2(bar.End.X - fillWidth, bar.Position.Y, fillWidth, bar.Size.Y)
            : new Rect2(bar.Position, new Vector2(fillWidth, bar.Size.Y));
        DrawStyleBox(_fillStyle, fillRect);
    }

    // ---------------------------------------------------------------------------------------------
    // Match phase banner
    // ---------------------------------------------------------------------------------------------

    private void DrawMatchBanner(Vector2 viewport)
    {
        if (_phase == MatchPhase.FullTime)
        {
            DrawBanner(viewport, new Vector2(340f, 102f), "FULL TIME",
                $"{HomeLabel}  {_homeScore}  –  {_awayScore}  {AwayLabel}", "ESC  ·  MAIN MENU", 24, 11);
            return;
        }

        if (_phase == MatchPhase.HalfTime)
        {
            DrawBanner(viewport, new Vector2(248f, 62f), "HALF TIME",
                $"{HomeLabel}  {_homeScore}  –  {_awayScore}  {AwayLabel}", string.Empty, 18, 10);
        }
    }

    private void DrawBanner(Vector2 viewport, Vector2 size, string title, string subtitle, string hint,
        int titleSize, int subtitleSize)
    {
        var rect = new Rect2((viewport - size) * 0.5f, size);
        DrawPanel(rect);

        float width = rect.Size.X;
        DrawTextCentered(title, new Vector2(rect.Position.X, rect.Position.Y + 16f), width, titleSize, TextPrimary);

        if (!string.IsNullOrEmpty(subtitle))
        {
            DrawTextCentered(subtitle, new Vector2(rect.Position.X, rect.Position.Y + 16f + titleSize + 10f),
                width, subtitleSize, TextSecondary);
        }

        if (!string.IsNullOrEmpty(hint))
        {
            DrawTextCentered(hint, new Vector2(rect.Position.X, rect.End.Y - 20f), width, 7, TextMuted);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Drawing helpers
    // ---------------------------------------------------------------------------------------------

    private void DrawPanel(Rect2 rect)
    {
        _panelStyle.BgColor = PanelBackground;
        _panelStyle.BorderColor = PanelBorder;
        DrawStyleBox(_panelStyle, rect);
    }

    private void DrawAccentBar(Rect2 rect, Color color) => DrawRect(rect, color);

    private void DrawText(string text, Vector2 topLeft, int fontSize, Color color) =>
        DrawString(_font, new Vector2(topLeft.X, topLeft.Y + _font.GetAscent(fontSize)), text,
            HorizontalAlignment.Left, -1f, fontSize, color);

    private void DrawTextRight(string text, Vector2 topRight, int fontSize, Color color)
    {
        float width = _font.GetStringSize(text, HorizontalAlignment.Left, -1f, fontSize).X;
        DrawString(_font, new Vector2(topRight.X - width, topRight.Y + _font.GetAscent(fontSize)), text,
            HorizontalAlignment.Left, -1f, fontSize, color);
    }

    private void DrawTextCentered(string text, Vector2 topLeft, float width, int fontSize, Color color)
    {
        float textWidth = _font.GetStringSize(text, HorizontalAlignment.Left, -1f, fontSize).X;
        float x = topLeft.X + (width - textWidth) * 0.5f;
        DrawString(_font, new Vector2(x, topLeft.Y + _font.GetAscent(fontSize)), text,
            HorizontalAlignment.Left, -1f, fontSize, color);
    }

    private void DrawTextCenteredAt(string text, Vector2 centre, int fontSize, Color color)
    {
        float textWidth = _font.GetStringSize(text, HorizontalAlignment.Left, -1f, fontSize).X;
        float x = centre.X - textWidth * 0.5f;
        float ascent = _font.GetAscent(fontSize);
        float descent = _font.GetDescent(fontSize);
        float baselineY = centre.Y + (ascent - descent) * 0.5f;
        DrawString(_font, new Vector2(x, baselineY), text, HorizontalAlignment.Left, -1f, fontSize, color);
    }

    // ---------------------------------------------------------------------------------------------
    // Formatting
    // ---------------------------------------------------------------------------------------------

    private static string FormatClock(float seconds)
    {
        int total = Mathf.Max(0, Mathf.FloorToInt(seconds));
        return $"{total / 60:D2}:{total % 60:D2}";
    }

    private static string PhaseLabel(MatchPhase phase) => phase switch
    {
        MatchPhase.PreKickoff => "KICK-OFF",
        MatchPhase.FirstHalf => "FIRST HALF",
        MatchPhase.HalfTime => "HALF TIME",
        MatchPhase.SecondHalf => "SECOND HALF",
        MatchPhase.GoalScored => "GOAL",
        MatchPhase.ThrowIn => "THROW-IN",
        MatchPhase.GoalKick => "GOAL KICK",
        MatchPhase.CornerKick => "CORNER",
        MatchPhase.FreeKick => "FREE KICK",
        MatchPhase.PenaltyKick => "PENALTY",
        MatchPhase.FullTime => "FULL TIME",
        _ => phase.ToString().ToUpperInvariant()
    };

    private static string DescribeRoleLabel(FootballerState player)
    {
        if (player.Team == TeamId.Home && player.ShirtNumber >= 1 && player.ShirtNumber <= 11)
        {
            MatchPositionOption option = MatchPositions.Find(player.ShirtNumber);
            if (option.PlayerId == player.ShirtNumber) return option.Label.ToUpperInvariant();
        }

        return player.Role switch
        {
            TacticalRole.Goalkeeper => "GOALKEEPER",
            TacticalRole.CenterBack => "CENTRE BACK",
            TacticalRole.FullBack => "FULL BACK",
            TacticalRole.DefensiveMidfielder => "DEFENSIVE MID",
            TacticalRole.CentralMidfielder => "CENTRAL MID",
            TacticalRole.Winger => "WINGER",
            TacticalRole.Striker => "STRIKER",
            _ => player.Role.ToString().ToUpperInvariant()
        };
    }

    private readonly record struct CardView(
        int ShirtNumber,
        string RoleLabel,
        string TeamLabel,
        Color TeamAccent,
        float Stamina01,
        string Tag,
        bool IsHuman)
    {
        public static CardView From(FootballerState player, string tag) => new(
            player.ShirtNumber,
            DescribeRoleLabel(player),
            player.Team == TeamId.Home ? "HOME" : "AWAY",
            player.Team == TeamId.Home ? HomeAccent : AwayAccent,
            player.Stamina01,
            tag,
            tag == "YOU");
    }
}
