using System;
using System.Collections.Generic;
using Godot;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Physics;
using NVector2 = System.Numerics.Vector2;

namespace FootballSim.GodotClient;

/// <summary>
/// One simulated continuation of the live ball under the match's real drag / bounce / rolling model.
/// The debug overlay uses it to answer the two questions the old text HUD could not: where will this
/// ball actually stop, and when will it pass a point on the chosen pass line.
/// </summary>
internal sealed class BallFlightPreview
{
    public const float StepSeconds = 1f / 60f;
    private const int MaxSteps = 720; // 12 s: enough for any ground pass to roll out

    /// <summary>Planar/path samples from the build instant, starting with the live position at t=0.</summary>
    public readonly List<Vector3> Positions = new();
    public readonly List<float> Times = new();

    public Vector3 Start { get; private set; }
    public Vector3 RestingPoint { get; private set; }
    public bool HasRestingPoint { get; private set; }
    public bool LeftPitch { get; private set; }
    public float StoppedSeconds { get; private set; }
    public float LastSeconds => Times.Count > 0 ? Times[Times.Count - 1] : 0f;

    /// <summary>
    /// Replays the live ball through the same solver the match uses. Read-only: it clones the state.
    /// <paramref name="startPosition"/> rewinds the clone to the contact position when the overlay only saw
    /// the contact a frame or two after it happened, so the drawn resting point starts where the ball was
    /// actually struck. <paramref name="pitch"/> stops the replay at the first line the ball would fully
    /// cross: the live ball goes out of play there, so "where it stops 40 m off the pitch" is not an answer
    /// anybody can use.
    /// </summary>
    public void Build(BallState ball, BallPhysicsSolver solver, Vector3? startPosition = null,
        PitchDimensions? pitch = null)
    {
        Positions.Clear();
        Times.Clear();
        HasRestingPoint = false;
        LeftPitch = false;
        StoppedSeconds = 0f;

        BallState clone = ball.Clone();
        clone.HeldByPlayerId = null;
        if (startPosition is Vector3 start)
        {
            clone.Position = new System.Numerics.Vector3(start.X, start.Y, start.Z);
        }

        Start = new Vector3(clone.Position.X, clone.Position.Y, clone.Position.Z);
        Positions.Add(Start);
        Times.Add(0f);

        float goalLinePlane = pitch is null ? float.MaxValue : pitch.HalfLength + solver.Parameters.Radius;
        float touchLinePlane = pitch is null ? float.MaxValue : pitch.HalfWidth + solver.Parameters.Radius;
        float restingHeight = solver.Parameters.Radius + 0.01f;
        for (int step = 1; step <= MaxSteps; step++)
        {
            solver.Step(clone, StepSeconds);
            float time = step * StepSeconds;

            if (step % 2 == 0 || step <= 6)
            {
                Positions.Add(new Vector3(clone.Position.X, clone.Position.Y, clone.Position.Z));
                Times.Add(time);
            }

            if (!float.IsFinite(clone.Position.X) || !float.IsFinite(clone.Velocity.X))
                break;

            if (MathF.Abs(clone.Position.X) > goalLinePlane || MathF.Abs(clone.Position.Z) > touchLinePlane)
            {
                RestingPoint = new Vector3(
                    Math.Clamp(clone.Position.X, -pitch!.HalfLength, pitch.HalfLength),
                    solver.Parameters.Radius,
                    Math.Clamp(clone.Position.Z, -pitch.HalfWidth, pitch.HalfWidth));
                HasRestingPoint = true;
                LeftPitch = true;
                StoppedSeconds = time;
                break;
            }

            float horizontalSpeed = new NVector2(clone.Velocity.X, clone.Velocity.Z).Length();
            bool grounded = clone.Position.Y <= restingHeight && MathF.Abs(clone.Velocity.Y) <= 0.05f;
            if (grounded && horizontalSpeed <= solver.Parameters.StopVelocityEpsilon)
            {
                RestingPoint = new Vector3(clone.Position.X, solver.Parameters.Radius, clone.Position.Z);
                HasRestingPoint = true;
                StoppedSeconds = time;
                break;
            }
        }

        if (HasRestingPoint) return;

        RestingPoint = new Vector3(clone.Position.X, MathF.Min(clone.Position.Y, solver.Parameters.Radius), clone.Position.Z);
        HasRestingPoint = true;
        StoppedSeconds = LastSeconds;
    }

    /// <summary>Closest planar approach to a point within the sampled flight.</summary>
    public bool TryClosestApproach(NVector2 targetXZ, float horizonSeconds, out float distanceMeters,
        out float seconds, out Vector3 point)
    {
        distanceMeters = float.MaxValue;
        seconds = 0f;
        point = Start;
        bool found = false;

        for (int index = 0; index < Positions.Count; index++)
        {
            float time = Times[index];
            if (time > horizonSeconds) break;

            Vector3 sample = Positions[index];
            float distance = new NVector2(sample.X - targetXZ.X, sample.Z - targetXZ.Y).Length();
            if (found && distance >= distanceMeters) continue;

            found = true;
            distanceMeters = distance;
            seconds = time;
            point = sample;
        }

        return found;
    }

    /// <summary>
    /// First sampled time at which the ball has covered <paramref name="distanceAlong"/> metres along the
    /// straight pass line. Negative when the ball never gets that far before stopping.
    /// </summary>
    public float TimeAtProjection(Vector3 start, Vector3 target, float distanceAlong)
    {
        Vector3 delta = target - start;
        float length = new NVector2(delta.X, delta.Z).Length();
        if (length < 1e-3f) return -1f;

        var direction = new NVector2(delta.X / length, delta.Z / length);
        float previousAlong = 0f;
        float previousTime = 0f;

        for (int index = 0; index < Positions.Count; index++)
        {
            Vector3 sample = Positions[index];
            float along = (sample.X - start.X) * direction.X + (sample.Z - start.Z) * direction.Y;
            float time = Times[index];

            if (along >= distanceAlong)
            {
                if (index == 0) return time;
                float span = along - previousAlong;
                float fraction = span > 1e-5f ? (distanceAlong - previousAlong) / span : 1f;
                return Mathf.Lerp(previousTime, time, Mathf.Clamp(fraction, 0f, 1f));
            }

            previousAlong = along;
            previousTime = time;
        }

        return -1f;
    }
}

/// <summary>One defender's time-scaled reach across the chosen pass line, frozen at kick time.</summary>
internal readonly record struct DefenderReach(
    int PlayerId,
    Vector3 Position,
    float Radius,
    bool OverlapsCorridor);

/// <summary>The latest pass, frozen at kick time and then resolved with the actual outcome.</summary>
internal sealed class DeliverySnapshot
{
    public int KickerId;
    public TeamId Team;
    public KickIntentType Intent;
    public int KickTick;
    public int? ReceiverId;

    public Vector3 PasserPosition;
    public Vector3 ContactBallPosition;
    public Vector3 Target;
    public Vector3 InitialVelocity;
    public Vector3 ReceiverStart;
    public Vector3 ReceiverEnd;

    public float BallEtaSeconds = -1f;
    public float ReceiverEtaSeconds = -1f;
    public Vector3 BallClosestPoint;
    public Vector3 RestingPoint;
    public bool WouldLeavePitch;
    public float CorridorHalfWidth;
    public bool CorridorUnsafe;
    public readonly List<DefenderReach> DefenderReaches = new();

    public bool Resolved;
    public int ResolvedTick;
    public float ArrivalDeltaMeters;
    public int? ActualToucherId;

    public int AgeTicks(int currentTick) => currentTick - KickTick;
}

/// <summary>One option line in a frozen carrier decision.</summary>
internal sealed class DecisionOption
{
    public string ActionLabel = string.Empty;
    public string Reason = string.Empty;
    public float Score;
    public bool Chosen;
    public bool Rejected;
    public int TargetPlayerId;
    public Vector3 Target;
    public ValueTerms Terms;
    public CandidateKind Kind;
}

/// <summary>The carrier's decision as it looked when it was made, frozen for reading.</summary>
internal sealed class DecisionSnapshot
{
    public int PlayerId;
    public int CreatedTick;
    public int ExpiresTick;
    public string Title = string.Empty;
    public string SelectedIdentity = string.Empty;
    public Vector3 Origin;

    /// <summary>Seconds left in the three-second reading window, refreshed every frame.</summary>
    public float SecondsLeft;

    /// <summary>Every option the carrier considered: chosen first, then alternatives by score, then refusals.</summary>
    public readonly List<DecisionOption> Options = new();

    /// <summary>The rows the corner readout shows: winner, best alternative and the most useful refusal.</summary>
    public readonly List<DecisionOption> PanelRows = new();
}

/// <summary>Temporary floating reasons for "the ball is inside the trap radius but was not controlled".</summary>
internal sealed class TouchRejectionTracker
{
    private const int TagDurationTicks = 120; // 2 s
    private readonly Dictionary<int, (string Reason, int ExpiresTick)> _tags = new();

    public void Update(int currentTick)
    {
        if (_tags.Count == 0) return;

        List<int>? expired = null;
        foreach (KeyValuePair<int, (string Reason, int ExpiresTick)> pair in _tags)
        {
            if (pair.Value.ExpiresTick >= currentTick) continue;
            expired ??= new List<int>();
            expired.Add(pair.Key);
        }

        if (expired is null) return;
        foreach (int playerId in expired) _tags.Remove(playerId);
    }

    public void Tag(int playerId, string reason, int currentTick) =>
        _tags[playerId] = (reason, currentTick + TagDurationTicks);

    public bool TryGet(int playerId, int currentTick, out string reason)
    {
        if (_tags.TryGetValue(playerId, out (string Reason, int ExpiresTick) tag) && tag.ExpiresTick >= currentTick)
        {
            reason = tag.Reason;
            return true;
        }

        reason = string.Empty;
        return false;
    }

    public void Clear() => _tags.Clear();
}
