using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;

namespace FootballSim.AI;

/// <summary>
/// One cell of a local pressure grid (A2.4/A2.5): who can get here first, by how much, and what that
/// means for the player making the decision.
///
/// A2.5 split the old single "control" number into three named parts that sum to 1. Absolute ability to
/// arrive (<see cref="MyArrivalSeconds"/>, <see cref="OpponentArrivalSeconds"/>) is kept alongside the
/// relative influence shares, because a 50/50 share between two players who are both two seconds away is
/// not the same statement as a 50/50 share between two players standing on the ball — and only the
/// arrivals can tell them apart. The third share is explicitly <see cref="UnclaimedShare"/>: influence
/// nobody has yet, which is the component a normalised pair of shares has no room for.
/// </summary>
public readonly record struct PressureCell(
    Vector2 CentreXZ,
    float MyArrivalSeconds,
    float OpponentArrivalSeconds,
    float ArrivalMarginSeconds,
    float InfluenceShare,
    float OpponentInfluenceShare,
    float UnclaimedShare,
    float Pressure01,
    bool IsBoundaryCell,
    bool IsUnclaimed)
{
    /// <summary>True when the observer's own side can be at this cell inside the model's horizon.</summary>
    public bool CanArriveMyself => MyArrivalSeconds >= 0f;

    /// <summary>True when an opponent can be at this cell inside the model's horizon.</summary>
    public bool CanOpponentArrive => OpponentArrivalSeconds >= 0f;

    /// <summary>True when either side can be at this cell inside the model's horizon.</summary>
    public bool IsReachable => CanArriveMyself || CanOpponentArrive;

    /// <summary>
    /// Share of the cell's influence the observer's own team is believed to hold. This is a *relative*
    /// statement scaled by how reachable the cell is at all; read it with the arrivals.
    /// </summary>
    public float Mine => InfluenceShare;
}

/// <summary>
/// A small, reusable local pressure field around one decision point (A2.4).
///
/// The pitch-wide version of this question ("who controls each 3 m cell?") costs a reach estimate per
/// cell per player, which is far more than a 60 Hz tick can afford for 22 players. So the field is
/// *local*: a 5×5 window of 3 m cells centred on the decision maker, built from that player's own belief
/// picture, at most <see cref="MaxOpponents"/> nearby opponents, and cached for the tick. Every cell
/// stores what it took to decide — both arrival times, the margin between them, a directional pressure
/// and a coarse control share — rather than only a final score, because A2.5 has to separate those
/// notions and cannot recover them from a single number.
///
/// The arrival kernel is the reach model's straight-line part (fatigue cap, current velocity along the
/// route, bounded acceleration) without the per-tick turn term: a turn measured over a 3 m hop is noise,
/// and 25 cells × 5 opponents has to stay inside a few microseconds.
///
/// Cells outside the pitch are marked <see cref="PressureCell.IsBoundaryCell"/> and given explicit values
/// (nobody can arrive at a cell that is out of play, so control is neutral and the flag is what a caller
/// reads) rather than being silently treated as empty space.
/// </summary>
public sealed class LocalPressureField
{
    /// <summary>Edge length of one grid cell (A2.4 asks for roughly 3 m).</summary>
    public const float CellSizeMeters = 3f;

    /// <summary>Cells to each side of the centre cell, so the window is 5×5 = 15 m across.</summary>
    public const int GridRadius = 2;

    /// <summary>Cells per side of the window.</summary>
    public const int CellsPerSide = GridRadius * 2 + 1;

    /// <summary>Total cells in the window.</summary>
    public const int CellCount = CellsPerSide * CellsPerSide;

    /// <summary>How far ahead arrival is estimated; beyond this a cell is not worth distinguishing.</summary>
    public const float HorizonSeconds = 1.5f;

    /// <summary>Opponents considered per build — the nearest ones to the window centre.</summary>
    public const int MaxOpponents = 5;

    /// <summary>
    /// Legacy margin (seconds) at which the coarse share used to reach ~0.73. Kept because A2.5's
    /// reachability weighting replaced the logistic that used it; callers and tests still reference the
    /// constant when describing the old behaviour.
    /// </summary>
    public const float ControlWindowSeconds = 0.6f;

    /// <summary>How much of a cell's pressure comes from an opponent's actual closing direction.</summary>
    public const float DirectionalWeight = 0.25f;

    /// <summary>Cells whose centre is closer than this to a line are treated as boundary cells.</summary>
    public const float BoundaryMarginMeters = 0.9f;

    private readonly PressureCell[] _cells = new PressureCell[CellCount];
    private readonly ReachKinematics[] _opponentKinematics = new ReachKinematics[MaxOpponents];
    private readonly float[] _opponentDistance = new float[MaxOpponents];

    private PitchDimensions _pitch = new();
    private ReachKinematics _self;
    private Vector2 _centreXZ;
    private int _opponentCount;
    private int _builtForTick = -1;
    private int _builtForObserver = -1;
    private float _builtCentreX;
    private float _builtCentreZ;
    private bool _hasBuild;

    /// <summary>How many times this field actually recomputed. Tests use it to prove the cache works.</summary>
    public int BuildCount { get; private set; }

    /// <summary>True once a build has happened; a read before that is a programming error.</summary>
    public bool HasBuild => _hasBuild;

    /// <summary>Tick the field was built for, or −1.</summary>
    public int BuiltForTick => _builtForTick;

    /// <summary>Centre of the window, in metres.</summary>
    public Vector2 CentreXZ => _centreXZ;

    /// <summary>
    /// Builds or reuses the window for this observation and centre point.
    ///
    /// Returns false when the previous build still describes the question (same observer, same tick,
    /// same centre), so a decision that scores twenty candidates pays for the field once. A tick of 0
    /// means "no match clock" for a standalone controller, and every call then rebuilds: caching on an
    /// unknown clock would serve a stale field to the next test without anything to invalidate it.
    /// </summary>
    public bool TryBuild(in FootballerObservation obs, Vector2 centreXZ, PitchDimensions? pitch = null)
    {
        if (pitch is not null) _pitch = pitch;

        bool cacheable = obs.Tick > 0;
        if (cacheable && _hasBuild && _builtForTick == obs.Tick && _builtForObserver == obs.SelfId &&
            MathF.Abs(_builtCentreX - centreXZ.X) < 0.01f && MathF.Abs(_builtCentreZ - centreXZ.Y) < 0.01f)
        {
            return false;
        }

        _hasBuild = true;
        BuildCount++;
        _builtForTick = obs.Tick;
        _builtForObserver = obs.SelfId;
        _builtCentreX = centreXZ.X;
        _builtCentreZ = centreXZ.Y;
        _centreXZ = centreXZ;
        _self = ReachKinematics.FromObservation(obs);

        CollectOpponents(obs, centreXZ);

        for (int z = 0; z < CellsPerSide; z++)
        {
            for (int x = 0; x < CellsPerSide; x++)
            {
                Vector2 cellCentre = CellCentre(x, z, centreXZ);
                _cells[z * CellsPerSide + x] = Evaluate(cellCentre);
            }
        }

        return true;
    }

    /// <summary>
    /// Nearest window cell to a point. <paramref name="insideWindow"/> is false when the point is outside
    /// the 15 m window, in which case the edge cell's values are returned and the caller knows why.
    /// </summary>
    public PressureCell Read(Vector2 pointXZ, out bool insideWindow)
    {
        if (!_hasBuild)
            throw new InvalidOperationException("Read before TryBuild.");

        Vector2 offset = pointXZ - _centreXZ;
        float half = GridRadius * CellSizeMeters + CellSizeMeters * 0.5f;
        insideWindow = MathF.Abs(offset.X) <= half && MathF.Abs(offset.Y) <= half;

        int x = Math.Clamp((int)MathF.Round(offset.X / CellSizeMeters) + GridRadius, 0, CellsPerSide - 1);
        int z = Math.Clamp((int)MathF.Round(offset.Y / CellSizeMeters) + GridRadius, 0, CellsPerSide - 1);
        return _cells[z * CellsPerSide + x];
    }

    /// <summary>Pressure at a point, 0..1, for the common "should I go there" question.</summary>
    public float PressureAt(Vector2 pointXZ) => Read(pointXZ, out _).Pressure01;

    /// <summary>Every cell of the window, in row-major (z, then x) order. Diagnostics and tests read it.</summary>
    public IReadOnlyList<PressureCell> Cells => _cells;

    /// <summary>Cell centre for a grid coordinate of the window.</summary>
    public static Vector2 CellCentre(int x, int z, Vector2 centreXZ) => new(
        centreXZ.X + (x - GridRadius) * CellSizeMeters,
        centreXZ.Y + (z - GridRadius) * CellSizeMeters);

    private void CollectOpponents(in FootballerObservation obs, Vector2 centreXZ)
    {
        _opponentCount = 0;

        // Insertion into a tiny fixed array: the five nearest opponents by distance to the centre. No
        // per-build collection and no LINQ in a path that runs for every decision.
        foreach (PerceivedEntity entity in obs.VisiblePlayers)
        {
            if (entity.Team == obs.SelfTeam || entity.Role == TacticalRole.Goalkeeper) continue;

            Vector2 positionXZ = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            float distance = Vector2.Distance(positionXZ, centreXZ);

            if (_opponentCount == MaxOpponents && distance >= _opponentDistance[MaxOpponents - 1]) continue;

            int slot = _opponentCount < MaxOpponents ? _opponentCount : MaxOpponents - 1;
            while (slot > 0 && _opponentDistance[slot - 1] > distance)
            {
                _opponentDistance[slot] = _opponentDistance[slot - 1];
                _opponentKinematics[slot] = _opponentKinematics[slot - 1];
                slot--;
            }

            _opponentDistance[slot] = distance;
            _opponentKinematics[slot] = ReachKinematics.FromEntity(entity);
            if (_opponentCount < MaxOpponents) _opponentCount++;
        }
    }

    private PressureCell Evaluate(Vector2 cellCentre)
    {
        if (IsOutsidePitch(cellCentre))
        {
            // Explicit: a cell that is out of play has no contested arrival, and pretending otherwise
            // would let a carrier "find space" off the pitch. Nobody holds it, so the unclaimed share
            // is the whole cell rather than a made-up 50/50.
            return new PressureCell(
                cellCentre, MyArrivalSeconds: -1f, OpponentArrivalSeconds: -1f, ArrivalMarginSeconds: 0f,
                InfluenceShare: 0f, OpponentInfluenceShare: 0f, UnclaimedShare: 1f,
                Pressure01: 0f, IsBoundaryCell: true, IsUnclaimed: true);
        }

        float myArrival = EstimateArrivalSeconds(_self, cellCentre);

        float opponentArrival = -1f;
        float pressure = 0f;
        for (int i = 0; i < _opponentCount; i++)
        {
            float arrival = EstimateArrivalSeconds(_opponentKinematics[i], cellCentre);
            if (arrival < 0f) continue;

            if (opponentArrival < 0f || arrival < opponentArrival)
                opponentArrival = arrival;

            float contribution = PressureFrom(_opponentKinematics[i], cellCentre, arrival);
            if (contribution > pressure) pressure = contribution;
        }

        bool iCan = myArrival >= 0f;
        bool theyCan = opponentArrival >= 0f;

        float margin;
        if (iCan && theyCan) margin = opponentArrival - myArrival;
        else if (iCan) margin = HorizonSeconds - myArrival;
        else if (theyCan) margin = -(HorizonSeconds - opponentArrival);
        else margin = 0f;

        // A2.5: reachability weights, so a late arrival claims only part of the cell and the rest stays
        // unclaimed. Absolute arrival times are still reported above; these shares never replace them.
        (float influence, float opponentInfluence, float unclaimed) = SplitInfluence(myArrival, opponentArrival);

        return new PressureCell(
            cellCentre,
            MyArrivalSeconds: myArrival,
            OpponentArrivalSeconds: opponentArrival,
            ArrivalMarginSeconds: margin,
            InfluenceShare: influence,
            OpponentInfluenceShare: opponentInfluence,
            UnclaimedShare: unclaimed,
            Pressure01: Math.Clamp(pressure, 0f, 1f),
            IsBoundaryCell: false,
            IsUnclaimed: !iCan && !theyCan);
    }

    /// <summary>
    /// Splits one cell into the three shares A2.5 asks for. The weights are reachability, not a
    /// probability: a side that needs 1.2 s of a 1.5 s horizon claims a fifth of the cell and the rest
    /// stays unclaimed. The three outputs always sum to 1.
    /// </summary>
    public static (float Mine, float Theirs, float Unclaimed) SplitInfluence(
        float myArrivalSeconds,
        float opponentArrivalSeconds,
        float horizonSeconds = HorizonSeconds)
    {
        bool iCan = myArrivalSeconds >= 0f;
        bool theyCan = opponentArrivalSeconds >= 0f;

        float myWeight = iCan ? 1f - Math.Clamp(myArrivalSeconds / horizonSeconds, 0f, 1f) : 0f;
        float opponentWeight = theyCan ? 1f - Math.Clamp(opponentArrivalSeconds / horizonSeconds, 0f, 1f) : 0f;
        float claimed = myWeight + opponentWeight;
        float unclaimed = Math.Clamp(1f - claimed, 0f, 1f);

        if (claimed <= 1e-4f)
            return (0f, 0f, 1f);

        float myFraction = myWeight / claimed;
        float mine = Math.Clamp(myFraction * (1f - unclaimed), 0f, 1f);
        float theirs = Math.Clamp((1f - myFraction) * (1f - unclaimed), 0f, 1f);
        return (mine, theirs, unclaimed);
    }

    /// <summary>
    /// How much this opponent threatens a cell: how soon they can be there, weighted by whether they are
    /// actually running at it. A defender standing still beside a point is a smaller threat than one
    /// closing on it, which is the "directional" part of local pressure.
    /// </summary>
    private static float PressureFrom(in ReachKinematics opponent, Vector2 cellCentre, float arrival)
    {
        float timeFactor = 1f - Math.Clamp(arrival / HorizonSeconds, 0f, 1f);

        Vector2 toCell = cellCentre - opponent.PositionXZ;
        float distance = toCell.Length();
        float closing = 1f;
        if (distance > 0.25f)
        {
            Vector2 direction = toCell / distance;
            Vector2 velocity = opponent.VelocityXZ;
            float speed = velocity.Length();
            if (speed > 0.5f)
            {
                float approach = Vector2.Dot(direction, velocity / speed);
                closing = 1f - DirectionalWeight * (1f - Math.Clamp(approach, 0f, 1f));
            }
        }

        return timeFactor * closing;
    }

    /// <summary>
    /// Straight-line arrival time for this body, in seconds, or −1 when it is beyond the horizon.
    /// Public because A2.5 and the tests need to check the kernel itself, not only its cells.
    /// </summary>
    public static float EstimateArrivalSeconds(in ReachKinematics state, Vector2 targetXZ, float horizon = HorizonSeconds)
    {
        if (!SimMath.IsFinite(state.PositionXZ) || !SimMath.IsFinite(targetXZ)) return -1f;

        Vector2 offset = targetXZ - state.PositionXZ;
        float distance = offset.Length();
        float reach = MathF.Max(0.05f, state.ReachRadius);
        if (distance <= reach) return 0f;

        // Arrival is a contact, exactly as in the reach model: the player only has to get inside reach,
        // not stand on the point.
        distance -= reach;

        float cap = TimeToReachModel.SpeedCap(state, sprint: true);
        if (cap <= 0.1f) return -1f;

        float balance = float.IsFinite(state.Balance01) ? Math.Clamp(state.Balance01, 0.2f, 1f) : 1f;
        float acceleration = MathF.Max(1f, (float.IsFinite(state.Acceleration) ? state.Acceleration : 6.8f) * 1.15f * balance);

        float along = distance > 1e-4f ? Vector2.Dot(state.VelocityXZ, offset / distance) : 0f;
        float seconds;

        if (along >= cap)
        {
            // Already faster than the sustainable cap: no acceleration to model.
            seconds = distance / along;
        }
        else
        {
            float rampSeconds = (cap - along) / acceleration;
            float rampDistance = (cap * cap - along * along) / (2f * acceleration);
            seconds = distance <= rampDistance
                ? (-along + MathF.Sqrt(along * along + 2f * acceleration * distance)) / acceleration
                : rampSeconds + (distance - rampDistance) / cap;
        }

        return seconds >= 0f && seconds <= horizon ? seconds : -1f;
    }

    private bool IsOutsidePitch(Vector2 pointXZ) =>
        MathF.Abs(pointXZ.X) > _pitch.HalfLength - BoundaryMarginMeters ||
        MathF.Abs(pointXZ.Y) > _pitch.HalfWidth - BoundaryMarginMeters;
}

/// <summary>
/// Pitch-wide, coarse version of the pressure question for overlays and validation (A2.6).
///
/// This is a *diagnostic* map, not a decision input: it sweeps every 3 m cell of the whole pitch against
/// every player, which is far too expensive for a 60 Hz tick (and would be a second spatial model if a
/// decision ever read it). A decision uses <see cref="LocalPressureField"/>; this exists so a human — or a
/// test — can see the same quantities over the pitch, labelled by whether they came from a team's belief
/// or from the truth.
///
/// Every cell publishes the same three normalised shares as a local cell (mine / theirs / unclaimed), the
/// arrival time of the best opponent, and how old the information behind it is. The own team is read from
/// live positions: teammates are fully observed by perception itself (A2.2), so that is the same knowledge
/// the player would have.
/// </summary>
public sealed class SpatialOverlayMap
{
    /// <summary>Edge length of one mapped cell.</summary>
    public const float CellSizeMeters = 3f;

    /// <summary>Horizon for the mapped arrivals; matches the local field so the colours mean the same.</summary>
    public const float HorizonSeconds = LocalPressureField.HorizonSeconds;

    private readonly float[] _mine;
    private readonly float[] _theirs;
    private readonly float[] _unclaimed;
    private readonly float[] _pressure;
    private readonly float[] _opponentArrival;
    private readonly float[] _perceptionAge;

    public SpatialOverlayMap(PitchDimensions pitch)
    {
        ArgumentNullException.ThrowIfNull(pitch);
        Pitch = pitch;
        Columns = (int)MathF.Ceiling(pitch.Length / CellSizeMeters) + 1;
        Rows = (int)MathF.Ceiling(pitch.Width / CellSizeMeters) + 1;

        int cellCount = Columns * Rows;
        _mine = new float[cellCount];
        _theirs = new float[cellCount];
        _unclaimed = new float[cellCount];
        _pressure = new float[cellCount];
        _opponentArrival = new float[cellCount];
        _perceptionAge = new float[cellCount];
    }

    public PitchDimensions Pitch { get; }
    public int Columns { get; }
    public int Rows { get; }
    public int CellCount => Columns * Rows;

    /// <summary>Observer's team share per cell (row-major, z then x).</summary>
    public IReadOnlyList<float> InfluenceShare => _mine;

    /// <summary>Opponents' share per cell.</summary>
    public IReadOnlyList<float> OpponentInfluenceShare => _theirs;

    /// <summary>Share of each cell that neither side can claim inside the horizon.</summary>
    public IReadOnlyList<float> UnclaimedShare => _unclaimed;

    /// <summary>Pressure from the best opponent arrival per cell, 0..1.</summary>
    public IReadOnlyList<float> Pressure01 => _pressure;

    /// <summary>Best opponent arrival per cell in seconds, or −1 when none can arrive.</summary>
    public IReadOnlyList<float> OpponentArrivalSeconds => _opponentArrival;

    /// <summary>Age of the information behind each cell's opponent read, in seconds.</summary>
    public IReadOnlyList<float> PerceptionAgeSeconds => _perceptionAge;

    /// <summary>Centre of a mapped cell.</summary>
    public Vector2 CellCentre(int column, int row) => new(
        -Pitch.HalfLength + column * CellSizeMeters,
        -Pitch.HalfWidth + row * CellSizeMeters);

    /// <summary>
    /// Fills the map for one team: its own live players against the opponents it believes in. A truth
    /// belief produces the validation map; a rebuilt belief produces the tactical one.
    /// </summary>
    public void Build(IReadOnlyList<FootballerState> players, TeamId team, TeamBelief opponents)
    {
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(opponents);

        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns; column++)
            {
                int index = row * Columns + column;
                Vector2 centre = CellCentre(column, row);

                float myArrival = -1f;
                foreach (FootballerState player in players)
                {
                    if (player.Team != team || !player.IsActiveOnPitch) continue;
                    float arrival = LocalPressureField.EstimateArrivalSeconds(
                        ReachKinematics.From(player), centre, HorizonSeconds);
                    if (arrival < 0f) continue;
                    if (myArrival < 0f || arrival < myArrival) myArrival = arrival;
                }

                float opponentArrival = -1f;
                float pressure = 0f;
                float age = 0f;
                for (int i = 0; i < opponents.Opponents.Count; i++)
                {
                    TeamBelief.Opponent opponent = opponents.Opponents[i];
                    var kinematics = new ReachKinematics(
                        opponent.PositionXZ, opponent.VelocityXZ, SimMath.SafeNormalize(opponent.VelocityXZ, new Vector2(1f, 0f)),
                        CapabilityEstimate.Generic.MaxSprintSpeed,
                        CapabilityEstimate.Generic.Acceleration,
                        CapabilityEstimate.Generic.AgilityTurnRate,
                        Balance01: 1f,
                        Stamina01: 1f,
                        ReachRadius: 0.75f * 1.65f);

                    float arrival = LocalPressureField.EstimateArrivalSeconds(kinematics, centre, HorizonSeconds);
                    if (arrival < 0f) continue;

                    if (opponentArrival < 0f || arrival < opponentArrival)
                    {
                        opponentArrival = arrival;
                        age = opponent.AgeTicks * SimConstants.FixedDeltaTime;
                    }

                    float timeFactor = 1f - Math.Clamp(arrival / HorizonSeconds, 0f, 1f);
                    if (timeFactor > pressure) pressure = timeFactor;
                }

                (float mine, float theirs, float unclaimed) =
                    LocalPressureField.SplitInfluence(myArrival, opponentArrival);

                _mine[index] = mine;
                _theirs[index] = theirs;
                _unclaimed[index] = unclaimed;
                _pressure[index] = Math.Clamp(pressure, 0f, 1f);
                _opponentArrival[index] = opponentArrival;
                _perceptionAge[index] = age;
            }
        }
    }
}
