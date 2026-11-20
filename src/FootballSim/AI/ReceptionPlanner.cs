using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;

namespace FootballSim.AI;

/// <summary>
/// A7.3: the touch a receiver has decided to take before the ball arrives.
///
/// It is a <b>touch kind</b>, not a command: the plan names what the first touch is for and where the
/// body has to be when it happens. The command boundary (A7.4) is deliberately untouched — every field
/// here is expressed through the movement/orientation vocabulary the player already has.
/// </summary>
public enum ReceptionTouchKind
{
    /// <summary>No plan (the neutral default of a fixture or a non-receiver).</summary>
    None = 0,

    /// <summary>Kill it: meet the ball slowly and stop it, body already open toward the next action.</summary>
    Kill,

    /// <summary>Open the body: meet the ball on the half-turn so the first touch can go up the pitch.</summary>
    OpenBody,

    /// <summary>Stride touch: keep the run going and touch the ball into the space ahead.</summary>
    StrideTouch,

    /// <summary>Shield it: get the body between the ball and the believed presser.</summary>
    Shield,

    /// <summary>Escape pressure: touch the ball away from the believed presser into the larger space.</summary>
    EscapePressure
}

/// <summary>
/// A7.3: what the reception is preparing to do with the ball next. The orientation in
/// <see cref="ReceptionPlan"/> points at this action's target; "prepare pass/shot/cross" is exactly the
/// difference between two otherwise identical plans.
/// </summary>
public enum ReceptionNextAction
{
    /// <summary>No next action is worth orienting for from the arrival point.</summary>
    None = 0,

    /// <summary>Prepare a pass: the body opens toward the best-placed team-mate.</summary>
    Pass,

    /// <summary>Prepare a shot: the body opens toward the opponent goal.</summary>
    Shot,

    /// <summary>Prepare a cross: the body opens toward the box from a wide, advanced arrival.</summary>
    Cross
}

/// <summary>
/// A7.3: the reception plan — the answer to "which touch, facing where, meeting the ball at what point,
/// against what next action, and how sure am I?".
///
/// <b>What it is not.</b> It carries no opponent position and no live attribute. <see cref="Pressure01"/>
/// and <see cref="PressureUncertainty01"/> describe <i>this player's belief</i>, built from his own
/// <see cref="FootballerObservation"/> (sightings, memory ages, and A7.2 calls addressed to him). The
/// command the controller emits is still an ordinary <see cref="FootballerCommand"/>: execution decides
/// whether the body actually gets there, whether the ball is reachable, and what the first touch does.
/// An intent never guarantees control (the plan's A7.4 rule, honoured early here).
///
/// <b>"Cannot say" is a value.</b> <see cref="PressureKnown"/> false means the player has no evidence of
/// a presser at all — not that he believes the space is free. The planner records uncertainty instead of
/// substituting a mean pressure, and the options that assume knowledge of the presser are scored down,
/// not ruled out as if the space were known to be empty.
/// </summary>
public readonly record struct ReceptionPlan(
    ReceptionTouchKind TouchKind,
    ReceptionNextAction NextAction,
    Vector2 MeetingPointXZ,
    Vector2 FacingXZ,
    bool Sprint,
    bool Shield,
    float ArrivalSeconds,
    float Pressure01,
    float PressureUncertainty01,
    bool PressureKnown,
    float NextActionValue,
    float Score,
    int PlannedTick)
{
    /// <summary>The neutral plan: what a fixture that knows nothing about reception planning carries.</summary>
    public static ReceptionPlan None => default;

    /// <summary>True when a real touch was planned.</summary>
    public bool IsPlanned => TouchKind != ReceptionTouchKind.None;

    /// <summary>Compact text form for a decision trace, the inspector or a failure message.</summary>
    public string Describe() =>
        IsPlanned
            ? $"{TouchKind} -> {NextAction} @ ({MeetingPointXZ.X:F1},{MeetingPointXZ.Y:F1})" +
              $" facing ({FacingXZ.X:F2},{FacingXZ.Y:F2})" +
              (Sprint ? " sprint" : string.Empty) +
              (Shield ? " shield" : string.Empty) +
              $" arrive {ArrivalSeconds:F2}s pressure {Pressure01:F2}" +
              (PressureKnown ? string.Empty : " (unknown)") +
              $" +/-{PressureUncertainty01:F2} score {Score:F2}"
            : "no plan";
}

/// <summary>
/// One evaluated (orientation, touch) pair, feasible or refused, with the reason. A7.3's plan is the
/// best feasible pair; the whole evaluated set is exposed because "evaluate feasible orientation/touch
/// pairs" is only checkable if the refusals are visible rather than folded into the winner.
/// </summary>
public readonly record struct ReceptionOption(
    ReceptionTouchKind TouchKind,
    bool Feasible,
    float Score,
    Vector2 MeetingPointXZ,
    Vector2 FacingXZ,
    bool Sprint,
    bool Shield,
    string Reason)
{
    /// <summary>A pair that was evaluated and refused, with the stated reason.</summary>
    public static ReceptionOption Refused(ReceptionTouchKind kind, string reason) =>
        new(kind, false, 0f, default, default, false, false, reason);

    /// <summary>Compact text form for a failure message or a probe column.</summary>
    public string Describe() =>
        Feasible
            ? $"{TouchKind} feasible score {Score:F2} ({(Sprint ? "sprint" : "steady")}" +
              $"{(Shield ? ", shield" : string.Empty)})"
            : $"{TouchKind} refused: {Reason}";
}

/// <summary>
/// A7.3: plans the first touch before the ball arrives.
///
/// <b>Inputs, all from one observation.</b> The receiver must be the intended receiver of a pass in
/// flight (<see cref="FootballerObservation.IntendedReceiverId"/>, <see cref="FootballerObservation.BallControlState"/>).
/// The ball's predicted flight is the real solver rolled forward from the <i>perceived</i> ball state
/// (<see cref="BallTrajectoryEstimate"/>, the same estimate the keeper trusts), so reaction delay and ball
/// noise are inherited rather than bypassed. The presser is a <i>believed</i> opponent from
/// <see cref="FootballerObservation.VisiblePlayers"/>: a fresh sighting has no extrapolation uncertainty,
/// an aged one carries <see cref="PerceivedEntity.PositionUncertaintyMeters"/>, and an A7.2 man-on call
/// addressed to this player is evidence that pressure exists even when the sighting is stale. No
/// authoritative player position is read anywhere in this file.
///
/// <b>The evaluation.</b> Bounded (five kinds, no search) and deterministic: for each feasible
/// (body orientation, touch) pair the planner scores the next-action value of the meeting point
/// (<see cref="ReceptionValueModel"/>, the same reception value the passer priced), how much of the
/// needed body turn the arrival time allows, the pressure relief the touch buys, the space the touch
/// needs, and the uncertainty of the belief it leans on. The best feasible pair becomes the plan; the
/// whole set is retained for the trace/tests so a refusal is explained rather than invisible.
///
/// <b>The cost.</b> One trajectory build (32 solver steps at 15 Hz) and at most five reach solves plus
/// five reception-value evaluations per receiver decision, all allocation-free. That is the A9.3 budget
/// shape: bounded counts, reused buffers, no LINQ.
/// </summary>
public sealed class ReceptionPlanner
{
    // ---------------------------------------------------------------------------------------------
    // Bounds.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Pairs the planner evaluates per decision: one per <see cref="ReceptionTouchKind"/>.</summary>
    public const int MaxOptions = 5;

    /// <summary>
    /// How far ahead the flight is predicted, in seconds. The trajectory estimate's own ceiling is 32
    /// samples at 15 Hz (2.13 s); 2.0 s covers a long pass while staying inside it.
    /// </summary>
    public const float PlanHorizonSeconds = 2.0f;

    /// <summary>Slack a shifted meeting point may need over the ball's own arrival time, in seconds.</summary>
    public const float ArrivalSlackSeconds = 0.12f;

    // ---------------------------------------------------------------------------------------------
    // Believed pressure.
    // ---------------------------------------------------------------------------------------------

    /// <summary>An opponent this close to the meeting point counts as pressing it, in metres.</summary>
    public const float PressureRadiusMeters = 6f;

    /// <summary>Oldest sighting that still counts as fresh when judging how well the presser is known, in ticks.</summary>
    public const int FreshSightingTicks = 12;

    /// <summary>Weakest sighting confidence that still counts as a real sighting of the presser.</summary>
    public const float MinSightingConfidence01 = 0.5f;

    /// <summary>Positional uncertainty that saturates "how well do I know this presser", in metres.</summary>
    public const float UncertaintyReferenceMeters = 2.5f;

    /// <summary>Uncertainty about pressure when nobody is believed near the meeting point at all.</summary>
    public const float NoPressureEvidenceUncertainty01 = 0.15f;

    /// <summary>Age of the whole picture at which "nobody is near" stops meaning much, in ticks (0.5 s).</summary>
    public const int MemoryUncertaintyAgeTicks = 30;

    /// <summary>
    /// Pressure belief an A7.2 man-on call carries. A shout says "there is a man on you", not where he
    /// is, so it raises pressure and lowers the uncertainty of the <i>absence</i> of a sighting without
    /// pretending to be a sighting of the presser's position.
    /// </summary>
    public const float ManOnPressure01 = 0.80f;

    /// <summary>Uncertainty a man-on call leaves: the pressure is known, its direction is not.</summary>
    public const float ManOnUncertainty01 = 0.30f;

    // ---------------------------------------------------------------------------------------------
    // Geometry of each touch.
    // ---------------------------------------------------------------------------------------------

    /// <summary>How far to the side of the ball the body meets it to open up, in metres.</summary>
    public const float OpenBodyOffsetMeters = 1.1f;

    /// <summary>How far ahead of the meeting point a stride touch meets the ball, in metres.</summary>
    public const float StrideLeadMeters = 0.8f;

    /// <summary>How far a touch away from the presser carries the meeting point, in metres.</summary>
    public const float EscapeOffsetMeters = 1.3f;

    /// <summary>How much of the escape direction leans up the pitch instead of straight away, 0..1.</summary>
    public const float EscapeUpfieldBias = 0.75f;

    /// <summary>A stride touch needs this much room ahead of the touch point, in metres.</summary>
    public const float StrideClearanceMeters = 2.5f;

    /// <summary>An escape touch needs this much room in the escape direction, in metres.</summary>
    public const float EscapeClearanceMeters = 2.0f;

    /// <summary>Incoming pace at or below this makes a stride touch a clean one, in metres per second.</summary>
    public const float StrideSafeIncomingSpeed = 6f;

    /// <summary>Extra incoming pace over the safe figure at which the stride penalty saturates, in m/s.</summary>
    public const float StrideIncomingSpeedSpan = 6f;

    /// <summary>Believed room at which the space bonus of a stride/escape touch saturates, in metres.</summary>
    public const float SpaceReferenceMeters = 5f;

    /// <summary>
    /// Longest the believed presser's motion is projected forward, in metres. A pure velocity
    /// extrapolation across a two-second flight would be a second prediction model; this bound keeps the
    /// projection a local closing-motion estimate.
    /// </summary>
    public const float MaxPresserProjectionMeters = 8f;

    // ---------------------------------------------------------------------------------------------
    // Next action.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Shooting chance at which the reception prepares a shot rather than a pass, in probability.</summary>
    public const float ShotChanceThreshold = 0.06f;

    /// <summary>Attack-relative projection from which a wide arrival counts as a crossing position, in metres.</summary>
    public const float CrossMinProjMeters = 25f;

    /// <summary>Lateral distance from the centre line at which an arrival is wide enough to cross from, in metres.</summary>
    public const float CrossMinAbsZMeters = 12f;

    /// <summary>Attack-relative depth of the box a cross is aimed at, in metres from the goal line.</summary>
    public const float CrossTargetDepthMeters = 10f;

    /// <summary>Half-width of the box region a cross target/team-mate may occupy, in metres.</summary>
    public const float CrossBoxHalfWidthMeters = 14f;

    /// <summary>Nearest-team-mate support at which preparing a pass is worth it, 0..1.</summary>
    public const float NextOptionMinimum01 = 0.05f;

    // ---------------------------------------------------------------------------------------------
    // Scoring. Hand-set calibration, like every other A7 threshold: A9.2's ablations own the fit.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Weight of the reception value at the plan's meeting point.</summary>
    public const float ValueWeight = 0.55f;

    /// <summary>Weight of the pressure relief a shield/escape touch buys.</summary>
    public const float PressureReliefWeight = 0.40f;

    /// <summary>Weight of being able to face the next action by the time the ball arrives.</summary>
    public const float OrientationWeight = 0.12f;

    /// <summary>Weight of the room a stride/escape touch needs.</summary>
    public const float SpaceWeight = 0.10f;

    /// <summary>Penalty weight of relying on an uncertain belief.</summary>
    public const float UncertaintyWeight = 0.40f;

    /// <summary>Penalty weight of a stride touch on a fast incoming ball.</summary>
    public const float StridePacePenaltyWeight = 0.10f;

    /// <summary>How much of the uncertainty the pressure relief itself loses at full uncertainty.</summary>
    public const float ReliefUncertaintyDiscount = 0.5f;

    /// <summary>Smallest fraction of the required body turn the arrival time must allow to open up.</summary>
    public const float MinOpenBodyOrientation01 = 0.5f;

    private const string ReasonUnreachable = "unreachable in time";
    private const string ReasonCannotOpen = "cannot open the body by arrival";
    private const string ReasonStrideBlocked = "no room ahead for a stride touch";
    private const string ReasonEscapeBlocked = "escape lane blocked";
    private const string ReasonNotFinite = "non-finite score";

    private struct Draft
    {
        public ReceptionTouchKind Kind;
        public bool Feasible;
        public float Score;
        public Vector2 MeetingPointXZ;
        public Vector2 FacingXZ;
        public bool Sprint;
        public bool Shield;
        public float NextActionValue;
        public string Reason;
    }

    private readonly PitchDimensions _pitch;
    private readonly ReceptionValueModel _receptionValue;
    private readonly BallTrajectoryEstimate _trajectory;

    private readonly Draft[] _drafts = new Draft[MaxOptions];
    private readonly ReceptionPlan[] _plans = new ReceptionPlan[MaxOptions];
    private int _optionCount;

    // The common half of every option this decision: what the ball does and what the receiver believes.
    private float _arrivalSeconds;
    private float _pressure;
    private float _uncertainty;
    private bool _pressureKnown;
    private ReceptionNextAction _nextAction;

    public ReceptionPlanner(PitchDimensions pitch)
    {
        _pitch = pitch ?? throw new ArgumentNullException(nameof(pitch));
        _receptionValue = new ReceptionValueModel(pitch);
        _trajectory = new BallTrajectoryEstimate(pitch: pitch);
    }

    /// <summary>Pairs evaluated on the last <see cref="TryPlan"/> call (feasible or refused).</summary>
    public int OptionCount => _optionCount;

    /// <summary>One evaluated pair from the last decision.</summary>
    public ReceptionOption GetOption(int index) => new(
        _drafts[index].Kind,
        _drafts[index].Feasible,
        _drafts[index].Score,
        _drafts[index].MeetingPointXZ,
        _drafts[index].FacingXZ,
        _drafts[index].Sprint,
        _drafts[index].Shield,
        _drafts[index].Reason);

    /// <summary>The full plan of one evaluated pair (the best pair is the <see cref="TryPlan"/> output).</summary>
    public ReceptionPlan PlanForOption(int index) => _plans[index];

    /// <summary>The plan's stable candidate tag, used by the candidate identity and the trace.</summary>
    public static string TagOf(ReceptionTouchKind kind) => kind switch
    {
        ReceptionTouchKind.Kill => "kill",
        ReceptionTouchKind.OpenBody => "open-body",
        ReceptionTouchKind.StrideTouch => "stride",
        ReceptionTouchKind.Shield => "shield",
        ReceptionTouchKind.EscapePressure => "escape",
        _ => "none"
    };

    /// <summary>A human-readable action name for a planned touch.</summary>
    public static string NameOf(ReceptionTouchKind kind) => kind switch
    {
        ReceptionTouchKind.Kill => "ReceiveKill",
        ReceptionTouchKind.OpenBody => "ReceiveOpenBody",
        ReceptionTouchKind.StrideTouch => "ReceiveStride",
        ReceptionTouchKind.Shield => "ReceiveShield",
        ReceptionTouchKind.EscapePressure => "ReceiveEscape",
        _ => "ReceiveNone"
    };

    /// <summary>
    /// Plans the first touch for the intended receiver of a pass in flight. Returns false — leaving the
    /// caller to its pre-A7.3 intercept chase — when this player is not the intended receiver, when the
    /// flight cannot be predicted, when the ball is never meetable at body height inside the horizon, or
    /// when no pair is feasible. False is a statement, not an error: "cannot plan" must stay visible.
    /// </summary>
    public bool TryPlan(in FootballerObservation obs, out ReceptionPlan plan)
    {
        plan = ReceptionPlan.None;
        _optionCount = 0;

        if (obs.BallControlState != BallControlState.InFlightPass) return false;
        if (obs.PossessingTeam != obs.SelfTeam) return false;
        if (obs.IntendedReceiverId != obs.SelfId) return false;

        _trajectory.Invalidate();
        if (!_trajectory.TryBuild(obs.PerceivedBallPosition, obs.PerceivedBallVelocity, PlanHorizonSeconds))
            return false;

        ReachKinematics reach = ReachKinematics.FromObservation(obs);

        // The receiver can only plan a touch he could physically take: a ball above torso height at the
        // meeting sample is a header/volley the command vocabulary does not describe, so it is left to
        // the legacy chase (the known height-aware gate belongs to this milestone, and it is enforced
        // where the candidate set is chosen).
        float maxHeight = BallContactRules.MaxContactHeight(keeperHandling: false, jumpingOrHeading: false);
        if (!_trajectory.TryMeet(reach, PlanHorizonSeconds, maxHeight,
                out float meetSeconds, out Vector3 meetPoint, out int meetIndex))
            return false;

        Vector2 meetXZ = new(meetPoint.X, meetPoint.Z);
        Vector3 ballVelocityAtMeet = _trajectory.VelocityAt(meetIndex);
        float incomingSpeed = new Vector2(ballVelocityAtMeet.X, ballVelocityAtMeet.Z).Length();

        bool hasPresser = TryNearestOpponent(obs, meetXZ, meetSeconds,
            out Vector2 presserAtArrivalXZ, out float presserDistance, out PerceivedEntity presser);
        float pressure = hasPresser ? Clamp01(1f - presserDistance / PressureRadiusMeters) : 0f;
        float uncertainty = hasPresser ? SightingUncertainty(presser) : MemoryUncertainty(obs);

        // A7.2's reception-side consumer: a call addressed to me says somebody is on me. It cannot say
        // where from (no position in the payload), so it moves the belief in pressure, never a sighting.
        bool heardManOn = obs.Calls.Kind == TeamCallKind.ManOn;
        bool pressureKnown = hasPresser;
        if (heardManOn)
        {
            pressure = MathF.Max(pressure, ManOnPressure01);
            uncertainty = MathF.Min(uncertainty, ManOnUncertainty01);
            pressureKnown = true;
        }

        // The direction the pressure comes from, where the body has to be when the ball arrives. Without
        // a sighting the player assumes it comes from his own goal side — a belief, not a read: a shout
        // tells him he is pressed, not where from.
        Vector2 pressureFromXZ = hasPresser
            ? presserAtArrivalXZ
            : new Vector2(-obs.AttackingDirectionX * _pitch.HalfLength, meetXZ.Y);

        ReceptionValueTerms terms = _receptionValue.Evaluate(obs, obs.SelfId, meetXZ, pressure);
        _nextAction = ChooseNextAction(obs, meetXZ, terms, out Vector2 nextActionTargetXZ);
        Vector2 actionFacing = SafeNormalize(nextActionTargetXZ - meetXZ, AttackVector(obs));
        Vector2 attackXZ = AttackVector(obs);

        _arrivalSeconds = meetSeconds;
        _pressure = pressure;
        _uncertainty = uncertainty;
        _pressureKnown = pressureKnown;

        // 1. Kill: meet it dead, body already open toward the next action. The most robust touch: it
        //    assumes nothing about the presser's position, so uncertainty barely touches it.
        AddOption(obs, reach, meetSeconds, pressure, uncertainty, pressureKnown,
            ReceptionTouchKind.Kill, meetXZ, actionFacing, sprint: false, shield: false, space01: 0f, pace01: 0f);

        // 2. Open body: half a body to the side of the ball, turned toward the next action. The side is
        //    the one away from the believed presser, so opening up does not walk into him.
        Vector2 actionPerp = new(-actionFacing.Y, actionFacing.X);
        float openSide = hasPresser && Vector2.Dot(actionPerp, pressureFromXZ - meetXZ) > 0f ? -1f : 1f;
        AddOption(obs, reach, meetSeconds, pressure, uncertainty, pressureKnown,
            ReceptionTouchKind.OpenBody, meetXZ + actionPerp * (OpenBodyOffsetMeters * openSide), actionFacing,
            sprint: false, shield: false, space01: 0f, pace01: 0f);

        // 3. Stride touch: keep running and touch the ball into the space ahead of the run.
        Vector2 runXZ = SafeNormalize(new Vector2(obs.SelfVelocity.X, obs.SelfVelocity.Z), actionFacing);
        Vector2 stridePoint = meetXZ + runXZ * StrideLeadMeters;
        float strideClearance = NearestOpponentDistance(obs, stridePoint + runXZ * StrideLeadMeters, out _);
        float strideSpace01 = Clearance01(strideClearance, SpaceReferenceMeters);
        float stridePace01 = Clamp01((incomingSpeed - StrideSafeIncomingSpeed) / StrideIncomingSpeedSpan);
        if (strideClearance < StrideClearanceMeters)
        {
            AddRefused(ReceptionTouchKind.StrideTouch, ReasonStrideBlocked);
        }
        else
        {
            AddOption(obs, reach, meetSeconds, pressure, uncertainty, pressureKnown,
                ReceptionTouchKind.StrideTouch, stridePoint, runXZ, sprint: true, shield: false,
                space01: strideSpace01, pace01: stridePace01);
        }

        // 4. Shield: meet it with the body on the presser's side and shield the first touch. When only a
        //    call exists the body still goes between the ball and the goal side — that is what the shout
        //    can justify; it is scored down by the call's own direction uncertainty.
        Vector2 awayFromPressure = SafeNormalize(meetXZ - pressureFromXZ, attackXZ);
        Vector2 shieldFacing = hasPresser ? awayFromPressure : SafeNormalize(awayFromPressure, attackXZ);
        AddOption(obs, reach, meetSeconds, pressure, uncertainty, pressureKnown,
            ReceptionTouchKind.Shield, meetXZ, shieldFacing, sprint: false, shield: true, space01: 0f, pace01: 0f);

        // 5. Escape pressure: touch it away from the presser, leaning up the pitch, into the larger space.
        Vector2 escapeDir = SafeNormalize(awayFromPressure + attackXZ * EscapeUpfieldBias, awayFromPressure);
        Vector2 escapePoint = meetXZ + escapeDir * EscapeOffsetMeters;
        float escapeClearance = NearestOpponentDistance(obs, escapePoint, out _);
        float escapeSpace01 = Clearance01(escapeClearance, SpaceReferenceMeters);
        if (escapeClearance < EscapeClearanceMeters)
        {
            AddRefused(ReceptionTouchKind.EscapePressure, ReasonEscapeBlocked);
        }
        else
        {
            AddOption(obs, reach, meetSeconds, pressure, uncertainty, pressureKnown,
                ReceptionTouchKind.EscapePressure, escapePoint, escapeDir, sprint: true, shield: false,
                space01: escapeSpace01, pace01: 0f);
        }

        int best = -1;
        float bestScore = float.NegativeInfinity;
        for (int i = 0; i < _optionCount; i++)
        {
            if (!_drafts[i].Feasible) continue;
            if (_drafts[i].Score <= bestScore) continue;
            best = i;
            bestScore = _drafts[i].Score;
        }

        if (best < 0) return false;

        BuildPlans(meetSeconds, pressure, uncertainty, pressureKnown, obs.Tick);
        plan = _plans[best];
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // One evaluated pair.
    // ---------------------------------------------------------------------------------------------

    private void AddOption(
        in FootballerObservation obs,
        in ReachKinematics reach,
        float ballArrivalSeconds,
        float pressure,
        float uncertainty,
        bool pressureKnown,
        ReceptionTouchKind kind,
        Vector2 meetingPointXZ,
        Vector2 facingXZ,
        bool sprint,
        bool shield,
        float space01,
        float pace01)
    {
        meetingPointXZ = ClampToPitch(meetingPointXZ);
        facingXZ = SafeNormalize(facingXZ, AttackVector(obs));

        // Feasible means the body can physically be at the meeting point by the time the ball is there:
        // one reach solve against the same arrival clock the trajectory just gave us.
        ReachEstimate arrival = TimeToReachModel.ToPoint(reach, meetingPointXZ, PlanHorizonSeconds);
        if (!arrival.Reachable || arrival.Seconds > ballArrivalSeconds + ArrivalSlackSeconds)
        {
            AddRefused(kind, ReasonUnreachable);
            return;
        }

        float orientation01 = OrientationFeasibility01(obs, facingXZ, ballArrivalSeconds);
        if (kind == ReceptionTouchKind.OpenBody && orientation01 < MinOpenBodyOrientation01)
        {
            AddRefused(kind, ReasonCannotOpen);
            return;
        }

        ReceptionValueTerms terms = _receptionValue.Evaluate(obs, obs.SelfId, meetingPointXZ, pressure);
        float normalizedValue = _receptionValue.MaxValue > 1e-4f ? terms.Value / _receptionValue.MaxValue : 0f;

        bool reliefKind = kind is ReceptionTouchKind.Shield or ReceptionTouchKind.EscapePressure;
        bool spaceKind = kind is ReceptionTouchKind.StrideTouch or ReceptionTouchKind.EscapePressure;

        float score = ValueWeight * normalizedValue
                      + OrientationWeight * orientation01
                      + KindBias(kind)
                      + (spaceKind ? SpaceWeight * space01 : 0f)
                      + (reliefKind && pressureKnown
                          ? PressureReliefWeight * pressure * (1f - ReliefUncertaintyDiscount * uncertainty)
                          : 0f)
                      - UncertaintyWeight * uncertainty * UncertaintySensitivity(kind)
                      - (kind == ReceptionTouchKind.StrideTouch ? StridePacePenaltyWeight * pace01 : 0f);

        if (!float.IsFinite(score))
        {
            AddRefused(kind, ReasonNotFinite);
            return;
        }

        _drafts[_optionCount] = new Draft
        {
            Kind = kind,
            Feasible = true,
            Score = Clamp01(score),
            MeetingPointXZ = meetingPointXZ,
            FacingXZ = facingXZ,
            Sprint = sprint,
            Shield = shield,
            NextActionValue = terms.Value,
            Reason = string.Empty
        };
        _optionCount++;
    }

    private void AddRefused(ReceptionTouchKind kind, string reason)
    {
        _drafts[_optionCount] = new Draft
        {
            Kind = kind,
            Feasible = false,
            Score = 0f,
            Reason = reason
        };
        _optionCount++;
    }

    private void BuildPlans(float arrivalSeconds, float pressure, float uncertainty, bool pressureKnown, int tick)
    {
        for (int i = 0; i < _optionCount; i++)
        {
            Draft draft = _drafts[i];
            _plans[i] = draft.Feasible
                ? new ReceptionPlan(
                    draft.Kind, _nextAction, draft.MeetingPointXZ, draft.FacingXZ,
                    draft.Sprint, draft.Shield, arrivalSeconds, pressure, uncertainty, pressureKnown,
                    draft.NextActionValue, draft.Score, tick)
                : ReceptionPlan.None;
        }
    }

    /// <summary>
    /// Preference for each touch kind before value and pressure are counted. Kill is the safest default
    /// (it needs nothing but the ball); stride is a run continuation; shield and escape earn their place
    /// through the pressure relief above, so their base is deliberately low.
    /// </summary>
    private static float KindBias(ReceptionTouchKind kind) => kind switch
    {
        ReceptionTouchKind.Kill => 0.06f,
        ReceptionTouchKind.OpenBody => 0.05f,
        ReceptionTouchKind.StrideTouch => 0.03f,
        ReceptionTouchKind.Shield => 0.02f,
        ReceptionTouchKind.EscapePressure => 0.01f,
        _ => 0f
    };

    /// <summary>
    /// How much a touch suffers when the belief it leans on is uncertain. Kill assumes nothing (0.15);
    /// open body assumes the side away from pressure (0.5); a stride touch assumes the space ahead is
    /// really empty and shield/escape assume the presser's position (1.0).
    /// </summary>
    private static float UncertaintySensitivity(ReceptionTouchKind kind) => kind switch
    {
        ReceptionTouchKind.Kill => 0.15f,
        ReceptionTouchKind.OpenBody => 0.5f,
        ReceptionTouchKind.StrideTouch => 1.0f,
        ReceptionTouchKind.Shield => 1.0f,
        ReceptionTouchKind.EscapePressure => 1.0f,
        _ => 0f
    };

    // ---------------------------------------------------------------------------------------------
    // Belief reads.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The nearest believed opponent <i>at the moment the ball arrives</i>, and where he is believed to
    /// be then. A presser closing from behind is a real reception risk even while he is five metres away
    /// now, so the belief's own velocity is projected forward — bounded, and never a live read: this loop
    /// only touches <see cref="FootballerObservation.VisiblePlayers"/>.
    /// </summary>
    private static bool TryNearestOpponent(
        in FootballerObservation obs,
        Vector2 pointXZ,
        float arrivalSeconds,
        out Vector2 opponentAtArrivalXZ,
        out float distance,
        out PerceivedEntity opponent)
    {
        opponentAtArrivalXZ = default;
        distance = float.MaxValue;
        opponent = default;
        bool found = false;
        float horizon = MathF.Max(0f, arrivalSeconds);

        for (int i = 0; i < obs.VisiblePlayers.Count; i++)
        {
            PerceivedEntity candidate = obs.VisiblePlayers[i];
            if (candidate.Team == obs.SelfTeam) continue;

            Vector2 candidateXZ = new(candidate.EstimatedPosition.X, candidate.EstimatedPosition.Z);
            Vector2 velocityXZ = new(candidate.EstimatedVelocity.X, candidate.EstimatedVelocity.Z);
            Vector2 projected = candidateXZ + velocityXZ * horizon;
            Vector2 offset = projected - candidateXZ;
            float offsetLength = offset.Length();
            if (offsetLength > MaxPresserProjectionMeters)
                projected = candidateXZ + offset * (MaxPresserProjectionMeters / offsetLength);

            float candidateDistance = Vector2.Distance(projected, pointXZ);
            if (found && candidateDistance >= distance) continue;

            found = true;
            distance = candidateDistance;
            opponentAtArrivalXZ = projected;
            opponent = candidate;
        }

        return found;
    }

    /// <summary>Nearest believed-opponent distance to a point, or +infinity when none is believed.</summary>
    private static float NearestOpponentDistance(in FootballerObservation obs, Vector2 pointXZ, out int playerId)
    {
        float nearest = float.MaxValue;
        playerId = 0;

        for (int i = 0; i < obs.VisiblePlayers.Count; i++)
        {
            PerceivedEntity candidate = obs.VisiblePlayers[i];
            if (candidate.Team == obs.SelfTeam) continue;

            Vector2 candidateXZ = new(candidate.EstimatedPosition.X, candidate.EstimatedPosition.Z);
            float candidateDistance = Vector2.Distance(candidateXZ, pointXZ);
            if (candidateDistance >= nearest) continue;

            nearest = candidateDistance;
            playerId = candidate.PlayerId;
        }

        return nearest;
    }

    private static float SightingUncertainty(in PerceivedEntity presser)
    {
        if (presser.ObservedAgeTicks <= FreshSightingTicks && presser.Confidence01 >= MinSightingConfidence01)
        {
            // A fresh sighting still carries the distance-based confidence of the picture.
            return Clamp01((1f - presser.Confidence01) * 0.5f);
        }

        // An aged belief: the A2.2 memory's own growing positional uncertainty is the honest number.
        return MathF.Max(0.5f, Clamp01(presser.PositionUncertaintyMeters / UncertaintyReferenceMeters));
    }

    private static float MemoryUncertainty(in FootballerObservation obs)
    {
        float age = Clamp01(obs.KnowledgeAgeTicks / (float)MemoryUncertaintyAgeTicks);
        return NoPressureEvidenceUncertainty01
               + (1f - NoPressureEvidenceUncertainty01) * age;
    }

    /// <summary>How much of the body turn the arrival time allows, 0 (not at all) to 1 (done already).</summary>
    private static float OrientationFeasibility01(in FootballerObservation obs, Vector2 desiredFacingXZ, float seconds)
    {
        Vector2 current = SafeNormalize(obs.SelfFacingXZ, Vector2.UnitX);
        float dot = Math.Clamp(Vector2.Dot(current, desiredFacingXZ), -1f, 1f);
        float angle = MathF.Acos(dot);
        if (angle < 1e-3f) return 1f;

        float speed = new Vector2(obs.SelfVelocity.X, obs.SelfVelocity.Z).Length();
        float speedRatio = obs.SelfAttributes.MaxSprintSpeed > 1e-3f
            ? Math.Clamp(speed / obs.SelfAttributes.MaxSprintSpeed, 0f, 1f)
            : 0f;
        float available = obs.SelfAttributes.AgilityTurnRate * 1.25f * (1f - 0.25f * speedRatio) * MathF.Max(0f, seconds);
        if (available <= 1e-4f) return 0f;
        return Clamp01(available / angle);
    }

    // ---------------------------------------------------------------------------------------------
    // Next action.
    // ---------------------------------------------------------------------------------------------

    private ReceptionNextAction ChooseNextAction(
        in FootballerObservation obs,
        Vector2 meetingPointXZ,
        in ReceptionValueTerms terms,
        out Vector2 targetXZ)
    {
        Vector2 opponentGoalXZ = new(obs.AttackingDirectionX * _pitch.HalfLength, 0f);

        if (terms.ShootingChance01 >= ShotChanceThreshold)
        {
            targetXZ = opponentGoalXZ;
            return ReceptionNextAction.Shot;
        }

        float projection = meetingPointXZ.X * obs.AttackingDirectionX;
        if (projection >= CrossMinProjMeters &&
            MathF.Abs(meetingPointXZ.Y) >= CrossMinAbsZMeters &&
            TryFindBoxMate(obs, out Vector2 boxMateXZ))
        {
            targetXZ = boxMateXZ;
            return ReceptionNextAction.Cross;
        }

        if (terms.NextOptions01 >= NextOptionMinimum01 && TryFindForwardMate(obs, out Vector2 mateXZ))
        {
            targetXZ = mateXZ;
            return ReceptionNextAction.Pass;
        }

        targetXZ = opponentGoalXZ;
        return ReceptionNextAction.None;
    }

    /// <summary>
    /// The deepest team-mate inside the opponent's box: the cross target. "Deepest" rather than nearest
    /// to a chosen point because the question is where the delivery should go, not how far the pass is.
    /// </summary>
    private bool TryFindBoxMate(in FootballerObservation obs, out Vector2 mateXZ)
    {
        mateXZ = default;
        float attackDir = obs.AttackingDirectionX;
        float boxDepth = _pitch.HalfLength - CrossTargetDepthMeters;
        float bestProjection = float.MinValue;

        for (int i = 0; i < obs.VisiblePlayers.Count; i++)
        {
            PerceivedEntity mate = obs.VisiblePlayers[i];
            if (mate.Team != obs.SelfTeam || mate.PlayerId == obs.SelfId) continue;

            float projection = mate.EstimatedPosition.X * attackDir;
            if (projection < boxDepth) continue;
            if (MathF.Abs(mate.EstimatedPosition.Z) > CrossBoxHalfWidthMeters) continue;
            if (projection <= bestProjection) continue;

            bestProjection = projection;
            mateXZ = new Vector2(mate.EstimatedPosition.X, mate.EstimatedPosition.Z);
        }

        return bestProjection > float.MinValue;
    }

    /// <summary>
    /// The most advanced visible team-mate: what a prepared pass should point at. Choosing the deepest
    /// option (rather than the nearest) is what makes "prepare pass" open the body up the pitch instead
    /// of squaring it to whoever happens to stand beside the receiver.
    /// </summary>
    private bool TryFindForwardMate(in FootballerObservation obs, out Vector2 mateXZ)
    {
        mateXZ = default;
        float attackDir = obs.AttackingDirectionX;
        float bestProjection = float.MinValue;

        for (int i = 0; i < obs.VisiblePlayers.Count; i++)
        {
            PerceivedEntity mate = obs.VisiblePlayers[i];
            if (mate.Team != obs.SelfTeam || mate.PlayerId == obs.SelfId) continue;
            if (mate.Role == TacticalRole.Goalkeeper) continue;

            float projection = mate.EstimatedPosition.X * attackDir;
            if (projection <= bestProjection) continue;

            bestProjection = projection;
            mateXZ = new Vector2(mate.EstimatedPosition.X, mate.EstimatedPosition.Z);
        }

        return bestProjection > float.MinValue;
    }

    // ---------------------------------------------------------------------------------------------
    // Small helpers.
    // ---------------------------------------------------------------------------------------------

    private Vector2 ClampToPitch(Vector2 point) => new(
        Math.Clamp(point.X, -_pitch.HalfLength + 1f, _pitch.HalfLength - 1f),
        Math.Clamp(point.Y, -_pitch.HalfWidth + 1f, _pitch.HalfWidth - 1f));

    private static Vector2 AttackVector(in FootballerObservation obs) => new(obs.AttackingDirectionX, 0f);

    private static Vector2 SafeNormalize(Vector2 value, Vector2 fallback)
    {
        float length = value.Length();
        return length > 1e-4f && float.IsFinite(length) ? value / length : fallback;
    }

    private static float Clearance01(float distance, float reference) =>
        distance == float.MaxValue || !float.IsFinite(distance) ? 1f : Clamp01(distance / reference);

    private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;
}
