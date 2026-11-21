using Godot;
using FootballSim.AI;

namespace FootballSim.GodotClient;

/// <summary>
/// The four-line decision readout requested alongside the frozen option lines: title plus the winner and
/// up to three runners-up/rejections, with only the few numbers that produced the score. It is a debug
/// panel (top-right, only while debug mode is on), not part of the player HUD.
///
/// The rows are chosen by the view so the readout can show the winner, the best alternative and the most
/// useful refusal in a fixed order, while every considered option still gets its own frozen line on the
/// pitch. The decision stays legible for three seconds after it was made — long enough to read even when
/// the carrier has already played the ball — so the header carries a countdown instead of silently looking
/// live.
/// </summary>
public partial class MatchDebugPanel : Control
{
    private static readonly Color PanelBackground = new(0.024f, 0.040f, 0.058f, 0.95f);
    private static readonly Color PanelBorder = new(1f, 1f, 1f, 0.085f);
    private static readonly Color TitleColour = new(0.95f, 0.97f, 1f);
    private static readonly Color FrozenColour = new(0.58f, 0.66f, 0.76f);
    private static readonly Color ChosenColour = new(0.26f, 0.98f, 0.48f);
    private static readonly Color RunnerUpColour = new(0.98f, 0.84f, 0.26f);
    private static readonly Color RejectedColour = new(0.99f, 0.34f, 0.34f);
    private static readonly Color DetailColour = new(0.66f, 0.74f, 0.82f);

    private const float Margin = 18f;
    private const float PanelWidth = 548f;
    private const float RowHeight = 17f;
    private const int MaxRows = 3;

    private Font _font = null!;
    private StyleBoxFlat _panelStyle = null!;
    private DecisionSnapshot? _snapshot;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _font = GetThemeDefaultFont();

        _panelStyle = new StyleBoxFlat
        {
            BgColor = PanelBackground,
            BorderColor = PanelBorder,
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1
        };
        _panelStyle.SetCornerRadiusAll(6);
        Visible = false;
    }

    internal void SetSnapshot(DecisionSnapshot? snapshot)
    {
        _snapshot = snapshot;
        Visible = snapshot is not null;
        if (snapshot is not null) QueueRedraw();
    }

    public override void _Draw()
    {
        if (_font is null || _snapshot is null) return;

        Vector2 viewport = Size;
        if (viewport.X < PanelWidth + Margin * 2f || viewport.Y < 180f) return;

        DecisionSnapshot snapshot = _snapshot;
        int rows = Mathf.Min(MaxRows, snapshot.PanelRows.Count);
        float height = 36f + rows * RowHeight + 10f;
        var panel = new Rect2(viewport.X - Margin - PanelWidth, Margin, PanelWidth, height);
        DrawStyleBox(_panelStyle, panel);

        float left = panel.Position.X;
        float top = panel.Position.Y;

        DrawText(snapshot.Title, new Vector2(left + 12f, top + 7f), 13, TitleColour);
        DrawTextRight($"FROZEN {snapshot.SecondsLeft:F1}s", panel.End.X - 12f, top + 9f, 9, FrozenColour);
        DrawRect(new Rect2(left + 12f, top + 26f, PanelWidth - 24f, 1f), new Color(1f, 1f, 1f, 0.08f));

        for (int index = 0; index < rows; index++)
        {
            DecisionOption option = snapshot.PanelRows[index];
            float rowTop = top + 33f + index * RowHeight;
            Color colour = option.Rejected ? RejectedColour : option.Chosen ? ChosenColour : RunnerUpColour;
            string rank = option.Chosen ? "[WIN]" : option.Rejected ? "[ X ]" : $"[ {index + 1} ]";
            string score = option.Rejected ? "SKIP" : option.Score.ToString("F2");
            string detail = option.Rejected ? $"({option.Reason})" : TermsText(option.Terms);

            DrawText(rank, new Vector2(left + 12f, rowTop), 10, colour);
            DrawText(option.ActionLabel, new Vector2(left + 62f, rowTop), 10, colour);
            DrawText(score, new Vector2(left + 208f, rowTop), 10, colour);
            DrawText(detail, new Vector2(left + 256f, rowTop), 10, DetailColour);
        }
    }

    /// <summary>
    /// The score is <c>p × gain − (1 − p) × loss + preference</c> (A4.6): those are the only numbers that
    /// explain it, so the readout prints exactly the first three and nothing else. Capitals keep the three
    /// letters distinguishable from the bar separators at this tiny size.
    /// </summary>
    private static string TermsText(ValueTerms terms) =>
        terms.IsPresent
            ? $"(P {terms.Probability01:F2} | G {terms.Gain:F2} | L {terms.Loss:F2})"
            : string.Empty;

    private void DrawText(string text, Vector2 topLeft, int fontSize, Color color)
    {
        if (string.IsNullOrEmpty(text)) return;
        DrawString(_font, new Vector2(topLeft.X, topLeft.Y + _font.GetAscent(fontSize)), text,
            HorizontalAlignment.Left, -1f, fontSize, color);
    }

    private void DrawTextRight(string text, float rightX, float topY, int fontSize, Color color)
    {
        if (string.IsNullOrEmpty(text)) return;
        float width = _font.GetStringSize(text, HorizontalAlignment.Left, -1f, fontSize).X;
        DrawString(_font, new Vector2(rightX - width, topY + _font.GetAscent(fontSize)), text,
            HorizontalAlignment.Left, -1f, fontSize, color);
    }
}
