using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using FootballSim;

namespace FootballSim.GodotClient;

public enum ControlAction
{
    MoveUp,
    MoveDown,
    MoveLeft,
    MoveRight,
    Sprint,
    Pass,
    ThroughPass,
    Cross,
    Shoot,
    ChipShot,
    Tackle,
    Shield,
    HighModifier
}

/// <summary>
/// Keyboard bindings for the on-pitch controls. Mouse and gamepad buttons stay wired to the
/// same actions in <see cref="HumanControlReader"/> so rebinding a key never removes a controller.
/// </summary>
public sealed class ControlBindings
{
    public static readonly IReadOnlyList<ControlAction> AllActions = new[]
    {
        ControlAction.MoveUp,
        ControlAction.MoveDown,
        ControlAction.MoveLeft,
        ControlAction.MoveRight,
        ControlAction.Sprint,
        ControlAction.Pass,
        ControlAction.ThroughPass,
        ControlAction.Cross,
        ControlAction.Shoot,
        ControlAction.ChipShot,
        ControlAction.Tackle,
        ControlAction.Shield,
        ControlAction.HighModifier
    };

    private readonly Dictionary<ControlAction, Key> _keys = new();

    public ControlBindings() => ResetToDefaults();

    public Key Get(ControlAction action) =>
        _keys.TryGetValue(action, out Key key) ? key : Key.None;

    /// <summary>
    /// Binds <paramref name="key"/> to <paramref name="action"/>. If another action already owns
    /// the key the two bindings are swapped, so no two actions can ever share a key.
    /// </summary>
    public void Assign(ControlAction action, Key key)
    {
        if (key == Key.None) return;

        Key previous = Get(action);
        foreach (ControlAction other in AllActions)
        {
            if (other == action) continue;
            if (_keys.TryGetValue(other, out Key otherKey) && otherKey == key)
                _keys[other] = previous;
        }

        _keys[action] = key;
    }

    public void ResetToDefaults()
    {
        _keys.Clear();
        _keys[ControlAction.MoveUp] = Key.W;
        _keys[ControlAction.MoveDown] = Key.S;
        _keys[ControlAction.MoveLeft] = Key.A;
        _keys[ControlAction.MoveRight] = Key.D;
        _keys[ControlAction.Sprint] = Key.Shift;
        _keys[ControlAction.Pass] = Key.Space;
        _keys[ControlAction.ThroughPass] = Key.E;
        _keys[ControlAction.Cross] = Key.C;
        _keys[ControlAction.Shoot] = Key.F;
        _keys[ControlAction.ChipShot] = Key.V;
        _keys[ControlAction.Tackle] = Key.R;
        _keys[ControlAction.Shield] = Key.Ctrl;
        _keys[ControlAction.HighModifier] = Key.Q;
    }

    public static string DisplayName(ControlAction action) => action switch
    {
        ControlAction.MoveUp => "Move Up",
        ControlAction.MoveDown => "Move Down",
        ControlAction.MoveLeft => "Move Left",
        ControlAction.MoveRight => "Move Right",
        ControlAction.Sprint => "Sprint",
        ControlAction.Pass => "Pass",
        ControlAction.ThroughPass => "Through Pass",
        ControlAction.Cross => "Cross",
        ControlAction.Shoot => "Shoot",
        ControlAction.ChipShot => "Chip Shot (hold + shoot)",
        ControlAction.Tackle => "Tackle",
        ControlAction.Shield => "Shield Ball",
        ControlAction.HighModifier => "High / Finesse",
        _ => action.ToString()
    };

    public static string KeyName(Key key) =>
        key == Key.None ? "Unbound" : OS.GetKeycodeString(key);
}

/// <summary>
/// Persisted player preferences (window resolution, match length, last position, key bindings).
/// Stored in <c>user://settings.cfg</c> so it survives restarts and scene changes.
/// </summary>
public static class GameSettings
{
    private const string SettingsPath = "user://settings.cfg";
    private const int SettingsVersion = 2;
    public const int DefaultWindowWidth = 1280;
    public const int DefaultWindowHeight = 720;

    public static readonly IReadOnlyList<Vector2I> Resolutions = new[]
    {
        new Vector2I(1280, 720),
        new Vector2I(1366, 768),
        new Vector2I(1600, 900),
        new Vector2I(1920, 1080),
        new Vector2I(2560, 1440),
        new Vector2I(3840, 2160)
    };

    public static ControlBindings Controls { get; } = new();
    public static GamepadBindings Gamepad { get; } = new();
    public static Vector2I Resolution { get; private set; } = new(DefaultWindowWidth, DefaultWindowHeight);
    public static int HalfLengthMinutes { get; private set; } = MatchPresets.DefaultHalfLengthMinutes;
    public static int LastPositionIndex { get; private set; } = MatchPositions.IndexOf(MatchPositions.DefaultPlayerId);

    private static bool _loaded;

    public static int ResolutionIndex
    {
        get
        {
            for (int i = 0; i < Resolutions.Count; i++)
            {
                if (Resolutions[i] == Resolution) return i;
            }
            return 0;
        }
    }

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;

        var config = new ConfigFile();
        if (config.Load(SettingsPath) != Error.Ok) return;

        int width = Math.Max(800, (int)config.GetValue("display", "width", DefaultWindowWidth));
        int height = Math.Max(600, (int)config.GetValue("display", "height", DefaultWindowHeight));
        Resolution = new Vector2I(width, height);

        int minutes = ClampHalfLength((int)config.GetValue("match", "half_minutes", MatchPresets.DefaultHalfLengthMinutes));
        HalfLengthMinutes = minutes;
        LastPositionIndex = Math.Clamp(
            (int)config.GetValue("match", "position_index", LastPositionIndex),
            0,
            MatchPositions.All.Count - 1);

        foreach (ControlAction action in ControlBindings.AllActions)
        {
            string stored = (string)config.GetValue("controls", action.ToString(), string.Empty);
            if (Enum.TryParse(stored, ignoreCase: true, out Key key) && key != Key.None)
                Controls.Assign(action, key);
        }

        // Version 2 introduced the contextual gamepad X (Cross/Tackle). Older files may contain
        // accidental swaps or an unbound tackle, so start those users from the new gamepad defaults.
        int version = (int)config.GetValue("meta", "version", 1);
        if (version < SettingsVersion)
        {
            Gamepad.ResetToDefaults();
            return;
        }

        foreach (GamepadAction action in GamepadBindings.AllActions)
        {
            string stored = (string)config.GetValue("gamepad", action.ToString(), Gamepad.Get(action).Serialize());
            Gamepad.Assign(action, GamepadBinding.Parse(stored));
        }
    }

    public static void Save()
    {
        var config = new ConfigFile();
        config.SetValue("meta", "version", SettingsVersion);
        config.SetValue("display", "width", Resolution.X);
        config.SetValue("display", "height", Resolution.Y);
        config.SetValue("match", "half_minutes", HalfLengthMinutes);
        config.SetValue("match", "position_index", LastPositionIndex);

        foreach (ControlAction action in ControlBindings.AllActions)
            config.SetValue("controls", action.ToString(), Controls.Get(action).ToString());

        foreach (GamepadAction action in GamepadBindings.AllActions)
            config.SetValue("gamepad", action.ToString(), Gamepad.Get(action).Serialize());

        config.Save(SettingsPath);
    }

    public static void SetResolution(int resolutionIndex)
    {
        int index = Math.Clamp(resolutionIndex, 0, Resolutions.Count - 1);
        Resolution = Resolutions[index];
    }

    public static void SetHalfLengthMinutes(int minutes)
    {
        HalfLengthMinutes = ClampHalfLength(minutes);
    }

    public static void SetLastPositionIndex(int index)
    {
        LastPositionIndex = Math.Clamp(index, 0, MatchPositions.All.Count - 1);
    }

    public static void ResetControls()
    {
        Controls.ResetToDefaults();
        Gamepad.ResetToDefaults();
    }

    /// <summary>Applies the stored resolution to the real window and centres it on its screen.</summary>
    public static void ApplyResolution()
    {
        Vector2I screenSize = DisplayServer.ScreenGetSize(DisplayServer.WindowGetCurrentScreen());
        int width = Math.Min(Resolution.X, screenSize.X);
        int height = Math.Min(Resolution.Y, screenSize.Y);

        DisplayServer.WindowSetSize(new Vector2I(width, height));
        DisplayServer.WindowSetPosition(new Vector2I(
            Math.Max(0, (screenSize.X - width) / 2),
            Math.Max(0, (screenSize.Y - height) / 2)));
    }

    public static MatchSetupOptions BuildMatchSetup()
    {
        MatchPositionOption position = MatchPositions.All[
            Math.Clamp(LastPositionIndex, 0, MatchPositions.All.Count - 1)];

        return new MatchSetupOptions
        {
            HumanPlayerId = position.PlayerId,
            HalfDurationSeconds = ClampHalfLength(HalfLengthMinutes) * 60f
        };
    }

    private static int ClampHalfLength(int minutes) =>
        MatchPresets.ClampHalfLengthMinutes(minutes);
}
