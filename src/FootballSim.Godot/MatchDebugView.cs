using System;
using System.Collections.Generic;
using Godot;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Tactics;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

namespace FootballSim.GodotClient;

/// <summary>
/// The on-pitch AI debug overlay: what debug mode shows instead of the old wall of HUD text.
///
/// Every layer is a fact the AI already produces — no decision is recomputed here and nothing read here
/// feeds back into a decision, so the simulation is untouched (the overlay may be built only after the
/// tick has been stepped). The layers, bottom to top:
///
/// <list type="bullet">
/// <item><b>Control map</b> (A2.4–A2.6): the whole-pitch <see cref="SpatialOverlayMap"/> of the watched
/// team's belief, cyan where it has the stronger arrival and red where the opponent does.</item>
/// <item><b>Defensive-line band</b> (A6.3): the shared depth strip and its allowed range, plus a ring on a
/// credible runner in behind.</item>
/// <item><b>Coordination</b>: advertised runs (A5.3), combination return regions (A8.1), short traps
/// (A8.4), the live restart routine (A8.5), coverage requests (A3.5) and heard team calls (A7.2).</item>
/// <item><b>Jobs</b> (A3.3/A3.4): the allocator's anchor per player, coloured by duty, with a dashed line
/// to the player and marking links for marker jobs.</item>
/// <item><b>Belief vs truth</b> (A2.2/A2.6): amber ghost rings are where the watched team believes its
/// opponents are; a dashed link joins a stale ghost to the real body.</item>
/// <item><b>Perception</b> (A2.2/A7.1): the active player's gaze wedge and the opponents in his current
/// picture (green = seen this tick, amber = memory).</item>
/// <item><b>Ball and responders</b> (A6.1): the elected responder, the carrier's feet, the intended
/// receiver of a pass in flight and the ball itself.</item>
/// <item><b>Selected action</b> (A4.6): an arrow from the active AI player to the target of his last
/// chosen candidate.</item>
/// </list>
///
/// F4 (<see cref="ToggleWatchedTeam"/>) swaps which team's heat map/belief/perception the overlay shows;
/// team-coded geometry is cyan for the watched team and red for the other one in both views.
/// </summary>
public partial class MatchDebugView : Node3D
{
    /// <summary>How often the whole-pitch control map is rebuilt, in engine ticks (0.1 s).</summary>
    private const int HeatMapRefreshTicks = 6;

    /// <summary>Sight cone used by perception (A2.2): ±100° around the gaze direction.</summary>
    private const float SightHalfAngleRadians = 1.745f;

    private static readonly Color WatchedColour = new(0.13f, 0.83f, 0.93f);
    private static readonly Color OpponentColour = new(0.97f, 0.44f, 0.44f);
    private static readonly Color BeliefColour = new(0.99f, 0.77f, 0.30f);
    private static readonly Color SeenColour = new(0.36f, 0.95f, 0.58f);
    private static readonly Color MemoryColour = new(0.99f, 0.78f, 0.30f);
    private static readonly Color CoordinationColour = new(0.76f, 0.55f, 0.99f);
    private static readonly Color GoldColour = new(1.00f, 0.84f, 0.32f);
    private static readonly Color ActionColour = new(0.99f, 0.45f, 0.73f);

    private readonly Dictionary<int, FootballerState> _byId = new();
    private readonly List<FootballerState> _activePlayers = new();

    private DebugMeshPainter _painter = null!;
    private SpatialOverlayMap? _heatMap;
    private Color[] _heatColours = Array.Empty<Color>();
    private int _heatBuiltTick = -1;
    private TeamId _watchedTeam = TeamId.Home;
    private bool _enabled;

    public bool Enabled => _enabled;

    public TeamId WatchedTeam => _watchedTeam;

    public override void _Ready()
    {
        _painter = new DebugMeshPainter(this);
        _painter.Visible = _enabled;
    }

    /// <summary>Shows or hides the whole overlay. Hidden means no mesh is rebuilt at all.</summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (_painter is not null) _painter.Visible = enabled;
        if (enabled) _heatBuiltTick = -1;
    }

    /// <summary>F4: switch which team's map/belief/perception is drawn.</summary>
    public void ToggleWatchedTeam()
    {
        _watchedTeam = _watchedTeam == TeamId.Home ? TeamId.Away : TeamId.Home;
        _heatBuiltTick = -1;
    }

    /// <summary>
    /// Rebuilds the overlay for this rendered frame. Called after the simulation step, so everything read
    /// is the state of the tick that is about to be drawn. Read-only with respect to the match.
    /// </summary>
    public void Refresh(MatchSimulationEngine engine, FootballerState activePlayer)
    {
        if (!_enabled || _painter is null) return;

        BuildPlayerLookup(engine);

        _painter.Begin();
        try
        {
            DrawControlMap(engine);
            DrawDefensiveLineBands(engine);
            DrawCoverageRequests(engine);
            DrawRuns(engine);
            DrawCombinations(engine);
            DrawTraps(engine);
            DrawRestartRoutine(engine);
            DrawHeardCalls(engine);
            DrawJobs(engine);
            DrawBelief(engine);
            DrawPerception(engine, activePlayer);
            DrawBallState(engine);
            DrawResponders(engine);
            DrawSelectedAction(engine, activePlayer);
        }
        finally
        {
            _painter.End();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // A2.4–A2.6: whole-pitch control map
    // ---------------------------------------------------------------------------------------------

    private void DrawControlMap(MatchSimulationEngine engine)
    {
        if (_heatMap is null || !ReferenceEquals(_heatMap.Pitch, engine.Pitch))
        {
            _heatMap = new SpatialOverlayMap(engine.Pitch);
            _heatColours = new Color[_heatMap.CellCount];
            _heatBuiltTick = -1;
        }

        if (_heatBuiltTick < 0 || engine.CurrentTick - _heatBuiltTick >= HeatMapRefreshTicks)
        {
            _heatMap.Build(engine.Players, _watchedTeam, engine.TeamBeliefOf(_watchedTeam));
            _heatBuiltTick = engine.CurrentTick;

            for (int index = 0; index < _heatColours.Length; index++)
            {
                float mine = _heatMap.InfluenceShare[index];
                float theirs = _heatMap.OpponentInfluenceShare[index];
                float total = Mathf.Clamp(mine + theirs, 0f, 1f);
                if (total <= 0.02f)
                {
                    _heatColours[index] = new Color(0f, 0f, 0f, 0f);
                    continue;
                }

                float dominance = (mine - theirs) / Mathf.Max(total, 1e-4f);
                float watched = (dominance + 1f) * 0.5f;
                Color tint = new(
                    Mathf.Lerp(OpponentColour.R, WatchedColour.R, watched),
                    Mathf.Lerp(OpponentColour.G, WatchedColour.G, watched),
                    Mathf.Lerp(OpponentColour.B, WatchedColour.B, watched));
                _heatColours[index] = new Color(tint.R, tint.G, tint.B, 0.02f + 0.26f * total);
            }
        }

        const float y = 0.035f;
        const float half = SpatialOverlayMap.CellSizeMeters * 0.5f - 0.15f;
        for (int row = 0; row < _heatMap.Rows; row++)
        {
            for (int column = 0; column < _heatMap.Columns; column++)
            {
                Color colour = _heatColours[row * _heatMap.Columns + column];
                if (colour.A <= 0.001f) continue;

                NVector2 centre = _heatMap.CellCentre(column, row);
                var topLeft = new Vector3(centre.X - half, y, centre.Y - half);
                var topRight = new Vector3(centre.X + half, y, centre.Y - half);
                var bottomRight = new Vector3(centre.X + half, y, centre.Y + half);
                var bottomLeft = new Vector3(centre.X - half, y, centre.Y + half);
                _painter.Quad(topLeft, topRight, bottomRight, bottomLeft, colour);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // A6.3: defensive-line band
    // ---------------------------------------------------------------------------------------------

    private void DrawDefensiveLineBands(MatchSimulationEngine engine)
    {
        float homeDir = engine.Rules.GetHomeAttackDirectionX();
        float halfWidth = engine.Pitch.HalfWidth;

        DrawLineBand(engine, TeamId.Home, homeDir, halfWidth);
        DrawLineBand(engine, TeamId.Away, -homeDir, halfWidth);
    }

    private void DrawLineBand(MatchSimulationEngine engine, TeamId team, float attackDirX, float halfWidth)
    {
        DefensiveLineBand band = engine.TeamJobs.LineBandOf(team);
        if (!band.IsValid) return;

        Color colour = TeamColour(team);
        float x1 = band.MinProjX * attackDirX;
        float x2 = band.MaxProjX * attackDirX;
        float lineX = band.ProjX * attackDirX;
        float minX = MathF.Min(x1, x2);
        float maxX = MathF.Max(x1, x2);

        // The allowed range is a faint strip; the band the line is standing in is the bright hairline.
        const float y = 0.045f;
        _painter.Quad(
            new Vector3(minX, y, -halfWidth), new Vector3(maxX, y, -halfWidth),
            new Vector3(maxX, y, halfWidth), new Vector3(minX, y, halfWidth),
            WithAlpha(colour, 0.045f));

        _painter.Line(new Vector3(minX, y, -halfWidth), new Vector3(minX, y, halfWidth), 0.035f,
            WithAlpha(colour, 0.20f));
        _painter.Line(new Vector3(maxX, y, -halfWidth), new Vector3(maxX, y, halfWidth), 0.035f,
            WithAlpha(colour, 0.20f));
        _painter.Line(new Vector3(lineX, 0.075f, -halfWidth), new Vector3(lineX, 0.075f, halfWidth), 0.11f,
            WithAlpha(colour, 0.65f));

        if (band.HasCredibleRunner && _byId.TryGetValue(band.DeepestRunnerId, out FootballerState? runner) &&
            runner.IsActiveOnPitch)
        {
            _painter.Ring(Ground(runner.Position, 0.085f), 0.55f, 0.07f, WithAlpha(BeliefColour, 0.55f), 20);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // A3.5: coverage request
    // ---------------------------------------------------------------------------------------------

    private void DrawCoverageRequests(MatchSimulationEngine engine)
    {
        DrawCoverageRequest(engine, TeamId.Home);
        DrawCoverageRequest(engine, TeamId.Away);
    }

    private void DrawCoverageRequest(MatchSimulationEngine engine, TeamId team)
    {
        CoverageRequest request = engine.TeamJobs.CoverageOf(team);
        if (!request.IsLive) return;

        Color colour = new(0.92f, 0.47f, 0.98f);
        Vector3 region = Ground(request.Region, 0.085f);
        _painter.Ring(region, 0.85f, 0.06f, WithAlpha(colour, 0.55f), 22);

        if (_byId.TryGetValue(request.OwnerPlayerId, out FootballerState? owner) && owner.IsActiveOnPitch)
            _painter.DashedLine(Ground(owner.Position, 0.085f), region, 0.045f, WithAlpha(colour, 0.35f),
                0.55f, 0.45f);
    }

    // ---------------------------------------------------------------------------------------------
    // A5.3: advertised runs
    // ---------------------------------------------------------------------------------------------

    private void DrawRuns(MatchSimulationEngine engine)
    {
        DrawRunsForTeam(engine, TeamId.Home);
        DrawRunsForTeam(engine, TeamId.Away);
    }

    private void DrawRunsForTeam(MatchSimulationEngine engine, TeamId team)
    {
        int count = engine.Communication.Runs.VisibleCount(team, engine.CurrentTick);
        for (int index = 0; index < count; index++)
        {
            if (!engine.Communication.Runs.TryGetVisibleAt(team, index, engine.CurrentTick,
                    out RunAdvertisement advertisement)) continue;
            if (!_byId.TryGetValue(advertisement.PlayerId, out FootballerState? runner) || !runner.IsActiveOnPitch)
                continue;

            float promise = advertisement.Promise01(engine.CurrentTick);
            Color colour = WithAlpha(TeamColour(team), 0.16f + 0.40f * promise);
            Vector3 meet = Ground(advertisement.MeetXZ, 0.085f);
            _painter.DashedLine(Ground(runner.Position, 0.085f), meet, 0.045f, colour, 0.65f, 0.45f);
            _painter.Ring(meet, advertisement.MeetRadiusMeters, 0.05f, colour, 22);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // A8.1: combination return region
    // ---------------------------------------------------------------------------------------------

    private void DrawCombinations(MatchSimulationEngine engine)
    {
        DrawCombinationsForTeam(engine, TeamId.Home);
        DrawCombinationsForTeam(engine, TeamId.Away);
    }

    private void DrawCombinationsForTeam(MatchSimulationEngine engine, TeamId team)
    {
        int count = engine.Communication.Combinations.LiveCount(team, engine.CurrentTick);
        for (int index = 0; index < count; index++)
        {
            if (!engine.Communication.Combinations.TryGetLiveAt(team, index, engine.CurrentTick,
                    out CombinationContract contract)) continue;
            if (!_byId.TryGetValue(contract.ReceiverId, out FootballerState? receiver) || !receiver.IsActiveOnPitch)
                continue;

            Color colour = WithAlpha(CoordinationColour, 0.45f);
            Vector3 region = Ground(contract.ReturnXZ, 0.085f);
            _painter.DashedLine(Ground(receiver.Position, 0.085f), region, 0.045f, colour, 0.6f, 0.45f);
            _painter.Ring(region, MathF.Max(0.35f, contract.ReturnRadiusMeters), 0.055f, colour, 24);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // A8.4: short trap
    // ---------------------------------------------------------------------------------------------

    private void DrawTraps(MatchSimulationEngine engine)
    {
        DrawTrapForTeam(engine, TeamId.Home);
        DrawTrapForTeam(engine, TeamId.Away);
    }

    private void DrawTrapForTeam(MatchSimulationEngine engine, TeamId team)
    {
        if (!engine.Communication.Traps.TryGetLive(team, engine.CurrentTick, out TrapContract trap)) return;

        Color press = new(0.99f, 0.57f, 0.23f);
        Color screen = new(0.99f, 0.82f, 0.30f);
        Vector3 carrier = Ground(trap.CarrierXZ, 0.09f);
        Vector3 pressPoint = Ground(trap.CarrierXZ + trap.PressOffsetXZ, 0.09f);
        Vector3 screenPoint = Ground(trap.CarrierXZ + trap.ScreenOffsetXZ, 0.09f);

        _painter.Ring(carrier, 0.95f, 0.07f, WithAlpha(press, 0.75f), 26);
        _painter.Ring(pressPoint, 0.38f, 0.05f, WithAlpha(press, 0.55f), 16);
        _painter.Ring(screenPoint, 0.38f, 0.05f, WithAlpha(screen, 0.65f), 16);

        if (_byId.TryGetValue(trap.PresserId, out FootballerState? presser) && presser.IsActiveOnPitch)
            _painter.DashedLine(Ground(presser.Position, 0.09f), pressPoint, 0.045f, WithAlpha(press, 0.35f),
                0.55f, 0.45f);
        if (_byId.TryGetValue(trap.ScreenId, out FootballerState? screener) && screener.IsActiveOnPitch)
            _painter.DashedLine(Ground(screener.Position, 0.09f), screenPoint, 0.045f, WithAlpha(screen, 0.35f),
                0.55f, 0.45f);
        if (_byId.TryGetValue(trap.CoverId, out FootballerState? cover) && cover.IsActiveOnPitch)
            _painter.DashedLine(Ground(cover.Position, 0.09f), carrier, 0.045f, WithAlpha(CoordinationColour, 0.30f),
                0.55f, 0.45f);
    }

    // ---------------------------------------------------------------------------------------------
    // A8.5: restart routine
    // ---------------------------------------------------------------------------------------------

    private void DrawRestartRoutine(MatchSimulationEngine engine)
    {
        if (!engine.Communication.Restarts.TryGetLive(engine.CurrentTick, out RestartRoutine routine)) return;

        Color colour = WithAlpha(GoldColour, 0.60f);
        Vector3 spot = Ground(routine.BallXZ, 0.085f);
        Vector3 receiver = Ground(routine.ReceiverXZ, 0.085f);

        _painter.Flag(spot, 1.10f, 0.05f, colour);
        _painter.Ring(receiver, 0.60f, 0.055f, colour, 22);

        if (_byId.TryGetValue(routine.TakerId, out FootballerState? taker) && taker.IsActiveOnPitch)
            _painter.DashedLine(Ground(taker.Position, 0.085f), receiver, 0.05f, colour, 0.7f, 0.5f);
    }

    // ---------------------------------------------------------------------------------------------
    // A7.2: calls heard by the watched team
    // ---------------------------------------------------------------------------------------------

    private void DrawHeardCalls(MatchSimulationEngine engine)
    {
        Color colour = WithAlpha(SeenColour, 0.16f);
        foreach (FootballerState player in _activePlayers)
        {
            if (player.Team != _watchedTeam) continue;

            FootballerObservation? observation = engine.GetLastObservation(player.Id);
            if (observation is null || !observation.Calls.HasPlayerCall) continue;
            if (!_byId.TryGetValue(observation.Calls.CallerId, out FootballerState? caller) ||
                !caller.IsActiveOnPitch) continue;

            _painter.DashedLine(Ground(caller.Position, 0.075f), Ground(player.Position, 0.075f),
                0.025f, colour, 0.35f, 0.55f);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // A3.3/A3.4: job anchors and marking links
    // ---------------------------------------------------------------------------------------------

    private void DrawJobs(MatchSimulationEngine engine)
    {
        foreach (FootballerState player in _activePlayers)
        {
            if (engine.TeamJobs.Get(player.Id) is not { } assignment) continue;

            bool shape = assignment.Job == TacticalJob.Shape;
            Color colour = JobColour(assignment.Job);
            Vector3 anchor = Ground(assignment.Anchor, 0.075f);

            _painter.Disc(anchor, shape ? 0.28f : 0.42f, WithAlpha(colour, shape ? 0.13f : 0.40f), 10);
            if (!shape)
            {
                _painter.DashedLine(Ground(player.Position, 0.075f), anchor, 0.03f,
                    WithAlpha(colour, 0.14f), 0.55f, 0.55f);
            }

            if (assignment.IsTemporary)
                _painter.Ring(anchor, 0.64f, 0.05f, new Color(1f, 1f, 1f, 0.50f), 18);

            if (assignment.Job == TacticalJob.Marker && assignment.TargetPlayerId is int targetId &&
                _byId.TryGetValue(targetId, out FootballerState? marked) && marked.IsActiveOnPitch)
            {
                _painter.DashedLine(Ground(player.Position, 0.078f), Ground(marked.Position, 0.078f),
                    0.04f, WithAlpha(OpponentColour, 0.30f), 0.6f, 0.5f);
            }
            else if (assignment.Job == TacticalJob.Presser)
            {
                _painter.DashedLine(Ground(player.Position, 0.078f), Ground(engine.Ball.Position, 0.078f),
                    0.035f, WithAlpha(colour, 0.16f), 0.45f, 0.55f);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // A2.2/A2.6: team belief against the real bodies
    // ---------------------------------------------------------------------------------------------

    private void DrawBelief(MatchSimulationEngine engine)
    {
        TeamBelief belief = engine.TeamBeliefOf(_watchedTeam);
        for (int index = 0; index < belief.Opponents.Count; index++)
        {
            TeamBelief.Opponent opponent = belief.Opponents[index];
            Vector3 ghost = Ground(opponent.PositionXZ, 0.09f);
            float radius = 0.42f + MathF.Min(0.45f, opponent.AgeTicks * 0.004f);
            float alpha = 0.28f + 0.57f * Mathf.Clamp(opponent.Confidence01, 0f, 1f);
            _painter.Ring(ghost, radius, 0.11f, WithAlpha(BeliefColour, alpha), 18);

            if (!_byId.TryGetValue(opponent.PlayerId, out FootballerState? actual) || !actual.IsActiveOnPitch)
                continue;

            Vector3 truth = Ground(actual.Position, 0.09f);
            if ((truth - ghost).LengthSquared() <= 0.1225f) continue; // within 0.35 m: the ghost is the body

            _painter.DashedLine(ghost, truth, 0.035f, new Color(0.92f, 0.95f, 1f, 0.22f), 0.45f, 0.45f);
            _painter.Line(truth - new Vector3(0.22f, 0f, 0f), truth + new Vector3(0.22f, 0f, 0f), 0.035f,
                new Color(0.92f, 0.95f, 1f, 0.30f));
            _painter.Line(truth - new Vector3(0f, 0f, 0.22f), truth + new Vector3(0f, 0f, 0.22f), 0.035f,
                new Color(0.92f, 0.95f, 1f, 0.30f));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // A2.2/A7.1: the active player's sight cone and picture
    // ---------------------------------------------------------------------------------------------

    private void DrawPerception(MatchSimulationEngine engine, FootballerState activePlayer)
    {
        FootballerObservation? observation = engine.GetLastObservation(activePlayer.Id);
        if (observation is null) return;

        Vector3 eye = Ground(activePlayer.Position, 0.06f);
        NVector2 gaze = observation.EffectiveGazeXZ;
        if (gaze.LengthSquared() < 1e-4f) return;

        _painter.Wedge(eye, new Vector2(gaze.X, gaze.Y), 38f, SightHalfAngleRadians,
            new Color(0.80f, 0.93f, 1f, 0.055f), 30);
        _painter.Line(eye, eye + new Vector3(gaze.X, 0f, gaze.Y) * 2.4f, 0.07f,
            new Color(0.88f, 0.96f, 1f, 0.55f));
        // The two cone edges make the wedge boundary readable over the control map.
        Vector2 gaze2D = new(gaze.X, gaze.Y);
        foreach (float sign in new[] { -1f, 1f })
        {
            float angle = MathF.Atan2(gaze2D.Y, gaze2D.X) + sign * SightHalfAngleRadians;
            var edge = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
            _painter.Line(eye, eye + edge * 30f, 0.035f, new Color(0.88f, 0.96f, 1f, 0.20f));
        }

        for (int index = 0; index < observation.VisiblePlayers.Count; index++)
        {
            PerceivedEntity entity = observation.VisiblePlayers[index];
            if (entity.Team == activePlayer.Team) continue;

            bool fresh = entity.ObservedAgeTicks == 0;
            Color colour = fresh ? SeenColour : MemoryColour;
            Vector3 position = Ground(entity.EstimatedPosition, 0.11f);
            _painter.Disc(position, fresh ? 0.26f : 0.32f,
                WithAlpha(colour, fresh ? 0.90f : 0.55f), 8);

            if (entity.PositionUncertaintyMeters <= 0.30f) continue;
            float uncertainty = MathF.Min(0.60f, entity.PositionUncertaintyMeters);
            _painter.Ring(position, 0.30f + uncertainty, 0.04f, WithAlpha(colour, 0.32f), 14);
        }

        // Perceived ball vs the real ball: the gap is the reaction delay and observation noise (A7.5/A2.2).
        Vector3 perceivedBall = Ground(observation.PerceivedBallPosition, 0.115f);
        NVector3 realBall = engine.Ball.Position;
        var realBallGround = new Vector3(realBall.X, 0.115f, realBall.Z);
        if ((perceivedBall - realBallGround).LengthSquared() <= 0.04f) return;

        _painter.Ring(perceivedBall, 0.17f, 0.035f, new Color(1f, 1f, 1f, 0.35f), 12);
        _painter.DashedLine(perceivedBall, realBallGround, 0.025f, new Color(1f, 1f, 1f, 0.20f), 0.25f, 0.25f);
    }

    // ---------------------------------------------------------------------------------------------
    // Ball, carrier and the elected responders
    // ---------------------------------------------------------------------------------------------

    private void DrawBallState(MatchSimulationEngine engine)
    {
        Vector3 ball = Ground(engine.Ball.Position, 0.10f);
        _painter.Ring(ball, 0.36f, 0.055f, new Color(1f, 1f, 1f, 0.80f), 18);

        if (engine.Ball.Velocity.LengthSquared() > 0.25f)
        {
            Vector3 velocity = new(engine.Ball.Velocity.X, 0f, engine.Ball.Velocity.Z);
            _painter.Arrow(ball, ball + velocity * 0.55f, 0.05f,
                new Color(1f, 0.93f, 0.55f, 0.55f), 0.55f, 0.28f);
        }

        BallPossessionSnapshot possession = engine.Possession;
        if (possession.ControllingPlayerId is int carrierId && _byId.TryGetValue(carrierId, out FootballerState? carrier) &&
            carrier.IsActiveOnPitch)
        {
            Vector3 feet = Ground(carrier.Position, 0.085f);
            _painter.Ring(feet, 0.62f, 0.07f, WithAlpha(TeamColour(carrier.Team), 0.75f), 22);
            _painter.Line(feet, ball, 0.03f, WithAlpha(TeamColour(carrier.Team), 0.25f));
        }

        if (possession.IntendedReceiverId is not int receiverId) return;
        if (!_byId.TryGetValue(receiverId, out FootballerState? receiver) || !receiver.IsActiveOnPitch) return;

        Vector3 target = Ground(receiver.Position, 0.09f);
        _painter.Ring(target, 0.70f, 0.055f, new Color(1f, 1f, 1f, 0.50f), 22);
        _painter.DashedLine(ball, target, 0.04f, new Color(1f, 1f, 1f, 0.22f), 0.65f, 0.45f);
    }

    private void DrawResponders(MatchSimulationEngine engine)
    {
        DrawResponder(engine, TeamId.Home);
        DrawResponder(engine, TeamId.Away);
    }

    private void DrawResponder(MatchSimulationEngine engine, TeamId team)
    {
        int? responderId = engine.GetBallResponder(team);
        if (responderId is not int id) return;
        if (!_byId.TryGetValue(id, out FootballerState? responder) || !responder.IsActiveOnPitch) return;

        _painter.Ring(Ground(responder.Position, 0.095f), 0.90f, 0.09f, WithAlpha(TeamColour(team), 0.85f), 26);
    }

    // ---------------------------------------------------------------------------------------------
    // A4.6: the active AI player's last chosen target
    // ---------------------------------------------------------------------------------------------

    private void DrawSelectedAction(MatchSimulationEngine engine, FootballerState activePlayer)
    {
        if (engine.GetController(activePlayer.Id) is not TacticalUtilityAiController controller) return;
        if (controller.LastSelected.TargetXZ is not NVector3 target) return;

        Vector3 from = Ground(activePlayer.Position, 0.10f);
        Vector3 to = Ground(target, 0.10f);
        if ((to - from).LengthSquared() <= 0.25f) return;

        Color colour = WithAlpha(ActionColour, 0.70f);
        _painter.Arrow(from, to, 0.055f, colour, 1.0f, 0.50f);
        _painter.Ring(to, 0.48f, 0.05f, colour, 20);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private void BuildPlayerLookup(MatchSimulationEngine engine)
    {
        _byId.Clear();
        _activePlayers.Clear();
        foreach (FootballerState player in engine.Players)
        {
            _byId[player.Id] = player;
            if (player.IsActiveOnPitch) _activePlayers.Add(player);
        }
    }

    private Color TeamColour(TeamId team) => team == _watchedTeam ? WatchedColour : OpponentColour;

    private static Color WithAlpha(Color colour, float alpha) => new(colour.R, colour.G, colour.B, alpha);

    private static Vector3 Ground(NVector3 position, float y) => new(position.X, y, position.Z);

    private static Vector3 Ground(NVector2 position, float y) => new(position.X, y, position.Y);

    private static Color JobColour(TacticalJob job) => job switch
    {
        TacticalJob.BallResponder => new Color(0.99f, 0.88f, 0.28f),
        TacticalJob.Presser => new Color(0.99f, 0.57f, 0.23f),
        TacticalJob.CoverDefender => new Color(0.96f, 0.44f, 0.18f),
        TacticalJob.DefensiveCover => new Color(0.66f, 0.56f, 0.98f),
        TacticalJob.Marker => new Color(0.97f, 0.44f, 0.44f),
        TacticalJob.DefensiveLine => new Color(0.25f, 0.75f, 0.97f),
        TacticalJob.DepthRun => new Color(0.30f, 0.88f, 0.50f),
        TacticalJob.Overlap => new Color(0.18f, 0.84f, 0.76f),
        TacticalJob.FarSideWidth => new Color(0.42f, 0.91f, 0.98f),
        TacticalJob.NearOutlet => new Color(0.65f, 0.90f, 0.22f),
        TacticalJob.ThirdPlayerOption => new Color(0.55f, 0.94f, 0.68f),
        TacticalJob.ProtectGap => new Color(0.92f, 0.48f, 0.98f),
        _ => new Color(0.60f, 0.67f, 0.74f)
    };
}
