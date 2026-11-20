using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;

namespace FootballSim.AI;

/// <summary>
/// What a player's attention is on while his head is turned (A7.1).
///
/// The ball is deliberately not a value here: <see cref="PerceptionSystem"/> always publishes the ball,
/// because it is the action point of the game and a player who "lost" it would be modelling a defect, not
/// attention. A scan exists for the part of the pitch the body is not facing.
/// </summary>
public enum ScanAttentionKind
{
    /// <summary>No deliberate scan: the head faces where the body faces.</summary>
    None = 0,

    /// <summary>A believed opponent was last seen long enough ago to be worth a fresh look.</summary>
    Threat,

    /// <summary>The blind space behind the player, where an unseen threat would be standing or running.</summary>
    DangerSpace
}

/// <summary>
/// One player's head state on one tick (A7.1): where the head points relative to the body, what attention
/// produced the turn, and whether the head has arrived and is holding (a completed visual fix).
///
/// The default value is the neutral state every controller saw before A7.1: the head is not turned, so the
/// body facing <i>is</i> the gaze and <see cref="IsScanning"/> is false. A standalone observation that never
/// sets this therefore behaves exactly as it did before scanning existed.
/// </summary>
public readonly record struct ScanStatus(ScanAttentionKind Attention, float HeadYawDegrees, bool IsScanning)
{
    /// <summary>The neutral state: head facing where the body faces.</summary>
    public static ScanStatus None => default;

    /// <summary>
    /// Where the head/eyes point in the XZ plane, given the body facing. The head yaw is a rotation about
    /// the body's vertical axis; the result is a unit direction (the body facing is normalised first, so a
    /// caller that hands in an unnormalised facing cannot produce a non-unit gaze).
    /// </summary>
    public Vector2 GazeFrom(in Vector2 bodyFacingXZ)
    {
        Vector2 facing = SimMath.SafeNormalize(bodyFacingXZ, Vector2.UnitX);
        if (MathF.Abs(HeadYawDegrees) < 1e-4f) return facing;

        float radians = HeadYawDegrees * (MathF.PI / 180f);
        float sin = MathF.Sin(radians);
        float cos = MathF.Cos(radians);
        return new Vector2(
            facing.X * cos - facing.Y * sin,
            facing.X * sin + facing.Y * cos);
    }
}

/// <summary>
/// The facts one player's scan decision reads (A7.1). Deliberately tiny: whether the ball is about to
/// arrive, whether he is carrying it, and the one believed opponent whose sighting has gone stale.
/// </summary>
/// <param name="PlayerId">Observer id. Its parity seeds which shoulder a danger scan uses first.</param>
/// <param name="Tick">Engine tick, used for hold and cooldown windows (absolute, never a delta).</param>
/// <param name="BodyFacingXZ">The body facing the head turns relative to.</param>
/// <param name="Receiving">
/// A planned pass is in flight to this player, or he is contesting a loose ball nearby: the "scan before
/// an uncertain action" case.
/// </param>
/// <param name="IsCarrier">He currently controls the ball. A carrier does not scan (A7.3 plans reception).</param>
/// <param name="StaleThreatKnown">
/// A believed opponent within attention range has not been seen for the refresh threshold; the position and
/// direction come from <see cref="PlayerMemory"/>, never from the authoritative world.
/// </param>
/// <param name="ThreatDirectionXZ">Direction to that believed opponent, in world XZ.</param>
/// <param name="ThreatDistanceMeters">Distance to that believed opponent, in metres.</param>
public readonly record struct ScanSituation(
    int PlayerId,
    int Tick,
    Vector2 BodyFacingXZ,
    bool Receiving,
    bool IsCarrier,
    bool StaleThreatKnown,
    Vector2 ThreatDirectionXZ,
    float ThreatDistanceMeters);

/// <summary>
/// Match-level scan telemetry (A7.1). Diagnostics only: no decision reads it, and it consumes no
/// simulation randomness.
/// </summary>
/// <param name="ScanStarts">Completed visual fixes (the head arrived on an attention target and held).</param>
/// <param name="ThreatScanStarts">Those aimed at a believed opponent.</param>
/// <param name="DangerScanStarts">Those aimed into the blind space behind the player.</param>
/// <param name="ScanTicks">Player-ticks spent holding a visual fix.</param>
/// <param name="ObserverTicks">Player-ticks observed with scanning enabled (the denominator).</param>
/// <param name="RevealedByGaze">
/// Opponent observations that were inside the gaze cone but outside the body-facing cone: sightings the
/// head turn is the reason for.
/// </param>
/// <param name="OccludedByBody">Opponent observations suppressed because a body stood on the sight line.</param>
public readonly record struct ScanTelemetry(
    int ScanStarts,
    int ThreatScanStarts,
    int DangerScanStarts,
    long ScanTicks,
    long ObserverTicks,
    long RevealedByGaze,
    long OccludedByBody)
{
    /// <summary>Share of observed player-ticks spent fixing a scan target.</summary>
    public float ScanTickShare01 => ObserverTicks <= 0 ? 0f : ScanTicks / (float)ObserverTicks;

    /// <summary>Completed scans per player per minute of observed live play.</summary>
    public float ScansPerPlayerMinute => ObserverTicks <= 0 ? 0f : ScanStarts / (ObserverTicks / 3600f);
}

/// <summary>
/// A7.1: per-player head/gaze state and the bounded scan motion that feeds the A2.2 memory.
///
/// Before this existed a player saw the world through one cone fixed to the body facing
/// (<see cref="PerceptionSystem"/>'s "±100° of facing"). That made "looking around" impossible: a receiver
/// could never learn about a defender behind him before the ball arrived, and an unmodelled head turn cost
/// nothing. Here:
/// <list type="bullet">
/// <item><b>the head is independent of the body</b>: a head yaw relative to the body facing, bounded by
/// <see cref="AiRuntimeConfig.ScanMaxHeadTurnDegrees"/> (a neck cannot spin);</item>
/// <item><b>the motion is bounded</b>: the head turns at
/// <see cref="AiRuntimeConfig.ScanTurnRateDegreesPerSecond"/>, holds the target for
/// <see cref="AiRuntimeConfig.ScanHoldSeconds"/>, then returns to the body facing through a cooldown, so
/// looking behind is a real glance away from the ball rather than free sight;</item>
/// <item><b>attention decides when</b>: a player about to receive (or contest a loose ball) glances at the
/// one believed opponent whose sighting is stale, or into the blind space behind him — and the danger side
/// alternates, so repeated receptions cover both shoulders;</item>
/// <item><b>the belief stays the source</b>: the threat target is a <see cref="PlayerBelief"/> position,
/// so a player scans where he <i>remembers</i> someone, never where the world actually has him.</item>
/// </list>
///
/// The planner is pure state and deterministic: no RNG, no wall clock, absolute ticks only.
/// </summary>
public sealed class ScanPlanner
{
    /// <summary>How close the head must be to its target before a visual fix is recorded, in degrees.</summary>
    private const float ArrivalToleranceDegrees = 3f;

    /// <summary>Player head state. Mutable and internal to the planner.</summary>
    private sealed class HeadState
    {
        public float YawDegrees;
        public int HoldUntilTick;
        public int CooldownUntilTick;
        public bool IsScanning;
        public ScanAttentionKind Attention;
        public int DangerSide;
    }

    private readonly float _maxHeadTurnDegrees;
    private readonly float _turnRateDegreesPerSecond;
    private readonly int _holdTicks;
    private readonly int _cooldownTicks;
    private readonly float _attentionRangeMeters;
    private readonly Dictionary<int, HeadState> _heads = new();
    private int _scanStarts;
    private int _threatScanStarts;
    private int _dangerScanStarts;

    /// <param name="config">
    /// Validated scan settings. The planner owns no tunable constant of its own: every number it moves by
    /// is a configured value, so A9.2's ablation can turn scanning off or slow the head from one place.
    /// </param>
    public ScanPlanner(AiRuntimeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _maxHeadTurnDegrees = config.ScanMaxHeadTurnDegrees;
        _turnRateDegreesPerSecond = config.ScanTurnRateDegreesPerSecond;
        _holdTicks = SecondsToTicks(config.ScanHoldSeconds);
        _cooldownTicks = SecondsToTicks(config.ScanCooldownSeconds);
        _attentionRangeMeters = config.ScanAttentionRangeMeters;
        RefreshAgeTicks = SecondsToTicks(config.ScanRefreshAgeSeconds);
    }

    /// <summary>How stale a sighting must be before a player turns to refresh it.</summary>
    public int RefreshAgeTicks { get; }

    /// <summary>Furthest a believed opponent can be and still attract a scan.</summary>
    public float AttentionRangeMeters => _attentionRangeMeters;

    /// <summary>Completed visual fixes so far (diagnostics).</summary>
    public int ScanStarts => _scanStarts;

    /// <summary>Completed fixes aimed at a believed opponent (diagnostics).</summary>
    public int ThreatScanStarts => _threatScanStarts;

    /// <summary>Completed fixes aimed into the blind space behind the player (diagnostics).</summary>
    public int DangerScanStarts => _dangerScanStarts;

    /// <summary>
    /// Advances one player's head by one tick and returns the state perception should use. Called once per
    /// player per live tick by <see cref="PerceptionSystem"/>, before the sight filter so the gaze the
    /// returned state describes is the gaze that filters this tick.
    /// </summary>
    public ScanStatus Think(in ScanSituation situation)
    {
        HeadState head = GetOrCreate(situation.PlayerId);
        Vector2 facing = SimMath.SafeNormalize(situation.BodyFacingXZ, Vector2.UnitX);

        bool threatInRange = situation.StaleThreatKnown &&
                             situation.ThreatDistanceMeters <= _attentionRangeMeters;

        bool scanWanted;
        ScanAttentionKind targetKind;
        if (situation.IsCarrier && !situation.Receiving)
        {
            // The ball is at his feet, so the action point is in front of him; deciding what to do with it
            // is A7.3's reception plan. A carrier who turns his head away from the ball is modelling the
            // wrong player.
            scanWanted = false;
            targetKind = ScanAttentionKind.None;
        }
        else if (situation.Receiving)
        {
            scanWanted = true;
            targetKind = threatInRange ? ScanAttentionKind.Threat : ScanAttentionKind.DangerSpace;
        }
        else if (threatInRange)
        {
            scanWanted = true;
            targetKind = ScanAttentionKind.Threat;
        }
        else
        {
            scanWanted = false;
            targetKind = ScanAttentionKind.None;
        }

        // The release phase: after a fix has been held, the head goes back to the body facing before the
        // next glance. That return is what makes a scan a discrete act instead of continuous sight.
        bool releasing = scanWanted && situation.Tick < head.CooldownUntilTick && !head.IsScanning;

        float desiredYaw = 0f;
        if (!scanWanted)
        {
            head.IsScanning = false;
            head.Attention = ScanAttentionKind.None;
        }
        else if (!releasing)
        {
            // Report the attention from the moment the head starts moving, not only once it arrives: the
            // trace/inspector should say what the player is trying to look at while he turns.
            head.Attention = targetKind;
            desiredYaw = targetKind == ScanAttentionKind.Threat
                ? ClampSignedAngle(facing, situation.ThreatDirectionXZ, _maxHeadTurnDegrees)
                : head.DangerSide * _maxHeadTurnDegrees;
        }
        else
        {
            head.Attention = ScanAttentionKind.None;
        }

        float maxStep = _turnRateDegreesPerSecond * SimConstants.FixedDeltaTime;
        head.YawDegrees = MoveTowards(head.YawDegrees, desiredYaw, maxStep);

        if (head.IsScanning)
        {
            // A hold is a bounded window, not an instruction to keep looking: when it expires the player
            // is already returning to his body facing.
            if (situation.Tick >= head.HoldUntilTick)
            {
                head.IsScanning = false;
                head.Attention = ScanAttentionKind.None;
                head.CooldownUntilTick = situation.Tick + _cooldownTicks;

                // Alternate the shoulder so repeated receptions cover both sides of the blind wedge.
                if (targetKind == ScanAttentionKind.DangerSpace) head.DangerSide = -head.DangerSide;
            }
        }
        else if (scanWanted &&
                 situation.Tick >= head.CooldownUntilTick &&
                 MathF.Abs(head.YawDegrees - desiredYaw) <= ArrivalToleranceDegrees)
        {
            head.IsScanning = true;
            head.HoldUntilTick = situation.Tick + _holdTicks;
            head.Attention = targetKind;
            _scanStarts++;
            if (targetKind == ScanAttentionKind.Threat) _threatScanStarts++;
            else _dangerScanStarts++;
        }

        return new ScanStatus(head.Attention, head.YawDegrees, head.IsScanning);
    }

    /// <summary>Drops one player's head state (they left the pitch, A1.5). Counters are match totals.</summary>
    public void Forget(int playerId) => _heads.Remove(playerId);

    /// <summary>Drops every player's head state at a half-time/full-time world reset. Counters are match totals.</summary>
    public void Clear() => _heads.Clear();

    private HeadState GetOrCreate(int playerId)
    {
        if (_heads.TryGetValue(playerId, out HeadState? head)) return head;

        head = new HeadState
        {
            // Seed the shoulder from the id: deterministic, and half the squad starts on each side.
            DangerSide = (playerId & 1) == 0 ? 1 : -1
        };
        _heads[playerId] = head;
        return head;
    }

    private static int SecondsToTicks(float seconds) =>
        Math.Max(1, (int)MathF.Round(seconds / SimConstants.FixedDeltaTime));

    private static float MoveTowards(float current, float target, float maxDelta)
    {
        float delta = target - current;
        if (MathF.Abs(delta) <= maxDelta) return target;
        return current + (delta > 0f ? maxDelta : -maxDelta);
    }

    /// <summary>
    /// Signed angle from <paramref name="from"/> to <paramref name="to"/> in degrees, clamped to
    /// ±<paramref name="maxDegrees"/>. The sign is the rotation direction in the (x, z) plane, the same
    /// convention <see cref="ScanStatus.GazeFrom(in Vector2)"/> rotates by.
    /// </summary>
    private static float ClampSignedAngle(in Vector2 from, in Vector2 to, float maxDegrees)
    {
        Vector2 a = SimMath.SafeNormalize(from, Vector2.UnitX);
        Vector2 b = SimMath.SafeNormalize(to, Vector2.UnitX);
        float cross = a.X * b.Y - a.Y * b.X;
        float dot = Vector2.Dot(a, b);
        float degrees = MathF.Atan2(cross, dot) * (180f / MathF.PI);
        return Math.Clamp(degrees, -maxDegrees, maxDegrees);
    }
}
