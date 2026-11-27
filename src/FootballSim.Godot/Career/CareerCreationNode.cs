using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using FootballSim.Core;
using FootballSim.World.Career;
using FootballSim.World.Players;

namespace FootballSim.GodotClient;

/// <summary>
/// Career creation screen (plan stage W6, "Two Creation Modes, One Engine"): mode toggle, identity, foot, age,
/// goalkeeper flag, backstory/academy choice with its trade-offs spelled out, and the Custom potential panel.
/// The screen makes the plan's hard invariant explicit — the backstory never changes hidden potential — and
/// then hands a <see cref="CareerConfig"/> to <see cref="CareerSession.StartNewCareer"/>.
/// </summary>
public partial class CareerCreationNode : Control
{
    private static readonly string[] BackstoryLabels =
    {
        "Elite Academy — released youth prospect",
        "Regional Academy — solid grounding",
        "Local Club Youth — the default route",
        "Street Football — raw and resilient",
        "Multisport Late Starter — late to football"
    };

    private static readonly string[] BackstoryDescriptions =
    {
        "Starts further along his talent (highest starting realization) with early technical and positioning " +
        "tilts, excellent early coaching and high scouting visibility — but low resilience when benched or " +
        "dropped down the divisions.",
        "A good all-round start: medium-high realization, balanced technical/mental tilts, good coaching and " +
        "medium-high visibility. No notable weakness.",
        "Baseline realization with a small stamina/work-ethic tilt, modest coaching and low visibility. Solid " +
        "work ethic and steady resilience — the default amateur route.",
        "Low-medium realization but real dribbling and first-touch tilts, high resilience and creativity, and " +
        "lower discipline. Poor coaching and very low visibility: you have to earn the attention.",
        "Low technical realization with a body-first start (stamina and strength), weaker passing and first " +
        "touch, modest coaching and low visibility, but high work ethic and resilience."
    };

    private static readonly string[] PotentialLabels =
    {
        "Journeyman — amateur / semi-pro ceiling",
        "Regional Standout — solid lower-league ceiling",
        "Pro Prospect — top-flight prospect ceiling",
        "Top-Flight Star — top-flight star ceiling",
        "Generational — the rarest ceiling"
    };

    private VBoxContainer _pages = null!;
    private OptionButton _modeOption = null!;
    private LineEdit _firstName = null!;
    private LineEdit _lastName = null!;
    private OptionButton _footOption = null!;
    private OptionButton _ageOption = null!;
    private CheckBox _goalkeeper = null!;
    private OptionButton _backstoryOption = null!;
    private Label _backstoryDescription = null!;
    private VBoxContainer _customPanel = null!;
    private OptionButton _potentialOption = null!;
    private CheckBox _useSliders = null!;
    private VBoxContainer _sliderPanel = null!;
    private HSlider _athleticism = null!;
    private HSlider _technical = null!;
    private HSlider _mental = null!;
    private Label _athleticismValue = null!;
    private Label _technicalValue = null!;
    private Label _mentalValue = null!;
    private CheckBox _showHiddenPotential = null!;
    private Label _status = null!;
    private Button _startButton = null!;

    public override void _Ready()
    {
        _pages = CareerUi.BuildChrome(this, minimumWidth: 980f);
        BuildForm();
    }

    public override void _Input(InputEvent @event)
    {
        bool back = @event switch
        {
            InputEventKey key => key.Pressed && !key.Echo && key.Keycode == Key.Escape,
            InputEventJoypadButton pad => pad.Pressed && pad.ButtonIndex == JoyButton.B,
            _ => false
        };

        if (back)
        {
            GetTree().ChangeSceneToFile(GameSession.CareerEntryScene);
            GetViewport().SetInputAsHandled();
        }
    }

    private void BuildForm()
    {
        CareerUi.Clear(_pages);
        CareerUi.Heading(_pages, "NEW CAREER", 20);
        CareerUi.Paragraph(
            _pages,
            "Both creation modes run the exact same player engine: attributes, hidden ceilings, growth, injury " +
            "and emergent identity. Authentic hides the potential and rolls it from the football pyramid; Custom " +
            "lets you choose it. In both modes you start at a lower-league club and have to earn your way up.",
            13, CareerUi.MutedColor);

        VBoxContainer body = CareerUi.Scroll(_pages, minimumHeight: 480);

        _modeOption = CareerUi.Option();
        _modeOption.AddItem("Authentic — hidden potential (default)");
        _modeOption.AddItem("Custom — choose your potential");
        _modeOption.ItemSelected += _ => RefreshModeVisibility();
        CareerUi.FieldRow(body, "Creation mode", _modeOption);

        HBoxContainer names = CareerUi.Row(body);
        _firstName = CareerUi.LineEdit("First name");
        _lastName = CareerUi.LineEdit("Last name", "Player");
        names.AddChild(_firstName);
        names.AddChild(_lastName);

        _footOption = CareerUi.Option();
        _footOption.AddItem("Generated");
        _footOption.AddItem("Right");
        _footOption.AddItem("Left");
        CareerUi.FieldRow(body, "Preferred foot", _footOption);

        _ageOption = CareerUi.Option();
        for (int age = CareerConfig.MinimumStartingAge; age <= CareerConfig.MaximumStartingAge; age++)
        {
            _ageOption.AddItem($"{age} years old");
        }
        _ageOption.Selected = 1; // 17: a long development runway but old enough to be near a first team
        CareerUi.FieldRow(body, "Starting age", _ageOption);

        _goalkeeper = CareerUi.CheckBox("Create as a goalkeeper");
        body.AddChild(_goalkeeper);

        CareerUi.Heading(body, "BACKSTORY / ACADEMY", 15);
        CareerUi.Paragraph(
            body,
            "Backstory changes your starting point, personality and early opportunity — it never changes your " +
            "hidden potential. The same seed with a different academy produces exactly the same ceilings.",
            12, CareerUi.Accent);

        _backstoryOption = CareerUi.Option();
        foreach (string label in BackstoryLabels) _backstoryOption.AddItem(label);
        _backstoryOption.Selected = 2; // Local Club Youth
        _backstoryOption.ItemSelected += _ => RefreshBackstoryDescription();
        CareerUi.FieldRow(body, "Backstory", _backstoryOption);

        _backstoryDescription = CareerUi.Paragraph(body, string.Empty, 12, CareerUi.MutedColor);

        _customPanel = CareerUi.Column(body);
        CareerUi.Heading(_customPanel, "CUSTOM POTENTIAL", 15);
        CareerUi.Paragraph(
            _customPanel,
            "Pick a preset ceiling, or take the sliders and shape the three latent aptitudes yourself. " +
            "A keeper gets keeper-level hands either way.",
            12, CareerUi.MutedColor);

        _potentialOption = CareerUi.Option();
        foreach (string label in PotentialLabels) _potentialOption.AddItem(label);
        _potentialOption.Selected = 1; // Regional Standout
        CareerUi.FieldRow(_customPanel, "Potential preset", _potentialOption);

        _useSliders = CareerUi.CheckBox("Use custom aptitude sliders instead of a preset");
        _useSliders.Toggled += _ => RefreshSliderVisibility();
        _customPanel.AddChild(_useSliders);

        _sliderPanel = CareerUi.Column(_customPanel);
        (_athleticism, _athleticismValue) = AddSlider(_sliderPanel, "Athleticism", 52);
        (_technical, _technicalValue) = AddSlider(_sliderPanel, "Technical", 52);
        (_mental, _mentalValue) = AddSlider(_sliderPanel, "Mental", 55);

        _showHiddenPotential = CareerUi.CheckBox("Show exact potential numbers in my player screen");
        _pages.AddChild(_showHiddenPotential);

        _status = CareerUi.Paragraph(_pages, string.Empty, 13, CareerUi.WarnColor);

        HBoxContainer actions = CareerUi.Row(_pages, separation: 10);
        _startButton = CareerUi.Button(actions, "Start Career", StartCareer, primary: true);
        CareerUi.Button(actions, "Back", () => GetTree().ChangeSceneToFile(GameSession.CareerEntryScene));

        RefreshBackstoryDescription();
        RefreshModeVisibility();
        Callable.From(() => CareerUi.FocusFirstIfNone(this)).CallDeferred();
    }

    private (HSlider Slider, Label ValueLabel) AddSlider(Node parent, string label, int initial)
    {
        HBoxContainer row = CareerUi.Row(parent);
        var text = new Label
        {
            Text = label,
            CustomMinimumSize = new Vector2(140f, 0f),
            VerticalAlignment = VerticalAlignment.Center
        };
        text.AddThemeFontSizeOverride("font_size", 13);
        text.AddThemeColorOverride("font_color", CareerUi.TextColor);
        row.AddChild(text);

        var slider = new HSlider
        {
            MinValue = 20,
            MaxValue = 95,
            Step = 1,
            Value = initial,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, 28f)
        };
        row.AddChild(slider);

        var value = new Label
        {
            Text = initial.ToString(),
            CustomMinimumSize = new Vector2(46f, 0f),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        value.AddThemeFontSizeOverride("font_size", 13);
        value.AddThemeColorOverride("font_color", CareerUi.Accent);
        row.AddChild(value);

        slider.ValueChanged += newValue => value.Text = ((int)newValue).ToString();
        return (slider, value);
    }

    private void RefreshBackstoryDescription()
    {
        int index = Math.Clamp(_backstoryOption.Selected, 0, BackstoryDescriptions.Length - 1);
        _backstoryDescription.Text = BackstoryDescriptions[index];
    }

    private void RefreshModeVisibility() => _customPanel.Visible = _modeOption.Selected == 1;

    private void RefreshSliderVisibility()
    {
        bool sliders = _useSliders.ButtonPressed;
        _sliderPanel.Visible = sliders;
        _potentialOption.Disabled = sliders;
    }

    private void StartCareer()
    {
        try
        {
            CareerConfig config = BuildConfig();
            config.Validate();

            _startButton.Disabled = true;
            _status.Text = "Generating the world…";
            CareerSession session = CareerSession.StartNewCareer(config);

            GameSession.ActiveCareer = session;
            GameSession.PendingCareerMatch = null;
            CareerSaveFiles.TryAutosave(session, out _);
            GetTree().ChangeSceneToFile(GameSession.CareerHubScene);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            InvalidDataException or FileNotFoundException or IOException or
            FootballSim.World.Data.WorldPackException)
        {
            _startButton.Disabled = false;
            _status.Text = $"Could not start the career: {exception.Message}";
        }
    }

    private CareerConfig BuildConfig()
    {
        bool custom = _modeOption.Selected == 1;
        bool sliders = custom && _useSliders.ButtonPressed;

        LatentAptitudes? aptitudes = null;
        if (sliders)
        {
            float athleticism = (float)_athleticism.Value;
            float technical = (float)_technical.Value;
            float mental = (float)_mental.Value;
            float goalkeeping = _goalkeeper.ButtonPressed
                ? Math.Clamp((athleticism + technical + mental) / 3f + 8f, 5f, 98f)
                : 20f;
            aptitudes = new LatentAptitudes(athleticism, technical, mental, goalkeeping);
        }

        PotentialPreset? potential = custom && !sliders
            ? (PotentialPreset)Math.Clamp(_potentialOption.Selected, 0, PotentialLabels.Length - 1)
            : null;

        Footedness? foot = _footOption.Selected switch
        {
            1 => Footedness.Right,
            2 => Footedness.Left,
            _ => null
        };

        return new CareerConfig
        {
            Mode = custom ? CareerCreationMode.Custom : CareerCreationMode.Authentic,
            FirstName = _firstName.Text.Trim(),
            LastName = _lastName.Text.Trim(),
            PreferredFoot = foot,
            StartingAge = CareerConfig.MinimumStartingAge + _ageOption.Selected,
            Backstory = (BackstoryKind)Math.Clamp(_backstoryOption.Selected, 0, 4),
            PreferGoalkeeper = _goalkeeper.ButtonPressed,
            CustomAptitudes = aptitudes,
            Potential = potential,
            ShowHiddenPotential = _showHiddenPotential.ButtonPressed,
            InitialTrainingFocus = TrainingFocus.Balanced
        };
    }
}
