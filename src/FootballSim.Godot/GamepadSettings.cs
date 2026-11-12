using System;
using System.Collections.Generic;
using Godot;

namespace FootballSim.GodotClient;

public enum GamepadAction
{
    Sprint,
    Shield,
    HighModifier,
    Pass,
    ThroughPass,
    Cross,
    Shoot,
    ChipShot,
    Tackle
}

public enum GamepadBindingKind
{
    None,
    Button,
    Axis
}

/// <summary>
/// One gamepad control: a button, a trigger axis, or unbound.
/// </summary>
public readonly record struct GamepadBinding(GamepadBindingKind Kind, JoyButton Button, JoyAxis Axis)
{
    public static GamepadBinding None => new(GamepadBindingKind.None, JoyButton.A, JoyAxis.LeftX);
    public static GamepadBinding FromButton(JoyButton button) => new(GamepadBindingKind.Button, button, JoyAxis.LeftX);
    public static GamepadBinding FromAxis(JoyAxis axis) => new(GamepadBindingKind.Axis, JoyButton.A, axis);

    public bool IsHeld() => IsHeld(GamepadBindings.PrimaryDeviceId);

    public bool IsHeld(int device)
    {
        if (device < 0) return false;

        return Kind switch
        {
            GamepadBindingKind.Button => Godot.Input.IsJoyButtonPressed(device, Button),
            GamepadBindingKind.Axis => MathF.Abs(Godot.Input.GetJoyAxis(device, Axis)) > 0.35f,
            _ => false
        };
    }

    public string DisplayName => Kind switch
    {
        GamepadBindingKind.Button => ButtonName(Button),
        GamepadBindingKind.Axis => AxisName(Axis),
        _ => "Unbound"
    };

    public string Serialize() => Kind switch
    {
        GamepadBindingKind.Button => "button:" + Button,
        GamepadBindingKind.Axis => "axis:" + Axis,
        _ => "none"
    };

    public static GamepadBinding Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
            return None;

        string[] parts = text.Split(':', 2);
        if (parts.Length != 2) return None;

        if (string.Equals(parts[0], "button", StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse(parts[1], ignoreCase: true, out JoyButton button))
        {
            return FromButton(button);
        }

        if (string.Equals(parts[0], "axis", StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse(parts[1], ignoreCase: true, out JoyAxis axis))
        {
            return FromAxis(axis);
        }

        return None;
    }

    private static string ButtonName(JoyButton button) => button switch
    {
        JoyButton.A => "A",
        JoyButton.B => "B",
        JoyButton.X => "X",
        JoyButton.Y => "Y",
        JoyButton.LeftShoulder => "LB",
        JoyButton.RightShoulder => "RB",
        JoyButton.LeftStick => "L3",
        JoyButton.RightStick => "R3",
        JoyButton.Back => "Back",
        JoyButton.Start => "Start",
        JoyButton.Guide => "Guide",
        JoyButton.DpadUp => "D-Pad Up",
        JoyButton.DpadDown => "D-Pad Down",
        JoyButton.DpadLeft => "D-Pad Left",
        JoyButton.DpadRight => "D-Pad Right",
        _ => button.ToString()
    };

    private static string AxisName(JoyAxis axis) => axis switch
    {
        JoyAxis.TriggerLeft => "LT",
        JoyAxis.TriggerRight => "RT",
        _ => axis.ToString()
    };
}

/// <summary>
/// Persisted gamepad bindings. Movement (left stick / D-pad) — which is also the aim — stays fixed;
/// every action button can be rebound here.
/// </summary>
public sealed class GamepadBindings
{
    public static readonly IReadOnlyList<GamepadAction> AllActions = new[]
    {
        GamepadAction.Sprint,
        GamepadAction.Shield,
        GamepadAction.HighModifier,
        GamepadAction.Pass,
        GamepadAction.ThroughPass,
        GamepadAction.Cross,
        GamepadAction.Shoot,
        GamepadAction.ChipShot,
        GamepadAction.Tackle
    };

    private readonly Dictionary<GamepadAction, GamepadBinding> _bindings = new();

    public GamepadBindings() => ResetToDefaults();

    public GamepadBinding Get(GamepadAction action) =>
        _bindings.TryGetValue(action, out GamepadBinding binding) ? binding : GamepadBinding.None;

    /// <summary>
    /// Binds a control to <paramref name="action"/>. If another action already owns it, the two
    /// bindings are swapped so two actions never share one input.
    /// </summary>
    public void Assign(GamepadAction action, GamepadBinding binding)
    {
        GamepadBinding previous = Get(action);
        foreach (GamepadAction other in AllActions)
        {
            if (other == action) continue;
            if (CanShareTheSameInput(action, other)) continue;
            if (_bindings.TryGetValue(other, out GamepadBinding otherBinding) && otherBinding == binding)
                _bindings[other] = previous;
        }

        _bindings[action] = binding;
    }

    /// <summary>Cross and Tackle intentionally share X: Cross with the ball, Tackle without it.</summary>
    private static bool CanShareTheSameInput(GamepadAction a, GamepadAction b) =>
        (a == GamepadAction.Cross && b == GamepadAction.Tackle) ||
        (a == GamepadAction.Tackle && b == GamepadAction.Cross);

    public void ResetToDefaults()
    {
        _bindings.Clear();
        _bindings[GamepadAction.Sprint] = GamepadBinding.FromAxis(JoyAxis.TriggerRight);
        _bindings[GamepadAction.Shield] = GamepadBinding.FromButton(JoyButton.LeftShoulder);
        _bindings[GamepadAction.HighModifier] = GamepadBinding.FromAxis(JoyAxis.TriggerLeft);
        _bindings[GamepadAction.Pass] = GamepadBinding.FromButton(JoyButton.A);
        _bindings[GamepadAction.ThroughPass] = GamepadBinding.FromButton(JoyButton.Y);
        _bindings[GamepadAction.Cross] = GamepadBinding.FromButton(JoyButton.X);
        _bindings[GamepadAction.Shoot] = GamepadBinding.FromButton(JoyButton.B);
        _bindings[GamepadAction.ChipShot] = GamepadBinding.FromButton(JoyButton.RightShoulder);
        _bindings[GamepadAction.Tackle] = GamepadBinding.FromButton(JoyButton.X);
    }

    /// <summary>
    /// Device id of the pad the game reads. Audit F16: input used to be hard-coded to device 0,
    /// which is the wrong pad whenever the player's controller is not the first registered one.
    /// </summary>
    public static int PrimaryDeviceId
    {
        get
        {
            var pads = Godot.Input.GetConnectedJoypads();
            return pads.Count > 0 ? pads[0] : -1;
        }
    }

    public bool IsHeld(GamepadAction action) => IsHeld(action, PrimaryDeviceId);

    public bool IsHeld(GamepadAction action, int device) => Get(action).IsHeld(device);

    /// <summary>Help text generated from the live gamepad bindings.</summary>
    public string Describe()
    {
        return $"Pad: Pass {Get(GamepadAction.Pass).DisplayName} · " +
               $"Through {Get(GamepadAction.ThroughPass).DisplayName} · " +
               $"Cross {Get(GamepadAction.Cross).DisplayName} · " +
               $"Shoot {Get(GamepadAction.Shoot).DisplayName} · " +
               $"Chip {Get(GamepadAction.ChipShot).DisplayName} · " +
               $"Tackle {Get(GamepadAction.Tackle).DisplayName} · " +
               $"High {Get(GamepadAction.HighModifier).DisplayName} · " +
               $"Sprint {Get(GamepadAction.Sprint).DisplayName} · " +
               $"Shield {Get(GamepadAction.Shield).DisplayName}";
    }

    public static string DisplayName(GamepadAction action) => action switch
    {
        GamepadAction.Sprint => "Sprint",
        GamepadAction.Shield => "Shield Ball",
        GamepadAction.HighModifier => "High / Finesse",
        GamepadAction.Pass => "Pass",
        GamepadAction.ThroughPass => "Through Pass",
        GamepadAction.Cross => "Cross",
        GamepadAction.Shoot => "Shoot",
        GamepadAction.ChipShot => "Chip Shot (hold + shoot)",
        GamepadAction.Tackle => "Tackle",
        _ => action.ToString()
    };
}
