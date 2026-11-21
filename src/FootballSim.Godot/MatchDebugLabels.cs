using System;
using System.Collections.Generic;
using Godot;

namespace FootballSim.GodotClient;

/// <summary>
/// Screen-space debug text with a fixed pixel size, one draw call per frame and simple collision avoidance.
///
/// Why not <see cref="Label3D"/>: a world-space label shrinks with camera distance and with the broadcast
/// camera's zoom, so the same 44 px font measured about 7 px on a far player and the pass labels printed on
/// top of the player state labels they were supposed to sit clear of. Debug text has to stay readable at the
/// game's own resolution, so every label is projected to the viewport and drawn at a constant pixel size.
///
/// Labels are added with a priority; higher priority is placed first and gets the free spot, and a label that
/// would overlap an already-placed one is lifted until it does not. That is what keeps the pass cluster
/// (target X, ETAs, speed, stopped-short number, arrival delta) readable while twenty player state labels are
/// on screen. Nothing here feeds the simulation: the layer only reads engine state to draw.
/// </summary>
public partial class MatchDebugLabels : Control
{
    private const float PaddingX = 5f;
    private const float PaddingY = 2f;
    private const int MaxLiftAttempts = 10;

    private readonly List<Label> _labels = new();
    private readonly List<Rect2> _placed = new();
    private Font _font = null!;
    private bool _wantVisible;
    private string _status = string.Empty;

    private struct Label
    {
        public Vector3 World;
        public string Text;
        public Color Colour;
        public int FontSize;
        public int Priority;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _font = GetThemeDefaultFont();
        Visible = _wantVisible;
    }

    /// <summary>Starts a frame: clears the label list. The previous frame stays drawn until the redraw.</summary>
    public void Begin() => _labels.Clear();

    /// <summary>
    /// Adds one label anchored at a world point. <paramref name="priority"/> decides draw order and which
    /// label keeps its spot: pass forensics (100) &gt; touch rejection (90) &gt; pass geometry (70-80) &gt;
    /// frozen decision scores (55-65) &gt; player states (10). Text is drawn bare (a 1 px offset shadow only):
    /// the owner asked for no boxes or outlines because they obscured the pitch.
    /// </summary>
    public void Add(Vector3 world, string text, Color colour, int fontSize, int priority = 10)
    {
        if (string.IsNullOrEmpty(text)) return;
        _labels.Add(new Label
        {
            World = world,
            Text = text,
            Colour = colour,
            FontSize = fontSize,
            Priority = priority
        });
    }

    /// <summary>
    /// One small always-on-screen line at the top centre, used by the debug-only time controls to say
    /// "PAUSED" or the current speed. Empty hides it.
    /// </summary>
    public void SetStatus(string status)
    {
        _status = status ?? string.Empty;
    }

    /// <summary>Finishes the frame and schedules the redraw.</summary>
    public void End()
    {
        Visible = _labels.Count > 0 || !string.IsNullOrEmpty(_status);
        if (!Visible) return;
        QueueRedraw();
    }

    /// <summary>Hides the layer for the next frames (debug mode off clears every label).</summary>
    public void SetLayerVisible(bool visible)
    {
        _wantVisible = visible;
        if (visible) return;
        _labels.Clear();
        Visible = false;
    }

    public override void _Draw()
    {
        if (_font is null) return;

        Vector2 viewport = Size;
        _placed.Clear();

        // The time status claims its spot first so player labels lift around it instead of overprinting it.
        Rect2 statusRect = DrawStatus(viewport);
        if (statusRect.Size.X > 0f) _placed.Add(statusRect);

        if (_labels.Count == 0) return;

        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (camera is null) return;

        _labels.Sort((left, right) => right.Priority.CompareTo(left.Priority));

        // One-pixel offset shadow instead of an outline or a plate: enough to keep a coloured word legible
        // over turf and white pitch lines, without the dark halo/box that obscured the view.
        var shadow = new Color(0f, 0f, 0f, 0.55f);

        foreach (Label label in _labels)
        {
            if (camera.IsPositionBehind(label.World)) continue;
            Vector2 anchor = camera.UnprojectPosition(label.World);
            if (anchor.X < -80f || anchor.X > viewport.X + 80f ||
                anchor.Y < -40f || anchor.Y > viewport.Y + 40f) continue;

            string[] lines = label.Text.Split('\n');
            float lineHeight = label.FontSize + 3f;
            float width = 0f;
            for (int index = 0; index < lines.Length; index++)
                width = MathF.Max(width, _font.GetStringSize(lines[index], HorizontalAlignment.Left, -1f,
                    label.FontSize).X);

            var baseRect = new Rect2(anchor.X - width * 0.5f - PaddingX, anchor.Y - lines.Length * lineHeight,
                width + PaddingX * 2f, lines.Length * lineHeight + PaddingY);

            // Try the natural spot first, then alternate downward/upward nudges. Downward matters at the top
            // edge: a label whose anchor is off-screen used to be clamped under the time status and could not
            // move anywhere, so it printed over it.
            Rect2 rect = baseRect;
            for (int attempt = 0; attempt < MaxLiftAttempts; attempt++)
            {
                float direction = attempt % 2 == 1 ? 1f : -1f;
                float distance = (attempt + 1) / 2 * (lineHeight + 2f);
                var position = new Vector2(
                    Mathf.Clamp(baseRect.Position.X, 2f, Mathf.Max(2f, viewport.X - baseRect.Size.X - 2f)),
                    Mathf.Clamp(baseRect.Position.Y + direction * distance, 2f,
                        Mathf.Max(2f, viewport.Y - baseRect.Size.Y - 2f)));
                rect = new Rect2(position, baseRect.Size);
                if (!Overlaps(rect)) break;
            }

            _placed.Add(rect);

            for (int index = 0; index < lines.Length; index++)
            {
                float lineWidth = _font.GetStringSize(lines[index], HorizontalAlignment.Left, -1f,
                    label.FontSize).X;
                float x = rect.Position.X + (rect.Size.X - lineWidth) * 0.5f;
                float baseline = rect.Position.Y + PaddingY * 0.5f + label.FontSize +
                                 index * lineHeight;
                DrawString(_font, new Vector2(x + 1f, baseline + 1f), lines[index],
                    HorizontalAlignment.Left, -1f, label.FontSize, shadow);
                DrawString(_font, new Vector2(x, baseline), lines[index], HorizontalAlignment.Left, -1f,
                    label.FontSize, label.Colour);
            }
        }
    }

    /// <summary>Debug-only time-control state, drawn small at the top centre (never part of the player HUD).</summary>
    private Rect2 DrawStatus(Vector2 viewport)
    {
        if (string.IsNullOrEmpty(_status)) return default;

        const int fontSize = 13;
        float width = _font.GetStringSize(_status, HorizontalAlignment.Left, -1f, fontSize).X;
        var position = new Vector2((viewport.X - width) * 0.5f, 10f + _font.GetAscent(fontSize));
        var shadow = new Color(0f, 0f, 0f, 0.55f);
        DrawString(_font, position + new Vector2(1f, 1f), _status, HorizontalAlignment.Left, -1f, fontSize,
            shadow);
        DrawString(_font, position, _status, HorizontalAlignment.Left, -1f, fontSize,
            new Color(0.93f, 0.97f, 1f, 0.95f));
        return new Rect2(position.X - 2f, 8f, width + 4f, fontSize + 5f);
    }

    private bool Overlaps(Rect2 rect)
    {
        for (int index = 0; index < _placed.Count; index++)
        {
            if (_placed[index].Intersects(rect)) return true;
        }

        return false;
    }
}
