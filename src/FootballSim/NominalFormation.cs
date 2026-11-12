using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;

namespace FootballSim.Tactics;

/// <summary>
/// One player's owned place in his team's nominal formation (A3.3). Coordinates are normalised in the
/// team's attacking frame: <c>NormX</c> +1 is the opponent's goal line and −1 the team's own;
/// <c>NormZ</c> is the touchline on the player's side. This is the *slot*, not the player: the shape
/// may move around it (and the ball may drag it a few metres) without the slot itself changing.
/// </summary>
public readonly record struct NominalSlot(TacticalRole Role, float NormX, float NormZ);

/// <summary>
/// Per-team nominal formation ownership (A3.3): which place in the shape each player owns.
///
/// Ownership is assigned once — from the pre-match shirt template for a generated squad, from the
/// fixture's own layout for a scenario fragment — and then held, keyed by player id. Anchor writing
/// reads this instead of a shirt table, which is what stops a player's *role* from being derived from
/// a number every tick, and what lets an unbalanced fixture (3v2, 4v3) own its own shape instead of
/// inheriting the full-size slot table.
///
/// Reassignment is an explicit act, never a per-tick one (A3.3): <see cref="Reassign"/> moves one
/// player's slot and is only called for a persistent mismatch (<see cref="ReviewMismatch"/>) or a
/// future explicit event such as a guaranteed handover (A3.5). Counters expose both, so a test can
/// prove that running the shape for a while does not rewrite the formation.
/// </summary>
public sealed class TeamFormation
{
    /// <summary>
    /// The horizontal compression the anchor model applies to a normalized slot. One constant, used
    /// by the slot → world mapping and by the anchor writer, so a slot and the anchor it produces
    /// cannot drift apart.
    /// </summary>
    public const float NormXToWorldScale = 0.78f;

    /// <summary>How far from his slot a player must be to count as displaced, in metres.</summary>
    public const float MismatchDistanceMeters = 20f;

    /// <summary>How long he must stay there, stationary and on his own duty, before the slot moves.</summary>
    public const float MismatchSeconds = 20f;

    /// <summary>Fraction of the gap closed per reassignment; reassignment is not a teleport.</summary>
    public const float ReassignStep = 0.5f;

    /// <summary>Speed above which a displaced player is travelling, not settled somewhere else.</summary>
    private const float SettledSpeed = 2.0f;

    private readonly PitchDimensions _pitch;
    private readonly Dictionary<int, NominalSlot> _slots = new();
    private readonly Dictionary<int, float> _mismatchSeconds = new();
    private readonly Dictionary<int, int> _lastMismatchTick = new();

    public TeamFormation(TeamId team, PitchDimensions pitch)
    {
        Team = team;
        _pitch = pitch;
    }

    public TeamId Team { get; }

    /// <summary>How many players currently own a slot.</summary>
    public int Count => _slots.Count;

    /// <summary>Lifetime first assignments (a player is assigned once, not once per tick).</summary>
    public int Assignments { get; private set; }

    /// <summary>Lifetime deliberate reassignments of an owned slot.</summary>
    public int Reassignments { get; private set; }

    public bool TryGet(int playerId, out NominalSlot slot) => _slots.TryGetValue(playerId, out slot);

    /// <summary>Where <paramref name="playerId"/>'s slot lies in the world, or false when he owns none.</summary>
    public bool TryGetWorldAnchor(int playerId, float attackDirX, out Vector3 anchor)
    {
        if (!_slots.TryGetValue(playerId, out NominalSlot slot))
        {
            anchor = default;
            return false;
        }

        anchor = ToWorld(slot, attackDirX);
        return true;
    }

    public Vector3 ToWorld(in NominalSlot slot, float attackDirX) => new(
        attackDirX * slot.NormX * _pitch.HalfLength * NormXToWorldScale,
        0f,
        slot.NormZ * _pitch.HalfWidth);

    /// <summary>
    /// The pre-match template for a generated squad: the shirt table, read **once**, at setup. After
    /// this the table is no longer consulted for the player — his role and slot are owned per id, so
    /// nothing downstream can re-derive them from a shirt number.
    ///
    /// Goalkeepers are deliberately left out: a keeper's position is his own line (the keeper branch of
    /// the anchor owns it), he is never a vacated-gap source or a cover, and clamping his spineline to
    /// the outfield frame would only record a place he does not own.
    /// </summary>
    public void AssignFromShirts(IReadOnlyList<FootballerState> players, int teamSize)
    {
        Clear();
        foreach (FootballerState player in players)
        {
            if (player.Team != Team || !player.IsActiveOnPitch) continue;
            if (player.Role == TacticalRole.Goalkeeper) continue;

            var (_, normX, normZ) = TeamTacticalCoordinator.GetFormationRoleAndSlot(player.ShirtNumber, teamSize);
            Assign(player.Id, new NominalSlot(player.Role, normX, normZ));
        }
    }

    /// <summary>
    /// The explicit template for a scenario fragment: the fixture's own layout, normalised into the
    /// team's attacking frame. A fragment describes where its players stand, so that is the formation
    /// it owns — which also means an unbalanced side gets a shape of its own size rather than shirts
    /// 1..N of the eleven-a-side table. Because the normalisation inverts the anchor model's
    /// compression, a player's slot maps back to exactly where the fixture placed him.
    ///
    /// Goalkeepers are left out for the same reason as above: the keeper branch of the anchor owns the
    /// keeper's position, and a slot within the outfield frame cannot describe a player on his line.
    /// </summary>
    public void AssignFromPositions(IReadOnlyList<FootballerState> players, float attackDirX)
    {
        Clear();
        foreach (FootballerState player in players)
        {
            if (player.Team != Team || !player.IsActiveOnPitch) continue;
            if (player.Role == TacticalRole.Goalkeeper) continue;

            float progress = player.Position.X * attackDirX;
            float normX = Math.Clamp(progress / (_pitch.HalfLength * NormXToWorldScale), -1f, 1f);
            float normZ = Math.Clamp(player.Position.Z / _pitch.HalfWidth, -1f, 1f);
            Assign(player.Id, new NominalSlot(player.Role, normX, normZ));
        }
    }

    /// <summary>First assignment of a slot. Assigning the same player again is a reassignment.</summary>
    public void Assign(int playerId, NominalSlot slot)
    {
        if (_slots.ContainsKey(playerId))
        {
            Reassign(playerId, slot);
            return;
        }

        _slots[playerId] = slot;
        Assignments++;
    }

    /// <summary>
    /// Moves an owned slot deliberately (A3.3's slow structural reassignment). This is the only path
    /// that changes a slot after setup; it is counted so a test can prove normality never calls it.
    /// </summary>
    public bool Reassign(int playerId, NominalSlot slot)
    {
        if (!_slots.TryGetValue(playerId, out NominalSlot previous)) return false;
        if (previous == slot) return false;

        _slots[playerId] = slot;
        Reassignments++;
        return true;
    }

    /// <summary>Drops a player's ownership (a player who left the pitch cannot hold a place).</summary>
    public void Remove(int playerId)
    {
        _slots.Remove(playerId);
        _mismatchSeconds.Remove(playerId);
        _lastMismatchTick.Remove(playerId);
    }

    public void Clear()
    {
        _slots.Clear();
        _mismatchSeconds.Clear();
        _lastMismatchTick.Clear();
    }

    /// <summary>
    /// A3.3's "persistent mismatch" path. A player whose duty *is* his own nominal shape, but who has
    /// been standing still far from the slot he owns for <see cref="MismatchSeconds"/>, has in effect
    /// been playing somewhere else: the slot is nudged halfway toward where he actually plays. The
    /// call deliberately takes <paramref name="onOwnDuty"/> so a player pulled away by a contextual
    /// job (width, marker, press) is never mistaken for a broken formation — his duty is elsewhere,
    /// his ownership is not wrong.
    /// </summary>
    /// <returns>True when a slot moved on this call.</returns>
    public bool ReviewMismatch(FootballerState player, float attackDirX, int tick, bool onOwnDuty)
    {
        if (!_slots.TryGetValue(player.Id, out NominalSlot slot)) return false;

        int lastTick = _lastMismatchTick.TryGetValue(player.Id, out int recorded) ? recorded : tick;
        _lastMismatchTick[player.Id] = tick;
        float elapsed = Math.Clamp((tick - lastTick) * SimConstants.FixedDeltaTime, 0f, 1f);

        Vector3 anchor = ToWorld(slot, attackDirX);
        float distance = PlanarDistance(player.Position, anchor);
        float speed = PlanarSpeed(player.Velocity);

        bool mismatched = onOwnDuty && distance >= MismatchDistanceMeters && speed <= SettledSpeed;
        float accumulated = mismatched ? _mismatchSeconds.GetValueOrDefault(player.Id) + elapsed : 0f;
        _mismatchSeconds[player.Id] = accumulated;
        if (accumulated < MismatchSeconds) return false;

        // Nudge the slot part of the way toward where he actually plays. Repeated only after another
        // full window, and never by more than the step, so the formation drifts instead of jumping.
        float progress = player.Position.X * attackDirX;
        float targetNormX = Math.Clamp(progress / (_pitch.HalfLength * NormXToWorldScale), -1f, 1f);
        float targetNormZ = Math.Clamp(player.Position.Z / _pitch.HalfWidth, -1f, 1f);
        var moved = new NominalSlot(
            slot.Role,
            slot.NormX + (targetNormX - slot.NormX) * ReassignStep,
            slot.NormZ + (targetNormZ - slot.NormZ) * ReassignStep);

        _mismatchSeconds[player.Id] = 0f;
        return Reassign(player.Id, moved);
    }

    private static float PlanarDistance(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static float PlanarSpeed(Vector3 velocity)
    {
        float x = velocity.X;
        float z = velocity.Z;
        return MathF.Sqrt(x * x + z * z);
    }
}
