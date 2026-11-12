using System;
using System.Collections.Generic;
using Godot;
using FootballSim;
using FootballSim.Training;

namespace FootballSim.GodotClient;

/// <summary>
/// Start-up menu: Match (position + half length), Practice (drills), Settings (resolution plus
/// keyboard and gamepad bindings) and Exit.
/// </summary>
public partial class MainMenuNode : Control
{
    private enum Page
    {
        Main,
        MatchSetup,
        Practice,
        Settings
    }

    private enum SettingsTab
    {
        Keyboard,
        Gamepad
    }

    private readonly Color _accent = new(0.34f, 0.86f, 0.50f);
    private readonly Color _textColor = new(0.90f, 0.96f, 0.90f);
    private readonly Color _mutedColor = new(0.60f, 0.71f, 0.62f);

    private VBoxContainer _pageHost = null!;
    private Page _page = Page.Main;
    private SettingsTab _settingsTab = SettingsTab.Keyboard;
    private ControlAction? _awaitingBinding;
    private GamepadAction? _awaitingGamepadBinding;

    public override void _Ready()
    {
        GameSettings.Load();
        GameSettings.ApplyResolution();
        EnsureGamepadMenuActions();

        BuildChrome();
        ShowMainMenu();
    }

    public override void _Input(InputEvent @event)
    {
        if (_awaitingGamepadBinding is GamepadAction gamepadAction)
        {
            HandleGamepadBindingInput(gamepadAction, @event);
            return;
        }

        if (_awaitingBinding is ControlAction action)
        {
            if (@event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo)
                return;

            if (keyEvent.Keycode == Key.Escape)
            {
                _awaitingBinding = null;
                ShowSettings();
            }
            else
            {
                GameSettings.Controls.Assign(action, keyEvent.Keycode);
                GameSettings.Save();
                _awaitingBinding = null;
                ShowSettings();
            }

            GetViewport().SetInputAsHandled();
            return;
        }

        if (TryGetVisiblePopup(out PopupMenu? visiblePopup) && visiblePopup != null)
        {
            if (@event is InputEventJoypadButton popupButton && popupButton.Pressed)
            {
                if (popupButton.ButtonIndex == JoyButton.A)
                {
                    int focusedItem = visiblePopup.GetFocusedItem();
                    if (focusedItem >= 0)
                    {
                        visiblePopup.EmitSignal(
                            PopupMenu.SignalName.IdPressed,
                            visiblePopup.GetItemId(focusedItem));
                        visiblePopup.Hide();
                    }

                    GetViewport().SetInputAsHandled();
                }
                else if (popupButton.ButtonIndex == JoyButton.B)
                {
                    visiblePopup.Hide();
                    GetViewport().SetInputAsHandled();
                }
            }

            return;
        }

        if (@event is InputEventJoypadButton padButton && padButton.Pressed &&
            padButton.ButtonIndex == JoyButton.B)
        {
            // B goes back one page (a visible popup was already handled above).
            if (_page != Page.Main)
            {
                ShowMainMenu();
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        if (@event is InputEventJoypadButton acceptButton && acceptButton.Pressed &&
            acceptButton.ButtonIndex == JoyButton.A)
        {
            Control? focus = GetViewport().GuiGetFocusOwner();
            if (focus is OptionButton optionButton)
            {
                optionButton.ShowPopup();
            }
            else if (focus is BaseButton button && !button.Disabled)
            {
                button.EmitSignal(BaseButton.SignalName.Pressed);
            }

            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is InputEventKey escape && escape.Pressed && !escape.Echo &&
            escape.Keycode == Key.Escape && _page != Page.Main)
        {
            ShowMainMenu();
            GetViewport().SetInputAsHandled();
        }
    }

    private static void EnsureGamepadMenuActions()
    {
        AddJoypadButtonToAction("ui_accept", JoyButton.A);
        AddJoypadButtonToAction("ui_cancel", JoyButton.B);
    }

    private static void AddJoypadButtonToAction(string action, JoyButton button)
    {
        if (!InputMap.HasAction(action)) return;

        foreach (InputEvent inputEvent in InputMap.ActionGetEvents(action))
        {
            if (inputEvent is InputEventJoypadButton joypad && joypad.ButtonIndex == button)
                return;
        }

        InputMap.ActionAddEvent(action, new InputEventJoypadButton { ButtonIndex = button });
    }

    private bool TryGetVisiblePopup(out PopupMenu? popup) => TryFindVisiblePopup(this, out popup);

    private static bool TryFindVisiblePopup(Node node, out PopupMenu? popup)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is PopupMenu menu && menu.Visible)
            {
                popup = menu;
                return true;
            }

            if (TryFindVisiblePopup(child, out popup)) return true;
        }

        popup = null;
        return false;
    }

    private void HandleGamepadBindingInput(GamepadAction action, InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo &&
            keyEvent.Keycode == Key.Escape)
        {
            _awaitingGamepadBinding = null;
            ShowSettings();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is InputEventJoypadButton buttonEvent && buttonEvent.Pressed)
        {
            // The D-pad is a fixed movement control; do not let an action steal it.
            if (buttonEvent.ButtonIndex is JoyButton.DpadUp or JoyButton.DpadDown or
                JoyButton.DpadLeft or JoyButton.DpadRight)
            {
                return;
            }

            GameSettings.Gamepad.Assign(action, GamepadBinding.FromButton(buttonEvent.ButtonIndex));
            GameSettings.Save();
            _awaitingGamepadBinding = null;
            ShowSettings();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is InputEventJoypadMotion motion &&
            motion.Axis is JoyAxis.TriggerLeft or JoyAxis.TriggerRight &&
            MathF.Abs(motion.AxisValue) > 0.5f)
        {
            GameSettings.Gamepad.Assign(action, GamepadBinding.FromAxis(motion.Axis));
            GameSettings.Save();
            _awaitingGamepadBinding = null;
            ShowSettings();
            GetViewport().SetInputAsHandled();
        }
    }

    private void ShowMainMenu()
    {
        _awaitingBinding = null;
        _awaitingGamepadBinding = null;
        BeginPage(Page.Main, "MAIN MENU");

        AddMenuButton("1.  Match", ShowMatchSetup);
        AddMenuButton("2.  Practice", ShowPractice);
        AddMenuButton("3.  Settings", ShowSettings);
        AddMenuButton("4.  Exit", () => GetTree().Quit());

        FocusFirstButtonDeferred();
    }

    // ---------------------------------------------------------------------
    // Match setup
    // ---------------------------------------------------------------------

    private void ShowMatchSetup()
    {
        _awaitingBinding = null;
        _awaitingGamepadBinding = null;
        BeginPage(Page.MatchSetup, "MATCH SETUP");

        AddParagraph("Choose a position to control. Pick None to watch a full 11 v 11 AI simulation.");

        var positionOption = new OptionButton
        {
            CustomMinimumSize = new Vector2(0f, 40f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        foreach (MatchPositionOption position in MatchPositions.All)
            positionOption.AddItem(position.DisplayLabel);
        positionOption.Selected = Math.Clamp(GameSettings.LastPositionIndex, 0, MatchPositions.All.Count - 1);
        StyleOptionButton(positionOption);
        AddFieldRow("Position", positionOption);

        var halfOption = new OptionButton
        {
            CustomMinimumSize = new Vector2(0f, 40f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        int halfIndex = 0;
        for (int i = 0; i < MatchPresets.HalfLengthMinuteOptions.Count; i++)
        {
            int minutes = MatchPresets.HalfLengthMinuteOptions[i];
            halfOption.AddItem(MatchPresets.DescribeHalfLength(minutes));
            if (minutes == GameSettings.HalfLengthMinutes) halfIndex = i;
        }
        halfOption.Selected = halfIndex;
        StyleOptionButton(halfOption);
        AddFieldRow("Half length", halfOption);

        AddParagraph("Four minutes per half is the default; the full match then lasts eight minutes.",
            13, _mutedColor);

        AddMenuButton("Start Match", () =>
        {
            int positionIndex = positionOption.Selected;
            int minutes = MatchPresets.HalfLengthMinuteOptions[halfOption.Selected];

            GameSettings.SetLastPositionIndex(positionIndex);
            GameSettings.SetHalfLengthMinutes(minutes);
            GameSettings.Save();

            GameSession.PendingMatch = GameSettings.BuildMatchSetup();

            GetTree().ChangeSceneToFile(GameSession.MatchScene);
        });

        AddMenuButton("Back", ShowMainMenu);

        FocusFirstButtonDeferred();
    }

    // ---------------------------------------------------------------------
    // Practice
    // ---------------------------------------------------------------------

    private void ShowPractice()
    {
        _awaitingBinding = null;
        _awaitingGamepadBinding = null;
        BeginPage(Page.Practice, "PRACTICE");

        AddParagraph("Free Training is live: an empty pitch, one controllable player, a ball and one goal. " +
                     "Score or put the ball out and everything respawns near the box.");

        foreach (TrainingDrill drill in TrainingDrillRegistry.All)
        {
            TrainingDrill captured = drill;
            AddMenuButton(
                $"{drill.Title} — {drill.Description}",
                () => StartDrill(captured),
                drill.IsAvailable);
        }

        AddParagraph("More drills (passing, dribbling, crossing) plug into the same training session.",
            13, _mutedColor);

        AddMenuButton("Back", ShowMainMenu);

        FocusFirstButtonDeferred();
    }

    private void StartDrill(TrainingDrill drill)
    {
        GameSession.PendingDrillId = drill.Id;
        GetTree().ChangeSceneToFile(GameSession.FreeTrainingScene);
    }

    // ---------------------------------------------------------------------
    // Settings
    // ---------------------------------------------------------------------

    private void ShowSettings()
    {
        BeginPage(Page.Settings, "SETTINGS");

        var resolutionOption = new OptionButton
        {
            CustomMinimumSize = new Vector2(0f, 40f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        for (int i = 0; i < GameSettings.Resolutions.Count; i++)
        {
            Vector2I size = GameSettings.Resolutions[i];
            resolutionOption.AddItem($"{size.X} × {size.Y}");
        }
        resolutionOption.Selected = GameSettings.ResolutionIndex;
        StyleOptionButton(resolutionOption);
        resolutionOption.ItemSelected += index =>
        {
            GameSettings.SetResolution((int)index);
            GameSettings.ApplyResolution();
            GameSettings.Save();
        };
        AddFieldRow("Resolution", resolutionOption);

        AddPageHeading("CONTROLS");

        var tabs = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        tabs.AddThemeConstantOverride("separation", 8);
        _pageHost.AddChild(tabs);
        AddTabButton(tabs, "Keyboard", _settingsTab == SettingsTab.Keyboard,
            () => SwitchSettingsTab(SettingsTab.Keyboard));
        AddTabButton(tabs, "Gamepad", _settingsTab == SettingsTab.Gamepad,
            () => SwitchSettingsTab(SettingsTab.Gamepad));

        if (_settingsTab == SettingsTab.Keyboard)
        {
            AddParagraph("Click a key to rebind it. Esc cancels. Arrow keys remain movement aliases.",
                13, _mutedColor);
            BuildKeyboardBindingGrid();
        }
        else
        {
            AddParagraph("Click a binding, then press a gamepad button (LT / RT included). Esc cancels. " +
                         "Left stick and D-pad are fixed; aim with the left stick (the right stick is not used). X is Cross with the ball and " +
                         "Tackle without it.", 13, _mutedColor);
            BuildGamepadBindingGrid();
        }

        AddMenuButton("Reset Controls to Default", () =>
        {
            GameSettings.ResetControls();
            GameSettings.Save();
            ShowSettings();
        });

        AddMenuButton("Back", ShowMainMenu);

        if (_awaitingBinding is null && _awaitingGamepadBinding is null)
            FocusFirstButtonDeferred();
    }

    private void SwitchSettingsTab(SettingsTab tab)
    {
        _settingsTab = tab;
        _awaitingBinding = null;
        _awaitingGamepadBinding = null;
        ShowSettings();
    }

    private Button AddTabButton(HBoxContainer parent, string text, bool active, Action onPressed)
    {
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(0f, 30f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        button.AddThemeFontSizeOverride("font_size", 14);
        StyleButton(button, primary: active);
        button.Pressed += onPressed;
        parent.AddChild(button);
        return button;
    }

    private void BuildKeyboardBindingGrid()
    {
        (VBoxContainer left, VBoxContainer right) = AddBindingColumns();

        for (int i = 0; i < ControlBindings.AllActions.Count; i++)
        {
            ControlAction action = ControlBindings.AllActions[i];
            ControlAction captured = action;
            bool awaiting = _awaitingBinding == action;
            VBoxContainer column = i < (ControlBindings.AllActions.Count + 1) / 2 ? left : right;

            AddBindingRow(
                column,
                ControlBindings.DisplayName(action),
                awaiting ? "Press a key…" : ControlBindings.KeyName(GameSettings.Controls.Get(action)),
                awaiting,
                () =>
                {
                    _awaitingBinding = captured;
                    _awaitingGamepadBinding = null;
                    ShowSettings();
                });
        }
    }

    private void BuildGamepadBindingGrid()
    {
        (VBoxContainer left, VBoxContainer right) = AddBindingColumns();

        for (int i = 0; i < GamepadBindings.AllActions.Count; i++)
        {
            GamepadAction action = GamepadBindings.AllActions[i];
            GamepadAction captured = action;
            bool awaiting = _awaitingGamepadBinding == action;
            VBoxContainer column = i < (GamepadBindings.AllActions.Count + 1) / 2 ? left : right;

            AddBindingRow(
                column,
                GamepadBindings.DisplayName(action),
                awaiting ? "Press a button…" : GameSettings.Gamepad.Get(action).DisplayName,
                awaiting,
                () =>
                {
                    _awaitingGamepadBinding = captured;
                    _awaitingBinding = null;
                    ShowSettings();
                });
        }
    }

    private (VBoxContainer Left, VBoxContainer Right) AddBindingColumns()
    {
        var columns = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 22);
        _pageHost.AddChild(columns);

        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 4);
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 4);
        columns.AddChild(left);
        columns.AddChild(right);

        return (left, right);
    }

    private void AddBindingRow(
        VBoxContainer column,
        string labelText,
        string buttonText,
        bool awaiting,
        Action onPressed)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);

        var label = new Label
        {
            Text = labelText,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        label.AddThemeColorOverride("font_color", _textColor);
        label.AddThemeFontSizeOverride("font_size", 13);
        row.AddChild(label);

        var button = new Button
        {
            Text = buttonText,
            CustomMinimumSize = new Vector2(112f, 28f)
        };
        button.AddThemeFontSizeOverride("font_size", 13);
        StyleButton(button, primary: awaiting);
        button.Pressed += onPressed;
        row.AddChild(button);

        if (awaiting && button.IsInsideTree())
            button.GrabFocus();

        column.AddChild(row);
    }

    // ---------------------------------------------------------------------
    // Chrome and helpers
    // ---------------------------------------------------------------------

    private void BuildChrome()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);

        var background = new ColorRect { Color = new Color(0.035f, 0.065f, 0.045f) };
        background.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(background);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(660f, 0f)
        };
        var panelStyle = new StyleBoxFlat
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
        panelStyle.SetCornerRadiusAll(18);
        panel.AddThemeStyleboxOverride("panel", panelStyle);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 30);
        margin.AddThemeConstantOverride("margin_right", 30);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        panel.AddChild(margin);
        center.AddChild(panel);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        margin.AddChild(column);

        var title = new Label
        {
            Text = "FOOTBALL SIMULATION",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.AddThemeFontSizeOverride("font_size", 28);
        title.AddThemeColorOverride("font_color", new Color(0.84f, 1f, 0.86f));
        column.AddChild(title);

        column.AddChild(new HSeparator());

        _pageHost = new VBoxContainer();
        _pageHost.AddThemeConstantOverride("separation", 8);
        _pageHost.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        column.AddChild(_pageHost);
    }

    private void BeginPage(Page page, string title)
    {
        _page = page;

        foreach (Node child in _pageHost.GetChildren())
        {
            _pageHost.RemoveChild(child);
            child.QueueFree();
        }

        AddPageHeading(title);
    }

    private Label AddPageHeading(string text)
    {
        var heading = new Label { Text = text };
        heading.AddThemeFontSizeOverride("font_size", 16);
        heading.AddThemeColorOverride("font_color", _accent);
        _pageHost.AddChild(heading);
        return heading;
    }

    private Label AddParagraph(string text, int fontSize = 14, Color? color = null)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color ?? _textColor);
        _pageHost.AddChild(label);
        return label;
    }

    private Button AddMenuButton(string text, Action onPressed, bool enabled = true)
    {
        var button = new Button
        {
            Text = text,
            Disabled = !enabled,
            Alignment = HorizontalAlignment.Left,
            CustomMinimumSize = new Vector2(0f, 44f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        StyleButton(button);
        button.Pressed += onPressed;
        _pageHost.AddChild(button);
        return button;
    }

    private void FocusFirstButtonDeferred()
    {
        Callable.From(FocusFirstButton).CallDeferred();
    }

    private void FocusFirstButton()
    {
        if (!IsInsideTree()) return;

        foreach (Node child in _pageHost.GetChildren())
        {
            if (child is Button button && !button.Disabled && button.IsInsideTree())
            {
                button.GrabFocus();
                return;
            }
        }
    }

    private void AddFieldRow(string labelText, Control field)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);

        var label = new Label
        {
            Text = labelText,
            CustomMinimumSize = new Vector2(170f, 0f),
            VerticalAlignment = VerticalAlignment.Center
        };
        label.AddThemeFontSizeOverride("font_size", 15);
        label.AddThemeColorOverride("font_color", _textColor);
        row.AddChild(label);
        row.AddChild(field);

        _pageHost.AddChild(row);
    }

    private void StyleOptionButton(OptionButton option)
    {
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
        normal.SetContentMarginAll(8f);

        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = new Color(0.12f, 0.25f, 0.16f);

        option.AddThemeStyleboxOverride("normal", normal);
        option.AddThemeStyleboxOverride("hover", hover);
        option.AddThemeStyleboxOverride("pressed", normal);
        option.AddThemeStyleboxOverride("focus", normal);
        option.AddThemeFontSizeOverride("font_size", 15);
        option.AddThemeColorOverride("font_color", _textColor);
        option.AddThemeColorOverride("font_hover_color", Colors.White);
        option.AddThemeColorOverride("font_pressed_color", _textColor);
        option.AddThemeColorOverride("font_focus_color", _textColor);

        PopupMenu popup = option.GetPopup();
        popup.AddThemeColorOverride("font_color", _textColor);
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

    private void StyleButton(Button button, bool primary = false)    {
        Color baseColor = primary
            ? new Color(0.18f, 0.47f, 0.26f)
            : new Color(0.09f, 0.19f, 0.12f);
        Color hoverColor = primary
            ? new Color(0.24f, 0.60f, 0.33f)
            : new Color(0.13f, 0.28f, 0.18f);

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
        normal.SetContentMarginAll(10f);

        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = hoverColor;

        var pressed = (StyleBoxFlat)normal.Duplicate();
        pressed.BgColor = new Color(0.07f, 0.15f, 0.10f);

        var focus = (StyleBoxFlat)hover.Duplicate();
        focus.BorderColor = _accent;
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
        button.AddThemeFontSizeOverride("font_size", 17);
        button.AddThemeColorOverride("font_color", _textColor);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_disabled_color", new Color(0.38f, 0.45f, 0.39f));
    }
}
