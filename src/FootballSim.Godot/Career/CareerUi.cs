using System;
using Godot;

namespace FootballSim.GodotClient;

/// <summary>
/// Shared look-and-feel helpers for the career screens (plan stage W6): the same palette, panel chrome and
/// widget factories the start menu uses, so the career UI is one visual family without duplicating the styling
/// code in every node.
/// </summary>
public static class CareerUi
{
    /// <summary>Highlight colour for headings and focused primary actions.</summary>
    public static readonly Color Accent = new(0.34f, 0.86f, 0.50f);

    /// <summary>Default readable text colour.</summary>
    public static readonly Color TextColor = new(0.90f, 0.96f, 0.90f);

    /// <summary>Secondary text colour for notes and muted values.</summary>
    public static readonly Color MutedColor = new(0.60f, 0.71f, 0.62f);

    /// <summary>Warning/negative text colour.</summary>
    public static readonly Color WarnColor = new(0.95f, 0.62f, 0.45f);

    /// <summary>Positive text colour (form arrows, wins).</summary>
    public static readonly Color GoodColor = new(0.55f, 0.90f, 0.60f);

    /// <summary>Builds the full-screen chrome: dark background plus a centred, scrollable page host.</summary>
    /// <param name="owner">The Control node that owns the chrome.</param>
    /// <param name="minimumWidth">Minimum panel width in pixels.</param>
    /// <returns>The VBoxContainer pages are appended to.</returns>
    public static VBoxContainer BuildChrome(Control owner, float minimumWidth = 960f)
    {
        ArgumentNullException.ThrowIfNull(owner);

        owner.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var background = new ColorRect { Color = new Color(0.035f, 0.065f, 0.045f) };
        background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        owner.AddChild(background);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        owner.AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(minimumWidth, 0f) };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.055f, 0.105f, 0.07f, 0.97f),
            BorderColor = new Color(0.18f, 0.43f, 0.26f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            ShadowColor = new Color(0f, 0f, 0f, 0.45f),
            ShadowSize = 18
        };
        style.SetCornerRadiusAll(18);
        panel.AddThemeStyleboxOverride("panel", style);
        center.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 26);
        margin.AddThemeConstantOverride("margin_right", 26);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        panel.AddChild(margin);

        var column = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 8);
        margin.AddChild(column);

        var title = new Label
        {
            Text = "FOOTBALL SIMULATION",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.AddThemeFontSizeOverride("font_size", 22);
        title.AddThemeColorOverride("font_color", new Color(0.84f, 1f, 0.86f));
        column.AddChild(title);
        column.AddChild(new HSeparator());

        var pages = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        pages.AddThemeConstantOverride("separation", 8);
        column.AddChild(pages);
        return pages;
    }

    /// <summary>Adds a section heading label.</summary>
    public static Label Heading(Node parent, string text, int fontSize = 17)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", Accent);
        parent.AddChild(label);
        return label;
    }

    /// <summary>Adds a wrapped paragraph label.</summary>
    public static Label Paragraph(Node parent, string text, int fontSize = 14, Color? color = null)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color ?? TextColor);
        parent.AddChild(label);
        return label;
    }

    /// <summary>Adds a styled button and returns it.</summary>
    public static Button Button(Node parent, string text, Action onPressed, bool primary = false,
        bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(onPressed);

        var button = new Button
        {
            Text = text,
            Disabled = !enabled,
            Alignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(0f, primary ? 42f : 34f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        StyleButton(button, primary);
        button.Pressed += onPressed;
        parent.AddChild(button);
        return button;
    }

    /// <summary>Adds a label + widget row.</summary>
    public static HBoxContainer FieldRow(Node parent, string labelText, Control field, float labelWidth = 170f)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 14);

        var label = new Label
        {
            Text = labelText,
            CustomMinimumSize = new Vector2(labelWidth, 0f),
            VerticalAlignment = VerticalAlignment.Center
        };
        label.AddThemeFontSizeOverride("font_size", 14);
        label.AddThemeColorOverride("font_color", TextColor);
        row.AddChild(label);
        row.AddChild(field);
        parent.AddChild(row);
        return row;
    }

    /// <summary>Adds a horizontal row with the given separation.</summary>
    public static HBoxContainer Row(Node parent, int separation = 10)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", separation);
        parent.AddChild(row);
        return row;
    }

    /// <summary>Adds a vertical column with the given separation.</summary>
    public static VBoxContainer Column(Node parent, int separation = 6)
    {
        var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", separation);
        parent.AddChild(column);
        return column;
    }

    /// <summary>Adds a scroll area and returns the VBox that content goes into.</summary>
    public static VBoxContainer Scroll(Node parent, int minimumHeight = 420)
    {
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, minimumHeight)
        };
        parent.AddChild(scroll);

        var content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        content.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(content);
        return content;
    }

    /// <summary>Creates a styled OptionButton.</summary>
    public static OptionButton Option()
    {
        var option = new OptionButton
        {
            CustomMinimumSize = new Vector2(0f, 34f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        StyleOption(option);
        return option;
    }

    /// <summary>Creates a single-line text input.</summary>
    public static LineEdit LineEdit(string placeholder, string text = "")
    {
        var edit = new LineEdit
        {
            Text = text,
            PlaceholderText = placeholder,
            CustomMinimumSize = new Vector2(0f, 34f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        edit.AddThemeFontSizeOverride("font_size", 14);
        edit.AddThemeColorOverride("font_color", TextColor);
        edit.AddThemeColorOverride("caret_color", Accent);
        return edit;
    }

    /// <summary>Creates a checkbox with readable styling.</summary>
    public static CheckBox CheckBox(string text, bool pressed = false)
    {
        var box = new CheckBox { Text = text, ButtonPressed = pressed, CustomMinimumSize = new Vector2(0f, 30f) };
        box.AddThemeFontSizeOverride("font_size", 14);
        box.AddThemeColorOverride("font_color", TextColor);
        return box;
    }

    /// <summary>A simple two-column info row (label on the left, value on the right).</summary>
    public static void InfoRow(Node parent, string labelText, string valueText, float labelWidth = 210f)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);

        var label = new Label
        {
            Text = labelText,
            CustomMinimumSize = new Vector2(labelWidth, 0f),
            VerticalAlignment = VerticalAlignment.Center
        };
        label.AddThemeFontSizeOverride("font_size", 13);
        label.AddThemeColorOverride("font_color", MutedColor);
        row.AddChild(label);

        var value = new Label
        {
            Text = valueText,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        value.AddThemeFontSizeOverride("font_size", 14);
        value.AddThemeColorOverride("font_color", TextColor);
        row.AddChild(value);
        parent.AddChild(row);
    }

    /// <summary>Creates a thin separator with some vertical breathing room.</summary>
    public static Control Spacer(Node parent, float height = 6f)
    {
        var spacer = new Control { CustomMinimumSize = new Vector2(0f, height) };
        parent.AddChild(spacer);
        return spacer;
    }

    /// <summary>Applies the standard button look (copied from the main menu's own palette).</summary>
    public static void StyleButton(Button button, bool primary = false)
    {
        ArgumentNullException.ThrowIfNull(button);

        Color baseColor = primary ? new Color(0.18f, 0.47f, 0.26f) : new Color(0.09f, 0.19f, 0.12f);
        Color hoverColor = primary ? new Color(0.24f, 0.60f, 0.33f) : new Color(0.13f, 0.28f, 0.18f);

        var normal = new StyleBoxFlat
        {
            BgColor = baseColor,
            BorderColor = new Color(0.21f, 0.46f, 0.29f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1
        };
        normal.SetCornerRadiusAll(9);
        normal.SetContentMarginAll(8f);

        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = hoverColor;

        var pressed = (StyleBoxFlat)normal.Duplicate();
        pressed.BgColor = new Color(0.07f, 0.15f, 0.10f);

        var focus = (StyleBoxFlat)hover.Duplicate();
        focus.BorderColor = Accent;
        focus.BorderWidthLeft = 2;
        focus.BorderWidthTop = 2;
        focus.BorderWidthRight = 2;
        focus.BorderWidthBottom = 2;

        var disabled = (StyleBoxFlat)normal.Duplicate();
        disabled.BgColor = new Color(0.07f, 0.11f, 0.08f);
        disabled.BorderColor = new Color(0.13f, 0.22f, 0.15f);

        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", pressed);
        button.AddThemeStyleboxOverride("focus", focus);
        button.AddThemeStyleboxOverride("disabled", disabled);
        button.AddThemeFontSizeOverride("font_size", primary ? 16 : 14);
        button.AddThemeColorOverride("font_color", TextColor);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_disabled_color", new Color(0.38f, 0.45f, 0.39f));
    }

    /// <summary>Applies the standard dropdown look.</summary>
    public static void StyleOption(OptionButton option)
    {
        ArgumentNullException.ThrowIfNull(option);

        var normal = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.16f, 0.10f),
            BorderColor = new Color(0.21f, 0.46f, 0.29f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1
        };
        normal.SetCornerRadiusAll(8);
        normal.SetContentMarginAll(6f);

        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = new Color(0.12f, 0.25f, 0.16f);

        option.AddThemeStyleboxOverride("normal", normal);
        option.AddThemeStyleboxOverride("hover", hover);
        option.AddThemeStyleboxOverride("pressed", normal);
        option.AddThemeStyleboxOverride("focus", normal);
        option.AddThemeFontSizeOverride("font_size", 14);
        option.AddThemeColorOverride("font_color", TextColor);
        option.AddThemeColorOverride("font_hover_color", Colors.White);
        option.AddThemeColorOverride("font_pressed_color", TextColor);
        option.AddThemeColorOverride("font_focus_color", TextColor);

        PopupMenu popup = option.GetPopup();
        popup.AddThemeColorOverride("font_color", TextColor);
        popup.AddThemeColorOverride("font_hover_color", Colors.White);

        var popupPanel = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.10f, 0.065f),
            BorderColor = new Color(0.21f, 0.46f, 0.29f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1
        };
        popupPanel.SetCornerRadiusAll(6);
        popupPanel.SetContentMarginAll(6f);
        popup.AddThemeStyleboxOverride("panel", popupPanel);
    }

    /// <summary>Clears every child of a container (for rebuilding a panel).</summary>
    public static void Clear(Node container)
    {
        ArgumentNullException.ThrowIfNull(container);
        foreach (Node child in container.GetChildren())
        {
            container.RemoveChild(child);
            child.QueueFree();
        }
    }

    /// <summary>
    /// Gives the first enabled button keyboard/gamepad focus when nothing owns it yet, so a career screen is
    /// navigable without a mouse (the start menu does the same for its pages).
    /// </summary>
    /// <param name="owner">The screen root to search.</param>
    public static void FocusFirstIfNone(Control owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        Control? current = owner.GetViewport().GuiGetFocusOwner();
        if (current is not null && IsInside(owner, current)) return;

        FocusFirst(owner);

        static bool IsInside(Node root, Node candidate)
        {
            Node? node = candidate;
            while (node is not null)
            {
                if (node == root) return true;
                node = node.GetParent();
            }

            return false;
        }

        static bool FocusFirst(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is Button { Disabled: false, Visible: true } button && button.IsInsideTree())
                {
                    button.GrabFocus();
                    return true;
                }

                if (FocusFirst(child)) return true;
            }

            return false;
        }
    }
}
