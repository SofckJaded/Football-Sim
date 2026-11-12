using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Tactics;

namespace FootballSim.AI;

/// <summary>
/// One decision as the engine saw it: who decided, under which duty, what won, by how much, and what
/// came second.
///
/// This is deliberately *not* a full candidate dump. The value of a trace is that it is bounded and
/// cheap enough to leave switched on while reproducing a bug; a full utility table per player per tick
/// belongs in the inspector, not in a running match.
///
/// Fields that the current implementation cannot honestly supply are nullable and documented as such,
/// rather than filled with a zero that would look like a measurement:
/// <list type="bullet">
/// <item><see cref="KnowledgeAgeTicks"/> — perception has no memory yet (arrives in A2.2).</item>
/// <item><see cref="CandidateKey"/> — candidate identity is still the action name; stable keys arrive
/// in A1.1.</item>
/// <item><see cref="IntentId"/> — there is no persistent intent object yet (arrives in A1.2).</item>
/// </list>
/// </summary>
public readonly record struct DecisionTraceEntry(
    int Tick,
    int PlayerId,
    TeamId Team,
    TacticalRole Role,
    TacticalJob Job,
    string JobReason,
    string SelectedAction,
    float SelectedUtility,
    string? RunnerUpAction,
    float RunnerUpUtility,
    int CandidateCount
)
{
    /// <summary>Target point of the chosen action, when the action has one.</summary>
    public Vector3? Target { get; init; }

    /// <summary>Player the chosen action is aimed at (a pass receiver), when it is aimed at one.</summary>
    public int? TargetPlayerId { get; init; }

    /// <summary>How stale the observation behind this decision was. Null until A2.2 models memory age.</summary>
    public int? KnowledgeAgeTicks { get; init; }

    /// <summary>Stable candidate identity (action type + target/run/region + variant). Null until A1.1.</summary>
    public string? CandidateKey { get; init; }

    /// <summary>Owning intent, when the action continues one. Null until A1.2 introduces persistence.</summary>
    public int? IntentId { get; init; }

    public string Describe() =>
        $"t{Tick} p{PlayerId} {Job}{(string.IsNullOrEmpty(JobReason) ? string.Empty : $"({JobReason})")} " +
        $"-> {SelectedAction} U={SelectedUtility:F3}" +
        (RunnerUpAction is null ? string.Empty : $" (2nd {RunnerUpAction} U={RunnerUpUtility:F3})") +
        $" of {CandidateCount}";
}

/// <summary>
/// Bounded, append-only decision log. Oldest entries are dropped when full, so a long match cannot
/// grow it without limit and a fresh problem is always visible at the end.
///
/// Recording is off by default: <see cref="IsEnabled"/> is checked by the engine before it builds an
/// entry, so a tracing-off run pays one boolean test per player per tick.
/// </summary>
public sealed class DecisionTraceBuffer
{
    public const int DefaultCapacity = 4096;

    private readonly List<DecisionTraceEntry> _entries;

    public DecisionTraceBuffer(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        Capacity = capacity;
        _entries = new List<DecisionTraceEntry>(Math.Min(capacity, 256));
    }

    public int Capacity { get; }

    /// <summary>
    /// Off by default. Tracing must never consume simulation randomness or alter a decision, so this
    /// only gates recording — the engine's decision path itself does not branch on it.
    /// </summary>
    public bool IsEnabled { get; set; }

    public int Count => _entries.Count;
    public bool IsFull => _entries.Count >= Capacity;

    public void Record(in DecisionTraceEntry entry)
    {
        if (_entries.Count >= Capacity) _entries.RemoveAt(0);
        _entries.Add(entry);
    }

    public void Record(
        int tick, FootballerObservation observation, string selectedAction, float selectedUtility,
        string? runnerUpAction, float runnerUpUtility, int candidateCount,
        Vector3? target = null, int? targetPlayerId = null)
    {
        Record(new DecisionTraceEntry(
            tick, observation.SelfId, observation.SelfTeam, observation.SelfRole,
            observation.TacticalJob, observation.JobReason,
            selectedAction, selectedUtility, runnerUpAction, runnerUpUtility, candidateCount)
        {
            Target = target,
            TargetPlayerId = targetPlayerId
        });
    }

    public IReadOnlyList<DecisionTraceEntry> Entries => _entries;

    /// <summary>Most recent entry, or null when nothing has been recorded.</summary>
    public DecisionTraceEntry? Last => _entries.Count == 0 ? null : _entries[^1];

    public void Clear() => _entries.Clear();
}
