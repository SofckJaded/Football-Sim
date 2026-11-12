using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Scenarios;

/// <summary>
/// Command factories for scenario opponents. A fixture that wants "this defender just stands there"
/// must not silently get a utility AI that presses, so scripted sides are built from explicit
/// commands rather than from a disabled-feature switch.
/// </summary>
public static class ScenarioCommands
{
    /// <summary>Stand still and keep facing the current direction.</summary>
    public static FootballerCommand Hold(Vector2? facing = null) => FootballerCommand.Idle with
    {
        DesiredFacingDirectionXZ = Normalize(facing ?? new Vector2(1f, 0f), new Vector2(1f, 0f))
    };

    /// <summary>Run or jog in a direction, optionally sprinting.</summary>
    public static FootballerCommand Move(Vector2 direction, bool sprint = false, Vector2? facing = null)
    {
        Vector2 normalized = Normalize(direction, Vector2.Zero);
        return FootballerCommand.Idle with
        {
            DesiredMoveDirectionXZ = normalized,
            DesiredFacingDirectionXZ = Normalize(facing ?? normalized, new Vector2(1f, 0f)),
            Sprint = sprint
        };
    }

    /// <summary>Jog to a world position along the straight line from <paramref name="from"/>.</summary>
    public static FootballerCommand MoveToward(Vector3 from, Vector3 to, bool sprint = false)
    {
        Vector2 delta = new(to.X - from.X, to.Z - from.Z);
        return Move(delta, sprint);
    }

    /// <summary>The command a unit vector, or the fallback when the input is degenerate.</summary>
    private static Vector2 Normalize(Vector2 value, Vector2 fallback)
    {
        float length = value.Length();
        return length > 1e-5f ? value / length : fallback;
    }
}

/// <summary>
/// Returns one fixed command for the whole fragment. This is the "opponent that does not react"
/// primitive: useful for isolating a tactical behaviour from an opponent's response.
/// </summary>
public sealed class FixedCommandController : IFootballerController
{
    private readonly FootballerCommand _command;
    private readonly string _label;

    public FixedCommandController(FootballerCommand command, string label = "fixed")
    {
        _command = command;
        _label = label;
    }

    public ControllerSourceType SourceType => ControllerSourceType.Scripted;
    public FootballerCommand Decide(in FootballerObservation observation, float dt) => _command;
    public string GetDebugSummary() => _label;
}

/// <summary>
/// Replays a per-decision command sequence, then falls back to a fixed command. Decisions are counted
/// from zero, one per live tick this player is asked to decide — scenarios do not suspend play, so
/// decision ordinal equals elapsed live tick.
/// </summary>
public sealed class ScriptedCommandController : IFootballerController
{
    private readonly Dictionary<int, FootballerCommand> _script;
    private readonly FootballerCommand _after;
    private readonly string _label;
    private int _decisions;

    public ScriptedCommandController(
        IReadOnlyDictionary<int, FootballerCommand> script,
        FootballerCommand? afterScript = null,
        string label = "scripted")
    {
        ArgumentNullException.ThrowIfNull(script);
        _script = new Dictionary<int, FootballerCommand>(script.Count);
        foreach (KeyValuePair<int, FootballerCommand> entry in script)
        {
            if (entry.Key < 0)
                throw new ArgumentOutOfRangeException(nameof(script), entry.Key, "Decision index must not be negative.");
            _script[entry.Key] = entry.Value;
        }
        _after = afterScript ?? ScenarioCommands.Hold();
        _label = label;
    }

    public ControllerSourceType SourceType => ControllerSourceType.Scripted;
    public int DecisionsMade => _decisions;

    public FootballerCommand Decide(in FootballerObservation observation, float dt)
    {
        int index = _decisions++;
        return _script.TryGetValue(index, out FootballerCommand command) ? command : _after;
    }

    public string GetDebugSummary() => $"{_label} (decisions {_decisions}, scripted {_script.Count})";
}
