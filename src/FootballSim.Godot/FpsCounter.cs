using Godot;

namespace FootballSim.GodotClient;

/// <summary>
/// Small always-on FPS / frame-time readout for the gameplay scenes (match and Free Training).
///
/// It is a plain <see cref="Label"/> that refreshes four times a second from the mean of the real frame
/// deltas it saw in that window, so it reports the paced frame rate (whatever
/// <c>application/run/max_fps</c> / vsync is delivering) rather than an instantaneous spike. Bound in the
/// top-right corner with a fixed footprint so a changing string never reflows it, translucent and
/// non-interactive, so it never competes with the scoreboard (top-left) or the player cards (bottom).
/// </summary>
public sealed partial class FpsCounter : Label
{
    private const double RefreshSeconds = 0.25;
    private const float Width = 168f;
    private const float Height = 22f;
    private const float Margin = 14f;

    private double _elapsedSeconds;
    private double _accumulatedFrameSeconds;
    private int _frames;

    /// <summary>Creates the readout and adds it to the top-right of <paramref name="canvas"/>.</summary>
    public static FpsCounter Attach(CanvasLayer canvas)
    {
        var counter = new FpsCounter
        {
            Text = "--- fps",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            Modulate = new Color(1f, 1f, 1f, 0.66f),
            CustomMinimumSize = new Vector2(Width, Height),
            Size = new Vector2(Width, Height)
        };
        counter.AddThemeFontSizeOverride("font_size", 14);
        counter.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
        counter.AddThemeConstantOverride("outline_size", 4);
        canvas.AddChild(counter);

        // Fixed top-right footprint: anchors only, then explicit offsets, so the readout is always the
        // same size and position no matter what the text says (offsets in a CanvasLayer are in pixels).
        counter.SetAnchorsPreset(LayoutPreset.TopRight);
        counter.OffsetLeft = -Width - Margin;
        counter.OffsetRight = -Margin;
        counter.OffsetTop = Margin;
        counter.OffsetBottom = Margin + Height;
        return counter;
    }

    public override void _Process(double delta)
    {
        _elapsedSeconds += delta;
        _accumulatedFrameSeconds += delta;
        _frames++;
        if (_elapsedSeconds < RefreshSeconds)
            return;

        double meanSeconds = _frames > 0 ? _accumulatedFrameSeconds / _frames : 0.0;
        double fps = meanSeconds > 1e-9 ? 1.0 / meanSeconds : 0.0;
        Text = fps >= 100.0
            ? $"{fps:0} FPS   {meanSeconds * 1000.0:0.0} ms"
            : $"{fps:0.0} FPS   {meanSeconds * 1000.0:0.0} ms";

        _elapsedSeconds = 0.0;
        _accumulatedFrameSeconds = 0.0;
        _frames = 0;
    }
}
