using System;
using Godot;
using FootballSim.Core;
using FootballSim.Input;

namespace FootballSim.GodotClient;

/// <summary>
/// Reads the live keyboard / mouse / gamepad state into a <see cref="HumanInputController"/>.
/// Shared by the match and the training scene so both honour the Settings key bindings.
/// </summary>
public sealed class HumanControlReader
{
    // Priority when more than one kick button is held: one charge at a time (audit F16).
    private static readonly (ControlAction Action, GamepadAction PadAction, KickIntentType Intent)[] KickButtons =
    {
        (ControlAction.Pass, GamepadAction.Pass, KickIntentType.GroundPass),
        (ControlAction.ThroughPass, GamepadAction.ThroughPass, KickIntentType.ThroughPass),
        (ControlAction.Cross, GamepadAction.Cross, KickIntentType.Cross),
        (ControlAction.Shoot, GamepadAction.Shoot, KickIntentType.Shot)
    };

    private bool _prevTackle;

    /// <summary>Mouse aim is the active source only while the mouse has moved recently.</summary>
    public float MouseAimActivitySeconds { get; private set; }

    public void Read(HumanInputController? human, float dt, System.Numerics.Vector2? mouseAimPoint = null)
    {
        if (human is null || human.IsSpectatorMode) return;

        ControlBindings bindings = GameSettings.Controls;
        GamepadBindings pad = GameSettings.Gamepad;
        int padId = GamepadBindings.PrimaryDeviceId;
        bool hasPad = padId >= 0;

        float moveX = 0f;
        float moveZ = 0f;

        if (Held(bindings.Get(ControlAction.MoveRight)) || Godot.Input.IsPhysicalKeyPressed(Key.Right)) moveX += 1f;
        if (Held(bindings.Get(ControlAction.MoveLeft)) || Godot.Input.IsPhysicalKeyPressed(Key.Left)) moveX -= 1f;
        if (Held(bindings.Get(ControlAction.MoveDown)) || Godot.Input.IsPhysicalKeyPressed(Key.Down)) moveZ += 1f;
        if (Held(bindings.Get(ControlAction.MoveUp)) || Godot.Input.IsPhysicalKeyPressed(Key.Up)) moveZ -= 1f;

        if (hasPad)
        {
            Vector2 joy = new(
                Godot.Input.GetJoyAxis(padId, JoyAxis.LeftX),
                Godot.Input.GetJoyAxis(padId, JoyAxis.LeftY));
            if (joy.Length() > 0.25f)
            {
                moveX += joy.X;
                moveZ += joy.Y;
            }

            if (Godot.Input.IsJoyButtonPressed(padId, JoyButton.DpadUp)) moveZ -= 1f;
            if (Godot.Input.IsJoyButtonPressed(padId, JoyButton.DpadDown)) moveZ += 1f;
            if (Godot.Input.IsJoyButtonPressed(padId, JoyButton.DpadLeft)) moveX -= 1f;
            if (Godot.Input.IsJoyButtonPressed(padId, JoyButton.DpadRight)) moveX += 1f;
        }

        var moveVec = new System.Numerics.Vector2(moveX, moveZ);
        if (moveVec.LengthSquared() > 1f)
            moveVec = System.Numerics.Vector2.Normalize(moveVec);

        human.MoveStickXZ = moveVec;

        // Aim is the movement direction (left stick / WASD). The right stick is not an aim input.
        // Cursor raycast is only offered when no gamepad is connected, so a pad player's aim never
        // jumps to wherever the cursor happens to be.
        human.AimPointXZ = hasPad ? null : mouseAimPoint;
        if (human.AimPointXZ.HasValue)
            MouseAimActivitySeconds = 2.0f;
        else
            MouseAimActivitySeconds = MathF.Max(0f, MouseAimActivitySeconds - dt);

        human.SprintHeld = Held(bindings.Get(ControlAction.Sprint)) || (hasPad && pad.IsHeld(GamepadAction.Sprint, padId));

        // Chip modifier: RB / V. Sprint (R2 / Shift) doubles as the trivela modifier in the
        // controller's shot resolution, matching the requested gamepad mapping.
        human.ChipShotHeld = Held(bindings.Get(ControlAction.ChipShot)) ||
                             (hasPad && pad.IsHeld(GamepadAction.ChipShot, padId));

        human.ShieldHeld = Held(bindings.Get(ControlAction.Shield)) || (hasPad && pad.IsHeld(GamepadAction.Shield, padId));

        // Left Alt is always an extra finesse/modifier key on top of the rebindable ones.
        human.HighModifierHeld = Held(bindings.Get(ControlAction.HighModifier)) ||
                                 Godot.Input.IsPhysicalKeyPressed(Key.Alt) ||
                                 (hasPad && pad.IsHeld(GamepadAction.HighModifier, padId));

        bool passHeld = Held(bindings.Get(ControlAction.Pass)) ||
                        Godot.Input.IsMouseButtonPressed(MouseButton.Left) ||
                        (hasPad && pad.IsHeld(GamepadAction.Pass, padId));

        bool throughHeld = Held(bindings.Get(ControlAction.ThroughPass)) ||
                           (hasPad && pad.IsHeld(GamepadAction.ThroughPass, padId));

        // Cross and Tackle share the gamepad X button by default: X crosses while the trainee has
        // the ball and tackles while they do not.
        bool hasBallAtFeet = human.HasBallAtFeet;
        bool crossHeld = Held(bindings.Get(ControlAction.Cross)) ||
                         (hasPad && pad.IsHeld(GamepadAction.Cross, padId) && hasBallAtFeet);

        bool shootHeld = Held(bindings.Get(ControlAction.Shoot)) ||
                         Godot.Input.IsMouseButtonPressed(MouseButton.Right) ||
                         (hasPad && pad.IsHeld(GamepadAction.Shoot, padId));

        bool tackleHeld = Held(bindings.Get(ControlAction.Tackle)) ||
                          (hasPad && pad.IsHeld(GamepadAction.Tackle, padId) && !hasBallAtFeet);

        // Audit F16: one active charge. A second held kick button is ignored while another is
        // charging; a release only fires the button that owns the charge.
        HandleKickButton(human, passHeld, KickIntentType.GroundPass, dt);
        HandleKickButton(human, throughHeld, KickIntentType.ThroughPass, dt);
        HandleKickButton(human, crossHeld, KickIntentType.Cross, dt);
        HandleKickButton(human, shootHeld, KickIntentType.Shot, dt);

        if (tackleHeld && !_prevTackle) human.QueueTackle();
        _prevTackle = tackleHeld;
    }

    /// <summary>
    /// Help text generated from the live bindings, so the HUD can never drift from the actual keys.
    /// </summary>
    public static string DescribeControls()
    {
        ControlBindings keyboard = GameSettings.Controls;
        string keys =
            $"Pass {ControlBindings.KeyName(keyboard.Get(ControlAction.Pass))} · " +
            $"Through {ControlBindings.KeyName(keyboard.Get(ControlAction.ThroughPass))} · " +
            $"Cross {ControlBindings.KeyName(keyboard.Get(ControlAction.Cross))} · " +
            $"Shoot {ControlBindings.KeyName(keyboard.Get(ControlAction.Shoot))} · " +
            $"Chip {ControlBindings.KeyName(keyboard.Get(ControlAction.ChipShot))} · " +
            $"Tackle {ControlBindings.KeyName(keyboard.Get(ControlAction.Tackle))} · " +
            $"High {ControlBindings.KeyName(keyboard.Get(ControlAction.HighModifier))} · " +
            $"Sprint {ControlBindings.KeyName(keyboard.Get(ControlAction.Sprint))} · " +
            $"Shield {ControlBindings.KeyName(keyboard.Get(ControlAction.Shield))}";

        string padText = GamepadBindings.PrimaryDeviceId >= 0
            ? "   |   " + GameSettings.Gamepad.Describe()
            : "   |   (no gamepad connected)";

        return keys + padText;
    }

    private static void HandleKickButton(
        HumanInputController human,
        bool isHeld,
        KickIntentType intent,
        float dt)
    {
        if (isHeld)
        {
            // The controller keeps one charge; a second held button is ignored until it is released.
            human.UpdateButtonCharge(intent, dt);
        }
        else if (human.ChargingIntent == intent)
        {
            human.ReleaseChargedKick(intent, MathF.Max(0.22f, human.ChargedPower01));
        }
    }

    private static bool Held(Key key) =>
        key != Key.None && Godot.Input.IsPhysicalKeyPressed(key);
}

/// <summary>
/// Turns the mouse cursor into a pitch point for aiming (audit F16). The camera ray is intersected
/// with the ball-height ground plane, so the aim is a real world position rather than a screen guess.
/// </summary>
public static class MouseAim
{
    public static System.Numerics.Vector2? TryGetPitchPoint(Camera3D? camera, float planeY = 0.11f)
    {
        if (camera is null || !camera.IsInsideTree()) return null;

        Vector2 screen = camera.GetViewport().GetMousePosition();
        Vector3 origin = camera.ProjectRayOrigin(screen);
        Vector3 direction = camera.ProjectRayNormal(screen);
        if (MathF.Abs(direction.Y) < 1e-4f) return null;

        float t = (planeY - origin.Y) / direction.Y;
        if (t <= 0f) return null;

        Vector3 hit = origin + direction * t;
        return new System.Numerics.Vector2(hit.X, hit.Z);
    }

    /// <summary>True while the mouse should be treated as the active aim device.</summary>
    public static bool HasRecentMotion() =>
        Godot.Input.GetLastMouseVelocity().LengthSquared() > 4f;
}
