using System;
using Godot;
using FootballSim;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Rules;

namespace FootballSim.GodotClient;

/// <summary>
/// The in-match HUD: a scoreboard in the top-left corner and two player cards (bottom-left and
/// bottom-right) drawn with flat panels, thin bars and upper-case micro labels.
///
/// It replaces the old wall of debug text. The debug text overlay still exists in
/// <see cref="MatchClientNode"/> behind <c>ShowDebugOverlay</c> (off by default), but this control is
/// what a player sees: no control listing, no contract readouts, no match-context lines.
///
/// Everything is custom-painted in <see cref="_Draw"/> rather than built from Labels/ProgressBars: the
/// HUD is small, fixed-proportion and needs the same visual weight at every supported resolution, and a
/// single control redraw is cheaper and easier to keep minimal than a tree of themed containers.
/// </summary>
public partial class MatchHud : Control
{
    // --- palette ---------------------------------------------------------------------------------
    private static readonly Color PanelBackground = new(0.035f, 0.058f, 0.082f, 0.80f);
    private static readonly Color PanelBorder = new(1f, 1f, 1f, 0.07f);
    private static readonly Color TextPrimary = new(0.94f, 0.96f, 0.99f);
    private static readonly Color TextSecondary = new(0.70f, 0.78f, 0.86f);
    private static readonly Color TextMuted = new(0.47f, 0.55f, 0.63f);
    private static readonly Color Track = new(1f, 1f, 1f, 0.13f);
    private static readonly Color HomeAccent = new(0.34f, 0.63f, 1.00f);
    private static readonly Color AwayAccent = new(1.00f, 0.42f, 0.42f);
    private static readonly Color StaminaGood = new(0.40f, 0.86f, 0.58f);
    private static readonly Color StaminaMid = new(0.96f, 0.76f, 0.30f);
    private static readonly Color StaminaLow = new(0.95f, 0.36f, 0.36f);
    private static readonly Color PowerFill = new(0.55f, 0.85f, 1.00f);

    // --- geometry --------------------------------------------------------------------------------
    private const float EdgeMargin = 28f;
    private const float ScoreboardWidth = 306f;
    private const float ScoreboardHeight = 76f;
    private const float CardWidth = 340f;
    private const float CardHeight = 96f;

    private Font _font = null!;
    private StyleBoxFlat _panelStyle = null!;
    private StyleBoxFlat _fillStyle = null!;
    private StyleBoxFlat _trackStyle = null!;
    private StyleBoxFlat _chipStyle = null!;

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
        _panelStyle.SetCornerRadiusAll(10);

        _fillStyle = new StyleBoxFlat();
        _fillStyle.SetCornerRadiusAll(2);

        _trackStyle = new StyleBoxFlat { BgColor = Track };
        _trackStyle.SetCornerRadiusAll(2);

        _chipStyle = new StyleBoxFlat();
        _chipStyle.SetCornerRadiusAll(4);
    }

    /// <summary>
    /// Pushes one frame of state. The caller resolves which two players are shown; the HUD only formats
    /// them. Called every rendered frame, so a score, clock or stamina change is visible immediately.
    /// </summary>
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
        if (viewport.X < 360f || viewport.Y < 240f) return;

        DrawScoreboard(new Vector2(EdgeMargin, 22f));

        float cardY = viewport.Y - EdgeMargin - CardHeight;
        if (_hasActiveCard)
            DrawPlayerCard(new Rect2(EdgeMargin, cardY, CardWidth, CardHeight), _activeCard,
                mirror: false, showPower: _humanControlActive);
        if (_hasFocusCard)
            DrawPlayerCard(new Rect2(viewport.X - EdgeMargin - CardWidth, cardY, CardWidth, CardHeight),
                _focusCard, mirror: true, showPower: false);

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

        // Team accent chips. The colour is the only team identity in the HUD; the labels say HOME/AWAY.
        DrawAccentBar(new Rect2(left + 14f, top + 13f, 3f, 50f), HomeAccent);
        DrawAccentBar(new Rect2(right - 17f, top + 13f, 3f, 50f), AwayAccent);

        // Dividers around the clock.
        Color divider = new(1f, 1f, 1f, 0.08f);
        DrawRect(new Rect2(left + 104f, top + 13f, 1f, 50f), divider);
        DrawRect(new Rect2(right - 104f, top + 13f, 1f, 50f), divider);

        int scoreSize = 26;
        int teamSize = 10;
        int clockSize = 18;
        int phaseSize = 8;

        DrawText("HOME", new Vector2(left + 26f, top + 15f), teamSize, TextMuted);
        DrawText(_homeScore.ToString(), new Vector2(left + 26f, top + 34f), scoreSize, TextPrimary);

        DrawTextRight("AWAY", new Vector2(right - 29f, top + 15f), teamSize, TextMuted);
        DrawTextRight(_awayScore.ToString(), new Vector2(right - 29f, top + 34f), scoreSize, TextPrimary);

        string clock = FormatClock(_clockSeconds);
        DrawTextCentered(clock, new Vector2(left + 104f, top + 17f), right - 104f - left - 104f,
            clockSize, _phase == MatchPhase.FullTime ? TextSecondary : TextPrimary);

        DrawTextCentered(PhaseLabel(_phase), new Vector2(left + 104f, top + 45f),
            right - 104f - left - 104f, phaseSize, TextMuted);
    }

    // ---------------------------------------------------------------------------------------------
    // Player cards
    // ---------------------------------------------------------------------------------------------

    private void DrawPlayerCard(Rect2 card, in CardView view, bool mirror, bool showPower)
    {
        DrawPanel(card);

        Color accent = view.TeamAccent;
        float pad = 14f;
        float accentX = mirror ? card.End.X - pad : card.Position.X + pad;
        DrawAccentBar(new Rect2(accentX, card.Position.Y + 14f, 3f, card.Size.Y - 28f), accent);

        float innerLeft = card.Position.X + 26f;
        float innerRight = card.End.X - 26f;
        float numberLeft = mirror ? innerRight : innerLeft;

        if (mirror)
        {
            DrawTextRight($"#{view.ShirtNumber}", new Vector2(innerRight, card.Position.Y + 12f), 30, TextPrimary);
            DrawTextRight(view.RoleLabel, new Vector2(innerRight - 54f, card.Position.Y + 20f), 13, TextSecondary);
            DrawTextRight(view.TeamLabel, new Vector2(innerRight - 54f, card.Position.Y + 38f), 8, TextMuted);
            DrawText(view.Tag, new Vector2(innerLeft, card.Position.Y + 20f), 8,
                view.IsHuman ? HomeAccent : TextMuted);
        }
        else
        {
            DrawText($"#{view.ShirtNumber}", new Vector2(numberLeft, card.Position.Y + 12f), 30, TextPrimary);
            DrawText(view.RoleLabel, new Vector2(innerLeft + 54f, card.Position.Y + 20f), 13, TextSecondary);
            DrawText(view.TeamLabel, new Vector2(innerLeft + 54f, card.Position.Y + 38f), 8, TextMuted);
            DrawTextRight(view.Tag, new Vector2(innerRight, card.Position.Y + 20f), 8,
                view.IsHuman ? HomeAccent : TextMuted);
        }

        // Stamina: always visible, because it is the one number that changes a player's whole feel.
        // Without a power row (the opponent's card) the bar sits lower so the card does not look
        // top-heavy.
        float stamina = Mathf.Clamp(view.Stamina01, 0f, 1f);
        Color staminaColor = stamina > 0.6f ? StaminaGood : stamina > 0.3f ? StaminaMid : StaminaLow;
        int staminaRow = showPower ? 0 : 1;
        DrawStatBar(card, mirror, row: staminaRow, "STAMINA", stamina, staminaColor,
            $"{Mathf.RoundToInt(stamina * 100f)}%", power: false);

        // Power: the human's charge. While not charging it stays an empty track (no fake value).
        if (!showPower) return;

        string powerValue = _charging ? $"{Mathf.RoundToInt(_chargedPower01 * 100f)}%" : "—";
        DrawStatBar(card, mirror, row: 1, "POWER", _chargedPower01, PowerFill, powerValue, power: true);
    }

    private void DrawStatBar(Rect2 card, bool mirror, int row, string label, float value, Color fill,
        string valueText, bool power)
    {
        float top = card.Position.Y + 57f + row * 20f;
        float labelX = mirror ? card.End.X - 24f : card.Position.X + 24f;
        float barLeft = card.Position.X + 86f;
        float barWidth = card.Size.X - 86f - 62f;
        float valueX = mirror ? card.Position.X + 16f : card.End.X - 16f;

        if (mirror)
        {
            DrawTextRight(label, new Vector2(labelX, top), 8, TextMuted);
            DrawText(valueText, new Vector2(valueX, top - 1f), 9, TextMuted);
        }
        else
        {
            DrawText(label, new Vector2(labelX, top), 8, TextMuted);
            DrawTextRight(valueText, new Vector2(valueX, top - 1f), 9, TextMuted);
        }

        var bar = new Rect2(barLeft, top, barWidth, 5f);
        DrawStyleBox(_trackStyle, bar);

        float fillWidth = Mathf.Round(barWidth * Mathf.Clamp(value, 0f, 1f));
        if (fillWidth >= 1f)
        {
            _fillStyle.BgColor = fill;
            var fillRect = mirror
                ? new Rect2(bar.End.X - fillWidth, bar.Position.Y, fillWidth, bar.Size.Y)
                : new Rect2(bar.Position, new Vector2(fillWidth, bar.Size.Y));
            DrawStyleBox(_fillStyle, fillRect);
        }

        if (!power || !_highModifier) return;

        // High/finesse modifier is a state, not a meter, so it gets a chip on the power row.
        float chipWidth = 30f;
        float chipX = mirror ? card.End.X - 24f - 30f : card.Position.X + 54f;
        var chipRect = new Rect2(chipX, top - 5f, chipWidth, 12f);
        _chipStyle.BgColor = new Color(PowerFill.R, PowerFill.G, PowerFill.B, 0.16f);
        DrawStyleBox(_chipStyle, chipRect);
        DrawTextCentered("HIGH", new Vector2(chipRect.Position.X, top - 3f), chipWidth, 7, PowerFill);
    }

    // ---------------------------------------------------------------------------------------------
    // Match phase banner
    // ---------------------------------------------------------------------------------------------

    private void DrawMatchBanner(Vector2 viewport)
    {
        if (_phase == MatchPhase.FullTime)
        {
            DrawBanner(viewport, new Vector2(400f, 126f), "FULL TIME",
                $"HOME  {_homeScore}  –  {_awayScore}  AWAY", "ESC  ·  MAIN MENU", 30, 13);
            return;
        }

        if (_phase == MatchPhase.HalfTime)
        {
            DrawBanner(viewport, new Vector2(300f, 78f), "HALF TIME",
                $"HOME  {_homeScore}  –  {_awayScore}  AWAY", string.Empty, 22, 12);
        }
    }

    private void DrawBanner(Vector2 viewport, Vector2 size, string title, string subtitle, string hint,
        int titleSize, int subtitleSize)
    {
        var rect = new Rect2((viewport - size) * 0.5f, size);
        DrawPanel(rect);

        float centre = rect.Position.X;
        float width = rect.Size.X;
        DrawTextCentered(title, new Vector2(centre, rect.Position.Y + 20f), width, titleSize, TextPrimary);

        if (!string.IsNullOrEmpty(subtitle))
        {
            DrawTextCentered(subtitle, new Vector2(centre, rect.Position.Y + 20f + titleSize + 12f),
                width, subtitleSize, TextSecondary);
        }

        if (!string.IsNullOrEmpty(hint))
        {
            DrawTextCentered(hint, new Vector2(centre, rect.End.Y - 24f), width, 8, TextMuted);
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
        // A home player's shirt number has a human-readable position on the match setup page; use that
        // label for him, and the generic role for everybody else (the away squad has no setup entry).
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
