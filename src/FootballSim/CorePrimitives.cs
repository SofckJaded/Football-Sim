using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Tactics;

namespace FootballSim.Core;

public static class SimConstants
{
    public const float FixedDeltaTime = 1.0f / 60.0f;
    public const int PhysicsSubsteps = 2;
    public const float Gravity = 9.81f;
}

/// <summary>
/// Small numeric helpers shared by controllers, locomotion and physics.
/// Audit F09: bare <see cref="Vector2.Normalize(Vector2)"/> / <see cref="Vector3.Normalize(Vector3)"/>
/// emit NaN for a zero-length vector, and nothing in the pipeline rejected non-finite components.
/// </summary>
public static class SimMath
{
    private const float NormalizeEpsilonSquared = 1e-12f;

    public static bool IsFinite(in Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);

    public static bool IsFinite(in Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>Normalizes <paramref name="v"/>, returning <paramref name="fallback"/> for
    /// zero-length, near-zero or non-finite input instead of producing NaN.</summary>
    public static Vector2 SafeNormalize(in Vector2 v, in Vector2 fallback)
    {
        float lenSq = v.LengthSquared();
        if (!float.IsFinite(lenSq) || lenSq <= NormalizeEpsilonSquared) return fallback;
        Vector2 result = v * (1f / MathF.Sqrt(lenSq));
        return IsFinite(result) ? result : fallback;
    }

    /// <summary>3D counterpart of <see cref="SafeNormalize(in Vector2, in Vector2)"/>.</summary>
    public static Vector3 SafeNormalize(in Vector3 v, in Vector3 fallback)
    {
        float lenSq = v.LengthSquared();
        if (!float.IsFinite(lenSq) || lenSq <= NormalizeEpsilonSquared) return fallback;
        Vector3 result = v * (1f / MathF.Sqrt(lenSq));
        return IsFinite(result) ? result : fallback;
    }
}

public sealed class PitchDimensions
{
    public float Length { get; init; } = 105.0f;
    public float Width { get; init; } = 68.0f;
    public float GoalWidth { get; init; } = 7.32f;
    public float GoalHeight { get; init; } = 2.44f;
    public float GoalDepth { get; init; } = 2.0f;
    public float GoalPostRadius { get; init; } = 0.06f;
    public float PenaltyAreaLength { get; init; } = 16.5f;
    public float PenaltyAreaWidth { get; init; } = 40.32f;
    public float GoalAreaLength { get; init; } = 5.5f;
    public float GoalAreaWidth { get; init; } = 18.32f;
    public float PenaltySpotDistance { get; init; } = 11.0f;
    public float CenterCircleRadius { get; init; } = 9.15f;

    public float HalfLength => Length * 0.5f;
    public float HalfWidth => Width * 0.5f;
    public float HalfGoalWidth => GoalWidth * 0.5f;
}

public sealed class SeededRandom
{
    private ulong _state;

    public SeededRandom(ulong seed)
    {
        _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
    }

    public ulong State => _state;

    public uint NextUInt()
    {
        ulong x = _state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _state = x;
        return (uint)((x * 0x2545F4914F6CDD1DUL) >> 32);
    }

    public float NextFloat() => (NextUInt() & 0x00FFFFFF) / (float)0x01000000;

    public float NextRange(float min, float max) => min + (max - min) * NextFloat();

    public float NextGaussian(float mean = 0f, float stdDev = 1f)
    {
        float u1 = MathF.Max(1e-6f, NextFloat());
        float u2 = NextFloat();
        float mag = MathF.Sqrt(-2.0f * MathF.Log(u1));
        float z0 = mag * MathF.Cos(2.0f * MathF.PI * u2);
        return mean + z0 * stdDev;
    }
}

/// <summary>
/// Independent random consumers in one match. One shared <see cref="SeededRandom"/> used to mean that
/// adding a perception query (or a diagnostic probe that observed the ball) shifted every subsequent
/// physics sample: two features could never be developed without invalidating each other's replays.
/// </summary>
public enum RandomStream
{
    /// <summary>
    /// Physical execution: contact arbitration, first touch, tackles, keeper saves. This is the stream a
    /// command-only replay must reproduce, so nothing else may draw from it.
    /// </summary>
    Execution = 0,

    /// <summary>Perception: observation noise, later scanning uncertainty and attention jitter.</summary>
    Perception = 1,

    /// <summary>
    /// Decision uncertainty: reservation or intent-conversion noise. Reserved now so that the derived
    /// seeds of the existing streams never have to move when it starts being used.
    /// </summary>
    Decision = 2
}

/// <summary>
/// Derives a match-local random stream from the match seed.
///
/// The mixer is SplitMix64's finalizer over <c>seed + golden-ratio * (ordinate + 1)</c>. It only has to
/// satisfy two properties, and both are tested: the same (seed, ordinate) always yields the same value,
/// and different ordinates yield different streams. It is deliberately not cryptographic — a football
/// match's replay identity is versioned, not secret.
/// </summary>
public static class RandomStreams
{
    /// <summary>Golden-ratio increment used to separate stream ordinates before mixing.</summary>
    private const ulong OrdinateStride = 0x9E3779B97F4A7C15UL;

    /// <summary>Creates the stream for <paramref name="stream"/> under <paramref name="matchSeed"/>.</summary>
    public static SeededRandom Create(ulong matchSeed, RandomStream stream) =>
        new(Derive(matchSeed, stream));

    /// <summary>
    /// The seed <paramref name="stream"/> would start from. Exposed because diagnostics record it, and
    /// because a replay tool has to be able to rebuild one stream without rebuilding the match.
    /// </summary>
    public static ulong Derive(ulong matchSeed, RandomStream stream)
    {
        ulong z = matchSeed + OrdinateStride * ((ulong)stream + 1UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return z == 0UL ? OrdinateStride : z;
    }
}

public enum TeamId
{
    Home = 0,
    Away = 1
}

/// <summary>
/// Behavioural version of the simulation. Two builds with different values are not required to produce
/// identical trajectories, so every recorded artifact states it.
///
/// History:
/// <list type="bullet">
/// <item><c>AiBehaviour 1</c> — everything up to and including A0: one shared random stream.</item>
/// <item><c>AiBehaviour 2</c> — A1.3: perception and execution draw from separate derived streams, so
/// trajectories differ from version 1 by construction.</item>
/// <item><c>AiBehaviour 3</c> — A1.1c: commitment is matched on a stable candidate key
/// (<see cref="CandidateKey"/>) instead of the action label. The reference 11v11 match is byte-identical
/// to version 2, so the version bump records a semantic change rather than an observed behaviour change.</item>
/// <item><c>AiBehaviour 4</c> — A1.5/A1.2: commitment, tackle cooldown and the held intent are dropped at
/// every discontinuity (restart, half time, full time, removal, training reset, controller replacement),
/// and a controller-owned intent executor exists behind an off-by-default scheduler. Trajectories differ
/// from version 3 because a commitment no longer survives a restart.</item>
/// <item><c>AiBehaviour 5</c> — A2.1: one reach model (<see cref="AI.TimeToReachModel"/>) replaces the
/// four ad-hoc time-to-reach estimates in the responder election, the ball interception target, the
/// carrier pursuit point and the goalkeeper sweep. It uses current velocity, turn cost, acceleration,
/// fatigue, recovery balance and contact reach, and the responder election now scores candidates
/// against their own meeting with the ball instead of one shared intercept point.</item>
/// <item><c>AiBehaviour 6</c> — A2.2: perception has sight filtering and a persistent, ageing memory.
/// An opponent outside the facing cone is no longer measured every tick; the player keeps a predicted
/// position from the last sighting, with growing uncertainty and decaying confidence. Teammates are
/// still observed every tick (teammate belief is A7.2's team calls).</item>
/// <item><c>AiBehaviour 7</c> — A2.3/A2.4: scorers estimate an opponent's body from a scouted
/// <see cref="CapabilityEstimate"/> band (never the live attributes), and the carrier chooses a carry
/// direction with a local pressure window (arrival-based, directional) blended into its freedom term.</item>
/// <item><c>AiBehaviour 8</c> — A2.5/A2.6: a cell's influence is split into mine / theirs / unclaimed
/// shares with the absolute arrival times kept alongside, and the persistent team-job coordinator reads
/// a team belief (union of the team's observations) instead of the authoritative squad, so an unseen
/// opponent can no longer change a team's structure.</item>
/// </list>
/// </summary>
public static class SimulationVersion
{
    /// <summary>Incremented when an AI/physics change makes trajectories differ from the previous version.</summary>
    public const int AiBehaviour = 8;

    /// <summary>Version of the <see cref="RandomStreams"/> derivation.</summary>
    public const int RandomStreamsDerivation = 1;

    /// <summary>Compact tag for artifacts, e.g. <c>ai2-rs1</c>.</summary>
    public static string Tag => $"ai{AiBehaviour}-rs{RandomStreamsDerivation}";
}
public enum TacticalRole
{
    Goalkeeper,
    CenterBack,
    FullBack,
    DefensiveMidfielder,
    CentralMidfielder,
    Winger,
    Striker
}

public enum KickIntentType
{
    None = 0,
    GroundPass,         // Pass 1: To teammate's feet (Ground)
    LoftedPass,         // Pass 1 + High Modifier: To teammate's feet (Air/Lobbed)
    ThroughPass,        // Pass 2: Into space ahead of runner (Ground)
    LobbedThroughPass,  // Pass 2 + High Modifier: Into space ahead of runner (Air/Lobbed)
    DrivenPass,         // Fast pinged pass
    Cross,              // Pass 3: Whipped aerial cross into the box
    Shot,               // Standard shot
    CurledShot,         // Shot + High/Finesse modifier: inside-foot curled shot
    ChipShot,           // Shot + Chip button (RB / V): lofted shot over the keeper
    TrivelaShot,        // Shot + Sprint held (R2 / Shift): outside-foot curled shot
    Header,
    Clearance
}

/// <summary>
/// Classification of <see cref="KickIntentType"/> used by the ownership model, the execution layer
/// and match statistics. Audit F17: pass counting used to be a hand-written list that omitted
/// <see cref="KickIntentType.ThroughPass"/> and <see cref="KickIntentType.LobbedThroughPass"/>
/// entirely, so every through ball was invisible to analytics.
/// </summary>
public static class KickIntents
{
    public static bool IsPass(KickIntentType intent) => intent is
        KickIntentType.GroundPass or
        KickIntentType.LoftedPass or
        KickIntentType.ThroughPass or
        KickIntentType.LobbedThroughPass or
        KickIntentType.DrivenPass or
        KickIntentType.Cross;

    public static bool IsShot(KickIntentType intent) => intent is
        KickIntentType.Shot or
        KickIntentType.CurledShot or
        KickIntentType.ChipShot or
        KickIntentType.TrivelaShot;
}

public enum ControllerSourceType
{
    AI,
    Scripted,
    RecordedReplay,
    HumanPreMatchLocked
}

/// <summary>How a player last played the ball. Used by the rules engine to judge offside at the
/// moment the ball is played rather than when the toucher identity happens to change (audit F08).</summary>
public enum BallContactKind
{
    /// <summary>Pass, shot, cross, clearance or keeper distribution.</summary>
    Kick,
    /// <summary>Standing tackle that dispossessed an opponent.</summary>
    Tackle,
    /// <summary>First-touch control or a dribble touch.</summary>
    Collection,
    /// <summary>Goalkeeper catch or parry.</summary>
    Save
}

/// <summary>
/// Which restart put the ball back into play (audit F14). Law 11 treats restarts differently:
/// a player can receive the ball directly from a throw-in, goal kick or corner kick without being
/// offside, while a goal cannot be scored directly from an indirect free kick or a throw-in.
/// The old model carried nothing but the phase, so every restart was treated as open play.
/// </summary>
public enum RestartKind
{
    /// <summary>Open play: no restart is awaiting its first touch.</summary>
    None = 0,
    Kickoff,
    ThrowIn,
    GoalKick,
    CornerKick,
    /// <summary>A direct free kick (a foul): it can score directly and offside applies normally.</summary>
    FreeKick,
    /// <summary>An indirect free kick (an offside offence): a goal needs a second touch first.</summary>
    IndirectFreeKick,
    PenaltyKick
}

/// <summary>Law 11 semantics of each <see cref="RestartKind"/>, kept in one place (audit F14).</summary>
public static class RestartRules
{
    /// <summary>
    /// There is no offside offence when a player receives the ball directly from a throw-in, a goal
    /// kick or a corner kick (IFAB Law 11). The restarting team's receivers must therefore not be put
    /// into the offside snapshot at the restart's first touch.
    /// </summary>
    public static bool ExemptsOffside(RestartKind kind) => kind is
        RestartKind.ThrowIn or RestartKind.GoalKick or RestartKind.CornerKick;

    /// <summary>
    /// These restarts cannot score directly: the ball must be touched by another player first. That
    /// covers an indirect free kick and a throw-in. A ball that crosses the goal line from such a
    /// restart without a second touch is a goal kick (opponents' goal) or a corner kick (own goal),
    /// never a goal.
    /// </summary>
    public static bool RequiresSecondTouchBeforeGoal(RestartKind kind) => kind is
        RestartKind.IndirectFreeKick or RestartKind.ThrowIn;
}

/// <summary>
/// A play/touch of the ball, emitted by the execution layer and consumed by the rules engine in the
/// same tick. Player positions at the moment of the play are the positions the rules engine sees when
/// it consumes the event, so no separate snapshot array is carried.
/// </summary>
public readonly record struct BallContactEvent(
    int Tick,
    BallContactKind Kind,
    int PlayerId,
    TeamId Team,
    Vector3 BallPosition
)
{
    /// <summary>The kick that produced this contact (pass, shot, clearance...).</summary>
    public KickIntentType Intent { get; init; } = KickIntentType.None;

    /// <summary>
    /// The player the ball was actually played to, when the play was a planned pass (audit F01/F10).
    /// Reception assistance and the responder election use it instead of guessing from proximity.
    /// </summary>
    public int? IntendedReceiverId { get; init; }

    /// <summary>
    /// True for a <see cref="BallContactKind.Save"/> that only deflected the ball (a parry) rather
    /// than catching it. A deflection must not hand team possession to the keeper's team (F10).
    /// </summary>
    public bool IsParry { get; init; }

    /// <summary>
    /// The restart that put the ball back into play, when this touch *is* that restart's first play
    /// (audit F14). It lets the rules engine apply the Law 11 restart exemptions (no offside from a
    /// throw-in, goal kick or corner) and the no-direct-goal rule for indirect restarts, which a bare
    /// contact kind cannot express. <see cref="RestartKind.None"/> for ordinary open play.
    /// </summary>
    public RestartKind RestartOrigin { get; init; } = RestartKind.None;

    /// <summary>
    /// A deliberate play: a kick, a tackle or a controlled touch. Under Law 11 an opponent's
    /// deliberate play removes a previously offside attacker's restriction, whereas a save - a
    /// reaction, not a deliberate play - does not (audit F14).
    /// </summary>
    public bool IsDeliberatePlay => Kind is BallContactKind.Kick or BallContactKind.Tackle or BallContactKind.Collection;

    /// <summary>
    /// A keeper catch/parry. It is a "deliberate save", which by law does **not** reset offside.
    /// </summary>
    public bool IsSaveOrDeflection => Kind == BallContactKind.Save;
}

/// <summary>
/// Why the execution layer refused to play the ball for a player who asked to (audit F17).
/// </summary>
public enum ContactRejectionReason
{
    /// <summary>The contact was executed (or nothing was requested).</summary>
    None = 0,
    /// <summary>The ball was outside the physical reach for this player's current action.</summary>
    OutOfReach,
    /// <summary>The ball was above the reachable height (no jump/header, or above it).</summary>
    BallTooHigh,
    /// <summary>The player's body was facing away from the ball and could not play it.</summary>
    FacingAway,
    /// <summary>Kick/tackle recovery: this player was not allowed to play the ball yet.</summary>
    InRecovery,
    /// <summary>In reach, but another player won the single-contact arbitration for this tick.</summary>
    LostArbitration,
    /// <summary>The ball is held by a goalkeeper and only the holder may release it.</summary>
    BallHeld,
    /// <summary>A foul triggered a restart, so the pending contact was cancelled.</summary>
    RestartTriggered
}

/// <summary>
/// One player's requested ball contact this tick and whether execution actually played the ball.
/// Statistics and input buffers consume this instead of assuming a proposed command happened
/// (audit F17). Contacts the execution layer made without a request (a plain first touch) are not
/// recorded here, only players who asked to kick or tackle.
/// </summary>
public readonly record struct BallContactAttempt(
    int Tick,
    int PlayerId,
    TeamId Team,
    KickIntentType Intent,
    bool WantedTackle,
    bool Executed,
    ContactRejectionReason Reason
)
{
    public bool WantedKick => Intent != KickIntentType.None;
    public bool WantedContact => WantedKick || WantedTackle;
}

public readonly record struct FootballerCommand(
    Vector2 DesiredMoveDirectionXZ,
    Vector2 DesiredFacingDirectionXZ,
    bool Sprint,
    bool ShieldBall,
    bool RequestTackle,
    bool RequestJumpHeader,
    bool RequestKeeperDive,
    Vector3 KeeperDiveDirection,
    KickIntentType KickType,
    Vector3 KickAimDirectionXZ,
    float KickPower01,
    float RequestedCurveSpin
)
{
    /// <summary>
    /// Player this kick is meant for when <see cref="KickType"/> is a pass. Null for shots,
    /// clearances and unplanned kicks. Carried into the contact event so reception and possession
    /// no longer have to infer the receiver from "whoever is nearest" (audit F10/F11).
    /// </summary>
    public int? IntendedReceiverId { get; init; }

    public static FootballerCommand Idle => new(
        Vector2.Zero, new Vector2(1f, 0f), false, false, false, false, false,
        Vector3.Zero, KickIntentType.None, new Vector3(1f, 0f, 0f), 0f, 0f);
}

/// <summary>
/// Who actually has the ball, separated from "who last touched it" (audit F10). The old model used
/// <see cref="BallState.LastTouchedByTeam"/> for both, so a deflection, an unreachable clearance or a
/// keeper parry could be read as sustained possession.
/// </summary>
public enum BallControlState
{
    /// <summary>Nobody controls the ball; it is rolling, bouncing or in flight without a planned receiver.</summary>
    Loose,
    /// <summary>A player has the ball at their feet (dribble touch / first-touch control).</summary>
    Controlled,
    /// <summary>A planned pass is travelling toward a teammate.</summary>
    InFlightPass,
    /// <summary>Goalkeeper has caught or parried the ball and currently holds it.</summary>
    KeeperHeld
}

/// <summary>Snapshot of the authoritative ball-ownership model, refreshed every live tick.</summary>
public readonly record struct BallPossessionSnapshot(
    BallControlState State,
    TeamId? PossessionTeam,
    int? ControllingPlayerId,
    int? IntendedReceiverId
)
{
    public static BallPossessionSnapshot Loose => new(BallControlState.Loose, null, null, null);

    public bool IsControlledBy(TeamId team) =>
        State is BallControlState.Controlled or BallControlState.KeeperHeld && PossessionTeam == team;
}

/// <summary>
/// What the execution layer needs to know to apply (or refuse) reception assistance this tick.
/// </summary>
public readonly record struct ReceptionAssistContext(
    BallControlState ControlState,
    int? IntendedReceiverId,
    int? HumanPlayerId
);

public readonly record struct PerceivedEntity(
    int PlayerId,
    TeamId Team,
    TacticalRole Role,
    Vector3 EstimatedPosition,
    Vector3 EstimatedVelocity,
    float Confidence01
)
{
    /// <summary>
    /// Ticks since this entity was actually observed (A2.2). 0 means the estimate comes from this tick's
    /// observation; a positive value means it is a prediction from memory and its position is where the
    /// entity is *expected* to be, not where it is.
    /// </summary>
    public int ObservedAgeTicks { get; init; }

    /// <summary>
    /// Positional uncertainty introduced by that age, in metres (A2.2). Zero for a fresh observation.
    /// A scorer may widen its tolerance by this instead of pretending a stale position is exact.
    /// </summary>
    public float PositionUncertaintyMeters { get; init; }

    /// <summary>
    /// What this observer believes the player is physically capable of (A2.3). Scouting knowledge or a
    /// conservative default — never the live <see cref="PlayerAttributes"/>. A scorer that estimates
    /// arrival times must go through this, which is what keeps a new attribute from becoming a leak.
    /// </summary>
    public CapabilityEstimate Capability { get; init; } = CapabilityEstimate.Generic;
}

public enum MatchPhase
{
    PreKickoff,
    FirstHalf,
    HalfTime,
    SecondHalf,
    GoalScored,
    ThrowIn,
    GoalKick,
    CornerKick,
    FreeKick,
    PenaltyKick,
    FullTime
}

/// <summary>
/// Which half is being played, tracked independently of <see cref="MatchPhase"/>.
/// Audit F05: the old code inferred the half from the phase, so every restart inside the
/// second half (throw-in, corner, goal kick, free kick, penalty, goal celebration) looked
/// like the first half and flipped attacking directions.
/// </summary>
public enum MatchPeriod
{
    FirstHalf,
    SecondHalf
}

public sealed class FootballerObservation
{
    public required int SelfId { get; init; }
    public required TeamId SelfTeam { get; init; }
    public required TacticalRole SelfRole { get; init; }
    public required Vector3 SelfPosition { get; init; }
    public required Vector3 SelfVelocity { get; init; }
    public required Vector2 SelfFacingXZ { get; init; }
    public required float Stamina01 { get; init; }
    public required bool AvailableToKick { get; init; }
    public required float ControlQuality01 { get; init; }

    /// <summary>
    /// How balanced this player currently is (A2.1). Execution scales acceleration by it after a
    /// tackle or a stumble, so a reach estimate that assumed a balanced player would claim a recovery
    /// time the body cannot produce. Defaults to 1 (balanced) for callers that do not set it.
    /// </summary>
    public float SelfBalance01 { get; init; } = 1f;
    public required Vector3 PerceivedBallPosition { get; init; }
    public required Vector3 PerceivedBallVelocity { get; init; }
    public required TeamId? PossessingTeam { get; init; }
    public required int? BallLastTouchedByPlayerId { get; init; }

    /// <summary>
    /// Team of the last touch. This is a physical fact (the execution layer's reach rule depends on
    /// it), not the possession model — use <see cref="BallControlState"/> for "who has the ball".
    /// </summary>
    public TeamId? BallLastTouchedByTeam { get; init; }
    public required Vector3 TacticalAnchorPosition { get; init; }
    public required float AttackingDirectionX { get; init; }
    public required float MatchTimeSeconds { get; init; }
    public required MatchPhase CurrentMatchPhase { get; init; }
    public required bool IsSetPieceTaker { get; init; }

    /// <summary>
    /// Engine tick this observation was built on, or 0 for a controller built without a match (probes,
    /// unit tests). The AI scheduler needs an absolute, monotonic tick so a cadence can never be extended by
    /// a paused or suspended tick; zero means "unknown", which always forces a fresh decision.
    /// </summary>
    public int Tick { get; init; }
    public required IReadOnlyList<PerceivedEntity> VisiblePlayers { get; init; }
    public required TeamTacticalProfile TeamProfile { get; init; }
    public required PlayerTendencies SelfTendencies { get; init; }
    public required PlayerAttributes SelfAttributes { get; init; }

    /// <summary>Authoritative control state of the ball (audit F10).</summary>
    public BallControlState BallControlState { get; init; } = BallControlState.Loose;

    /// <summary>The player currently controlling the ball, when there is one.</summary>
    public int? BallControllingPlayerId { get; init; }

    /// <summary>Receiver of the pass currently in flight, when the play was a planned pass.</summary>
    public int? IntendedReceiverId { get; init; }

    /// <summary>
    /// Seconds left before this player may release a ball they are holding in their hands
    /// (goalkeeper catch, audit F03). Zero for every other player/tick.
    /// </summary>
    public float BallHoldRemainingSeconds { get; init; }

    /// <summary>Persistent team-level off-ball job (audit F04).</summary>
    public TacticalJob TacticalJob { get; init; } = TacticalJob.Shape;

    /// <summary>Target point the assigned job wants this player to occupy/work toward.</summary>
    public Vector3? TacticalJobAnchor { get; init; }

    /// <summary>Opponent to mark, for <see cref="TacticalJob.Marker"/>.</summary>
    public int? JobTargetPlayerId { get; init; }

    /// <summary>Human-readable job reason for debug HUDs/probes.</summary>
    public string JobReason { get; init; } = string.Empty;

    /// <summary>
    /// True when the match-level election has made this player their team's ball responder.
    /// Null means the controller was built without a match (standalone probes/tests); controllers
    /// then fall back to their local nearest-teammate estimate.
    /// </summary>
    public bool? IsDesignatedBallResponder { get; init; }

    /// <summary>
    /// Oldest age, in ticks, among the opponent estimates in <see cref="VisiblePlayers"/> (A2.2), or 0
    /// when every opponent in the picture was seen this tick. This is the staleness the trace records as
    /// knowledge age (A0.4) and the first honest answer that field has had.
    /// </summary>
    public int KnowledgeAgeTicks { get; init; }
}

public interface IFootballerController
{
    ControllerSourceType SourceType { get; }
    FootballerCommand Decide(in FootballerObservation observation, float dt);
    string GetDebugSummary();
}

/// <summary>Kicking foot, used for the inside/outside-foot strike choice (audit follow-up).</summary>
public enum Footedness
{
    Right,
    Left
}

public sealed class PlayerAttributes
{
    /// <summary>
    /// Strong foot. Shooting across the body to the opposite side forces an outside-foot strike
    /// (or the weak foot), which is less accurate and curves the other way.
    /// </summary>
    public Footedness PreferredFoot { get; init; } = Footedness.Right;

    public float MaxSprintSpeed { get; init; } = 8.4f;
    public float Acceleration { get; init; } = 6.8f;
    public float AgilityTurnRate { get; init; } = 9.5f;
    public float StaminaCapacity { get; init; } = 1.0f;
    public float FootAccuracy01 { get; init; } = 0.82f;
    public float ShotPowerMax { get; init; } = 31.0f;
    public float FirstTouch01 { get; init; } = 0.82f;
    public float Balance01 { get; init; } = 0.80f;
    public float Strength01 { get; init; } = 0.78f;
    public float ReachRadius { get; init; } = 0.75f;
    public float ReactionDelaySeconds { get; init; } = 0.08f;
    public float Composure01 { get; init; } = 0.80f;
    public float GoalkeepingReflex01 { get; init; } = 0.82f;
}

/// <summary>
/// Neutral values for <see cref="PlayerTendencies"/>. Behaviour code applies deltas around these
/// baselines, so the default tendencies (and every place that constructs a default) keep the exact
/// pre-F18 behaviour while a configured tendency has a measurable effect (audit F18).
/// </summary>
public static class PlayerTendencyBaselines
{
    public const float RiskTolerance = 0.55f;
    public const float DirectPassPreference = 0.55f;
    public const float ShootingAppetite = 0.60f;
    public const float DribblePreference = 0.50f;
    public const float PositionalDiscipline = 0.70f;
    public const float PressingAggression = 0.60f;
}

public sealed class PlayerTendencies
{
    public float RiskTolerance01 { get; init; } = PlayerTendencyBaselines.RiskTolerance;
    public float DirectPassPreference01 { get; init; } = PlayerTendencyBaselines.DirectPassPreference;
    public float ShootingAppetite01 { get; init; } = PlayerTendencyBaselines.ShootingAppetite;
    public float DribblePreference01 { get; init; } = PlayerTendencyBaselines.DribblePreference;
    public float PositionalDiscipline01 { get; init; } = PlayerTendencyBaselines.PositionalDiscipline;
    public float PressingAggression01 { get; init; } = PlayerTendencyBaselines.PressingAggression;
}

/// <summary>
/// Deterministic tendency presets per role, so a match expresses individual players instead of every
/// footballer being identical. Audit F18: these fields used to be stored and exposed but never read,
/// so no preset could have mattered. Values stay near the neutral baselines — the role differences
/// are what the AI responds to.
/// </summary>
public static class PlayerTendencyProfile
{
    public static PlayerTendencies ForRole(TacticalRole role) => role switch
    {
        TacticalRole.Striker => new PlayerTendencies
        {
            ShootingAppetite01 = 0.80f,
            DirectPassPreference01 = 0.65f,
            RiskTolerance01 = 0.62f,
            DribblePreference01 = 0.55f,
            PositionalDiscipline01 = 0.62f,
            PressingAggression01 = 0.55f
        },
        TacticalRole.Winger => new PlayerTendencies
        {
            ShootingAppetite01 = 0.68f,
            DirectPassPreference01 = 0.60f,
            RiskTolerance01 = 0.62f,
            DribblePreference01 = 0.72f,
            PositionalDiscipline01 = 0.55f,
            PressingAggression01 = 0.52f
        },
        TacticalRole.CentralMidfielder => new PlayerTendencies
        {
            ShootingAppetite01 = 0.55f,
            DirectPassPreference01 = 0.62f,
            RiskTolerance01 = 0.60f,
            DribblePreference01 = 0.52f,
            PositionalDiscipline01 = 0.72f,
            PressingAggression01 = 0.62f
        },
        TacticalRole.DefensiveMidfielder => new PlayerTendencies
        {
            ShootingAppetite01 = 0.40f,
            DirectPassPreference01 = 0.45f,
            RiskTolerance01 = 0.42f,
            DribblePreference01 = 0.45f,
            PositionalDiscipline01 = 0.80f,
            PressingAggression01 = 0.70f
        },
        TacticalRole.FullBack => new PlayerTendencies
        {
            ShootingAppetite01 = 0.30f,
            DirectPassPreference01 = 0.50f,
            RiskTolerance01 = 0.40f,
            DribblePreference01 = 0.45f,
            PositionalDiscipline01 = 0.78f,
            PressingAggression01 = 0.72f
        },
        TacticalRole.CenterBack => new PlayerTendencies
        {
            ShootingAppetite01 = 0.25f,
            DirectPassPreference01 = 0.40f,
            RiskTolerance01 = 0.35f,
            DribblePreference01 = 0.30f,
            PositionalDiscipline01 = 0.86f,
            PressingAggression01 = 0.65f
        },
        _ => new PlayerTendencies()
    };
}

public sealed class TeamTacticalProfile
{
    public string Name { get; init; } = "Balanced";
    public float Directness01 { get; init; } = 0.50f;
    public float Tempo01 { get; init; } = 0.55f;
    public float AttackingWidth01 { get; init; } = 0.65f;
    public float DefensiveLineHeight01 { get; init; } = 0.50f;
    public float Compactness01 { get; init; } = 0.60f;
    public float PressingIntensity01 { get; init; } = 0.55f;
    public float RiskTolerance01 { get; init; } = 0.50f;
    public float CounterPressDurationSeconds { get; init; } = 3.5f;
    public float PositionalFreedom01 { get; init; } = 0.55f;
    public bool ShortGoalkeeperDistribution { get; init; } = true;

    public static TeamTacticalProfile PossessionControl => new()
    {
        Name = "Possession / Positional Play",
        Directness01 = 0.25f,
        Tempo01 = 0.55f,
        AttackingWidth01 = 0.82f,
        DefensiveLineHeight01 = 0.68f,
        Compactness01 = 0.75f,
        PressingIntensity01 = 0.70f,
        RiskTolerance01 = 0.40f,
        CounterPressDurationSeconds = 4.5f,
        PositionalFreedom01 = 0.65f,
        ShortGoalkeeperDistribution = true
    };

    public static TeamTacticalProfile DirectCounterattack => new()
    {
        Name = "Direct Counterattack",
        Directness01 = 0.85f,
        Tempo01 = 0.85f,
        AttackingWidth01 = 0.52f,
        DefensiveLineHeight01 = 0.28f,
        Compactness01 = 0.85f,
        PressingIntensity01 = 0.35f,
        RiskTolerance01 = 0.75f,
        CounterPressDurationSeconds = 2.0f,
        PositionalFreedom01 = 0.40f,
        ShortGoalkeeperDistribution = false
    };

    public static TeamTacticalProfile HighPressGegenpress => new()
    {
        Name = "High-Press Gegenpressing",
        Directness01 = 0.65f,
        Tempo01 = 0.90f,
        AttackingWidth01 = 0.60f,
        DefensiveLineHeight01 = 0.82f,
        Compactness01 = 0.88f,
        PressingIntensity01 = 0.92f,
        RiskTolerance01 = 0.68f,
        CounterPressDurationSeconds = 5.5f,
        PositionalFreedom01 = 0.60f,
        ShortGoalkeeperDistribution = true
    };
}

public sealed class PreMatchConfig
{
    public ulong Seed { get; init; } = 20260818UL;
    public int PlayersPerTeam { get; init; } = 11;
    public float HalfDurationSeconds { get; init; } = 2700.0f;
    public int? DesignatedHumanPlayerId { get; init; } = null;
    public TeamTacticalProfile HomeProfile { get; init; } = TeamTacticalProfile.PossessionControl;
    public TeamTacticalProfile AwayProfile { get; init; } = TeamTacticalProfile.DirectCounterattack;

    /// <summary>
    /// AI runtime settings for this match. The default reproduces the behaviour that preceded the
    /// settings object, so an existing caller that does not mention it keeps its exact behaviour.
    /// </summary>
    public AiRuntimeConfig Ai { get; init; } = AiRuntimeConfig.Default;
}

public readonly record struct RecordedTickCommands(int Tick, Dictionary<int, FootballerCommand> CommandsByPlayer);
