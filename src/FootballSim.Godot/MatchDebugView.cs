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
/// The on-pitch AI debug overlay (debug mode only).
///
/// The view is information-first and deliberately not a second HUD: it shows what each body is doing on
/// the pitch, and freezes the one decision and the one pass that are worth reading.
///
/// <list type="bullet">
/// <item><b>State labels</b>: one small floating state above every player (<c>PRESSING</c>, <c>MARKING</c>,
/// <c>SUPPORT_RUN</c>, <c>RECOVERY</c> ...), tinted by the team's phase (green attacking, blue defending).</item>
/// <item><b>Tethers</b>: a thin line from a marker to the exact opponent he marks, and from a runner to the
/// target coordinate he is sprinting toward.</item>
/// <item><b>Pass forensics</b>: the intended target (cyan X), the passer-to-target line, the actual post-error
/// initial velocity arrow, the predicted receiver vector, the ball's resting point from a real solver replay,
/// the ETA labels, and the actual arrival delta once the pass resolves.</item>
/// <item><b>Trap radius and touch rejection</b>: the receiver's exact contact radius as a wireframe circle, and
/// a 2 s floating reason (<c>TOO_FAST</c>, <c>TOO_HIGH</c>, <c>ON_COOLDOWN</c>, <c>WRONG_STATE</c>) when the ball
/// is inside it without being controlled.</item>
/// <item><b>Pass corridor</b>: the corridor at the defender's reach width, plus every nearby defender's
/// time-scaled sprint circle during the ball's ETA; a circle that overlaps the corridor marks the pass unsafe.</item>
/// <item><b>Frozen decision</b>: straight option lines (green chosen, yellow alternatives, red rejected) with
/// the score on each line, held until the next kick or 3 seconds, plus the four-line decision readout in the
/// top-right corner.</item>
/// </list>
///
/// Every layer is derived from state the simulation already publishes; nothing here is written back and no
/// simulation RNG is drawn. Ball flight and resting points are replayed on a cloned <see cref="BallState"/> by
/// a second <see cref="BallPhysicsSolver"/> with the match's own pitch, so they use the same drag, rolling
/// resistance and bounce rules as the live ball.
/// </summary>
public partial class MatchDebugView : Node3D
{
    private const int HeatMapRefreshTicks = 8;
    private const int MaxContactSettleTicks = 180;
    private const int DeliveryLifetimeTicks = 1800;
    private const int ResolvedDeliveryLifetimeTicks = 300;
    private const int DecisionLifetimeTicks = 180;
    private const float FallbackCorridorHalfWidthMeters = 1.20f;
    private const float MaxDefenderCircleMeters = 14f;

    private static readonly Color SupportColour = new(0.32f, 0.92f, 0.52f);
    private static readonly Color DefendColour = new(0.34f, 0.68f, 1.00f);
    private static readonly Color WatchedColour = new(0.13f, 0.83f, 0.93f);
    private static readonly Color OpponentColour = new(0.97f, 0.44f, 0.44f);
    private static readonly Color Cyan = new(0.20f, 0.95f, 1.00f);
    private static readonly Color Yellow = new(1.00f, 0.86f, 0.16f);
    private static readonly Color Magenta = new(0.98f, 0.42f, 0.98f);
    private static readonly Color RejectColour = new(1.00f, 0.20f, 0.20f);
    private static readonly Color UnsafeCorridorColour = new(1.00f, 0.55f, 0.10f);
    private static readonly Color ChosenColour = new(0.15f, 1.00f, 0.42f);
    private static readonly Color White = new(1f, 1f, 1f);
    private static readonly Color WarmWhite = new(0.93f, 0.95f, 1.00f);

    private readonly Dictionary<int, FootballerState> _byId = new();
    private readonly List<FootballerState> _activePlayers = new();
    private readonly BallFlightPreview _flight = new();
    private readonly TouchRejectionTracker _touchRejections = new();

    private DebugMeshPainter _painter = null!;
    private MatchDebugLabels _labels = null!;
    private MatchDebugPanel _panel = null!;
    private BallPhysicsSolver? _ballSolver;
    private PitchDimensions? _solverPitch;
    private PassPlanner? _passPlanner;
    private SpatialOverlayMap? _heatMap;
    private Color[] _heatColours = Array.Empty<Color>();
    private int _heatBuiltTick = -1;
    private DeliverySnapshot? _delivery;
    private DecisionSnapshot? _decision;
    private int _lastContactTick = -1;
    private TeamId _watchedTeam = TeamId.Home;
    private bool _enabled;

    public bool Enabled => _enabled;

    public TeamId WatchedTeam => _watchedTeam;

    public override void _Ready()
    {
        _painter = new DebugMeshPainter(this);

        var canvas = new CanvasLayer();
        AddChild(canvas);
        _labels = new MatchDebugLabels();
        canvas.AddChild(_labels);
        _panel = new MatchDebugPanel();
        canvas.AddChild(_panel);

        _painter.Visible = _enabled;
        _labels.SetLayerVisible(false);
        _panel.SetSnapshot(null);
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (_painter is not null) _painter.Visible = enabled;
        if (_labels is not null) _labels.SetLayerVisible(enabled);
        if (_panel is not null) _panel.SetSnapshot(enabled ? _decision : null);

        if (!enabled)
        {
            _delivery = null;
            _decision = null;
            _touchRejections.Clear();
            _lastContactTick = -1;
            return;
        }

        _heatBuiltTick = -1;
        _lastContactTick = -1;
    }

    /// <summary>F4: switch which team the background control map shows.</summary>
    public void ToggleWatchedTeam()
    {
        _watchedTeam = _watchedTeam == TeamId.Home ? TeamId.Away : TeamId.Home;
        _heatBuiltTick = -1;
    }

    public void Refresh(MatchSimulationEngine engine, FootballerState activePlayer)
    {
        if (!_enabled || _painter is null) return;

        EnsureServices(engine);
        BuildPlayerLookup(engine);

        if (_lastContactTick < 0) _lastContactTick = engine.CurrentTick - 1;
        ProcessContacts(engine);
        UpdateDecision(engine);
        UpdateTouchRejections(engine);
        ExpireState(engine);

        _painter.Begin();
        _labels.Begin();
        try
        {
            DrawControlMap(engine);
            DrawLineBands(engine);
            DrawPlayerStates(engine);
            DrawDelivery(engine);
            DrawDecision();
            DrawTouchRejections(engine);
        }
        finally
        {
            _labels.End();
            _painter.End();
        }

        if (_decision is { } frozenDecision)
            frozenDecision.SecondsLeft = Mathf.Max(0f, (frozenDecision.ExpiresTick - engine.CurrentTick) / 60f);
        _panel.SetSnapshot(_decision);
    }

    // ---------------------------------------------------------------------------------------------
    // Services and lookups
    // ---------------------------------------------------------------------------------------------

    private void EnsureServices(MatchSimulationEngine engine)
    {
        if (_ballSolver is not null && ReferenceEquals(_solverPitch, engine.Pitch)) return;

        // Parameters are the match's defaults; the pitch instance is what matters for boundary bounces.
        _ballSolver = new BallPhysicsSolver(null, engine.Pitch);
        _solverPitch = engine.Pitch;
        _passPlanner = new PassPlanner(engine.Pitch);
    }

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

    // ---------------------------------------------------------------------------------------------
    // Contact log: kick detection, pass snapshot, arrival resolution
    // ---------------------------------------------------------------------------------------------

    private void ProcessContacts(MatchSimulationEngine engine)
    {
        IReadOnlyList<BallContactEvent> log = engine.ContactLog;
        if (log.Count == 0)
        {
            if (_lastContactTick > engine.CurrentTick) _lastContactTick = engine.CurrentTick;
            return;
        }

        int processed = _lastContactTick;
        for (int index = 0; index < log.Count; index++)
        {
            BallContactEvent contact = log[index];
            if (contact.Tick <= processed) continue;
            ProcessContact(engine, contact);
            processed = Mathf.Max(processed, contact.Tick);
        }

        _lastContactTick = processed;
    }

    private void ProcessContact(MatchSimulationEngine engine, in BallContactEvent contact)
    {
        if (_delivery is { } delivery && contact.Kind != BallContactKind.Kick)
        {
            if (!delivery.Resolved && contact.PlayerId != delivery.KickerId)
                ResolveDelivery(delivery, contact.Tick, contact.PlayerId);
        }
        else if (_delivery is { } passing && contact.Kind == BallContactKind.Kick &&
                 contact.PlayerId != passing.KickerId && !passing.Resolved)
        {
            ResolveDelivery(passing, contact.Tick, contact.PlayerId);
        }

        if (contact.Kind != BallContactKind.Kick) return;

        // A restart kick can carry no KickIntentType yet still name the man it is played to (the rules engine
        // owns that play). Treat it as a pass for the overlay: the owner's "see the decision when they pass"
        // question applies to the kick-off as much as to open play.
        if (!KickIntents.IsPass(contact.Intent) && contact.IntendedReceiverId is null) return;
        CreateDelivery(engine, contact);
    }

    private void CreateDelivery(MatchSimulationEngine engine, in BallContactEvent contact)
    {
        if (!_byId.TryGetValue(contact.PlayerId, out FootballerState? kicker)) return;

        int? receiverId = contact.IntendedReceiverId;
        FootballerState? receiver = receiverId is int rid && _byId.TryGetValue(rid, out FootballerState? recv)
            ? recv
            : null;

        // The intended target: the AI's selected pass candidate carries the exact meeting point the planner
        // aimed at; a human or a fallback uses the receiver / cursor / command aim.
        NVector2 targetXZ = default;
        bool hasTarget = false;
        if (engine.GetController(kicker.Id) is TacticalUtilityAiController { LastSelected: var selected } &&
            selected.Key.Kind == CandidateKind.Pass &&
            selected.Key.TargetPlayerId == (receiverId ?? 0) &&
            selected.TargetXZ is NVector3 candidateTarget)
        {
            targetXZ = new NVector2(candidateTarget.X, candidateTarget.Z);
            hasTarget = true;
        }

        if (!hasTarget && receiver is not null)
        {
            targetXZ = new NVector2(receiver.Position.X, receiver.Position.Z);
            hasTarget = true;
        }

        if (!hasTarget && engine.LockedHumanController is { IsSpectatorMode: false } human &&
            human.LockedPlayerId == kicker.Id && human.AimPointXZ is NVector2 aimPoint &&
            (aimPoint - new NVector2(kicker.Position.X, kicker.Position.Z)).LengthSquared() > 1f)
        {
            targetXZ = aimPoint;
            hasTarget = true;
        }

        if (!hasTarget)
        {
            NVector3 aim = kicker.LastCommand.KickAimDirectionXZ;
            var aimXZ = new NVector2(aim.X, aim.Z);
            if (aimXZ.LengthSquared() < 1e-4f) return;
            float range = _passPlanner?.RangeFor(kicker.LastCommand.KickPower01) ?? 20f;
            if (!float.IsFinite(range) || range <= 0f) range = 20f;
            aimXZ = NVector2.Normalize(aimXZ);
            targetXZ = new NVector2(kicker.Position.X, kicker.Position.Z) + aimXZ * range;
        }

        var delivery = new DeliverySnapshot
        {
            KickerId = kicker.Id,
            Team = kicker.Team,
            Intent = contact.Intent,
            KickTick = contact.Tick,
            ReceiverId = receiverId,
            PasserPosition = Ground(kicker.Position, 0.08f),
            ContactBallPosition = Ground(contact.BallPosition, 0.07f),
            Target = new Vector3(targetXZ.X, 0.07f, targetXZ.Y),
            InitialVelocity = new Vector3(engine.Ball.Velocity.X, engine.Ball.Velocity.Y, engine.Ball.Velocity.Z),
            ReceiverStart = receiver is null ? Vector3.Zero : Ground(receiver.Position, 0.08f),
            CorridorHalfWidth = DefenderTackleReach(engine, kicker.Team)
        };

        // Start the replay where the ball was actually struck, not where it has drifted to while the contact
        // was being noticed: the resting point and the ETAs are read as the pass's, at kick time.
        _flight.Build(engine.Ball, _ballSolver!, delivery.ContactBallPosition, engine.Pitch);
        if (_flight.TryClosestApproach(new NVector2(targetXZ.X, targetXZ.Y), 6f,
                out float closestDistance, out float ballEta, out Vector3 closestPoint))
        {
            delivery.BallEtaSeconds = ballEta;
            delivery.BallClosestPoint = closestPoint;
            if (closestDistance > 3f) delivery.BallEtaSeconds = -1f;
        }

        if (receiver is not null)
        {
            ReachKinematics kinematics = ReachKinematics.From(receiver);
            ReachEstimate estimate = TimeToReachModel.ToPoint(
                kinematics, targetXZ, horizonSeconds: 8f, sprint: true);
            delivery.ReceiverEtaSeconds = estimate.Reachable ? estimate.Seconds : -1f;
        }

        delivery.RestingPoint = _flight.RestingPoint;
        delivery.WouldLeavePitch = _flight.LeftPitch;
        BuildCorridor(delivery);
        _delivery = delivery;
    }

    /// <summary>
    /// Half the corridor's width: the widest tackle/interception reach among the opponents. A defender whose
    /// body is closer to the pass line than this can play the ball, so the corridor is drawn at exactly the
    /// distance that makes the pass contestable — the same question A4.8's lane radius answers for the planner.
    /// </summary>
    private static float DefenderTackleReach(MatchSimulationEngine engine, TeamId passerTeam)
    {
        float widest = 0f;
        foreach (FootballerState player in engine.Players)
        {
            if (!player.IsActiveOnPitch || player.Team == passerTeam) continue;
            float reach = BallContactRules.ReachRadius(
                player.Attributes.ReachRadius, keeperHandling: false, player.IsDiving,
                requestingTackle: true, receivingOrDribbling: false);
            if (reach > widest) widest = reach;
        }

        return widest > 0.2f ? widest : FallbackCorridorHalfWidthMeters;
    }

    private void BuildCorridor(DeliverySnapshot delivery)
    {
        Vector3 start = delivery.ContactBallPosition;
        Vector3 target = delivery.Target;
        Vector3 delta = target - start;
        float length = new Vector2(delta.X, delta.Z).Length();
        if (length < 0.5f) return;

        var origin = new NVector2(start.X, start.Z);
        var end = new NVector2(target.X, target.Z);
        NVector2 direction = NVector2.Normalize(end - origin);

        foreach (FootballerState defender in _activePlayers)
        {
            if (defender.Team == delivery.Team) continue;

            var position = new NVector2(defender.Position.X, defender.Position.Z);
            float along = NVector2.Dot(position - origin, direction);
            if (along <= 0f || along >= length) continue;

            NVector2 projection = origin + direction * along;
            float perpendicular = (position - projection).Length();
            if (perpendicular > 18f) continue;

            float eta = _flight.TimeAtProjection(start, target, along);
            if (eta < 0f) continue;

            float sprintSpeed = TimeToReachModel.SpeedCap(ReachKinematics.From(defender), sprint: true);
            float radius = Mathf.Min(MaxDefenderCircleMeters, sprintSpeed * eta);
            if (radius < 0.6f) continue;

            bool overlaps = perpendicular <= radius + delivery.CorridorHalfWidth;
            delivery.DefenderReaches.Add(new DefenderReach(
                defender.Id, Ground(defender.Position, 0.06f), radius, overlaps));
            if (overlaps) delivery.CorridorUnsafe = true;
        }
    }

    private void ResolveDelivery(DeliverySnapshot delivery, int tick, int toucherId)
    {
        delivery.Resolved = true;
        delivery.ResolvedTick = tick;
        delivery.ActualToucherId = toucherId;

        FootballerState? receiver = delivery.ReceiverId is int id && _byId.TryGetValue(id, out FootballerState? found)
            ? found
            : null;
        delivery.ReceiverEnd = receiver is not null
            ? Ground(receiver.Position, 0.08f)
            : _byId.TryGetValue(toucherId, out FootballerState? toucher)
                ? Ground(toucher.Position, 0.08f)
                : delivery.Target;
        delivery.ArrivalDeltaMeters = new Vector2(
            delivery.ReceiverEnd.X - delivery.Target.X,
            delivery.ReceiverEnd.Z - delivery.Target.Z).Length();
    }

    // ---------------------------------------------------------------------------------------------
    // Decision snapshot
    // ---------------------------------------------------------------------------------------------

    private void UpdateDecision(MatchSimulationEngine engine)
    {
        // A frozen decision owns its full three-second read window: a newer decision (usually the next
        // carrier, a second after the pass) does not overwrite it mid-window. That is what makes "keep it
        // frozen so you have time to read it" true when play does not pause for the debugger.
        if (_decision is { } frozen && engine.CurrentTick <= frozen.ExpiresTick) return;

        int? carrierId = engine.Possession.ControllingPlayerId;
        if (carrierId is not int playerId ||
            engine.GetController(playerId) is not TacticalUtilityAiController controller)
        {
            return;
        }

        UtilityCandidate selected = controller.LastSelected;
        if (!IsOnBallKind(selected.Key.Kind)) return;

        var snapshot = new DecisionSnapshot
        {
            PlayerId = playerId,
            CreatedTick = engine.CurrentTick,
            ExpiresTick = engine.CurrentTick + DecisionLifetimeTicks,
            Origin = Ground(_byId.TryGetValue(playerId, out FootballerState? carrier)
                    ? carrier.Position
                    : NVector3.Zero, 0.08f),
            SelectedIdentity = selected.Identity,
            SecondsLeft = DecisionLifetimeTicks / 60f,
            Title = $"{PlayerLabel(playerId)} → {ActionLabel(selected)}"
        };

        // The winner's row is read from this tick's candidate table when the key is still there, so its score
        // and terms are the same snapshot as the alternatives. Taking the controller's LastSelected score (a
        // decision from an earlier tick) could print a winner below its runner-up when the table had moved on.
        IReadOnlyList<UtilityCandidate> candidates = controller.LastCandidates;
        UtilityCandidate chosen = selected;
        for (int index = 0; index < candidates.Count; index++)
        {
            if (candidates[index].Key != selected.Key) continue;
            chosen = candidates[index];
            break;
        }

        DecisionOption chosenRow = ToOption(chosen, chosen: true, rejected: false, reason: string.Empty);
        snapshot.Options.Add(chosenRow);

        // Every other option the carrier actually generated this decision: passes, carries, the take-on, the
        // shot. They are all drawn (the request is to see what was considered, not only the top three), with
        // the corner readout left to summarise the winner and the two nearest callers.
        var alternatives = new List<UtilityCandidate>();
        for (int index = 0; index < candidates.Count; index++)
        {
            UtilityCandidate candidate = candidates[index];
            if (!IsOnBallKind(candidate.Key.Kind)) continue;
            if (candidate.Key == chosen.Key) continue;
            alternatives.Add(candidate);
        }

        alternatives.Sort((left, right) => right.UtilityScore.CompareTo(left.UtilityScore));
        var alternativeRows = new List<DecisionOption>();
        for (int index = 0; index < alternatives.Count; index++)
        {
            DecisionOption row = ToOption(alternatives[index], chosen: false, rejected: false, reason: string.Empty);
            snapshot.Options.Add(row);
            alternativeRows.Add(row);
        }

        // The refusals are options too: a blocked lane or a man out of range is what makes the chosen line
        // the chosen one, so each gets a dashed red line of its own.
        var rejectionRows = new List<DecisionOption>();
        for (int index = 0; index < controller.RejectionCount; index++)
        {
            CandidateRejection rejection = controller.GetRejection(index);
            if (!_byId.TryGetValue(rejection.TargetPlayerId, out FootballerState? target)) continue;

            var row = new DecisionOption
            {
                ActionLabel = $"PASS {PlayerLabel(rejection.TargetPlayerId)}",
                Reason = RejectionLabel(rejection.Reason),
                Rejected = true,
                Kind = CandidateKind.Pass,
                TargetPlayerId = rejection.TargetPlayerId,
                Target = Ground(target.Position, 0.08f)
            };
            snapshot.Options.Add(row);
            rejectionRows.Add(row);
        }

        // The readout is three rows: winner, best alternative, most useful refusal. A blocked option stays
        // visible even when a second alternative scored above it.
        snapshot.PanelRows.Add(chosenRow);
        int alternativeIndex = 0;
        int rejectionIndex = 0;
        if (alternativeIndex < alternativeRows.Count)
            snapshot.PanelRows.Add(alternativeRows[alternativeIndex++]);
        if (rejectionIndex < rejectionRows.Count)
            snapshot.PanelRows.Add(rejectionRows[rejectionIndex++]);
        if (snapshot.PanelRows.Count < 3 && alternativeIndex < alternativeRows.Count)
            snapshot.PanelRows.Add(alternativeRows[alternativeIndex++]);
        if (snapshot.PanelRows.Count < 3 && rejectionIndex < rejectionRows.Count)
            snapshot.PanelRows.Add(rejectionRows[rejectionIndex]);

        _decision = snapshot;
    }

    /// <summary>Shirt number for a debug label; the entity id is only a fallback when the player is unknown.</summary>
    private string PlayerLabel(int playerId) =>
        _byId.TryGetValue(playerId, out FootballerState? player) ? $"#{player.ShirtNumber}" : $"#{playerId}";

    private DecisionOption ToOption(UtilityCandidate candidate, bool chosen, bool rejected, string reason) =>
        new()
        {
            ActionLabel = ActionLabel(candidate),
            Score = candidate.UtilityScore,
            Chosen = chosen,
            Rejected = rejected,
            Reason = reason,
            Terms = candidate.Terms,
            Kind = candidate.Key.Kind,
            TargetPlayerId = candidate.Key.TargetPlayerId,
            Target = candidate.TargetXZ is NVector3 target
                ? new Vector3(target.X, 0.08f, target.Z)
                : Vector3.Zero
        };

    private static bool IsOnBallKind(CandidateKind kind) => kind is
        CandidateKind.Pass or CandidateKind.Carry or CandidateKind.Dribble or
        CandidateKind.Shot or CandidateKind.Shield or CandidateKind.Clearance;

    private string ActionLabel(UtilityCandidate candidate)
    {
        string label = candidate.Key.Kind switch
        {
            CandidateKind.Pass => "PASS",
            CandidateKind.Carry => "CARRY",
            CandidateKind.Dribble => "DRIBBLE",
            CandidateKind.Shot => "SHOT",
            CandidateKind.Shield => "SHIELD",
            CandidateKind.Clearance => "CLEAR",
            _ => candidate.ActionName.ToUpperInvariant()
        };

        return candidate.Key.TargetPlayerId > 0
            ? $"{label} {PlayerLabel(candidate.Key.TargetPlayerId)}"
            : label;
    }

    private static string RejectionLabel(string reason) => reason switch
    {
        "lane-blocked" => "BLOCKED",
        "low-completion" => "LOW_P",
        "offside" => "OFFSIDE",
        "out-of-range" => "RANGE",
        "unplannable" => "NO_PLAN",
        "own-goal-margin" => "OWN_GOAL",
        _ => reason.ToUpperInvariant()
    };

    // ---------------------------------------------------------------------------------------------
    // Touch rejection tracker
    // ---------------------------------------------------------------------------------------------

    private void UpdateTouchRejections(MatchSimulationEngine engine)
    {
        int tick = engine.CurrentTick;
        _touchRejections.Update(tick);
        TagRefusedAttempts(engine, tick);
        TagReceiverTrapFailure(engine, tick);
    }

    /// <summary>
    /// The authoritative half: a player asked to play the ball and the execution layer refused him. The attempt
    /// log is the execution's own verdict (audit F17), so the floating tag repeats the real reason instead of
    /// re-deriving one. Refusals of a ball that was not physically at the player are not trap failures.
    /// </summary>
    private void TagRefusedAttempts(MatchSimulationEngine engine, int tick)
    {
        IReadOnlyList<BallContactAttempt> attempts = engine.LastContactAttempts;
        for (int index = 0; index < attempts.Count; index++)
        {
            BallContactAttempt attempt = attempts[index];
            if (attempt.Executed || attempt.Tick != tick) continue;

            string? reason = attempt.Reason switch
            {
                ContactRejectionReason.BallTooHigh => "TOO_HIGH",
                ContactRejectionReason.FacingAway => "WRONG_STATE",
                ContactRejectionReason.InRecovery => "ON_COOLDOWN",
                ContactRejectionReason.LostArbitration => "CONTESTED",
                ContactRejectionReason.BallHeld => "HELD",
                ContactRejectionReason.RestartTriggered => "RESTART",
                _ => null
            };
            if (reason is not null) TagTouch(attempt.PlayerId, reason, tick);
        }
    }

    /// <summary>
    /// The receiver half: the ball entered the intended receiver's trap radius and he did not control it. The
    /// gates below are the execution layer's own rules and predicates; the tag only reports which one refused
    /// the contact. It never decides anything and never moves a body.
    /// </summary>
    private void TagReceiverTrapFailure(MatchSimulationEngine engine, int tick)
    {
        if (_delivery is not { Resolved: false } delivery || delivery.ReceiverId is not int receiverId) return;
        if (!_byId.TryGetValue(receiverId, out FootballerState? receiver) || !receiver.IsActiveOnPitch) return;
        if (engine.Rules.IsPlaySuspended) return;
        if (engine.Possession.ControllingPlayerId == receiverId || engine.Ball.HeldByPlayerId is not null) return;

        BallState ball = engine.Ball;
        var toBall = new NVector2(ball.Position.X - receiver.Position.X,
            ball.Position.Z - receiver.Position.Z);
        float distance = toBall.Length();
        bool receiving = BallContactRules.IsReceivingOrDribbling(
            ball.LastTouchedByTeam, ball.LastTouchedByPlayerId, receiver.Team, receiver.Id);
        float reach = BallContactRules.ReachRadius(
            receiver.Attributes.ReachRadius, keeperHandling: false, receiver.IsDiving,
            receiver.LastCommand.RequestTackle, receiving);
        if (distance > reach) return;

        float maxHeight = BallContactRules.MaxContactHeight(
            keeperHandling: false,
            jumpingOrHeading: receiver.IsJumping || receiver.LastCommand.KickType == KickIntentType.Header);
        if (!BallContactRules.IsHeightReachable(ball.Position.Y, receiver.Position.Y, maxHeight))
        {
            TagTouch(receiver.Id, "TOO_HIGH", tick);
            return;
        }

        if (!receiver.AvailableToKick)
        {
            TagTouch(receiver.Id, "ON_COOLDOWN", tick);
            return;
        }

        float facingDot = distance > 1e-4f
            ? NVector2.Dot(toBall * (1f / distance), new NVector2(receiver.FacingXZ.X, receiver.FacingXZ.Y))
            : 1f;
        if (!BallContactRules.IsFacingTheBall(facingDot, receiving, keeperHandling: false))
            TagTouch(receiver.Id, "WRONG_STATE", tick);
    }

    /// <summary>Records a touch-rejection tag for the 2 s floating reason above the player.</summary>
    private void TagTouch(int playerId, string reason, int tick)
    {
        _touchRejections.Tag(playerId, reason, tick);
    }

    // ---------------------------------------------------------------------------------------------
    // Expiry / settling
    // ---------------------------------------------------------------------------------------------

    private void ExpireState(MatchSimulationEngine engine)
    {
        int tick = engine.CurrentTick;

        if (_decision is not null && tick > _decision.ExpiresTick)
            _decision = null;

        if (_delivery is null) return;

        int age = _delivery.AgeTicks(tick);
        int lifetime = _delivery.Resolved ? ResolvedDeliveryLifetimeTicks : DeliveryLifetimeTicks;
        if (age > lifetime)
        {
            _delivery = null;
            return;
        }

        if (_delivery.Resolved || age < 30) return;

        // A ball that came back under somebody's control before the contact log closed the pass means the
        // pass has effectively ended; resolve it now so the delta is this pass's, not the next passage's.
        if (engine.Possession.ControllingPlayerId is int controllerId && controllerId != _delivery.KickerId)
        {
            ResolveDelivery(_delivery, tick, controllerId);
            return;
        }

        float speed = new NVector2(engine.Ball.Velocity.X, engine.Ball.Velocity.Z).Length();
        bool settling = speed < 0.25f && engine.Ball.ContactState == BallContactSurface.RollingOrSlidingGround;
        if (settling || age > MaxContactSettleTicks)
        {
            ResolveDelivery(_delivery, tick, _delivery.KickerId);
            _delivery.ReceiverEnd = _delivery.ReceiverId is int receiverId &&
                                    _byId.TryGetValue(receiverId, out FootballerState? receiver)
                ? Ground(receiver.Position, 0.08f)
                : Ground(engine.Ball.Position, 0.08f);
            _delivery.ArrivalDeltaMeters = new Vector2(
                _delivery.ReceiverEnd.X - _delivery.Target.X,
                _delivery.ReceiverEnd.Z - _delivery.Target.Z).Length();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Background: control map and defensive-line bands
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
                if (total <= 0.08f)
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
                _heatColours[index] = new Color(tint.R, tint.G, tint.B, 0.012f + 0.09f * total);
            }
        }

        const float y = 0.030f;
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

    private void DrawLineBands(MatchSimulationEngine engine)
    {
        float homeDir = engine.Rules.GetHomeAttackDirectionX();
        DrawLineBand(engine, TeamId.Home, homeDir);
        DrawLineBand(engine, TeamId.Away, -homeDir);
    }

    private void DrawLineBand(MatchSimulationEngine engine, TeamId team, float attackDirX)
    {
        DefensiveLineBand band = engine.TeamJobs.LineBandOf(team);
        if (!band.IsValid) return;

        Color colour = team == _watchedTeam ? WatchedColour : OpponentColour;
        float halfWidth = engine.Pitch.HalfWidth;
        float x1 = band.MinProjX * attackDirX;
        float x2 = band.MaxProjX * attackDirX;
        float lineX = band.ProjX * attackDirX;
        float minX = MathF.Min(x1, x2);
        float maxX = MathF.Max(x1, x2);

        const float y = 0.040f;
        _painter.Quad(
            new Vector3(minX, y, -halfWidth), new Vector3(maxX, y, -halfWidth),
            new Vector3(maxX, y, halfWidth), new Vector3(minX, y, halfWidth),
            WithAlpha(colour, 0.020f));
        _painter.Line(new Vector3(lineX, 0.060f, -halfWidth), new Vector3(lineX, 0.060f, halfWidth),
            0.090f, WithAlpha(colour, 0.42f));
    }

    // ---------------------------------------------------------------------------------------------
    // State labels and tethers
    // ---------------------------------------------------------------------------------------------

    private void DrawPlayerStates(MatchSimulationEngine engine)
    {
        for (int index = 0; index < _activePlayers.Count; index++)
        {
            FootballerState player = _activePlayers[index];
            FootballerObservation? observation = engine.GetLastObservation(player.Id);
            TeamContext context = observation?.TacticalContext ?? engine.Tactics.ContextFor(player.Team);
            Color phaseColour = context.Phase == TeamPhase.Attacking ? SupportColour : DefendColour;

            string state = DescribePlayerState(engine, player, observation, context);
            float labelLift = 1.95f + player.Id % 3 * 0.20f;
            _labels.Add(Ground(player.Position, labelLift), state, phaseColour, 13);

            DrawMarkingTether(player, observation);
            DrawTargetTether(engine, player, observation, context);
        }
    }

    private string DescribePlayerState(MatchSimulationEngine engine, FootballerState player,
        FootballerObservation? observation, TeamContext context)
    {
        if (engine.Ball.HeldByPlayerId == player.Id) return "KEEPER_HOLD";
        if (engine.Possession.ControllingPlayerId == player.Id) return "ON_BALL";
        if (engine.Possession.IntendedReceiverId == player.Id &&
            engine.Possession.State == BallControlState.InFlightPass) return "RECEIVING";
        if (player.Role == TacticalRole.Goalkeeper) return "KEEPER";

        TacticalJob job = observation?.TacticalJob ?? engine.TeamJobs.Get(player.Id)?.Job ?? TacticalJob.Shape;
        bool attacking = context.Phase == TeamPhase.Attacking;

        return job switch
        {
            TacticalJob.BallResponder => attacking ? "SUPPORT" : "CHASING",
            TacticalJob.Presser => "PRESSING",
            TacticalJob.Marker => "MARKING",
            TacticalJob.CoverDefender => "COVERING",
            TacticalJob.DefensiveCover => "REST_DEFENCE",
            TacticalJob.DefensiveLine => "LINE",
            TacticalJob.ProtectGap => "COVER_GAP",
            TacticalJob.NearOutlet => "OUTLET",
            TacticalJob.ThirdPlayerOption => "THIRD_OPTION",
            TacticalJob.DepthRun => "SUPPORT_RUN",
            TacticalJob.Overlap => "OVERLAP",
            TacticalJob.FarSideWidth => "HOLD_WIDTH",
            _ => DescribeShapeState(engine, player, context, attacking)
        };
    }

    private static string DescribeShapeState(MatchSimulationEngine engine, FootballerState player,
        TeamContext context, bool attacking)
    {
        if (attacking)
            return context.Transition == TransitionMode.CounterAttack ? "COUNTER_RUN" : "SUPPORT";

        TurnoverSituation turnover = engine.TeamJobs.TurnoverOf(player.Team);
        if (turnover.Response == TurnoverResponse.Retreat) return "RECOVERY";
        if (turnover.Response == TurnoverResponse.CounterPress) return "COUNTER_PRESS";
        return "DEFENDING";
    }

    private void DrawMarkingTether(FootballerState player, FootballerObservation? observation)
    {
        if (observation is not { TacticalJob: TacticalJob.Marker, JobTargetPlayerId: int targetId }) return;
        if (!_byId.TryGetValue(targetId, out FootballerState? target) || !target.IsActiveOnPitch) return;

        // Dark underlay + a dot on the marked man: a 1 px blue line alone was unassignable when several
        // tethers crossed, and it read as just another pitch marking.
        Vector3 from = Ground(player.Position, 0.070f);
        Vector3 to = Ground(target.Position, 0.070f);
        _painter.Line(from, to, 0.200f, new Color(0f, 0f, 0f, 0.30f));
        _painter.Line(from, to, 0.120f, WithAlpha(DefendColour, 0.90f));
        _painter.Disc(to + Vector3.Up * 0.005f, 0.35f, WithAlpha(DefendColour, 0.85f), 12);
    }

    private void DrawTargetTether(MatchSimulationEngine engine, FootballerState player,
        FootballerObservation? observation, TeamContext context)
    {
        if (context.Phase != TeamPhase.Attacking) return;

        TacticalJob job = observation?.TacticalJob ?? TacticalJob.Shape;
        if (job is not (TacticalJob.DepthRun or TacticalJob.Overlap or TacticalJob.FarSideWidth or
            TacticalJob.NearOutlet or TacticalJob.ThirdPlayerOption)) return;

        Vector3 from = Ground(player.Position, 0.070f);
        Vector3 target;
        if (engine.Communication.Runs.TryGetAdvertisement(player.Team, player.Id, engine.CurrentTick,
                out RunAdvertisement advertisement) && advertisement.IsUsableAt(engine.CurrentTick))
        {
            target = Ground(advertisement.MeetXZ, 0.070f);
            _painter.DashedLine(from, target, 0.180f, new Color(0f, 0f, 0f, 0.28f), 0.55f, 0.45f);
            _painter.DashedLine(from, target, 0.090f, WithAlpha(SupportColour, 0.88f), 0.55f, 0.45f);
        }
        else
        {
            if (observation?.TacticalJobAnchor is not NVector3 anchor) return;
            target = Ground(anchor, 0.070f);
            _painter.Line(from, target, 0.180f, new Color(0f, 0f, 0f, 0.28f));
            _painter.Line(from, target, 0.090f, WithAlpha(SupportColour, 0.80f));
        }

        _painter.Cross(target, 0.90f, 0.100f, WithAlpha(SupportColour, 0.90f));
    }

    // ---------------------------------------------------------------------------------------------
    // Pass forensics
    // ---------------------------------------------------------------------------------------------

    private void DrawDelivery(MatchSimulationEngine engine)
    {
        DeliverySnapshot? delivery = _delivery;
        if (delivery is null) return;

        if (!delivery.Resolved)
        {
            // In flight: the full forensic picture while the ball is travelling.
            DrawCorridor(delivery);
            DrawTargetMarker(delivery, withEtas: true);
            DrawInitialVelocity(delivery);
            DrawRestingPoint(delivery);
            DrawReceiverVector(delivery);
            DrawTrapRadius(engine, delivery);
            return;
        }

        // Resolved: keep only the marker and the outcome (the arrival delta), so the next passage is not
        // read through the previous pass's clutter while its short result window is open.
        DrawTargetMarker(delivery, withEtas: false);
        DrawArrivalDelta(delivery);
    }

    private void DrawCorridor(DeliverySnapshot delivery)
    {
        Vector3 start = delivery.ContactBallPosition;
        Vector3 target = delivery.Target;
        var delta = new Vector3(target.X - start.X, 0f, target.Z - start.Z);
        float length = delta.Length();
        if (length < 1f) return;

        Vector3 direction = delta / length;
        Vector3 side = direction.Cross(Vector3.Up).Normalized() * delivery.CorridorHalfWidth;
        Vector3 a = start - side;
        Vector3 b = start + side;
        Vector3 c = target + side;
        Vector3 d = target - side;

        Color fill = delivery.CorridorUnsafe
            ? new Color(1f, 0.55f, 0.10f, 0.14f)
            : new Color(0.75f, 0.90f, 1f, 0.075f);
        _painter.Quad(a, b, c, d, fill);
        _painter.QuadOutline(a, b, c, d, 0.160f,
            delivery.CorridorUnsafe
                ? WithAlpha(UnsafeCorridorColour, 0.92f)
                : new Color(0.75f, 0.92f, 1f, 0.85f));

        foreach (DefenderReach reach in delivery.DefenderReaches)
        {
            // Spoked rings rather than plain circles: a plain 14 m ring reads as a pitch marking, while the
            // radial ticks make it obviously debug geometry. Orange is "this pass is unsafe", blue is a
            // reachable sprint that misses the corridor; red stays reserved for the delta and the touch tag.
            Color colour = reach.OverlapsCorridor
                ? WithAlpha(UnsafeCorridorColour, 0.95f)
                : new Color(0.35f, 0.60f, 1.00f, 0.70f);
            _painter.WireCircle(reach.Position, reach.Radius, reach.OverlapsCorridor ? 0.180f : 0.110f,
                colour, spokes: reach.OverlapsCorridor ? 10 : 6);
        }
    }

    private void DrawTargetMarker(DeliverySnapshot delivery, bool withEtas)
    {
        // White line over white pitch markings disappears; a dark underlay keeps the pass line readable.
        _painter.Line(delivery.PasserPosition, delivery.Target, 0.300f, new Color(0f, 0f, 0f, 0.30f));
        _painter.Line(delivery.PasserPosition, delivery.Target, 0.150f, new Color(1f, 1f, 1f, 0.82f));
        _painter.Cross(delivery.Target, 1.10f, 0.180f, Cyan);
        // A second X floating at head height: the ground marker disappears under the receiver's body when the
        // meeting point is at his feet, and the aim point is the one thing the pass view must never hide.
        _painter.Cross(delivery.Target + Vector3.Up * 1.75f, 0.45f, 0.110f, Cyan);

        if (!withEtas) return;

        string eta = $"Ball: {FormatSeconds(delivery.BallEtaSeconds)}\nRecv: {FormatSeconds(delivery.ReceiverEtaSeconds)}";
        _labels.Add(delivery.Target + Vector3.Up * 2.0f, eta, Cyan, 15, priority: 100, plate: true);
    }

    private void DrawInitialVelocity(DeliverySnapshot delivery)
    {
        Vector3 velocity = delivery.InitialVelocity;
        float speed = new Vector2(velocity.X, velocity.Z).Length();
        if (speed < 0.25f) return;

        Vector3 direction = new(velocity.X / speed, 0f, velocity.Z / speed);
        float length = Mathf.Clamp(speed * 0.55f, 2f, 10f);
        Vector3 from = delivery.ContactBallPosition;
        Vector3 to = from + direction * length;

        _painter.DashedLine(from, to, 0.140f, WithAlpha(Yellow, 0.92f), 0.40f, 0.30f);
        Vector3 side = direction.Cross(Vector3.Up).Normalized() * 0.45f;
        _painter.Line(to, to - direction * 0.80f + side, 0.120f, WithAlpha(Yellow, 0.92f));
        _painter.Line(to, to - direction * 0.80f - side, 0.120f, WithAlpha(Yellow, 0.92f));
        // Sit the speed beside the arrow rather than on it: the white pass line and the corridor fill run
        // through the midpoint, and a label on top of them is unreadable at game resolution.
        Vector3 perpendicular = direction.Cross(Vector3.Up).Normalized() * 1.6f;
        _labels.Add(from.Lerp(to, 0.55f) + perpendicular + Vector3.Up * 0.8f, $"{speed:F1} m/s", Yellow,
            14, priority: 80, plate: true);
    }

    private void DrawRestingPoint(DeliverySnapshot delivery)
    {
        _painter.Ring(delivery.RestingPoint + Vector3.Up * 0.02f, 0.75f, 0.180f, WithAlpha(Magenta, 0.95f), 30);
        _painter.Disc(delivery.RestingPoint + Vector3.Up * 0.015f, 0.22f, WithAlpha(Magenta, 0.90f), 10);

        // The requested diagnostic stated as one number: how far the replayed ball stops from the aim point,
        // and on which side. A big "short" or "past" is a power/turf mismatch, which is exactly the question
        // the resting ring exists to answer. The number sits at the cyan X (always in view) and a magenta
        // dashed line joins it to the ring, so the two can be compared even when the ring is far away.
        var delta = new NVector2(delivery.RestingPoint.X - delivery.Target.X,
            delivery.RestingPoint.Z - delivery.Target.Z);
        float distance = delta.Length();
        if (distance < 0.05f) return;

        var passDirection = new NVector2(delivery.Target.X - delivery.ContactBallPosition.X,
            delivery.Target.Z - delivery.ContactBallPosition.Z);
        float along = passDirection.LengthSquared() > 1e-4f
            ? NVector2.Dot(delta, NVector2.Normalize(passDirection))
            : distance;
        // "past" can mean past the touchline: the live ball goes out of play there, so say that instead of
        // pretending the ball would keep rolling through the advertising boards.
        string text = delivery.WouldLeavePitch
            ? along < 0f
                ? $"would leave pitch, {distance:F1} m short"
                : $"would leave pitch, {distance:F1} m past"
            : along < 0f
                ? $"stopped {distance:F1} m short"
                : $"stopped {distance:F1} m past";

        if (distance > 1f)
            _painter.DashedLine(delivery.Target, delivery.RestingPoint, 0.100f,
                WithAlpha(Magenta, 0.65f), 0.55f, 0.45f);
        _labels.Add(delivery.Target + Vector3.Up * 0.6f, text, Magenta, 14, priority: 95, plate: true);
    }

    private void DrawReceiverVector(DeliverySnapshot delivery)
    {
        if (delivery.ReceiverId is null) return;

        // Dotted rather than long-dashed: the request distinguishes this from the velocity arrow by texture.
        _painter.DashedLine(delivery.ReceiverStart, delivery.Target, 0.160f,
            new Color(0.92f, 0.98f, 1f, 0.95f), 0.32f, 0.28f);
    }

    private void DrawTrapRadius(MatchSimulationEngine engine, DeliverySnapshot delivery)
    {
        if (delivery.Resolved || delivery.ReceiverId is not int receiverId) return;
        if (!_byId.TryGetValue(receiverId, out FootballerState? receiver) || !receiver.IsActiveOnPitch) return;

        bool receiving = BallContactRules.IsReceivingOrDribbling(
            engine.Ball.LastTouchedByTeam, engine.Ball.LastTouchedByPlayerId, receiver.Team, receiver.Id);
        float radius = BallContactRules.ReachRadius(
            receiver.Attributes.ReachRadius, keeperHandling: false, receiver.IsDiving,
            receiver.LastCommand.RequestTackle, receiving);

        _painter.WireCircle(Ground(receiver.Position, 0.060f), radius, 0.120f,
            WithAlpha(Cyan, 0.88f), spokes: 12);
    }

    private void DrawArrivalDelta(DeliverySnapshot delivery)
    {
        if (!delivery.Resolved) return;

        _painter.Line(delivery.Target, delivery.ReceiverEnd, 0.160f, WithAlpha(RejectColour, 0.95f));
        _painter.Disc(delivery.ReceiverEnd + Vector3.Up * 0.02f, 0.30f, WithAlpha(RejectColour, 0.92f), 12);
        Vector3 midpoint = (delivery.Target + delivery.ReceiverEnd) * 0.5f + Vector3.Up * 0.60f;
        _labels.Add(midpoint, $"Δ {delivery.ArrivalDeltaMeters:F1} m", RejectColour, 15, priority: 70,
            plate: true);
    }

    // ---------------------------------------------------------------------------------------------
    // Frozen decision lines and touch-rejection labels
    // ---------------------------------------------------------------------------------------------

    private void DrawDecision()
    {
        DecisionSnapshot? decision = _decision;
        if (decision is null) return;

        // While a pass is live the pass forensics own the pitch: the frozen option lines stay (they are the
        // context the ball was played from) but fade back, and their score labels step aside for the X/ETA
        // cluster. The panel still carries every decision number.
        bool passLive = _delivery is { Resolved: false };
        float lineAlpha = passLive ? 0.40f : 1f;

        for (int index = 0; index < decision.Options.Count; index++)
        {
            DecisionOption option = decision.Options[index];
            if (option.Target == Vector3.Zero) continue;

            Vector3 target = option.Target;
            if ((target - decision.Origin).LengthSquared() < 0.8f) continue;

            Color colour = option.Rejected ? RejectColour : option.Chosen ? ChosenColour : Yellow;
            float width = option.Chosen ? 0.180f : option.Rejected ? 0.130f : 0.110f;

            // Every option line carries a dark underlay: green/yellow/red over green grass washes out to
            // pale mint/cream/salmon at this camera distance, and a 2 px dark edge keeps the semantics.
            if (option.Rejected)
            {
                _painter.DashedLine(decision.Origin, target, width * 1.7f, new Color(0f, 0f, 0f, 0.50f),
                    0.45f, 0.35f);
                _painter.DashedLine(decision.Origin, target, width,
                    WithAlpha(colour, (option.Rejected ? 0.95f : 0.92f) * lineAlpha), 0.45f, 0.35f);
            }
            else
            {
                _painter.Line(decision.Origin, target, width * 1.7f, new Color(0f, 0f, 0f, 0.45f));
                _painter.Line(decision.Origin, target, width,
                    WithAlpha(colour, (option.Chosen ? 1f : 0.92f) * lineAlpha));
            }

            if (passLive) continue;

            // The score sits on its own line, like the request's hand-written example; a refusal prints the
            // reason and the man it refused so the line can be matched to the box's [X] row.
            string text = option.Rejected
                ? option.TargetPlayerId > 0
                    ? $"{option.Reason} {PlayerLabel(option.TargetPlayerId)}"
                    : option.Reason
                : option.Score.ToString("F2");
            Vector3 labelPosition = decision.Origin.Lerp(target, 0.45f) + Vector3.Up * 0.45f;
            _labels.Add(labelPosition, text, colour, option.Chosen ? 15 : 14,
                priority: option.Chosen ? 65 : 55, plate: true);
        }
    }

    private void DrawTouchRejections(MatchSimulationEngine engine)
    {
        int tick = engine.CurrentTick;
        foreach (FootballerState player in _activePlayers)
        {
            if (!_touchRejections.TryGet(player.Id, tick, out string reason)) continue;
            float lift = 2.85f + player.Id % 2 * 0.25f;
            _labels.Add(Ground(player.Position, lift), reason, RejectColour, 15, priority: 90, plate: true);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static string FormatSeconds(float seconds) =>
        float.IsFinite(seconds) && seconds >= 0f ? $"{seconds:F2}s" : "—";

    private static Color WithAlpha(Color colour, float alpha) => new(colour.R, colour.G, colour.B, alpha);

    private static Vector3 Ground(NVector3 position, float y) => new(position.X, y, position.Z);

    private static Vector3 Ground(NVector2 position, float y) => new(position.X, y, position.Y);

    private static Vector3 Ground(Vector2 position, float y) => new(position.X, y, position.Y);
}
